[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Get-Sha256Hex([string] $value) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes($value)))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-TestTotp {
    $secret = [Text.Encoding]::ASCII.GetBytes("12345678901234567890")
    $step = [long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30)
    $counter = [BitConverter]::GetBytes($step)
    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($counter)
    }
    $algorithm = [Security.Cryptography.HMACSHA1]::new($secret)
    try {
        $digest = $algorithm.ComputeHash($counter)
        $offset = $digest[$digest.Length - 1] -band 0x0f
        $binary = (([int]$digest[$offset] -band 0x7f) * 16777216) + `
            ([int]$digest[$offset + 1] * 65536) + `
            ([int]$digest[$offset + 2] * 256) + `
            [int]$digest[$offset + 3]
        return ($binary % 1000000).ToString("D6")
    }
    finally {
        $algorithm.Dispose()
        [Array]::Clear($secret, 0, $secret.Length)
    }
}

function New-TestHttpClient {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.UseCookies = $true
    $handler.CookieContainer = [Net.CookieContainer]::new()
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    return $client
}

function Invoke-TestRequest(
    [Net.Http.HttpClient] $client,
    [string] $method,
    [string] $uri,
    [AllowNull()] [string] $json = $null,
    [hashtable] $headers = @{}) {
    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::new($method),
        $uri)
    if (-not [string]::IsNullOrEmpty($json)) {
        $request.Content = [Net.Http.StringContent]::new(
            $json,
            [Text.Encoding]::UTF8,
            "application/json")
    }
    try {
        foreach ($header in $headers.GetEnumerator()) {
            if (-not $request.Headers.TryAddWithoutValidation(
                    [string]$header.Key,
                    [string]$header.Value)) {
                throw "Could not add HTTP header '$($header.Key)'."
            }
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            return [pscustomobject]@{
                Status = [int]$response.StatusCode
                Text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

function Convert-ResponseJson([object] $response, [string] $context) {
    try {
        return $response.Text | ConvertFrom-Json
    }
    catch {
        throw "$context did not return JSON: $($response.Text)"
    }
}

function Assert-Status([object] $response, [int] $expected, [string] $context) {
    if ($response.Status -ne $expected) {
        throw "$context expected HTTP $expected, received $($response.Status): $($response.Text)"
    }
}

function Assert-ErrorCode(
    [object] $response,
    [int] $status,
    [string] $code,
    [string] $context) {
    Assert-Status $response $status $context
    $body = Convert-ResponseJson $response $context
    if ($body.code -ne $code) {
        throw "$context expected error '$code': $($response.Text)"
    }
}

function Wait-ForHttp(
    [string] $uri,
    [Diagnostics.Process] $process,
    [int] $timeoutSeconds = 60) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($timeoutSeconds)
    $lastFailure = "no request attempted"
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "Process exited before $uri became ready (exit $($process.ExitCode))."
        }
        try {
            $response = Invoke-TestRequest $script:probeClient "GET" $uri
            if ($response.Status -eq 200) {
                return
            }
            $lastFailure = "HTTP $($response.Status): $($response.Text)"
        }
        catch {
            $lastFailure = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $uri. Last failure: $lastFailure"
}

function Get-FakeState {
    $response = Invoke-TestRequest $script:probeClient "GET" "$script:fakeBase/__state"
    Assert-Status $response 200 "fake dependency state"
    return Convert-ResponseJson $response "fake dependency state"
}

function Get-ObjectProperty([object] $value, [string] $name) {
    $property = $value.PSObject.Properties[$name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Login-Player(
    [Net.Http.HttpClient] $client,
    [string] $userId,
    [bool] $verifyWrongCode = $false) {
    $request = Invoke-TestRequest $client "POST" `
        "$script:apiBase/api/v1/player/auth/request-code" `
        (@{ userId = $userId } | ConvertTo-Json -Compress) `
        @{ Origin = $script:playerOrigin }
    Assert-Status $request 202 "$userId login-code request"
    $challenge = Convert-ResponseJson $request "$userId login-code request"
    if ([string]::IsNullOrWhiteSpace($challenge.challengeId)) {
        throw "$userId login-code request omitted challengeId."
    }

    $code = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $state = Get-FakeState
        $code = Get-ObjectProperty $state.loginCodes $userId
        if ($code -match '^[0-9]{8}$') {
            break
        }
        Start-Sleep -Milliseconds 50
    }
    if ($code -notmatch '^[0-9]{8}$') {
        throw "The fake RCON server did not capture the $userId login code."
    }

    if ($verifyWrongCode) {
        $wrongLast = if ($code.EndsWith("0")) { "1" } else { "0" }
        $wrongCode = $code.Substring(0, 7) + $wrongLast
        $wrong = Invoke-TestRequest $client "POST" `
            "$script:apiBase/api/v1/player/auth/verify" `
            (@{ challengeId = $challenge.challengeId; code = $wrongCode } |
                ConvertTo-Json -Compress) `
            @{ Origin = $script:playerOrigin }
        Assert-ErrorCode $wrong 401 "INVALID_OR_EXPIRED_LOGIN_CODE" `
            "$userId wrong-code verification"
    }

    $verified = Invoke-TestRequest $client "POST" `
        "$script:apiBase/api/v1/player/auth/verify" `
        (@{ challengeId = $challenge.challengeId; code = $code } |
            ConvertTo-Json -Compress) `
        @{ Origin = $script:playerOrigin }
    Assert-Status $verified 200 "$userId login verification"
    $session = Convert-ResponseJson $verified "$userId login verification"
    if ($session.userId -ne $userId -or $session.csrfToken -notmatch '^[A-Za-z0-9_-]{43}$') {
        throw "$userId verification returned an invalid session: $($verified.Text)"
    }
    return [string]$session.csrfToken
}

function Admin-Headers([string] $reason) {
    return @{
        "X-Pal-Admin-Key" = $script:adminKey
        "X-Pal-Admin-Totp" = Get-TestTotp
        "X-Pal-Admin-Reason" = $reason
        "Idempotency-Key" = "admin-operation-$([guid]::NewGuid().ToString('N'))"
    }
}

function Set-Maintenance([bool] $enabled, [string] $reason) {
    $response = Invoke-TestRequest $script:adminClient "POST" `
        "$script:apiBase/api/v1/extraction/admin/rollover/maintenance" `
        (@{ maintenance = $enabled; reason = $reason } | ConvertTo-Json -Compress) `
        (Admin-Headers $reason)
    Assert-Status $response 200 "set rollover maintenance=$enabled"
}

function Start-TestApi([int] $generation) {
    $stdout = Join-Path $script:testRoot "control-api-$generation.out.log"
    $stderr = Join-Path $script:testRoot "control-api-$generation.err.log"
    $process = Start-Process -FilePath $script:dotnetExecutable `
        -ArgumentList $script:apiArguments -WorkingDirectory $script:buildRoot `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr
    try {
        Wait-ForHttp "$script:apiBase/health/live" $process
        return $process
    }
    catch {
        if (Test-Path -LiteralPath $stdout) { Write-Host (Get-Content -Raw $stdout) }
        if (Test-Path -LiteralPath $stderr) { Write-Host (Get-Content -Raw $stderr) }
        throw
    }
}

function Stop-TestProcess([Diagnostics.Process] $process) {
    if ($null -eq $process) {
        return
    }
    try {
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            if (-not $process.WaitForExit(5000)) {
                throw "Test process $($process.Id) did not exit within 5 seconds."
            }
        }

        # The parameterless wait also lets redirected stdout/stderr handlers
        # flush before Dispose releases their file handles on Windows.
        $process.WaitForExit()
    }
    finally {
        $process.Dispose()
    }
}

function Remove-TestTree([string] $path) {
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
    $target = [IO.Path]::GetFullPath($path)
    if (-not $target.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($target) -notlike "pal-control-player-economy-smoke-*") {
        throw "Refusing to remove unsafe player-economy smoke path: $target"
    }
    if (Test-Path -LiteralPath $target) {
        for ($attempt = 1; $attempt -le 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
                return
            }
            catch {
                if ($attempt -eq 20) {
                    throw
                }
                Start-Sleep -Milliseconds 100
            }
        }
    }
}

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot "helpers\synthetic-resource-catalog.ps1")
$script:serviceRoot = Join-Path $repositoryRoot "services\control-api"
$project = Join-Path $script:serviceRoot "PalControl.ControlApi.csproj"
$fakeScript = Join-Path $PSScriptRoot "fake_palworld_rest.py"
$script:dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source
$pythonExecutable = (Get-Command python -ErrorAction Stop).Source
$reservedPorts = [Collections.Generic.HashSet[int]]::new()
while ($reservedPorts.Count -lt 3) {
    [void]$reservedPorts.Add((Get-FreeTcpPort))
}
$ports = @($reservedPorts)
$apiPort = $ports[0]
$fakePort = $ports[1]
$rconPort = $ports[2]
$script:apiBase = "http://127.0.0.1:$apiPort"
$script:fakeBase = "http://127.0.0.1:$fakePort"
$script:playerOrigin = "http://127.0.0.1:5174"
$script:adminKey = "player-e2e-admin-key-000000000001"
$worldId = "ABCDEF0123456789ABCDEF0123456789"
$conflictingWorldId = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
$script:testRoot = Join-Path $env:TEMP (
    "pal-control-player-economy-smoke-" + [Guid]::NewGuid().ToString("N"))
$script:buildRoot = Join-Path $script:testRoot "build"
$dataRoot = Join-Path $script:testRoot "economy"
$commandRoot = Join-Path $script:testRoot "commands"
$gameBackupRoot = Join-Path $script:testRoot "game-backups"
$economyBackupRoot = Join-Path $script:testRoot "economy-backups"
$economyStagingRoot = Join-Path $script:testRoot "economy-staging"
$installRoot = Join-Path $script:testRoot "PalServer"
$settingsRoot = Join-Path $installRoot "Pal\Saved\Config\WindowsServer"
$worldRoot = Join-Path $installRoot "Pal\Saved\SaveGames\0\$worldId"
$fakeStdout = Join-Path $script:testRoot "fake.out.log"
$fakeStderr = Join-Path $script:testRoot "fake.err.log"

$script:probeClient = New-TestHttpClient
$script:probeClient.Timeout = [TimeSpan]::FromSeconds(2)
$script:adminClient = New-TestHttpClient
$anonymousClient = New-TestHttpClient
$aliceClient = $null
$bobClient = $null
$fake = $null
$api = $null

try {
    New-Item -ItemType Directory -Force -Path `
        $script:testRoot, $settingsRoot, $worldRoot | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $installRoot "PalServer.exe"), [byte[]]@(0))
    [IO.File]::WriteAllText(
        (Join-Path $settingsRoot "GameUserSettings.ini"),
        "[/Script/Pal.PalGameLocalSettings]`r`nDedicatedServerName=$worldId`r`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes((Join-Path $worldRoot "Level.sav"), [byte[]](1..64))

    & dotnet build $project --configuration Release --output $script:buildRoot --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Control API Release build failed with exit code $LASTEXITCODE."
    }
    Write-SyntheticResourceCatalog $script:buildRoot

    $fake = Start-Process -FilePath $pythonExecutable -ArgumentList @(
        $fakeScript,
        "--port", $fakePort,
        "--world-guid", $worldId,
        "--rcon-port", $rconPort,
        "--rcon-password", "integration-rcon-password"
    ) -WorkingDirectory $repositoryRoot -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $fakeStdout -RedirectStandardError $fakeStderr
    Wait-ForHttp "$script:fakeBase/__state" $fake 15

    $script:apiArguments = @(
        (Join-Path $script:buildRoot "PalControl.ControlApi.dll"),
        "--contentRoot=$script:buildRoot",
        "--environment=Development",
        "--urls=$script:apiBase",
        "--Security:DevelopmentMode=true",
        "--Security:StartupValidation:Strict=false",
        "--Security:AllowedOrigins:0=$script:playerOrigin",
        "--Security:AdminAuthentication:Enabled=true",
        "--Security:AdminAuthentication:EnableLoopbackDevelopmentPrincipal=false",
        "--Security:AdminAuthentication:Principals:0:Subject=player-e2e-owner",
        "--Security:AdminAuthentication:Principals:0:ApiKeySha256=$(Get-Sha256Hex $script:adminKey)",
        "--Security:AdminAuthentication:Principals:0:Roles:0=Owner",
        "--Security:AdminAuthentication:Principals:0:TotpSecretBase32=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ",
        "--Palworld:ServerId=local",
        "--Palworld:InstallRoot=$installRoot",
        "--Palworld:OfficialRestApi:BaseUrl=$script:fakeBase/v1/api/",
        "--Palworld:OfficialRestApi:Username=admin",
        "--Palworld:OfficialRestApi:Password=test-password",
        # This black-box suite performs two API generations, content publication,
        # delivery and settlement under the unified test runner's build load.
        # Keep the fake dependency timeout above short scheduler stalls; the
        # production fail-closed world check itself is unchanged.
        "--Palworld:OfficialRestApi:TimeoutSeconds=5",
        "--Palworld:PalDefenderRestApi:Enabled=true",
        "--Palworld:PalDefenderRestApi:BaseUrl=$script:fakeBase/v1/pdapi/",
        "--Palworld:PalDefenderRestApi:Token=integration-pd-token",
        "--Palworld:PalDefenderRestApi:TokenFile=",
        "--Palworld:PalDefenderRestApi:Origin=$script:apiBase",
        "--Palworld:PalDefenderRestApi:TimeoutSeconds=2",
        "--Palworld:PalDefenderRestApi:Permissions:0=REST.Version.Read",
        "--Palworld:PalDefenderRestApi:Permissions:1=REST.Players.Read",
        "--Palworld:PalDefenderRestApi:Permissions:2=REST.Items.Read",
        "--Palworld:PalDefenderRestApi:Permissions:3=REST.Items.Give",
        "--CommandPersistence:DataDirectory=$commandRoot",
        "--CommandPersistence:PalDefenderQueueCapacity=64",
        "--ExtractionMode:Enabled=true",
        "--ExtractionMode:ServerId=local",
        "--ExtractionMode:InitialMarketCoin=1000",
        "--ExtractionMode:InitialSeasonVoucher=300",
        "--ExtractionMode:BootstrapPolicyVersion=legacy-v1",
        "--ExtractionMode:DeliveryPollMilliseconds=250",
        "--ExtractionMode:ExtractionPositionSampleMilliseconds=500",
        "--ExtractionMode:SettlementQueueCapacity=8",
        "--ExtractionMode:SettlementWorkerCount=2",
        "--ExtractionMode:SettlementQueueOperationTimeoutSeconds=60",
        "--ExtractionMode:Persistence:DataDirectory=$dataRoot",
        "--ExtractionMode:Safety:MinimumFreeSpaceBytes=16777216",
        "--ExtractionMode:Safety:ApprovedGameVersion=1.0.1.100619",
        "--ExtractionMode:Safety:ApprovedPalDefenderVersion=1.8.1.3933",
        "--ExtractionMode:Safety:PalDefenderGrantReceiptSemanticsVerified=true",
        "--ExtractionMode:Safety:RequireNativeForPurchase=false",
        "--ExtractionMode:Safety:RequireNativeForResourceExchange=false",
        "--ExtractionMode:Continuity:BackupRoot=$economyBackupRoot",
        "--ExtractionMode:Continuity:StagingRoot=$economyStagingRoot",
        "--ExtractionMode:Continuity:MinimumFreeSpaceBytes=16777216",
        "--ExtractionMode:Observability:Enabled=false",
        "--ExtractionMode:Rcon:Enabled=true",
        "--ExtractionMode:Rcon:AllowDevelopmentSettlement=true",
        "--ExtractionMode:Rcon:Host=127.0.0.1",
        "--ExtractionMode:Rcon:Port=$rconPort",
        "--ExtractionMode:Rcon:TimeoutSeconds=2",
        "--ExtractionMode:Rcon:ApprovedGameVersion=1.0.1.100619",
        "--ExtractionMode:Rcon:ApprovedPalDefenderVersion=1.8.1.3933",
        "--ExtractionMode:Rcon:Password=integration-rcon-password",
        "--ExtractionMode:ExtractionZones:0:Id=portal-e2e-zone",
        "--ExtractionMode:ExtractionZones:0:DisplayName=Portal E2E Zone",
        "--ExtractionMode:ExtractionZones:0:RouteHint=Remain near the test origin.",
        "--ExtractionMode:ExtractionZones:0:MapX=0",
        "--ExtractionMode:ExtractionZones:0:MapY=0",
        "--ExtractionMode:ExtractionZones:0:Radius=1000",
        # The production defaults intentionally contain two daily-rotated zones.
        # Pin both test candidates to the fake players' authoritative map
        # position so the date-selected zone cannot make this identity/security
        # smoke depend on the wall-clock rotation.
        "--ExtractionMode:ExtractionZones:1:Id=portal-e2e-zone-secondary",
        "--ExtractionMode:ExtractionZones:1:DisplayName=Portal E2E Zone Secondary",
        "--ExtractionMode:ExtractionZones:1:RouteHint=Remain near the test origin.",
        "--ExtractionMode:ExtractionZones:1:MapX=0",
        "--ExtractionMode:ExtractionZones:1:MapY=0",
        "--ExtractionMode:ExtractionZones:1:Radius=1000",
        "--PlayerPortal:Enabled=true",
        "--PlayerPortal:PublicSteam=false",
        "--PlayerPortal:CookieSecure=false",
        "--PlayerPortal:AllowedOrigins:0=$script:playerOrigin",
        # This case deliberately fans out 100 authenticated map reads to prove
        # an unchanged content version no longer reacquires the SQLite writer.
        # Raise only the isolated test host limit; the production default stays
        # at its conservative value and is covered by the regular rate tests.
        "--PlayerPortal:ConcurrentRequestLimit=128",
        "--PlayerPortal:UserCooldownSeconds=1",
        "--PlayerPortal:IpCooldownSeconds=1",
        "--TeamEconomy:Enabled=true",
        "--TeamEconomy:InvitePepper=team-economy-smoke-pepper-value-with-more-than-32-characters",
        "--SaveManagement:BackupRoot=$gameBackupRoot",
        "--SaveManagement:RequireRunningProcess=false",
        "--SaveManagement:MinimumFreeSpaceBytes=16777216"
    )

    $api = Start-TestApi 1

    $unauthenticatedSettlementStatus = Invoke-TestRequest $script:adminClient "GET" `
        "$script:apiBase/api/v1/extraction/admin/settlement/status"
    Assert-Status $unauthenticatedSettlementStatus 401 `
        "unauthenticated settlement status"
    $settlementStatusResponse = Invoke-TestRequest $script:adminClient "GET" `
        "$script:apiBase/api/v1/extraction/admin/settlement/status" $null `
        (Admin-Headers "verify settlement adapter status")
    Assert-Status $settlementStatusResponse 200 "settlement adapter status"
    $settlementStatus = Convert-ResponseJson $settlementStatusResponse `
        "settlement adapter status"
    if ($settlementStatus.adapter -ne "development-rcon" -or
        $settlementStatus.enabled -ne $true -or
        $settlementStatus.connected -ne $true -or
        $settlementStatus.outcome -ne "success" -or
        $null -ne $settlementStatus.error) {
        throw "Settlement status did not expose the active adapter-neutral success schema: $($settlementStatusResponse.Text)"
    }
    $legacyStatusResponse = Invoke-TestRequest $script:adminClient "GET" `
        "$script:apiBase/api/v1/extraction/admin/rcon/status" $null `
        (Admin-Headers "verify deprecated settlement status alias")
    Assert-Status $legacyStatusResponse 200 "deprecated settlement status alias"
    $legacyStatus = Convert-ResponseJson $legacyStatusResponse `
        "deprecated settlement status alias"
    if ($legacyStatus.adapter -ne $settlementStatus.adapter -or
        $legacyStatus.enabled -ne $settlementStatus.enabled -or
        $legacyStatus.connected -ne $settlementStatus.connected -or
        $legacyStatus.outcome -ne $settlementStatus.outcome) {
        throw "Deprecated status alias diverged from the adapter-neutral status schema: $($legacyStatusResponse.Text)"
    }

    Assert-ErrorCode (
        Invoke-TestRequest $anonymousClient "GET" `
            "$script:apiBase/api/v1/player/auth/session") `
        401 "PLAYER_SESSION_REQUIRED" "anonymous player session"
    Assert-ErrorCode (
        Invoke-TestRequest $anonymousClient "POST" `
            "$script:apiBase/api/v1/player/auth/request-code" `
            '{"userId":"steam_111"}' @{ Origin = "https://attacker.invalid" }) `
        403 "ORIGIN_NOT_ALLOWED" "cross-origin login request"

    $aliceClient = New-TestHttpClient
    $aliceCsrf = Login-Player $aliceClient "steam_111" $true
    $aliceSession = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/auth/session"
    Assert-Status $aliceSession 200 "Alice authenticated session"

    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "GET" `
            "$script:apiBase/api/v1/player/me/overview?userId=steam_222") `
        400 "PLAYER_IDENTITY_OVERRIDE_FORBIDDEN" "player identity override"
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/orders" `
            '{"productId":"00000000-0000-0000-0000-000000000001","quantity":1}' `
            @{ Origin = $script:playerOrigin; "Idempotency-Key" = "missing-csrf-order" }) `
        403 "CSRF_TOKEN_INVALID" "missing-CSRF purchase"

    # The first read creates the unique active/unbound season. It must remain
    # unavailable until the audited initial world binding is committed.
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "GET" `
            "$script:apiBase/api/v1/player/me/overview") `
        423 "SEASON_WORLD_UNBOUND" "unbound initial season"
    Set-Maintenance $true "player economy initial world binding"
    $commit = Invoke-TestRequest $script:adminClient "POST" `
        "$script:apiBase/api/v1/extraction/admin/rollover/commit" `
        (@{ worldId = $worldId } | ConvertTo-Json -Compress) `
        (Admin-Headers "bind initial player economy world")
    Assert-Status $commit 200 "initial world binding"
    $committed = Convert-ResponseJson $commit "initial world binding"
    if ($committed.worldId -ne $worldId) {
        throw "Initial world binding returned the wrong WorldId: $($commit.Text)"
    }

    Assert-ErrorCode (
        Invoke-TestRequest $script:adminClient "POST" `
            "$script:apiBase/api/v1/extraction/admin/rollover/commit" `
            (@{ worldId = $worldId } | ConvertTo-Json -Compress) `
            (Admin-Headers "reject repeated initial binding")) `
        409 "ROLLOVER_PERSISTENT_STATE_REQUIRED" "repeated initial world binding"
    Assert-ErrorCode (
        Invoke-TestRequest $script:adminClient "POST" `
            "$script:apiBase/api/v1/extraction/admin/rollover/commit" `
            (@{ worldId = $conflictingWorldId } | ConvertTo-Json -Compress) `
            (Admin-Headers "reject conflicting initial binding")) `
        409 "ROLLOVER_PERSISTENT_STATE_REQUIRED" "conflicting initial world binding"
    Set-Maintenance $false "initial world binding complete"

    # Restart with the same SQLite roots. Process sessions must be revoked,
    # while the bound season remains durable and cannot re-enter bootstrap.
    Stop-TestProcess $api
    $api = Start-TestApi 2
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "GET" `
            "$script:apiBase/api/v1/player/auth/session") `
        401 "PLAYER_SESSION_REQUIRED" "pre-restart player session"
    Set-Maintenance $true "verify persisted initial binding"
    Assert-ErrorCode (
        Invoke-TestRequest $script:adminClient "POST" `
            "$script:apiBase/api/v1/extraction/admin/rollover/commit" `
            (@{ worldId = $worldId } | ConvertTo-Json -Compress) `
            (Admin-Headers "reject bootstrap after restart")) `
        409 "ROLLOVER_PERSISTENT_STATE_REQUIRED" "initial binding replay after restart"
    Set-Maintenance $false "binding persistence verified"

    $aliceClient.Dispose()
    $aliceClient = New-TestHttpClient
    $aliceCsrf = Login-Player $aliceClient "steam_111" $false
    Start-Sleep -Milliseconds 1100
    $bobClient = New-TestHttpClient
    $bobCsrf = Login-Player $bobClient "steam_222" $false

    $aliceOverviewResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/overview"
    Assert-Status $aliceOverviewResponse 200 "Alice bound economy overview"
    $aliceOverview = Convert-ResponseJson $aliceOverviewResponse "Alice bound economy overview"
    $bobOverviewResponse = Invoke-TestRequest $bobClient "GET" `
        "$script:apiBase/api/v1/player/me/overview"
    Assert-Status $bobOverviewResponse 200 "Bob bound economy overview"

    # Once-per-process projection verification must not turn every map poll
    # into a SQLite writer. Start all reads before awaiting any response so a
    # regression is exposed as busy/timeout/5xx under realistic fan-out.
    $mapTasks = @(1..100 | ForEach-Object {
        $aliceClient.GetAsync(
            "$script:apiBase/api/v1/player/me/extraction-zones")
    })
    foreach ($mapTask in $mapTasks) {
        $mapResponse = $mapTask.GetAwaiter().GetResult()
        try {
            if ([int]$mapResponse.StatusCode -ne 200) {
                $mapBody = $mapResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                throw "Concurrent player map read returned HTTP $([int]$mapResponse.StatusCode): $mapBody"
            }
        }
        finally {
            $mapResponse.Dispose()
        }
    }

    # These routes are mapped below the operator-protected /api/v1 group, but
    # must authenticate with only the player session. Never attach an admin
    # credential here: this is the production-policy black-box regression for
    # the explicit player route boundary.
    Assert-ErrorCode (
        Invoke-TestRequest $anonymousClient "GET" `
            "$script:apiBase/api/v1/player/me/notifications") `
        401 "PLAYER_SESSION_REQUIRED" "anonymous player notification feed"
    $notificationFeedResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/notifications"
    Assert-Status $notificationFeedResponse 200 "player-only notification feed"
    $notificationFeed = Convert-ResponseJson $notificationFeedResponse `
        "player-only notification feed"
    if ($null -eq $notificationFeed.items -or $null -eq $notificationFeed.unreadCount) {
        throw "Player notification feed omitted its stable schema: $($notificationFeedResponse.Text)"
    }
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "GET" `
            "$script:apiBase/api/v1/player/me/notifications?accountId=00000000-0000-0000-0000-000000000001") `
        400 "PLAYER_IDENTITY_OVERRIDE_FORBIDDEN" `
        "player notification identity override"
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/notifications/read-all" $null @{
                Origin = $script:playerOrigin
            }) `
        403 "CSRF_TOKEN_INVALID" "player notification missing CSRF"
    $readAllNotifications = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/notifications/read-all" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
        }
    Assert-Status $readAllNotifications 200 "player-only notification read-all"

    Assert-ErrorCode (
        Invoke-TestRequest $anonymousClient "GET" `
            "$script:apiBase/api/v1/player/me/team-economy") `
        401 "PLAYER_SESSION_REQUIRED" "anonymous team economy dashboard"
    $emptyTeamResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/team-economy"
    Assert-Status $emptyTeamResponse 200 "player-only team economy dashboard"
    $emptyTeam = Convert-ResponseJson $emptyTeamResponse `
        "player-only team economy dashboard"
    if ($emptyTeam.enabled -ne $true -or $emptyTeam.hasTeam -ne $false) {
        throw "Team economy did not return the enabled no-team state: $($emptyTeamResponse.Text)"
    }
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/team-economy/teams" `
            '{"name":"Player Boundary Team"}' @{
                Origin = $script:playerOrigin
                "Idempotency-Key" = "team-player-boundary-missing-csrf"
            }) `
        403 "CSRF_TOKEN_INVALID" "team economy missing CSRF"
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/team-economy/teams" `
            '{"name":"Player Boundary Team","accountId":"00000000-0000-0000-0000-000000000001"}' @{
                Origin = $script:playerOrigin
                "X-CSRF-Token" = $aliceCsrf
                "Idempotency-Key" = "team-player-boundary-identity-override"
            }) `
        400 "TEAM_IDENTITY_OVERRIDE_FORBIDDEN" "team economy identity override"
    $createdTeamResponse = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/team-economy/teams" `
        '{"name":"Player Boundary Team"}' @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
            "Idempotency-Key" = "team-player-boundary-create-0001"
        }
    Assert-Status $createdTeamResponse 200 "player-only team creation"
    $createdTeam = Convert-ResponseJson $createdTeamResponse "player-only team creation"
    if ($createdTeam.name -ne "Player Boundary Team" -or $createdTeam.isOwner -ne $true) {
        throw "Player-only team creation returned an invalid result: $($createdTeamResponse.Text)"
    }

    # Read-only overview deliberately does not require the player online and
    # therefore does not create a current-world PlayerUID binding. A quote is
    # the least destructive authenticated operation that establishes Bob's
    # observed binding before exercising cross-account settlement isolation.
    $bobBindingQuote = Invoke-TestRequest $bobClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/quote" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $bobCsrf
        }
    Assert-Status $bobBindingQuote 200 "Bob current-world identity binding"

    $catalogResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/catalog"
    Assert-Status $catalogResponse 200 "player shop catalog"
    $catalog = Convert-ResponseJson $catalogResponse "player shop catalog"
    if ([string]$catalog.contentVersionId -notmatch `
            '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' -or
        [string]$catalog.contentHash -notmatch '^[a-f0-9]{64}$' -or
        [string]$catalog.businessDate -notmatch '^\d{4}-\d{2}-\d{2}$' -or
        [string]::IsNullOrWhiteSpace([string]$catalog.rulesVersion) -or
        $null -eq $catalog.rotation -or
        [string]$catalog.rotation.currentContentVersionId -ne [string]$catalog.contentVersionId -or
        [string]$catalog.rotation.contentHash -ne [string]$catalog.contentHash) {
        throw "The player catalog omitted or mismatched published content evidence: $($catalogResponse.Text)"
    }
    foreach ($catalogProduct in @($catalog.items)) {
        if ([string]::IsNullOrWhiteSpace([string]$catalogProduct.sku) -or
            [string]$catalogProduct.contentVersionId -ne [string]$catalog.contentVersionId -or
            [string]$catalogProduct.contentHash -ne [string]$catalog.contentHash) {
            throw "A player catalog product did not carry the current offer evidence: $($catalogResponse.Text)"
        }
    }
    $product = @($catalog.items | Where-Object {
        $_.enabled -eq $true -and $_.price.currency -eq "merchantCoin" -and
            [long]$_.price.amount -le [long]$aliceOverview.balances.merchantCoin
    }) | Select-Object -First 1
    if ($null -eq $product) {
        throw "The player catalog contained no affordable merchantCoin product."
    }

    $beforeDelivery = Get-FakeState
    $staleVersionId = "00000000-0000-0000-0000-000000000000"
    if ([string]$catalog.contentVersionId -eq $staleVersionId) {
        $staleVersionId = "ffffffff-ffff-ffff-ffff-ffffffffffff"
    }
    $staleOrderBody = @{
        productId = [string]$product.productId
        quantity = 1
        sku = [string]$product.sku
        contentVersionId = $staleVersionId
        contentHash = [string]$product.contentHash
    } | ConvertTo-Json -Compress
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/orders" $staleOrderBody @{
                Origin = $script:playerOrigin
                "X-CSRF-Token" = $aliceCsrf
                "Idempotency-Key" = "stale-shop-offer-e2e-0001"
            }) `
        409 "OFFER_NOT_AVAILABLE" "stale shop offer"
    if ([int](Get-FakeState).pdGiveCount -ne [int]$beforeDelivery.pdGiveCount) {
        throw "A stale shop offer dispatched a PalDefender item grant."
    }

    $orderKey = "player-shop-order-e2e-0001"
    $orderBody = @{
        productId = [string]$product.productId
        quantity = 1
        sku = [string]$product.sku
        contentVersionId = [string]$product.contentVersionId
        contentHash = [string]$product.contentHash
    } | ConvertTo-Json -Compress
    $orderResponse = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/orders" $orderBody @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
            "Idempotency-Key" = $orderKey
        }
    Assert-Status $orderResponse 200 "player shop purchase"
    $createdOrder = Convert-ResponseJson $orderResponse "player shop purchase"

    $deliveredOrder = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $ordersResponse = Invoke-TestRequest $aliceClient "GET" `
            "$script:apiBase/api/v1/player/me/orders"
        Assert-Status $ordersResponse 200 "Alice order history"
        $orders = Convert-ResponseJson $ordersResponse "Alice order history"
        $deliveredOrder = @($orders.items | Where-Object {
            [string]$_.orderId -eq [string]$createdOrder.orderId -and $_.state -eq "succeeded"
        }) | Select-Object -First 1
        if ($null -ne $deliveredOrder) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $deliveredOrder) {
        throw "The purchased order did not reach a receipt-backed delivered state."
    }
    $afterDelivery = Get-FakeState
    if ([int]$afterDelivery.pdGiveCount -le [int]$beforeDelivery.pdGiveCount) {
        throw "The delivered order did not issue an attributable PalDefender grant."
    }

    $deliveryCount = [int]$afterDelivery.pdGiveCount
    $orderReplay = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/orders" $orderBody @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
            "Idempotency-Key" = $orderKey
        }
    Assert-Status $orderReplay 200 "exact shop purchase replay"
    $replayedOrder = Convert-ResponseJson $orderReplay "exact shop purchase replay"
    if ([string]$replayedOrder.orderId -ne [string]$createdOrder.orderId) {
        throw "An exact purchase replay returned a different order."
    }
    Start-Sleep -Milliseconds 500
    if ([int](Get-FakeState).pdGiveCount -ne $deliveryCount) {
        throw "An exact purchase replay dispatched a duplicate item grant."
    }

    $bobOrdersResponse = Invoke-TestRequest $bobClient "GET" `
        "$script:apiBase/api/v1/player/me/orders"
    Assert-Status $bobOrdersResponse 200 "Bob isolated order history"
    $bobOrders = Convert-ResponseJson $bobOrdersResponse "Bob isolated order history"
    if (@($bobOrders.items | Where-Object {
            [string]$_.orderId -eq [string]$createdOrder.orderId
        }).Count -ne 0) {
        throw "Bob could read Alice's shop order."
    }

    $quoteResponse = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/quote" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
        }
    Assert-Status $quoteResponse 200 "resource-exchange quote"
    $quote = Convert-ResponseJson $quoteResponse "resource-exchange quote"
    if ([long]$quote.totalValue -le 0 -or @($quote.items).Count -eq 0) {
        throw "The resource-exchange quote did not contain positive whitelist value."
    }

    $firstQuotedItem = @($quote.items)[0]
    $validSelectionBody = @{
        sourceRevision = [long]$quote.revision
        items = @(@{
            itemId = [string]$firstQuotedItem.itemId
            quantity = 1
        })
    } | ConvertTo-Json -Depth 10 -Compress
    $bobSelect = Invoke-TestRequest $bobClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/select" `
        $validSelectionBody @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $bobCsrf
            "Idempotency-Key" = "bob-idor-select-0001"
        }
    Assert-ErrorCode $bobSelect 403 "EXTRACTION_RUN_OWNER_MISMATCH" `
        "cross-player selective-sale IDOR"

    $identityOverrideBody = @{
        sourceRevision = [long]$quote.revision
        userId = "steam_222"
        items = @(@{
            itemId = [string]$firstQuotedItem.itemId
            quantity = 1
        })
    } | ConvertTo-Json -Depth 10 -Compress
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/select" `
            $identityOverrideBody @{
                Origin = $script:playerOrigin
                "X-CSRF-Token" = $aliceCsrf
                "Idempotency-Key" = "alice-select-identity-override-0001"
            }) `
        400 "PLAYER_IDENTITY_OVERRIDE_FORBIDDEN" "selective-sale body identity override"

    $rejectedSelections = @(
        @{
            Name = "empty"
            Code = "EXTRACTION_SELECTION_EMPTY"
            Items = @()
        },
        @{
            Name = "duplicate"
            Code = "EXTRACTION_SELECTION_DUPLICATE_ITEM"
            Items = @(
                @{ itemId = [string]$firstQuotedItem.itemId; quantity = 1 },
                @{ itemId = ([string]$firstQuotedItem.itemId).ToLowerInvariant(); quantity = 1 }
            )
        },
        @{
            Name = "unknown"
            Code = "EXTRACTION_SELECTION_ITEM_UNKNOWN"
            Items = @(@{ itemId = "UnknownItem"; quantity = 1 })
        },
        @{
            Name = "overqty"
            Code = "EXTRACTION_SELECTION_OVER_QUANTITY"
            Items = @(@{
                itemId = [string]$firstQuotedItem.itemId
                quantity = [int]$firstQuotedItem.quantity + 1
            })
        }
    )
    foreach ($rejectedSelection in $rejectedSelections) {
        $body = @{
            sourceRevision = [long]$quote.revision
            items = $rejectedSelection.Items
        } | ConvertTo-Json -Depth 10 -Compress
        Assert-ErrorCode (
            Invoke-TestRequest $aliceClient "POST" `
                "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/select" `
                $body @{
                    Origin = $script:playerOrigin
                    "X-CSRF-Token" = $aliceCsrf
                    "Idempotency-Key" = "alice-select-reject-$($rejectedSelection.Name)-0001"
                }) `
            $(if ($rejectedSelection.Name -in @("empty", "duplicate")) { 400 } else { 422 }) `
            $rejectedSelection.Code "rejected $($rejectedSelection.Name) selection"
    }

    $bobSettle = Invoke-TestRequest $bobClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/settle" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $bobCsrf
            "Idempotency-Key" = "bob-idor-settle-0001"
        }
    Assert-ErrorCode $bobSettle 403 "EXTRACTION_RUN_SCOPE_MISMATCH" `
        "cross-player settlement IDOR"

    # A quote is immutable evidence from content A. Publish content B by the
    # normal maintenance-gated atomic pointer switch, then prove that settling
    # A fails before any inventory dispatch or durable economy mutation.
    $beforeContentSwitchOverviewResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/overview"
    Assert-Status $beforeContentSwitchOverviewResponse 200 `
        "pre-content-switch overview"
    $beforeContentSwitchOverview = Convert-ResponseJson `
        $beforeContentSwitchOverviewResponse "pre-content-switch overview"
    $beforeContentSwitchLedgerResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/ledger"
    Assert-Status $beforeContentSwitchLedgerResponse 200 `
        "pre-content-switch ledger"
    $beforeContentSwitchLedger = Convert-ResponseJson `
        $beforeContentSwitchLedgerResponse "pre-content-switch ledger"
    $beforeContentSwitchRunsResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/runs"
    Assert-Status $beforeContentSwitchRunsResponse 200 `
        "pre-content-switch runs"
    $beforeContentSwitchRuns = Convert-ResponseJson `
        $beforeContentSwitchRunsResponse "pre-content-switch runs"
    $beforeContentSwitchState = Get-FakeState
    $beforeContentSwitchInventory = [long](Get-ObjectProperty (
        Get-ObjectProperty $beforeContentSwitchState.pdInventories "steam_111") "Leather")
    $beforeEvidenceResponse = Invoke-TestRequest $script:adminClient "GET" `
        "$script:apiBase/api/v1/extraction/admin/operations/runs/$($quote.runId)/evidence" `
        $null (Admin-Headers "capture quote A evidence before content switch")
    Assert-Status $beforeEvidenceResponse 200 "pre-content-switch run evidence"
    $beforeEvidence = Convert-ResponseJson $beforeEvidenceResponse `
        "pre-content-switch run evidence"
    if ([string]$beforeEvidence.run.state -ne "Quoted" -or
        [string]$beforeEvidence.run.contentVersionId -ne [string]$catalog.contentVersionId -or
        $null -ne $beforeEvidence.settlement.settlementIdempotencyKey -or
        $null -ne $beforeEvidence.settlement.settlementRequestHash -or
        [int]$beforeEvidence.settlement.attemptCount -ne 0) {
        throw "Quote A was not pristine before the content switch: $($beforeEvidenceResponse.Text)"
    }

    $currentContentResponse = Invoke-TestRequest $script:adminClient "GET" `
        "$script:apiBase/api/v1/servers/local/economy-content/current" $null `
        (Admin-Headers "read content A for stale quote test")
    Assert-Status $currentContentResponse 200 "read current content A"
    $currentContent = Convert-ResponseJson $currentContentResponse "read current content A"
    if ([string]$currentContent.version.versionId -ne [string]$beforeEvidence.run.contentVersionId) {
        throw "Quote A did not bind the current content pointer before publication."
    }
    $currentContent.version.definition.displayName =
        ([string]$currentContent.version.definition.displayName) + " / black-box content B"
    $draftBody = @{
        name = "black-box quote invalidation B"
        basedOnVersionId = [string]$currentContent.version.versionId
        definition = $currentContent.version.definition
    } | ConvertTo-Json -Depth 100 -Compress
    $draftResponse = Invoke-TestRequest $script:adminClient "POST" `
        "$script:apiBase/api/v1/servers/local/economy-content/drafts" $draftBody `
        (Admin-Headers "create content B for stale quote test")
    Assert-Status $draftResponse 201 "create content B draft"
    $draft = Convert-ResponseJson $draftResponse "create content B draft"

    Set-Maintenance $true "atomically switch quote test content A to B"
    try {
        $publishHeaders = Admin-Headers "publish content B for stale quote test"
        $publishHeaders["If-Match"] = [string]$draft.revision
        $publishHeaders["Idempotency-Key"] = "quote-content-switch-publish-0001"
        $publishBody = @{
            businessDate = [string]$currentContent.version.businessDate
            reason = "Prove stale quote rejection before inventory dispatch"
            confirmation = "PUBLISH ECONOMY CONTENT"
        } | ConvertTo-Json -Compress
        $publishResponse = Invoke-TestRequest $script:adminClient "POST" `
            "$script:apiBase/api/v1/servers/local/economy-content/drafts/$($draft.draftId)/publish" `
            $publishBody $publishHeaders
        Assert-Status $publishResponse 200 "publish content B"
        $publishedContent = Convert-ResponseJson $publishResponse "publish content B"
        if ([string]$publishedContent.pointer.versionId -eq [string]$beforeEvidence.run.contentVersionId -or
            [string]$publishedContent.pointer.versionId -ne [string]$publishedContent.version.versionId) {
            throw "Content B did not atomically replace current pointer A: $($publishResponse.Text)"
        }
    }
    finally {
        Set-Maintenance $false "content B pointer switch complete"
    }

    $staleSelectBody = @{
        sourceRevision = [long]$quote.revision
        items = @(@{
            itemId = [string]@($quote.items)[0].itemId
            quantity = 1
        })
    } | ConvertTo-Json -Depth 10 -Compress
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/select" `
            $staleSelectBody @{
                Origin = $script:playerOrigin
                "X-CSRF-Token" = $aliceCsrf
                "Idempotency-Key" = "alice-stale-content-select-0001"
            }) `
        409 "QUOTE_CONTENT_CHANGED" "content A selective quote after pointer switch"

    $staleSettleResponse = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/settle" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
            "Idempotency-Key" = "alice-stale-content-settle-0001"
        }
    Assert-ErrorCode $staleSettleResponse 409 "QUOTE_CONTENT_CHANGED" `
        "content A quote after atomic pointer switch to B"

    $afterContentSwitchOverviewResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/overview"
    Assert-Status $afterContentSwitchOverviewResponse 200 `
        "post-stale-quote overview"
    $afterContentSwitchOverview = Convert-ResponseJson `
        $afterContentSwitchOverviewResponse "post-stale-quote overview"
    $afterContentSwitchLedgerResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/ledger"
    Assert-Status $afterContentSwitchLedgerResponse 200 `
        "post-stale-quote ledger"
    $afterContentSwitchLedger = Convert-ResponseJson `
        $afterContentSwitchLedgerResponse "post-stale-quote ledger"
    $afterContentSwitchRunsResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/runs"
    Assert-Status $afterContentSwitchRunsResponse 200 `
        "post-stale-quote runs"
    $afterContentSwitchRuns = Convert-ResponseJson `
        $afterContentSwitchRunsResponse "post-stale-quote runs"
    $afterContentSwitchState = Get-FakeState
    $afterContentSwitchInventory = [long](Get-ObjectProperty (
        Get-ObjectProperty $afterContentSwitchState.pdInventories "steam_111") "Leather")
    $afterEvidenceResponse = Invoke-TestRequest $script:adminClient "GET" `
        "$script:apiBase/api/v1/extraction/admin/operations/runs/$($quote.runId)/evidence" `
        $null (Admin-Headers "verify stale quote has no side effects")
    Assert-Status $afterEvidenceResponse 200 "post-stale-quote run evidence"
    $afterEvidence = Convert-ResponseJson $afterEvidenceResponse `
        "post-stale-quote run evidence"

    if ([int]$afterContentSwitchState.rconDeleteCount -ne
            [int]$beforeContentSwitchState.rconDeleteCount -or
        $afterContentSwitchInventory -ne $beforeContentSwitchInventory) {
        throw "A stale content quote dispatched inventory deletion or changed Leather: " +
            "delete $($beforeContentSwitchState.rconDeleteCount)->$($afterContentSwitchState.rconDeleteCount), " +
            "Leather $beforeContentSwitchInventory->$afterContentSwitchInventory."
    }
    if ([long]$afterContentSwitchOverview.balances.weeklyTicket -ne
            [long]$beforeContentSwitchOverview.balances.weeklyTicket -or
        [long]$afterContentSwitchOverview.balances.merchantCoin -ne
            [long]$beforeContentSwitchOverview.balances.merchantCoin -or
        ($afterContentSwitchLedger | ConvertTo-Json -Depth 20 -Compress) -ne
            ($beforeContentSwitchLedger | ConvertTo-Json -Depth 20 -Compress)) {
        throw "A stale content quote changed the wallet or ledger."
    }
    $beforePlayerRun = @($beforeContentSwitchRuns.items | Where-Object {
        [string]$_.runId -eq [string]$quote.runId
    }) | Select-Object -First 1
    $afterPlayerRun = @($afterContentSwitchRuns.items | Where-Object {
        [string]$_.runId -eq [string]$quote.runId
    }) | Select-Object -First 1
    if ($null -eq $beforePlayerRun -or $null -eq $afterPlayerRun -or
        [string]$beforePlayerRun.internalState -ne "Quoted" -or
        [string]$afterPlayerRun.internalState -ne "Quoted" -or
        [long]$afterEvidence.run.revision -ne [long]$beforeEvidence.run.revision -or
        [string]$afterEvidence.run.updatedAt -ne [string]$beforeEvidence.run.updatedAt -or
        [string]$afterEvidence.run.contentVersionId -ne [string]$beforeEvidence.run.contentVersionId -or
        [string]$afterEvidence.run.contentHash -ne [string]$beforeEvidence.run.contentHash -or
        $null -ne $afterEvidence.settlement.settlementIdempotencyKey -or
        $null -ne $afterEvidence.settlement.settlementRequestHash -or
        $null -ne $afterEvidence.settlement.nativeConsumeReceipt -or
        [int]$afterEvidence.settlement.attemptCount -ne 0) {
        throw "QUOTE_CONTENT_CHANGED mutated the quoted run or settlement evidence: $($afterEvidenceResponse.Text)"
    }

    # Continue the happy path only with a fresh quote carrying content B.
    $freshQuoteResponse = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/quote" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
        }
    Assert-Status $freshQuoteResponse 200 "fresh content B resource-exchange quote"
    $quote = Convert-ResponseJson $freshQuoteResponse "fresh content B resource-exchange quote"
    if ([long]$quote.totalValue -le 0 -or @($quote.items).Count -eq 0) {
        throw "The fresh content B quote did not contain positive whitelist value."
    }

    $sourceQuote = $quote
    $selectedSourceItem = @($sourceQuote.items | Where-Object {
        [string]$_.itemId -eq "Bone"
    }) | Select-Object -First 1
    if ($null -eq $selectedSourceItem -or [int]$selectedSourceItem.quantity -lt 2) {
        throw "The selective-sale HTTP fixture did not expose Bone x2."
    }
    $selectionKey = "alice-resource-selection-0001"
    $selectionBody = @{
        sourceRevision = [long]$sourceQuote.revision
        items = @(@{ itemId = "bone"; quantity = 2 })
    } | ConvertTo-Json -Depth 10 -Compress
    $selectionResponse = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/$($sourceQuote.runId)/select" `
        $selectionBody @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
            "Idempotency-Key" = $selectionKey
        }
    Assert-Status $selectionResponse 200 "select Bone x2 child quote"
    $selectedQuote = Convert-ResponseJson $selectionResponse "select Bone x2 child quote"
    $expectedSelectedValue = 2L * [long]$selectedSourceItem.unitValue
    if ($selectedQuote.selectionDerived -ne $true -or
        [string]$selectedQuote.sourceQuoteRunId -ne [string]$sourceQuote.runId -or
        [long]$selectedQuote.revision -ne 1 -or
        @($selectedQuote.items).Count -ne 1 -or
        [string]@($selectedQuote.items)[0].itemId -ne "Bone" -or
        [int]@($selectedQuote.items)[0].quantity -ne 2 -or
        [long]$selectedQuote.totalValue -ne $expectedSelectedValue) {
        throw "Selected child quote did not contain exactly canonical Bone x2 / value $expectedSelectedValue`: $($selectionResponse.Text)"
    }
    for ($replayIndex = 0; $replayIndex -lt 20; $replayIndex++) {
        $selectionReplay = Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/runs/$($sourceQuote.runId)/select" `
            $selectionBody @{
                Origin = $script:playerOrigin
                "X-CSRF-Token" = $aliceCsrf
                "Idempotency-Key" = $selectionKey
            }
        Assert-Status $selectionReplay 200 "selection replay $replayIndex"
        $replayedSelection = Convert-ResponseJson $selectionReplay "selection replay $replayIndex"
        if ([string]$replayedSelection.runId -ne [string]$selectedQuote.runId) {
            throw "Selection replay $replayIndex created a different child."
        }
    }
    $conflictingSelectionBody = @{
        sourceRevision = [long]$sourceQuote.revision
        items = @(@{ itemId = "Bone"; quantity = 1 })
    } | ConvertTo-Json -Depth 10 -Compress
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/runs/$($sourceQuote.runId)/select" `
            $conflictingSelectionBody @{
                Origin = $script:playerOrigin
                "X-CSRF-Token" = $aliceCsrf
                "Idempotency-Key" = $selectionKey
            }) `
        409 "IDEMPOTENCY_CONFLICT" "same selection key with different quantity"
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/runs/$($sourceQuote.runId)/settle" $null @{
                Origin = $script:playerOrigin
                "X-CSRF-Token" = $aliceCsrf
                "Idempotency-Key" = "cancelled-source-settle-0001"
            }) `
        409 "EXTRACTION_QUOTE_NOT_SETTLEABLE" "cancelled source cannot settle"
    $quote = $selectedQuote

    $beforeSettleOverviewResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/overview"
    Assert-Status $beforeSettleOverviewResponse 200 "pre-settlement overview"
    $beforeSettleOverview = Convert-ResponseJson $beforeSettleOverviewResponse `
        "pre-settlement overview"
    $beforeSettleState = Get-FakeState
    $beforeSettleLeather = [long](Get-ObjectProperty (
        Get-ObjectProperty $beforeSettleState.pdInventories "steam_111") "Leather")
    $beforeSettleBone = [long](Get-ObjectProperty (
        Get-ObjectProperty $beforeSettleState.pdInventories "steam_111") "Bone")
    $settlementKey = "alice-resource-settle-0001"
    $settledResponse = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/settle" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
            "Idempotency-Key" = $settlementKey
        }
    Assert-Status $settledResponse 200 "resource-exchange settlement"
    $settled = Convert-ResponseJson $settledResponse "resource-exchange settlement"
    if ($settled.state -ne "extracted") {
        throw "The resource exchange did not reach settled: $($settledResponse.Text)"
    }
    $afterSettleState = Get-FakeState
    if ([int]$afterSettleState.rconDeleteCount -ne
        ([int]$beforeSettleState.rconDeleteCount + 1)) {
        throw "The settled exchange did not issue exactly one fake RCON deletion."
    }
    $afterSettleLeather = [long](Get-ObjectProperty (
        Get-ObjectProperty $afterSettleState.pdInventories "steam_111") "Leather")
    $afterSettleBone = [long](Get-ObjectProperty (
        Get-ObjectProperty $afterSettleState.pdInventories "steam_111") "Bone")
    $lastDeleteCommand = [string]@($afterSettleState.rconCommands)[-1]
    if ($afterSettleLeather -ne $beforeSettleLeather -or
        $afterSettleBone -ne ($beforeSettleBone - 2) -or
        $lastDeleteCommand -ne "delitems steam_111 Bone:2" -or
        $lastDeleteCommand.IndexOf("Leather", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Selective settlement touched an unselected item or sent an inexact RCON command: " +
            "Leather $beforeSettleLeather->$afterSettleLeather, Bone $beforeSettleBone->$afterSettleBone, command '$lastDeleteCommand'."
    }
    $afterSettleOverviewResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/overview"
    Assert-Status $afterSettleOverviewResponse 200 "post-settlement overview"
    $afterSettleOverview = Convert-ResponseJson $afterSettleOverviewResponse `
        "post-settlement overview"
    if ([long]$afterSettleOverview.balances.weeklyTicket -ne
        ([long]$beforeSettleOverview.balances.weeklyTicket + [long]$quote.totalValue)) {
        throw "SQLite wallet credit did not equal the settled whitelist value."
    }

    $settleReplay = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/settle" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
            "Idempotency-Key" = $settlementKey
        }
    Assert-Status $settleReplay 200 "exact settlement replay"
    if ([int](Get-FakeState).rconDeleteCount -ne [int]$afterSettleState.rconDeleteCount) {
        throw "An exact settlement replay dispatched a duplicate deletion."
    }
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/settle" $null @{
                Origin = $script:playerOrigin
                "X-CSRF-Token" = $aliceCsrf
                "Idempotency-Key" = "alice-resource-settle-conflict"
            }) `
        409 "IDEMPOTENCY_CONFLICT" "conflicting settlement replay"

    # Publish an intentionally all-closed schedule and prove the public quote
    # endpoint fails closed with a machine-readable earliest next opening.
    $contentBResponse = Invoke-TestRequest $script:adminClient "GET" `
        "$script:apiBase/api/v1/servers/local/economy-content/current" $null `
        (Admin-Headers "read content B for all-closed schedule test")
    Assert-Status $contentBResponse 200 "read current content B"
    $contentB = Convert-ResponseJson $contentBResponse "read current content B"
    $shanghai = [TimeZoneInfo]::FindSystemTimeZoneById("China Standard Time")
    $nowInShanghai = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $shanghai)
    if ($nowInShanghai.TimeOfDay -lt [TimeSpan]::FromHours(22)) {
        $futureWindow = $nowInShanghai.AddHours(1)
        $closedWindowOpensAt = $futureWindow.TimeOfDay
        $closedWindowClosesAt = $futureWindow.AddMinutes(30).TimeOfDay
    }
    else {
        # Keep the window non-wrapping. Today's 00:30 window has already
        # passed, so the next matching weekday remains safely in the future.
        $closedWindowOpensAt = [TimeSpan]::FromMinutes(30)
        $closedWindowClosesAt = [TimeSpan]::FromHours(1)
    }
    $dailyClosedWindows = @(0..6 | ForEach-Object {
        @{
            dayOfWeek = $_
            opensAt = $closedWindowOpensAt.ToString("hh\:mm\:ss")
            closesAt = $closedWindowClosesAt.ToString("hh\:mm\:ss")
            graceSeconds = 60
        }
    })
    foreach ($zone in @($contentB.version.definition.exchangeZones)) {
        $zone.openWindows = $dailyClosedWindows
    }
    $closedDraftBody = @{
        name = "black-box all zones closed"
        basedOnVersionId = [string]$contentB.version.versionId
        definition = $contentB.version.definition
    } | ConvertTo-Json -Depth 100 -Compress
    $closedDraftResponse = Invoke-TestRequest $script:adminClient "POST" `
        "$script:apiBase/api/v1/servers/local/economy-content/drafts" `
        $closedDraftBody (Admin-Headers "create all-closed content draft")
    Assert-Status $closedDraftResponse 201 "create all-closed content draft"
    $closedDraft = Convert-ResponseJson $closedDraftResponse `
        "create all-closed content draft"
    Set-Maintenance $true "publish all-closed schedule test content"
    try {
        $closedPublishHeaders = Admin-Headers "publish all-closed schedule test content"
        $closedPublishHeaders["If-Match"] = [string]$closedDraft.revision
        $closedPublishHeaders["Idempotency-Key"] = "all-zones-closed-publish-0001"
        $closedPublishBody = @{
            businessDate = [string]$contentB.version.businessDate
            reason = "Prove quote rejection and earliest next opening"
            confirmation = "PUBLISH ECONOMY CONTENT"
        } | ConvertTo-Json -Compress
        $closedPublishResponse = Invoke-TestRequest $script:adminClient "POST" `
            "$script:apiBase/api/v1/servers/local/economy-content/drafts/$($closedDraft.draftId)/publish" `
            $closedPublishBody $closedPublishHeaders
        Assert-Status $closedPublishResponse 200 "publish all-closed schedule"
    }
    finally {
        Set-Maintenance $false "all-closed schedule test content published"
    }
    $closedQuoteResponse = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/quote" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
        }
    Assert-ErrorCode $closedQuoteResponse 409 "EXTRACTION_ZONE_CLOSED" `
        "all zones closed quote"
    $closedQuoteError = Convert-ResponseJson $closedQuoteResponse `
        "all zones closed quote"
    $parsedNextOpen = [DateTimeOffset]::MinValue
    if ([string]::IsNullOrWhiteSpace([string]$closedQuoteError.nextOpensAt) -or
        -not [DateTimeOffset]::TryParse(
            [string]$closedQuoteError.nextOpensAt,
            [ref]$parsedNextOpen) -or
        $parsedNextOpen -le [DateTimeOffset]::UtcNow) {
        throw "All-closed quote did not return a future nextOpensAt: $($closedQuoteResponse.Text)"
    }

    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "POST" `
            "$script:apiBase/api/v1/player/auth/logout" $null @{
                Origin = $script:playerOrigin
            }) `
        403 "CSRF_TOKEN_INVALID" "logout without CSRF"
    $logout = Invoke-TestRequest $aliceClient "POST" `
        "$script:apiBase/api/v1/player/auth/logout" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $aliceCsrf
        }
    Assert-Status $logout 204 "player logout"
    Assert-ErrorCode (
        Invoke-TestRequest $aliceClient "GET" `
            "$script:apiBase/api/v1/player/auth/session") `
        401 "PLAYER_SESSION_REQUIRED" "revoked player session"

    [pscustomobject]@{
        login = "in-game-code"
        originRejected = $true
        csrfRejected = $true
        playerNotificationPolicyBoundaryVerified = $true
        teamEconomyPolicyBoundaryVerified = $true
        concurrentMapReads = 100
        identityOverrideRejected = $true
        crossPlayerOrderHidden = $true
        crossPlayerSettlementRejected = $true
        settlementAdapter = $settlementStatus.adapter
        deprecatedSettlementStatusAlias = $true
        initialWorldBindingPersisted = $true
        catalogContentVersion = $catalog.contentVersionId
        catalogEvidenceVerified = $true
        staleOfferRejected = $true
        staleQuoteContentRejected = $true
        staleQuoteNoInventoryDispatch = $true
        staleQuoteNoWalletLedgerRunMutation = $true
        allClosedQuoteRejectedWithNextOpen = $true
        orderId = $createdOrder.orderId
        deliveryState = $deliveredOrder.state
        settlementRunId = $quote.runId
        settlementState = $settled.state
        settledValue = [long]$quote.totalValue
        duplicateGrantPrevented = $true
        duplicateConsumePrevented = $true
    } | ConvertTo-Json -Compress
}
catch {
    if ($null -ne $fake) {
        $fake.Refresh()
        Write-Host "Fake dependency exited=$($fake.HasExited) pid=$($fake.Id)."
        if ($fake.HasExited) { Write-Host "Fake dependency exit code=$($fake.ExitCode)." }
        $fakeProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($fake.Id)" `
            -ErrorAction SilentlyContinue
        if ($null -ne $fakeProcess) { Write-Host $fakeProcess.CommandLine }
    }
    if (Test-Path -LiteralPath $fakeStdout) { Write-Host (Get-Content -Raw $fakeStdout) }
    if (Test-Path -LiteralPath $fakeStderr) { Write-Host (Get-Content -Raw $fakeStderr) }
    Get-ChildItem -LiteralPath $script:testRoot -Filter "control-api-*.out.log" `
        -ErrorAction SilentlyContinue | ForEach-Object { Write-Host (Get-Content -Raw $_.FullName) }
    Get-ChildItem -LiteralPath $script:testRoot -Filter "control-api-*.err.log" `
        -ErrorAction SilentlyContinue | ForEach-Object { Write-Host (Get-Content -Raw $_.FullName) }
    throw
}
finally {
    Stop-TestProcess $api
    Stop-TestProcess $fake
    foreach ($client in @($aliceClient, $bobClient, $anonymousClient,
            $script:adminClient, $script:probeClient)) {
        if ($null -ne $client) { $client.Dispose() }
    }
    if (Test-Path -LiteralPath $script:testRoot) {
        Remove-TestTree $script:testRoot
    }
}

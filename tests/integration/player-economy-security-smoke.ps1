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
    }
}

function Set-Maintenance([bool] $enabled, [string] $reason) {
    $response = Invoke-TestRequest $script:adminClient "POST" `
        "$script:apiBase/api/v1/extraction/admin/rollover/maintenance" `
        (@{ maintenance = $enabled; reason = $reason } | ConvertTo-Json -Compress) `
        (Admin-Headers $reason)
    Assert-Status $response 200 "set rollover maintenance=$enabled"
}

function Write-SyntheticResourceCatalog([string] $contentRoot) {
    # The redistributable repository deliberately excludes the real game-data
    # catalog. Keep this integration test self-contained by writing the smallest
    # synthetic catalog required by the built-in shop products to the isolated
    # test content root.
    $resourceRoot = Join-Path $contentRoot "Resources"
    New-Item -ItemType Directory -Force -Path $resourceRoot | Out-Null
    $catalog = @'
{
  "schemaVersion": "synthetic-test-v1",
  "revision": "player-economy-smoke",
  "generatedAt": "2026-01-01T00:00:00Z",
  "source": {
    "name": "synthetic integration-test fixture",
    "note": "Contains identifiers already referenced by the test target; no game data is redistributed.",
    "itemsUrl": "https://example.invalid/synthetic-items",
    "palsUrl": "https://example.invalid/synthetic-pals",
    "technologiesUrl": "https://example.invalid/synthetic-technologies"
  },
  "coverage": {
    "items": "built-in test products only",
    "pals": "single synthetic validation entry",
    "eggs": "none",
    "technologies": "none",
    "templates": "none"
  },
  "items": [
    { "id": "PalSphere", "name": "Synthetic PalSphere", "category": "Item" },
    { "id": "Baked_Berries", "name": "Synthetic Baked Berries", "category": "Item" },
    { "id": "Herbs", "name": "Synthetic Herbs", "category": "Item" },
    { "id": "Medicines", "name": "Synthetic Medicines", "category": "Item" },
    { "id": "PalSphere_Mega", "name": "Synthetic Mega Sphere", "category": "Item" },
    { "id": "RoughBullet", "name": "Synthetic Rough Bullet", "category": "Item" },
    { "id": "BowGun", "name": "Synthetic Crossbow", "category": "Item" },
    { "id": "Arrow", "name": "Synthetic Arrow", "category": "Item" }
  ],
  "pals": [
    { "id": "SyntheticPal", "name": "Synthetic Pal", "category": "Pal" }
  ],
  "eggs": [],
  "technologies": []
}
'@
    [IO.File]::WriteAllText(
        (Join-Path $resourceRoot "palworld-resource-catalog.json"),
        $catalog,
        [Text.UTF8Encoding]::new($false))
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
        "--Palworld:OfficialRestApi:TimeoutSeconds=2",
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
        "--ExtractionMode:Rcon:ApprovedGameVersion=1.0.0.100427",
        "--ExtractionMode:Rcon:ApprovedPalDefenderVersion=1.8.1.3933",
        "--ExtractionMode:Rcon:Password=integration-rcon-password",
        "--ExtractionMode:ExtractionZones:0:Id=portal-e2e-zone",
        "--ExtractionMode:ExtractionZones:0:DisplayName=Portal E2E Zone",
        "--ExtractionMode:ExtractionZones:0:RouteHint=Remain near the test origin.",
        "--ExtractionMode:ExtractionZones:0:MapX=0",
        "--ExtractionMode:ExtractionZones:0:MapY=0",
        "--ExtractionMode:ExtractionZones:0:Radius=1000",
        "--PlayerPortal:Enabled=true",
        "--PlayerPortal:PublicSteam=false",
        "--PlayerPortal:CookieSecure=false",
        "--PlayerPortal:AllowedOrigins:0=$script:playerOrigin",
        "--PlayerPortal:UserCooldownSeconds=1",
        "--PlayerPortal:IpCooldownSeconds=1",
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
    $product = @($catalog.items | Where-Object {
        $_.enabled -eq $true -and $_.price.currency -eq "merchantCoin" -and
            [long]$_.price.amount -le [long]$aliceOverview.balances.merchantCoin
    }) | Select-Object -First 1
    if ($null -eq $product) {
        throw "The player catalog contained no affordable merchantCoin product."
    }

    $beforeDelivery = Get-FakeState
    $orderKey = "player-shop-order-e2e-0001"
    $orderBody = @{
        productId = [string]$product.productId
        quantity = 1
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

    $bobSettle = Invoke-TestRequest $bobClient "POST" `
        "$script:apiBase/api/v1/player/me/runs/$($quote.runId)/settle" $null @{
            Origin = $script:playerOrigin
            "X-CSRF-Token" = $bobCsrf
            "Idempotency-Key" = "bob-idor-settle-0001"
        }
    Assert-ErrorCode $bobSettle 403 "EXTRACTION_RUN_SCOPE_MISMATCH" `
        "cross-player settlement IDOR"

    $beforeSettleOverviewResponse = Invoke-TestRequest $aliceClient "GET" `
        "$script:apiBase/api/v1/player/me/overview"
    Assert-Status $beforeSettleOverviewResponse 200 "pre-settlement overview"
    $beforeSettleOverview = Convert-ResponseJson $beforeSettleOverviewResponse `
        "pre-settlement overview"
    $beforeSettleState = Get-FakeState
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
        identityOverrideRejected = $true
        crossPlayerOrderHidden = $true
        crossPlayerSettlementRejected = $true
        settlementAdapter = $settlementStatus.adapter
        deprecatedSettlementStatusAlias = $true
        initialWorldBindingPersisted = $true
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

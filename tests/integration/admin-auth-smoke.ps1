$ErrorActionPreference = "Stop"
$env:PlayerPortal__Enabled = "false"
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
        $bytes = [Text.Encoding]::UTF8.GetBytes($value)
        $hash = $algorithm.ComputeHash($bytes)
        return ([BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-Totp {
    $secret = [Text.Encoding]::ASCII.GetBytes("12345678901234567890")
    $step = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30
    $counter = [BitConverter]::GetBytes([long][Math]::Floor($step))
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

function Wait-ForEndpoint([string] $uri, [Diagnostics.Process] $process) {
    for ($attempt = 0; $attempt -lt 100; $attempt += 1) {
        if ($process.HasExited) {
            throw "Control API exited before it became ready (exit $($process.ExitCode))."
        }
        try {
            $response = $script:HttpClient.GetAsync($uri).GetAwaiter().GetResult()
            if ([int]$response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }
    throw "Control API did not become ready: $uri"
}

function Invoke-Api(
    [string] $method,
    [string] $uri,
    [hashtable] $headers = @{},
    [object] $body = $null) {
    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::new($method),
        $uri)
    try {
        foreach ($entry in $headers.GetEnumerator()) {
            if (-not $request.Headers.TryAddWithoutValidation(
                    [string]$entry.Key,
                    [string]$entry.Value)) {
                throw "Could not add request header '$($entry.Key)'."
            }
        }
        if ($null -ne $body) {
            $json = $body | ConvertTo-Json -Depth 8 -Compress
            $request.Content = [Net.Http.StringContent]::new(
                $json,
                [Text.Encoding]::UTF8,
                "application/json")
        }
        $response = $script:HttpClient.SendAsync($request).GetAwaiter().GetResult()
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{
            status = [int]$response.StatusCode
            body = if ([string]::IsNullOrWhiteSpace($content)) {
                $null
            }
            else {
                $content | ConvertFrom-Json
            }
        }
    }
    finally {
        $request.Dispose()
    }
}

function Assert-Status([object] $response, [int] $expected, [string] $message) {
    if ($response.status -ne $expected) {
        throw "$message Expected HTTP $expected, received $($response.status)."
    }
}

function Remove-TestTree([string] $path) {
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $target = [IO.Path]::GetFullPath($path)
    if (-not $target.StartsWith(
            $tempRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($target) -notlike "pal-control-admin-auth-smoke-*") {
        throw "Refusing to remove unexpected test path '$target'."
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serviceRoot = Join-Path $repositoryRoot "services\control-api"
$project = Join-Path $serviceRoot "PalControl.ControlApi.csproj"
$testRoot = Join-Path $env:TEMP (
    "pal-control-admin-auth-smoke-" + [Guid]::NewGuid().ToString("N"))
$buildRoot = Join-Path $testRoot "build"
$dataRoot = Join-Path $testRoot "data"
$stdout = Join-Path $testRoot "api.out.log"
$stderr = Join-Path $testRoot "api.err.log"
$port = Get-FreeTcpPort
$baseUri = "http://127.0.0.1:$port"
$viewerKey = "viewer-test-key-000000000000"
$operatorKey = "operator-test-key-000000000"
$seasonKey = "season-test-key-000000000000"
$ownerKey = "owner-test-key-0000000000000"
$totpSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$script:HttpClient = [Net.Http.HttpClient]::new($handler)
$script:HttpClient.Timeout = [TimeSpan]::FromSeconds(5)
$api = $null

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    & dotnet build $project --configuration Release --output $buildRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Control API build failed with exit code $LASTEXITCODE."
    }
    $apiDll = Join-Path $buildRoot "PalControl.ControlApi.dll"
    $arguments = @(
        $apiDll,
        "--urls=$baseUri",
        "--Security:DevelopmentMode=false",
        "--Security:AdminAuthentication:Enabled=true",
        "--Security:AdminAuthentication:EnableLoopbackDevelopmentPrincipal=false",
        "--Security:AdminAuthentication:Principals:0:Subject=viewer-a",
        "--Security:AdminAuthentication:Principals:0:ApiKeySha256=$(Get-Sha256Hex $viewerKey)",
        "--Security:AdminAuthentication:Principals:0:Roles:0=Viewer",
        "--Security:AdminAuthentication:Principals:1:Subject=operator-a",
        "--Security:AdminAuthentication:Principals:1:ApiKeySha256=$(Get-Sha256Hex $operatorKey)",
        "--Security:AdminAuthentication:Principals:1:Roles:0=Operator",
        "--Security:AdminAuthentication:Principals:2:Subject=season-a",
        "--Security:AdminAuthentication:Principals:2:ApiKeySha256=$(Get-Sha256Hex $seasonKey)",
        "--Security:AdminAuthentication:Principals:2:Roles:0=SeasonAdmin",
        "--Security:AdminAuthentication:Principals:2:TotpSecretBase32=$totpSecret",
        "--Security:AdminAuthentication:Principals:3:Subject=owner-a",
        "--Security:AdminAuthentication:Principals:3:ApiKeySha256=$(Get-Sha256Hex $ownerKey)",
        "--Security:AdminAuthentication:Principals:3:Roles:0=Owner",
        "--Security:AdminAuthentication:Principals:3:TotpSecretBase32=$totpSecret",
        "--ExtractionMode:Enabled=false",
        "--ExtractionMode:BootstrapPolicyVersion=production-v1",
        "--ExtractionMode:InitialMarketCoin=0",
        "--ExtractionMode:InitialSeasonVoucher=0",
        "--ExtractionMode:Rcon:Enabled=false",
        "--ExtractionMode:Persistence:DataDirectory=$dataRoot",
        "--PlayerPortal:Enabled=false",
        "--CommandPersistence:DataDirectory=$(Join-Path $testRoot 'commands')"
    )
    $api = Start-Process -FilePath "dotnet" -ArgumentList $arguments `
        -WorkingDirectory $serviceRoot -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Wait-ForEndpoint "$baseUri/health/live" $api

    Assert-Status (Invoke-Api "GET" "$baseUri/api/v1/admin/session") 401 `
        "Anonymous administrator access was not rejected."
    Assert-Status (Invoke-Api "GET" "$baseUri/api/v1/admin/session" @{
        "X-Pal-Admin-Key" = "invalid-test-key-000000000000"
    }) 401 "An invalid administrator key was not rejected."

    $viewer = Invoke-Api "GET" "$baseUri/api/v1/admin/session" @{
        "X-Pal-Admin-Key" = $viewerKey
    }
    Assert-Status $viewer 200 "Viewer could not read administrator session."
    if ($viewer.body.subject -ne "viewer-a" -or
        @($viewer.body.roles).Count -ne 1 -or
        $viewer.body.roles[0] -ne "Viewer") {
        throw "Viewer identity or role claims were incorrect."
    }

    $maintenanceUri = "$baseUri/api/v1/extraction/admin/rollover/maintenance"
    $maintenanceBody = @{ maintenance = $true; reason = "admin auth smoke" }
    Assert-Status (Invoke-Api "POST" $maintenanceUri @{
        "X-Pal-Admin-Key" = $viewerKey
    } $maintenanceBody) 403 "Viewer was allowed to write."
    Assert-Status (Invoke-Api "POST" $maintenanceUri @{
        "X-Pal-Admin-Key" = $operatorKey
    } $maintenanceBody) 403 "Operator bypassed SeasonAdmin authorization."
    Assert-Status (Invoke-Api "POST" $maintenanceUri @{
        "X-Pal-Admin-Key" = $seasonKey
    } $maintenanceBody) 403 "SeasonAdmin bypassed TOTP."
    Assert-Status (Invoke-Api "POST" $maintenanceUri @{
        "X-Pal-Admin-Key" = $seasonKey
        "X-Pal-Admin-Totp" = "000000"
    } $maintenanceBody) 403 "SeasonAdmin used an invalid TOTP."
    Assert-Status (Invoke-Api "POST" $maintenanceUri @{
        "X-Pal-Admin-Key" = $seasonKey
        "X-Pal-Admin-Totp" = Get-Totp
    } $maintenanceBody) 403 "SeasonAdmin bypassed the audited reason requirement."

    $seasonWrite = Invoke-Api "POST" $maintenanceUri @{
        "X-Pal-Admin-Key" = $seasonKey
        "X-Pal-Admin-Totp" = Get-Totp
        "X-Pal-Admin-Reason" = "admin auth smoke"
        "X-Correlation-ID" = "11111111-2222-3333-4444-555555555555"
    } $maintenanceBody
    Assert-Status $seasonWrite 200 "SeasonAdmin with TOTP could not enter maintenance."
    if (-not $seasonWrite.body.maintenance -or $seasonWrite.body.actor -ne "season-a") {
        throw "Maintenance state did not persist the authenticated administrator actor."
    }

    $circuitUri = "$baseUri/api/v1/extraction/admin/safety-gate/purchase"
    $circuitHeaders = @{
        "X-Pal-Admin-Key" = $ownerKey
        "X-Pal-Admin-Totp" = Get-Totp
        "X-Pal-Admin-Reason" = "purchase circuit smoke"
    }
    $closedCircuit = Invoke-Api "PUT" $circuitUri $circuitHeaders @{
        writesEnabled = $false
        reason = "purchase circuit smoke"
    }
    Assert-Status $closedCircuit 200 "Owner could not close the purchase circuit."
    if ($closedCircuit.body.purchase.writesEnabled -or
        -not $closedCircuit.body.resourceExchange.writesEnabled) {
        throw "Purchase and resource-exchange circuits were not independent."
    }
    $closedCapabilities = Invoke-Api "GET" `
        "$baseUri/api/v1/extraction/capabilities" @{
            "X-Pal-Admin-Key" = $ownerKey
        }
    Assert-Status $closedCapabilities 200 "Safety-gate capabilities became unavailable."
    if (@($closedCapabilities.body.writes.purchase.blockers | Where-Object {
            $_.code -eq "PURCHASE_CIRCUIT_OPEN"
        }).Count -ne 1 -or
        @($closedCapabilities.body.writes.resourceExchange.blockers | Where-Object {
            $_.code -eq "RESOURCE_EXCHANGE_CIRCUIT_OPEN"
        }).Count -ne 0) {
        throw "Capabilities did not expose the independent stable circuit blocker."
    }
    $reopenedCircuit = Invoke-Api "PUT" $circuitUri $circuitHeaders @{
        writesEnabled = $true
        reason = "purchase circuit recovered"
    }
    Assert-Status $reopenedCircuit 200 "Owner could not reopen purchase without a restart."
    $reopenedCapabilities = Invoke-Api "GET" `
        "$baseUri/api/v1/extraction/capabilities" @{
            "X-Pal-Admin-Key" = $ownerKey
        }
    if (@($reopenedCapabilities.body.writes.purchase.blockers | Where-Object {
            $_.code -eq "PURCHASE_CIRCUIT_OPEN"
        }).Count -ne 0) {
        throw "Reopening purchase still exposed a stale circuit blocker."
    }

    $playerResponse = Invoke-Api "GET" "$baseUri/api/v1/player/auth/session"
    if ($playerResponse.status -eq 401 -or $playerResponse.status -eq 403) {
        throw "Player portal inherited administrator authorization."
    }

    $audit = Invoke-Api "GET" "$baseUri/api/v1/admin/audit?limit=100" @{
        "X-Pal-Admin-Key" = $ownerKey
    }
    Assert-Status $audit 200 "Owner could not read administrator audit records."
    $seasonAudits = @($audit.body.items | Where-Object {
        $_.subject -eq "season-a" -and
        $_.correlationId -eq "11111111-2222-3333-4444-555555555555"
    })
    if ($seasonAudits.Count -ne 2 -or
        @($seasonAudits.phase | Sort-Object) -join "," -ne "completed,started" -or
        @($seasonAudits | Where-Object { $_.requestHash -notmatch '^[0-9a-f]{64}$' }).Count -ne 0 -or
        @($seasonAudits | Where-Object { $_.sourceIp -notin @('127.0.0.1', '::1') }).Count -ne 0 -or
        @($seasonAudits | Where-Object { $_.reason -ne 'admin auth smoke' }).Count -ne 0) {
        throw "Administrator audit did not preserve both phases and required attribution fields."
    }
    $completedAudit = @($seasonAudits | Where-Object { $_.phase -eq "completed" })[0]
    if ([string]::IsNullOrWhiteSpace([string]$completedAudit.beforeJson) -or
        [string]::IsNullOrWhiteSpace([string]$completedAudit.afterJson)) {
        throw "Privileged audit completion omitted before/after evidence."
    }
    $before = $completedAudit.beforeJson | ConvertFrom-Json
    $after = $completedAudit.afterJson | ConvertFrom-Json
    if ($before.maintenance -or -not $after.maintenance -or $after.actor -ne "season-a") {
        throw "Privileged audit before/after evidence does not match the persisted state change."
    }

    [pscustomobject]@{
        anonymousStatus = 401
        viewerWriteStatus = 403
        operatorSeasonStatus = 403
        totpStatus = 200
        playerRouteIsolated = $true
        auditPhases = ($seasonAudits.phase | Sort-Object) -join ","
        auditSubject = "season-a"
        auditBeforeAfter = $true
        purchaseCircuitHotReload = $true
    } | ConvertTo-Json -Compress
}
finally {
    if ($api -and -not $api.HasExited) {
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
        $api.WaitForExit(5000) | Out-Null
    }
    $script:HttpClient.Dispose()
    $handler.Dispose()
    Remove-TestTree $testRoot
}

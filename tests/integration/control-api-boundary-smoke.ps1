$ErrorActionPreference = "Stop"
$env:PlayerPortal__Enabled = "false"

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

function Wait-ForEndpoint(
    [string] $uri,
    [Diagnostics.Process] $process,
    [int] $timeoutSeconds = 60) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "Control API exited before becoming ready (exit $($process.ExitCode))."
        }
        try {
            $response = $script:LoopbackHttpClient.GetAsync($uri).GetAwaiter().GetResult()
            try {
                if ($response.IsSuccessStatusCode) {
                    return
                }
            }
            finally {
                $response.Dispose()
            }
        }
        catch {
            # Keep polling until the process exits or the wall-clock deadline.
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Endpoint did not become ready within ${timeoutSeconds}s: $uri"
}

function Invoke-CapturedGet([string] $uri, [hashtable] $headers = @{}) {
    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::Get,
        $uri)
    try {
        foreach ($header in $headers.GetEnumerator()) {
            if (-not $request.Headers.TryAddWithoutValidation(
                    [string]$header.Key,
                    [string]$header.Value)) {
                throw "Could not add test HTTP header '$($header.Key)'."
            }
        }
        $response = $script:LoopbackHttpClient.SendAsync($request).GetAwaiter().GetResult()
        try {
            return [pscustomobject]@{
                StatusCode = [int]$response.StatusCode
                Content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
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

function Invoke-CapturedPost(
    [string] $uri,
    [string] $json,
    [hashtable] $headers = @{}) {
    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::Post,
        $uri)
    $request.Content = [Net.Http.StringContent]::new(
        $json,
        [Text.Encoding]::UTF8,
        "application/json")
    try {
        foreach ($header in $headers.GetEnumerator()) {
            if (-not $request.Headers.TryAddWithoutValidation(
                    [string]$header.Key,
                    [string]$header.Value)) {
                throw "Could not add test HTTP header '$($header.Key)'."
            }
        }
        $response = $script:LoopbackHttpClient.SendAsync($request).GetAwaiter().GetResult()
        try {
            return [pscustomobject]@{
                StatusCode = [int]$response.StatusCode
                Content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
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
        return $response.Content | ConvertFrom-Json
    }
    catch {
        throw "$context did not return valid JSON: $($response.Content)"
    }
}

function Remove-TestTree([string] $path) {
    $fullTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
    $fullTarget = [IO.Path]::GetFullPath($path)
    $leaf = [IO.Path]::GetFileName($fullTarget)
    if (-not $fullTarget.StartsWith(
            $fullTemp + '\',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith(
            "pal-control-boundary-smoke-",
            [StringComparison]::Ordinal)) {
        throw "Refusing to remove unsafe boundary-smoke path: $fullTarget"
    }
    Remove-Item -LiteralPath $fullTarget -Recurse -Force -ErrorAction SilentlyContinue
}

Add-Type -AssemblyName System.Net.Http
$loopbackHandler = [Net.Http.HttpClientHandler]::new()
$loopbackHandler.UseProxy = $false
$script:LoopbackHttpClient = [Net.Http.HttpClient]::new($loopbackHandler)
$script:LoopbackHttpClient.Timeout = [TimeSpan]::FromSeconds(5)

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serviceRoot = Join-Path $repositoryRoot "services\control-api"
$project = Join-Path $serviceRoot "PalControl.ControlApi.csproj"
$apiAssembly = Join-Path $serviceRoot `
    "bin\Release\net10.0\PalControl.ControlApi.dll"
$dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source
$apiPort = Get-FreeTcpPort
$testRoot = Join-Path $env:TEMP (
    "pal-control-boundary-smoke-" + [guid]::NewGuid().ToString("N"))
$dataDirectory = Join-Path $testRoot "data"
$stdout = Join-Path $testRoot "control-api.out.log"
$stderr = Join-Path $testRoot "control-api.err.log"
$api = $null
$baseUri = "http://127.0.0.1:$apiPort"

try {
    & dotnet build $project --configuration Release --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Control API Release build failed."
    }

    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $env:Urls = $baseUri
    $env:Palworld__OfficialRestApi__BaseUrl = "http://127.0.0.1:1/v1/api/"
    $env:Palworld__OfficialRestApi__Username = "admin"
    $env:Palworld__OfficialRestApi__Password = "test-password"
    $env:Palworld__OfficialRestApi__TimeoutSeconds = "1"
    $env:Palworld__Bridge__PipeName =
        "pal-control.boundary-smoke." + [guid]::NewGuid().ToString("N")
    $env:Palworld__PalDefenderRestApi__Enabled = "false"
    $env:CommandPersistence__DataDirectory = $dataDirectory
    $env:ExtractionMode__Enabled = "true"
    $env:ExtractionMode__InitialMarketCoin = "1000"
    $env:ExtractionMode__InitialSeasonVoucher = "300"
    $env:ExtractionMode__BootstrapPolicyVersion = "legacy-v1"
    $env:ExtractionMode__Persistence__DataDirectory = Join-Path $dataDirectory "extraction"
    $env:ExtractionMode__Rcon__Enabled = "false"
    $env:PlayerPortal__Enabled = "false"
    $env:SaveManagement__BackupRoot = Join-Path $testRoot "backups"

    # Keep the standalone smoke self-contained. The loopback development
    # principal is accepted only because this process explicitly enables
    # development mode, references one valid configured Owner, and still
    # requires the production TOTP/reason checks on high-risk routes.
    $apiArguments = @(
        ('"{0}"' -f $apiAssembly),
        "--Security:DevelopmentMode=true",
        "--Security:StartupValidation:Strict=false",
        "--Security:AdminAuthentication:Enabled=true",
        "--Security:AdminAuthentication:EnableLoopbackDevelopmentPrincipal=true",
        "--Security:AdminAuthentication:DevelopmentPrincipalSubject=boundary-smoke",
        "--Security:AdminAuthentication:Principals:0:Subject=boundary-smoke",
        "--Security:AdminAuthentication:Principals:0:ApiKeySha256=87052f5138109134ec8e8b25a5e18545e39c90244679e52b6c40c364cb671060",
        "--Security:AdminAuthentication:Principals:0:Roles:0=Owner",
        "--Security:AdminAuthentication:Principals:0:TotpSecretBase32=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
    )
    $api = Start-Process -FilePath $dotnetExecutable `
        -ArgumentList $apiArguments -WorkingDirectory $serviceRoot `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr
    Wait-ForEndpoint "$baseUri/health/live" $api

    $readyResponse = Invoke-CapturedGet "$baseUri/health/ready"
    $ready = Convert-ResponseJson $readyResponse "readiness endpoint"
    if ($readyResponse.StatusCode -ne 200 -or $ready.readReady -ne $true) {
        throw "Readiness was not HTTP 200 with readReady=true: $($readyResponse.Content)"
    }

    $settlementStatusResponse = Invoke-CapturedGet `
        "$baseUri/api/v1/extraction/admin/settlement/status"
    $settlementStatus = Convert-ResponseJson $settlementStatusResponse `
        "settlement status endpoint"
    if ($settlementStatusResponse.StatusCode -ne 200 -or
        $settlementStatus.adapter -ne "native" -or
        $settlementStatus.enabled -ne $false -or
        $settlementStatus.connected -ne $false -or
        $settlementStatus.outcome -ne "failed" -or
        $settlementStatus.error.code -ne "SETTLEMENT_PROBE_FAILED" -or
        ([string]$settlementStatus.error.message).IndexOf(
            "RCON", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Production settlement status did not fail closed with its adapter-neutral schema: $($settlementStatusResponse.Content)"
    }

    $capabilityResponse = Invoke-CapturedGet `
        "$baseUri/api/v1/extraction/capabilities"
    $capabilities = Convert-ResponseJson $capabilityResponse `
        "extraction capability endpoint"
    $purchaseBlockerCodes = @(
        $capabilities.writes.purchase.blockers | ForEach-Object { $_.code })
    if ($capabilityResponse.StatusCode -ne 200 -or
        $capabilities.readReady -ne $true -or
        $capabilities.writes.purchase.enabled -ne $false -or
        $purchaseBlockerCodes -notcontains "PALDEFENDER_DISABLED") {
        throw "Disabled PalDefender did not fail closed while preserving read readiness: $($capabilityResponse.Content)"
    }
    $exchangeBlockerCodes = @(
        $capabilities.writes.resourceExchange.blockers | ForEach-Object { $_.code })
    if ($capabilities.writes.resourceExchange.enabled -ne $false -or
        $exchangeBlockerCodes -notcontains "NATIVE_ECONOMY_ADAPTER_NOT_CONNECTED") {
        throw "Production resource exchange did not fail closed on the required stable Native adapter: $($capabilityResponse.Content)"
    }

    $maintenanceResponse = Invoke-CapturedPost `
        "$baseUri/api/v1/extraction/admin/rollover/maintenance" `
        '{"maintenance":true,"reason":"capability smoke maintenance"}' `
        @{
            "X-Pal-Admin-Totp" = Get-TestTotp
            "X-Pal-Admin-Reason" = "capability smoke maintenance"
        }
    if ($maintenanceResponse.StatusCode -ne 200) {
        throw "Could not enter maintenance for capability smoke: $($maintenanceResponse.Content)"
    }
    $maintenanceCapabilityResponse = Invoke-CapturedGet `
        "$baseUri/api/v1/extraction/capabilities"
    $maintenanceCapabilities = Convert-ResponseJson `
        $maintenanceCapabilityResponse "maintenance capability endpoint"
    $purchaseMaintenanceBlockers = @(
        $maintenanceCapabilities.writes.purchase.blockers | ForEach-Object { $_.code })
    $exchangeMaintenanceBlockers = @(
        $maintenanceCapabilities.writes.resourceExchange.blockers | ForEach-Object { $_.code })
    if ($maintenanceCapabilityResponse.StatusCode -ne 200 -or
        $maintenanceCapabilities.writes.purchase.enabled -ne $false -or
        $maintenanceCapabilities.writes.resourceExchange.enabled -ne $false -or
        $purchaseMaintenanceBlockers -notcontains "EXTRACTION_MAINTENANCE" -or
        $exchangeMaintenanceBlockers -notcontains "EXTRACTION_MAINTENANCE") {
        throw "Maintenance did not close both economy write gates: $($maintenanceCapabilityResponse.Content)"
    }

    $forwardedHeaders = @{ "X-Forwarded-For" = "203.0.113.10" }
    $operatorResponse = Invoke-CapturedGet `
        "$baseUri/api/v1/servers/local/capabilities" $forwardedHeaders
    $operatorError = Convert-ResponseJson $operatorResponse "operator boundary"
    if ($operatorResponse.StatusCode -ne 403 -or
        $operatorError.code -ne "OPERATOR_API_LOOPBACK_REQUIRED") {
        throw "Forwarded public operator request was not rejected by the loopback boundary: $($operatorResponse.Content)"
    }

    $playerResponse = Invoke-CapturedGet `
        "$baseUri/api/v1/player/auth/session" $forwardedHeaders
    $playerErrorCode = $null
    if (-not [string]::IsNullOrWhiteSpace($playerResponse.Content)) {
        try {
            $playerErrorCode = ($playerResponse.Content | ConvertFrom-Json).code
        }
        catch {
            # The only forbidden result here is the operator-only middleware code.
        }
    }
    if ($playerResponse.StatusCode -eq 403 -or
        $playerErrorCode -eq "OPERATOR_API_LOOPBACK_REQUIRED") {
        throw "Player route was incorrectly rejected by the operator loopback boundary."
    }

    [pscustomobject]@{
        readyStatus = $readyResponse.StatusCode
        readReady = [bool]$ready.readReady
        settlementAdapter = $settlementStatus.adapter
        settlementProbeError = $settlementStatus.error.code
        purchaseWriteEnabled = [bool]$capabilities.writes.purchase.enabled
        purchaseFailClosed = $true
        resourceExchangeWriteEnabled = [bool]$capabilities.writes.resourceExchange.enabled
        maintenanceClosedBothWrites = $true
        operatorStatus = $operatorResponse.StatusCode
        operatorError = $operatorError.code
        playerStatus = $playerResponse.StatusCode
        playerError = $playerErrorCode
    } | ConvertTo-Json -Compress
}
catch {
    if (Test-Path $stdout) {
        Write-Host (Get-Content -Raw $stdout)
    }
    if (Test-Path $stderr) {
        Write-Host (Get-Content -Raw $stderr)
    }
    throw
}
finally {
    if ($api -and -not $api.HasExited) {
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $api.Id -Timeout 5 -ErrorAction SilentlyContinue
    }
    if (Test-Path $testRoot) {
        Remove-TestTree $testRoot
    }
    $script:LoopbackHttpClient.Dispose()
}

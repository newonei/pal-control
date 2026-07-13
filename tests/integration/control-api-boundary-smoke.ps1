$ErrorActionPreference = "Stop"

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
            Invoke-WebRequest $uri -TimeoutSec 1 | Out-Null
            return
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    }
    throw "Endpoint did not become ready within ${timeoutSeconds}s: $uri"
}

function Invoke-CapturedGet([string] $uri, [hashtable] $headers = @{}) {
    try {
        $response = Invoke-WebRequest $uri -Headers $headers -TimeoutSec 5
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Content = [string]$response.Content
        }
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) {
            throw
        }
        $content = [string]$_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($content)) {
            try {
                $reader = [IO.StreamReader]::new($response.GetResponseStream())
                try {
                    $content = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }
            }
            catch {
                $content = ""
            }
        }
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Content = $content
        }
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

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serviceRoot = Join-Path $repositoryRoot "services\control-api"
$project = Join-Path $serviceRoot "PalControl.ControlApi.csproj"
$apiExecutable = Join-Path $serviceRoot `
    "bin\Release\net10.0\PalControl.ControlApi.exe"
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

    $api = Start-Process -FilePath $apiExecutable -WorkingDirectory $serviceRoot `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr
    Wait-ForEndpoint "$baseUri/health/live" $api

    $readyResponse = Invoke-CapturedGet "$baseUri/health/ready"
    $ready = Convert-ResponseJson $readyResponse "readiness endpoint"
    if ($readyResponse.StatusCode -ne 200 -or $ready.readReady -ne $true) {
        throw "Readiness was not HTTP 200 with readReady=true: $($readyResponse.Content)"
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
}

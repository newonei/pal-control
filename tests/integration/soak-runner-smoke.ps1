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

function Wait-ForTarget([string] $uri, [Diagnostics.Process] $process) {
    for ($attempt = 0; $attempt -lt 150; $attempt += 1) {
        if ($process.HasExited) {
            throw "Synthetic soak target exited before readiness (exit $($process.ExitCode))."
        }
        try {
            $response = $script:HttpClient.GetAsync($uri).GetAwaiter().GetResult()
            if ([int]$response.StatusCode -eq 200) {
                $response.Dispose()
                return
            }
            $response.Dispose()
        }
        catch {
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Synthetic soak target did not become ready."
}

function Get-Sha256Hex([string] $path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::Open(
            $path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        try {
            return ([BitConverter]::ToString(
                $algorithm.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $algorithm.Dispose()
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
        [IO.Path]::GetFileName($target) -notlike "pal-control-soak-smoke-*") {
        throw "Refusing to remove unexpected test path '$target'."
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolProject = Join-Path $repositoryRoot "tools\soak\PalControl.Soak.csproj"
$harnessProject = Join-Path $repositoryRoot `
    "tests\soak\PalControl.Soak.Harness.csproj"
$targetProject = Join-Path $repositoryRoot `
    "tests\soak-target\PalControl.SoakTarget.csproj"
$testRoot = Join-Path $env:TEMP `
    ("pal-control-soak-smoke-" + [Guid]::NewGuid().ToString("N"))
$toolOutput = Join-Path $testRoot "tool"
$targetOutput = Join-Path $testRoot "target"
$harnessOutput = Join-Path $testRoot "harness-bin\"
$dataRoot = Join-Path $testRoot "data"
$logRoot = Join-Path $testRoot "logs"
$reportRoot = Join-Path $testRoot "report"
$wrongDataRoot = Join-Path $testRoot "wrong-data"
$wrongReportRoot = Join-Path $testRoot "wrong-report"
$productionRejectRoot = Join-Path $testRoot "production-reject-report"
$thresholdsPath = Join-Path $testRoot "thresholds.ci.json"
$targetStdout = Join-Path $logRoot "target.out.log"
$targetStderr = Join-Path $logRoot "target.err.log"
$port = Get-FreeTcpPort
$baseUri = "http://127.0.0.1:$port"
$apiKeyCanary = "super-secret-soak-api-key-000000000000"
$oldApiKey = [Environment]::GetEnvironmentVariable(
    "PAL_CONTROL_SOAK_API_KEY",
    [EnvironmentVariableTarget]::Process)
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$script:HttpClient = [Net.Http.HttpClient]::new($handler)
$script:HttpClient.Timeout = [TimeSpan]::FromSeconds(3)
$target = $null

try {
    New-Item -ItemType Directory -Force -Path `
        $testRoot, $dataRoot, $logRoot, $wrongDataRoot | Out-Null
    [IO.File]::WriteAllBytes(
        (Join-Path $dataRoot "economy.db"),
        [byte[]]::new(4096))
    [IO.File]::WriteAllBytes(
        (Join-Path $dataRoot "economy.db-wal"),
        [byte[]]::new(512))
    [IO.File]::WriteAllBytes(
        (Join-Path $dataRoot "economy.db-shm"),
        [byte[]]::new(256))
    [IO.File]::WriteAllText(
        (Join-Path $logRoot "application.log"),
        "log-content-secret-must-not-enter-report",
        [Text.UTF8Encoding]::new($false))
    $relaxedThresholds = @{
        maximumProbeFailurePercent = 0
        maximumWorkloadFailurePercent = 0
        maximumWorkingSetSlopeBytesPerHour = 1e300
        maximumPrivateBytesSlopeBytesPerHour = 1e300
        maximumGcHeapSlopeBytesPerHour = 1e300
        maximumHandleSlopePerHour = 1e300
        maximumThreadSlopePerHour = 1e300
        maximumDatabaseSlopeBytesPerHour = 1e300
        maximumWalSlopeBytesPerHour = 1e300
        maximumShmSlopeBytesPerHour = 1e300
        maximumLogSlopeBytesPerHour = 1e300
        maximumSessionSlopePerHour = 1e300
        maximumQueueSlopePerHour = 1e300
        maximumWorkingSetPeakGrowthBytes = [long]::MaxValue
        maximumPrivateBytesPeakGrowthBytes = [long]::MaxValue
        maximumGcHeapPeakGrowthBytes = [long]::MaxValue
        maximumHandlePeakGrowth = [int]::MaxValue
        maximumThreadPeakGrowth = [int]::MaxValue
        maximumDatabasePeakGrowthBytes = [long]::MaxValue
        maximumWalPeakBytes = [long]::MaxValue
        maximumShmPeakBytes = [long]::MaxValue
        maximumLogPeakGrowthBytes = [long]::MaxValue
        maximumSessionPeakGrowth = [int]::MaxValue
        maximumQueuePeakGrowth = [int]::MaxValue
        maximumWorkingSetRecoveryGrowthBytes = [long]::MaxValue
        maximumPrivateBytesRecoveryGrowthBytes = [long]::MaxValue
        maximumGcHeapRecoveryGrowthBytes = [long]::MaxValue
        maximumHandleRecoveryGrowth = [int]::MaxValue
        maximumThreadRecoveryGrowth = [int]::MaxValue
        maximumWalRecoveryBytes = [long]::MaxValue
        maximumShmRecoveryBytes = [long]::MaxValue
        maximumSessionRecoveryGrowth = [int]::MaxValue
        maximumQueueRecoveryGrowth = [int]::MaxValue
        baselineSampleCount = 3
    } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText(
        $thresholdsPath,
        $relaxedThresholds,
        [Text.UTF8Encoding]::new($false))

    & dotnet run --no-restore --project $harnessProject -c Release --nologo `
        --property:UseAppHost=false --property:BaseOutputPath=$harnessOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Synthetic soak analyzer harness failed with exit code $LASTEXITCODE."
    }
    & dotnet build $toolProject -c Release --no-restore --nologo `
        --property:UseAppHost=false --output $toolOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Soak tool build failed with exit code $LASTEXITCODE."
    }
    & dotnet build $targetProject -c Release --no-restore --nologo `
        --property:UseAppHost=false --output $targetOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Synthetic soak target build failed with exit code $LASTEXITCODE."
    }

    $target = Start-Process -FilePath "dotnet" -ArgumentList @(
        (Join-Path $targetOutput "PalControl.SoakTarget.dll"),
        "--urls=$baseUri",
        "--data-directory=$dataRoot",
        "--log-directory=$logRoot"
    ) -WorkingDirectory $targetOutput -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $targetStdout -RedirectStandardError $targetStderr
    Wait-ForTarget "$baseUri/health/live" $target

    [Environment]::SetEnvironmentVariable(
        "PAL_CONTROL_SOAK_API_KEY",
        $apiKeyCanary,
        [EnvironmentVariableTarget]::Process)
    $ErrorActionPreference = "Continue"
    & dotnet (Join-Path $toolOutput "PalControl.Soak.dll") `
        --ci-mode `
        --pid $target.Id `
        --base-uri $baseUri `
        --data-directory $wrongDataRoot `
        --log-directory $logRoot `
        --output-directory $wrongReportRoot `
        --duration-seconds 4 `
        --sample-interval-seconds 1 `
        --recovery-seconds 2 `
        --requests-per-second 2 `
        --request-timeout-seconds 2 `
        --thresholds $thresholdsPath 2>$null
    $bindingFailureExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($bindingFailureExit -ne 2) {
        throw "Mismatched process/data instance binding did not fail closed."
    }
    $operationsCount = ($script:HttpClient.GetStringAsync(
            "$baseUri/test/operations-count").GetAwaiter().GetResult() |
        ConvertFrom-Json).count
    if ($operationsCount -ne 0) {
        throw "Viewer credentials were sent before unauthenticated instance binding completed."
    }
    $ErrorActionPreference = "Continue"
    & dotnet (Join-Path $toolOutput "PalControl.Soak.dll") `
        --pid $target.Id --base-uri $baseUri `
        --data-directory $dataRoot --log-directory $logRoot `
        --output-directory $productionRejectRoot `
        --duration-seconds 86400 --thresholds $thresholdsPath 2>$null
    $productionThresholdExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($productionThresholdExit -ne 1) {
        throw "Production mode accepted thresholds that differ from the embedded profile."
    }
    & dotnet (Join-Path $toolOutput "PalControl.Soak.dll") `
        --ci-mode `
        --pid $target.Id `
        --base-uri $baseUri `
        --data-directory $dataRoot `
        --log-directory $logRoot `
        --output-directory $reportRoot `
        --duration-seconds 4 `
        --sample-interval-seconds 1 `
        --recovery-seconds 2 `
        --requests-per-second 2 `
        --request-timeout-seconds 2 `
        --thresholds $thresholdsPath
    if ($LASTEXITCODE -ne 0) {
        throw "Short real-process soak failed with exit code $LASTEXITCODE."
    }

    $ErrorActionPreference = "Continue"
    & dotnet (Join-Path $toolOutput "PalControl.Soak.dll") `
        --ci-mode --pid $target.Id --base-uri $baseUri `
        --data-directory $dataRoot --log-directory $logRoot `
        --output-directory $reportRoot 2>$null
    $overwriteExit = $LASTEXITCODE
    & dotnet (Join-Path $toolOutput "PalControl.Soak.dll") `
        --ci-mode --pid $target.Id --base-uri $baseUri `
        --data-directory $dataRoot --log-directory $logRoot `
        --output-directory (Join-Path $dataRoot "nested-report") 2>$null
    $nestedExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($overwriteExit -ne 1) {
        throw "Soak tool did not refuse to overwrite an existing evidence directory."
    }
    if ($nestedExit -ne 1) {
        throw "Soak tool did not refuse an output directory nested under production data."
    }

    $reportPath = Join-Path $reportRoot "report.json"
    $hashPath = Join-Path $reportRoot "report.json.sha256"
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $hashPath -PathType Leaf)) {
        throw "Soak evidence report or SHA-256 sidecar is missing."
    }
    $raw = [IO.File]::ReadAllText($reportPath, [Text.Encoding]::UTF8)
    $report = $raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 1 -or
        $report.canonicalization -ne "pal-control-soak-canonical-json-v1" -or
        $report.evidenceProfile -ne "ci-non-acceptance" -or
        $report.thresholdsSha256 -notmatch '^[0-9a-f]{64}$' -or
        $report.status -ne "passed" -or
        @($report.samples).Count -lt 7 -or
        $report.analysis.passed -ne $true -or
        @($report.analysis.violations).Count -ne 0) {
        throw "Short soak report did not pass its complete evidence contract."
    }
    $first = @($report.samples)[0]
    if ($first.process.available -ne $true -or
        $first.api.instance.available -ne $true -or
        $first.api.instance.bindingSha256 -notmatch '^[0-9a-f]{64}$' -or
        $first.gc.available -ne $true -or
        $first.sqlite.available -ne $true -or
        $first.sqlite.databaseFileCount -ne 1 -or
        $first.logs.available -ne $true -or
        $first.api.sessions.available -ne $true -or
        $first.api.sessions.active -ne 0 -or
        $first.api.queues.available -ne $true -or
        $null -eq $first.api.queues.delivery.pending) {
        throw "Real-process sample omitted process, GC, SQLite, log, session or queue evidence."
    }
    $metricNames = @($report.analysis.metrics | ForEach-Object name)
    foreach ($requiredMetric in @(
            "process.workingSetBytes", "process.privateBytes",
            "process.handleCount", "process.threadCount", "gc.heapSizeBytes",
            "sqlite.databaseBytes", "sqlite.walBytes", "sqlite.shmBytes",
            "logs.totalBytes", "sessions.active", "queues.totalPending")) {
        if (-not $metricNames.Contains($requiredMetric)) {
            throw "Soak analysis omitted required metric '$requiredMetric'."
        }
    }
    foreach ($forbidden in @(
            $apiKeyCanary,
            "api-response-secret-must-not-enter-report",
            "log-content-secret-must-not-enter-report",
            $testRoot,
            $baseUri,
            "X-Pal-Admin-Key")) {
        if ($raw.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Soak report leaked sensitive or machine-local material."
        }
    }
    $expectedHash = [IO.File]::ReadAllText(
        $hashPath,
        [Text.Encoding]::ASCII).Trim()
    $actualHash = Get-Sha256Hex $reportPath
    if ($expectedHash -notmatch '^[0-9a-f]{64}$' -or
        $expectedHash -cne $actualHash) {
        throw "Soak report SHA-256 sidecar does not match canonical report bytes."
    }

    [pscustomobject]@{
        status = $report.status
        samples = @($report.samples).Count
        workloadRequests = (@(
                $report.samples | ForEach-Object {
                    [int]$_.workload.attempted
                }) | Measure-Object -Sum).Sum
        sensitiveFieldsAbsent = $true
        canonicalHashVerified = $true
    } | ConvertTo-Json -Compress
}
finally {
    [Environment]::SetEnvironmentVariable(
        "PAL_CONTROL_SOAK_API_KEY",
        $oldApiKey,
        [EnvironmentVariableTarget]::Process)
    if ($null -ne $target -and -not $target.HasExited) {
        Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
        $target.WaitForExit(5000) | Out-Null
    }
    $script:HttpClient.Dispose()
    $handler.Dispose()
    Remove-TestTree $testRoot
}

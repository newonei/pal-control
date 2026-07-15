[CmdletBinding()]
param()

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
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Wait-ForApi([string] $uri, [Diagnostics.Process] $process) {
    for ($attempt = 0; $attempt -lt 120; $attempt += 1) {
        if ($process.HasExited) {
            $process.WaitForExit()
            $process.Refresh()
            foreach ($log in @(
                    @{ Label = "Control API stdout"; Path = $script:apiStdout },
                    @{ Label = "Control API stderr"; Path = $script:apiStderr })) {
                if (Test-Path -LiteralPath $log.Path) {
                    $content = Get-Content -LiteralPath $log.Path -Raw
                    if (-not [string]::IsNullOrWhiteSpace($content)) {
                        Write-Host "--- $($log.Label) ---"
                        Write-Host $content
                    }
                }
            }
            throw "Control API exited before startup (exit $($process.ExitCode))."
        }
        try {
            $response = $script:HttpClient.GetAsync($uri).GetAwaiter().GetResult()
            if ([int]$response.StatusCode -eq 200) {
                return
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Control API did not become ready at $uri."
}

function Invoke-Get([string] $uri, [hashtable] $headers = @{}) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
    try {
        foreach ($entry in $headers.GetEnumerator()) {
            if (-not $request.Headers.TryAddWithoutValidation(
                    [string]$entry.Key,
                    [string]$entry.Value)) {
                throw "Could not add HTTP header '$($entry.Key)'."
            }
        }
        $response = $script:HttpClient.SendAsync($request).GetAwaiter().GetResult()
        return [pscustomobject]@{
            status = [int]$response.StatusCode
            contentType = [string]$response.Content.Headers.ContentType
            text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
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
        [IO.Path]::GetFileName($target) -notlike
            "pal-control-economy-observability-smoke-*") {
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
    "pal-control-economy-observability-smoke-" + [Guid]::NewGuid().ToString("N"))
$buildRoot = Join-Path $testRoot "build"
$dataRoot = Join-Path $testRoot "economy"
$commandRoot = Join-Path $testRoot "commands"
$gameBackupRoot = Join-Path $testRoot "game-backups"
$economyBackupRoot = Join-Path $testRoot "economy-backups"
$economyStagingRoot = Join-Path $testRoot "economy-staging"
$palDefenderCredential = Join-Path $testRoot "paldefender-credential.json"
$stdout = Join-Path $testRoot "api.out.log"
$stderr = Join-Path $testRoot "api.err.log"
$script:apiStdout = $stdout
$script:apiStderr = $stderr
$palworldRoot = Join-Path $testRoot "PalServer"
$logRoot = Join-Path $testRoot "logs"
$port = Get-FreeTcpPort
$baseUri = "http://127.0.0.1:$port"
$viewerKey = "observability-viewer-key-000000000000"
$headers = @{ "X-Pal-Admin-Key" = $viewerKey }
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$script:HttpClient = [Net.Http.HttpClient]::new($handler)
$script:HttpClient.Timeout = [TimeSpan]::FromSeconds(10)
$api = $null

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    $credentialDocument = @{
        Permissions = @(
            "REST.Version.Read",
            "REST.Players.Read",
            "REST.Items.Read",
            "REST.Items.Give"
        )
    } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText(
        $palDefenderCredential,
        $credentialDocument,
        [Text.UTF8Encoding]::new($false))
    & dotnet build $project --configuration Release --output $buildRoot --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Control API Release build failed with exit code $LASTEXITCODE."
    }

    $arguments = @(
        (Join-Path $buildRoot "PalControl.ControlApi.dll"),
        "--contentRoot=$buildRoot",
        "--urls=$baseUri",
        "--Security:DevelopmentMode=false",
        "--Security:StartupValidation:Strict=true",
        "--Security:StartupValidation:LogDirectory=$logRoot",
        "--Security:AdminAuthentication:Enabled=true",
        "--Security:AdminAuthentication:EnableLoopbackDevelopmentPrincipal=false",
        "--Security:AdminAuthentication:Principals:0:Subject=observability-viewer",
        "--Security:AdminAuthentication:Principals:0:ApiKeySha256=$(Get-Sha256Hex $viewerKey)",
        "--Security:AdminAuthentication:Principals:0:Roles:0=Viewer",
        "--ExtractionMode:Enabled=true",
        "--ExtractionMode:BootstrapPolicyVersion=production-v1",
        "--ExtractionMode:InitialMarketCoin=0",
        "--ExtractionMode:InitialSeasonVoucher=0",
        "--ExtractionMode:Rcon:Enabled=false",
        "--Palworld:PalDefenderRestApi:Enabled=true",
        "--Palworld:PalDefenderRestApi:TokenFile=$palDefenderCredential",
        "--ExtractionMode:Persistence:DataDirectory=$dataRoot",
        "--ExtractionMode:Continuity:BackupRoot=$economyBackupRoot",
        "--ExtractionMode:Continuity:StagingRoot=$economyStagingRoot",
        "--ExtractionMode:Observability:Enabled=true",
        "--ExtractionMode:Observability:AutoCircuitBreakEnabled=true",
        "--PlayerPortal:Enabled=false",
        "--Palworld:InstallRoot=$palworldRoot",
        "--CommandPersistence:DataDirectory=$commandRoot",
        "--SaveManagement:BackupRoot=$gameBackupRoot",
        "--SaveManagement:RequireRunningProcess=false"
    )
    $api = Start-Process -FilePath "dotnet" -ArgumentList $arguments `
        -WorkingDirectory $buildRoot -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Wait-ForApi "$baseUri/health/live" $api

    Assert-Status (Invoke-Get "$baseUri/api/v1/economy/observability") 401 `
        "Anonymous JSON metrics access was not rejected."
    Assert-Status (Invoke-Get "$baseUri/api/v1/economy/metrics") 401 `
        "Anonymous Prometheus metrics access was not rejected."

    $automaticCircuitObserved = $false
    for ($attempt = 0; $attempt -lt 50; $attempt += 1) {
        $candidate = Invoke-Get "$baseUri/api/v1/economy/observability" $headers
        if ($candidate.status -eq 200) {
            $candidateSnapshot = $candidate.text | ConvertFrom-Json
            if ($candidateSnapshot.circuits.purchase.writesEnabled -eq $false -and
                $candidateSnapshot.circuits.resourceExchange.writesEnabled -eq $false -and
                $candidateSnapshot.circuits.purchase.source -eq "automatic" -and
                $candidateSnapshot.circuits.resourceExchange.source -eq "automatic") {
                $automaticCircuitObserved = $true
                break
            }
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $automaticCircuitObserved) {
        throw "Critical dependency alerts did not automatically open both economy circuits."
    }

    $jsonResponse = Invoke-Get `
        "$baseUri/api/v1/economy/observability?refresh=true" $headers
    Assert-Status $jsonResponse 200 "Viewer could not read economy observability JSON."
    if ($jsonResponse.contentType -notlike "application/json*") {
        throw "Economy observability endpoint returned '$($jsonResponse.contentType)'."
    }
    $snapshot = $jsonResponse.text | ConvertFrom-Json
    if ($snapshot.schemaVersion -ne 1 -or
        $snapshot.gameplayMode -ne "weekly-resource-economy" -or
        $null -eq $snapshot.orders.pendingDelivery -or
        $null -eq $snapshot.resourceSettlements.uncertain -or
        $null -eq $snapshot.deliveryQueue.capacity -or
        $null -eq $snapshot.outbox.PSObject.Properties["oldestAgeSeconds"] -or
        $null -eq $snapshot.ledger.conserved -or
        $null -eq $snapshot.identity.recentRejectedConflictCount -or
        $null -eq $snapshot.dependencyConsistency.consistent -or
        -not @($snapshot.dependencyConsistency.purchaseBlockerCodes).Contains(
            "PALDEFENDER_CREDENTIAL_UNAVAILABLE") -or
        $null -eq $snapshot.versionConsistency.consistent -or
        $null -eq $snapshot.worldConsistency.consistent -or
        $null -eq $snapshot.gameBackup.maximumAgeSeconds -or
        $null -eq $snapshot.economyBackup.maximumAgeSeconds -or
        $null -eq $snapshot.circuits.purchase.writesEnabled -or
        @($snapshot.alerts).Count -lt 1) {
        throw "Economy observability JSON omitted a required state, latency, queue, invariant, consistency, backup, circuit, or alert field."
    }

    $metricsResponse = Invoke-Get "$baseUri/api/v1/economy/metrics" $headers
    Assert-Status $metricsResponse 200 "Viewer could not scrape economy OpenMetrics."
    if ($metricsResponse.contentType -notlike "text/plain*") {
        throw "Economy metrics endpoint returned '$($metricsResponse.contentType)'."
    }
    $requiredMetrics = @(
        "pal_control_economy_state_total",
        "pal_control_economy_state_latency_seconds",
        "pal_control_economy_queue_oldest_age_seconds",
        "pal_control_economy_uncertain_total",
        "pal_control_economy_ledger_invariant_mismatch_total",
        "pal_control_economy_identity_conflict_total",
        "pal_control_economy_dependency_consistent",
        "pal_control_economy_version_consistent",
        "pal_control_economy_world_consistent",
        "pal_control_economy_backup_age_seconds",
        "pal_control_economy_circuit_open",
        "pal_control_economy_alert_active"
    )
    foreach ($metric in $requiredMetrics) {
        if ($metricsResponse.text.IndexOf($metric, [StringComparison]::Ordinal) -lt 0) {
            throw "Prometheus export omitted '$metric'."
        }
    }
    $combined = ($jsonResponse.text + "`n" + $metricsResponse.text).ToLowerInvariant()
    foreach ($forbidden in @(
            "playeridentifier", "playeruid", "externaluserid", "steamid",
            "cookie", "token", "password", $viewerKey.ToLowerInvariant())) {
        if ($combined.IndexOf($forbidden, [StringComparison]::Ordinal) -ge 0) {
            throw "Economy metrics leaked forbidden content '$forbidden'."
        }
    }

    [pscustomobject]@{
        status = $snapshot.status
        alerts = @($snapshot.alerts | Where-Object active).Count
        metricsBytes = [Text.Encoding]::UTF8.GetByteCount($metricsResponse.text)
        anonymousProtected = $true
        automaticCircuits = $true
        sensitiveFieldsAbsent = $true
    } | ConvertTo-Json -Compress
}
finally {
    if ($null -ne $api -and -not $api.HasExited) {
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
        $api.WaitForExit(5000) | Out-Null
    }
    $script:HttpClient.Dispose()
    $handler.Dispose()
    Remove-TestTree $testRoot
}

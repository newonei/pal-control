Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$deploymentScript = Join-Path $repositoryRoot `
    "deploy\windows\production\Invoke-PalControlDeployment.ps1"
$deploymentModule = Join-Path $repositoryRoot `
    "deploy\windows\production\PalControl.Deployment.psm1"
Import-Module $deploymentModule -Force

# GitHub-hosted Windows images do not guarantee that Get-FileHash is
# discoverable in the child Windows PowerShell used by npm test. Keep the
# production verifier self-contained and make any regression deterministic on
# developer machines where that cmdlet happens to be available.
function Get-FileHash {
    throw "Production deployment verification must use the self-contained SHA-256 helper."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "pal-control-production-deployment-" + [Guid]::NewGuid().ToString("N"))
$installRoot = Join-Path $testRoot "install"
$stateRoot = Join-Path $testRoot "state"
$buildRoot = Join-Path $testRoot "build"
$toolRoot = Join-Path $testRoot "tools"
$configPath = Join-Path $stateRoot "config\appsettings.Production.json"
$caddyPath = Join-Path $toolRoot "caddy.exe"
$caddyConfig = Join-Path $stateRoot "caddy\Caddyfile"
$caddyEnvironment = Join-Path $stateRoot "caddy\player-portal.env"
$caddyData = Join-Path $stateRoot "caddy-data"
$caddyRuntimeConfig = Join-Path $stateRoot "caddy-config"
$caddyLogRoot = Join-Path $stateRoot "caddy-logs"
$secretsRoot = Join-Path $stateRoot "secrets"
$federationIdentityKey = Join-Path $secretsRoot "federation-identity.key"
$federationInboundKey = Join-Path $secretsRoot "federation-node-inbound.key"
$federationRemoteNodeKey = Join-Path $secretsRoot "federation-node-02.key"
$previousHookValue = $env:PAL_CONTROL_DEPLOYMENT_TEST_HOOKS

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern)
    $caught = $null
    try { & $Action | Out-Null }
    catch { $caught = $_ }
    if ($null -eq $caught) {
        throw "Expected operation to fail with '$Pattern'."
    }
    if ($caught.Exception.Message -notmatch $Pattern) {
        throw "Unexpected error. Expected '$Pattern', got '$($caught.Exception.Message)'."
    }
}

function New-TestRelease {
    param(
        [Parameter(Mandatory)] [string]$Version,
        [Parameter(Mandatory)] [string]$RevisionCharacter,
        [int]$DataContractVersion = 1
    )
    $releaseId = "$Version-$($RevisionCharacter * 12)"
    $container = Join-Path $buildRoot $releaseId
    $payload = Join-Path $container "PalControl"
    New-Item -ItemType Directory -Path (Join-Path $payload "wwwroot\player") -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $payload "PalControl.ControlApi.exe"),
        "fake-control-api-$Version",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        (Join-Path $payload "wwwroot\player\index.html"),
        "<html><body>$Version</body></html>",
        (New-Object Text.UTF8Encoding($false)))
    $files = @(Get-FileInventory -Root $payload)
    $manifest = [ordered]@{
        schemaVersion = 1
        product = "PalControl"
        version = $Version
        releaseId = $releaseId
        sourceRevision = ($RevisionCharacter * 40)
        sourceDirty = $false
        platform = "win-x64"
        executable = "PalControl.ControlApi.exe"
        dataContract = [ordered]@{
            provider = "sqlite"
            version = $DataContractVersion
            migrationMode = "startup-idempotent"
            rollbackPolicy = "same-contract-only"
        }
        files = $files
    }
    Write-AtomicUtf8Json -Path (Join-Path $payload "release-manifest.json") -Value $manifest
    $archive = Join-Path $container "$releaseId.zip"
    Compress-Archive -Path $payload -DestinationPath $archive -CompressionLevel Optimal
    [pscustomobject]@{
        Id = $releaseId
        Container = $container
        Payload = $payload
        Archive = $archive
        Sha256 = Get-PalControlFileSha256 -Path $archive
    }
}

function Invoke-TestDeployment {
    param(
        [Parameter(Mandatory)] [string]$Action,
        $Release,
        [string]$TargetReleaseId
    )
    $parameters = @{
        Action = $Action
        InstallRoot = $installRoot
        StateRoot = $stateRoot
        ConfigurationPath = $configPath
        HealthTimeoutSeconds = 5
        CaddyExecutablePath = $caddyPath
        CaddyExpectedSha256 = $script:caddySha256
        CaddyConfigPath = $caddyConfig
        CaddyEnvironmentFile = $caddyEnvironment
        CaddyDataHome = $caddyData
        CaddyConfigHome = $caddyRuntimeConfig
        CaddyLogRoot = $caddyLogRoot
        EnableTestHooks = $true
        ServiceAdapter = $script:serviceAdapter
        HealthProbeAdapter = $script:healthAdapter
        DrainProbeAdapter = $script:drainAdapter
        CaddyValidationAdapter = $script:caddyAdapter
    }
    if ($null -ne $Release) {
        $parameters.ReleaseArchive = $Release.Archive
        $parameters.ExpectedSha256 = $Release.Sha256
    }
    if (-not [string]::IsNullOrWhiteSpace($TargetReleaseId)) {
        $parameters.TargetReleaseId = $TargetReleaseId
    }
    & $deploymentScript @parameters
}

function Get-FreeTcpPort {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-ExternalConfigApiOnce {
    param(
        [Parameter(Mandatory)] [string]$ApiAssembly,
        [Parameter(Mandatory)] [string]$ExternalConfig,
        [Parameter(Mandatory)] [int]$Port,
        [Parameter(Mandatory)] [string]$LogPrefix
    )
    $savedEnvironment = @{}
    foreach ($name in @(
        "PAL_CONTROL_CONFIG_PATH",
        "DOTNET_ENVIRONMENT",
        "ASPNETCORE_ENVIRONMENT",
        "ASPNETCORE_URLS")) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    $process = $null
    $client = $null
    try {
        $env:PAL_CONTROL_CONFIG_PATH = $ExternalConfig
        $env:DOTNET_ENVIRONMENT = "Development"
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
        $stdout = "$LogPrefix.stdout.log"
        $stderr = "$LogPrefix.stderr.log"
        $process = Start-Process -FilePath "dotnet" -ArgumentList @($ApiAssembly) `
            -WorkingDirectory (Split-Path -Parent $ApiAssembly) `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
            -WindowStyle Hidden -PassThru
        $handler = [Net.Http.HttpClientHandler]::new()
        $handler.UseProxy = $false
        $client = [Net.Http.HttpClient]::new($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(5)
        $deadline = (Get-Date).AddSeconds(40)
        $ready = $false
        while ((Get-Date) -lt $deadline) {
            if ($process.HasExited) { break }
            try {
                $httpResponse = $client.GetAsync(
                    "http://127.0.0.1:$Port/health/ready").GetAwaiter().GetResult()
                try {
                    if ($httpResponse.IsSuccessStatusCode) {
                        $body = $httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() |
                            ConvertFrom-Json
                        if ($body.readReady -eq $true) { $ready = $true; break }
                    }
                }
                finally { $httpResponse.Dispose() }
            }
            catch { }
            Start-Sleep -Milliseconds 250
        }
        if (-not $ready) {
            $stdoutTail = if (Test-Path -LiteralPath $stdout) {
                (Get-Content -LiteralPath $stdout -Tail 30 -ErrorAction SilentlyContinue) -join [Environment]::NewLine
            } else { "" }
            $stderrTail = if (Test-Path -LiteralPath $stderr) {
                (Get-Content -LiteralPath $stderr -Tail 30 -ErrorAction SilentlyContinue) -join [Environment]::NewLine
            } else { "" }
            throw (
                "External production configuration did not become ready." +
                " stdout: $stdoutTail stderr: $stderrTail")
        }
    }
    finally {
        if ($null -ne $client) { $client.Dispose() }
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
        }
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
}

try {
    New-Item -ItemType Directory -Path $testRoot, $stateRoot, $toolRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $configPath), `
        (Split-Path -Parent $caddyConfig), (Join-Path $stateRoot "data"), `
        $secretsRoot -Force | Out-Null
    [IO.File]::WriteAllText($caddyPath, "fake-caddy", (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $federationIdentityKey,
        ("i" * 48),
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $federationInboundKey,
        ("n" * 48),
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $federationRemoteNodeKey,
        ("r" * 48),
        (New-Object Text.UTF8Encoding($false)))
    $script:caddySha256 = Get-PalControlFileSha256 -Path $caddyPath
    [IO.File]::WriteAllText(
        $caddyConfig,
        "{`n admin 127.0.0.1:2019`n}`n{`$PLAYER_PORTAL_DOMAIN} { root * {`$PLAYER_PORTAL_ROOT} }`n",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $caddyEnvironment,
        "PLAYER_PORTAL_DOMAIN=portal.example.test`nPLAYER_PORTAL_ROOT=C:/invalid`nPLAYER_PORTAL_ACCESS_LOG=$($caddyLogRoot.Replace('\', '/'))/test.json`n",
        (New-Object Text.UTF8Encoding($false)))

    $settings = [ordered]@{
        Urls = "http://127.0.0.1:5180"
        Palworld = [ordered]@{
            ResourceCatalogPath = (Join-Path $stateRoot "resources\palworld-resource-catalog.json")
        }
        SaveManagement = [ordered]@{
            BackupRoot = (Join-Path $stateRoot "backups\savegames")
        }
        CommandPersistence = [ordered]@{
            DataDirectory = (Join-Path $stateRoot "data")
        }
        ExtractionMode = [ordered]@{
            Enabled = $false
            Persistence = [ordered]@{
                DataDirectory = (Join-Path $stateRoot "data\extraction")
            }
            Continuity = [ordered]@{
                BackupRoot = (Join-Path $stateRoot "backups\economy")
                StagingRoot = (Join-Path $stateRoot "staging\economy")
            }
            Rcon = [ordered]@{ PasswordFile = "" }
        }
        PlayerPortal = [ordered]@{ Enabled = $true }
        Federation = [ordered]@{
            Enabled = $true
            IdentityHmacKeyFile = $federationIdentityKey
            InboundNodeKeyFile = $federationInboundKey
            Nodes = @(
                [ordered]@{
                    Local = $true
                    NodeKeyFile = (Join-Path $testRoot "ignored-local-node.key")
                },
                [ordered]@{
                    Local = $false
                    NodeKeyFile = $federationRemoteNodeKey
                }
            )
        }
        Security = [ordered]@{
            StartupValidation = [ordered]@{
                LogDirectory = (Join-Path $stateRoot "logs")
            }
        }
    }
    Write-AtomicUtf8Json -Path $configPath -Value $settings

    $shared = [pscustomobject]@{
        services = @{}
        rejectedReleaseId = $null
        configureRequests = (New-Object Collections.Generic.List[object])
    }
    $script:serviceAdapter = {
        param($request)
        switch ($request.operation) {
            "Exists" { return $shared.services.ContainsKey($request.name) }
            "Configure" {
                $shared.services[$request.name] = [ordered]@{
                    binaryPath = [string]$request.properties.BinaryPath
                    running = $false
                    account = [string]$request.properties.Account
                    environment = @($request.properties.Environment)
                }
                $shared.configureRequests.Add($request)
                return $true
            }
            "Start" {
                if (-not $shared.services.ContainsKey($request.name)) {
                    throw "Test service was started before it was configured: $($request.name)"
                }
                $shared.services[$request.name].running = $true
                return $true
            }
            "Stop" {
                if ($shared.services.ContainsKey($request.name)) {
                    $shared.services[$request.name].running = $false
                }
                return $true
            }
            default { throw "Unexpected service operation: $($request.operation)" }
        }
    }.GetNewClosure()
    $script:healthAdapter = {
        param($uri)
        $service = $shared.services["PalControl.ControlApi"]
        if ($null -eq $service -or -not $service.running) { return $false }
        if (-not [string]::IsNullOrWhiteSpace($shared.rejectedReleaseId) -and
            $service.binaryPath -like "*$($shared.rejectedReleaseId)*") {
            return $false
        }
        return $true
    }.GetNewClosure()
    $script:drainAdapter = { param($baseUri) return $true }.GetNewClosure()
    $script:caddyAdapter = { param($exe, $config, $environment) return $true }.GetNewClosure()
    $env:PAL_CONTROL_DEPLOYMENT_TEST_HOOKS = "1"

    $v1 = New-TestRelease -Version "1.0.0" -RevisionCharacter "a"
    $v2 = New-TestRelease -Version "1.1.0" -RevisionCharacter "b"
    $v3 = New-TestRelease -Version "2.0.0" -RevisionCharacter "c" -DataContractVersion 2

    Assert-Throws -Action {
        $bad = [pscustomobject]@{ Archive = $v1.Archive; Sha256 = ("0" * 64) }
        Invoke-TestDeployment -Action Stage -Release $bad
    } -Pattern "SHA-256 does not match"

    $undeclaredFile = Join-Path $v1.Container "undeclared.txt"
    [IO.File]::WriteAllText($undeclaredFile, "not-in-release-manifest", (New-Object Text.UTF8Encoding($false)))
    $undeclaredArchive = Join-Path $v1.Container "undeclared-payload.zip"
    Compress-Archive -Path @($v1.Payload, $undeclaredFile) `
        -DestinationPath $undeclaredArchive -CompressionLevel Optimal
    $undeclaredRelease = [pscustomobject]@{
        Archive = $undeclaredArchive
        Sha256 = Get-PalControlFileSha256 -Path $undeclaredArchive
    }
    Assert-Throws -Action {
        Invoke-TestDeployment -Action Stage -Release $undeclaredRelease
    } -Pattern "outside its declared payload root"

    $staged = Invoke-TestDeployment -Action Stage -Release $v1
    Assert-True ($staged.releaseId -eq $v1.Id) "Stage did not return the verified release id."
    $stagedAgain = Invoke-TestDeployment -Action Stage -Release $v1
    Assert-True ($stagedAgain.manifestSha256 -eq $staged.manifestSha256) `
        "Repeated staging was not idempotent."

    [IO.File]::WriteAllText(
        (Join-Path $stateRoot "data\ledger.fixture"),
        "before-v1",
        (New-Object Text.UTF8Encoding($false)))
    $installed = Invoke-TestDeployment -Action Install -Release $v1
    Assert-True ($installed.activeReleaseId -eq $v1.Id) "Install did not activate v1."
    Assert-True ($shared.services["PalControl.ControlApi"].account -eq "NT SERVICE\PalControl.ControlApi") `
        "Control API was not configured with its virtual service account."
    Assert-True ($shared.services["PalControl.Caddy"].account -eq "NT SERVICE\PalControl.Caddy") `
        "Caddy was not configured with its isolated virtual service account."
    Assert-True (($shared.services["PalControl.ControlApi"].environment -join "`n") -match "PAL_CONTROL_CONFIG_PATH=") `
        "Control API external configuration was not injected."
    Assert-True (($shared.services["PalControl.Caddy"].environment -join "`n") -match "XDG_DATA_HOME=") `
        "Caddy persistent TLS data location was not injected."
    Assert-True ((Get-Content -LiteralPath $caddyEnvironment -Raw) -like "*$($v1.Id)*") `
        "Caddy player root was not switched to v1."

    $settings.Federation.Nodes[1].NodeKeyFile = Join-Path $testRoot "outside-secrets.key"
    Write-AtomicUtf8Json -Path $configPath -Value $settings
    Assert-Throws -Action {
        Invoke-TestDeployment -Action Install -Release $v1
    } -Pattern "service secret escapes its approved root"

    $settings.Federation.Nodes[1].NodeKeyFile = $federationRemoteNodeKey
    $settings.Federation.IdentityHmacKeyFile = Join-Path $secretsRoot "missing-identity.key"
    Write-AtomicUtf8Json -Path $configPath -Value $settings
    Assert-Throws -Action {
        Invoke-TestDeployment -Action Install -Release $v1
    } -Pattern "Configured service secret is missing"

    $settings.Federation.IdentityHmacKeyFile = $federationIdentityKey
    Write-AtomicUtf8Json -Path $configPath -Value $settings

    $replay = Invoke-TestDeployment -Action Install -Release $v1
    Assert-True ($replay.idempotentReplay -eq $true) "Repeated install was not idempotent."
    $caddyText = Get-Content -LiteralPath $caddyEnvironment -Raw -Encoding UTF8
    [IO.File]::WriteAllText(
        $caddyEnvironment,
        ($caddyText -replace [regex]::Escape($v1.Id), "wrong-static-root"),
        (New-Object Text.UTF8Encoding($false)))
    $repairedReplay = Invoke-TestDeployment -Action Install -Release $v1
    Assert-True ($repairedReplay.idempotentReplay -eq $true) `
        "Repeated install with drift was not handled idempotently."
    Assert-True ((Get-Content -LiteralPath $caddyEnvironment -Raw) -like "*$($v1.Id)*") `
        "Repeated install did not repair the versioned Caddy static root."

    [IO.File]::WriteAllText(
        (Join-Path $stateRoot "data\ledger.fixture"),
        "before-v2",
        (New-Object Text.UTF8Encoding($false)))
    $shared.rejectedReleaseId = $v2.Id
    Assert-Throws -Action {
        Invoke-TestDeployment -Action Upgrade -Release $v2
    } -Pattern "did not become read-ready"
    $stateAfterFailure = Get-Content -LiteralPath (Join-Path $stateRoot "deployment\state.json") `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($stateAfterFailure.activeReleaseId -eq $v1.Id) `
        "Failed upgrade changed the active deployment state."
    Assert-True ((Get-Content -LiteralPath (Join-Path $stateRoot "data\ledger.fixture") -Raw) -eq "before-v2") `
        "Failed upgrade did not restore the stopped-state snapshot."
    Assert-True ($shared.services["PalControl.ControlApi"].binaryPath -like "*$($v1.Id)*") `
        "Failed upgrade did not restore the previous service binary."
    Assert-True ((Get-Content -LiteralPath $caddyEnvironment -Raw) -like "*$($v1.Id)*") `
        "Failed upgrade did not restore the previous Caddy root."

    $v2Snapshot = Get-ChildItem -LiteralPath (Join-Path $stateRoot "deployment\snapshots") `
        -Directory | Where-Object {
            $manifest = Get-Content -LiteralPath (Join-Path $_.FullName "manifest.json") `
                -Raw -Encoding UTF8 | ConvertFrom-Json
            $manifest.fromReleaseId -eq $v1.Id -and $manifest.toReleaseId -eq $v2.Id
        } | Select-Object -First 1
    Assert-True ($null -ne $v2Snapshot) "The failed upgrade did not retain its cold snapshot."
    $snapshotFixture = Join-Path $v2Snapshot.FullName "data\ledger.fixture"
    $snapshotFixtureContent = Get-Content -LiteralPath $snapshotFixture -Raw -Encoding UTF8
    [IO.File]::WriteAllText(
        $snapshotFixture,
        "tampered-snapshot",
        (New-Object Text.UTF8Encoding($false)))
    Assert-Throws -Action {
        Invoke-TestDeployment -Action Upgrade -Release $v2
    } -Pattern "Cold snapshot data failed SHA-256 validation"
    Assert-True ($shared.services["PalControl.ControlApi"].running -eq $true) `
        "A rejected reused snapshot left the previous Control API stopped."
    Assert-True ($shared.services["PalControl.Caddy"].running -eq $true) `
        "A rejected reused snapshot left the previous public boundary stopped."
    Assert-True ($shared.services["PalControl.ControlApi"].binaryPath -like "*$($v1.Id)*") `
        "A rejected reused snapshot changed the active binary."
    [IO.File]::WriteAllText(
        $snapshotFixture,
        $snapshotFixtureContent,
        (New-Object Text.UTF8Encoding($false)))

    $shared.rejectedReleaseId = $null
    $upgraded = Invoke-TestDeployment -Action Upgrade -Release $v2
    Assert-True ($upgraded.activeReleaseId -eq $v2.Id) "Upgrade did not activate v2."
    [IO.File]::WriteAllText(
        (Join-Path $stateRoot "data\ledger.fixture"),
        "after-v2-transaction",
        (New-Object Text.UTF8Encoding($false)))
    $rolledBack = Invoke-TestDeployment -Action Rollback -TargetReleaseId $v1.Id
    Assert-True ($rolledBack.activeReleaseId -eq $v1.Id) "Binary rollback did not activate v1."
    Assert-True ((Get-Content -LiteralPath (Join-Path $stateRoot "data\ledger.fixture") -Raw) -eq "after-v2-transaction") `
        "Same-contract binary rollback rewound authoritative data."

    $upgradedContract = Invoke-TestDeployment -Action Upgrade -Release $v3
    Assert-True ($upgradedContract.activeReleaseId -eq $v3.Id) "Contract v2 release did not activate."
    Assert-Throws -Action {
        Invoke-TestDeployment -Action Rollback -TargetReleaseId $v1.Id
    } -Pattern "same data contract"
    $finalState = Get-Content -LiteralPath (Join-Path $stateRoot "deployment\state.json") `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($finalState.activeReleaseId -eq $v3.Id) `
        "Incompatible rollback changed deployment state."

    $verified = Invoke-TestDeployment -Action Verify -TargetReleaseId $v3.Id
    Assert-True ($verified.active -eq $true) "Verify did not identify the active release."

    $apiProject = Join-Path $repositoryRoot "services\control-api\PalControl.ControlApi.csproj"
    $apiAssembly = Join-Path $repositoryRoot `
        "services\control-api\bin\Release\net10.0\PalControl.ControlApi.dll"
    $migrationHarness = Join-Path $repositoryRoot `
        "tests\production-deployment\PalControl.ProductionDeployment.Harness.csproj"
    $migrationHarnessAssembly = Join-Path $repositoryRoot `
        "tests\production-deployment\bin\Release\net10.0\PalControl.ProductionDeployment.Harness.dll"
    & dotnet build $apiProject --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Control API Release build failed." }
    & dotnet build $migrationHarness --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Production migration harness build failed." }

    $runtimeRoot = Join-Path $testRoot "runtime"
    $runtimeData = Join-Path $runtimeRoot "data"
    $runtimeConfig = Join-Path $runtimeRoot "appsettings.external.json"
    $runtimeInstall = Join-Path $runtimeRoot "PalServer"
    $runtimeWorldId = "ABCDEF0123456789ABCDEF0123456789"
    $runtimeSettingsRoot = Join-Path $runtimeInstall "Pal\Saved\Config\WindowsServer"
    $runtimeWorldRoot = Join-Path $runtimeInstall "Pal\Saved\SaveGames\0\$runtimeWorldId"
    New-Item -ItemType Directory -Path $runtimeRoot, $runtimeData, `
        $runtimeSettingsRoot, $runtimeWorldRoot -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $runtimeInstall "PalServer.exe"), [byte[]]@(0))
    [IO.File]::WriteAllText(
        (Join-Path $runtimeSettingsRoot "GameUserSettings.ini"),
        "[/Script/Pal.PalGameLocalSettings]`r`nDedicatedServerName=$runtimeWorldId`r`n",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllBytes((Join-Path $runtimeWorldRoot "Level.sav"), [byte[]](1..32))
    $runtimeSettings = Get-Content -LiteralPath (
        Join-Path $repositoryRoot "services\control-api\appsettings.json") `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    $runtimePort = Get-FreeTcpPort
    $unavailableRestPort = Get-FreeTcpPort
    $runtimeSettings.Urls = "http://127.0.0.1:$runtimePort"
    $runtimeSettings.Palworld.InstallRoot = $runtimeInstall
    $runtimeSettings.Palworld.OfficialRestApi.BaseUrl =
        "http://127.0.0.1:$unavailableRestPort/v1/api/"
    $runtimeSettings.Palworld.OfficialRestApi.TimeoutSeconds = 1
    $runtimeSettings.CommandPersistence.DataDirectory = $runtimeData
    $runtimeSettings.ExtractionMode.Enabled = $false
    $runtimeSettings.ExtractionMode.Persistence.DataDirectory = Join-Path $runtimeData "extraction"
    $runtimeSettings.ExtractionMode.Continuity.BackupRoot = Join-Path $runtimeRoot "backups\economy"
    $runtimeSettings.ExtractionMode.Continuity.StagingRoot = Join-Path $runtimeRoot "staging\economy"
    $runtimeSettings.SaveManagement.BackupRoot = Join-Path $runtimeRoot "backups\savegames"
    $runtimeSettings.SaveManagement.RequireRunningProcess = $false
    $runtimeSettings.Security.StartupValidation.LogDirectory = Join-Path $runtimeRoot "logs"
    $runtimeSettings.Security.AdminAuthentication.Enabled = $true
    $runtimeSettings.Security.AdminAuthentication.EnableLoopbackDevelopmentPrincipal = $true
    $runtimeSettings.Security.AdminAuthentication.DevelopmentPrincipalSubject = "deployment-test"
    $runtimeSettings.Security.AdminAuthentication.Principals = @(
        [pscustomobject]@{
            Subject = "deployment-test"
            ApiKeySha256 = "87052f5138109134ec8e8b25a5e18545e39c90244679e52b6c40c364cb671060"
            Roles = @("Owner")
            TotpSecretBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
            Enabled = $true
        }
    )
    $runtimeSettings.PlayerPortal.Enabled = $false
    $runtimeSettings.Palworld | Add-Member -NotePropertyName ResourceCatalogPath `
        -NotePropertyValue (Join-Path $runtimeRoot "resources\palworld-resource-catalog.json") -Force
    Write-AtomicUtf8Json -Path $runtimeConfig -Value $runtimeSettings -Depth 20

    Start-ExternalConfigApiOnce -ApiAssembly $apiAssembly -ExternalConfig $runtimeConfig `
        -Port $runtimePort -LogPrefix (Join-Path $runtimeRoot "first-start")
    $database = Join-Path $runtimeData "extraction\extraction-commerce.db"
    $firstEvidenceJson = & dotnet $migrationHarnessAssembly $database
    if ($LASTEXITCODE -ne 0) { throw "First startup migration inspection failed." }
    $firstEvidence = $firstEvidenceJson | ConvertFrom-Json

    Start-ExternalConfigApiOnce -ApiAssembly $apiAssembly -ExternalConfig $runtimeConfig `
        -Port $runtimePort -LogPrefix (Join-Path $runtimeRoot "second-start")
    $secondEvidenceJson = & dotnet $migrationHarnessAssembly $database
    if ($LASTEXITCODE -ne 0) { throw "Second startup migration inspection failed." }
    $secondEvidence = $secondEvidenceJson | ConvertFrom-Json
    Assert-True ($secondEvidence.migrationFingerprint -eq $firstEvidence.migrationFingerprint) `
        "Repeated startup changed the idempotent migration evidence."
    Assert-True ([int]$secondEvidence.migrationCount -eq [int]$firstEvidence.migrationCount) `
        "Repeated startup duplicated a component migration."

    Write-Host "PASS: production deployment staging, isolated services, cold backup, upgrade recovery, same-contract rollback and real idempotent startup migration."
}
finally {
    $env:PAL_CONTROL_DEPLOYMENT_TEST_HOOKS = $previousHookValue
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedTestRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a test path outside the temporary directory: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

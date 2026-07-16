[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Stage", "Install", "Upgrade", "Rollback", "Verify")]
    [string]$Action,

    [string]$ReleaseArchive,
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedSha256,
    [string]$TargetReleaseId,

    [string]$InstallRoot = "C:\Program Files\PalControl",
    [string]$StateRoot = "C:\ProgramData\PalControl",
    [string]$ConfigurationPath = "C:\ProgramData\PalControl\config\appsettings.Production.json",
    [string]$ControlApiServiceName = "PalControl.ControlApi",
    [string]$ControlApiServiceAccount = "NT SERVICE\PalControl.ControlApi",
    [uri]$ControlApiBaseUri = "http://127.0.0.1:5180",
    [ValidateRange(5, 300)]
    [int]$HealthTimeoutSeconds = 60,

    [securestring]$AdminApiKey,
    [string]$AdminApiKeyFile,

    [switch]$SkipCaddy,
    [string]$CaddyExecutablePath,
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$CaddyExpectedSha256,
    [string]$CaddyConfigPath = "C:\ProgramData\PalControl\caddy\Caddyfile",
    [string]$CaddyEnvironmentFile = "C:\ProgramData\PalControl\caddy\player-portal.env",
    [string]$CaddyServiceName = "PalControl.Caddy",
    [string]$CaddyServiceAccount = "NT SERVICE\PalControl.Caddy",
    [string]$CaddyDataHome = "C:\ProgramData\PalControl\caddy-data",
    [string]$CaddyConfigHome = "C:\ProgramData\PalControl\caddy-config",
    [string]$CaddyLogRoot = "C:\ProgramData\PalControl\caddy-logs",

    [switch]$EnableTestHooks,
    [scriptblock]$ServiceAdapter,
    [scriptblock]$HealthProbeAdapter,
    [scriptblock]$DrainProbeAdapter,
    [scriptblock]$CaddyValidationAdapter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "PalControl.Deployment.psm1") -Force

$testHooksRequested = $null -ne $ServiceAdapter -or
    $null -ne $HealthProbeAdapter -or
    $null -ne $DrainProbeAdapter -or
    $null -ne $CaddyValidationAdapter
if (($testHooksRequested -or $EnableTestHooks) -and
    (-not $EnableTestHooks -or $env:PAL_CONTROL_DEPLOYMENT_TEST_HOOKS -ne "1")) {
    throw "Deployment adapters are test-only and require both -EnableTestHooks and PAL_CONTROL_DEPLOYMENT_TEST_HOOKS=1."
}

$install = Assert-SafeDeploymentRoot -Path $InstallRoot -Name "InstallRoot"
$state = Assert-SafeDeploymentRoot -Path $StateRoot -Name "StateRoot"
if ($install.StartsWith($state.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $state.StartsWith($install.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "InstallRoot and StateRoot must be independent trees."
}
if ($ControlApiBaseUri.AbsoluteUri.TrimEnd('/') -ne "http://127.0.0.1:5180") {
    throw "ControlApiBaseUri must be exactly http://127.0.0.1:5180."
}
foreach ($serviceName in @($ControlApiServiceName, $CaddyServiceName)) {
    if ($serviceName -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{2,79}$') {
        throw "Windows service names must use 3-80 safe characters."
    }
}
if ($ControlApiServiceName -eq $CaddyServiceName -or
    $ControlApiServiceAccount -ne "NT SERVICE\$ControlApiServiceName" -or
    $CaddyServiceAccount -ne "NT SERVICE\$CaddyServiceName") {
    throw "Control API and Caddy require distinct matching NT SERVICE virtual accounts."
}
Assert-PathWithinRoot -Path $ConfigurationPath -Root $state `
    -Description "production configuration" | Out-Null
if (-not $SkipCaddy) {
    foreach ($caddyStatePath in @(
        $CaddyConfigPath,
        $CaddyEnvironmentFile,
        $CaddyDataHome,
        $CaddyConfigHome,
        $CaddyLogRoot)) {
        Assert-PathWithinRoot -Path $caddyStatePath -Root $state `
            -Description "Caddy persistent path" | Out-Null
    }
}
if ($EnableTestHooks) {
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $install.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $state.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Test hooks may only operate under the current temporary directory."
    }
}
else {
    $principal = New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Production deployment must run from an elevated PowerShell session."
    }
}

New-Item -ItemType Directory -Path $install, $state -Force | Out-Null
Assert-NoReparsePoint -Path $install | Out-Null
Assert-NoReparsePoint -Path $state | Out-Null
$deploymentRoot = Join-Path $state "deployment"
New-Item -ItemType Directory -Path $deploymentRoot -Force | Out-Null
if (-not $EnableTestHooks) {
    & icacls.exe $deploymentRoot /inheritance:r /grant:r `
        "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to protect the production deployment state."
    }
    if (@(Get-ChildItem -LiteralPath $deploymentRoot -Force).Count -gt 0) {
        & icacls.exe (Join-Path $deploymentRoot "*") /reset /T /C | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to reset child ACLs under the production deployment state."
        }
    }
}
$deploymentStatePath = Join-Path $deploymentRoot "state.json"
$lockPath = Join-Path $deploymentRoot "deployment.lock"
$lock = $null

function ConvertFrom-SecureValue {
    param([Parameter(Mandatory)] [securestring]$Value)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Get-OptionalPropertyValue {
    param(
        $Object,
        [Parameter(Mandatory)] [string[]]$Path
    )
    $current = $Object
    foreach ($name in $Path) {
        if ($null -eq $current) { return $null }
        $property = $current.PSObject.Properties[$name]
        if ($null -eq $property) { return $null }
        $current = $property.Value
    }
    return $current
}

function Get-ConfiguredServiceSecretPaths {
    param([Parameter(Mandatory)]$Settings)

    $paths = New-Object Collections.Generic.List[string]
    foreach ($path in @(
        [string](Get-OptionalPropertyValue $Settings @("Palworld", "PalDefenderRestApi", "TokenFile")),
        [string](Get-OptionalPropertyValue $Settings @("ExtractionMode", "Rcon", "PasswordFile")))) {
        if (-not [string]::IsNullOrWhiteSpace($path)) { $paths.Add($path) }
    }

    if ((Get-OptionalPropertyValue $Settings @("Federation", "Enabled")) -eq $true) {
        foreach ($path in @(
            [string](Get-OptionalPropertyValue $Settings @("Federation", "IdentityHmacKeyFile")),
            [string](Get-OptionalPropertyValue $Settings @("Federation", "InboundNodeKeyFile")))) {
            if (-not [string]::IsNullOrWhiteSpace($path)) { $paths.Add($path) }
        }

        $nodes = Get-OptionalPropertyValue $Settings @("Federation", "Nodes")
        if ($null -ne $nodes) {
            foreach ($node in @($nodes)) {
                if ($null -eq $node -or
                    (Get-OptionalPropertyValue $node @("Local")) -eq $true) {
                    continue
                }
                $path = [string](Get-OptionalPropertyValue $node @("NodeKeyFile"))
                if (-not [string]::IsNullOrWhiteSpace($path)) { $paths.Add($path) }
            }
        }
    }

    return @($paths | Sort-Object -Unique)
}

function Assert-ServiceSecretFile {
    param([Parameter(Mandatory)] [string]$Path)

    $isDriveAbsolute = $Path -match '^[A-Za-z]:[\\/]'
    $isUncAbsolute = $Path -match '^[\\/]{2}[^\\/]+[\\/][^\\/]+(?:[\\/]|$)'
    if (-not [IO.Path]::IsPathRooted($Path) -or
        (-not $isDriveAbsolute -and -not $isUncAbsolute)) {
        throw "Production secret file paths must be absolute."
    }

    $full = Assert-PathWithinRoot -Path $Path -Root (Join-Path $state "secrets") `
        -Description "service secret"
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Configured service secret is missing: $full"
    }
    return Assert-NoReparsePoint -Path $full
}

function Assert-PrivateSecretFile {
    param([Parameter(Mandatory)] [string]$Path)
    $full = Assert-NoReparsePoint -Path $Path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Secret file does not exist: $full"
    }
    if ($EnableTestHooks) { return $full }
    $broadSids = @("S-1-1-0", "S-1-5-11", "S-1-5-32-545")
    $acl = Get-Acl -LiteralPath $full
    foreach ($rule in $acl.Access) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($broadSids -contains $sid -and
            $rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadData)) {
            throw "Secret file grants read access to a broad principal: $sid"
        }
    }
    return $full
}

function Get-AdminKeyText {
    if ($null -ne $AdminApiKey) {
        return ConvertFrom-SecureValue -Value $AdminApiKey
    }
    if (-not [string]::IsNullOrWhiteSpace($AdminApiKeyFile)) {
        $path = Assert-PrivateSecretFile -Path $AdminApiKeyFile
        $value = (Get-Content -LiteralPath $path -Raw -Encoding UTF8).Trim()
        if ([string]::IsNullOrWhiteSpace($value)) { throw "AdminApiKeyFile is empty." }
        return $value
    }
    throw "Upgrade and rollback require AdminApiKey or AdminApiKeyFile for the drain preflight."
}

function Invoke-ScChecked {
    param([Parameter(Mandatory)] [string[]]$Arguments)
    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failed ($LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }
    return $output
}

function Invoke-ServiceRequest {
    param(
        [Parameter(Mandatory)] [string]$Operation,
        [Parameter(Mandatory)] [string]$Name,
        [hashtable]$Properties = @{}
    )
    $request = [pscustomobject]@{
        operation = $Operation
        name = $Name
        properties = [pscustomobject]$Properties
    }
    if ($null -ne $ServiceAdapter) {
        return & $ServiceAdapter $request
    }
    switch ($Operation) {
        "Exists" { return $null -ne (Get-Service -Name $Name -ErrorAction SilentlyContinue) }
        "Stop" {
            $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
            if ($service -and $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
                Stop-Service -Name $Name -Force
                $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
            }
            return $true
        }
        "Start" {
            $service = Get-Service -Name $Name -ErrorAction Stop
            if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running) {
                Start-Service -Name $Name
                $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(60))
            }
            return $true
        }
        "Configure" {
            $binaryPath = [string]$Properties.BinaryPath
            $account = [string]$Properties.Account
            $displayName = [string]$Properties.DisplayName
            if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
                Invoke-ScChecked @("config", $Name, "binPath=", $binaryPath, "start=", "delayed-auto", "obj=", $account) | Out-Null
            }
            else {
                Invoke-ScChecked @("create", $Name, "binPath=", $binaryPath, "start=", "delayed-auto", "obj=", $account, "DisplayName=", $displayName) | Out-Null
            }
            Invoke-ScChecked @("description", $Name, [string]$Properties.Description) | Out-Null
            Invoke-ScChecked @("sidtype", $Name, "unrestricted") | Out-Null
            Invoke-ScChecked @("failure", $Name, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000") | Out-Null
            Invoke-ScChecked @("failureflag", $Name, "1") | Out-Null
            $environment = @($Properties.Environment)
            if ($environment.Count -gt 0) {
                $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
                New-ItemProperty -LiteralPath $registryPath -Name Environment `
                    -PropertyType MultiString -Value $environment -Force | Out-Null
            }
            return $true
        }
        default { throw "Unsupported service operation '$Operation'." }
    }
}

function Test-ControlApiHealth {
    $healthUri = [uri]::new($ControlApiBaseUri, "/health/ready")
    if ($null -ne $HealthProbeAdapter) {
        return [bool](& $HealthProbeAdapter $healthUri)
    }
    try {
        $response = Invoke-RestMethod -Uri $healthUri -TimeoutSec 3
        return $response.readReady -eq $true
    }
    catch { return $false }
}

function Wait-ControlApiHealth {
    $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-ControlApiHealth) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Control API did not become read-ready within $HealthTimeoutSeconds seconds."
}

function Test-UpgradeDrain {
    if ($null -ne $DrainProbeAdapter) {
        $result = & $DrainProbeAdapter $ControlApiBaseUri
        if ($result -ne $true -and $result.ready -ne $true) {
            throw "Test drain adapter reported a blocked deployment."
        }
        return
    }
    $key = Get-AdminKeyText
    try {
        $headers = @{ "X-Pal-Admin-Key" = $key }
        $readiness = Invoke-RestMethod `
            -Uri ([uri]::new($ControlApiBaseUri, "/api/v1/extraction/admin/rollover/readiness")) `
            -Headers $headers -TimeoutSec 10 -MaximumRedirection 0
        if ($readiness.maintenance.maintenance -ne $true -or
            $readiness.readyForWorldSwitch -ne $true -or
            [int]$readiness.activeOperations -ne 0 -or
            @($readiness.blockingOrders).Count -ne 0 -or
            @($readiness.blockingRuns).Count -ne 0) {
            throw "Upgrade requires maintenance mode, zero active operations, and no blocking orders or settlements."
        }
        $overview = Invoke-RestMethod `
            -Uri ([uri]::new($ControlApiBaseUri, "/api/v1/extraction/admin/operations/overview?limit=250&refresh=true")) `
            -Headers $headers -TimeoutSec 20 -MaximumRedirection 0
        $pending = [int]$overview.queues.delivery.pending +
            [int]$overview.queues.settlement.pending +
            [int]$overview.queues.outbox.pending
        $uncertain = 0
        foreach ($property in $overview.queues.uncertain.PSObject.Properties) {
            $uncertain += [int]$property.Value
        }
        if ($pending -ne 0 -or $uncertain -ne 0 -or $null -ne $overview.rollover) {
            throw "Upgrade is blocked by queue work, uncertain transactions, or an incomplete weekly rollover."
        }
        if ($overview.backups.economy.requiredForWrites -eq $true -and
            $overview.backups.economy.fresh -ne $true) {
            throw "Upgrade requires the configured recent economy backup evidence."
        }
    }
    finally {
        $key = $null
    }
}

function Read-DeploymentState {
    if (-not (Test-Path -LiteralPath $deploymentStatePath -PathType Leaf)) { return $null }
    try {
        $value = Get-Content -LiteralPath $deploymentStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch { throw "Deployment state is not valid JSON: $($_.Exception.Message)" }
    if ($value.schemaVersion -ne 1) { throw "Unsupported deployment state schema." }
    return $value
}

function Assert-ProductionConfiguration {
    $path = Assert-NoReparsePoint -Path $ConfigurationPath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Production configuration is missing: $path"
    }
    try { $settings = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Production configuration is not valid JSON: $($_.Exception.Message)" }
    if ([string]$settings.Urls -ne "http://127.0.0.1:5180") {
        throw "Production Control API must listen only on http://127.0.0.1:5180."
    }
    $persistentPaths = @(
        [string]$settings.CommandPersistence.DataDirectory,
        [string]$settings.ExtractionMode.Persistence.DataDirectory,
        [string]$settings.ExtractionMode.Continuity.BackupRoot,
        [string]$settings.ExtractionMode.Continuity.StagingRoot,
        [string]$settings.SaveManagement.BackupRoot,
        [string]$settings.Security.StartupValidation.LogDirectory,
        [string]$settings.Palworld.ResourceCatalogPath
    )
    foreach ($persistentPath in $persistentPaths) {
        if ([string]::IsNullOrWhiteSpace($persistentPath) -or
            -not [IO.Path]::IsPathRooted($persistentPath)) {
            throw "Every production state/catalog/log path must be absolute."
        }
        Assert-PathWithinRoot -Path $persistentPath -Root $state -Description "persistent production path" | Out-Null
    }
    if ($settings.ExtractionMode.Enabled -eq $true -and
        -not (Test-Path -LiteralPath ([string]$settings.Palworld.ResourceCatalogPath) -PathType Leaf)) {
        throw "Enabled extraction mode requires the authorized external resource catalog."
    }
    $secretPaths = @(
        Get-ConfiguredServiceSecretPaths -Settings $settings |
            ForEach-Object { Assert-ServiceSecretFile -Path $_ }
    )
    return [pscustomobject]@{
        Path = $path
        Settings = $settings
        PersistentPaths = $persistentPaths
        SecretPaths = $secretPaths
    }
}

function Set-PrivateAcl {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Account,
        [ValidateSet("Read", "Modify")] [string]$Access,
        [switch]$Directory
    )
    if ($EnableTestHooks) { return }
    if ($Directory) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        $suffix = if ($Access -eq "Modify") { "(OI)(CI)(M)" } else { "(OI)(CI)(RX)" }
        $system = "*S-1-5-18:(OI)(CI)(F)"
        $admins = "*S-1-5-32-544:(OI)(CI)(F)"
    }
    else {
        $suffix = if ($Access -eq "Modify") { "(M)" } else { "(R)" }
        $system = "*S-1-5-18:(F)"
        $admins = "*S-1-5-32-544:(F)"
    }
    & icacls.exe $Path /inheritance:r /grant:r $system $admins "${Account}:$suffix" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to apply the private ACL to '$Path'." }
}

function Set-StateRootAcl {
    if ($EnableTestHooks) { return }
    $grants = @(
        "*S-1-5-18:(F)",
        "*S-1-5-32-544:(F)",
        "${ControlApiServiceAccount}:(RX)"
    )
    if (-not $SkipCaddy) { $grants += "${CaddyServiceAccount}:(RX)" }
    & icacls.exe $state /inheritance:r /grant:r $grants | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to protect the production state root." }
    & icacls.exe $deploymentRoot /inheritance:r /grant:r `
        "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to protect deployment snapshots and state." }
}

function Get-CaddyEnvironmentContent {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$PlayerRoot
    )
    $full = Assert-NoReparsePoint -Path $Path
    $lines = @(Get-Content -LiteralPath $full -Encoding UTF8)
    $found = 0
    $accessLogFound = 0
    $updated = foreach ($line in $lines) {
        if ($line -match '^\s*PLAYER_PORTAL_ROOT\s*=') {
            $found++
            "PLAYER_PORTAL_ROOT=$($PlayerRoot.Replace('\', '/'))"
        }
        elseif ($line -match '^\s*PLAYER_PORTAL_ACCESS_LOG\s*=\s*(.+?)\s*$') {
            $accessLogFound++
            $logValue = $Matches[1].Trim().Trim('"').Replace('/', '\')
            if (-not [IO.Path]::IsPathRooted($logValue)) {
                throw "PLAYER_PORTAL_ACCESS_LOG must be an absolute path."
            }
            Assert-PathWithinRoot -Path $logValue -Root $CaddyLogRoot `
                -Description "Caddy access log" | Out-Null
            $line
        }
        else { $line }
    }
    if ($found -ne 1) {
        throw "Caddy environment file must contain exactly one PLAYER_PORTAL_ROOT entry."
    }
    if ($accessLogFound -ne 1) {
        throw "Caddy environment file must contain exactly one PLAYER_PORTAL_ACCESS_LOG entry."
    }
    return ($updated -join [Environment]::NewLine) + [Environment]::NewLine
}

function Write-AtomicText {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$Content)
    $full = Resolve-FullPath $Path
    $temporary = "$full.$([Guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, $Content, (New-Object Text.UTF8Encoding($false)))
    try {
        if (Test-Path -LiteralPath $full) {
            $backup = "$full.replace-backup"
            [IO.File]::Replace($temporary, $full, $backup, $true)
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        }
        else { Move-Item -LiteralPath $temporary -Destination $full }
    }
    finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
}

function Test-CaddyConfiguration {
    param([Parameter(Mandatory)] [string]$PlayerRoot)
    if ($SkipCaddy) { return }
    $caddy = Assert-NoReparsePoint -Path $CaddyExecutablePath
    $config = Assert-NoReparsePoint -Path $CaddyConfigPath
    $envFile = Assert-NoReparsePoint -Path $CaddyEnvironmentFile
    if (-not (Test-Path -LiteralPath $caddy -PathType Leaf) -or
        -not (Test-Path -LiteralPath $config -PathType Leaf) -or
        -not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
        throw "Caddy executable, Caddyfile and environment file are required."
    }
    if ([string]::IsNullOrWhiteSpace($CaddyExpectedSha256) -or
        (Get-FileHash -LiteralPath $caddy -Algorithm SHA256).Hash.ToLowerInvariant() -ne
            $CaddyExpectedSha256.ToLowerInvariant()) {
        throw "Caddy executable SHA-256 does not match the approved pinned binary."
    }
    $content = Get-CaddyEnvironmentContent -Path $envFile -PlayerRoot $PlayerRoot
    $temporary = Join-Path (Split-Path -Parent $envFile) (
        ".player-portal-$([Guid]::NewGuid().ToString('N')).env")
    [IO.File]::WriteAllText($temporary, $content, (New-Object Text.UTF8Encoding($false)))
    try {
        if ($null -ne $CaddyValidationAdapter) {
            if (-not [bool](& $CaddyValidationAdapter $caddy $config $temporary)) {
                throw "Caddy validation adapter rejected the deployment."
            }
        }
        else {
            & $caddy validate --config $config --adapter caddyfile --envfile $temporary
            if ($LASTEXITCODE -ne 0) { throw "caddy validate failed with exit code $LASTEXITCODE." }
        }
    }
    finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    return $content
}

function Configure-ControlApiService {
    param([Parameter(Mandatory)]$Release)
    Invoke-ServiceRequest -Operation Configure -Name $ControlApiServiceName -Properties @{
        BinaryPath = '"' + $Release.ExecutablePath + '"'
        Account = $ControlApiServiceAccount
        DisplayName = "Pal Control API"
        Description = "Pal Control loopback API and hosted durable workers"
        Environment = @(
            "DOTNET_ENVIRONMENT=Production",
            "PAL_CONTROL_CONFIG_PATH=$([IO.Path]::GetFullPath($ConfigurationPath))"
        )
    } | Out-Null
}

function Configure-CaddyService {
    if ($SkipCaddy) { return }
    $binary = '"' + [IO.Path]::GetFullPath($CaddyExecutablePath) + '" run --config "' +
        [IO.Path]::GetFullPath($CaddyConfigPath) + '" --adapter caddyfile --envfile "' +
        [IO.Path]::GetFullPath($CaddyEnvironmentFile) + '"'
    Invoke-ServiceRequest -Operation Configure -Name $CaddyServiceName -Properties @{
        BinaryPath = $binary
        Account = $CaddyServiceAccount
        DisplayName = "Pal Control Caddy"
        Description = "Public TLS boundary for the Pal Control player portal"
        Environment = @(
            "XDG_DATA_HOME=$([IO.Path]::GetFullPath($CaddyDataHome))",
            "XDG_CONFIG_HOME=$([IO.Path]::GetFullPath($CaddyConfigHome))"
        )
    } | Out-Null
}

function Apply-ProductionAcls {
    param([Parameter(Mandatory)]$Release, [Parameter(Mandatory)]$Configuration)
    $secretPaths = @(
        $Configuration.SecretPaths |
            ForEach-Object { Assert-ServiceSecretFile -Path $_ }
    )
    Set-StateRootAcl
    Set-PrivateAcl -Path $Release.Root -Account $ControlApiServiceAccount -Access Read -Directory
    foreach ($path in $Configuration.PersistentPaths) {
        $isFile = [IO.Path]::HasExtension($path) -and
            $path.EndsWith("palworld-resource-catalog.json", [StringComparison]::OrdinalIgnoreCase)
        if ($isFile) {
            $parent = Split-Path -Parent $path
            Set-PrivateAcl -Path $parent -Account $ControlApiServiceAccount -Access Read -Directory
            if (Test-Path -LiteralPath $path) { Set-PrivateAcl -Path $path -Account $ControlApiServiceAccount -Access Read }
        }
        else { Set-PrivateAcl -Path $path -Account $ControlApiServiceAccount -Access Modify -Directory }
    }
    Set-PrivateAcl -Path (Split-Path -Parent $Configuration.Path) `
        -Account $ControlApiServiceAccount -Access Read -Directory
    Set-PrivateAcl -Path $Configuration.Path -Account $ControlApiServiceAccount -Access Read
    foreach ($secretPath in $secretPaths) {
        Set-PrivateAcl -Path (Split-Path -Parent $secretPath) `
            -Account $ControlApiServiceAccount -Access Read -Directory
        Set-PrivateAcl -Path $secretPath -Account $ControlApiServiceAccount -Access Read
    }
    if (-not $SkipCaddy) {
        Set-PrivateAcl -Path (Split-Path -Parent $CaddyConfigPath) `
            -Account $CaddyServiceAccount -Access Read -Directory
        Set-PrivateAcl -Path $CaddyExecutablePath -Account $CaddyServiceAccount -Access Read
        Set-PrivateAcl -Path $Release.Root -Account $CaddyServiceAccount -Access Read -Directory
        Set-PrivateAcl -Path $CaddyConfigPath -Account $CaddyServiceAccount -Access Read
        Set-PrivateAcl -Path $CaddyEnvironmentFile -Account $CaddyServiceAccount -Access Read
        Set-PrivateAcl -Path $CaddyDataHome -Account $CaddyServiceAccount -Access Modify -Directory
        Set-PrivateAcl -Path $CaddyConfigHome -Account $CaddyServiceAccount -Access Modify -Directory
        Set-PrivateAcl -Path $CaddyLogRoot -Account $CaddyServiceAccount -Access Modify -Directory
    }
}

function Resolve-InstalledRelease {
    param([Parameter(Mandatory)] [string]$ReleaseId)
    if ($ReleaseId -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{2,100}$') {
        throw "TargetReleaseId is invalid."
    }
    $releasesRoot = Join-Path $install "releases"
    $path = Assert-PathWithinRoot -Path (Join-Path $releasesRoot $ReleaseId) `
        -Root $releasesRoot -Description "installed release"
    return Test-ReleaseDirectory -ReleaseRoot $path
}

function Save-DeploymentState {
    param($PreviousState, $Release, [string]$PreviousReleaseId, [string]$SnapshotId, [string]$Operation)
    $history = @()
    if ($null -ne $PreviousState -and $null -ne $PreviousState.history) {
        $history = @($PreviousState.history | Select-Object -Last 49)
    }
    $history += [pscustomobject]@{
        operation = $Operation
        releaseId = [string]$Release.Manifest.releaseId
        previousReleaseId = $PreviousReleaseId
        snapshotId = $SnapshotId
        completedAtUtc = [DateTime]::UtcNow.ToString("o")
    }
    $value = [ordered]@{
        schemaVersion = 1
        activeReleaseId = [string]$Release.Manifest.releaseId
        previousReleaseId = $PreviousReleaseId
        activeManifestSha256 = $Release.ManifestSha256
        dataContract = $Release.Manifest.dataContract
        controlApiServiceName = $ControlApiServiceName
        caddyServiceName = if ($SkipCaddy) { $null } else { $CaddyServiceName }
        caddyBinarySha256 = if ($SkipCaddy) { $null } else { $CaddyExpectedSha256.ToLowerInvariant() }
        lastColdSnapshotId = $SnapshotId
        updatedAtUtc = [DateTime]::UtcNow.ToString("o")
        history = $history
    }
    Write-AtomicUtf8Json -Path $deploymentStatePath -Value $value
    return [pscustomobject]$value
}

function Invoke-Activation {
    param(
        [Parameter(Mandatory)]$Target,
        $Current,
        [Parameter(Mandatory)]$Configuration,
        [Parameter(Mandatory)] [string]$Operation
    )
    $currentId = if ($null -eq $Current) { "none" } else { [string]$Current.activeReleaseId }
    $targetId = [string]$Target.Manifest.releaseId
    $playerRoot = Join-Path $Target.Root "wwwroot\player"
    if (-not (Test-Path -LiteralPath (Join-Path $playerRoot "index.html") -PathType Leaf)) {
        throw "Target release does not contain the player portal build."
    }
    $caddyContent = Test-CaddyConfiguration -PlayerRoot $playerRoot
    if ($currentId -eq $targetId) {
        Configure-ControlApiService -Release $Target
        Configure-CaddyService
        if (-not $SkipCaddy) {
            $currentCaddyContent = Get-Content -LiteralPath $CaddyEnvironmentFile -Raw -Encoding UTF8
            if ($currentCaddyContent -ne $caddyContent) {
                Invoke-ServiceRequest -Operation Stop -Name $CaddyServiceName | Out-Null
                Write-AtomicText -Path $CaddyEnvironmentFile -Content $caddyContent
            }
        }
        Apply-ProductionAcls -Release $Target -Configuration $Configuration
        Invoke-ServiceRequest -Operation Start -Name $ControlApiServiceName | Out-Null
        Wait-ControlApiHealth
        if (-not $SkipCaddy) { Invoke-ServiceRequest -Operation Start -Name $CaddyServiceName | Out-Null }
        return [pscustomobject]@{ idempotentReplay = $true; activeReleaseId = $targetId }
    }

    if ($null -ne $Current) { Test-UpgradeDrain }
    $originalCaddyContent = if ($SkipCaddy) { $null } else {
        Get-Content -LiteralPath $CaddyEnvironmentFile -Raw -Encoding UTF8
    }
    $previousRelease = if ($null -eq $Current) { $null } else {
        Resolve-InstalledRelease -ReleaseId ([string]$Current.activeReleaseId)
    }
    $snapshot = $null
    try {
        if (-not $SkipCaddy) { Invoke-ServiceRequest -Operation Stop -Name $CaddyServiceName | Out-Null }
        Invoke-ServiceRequest -Operation Stop -Name $ControlApiServiceName | Out-Null
        $snapshot = New-ColdStateSnapshot -StateRoot $state -FromReleaseId $currentId -ToReleaseId $targetId
        Configure-ControlApiService -Release $Target
        Configure-CaddyService
        if (-not $SkipCaddy) { Write-AtomicText -Path $CaddyEnvironmentFile -Content $caddyContent }
        Apply-ProductionAcls -Release $Target -Configuration $Configuration
        Invoke-ServiceRequest -Operation Start -Name $ControlApiServiceName | Out-Null
        Wait-ControlApiHealth
        if (-not $SkipCaddy) { Invoke-ServiceRequest -Operation Start -Name $CaddyServiceName | Out-Null }
        return Save-DeploymentState -PreviousState $Current -Release $Target `
            -PreviousReleaseId $currentId -SnapshotId $snapshot.Id -Operation $Operation
    }
    catch {
        $activationError = $_
        if (-not $SkipCaddy) {
            try { Invoke-ServiceRequest -Operation Stop -Name $CaddyServiceName | Out-Null } catch { }
        }
        try { Invoke-ServiceRequest -Operation Stop -Name $ControlApiServiceName | Out-Null } catch { }
        if ($null -ne $snapshot) {
            try { Restore-ColdStateSnapshot -StateRoot $state -SnapshotRoot $snapshot.Root | Out-Null } catch {
                throw "Activation failed and the stopped-state snapshot could not be restored: $($_.Exception.Message). Original error: $($activationError.Exception.Message)"
            }
        }
        if (-not $SkipCaddy -and $null -ne $originalCaddyContent) {
            Write-AtomicText -Path $CaddyEnvironmentFile -Content $originalCaddyContent
        }
        if ($null -ne $previousRelease) {
            Configure-ControlApiService -Release $previousRelease
            Apply-ProductionAcls -Release $previousRelease -Configuration $Configuration
            Invoke-ServiceRequest -Operation Start -Name $ControlApiServiceName | Out-Null
            Wait-ControlApiHealth
            if (-not $SkipCaddy) { Invoke-ServiceRequest -Operation Start -Name $CaddyServiceName | Out-Null }
        }
        throw $activationError
    }
}

try {
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $current = Read-DeploymentState
    $release = $null
    if ($Action -in @("Stage", "Install", "Upgrade")) {
        if ([string]::IsNullOrWhiteSpace($ReleaseArchive) -or
            [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
            throw "$Action requires ReleaseArchive and ExpectedSha256."
        }
        $release = Expand-VerifiedReleaseArchive -ArchivePath $ReleaseArchive `
            -ExpectedSha256 $ExpectedSha256 -InstallRoot $install
    }

    switch ($Action) {
        "Stage" {
            [pscustomobject]@{
                action = "Stage"
                releaseId = [string]$release.Manifest.releaseId
                manifestSha256 = $release.ManifestSha256
                root = $release.Root
                fileCount = $release.FileCount
            }
        }
        "Verify" {
            if ([string]::IsNullOrWhiteSpace($TargetReleaseId)) {
                if ($null -eq $current) { throw "No active deployment state exists." }
                $TargetReleaseId = [string]$current.activeReleaseId
            }
            $verified = Resolve-InstalledRelease -ReleaseId $TargetReleaseId
            [pscustomobject]@{
                action = "Verify"
                releaseId = [string]$verified.Manifest.releaseId
                manifestSha256 = $verified.ManifestSha256
                fileCount = $verified.FileCount
                active = $null -ne $current -and $current.activeReleaseId -eq $TargetReleaseId
            }
        }
        "Install" {
            if ($null -ne $current -and $current.activeReleaseId -ne $release.Manifest.releaseId) {
                throw "Install cannot replace an active release; use Upgrade."
            }
            $configuration = Assert-ProductionConfiguration
            if ($configuration.Settings.PlayerPortal.Enabled -eq $true -and $SkipCaddy) {
                throw "PlayerPortal is enabled, so Caddy cannot be skipped."
            }
            Invoke-Activation -Target $release -Current $current -Configuration $configuration -Operation "Install"
        }
        "Upgrade" {
            if ($null -eq $current) { throw "Upgrade requires an existing deployment state; use Install first." }
            $configuration = Assert-ProductionConfiguration
            if ($configuration.Settings.PlayerPortal.Enabled -eq $true -and $SkipCaddy) {
                throw "PlayerPortal is enabled, so Caddy cannot be skipped."
            }
            Invoke-Activation -Target $release -Current $current -Configuration $configuration -Operation "Upgrade"
        }
        "Rollback" {
            if ($null -eq $current) { throw "Rollback requires an existing deployment state." }
            if ([string]::IsNullOrWhiteSpace($TargetReleaseId)) {
                $TargetReleaseId = [string]$current.previousReleaseId
            }
            if ([string]::IsNullOrWhiteSpace($TargetReleaseId) -or $TargetReleaseId -eq "none") {
                throw "No previous release is available for rollback."
            }
            $release = Resolve-InstalledRelease -ReleaseId $TargetReleaseId
            if ([string]$release.Manifest.dataContract.provider -ne [string]$current.dataContract.provider -or
                [int]$release.Manifest.dataContract.version -ne [int]$current.dataContract.version) {
                throw "Binary rollback is allowed only for the same data contract; use the staged recovery runbook for a schema downgrade."
            }
            $configuration = Assert-ProductionConfiguration
            Invoke-Activation -Target $release -Current $current -Configuration $configuration -Operation "Rollback"
        }
    }
}
finally {
    if ($null -ne $lock) { $lock.Dispose() }
}

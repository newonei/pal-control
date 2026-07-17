[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PalServerRoot,

    [Parameter(Mandatory = $true)]
    [string]$DeploymentResultPath,

    [string[]]$ServerArguments = @("-log"),

    [ValidateRange(15, 300)]
    [int]$StartupTimeoutSeconds = 120,

    [switch]$NetworkExposureAcknowledged,

    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$lockPath = Join-Path $repositoryRoot "mods\pal-control-native\dependencies.lock.json"

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-FileIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$ExpectedLength,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: $Path"
    }
    $file = Get-Item -LiteralPath $Path
    $sha256 = Get-Sha256 -Path $Path
    if ($file.Length -ne $ExpectedLength -or $sha256 -cne $ExpectedSha256.ToLowerInvariant()) {
        throw "$Label identity mismatch: $($file.Length) bytes/$sha256."
    }
}

function Assert-NoReparseAncestor {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not (Test-Path -LiteralPath $cursor)) {
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) {
            throw "$Label has no existing filesystem ancestor."
        }
        $cursor = $parent.FullName
    }
    while ($true) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a reparse-point ancestor: $cursor"
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName
    }
}

function Get-CurrentOperatorSid {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User) {
        throw "The current maintenance identity has no Windows SID."
    }
    return $identity.User.Value
}

function Get-PrivateAclSidValues {
    param([Parameter(Mandatory = $true)][string]$OperatorSid)

    return @("S-1-5-18", "S-1-5-32-544", $OperatorSid) | Select-Object -Unique
}

function Assert-PrivateAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OperatorSid,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label is a reparse point: $Path"
    }
    $allowed = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($sidValue in (Get-PrivateAclSidValues -OperatorSid $OperatorSid)) {
        [void]$allowed.Add($sidValue)
    }
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "$Label inherits filesystem permissions: $Path"
    }
    try {
        $ownerSid = ([Security.Principal.NTAccount]$acl.Owner).Translate(
            [Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        $ownerSid = ([Security.Principal.SecurityIdentifier]$acl.Owner).Value
    }
    if (-not $allowed.Contains($ownerSid)) {
        throw "$Label has an unapproved owner SID: $Path"
    }
    $present = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($rule in @($acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))) {
        $sidValue = $rule.IdentityReference.Value
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            -not $allowed.Contains($sidValue) -or
            ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
                [Security.AccessControl.FileSystemRights]::FullControl) {
            throw "$Label has an unapproved filesystem rule: $Path"
        }
        [void]$present.Add($sidValue)
    }
    foreach ($sidValue in $allowed) {
        if (-not $present.Contains($sidValue)) {
            throw "$Label is missing a required private ACL principal: $Path"
        }
    }
}

function Get-SafeTreeItems {
    param([Parameter(Mandatory = $true)][string]$Root)

    $items = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue([IO.Path]::GetFullPath($Root))
    while ($pending.Count -gt 0) {
        $current = Get-Item -LiteralPath $pending.Dequeue() -Force
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Maintenance backup contains a reparse point: $($current.FullName)"
        }
        $items.Add($current)
        if (-not $current.PSIsContainer) { continue }
        foreach ($child in @(Get-ChildItem -LiteralPath $current.FullName -Force)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Maintenance backup contains a reparse point: $($child.FullName)"
            }
            if ($child.PSIsContainer) { $pending.Enqueue($child.FullName) }
            else { $items.Add($child) }
        }
    }
    return @($items)
}

function Assert-ExactPropertyNames {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actual = @($Object.PSObject.Properties.Name)
    $unexpected = @($actual | Where-Object { $Expected -cnotcontains $_ })
    $missing = @($Expected | Where-Object { $actual -cnotcontains $_ })
    if ($unexpected.Count -gt 0 -or $missing.Count -gt 0 -or
        $actual.Count -ne $Expected.Count) {
        throw "$Label does not match the exact supported schema."
    }
}

function Assert-BackupTreeMatchesManifest {
    param(
        [Parameter(Mandatory = $true)][string]$BackupDirectory,
        [Parameter(Mandatory = $true)]$BackupManifest,
        [Parameter(Mandatory = $true)][string]$OperatorSid
    )

    $backupPrefix = ([IO.Path]::GetFullPath($BackupDirectory)).TrimEnd('\') + '\'
    $actualFiles = @{}
    foreach ($item in @(Get-SafeTreeItems -Root $BackupDirectory)) {
        Assert-PrivateAcl `
            -Path $item.FullName `
            -OperatorSid $OperatorSid `
            -Label "Maintenance backup item"
        if ($item.PSIsContainer -or
            $item.Name -ceq "backup-manifest.json" -or
            $item.Name -ceq "deployment-result.json") {
            continue
        }
        $relativePath = $item.FullName.Substring($backupPrefix.Length).Replace('\', '/')
        $key = $relativePath.ToLowerInvariant()
        if ($actualFiles.ContainsKey($key)) {
            throw "Maintenance backup contains a case-insensitive duplicate path."
        }
        $actualFiles[$key] = $item
    }

    $expectedFiles = @($BackupManifest.files)
    if ($expectedFiles.Count -eq 0 -or $actualFiles.Count -ne $expectedFiles.Count) {
        throw "Maintenance backup file count does not match its manifest."
    }
    $seen = @{}
    foreach ($expected in $expectedFiles) {
        Assert-ExactPropertyNames `
            -Object $expected `
            -Expected @("Path", "Length", "Sha256") `
            -Label "Backup file entry"
        $relativePath = [string]$expected.Path
        if ($relativePath -cnotmatch '^[^\\/:\x00-\x1f]+(?:/[^\\/:\x00-\x1f]+)*$' -or
            @($relativePath.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -gt 0) {
            throw "Backup manifest contains an unsafe relative path."
        }
        $key = $relativePath.ToLowerInvariant()
        if ($seen.ContainsKey($key) -or -not $actualFiles.ContainsKey($key)) {
            throw "Backup manifest contains a duplicate or missing path."
        }
        $seen[$key] = $true
        $expectedPath = [IO.Path]::GetFullPath(
            (Join-Path $BackupDirectory $relativePath.Replace('/', '\')))
        if (-not $expectedPath.StartsWith($backupPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not $expectedPath.Equals(
                $actualFiles[$key].FullName,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Backup manifest path escapes or aliases the maintenance backup."
        }
        $expectedLength = 0L
        if (-not [long]::TryParse(
                ([string]$expected.Length),
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$expectedLength) -or
            $expectedLength -lt 0 -or
            [string]$expected.Sha256 -cnotmatch '^[a-f0-9]{64}$') {
            throw "Backup manifest contains an invalid file identity."
        }
        $actual = $actualFiles[$key]
        if ($actual.Length -ne $expectedLength -or
            (Get-Sha256 -Path $actual.FullName) -cne [string]$expected.Sha256) {
            throw "Backup file identity does not match the manifest: $relativePath"
        }
    }
}

function Enter-RootMaintenanceMutex {
    param([Parameter(Mandatory = $true)][string]$Root)

    $bytes = [Text.Encoding]::UTF8.GetBytes(
        ([IO.Path]::GetFullPath($Root)).TrimEnd('\').ToLowerInvariant())
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $suffix = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '') }
    finally {
        $sha.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
    $mutex = [Threading.Mutex]::new($false, "Global\PalControlNativeMaintenance-$suffix")
    try { $acquired = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) {
        $mutex.Dispose()
        throw "Another maintenance operation is active for this PalServer root."
    }
    return $mutex
}

function Exit-RootMaintenanceMutex {
    param([AllowNull()][Threading.Mutex]$Mutex)

    if ($null -ne $Mutex) {
        $Mutex.ReleaseMutex()
        $Mutex.Dispose()
    }
}

function Assert-SavedStateMatchesBackup {
    param(
        [Parameter(Mandatory = $true)][string]$SavedRoot,
        [Parameter(Mandatory = $true)]$BackupManifest
    )

    $expectedFiles = @($BackupManifest.files | Where-Object {
        $_.path -like "Saved/*"
    })
    if ($BackupManifest.savedStateIncluded -ne $true -or $expectedFiles.Count -eq 0) {
        throw "Deployment evidence does not include a complete offline Pal\\Saved backup."
    }

    $actualFiles = @(Get-ChildItem -LiteralPath $SavedRoot -File -Recurse | ForEach-Object {
        [pscustomobject]@{
            Path = "Saved/" + $_.FullName.Substring($SavedRoot.Length + 1).Replace('\', '/')
            Length = $_.Length
            Sha256 = Get-Sha256 -Path $_.FullName
        }
    })
    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw "Pal\\Saved changed after the offline maintenance backup."
    }

    $actualByPath = @{}
    foreach ($actual in $actualFiles) {
        $key = $actual.Path.ToLowerInvariant()
        if ($actualByPath.ContainsKey($key)) {
            throw "Pal\\Saved contains a case-insensitive duplicate path."
        }
        $actualByPath[$key] = $actual
    }
    foreach ($expected in $expectedFiles) {
        $key = ([string]$expected.path).ToLowerInvariant()
        if (-not $actualByPath.ContainsKey($key)) {
            throw "Pal\\Saved backup is missing the current path: $($expected.path)"
        }
        $actual = $actualByPath[$key]
        if ($actual.Length -ne [long]$expected.length -or
            $actual.Sha256 -cne [string]$expected.sha256) {
            throw "Pal\\Saved changed after backup: $($expected.path)"
        }
    }
}

if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw "Native dependency lock is missing: $lockPath"
}
$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($lock.native.capabilityStatus -cne "read-only-candidate-unverified" -or
    $lock.native.writeCapabilities -ne $false -or
    $lock.native.modVersion -cne "0.3.0-dev.39-ro") {
    throw "The repository lock is not the reviewed dev39-ro read-only candidate."
}

$root = (Resolve-Path -LiteralPath $PalServerRoot).Path
$deploymentPath = (Resolve-Path -LiteralPath $DeploymentResultPath).Path
Assert-NoReparseAncestor -Path $root -Label "PalServer root"
Assert-NoReparseAncestor -Path $deploymentPath -Label "Deployment result"

$rootPrefix = $root.TrimEnd('\') + '\'
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if ($deploymentPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $deploymentPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "DeploymentResultPath must be outside both PalServer and the public repository."
}
if ($deploymentPath.StartsWith('\\')) {
    throw "DeploymentResultPath must be on a local filesystem."
}
if ((Split-Path -Leaf $deploymentPath) -cne "deployment-result.json") {
    throw "DeploymentResultPath must identify the canonical deployment-result.json file."
}

$operatorSid = Get-CurrentOperatorSid
$deploymentDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $deploymentPath))
Assert-PrivateAcl `
    -Path $deploymentDirectory `
    -OperatorSid $operatorSid `
    -Label "Deployment operation directory"
Assert-PrivateAcl `
    -Path $deploymentPath `
    -OperatorSid $operatorSid `
    -Label "Deployment result"
$deployment = Get-Content -LiteralPath $deploymentPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-ExactPropertyNames `
    -Object $deployment `
    -Expected @(
        "schemaVersion", "operationId", "deployedAtUtc", "deployed", "readOnly",
        "nativeVersion", "nativeSha256", "ue4ssRuntimeSha256", "ue4ssProxySha256",
        "palDefenderLoaderQuarantined", "legacyWorkshopPackagesQuarantined",
        "palServerRoot", "backupRoot", "backupDirectory", "operatorSid", "aclPolicy",
        "backupManifestSha256", "serverStarted") `
    -Label "Deployment result"
if ($deployment.schemaVersion -ne 2 -or
    [string]$deployment.operationId -cnotmatch '^\d{8}-\d{6}-[a-f0-9]{32}$' -or
    $deployment.deployed -ne $true -or
    $deployment.readOnly -ne $true -or
    $deployment.nativeVersion -cne [string]$lock.native.modVersion -or
    $deployment.nativeSha256 -cne [string]$lock.build.palControlNativeDllSha256 -or
    $deployment.ue4ssRuntimeSha256 -cne [string]$lock.ue4ss.runtimeDllSha256 -or
    $deployment.ue4ssProxySha256 -cne [string]$lock.ue4ss.proxyDllSha256 -or
    $deployment.palDefenderLoaderQuarantined -ne $true -or
    [int]$deployment.legacyWorkshopPackagesQuarantined -lt 0 -or
    [int]$deployment.legacyWorkshopPackagesQuarantined -gt 3 -or
    [string]$deployment.operatorSid -cne $operatorSid -or
    [string]$deployment.aclPolicy -cne
        "system-administrators-operator-full-control-protected" -or
    [string]$deployment.backupManifestSha256 -cnotmatch '^[a-f0-9]{64}$' -or
    $deployment.serverStarted -ne $false) {
    throw "Deployment result does not describe the exact stopped dev39-ro read-only layout."
}

$boundRoot = [IO.Path]::GetFullPath([string]$deployment.palServerRoot)
$backupDirectory = [IO.Path]::GetFullPath([string]$deployment.backupDirectory)
$backupRootPath = [IO.Path]::GetFullPath([string]$deployment.backupRoot)
if (-not $boundRoot.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
    -not $backupDirectory.Equals(
        $deploymentDirectory,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not (Split-Path -Leaf $backupDirectory).Equals(
        [string]$deployment.operationId,
        [StringComparison]::Ordinal) -or
    -not ([IO.Path]::GetFullPath((Split-Path -Parent $backupDirectory))).Equals(
        $backupRootPath,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Deployment result is not bound to this root, operation, or backup directory."
}

$backupManifestPath = Join-Path $backupDirectory "backup-manifest.json"
if (-not (Test-Path -LiteralPath $backupManifestPath -PathType Leaf)) {
    throw "Deployment backup manifest is missing: $backupManifestPath"
}
Assert-NoReparseAncestor -Path $backupManifestPath -Label "Deployment backup manifest"
Assert-PrivateAcl `
    -Path $backupManifestPath `
    -OperatorSid $operatorSid `
    -Label "Deployment backup manifest"
$backupManifestSha256 = Get-Sha256 -Path $backupManifestPath
if ($backupManifestSha256 -cne [string]$deployment.backupManifestSha256) {
    throw "Deployment backup manifest hash does not match deployment-result.json."
}
$backupManifest = Get-Content -LiteralPath $backupManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-ExactPropertyNames `
    -Object $backupManifest `
    -Expected @(
        "schemaVersion", "operationId", "createdAtUtc", "purpose", "palworldTarget",
        "steamBuild", "palServerRoot", "backupRoot", "backupDirectory", "operatorSid",
        "aclPolicy", "previousNative", "savedStateIncluded", "files") `
    -Label "Backup manifest"
if ($backupManifest.schemaVersion -ne 2 -or
    [string]$backupManifest.operationId -cne [string]$deployment.operationId -or
    [string]$backupManifest.purpose -cne
        "dev39-ro-readonly-native-maintenance-backup" -or
    [string]$backupManifest.palworldTarget -cne [string]$lock.palworldTarget -or
    [string]$backupManifest.steamBuild -cne [string]$lock.steamBuild -or
    [string]$backupManifest.operatorSid -cne $operatorSid -or
    [string]$backupManifest.aclPolicy -cne
        "system-administrators-operator-full-control-protected" -or
    $backupManifest.savedStateIncluded -ne $true -or
    -not ([IO.Path]::GetFullPath([string]$backupManifest.palServerRoot)).Equals(
        $root,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFullPath([string]$backupManifest.backupRoot)).Equals(
        $backupRootPath,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFullPath([string]$backupManifest.backupDirectory)).Equals(
        $backupDirectory,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Backup manifest is not bound to the exact deployment operation and root."
}
Assert-BackupTreeMatchesManifest `
    -BackupDirectory $backupDirectory `
    -BackupManifest $backupManifest `
    -OperatorSid $operatorSid

$win64 = Join-Path $root "Pal\Binaries\Win64"
$launcher = Join-Path $root "PalServer.exe"
$shippingExecutable = Join-Path $win64 "PalServer-Win64-Shipping-Cmd.exe"
$runtimeDll = Join-Path $win64 "ue4ss\UE4SS.dll"
$proxyDll = Join-Path $win64 "dwmapi.dll"
$palDefenderLoader = Join-Path $win64 "d3d9.dll"
$targetDll = Join-Path $win64 "ue4ss\Mods\PalControlNative\dlls\main.dll"
$modsFile = Join-Path $win64 "ue4ss\Mods\mods.txt"
$savedRoot = Join-Path $root "Pal\Saved"
$legacyWorkshopPaths = @(
    (Join-Path $root "Mods\Workshop\PalControlNative"),
    (Join-Path $root "Mods\Workshop\PalControlUE4SSRuntime"),
    (Join-Path $root "Mods\ManagedMods\PalControlNative")
)

Assert-FileIdentity `
    -Path $shippingExecutable `
    -ExpectedLength ([long]$lock.palServerExecutable.size) `
    -ExpectedSha256 ([string]$lock.palServerExecutable.sha256) `
    -Label "PalServer shipping executable"
Assert-FileIdentity `
    -Path $runtimeDll `
    -ExpectedLength ([long]$lock.ue4ss.runtimeDllSize) `
    -ExpectedSha256 ([string]$lock.ue4ss.runtimeDllSha256) `
    -Label "Installed UE4SS runtime"
Assert-FileIdentity `
    -Path $proxyDll `
    -ExpectedLength ([long]$lock.ue4ss.proxyDllSize) `
    -ExpectedSha256 ([string]$lock.ue4ss.proxyDllSha256) `
    -Label "Installed UE4SS proxy"
Assert-FileIdentity `
    -Path $targetDll `
    -ExpectedLength ([long]$lock.build.palControlNativeDllSize) `
    -ExpectedSha256 ([string]$lock.build.palControlNativeDllSha256) `
    -Label "Installed PalControl Native candidate"

if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "PalServer launcher is missing: $launcher"
}
if (Test-Path -LiteralPath $palDefenderLoader -PathType Leaf) {
    throw "PalDefender d3d9 loader must remain quarantined during the Native-only probe."
}
foreach ($legacyPath in $legacyWorkshopPaths) {
    if (Test-Path -LiteralPath $legacyPath) {
        throw "Legacy PalControl Workshop package remains under the active server root: $legacyPath"
    }
}

$activeNativeLines = @([IO.File]::ReadAllLines($modsFile) | Where-Object {
    $_ -match '^\s*PalControlNative\s*:\s*1\s*$'
})
if ($activeNativeLines.Count -ne 1) {
    throw "mods.txt must activate PalControlNative exactly once."
}
Assert-SavedStateMatchesBackup -SavedRoot $savedRoot -BackupManifest $backupManifest

$runningServers = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @(
        "PalServer",
        "PalServer-Win64-Shipping-Cmd",
        "PalServer-Win64-Shipping")
})
if ($runningServers.Count -gt 0) {
    throw "A PalServer process is already running; this script will not attach to it."
}

if ($ServerArguments.Count -ne 1 -or $ServerArguments[0] -cne "-log") {
    throw "ServerArguments are locked to the single reviewed -log argument."
}

$plan = [pscustomobject]@{
    Execute = [bool]$Execute
    PalServerRoot = $root
    PalworldTarget = [string]$lock.palworldTarget
    SteamBuild = [string]$lock.steamBuild
    NativeVersion = [string]$lock.native.modVersion
    ReadOnly = $true
    PalDefenderLoaderQuarantined = $true
    LegacyWorkshopPackagesQuarantined = $true
    SavedStateBackupVerified = $true
    PublicLobbyRequested = $false
    ServerArguments = @($ServerArguments)
    NetworkExposureAcknowledged = [bool]$NetworkExposureAcknowledged
}

if (-not $Execute) {
    [pscustomobject]@{
        Started = $false
        PlanOnly = $true
        Plan = $plan
        NextStep = "Review local firewall/NAT exposure, then rerun with -Execute and -NetworkExposureAcknowledged."
    }
    return
}
if (-not $NetworkExposureAcknowledged) {
    throw "Starting PalServer binds game/query ports. Pass -NetworkExposureAcknowledged after reviewing firewall/NAT exposure."
}

$maintenanceMutex = Enter-RootMaintenanceMutex -Root $root
try {
    $runningServers = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in @(
            "PalServer",
            "PalServer-Win64-Shipping-Cmd",
            "PalServer-Win64-Shipping")
    })
    if ($runningServers.Count -gt 0) {
        throw "A PalServer process appeared before startup; refusing a second session."
    }

    $launcherProcess = Start-Process `
        -FilePath $launcher `
        -ArgumentList $ServerArguments `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -PassThru
$launcherStartTimeUtc = $launcherProcess.StartTime.ToUniversalTime()

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
$shippingProcess = $null
$shippingCimProcess = $null
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    $candidates = @(Get-Process -Name "PalServer-Win64-Shipping-Cmd" -ErrorAction SilentlyContinue)
    foreach ($candidateProcess in $candidates) {
        try {
            $candidatePath = [IO.Path]::GetFullPath($candidateProcess.Path)
        }
        catch {
            continue
        }
        $candidateCim = Get-CimInstance `
            Win32_Process `
            -Filter "ProcessId = $($candidateProcess.Id)"
        if ($null -eq $candidateCim -or
            [int]$candidateCim.ParentProcessId -ne $launcherProcess.Id -or
            $candidateProcess.StartTime.ToUniversalTime() -lt $launcherStartTimeUtc) {
            continue
        }
        if ($candidatePath.Equals(
                [IO.Path]::GetFullPath($shippingExecutable),
                [StringComparison]::OrdinalIgnoreCase)) {
            $shippingProcess = $candidateProcess
            $shippingCimProcess = $candidateCim
            break
        }
    }
    if ($null -ne $shippingProcess) { break }
    Start-Sleep -Milliseconds 500
}
if ($null -eq $shippingProcess) {
    throw (
        "PalServer launcher started as PID $($launcherProcess.Id), but the exact locked " +
        "shipping process was not observed within $StartupTimeoutSeconds seconds."
    )
}

$owner = Invoke-CimMethod -InputObject $shippingCimProcess -MethodName GetOwnerSid
if ($owner.ReturnValue -ne 0 -or [string]::IsNullOrWhiteSpace($owner.Sid)) {
    throw "PalServer started, but its process SID could not be obtained for the live probe."
}
$canonicalSid = ([Security.Principal.SecurityIdentifier]::new($owner.Sid)).Value
$shippingProcessCreationTimeUtcFileTime = `
    $shippingProcess.StartTime.ToUniversalTime().ToFileTimeUtc()

[pscustomobject]@{
    Started = $true
    ReadOnlyNative = $true
    LauncherProcessId = $launcherProcess.Id
    ShippingProcessId = $shippingProcess.Id
    ShippingParentProcessId = [int]$shippingCimProcess.ParentProcessId
    ShippingExecutablePath = [IO.Path]::GetFullPath($shippingProcess.Path)
    ShippingProcessCreationTimeUtcFileTime = $shippingProcessCreationTimeUtcFileTime
    LauncherStartTimeUtc = $launcherStartTimeUtc.ToString("O")
    ShippingStartTimeUtc = $shippingProcess.StartTime.ToUniversalTime().ToString("O")
    ShippingProcessSid = $canonicalSid
    NativeVersion = [string]$lock.native.modVersion
    NativeSha256 = Get-Sha256 -Path $targetDll
    PalDefenderLoaderQuarantined = -not (Test-Path -LiteralPath $palDefenderLoader)
    PublicLobby = $false
    NextStep = "Run tools/native-bridge-probe.ps1 with this exact executable path and process SID."
}
}
finally {
    Exit-RootMaintenanceMutex -Mutex $maintenanceMutex
}

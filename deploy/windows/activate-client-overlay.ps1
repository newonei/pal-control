[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PalServerRoot,

    [Parameter(Mandatory = $true)]
    [string]$CandidateDllPath,

    [Parameter(Mandatory = $true)]
    [string]$Ue4ssReleaseArchivePath,

    [string]$BackupRoot = "C:\ProgramData\PalControl\backups\native-overlay",

    [switch]$IncludeSavedStateBackup,

    [switch]$QuarantineLegacyWorkshopPackages,

    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$lockPath = Join-Path $repositoryRoot "mods\pal-control-native\dependencies.lock.json"
$configSource = Join-Path $repositoryRoot "mods\pal-control-native\Config\PalControl.ini"

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
        throw (
            "$Label identity mismatch. Expected $ExpectedLength bytes/$ExpectedSha256; " +
            "actual $($file.Length) bytes/$sha256."
        )
    }
}

function Assert-NoReparseAncestor {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $cursor = $fullPath
    while (-not (Test-Path -LiteralPath $cursor)) {
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) {
            throw "$Label has no existing filesystem ancestor: $fullPath"
        }
        $cursor = $parent.FullName
    }

    while ($true) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a reparse-point ancestor: $cursor"
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) {
            break
        }
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

    return @(
        "S-1-5-18"      # NT AUTHORITY\SYSTEM
        "S-1-5-32-544"  # BUILTIN\Administrators
        $OperatorSid
    ) | Select-Object -Unique
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
    $rules = @($acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    foreach ($rule in $rules) {
        $sidValue = $rule.IdentityReference.Value
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            -not $allowed.Contains($sidValue)) {
            throw "$Label grants an unapproved filesystem rule: $Path"
        }
        if (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
            [Security.AccessControl.FileSystemRights]::FullControl) {
            throw "$Label does not grant the approved SID full control: $Path"
        }
        [void]$present.Add($sidValue)
    }
    foreach ($sidValue in $allowed) {
        if (-not $present.Contains($sidValue)) {
            throw "$Label is missing a required private ACL principal: $Path"
        }
    }
}

function Set-PrivateAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OperatorSid
    )

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to secure a reparse point: $Path"
    }

    if ($item.PSIsContainer) {
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $inheritance = (
            [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit)
    }
    else {
        $security = [Security.AccessControl.FileSecurity]::new()
        $inheritance = [Security.AccessControl.InheritanceFlags]::None
    }
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner([Security.Principal.SecurityIdentifier]::new($OperatorSid))
    foreach ($sidValue in (Get-PrivateAclSidValues -OperatorSid $OperatorSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sidValue),
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
    Assert-PrivateAcl -Path $Path -OperatorSid $OperatorSid -Label "Private maintenance item"
}

function Get-SafeTreeItems {
    param([Parameter(Mandatory = $true)][string]$Root)

    $items = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue([IO.Path]::GetFullPath($Root))
    while ($pending.Count -gt 0) {
        $currentPath = $pending.Dequeue()
        $current = Get-Item -LiteralPath $currentPath -Force
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Private maintenance tree contains a reparse point: $currentPath"
        }
        $items.Add($current)
        if (-not $current.PSIsContainer) {
            continue
        }
        foreach ($child in @(Get-ChildItem -LiteralPath $current.FullName -Force)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Private maintenance tree contains a reparse point: $($child.FullName)"
            }
            if ($child.PSIsContainer) {
                $pending.Enqueue($child.FullName)
            }
            else {
                $items.Add($child)
            }
        }
    }
    return @($items)
}

function Protect-PrivateTree {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$OperatorSid
    )

    foreach ($item in @(Get-SafeTreeItems -Root $Root)) {
        Set-PrivateAcl -Path $item.FullName -OperatorSid $OperatorSid
    }
    foreach ($item in @(Get-SafeTreeItems -Root $Root)) {
        Assert-PrivateAcl `
            -Path $item.FullName `
            -OperatorSid $OperatorSid `
            -Label "Private maintenance tree item"
    }
}

function Enter-RootMaintenanceMutex {
    param([Parameter(Mandatory = $true)][string]$Root)

    $bytes = [Text.Encoding]::UTF8.GetBytes(
        ([IO.Path]::GetFullPath($Root)).TrimEnd('\').ToLowerInvariant())
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $suffix = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '')
    }
    finally {
        $sha.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
    $mutex = [Threading.Mutex]::new(
        $false,
        "Global\PalControlNativeMaintenance-$suffix")
    try {
        $acquired = $mutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $acquired = $true
    }
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

function Get-ServerRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootPrefix = ([IO.Path]::GetFullPath($Root)).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Maintenance source is outside the requested PalServer root: $fullPath"
    }
    return $fullPath.Substring($rootPrefix.Length)
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $encoding = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Get-ZipEntryIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $matches = @($archive.Entries | Where-Object { $_.FullName -ceq $EntryName })
        if ($matches.Count -ne 1) {
            throw "Locked UE4SS archive must contain exactly one $EntryName entry."
        }

        $entry = $matches[0]
        $stream = $entry.Open()
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $hashBytes = $sha.ComputeHash($stream)
            }
            finally {
                $sha.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }

        return [pscustomobject]@{
            Name = $entry.FullName
            Length = [long]$entry.Length
            Sha256 = ([BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Expand-LockedZipEntry {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $matches = @($archive.Entries | Where-Object { $_.FullName -ceq $EntryName })
        if ($matches.Count -ne 1) {
            throw "Locked UE4SS archive must contain exactly one $EntryName entry."
        }

        $input = $matches[0].Open()
        try {
            $output = New-Object IO.FileStream(
                $DestinationPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $input.CopyTo($output)
                $output.Flush($true)
            }
            finally {
                $output.Dispose()
            }
        }
        finally {
            $input.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Copy-BackupFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent | Out-Null
    }
    if (Test-Path -LiteralPath $Destination) {
        throw "Backup destination already exists: $Destination"
    }
    Assert-NoReparseAncestor -Path $Source -Label "Backup source"
    Assert-NoReparseAncestor -Path $Destination -Label "Backup destination"
    Copy-Item -LiteralPath $Source -Destination $Destination
}

if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw "Native dependency lock is missing: $lockPath"
}
if (-not (Test-Path -LiteralPath $configSource -PathType Leaf)) {
    throw "Read-only Native policy config is missing: $configSource"
}

$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($lock.native.capabilityStatus -cne "read-only-candidate-unverified" -or
    $lock.native.writeCapabilities -ne $false -or
    $lock.native.modVersion -cne "0.3.0-dev.39-ro") {
    throw "The repository lock is not an unverified read-only dev39-ro candidate."
}

$runningServers = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @(
        "PalServer",
        "PalServer-Win64-Shipping-Cmd",
        "PalServer-Win64-Shipping")
})
if ($runningServers.Count -gt 0) {
    throw (
        "PalServer is running. Use the normal save/shutdown flow before deployment; " +
        "this script never stops players or a server process."
    )
}

$root = (Resolve-Path -LiteralPath $PalServerRoot).Path
$candidate = (Resolve-Path -LiteralPath $CandidateDllPath).Path
$archivePath = (Resolve-Path -LiteralPath $Ue4ssReleaseArchivePath).Path
$backupRootPath = [IO.Path]::GetFullPath($BackupRoot)

foreach ($entry in @(
    @{ Path = $root; Label = "PalServer root" },
    @{ Path = $candidate; Label = "Native candidate" },
    @{ Path = $archivePath; Label = "UE4SS release archive" },
    @{ Path = $backupRootPath; Label = "Native backup root" }
)) {
    Assert-NoReparseAncestor -Path $entry.Path -Label $entry.Label
}

$rootPrefix = $root.TrimEnd('\') + '\'
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if ($backupRootPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "BackupRoot must be outside the PalServer installation."
}
if ($backupRootPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "BackupRoot must be outside the public Git repository."
}
if ($backupRootPath.StartsWith('\\')) {
    throw "BackupRoot must be on a local filesystem, not UNC storage."
}

$win64 = Join-Path $root "Pal\Binaries\Win64"
$shippingExecutable = Join-Path $win64 "PalServer-Win64-Shipping-Cmd.exe"
$steamManifest = Join-Path $root "steamapps\appmanifest_2394010.acf"
$runtimeDll = Join-Path $win64 "ue4ss\UE4SS.dll"
$proxyDll = Join-Path $win64 "dwmapi.dll"
$palDefenderLoader = Join-Path $win64 "d3d9.dll"
$modRoot = Join-Path $win64 "ue4ss\Mods\PalControlNative"
$targetDll = Join-Path $modRoot "dlls\main.dll"
$targetConfig = Join-Path $modRoot "PalControl.ini"
$modsFile = Join-Path $win64 "ue4ss\Mods\mods.txt"
$savedRoot = Join-Path $root "Pal\Saved"
$legacyWorkshopPaths = @(
    (Join-Path $root "Mods\Workshop\PalControlNative"),
    (Join-Path $root "Mods\Workshop\PalControlUE4SSRuntime"),
    (Join-Path $root "Mods\ManagedMods\PalControlNative")
)
$presentLegacyWorkshopPaths = @($legacyWorkshopPaths | Where-Object {
    Test-Path -LiteralPath $_ -PathType Container
})

if (Test-Path -LiteralPath $palDefenderLoader -PathType Leaf) {
    throw (
        "The unreviewed PalDefender d3d9 loader is active for the current game build. " +
        "Keep it quarantined during the dev39-ro Native probe: $palDefenderLoader"
    )
}

Assert-FileIdentity `
    -Path $shippingExecutable `
    -ExpectedLength ([long]$lock.palServerExecutable.size) `
    -ExpectedSha256 ([string]$lock.palServerExecutable.sha256) `
    -Label "PalServer executable"
Assert-FileIdentity `
    -Path $runtimeDll `
    -ExpectedLength ([long]$lock.ue4ss.runtimeDllSize) `
    -ExpectedSha256 ([string]$lock.ue4ss.runtimeDllSha256) `
    -Label "Installed UE4SS runtime"
Assert-FileIdentity `
    -Path $candidate `
    -ExpectedLength ([long]$lock.build.palControlNativeDllSize) `
    -ExpectedSha256 ([string]$lock.build.palControlNativeDllSha256) `
    -Label "PalControl Native candidate"

if (-not (Test-Path -LiteralPath $steamManifest -PathType Leaf)) {
    throw "Steam app manifest is missing: $steamManifest"
}
$manifestText = Get-Content -LiteralPath $steamManifest -Raw
$buildMatch = [regex]::Match($manifestText, '"buildid"\s+"(?<id>\d+)"')
if (-not $buildMatch.Success -or $buildMatch.Groups['id'].Value -cne [string]$lock.steamBuild) {
    throw "Steam build does not match the Native dependency lock."
}

$archiveSha256 = Get-Sha256 -Path $archivePath
if ($archiveSha256 -cne ([string]$lock.ue4ss.releaseArchiveSha256).ToLowerInvariant()) {
    throw "UE4SS release archive hash does not match dependencies.lock.json."
}
$runtimeEntry = Get-ZipEntryIdentity -ArchivePath $archivePath -EntryName "ue4ss/UE4SS.dll"
$proxyEntry = Get-ZipEntryIdentity -ArchivePath $archivePath -EntryName "dwmapi.dll"
if ($runtimeEntry.Length -ne [long]$lock.ue4ss.runtimeDllSize -or
    $runtimeEntry.Sha256 -cne [string]$lock.ue4ss.runtimeDllSha256) {
    throw "The locked archive does not contain the locked UE4SS runtime."
}
if ($proxyEntry.Length -ne [long]$lock.ue4ss.proxyDllSize -or
    $proxyEntry.Sha256 -cne [string]$lock.ue4ss.proxyDllSha256) {
    throw "The locked archive does not contain the locked UE4SS proxy."
}

$previousNative = $null
if (Test-Path -LiteralPath $targetDll -PathType Leaf) {
    $previousFile = Get-Item -LiteralPath $targetDll
    $previousNative = [pscustomobject]@{
        Length = $previousFile.Length
        Sha256 = Get-Sha256 -Path $targetDll
    }
}

$plan = [pscustomobject]@{
    Execute = [bool]$Execute
    PalServerRoot = $root
    PalworldTarget = [string]$lock.palworldTarget
    SteamBuild = [string]$lock.steamBuild
    NativeVersion = [string]$lock.native.modVersion
    NativeSha256 = [string]$lock.build.palControlNativeDllSha256
    Ue4ssRuntimeSha256 = [string]$lock.ue4ss.runtimeDllSha256
    Ue4ssProxySha256 = [string]$lock.ue4ss.proxyDllSha256
    PreviousNative = $previousNative
    PalDefenderLoaderQuarantined = -not (Test-Path -LiteralPath $palDefenderLoader)
    LegacyWorkshopPackages = @($presentLegacyWorkshopPaths)
    QuarantineLegacyWorkshopPackages = [bool]$QuarantineLegacyWorkshopPackages
    IncludeSavedStateBackup = [bool]$IncludeSavedStateBackup
    BackupRoot = $backupRootPath
}

if (-not $Execute) {
    [pscustomobject]@{
        Deployed = $false
        PlanOnly = $true
        Plan = $plan
        NextStep = "Review the plan, enter a maintenance window, then rerun with -Execute."
    }
    return
}

if ($presentLegacyWorkshopPaths.Count -gt 0 -and -not $QuarantineLegacyWorkshopPackages) {
    throw (
        "Legacy PalControl Workshop packages remain under the active PalServer root. " +
        "Review the plan and rerun with -QuarantineLegacyWorkshopPackages so they are " +
        "moved into the external maintenance backup before startup."
    )
}

$operatorSid = Get-CurrentOperatorSid
$maintenanceMutex = Enter-RootMaintenanceMutex -Root $root
try {
    if (-not (Test-Path -LiteralPath $backupRootPath)) {
        New-Item -ItemType Directory -Path $backupRootPath | Out-Null
    }
    Assert-NoReparseAncestor -Path $backupRootPath -Label "Native backup root"
    Set-PrivateAcl -Path $backupRootPath -OperatorSid $operatorSid

    $operationId = (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + [Guid]::NewGuid().ToString("N")
    $backupDirectory = Join-Path $backupRootPath $operationId
    if (Test-Path -LiteralPath $backupDirectory) {
        throw "Generated maintenance operation already exists: $backupDirectory"
    }
    New-Item -ItemType Directory -Path $backupDirectory | Out-Null
    Assert-NoReparseAncestor -Path $backupDirectory -Label "Native backup operation"
    Set-PrivateAcl -Path $backupDirectory -OperatorSid $operatorSid

$targetExisted = Test-Path -LiteralPath $targetDll -PathType Leaf
$proxyExisted = Test-Path -LiteralPath $proxyDll -PathType Leaf
$modsExisted = Test-Path -LiteralPath $modsFile -PathType Leaf
$configExisted = Test-Path -LiteralPath $targetConfig -PathType Leaf

$nativeBackup = Join-Path $backupDirectory "overlay\main.dll"
$proxyBackup = Join-Path $backupDirectory "overlay\dwmapi.dll"
$modsBackup = Join-Path $backupDirectory "overlay\mods.txt"
$configBackup = Join-Path $backupDirectory "overlay\PalControl.ini"

if ($targetExisted) { Copy-BackupFile -Source $targetDll -Destination $nativeBackup }
if ($proxyExisted) { Copy-BackupFile -Source $proxyDll -Destination $proxyBackup }
if ($modsExisted) { Copy-BackupFile -Source $modsFile -Destination $modsBackup }
if ($configExisted) { Copy-BackupFile -Source $targetConfig -Destination $configBackup }

    if ($IncludeSavedStateBackup) {
        if (-not (Test-Path -LiteralPath $savedRoot -PathType Container)) {
            throw "Saved-state backup was requested but Pal\\Saved is missing."
        }
        $savedBackup = Join-Path $backupDirectory "Saved"
        if (Test-Path -LiteralPath $savedBackup) {
            throw "Saved-state backup destination already exists: $savedBackup"
        }
        Assert-NoReparseAncestor -Path $savedRoot -Label "Saved-state source"
        [void](Get-SafeTreeItems -Root $savedRoot)
        Copy-Item -LiteralPath $savedRoot -Destination $savedBackup -Recurse
    }

    $legacyWorkshopBackupRoot = Join-Path $backupDirectory "legacy-workshop-copy"
    foreach ($legacyPath in $presentLegacyWorkshopPaths) {
        $legacyRelativePath = Get-ServerRelativePath -Root $root -Path $legacyPath
        $legacyDestination = Join-Path $legacyWorkshopBackupRoot $legacyRelativePath
        if (Test-Path -LiteralPath $legacyDestination) {
            throw "Legacy backup destination already exists: $legacyDestination"
        }
        $legacyParent = Split-Path -Parent $legacyDestination
        if (-not (Test-Path -LiteralPath $legacyParent)) {
            New-Item -ItemType Directory -Path $legacyParent | Out-Null
        }
        Assert-NoReparseAncestor -Path $legacyPath -Label "Legacy package source"
        [void](Get-SafeTreeItems -Root $legacyPath)
        Copy-Item -LiteralPath $legacyPath -Destination $legacyDestination -Recurse
    }

    Protect-PrivateTree -Root $backupDirectory -OperatorSid $operatorSid

$backupFiles = @(Get-ChildItem -LiteralPath $backupDirectory -File -Recurse | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName.Substring($backupDirectory.Length + 1).Replace('\', '/')
        Length = $_.Length
        Sha256 = Get-Sha256 -Path $_.FullName
    }
})
$backupManifestPath = Join-Path $backupDirectory "backup-manifest.json"
$backupManifest = [ordered]@{
    schemaVersion = 2
    operationId = $operationId
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    purpose = "dev39-ro-readonly-native-maintenance-backup"
    palworldTarget = [string]$lock.palworldTarget
    steamBuild = [string]$lock.steamBuild
    palServerRoot = $root
    backupRoot = $backupRootPath
    backupDirectory = $backupDirectory
    operatorSid = $operatorSid
    aclPolicy = "system-administrators-operator-full-control-protected"
    previousNative = $previousNative
    savedStateIncluded = [bool]$IncludeSavedStateBackup
    files = $backupFiles
}
Write-Utf8NoBom `
    -Path $backupManifestPath `
    -Content ($backupManifest | ConvertTo-Json -Depth 8)
Set-PrivateAcl -Path $backupManifestPath -OperatorSid $operatorSid

$temporarySuffix = ".palcontrol-new-$operationId"
$temporaryNative = $targetDll + $temporarySuffix
$temporaryProxy = $proxyDll + $temporarySuffix
$temporaryMods = $modsFile + $temporarySuffix
$temporaryConfig = $targetConfig + $temporarySuffix
$movedLegacyWorkshopPackages = New-Object System.Collections.Generic.List[object]

try {
    foreach ($directory in @((Split-Path -Parent $targetDll), (Split-Path -Parent $modsFile))) {
        if (-not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory | Out-Null
        }
    }

    Copy-Item -LiteralPath $candidate -Destination $temporaryNative
    Assert-FileIdentity `
        -Path $temporaryNative `
        -ExpectedLength ([long]$lock.build.palControlNativeDllSize) `
        -ExpectedSha256 ([string]$lock.build.palControlNativeDllSha256) `
        -Label "Staged PalControl Native candidate"

    Expand-LockedZipEntry `
        -ArchivePath $archivePath `
        -EntryName "dwmapi.dll" `
        -DestinationPath $temporaryProxy
    Assert-FileIdentity `
        -Path $temporaryProxy `
        -ExpectedLength ([long]$lock.ue4ss.proxyDllSize) `
        -ExpectedSha256 ([string]$lock.ue4ss.proxyDllSha256) `
        -Label "Staged UE4SS proxy"

    $existingModLines = @()
    if ($modsExisted) {
        $existingModLines = @([IO.File]::ReadAllLines($modsFile) | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            $_ -notmatch '^\s*PalControlNative\s*:'
        })
    }
    $newModLines = @($existingModLines + "PalControlNative : 1")
    Write-Utf8NoBom -Path $temporaryMods -Content (($newModLines -join "`r`n") + "`r`n")
    Copy-Item -LiteralPath $configSource -Destination $temporaryConfig

    $legacyWorkshopQuarantineRoot = Join-Path $backupDirectory "legacy-workshop-quarantined"
    foreach ($legacyPath in $presentLegacyWorkshopPaths) {
        $legacyRelativePath = Get-ServerRelativePath -Root $root -Path $legacyPath
        $legacyDestination = Join-Path $legacyWorkshopQuarantineRoot $legacyRelativePath
        if (Test-Path -LiteralPath $legacyDestination) {
            throw "Legacy quarantine destination already exists: $legacyDestination"
        }
        $legacyParent = Split-Path -Parent $legacyDestination
        if (-not (Test-Path -LiteralPath $legacyParent)) {
            New-Item -ItemType Directory -Path $legacyParent | Out-Null
        }
        Move-Item -LiteralPath $legacyPath -Destination $legacyDestination
        $movedLegacyWorkshopPackages.Add([pscustomobject]@{
            Source = $legacyPath
            Destination = $legacyDestination
        })
    }

    Move-Item -LiteralPath $temporaryNative -Destination $targetDll -Force
    Move-Item -LiteralPath $temporaryProxy -Destination $proxyDll -Force
    Move-Item -LiteralPath $temporaryMods -Destination $modsFile -Force
    Move-Item -LiteralPath $temporaryConfig -Destination $targetConfig -Force

    Assert-FileIdentity `
        -Path $targetDll `
        -ExpectedLength ([long]$lock.build.palControlNativeDllSize) `
        -ExpectedSha256 ([string]$lock.build.palControlNativeDllSha256) `
        -Label "Installed PalControl Native candidate"
    Assert-FileIdentity `
        -Path $proxyDll `
        -ExpectedLength ([long]$lock.ue4ss.proxyDllSize) `
        -ExpectedSha256 ([string]$lock.ue4ss.proxyDllSha256) `
        -Label "Installed UE4SS proxy"

    $activeNativeLines = @([IO.File]::ReadAllLines($modsFile) | Where-Object {
        $_ -match '^\s*PalControlNative\s*:\s*1\s*$'
    })
    if ($activeNativeLines.Count -ne 1) {
        throw "mods.txt does not activate PalControlNative exactly once."
    }
    if (Test-Path -LiteralPath $palDefenderLoader -PathType Leaf) {
        throw "PalDefender d3d9 loader appeared during deployment; refusing the probe candidate."
    }
    foreach ($legacyPath in $legacyWorkshopPaths) {
        if (Test-Path -LiteralPath $legacyPath) {
            throw "A legacy PalControl Workshop package remains active after quarantine: $legacyPath"
        }
    }
}
catch {
    foreach ($temporaryFile in @($temporaryNative, $temporaryProxy, $temporaryMods, $temporaryConfig)) {
        if (Test-Path -LiteralPath $temporaryFile -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryFile -Force
        }
    }

    if ($targetExisted) { Copy-Item -LiteralPath $nativeBackup -Destination $targetDll -Force }
    elseif (Test-Path -LiteralPath $targetDll -PathType Leaf) { Remove-Item -LiteralPath $targetDll -Force }
    if ($proxyExisted) { Copy-Item -LiteralPath $proxyBackup -Destination $proxyDll -Force }
    elseif (Test-Path -LiteralPath $proxyDll -PathType Leaf) { Remove-Item -LiteralPath $proxyDll -Force }
    if ($modsExisted) { Copy-Item -LiteralPath $modsBackup -Destination $modsFile -Force }
    elseif (Test-Path -LiteralPath $modsFile -PathType Leaf) { Remove-Item -LiteralPath $modsFile -Force }
    if ($configExisted) { Copy-Item -LiteralPath $configBackup -Destination $targetConfig -Force }
    elseif (Test-Path -LiteralPath $targetConfig -PathType Leaf) { Remove-Item -LiteralPath $targetConfig -Force }

    foreach ($movedLegacy in @($movedLegacyWorkshopPackages)) {
        $legacyDestinationExists = Test-Path `
            -LiteralPath $movedLegacy.Destination `
            -PathType Container
        $legacySourceExists = Test-Path -LiteralPath $movedLegacy.Source
        if ($legacyDestinationExists -and -not $legacySourceExists) {
            Move-Item -LiteralPath $movedLegacy.Destination -Destination $movedLegacy.Source
        }
    }

    throw "Native deployment failed and the prior overlay was restored. Backup: $backupDirectory. $($_.Exception.Message)"
}

Protect-PrivateTree -Root $backupDirectory -OperatorSid $operatorSid
$finalBackupFiles = @(Get-SafeTreeItems -Root $backupDirectory | Where-Object {
    -not $_.PSIsContainer -and
    $_.FullName -cne $backupManifestPath -and
    $_.Name -cne "deployment-result.json"
} | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName.Substring($backupDirectory.Length + 1).Replace('\', '/')
        Length = $_.Length
        Sha256 = Get-Sha256 -Path $_.FullName
    }
})
$backupManifest.files = $finalBackupFiles
Write-Utf8NoBom `
    -Path $backupManifestPath `
    -Content ($backupManifest | ConvertTo-Json -Depth 8)
Set-PrivateAcl -Path $backupManifestPath -OperatorSid $operatorSid

$result = [ordered]@{
    schemaVersion = 2
    operationId = $operationId
    deployedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    deployed = $true
    readOnly = $true
    nativeVersion = [string]$lock.native.modVersion
    nativeSha256 = Get-Sha256 -Path $targetDll
    ue4ssRuntimeSha256 = Get-Sha256 -Path $runtimeDll
    ue4ssProxySha256 = Get-Sha256 -Path $proxyDll
    palDefenderLoaderQuarantined = -not (Test-Path -LiteralPath $palDefenderLoader)
    legacyWorkshopPackagesQuarantined = $presentLegacyWorkshopPaths.Count
    palServerRoot = $root
    backupRoot = $backupRootPath
    backupDirectory = $backupDirectory
    operatorSid = $operatorSid
    aclPolicy = "system-administrators-operator-full-control-protected"
    backupManifestSha256 = Get-Sha256 -Path $backupManifestPath
    serverStarted = $false
}
$resultPath = Join-Path $backupDirectory "deployment-result.json"
Write-Utf8NoBom -Path $resultPath -Content ($result | ConvertTo-Json -Depth 6)
Set-PrivateAcl -Path $resultPath -OperatorSid $operatorSid
Protect-PrivateTree -Root $backupDirectory -OperatorSid $operatorSid

[pscustomobject]@{
    Deployed = $true
    ReadOnly = $true
    NativeVersion = $result.nativeVersion
    NativeSha256 = $result.nativeSha256
    Ue4ssProxySha256 = $result.ue4ssProxySha256
    PalDefenderLoaderQuarantined = $result.palDefenderLoaderQuarantined
    BackupDirectory = $backupDirectory
    ResultPath = $resultPath
    NextStep = "Start the controlled PalServer maintenance session, then run native-bridge-probe.ps1."
}
}
finally {
    Exit-RootMaintenanceMutex -Mutex $maintenanceMutex
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([Parameter(Mandatory)] [string]$Path)
    [System.IO.Path]::GetFullPath($Path)
}

function Get-RelativePathPortable {
    param(
        [Parameter(Mandatory)] [string]$BasePath,
        [Parameter(Mandatory)] [string]$Path
    )
    $baseFull = (Resolve-FullPath $BasePath).TrimEnd('\') + '\'
    $pathFull = Resolve-FullPath $Path
    $baseUri = New-Object System.Uri($baseFull)
    $pathUri = New-Object System.Uri($pathFull)
    [System.Uri]::UnescapeDataString(
        $baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Assert-SafeDeploymentRoot {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Name
    )
    $full = Resolve-FullPath $Path
    $root = [System.IO.Path]::GetPathRoot($full)
    if ([string]::IsNullOrWhiteSpace($root) -or
        $full.TrimEnd('\').Equals($root.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name cannot be a drive root: $full"
    }
    if ($full.Length -lt ($root.Length + 6)) {
        throw "$Name is too broad for a deployment operation: $full"
    }
    return $full.TrimEnd('\')
}

function Assert-PathWithinRoot {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Root,
        [string]$Description = "path"
    )
    $full = Resolve-FullPath $Path
    $rootFull = (Resolve-FullPath $Root).TrimEnd('\') + '\'
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escapes its approved root: $full"
    }
    return $full
}

function Assert-NoReparsePoint {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [switch]$AllowMissingLeaf
    )
    $full = Resolve-FullPath $Path
    $cursor = $full
    $missingLeaf = $false
    while (-not (Test-Path -LiteralPath $cursor)) {
        $missingLeaf = $true
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    if ($missingLeaf -and -not $AllowMissingLeaf) {
        throw "Required path does not exist: $full"
    }
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Deployment paths cannot traverse a reparse point: $($item.FullName)"
            }
        }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    if (Test-Path -LiteralPath $full -PathType Container) {
        $reparse = Get-ChildItem -LiteralPath $full -Recurse -Force |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Select-Object -First 1
        if ($reparse) {
            throw "Deployment trees cannot contain a reparse point: $($reparse.FullName)"
        }
    }
    return $full
}

function Remove-SafeDeploymentTree {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ApprovedRoot
    )
    $full = Assert-PathWithinRoot -Path $Path -Root $ApprovedRoot -Description "cleanup path"
    if (Test-Path -LiteralPath $full) {
        Assert-NoReparsePoint -Path $full | Out-Null
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

function Get-FileInventory {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [string[]]$ExcludeRelativePath = @()
    )
    $rootFull = Assert-NoReparsePoint -Path $Root
    $excluded = @{}
    foreach ($path in $ExcludeRelativePath) {
        $excluded[$path.Replace('\', '/').ToLowerInvariant()] = $true
    }
    @(
        Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force |
            Sort-Object FullName |
            ForEach-Object {
                $relative = (Get-RelativePathPortable -BasePath $rootFull -Path $_.FullName).Replace('\', '/')
                if (-not $excluded.ContainsKey($relative.ToLowerInvariant())) {
                    $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                    [pscustomobject]@{
                        path = $relative
                        bytes = [long]$_.Length
                        sha256 = $hash.Hash.ToLowerInvariant()
                    }
                }
            }
    )
}

function Get-InventoryFingerprint {
    param([Parameter(Mandatory)] [object[]]$Inventory)
    $lines = @($Inventory | Sort-Object path | ForEach-Object {
        "$($_.path)`n$($_.bytes)`n$($_.sha256)"
    })
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($bytes)
        return ([BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Write-AtomicUtf8Json {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] $Value,
        [int]$Depth = 12
    )
    $full = Resolve-FullPath $Path
    $directory = Split-Path -Parent $full
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Assert-NoReparsePoint -Path $directory | Out-Null
    $temporary = "$full.$([Guid]::NewGuid().ToString('N')).tmp"
    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText(
        $temporary,
        $json + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
    try {
        if (Test-Path -LiteralPath $full) {
            $backup = "$full.replace-backup"
            [IO.File]::Replace($temporary, $full, $backup, $true)
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        }
        else {
            Move-Item -LiteralPath $temporary -Destination $full
        }
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

function Read-ReleaseManifest {
    param([Parameter(Mandatory)] [string]$ReleaseRoot)
    $root = Assert-NoReparsePoint -Path $ReleaseRoot
    $manifestPath = Join-Path $root "release-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Release manifest is missing: $manifestPath"
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "Release manifest is not valid JSON: $($_.Exception.Message)"
    }
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.product -ne "PalControl" -or
        [string]::IsNullOrWhiteSpace([string]$manifest.releaseId) -or
        [string]$manifest.releaseId -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{2,100}$' -or
        [string]$manifest.sourceRevision -notmatch '^[0-9a-f]{40,64}$' -or
        $manifest.sourceDirty -ne $false -or
        $manifest.platform -ne "win-x64" -or
        $manifest.dataContract.provider -ne "sqlite" -or
        [int]$manifest.dataContract.version -lt 1 -or
        $manifest.dataContract.rollbackPolicy -ne "same-contract-only") {
        throw "Release manifest is not eligible for production deployment."
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.executable)) {
        throw "Release manifest does not declare an executable."
    }
    return [pscustomobject]@{
        Root = $root
        Path = $manifestPath
        Manifest = $manifest
        ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Test-ReleaseDirectory {
    param([Parameter(Mandatory)] [string]$ReleaseRoot)
    $release = Read-ReleaseManifest -ReleaseRoot $ReleaseRoot
    $manifest = $release.Manifest
    $expected = @{}
    foreach ($file in @($manifest.files)) {
        $path = [string]$file.path
        if ([string]::IsNullOrWhiteSpace($path) -or
            $path.StartsWith('/') -or
            $path.StartsWith('\') -or
            $path -match '(^|[\\/])\.\.([\\/]|$)' -or
            $path.Contains(':') -or
            [string]$file.sha256 -notmatch '^[0-9a-f]{64}$' -or
            [long]$file.bytes -lt 0) {
            throw "Release manifest contains an unsafe file entry: '$path'."
        }
        $key = $path.Replace('\', '/').ToLowerInvariant()
        if ($key -eq "release-manifest.json" -or $expected.ContainsKey($key)) {
            throw "Release manifest contains a duplicate or reserved file entry: '$path'."
        }
        $expected[$key] = $file
    }
    if ($expected.Count -eq 0) {
        throw "Release manifest cannot contain an empty file inventory."
    }
    $actual = @(Get-FileInventory -Root $release.Root -ExcludeRelativePath @("release-manifest.json"))
    if ($actual.Count -ne $expected.Count) {
        throw "Release file count does not match its manifest."
    }
    foreach ($file in $actual) {
        $key = $file.path.ToLowerInvariant()
        if (-not $expected.ContainsKey($key)) {
            throw "Release contains an undeclared file: $($file.path)"
        }
        $declared = $expected[$key]
        if ([long]$declared.bytes -ne $file.bytes -or
            [string]$declared.sha256 -ne $file.sha256) {
            throw "Release file failed size or SHA-256 validation: $($file.path)"
        }
    }
    $executable = Assert-PathWithinRoot `
        -Path (Join-Path $release.Root ([string]$manifest.executable)) `
        -Root $release.Root `
        -Description "release executable"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Release executable is missing: $executable"
    }
    return [pscustomobject]@{
        Root = $release.Root
        Manifest = $manifest
        ManifestPath = $release.Path
        ManifestSha256 = $release.ManifestSha256
        ExecutablePath = $executable
        FileCount = $actual.Count
    }
}

function Expand-VerifiedReleaseArchive {
    param(
        [Parameter(Mandatory)] [string]$ArchivePath,
        [Parameter(Mandatory)] [string]$ExpectedSha256,
        [Parameter(Mandatory)] [string]$InstallRoot
    )
    $archive = Assert-NoReparsePoint -Path $ArchivePath
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or
        [IO.Path]::GetExtension($archive) -ne ".zip") {
        throw "ReleaseArchive must be an existing ZIP file."
    }
    if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "ExpectedSha256 must contain exactly 64 hexadecimal characters."
    }
    $actualArchiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualArchiveHash -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "Release archive SHA-256 does not match the approved value."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if ($zip.Entries.Count -lt 2 -or $zip.Entries.Count -gt 50000) {
            throw "Release archive has an unsafe entry count."
        }
        $expandedBytes = [long]0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($name) -or
                $name.StartsWith('/') -or
                $name -match '(^|/)\.\.(/|$)' -or
                $name.Contains(':')) {
                throw "Release archive contains an unsafe entry: '$name'."
            }
            $expandedBytes += [long]$entry.Length
            if ($expandedBytes -gt 4GB) {
                throw "Release archive expands beyond the 4 GiB safety limit."
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    $install = Assert-SafeDeploymentRoot -Path $InstallRoot -Name "InstallRoot"
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    Assert-NoReparsePoint -Path $install | Out-Null
    $stagingRoot = Join-Path $install ".staging"
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    $staging = Join-Path $stagingRoot $actualArchiveHash.Substring(0, 24)
    Remove-SafeDeploymentTree -Path $staging -ApprovedRoot $stagingRoot
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        Expand-Archive -LiteralPath $archive -DestinationPath $staging -Force
        Assert-NoReparsePoint -Path $staging | Out-Null
        $manifests = @(Get-ChildItem -LiteralPath $staging -Recurse -Filter "release-manifest.json" -File -Force)
        if ($manifests.Count -ne 1) {
            throw "Release archive must contain exactly one release-manifest.json."
        }
        $payloadRoot = Split-Path -Parent $manifests[0].FullName
        foreach ($extractedFile in @(Get-ChildItem -LiteralPath $staging -Recurse -File -Force)) {
            try {
                Assert-PathWithinRoot -Path $extractedFile.FullName -Root $payloadRoot `
                    -Description "release archive file" | Out-Null
            }
            catch {
                throw "Release archive contains a file outside its declared payload root: $($extractedFile.FullName)"
            }
        }
        $release = Test-ReleaseDirectory -ReleaseRoot $payloadRoot
        $releasesRoot = Join-Path $install "releases"
        New-Item -ItemType Directory -Path $releasesRoot -Force | Out-Null
        $destination = Join-Path $releasesRoot ([string]$release.Manifest.releaseId)
        Assert-PathWithinRoot -Path $destination -Root $releasesRoot -Description "release destination" | Out-Null
        if (Test-Path -LiteralPath $destination) {
            $existing = Test-ReleaseDirectory -ReleaseRoot $destination
            if ($existing.ManifestSha256 -ne $release.ManifestSha256) {
                throw "ReleaseId '$($release.Manifest.releaseId)' already exists with different content."
            }
            return $existing
        }
        Move-Item -LiteralPath $payloadRoot -Destination $destination
        return Test-ReleaseDirectory -ReleaseRoot $destination
    }
    finally {
        Remove-SafeDeploymentTree -Path $staging -ApprovedRoot $stagingRoot
    }
}

function New-ColdStateSnapshot {
    param(
        [Parameter(Mandatory)] [string]$StateRoot,
        [Parameter(Mandatory)] [string]$FromReleaseId,
        [Parameter(Mandatory)] [string]$ToReleaseId
    )
    $state = Assert-SafeDeploymentRoot -Path $StateRoot -Name "StateRoot"
    New-Item -ItemType Directory -Path $state -Force | Out-Null
    Assert-NoReparsePoint -Path $state | Out-Null
    $dataRoot = Join-Path $state "data"
    if (-not (Test-Path -LiteralPath $dataRoot)) {
        New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    }
    $inventory = @(Get-FileInventory -Root $dataRoot)
    $fingerprint = Get-InventoryFingerprint -Inventory $inventory
    $identityBytes = [Text.Encoding]::UTF8.GetBytes("$FromReleaseId`n$ToReleaseId`n$fingerprint")
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $identityHash = ([BitConverter]::ToString($sha.ComputeHash($identityBytes)) -replace '-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
    $snapshotId = "upgrade-$($identityHash.Substring(0, 24))"
    $snapshotRoot = Join-Path (Join-Path $state "deployment\snapshots") $snapshotId
    $manifestPath = Join-Path $snapshotRoot "manifest.json"
    if (Test-Path -LiteralPath $manifestPath) {
        $verified = Test-ColdStateSnapshot -SnapshotRoot $snapshotRoot
        $existing = $verified.Manifest
        if ($existing.sourceFingerprint -ne $fingerprint -or
            $existing.fromReleaseId -ne $FromReleaseId -or
            $existing.toReleaseId -ne $ToReleaseId) {
            throw "Existing cold snapshot identity does not match its source state."
        }
        return [pscustomobject]@{ Id = $snapshotId; Root = $snapshotRoot; Manifest = $existing }
    }
    $parent = Split-Path -Parent $snapshotRoot
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = Join-Path $parent ".$snapshotId-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temporary -Force | Out-Null
    try {
        $copyRoot = Join-Path $temporary "data"
        New-Item -ItemType Directory -Path $copyRoot -Force | Out-Null
        if ($inventory.Count -gt 0) {
            Copy-Item -Path (Join-Path $dataRoot "*") -Destination $copyRoot -Recurse -Force
        }
        $copyInventory = @(Get-FileInventory -Root $copyRoot)
        if ((Get-InventoryFingerprint -Inventory $copyInventory) -ne $fingerprint) {
            throw "Cold state snapshot did not reproduce the stopped data directory."
        }
        $manifest = [ordered]@{
            schemaVersion = 1
            snapshotId = $snapshotId
            createdAtUtc = [DateTime]::UtcNow.ToString("o")
            fromReleaseId = $FromReleaseId
            toReleaseId = $ToReleaseId
            sourceFingerprint = $fingerprint
            files = $copyInventory
        }
        Write-AtomicUtf8Json -Path (Join-Path $temporary "manifest.json") -Value $manifest
        Move-Item -LiteralPath $temporary -Destination $snapshotRoot
        return [pscustomobject]@{ Id = $snapshotId; Root = $snapshotRoot; Manifest = $manifest }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-SafeDeploymentTree -Path $temporary -ApprovedRoot $parent
        }
    }
}

function Test-ColdStateSnapshot {
    param([Parameter(Mandatory)] [string]$SnapshotRoot)
    $root = Assert-NoReparsePoint -Path $SnapshotRoot
    $manifestPath = Join-Path $root "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Cold snapshot manifest is missing."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        [string]$manifest.sourceFingerprint -notmatch '^[0-9a-f]{64}$') {
        throw "Cold snapshot manifest is invalid."
    }
    $inventory = @(Get-FileInventory -Root (Join-Path $root "data"))
    if ((Get-InventoryFingerprint -Inventory $inventory) -ne $manifest.sourceFingerprint) {
        throw "Cold snapshot data failed SHA-256 validation."
    }
    return [pscustomobject]@{ Root = $root; Manifest = $manifest; Inventory = $inventory }
}

function Restore-ColdStateSnapshot {
    param(
        [Parameter(Mandatory)] [string]$StateRoot,
        [Parameter(Mandatory)] [string]$SnapshotRoot
    )
    $state = Assert-SafeDeploymentRoot -Path $StateRoot -Name "StateRoot"
    Assert-NoReparsePoint -Path $state | Out-Null
    $snapshot = Test-ColdStateSnapshot -SnapshotRoot $SnapshotRoot
    $restoreParent = Join-Path $state "deployment\restore-staging"
    New-Item -ItemType Directory -Path $restoreParent -Force | Out-Null
    $restore = Join-Path $restoreParent ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $restore -Force | Out-Null
    try {
        $restoredData = Join-Path $restore "data"
        New-Item -ItemType Directory -Path $restoredData -Force | Out-Null
        $sourceData = Join-Path $snapshot.Root "data"
        if (@(Get-ChildItem -LiteralPath $sourceData -Force).Count -gt 0) {
            Copy-Item -Path (Join-Path $sourceData "*") -Destination $restoredData -Recurse -Force
        }
        $restoredInventory = @(Get-FileInventory -Root $restoredData)
        if ((Get-InventoryFingerprint -Inventory $restoredInventory) -ne $snapshot.Manifest.sourceFingerprint) {
            throw "Restored data staging failed SHA-256 validation."
        }
        $currentData = Join-Path $state "data"
        $quarantineRoot = Join-Path $state "deployment\quarantine"
        New-Item -ItemType Directory -Path $quarantineRoot -Force | Out-Null
        $quarantine = $null
        if (Test-Path -LiteralPath $currentData) {
            $quarantine = Join-Path $quarantineRoot (
                "data-" + [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                [Guid]::NewGuid().ToString('N').Substring(0, 8))
            Move-Item -LiteralPath $currentData -Destination $quarantine
        }
        try {
            Move-Item -LiteralPath $restoredData -Destination $currentData
        }
        catch {
            if ($null -ne $quarantine -and
                (Test-Path -LiteralPath $quarantine) -and
                -not (Test-Path -LiteralPath $currentData)) {
                Move-Item -LiteralPath $quarantine -Destination $currentData
            }
            throw
        }
        return $snapshot.Manifest
    }
    finally {
        if (Test-Path -LiteralPath $restore) {
            Remove-SafeDeploymentTree -Path $restore -ApprovedRoot $restoreParent
        }
    }
}

Export-ModuleMember -Function @(
    "Resolve-FullPath",
    "Get-RelativePathPortable",
    "Assert-SafeDeploymentRoot",
    "Assert-PathWithinRoot",
    "Assert-NoReparsePoint",
    "Remove-SafeDeploymentTree",
    "Get-FileInventory",
    "Get-InventoryFingerprint",
    "Write-AtomicUtf8Json",
    "Read-ReleaseManifest",
    "Test-ReleaseDirectory",
    "Expand-VerifiedReleaseArchive",
    "New-ColdStateSnapshot",
    "Test-ColdStateSnapshot",
    "Restore-ColdStateSnapshot"
)

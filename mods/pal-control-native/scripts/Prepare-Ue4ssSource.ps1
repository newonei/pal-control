<#
.SYNOPSIS
Prepares an already-cloned UE4SS checkout for the locked PalControlNative build.

.DESCRIPTION
This script only applies the four reviewed, deterministic source integrations required by
Build-PalControlNative.ps1. It never clones, fetches, checks out, builds, deploys, or
restarts anything. The input must be either a pristine checkout at the locked UE4SS and
Unreal commits, or the exact state previously produced by this script.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Ue4ssRoot,

    [string]$CMakePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ModRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepositoryRoot = (Resolve-Path (Join-Path $ModRoot "..\..")).Path
$LockPath = Join-Path $ModRoot "dependencies.lock.json"
$BuildScript = Join-Path $PSScriptRoot "Build-PalControlNative.ps1"
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false, $true)

function Get-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $script:GitPath -C $Repository @ArgumentList 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    # Preserve a leading status byte (notably the exact-commit space emitted by
    # `git submodule status`) while discarding only terminal line endings.
    $text = (($output | Out-String).TrimEnd())
    if ($exitCode -ne 0) {
        throw "[PALCONTROL_NATIVE_PREPARE] $FailureCode (git exit $exitCode): $text"
    }

    return $text
}

function Get-GitPathList {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    $text = Invoke-GitText `
        -Repository $Repository `
        -ArgumentList $ArgumentList `
        -FailureCode $FailureCode
    if ([string]::IsNullOrWhiteSpace($text)) {
        return @()
    }

    return @($text -split "`r?`n" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
}

function Normalize-Text {
    param([Parameter(Mandatory = $true)][string]$Value)

    return (($Value -replace "`r`n", "`n") -replace "`r", "`n").Trim()
}

function Convert-ToUtf8FileText {
    param([Parameter(Mandatory = $true)][string]$Value)

    return (Normalize-Text -Value $Value) + "`n"
}

function Assert-SingleAnchor {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Anchor,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    $first = $Text.IndexOf($Anchor, [System.StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_MISSING: locked UE4SS source changed unexpectedly."
    }
    $second = $Text.IndexOf(
        $Anchor,
        $first + $Anchor.Length,
        [System.StringComparison]::Ordinal)
    if ($second -ge 0) {
        throw "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_AMBIGUOUS: expected exactly one anchor."
    }
}

function Assert-NoReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_REPARSE_POINT: $Path"
    }
}

function Assert-NoReparseAncestors {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    $current = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        Assert-NoReparsePoint -Path $current -FailureCode $FailureCode
        $parent = [System.IO.Directory]::GetParent($current)
        if ($null -eq $parent) {
            break
        }
        $current = $parent.FullName
    }
}

function Assert-SafeOutputPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    Assert-NoReparsePoint -Path $Root -FailureCode "UE4SS_ROOT"
    $current = $Root
    foreach ($component in ($RelativePath -split '[\\/]')) {
        if ([string]::IsNullOrWhiteSpace($component)) {
            continue
        }
        $current = Join-Path $current $component
        Assert-NoReparsePoint -Path $current -FailureCode "OUTPUT_PATH"
    }
}

function Assert-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_NOT_FOUND: $Path"
    }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_UTF8_BOM: UTF-8 BOM is not permitted: $Path"
    }
    if (($bytes.Length -ge 2 -and
            (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or
             ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF))) -or
        ($bytes.Length -ge 4 -and
            (($bytes[0] -eq 0x00 -and $bytes[1] -eq 0x00 -and
              $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF) -or
             ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and
              $bytes[2] -eq 0x00 -and $bytes[3] -eq 0x00)))) {
        throw "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_NOT_UTF8: UTF-16/UTF-32 is not permitted: $Path"
    }
    if ($bytes -contains [byte]0) {
        throw "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_NOT_UTF8: NUL byte is not permitted: $Path"
    }
    try {
        [void]$Utf8NoBom.GetString($bytes)
    }
    catch {
        throw "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_NOT_UTF8: $Path"
    }
}

function Assert-FileIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$ExpectedSize,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_NOT_FOUND: $Path"
    }
    $file = Get-Item -LiteralPath $Path
    $sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.Length -ne $ExpectedSize -or $sha256 -cne $ExpectedSha256) {
        throw (
            "[PALCONTROL_NATIVE_PREPARE] ${FailureCode}_MISMATCH: expected " +
            "$ExpectedSize bytes/$ExpectedSha256, actual $($file.Length) bytes/$sha256.")
    }
}

function Test-ExactStringSet {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Actual,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Expected
    )

    $actualSorted = @($Actual | Sort-Object -CaseSensitive)
    $expectedSorted = @($Expected | Sort-Object -CaseSensitive)
    if ($actualSorted.Count -ne $expectedSorted.Count) {
        return $false
    }
    for ($index = 0; $index -lt $actualSorted.Count; $index++) {
        if ($actualSorted[$index] -cne $expectedSorted[$index]) {
            return $false
        }
    }
    return $true
}

function Convert-ToGitPath {
    param(
        [Parameter(Mandatory = $true)][string]$FullPath,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $rootPrefix = $Root.TrimEnd([char[]]@(92, 47))
    if (-not $FullPath.StartsWith(
        $rootPrefix + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "[PALCONTROL_NATIVE_PREPARE] PATH_OUTSIDE_ROOT: $FullPath"
    }
    return $FullPath.Substring($rootPrefix.Length + 1).Replace('\', '/')
}

function Assert-PreparedIntegration {
    param(
        [Parameter(Mandatory = $true)][hashtable]$ExpectedTextFiles,
        [Parameter(Mandatory = $true)][string]$IntegratedCargoLock,
        [Parameter(Mandatory = $true)][long]$CargoLockSize,
        [Parameter(Mandatory = $true)][string]$CargoLockSha256,
        [Parameter(Mandatory = $true)][hashtable]$IntegratedFiles,
        [Parameter(Mandatory = $true)][string]$IntegratedRoot,
        [Parameter(Mandatory = $true)][string[]]$ExpectedTrackedPaths,
        [Parameter(Mandatory = $true)][string[]]$ExpectedUntrackedPaths
    )

    $tracked = @(Get-GitPathList `
        -Repository $Ue4ssRoot `
        -ArgumentList @(
            "-c", "core.safecrlf=false", "diff", "--no-ext-diff", "--name-only",
            "--ignore-submodules=none", "HEAD", "--") `
        -FailureCode "POST_PREPARE_TRACKED_STATUS_UNREADABLE")
    $untracked = @(Get-GitPathList `
        -Repository $Ue4ssRoot `
        -ArgumentList @("ls-files", "--others", "--exclude-standard") `
        -FailureCode "POST_PREPARE_UNTRACKED_STATUS_UNREADABLE")
    if (-not (Test-ExactStringSet -Actual $tracked -Expected $ExpectedTrackedPaths) -or
        -not (Test-ExactStringSet -Actual $untracked -Expected $ExpectedUntrackedPaths)) {
        throw "[PALCONTROL_NATIVE_PREPARE] POST_PREPARE_STATUS_DRIFT: prepared paths do not match the reviewed integration set."
    }

    foreach ($entry in $ExpectedTextFiles.GetEnumerator()) {
        Assert-Utf8NoBom -Path $entry.Key -FailureCode "PREPARED_TEXT"
        $actual = [System.IO.File]::ReadAllText($entry.Key, $Utf8NoBom)
        if ((Normalize-Text -Value $actual) -cne
            (Normalize-Text -Value ([string]$entry.Value))) {
            throw "[PALCONTROL_NATIVE_PREPARE] PREPARED_TEXT_DRIFT: $($entry.Key)"
        }
    }

    Assert-Utf8NoBom -Path $IntegratedCargoLock -FailureCode "INTEGRATED_CARGO_LOCK"
    Assert-FileIdentity `
        -Path $IntegratedCargoLock `
        -ExpectedSize $CargoLockSize `
        -ExpectedSha256 $CargoLockSha256 `
        -FailureCode "INTEGRATED_CARGO_LOCK"

    if (-not (Test-Path -LiteralPath $IntegratedRoot -PathType Container)) {
        throw "[PALCONTROL_NATIVE_PREPARE] INTEGRATED_MOD_NOT_FOUND: $IntegratedRoot"
    }
    foreach ($item in (Get-ChildItem -LiteralPath $IntegratedRoot -Recurse -Force)) {
        Assert-NoReparsePoint -Path $item.FullName -FailureCode "INTEGRATED_MOD"
    }
    $actualIntegratedPaths = @(
        Get-ChildItem -LiteralPath $IntegratedRoot -Recurse -File -Force |
            ForEach-Object { Convert-ToGitPath -FullPath $_.FullName -Root $Ue4ssRoot }
    )
    if (-not (Test-ExactStringSet `
        -Actual $actualIntegratedPaths `
        -Expected @($IntegratedFiles.Keys))) {
        throw "[PALCONTROL_NATIVE_PREPARE] INTEGRATED_MOD_FILE_SET_DRIFT: $IntegratedRoot"
    }

    $integratedRootGitPath = Convert-ToGitPath `
        -FullPath $IntegratedRoot `
        -Root $Ue4ssRoot
    $expectedDirectorySet = @{$integratedRootGitPath = $true}
    foreach ($gitPath in $IntegratedFiles.Keys) {
        $parent = $gitPath
        while ($parent.LastIndexOf('/') -ge $integratedRootGitPath.Length) {
            $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
            $expectedDirectorySet[$parent] = $true
        }
    }
    $actualIntegratedDirectories = @($integratedRootGitPath)
    $actualIntegratedDirectories += @(
        Get-ChildItem -LiteralPath $IntegratedRoot -Recurse -Directory -Force |
            ForEach-Object { Convert-ToGitPath -FullPath $_.FullName -Root $Ue4ssRoot }
    )
    if (-not (Test-ExactStringSet `
        -Actual $actualIntegratedDirectories `
        -Expected @($expectedDirectorySet.Keys))) {
        throw "[PALCONTROL_NATIVE_PREPARE] INTEGRATED_MOD_DIRECTORY_SET_DRIFT: $IntegratedRoot"
    }
    foreach ($entry in $IntegratedFiles.GetEnumerator()) {
        $sourceHash = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash `
            -LiteralPath (Join-Path $Ue4ssRoot $entry.Key) `
            -Algorithm SHA256).Hash
        if ($sourceHash -cne $targetHash) {
            throw "[PALCONTROL_NATIVE_PREPARE] INTEGRATED_MOD_SOURCE_DRIFT: $($entry.Key)"
        }
    }
}

if (-not (Test-Path -LiteralPath $LockPath -PathType Leaf)) {
    throw "[PALCONTROL_NATIVE_PREPARE] LOCK_NOT_FOUND: $LockPath"
}
if (-not (Test-Path -LiteralPath $BuildScript -PathType Leaf)) {
    throw "[PALCONTROL_NATIVE_PREPARE] BUILD_GUARD_NOT_FOUND: $BuildScript"
}

$lock = Get-Content -LiteralPath $LockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$expectedUe4ssCommit = ([string]$lock.ue4ss.sourceCommit).Trim().ToLowerInvariant()
$expectedCorrosionCommit = `
    ([string]$lock.ue4ss.corrosionSourceCommit).Trim().ToLowerInvariant()
$expectedIconFontCommit = `
    ([string]$lock.ue4ss.iconFontCppHeadersSourceCommit).Trim().ToLowerInvariant()
$expectedCargoLockSize = [long]$lock.ue4ss.patternsleuthCargoLockSize
$expectedCargoLockSha256 = `
    ([string]$lock.ue4ss.patternsleuthCargoLockSha256).Trim().ToLowerInvariant()
if ($expectedUe4ssCommit -notmatch '^[0-9a-f]{40}$' -or
    $expectedCorrosionCommit -notmatch '^[0-9a-f]{40}$' -or
    $expectedIconFontCommit -notmatch '^[0-9a-f]{40}$' -or
    $expectedCargoLockSize -le 0 -or
    $expectedCargoLockSha256 -notmatch '^[0-9a-f]{64}$' -or
    $lock.build.cargoLocked -isnot [bool] -or
    -not [bool]$lock.build.cargoLocked) {
    throw (
        "[PALCONTROL_NATIVE_PREPARE] INVALID_LOCK: full UE4SS/Corrosion/IconFont " +
        "commits, reviewed Cargo.lock identity, and cargoLocked=true are required.")
}

$Ue4ssRoot = Get-AbsolutePath -Path $Ue4ssRoot -BasePath $RepositoryRoot
if (-not (Test-Path -LiteralPath $Ue4ssRoot -PathType Container)) {
    throw "[PALCONTROL_NATIVE_PREPARE] UE4SS_NOT_FOUND: $Ue4ssRoot. Clone it manually first."
}
Assert-NoReparsePoint -Path $Ue4ssRoot -FailureCode "UE4SS_ROOT"
Assert-NoReparseAncestors -Path $Ue4ssRoot -FailureCode "UE4SS_ANCESTOR"

$script:GitPath = (Get-Command "git.exe" -ErrorAction Stop).Source
$gitTopLevel = Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("rev-parse", "--show-toplevel") `
    -FailureCode "UE4SS_REPOSITORY_UNREADABLE"
$gitTopLevel = [System.IO.Path]::GetFullPath($gitTopLevel)
if (-not $gitTopLevel.Equals(
    $Ue4ssRoot,
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "[PALCONTROL_NATIVE_PREPARE] UE4SS_ROOT_MISMATCH: repository root is $gitTopLevel"
}

$actualUe4ssCommit = (Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("rev-parse", "HEAD") `
    -FailureCode "UE4SS_HEAD_UNREADABLE").ToLowerInvariant()
if ($actualUe4ssCommit -cne $expectedUe4ssCommit) {
    throw (
        "[PALCONTROL_NATIVE_PREPARE] UE4SS_HEAD_MISMATCH: expected " +
        "$expectedUe4ssCommit, actual $actualUe4ssCommit. No checkout was attempted.")
}

$unrealTreeEntry = Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("ls-tree", $expectedUe4ssCommit, "--", "deps/first/Unreal") `
    -FailureCode "UNREAL_GITLINK_UNREADABLE"
if ($unrealTreeEntry -notmatch
    '^160000\s+commit\s+([0-9a-fA-F]{40})\s+deps/first/Unreal$') {
    throw "[PALCONTROL_NATIVE_PREPARE] UNREAL_GITLINK_INVALID: $unrealTreeEntry"
}
$expectedUnrealCommit = $Matches[1].ToLowerInvariant()
$unrealRoot = Join-Path $Ue4ssRoot "deps\first\Unreal"
if (-not (Test-Path -LiteralPath $unrealRoot -PathType Container)) {
    throw "[PALCONTROL_NATIVE_PREPARE] UNREAL_SUBMODULE_NOT_FOUND: initialize submodules manually first."
}
Assert-NoReparsePoint -Path $unrealRoot -FailureCode "UNREAL_ROOT"
$actualUnrealCommit = (Invoke-GitText `
    -Repository $unrealRoot `
    -ArgumentList @("rev-parse", "HEAD") `
    -FailureCode "UNREAL_HEAD_UNREADABLE").ToLowerInvariant()
if ($actualUnrealCommit -cne $expectedUnrealCommit) {
    throw (
        "[PALCONTROL_NATIVE_PREPARE] UNREAL_HEAD_MISMATCH: locked gitlink " +
        "expects $expectedUnrealCommit, actual $actualUnrealCommit. No checkout was attempted.")
}
$unrealStatus = Invoke-GitText `
    -Repository $unrealRoot `
    -ArgumentList @("status", "--porcelain=v1", "--untracked-files=all") `
    -FailureCode "UNREAL_STATUS_UNREADABLE"
if (-not [string]::IsNullOrWhiteSpace($unrealStatus)) {
    throw "[PALCONTROL_NATIVE_PREPARE] UNREAL_WORKTREE_DIRTY: clean the locked Unreal submodule first."
}

$submoduleStatusLines = @(Get-GitPathList `
    -Repository $Ue4ssRoot `
    -ArgumentList @("submodule", "status", "--recursive") `
    -FailureCode "SUBMODULE_STATUS_UNREADABLE")
if ($submoduleStatusLines.Count -eq 0) {
    throw "[PALCONTROL_NATIVE_PREPARE] SUBMODULE_SET_EMPTY: initialize locked submodules manually first."
}
foreach ($statusLine in $submoduleStatusLines) {
    if ($statusLine -notmatch '^ [0-9a-fA-F]{40}\s+') {
        throw (
            "[PALCONTROL_NATIVE_PREPARE] SUBMODULE_HEAD_MISMATCH: every recursive " +
            "submodule must be initialized at its recorded gitlink; found '$statusLine'.")
    }
}
$recursiveSubmoduleDrift = Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @(
        "submodule", "foreach", "--recursive", "--quiet",
        "git status --porcelain=v1 --untracked-files=all --ignored") `
    -FailureCode "SUBMODULE_WORKTREE_STATUS_UNREADABLE"
if (-not [string]::IsNullOrWhiteSpace($recursiveSubmoduleDrift)) {
    throw (
        "[PALCONTROL_NATIVE_PREPARE] SUBMODULE_WORKTREE_DIRTY: recursive submodules " +
        "must have no tracked, untracked, or ignored drift.")
}

# Build every expected result from the immutable locked commit, and verify all four
# anchors before touching the checkout.
$baselineCppMods = Normalize-Text -Value (Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("show", "${expectedUe4ssCommit}:cppmods/CMakeLists.txt") `
    -FailureCode "CPPMODS_BASELINE_UNREADABLE")
$cppModsAnchor = 'add_subdirectory("EventViewerMod")'
Assert-SingleAnchor `
    -Text $baselineCppMods `
    -Anchor $cppModsAnchor `
    -FailureCode "CPPMODS_ATTACHMENT_ANCHOR"
$expectedCppMods = $baselineCppMods.Replace(
    $cppModsAnchor,
    $cppModsAnchor + "`n" + 'add_subdirectory("PalControlNative")')

$baselineThirdParty = Normalize-Text -Value (Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("show", "${expectedUe4ssCommit}:deps/third/CMakeLists.txt") `
    -FailureCode "THIRD_PARTY_BASELINE_UNREADABLE")
$iconFontAnchor = @'
GIT_REPOSITORY https://github.com/juliettef/IconFontCppHeaders.git
    GIT_TAG main
    GIT_SHALLOW TRUE
'@
$iconFontAnchor = Normalize-Text -Value $iconFontAnchor
$iconFontPin = Normalize-Text -Value @"
GIT_REPOSITORY https://github.com/juliettef/IconFontCppHeaders.git
    GIT_TAG $expectedIconFontCommit
    GIT_SHALLOW FALSE
"@
Assert-SingleAnchor `
    -Text $baselineThirdParty `
    -Anchor $iconFontAnchor `
    -FailureCode "ICONFONT_PIN_ANCHOR"
$expectedThirdParty = $baselineThirdParty.Replace($iconFontAnchor, $iconFontPin)

$corrosionFetchAnchor = @'
GIT_REPOSITORY https://github.com/UE4SS-RE/corrosion.git
    #TODO: Go back to main repo once this issue is fixed.
    GIT_TAG 52844733e14f095c947577627e367ee5f6458af7
    GIT_SHALLOW TRUE
'@
$corrosionFetchAnchor = Normalize-Text -Value $corrosionFetchAnchor
$corrosionFetchPin = Normalize-Text -Value @"
GIT_REPOSITORY https://github.com/UE4SS-RE/corrosion.git
    #TODO: Go back to main repo once this issue is fixed.
    GIT_TAG $expectedCorrosionCommit
    GIT_SHALLOW FALSE
"@
Assert-SingleAnchor `
    -Text $expectedThirdParty `
    -Anchor $corrosionFetchAnchor `
    -FailureCode "CORROSION_PIN_ANCHOR"
$expectedThirdParty = $expectedThirdParty.Replace(
    $corrosionFetchAnchor,
    $corrosionFetchPin)

$baselineCorrosion = Normalize-Text -Value (Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("show", "${expectedUe4ssCommit}:deps/third/corrosion/CMakeLists.txt") `
    -FailureCode "CORROSION_BASELINE_UNREADABLE")
$cargoImportAnchor = 'corrosion_import_crate(MANIFEST_PATH "${CMAKE_CURRENT_SOURCE_DIR}/../../first/patternsleuth_bind/Cargo.toml")'
$cargoLockedImport = Normalize-Text -Value @'
corrosion_import_crate(
    MANIFEST_PATH "${CMAKE_CURRENT_SOURCE_DIR}/../../first/patternsleuth_bind/Cargo.toml"
    LOCKED
)
'@
Assert-SingleAnchor `
    -Text $baselineCorrosion `
    -Anchor $cargoImportAnchor `
    -FailureCode "CARGO_LOCKED_IMPORT_ANCHOR"
$expectedCorrosion = $baselineCorrosion.Replace(
    $cargoImportAnchor,
    $cargoLockedImport)

$canonicalCargoLock = Join-Path $ModRoot "patches\patternsleuth_bind.Cargo.lock"
Assert-Utf8NoBom -Path $canonicalCargoLock -FailureCode "CANONICAL_CARGO_LOCK"
Assert-FileIdentity `
    -Path $canonicalCargoLock `
    -ExpectedSize $expectedCargoLockSize `
    -ExpectedSha256 $expectedCargoLockSha256 `
    -FailureCode "CANONICAL_CARGO_LOCK"

$canonicalFiles = New-Object System.Collections.Generic.List[System.IO.FileInfo]
$canonicalFiles.Add((Get-Item -LiteralPath (Join-Path $ModRoot "CMakeLists.txt")))
$canonicalSourceRoot = Join-Path $ModRoot "Source"
Assert-NoReparsePoint -Path $canonicalSourceRoot -FailureCode "CANONICAL_MOD_SOURCE"
foreach ($item in (Get-ChildItem -LiteralPath $canonicalSourceRoot -Recurse -Force)) {
    Assert-NoReparsePoint -Path $item.FullName -FailureCode "CANONICAL_MOD_SOURCE"
}
foreach ($file in (Get-ChildItem -LiteralPath $canonicalSourceRoot -Recurse -File -Force)) {
    $canonicalFiles.Add($file)
}
$integratedRoot = Join-Path $Ue4ssRoot "cppmods\PalControlNative"
$integratedFiles = @{}
foreach ($sourceFile in $canonicalFiles) {
    Assert-NoReparsePoint -Path $sourceFile.FullName -FailureCode "CANONICAL_MOD_SOURCE"
    Assert-Utf8NoBom -Path $sourceFile.FullName -FailureCode "CANONICAL_MOD_SOURCE"
    $relativeToMod = $sourceFile.FullName.Substring($ModRoot.Length).TrimStart('\', '/')
    $integratedPath = Join-Path $integratedRoot $relativeToMod
    $integratedGitPath = Convert-ToGitPath -FullPath $integratedPath -Root $Ue4ssRoot
    $integratedFiles[$integratedGitPath] = $sourceFile.FullName
}

$cppModsList = Join-Path $Ue4ssRoot "cppmods\CMakeLists.txt"
$thirdPartyList = Join-Path $Ue4ssRoot "deps\third\CMakeLists.txt"
$corrosionList = Join-Path $Ue4ssRoot "deps\third\corrosion\CMakeLists.txt"
$integratedCargoLock = Join-Path $Ue4ssRoot "deps\first\patternsleuth_bind\Cargo.lock"
foreach ($relativeOutput in @(
    "cppmods\CMakeLists.txt",
    "cppmods\PalControlNative",
    "deps\first\patternsleuth_bind\Cargo.lock",
    "deps\third\CMakeLists.txt",
    "deps\third\corrosion\CMakeLists.txt"
)) {
    Assert-SafeOutputPath -Root $Ue4ssRoot -RelativePath $relativeOutput
}

$expectedTextFiles = @{
    $cppModsList = $expectedCppMods
    $thirdPartyList = $expectedThirdParty
    $corrosionList = $expectedCorrosion
}
$expectedTrackedPaths = @(
    "cppmods/CMakeLists.txt",
    "deps/first/patternsleuth_bind/Cargo.lock",
    "deps/third/CMakeLists.txt",
    "deps/third/corrosion/CMakeLists.txt"
)
$expectedUntrackedPaths = @($integratedFiles.Keys)

$stagedPaths = @(Get-GitPathList `
    -Repository $Ue4ssRoot `
    -ArgumentList @(
        "diff", "--cached", "--no-ext-diff", "--name-only",
        "--ignore-submodules=none", "HEAD", "--") `
    -FailureCode "UE4SS_STAGED_STATUS_UNREADABLE")
if ($stagedPaths.Count -ne 0) {
    throw "[PALCONTROL_NATIVE_PREPARE] UE4SS_STAGED_CHANGES: unstage and clean the checkout first."
}
$trackedPaths = @(Get-GitPathList `
    -Repository $Ue4ssRoot `
    -ArgumentList @(
        "-c", "core.safecrlf=false", "diff", "--no-ext-diff", "--name-only",
        "--ignore-submodules=none", "HEAD", "--") `
    -FailureCode "UE4SS_TRACKED_STATUS_UNREADABLE")
$untrackedPaths = @(Get-GitPathList `
    -Repository $Ue4ssRoot `
    -ArgumentList @("ls-files", "--others", "--exclude-standard") `
    -FailureCode "UE4SS_UNTRACKED_STATUS_UNREADABLE")
$ignoredPaths = @(Get-GitPathList `
    -Repository $Ue4ssRoot `
    -ArgumentList @("ls-files", "--others", "--ignored", "--exclude-standard") `
    -FailureCode "UE4SS_IGNORED_STATUS_UNREADABLE")
if ($ignoredPaths.Count -ne 0) {
    throw (
        "[PALCONTROL_NATIVE_PREPARE] UE4SS_IGNORED_FILES_PRESENT: remove ignored " +
        "files from the source checkout before preparation.")
}

$isPristine = $trackedPaths.Count -eq 0 -and $untrackedPaths.Count -eq 0
$isPrepared = (Test-ExactStringSet `
        -Actual $trackedPaths `
        -Expected $expectedTrackedPaths) -and
    (Test-ExactStringSet `
        -Actual $untrackedPaths `
        -Expected $expectedUntrackedPaths)

if ($isPristine) {
    if (Test-Path -LiteralPath $integratedRoot) {
        throw "[PALCONTROL_NATIVE_PREPARE] UNTRACKED_TARGET_HIDDEN: remove the unexpected $integratedRoot"
    }
}
elseif ($isPrepared) {
    Assert-PreparedIntegration `
        -ExpectedTextFiles $expectedTextFiles `
        -IntegratedCargoLock $integratedCargoLock `
        -CargoLockSize $expectedCargoLockSize `
        -CargoLockSha256 $expectedCargoLockSha256 `
        -IntegratedFiles $integratedFiles `
        -IntegratedRoot $integratedRoot `
        -ExpectedTrackedPaths $expectedTrackedPaths `
        -ExpectedUntrackedPaths $expectedUntrackedPaths
    Write-Host "[prepare-ok] exact reviewed integration already present; no files changed"
}
else {
    throw (
        "[PALCONTROL_NATIVE_PREPARE] UE4SS_WORKTREE_NOT_PRISTINE_OR_PREPARED: " +
        "refusing tracked/untracked drift. Start from a clean locked checkout.")
}

if ($isPristine) {
    # All source, lock, path and anchor checks have completed. Only now mutate the checkout.
    foreach ($entry in $integratedFiles.GetEnumerator()) {
        $destination = Join-Path $Ue4ssRoot $entry.Key
        $destinationDirectory = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }
        [System.IO.File]::Copy($entry.Value, $destination, $false)
    }
    [System.IO.File]::WriteAllText(
        $cppModsList,
        (Convert-ToUtf8FileText -Value $expectedCppMods),
        $Utf8NoBom)
    [System.IO.File]::WriteAllText(
        $thirdPartyList,
        (Convert-ToUtf8FileText -Value $expectedThirdParty),
        $Utf8NoBom)
    [System.IO.File]::WriteAllText(
        $corrosionList,
        (Convert-ToUtf8FileText -Value $expectedCorrosion),
        $Utf8NoBom)
    [System.IO.File]::Copy($canonicalCargoLock, $integratedCargoLock, $true)

    Assert-PreparedIntegration `
        -ExpectedTextFiles $expectedTextFiles `
        -IntegratedCargoLock $integratedCargoLock `
        -CargoLockSize $expectedCargoLockSize `
        -CargoLockSha256 $expectedCargoLockSha256 `
        -IntegratedFiles $integratedFiles `
        -IntegratedRoot $integratedRoot `
        -ExpectedTrackedPaths $expectedTrackedPaths `
        -ExpectedUntrackedPaths $expectedUntrackedPaths
    Write-Host "[prepare-ok] applied the exact four reviewed UE4SS integrations"
}

Write-Host "[prepare-ok] UE4SS  $actualUe4ssCommit"
Write-Host "[prepare-ok] Unreal $actualUnrealCommit"
Write-Host "[guard] invoking Build-PalControlNative.ps1 -GuardOnly"
$guardParameters = @{
    Ue4ssRoot = $Ue4ssRoot
    GuardOnly = $true
}
if (-not [string]::IsNullOrWhiteSpace($CMakePath)) {
    $guardParameters.CMakePath = $CMakePath
}
& $BuildScript @guardParameters
Write-Host "[prepare-ok] build guard accepted the prepared source; no build or deployment was run"

<#
.SYNOPSIS
Builds PalControlNative against the exact UE4SS revision pinned by dependencies.lock.json.

.DESCRIPTION
This entry point is deliberately fail-fast. It never clones, fetches, or checks out the
UE4SS repository, and it never deploys or restarts anything. The caller must provide an
existing UE4SS checkout whose HEAD and deps/first/Unreal submodule match the lock file.
The upstream CMake project may populate its own build dependencies inside the independent
build directory. Output is kept outside the UE4SS source tree so a cache created for
another revision cannot be reused accidentally.
#>
[CmdletBinding()]
param(
    [string]$Ue4ssRoot,
    [string]$BuildDirectory,
    [string]$Configuration = "Game__Shipping__Win64",
    [string]$CMakePath,
    [string]$Ue4ssReleaseArchivePath,
    [string]$Ue4ssRuntimeDllPath,
    [switch]$GuardOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ModRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepositoryRoot = (Resolve-Path (Join-Path $ModRoot "..\..")).Path
$LockPath = Join-Path $ModRoot "dependencies.lock.json"

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

    $text = (($output | Out-String).Trim())
    if ($exitCode -ne 0) {
        throw "[PALCONTROL_NATIVE_GUARD] $FailureCode (git exit $exitCode): $text"
    }

    return $text
}

function Invoke-NativeTool {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "[PALCONTROL_NATIVE_BUILD] $FailureCode (exit $LASTEXITCODE)"
    }
}

function Invoke-NativeToolText {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $FilePath @ArgumentList 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    $text = (($output | Out-String).Trim())
    if ($exitCode -ne 0) {
        throw "[PALCONTROL_NATIVE_GUARD] $FailureCode (exit $exitCode): $text"
    }

    return $text
}

function Resolve-CMakeExecutable {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = Get-AbsolutePath -Path $RequestedPath -BasePath $RepositoryRoot
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "[PALCONTROL_NATIVE_GUARD] CMAKE_NOT_FOUND: $resolved"
        }
        return $resolved
    }

    $command = Get-Command "cmake.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    $vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vsWhere -PathType Leaf) {
        $installations = & $vsWhere `
            -products * `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath
        foreach ($installation in $installations) {
            if (-not [string]::IsNullOrWhiteSpace($installation)) {
                $candidates.Add((Join-Path $installation "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"))
            }
        }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "[PALCONTROL_NATIVE_GUARD] CMAKE_NOT_FOUND: install CMake or pass -CMakePath."
}

function Resolve-RustExecutable {
    $command = Get-Command "rustc.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidate = Join-Path $env:USERPROFILE ".cargo\bin\rustc.exe"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }

    throw "[PALCONTROL_NATIVE_GUARD] RUSTC_NOT_FOUND: install the locked Rust toolchain before building."
}

function Resolve-MsvcCompiler {
    $vsWhere = Join-Path ${env:ProgramFiles(x86)} `
        "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vsWhere -PathType Leaf)) {
        throw "[PALCONTROL_NATIVE_GUARD] VSWHERE_NOT_FOUND: $vsWhere"
    }
    $installation = @(& $vsWhere `
        -latest `
        -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace([string]$installation)) {
        throw "[PALCONTROL_NATIVE_GUARD] MSVC_NOT_FOUND: no x64 C++ toolchain was found."
    }
    $versionFile = Join-Path $installation `
        "VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt"
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        throw "[PALCONTROL_NATIVE_GUARD] MSVC_VERSION_FILE_NOT_FOUND: $versionFile"
    }
    $toolsVersion = ([IO.File]::ReadAllText($versionFile)).Trim()
    $compiler = Join-Path $installation `
        "VC\Tools\MSVC\$toolsVersion\bin\Hostx64\x64\cl.exe"
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "[PALCONTROL_NATIVE_GUARD] MSVC_COMPILER_NOT_FOUND: $compiler"
    }
    return $compiler
}

function Assert-FileIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$ExpectedSize,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "[PALCONTROL_NATIVE_GUARD] ${FailureCode}_NOT_FOUND: $Path"
    }
    $file = Get-Item -LiteralPath $Path
    $sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.Length -ne $ExpectedSize -or $sha256 -cne $ExpectedSha256) {
        throw (
            "[PALCONTROL_NATIVE_GUARD] ${FailureCode}_MISMATCH: expected " +
            "$ExpectedSize bytes/$ExpectedSha256, actual $($file.Length) bytes/$sha256.")
    }
}

function Assert-IntegratedModSource {
    param([Parameter(Mandatory = $true)][string]$IntegratedRoot)

    if (-not (Test-Path -LiteralPath $IntegratedRoot -PathType Container)) {
        throw "[PALCONTROL_NATIVE_GUARD] MOD_NOT_ATTACHED: missing $IntegratedRoot"
    }

    $filesToCompare = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    $filesToCompare.Add((Get-Item -LiteralPath (Join-Path $ModRoot "CMakeLists.txt")))
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $ModRoot "Source") -Recurse -File)) {
        $filesToCompare.Add($file)
    }

    foreach ($sourceFile in $filesToCompare) {
        $relativePath = $sourceFile.FullName.Substring($ModRoot.Length).TrimStart("\", "/")
        $integratedFile = Join-Path $IntegratedRoot $relativePath
        if (-not (Test-Path -LiteralPath $integratedFile -PathType Leaf)) {
            throw "[PALCONTROL_NATIVE_GUARD] MOD_SOURCE_MISMATCH: missing $relativePath in UE4SS cppmods."
        }

        $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
        $integratedHash = (Get-FileHash -LiteralPath $integratedFile -Algorithm SHA256).Hash
        if ($sourceHash -ne $integratedHash) {
            throw "[PALCONTROL_NATIVE_GUARD] MOD_SOURCE_MISMATCH: $relativePath differs from the canonical mod source."
        }
    }
}

function Get-ExpectedIntegratedPaths {
    $paths = New-Object System.Collections.Generic.List[string]
    $paths.Add("cppmods/PalControlNative/CMakeLists.txt")
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $ModRoot "Source") -Recurse -File)) {
        $relativePath = $file.FullName.Substring($ModRoot.Length).TrimStart("\", "/")
        $paths.Add(("cppmods/PalControlNative/" + ($relativePath -replace '\\', '/')))
    }
    return @($paths | Sort-Object)
}

function Assert-ExactUntrackedSourceSet {
    param([Parameter(Mandatory = $true)][string]$Repository)

    $untracked = Invoke-GitText `
        -Repository $Repository `
        -ArgumentList @("ls-files", "--others", "--exclude-standard") `
        -FailureCode "UE4SS_UNTRACKED_SOURCE_UNREADABLE"
    $actual = @($untracked -split "`r?`n" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    } | Sort-Object)
    $ignored = Invoke-GitText `
        -Repository $Repository `
        -ArgumentList @("ls-files", "--others", "--ignored", "--exclude-standard") `
        -FailureCode "UE4SS_IGNORED_SOURCE_UNREADABLE"
    $expected = @(Get-ExpectedIntegratedPaths)
    if (-not [string]::IsNullOrWhiteSpace($ignored) -or
        ($actual -join "`n") -cne ($expected -join "`n")) {
        throw (
            "[PALCONTROL_NATIVE_GUARD] UE4SS_UNTRACKED_SOURCE_DRIFT: only the " +
            "exact canonical cppmods/PalControlNative files may be untracked; found " +
            ($actual -join ", "))
    }
}

function Assert-SubmodulesClean {
    param([Parameter(Mandatory = $true)][string]$Repository)

    $status = Invoke-GitText `
        -Repository $Repository `
        -ArgumentList @("submodule", "status", "--recursive") `
        -FailureCode "UE4SS_SUBMODULE_STATUS_UNREADABLE"
    foreach ($line in @($status -split "`r?`n" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })) {
        if ($line -notmatch '^([ +\-U]?)([0-9a-fA-F]{40})\s+(.+?)(?:\s+\(.+\))?$' -or
            $Matches[1] -notin @('', ' ')) {
            throw "[PALCONTROL_NATIVE_GUARD] UE4SS_SUBMODULE_NOT_LOCKED: $line"
        }
        $relativeSubmodule = $Matches[3]
        $submodulePath = Join-Path $Repository ($relativeSubmodule -replace '/', '\')
        $submoduleChanges = Invoke-GitText `
            -Repository $submodulePath `
            -ArgumentList @(
                "status", "--porcelain=v1", "--untracked-files=all",
                "--ignored=matching") `
            -FailureCode "UE4SS_SUBMODULE_STATUS_UNREADABLE"
        if (-not [string]::IsNullOrWhiteSpace($submoduleChanges)) {
            throw (
                "[PALCONTROL_NATIVE_GUARD] UE4SS_SUBMODULE_SOURCE_DIRTY: " +
                "$relativeSubmodule contains tracked, untracked, or ignored files: " +
                $submoduleChanges)
        }
    }
}

if (-not (Test-Path -LiteralPath $LockPath -PathType Leaf)) {
    throw "[PALCONTROL_NATIVE_GUARD] LOCK_NOT_FOUND: $LockPath"
}

$lock = Get-Content -LiteralPath $LockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$targetGameBuild = ([string]$lock.palworldTarget).Trim()
$targetSteamBuild = ([string]$lock.steamBuild).Trim()
$targetExecutableSha256 = ([string]$lock.palServerExecutable.sha256).Trim().ToLowerInvariant()
$targetExecutableSize = [long]$lock.palServerExecutable.size
$nativeProtocolVersion = ([string]$lock.native.protocolVersion).Trim()
$nativeModVersion = ([string]$lock.native.modVersion).Trim()
$nativeCapabilityStatus = ([string]$lock.native.capabilityStatus).Trim()
$nativePipeName = ([string]$lock.native.pipeName).Trim()
$controlApiServiceName = ([string]$lock.native.controlApiServiceName).Trim()
$controlApiServiceSid = ([string]$lock.native.controlApiServiceSid).Trim()
$expectedArtifactSha256 = `
    ([string]$lock.build.palControlNativeDllSha256).Trim().ToLowerInvariant()
$expectedReleaseArchiveSha256 = `
    ([string]$lock.ue4ss.releaseArchiveSha256).Trim().ToLowerInvariant()
$expectedRuntimeDllSha256 = `
    ([string]$lock.ue4ss.runtimeDllSha256).Trim().ToLowerInvariant()
$expectedRuntimeDllSize = [long]$lock.ue4ss.runtimeDllSize
$expectedCorrosionCommit = `
    ([string]$lock.ue4ss.corrosionSourceCommit).Trim().ToLowerInvariant()
$expectedIconFontCommit = `
    ([string]$lock.ue4ss.iconFontCppHeadersSourceCommit).Trim().ToLowerInvariant()
$expectedPatternsleuthCargoLockSize = `
    [long]$lock.ue4ss.patternsleuthCargoLockSize
$expectedPatternsleuthCargoLockSha256 = `
    ([string]$lock.ue4ss.patternsleuthCargoLockSha256).Trim().ToLowerInvariant()
$cargoLocked = [bool]$lock.build.cargoLocked
$expectedArtifactSize = [long]$lock.build.palControlNativeDllSize
if ($targetGameBuild -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' -or
    $targetSteamBuild -notmatch '^[0-9]+$' -or
    $targetExecutableSha256 -notmatch '^[0-9a-f]{64}$' -or
    $targetExecutableSize -le 0 -or
    $nativeProtocolVersion -ne '1.1' -or
    $nativeModVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+-[a-z0-9.-]+$' -or
    $nativeCapabilityStatus -ne 'read-only-candidate-unverified' -or
    $nativePipeName -notmatch '^[A-Za-z0-9._-]{1,128}$' -or
    $controlApiServiceName -notmatch '^[A-Za-z0-9._-]{3,80}$' -or
    $controlApiServiceSid -notmatch '^S-1-5-80(-[0-9]+){5}$' -or
    $expectedReleaseArchiveSha256 -notmatch '^[0-9a-f]{64}$' -or
    $expectedRuntimeDllSha256 -notmatch '^[0-9a-f]{64}$' -or
    $expectedRuntimeDllSize -le 0 -or
    $expectedCorrosionCommit -notmatch '^[0-9a-f]{40}$' -or
    $expectedIconFontCommit -notmatch '^[0-9a-f]{40}$' -or
    $expectedPatternsleuthCargoLockSize -le 0 -or
    $expectedPatternsleuthCargoLockSha256 -notmatch '^[0-9a-f]{64}$' -or
    -not $cargoLocked -or
    $expectedArtifactSize -le 0 -or
    $expectedArtifactSha256 -notmatch '^[0-9a-f]{64}$' -or
    [bool]$lock.native.writeCapabilities) {
    throw (
        '[PALCONTROL_NATIVE_GUARD] INVALID_LOCK: the current candidate must pin an exact ' +
        'game version, Steam build, PalServer executable size/SHA-256, Native protocol 1.1, ' +
        'a prerelease mod version, locked Cargo metadata/build, deterministic DLL ' +
        'size/SHA-256, fetched dependency commits, pipe/service SID, and ' +
        'read-only-candidate-unverified/writeCapabilities=false.')
}

if ([string]::IsNullOrWhiteSpace($Ue4ssReleaseArchivePath) -xor
    [string]::IsNullOrWhiteSpace($Ue4ssRuntimeDllPath)) {
    throw "[PALCONTROL_NATIVE_GUARD] UE4SS_RUNTIME_PAIR_REQUIRED: pass both release archive and extracted UE4SS.dll paths, or neither."
}
if (-not [string]::IsNullOrWhiteSpace($Ue4ssReleaseArchivePath)) {
    $releaseArchive = Get-AbsolutePath `
        -Path $Ue4ssReleaseArchivePath `
        -BasePath $RepositoryRoot
    $runtimeDll = Get-AbsolutePath `
        -Path $Ue4ssRuntimeDllPath `
        -BasePath $RepositoryRoot
    if (-not (Test-Path -LiteralPath $releaseArchive -PathType Leaf) -or
        -not (Test-Path -LiteralPath $runtimeDll -PathType Leaf)) {
        throw "[PALCONTROL_NATIVE_GUARD] UE4SS_RUNTIME_NOT_FOUND: archive and extracted UE4SS.dll must both exist."
    }
    $actualReleaseArchiveSha256 = `
        (Get-FileHash -LiteralPath $releaseArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualRuntimeDllSha256 = `
        (Get-FileHash -LiteralPath $runtimeDll -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualReleaseArchiveSha256 -cne $expectedReleaseArchiveSha256 -or
        $actualRuntimeDllSha256 -cne $expectedRuntimeDllSha256 -or
        (Get-Item -LiteralPath $runtimeDll).Length -ne $expectedRuntimeDllSize) {
        throw "[PALCONTROL_NATIVE_GUARD] UE4SS_RUNTIME_HASH_MISMATCH: archive or extracted UE4SS.dll differs from dependencies.lock.json."
    }
    Write-Host "[guard-ok] UE4SS release archive/runtime DLL match the locked SHA-256 values"
}
$expectedUe4ssCommit = ([string]$lock.ue4ss.sourceCommit).Trim().ToLowerInvariant()
if ($expectedUe4ssCommit -notmatch "^[0-9a-f]{40}$") {
    throw "[PALCONTROL_NATIVE_GUARD] INVALID_LOCK: ue4ss.sourceCommit must be a full 40-character SHA."
}

if ([string]::IsNullOrWhiteSpace($Ue4ssRoot)) {
    $Ue4ssRoot = Join-Path $RepositoryRoot "third_party\RE-UE4SS-Palworld-c2ac246"
}
$Ue4ssRoot = Get-AbsolutePath -Path $Ue4ssRoot -BasePath $RepositoryRoot

if (-not (Test-Path -LiteralPath $Ue4ssRoot -PathType Container)) {
    throw "[PALCONTROL_NATIVE_GUARD] UE4SS_NOT_FOUND: $Ue4ssRoot. This script does not clone dependencies."
}

$script:GitPath = (Get-Command "git.exe" -ErrorAction Stop).Source
$actualUe4ssCommit = (Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("rev-parse", "HEAD") `
    -FailureCode "UE4SS_HEAD_UNREADABLE").ToLowerInvariant()
if ($actualUe4ssCommit -ne $expectedUe4ssCommit) {
    throw "[PALCONTROL_NATIVE_GUARD] UE4SS_HEAD_MISMATCH: expected $expectedUe4ssCommit, actual $actualUe4ssCommit. No checkout was attempted."
}
$trackedDependencyPaths = Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @(
        "-c", "core.safecrlf=false", "diff", "--name-only",
        "--ignore-submodules=none", "HEAD", "--") `
    -FailureCode "UE4SS_STATUS_UNREADABLE"
$allowedDependencyChanges = @(
    "cppmods/CMakeLists.txt",
    "deps/first/patternsleuth_bind/Cargo.lock",
    "deps/third/CMakeLists.txt",
    "deps/third/corrosion/CMakeLists.txt"
)
$unexpectedDependencyChanges = @($trackedDependencyPaths -split "`r?`n" | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_) -and
    $allowedDependencyChanges -cnotcontains $_
})
if ($unexpectedDependencyChanges.Count -ne 0) {
    throw (
        "[PALCONTROL_NATIVE_GUARD] UE4SS_TRACKED_SOURCE_DIRTY: only the exact " +
        "PalControlNative attachment, reviewed Cargo.lock, Cargo LOCKED import, " +
        "and IconFont commit pin " +
        "may differ from the locked commit; found " +
        ($unexpectedDependencyChanges -join ", "))
}
Assert-ExactUntrackedSourceSet -Repository $Ue4ssRoot
Assert-SubmodulesClean -Repository $Ue4ssRoot

$unrealTreeEntry = Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("ls-tree", $expectedUe4ssCommit, "--", "deps/first/Unreal") `
    -FailureCode "UNREAL_GITLINK_UNREADABLE"
if ($unrealTreeEntry -notmatch "^160000\s+commit\s+([0-9a-fA-F]{40})\s+deps/first/Unreal$") {
    throw "[PALCONTROL_NATIVE_GUARD] UNREAL_GITLINK_INVALID: $unrealTreeEntry"
}
$expectedUnrealCommit = $Matches[1].ToLowerInvariant()

$unrealRoot = Join-Path $Ue4ssRoot "deps\first\Unreal"
if (-not (Test-Path -LiteralPath $unrealRoot -PathType Container)) {
    throw "[PALCONTROL_NATIVE_GUARD] UNREAL_SUBMODULE_NOT_FOUND: $unrealRoot. Initialize submodules before building."
}
$actualUnrealCommit = (Invoke-GitText `
    -Repository $unrealRoot `
    -ArgumentList @("rev-parse", "HEAD") `
    -FailureCode "UNREAL_HEAD_UNREADABLE").ToLowerInvariant()
if ($actualUnrealCommit -ne $expectedUnrealCommit) {
    throw "[PALCONTROL_NATIVE_GUARD] UNREAL_HEAD_MISMATCH: expected $expectedUnrealCommit, actual $actualUnrealCommit. No checkout was attempted."
}

$cppModsList = Join-Path $Ue4ssRoot "cppmods\CMakeLists.txt"
if (-not (Test-Path -LiteralPath $cppModsList -PathType Leaf)) {
    throw "[PALCONTROL_NATIVE_GUARD] CPPMODS_CMAKE_NOT_FOUND: $cppModsList"
}
$cppModsText = Get-Content -LiteralPath $cppModsList -Raw
if ($cppModsText -notmatch 'add_subdirectory\s*\(\s*["'']?PalControlNative["'']?\s*\)') {
    throw "[PALCONTROL_NATIVE_GUARD] MOD_NOT_ATTACHED: cppmods/CMakeLists.txt must add PalControlNative."
}
$baselineCppModsText = Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("show", "${expectedUe4ssCommit}:cppmods/CMakeLists.txt") `
    -FailureCode "CPPMODS_BASELINE_UNREADABLE"
$attachmentAnchor = 'add_subdirectory("EventViewerMod")'
$expectedCppModsText = $baselineCppModsText.Replace(
    $attachmentAnchor,
    $attachmentAnchor + "`n" + 'add_subdirectory("PalControlNative")')
$normalizeText = {
    param([string]$Value)
    return ($Value -replace "`r`n", "`n").Trim()
}
if ((& $normalizeText $cppModsText) -cne (& $normalizeText $expectedCppModsText)) {
    throw "[PALCONTROL_NATIVE_GUARD] CPPMODS_CMAKE_DRIFT: only the exact PalControlNative add_subdirectory line is permitted."
}

$thirdPartyList = Join-Path $Ue4ssRoot "deps\third\CMakeLists.txt"
$corrosionList = Join-Path $Ue4ssRoot "deps\third\corrosion\CMakeLists.txt"
foreach ($requiredFile in @($thirdPartyList, $corrosionList)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "[PALCONTROL_NATIVE_GUARD] UE4SS_PATCH_FILE_NOT_FOUND: $requiredFile"
    }
}
$baselineThirdPartyText = Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("show", "${expectedUe4ssCommit}:deps/third/CMakeLists.txt") `
    -FailureCode "THIRD_PARTY_BASELINE_UNREADABLE"
$iconFontAnchor = @'
GIT_REPOSITORY https://github.com/juliettef/IconFontCppHeaders.git
    GIT_TAG main
    GIT_SHALLOW TRUE
'@
$iconFontPin = @"
GIT_REPOSITORY https://github.com/juliettef/IconFontCppHeaders.git
    GIT_TAG $expectedIconFontCommit
    GIT_SHALLOW FALSE
"@
$normalizedThirdPartyBaseline = & $normalizeText $baselineThirdPartyText
if (-not $normalizedThirdPartyBaseline.Contains((& $normalizeText $iconFontAnchor))) {
    throw "[PALCONTROL_NATIVE_GUARD] ICONFONT_PATCH_ANCHOR_MISSING: locked UE4SS source changed unexpectedly."
}
$expectedThirdPartyText = $normalizedThirdPartyBaseline.Replace(
    (& $normalizeText $iconFontAnchor),
    (& $normalizeText $iconFontPin))
$corrosionFetchAnchor = @'
GIT_REPOSITORY https://github.com/UE4SS-RE/corrosion.git
    #TODO: Go back to main repo once this issue is fixed.
    GIT_TAG 52844733e14f095c947577627e367ee5f6458af7
    GIT_SHALLOW TRUE
'@
$corrosionFetchPin = @"
GIT_REPOSITORY https://github.com/UE4SS-RE/corrosion.git
    #TODO: Go back to main repo once this issue is fixed.
    GIT_TAG $expectedCorrosionCommit
    GIT_SHALLOW FALSE
"@
if (-not $expectedThirdPartyText.Contains(
    (& $normalizeText $corrosionFetchAnchor))) {
    throw "[PALCONTROL_NATIVE_GUARD] CORROSION_FETCH_PATCH_ANCHOR_MISSING: locked UE4SS source changed unexpectedly."
}
$expectedThirdPartyText = $expectedThirdPartyText.Replace(
    (& $normalizeText $corrosionFetchAnchor),
    (& $normalizeText $corrosionFetchPin))
if ((& $normalizeText ([IO.File]::ReadAllText($thirdPartyList))) -cne
    $expectedThirdPartyText) {
    throw "[PALCONTROL_NATIVE_GUARD] FETCHCONTENT_PIN_DRIFT: only the exact locked Corrosion/IconFont commits with non-shallow fetches are permitted."
}

$baselineCorrosionText = Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @("show", "${expectedUe4ssCommit}:deps/third/corrosion/CMakeLists.txt") `
    -FailureCode "CORROSION_BASELINE_UNREADABLE"
$cargoImportAnchor = 'corrosion_import_crate(MANIFEST_PATH "${CMAKE_CURRENT_SOURCE_DIR}/../../first/patternsleuth_bind/Cargo.toml")'
$cargoLockedImport = @'
corrosion_import_crate(
    MANIFEST_PATH "${CMAKE_CURRENT_SOURCE_DIR}/../../first/patternsleuth_bind/Cargo.toml"
    LOCKED
)
'@
$normalizedCorrosionBaseline = & $normalizeText $baselineCorrosionText
if (-not $normalizedCorrosionBaseline.Contains($cargoImportAnchor)) {
    throw "[PALCONTROL_NATIVE_GUARD] CARGO_LOCKED_PATCH_ANCHOR_MISSING: locked UE4SS source changed unexpectedly."
}
$expectedCorrosionText = $normalizedCorrosionBaseline.Replace(
    $cargoImportAnchor,
    (& $normalizeText $cargoLockedImport))
if ((& $normalizeText ([IO.File]::ReadAllText($corrosionList))) -cne
    $expectedCorrosionText) {
    throw "[PALCONTROL_NATIVE_GUARD] CARGO_LOCKED_IMPORT_DRIFT: Corrosion must import patternsleuth_bind with the exact LOCKED option."
}
$canonicalCargoLock = Join-Path $ModRoot "patches\patternsleuth_bind.Cargo.lock"
$integratedCargoLock = Join-Path $Ue4ssRoot `
    "deps\first\patternsleuth_bind\Cargo.lock"
Assert-FileIdentity `
    -Path $canonicalCargoLock `
    -ExpectedSize $expectedPatternsleuthCargoLockSize `
    -ExpectedSha256 $expectedPatternsleuthCargoLockSha256 `
    -FailureCode "CANONICAL_CARGO_LOCK"
Assert-FileIdentity `
    -Path $integratedCargoLock `
    -ExpectedSize $expectedPatternsleuthCargoLockSize `
    -ExpectedSha256 $expectedPatternsleuthCargoLockSha256 `
    -FailureCode "INTEGRATED_CARGO_LOCK"
Assert-IntegratedModSource -IntegratedRoot (Join-Path $Ue4ssRoot "cppmods\PalControlNative")

$modCMakeText = Get-Content -LiteralPath (Join-Path $ModRoot "CMakeLists.txt") -Raw
foreach ($requiredCMakeToken in @(
    'PALCONTROL_TARGET_GAME_BUILD="${PALCONTROL_TARGET_GAME_BUILD}"',
    'PALCONTROL_TARGET_STEAM_BUILD="${PALCONTROL_TARGET_STEAM_BUILD}"',
    'PALCONTROL_TARGET_EXECUTABLE_SHA256="${PALCONTROL_TARGET_EXECUTABLE_SHA256}"',
    'PALCONTROL_TARGET_EXECUTABLE_SIZE=${PALCONTROL_TARGET_EXECUTABLE_SIZE}',
    'PALCONTROL_NATIVE_MOD_VERSION="${PALCONTROL_NATIVE_MOD_VERSION}"',
    'PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256="${PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256}"',
    'PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE=${PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE}',
    'PALCONTROL_PIPE_NAME="${PALCONTROL_PIPE_NAME}"',
    'PALCONTROL_CONTROL_API_SERVICE_SID="${PALCONTROL_CONTROL_API_SERVICE_SID}"',
    'PALCONTROL_ENABLE_WRITE_CAPABILITIES=$<BOOL:${PALCONTROL_ENABLE_WRITE_CAPABILITIES}>'
)) {
    if (-not $modCMakeText.Contains($requiredCMakeToken)) {
        throw "[PALCONTROL_NATIVE_GUARD] CMAKE_IDENTITY_BINDING_MISSING: CMakeLists.txt must contain $requiredCMakeToken"
    }
}

Write-Host "[guard-ok] UE4SS  $actualUe4ssCommit"
Write-Host "[guard-ok] Unreal $actualUnrealCommit"
Write-Host "[guard-ok] Source $ModRoot"
Write-Host "[guard-ok] Locked repository $($lock.ue4ss.repository)"
Write-Host "[guard-ok] Candidate $targetGameBuild / Steam $targetSteamBuild / Native $nativeModVersion (read-only)"
Write-Host "[guard-ok] PalServer bytes=$targetExecutableSize sha256=$targetExecutableSha256"
Write-Host "[guard-ok] Pipe $nativePipeName / service $controlApiServiceName ($controlApiServiceSid)"
Write-Host "[guard-ok] Cargo --locked / Corrosion $expectedCorrosionCommit / IconFont $expectedIconFontCommit"
Write-Host "[guard-ok] patternsleuth Cargo.lock bytes=$expectedPatternsleuthCargoLockSize sha256=$expectedPatternsleuthCargoLockSha256"

$cmakeExecutable = Resolve-CMakeExecutable -RequestedPath $CMakePath
$cmakeVersionText = Invoke-NativeToolText `
    -FilePath $cmakeExecutable `
    -ArgumentList @("--version") `
    -FailureCode "CMAKE_VERSION_UNREADABLE"
if ($cmakeVersionText -notmatch "cmake version\s+([0-9]+\.[0-9]+\.[0-9]+)") {
    throw "[PALCONTROL_NATIVE_GUARD] CMAKE_VERSION_UNREADABLE: $cmakeVersionText"
}
$actualCMakeVersion = $Matches[1]
$expectedCMakeVersion = ([string]$lock.build.cmake).Trim()
if ($actualCMakeVersion -ne $expectedCMakeVersion) {
    throw "[PALCONTROL_NATIVE_GUARD] CMAKE_VERSION_MISMATCH: expected $expectedCMakeVersion, actual $actualCMakeVersion."
}

$rustExecutable = Resolve-RustExecutable
$rustVersionText = Invoke-NativeToolText `
    -FilePath $rustExecutable `
    -ArgumentList @("--version") `
    -FailureCode "RUSTC_VERSION_UNREADABLE"
if ($rustVersionText -notmatch "^rustc\s+([^\s]+)") {
    throw "[PALCONTROL_NATIVE_GUARD] RUSTC_VERSION_UNREADABLE: $rustVersionText"
}
$actualRustVersion = $Matches[1]
$expectedRustVersion = ([string]$lock.build.rustc).Trim()
if ($actualRustVersion -ne $expectedRustVersion) {
    throw "[PALCONTROL_NATIVE_GUARD] RUSTC_VERSION_MISMATCH: expected $expectedRustVersion, actual $actualRustVersion."
}

$rustBin = Split-Path -Parent $rustExecutable
$cargoExecutable = Join-Path $rustBin "cargo.exe"
if (-not (Test-Path -LiteralPath $cargoExecutable -PathType Leaf)) {
    throw "[PALCONTROL_NATIVE_GUARD] CARGO_NOT_FOUND: expected $cargoExecutable"
}
if (-not (($env:PATH -split ";") -contains $rustBin)) {
    $env:PATH = "$rustBin;$env:PATH"
}

$msvcCompiler = Resolve-MsvcCompiler
$msvcFileVersion = (Get-Item -LiteralPath $msvcCompiler).VersionInfo.FileVersion
if ($msvcFileVersion -notmatch '^([0-9]+\.[0-9]+\.[0-9]+)') {
    throw "[PALCONTROL_NATIVE_GUARD] MSVC_VERSION_UNREADABLE: $msvcFileVersion"
}
$actualMsvcVersion = $Matches[1]
$expectedMsvcVersion = ([string]$lock.build.msvc).Trim()
if ($actualMsvcVersion -ne $expectedMsvcVersion) {
    throw "[PALCONTROL_NATIVE_GUARD] MSVC_VERSION_MISMATCH: expected $expectedMsvcVersion, actual $actualMsvcVersion."
}

Write-Host "[toolchain-ok] CMake $actualCMakeVersion"
Write-Host "[toolchain-ok] Rust  $actualRustVersion"
Write-Host "[toolchain-ok] MSVC  $actualMsvcVersion"
if ($GuardOnly) {
    return
}

if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = Join-Path $RepositoryRoot ".agent-build\native\pal-control-native-$($expectedUe4ssCommit.Substring(0, 12))"
}
$BuildDirectory = Get-AbsolutePath -Path $BuildDirectory -BasePath $RepositoryRoot

$sourcePrefix = $Ue4ssRoot.TrimEnd([char[]]@(92, 47))
$buildPrefix = $BuildDirectory.TrimEnd([char[]]@(92, 47))
if ($buildPrefix.Equals($sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    $buildPrefix.StartsWith($sourcePrefix + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "[PALCONTROL_NATIVE_GUARD] BUILD_DIRECTORY_NOT_INDEPENDENT: choose a directory outside $Ue4ssRoot"
}

$cachePath = Join-Path $BuildDirectory "CMakeCache.txt"
if (Test-Path -LiteralPath $cachePath -PathType Leaf) {
    $homeEntry = Select-String -LiteralPath $cachePath -Pattern "^CMAKE_HOME_DIRECTORY:INTERNAL=(.+)$" | Select-Object -First 1
    if ($null -eq $homeEntry) {
        throw "[PALCONTROL_NATIVE_GUARD] BUILD_CACHE_INVALID: CMAKE_HOME_DIRECTORY is missing from $cachePath"
    }
    $cachedSource = Get-AbsolutePath -Path $homeEntry.Matches[0].Groups[1].Value -BasePath $BuildDirectory
    if (-not $cachedSource.Equals($Ue4ssRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "[PALCONTROL_NATIVE_GUARD] BUILD_CACHE_SOURCE_MISMATCH: expected $Ue4ssRoot, cache uses $cachedSource"
    }
}

New-Item -ItemType Directory -Path $BuildDirectory -Force | Out-Null

Write-Host "[configure] $BuildDirectory"
Invoke-NativeTool `
    -FilePath $cmakeExecutable `
    -ArgumentList @(
        "-S", $Ue4ssRoot,
        "-B", $BuildDirectory,
        "-G", "Visual Studio 17 2022",
        "-A", "x64",
        "-DPALCONTROL_TARGET_GAME_BUILD=$targetGameBuild",
        "-DPALCONTROL_TARGET_STEAM_BUILD=$targetSteamBuild",
        "-DPALCONTROL_TARGET_EXECUTABLE_SHA256=$targetExecutableSha256",
        "-DPALCONTROL_TARGET_EXECUTABLE_SIZE=$targetExecutableSize",
        "-DPALCONTROL_NATIVE_MOD_VERSION=$nativeModVersion",
        "-DPALCONTROL_TARGET_UE4SS_RUNTIME_SHA256=$expectedRuntimeDllSha256",
        "-DPALCONTROL_TARGET_UE4SS_RUNTIME_SIZE=$expectedRuntimeDllSize",
        "-DPALCONTROL_PIPE_NAME=$nativePipeName",
        "-DPALCONTROL_CONTROL_API_SERVICE_SID=$controlApiServiceSid",
        "-DPALCONTROL_ENABLE_WRITE_CAPABILITIES=OFF",
        "-DPALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN=OFF") `
    -FailureCode "CMAKE_CONFIGURE_FAILED"

foreach ($fetchedDependency in @(
    @{
        Name = "Corrosion"
        Path = (Join-Path $BuildDirectory "_deps\corrosion-src")
        Commit = $expectedCorrosionCommit
    },
    @{
        Name = "IconFontCppHeaders"
        Path = (Join-Path $BuildDirectory "_deps\iconfontcppheaders-src")
        Commit = $expectedIconFontCommit
    }
)) {
    if (-not (Test-Path -LiteralPath $fetchedDependency.Path -PathType Container)) {
        throw "[PALCONTROL_NATIVE_GUARD] FETCHED_DEPENDENCY_NOT_FOUND: $($fetchedDependency.Name) at $($fetchedDependency.Path)"
    }
    $actualFetchedCommit = (Invoke-GitText `
        -Repository $fetchedDependency.Path `
        -ArgumentList @("rev-parse", "HEAD") `
        -FailureCode "FETCHED_DEPENDENCY_HEAD_UNREADABLE").ToLowerInvariant()
    if ($actualFetchedCommit -cne $fetchedDependency.Commit) {
        throw (
            "[PALCONTROL_NATIVE_GUARD] FETCHED_DEPENDENCY_HEAD_MISMATCH: " +
            "$($fetchedDependency.Name) expected $($fetchedDependency.Commit), " +
            "actual $actualFetchedCommit.")
    }
    Write-Host "[guard-ok] $($fetchedDependency.Name) $actualFetchedCommit"
}

Write-Host "[build] PalControlNative ($Configuration)"
Invoke-NativeTool `
    -FilePath $cmakeExecutable `
    -ArgumentList @("--build", $BuildDirectory, "--config", $Configuration, "--target", "PalControlNative", "--", "/m", "/verbosity:minimal") `
    -FailureCode "CMAKE_BUILD_FAILED"

$postBuildDependencyPaths = Invoke-GitText `
    -Repository $Ue4ssRoot `
    -ArgumentList @(
        "-c", "core.safecrlf=false", "diff", "--name-only",
        "--ignore-submodules=none", "HEAD", "--") `
    -FailureCode "UE4SS_POST_BUILD_STATUS_UNREADABLE"
$postBuildUnexpectedChanges = @($postBuildDependencyPaths -split "`r?`n" | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_) -and
    $allowedDependencyChanges -cnotcontains $_
})
if ($postBuildUnexpectedChanges.Count -ne 0 -or
    (& $normalizeText ([IO.File]::ReadAllText($cppModsList))) -cne
        (& $normalizeText $expectedCppModsText) -or
    (& $normalizeText ([IO.File]::ReadAllText($thirdPartyList))) -cne
        $expectedThirdPartyText -or
    (& $normalizeText ([IO.File]::ReadAllText($corrosionList))) -cne
        $expectedCorrosionText) {
    throw (
        "[PALCONTROL_NATIVE_GUARD] UE4SS_SOURCE_CHANGED_DURING_BUILD: the locked " +
        "tracked dependency tree changed; found " +
        ($postBuildUnexpectedChanges -join ", "))
}
Assert-FileIdentity `
    -Path $integratedCargoLock `
    -ExpectedSize $expectedPatternsleuthCargoLockSize `
    -ExpectedSha256 $expectedPatternsleuthCargoLockSha256 `
    -FailureCode "POST_BUILD_CARGO_LOCK"
Assert-ExactUntrackedSourceSet -Repository $Ue4ssRoot
Assert-SubmodulesClean -Repository $Ue4ssRoot

$artifactPath = Join-Path $BuildDirectory "$Configuration\bin\PalControlNative.dll"
if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
    throw "[PALCONTROL_NATIVE_BUILD] ARTIFACT_NOT_FOUND: $artifactPath"
}

$artifact = Get-Item -LiteralPath $artifactPath
$artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "[build-ok] $($artifact.FullName)"
Write-Host "[build-ok] bytes=$($artifact.Length) sha256=$artifactHash"
if ($artifact.Length -ne $expectedArtifactSize -or
    $artifactHash -cne $expectedArtifactSha256) {
    throw (
        "[PALCONTROL_NATIVE_BUILD] ARTIFACT_IDENTITY_MISMATCH: expected " +
        "$expectedArtifactSize bytes/$expectedArtifactSha256, actual " +
        "$($artifact.Length) bytes/$artifactHash. Do not deploy an unreviewed DLL.")
}
Write-Host "[build-ok] deterministic artifact size/SHA-256 match dependencies.lock.json"

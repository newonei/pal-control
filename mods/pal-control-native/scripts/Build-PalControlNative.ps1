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

if (-not (Test-Path -LiteralPath $LockPath -PathType Leaf)) {
    throw "[PALCONTROL_NATIVE_GUARD] LOCK_NOT_FOUND: $LockPath"
}

$lock = Get-Content -LiteralPath $LockPath -Raw -Encoding UTF8 | ConvertFrom-Json
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
Assert-IntegratedModSource -IntegratedRoot (Join-Path $Ue4ssRoot "cppmods\PalControlNative")

$modCMakeText = Get-Content -LiteralPath (Join-Path $ModRoot "CMakeLists.txt") -Raw
$expectedGameDefinition = 'PALCONTROL_TARGET_GAME_BUILD="' + ([string]$lock.palworldTarget) + '"'
if (-not $modCMakeText.Contains($expectedGameDefinition)) {
    throw "[PALCONTROL_NATIVE_GUARD] PALWORLD_TARGET_MISMATCH: CMakeLists.txt must define $expectedGameDefinition"
}

Write-Host "[guard-ok] UE4SS  $actualUe4ssCommit"
Write-Host "[guard-ok] Unreal $actualUnrealCommit"
Write-Host "[guard-ok] Source $ModRoot"
Write-Host "[guard-ok] Locked repository $($lock.ue4ss.repository)"

if ($GuardOnly) {
    return
}

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

Write-Host "[toolchain-ok] CMake $actualCMakeVersion"
Write-Host "[toolchain-ok] Rust  $actualRustVersion"
Write-Host "[configure] $BuildDirectory"
Invoke-NativeTool `
    -FilePath $cmakeExecutable `
    -ArgumentList @("-S", $Ue4ssRoot, "-B", $BuildDirectory, "-G", "Visual Studio 17 2022", "-A", "x64") `
    -FailureCode "CMAKE_CONFIGURE_FAILED"

Write-Host "[build] PalControlNative ($Configuration)"
Invoke-NativeTool `
    -FilePath $cmakeExecutable `
    -ArgumentList @("--build", $BuildDirectory, "--config", $Configuration, "--target", "PalControlNative", "--", "/m", "/verbosity:minimal") `
    -FailureCode "CMAKE_BUILD_FAILED"

$artifactPath = Join-Path $BuildDirectory "$Configuration\bin\PalControlNative.dll"
if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
    throw "[PALCONTROL_NATIVE_BUILD] ARTIFACT_NOT_FOUND: $artifactPath"
}

$artifact = Get-Item -LiteralPath $artifactPath
$artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
Write-Host "[build-ok] $($artifact.FullName)"
Write-Host "[build-ok] bytes=$($artifact.Length) sha256=$artifactHash"

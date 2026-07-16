[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = "0.1.0",
    [ValidatePattern('^[0-9a-fA-F]{40,64}$')]
    [string]$SourceRevision,
    [switch]$SkipRestore,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\release"))
$bundleRoot = Join-Path $artifactRoot "幻兽商域"
$publishRoot = $bundleRoot
$expectedPrefix = $repoRoot.TrimEnd('\') + "\artifacts\"

try {
    $gitHead = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
}
catch {
    throw "A clean Git worktree is required to produce release provenance."
}
if ($LASTEXITCODE -ne 0 -or $gitHead -notmatch '^[0-9a-fA-F]{40,64}$') {
    throw "A clean Git worktree is required to produce release provenance."
}
if ([string]::IsNullOrWhiteSpace($SourceRevision)) {
    $SourceRevision = if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) {
        $env:GITHUB_SHA.Trim()
    }
    else { $gitHead }
}
if ($SourceRevision -notmatch '^[0-9a-fA-F]{40,64}$') {
    throw "SourceRevision must be a 40-64 character hexadecimal commit identifier."
}
if (-not $SourceRevision.Equals($gitHead, [StringComparison]::OrdinalIgnoreCase)) {
    throw "SourceRevision does not match the checked-out Git HEAD."
}
$gitStatus = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all 2>$null)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to determine whether the release worktree is clean."
}
$sourceDirty = $gitStatus.Count -gt 0
$releaseId = "$($Version.Replace('+', '-'))-$($SourceRevision.Substring(0, 12).ToLowerInvariant())"
if ($releaseId -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{2,100}$') {
    throw "Version produces an invalid or overly long production release id."
}

if (-not $artifactRoot.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a release path outside the repository artifacts directory: $artifactRoot"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments
    )
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory)] [string]$BasePath,
        [Parameter(Mandatory)] [string]$Path
    )
    $baseFull = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $baseUri = New-Object System.Uri($baseFull)
    $pathUri = New-Object System.Uri($pathFull)
    [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString())
}

Write-Host "[1/7] Preparing release directory..."
if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        Write-Host "[2/7] Restoring frontend and backend dependencies..."
        Invoke-Checked -FilePath "npm.cmd" -Arguments @("ci")
        Invoke-Checked -FilePath "dotnet" -Arguments @(
            "restore",
            ".\services\control-api\PalControl.ControlApi.csproj",
            "--runtime", "win-x64")
    }
    else {
        Write-Host "[2/7] Dependency restore skipped."
    }

    Write-Host "[3/7] Building operator console and player portal..."
    Invoke-Checked -FilePath "npm.cmd" -Arguments @("run", "build")

    Write-Host "[4/7] Publishing self-contained Windows x64 backend..."
    $publishArguments = @(
        "publish",
        ".\services\control-api\PalControl.ControlApi.csproj",
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $publishRoot,
        "/p:DebugType=None",
        "/p:DebugSymbols=false"
    )
    if ($SkipRestore) { $publishArguments += "--no-restore" }
    Invoke-Checked -FilePath "dotnet" -Arguments $publishArguments

    Write-Host "[5/7] Assembling the no-SDK portable bundle..."
    $webRoot = Join-Path $publishRoot "wwwroot"
    New-Item -ItemType Directory -Path $webRoot -Force | Out-Null
    Copy-Item -Path ".\apps\console-web\dist\*" -Destination $webRoot -Recurse -Force
    $playerWebRoot = Join-Path $webRoot "player"
    New-Item -ItemType Directory -Path $playerWebRoot -Force | Out-Null
    Copy-Item -Path ".\apps\player-web\dist\*" -Destination $playerWebRoot -Recurse -Force

    Copy-Item -Path ".\deploy\windows\package\*" -Destination $bundleRoot -Recurse -Force
    Copy-Item -LiteralPath ".\docs\安装使用说明.html" -Destination (Join-Path $bundleRoot "安装使用说明.html") -Force
    Copy-Item -LiteralPath ".\docs\安装使用说明.html" -Destination (Join-Path $artifactRoot "安装使用说明.html") -Force
    Copy-Item -LiteralPath ".\LICENSE" -Destination (Join-Path $bundleRoot "LICENSE.txt") -Force
    Copy-Item -LiteralPath ".\THIRD_PARTY_NOTICES.md" -Destination (Join-Path $bundleRoot "THIRD_PARTY_NOTICES.md") -Force

    Write-Host "Collecting and verifying binary dependency licenses..."
    & ".\deploy\windows\collect-release-notices.ps1" -RepoRoot $repoRoot -PublishRoot $bundleRoot

    # Native is intentionally never auto-discovered from ignored local artifacts.
    # The current inventory.consume capability is experimental and has not passed
    # save/stop/restart/re-login persistence acceptance. A future signed release
    # workflow must require an explicit artifact, expected SHA-256, version evidence,
    # and acceptance record instead of silently copying whatever happens to exist.
    $localNativeArtifact = ".\artifacts\palworld-workshop\PalControlNative\dlls\main.dll"
    if (Test-Path -LiteralPath $localNativeArtifact) {
        Write-Host "Experimental local Native artifact detected and intentionally excluded."
    }

    $forbiddenReleasePaths = @(
        (Join-Path $bundleRoot "Resources\palworld-resource-catalog.json"),
        (Join-Path $bundleRoot "appsettings.Local.json"),
        (Join-Path $bundleRoot "可选组件\PalControlNative\dlls\main.dll")
    )
    foreach ($forbiddenPath in $forbiddenReleasePaths) {
        if (Test-Path -LiteralPath $forbiddenPath) {
            throw "Release assembly contains a publication-blocked file: $forbiddenPath"
        }
    }
    $forbiddenRuntimeFiles = @(Get-ChildItem -LiteralPath $bundleRoot -Recurse -File | Where-Object {
        $_.Extension -in ".db", ".sqlite", ".sqlite3", ".sav", ".jsonl", ".pfx", ".p12", ".pem", ".key" -or
        $_.Name -in "PalWorldSettings.ini", "PalModSettings.ini", "BanList.txt"
    })
    if ($forbiddenRuntimeFiles.Count -ne 0) {
        throw "Release assembly contains runtime data, a save, or a credential file: $($forbiddenRuntimeFiles[0].FullName)"
    }

    $versionInfo = [ordered]@{
        product = "幻兽商域"
        version = $Version
        sourceRevision = $SourceRevision.ToLowerInvariant()
        sourceDirty = $sourceDirty
        platform = "win-x64"
        builtAtUtc = [DateTime]::UtcNow.ToString("o")
        selfContainedDotNet = $true
        nativeIncluded = $false
        resourceCatalogIncluded = $false
        urls = [ordered]@{
            admin = "http://127.0.0.1:5180/"
            player = "http://127.0.0.1:5180/player/"
        }
    } | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText(
        (Join-Path $bundleRoot "版本信息.json"),
        $versionInfo + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))

    $releaseFiles = @(Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = (Get-RelativePath -BasePath $bundleRoot -Path $_.FullName).Replace('\', '/')
            $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
            [ordered]@{
                path = $relativePath
                bytes = $_.Length
                sha256 = $hash.Hash.ToLowerInvariant()
            }
        })
    $releaseManifest = [ordered]@{
        schemaVersion = 1
        product = "PalControl"
        version = $Version
        releaseId = $releaseId
        sourceRevision = $SourceRevision.ToLowerInvariant()
        sourceDirty = $sourceDirty
        platform = "win-x64"
        executable = "PalControl.ControlApi.exe"
        dataContract = [ordered]@{
            provider = "sqlite"
            version = 1
            migrationMode = "startup-idempotent"
            rollbackPolicy = "same-contract-only"
        }
        files = $releaseFiles
    } | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText(
        (Join-Path $bundleRoot "release-manifest.json"),
        $releaseManifest + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "[6/7] Creating portable ZIP..."
    $zipPath = Join-Path $artifactRoot "幻兽商域-Portable-$Version-win-x64.zip"
    Compress-Archive -Path $bundleRoot -DestinationPath $zipPath -CompressionLevel Optimal -Force

    if (-not $SkipInstaller) {
        $iscc = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
        if (-not $iscc) {
            $isccCandidates = @(
                (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
                (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
                (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
            )
            foreach ($candidate in $isccCandidates) {
                if (Test-Path -LiteralPath $candidate) {
                    $iscc = Get-Item -LiteralPath $candidate
                    break
                }
            }
        }
        if (-not $iscc) {
            throw "Inno Setup 6 was not found. Install JRSoftware.InnoSetup or pass -SkipInstaller."
        }
        Write-Host "[7/7] Compiling Windows installer..."
        Invoke-Checked -FilePath $iscc.FullName -Arguments @(
            "/DMyAppVersion=$Version",
            "/DMySourceDir=$bundleRoot",
            "/DMyOutputDir=$artifactRoot",
            ".\deploy\windows\installer.iss")
    }
    else {
        Write-Host "[7/7] Installer compilation skipped."
    }

    $deliverables = Get-ChildItem -LiteralPath $artifactRoot -File |
        Where-Object { $_.Extension -in ".zip", ".exe" } |
        Sort-Object Name
    $checksums = foreach ($file in $deliverables) {
        $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $($file.Name)"
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $artifactRoot "SHA256SUMS.txt"),
        $checksums,
        (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "Release completed: $artifactRoot"
    $deliverables | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
}
finally {
    Pop-Location
}

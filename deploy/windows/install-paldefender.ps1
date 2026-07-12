[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\PalServerRuntime",
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,
    [string]$ExpectedD3D9Sha256 = "8638fef6628d8c4c221696739d1ccf55cbf2d1ca02111e35dbb707f792325f21",
    [string]$ExpectedPalDefenderSha256 = "a88f4dfa056c2e4b1201d9a50ab0f74b13065257c4406bcfa42f97e2c60a3057"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (Get-Process PalServer, PalServer-Win64-Shipping-Cmd -ErrorAction SilentlyContinue) {
    throw "Stop PalServer before installing or upgrading PalDefender."
}

$resolvedRoot = (Resolve-Path -LiteralPath $InstallRoot).Path
$resolvedRelease = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$win64 = Join-Path $resolvedRoot "Pal\Binaries\Win64"
if (-not (Test-Path -LiteralPath (Join-Path $resolvedRoot "PalServer.exe")) -or
    -not (Test-Path -LiteralPath (Join-Path $win64 "PalServer-Win64-Shipping-Cmd.exe"))) {
    throw "InstallRoot is not a Palworld dedicated-server root: $resolvedRoot"
}

$expected = @{
    "d3d9.dll" = $ExpectedD3D9Sha256.ToLowerInvariant()
    "PalDefender.dll" = $ExpectedPalDefenderSha256.ToLowerInvariant()
}
foreach ($name in $expected.Keys) {
    $source = Join-Path $resolvedRelease $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Release asset is missing: $source"
    }
    $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected[$name]) {
        throw "Official digest mismatch for $name. Expected $($expected[$name]), got $actual."
    }
}

$existing = @($expected.Keys | Where-Object { Test-Path -LiteralPath (Join-Path $win64 $_) })
$backupRoot = $null
if ($existing.Count -gt 0) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $backupRoot = Join-Path $repoRoot "backups\native-overlay\paldefender-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    foreach ($name in $existing) {
        Copy-Item -LiteralPath (Join-Path $win64 $name) -Destination $backupRoot
    }
}

foreach ($name in $expected.Keys) {
    $source = Join-Path $resolvedRelease $name
    $destination = Join-Path $win64 $name
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $copied = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($copied -ne $expected[$name]) {
        throw "Post-copy digest mismatch for $destination."
    }
}

[pscustomobject]@{
    Installed = $true
    InstallRoot = $resolvedRoot
    ReleaseDirectory = $resolvedRelease
    BackupRoot = $backupRoot
    D3D9Sha256 = $expected["d3d9.dll"]
    PalDefenderSha256 = $expected["PalDefender.dll"]
}

[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\PalServerRuntime",
    [string]$ExpectedSteamBuildId = "24088465",
    [string]$ExpectedD3D9Sha256 = "8638fef6628d8c4c221696739d1ccf55cbf2d1ca02111e35dbb707f792325f21",
    [string]$ExpectedPalDefenderSha256 = "a88f4dfa056c2e4b1201d9a50ab0f74b13065257c4406bcfa42f97e2c60a3057",
    [string[]]$ServerArguments = @("-log", "-publiclobby", "-players=128")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (Get-Process PalServer, PalServer-Win64-Shipping-Cmd -ErrorAction SilentlyContinue) {
    throw "PalServer is already running."
}

$root = (Resolve-Path -LiteralPath $InstallRoot).Path
$serverExecutable = Join-Path $root "PalServer.exe"
$manifestPath = Join-Path $root "steamapps\appmanifest_2394010.acf"
$d3d9Path = Join-Path $root "Pal\Binaries\Win64\d3d9.dll"
$palDefenderPath = Join-Path $root "Pal\Binaries\Win64\PalDefender.dll"
foreach ($required in $serverExecutable, $manifestPath, $d3d9Path, $palDefenderPath) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required guarded-start file is missing: $required"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw
$match = [regex]::Match($manifest, '"buildid"\s+"(?<id>\d+)"')
if (-not $match.Success) {
    throw "Steam buildid could not be read from $manifestPath"
}
$actualBuildId = $match.Groups['id'].Value
if ($actualBuildId -ne $ExpectedSteamBuildId) {
    throw "Unverified Palworld build $actualBuildId. Expected $ExpectedSteamBuildId. Review PalDefender, UE4SS, and PalControl compatibility before starting."
}

$actualD3D9 = (Get-FileHash -LiteralPath $d3d9Path -Algorithm SHA256).Hash.ToLowerInvariant()
$actualPalDefender = (Get-FileHash -LiteralPath $palDefenderPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualD3D9 -ne $ExpectedD3D9Sha256.ToLowerInvariant()) {
    throw "Installed d3d9.dll does not match the reviewed PalDefender digest."
}
if ($actualPalDefender -ne $ExpectedPalDefenderSha256.ToLowerInvariant()) {
    throw "Installed PalDefender.dll does not match the reviewed digest."
}

$startParameters = @{
    FilePath = $serverExecutable
    ArgumentList = $ServerArguments
    WorkingDirectory = $root
    WindowStyle = "Hidden"
    PassThru = $true
}
$process = Start-Process @startParameters

[pscustomobject]@{
    Started = $true
    ProcessId = $process.Id
    SteamBuildId = $actualBuildId
    D3D9Sha256 = $actualD3D9
    PalDefenderSha256 = $actualPalDefender
}

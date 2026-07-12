param(
    [string] $PalServerRoot = "C:\PalServerRuntime"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Join-Path $repositoryRoot `
    "third_party\RE-UE4SS-Palworld-c2ac246\build-palcontrol\Game__Shipping__Win64\bin\PalControlNative.dll"
$target = Join-Path $PalServerRoot `
    "Pal\Binaries\Win64\ue4ss\Mods\PalControlNative\dlls\main.dll"
$expectedSha256 = "C216CDFDF57D5ED5C5E609FCB2F59818F3C9397001286B27878E626046EC4E5C"

$runningServer = Get-Process -ErrorAction SilentlyContinue |
    Where-Object ProcessName -In @("PalServer", "PalServer-Win64-Shipping-Cmd")
if ($runningServer) {
    throw "PalServer is still running. Stop it through the normal save/shutdown flow before running this script. The script will not disconnect players."
}
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "The compiled Native Mod was not found: $source"
}
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
    throw "The installed Native Mod was not found: $target"
}

$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ($sourceHash -ne $expectedSha256) {
    throw "Native Mod checksum mismatch. Expected $expectedSha256, got $sourceHash."
}

$backupDirectory = Join-Path $repositoryRoot "backups\native-overlay"
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
$backup = Join-Path $backupDirectory `
    ("main-{0}.dll" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
Copy-Item -LiteralPath $target -Destination $backup
Copy-Item -LiteralPath $source -Destination $target -Force

$installedHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
if ($installedHash -ne $expectedSha256) {
    throw "Installed Native Mod checksum mismatch. The backup remains at: $backup"
}

[pscustomobject]@{
    installed = $target
    backup = $backup
    sha256 = $installedHash
    nextStep = "Start PalServer; Control API should then see overlay, top-banner, and native notification write capabilities in hello."
} | Format-List

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MatrixPath,
    [Parameter(Mandatory = $true)]
    [string]$CombinationId,
    [string]$ExpectedSha256 = "",
    [switch]$RequireStable,
    [string]$GameVersion = "",
    [string]$SteamBuild = "",
    [string]$PalDefenderVersion = "",
    [string]$Ue4ssCommit = "",
    [string]$NativeProtocol = "",
    [string]$NativeMod = "",
    [string]$BridgeAvailability = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tools\compatibility-guard\PalControl.CompatibilityGuard.csproj"
$arguments = @(
    "run",
    "--project", $project,
    "--configuration", "Release",
    "--",
    "--matrix", (Resolve-Path -LiteralPath $MatrixPath).Path,
    "--combination", $CombinationId
)
if ($RequireStable) { $arguments += "--require-stable" }
if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
    $arguments += @("--expected-sha256", $ExpectedSha256)
}
$observed = [ordered]@{
    "game-version" = $GameVersion
    "steam-build" = $SteamBuild
    "paldefender-version" = $PalDefenderVersion
    "ue4ss-commit" = $Ue4ssCommit
    "native-protocol" = $NativeProtocol
    "native-mod" = $NativeMod
    "bridge-availability" = $BridgeAvailability
}
foreach ($entry in $observed.GetEnumerator()) {
    if (-not [string]::IsNullOrWhiteSpace([string]$entry.Value)) {
        $arguments += @("--$($entry.Key)", [string]$entry.Value)
    }
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Compatibility matrix guard rejected the requested combination."
}

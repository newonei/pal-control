param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot "tests\zone-calibration\PalControl.ZoneCalibration.Tests.csproj"

& dotnet run --project $project --configuration Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Zone calibration signed-evidence smoke failed: $LASTEXITCODE"
}

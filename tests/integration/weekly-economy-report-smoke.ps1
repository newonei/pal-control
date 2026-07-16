$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tests\weekly-economy-report\PalControl.WeeklyEconomyReport.Harness.csproj"

& dotnet run --project $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Weekly economy report harness failed with exit code $LASTEXITCODE."
}

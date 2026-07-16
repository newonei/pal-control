$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tests\team-economy-capacity\PalControl.TeamEconomy.CapacityHarness.csproj"

& dotnet run --project $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Team economy capacity harness failed with exit code $LASTEXITCODE."
}

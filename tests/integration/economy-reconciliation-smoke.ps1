$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tests\economy-reconciliation\PalControl.EconomyReconciliation.Harness.csproj"

& dotnet run --project $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Economy reconciliation harness failed with exit code $LASTEXITCODE."
}

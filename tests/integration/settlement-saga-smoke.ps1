[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot `
    "tests\settlement-saga\PalControl.SettlementSagaHarness.csproj"

dotnet run --project $projectPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Settlement saga harness failed with exit code $LASTEXITCODE."
}

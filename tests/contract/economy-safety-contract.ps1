[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root "tests\economy-safety\PalControl.EconomySafety.ContractTests.csproj"

dotnet run --project $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Economy safety-gate contract harness failed with exit code $LASTEXITCODE."
}

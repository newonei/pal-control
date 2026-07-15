[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$env:PlayerPortal__Enabled = "false"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot `
    "tests\content-projection-atomicity\PalControl.ContentProjectionAtomicity.Harness.csproj"

dotnet run --project $projectPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Atomic content product projection harness failed with exit code $LASTEXITCODE."
}

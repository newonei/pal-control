[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$env:PlayerPortal__Enabled = "false"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot `
    "tests\run-store-capacity\PalControl.RunStoreCapacity.Harness.csproj"

dotnet run --project $projectPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Run-store capacity harness failed with exit code $LASTEXITCODE."
}

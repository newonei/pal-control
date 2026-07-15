[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$env:PlayerPortal__Enabled = "false"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot `
    "tests\identity-binding\PalControl.IdentityBindingHarness.csproj"

dotnet run --project $projectPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Identity binding harness failed with exit code $LASTEXITCODE."
}

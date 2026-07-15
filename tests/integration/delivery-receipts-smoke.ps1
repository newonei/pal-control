[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$env:PlayerPortal__Enabled = "false"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot `
    "tests\delivery-receipts\PalControl.DeliveryReceiptsHarness.csproj"

dotnet run --project $projectPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Delivery receipt harness failed with exit code $LASTEXITCODE."
}

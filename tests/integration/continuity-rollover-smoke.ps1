[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$env:PlayerPortal__Enabled = "false"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot `
    "tests\continuity-rollover\PalControl.ContinuityRolloverHarness.csproj"

dotnet run --project $projectPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Continuity/rollover harness failed with exit code $LASTEXITCODE."
}

$clientTests = Join-Path $repositoryRoot `
    "tests\rollover-client\Invoke-WeeklyRollover.ClientTests.ps1"
& powershell -NoProfile -ExecutionPolicy Bypass -File $clientTests
if ($LASTEXITCODE -ne 0) {
    throw "Controlled rollover client tests failed with exit code $LASTEXITCODE."
}

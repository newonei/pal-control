[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot `
    "tests\reliable-tasks\PalControl.ReliableTasksHarness.csproj"

dotnet run --project $projectPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Reliable task harness failed with exit code $LASTEXITCODE."
}

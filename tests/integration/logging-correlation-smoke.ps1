[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot `
    "tests\logging-audit\PalControl.LoggingAudit.Harness.csproj"

dotnet run --project $projectPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Logging correlation and redaction audit failed with exit code $LASTEXITCODE."
}

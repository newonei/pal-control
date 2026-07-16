$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tests\admin-operation-keys\PalControl.AdminOperationKeys.Harness.csproj"

& dotnet run --project $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Administrator operation-key integration harness failed with exit code $LASTEXITCODE."
}

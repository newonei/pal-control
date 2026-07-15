$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tests\admin-security\PalControl.AdminSecurity.ContractTests.csproj"

& dotnet run --project $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Admin security contract harness failed with exit code $LASTEXITCODE."
}

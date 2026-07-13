$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot `
    "extraction-mode-options\PalControl.ExtractionModeOptions.ContractTests.csproj"

& dotnet run --project $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "ExtractionModeOptions contract harness failed with exit code $LASTEXITCODE."
}

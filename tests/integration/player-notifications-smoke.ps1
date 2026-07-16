$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tests\player-notifications\PalControl.PlayerNotifications.Harness.csproj"

& dotnet run --project $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Player notification smoke harness failed with exit code $LASTEXITCODE."
}

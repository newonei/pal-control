$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$apiPath = Join-Path $repositoryRoot "services\control-api"
$webPath = Join-Path $repositoryRoot "apps\console-web"

Write-Host "Starting Pal Control API at http://127.0.0.1:5180"
Start-Process -FilePath "dotnet" -ArgumentList "run" -WorkingDirectory $apiPath -WindowStyle Hidden

Write-Host "Starting Console Web at http://127.0.0.1:5173"
Start-Process -FilePath "npm.cmd" -ArgumentList "run", "dev" -WorkingDirectory $webPath -WindowStyle Hidden

Write-Host "Development services started. Stop the dotnet and node processes when finished."

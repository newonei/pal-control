[CmdletBinding()]
param(
    [ValidateSet("All", "Contract", "Integration")]
    [string] $Suite = "All"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$hostExecutable = (Get-Process -Id $PID).Path
$contractTests = @(
    "tests\contract\resource-economy-contract.ps1",
    "tests\contract\extraction-mode-options-contract.ps1"
)
$integrationTests = @(
    "tests\integration\settlement-saga-smoke.ps1",
    "tests\integration\control-api-boundary-smoke.ps1",
    "tests\integration\announcement-publish-smoke.ps1",
    "tests\integration\in-game-notification-smoke.ps1",
    "tests\integration\live-map-smoke.ps1",
    "tests\integration\save-backup-smoke.ps1"
)
$selectedTests = switch ($Suite) {
    "Contract" { $contractTests }
    "Integration" { $integrationTests }
    default { $contractTests + $integrationTests }
}

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
foreach ($relativePath in $selectedTests) {
    $testPath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $testPath -PathType Leaf)) {
        throw "Test script not found: $relativePath"
    }

    Write-Host "==> $relativePath"
    & $hostExecutable -NoLogo -NoProfile -NonInteractive `
        -ExecutionPolicy Bypass -File $testPath
    if ($LASTEXITCODE -ne 0) {
        throw "Test failed with exit code ${LASTEXITCODE}: $relativePath"
    }
}
$stopwatch.Stop()

Write-Host (
    "PASS: {0} test script(s) completed in {1:n1}s ({2})." -f `
        $selectedTests.Count,
        $stopwatch.Elapsed.TotalSeconds,
        $Suite)

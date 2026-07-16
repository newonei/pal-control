[CmdletBinding()]
param(
    [ValidateSet("All", "Contract", "Integration")]
    [string] $Suite = "All"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "integration\helpers\test-api-environment.ps1")

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$hostExecutable = (Get-Process -Id $PID).Path
$contractTests = @(
    "tests\contract\resource-economy-contract.ps1",
    "tests\contract\native-settlement-contract.ps1",
    "tests\contract\extraction-mode-options-contract.ps1",
    "tests\contract\admin-security-contract.ps1",
    "tests\contract\economy-safety-contract.ps1",
    "tests\contract\startup-security-contract.ps1",
    "tests\contract\player-identity-security-contract.ps1",
    "tests\contract\steam-openid-contract.ps1",
    "tests\contract\new-player-activity-contract.ps1",
    "tests\contract\economy-observability-contract.ps1",
    "tests\contract\economy-analytics-contract.ps1",
    "tests\contract\federation-contract.ps1",
    "tests\contract\team-economy-contract.ps1",
    "tests\contract\windows-configure-preservation-contract.ps1",
    "tests\contract\soak-observability-contract.ps1"
)
$integrationTests = @(
    "tests\integration\settlement-saga-smoke.ps1",
    "tests\integration\native-settlement-smoke.ps1",
    "tests\integration\selective-resource-sale-smoke.ps1",
    "tests\integration\delivery-receipts-smoke.ps1",
    "tests\integration\economy-invariants-smoke.ps1",
    "tests\integration\content-definitions-smoke.ps1",
    "tests\integration\content-projection-atomicity-smoke.ps1",
    "tests\integration\economy-balance-guard-smoke.ps1",
    "tests\integration\permanent-currency-contract-smoke.ps1",
    "tests\integration\reliable-tasks-smoke.ps1",
    "tests\integration\season-leaderboard-smoke.ps1",
    "tests\integration\player-notifications-smoke.ps1",
    "tests\integration\team-economy-smoke.ps1",
    "tests\integration\team-economy-capacity-smoke.ps1",
    "tests\integration\identity-binding-smoke.ps1",
    "tests\integration\admin-auth-smoke.ps1",
    "tests\integration\admin-operation-keys-smoke.ps1",
    "tests\integration\control-api-boundary-smoke.ps1",
    "tests\integration\player-economy-security-smoke.ps1",
    "tests\integration\announcement-publish-smoke.ps1",
    "tests\integration\in-game-notification-smoke.ps1",
    "tests\integration\live-map-smoke.ps1",
    "tests\integration\save-backup-smoke.ps1",
    "tests\integration\continuity-rollover-smoke.ps1",
    "tests\integration\new-player-activity-smoke.ps1",
    "tests\integration\economy-observability-smoke.ps1",
    "tests\integration\logging-correlation-smoke.ps1",
    "tests\integration\economy-reconciliation-smoke.ps1",
    "tests\integration\economy-analytics-smoke.ps1",
    "tests\integration\weekly-economy-report-smoke.ps1",
    "tests\integration\federation-smoke.ps1",
    "tests\integration\run-store-capacity-smoke.ps1",
    "tests\integration\soak-runner-smoke.ps1",
    "tests\integration\acceptance-evidence-smoke.ps1",
    "tests\integration\zone-calibration-smoke.ps1",
    "tests\integration\world-restore-smoke.ps1",
    "tests\integration\windows-production-deployment-smoke.ps1"
)
$selectedTests = switch ($Suite) {
    "Contract" { $contractTests }
    "Integration" { $integrationTests }
    default { $contractTests + $integrationTests }
}

# Keep `npm test` and direct suite runs reproducible in a completely clean
# clone. Harness scripts may skip a second restore after this bootstrap, but
# they must still build their own executable instead of relying on stale bin/
# output or the separate CI pre-build step.
$dotnetProjects = @(
    Join-Path $repositoryRoot "services\control-api\PalControl.ControlApi.csproj"
) + @(
    Get-ChildItem `
        -Path (Join-Path $repositoryRoot "tests"), (Join-Path $repositoryRoot "tools") `
        -Recurse `
        -Filter *.csproj |
        Sort-Object FullName |
        ForEach-Object FullName
)
$dotnetProjects = @($dotnetProjects | Sort-Object -Unique)
Write-Host "Restoring $($dotnetProjects.Count) .NET test/build project(s)..."
foreach ($project in $dotnetProjects) {
    & dotnet restore $project --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code ${LASTEXITCODE}: $project"
    }
}

$nonSelfContainedTests = @($selectedTests | Where-Object {
    Select-String `
        -LiteralPath (Join-Path $repositoryRoot $_) `
        -SimpleMatch "--no-build" `
        -Quiet
})
if ($nonSelfContainedTests.Count -gt 0) {
    throw (
        "Clean-clone test scripts must not rely on pre-existing build output: " +
        ($nonSelfContainedTests -join ", "))
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

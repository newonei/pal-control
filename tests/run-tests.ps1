[CmdletBinding()]
param(
    [ValidateSet("All", "Contract", "Integration")]
    [string] $Suite = "All"
)

$ErrorActionPreference = "Stop"

# Do not let an ignored developer appsettings.Local.json accidentally enable a
# production player portal in unrelated smoke processes. The dedicated player
# economy smoke explicitly overrides this on its command line in Development.
$env:PlayerPortal__Enabled = "false"

# Integration APIs require an authenticated, attributable administrator. The
# test-only loopback principal is explicit, process-scoped, and rejected unless
# Security:DevelopmentMode is true. Production examples never enable it.
$env:Security__AdminAuthentication__Enabled = "true"
$env:Security__AdminAuthentication__EnableLoopbackDevelopmentPrincipal = "true"
$env:Security__AdminAuthentication__DevelopmentPrincipalSubject = "integration-test"
$env:Security__AdminAuthentication__Principals__0__Subject = "integration-test"
$env:Security__AdminAuthentication__Principals__0__ApiKeySha256 = `
    "87052f5138109134ec8e8b25a5e18545e39c90244679e52b6c40c364cb671060"
$env:Security__AdminAuthentication__Principals__0__Roles__0 = "Owner"
$env:Security__AdminAuthentication__Principals__0__TotpSecretBase32 = `
    "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"

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
    "tests\contract\windows-configure-preservation-contract.ps1"
)
$integrationTests = @(
    "tests\integration\settlement-saga-smoke.ps1",
    "tests\integration\native-settlement-smoke.ps1",
    "tests\integration\delivery-receipts-smoke.ps1",
    "tests\integration\economy-invariants-smoke.ps1",
    "tests\integration\identity-binding-smoke.ps1",
    "tests\integration\admin-auth-smoke.ps1",
    "tests\integration\control-api-boundary-smoke.ps1",
    "tests\integration\player-economy-security-smoke.ps1",
    "tests\integration\announcement-publish-smoke.ps1",
    "tests\integration\in-game-notification-smoke.ps1",
    "tests\integration\live-map-smoke.ps1",
    "tests\integration\save-backup-smoke.ps1",
    "tests\integration\continuity-rollover-smoke.ps1",
    "tests\integration\new-player-activity-smoke.ps1",
    "tests\integration\economy-observability-smoke.ps1"
)
$selectedTests = switch ($Suite) {
    "Contract" { $contractTests }
    "Integration" { $integrationTests }
    default { $contractTests + $integrationTests }
}

# Keep `npm test` and direct suite runs reproducible in a completely clean
# clone. Several harnesses intentionally build with --no-restore after this
# single bootstrap, while CI also builds every project independently.
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

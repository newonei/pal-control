[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tests\player-identity-security\PalControl.PlayerIdentitySecurity.ContractTests.csproj"
$buildPath = Join-Path $repositoryRoot ".agent-build\player-identity-security-tests\bin\"
$openApiPath = Join-Path $repositoryRoot "packages\contracts\openapi\control-api.yaml"
$openApi = Get-Content -LiteralPath $openApiPath -Raw -Encoding utf8
foreach ($requiredFragment in @(
        "/admin/player-identity-security-audit:",
        "/admin/player-sessions/revoke:",
        "PlayerIdentitySecurityAuditEvent:",
        "administrative_session_revocation")) {
    if ($openApi.IndexOf($requiredFragment, [StringComparison]::Ordinal) -lt 0) {
        throw "OpenAPI is missing player identity security contract fragment '$requiredFragment'."
    }
}

dotnet run --no-restore --project $project -c Release --nologo `
    --property:UseAppHost=false --property:BaseOutputPath=$buildPath
if ($LASTEXITCODE -ne 0) {
    throw "Player identity security contract failed with exit code $LASTEXITCODE."
}

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tests\steam-openid\PalControl.SteamOpenId.ContractTests.csproj"
$buildPath = Join-Path $repositoryRoot ".agent-build\steam-openid-tests\bin\"
$openApi = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "packages\contracts\openapi\control-api.yaml") `
    -Raw -Encoding utf8
$authenticationSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot `
        "services\control-api\Infrastructure\PlayerPortalAuthenticationService.cs") `
    -Raw -Encoding utf8

foreach ($fragment in @(
        "/player/auth/steam/start:",
        "/player/auth/steam/callback:",
        "PlayerAuthenticationMode:",
        "check_authentication",
        "current worldId + PlayerUID")) {
    if ($openApi.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "OpenAPI is missing Steam OpenID contract fragment '$fragment'."
    }
}
foreach ($fragment in @(
        "GetAccountContextAsync(",
        "requireOnline: true",
        "binding.PlayerUid",
        "binding.WorldId",
        "CreateSessionIfAllowed(")) {
    if ($authenticationSource.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "Player session creation no longer proves current-world binding fragment '$fragment'."
    }
}

dotnet run --project $project -c Release --nologo `
    --property:UseAppHost=false --property:BaseOutputPath=$buildPath
if ($LASTEXITCODE -ne 0) {
    throw "Steam OpenID contract/E2E failed with exit code $LASTEXITCODE."
}

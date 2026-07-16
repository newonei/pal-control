$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Read-RepositoryFile([string]$relativePath) {
    $path = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required federation file is missing: $relativePath"
    }
    return [IO.File]::ReadAllText($path)
}

function Assert-Contains(
    [string]$content,
    [string]$fragment,
    [string]$message) {
    if ($content.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw $message
    }
}

$program = Read-RepositoryFile "services/control-api/Program.cs"
$profiles = Read-RepositoryFile `
    "services/control-api/Infrastructure/FederationProfiles.cs"
$client = Read-RepositoryFile `
    "services/control-api/Infrastructure/FederationNodeClient.cs"
$endpoints = Read-RepositoryFile `
    "services/control-api/Infrastructure/FederationEndpoints.cs"
$options = Read-RepositoryFile `
    "services/control-api/Infrastructure/FederationOptions.cs"
$matrix = Read-RepositoryFile `
    "services/control-api/Compatibility/compatibility-matrix.v1.json"
$schema = Read-RepositoryFile `
    "services/control-api/Compatibility/compatibility-matrix.v1.schema.json"
$openApi = Read-RepositoryFile "packages/contracts/openapi/control-api.yaml"

Assert-Contains $program 'api.MapFederationEndpoints();' `
    "Federation endpoints are not mapped."
Assert-Contains $program 'AllowAutoRedirect = false' `
    "Federation HTTP redirects are not disabled."
Assert-Contains $program '!path.StartsWithSegments("/api/v1/internal/federation")' `
    "The purpose-built server-to-server route is still trapped behind the loopback operator boundary."
Assert-Contains $profiles 'HMACSHA256.HashData' `
    "Federation identities are not derived with HMAC-SHA-256."
Assert-Contains $profiles 'ListAccountsAsync' `
    "Remote subject resolution does not use the local authoritative account repository."
Assert-Contains $client 'MaximumResponseBytes' `
    "Federation response bodies are not bounded."
Assert-Contains $client 'FEDERATION_REDIRECT_REJECTED' `
    "Federation redirect rejection is missing."
Assert-Contains $endpoints 'RejectIdentityOverrides' `
    "Player federation does not reject identity overrides."
Assert-Contains $options 'Production federation must pin ExpectedMatrixSha256' `
    "Production matrix pinning is not enforced."
Assert-Contains $options 'RequireProductionStable' `
    "Production federation can admit non-stable combinations."

Assert-Contains $matrix '"gameVersion": "v1.0.0.100427"' `
    "The experimental Native target is missing from the matrix."
Assert-Contains $matrix '"status": "experimental"' `
    "The old target is incorrectly presented as stable."
Assert-Contains $matrix '"gameVersion": "v1.0.1.100619"' `
    "The current observed Palworld version is missing."
Assert-Contains $matrix '"steamBuild": "24181105"' `
    "The current observed Steam build is missing."
Assert-Contains $matrix '"status": "quarantined"' `
    "The current unsupported runtime is not quarantined."
Assert-Contains $matrix '"bridgeAvailability": "unavailable"' `
    "The current unavailable Bridge is inaccurately represented."
Assert-Contains $schema '"additionalProperties": false' `
    "The compatibility schema does not reject hash-polluting fields."

foreach ($path in @(
    "/internal/federation/profile:",
    "/internal/federation/health:",
    "/player/me/federation:",
    "/admin/federation/health:",
    "/admin/federation/compatibility-matrix:")) {
    Assert-Contains $openApi $path "OpenAPI is missing federation path $path"
}
Assert-Contains $openApi 'federationNodeKey:' `
    "OpenAPI is missing the server-to-server key scheme."
Assert-Contains $openApi 'pattern: "^fed1_[A-Za-z0-9_-]{43}$"' `
    "OpenAPI does not constrain the irreversible federation token."

foreach ($match in [regex]::Matches(
        $profiles,
        'public sealed record Federation(?:LocalProfile|NodeSummary)\([\s\S]*?\);')) {
    if ($match.Value -match '\b(AccountId|PlayerUid|SteamId|ExternalUserId)\b') {
        throw "Federation response DTOs expose an internal or raw identity field."
    }
}

Write-Host (
    "PASS: federation source, compatibility observations, fail-closed transport, " +
    "identity boundary, and OpenAPI contract are present.")

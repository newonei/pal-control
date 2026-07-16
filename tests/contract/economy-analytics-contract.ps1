[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$utf8 = [Text.UTF8Encoding]::new($false)

function Read-RepositoryFile([string] $relativePath) {
    $path = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Economy analytics contract failed: missing '$relativePath'."
    }
    return [IO.File]::ReadAllText($path, $utf8)
}

function Assert-Contract([bool] $condition, [string] $message) {
    if (-not $condition) {
        throw "Economy analytics contract failed: $message"
    }
}

function Require-Ordinal([string] $text, [string] $value, [string] $message) {
    Assert-Contract ($text.IndexOf($value, [StringComparison]::Ordinal) -ge 0) $message
}

function Get-Block(
    [string] $document,
    [string] $marker,
    [string] $nextPattern,
    [string] $description) {
    $start = $document.IndexOf($marker, [StringComparison]::Ordinal)
    Assert-Contract ($start -ge 0) "$description is missing."
    $bodyStart = $start + $marker.Length
    $next = [regex]::Match($document.Substring($bodyStart), $nextPattern)
    $end = if ($next.Success) { $bodyStart + $next.Index } else { $document.Length }
    return $document.Substring($start, $end - $start)
}

$endpoint = Read-RepositoryFile `
    "services\control-api\Infrastructure\EconomyAnalyticsEndpoints.cs"
Require-Ordinal $endpoint 'MapGet("/economy/analytics", GetAsync)' `
    "analytics is not exposed as a read-only GET."
Require-Ordinal $endpoint '.RequireAuthorization(AdminPolicies.Viewer)' `
    "analytics is not protected by Viewer authorization."
Assert-Contract (-not [regex]::IsMatch($endpoint, 'Map(?:Post|Put|Patch|Delete)\("/economy/analytics')) `
    "analytics exposes a mutating HTTP route."

$store = Read-RepositoryFile `
    "services\control-api\Infrastructure\EconomyAnalyticsStore.cs"
foreach ($fragment in @(
        "economy_analytics_events",
        "event_key TEXT NOT NULL UNIQUE",
        "ON CONFLICT(event_key) DO NOTHING",
        "'server-observed'",
        "sqlite-authoritative-recomputation",
        "ContainsPlayerIdentifiers: false",
        "MinimumCohortSize = 5",
        "CATALOG_DENOMINATOR_INCOMPLETE",
        "ECONOMY_UNCERTAIN_PRESENT")) {
    Require-Ordinal $store $fragment "authoritative store is missing '$fragment'."
}
Assert-Contract (-not [regex]::IsMatch(
        $store,
        '(?i)(client|browser|frontend)[-_ ]?(event|fact)[-_ ]?(payload|upload)')) `
    "store appears to trust a client-supplied analytics fact."

$portal = Read-RepositoryFile `
    "services\control-api\Infrastructure\PlayerPortalEndpoints.cs"
Require-Ordinal $portal 'RecordPortalSessionAsync(' `
    "successful player portal reads do not record a unique server session fact."
Require-Ordinal $portal 'RecordCatalogViewAsync(' `
    "successful catalog reads do not record a unique server catalog fact."

$program = Read-RepositoryFile "services\control-api\Program.cs"
Require-Ordinal $program 'AddSingleton<EconomyAnalyticsStore>()' `
    "analytics store is not registered."
Require-Ordinal $program 'MapEconomyAnalyticsEndpoints()' `
    "analytics endpoints are not mapped."

$openApi = Read-RepositoryFile "packages\contracts\openapi\control-api.yaml"
$pathBlock = Get-Block $openApi "  /economy/analytics:" '(?m)^  /' `
    "OpenAPI analytics path"
Require-Ordinal $pathBlock "    get:" "OpenAPI analytics path omits GET."
Require-Ordinal $pathBlock 'operationId: getEconomyAnalytics' `
    "OpenAPI analytics operationId is missing."
Require-Ordinal $pathBlock '#/components/schemas/EconomyAnalyticsReport' `
    "OpenAPI analytics success response is not typed."
Assert-Contract ($pathBlock.IndexOf("requestBody:", [StringComparison]::Ordinal) -lt 0) `
    "OpenAPI analytics GET accepts a client event body."

$report = Get-Block $openApi "    EconomyAnalyticsReport:" `
    '(?m)^    [A-Za-z][A-Za-z0-9]+:\s*$' "OpenAPI analytics report schema"
foreach ($field in @(
        "window", "privacy", "source", "funnel", "products", "resourceExchange",
        "zones", "currencies", "uncertain", "alerts", "page")) {
    Require-Ordinal $report "        - $field" "report does not require '$field'."
}
foreach ($forbidden in @(
        "accountId", "playerId", "playerUid", "platformSubject", "steamId",
        "displayName", "sessionId")) {
    Assert-Contract ($report.IndexOf(
            $forbidden,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "report schema exports forbidden identity '$forbidden'."
}

$privacy = Get-Block $openApi "    EconomyAnalyticsPrivacy:" `
    '(?m)^    [A-Za-z][A-Za-z0-9]+:\s*$' "OpenAPI analytics privacy schema"
Require-Ordinal $privacy "          const: 5" `
    "OpenAPI does not lock the five-account suppression threshold."
Require-Ordinal $privacy "          const: false" `
    "OpenAPI does not prohibit player identifiers."

$app = Read-RepositoryFile "apps\console-web\src\app\App.tsx"
Require-Ordinal $app '"economy-analytics"' `
    "console navigation omits the analytics page."
Require-Ordinal $app '<EconomyAnalyticsDashboard />' `
    "console does not render the analytics dashboard."
$client = Read-RepositoryFile `
    "apps\console-web\src\features\economy-analytics\api.ts"
Require-Ordinal $client 'cache: "no-store"' `
    "console may reuse stale analytics responses."
foreach ($forbidden in @("accountId", "playerUid", "eventCount")) {
    Assert-Contract ($client.IndexOf(
            $forbidden,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "console analytics request exposes client fact '$forbidden'."
}

Write-Host "PASS: authoritative economy analytics read/privacy/console/OpenAPI contract."

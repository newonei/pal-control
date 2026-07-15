[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$openApiPath = Join-Path $repositoryRoot "packages\contracts\openapi\control-api.yaml"
$endpointPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\EconomyObservabilityEndpoints.cs"
$servicePath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\EconomyObservabilityService.cs"
$productionPath = Join-Path $repositoryRoot `
    "deploy\windows\appsettings.Production.example.json"
$harness = Join-Path $repositoryRoot `
    "tests\economy-observability\PalControl.EconomyObservability.ContractTests.csproj"

function Assert-Contract([bool] $condition, [string] $message) {
    if (-not $condition) {
        throw "Economy observability contract failed: $message"
    }
}

function Require-Ordinal([string] $text, [string] $value, [string] $message) {
    Assert-Contract ($text.IndexOf($value, [StringComparison]::Ordinal) -ge 0) $message
}

function Get-SchemaBlock([string] $document, [string] $schemaName) {
    $marker = "    ${schemaName}:"
    $start = $document.IndexOf($marker, [StringComparison]::Ordinal)
    Assert-Contract ($start -ge 0) "OpenAPI schema '$schemaName' is missing."
    $nextStart = $start + $marker.Length
    $next = [regex]::Match(
        $document.Substring($nextStart),
        '(?m)^    [A-Za-z][A-Za-z0-9]+:\s*$')
    $end = if ($next.Success) { $nextStart + $next.Index } else { $document.Length }
    return $document.Substring($start, $end - $start)
}

$utf8 = [Text.UTF8Encoding]::new($false)
$openApi = [IO.File]::ReadAllText($openApiPath, $utf8)
Require-Ordinal $openApi "  /economy/observability:" `
    "JSON observability path is missing."
Require-Ordinal $openApi "  /economy/metrics:" `
    "Prometheus observability path is missing."
$snapshotSchema = Get-SchemaBlock $openApi "EconomyObservabilitySnapshot"
foreach ($field in @(
        "orders", "resourceSettlements", "deliveries", "deliveryQueue",
        "resourceSettlementQueue", "outbox", "uncertain", "ledger", "identity",
        "dependencyConsistency", "versionConsistency", "worldConsistency", "gameBackup", "economyBackup",
        "circuits", "alerts", "collectionErrorCode")) {
    Require-Ordinal $snapshotSchema "        - $field" `
        "snapshot does not require '$field'."
}
foreach ($forbidden in @(
        "playerIdentifier", "playerUid", "externalUserId", "steamId",
        "cookieValue", "tokenValue", "password")) {
    Assert-Contract ($snapshotSchema.IndexOf(
            $forbidden,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "snapshot schema exports forbidden field '$forbidden'."
}
$circuitSchema = Get-SchemaBlock $openApi "EconomyCircuitObservability"
foreach ($forbidden in @("reason", "actor", "subject")) {
    Assert-Contract (-not [regex]::IsMatch(
            $circuitSchema,
            "(?im)^        ${forbidden}:\s*$")) `
        "observability circuit exports free-text '$forbidden'."
}

$endpoint = [IO.File]::ReadAllText($endpointPath, $utf8)
Require-Ordinal $endpoint '.RequireAuthorization(AdminPolicies.Viewer)' `
    "metrics endpoints are not protected by Viewer authorization."
Require-Ordinal $endpoint 'applyAutomaticCircuits: false' `
    "a read request may mutate economy circuits."
foreach ($metric in @(
        "pal_control_economy_state_total",
        "pal_control_economy_state_latency_seconds",
        "pal_control_economy_queue_oldest_age_seconds",
        "pal_control_economy_uncertain_total",
        "pal_control_economy_ledger_invariant_mismatch_total",
        "pal_control_economy_identity_conflict_total",
        "pal_control_economy_dependency_consistent",
        "pal_control_economy_version_consistent",
        "pal_control_economy_world_consistent",
        "pal_control_economy_backup_age_seconds",
        "pal_control_economy_circuit_open",
        "pal_control_economy_alert_active")) {
    Require-Ordinal $endpoint $metric "Prometheus export omits '$metric'."
}

$service = [IO.File]::ReadAllText($servicePath, $utf8)
foreach ($alertCode in @(
        "DELIVERY_QUEUE_SATURATED",
        "DELIVERY_QUEUE_STALLED",
        "OUTBOX_SATURATED",
        "OUTBOX_STALLED",
        "SETTLEMENT_QUEUE_SATURATED",
        "PURCHASE_UNCERTAIN_PRESENT",
        "RESOURCE_SETTLEMENT_UNCERTAIN_PRESENT",
        "LEDGER_INVARIANT_VIOLATION",
        "IDENTITY_BINDING_INVARIANT_VIOLATION",
        "IDENTITY_BINDING_CONFLICT_SPIKE",
        "PURCHASE_DEPENDENCY_UNAVAILABLE",
        "RESOURCE_DEPENDENCY_UNAVAILABLE",
        "PURCHASE_VERSION_INCONSISTENT",
        "RESOURCE_VERSION_INCONSISTENT",
        "WORLD_INCONSISTENT",
        "GAME_BACKUP_STALE",
        "ECONOMY_BACKUP_STALE",
        "ECONOMY_METRICS_COLLECTION_FAILED")) {
    Require-Ordinal $service $alertCode "stable alert '$alertCode' is missing."
}
Require-Ordinal $service '"system:economy-observability"' `
    "automatic circuit operations lack a stable attributable actor."
Require-Ordinal $service 'if (!current.WritesEnabled)' `
    "automatic evaluation can overwrite an already open human circuit."

$production = [IO.File]::ReadAllText($productionPath, $utf8) | ConvertFrom-Json
$options = $production.ExtractionMode.Observability
Assert-Contract ($null -ne $options) "production observability configuration is missing."
Assert-Contract ($options.Enabled -eq $true -and
                 $options.AutoCircuitBreakEnabled -eq $true) `
    "production metrics or automatic circuits are disabled."
Assert-Contract ($options.MaximumUncertainPurchaseCount -eq 0 -and
                 $options.MaximumUncertainSettlementCount -eq 0) `
    "production allows unexplained uncertain economy outcomes."
Assert-Contract ($options.RequireRecentGameBackupForWrites -eq $true -and
                 $options.RequireRecentEconomyBackupForWrites -eq $true) `
    "production writes do not require recent game and economy backups."

& dotnet run --project $harness --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Economy observability .NET harness failed with exit code $LASTEXITCODE."
}
Write-Host "PASS: economy observability API/config/alerts/privacy contract."

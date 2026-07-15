$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$schemaPath = Join-Path $repositoryRoot "packages\contracts\bridge\message.schema.json"
$adapterPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionNativeInventoryAdapter.cs"
$settlementPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionSettlementService.cs"
$safetyGatePath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\EconomySafetyGate.cs"
$developmentRconPolicyPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\DevelopmentRconSettlementPolicy.cs"
$rconModelsPath = Join-Path $repositoryRoot `
    "services\control-api\Extraction\RconModels.cs"
$storePath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionRunStore.cs"
$endpointsPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionModeEndpoints.cs"
$openApiPath = Join-Path $repositoryRoot `
    "packages\contracts\openapi\control-api.yaml"
$productionConfigPath = Join-Path $repositoryRoot `
    "deploy\windows\appsettings.Production.example.json"

function Assert-Contract([bool] $condition, [string] $message) {
    if (-not $condition) {
        throw "Native settlement contract failed: $message"
    }
}

$utf8 = [Text.UTF8Encoding]::new($false)
$schema = [IO.File]::ReadAllText($schemaPath, $utf8) | ConvertFrom-Json
$payload = $schema.'$defs'.inventoryConsumePayload
Assert-Contract ($payload.required -contains "snapshotVersion") `
    "inventory.consume does not require snapshotVersion."
Assert-Contract ($payload.properties.snapshotVersion.const -eq 1) `
    "inventory.consume snapshotVersion is not pinned to 1."

$exactSlotFields = @(
    "slotIndex",
    "itemId",
    "quantity",
    "dynamicCreatedWorldId",
    "dynamicLocalIdInCreatedWorld",
    "hasDynamicItemData",
    "corruptionProgress",
    "corruptionProgressBits"
)
foreach ($variant in $schema.'$defs'.inventoryExpectedSlot.oneOf) {
    foreach ($field in $exactSlotFields) {
        Assert-Contract ($variant.required -contains $field) `
            "slot variant does not require '$field'."
    }
}

$adapter = [IO.File]::ReadAllText($adapterPath, $utf8)
Assert-Contract ($adapter.IndexOf(
        'public const string StableConsumeCapability = "inventory.consume";',
        [StringComparison]::Ordinal) -ge 0) `
    "the Control adapter does not pin the stable capability name."
Assert-Contract ($adapter.IndexOf(
        'actual.ActualConsumed == item.Quantity',
        [StringComparison]::Ordinal) -ge 0) `
    "per-line actualConsumed equality is not enforced."
Assert-Contract ($adapter.IndexOf(
        'persistenceVerified && exactEvidence',
        [StringComparison]::Ordinal) -ge 0) `
    "Native success can bypass persistence and exact evidence."

$settlement = [IO.File]::ReadAllText($settlementPath, $utf8)
Assert-Contract ($settlement.IndexOf(
        '_useDevelopmentRconSettlement = DevelopmentRconSettlementPolicy.IsAllowed(',
        [StringComparison]::Ordinal) -ge 0) `
    "the settlement service bypasses the shared Development RCON policy."
$safetyGate = [IO.File]::ReadAllText($safetyGatePath, $utf8)
Assert-Contract ($safetyGate.IndexOf(
        '_useDevelopmentRconSettlement = DevelopmentRconSettlementPolicy.IsAllowed(',
        [StringComparison]::Ordinal) -ge 0) `
    "the economy safety probe bypasses the shared Development RCON policy."
$rconModels = [IO.File]::ReadAllText($rconModelsPath, $utf8)
Assert-Contract ($rconModels.IndexOf(
        'public bool AllowDevelopmentSettlement { get; init; }',
        [StringComparison]::Ordinal) -ge 0) `
    "the explicit Development settlement diagnostic switch is missing."
$developmentRconPolicy = [IO.File]::ReadAllText($developmentRconPolicyPath, $utf8)
foreach ($requiredPolicyText in @(
        'environment?.IsDevelopment() != true',
        'configuration?.GetValue<bool>("Security:DevelopmentMode") != true',
        'configuration?.GetValue<bool>("PlayerPortal:PublicSteam") == true',
        '!rcon.Enabled',
        '!rcon.AllowDevelopmentSettlement',
        'safety is null || safety.RequireNativeForResourceExchange'
    )) {
    Assert-Contract ($developmentRconPolicy.IndexOf(
            $requiredPolicyText,
            [StringComparison]::Ordinal) -ge 0) `
        "the shared Development RCON policy is missing '$requiredPolicyText'."
}
Assert-Contract ($settlement.IndexOf(
        'TryRecordNativeConsumeReceiptAsync(',
        [StringComparison]::Ordinal) -ge 0) `
    "Native success is not durably recorded before settlement transitions."
Assert-Contract ($settlement.IndexOf(
        'public string SettlementAdapter',
        [StringComparison]::Ordinal) -ge 0) `
    "the active settlement adapter is not exposed generically."

$endpoints = [IO.File]::ReadAllText($endpointsPath, $utf8)
Assert-Contract ($endpoints.IndexOf(
        'group.MapGet("/admin/settlement/status", GetSettlementStatusAsync);',
        [StringComparison]::Ordinal) -ge 0) `
    "the adapter-neutral settlement status endpoint is missing."
Assert-Contract ($endpoints.IndexOf(
        'group.MapGet("/admin/rcon/status", GetSettlementStatusAsync);',
        [StringComparison]::Ordinal) -ge 0) `
    "the deprecated RCON status alias does not share the generic schema."
Assert-Contract ($endpoints.IndexOf(
        'adapter = settlement.SettlementAdapter',
        [StringComparison]::Ordinal) -ge 0) `
    "the status response does not identify the selected settlement adapter."
$statusHandlerStart = $endpoints.IndexOf(
    'private static async Task<IResult> GetSettlementStatusAsync(',
    [StringComparison]::Ordinal)
Assert-Contract ($statusHandlerStart -ge 0) `
    "the generic settlement status handler is missing."
$statusHandlerEnd = $endpoints.IndexOf(
    'private static async Task<RolloverBlockers> GetRolloverBlockersAsync(',
    $statusHandlerStart,
    [StringComparison]::Ordinal)
Assert-Contract ($statusHandlerEnd -gt $statusHandlerStart) `
    "the generic settlement status handler block is malformed."
$statusHandler = $endpoints.Substring(
    $statusHandlerStart,
    $statusHandlerEnd - $statusHandlerStart)
foreach ($genericError in @(
    'SETTLEMENT_PROBE_FAILED',
    'SETTLEMENT_PROBE_UNCERTAIN'
)) {
    Assert-Contract ($statusHandler.IndexOf(
            $genericError,
            [StringComparison]::Ordinal) -ge 0) `
        "the generic status response omits '$genericError'."
}
Assert-Contract ($statusHandler.IndexOf(
        'result.ErrorMessage ??',
        [StringComparison]::Ordinal) -lt 0) `
    "the generic settlement status still passes through adapter-specific errors."

$openApi = [IO.File]::ReadAllText($openApiPath, $utf8)
Assert-Contract ($openApi.IndexOf(
        '  /extraction/admin/settlement/status:',
        [StringComparison]::Ordinal) -ge 0) `
    "OpenAPI omits the adapter-neutral settlement status path."
$legacyStatusStart = $openApi.IndexOf(
    '  /extraction/admin/rcon/status:',
    [StringComparison]::Ordinal)
Assert-Contract ($legacyStatusStart -ge 0) `
    "OpenAPI omits the legacy settlement-status alias."
$rolloverStart = $openApi.IndexOf(
    '  /extraction/admin/rollover/maintenance:',
    $legacyStatusStart,
    [StringComparison]::Ordinal)
Assert-Contract ($rolloverStart -gt $legacyStatusStart) `
    "OpenAPI legacy settlement-status alias block is malformed."
$legacyStatus = $openApi.Substring(
    $legacyStatusStart,
    $rolloverStart - $legacyStatusStart)
Assert-Contract ($legacyStatus.IndexOf(
        'deprecated: true',
        [StringComparison]::Ordinal) -ge 0) `
    "OpenAPI does not mark the RCON status alias deprecated."
Assert-Contract ($legacyStatus.IndexOf(
        '#/components/schemas/ExtractionSettlementStatus',
        [StringComparison]::Ordinal) -ge 0) `
    "the legacy status alias diverges from the generic response schema."
Assert-Contract ($openApi.IndexOf(
        'required: [adapter, enabled, connected, outcome, error]',
        [StringComparison]::Ordinal) -ge 0) `
    "the generic settlement status schema is incomplete."
Assert-Contract ($openApi.IndexOf(
        'enum: [native, development-rcon]',
        [StringComparison]::Ordinal) -ge 0) `
    "the generic settlement status adapter enum is missing."

$store = [IO.File]::ReadAllText($storePath, $utf8)
foreach ($field in @(
    "NativeInventorySnapshot",
    "SettlementRequestHash",
    "NativeConsumeReceipt"
)) {
    Assert-Contract ($store.IndexOf($field, [StringComparison]::Ordinal) -ge 0) `
        "settlement persistence does not retain '$field'."
}

$production = [IO.File]::ReadAllText($productionConfigPath, $utf8) | ConvertFrom-Json
$productionSafety = $production.ExtractionMode.Safety
Assert-Contract ($productionSafety.RequireNativeForResourceExchange -eq $true) `
    "the production example does not require Native resource settlement."
Assert-Contract ($productionSafety.ResourceExchangeNativeCapabilities -contains "inventory.probe") `
    "the production example does not require inventory.probe."
Assert-Contract ($productionSafety.ResourceExchangeNativeCapabilities -contains "inventory.consume") `
    "the production example does not require stable inventory.consume."
Assert-Contract ($productionSafety.ResourceExchangeNativeCapabilities -notcontains `
        "inventory.consume.experimental") `
    "the production example accepts the experimental consume capability."

Write-Host "PASS: stable Native consume, full snapshot, evidence, and durable idempotency contract."

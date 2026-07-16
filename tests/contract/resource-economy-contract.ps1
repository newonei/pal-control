$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$settlementPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionSettlementService.cs"
$contentDefaultsPath = Join-Path $repositoryRoot `
    "services\control-api\Content\EconomyContentDefaults.cs"
$programPath = Join-Path $repositoryRoot "services\control-api\Program.cs"
$resourceCatalogPath = Join-Path $repositoryRoot `
    "services\control-api\Resources\palworld-resource-catalog.json"
$approvedItemIdsPath = Join-Path $PSScriptRoot `
    "fixtures\resource-economy-item-ids.txt"
$coordinatorPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionModeCoordinator.cs"
$adminOverviewPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionModeEndpoints.cs"
$playerOverviewPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\PlayerPortalEndpoints.cs"
$openApiPath = Join-Path $repositoryRoot `
    "packages\contracts\openapi\control-api.yaml"
$operatorEconomyUiPath = Join-Path $repositoryRoot `
    "apps\console-web\src\features\extraction\ExtractionCenter.tsx"

function Assert-Contract([bool] $condition, [string] $message) {
    if (-not $condition) {
        throw "Resource economy contract failed: $message"
    }
}

function Assert-ContainsOrdinal(
    [string] $value,
    [string] $expected,
    [string] $message) {
    Assert-Contract ($value.IndexOf($expected, [StringComparison]::Ordinal) -ge 0) `
        $message
}

function Get-OpenApiSchemaBlock([string] $document, [string] $schemaName) {
    $marker = "    ${schemaName}:"
    $start = $document.IndexOf($marker, [StringComparison]::Ordinal)
    Assert-Contract ($start -ge 0) "OpenAPI schema '$schemaName' is missing."

    $remainingStart = $start + $marker.Length
    $next = [regex]::Match(
        $document.Substring($remainingStart),
        '(?m)^    [A-Za-z][A-Za-z0-9]+:\s*$')
    $end = if ($next.Success) {
        $remainingStart + $next.Index
    }
    else {
        $document.Length
    }
    return $document.Substring($start, $end - $start)
}

$utf8 = [Text.UTF8Encoding]::new($false)
$source = [IO.File]::ReadAllText($settlementPath, $utf8)
$contentDefaults = [IO.File]::ReadAllText($contentDefaultsPath, $utf8)
$catalogMarker = "var resources = new (string Id, string Category, long Value)[]"
$catalogStart = $contentDefaults.IndexOf($catalogMarker, [StringComparison]::Ordinal)
Assert-Contract ($catalogStart -ge 0) "Scheme A default resource declaration is missing."
$catalogEnd = $contentDefaults.IndexOf("        };", $catalogStart, [StringComparison]::Ordinal)
Assert-Contract ($catalogEnd -gt $catalogStart) "Scheme A default resource terminator is missing."
$catalogBlock = $contentDefaults.Substring($catalogStart, $catalogEnd - $catalogStart)

$entryPattern =
    '\("(?<id>[^"]+)",\s*"(?<category>(?:[^"\\]|\\.)*)",\s*(?<value>\d+)\)'
$matches = [regex]::Matches($catalogBlock, $entryPattern)
Assert-Contract ($matches.Count -gt 0) "the sellable resource whitelist is empty."
Assert-Contract ($matches.Count -le 500) `
    "the whitelist unexpectedly exceeds the reviewable safety limit of 500 entries."

$approvedItemIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($line in [IO.File]::ReadAllLines($approvedItemIdsPath, $utf8)) {
    $itemId = $line.Trim()
    if (-not [string]::IsNullOrWhiteSpace($itemId)) {
        Assert-Contract ($approvedItemIds.Add($itemId)) `
            "duplicate approved item id '$itemId'."
    }
}
Assert-Contract ($approvedItemIds.Count -gt 0) `
    "the committed Scheme A item-id fixture is empty."

$knownItemIds = $null
if (Test-Path -LiteralPath $resourceCatalogPath -PathType Leaf) {
    $knownCatalog = [IO.File]::ReadAllText($resourceCatalogPath, $utf8) |
        ConvertFrom-Json
    Assert-Contract ($null -ne $knownCatalog.items -and $knownCatalog.items.Count -gt 0) `
        "the local Palworld item catalog is empty."
    $knownItemIds = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $knownCatalog.items) {
        if (-not [string]::IsNullOrWhiteSpace([string]$item.id)) {
            [void]$knownItemIds.Add([string]$item.id)
        }
    }
}

$whitelistIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$totalUnitValue = 0L
foreach ($match in $matches) {
    $itemId = $match.Groups["id"].Value
    $category = $match.Groups["category"].Value
    $unitValue = [int64]$match.Groups["value"].Value

    Assert-Contract ($whitelistIds.Add($itemId)) `
        "duplicate item id '$itemId' (case-insensitive)."
    Assert-Contract (-not [string]::IsNullOrWhiteSpace($category)) `
        "item '$itemId' has no content category."
    Assert-Contract ($unitValue -gt 0 -and $unitValue -le 1000000) `
        "item '$itemId' has unsafe unit value '$unitValue'."
    Assert-Contract ($approvedItemIds.Contains($itemId)) `
        "item '$itemId' is absent from the committed Scheme A item-id fixture."
    if ($null -ne $knownItemIds) {
        Assert-Contract ($knownItemIds.Contains($itemId)) `
            "item '$itemId' is absent from the local Palworld item catalog."
    }
    $totalUnitValue += $unitValue
}
Assert-Contract ($whitelistIds.Count -eq $approvedItemIds.Count) `
    "the committed Scheme A item-id fixture contains stale or missing entries."

$singleline = [Text.RegularExpressions.RegexOptions]::Singleline
Assert-Contract ([regex]::IsMatch(
        $source,
        'var\s+runtimeContent\s*=\s*_content\s+is\s+null\s*\?\s*null\s*:\s*await\s+_content\.GetCurrentAsync',
        $singleline)) `
    "production quote generation no longer loads the published content version."
Assert-Contract ([regex]::IsMatch(
        $source,
        'var\s+sellableResources\s*=\s*runtimeContent\s+is\s+null\s*\?\s*LootCatalog[\s\S]*?:\s*runtimeContent\.Resources\.Values[\s\S]*?resource\.ExchangeZoneIds\.Contains',
        $singleline)) `
    "published resources are not scoped to the active exchange zone, or the legacy catalog escaped its null-runtime fallback."
Assert-Contract ([regex]::IsMatch(
        $source,
        '\.Where\(\s*item\s*=>\s*item\.Value\s*>\s*0\s*&&\s*sellableResources\.ContainsKey\(item\.Key\)\s*\)',
        $singleline)) `
    "quote generation no longer filters positive inventory through the published sell allow-list."
Assert-Contract ([regex]::IsMatch(
        $source,
        'foreach\s*\(\s*var containerName in new\[\]\s*\{\s*"Items"\s*,\s*"Food"\s*,\s*"DropSlot"\s*\}\s*\)',
        $singleline)) `
    "inventory aggregation no longer covers Items, Food and DropSlot."
Assert-Contract ($source.IndexOf(
        "checked(item.Value * effectiveUnitValue)",
        [StringComparison]::Ordinal) -ge 0) `
    "quote totals are no longer overflow-checked."
Assert-ContainsOrdinal $source `
    "nativeSnapshot?.SnapshotHash ?? HashSnapshot(" `
    "production Native snapshot evidence is no longer frozen into the quote."
Assert-ContainsOrdinal $source `
    "lines.Select(line => line.ItemId)" `
    "the development quote snapshot is not bound to every quoted Scheme A allow-list item."
Assert-ContainsOrdinal $source `
    "existing.Items.Select(line => line.ItemId)" `
    "settlement no longer verifies the exact allow-list item set frozen into the quote."
Assert-ContainsOrdinal $source `
    "EconomyContentEvidence.MatchesCurrent(" `
    "settlement no longer compares frozen quote evidence with the current content version."
Assert-ContainsOrdinal $source `
    '"QUOTE_CONTENT_CHANGED"' `
    "a quote crossing a content rotation no longer fails with a stable error code."
Assert-ContainsOrdinal $source `
    "zone.ActiveWorldEvents);" `
    "the player map exposes scheduled world events as if they were currently active."
Assert-ContainsOrdinal $source `
    "var activeWorldEvents = runtimeZones" `
    "the player map snapshot no longer derives its top-level event list from active runtime windows."

$programSource = [IO.File]::ReadAllText($programPath, $utf8)
Assert-ContainsOrdinal $programSource "AddSingleton<SqliteEconomyContentStore>()" `
    "the durable economy-content store is not registered."
Assert-ContainsOrdinal $programSource "AddSingleton<EconomyContentRuntimeService>()" `
    "the published economy-content runtime is not registered in production DI."
Assert-ContainsOrdinal $programSource "AddHostedService<EconomyContentStartupInitializer>()" `
    "an enabled fresh installation still waits for a player request before bootstrapping Scheme A content."

$coordinatorSource = [IO.File]::ReadAllText($coordinatorPath, $utf8)
Assert-ContainsOrdinal $coordinatorSource `
    'public const string GameplayMode = "weekly-resource-economy";' `
    "the API gameplay mode is not pinned to Scheme A."

$operatorEconomyUi = [IO.File]::ReadAllText($operatorEconomyUiPath, $utf8)
Assert-ContainsOrdinal $operatorEconomyUi `
    "Native" `
    "the operator resource-exchange UI does not describe the Native-only production boundary."
Assert-Contract ($operatorEconomyUi.IndexOf(
        "RCON",
        [StringComparison]::Ordinal) -lt 0) `
    "the operator UI still advertises RCON as the Scheme A settlement path."

$overviewFields = @(
    "settledExchanges",
    "failedSettlements",
    "uncertainSettlements",
    "exchangedValue"
)
foreach ($overviewPath in @($adminOverviewPath, $playerOverviewPath)) {
    $overviewSource = [IO.File]::ReadAllText($overviewPath, $utf8)
    $overviewName = [IO.Path]::GetFileName($overviewPath)
    Assert-ContainsOrdinal $overviewSource `
        "gameplayMode = ExtractionModeCoordinator.GameplayMode" `
        "$overviewName does not expose the Scheme A gameplay mode."
    foreach ($field in $overviewFields) {
        Assert-ContainsOrdinal $overviewSource $field `
            "$overviewName does not expose '$field'."
    }
}

$adminSource = [IO.File]::ReadAllText($adminOverviewPath, $utf8)
Assert-ContainsOrdinal $adminSource 'group.MapGet("/capabilities"' `
    "the extraction compatibility capability endpoint is missing."
Assert-ContainsOrdinal $adminSource "purchase = new" `
    "purchase does not have an independent write capability."
Assert-ContainsOrdinal $adminSource "resourceExchange = new" `
    "resource exchange does not have an independent write capability."

$openApi = [IO.File]::ReadAllText($openApiPath, $utf8)
Assert-ContainsOrdinal $openApi "  /extraction/capabilities:" `
    "OpenAPI does not expose the extraction capability compatibility path."
foreach ($contentPath in @(
        "  /servers/{serverId}/economy-content/current:",
        "  /servers/{serverId}/economy-content/drafts:",
        "  /servers/{serverId}/economy-content/drafts/{draftId}/publish:",
        "  /servers/{serverId}/economy-content/rollback:")) {
    Assert-ContainsOrdinal $openApi $contentPath `
        "OpenAPI does not expose the versioned content path '$contentPath'."
}
Assert-ContainsOrdinal $openApi "OFFER_NOT_AVAILABLE" `
    "OpenAPI does not document fail-closed stale shop offers."
$catalogSchema = Get-OpenApiSchemaBlock $openApi "ExtractionCatalog"
foreach ($field in @(
        "contentVersionId", "contentHash", "businessDate", "rulesVersion", "rotation")) {
    Assert-Contract ([regex]::IsMatch(
            $catalogSchema,
            "(?m)^        - $field\s*$")) `
        "OpenAPI ExtractionCatalog does not require '$field'."
}
$productSchema = Get-OpenApiSchemaBlock $openApi "ExtractionShopProduct"
foreach ($field in @(
        "sku", "personalLimitRemaining", "serverStockRemaining",
        "globalStock", "contentVersionId", "contentHash")) {
    Assert-Contract ([regex]::IsMatch(
            $productSchema,
            "(?m)^        - $field\s*$")) `
        "OpenAPI ExtractionShopProduct does not require '$field'."
}
foreach ($requestSchemaName in @(
        "CreateExtractionOrderRequest", "CreatePlayerPortalOrderRequest")) {
    $requestSchema = Get-OpenApiSchemaBlock $openApi $requestSchemaName
    foreach ($field in @("sku", "contentVersionId", "contentHash")) {
        Assert-Contract ([regex]::IsMatch(
                $requestSchema,
                "(?m)^\s+required:\s*\[[^\]]*\b$field\b[^\]]*\]\s*$")) `
            "OpenAPI $requestSchemaName does not require '$field'."
    }
}
$capabilitiesSchema = Get-OpenApiSchemaBlock $openApi "ExtractionCapabilities"
foreach ($field in @("gameplayMode", "readReady", "maintenance", "writes", "evaluatedAt")) {
    Assert-Contract ([regex]::IsMatch(
            $capabilitiesSchema,
            "(?m)^        - $field\s*$")) `
        "OpenAPI ExtractionCapabilities does not require '$field'."
}
$writeCapabilitiesSchema = Get-OpenApiSchemaBlock $openApi "ExtractionWriteCapabilities"
foreach ($field in @("purchase", "resourceExchange")) {
    Assert-Contract ([regex]::IsMatch(
            $writeCapabilitiesSchema,
            "(?m)^        ${field}:\s*$")) `
        "OpenAPI ExtractionWriteCapabilities does not define '$field'."
}
$statsSchema = Get-OpenApiSchemaBlock $openApi "ExtractionSeasonStats"
foreach ($field in $overviewFields) {
    Assert-Contract ([regex]::IsMatch(
            $statsSchema,
            "(?m)^        - $field\s*$")) `
        "OpenAPI ExtractionSeasonStats does not require '$field'."
    Assert-Contract ([regex]::IsMatch(
            $statsSchema,
            "(?m)^        ${field}:\s*$")) `
        "OpenAPI ExtractionSeasonStats does not define '$field'."
}

$overviewSchema = Get-OpenApiSchemaBlock $openApi "ExtractionOverview"
Assert-Contract ([regex]::IsMatch(
        $overviewSchema,
        '(?m)^        - gameplayMode\s*$')) `
    "OpenAPI ExtractionOverview does not require gameplayMode."
Assert-Contract ([regex]::IsMatch(
        $overviewSchema,
        '(?ms)^        gameplayMode:\s*\r?\n\s+type: string\s*\r?\n\s+const: weekly-resource-economy\s*$')) `
    "OpenAPI ExtractionOverview does not pin gameplayMode to Scheme A."

Write-Host (
    "PASS: Scheme A resource economy contract ({0} whitelist items, aggregate unit value {1})." -f `
        $whitelistIds.Count,
        $totalUnitValue)

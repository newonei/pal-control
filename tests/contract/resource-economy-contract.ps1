$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$settlementPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionSettlementService.cs"
$resourceCatalogPath = Join-Path $repositoryRoot `
    "services\control-api\Resources\palworld-resource-catalog.json"
$coordinatorPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionModeCoordinator.cs"
$adminOverviewPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\ExtractionModeEndpoints.cs"
$playerOverviewPath = Join-Path $repositoryRoot `
    "services\control-api\Infrastructure\PlayerPortalEndpoints.cs"
$openApiPath = Join-Path $repositoryRoot `
    "packages\contracts\openapi\control-api.yaml"

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
$catalogMarker =
    "private static readonly IReadOnlyDictionary<string, LootDefinition> LootCatalog"
$catalogStart = $source.IndexOf($catalogMarker, [StringComparison]::Ordinal)
Assert-Contract ($catalogStart -ge 0) "LootCatalog declaration is missing."
$catalogEnd = $source.IndexOf("        };", $catalogStart, [StringComparison]::Ordinal)
Assert-Contract ($catalogEnd -gt $catalogStart) "LootCatalog terminator is missing."
$catalogBlock = $source.Substring($catalogStart, $catalogEnd - $catalogStart)

$entryPattern =
    '(?m)^\s*\["(?<id>[^"]+)"\]\s*=\s*new\("(?<name>(?:[^"\\]|\\.)*)",\s*(?<value>\d+)\),?\s*$'
$matches = [regex]::Matches($catalogBlock, $entryPattern)
Assert-Contract ($matches.Count -gt 0) "the sellable resource whitelist is empty."
Assert-Contract ($matches.Count -le 500) `
    "the whitelist unexpectedly exceeds the reviewable safety limit of 500 entries."

$knownCatalog = [IO.File]::ReadAllText($resourceCatalogPath, $utf8) |
    ConvertFrom-Json
Assert-Contract ($null -ne $knownCatalog.items -and $knownCatalog.items.Count -gt 0) `
    "the versioned Palworld item catalog is empty."

$knownItemIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($item in $knownCatalog.items) {
    if (-not [string]::IsNullOrWhiteSpace([string]$item.id)) {
        [void]$knownItemIds.Add([string]$item.id)
    }
}

$whitelistIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$totalUnitValue = 0L
foreach ($match in $matches) {
    $itemId = $match.Groups["id"].Value
    $displayName = $match.Groups["name"].Value
    $unitValue = [int64]$match.Groups["value"].Value

    Assert-Contract ($whitelistIds.Add($itemId)) `
        "duplicate item id '$itemId' (case-insensitive)."
    Assert-Contract (-not [string]::IsNullOrWhiteSpace($displayName)) `
        "item '$itemId' has no display name."
    Assert-Contract ($unitValue -gt 0 -and $unitValue -le 1000000) `
        "item '$itemId' has unsafe unit value '$unitValue'."
    Assert-Contract ($knownItemIds.Contains($itemId)) `
        "item '$itemId' is absent from the versioned Palworld item catalog."
    $totalUnitValue += $unitValue
}

$singleline = [Text.RegularExpressions.RegexOptions]::Singleline
Assert-Contract ([regex]::IsMatch(
        $source,
        '\.Where\(\s*item\s*=>\s*item\.Value\s*>\s*0\s*&&\s*LootCatalog\.ContainsKey\(item\.Key\)\s*\)',
        $singleline)) `
    "quote generation no longer filters positive inventory through LootCatalog."
Assert-Contract ([regex]::IsMatch(
        $source,
        'foreach\s*\(\s*var containerName in new\[\]\s*\{\s*"Items"\s*,\s*"Food"\s*,\s*"DropSlot"\s*\}\s*\)',
        $singleline)) `
    "inventory aggregation no longer covers Items, Food and DropSlot."
Assert-Contract ($source.IndexOf(
        "checked(item.Value * definition.UnitValue)",
        [StringComparison]::Ordinal) -ge 0) `
    "quote totals are no longer overflow-checked."
Assert-Contract ([regex]::IsMatch(
        $source,
        'HashSnapshot[\s\S]*?LootCatalog\.Keys\s*\.OrderBy',
        $singleline)) `
    "inventory snapshot hashing is no longer scoped to the whitelist."

$coordinatorSource = [IO.File]::ReadAllText($coordinatorPath, $utf8)
Assert-ContainsOrdinal $coordinatorSource `
    'public const string GameplayMode = "weekly-resource-economy";' `
    "the API gameplay mode is not pinned to Scheme A."

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

$openApi = [IO.File]::ReadAllText($openApiPath, $utf8)
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

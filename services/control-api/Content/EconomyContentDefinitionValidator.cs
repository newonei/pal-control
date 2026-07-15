using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Content;

public static class EconomyContentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class EconomyContentCanonicalizer
{
    public static EconomyContentDefinition Normalize(EconomyContentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var dependencies = definition.Dependencies ?? new EconomyContentDependencies("", "", "", "");
        var rotation = definition.Rotation ?? new ContentRotationPolicy("", 0, "", [], 0, [], 0, [], 0);
        return definition with
        {
            ServerId = Clean(definition.ServerId),
            DisplayName = Clean(definition.DisplayName),
            TimeZoneId = Clean(definition.TimeZoneId),
            Dependencies = dependencies with
            {
                RulesVersion = Clean(dependencies.RulesVersion),
                ResourceCatalogRevision = Clean(dependencies.ResourceCatalogRevision),
                GameVersion = Clean(dependencies.GameVersion),
                PalDefenderVersion = Clean(dependencies.PalDefenderVersion)
            },
            Products = (definition.Products ?? [])
                .Select(product => product with
                {
                    Sku = Clean(product.Sku).ToUpperInvariant(),
                    DisplayName = Clean(product.DisplayName),
                    Description = Clean(product.Description),
                    Category = Clean(product.Category),
                    Tags = NormalizeStrings(product.Tags),
                    ItemGrants = (product.ItemGrants ?? [])
                        .Select(grant => grant with { ItemId = Clean(grant.ItemId) })
                        .OrderBy(grant => grant.ItemId, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(grant => grant.Quantity)
                        .ToArray(),
                    AvailableFrom = product.AvailableFrom?.ToUniversalTime(),
                    AvailableUntil = product.AvailableUntil?.ToUniversalTime()
                })
                .OrderBy(product => product.Sku, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Resources = (definition.Resources ?? [])
                .Select(resource => resource with
                {
                    ItemId = Clean(resource.ItemId),
                    DisplayName = Clean(resource.DisplayName),
                    Category = Clean(resource.Category),
                    Tags = NormalizeStrings(resource.Tags),
                    ExchangeZoneIds = NormalizeStrings(resource.ExchangeZoneIds)
                })
                .OrderBy(resource => resource.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ExchangeZones = (definition.ExchangeZones ?? [])
                .Select(zone => zone with
                {
                    ZoneId = Clean(zone.ZoneId).ToLowerInvariant(),
                    DisplayName = Clean(zone.DisplayName),
                    RouteHint = Clean(zone.RouteHint),
                    OpenWindows = (zone.OpenWindows ?? [])
                        .OrderBy(window => window.DayOfWeek)
                        .ThenBy(window => window.OpensAt)
                        .ThenBy(window => window.ClosesAt)
                        .ToArray()
                })
                .OrderBy(zone => zone.ZoneId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Tasks = (definition.Tasks ?? [])
                .Select(task => task with
                {
                    TaskKey = Clean(task.TaskKey).ToLowerInvariant(),
                    DisplayName = Clean(task.DisplayName),
                    Description = Clean(task.Description),
                    TargetItemId = CleanOptional(task.TargetItemId),
                    ExchangeZoneIds = NormalizeStrings(task.ExchangeZoneIds),
                    Reward = task.Reward ?? new ContentTaskReward(ExtractionCurrency.MarketCoin, 0, 0)
                })
                .OrderBy(task => task.TaskKey, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Rotation = rotation with
            {
                RulesVersion = Clean(rotation.RulesVersion),
                SeedNamespace = Clean(rotation.SeedNamespace),
                DailyTaskPool = NormalizeStrings(rotation.DailyTaskPool),
                WeeklyTaskPool = NormalizeStrings(rotation.WeeklyTaskPool),
                HotspotZonePool = NormalizeStrings(rotation.HotspotZonePool)
            }
        };
    }

    public static string Serialize(EconomyContentDefinition definition) =>
        JsonSerializer.Serialize(Normalize(definition), EconomyContentJson.Options);

    public static string Hash(EconomyContentDefinition definition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(definition))))
            .ToLowerInvariant();

    private static string[] NormalizeStrings(IReadOnlyList<string>? values) =>
        (values ?? [])
        .Select(Clean)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string Clean(string? value) => value?.Trim() ?? "";

    private static string? CleanOptional(string? value)
    {
        var clean = Clean(value);
        return clean.Length == 0 ? null : clean;
    }
}

public sealed class EconomyContentDefinitionValidator
{
    private const int CurrentSchemaVersion = 1;
    private const long MaximumWebSafeInteger = 9_007_199_254_740_991;
    private readonly TimeProvider _timeProvider;

    public EconomyContentDefinitionValidator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ContentValidationResult Validate(
        EconomyContentDefinition definition,
        EconomyContentValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        var normalized = EconomyContentCanonicalizer.Normalize(definition);
        var errors = new List<ContentValidationIssue>();
        var warnings = new List<ContentValidationIssue>();
        var knownItems = new HashSet<string>(context.KnownItemIds, StringComparer.OrdinalIgnoreCase);
        var supportedRules = new HashSet<string>(context.SupportedRulesVersions, StringComparer.Ordinal);

        if (normalized.SchemaVersion != CurrentSchemaVersion)
        {
            Error(errors, "UNSUPPORTED_SCHEMA_VERSION", "/schemaVersion",
                $"Schema version must be {CurrentSchemaVersion}.");
        }
        Required(errors, normalized.ServerId, 64, "/serverId", "SERVER_ID_REQUIRED");
        Required(errors, normalized.DisplayName, 128, "/displayName", "DISPLAY_NAME_REQUIRED");
        Required(errors, normalized.TimeZoneId, 64, "/timeZoneId", "TIME_ZONE_REQUIRED");
        if (normalized.DailyRefreshHour is < 0 or > 23)
        {
            Error(errors, "INVALID_DAILY_REFRESH_HOUR", "/dailyRefreshHour",
                "Daily refresh hour must be between 0 and 23.");
        }
        ValidateTimeZone(normalized.TimeZoneId, errors);
        ValidateDependencies(normalized.Dependencies, context, supportedRules, errors);

        if (normalized.Products.Count is < 1 or > 500)
        {
            Error(errors, "INVALID_PRODUCT_COUNT", "/products", "Products must contain 1 to 500 entries.");
        }
        if (normalized.Resources.Count is < 1 or > 5_000)
        {
            Error(errors, "INVALID_RESOURCE_COUNT", "/resources", "Resources must contain 1 to 5000 entries.");
        }
        if (normalized.ExchangeZones.Count is < 1 or > 100)
        {
            Error(errors, "INVALID_ZONE_COUNT", "/exchangeZones", "Exchange zones must contain 1 to 100 entries.");
        }
        if (normalized.Tasks.Count > 1_000)
        {
            Error(errors, "INVALID_TASK_COUNT", "/tasks", "Tasks cannot contain more than 1000 entries.");
        }

        ValidateProducts(normalized.Products, knownItems, errors);
        var zones = ValidateZones(normalized.ExchangeZones, errors);
        var resources = ValidateResources(normalized.Resources, knownItems, zones, errors);
        var tasks = ValidateTasks(normalized.Tasks, knownItems, resources, zones, errors);
        ValidateDirectResaleArbitrage(
            normalized.Products,
            normalized.Resources,
            normalized.ExchangeZones,
            errors,
            warnings);
        ValidateRotation(normalized.Rotation, normalized.Dependencies, tasks, zones, errors, warnings);

        return new ContentValidationResult(
            errors.Count == 0,
            EconomyContentCanonicalizer.Hash(normalized),
            errors.OrderBy(issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal).ToArray(),
            warnings.OrderBy(issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal).ToArray(),
            _timeProvider.GetUtcNow());
    }

    private static void ValidateDependencies(
        EconomyContentDependencies dependencies,
        EconomyContentValidationContext context,
        IReadOnlySet<string> supportedRules,
        List<ContentValidationIssue> errors)
    {
        Required(errors, dependencies.RulesVersion, 64, "/dependencies/rulesVersion", "RULES_VERSION_REQUIRED");
        Required(errors, dependencies.ResourceCatalogRevision, 128,
            "/dependencies/resourceCatalogRevision", "RESOURCE_CATALOG_REVISION_REQUIRED");
        Required(errors, dependencies.GameVersion, 64, "/dependencies/gameVersion", "GAME_VERSION_REQUIRED");
        Required(errors, dependencies.PalDefenderVersion, 64,
            "/dependencies/palDefenderVersion", "PALDEFENDER_VERSION_REQUIRED");
        if (dependencies.RulesVersion.Length > 0 && !supportedRules.Contains(dependencies.RulesVersion))
        {
            Error(errors, "UNSUPPORTED_RULES_VERSION", "/dependencies/rulesVersion",
                $"Rules version '{dependencies.RulesVersion}' is not supported.");
        }
        Expected(errors, dependencies.ResourceCatalogRevision, context.ExpectedResourceCatalogRevision,
            "/dependencies/resourceCatalogRevision", "RESOURCE_CATALOG_VERSION_MISMATCH");
        Expected(errors, dependencies.GameVersion, context.ExpectedGameVersion,
            "/dependencies/gameVersion", "GAME_VERSION_MISMATCH");
        Expected(errors, dependencies.PalDefenderVersion, context.ExpectedPalDefenderVersion,
            "/dependencies/palDefenderVersion", "PALDEFENDER_VERSION_MISMATCH");
    }

    private static void ValidateProducts(
        IReadOnlyList<ContentProductDefinition> products,
        IReadOnlySet<string> knownItems,
        List<ContentValidationIssue> errors)
    {
        ReportDuplicates(products.Select(product => product.Sku), "/products", "DUPLICATE_SKU", errors);
        for (var index = 0; index < products.Count; index++)
        {
            var product = products[index];
            var path = $"/products/{index}";
            if (!ValidKey(product.Sku, 64))
            {
                Error(errors, "INVALID_SKU", path + "/sku", "SKU must use 1 to 64 ASCII letters, digits, '-', '_' or '.'.");
            }
            Required(errors, product.DisplayName, 128, path + "/displayName", "PRODUCT_NAME_REQUIRED");
            Required(errors, product.Description, 512, path + "/description", "PRODUCT_DESCRIPTION_REQUIRED");
            Required(errors, product.Category, 64, path + "/category", "PRODUCT_CATEGORY_REQUIRED");
            ValidateTags(product.Tags, path + "/tags", errors);
            if (product.FeaturedRank is <= 0 or > 1000)
            {
                Error(errors, "INVALID_FEATURED_RANK", path + "/featuredRank",
                    "Featured rank must be null or between 1 and 1000.");
            }
            if (product.UnitPrice is < 0 or > MaximumWebSafeInteger)
            {
                Error(errors, "NEGATIVE_OR_INVALID_PRICE", path + "/unitPrice",
                    "Unit price must be a non-negative web-safe integer.");
            }
            if (product.PurchaseLimitPerSeason is <= 0)
            {
                Error(errors, "INVALID_PERSONAL_LIMIT", path + "/purchaseLimitPerSeason",
                    "Personal purchase limit must be null or positive.");
            }
            if (product.GlobalStock is <= 0 or > MaximumWebSafeInteger)
            {
                Error(errors, "INVALID_GLOBAL_STOCK", path + "/globalStock",
                    "Global stock must be null or a positive web-safe integer.");
            }
            if (product.AvailableFrom is not null && product.AvailableUntil is not null &&
                product.AvailableUntil <= product.AvailableFrom)
            {
                Error(errors, "INVALID_PRODUCT_WINDOW", path + "/availableUntil",
                    "Product availability must end after it starts.");
            }
            if (product.ItemGrants.Count is < 1 or > 100)
            {
                Error(errors, "INVALID_PRODUCT_GRANTS", path + "/itemGrants",
                    "A product must grant 1 to 100 distinct item entries.");
            }
            ReportDuplicates(product.ItemGrants.Select(grant => grant.ItemId), path + "/itemGrants",
                "DUPLICATE_PRODUCT_ITEM", errors);
            for (var grantIndex = 0; grantIndex < product.ItemGrants.Count; grantIndex++)
            {
                var grant = product.ItemGrants[grantIndex];
                var grantPath = $"{path}/itemGrants/{grantIndex}";
                if (!knownItems.Contains(grant.ItemId))
                {
                    Error(errors, "UNKNOWN_ITEM", grantPath + "/itemId",
                        $"Item '{grant.ItemId}' is not present in the approved catalog.");
                }
                if (grant.Quantity is <= 0 or > 1_000_000)
                {
                    Error(errors, "INVALID_ITEM_QUANTITY", grantPath + "/quantity",
                        "Item grant quantity must be between 1 and 1000000.");
                }
            }
        }
    }

    private static HashSet<string> ValidateZones(
        IReadOnlyList<ContentExchangeZoneDefinition> zones,
        List<ContentValidationIssue> errors)
    {
        ReportDuplicates(zones.Select(zone => zone.ZoneId), "/exchangeZones", "DUPLICATE_ZONE", errors);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < zones.Count; index++)
        {
            var zone = zones[index];
            var path = $"/exchangeZones/{index}";
            if (!ValidKey(zone.ZoneId, 64))
            {
                Error(errors, "INVALID_ZONE_ID", path + "/zoneId", "Zone ID is invalid.");
            }
            Required(errors, zone.DisplayName, 128, path + "/displayName", "ZONE_NAME_REQUIRED");
            Required(errors, zone.RouteHint, 512, path + "/routeHint", "ZONE_ROUTE_REQUIRED");
            if (!double.IsFinite(zone.MapX) || !double.IsFinite(zone.MapY) ||
                !double.IsFinite(zone.Radius) || zone.Radius is <= 0 or > 10_000)
            {
                Error(errors, "INVALID_ZONE_GEOMETRY", path,
                    "Zone coordinates must be finite and radius must be between 0 and 10000.");
            }
            if (zone.YieldMultiplierBasisPoints is < 1_000 or > 50_000)
            {
                Error(errors, "INVALID_ZONE_MULTIPLIER", path + "/yieldMultiplierBasisPoints",
                    "Zone yield multiplier must be between 1000 and 50000 basis points.");
            }
            var windowKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var windowIndex = 0; windowIndex < zone.OpenWindows.Count; windowIndex++)
            {
                var window = zone.OpenWindows[windowIndex];
                var windowPath = $"{path}/openWindows/{windowIndex}";
                var key = $"{window.DayOfWeek}|{window.OpensAt:HH:mm:ss}|{window.ClosesAt:HH:mm:ss}";
                if (!windowKeys.Add(key))
                {
                    Error(errors, "DUPLICATE_ZONE_WINDOW", windowPath, "Zone window is duplicated.");
                }
                if (window.OpensAt == window.ClosesAt)
                {
                    Error(errors, "INVALID_ZONE_WINDOW", windowPath,
                        "A zone window cannot open and close at the same time.");
                }
                if (window.GraceSeconds is < 0 or > 3600)
                {
                    Error(errors, "INVALID_ZONE_GRACE", windowPath + "/graceSeconds",
                        "Zone grace must be between 0 and 3600 seconds.");
                }
            }
            if (zone.Active)
            {
                active.Add(zone.ZoneId);
            }
        }
        return active;
    }

    private static HashSet<string> ValidateResources(
        IReadOnlyList<ContentResourceDefinition> resources,
        IReadOnlySet<string> knownItems,
        IReadOnlySet<string> activeZones,
        List<ContentValidationIssue> errors)
    {
        ReportDuplicates(resources.Select(resource => resource.ItemId), "/resources",
            "DUPLICATE_RESOURCE_ITEM", errors);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];
            var path = $"/resources/{index}";
            if (!knownItems.Contains(resource.ItemId))
            {
                Error(errors, "UNKNOWN_ITEM", path + "/itemId",
                    $"Item '{resource.ItemId}' is not present in the approved catalog.");
            }
            Required(errors, resource.DisplayName, 128, path + "/displayName", "RESOURCE_NAME_REQUIRED");
            Required(errors, resource.Category, 64, path + "/category", "RESOURCE_CATEGORY_REQUIRED");
            ValidateTags(resource.Tags, path + "/tags", errors);
            if (resource.UnitValue is <= 0 or > MaximumWebSafeInteger)
            {
                Error(errors, "NEGATIVE_OR_INVALID_RESOURCE_VALUE", path + "/unitValue",
                    "Resource unit value must be a positive web-safe integer.");
            }
            if (resource.Currency != ExtractionCurrency.SeasonVoucher)
            {
                Error(errors, "UNSUPPORTED_RESOURCE_CURRENCY", path + "/currency",
                    "Scheme A resource settlement must credit the weekly SeasonVoucher currency.");
            }
            if (resource.ExchangeZoneIds.Count == 0)
            {
                Error(errors, "RESOURCE_ZONE_REQUIRED", path + "/exchangeZoneIds",
                    "Every sellable resource must reference at least one active exchange zone.");
            }
            ReportDuplicates(resource.ExchangeZoneIds, path + "/exchangeZoneIds",
                "DUPLICATE_RESOURCE_ZONE", errors);
            foreach (var zoneId in resource.ExchangeZoneIds)
            {
                if (!activeZones.Contains(zoneId))
                {
                    Error(errors, "INVALID_OR_INACTIVE_ZONE", path + "/exchangeZoneIds",
                        $"Exchange zone '{zoneId}' does not exist or is inactive.");
                }
            }
            if (resource.Active)
            {
                active.Add(resource.ItemId);
            }
        }
        return active;
    }

    private static Dictionary<string, ContentTaskCadence> ValidateTasks(
        IReadOnlyList<ContentTaskDefinition> tasks,
        IReadOnlySet<string> knownItems,
        IReadOnlySet<string> activeResources,
        IReadOnlySet<string> activeZones,
        List<ContentValidationIssue> errors)
    {
        ReportDuplicates(tasks.Select(task => task.TaskKey), "/tasks", "DUPLICATE_TASK", errors);
        var active = new Dictionary<string, ContentTaskCadence>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            var path = $"/tasks/{index}";
            if (!ValidKey(task.TaskKey, 64))
            {
                Error(errors, "INVALID_TASK_KEY", path + "/taskKey", "Task key is invalid.");
            }
            Required(errors, task.DisplayName, 128, path + "/displayName", "TASK_NAME_REQUIRED");
            Required(errors, task.Description, 512, path + "/description", "TASK_DESCRIPTION_REQUIRED");
            if (task.TargetAmount is <= 0 or > MaximumWebSafeInteger)
            {
                Error(errors, "INVALID_TASK_TARGET", path + "/targetAmount",
                    "Task target must be a positive web-safe integer.");
            }
            if (task.EventKind == ContentTaskEventKind.ResourceItemSettled)
            {
                if (task.TargetItemId is null || !knownItems.Contains(task.TargetItemId) ||
                    !activeResources.Contains(task.TargetItemId))
                {
                    Error(errors, "INVALID_TASK_ITEM", path + "/targetItemId",
                        "Resource item tasks must target an active allow-listed resource.");
                }
            }
            else if (task.TargetItemId is not null)
            {
                Error(errors, "UNEXPECTED_TASK_ITEM", path + "/targetItemId",
                    "This task event kind cannot target an item.");
            }
            if (task.EventKind == ContentTaskEventKind.CurrencySpent && task.TargetCurrency is null)
            {
                Error(errors, "TASK_CURRENCY_REQUIRED", path + "/targetCurrency",
                    "Currency-spend tasks require a target currency.");
            }
            if (task.EventKind != ContentTaskEventKind.CurrencySpent && task.TargetCurrency is not null)
            {
                Error(errors, "UNEXPECTED_TASK_CURRENCY", path + "/targetCurrency",
                    "Only currency-spend tasks may specify a target currency.");
            }
            foreach (var zoneId in task.ExchangeZoneIds)
            {
                if (!activeZones.Contains(zoneId))
                {
                    Error(errors, "INVALID_OR_INACTIVE_ZONE", path + "/exchangeZoneIds",
                        $"Task zone '{zoneId}' does not exist or is inactive.");
                }
            }
            if (task.Reward.Amount is < 0 or > MaximumWebSafeInteger ||
                task.Reward.RankingPoints is < 0 or > 1_000_000 ||
                (task.Reward.Amount == 0 && task.Reward.RankingPoints == 0))
            {
                Error(errors, "INVALID_TASK_REWARD", path + "/reward",
                    "A task reward must provide a bounded currency amount or ranking points.");
            }
            if (task.Active)
            {
                active[task.TaskKey] = task.Cadence;
            }
        }
        return active;
    }

    /// <summary>
    /// Proves the smallest economy loop that can be derived from this schema:
    /// buy one product, then directly sell any allow-listed items it grants in
    /// the most profitable eligible active zone. The calculation intentionally
    /// matches settlement: zone-adjusted unit value is rounded up before it is
    /// multiplied by quantity.
    ///
    /// This is not a crafting-arbitrage solver. The content document contains no
    /// recipe graph, exchange fees, player-to-player pricing or transformation
    /// costs, so those paths are reported as indeterminate instead of being
    /// treated as safe.
    /// </summary>
    private static void ValidateDirectResaleArbitrage(
        IReadOnlyList<ContentProductDefinition> products,
        IReadOnlyList<ContentResourceDefinition> resources,
        IReadOnlyList<ContentExchangeZoneDefinition> zones,
        List<ContentValidationIssue> errors,
        List<ContentValidationIssue> warnings)
    {
        var activeZones = zones
            .Where(zone => zone.Active && zone.OpenWindows.Any(window => window.OpensAt != window.ClosesAt))
            .GroupBy(zone => zone.ZoneId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var activeResources = resources
            .Where(resource => resource.Active)
            .GroupBy(resource => resource.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        Warning(warnings, "INDIRECT_ARBITRAGE_NOT_EVALUATED", "/products",
            "No crafting or processing recipe graph is present in this content schema. " +
            "Only direct product-grant -> allow-listed resource resale paths are provable; " +
            "indirect conversion paths remain indeterminate.");

        for (var productIndex = 0; productIndex < products.Count; productIndex++)
        {
            var product = products[productIndex];
            if (!product.Active)
            {
                continue;
            }

            var comparableProceeds = 0m;
            var comparablePaths = new List<string>();
            var indeterminatePaths = new List<string>();

            for (var grantIndex = 0; grantIndex < product.ItemGrants.Count; grantIndex++)
            {
                var grant = product.ItemGrants[grantIndex];
                if (!activeResources.TryGetValue(grant.ItemId, out var resource))
                {
                    indeterminatePaths.Add($"{grant.ItemId}: not an active sell allow-list resource");
                    continue;
                }
                if (resource.Currency != product.PriceCurrency)
                {
                    indeterminatePaths.Add(
                        $"{grant.ItemId}: resale credits {resource.Currency}, product costs {product.PriceCurrency}");
                    continue;
                }

                var eligibleZones = resource.ExchangeZoneIds
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(zoneId => activeZones.GetValueOrDefault(zoneId))
                    .Where(zone => zone is not null)
                    .Cast<ContentExchangeZoneDefinition>()
                    .ToArray();
                if (eligibleZones.Length == 0)
                {
                    indeterminatePaths.Add($"{grant.ItemId}: no eligible active exchange zone");
                    continue;
                }

                var bestZone = eligibleZones
                    .OrderByDescending(zone => zone.YieldMultiplierBasisPoints)
                    .ThenBy(zone => zone.ZoneId, StringComparer.OrdinalIgnoreCase)
                    .First();
                var effectiveUnitValue = Math.Ceiling(
                    (decimal)resource.UnitValue * bestZone.YieldMultiplierBasisPoints / 10_000m);
                var lineProceeds = effectiveUnitValue * grant.Quantity;
                comparableProceeds += lineProceeds;
                comparablePaths.Add(
                    $"{grant.ItemId} x{grant.Quantity} @ {effectiveUnitValue:0} " +
                    $"({bestZone.ZoneId}, {bestZone.YieldMultiplierBasisPoints}bp) = {lineProceeds:0}");
            }

            var productPath = $"/products/{productIndex}";
            if (comparableProceeds > product.UnitPrice)
            {
                Error(errors, "PROVEN_DIRECT_RESALE_ARBITRAGE", productPath + "/unitPrice",
                    $"Direct resale arbitrage is proven for SKU '{product.Sku}': one purchase costs " +
                    $"{product.UnitPrice} {product.PriceCurrency}, while directly reselling comparable grants " +
                    $"returns {comparableProceeds:0} {product.PriceCurrency} at maximum eligible zone multipliers " +
                    $"(profit {comparableProceeds - product.UnitPrice:0}). Path: " +
                    string.Join("; ", comparablePaths) + ".");
            }
            else if (comparablePaths.Count > 0 && comparableProceeds == product.UnitPrice)
            {
                Warning(warnings, "DIRECT_RESALE_BREAK_EVEN", productPath + "/unitPrice",
                    $"SKU '{product.Sku}' has a break-even direct resale path at " +
                    $"{comparableProceeds:0} {product.PriceCurrency}. Path: " +
                    string.Join("; ", comparablePaths) + ".");
            }

            if (indeterminatePaths.Count > 0)
            {
                Warning(warnings, "DIRECT_RESALE_ANALYSIS_INCOMPLETE", productPath + "/itemGrants",
                    $"Direct resale analysis for SKU '{product.Sku}' is incomplete and is not proof of safety. " +
                    string.Join("; ", indeterminatePaths) +
                    ". Missing crafting/processing recipes also leave indirect paths indeterminate.");
            }
        }
    }

    private static void ValidateRotation(
        ContentRotationPolicy rotation,
        EconomyContentDependencies dependencies,
        IReadOnlyDictionary<string, ContentTaskCadence> activeTasks,
        IReadOnlySet<string> activeZones,
        List<ContentValidationIssue> errors,
        List<ContentValidationIssue> warnings)
    {
        if (!string.Equals(rotation.RulesVersion, dependencies.RulesVersion, StringComparison.Ordinal))
        {
            Error(errors, "ROTATION_RULES_VERSION_MISMATCH", "/rotation/rulesVersion",
                "Rotation rulesVersion must equal the document dependency rulesVersion.");
        }
        if (rotation.AlgorithmVersion <= 0)
        {
            Error(errors, "ROTATION_ALGORITHM_VERSION_REQUIRED", "/rotation/algorithmVersion",
                "Rotation algorithm version must be positive.");
        }
        Required(errors, rotation.SeedNamespace, 64, "/rotation/seedNamespace",
            "ROTATION_SEED_NAMESPACE_REQUIRED");
        ValidatePool(rotation.DailyTaskPool, rotation.DailyTaskCount, ContentTaskCadence.Daily,
            activeTasks, "/rotation/dailyTaskPool", errors);
        ValidatePool(rotation.WeeklyTaskPool, rotation.WeeklyTaskCount, ContentTaskCadence.Weekly,
            activeTasks, "/rotation/weeklyTaskPool", errors);
        ReportDuplicates(rotation.HotspotZonePool, "/rotation/hotspotZonePool",
            "DUPLICATE_HOTSPOT_ZONE", errors);
        if (rotation.DailyHotspotCount < 0 || rotation.DailyHotspotCount > rotation.HotspotZonePool.Count)
        {
            Error(errors, "INVALID_HOTSPOT_COUNT", "/rotation/dailyHotspotCount",
                "Daily hotspot count cannot exceed the hotspot pool.");
        }
        foreach (var zoneId in rotation.HotspotZonePool)
        {
            if (!activeZones.Contains(zoneId))
            {
                Error(errors, "INVALID_OR_INACTIVE_ZONE", "/rotation/hotspotZonePool",
                    $"Hotspot zone '{zoneId}' does not exist or is inactive.");
            }
        }
        if (rotation.DailyTaskPool.Count < 3)
        {
            Warning(warnings, "SMALL_DAILY_TASK_POOL", "/rotation/dailyTaskPool",
                "The launch target requires at least three daily task definitions.");
        }
        if (rotation.WeeklyTaskPool.Count < 3)
        {
            Warning(warnings, "SMALL_WEEKLY_TASK_POOL", "/rotation/weeklyTaskPool",
                "The launch target requires at least three weekly task definitions.");
        }
    }

    private static void ValidatePool(
        IReadOnlyList<string> pool,
        int count,
        ContentTaskCadence cadence,
        IReadOnlyDictionary<string, ContentTaskCadence> tasks,
        string path,
        List<ContentValidationIssue> errors)
    {
        ReportDuplicates(pool, path, "DUPLICATE_ROTATION_TASK", errors);
        if (count < 0 || count > pool.Count)
        {
            Error(errors, "INVALID_ROTATION_TASK_COUNT", path,
                "Selected task count cannot exceed the task pool.");
        }
        foreach (var key in pool)
        {
            if (!tasks.TryGetValue(key, out var actualCadence) || actualCadence != cadence)
            {
                Error(errors, "INVALID_ROTATION_TASK", path,
                    $"Task '{key}' is missing, inactive or has the wrong cadence.");
            }
        }
    }

    private static void ValidateTags(
        IReadOnlyList<string> tags,
        string path,
        List<ContentValidationIssue> errors)
    {
        if (tags.Count > 32 || tags.Any(tag => tag.Length is < 1 or > 64))
        {
            Error(errors, "INVALID_TAGS", path, "Tags must contain at most 32 non-empty values of at most 64 characters.");
        }
        ReportDuplicates(tags, path, "DUPLICATE_TAG", errors);
    }

    private static void ValidateTimeZone(string timeZoneId, List<ContentValidationIssue> errors)
    {
        if (timeZoneId.Length == 0)
        {
            return;
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(timeZoneId, "Asia/Shanghai", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                Error(errors, "INVALID_TIME_ZONE", "/timeZoneId", $"Time zone '{timeZoneId}' is unavailable.");
            }
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            Error(errors, "INVALID_TIME_ZONE", "/timeZoneId", $"Time zone '{timeZoneId}' is invalid.");
        }
    }

    private static void Expected(
        List<ContentValidationIssue> errors,
        string actual,
        string? expected,
        string path,
        string code)
    {
        if (!string.IsNullOrWhiteSpace(expected) &&
            !string.Equals(actual, expected.Trim(), StringComparison.Ordinal))
        {
            Error(errors, code, path, $"Version '{actual}' does not match the active dependency '{expected.Trim()}'.");
        }
    }

    private static void Required(
        List<ContentValidationIssue> errors,
        string value,
        int maximumLength,
        string path,
        string code)
    {
        if (value.Length is < 1 || value.Length > maximumLength)
        {
            Error(errors, code, path, $"Value must contain 1 to {maximumLength} characters.");
        }
    }

    private static void ReportDuplicates(
        IEnumerable<string> values,
        string path,
        string code,
        List<ContentValidationIssue> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                Error(errors, code, path, $"Duplicate value '{value}'.");
            }
        }
    }

    private static bool ValidKey(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static void Error(List<ContentValidationIssue> issues, string code, string path, string message) =>
        issues.Add(new ContentValidationIssue(code, path, message));

    private static void Warning(List<ContentValidationIssue> issues, string code, string path, string message) =>
        issues.Add(new ContentValidationIssue(code, path, message));
}

public static class EconomyContentDiff
{
    public static IReadOnlyList<ContentDiffEntry> Create(
        EconomyContentDefinition? before,
        EconomyContentDefinition after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var left = before is null ? new Dictionary<string, string>(StringComparer.Ordinal) : Flatten(before);
        var right = Flatten(after);
        return left.Keys.Concat(right.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var hadBefore = left.TryGetValue(path, out var beforeValue);
                var hasAfter = right.TryGetValue(path, out var afterValue);
                if (!hadBefore)
                {
                    return new ContentDiffEntry(path, ContentDiffKind.Added, null, afterValue);
                }
                if (!hasAfter)
                {
                    return new ContentDiffEntry(path, ContentDiffKind.Removed, beforeValue, null);
                }
                return new ContentDiffEntry(path, ContentDiffKind.Changed, beforeValue, afterValue);
            })
            .Where(entry => !string.Equals(entry.Before, entry.After, StringComparison.Ordinal))
            .ToArray();
    }

    private static Dictionary<string, string> Flatten(EconomyContentDefinition definition)
    {
        using var document = JsonDocument.Parse(EconomyContentCanonicalizer.Serialize(definition));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        FlattenElement(document.RootElement, "", result);
        return result;
    }

    private static void FlattenElement(JsonElement element, string path, Dictionary<string, string> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Length == 0)
            {
                result[path.Length == 0 ? "/" : path] = "{}";
            }
            foreach (var property in properties)
            {
                FlattenElement(property.Value, path + "/" + Escape(property.Name), result);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var values = element.EnumerateArray().ToArray();
            if (values.Length == 0)
            {
                result[path] = "[]";
            }
            for (var index = 0; index < values.Length; index++)
            {
                FlattenElement(values[index], path + "/" + index, result);
            }
            return;
        }
        result[path.Length == 0 ? "/" : path] = element.GetRawText();
    }

    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}

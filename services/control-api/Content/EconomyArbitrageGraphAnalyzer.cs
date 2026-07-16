using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Content;

public sealed record ContentCurrencyShadowRate(
    ExtractionCurrency Currency,
    long ShadowUnitsPerCurrencyUnit);

public sealed record ContentResourceShadowCost(
    string ItemId,
    ExtractionCurrency Currency,
    long UnitCost);

public sealed record ContentResourceCategoryPolicy(
    string Category,
    int TargetRecoveryBasisPoints,
    int RiskBufferBasisPoints);

public sealed record ContentItemTransformation(
    string TransformationId,
    string DisplayName,
    IReadOnlyList<ContentItemGrant> Inputs,
    IReadOnlyList<ContentItemGrant> Outputs,
    ExtractionCurrency? FeeCurrency = null,
    long FeeAmount = 0,
    bool Active = true,
    string EvidenceNote = "");

/// <summary>
/// Attests structural completeness of an operator-maintained economic shadow
/// graph. It is deliberately not presented as a Palworld recipe database.
/// </summary>
public sealed record ContentEconomyGraphAttestation(
    string EvidenceKind,
    string CatalogRevision,
    string AttestedBy,
    DateTimeOffset AttestedAt,
    bool ReachableGraphComplete,
    IReadOnlyList<string> CoveredItemIds);

/// <summary>
/// Published balance evidence for Scheme A. The item transformation graph is
/// required to be acyclic so every product inventory state can be exhaustively
/// enumerated. Hitting the state limit is a validation failure, never a claim
/// that an unexamined graph is safe.
/// </summary>
public sealed record ContentEconomyBalancePolicy(
    string PolicyVersion,
    IReadOnlyList<ContentCurrencyShadowRate> CurrencyShadowRates,
    IReadOnlyList<ContentResourceShadowCost> ResourceShadowCosts,
    IReadOnlyList<ContentResourceCategoryPolicy> ResourceCategoryPolicies,
    IReadOnlyList<ContentItemTransformation> Transformations,
    int StateLimitPerProduct = 20_000,
    ContentEconomyGraphAttestation? Attestation = null);

public sealed record EconomyBalanceAnalysisIssue(
    string Code,
    string Path,
    string Message);

public sealed record EconomyArbitrageFinding(
    string ProductSku,
    ExtractionCurrency PurchaseCurrency,
    long PurchasePrice,
    long PurchaseCostShadow,
    long AdditionalFeeShadow,
    long ReturnShadow,
    IReadOnlyDictionary<ExtractionCurrency, long> Returns,
    IReadOnlyList<string> Path);

public sealed record EconomyResourcePricingAssessment(
    string ItemId,
    string Category,
    long ReferenceCostShadow,
    long MaximumReturnShadow,
    int ActualRecoveryBasisPoints,
    int MaximumRecoveryBasisPoints,
    string ZoneId,
    int ZoneMultiplierBasisPoints);

public sealed record EconomyArbitrageGraphAnalysis(
    IReadOnlyList<EconomyBalanceAnalysisIssue> Errors,
    IReadOnlyList<EconomyBalanceAnalysisIssue> Warnings,
    IReadOnlyList<EconomyArbitrageFinding> ProfitablePaths,
    IReadOnlyList<EconomyArbitrageFinding> BreakEvenPaths,
    IReadOnlyList<EconomyResourcePricingAssessment> ResourcePricing,
    int ProductStatesEvaluated,
    bool Complete);

public static class EconomyArbitrageGraphAnalyzer
{
    public const string OperationalShadowGraphEvidenceKind =
        "operator-audited-economic-shadow-graph";

    public static EconomyArbitrageGraphAnalysis Analyze(
        EconomyContentDefinition definition,
        ContentEconomyBalancePolicy policy,
        IReadOnlySet<string>? knownItemIds = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(policy);

        var errors = new List<EconomyBalanceAnalysisIssue>();
        var warnings = new List<EconomyBalanceAnalysisIssue>();
        var profitable = new List<EconomyArbitrageFinding>();
        var breakEven = new List<EconomyArbitrageFinding>();
        var pricing = new List<EconomyResourcePricingAssessment>();

        var rates = UniqueMap(
            policy.CurrencyShadowRates,
            rate => rate.Currency,
            "/balancePolicy/currencyShadowRates",
            "DUPLICATE_CURRENCY_SHADOW_RATE",
            errors);
        foreach (var currency in Enum.GetValues<ExtractionCurrency>())
        {
            if (!rates.TryGetValue(currency, out var rate) || rate.ShadowUnitsPerCurrencyUnit <= 0)
            {
                Add(errors, "CURRENCY_SHADOW_RATE_REQUIRED", "/balancePolicy/currencyShadowRates",
                    $"A positive shadow rate is required for {currency}.");
            }
        }

        var resourceCosts = UniqueMap(
            policy.ResourceShadowCosts,
            cost => cost.ItemId,
            "/balancePolicy/resourceShadowCosts",
            "DUPLICATE_RESOURCE_SHADOW_COST",
            errors,
            StringComparer.OrdinalIgnoreCase);
        var categoryPolicies = UniqueMap(
            policy.ResourceCategoryPolicies,
            category => category.Category,
            "/balancePolicy/resourceCategoryPolicies",
            "DUPLICATE_RESOURCE_CATEGORY_POLICY",
            errors,
            StringComparer.OrdinalIgnoreCase);
        var transformations = policy.Transformations.Where(item => item.Active).ToArray();
        ValidatePolicy(
            definition,
            policy,
            transformations,
            rates,
            resourceCosts,
            categoryPolicies,
            knownItemIds,
            errors,
            warnings);
        ValidateTransformationDag(transformations, errors);

        var activeZones = definition.ExchangeZones
            .Where(zone => zone.Active && zone.OpenWindows.Any(window => window.OpensAt != window.ClosesAt))
            .GroupBy(zone => zone.ZoneId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var activeResources = definition.Resources
            .Where(resource => resource.Active)
            .GroupBy(resource => resource.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var saleValues = BuildSaleValues(definition, activeResources, activeZones, errors);

        ValidateGraphCoverage(
            definition,
            policy,
            transformations,
            activeResources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            saleValues.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            errors,
            warnings);

        try
        {
            AssessResourcePricing(
                activeResources,
                saleValues,
                rates,
                resourceCosts,
                categoryPolicies,
                pricing,
                errors);
        }
        catch (OverflowException)
        {
            Add(errors, "BALANCE_POLICY_NUMERIC_OVERFLOW", "/balancePolicy",
                "Shadow-rate or recovery-policy arithmetic exceeded the supported 64-bit range.");
        }

        var statesEvaluated = 0;
        if (errors.Count == 0)
        {
            var anyTransformationApplied = false;
            var anyTransformedSaleState = false;
            foreach (var product in definition.Products.Where(product => product.Active))
            {
                ProductAnalysis result;
                try
                {
                    result = AnalyzeProduct(
                        product,
                        EconomyContentRuntimeService.CalculateMinimumPossibleEffectiveUnitPrice(
                            definition,
                            product),
                        transformations,
                        saleValues,
                        rates,
                        policy.StateLimitPerProduct);
                }
                catch (OverflowException)
                {
                    Add(errors, "ARBITRAGE_ANALYSIS_NUMERIC_OVERFLOW", "/products",
                        $"SKU '{product.Sku}' exceeded the supported 64-bit shadow-value range. " +
                        "Publishing is blocked instead of treating an incomplete path as safe.");
                    continue;
                }
                statesEvaluated = checked(statesEvaluated + result.StatesEvaluated);
                anyTransformationApplied |= result.AnyTransformationApplied;
                anyTransformedSaleState |= result.AnyTransformedSaleState;
                if (result.LimitExceeded)
                {
                    Add(errors, "ARBITRAGE_ANALYSIS_STATE_LIMIT", "/balancePolicy/stateLimitPerProduct",
                        $"SKU '{product.Sku}' exceeded {policy.StateLimitPerProduct} reachable inventory states. " +
                        "Publishing is blocked until the graph is simplified or the reviewed limit is raised.");
                    continue;
                }
                if (result.UnrecoverableTerminalItemIds.Count > 0)
                {
                    var productIndex = definition.Products.ToList().FindIndex(candidate =>
                        string.Equals(candidate.Sku, product.Sku, StringComparison.OrdinalIgnoreCase));
                    Add(errors, "BALANCE_GRAPH_UNRECOVERABLE_REACHABLE_STATE",
                        productIndex >= 0 ? $"/products/{productIndex}/itemGrants" : "/products",
                        $"SKU '{product.Sku}' reaches a terminal inventory state containing non-sellable " +
                        $"ItemIDs [{string.Join(", ", result.UnrecoverableTerminalItemIds)}]. " +
                        "Every actually reachable state must still have a complete path to a sellable resource.");
                }
                if (result.BestFinding is { } finding)
                {
                    var totalCost = checked(finding.PurchaseCostShadow + finding.AdditionalFeeShadow);
                    if (finding.ReturnShadow > totalCost)
                    {
                        profitable.Add(finding);
                    }
                    else if (finding.ReturnShadow == totalCost && finding.ReturnShadow > 0)
                    {
                        breakEven.Add(finding);
                    }
                }
            }
            if (!anyTransformationApplied)
            {
                Add(errors, "BALANCE_TRANSFORMATION_LAYER_NOT_REACHABLE", "/balancePolicy/transformations",
                    "No active shadow transformation can actually be applied to an active product inventory. " +
                    "Item-level graph adjacency is not sufficient when a transformation has multiple inputs.");
            }
            else if (!anyTransformedSaleState)
            {
                Add(errors, "BALANCE_TRANSFORMED_SALE_PATH_REQUIRED", "/balancePolicy/transformations",
                    "At least one actually reachable BUY -> TRANSFORM -> SELL state is required.");
            }
        }

        foreach (var finding in profitable)
        {
            var productIndex = definition.Products.ToList().FindIndex(product =>
                string.Equals(product.Sku, finding.ProductSku, StringComparison.OrdinalIgnoreCase));
            Add(errors, "PROVEN_REACHABLE_ARBITRAGE",
                productIndex >= 0 ? $"/products/{productIndex}/unitPrice" : "/products",
                $"SKU '{finding.ProductSku}' costs {finding.PurchasePrice} {finding.PurchaseCurrency} " +
                $"({finding.PurchaseCostShadow} shadow units) plus {finding.AdditionalFeeShadow} shadow fee, " +
                $"but the reachable path returns {finding.ReturnShadow} shadow units. Path: " +
                string.Join(" -> ", finding.Path) + ".");
        }
        foreach (var finding in breakEven)
        {
            var productIndex = definition.Products.ToList().FindIndex(product =>
                string.Equals(product.Sku, finding.ProductSku, StringComparison.OrdinalIgnoreCase));
            Add(warnings, "REACHABLE_ARBITRAGE_BREAK_EVEN",
                productIndex >= 0 ? $"/products/{productIndex}/unitPrice" : "/products",
                $"SKU '{finding.ProductSku}' has a reachable break-even path: " +
                string.Join(" -> ", finding.Path) + ".");
        }

        return new EconomyArbitrageGraphAnalysis(
            errors,
            warnings,
            profitable,
            breakEven,
            pricing,
            statesEvaluated,
            Complete: errors.Count == 0);
    }

    private static ProductAnalysis AnalyzeProduct(
        ContentProductDefinition product,
        long purchasePrice,
        IReadOnlyList<ContentItemTransformation> transformations,
        IReadOnlyDictionary<string, SaleValue> saleValues,
        IReadOnlyDictionary<ExtractionCurrency, ContentCurrencyShadowRate> rates,
        int stateLimit)
    {
        var inventory = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in product.ItemGrants)
        {
            inventory[grant.ItemId] = checked(inventory.GetValueOrDefault(grant.ItemId) + grant.Quantity);
        }

        var start = new InventoryState(
            inventory,
            0,
            [$"BUY {product.Sku} ({purchasePrice} {product.PriceCurrency}, minimum event-adjusted price)"]);
        var queue = new Queue<InventoryState>();
        queue.Enqueue(start);
        var visited = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [InventoryKey(inventory)] = 0
        };
        EconomyArbitrageFinding? best = null;
        var states = 0;
        var anyTransformationApplied = false;
        var anyTransformedSaleState = false;
        var unrecoverableTerminalItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            states++;
            var returns = CalculateReturns(state.Inventory, saleValues);
            var returnShadow = returns.Sum(pair => checked(pair.Value * rates[pair.Key].ShadowUnitsPerCurrencyUnit));
            var costShadow = checked(purchasePrice * rates[product.PriceCurrency].ShadowUnitsPerCurrencyUnit);
            var path = state.Path;
            if (returnShadow > 0)
            {
                path = [.. state.Path, "SELL " + string.Join(", ", returns
                    .OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Value} {pair.Key}"))];
                if (state.Path.Count > 1)
                {
                    anyTransformedSaleState = true;
                }
            }
            var candidate = new EconomyArbitrageFinding(
                product.Sku,
                product.PriceCurrency,
                purchasePrice,
                costShadow,
                state.FeeShadow,
                returnShadow,
                returns,
                path);
            if (best is null || Profit(candidate) > Profit(best) ||
                (Profit(candidate) == Profit(best) && candidate.Path.Count < best.Path.Count))
            {
                best = candidate;
            }

            var applicableTransformations = transformations
                .Where(transformation => CanApply(state.Inventory, transformation.Inputs))
                .ToArray();
            if (applicableTransformations.Length == 0)
            {
                foreach (var itemId in state.Inventory.Keys.Where(itemId => !saleValues.ContainsKey(itemId)))
                {
                    unrecoverableTerminalItemIds.Add(itemId);
                }
            }

            foreach (var transformation in applicableTransformations)
            {
                anyTransformationApplied = true;
                var nextInventory = new SortedDictionary<string, long>(state.Inventory, StringComparer.OrdinalIgnoreCase);
                foreach (var input in transformation.Inputs)
                {
                    var next = checked(nextInventory[input.ItemId] - input.Quantity);
                    if (next == 0)
                    {
                        nextInventory.Remove(input.ItemId);
                    }
                    else
                    {
                        nextInventory[input.ItemId] = next;
                    }
                }
                foreach (var output in transformation.Outputs)
                {
                    nextInventory[output.ItemId] = checked(
                        nextInventory.GetValueOrDefault(output.ItemId) + output.Quantity);
                }
                var nextFee = state.FeeShadow;
                if (transformation.FeeCurrency is { } feeCurrency && transformation.FeeAmount > 0)
                {
                    nextFee = checked(nextFee + transformation.FeeAmount *
                        rates[feeCurrency].ShadowUnitsPerCurrencyUnit);
                }
                var key = InventoryKey(nextInventory);
                if (visited.TryGetValue(key, out var previousFee) && previousFee <= nextFee)
                {
                    continue;
                }
                if (visited.Count >= stateLimit)
                {
                    return new ProductAnalysis(
                        best,
                        states,
                        true,
                        anyTransformationApplied,
                        anyTransformedSaleState,
                        unrecoverableTerminalItemIds.OrderBy(itemId => itemId, StringComparer.OrdinalIgnoreCase).ToArray());
                }
                visited[key] = nextFee;
                queue.Enqueue(new InventoryState(
                    nextInventory,
                    nextFee,
                    [.. state.Path, $"TRANSFORM {transformation.TransformationId}"]));
            }
        }
        return new ProductAnalysis(
            best,
            states,
            false,
            anyTransformationApplied,
            anyTransformedSaleState,
            unrecoverableTerminalItemIds.OrderBy(itemId => itemId, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static long Profit(EconomyArbitrageFinding finding) => checked(
        finding.ReturnShadow - finding.PurchaseCostShadow - finding.AdditionalFeeShadow);

    private static IReadOnlyDictionary<ExtractionCurrency, long> CalculateReturns(
        IReadOnlyDictionary<string, long> inventory,
        IReadOnlyDictionary<string, SaleValue> saleValues)
    {
        var returns = new SortedDictionary<ExtractionCurrency, long>();
        foreach (var item in inventory)
        {
            if (!saleValues.TryGetValue(item.Key, out var sale))
            {
                continue;
            }
            returns[sale.Currency] = checked(
                returns.GetValueOrDefault(sale.Currency) + item.Value * sale.UnitValue);
        }
        return returns;
    }

    private static Dictionary<string, SaleValue> BuildSaleValues(
        EconomyContentDefinition definition,
        IReadOnlyDictionary<string, ContentResourceDefinition> resources,
        IReadOnlyDictionary<string, ContentExchangeZoneDefinition> zones,
        ICollection<EconomyBalanceAnalysisIssue> errors)
    {
        var result = new Dictionary<string, SaleValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources.Values)
        {
            var best = resource.ExchangeZoneIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(zoneId => zones.GetValueOrDefault(zoneId))
                .Where(zone => zone is not null)
                .Cast<ContentExchangeZoneDefinition>()
                .Select(zone => new
                {
                    Zone = zone,
                    Multiplier = EconomyContentRuntimeService
                        .CalculateMaximumPossibleZoneYieldMultiplierBasisPoints(definition, zone)
                })
                .OrderByDescending(value => value.Multiplier)
                .ThenBy(value => value.Zone.ZoneId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (best is null)
            {
                Add(errors, "RESOURCE_HAS_NO_REACHABLE_SALE_ZONE", "/resources",
                    $"Resource '{resource.ItemId}' has no active scheduled sale zone. Graph publication is blocked.");
                continue;
            }
            result[resource.ItemId] = new SaleValue(
                resource.Currency,
                EconomyContentRuntimeService.CalculateEffectiveResourceUnitValue(
                    resource.UnitValue,
                    best.Multiplier),
                best.Zone.ZoneId,
                best.Multiplier);
        }
        return result;
    }

    private static void AssessResourcePricing(
        IReadOnlyDictionary<string, ContentResourceDefinition> resources,
        IReadOnlyDictionary<string, SaleValue> saleValues,
        IReadOnlyDictionary<ExtractionCurrency, ContentCurrencyShadowRate> rates,
        IReadOnlyDictionary<string, ContentResourceShadowCost> costs,
        IReadOnlyDictionary<string, ContentResourceCategoryPolicy> categories,
        ICollection<EconomyResourcePricingAssessment> assessments,
        ICollection<EconomyBalanceAnalysisIssue> errors)
    {
        foreach (var resource in resources.Values.OrderBy(resource => resource.ItemId, StringComparer.OrdinalIgnoreCase))
        {
            if (!saleValues.TryGetValue(resource.ItemId, out var sale))
            {
                continue;
            }
            if (!costs.TryGetValue(resource.ItemId, out var cost) || cost.UnitCost <= 0 ||
                !rates.TryGetValue(cost.Currency, out var costRate) ||
                !rates.TryGetValue(sale.Currency, out var saleRate))
            {
                continue;
            }
            if (!categories.TryGetValue(resource.Category, out var category))
            {
                continue;
            }
            var referenceShadow = checked(cost.UnitCost * costRate.ShadowUnitsPerCurrencyUnit);
            var returnShadow = checked(sale.UnitValue * saleRate.ShadowUnitsPerCurrencyUnit);
            var actual = checked((int)Math.Min(int.MaxValue,
                (returnShadow * 10_000L + referenceShadow - 1) / referenceShadow));
            var maximum = category.TargetRecoveryBasisPoints - category.RiskBufferBasisPoints;
            assessments.Add(new EconomyResourcePricingAssessment(
                resource.ItemId,
                resource.Category,
                referenceShadow,
                returnShadow,
                actual,
                maximum,
                sale.ZoneId,
                sale.ZoneMultiplierBasisPoints));
            if (actual > maximum)
            {
                Add(errors, "RESOURCE_RECOVERY_POLICY_EXCEEDED", "/resources",
                    $"Resource '{resource.ItemId}' recovers {actual}bp of its reference shadow cost at " +
                    $"{sale.ZoneId} ({sale.ZoneMultiplierBasisPoints}bp), above category '{resource.Category}' " +
                    $"limit {maximum}bp after risk buffer.");
            }
        }
    }

    private static void ValidatePolicy(
        EconomyContentDefinition definition,
        ContentEconomyBalancePolicy policy,
        IReadOnlyList<ContentItemTransformation> transformations,
        IReadOnlyDictionary<ExtractionCurrency, ContentCurrencyShadowRate> rates,
        IReadOnlyDictionary<string, ContentResourceShadowCost> costs,
        IReadOnlyDictionary<string, ContentResourceCategoryPolicy> categories,
        IReadOnlySet<string>? knownItemIds,
        ICollection<EconomyBalanceAnalysisIssue> errors,
        ICollection<EconomyBalanceAnalysisIssue> warnings)
    {
        if (string.IsNullOrWhiteSpace(policy.PolicyVersion) || policy.PolicyVersion.Length > 64)
        {
            Add(errors, "BALANCE_POLICY_VERSION_REQUIRED", "/balancePolicy/policyVersion",
                "Balance policy version must contain 1 to 64 characters.");
        }
        if (policy.StateLimitPerProduct is < 100 or > 100_000)
        {
            Add(errors, "INVALID_ARBITRAGE_STATE_LIMIT", "/balancePolicy/stateLimitPerProduct",
                "State limit per product must be between 100 and 100,000.");
        }
        var activeProductCount = definition.Products.Count(product => product.Active);
        if ((long)Math.Max(1, activeProductCount) * policy.StateLimitPerProduct > 2_000_000L)
        {
            Add(errors, "ARBITRAGE_ANALYSIS_TOTAL_STATE_BUDGET_EXCEEDED",
                "/balancePolicy/stateLimitPerProduct",
                "Active product count multiplied by the per-product state limit cannot exceed 2,000,000 states.");
        }
        foreach (var rate in rates.Values)
        {
            if (rate.ShadowUnitsPerCurrencyUnit is <= 0 or > 1_000_000_000)
            {
                Add(errors, "INVALID_CURRENCY_SHADOW_RATE", "/balancePolicy/currencyShadowRates",
                    "Currency shadow rates must be between 1 and 1,000,000,000 units.");
            }
        }
        foreach (var cost in costs.Values)
        {
            if (string.IsNullOrWhiteSpace(cost.ItemId) || cost.ItemId.Length > 128 ||
                cost.UnitCost is <= 0 or > 9_007_199_254_740_991 || !rates.ContainsKey(cost.Currency))
            {
                Add(errors, "INVALID_RESOURCE_SHADOW_COST", "/balancePolicy/resourceShadowCosts",
                    "Resource shadow costs require an ItemID, positive web-safe cost and configured currency rate.");
            }
        }
        foreach (var category in categories.Values)
        {
            if (string.IsNullOrWhiteSpace(category.Category) ||
                category.TargetRecoveryBasisPoints is <= 0 or > 10_000 ||
                category.RiskBufferBasisPoints < 0 ||
                category.RiskBufferBasisPoints >= category.TargetRecoveryBasisPoints)
            {
                Add(errors, "INVALID_RESOURCE_CATEGORY_POLICY", "/balancePolicy/resourceCategoryPolicies",
                    "Category policy requires a 1..10000bp target and a smaller non-negative risk buffer.");
            }
        }
        if (transformations.Count == 0)
        {
            Add(errors, "BALANCE_TRANSFORMATION_LAYER_REQUIRED", "/balancePolicy/transformations",
                "A complete policy must contain at least one active operator-attested shadow transformation.");
        }
        else if (transformations.Count > 10_000)
        {
            Add(errors, "INVALID_BALANCE_TRANSFORMATION_COUNT", "/balancePolicy/transformations",
                "A balance policy cannot contain more than 10,000 active transformations.");
        }
        for (var index = 0; index < transformations.Count; index++)
        {
            var transformation = transformations[index];
            var path = $"/balancePolicy/transformations/{index}";
            if (string.IsNullOrWhiteSpace(transformation.TransformationId) ||
                transformation.TransformationId.Length > 64 ||
                string.IsNullOrWhiteSpace(transformation.DisplayName) ||
                transformation.DisplayName.Length > 128 ||
                string.IsNullOrWhiteSpace(transformation.EvidenceNote) ||
                transformation.EvidenceNote.Length > 512 ||
                transformation.Inputs.Count == 0 || transformation.Outputs.Count == 0 ||
                transformation.Inputs.Count > 100 || transformation.Outputs.Count > 100 ||
                transformation.Inputs.Any(item => string.IsNullOrWhiteSpace(item.ItemId) ||
                                                   item.ItemId.Length > 128 ||
                                                   item.Quantity is <= 0 or > 1_000_000) ||
                transformation.Outputs.Any(item => string.IsNullOrWhiteSpace(item.ItemId) ||
                                                    item.ItemId.Length > 128 ||
                                                    item.Quantity is <= 0 or > 1_000_000) ||
                transformation.FeeAmount is < 0 or > 9_007_199_254_740_991 ||
                (transformation.FeeAmount > 0 && transformation.FeeCurrency is null) ||
                (transformation.FeeCurrency is { } fee && !rates.ContainsKey(fee)))
            {
                Add(errors, "INVALID_ITEM_TRANSFORMATION", path,
                    "Active transformations require bounded ID/name/evidence, positive inputs/outputs and a valid optional fee.");
            }
            if (transformation.Inputs.GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1) ||
                transformation.Outputs.GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1))
            {
                Add(errors, "DUPLICATE_TRANSFORMATION_ITEM", path,
                    "Transformation inputs and outputs must each use distinct ItemIDs.");
            }
            if (knownItemIds is not null)
            {
                foreach (var itemId in transformation.Inputs.Concat(transformation.Outputs)
                             .Select(item => item.ItemId)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!knownItemIds.Contains(itemId))
                    {
                        Add(errors, "UNKNOWN_TRANSFORMATION_ITEM", path,
                            $"Transformation item '{itemId}' is not present in the approved catalog.");
                    }
                }
            }
        }

        var attestation = policy.Attestation;
        if (attestation is null)
        {
            Add(errors, "BALANCE_GRAPH_ATTESTATION_REQUIRED", "/balancePolicy/attestation",
                "Publishing requires an explicit completeness attestation for the economic shadow graph.");
        }
        else
        {
            if (!string.Equals(
                    attestation.EvidenceKind,
                    OperationalShadowGraphEvidenceKind,
                    StringComparison.Ordinal))
            {
                Add(errors, "UNSUPPORTED_BALANCE_GRAPH_EVIDENCE", "/balancePolicy/attestation/evidenceKind",
                    $"Evidence kind must be '{OperationalShadowGraphEvidenceKind}' so the table is not misrepresented as real game recipes.");
            }
            if (!string.Equals(
                    attestation.CatalogRevision,
                    definition.Dependencies.ResourceCatalogRevision,
                    StringComparison.Ordinal))
            {
                Add(errors, "BALANCE_GRAPH_CATALOG_MISMATCH", "/balancePolicy/attestation/catalogRevision",
                    "The graph attestation must match the published resource-catalog revision.");
            }
            if (string.IsNullOrWhiteSpace(attestation.AttestedBy) || attestation.AttestedBy.Length > 128 ||
                attestation.AttestedAt == default || !attestation.ReachableGraphComplete)
            {
                Add(errors, "INCOMPLETE_BALANCE_GRAPH_ATTESTATION", "/balancePolicy/attestation",
                    "Attestation requires an actor, timestamp and an explicit complete reachable-graph assertion.");
            }
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (attestation.CoveredItemIds.Count is < 1 or > 20_000)
            {
                Add(errors, "INVALID_BALANCE_GRAPH_COVERAGE", "/balancePolicy/attestation/coveredItemIds",
                    "Coverage must contain 1 to 20,000 audited ItemIDs.");
            }
            foreach (var itemId in attestation.CoveredItemIds)
            {
                if (string.IsNullOrWhiteSpace(itemId) || itemId.Length > 128 || !covered.Add(itemId))
                {
                    Add(errors, "INVALID_BALANCE_GRAPH_COVERAGE", "/balancePolicy/attestation/coveredItemIds",
                        "Covered ItemIDs must be non-empty and unique.");
                    continue;
                }
                if (knownItemIds is not null && !knownItemIds.Contains(itemId))
                {
                    Add(errors, "UNKNOWN_ATTESTED_GRAPH_ITEM", "/balancePolicy/attestation/coveredItemIds",
                        $"Attested item '{itemId}' is not present in the approved catalog.");
                }
            }
        }

        var activeResourceIds = definition.Resources.Where(resource => resource.Active)
            .Select(resource => resource.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in definition.Resources.Where(resource => resource.Active))
        {
            if (!costs.TryGetValue(resource.ItemId, out var cost) || cost.UnitCost <= 0)
            {
                Add(errors, "RESOURCE_SHADOW_COST_REQUIRED", "/balancePolicy/resourceShadowCosts",
                    $"Active resource '{resource.ItemId}' requires a positive ItemID reference cost.");
            }
            if (!categories.ContainsKey(resource.Category))
            {
                Add(errors, "RESOURCE_CATEGORY_POLICY_REQUIRED", "/balancePolicy/resourceCategoryPolicies",
                    $"Active resource category '{resource.Category}' requires a recovery target and risk buffer.");
            }
        }
        foreach (var extraCost in costs.Keys.Where(itemId => !activeResourceIds.Contains(itemId)))
        {
            Add(warnings, "UNUSED_RESOURCE_SHADOW_COST", "/balancePolicy/resourceShadowCosts",
                $"Reference cost '{extraCost}' does not correspond to an active sellable resource.");
        }
        var activeCategories = definition.Resources.Where(resource => resource.Active)
            .Select(resource => resource.Category)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extraCategory in categories.Keys.Where(category => !activeCategories.Contains(category)))
        {
            Add(warnings, "UNUSED_RESOURCE_CATEGORY_POLICY", "/balancePolicy/resourceCategoryPolicies",
                $"Category policy '{extraCategory}' does not correspond to an active resource category.");
        }
    }

    private static void ValidateGraphCoverage(
        EconomyContentDefinition definition,
        ContentEconomyBalancePolicy policy,
        IReadOnlyList<ContentItemTransformation> transformations,
        IReadOnlySet<string> activeResourceIds,
        IReadOnlySet<string> saleValueItemIds,
        ICollection<EconomyBalanceAnalysisIssue> errors,
        ICollection<EconomyBalanceAnalysisIssue> warnings)
    {
        if (policy.Attestation is null)
        {
            return;
        }
        var covered = policy.Attestation.CoveredItemIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var transformationsByInput = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var transformation in transformations)
        {
            foreach (var input in transformation.Inputs)
            {
                if (!graph.TryGetValue(input.ItemId, out var outputs))
                {
                    outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    graph[input.ItemId] = outputs;
                }
                if (!transformationsByInput.TryGetValue(input.ItemId, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    transformationsByInput[input.ItemId] = ids;
                }
                ids.Add(transformation.TransformationId);
                foreach (var output in transformation.Outputs)
                {
                    outputs.Add(output.ItemId);
                }
            }
        }

        var productNodes = definition.Products.Where(product => product.Active)
            .SelectMany(product => product.ItemGrants)
            .Select(grant => grant.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reachable = new HashSet<string>(productNodes, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(productNodes);
        while (queue.TryDequeue(out var itemId))
        {
            if (!graph.TryGetValue(itemId, out var outputs))
            {
                continue;
            }
            foreach (var output in outputs)
            {
                if (reachable.Add(output))
                {
                    queue.Enqueue(output);
                }
            }
        }

        var reachableTransformationIds = reachable
            .Where(transformationsByInput.ContainsKey)
            .SelectMany(itemId => transformationsByInput[itemId])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (reachableTransformationIds.Count == 0)
        {
            Add(errors, "BALANCE_TRANSFORMATION_LAYER_NOT_REACHABLE", "/balancePolicy/transformations",
                "At least one attested transformation must be reachable from an active product grant.");
        }

        foreach (var itemId in reachable.Concat(activeResourceIds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!covered.Contains(itemId))
            {
                Add(errors, "BALANCE_GRAPH_ITEM_NOT_ATTESTED", "/balancePolicy/attestation/coveredItemIds",
                    $"Reachable or sellable item '{itemId}' is missing from the completeness attestation.");
            }
        }
        foreach (var itemId in reachable)
        {
            if (!saleValueItemIds.Contains(itemId) && !graph.ContainsKey(itemId))
            {
                Add(errors, "BALANCE_GRAPH_UNCOVERED_TERMINAL", "/balancePolicy/transformations",
                    $"Reachable item '{itemId}' is neither sellable nor the input of another attested transformation.");
            }
        }
        foreach (var transformation in transformations)
        {
            foreach (var itemId in transformation.Inputs.Concat(transformation.Outputs)
                         .Select(item => item.ItemId)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!covered.Contains(itemId))
                {
                    Add(errors, "BALANCE_TRANSFORMATION_ITEM_NOT_ATTESTED",
                        "/balancePolicy/attestation/coveredItemIds",
                        $"Transformation '{transformation.TransformationId}' item '{itemId}' is not attested.");
                }
            }
            if (!reachableTransformationIds.Contains(transformation.TransformationId))
            {
                Add(warnings, "UNREACHABLE_BALANCE_TRANSFORMATION", "/balancePolicy/transformations",
                    $"Transformation '{transformation.TransformationId}' is not reachable from any active product grant.");
            }
        }
    }

    private static void ValidateTransformationDag(
        IReadOnlyList<ContentItemTransformation> transformations,
        ICollection<EconomyBalanceAnalysisIssue> errors)
    {
        var duplicate = transformations
            .GroupBy(item => item.TransformationId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            Add(errors, "DUPLICATE_ITEM_TRANSFORMATION", "/balancePolicy/transformations",
                $"Transformation ID '{duplicate.Key}' is duplicated.");
        }
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var indegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var transformation in transformations)
        {
            foreach (var input in transformation.Inputs)
            {
                indegree.TryAdd(input.ItemId, 0);
                var outputs = graph.GetValueOrDefault(input.ItemId);
                if (outputs is null)
                {
                    outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    graph[input.ItemId] = outputs;
                }
                foreach (var output in transformation.Outputs)
                {
                    indegree.TryAdd(output.ItemId, 0);
                    if (outputs.Add(output.ItemId))
                    {
                        indegree[output.ItemId] = checked(indegree[output.ItemId] + 1);
                    }
                }
            }
        }
        var ready = new SortedSet<string>(
            indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
        var visitedCount = 0;
        while (ready.Count > 0)
        {
            var node = ready.Min!;
            ready.Remove(node);
            visitedCount++;
            if (!graph.TryGetValue(node, out var outputs))
            {
                continue;
            }
            foreach (var output in outputs)
            {
                indegree[output]--;
                if (indegree[output] == 0)
                {
                    ready.Add(output);
                }
            }
        }
        if (visitedCount != indegree.Count)
        {
            var cycleNode = indegree.Where(pair => pair.Value > 0)
                .Select(pair => pair.Key)
                .OrderBy(itemId => itemId, StringComparer.OrdinalIgnoreCase)
                .First();
            Add(errors, "TRANSFORMATION_GRAPH_CYCLE", "/balancePolicy/transformations",
                $"Item transformation graph contains a cycle through '{cycleNode}'. " +
                "An acyclic shadow graph is required for exhaustive arbitrage analysis.");
        }
    }

    private static bool CanApply(
        IReadOnlyDictionary<string, long> inventory,
        IReadOnlyList<ContentItemGrant> inputs) => inputs.All(input =>
        inventory.TryGetValue(input.ItemId, out var quantity) && quantity >= input.Quantity);

    private static string InventoryKey(IReadOnlyDictionary<string, long> inventory) => string.Join('|',
        inventory.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key.ToUpperInvariant()}={item.Value}"));

    private static Dictionary<TKey, TValue> UniqueMap<TKey, TValue>(
        IEnumerable<TValue> values,
        Func<TValue, TKey> keySelector,
        string path,
        string duplicateCode,
        ICollection<EconomyBalanceAnalysisIssue> errors,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>(comparer);
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
            {
                Add(errors, duplicateCode, path, $"Duplicate balance-policy key '{key}'.");
            }
        }
        return result;
    }

    private static void Add(
        ICollection<EconomyBalanceAnalysisIssue> issues,
        string code,
        string path,
        string message) => issues.Add(new EconomyBalanceAnalysisIssue(code, path, message));

    private sealed record SaleValue(
        ExtractionCurrency Currency,
        long UnitValue,
        string ZoneId,
        int ZoneMultiplierBasisPoints);

    private sealed record InventoryState(
        SortedDictionary<string, long> Inventory,
        long FeeShadow,
        IReadOnlyList<string> Path);

    private sealed record ProductAnalysis(
        EconomyArbitrageFinding? BestFinding,
        int StatesEvaluated,
        bool LimitExceeded,
        bool AnyTransformationApplied,
        bool AnyTransformedSaleState,
        IReadOnlyList<string> UnrecoverableTerminalItemIds);
}

using System.Text.Json;
using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;
using PalControl.EconomySimulator;

var testRoot = Path.Combine(Path.GetTempPath(), "pal-control-balance-guard-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    await VerifyProvenArbitrageBlocksPublishAsync(testRoot);
    VerifyIndeterminatePathsAreExplicit();
    VerifyAttestedBundleCraftingAndCrossCurrencyPath();
    VerifyGraphCycleFailsClosed();
    VerifyGraphStateLimitFailsClosed();
    VerifyMultiInputTransformationMustBeActuallyReachable();
    VerifyReferenceCostAndCategoryPolicyFailClosed();
    VerifyMissingGraphEvidenceFailsClosed();
    VerifyDynamicExtremaParticipateInGraphGuard();
    VerifyReproducibleSevenDaySimulation();
    Console.WriteLine("PASS: direct and attested graph balance guards cover bundle splitting, crafting, cross-currency paths, cycles, state limits, category policy, missing evidence, and deterministic 100-player x 7-day simulation invariants.");
}
finally
{
    SqliteConnectionCleanup();
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static async Task VerifyProvenArbitrageBlocksPublishAsync(string directory)
{
    var definition = Definition(
        new ContentProductDefinition(
            "LOOP-PACK", "Loop pack", "Intentional direct loop", "test", ["test"], null,
            ExtractionCurrency.SeasonVoucher, 20,
            [new ContentItemGrant("ResaleItem", 2)], 5, null, true, null, null),
        new ContentResourceDefinition(
            "ResaleItem", "Resale item", "test", ["test"], ExtractionCurrency.SeasonVoucher,
            9, ["harbor", "ridge"], true));
    var context = Context("ResaleItem");
    var validator = new EconomyContentDefinitionValidator(new FixedTimeProvider());
    var validation = validator.Validate(definition, context);
    var issue = validation.Errors.SingleOrDefault(error => error.Code == "PROVEN_DIRECT_RESALE_ARBITRAGE");

    Assert(!validation.Valid, "A proven positive direct resale loop was accepted.");
    if (issue is null)
    {
        throw new InvalidOperationException("The direct resale loop did not return its stable validation code.");
    }
    Assert(issue.Path == "/products/0/unitPrice", "The direct resale issue did not identify the SKU price path.");
    Assert(issue.Message.Contains("LOOP-PACK", StringComparison.Ordinal) &&
           issue.Message.Contains("ResaleItem x2", StringComparison.Ordinal) &&
           issue.Message.Contains("ridge, 15000bp", StringComparison.Ordinal) &&
           issue.Message.Contains("returns 28", StringComparison.Ordinal) &&
           issue.Message.Contains("costs 18", StringComparison.Ordinal) &&
           issue.Message.Contains("profit 10", StringComparison.Ordinal),
        "The direct resale issue omitted its concrete SKU/item/quantity/maximum-zone proof path.");

    await using var store = new SqliteEconomyContentStore(directory, new FixedTimeProvider());
    var draft = await store.CreateDraftAsync(
        definition.ServerId, "arbitrage-draft", null, definition, "balance-test", CancellationToken.None);
    try
    {
        _ = await store.PublishAsync(
            draft.DraftId,
            draft.Revision,
            new DateOnly(2026, 7, 13),
            "block-proven-loop",
            "balance-test",
            context,
            CancellationToken.None);
        throw new InvalidOperationException("Publishing a proven positive direct resale loop succeeded.");
    }
    catch (ContentValidationException exception)
    {
        Assert(exception.Validation.Errors.Any(error => error.Code == "PROVEN_DIRECT_RESALE_ARBITRAGE"),
            "Publish failed without preserving the concrete arbitrage validation result.");
    }
    Assert(await store.GetCurrentAsync(definition.ServerId, CancellationToken.None) is null,
        "A rejected arbitrage draft changed the published content pointer.");
}

static void VerifyIndeterminatePathsAreExplicit()
{
    var definition = Definition(
        new ContentProductDefinition(
            "MIXED-PACK", "Mixed pack", "Cannot be directly compared", "test", ["test"], null,
            ExtractionCurrency.MarketCoin, 20,
            [new ContentItemGrant("GrantedItem", 2), new ContentItemGrant("ResaleItem", 1)],
            5, null, true, null, null),
        new ContentResourceDefinition(
            "ResaleItem", "Resale item", "test", ["test"], ExtractionCurrency.SeasonVoucher,
            9, ["harbor", "ridge"], true));
    var validation = new EconomyContentDefinitionValidator(new FixedTimeProvider())
        .Validate(definition, Context("GrantedItem", "ResaleItem"));

    Assert(validation.Valid, "Cross-currency or missing-resource paths were incorrectly called proven arbitrage.");
    var incomplete = validation.Warnings.SingleOrDefault(warning =>
        warning.Code == "DIRECT_RESALE_ANALYSIS_INCOMPLETE");
    Assert(incomplete is not null &&
           incomplete.Message.Contains("GrantedItem: not an active sell allow-list resource", StringComparison.Ordinal) &&
           incomplete.Message.Contains("ResaleItem: resale credits SeasonVoucher, product costs MarketCoin", StringComparison.Ordinal),
        "Indeterminate missing-resource and cross-currency paths were not stated explicitly.");
    Assert(validation.Warnings.Any(warning => warning.Code == "INDIRECT_ARBITRAGE_NOT_EVALUATED" &&
                                              warning.Message.Contains("recipe graph", StringComparison.OrdinalIgnoreCase)),
        "The validator implied recipe/crafting paths were covered when no recipe graph exists.");
    Assert(validation.Warnings.Any(warning => warning.Code == "BALANCE_POLICY_NOT_CONFIGURED"),
        "Legacy content without a balance policy did not retain its explicit compatibility warning.");
}

static void VerifyAttestedBundleCraftingAndCrossCurrencyPath()
{
    var definition = Definition(
        new ContentProductDefinition(
            "BUNDLE-CRAFT", "Bundle craft", "Intentional cross-currency graph loop", "test", ["test"], null,
            ExtractionCurrency.MarketCoin, 10,
            [new ContentItemGrant("Wrapper", 1), new ContentItemGrant("Catalyst", 1)],
            5, null, true, null, null),
        new ContentResourceDefinition(
            "ResaleItem", "Resale item", "test", ["test"], ExtractionCurrency.SeasonVoucher,
            1, ["harbor", "ridge"], true));
    definition = definition with
    {
        BalancePolicy = GraphPolicy(
            [
                Transform("unpack-wrapper", [new("Wrapper", 1)], [new("Intermediate", 1)]),
                Transform("craft-resale", [new("Intermediate", 1), new("Catalyst", 1)], [new("ResaleItem", 60)])
            ],
            ["Wrapper", "Catalyst", "Intermediate", "ResaleItem"])
    };
    var validation = new EconomyContentDefinitionValidator(new FixedTimeProvider())
        .Validate(definition, Context("Wrapper", "Catalyst", "Intermediate", "ResaleItem"));
    var issue = validation.Errors.SingleOrDefault(error => error.Code == "PROVEN_REACHABLE_ARBITRAGE");

    if (validation.Valid || issue is null)
    {
        throw new InvalidOperationException(
            "An attested bundle -> craft -> cross-currency resale path was accepted.");
    }
    Assert(issue.Message.Contains("BUNDLE-CRAFT", StringComparison.Ordinal) &&
           issue.Message.Contains("9 MarketCoin", StringComparison.Ordinal) &&
           issue.Message.Contains("90 shadow units", StringComparison.Ordinal) &&
           issue.Message.Contains("TRANSFORM unpack-wrapper", StringComparison.Ordinal) &&
           issue.Message.Contains("TRANSFORM craft-resale", StringComparison.Ordinal) &&
           issue.Message.Contains("120 shadow units", StringComparison.Ordinal) &&
           issue.Message.Contains("SELL 120 SeasonVoucher", StringComparison.Ordinal),
        "The graph rejection omitted its concrete bundle, transformation, currency or sale path.");
    Assert(!validation.Warnings.Any(warning => warning.Code == "INDIRECT_ARBITRAGE_NOT_EVALUATED"),
        "A complete attested graph was incorrectly reported as legacy direct-only analysis.");
}

static void VerifyGraphCycleFailsClosed()
{
    var definition = GraphDefinition(
        "CYCLE-PACK",
        1_000,
        1,
        [
            Transform("forward", [new("Token", 1)], [new("ResaleItem", 1)]),
            Transform("reverse", [new("ResaleItem", 1)], [new("Token", 1)])
        ]);
    var validation = new EconomyContentDefinitionValidator(new FixedTimeProvider())
        .Validate(definition, Context("Token", "ResaleItem"));
    Assert(!validation.Valid && validation.Errors.Any(error => error.Code == "TRANSFORMATION_GRAPH_CYCLE"),
        "A cyclic transformation graph was not blocked before publication.");
}

static void VerifyGraphStateLimitFailsClosed()
{
    var definition = GraphDefinition(
        "STATE-PACK",
        1_000,
        100,
        [Transform("one-at-a-time", [new("Token", 1)], [new("ResaleItem", 1)])],
        stateLimit: 100);
    var validation = new EconomyContentDefinitionValidator(new FixedTimeProvider())
        .Validate(definition, Context("Token", "ResaleItem"));
    Assert(!validation.Valid && validation.Errors.Any(error =>
            error.Code == "ARBITRAGE_ANALYSIS_STATE_LIMIT" &&
            error.Message.Contains("STATE-PACK", StringComparison.Ordinal) &&
            error.Message.Contains("100 reachable", StringComparison.Ordinal)),
        "Exhaustive enumeration reaching its configured state limit did not fail closed.");
}

static void VerifyMultiInputTransformationMustBeActuallyReachable()
{
    var definition = GraphDefinition(
        "MISSING-COINPUT",
        1_000,
        1,
        [Transform("needs-unavailable-coinput",
            [new("Token", 1), new("CoInput", 1)],
            [new("ResaleItem", 1)])]);
    definition = definition with
    {
        BalancePolicy = definition.BalancePolicy! with
        {
            Attestation = definition.BalancePolicy!.Attestation! with
            {
                CoveredItemIds = ["Token", "CoInput", "ResaleItem"]
            }
        }
    };
    var validation = new EconomyContentDefinitionValidator(new FixedTimeProvider())
        .Validate(definition, Context("Token", "CoInput", "ResaleItem"));
    Assert(!validation.Valid &&
           validation.Errors.Any(error =>
               error.Code == "BALANCE_GRAPH_UNRECOVERABLE_REACHABLE_STATE" &&
               error.Message.Contains("MISSING-COINPUT", StringComparison.Ordinal) &&
               error.Message.Contains("Token", StringComparison.Ordinal)) &&
           validation.Errors.Any(error => error.Code == "BALANCE_TRANSFORMATION_LAYER_NOT_REACHABLE"),
        "A multi-input edge with an unavailable co-input was incorrectly treated as an actually reachable transformation path.");
}

static void VerifyReferenceCostAndCategoryPolicyFailClosed()
{
    var definition = GraphDefinition(
        "CATEGORY-PACK",
        1_000,
        1,
        [Transform("to-resource", [new("Token", 1)], [new("ResaleItem", 1)])],
        resourceReferenceCost: 1,
        targetRecoveryBasisPoints: 7_500,
        riskBufferBasisPoints: 1_000);
    var validation = new EconomyContentDefinitionValidator(new FixedTimeProvider())
        .Validate(definition, Context("Token", "ResaleItem"));
    Assert(!validation.Valid && validation.Errors.Any(error =>
            error.Code == "RESOURCE_RECOVERY_POLICY_EXCEEDED" &&
            error.Message.Contains("ResaleItem", StringComparison.Ordinal) &&
            error.Message.Contains("6500bp", StringComparison.Ordinal)),
        "Per-ItemID reference cost and category risk buffer did not participate in publication validation.");
}

static void VerifyMissingGraphEvidenceFailsClosed()
{
    var definition = GraphDefinition(
        "MISSING-EVIDENCE",
        1_000,
        1,
        [Transform("to-resource", [new("Token", 1)], [new("ResaleItem", 1)])]);
    definition = definition with
    {
        BalancePolicy = definition.BalancePolicy! with
        {
            ResourceShadowCosts = [],
            Attestation = definition.BalancePolicy!.Attestation! with
            {
                ReachableGraphComplete = false,
                CoveredItemIds = ["Token"]
            }
        }
    };
    var validation = new EconomyContentDefinitionValidator(new FixedTimeProvider())
        .Validate(definition, Context("Token", "ResaleItem"));
    Assert(!validation.Valid &&
           validation.Errors.Any(error => error.Code == "RESOURCE_SHADOW_COST_REQUIRED") &&
           validation.Errors.Any(error => error.Code == "INCOMPLETE_BALANCE_GRAPH_ATTESTATION") &&
           validation.Errors.Any(error => error.Code == "BALANCE_GRAPH_ITEM_NOT_ATTESTED"),
        "Missing reference costs or incomplete graph attestation did not fail closed.");
}

static void VerifyDynamicExtremaParticipateInGraphGuard()
{
    var allWeek = Enum.GetValues<DayOfWeek>()
        .Select(day => new ContentExchangeWindow(
            day,
            TimeOnly.MinValue,
            TimeOnly.MaxValue,
            60))
        .ToArray();
    var definition = Definition(
        new ContentProductDefinition(
            "DYNAMIC-LOOP", "Dynamic loop", "Only profitable across event extrema", "test", ["test"], null,
            ExtractionCurrency.SeasonVoucher, 35,
            [new ContentItemGrant("Token", 1)], 5, null, true, null, null),
        new ContentResourceDefinition(
            "ResaleItem", "Resale item", "test", ["test"], ExtractionCurrency.SeasonVoucher,
            9, ["harbor", "ridge"], true));
    definition = definition with
    {
        ExchangeZones = definition.ExchangeZones.Select(zone => zone with { OpenWindows = allWeek }).ToArray(),
        DynamicEconomyPolicy = EconomyContentDefaults.CreateDynamicEconomyPolicy(["harbor", "ridge"]),
        BalancePolicy = GraphPolicy(
            [Transform("dynamic-conversion", [new("Token", 1)], [new("ResaleItem", 2)])],
            ["Token", "ResaleItem"],
            resourceReferenceCost: 100)
    };

    var validation = new EconomyContentDefinitionValidator(new FixedTimeProvider())
        .Validate(definition, Context("Token", "ResaleItem"));
    var issue = validation.Errors.SingleOrDefault(error =>
        error.Code == "PROVEN_REACHABLE_ARBITRAGE" &&
        error.Message.Contains("DYNAMIC-LOOP", StringComparison.Ordinal));
    var ridge = definition.ExchangeZones.Single(zone => zone.ZoneId == "ridge");
    var maximumMultiplier = EconomyContentRuntimeService
        .CalculateMaximumPossibleZoneYieldMultiplierBasisPoints(definition, ridge);
    var minimumPrice = EconomyContentRuntimeService.CalculateMinimumPossibleEffectiveUnitPrice(
        definition,
        definition.Products.Single());
    Assert(issue is not null &&
           issue.Message.Contains("minimum event-adjusted price", StringComparison.OrdinalIgnoreCase) &&
           issue.Message.Contains("costs 28", StringComparison.OrdinalIgnoreCase) &&
           issue.Message.Contains("returns 32", StringComparison.OrdinalIgnoreCase) &&
           maximumMultiplier == 17_250 && minimumPrice == 28,
        "The attested graph omitted the cross-day maximum yield/hotspot and minimum discount/daily-price combination.");

    var policyMissingDiscount = definition.DynamicEconomyPolicy! with
    {
        WorldEvents = definition.DynamicEconomyPolicy!.WorldEvents.Select(worldEvent =>
            worldEvent with { ProductPriceMultiplierBasisPoints = 10_000 }).ToArray()
    };
    var missingModel = new EconomyContentDefinitionValidator(new FixedTimeProvider())
        .Validate(definition with { DynamicEconomyPolicy = policyMissingDiscount }, Context("Token", "ResaleItem"));
    Assert(!missingModel.Valid && missingModel.Errors.Any(error =>
            error.Code == "WORLD_EVENT_DISCOUNT_REQUIRED"),
        "A dynamic policy that omitted the discount side of the temporal arbitrage model did not fail closed.");
}

static void VerifyReproducibleSevenDaySimulation()
{
    var definition = SchemeADefaultScenario.Create();
    var options = EconomySimulationOptions.ReproducibleDefault;
    var first = EconomySimulation.Run(definition, options, SchemeADefaultScenario.ContentVersionId);
    var second = EconomySimulation.Run(definition, options, SchemeADefaultScenario.ContentVersionId);
    var firstJson = JsonSerializer.Serialize(first, EconomyContentJson.Options);
    var secondJson = JsonSerializer.Serialize(second, EconomyContentJson.Options);

    Assert(firstJson == secondJson, "The same seed/content/options produced different seven-day reports.");
    Assert(first.PlayerCount == 100 && first.BusinessDays == 7,
        "The reproducible launch simulation is not 100 players x 7 business days.");
    Assert(first.ScopeLimit.Contains("maximum timed-hotspot/yield-event", StringComparison.Ordinal) &&
           first.ScopeLimit.Contains("minimum daily/event purchase-price", StringComparison.Ordinal),
        "The 100x7 report does not attest its maximum-yield/minimum-discount stress combination.");
    var policy = definition.DynamicEconomyPolicy
        ?? throw new InvalidOperationException("The simulation fixture is missing dynamic policy.");
    var contentHash = EconomyContentCanonicalizer.Hash(definition);
    var simulatedKinds = Enumerable.Range(0, 7)
        .SelectMany(day => EconomyDynamicEconomyRuntime.Create(
            new EconomyContentVersion(
                SchemeADefaultScenario.ContentVersionId,
                definition.ServerId,
                day + 1,
                options.StartingBusinessDate.AddDays(day),
                definition.Dependencies.RulesVersion,
                contentHash,
                definition,
                Guid.Empty,
                "simulation-test",
                DateTimeOffset.MinValue),
            EconomyDynamicEconomyRuntime.ResolveTimeZone(definition))!.WorldEvents)
        .Select(worldEvent => worldEvent.Kind)
        .ToHashSet();
    Assert(policy.WorldEvents.Select(worldEvent => worldEvent.Kind).All(simulatedKinds.Contains),
        "The seven-day deterministic evidence did not cover every configured economic event kind.");
    Assert(first.ContentVersionId == SchemeADefaultScenario.ContentVersionId &&
           first.ContentHash == "e7b6866df8c3085c180399e388e7a5e4ad37c02ef71058ed20c5baeddf1827e7",
        "The simulation report lost its immutable content-version evidence.");

    var voucher = first.Currencies.Single(currency => currency.Currency == ExtractionCurrency.SeasonVoucher);
    Assert(voucher.Produced == 137_292 && voucher.Consumed == 24_922 &&
           voucher.EndingBalanceMedian == 692 && voucher.EndingBalanceP95 == 2_829,
        "The fixed-seed currency production/consumption/balance snapshot changed unexpectedly.");
    Assert(first.ProductPurchases.Purchases == 324 &&
           first.ProductPurchases.UniqueBuyerRateBasisPoints == 9_800,
        "The fixed-seed product purchase-rate snapshot changed unexpectedly.");
    Assert(first.ResourceExchange.PrimaryCurrencyEarnings == 137_292 &&
           first.ResourceExchange.PrimaryCurrencyEarningsPerPlayer == 1_372,
        "The fixed-seed resource-exchange earnings snapshot changed unexpectedly.");
    Assert(first.TargetAssessments.Count == EconomySimulationTargets.SchemeADefault.Count &&
           first.WithinDefaultTargets &&
           first.TargetAssessments.All(assessment => assessment.WithinTarget),
        "The reproducible default scenario escaped a documented launch target interval.");
}

static EconomyContentDefinition Definition(
    ContentProductDefinition product,
    ContentResourceDefinition resource) => new(
    1,
    "balance-test",
    "Balance test",
    new EconomyContentDependencies("scheme-a-v1", "catalog-v1", "game-v1", "paldefender-v1"),
    "Asia/Shanghai",
    4,
    [product],
    [resource],
    [
        new ContentExchangeZoneDefinition(
            "harbor", "Harbor", "Test harbor", 10, 20, 50, 10_000,
            [new ContentExchangeWindow(DayOfWeek.Monday, TimeOnly.MinValue, TimeOnly.MaxValue, 60)], true),
        new ContentExchangeZoneDefinition(
            "ridge", "Ridge", "Test ridge", 100, 200, 50, 12_500,
            [new ContentExchangeWindow(DayOfWeek.Monday, TimeOnly.MinValue, TimeOnly.MaxValue, 60)], true)
    ],
    [],
    new ContentRotationPolicy("scheme-a-v1", 1, "balance-test-v1", [], 0, [], 0, ["ridge"], 1));

static EconomyContentDefinition GraphDefinition(
    string sku,
    long price,
    int tokenQuantity,
    IReadOnlyList<ContentItemTransformation> transformations,
    int stateLimit = 20_000,
    long resourceReferenceCost = 4,
    int targetRecoveryBasisPoints = 7_500,
    int riskBufferBasisPoints = 1_000)
{
    var definition = Definition(
        new ContentProductDefinition(
            sku, sku, "Graph guard fixture", "test", ["test"], null,
            ExtractionCurrency.SeasonVoucher, price,
            [new ContentItemGrant("Token", tokenQuantity)],
            5, null, true, null, null),
        new ContentResourceDefinition(
            "ResaleItem", "Resale item", "test", ["test"], ExtractionCurrency.SeasonVoucher,
            1, ["harbor", "ridge"], true));
    return definition with
    {
        BalancePolicy = GraphPolicy(
            transformations,
            ["Token", "ResaleItem"],
            stateLimit,
            resourceReferenceCost,
            targetRecoveryBasisPoints,
            riskBufferBasisPoints)
    };
}

static ContentEconomyBalancePolicy GraphPolicy(
    IReadOnlyList<ContentItemTransformation> transformations,
    IReadOnlyList<string> coveredItemIds,
    int stateLimit = 20_000,
    long resourceReferenceCost = 4,
    int targetRecoveryBasisPoints = 7_500,
    int riskBufferBasisPoints = 1_000) => new(
    "balance-test-shadow-v1",
    [
        new ContentCurrencyShadowRate(ExtractionCurrency.MarketCoin, 10),
        new ContentCurrencyShadowRate(ExtractionCurrency.SeasonVoucher, 1)
    ],
    [new ContentResourceShadowCost("ResaleItem", ExtractionCurrency.SeasonVoucher, resourceReferenceCost)],
    [new ContentResourceCategoryPolicy("test", targetRecoveryBasisPoints, riskBufferBasisPoints)],
    transformations,
    stateLimit,
    new ContentEconomyGraphAttestation(
        EconomyArbitrageGraphAnalyzer.OperationalShadowGraphEvidenceKind,
        "catalog-v1",
        "balance-test-reviewer",
        DateTimeOffset.Parse("2026-07-13T00:00:00Z"),
        ReachableGraphComplete: true,
        coveredItemIds));

static ContentItemTransformation Transform(
    string id,
    IReadOnlyList<ContentItemGrant> inputs,
    IReadOnlyList<ContentItemGrant> outputs) => new(
    id,
    id,
    inputs,
    outputs,
    Active: true,
    EvidenceNote: "Operator-audited economic shadow edge for tests; not an actual Palworld recipe.");

static EconomyContentValidationContext Context(params string[] knownItems) => new(
    new HashSet<string>(knownItems, StringComparer.OrdinalIgnoreCase),
    new HashSet<string>(["scheme-a-v1"], StringComparer.Ordinal),
    "catalog-v1",
    "game-v1",
    "paldefender-v1");

static void SqliteConnectionCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FixedTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-13T00:00:00Z");
}

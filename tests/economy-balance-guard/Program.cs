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
    VerifyReproducibleSevenDaySimulation();
    Console.WriteLine("PASS: economy balance guard and deterministic 100-player x 7-day simulation invariants hold.");
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
           issue.Message.Contains("ridge, 12500bp", StringComparison.Ordinal) &&
           issue.Message.Contains("returns 24", StringComparison.Ordinal) &&
           issue.Message.Contains("profit 4", StringComparison.Ordinal),
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
    Assert(first.ContentVersionId == SchemeADefaultScenario.ContentVersionId &&
           first.ContentHash == "16a8f0e7cdc8f56a7f3db9a0e4faf95fb76397e6d0e7af4006e3f152755aefdd",
        "The simulation report lost its immutable content-version evidence.");

    var voucher = first.Currencies.Single(currency => currency.Currency == ExtractionCurrency.SeasonVoucher);
    Assert(voucher.Produced == 96_167 && voucher.Consumed == 24_110 &&
           voucher.EndingBalanceMedian == 436 && voucher.EndingBalanceP95 == 2_016,
        "The fixed-seed currency production/consumption/balance snapshot changed unexpectedly.");
    Assert(first.ProductPurchases.Purchases == 275 &&
           first.ProductPurchases.UniqueBuyerRateBasisPoints == 9_100,
        "The fixed-seed product purchase-rate snapshot changed unexpectedly.");
    Assert(first.ResourceExchange.PrimaryCurrencyEarnings == 96_167 &&
           first.ResourceExchange.PrimaryCurrencyEarningsPerPlayer == 961,
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

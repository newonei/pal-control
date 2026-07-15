using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;

namespace PalControl.EconomySimulator;

public sealed record EconomySimulationOptions(
    ulong Seed,
    int PlayerCount,
    int BusinessDays,
    DateOnly StartingBusinessDate)
{
    public static EconomySimulationOptions ReproducibleDefault { get; } = new(
        0x50414C434F4E5452UL,
        100,
        7,
        new DateOnly(2026, 7, 13));
}

public sealed record EconomySimulationTarget(
    string Metric,
    long MinimumInclusive,
    long MaximumInclusive,
    string Unit);

public sealed record EconomySimulationAssessment(
    string Metric,
    long Actual,
    long MinimumInclusive,
    long MaximumInclusive,
    string Unit,
    bool WithinTarget);

public sealed record EconomySimulationCurrencyMetrics(
    ExtractionCurrency Currency,
    long StartingBalance,
    long Produced,
    long Consumed,
    long EndingBalanceTotal,
    long EndingBalanceMedian,
    long EndingBalanceP95);

public sealed record EconomySimulationProductMetrics(
    string Sku,
    ExtractionCurrency Currency,
    long Purchases,
    long CurrencyConsumed);

public sealed record EconomySimulationResourceMetrics(
    long ExchangeCount,
    long UnitsSold,
    long PrimaryCurrencyEarnings,
    long PrimaryCurrencyEarningsPerPlayer,
    long PrimaryCurrencyEarningsMedian,
    long PrimaryCurrencyEarningsP95);

public sealed record EconomySimulationPurchaseMetrics(
    long Purchases,
    int UniqueBuyers,
    int UniqueBuyerRateBasisPoints,
    long OfferImpressions,
    int PurchasePerImpressionBasisPoints,
    IReadOnlyList<EconomySimulationProductMetrics> Products);

public sealed record EconomySimulationReport(
    ulong Seed,
    int PlayerCount,
    int BusinessDays,
    DateOnly StartingBusinessDate,
    Guid? ContentVersionId,
    string ContentHash,
    ExtractionCurrency PrimaryCurrency,
    IReadOnlyList<EconomySimulationCurrencyMetrics> Currencies,
    EconomySimulationResourceMetrics ResourceExchange,
    EconomySimulationPurchaseMetrics ProductPurchases,
    IReadOnlyList<EconomySimulationAssessment> TargetAssessments,
    bool WithinDefaultTargets,
    string ScopeLimit);

public static class EconomySimulationTargets
{
    public static IReadOnlyList<EconomySimulationTarget> SchemeADefault { get; } =
    [
        new("primaryCurrencyProducedPerPlayer", 300, 2_500, "currency/player/7-business-days"),
        new("primaryCurrencyConsumptionRatio", 1_500, 9_000, "basis-points"),
        new("primaryCurrencyEndingBalanceMedian", 50, 2_000, "currency"),
        new("primaryCurrencyEndingBalanceP95", 150, 5_000, "currency"),
        new("uniqueBuyerRate", 4_000, 9_500, "basis-points"),
        new("purchasePerImpressionRate", 300, 2_000, "basis-points"),
        new("resourceExchangeEarningsPerPlayer", 300, 2_500, "currency/player/7-business-days")
    ];
}

public static class EconomySimulation
{
    public static EconomySimulationReport Run(
        EconomyContentDefinition definition,
        EconomySimulationOptions? options = null,
        Guid? contentVersionId = null,
        IReadOnlyList<EconomySimulationTarget>? targets = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        options ??= EconomySimulationOptions.ReproducibleDefault;
        targets ??= EconomySimulationTargets.SchemeADefault;
        if (options.PlayerCount is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Player count must be between 1 and 100000.");
        }
        if (options.BusinessDays is < 1 or > 365)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Business days must be between 1 and 365.");
        }

        var content = EconomyContentCanonicalizer.Normalize(definition);
        var rng = new StablePrng(options.Seed);
        var currencies = Enum.GetValues<ExtractionCurrency>();
        var players = Enumerable.Range(0, options.PlayerCount)
            .Select(index => new SimulatedPlayer(index, currencies))
            .ToArray();
        var activeZones = content.ExchangeZones
            .Where(zone => zone.Active && zone.OpenWindows.Any(window => window.OpensAt != window.ClosesAt))
            .ToDictionary(zone => zone.ZoneId, StringComparer.OrdinalIgnoreCase);
        var resources = content.Resources
            .Where(resource => resource.Active)
            .Select(resource => new SimulatedResource(
                resource,
                resource.ExchangeZoneIds
                    .Where(activeZones.ContainsKey)
                    .Select(zoneId => activeZones[zoneId].YieldMultiplierBasisPoints)
                    .DefaultIfEmpty(0)
                    .Max()))
            .Where(resource => resource.MaximumMultiplierBasisPoints > 0)
            .OrderBy(resource => resource.Definition.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var products = content.Products
            .Where(product => product.Active)
            .OrderBy(product => product.Sku, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var globalStockRemaining = products.ToDictionary(
            product => product.Sku,
            product => product.GlobalStock ?? long.MaxValue,
            StringComparer.OrdinalIgnoreCase);
        var productPurchases = products.ToDictionary(product => product.Sku, _ => 0L, StringComparer.OrdinalIgnoreCase);
        var productSpend = products.ToDictionary(product => product.Sku, _ => 0L, StringComparer.OrdinalIgnoreCase);
        var produced = currencies.ToDictionary(currency => currency, _ => 0L);
        var consumed = currencies.ToDictionary(currency => currency, _ => 0L);
        var resourceEarningsByPlayer = new long[options.PlayerCount];
        long exchangeCount = 0;
        long resourceUnits = 0;
        long offerImpressions = 0;
        var buyers = new HashSet<int>();

        var dailyTasks = SelectTasks(content, ContentTaskCadence.Daily);
        var weeklyTasks = SelectTasks(content, ContentTaskCadence.Weekly);

        for (var day = 0; day < options.BusinessDays; day++)
        {
            var businessDate = options.StartingBusinessDate.AddDays(day);
            foreach (var player in players)
            {
                if (!rng.Chance(8_500))
                {
                    continue;
                }

                if (resources.Length > 0 && rng.Chance(8_000))
                {
                    var lineCount = 1 + rng.NextInt(2);
                    for (var line = 0; line < lineCount; line++)
                    {
                        var resource = resources[rng.NextInt(resources.Length)];
                        var quantity = 1 + rng.NextInt(12);
                        var effectiveUnitValueDecimal = Math.Ceiling(
                            (decimal)resource.Definition.UnitValue * resource.MaximumMultiplierBasisPoints / 10_000m);
                        if (effectiveUnitValueDecimal > long.MaxValue)
                        {
                            throw new OverflowException(
                                $"Resource '{resource.Definition.ItemId}' exceeds the simulator's Int64 settlement range.");
                        }
                        var effectiveUnitValue = (long)effectiveUnitValueDecimal;
                        var earnings = checked(effectiveUnitValue * quantity);
                        Credit(player, resource.Definition.Currency, earnings, produced);
                        if (resource.Definition.Currency == ExtractionCurrency.SeasonVoucher)
                        {
                            resourceEarningsByPlayer[player.Id] = checked(
                                resourceEarningsByPlayer[player.Id] + earnings);
                        }
                        exchangeCount++;
                        resourceUnits = checked(resourceUnits + quantity);
                    }
                }

                foreach (var task in dailyTasks)
                {
                    if (rng.Chance(4_500))
                    {
                        Credit(player, task.Reward.Currency, task.Reward.Amount, produced);
                    }
                }
                if (day == options.BusinessDays - 1)
                {
                    foreach (var task in weeklyTasks)
                    {
                        if (rng.Chance(3_500))
                        {
                            Credit(player, task.Reward.Currency, task.Reward.Amount, produced);
                        }
                    }
                }

                var availableProducts = products
                    .Where(product => IsAvailable(product, businessDate))
                    .ToArray();
                if (availableProducts.Length == 0)
                {
                    continue;
                }
                var offset = rng.NextInt(availableProducts.Length);
                for (var productIndex = 0; productIndex < availableProducts.Length; productIndex++)
                {
                    var product = availableProducts[(productIndex + offset) % availableProducts.Length];
                    offerImpressions++;
                    if (product.PurchaseLimitPerSeason is int personalLimit &&
                        player.Purchases.GetValueOrDefault(product.Sku) >= personalLimit)
                    {
                        continue;
                    }
                    if (globalStockRemaining[product.Sku] <= 0 ||
                        player.Balances[product.PriceCurrency] < product.UnitPrice ||
                        !rng.Chance(1_400))
                    {
                        continue;
                    }

                    player.Balances[product.PriceCurrency] = checked(
                        player.Balances[product.PriceCurrency] - product.UnitPrice);
                    player.Purchases[product.Sku] = player.Purchases.GetValueOrDefault(product.Sku) + 1;
                    globalStockRemaining[product.Sku]--;
                    productPurchases[product.Sku]++;
                    productSpend[product.Sku] = checked(productSpend[product.Sku] + product.UnitPrice);
                    consumed[product.PriceCurrency] = checked(consumed[product.PriceCurrency] + product.UnitPrice);
                    buyers.Add(player.Id);
                }
            }
        }

        var currencyMetrics = currencies
            .Select(currency =>
            {
                var balances = players.Select(player => player.Balances[currency]).Order().ToArray();
                return new EconomySimulationCurrencyMetrics(
                    currency,
                    0,
                    produced[currency],
                    consumed[currency],
                    balances.Sum(),
                    PercentileNearestRank(balances, 50),
                    PercentileNearestRank(balances, 95));
            })
            .OrderBy(metric => metric.Currency)
            .ToArray();
        var primary = currencyMetrics.Single(metric => metric.Currency == ExtractionCurrency.SeasonVoucher);
        var sortedResourceEarnings = resourceEarningsByPlayer.Order().ToArray();
        var purchases = productPurchases.Values.Sum();
        var purchaseMetrics = new EconomySimulationPurchaseMetrics(
            purchases,
            buyers.Count,
            checked((int)(buyers.Count * 10_000L / options.PlayerCount)),
            offerImpressions,
            offerImpressions == 0 ? 0 : checked((int)(purchases * 10_000L / offerImpressions)),
            products.Select(product => new EconomySimulationProductMetrics(
                    product.Sku,
                    product.PriceCurrency,
                    productPurchases[product.Sku],
                    productSpend[product.Sku]))
                .ToArray());
        var resourceMetrics = new EconomySimulationResourceMetrics(
            exchangeCount,
            resourceUnits,
            resourceEarningsByPlayer.Sum(),
            resourceEarningsByPlayer.Sum() / options.PlayerCount,
            PercentileNearestRank(sortedResourceEarnings, 50),
            PercentileNearestRank(sortedResourceEarnings, 95));
        var actuals = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["primaryCurrencyProducedPerPlayer"] = primary.Produced / options.PlayerCount,
            ["primaryCurrencyConsumptionRatio"] = primary.Produced == 0
                ? 0
                : (long)((decimal)primary.Consumed * 10_000m / primary.Produced),
            ["primaryCurrencyEndingBalanceMedian"] = primary.EndingBalanceMedian,
            ["primaryCurrencyEndingBalanceP95"] = primary.EndingBalanceP95,
            ["uniqueBuyerRate"] = purchaseMetrics.UniqueBuyerRateBasisPoints,
            ["purchasePerImpressionRate"] = purchaseMetrics.PurchasePerImpressionBasisPoints,
            ["resourceExchangeEarningsPerPlayer"] = resourceMetrics.PrimaryCurrencyEarningsPerPlayer
        };
        var assessments = targets.Select(target =>
        {
            if (!actuals.TryGetValue(target.Metric, out var actual))
            {
                throw new ArgumentException($"Unknown simulation target metric '{target.Metric}'.", nameof(targets));
            }
            return new EconomySimulationAssessment(
                target.Metric,
                actual,
                target.MinimumInclusive,
                target.MaximumInclusive,
                target.Unit,
                actual >= target.MinimumInclusive && actual <= target.MaximumInclusive);
        }).ToArray();

        return new EconomySimulationReport(
            options.Seed,
            options.PlayerCount,
            options.BusinessDays,
            options.StartingBusinessDate,
            contentVersionId,
            EconomyContentCanonicalizer.Hash(content),
            ExtractionCurrency.SeasonVoucher,
            currencyMetrics,
            resourceMetrics,
            purchaseMetrics,
            assessments,
            assessments.All(assessment => assessment.WithinTarget),
            "Deterministic scenario model only: no crafting recipes, player trading, inflation response, " +
            "drop-table probabilities, churn, or live-server latency are modeled.");
    }

    private static ContentTaskDefinition[] SelectTasks(
        EconomyContentDefinition content,
        ContentTaskCadence cadence)
    {
        var pool = cadence == ContentTaskCadence.Daily
            ? content.Rotation.DailyTaskPool
            : content.Rotation.WeeklyTaskPool;
        var count = cadence == ContentTaskCadence.Daily
            ? content.Rotation.DailyTaskCount
            : content.Rotation.WeeklyTaskCount;
        var byKey = content.Tasks
            .Where(task => task.Active && task.Cadence == cadence)
            .ToDictionary(task => task.TaskKey, StringComparer.OrdinalIgnoreCase);
        return pool
            .Where(byKey.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, count))
            .Select(key => byKey[key])
            .ToArray();
    }

    private static void Credit(
        SimulatedPlayer player,
        ExtractionCurrency currency,
        long amount,
        IDictionary<ExtractionCurrency, long> produced)
    {
        player.Balances[currency] = checked(player.Balances[currency] + amount);
        produced[currency] = checked(produced[currency] + amount);
    }

    private static bool IsAvailable(ContentProductDefinition product, DateOnly businessDate)
    {
        var sample = new DateTimeOffset(
            businessDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            TimeSpan.Zero);
        return (product.AvailableFrom is null || product.AvailableFrom <= sample) &&
               (product.AvailableUntil is null || product.AvailableUntil > sample);
    }

    private static long PercentileNearestRank(IReadOnlyList<long> sortedValues, int percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }
        var rank = (int)Math.Ceiling(percentile / 100m * sortedValues.Count);
        return sortedValues[Math.Clamp(rank - 1, 0, sortedValues.Count - 1)];
    }

    private sealed record SimulatedResource(
        ContentResourceDefinition Definition,
        int MaximumMultiplierBasisPoints);

    private sealed class SimulatedPlayer
    {
        public SimulatedPlayer(int id, IEnumerable<ExtractionCurrency> currencies)
        {
            Id = id;
            Balances = currencies.ToDictionary(currency => currency, _ => 0L);
        }

        public int Id { get; }
        public Dictionary<ExtractionCurrency, long> Balances { get; }
        public Dictionary<string, int> Purchases { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StablePrng
    {
        private ulong _state;

        public StablePrng(ulong seed)
        {
            _state = seed;
        }

        public bool Chance(int basisPoints) => NextInt(10_000) < basisPoints;

        public int NextInt(int exclusiveMaximum)
        {
            if (exclusiveMaximum <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
            }
            return (int)(NextUInt64() % (uint)exclusiveMaximum);
        }

        private ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}

using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Content;

public sealed record EconomyPermanentCurrencyIssue(
    string Code,
    string Path,
    string Message);

public sealed record EconomyPermanentCurrencySourceAssessment(
    string TaskKey,
    ContentTaskCadence Cadence,
    ContentTaskEventKind EventKind,
    long RewardPerInstance);

public sealed record EconomyPermanentCurrencySinkAssessment(
    string Sku,
    string Category,
    long UnitPrice,
    int PurchaseLimitPerSeason,
    long MaximumSpendPerPlayerPerSeason);

public sealed record EconomyPermanentCurrencyAnalysis(
    IReadOnlyList<EconomyPermanentCurrencyIssue> Errors,
    IReadOnlyList<EconomyPermanentCurrencyIssue> Warnings,
    IReadOnlyList<EconomyPermanentCurrencySourceAssessment> Sources,
    IReadOnlyList<EconomyPermanentCurrencySinkAssessment> Sinks,
    long MinimumMarketCoinInflowPerPlayerPerWeek,
    long MaximumMarketCoinInflowPerPlayerPerWeek,
    long MaximumMarketCoinOutflowPerPlayerPerWeek,
    bool Complete);

/// <summary>
/// Proves the bounded permanent-currency contract used by Scheme A. Only
/// version-pinned reliable task instances count as gameplay inflow. Daily
/// instances can exist once per business date and weekly instances once per
/// week; the runtime/store enforce uniqueness for both the instance and its
/// wallet reward. Active MarketCoin products count as recurring sinks only
/// when price and per-season personal limit make their outflow finite.
/// </summary>
public static class EconomyPermanentCurrencyAnalyzer
{
    public const int BusinessDaysPerWeek = 7;

    public static EconomyPermanentCurrencyAnalysis Analyze(
        EconomyContentDefinition definition,
        IReadOnlySet<string>? knownItemIds = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new List<EconomyPermanentCurrencyIssue>();
        var warnings = new List<EconomyPermanentCurrencyIssue>();
        var taskMap = definition.Tasks
            .Where(task => task.Active)
            .GroupBy(task => task.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var daily = AssessCadence(
            ContentTaskCadence.Daily,
            definition.Rotation.DailyTaskPool,
            definition.Rotation.DailyTaskCount,
            taskMap);
        var weekly = AssessCadence(
            ContentTaskCadence.Weekly,
            definition.Rotation.WeeklyTaskPool,
            definition.Rotation.WeeklyTaskCount,
            taskMap);
        var sources = daily.Sources.Concat(weekly.Sources)
            .DistinctBy(source => (source.TaskKey.ToUpperInvariant(), source.Cadence))
            .OrderBy(source => source.Cadence)
            .ThenBy(source => source.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        long minimumInflow = 0;
        long maximumInflow = 0;
        try
        {
            minimumInflow = checked(daily.MinimumSelectedReward * BusinessDaysPerWeek +
                                    weekly.MinimumSelectedReward);
            maximumInflow = checked(daily.MaximumSelectedReward * BusinessDaysPerWeek +
                                    weekly.MaximumSelectedReward);
        }
        catch (OverflowException)
        {
            Add(errors, "MARKET_COIN_WEEKLY_INFLOW_OVERFLOW", "/tasks",
                "The selected daily/weekly MarketCoin rewards do not have a finite 64-bit weekly upper bound.");
        }

        var sourceSummary = DescribeSources(sources);
        if (maximumInflow <= 0)
        {
            Add(errors, "BOUNDED_MARKET_COIN_GAMEPLAY_SOURCE_REQUIRED", "/rotation",
                "Modern Scheme A content requires at least one scheduled positive MarketCoin reliable-task " +
                $"reward. Scheduled sources: {sourceSummary}. Bootstrap grants and administrator adjustments " +
                "do not count as repeatable gameplay sources.");
        }
        else if (minimumInflow <= 0)
        {
            Add(errors, "MARKET_COIN_SOURCE_NOT_GUARANTEED", "/rotation",
                "The task rotation can select a week with zero MarketCoin gameplay inflow. " +
                $"Scheduled positive sources: {sourceSummary}. Increase the selected count or remove zero/non-MarketCoin alternatives.");
        }

        var scheduledKeys = sources.Select(source => source.TaskKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var task in definition.Tasks.Where(task =>
                     task.Active &&
                     task.Reward.Currency == ExtractionCurrency.MarketCoin &&
                     task.Reward.Amount > 0 &&
                     !scheduledKeys.Contains(task.TaskKey)))
        {
            Add(warnings, "UNSCHEDULED_MARKET_COIN_REWARD_NOT_COUNTED", "/tasks",
                $"Task '{task.TaskKey}' grants {task.Reward.Amount} MarketCoin but is not in its " +
                $"{task.Cadence} rotation pool, so it contributes zero to the bounded gameplay-source calculation.");
        }

        var sinks = new List<EconomyPermanentCurrencySinkAssessment>();
        var sinkSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < definition.Products.Count; index++)
        {
            var product = definition.Products[index];
            if (!product.Active || product.PriceCurrency != ExtractionCurrency.MarketCoin)
            {
                continue;
            }
            var path = $"/products/{index}";
            if (product.PurchaseLimitPerSeason is null)
            {
                Add(errors, "UNBOUNDED_MARKET_COIN_SINK", path + "/purchaseLimitPerSeason",
                    $"Active MarketCoin SKU '{product.Sku}' has no personal per-season limit, so weekly " +
                    "per-player outflow cannot be bounded.");
                continue;
            }
            var grantsValid = product.ItemGrants.Count is >= 1 and <= 100 &&
                              product.ItemGrants.All(grant =>
                                  !string.IsNullOrWhiteSpace(grant.ItemId) &&
                                  grant.Quantity is > 0 and <= 1_000_000 &&
                                  (knownItemIds is null || knownItemIds.Contains(grant.ItemId)));
            if (product.UnitPrice <= 0 ||
                product.PurchaseLimitPerSeason <= 0 ||
                string.IsNullOrWhiteSpace(product.Category) ||
                !grantsValid ||
                !sinkSkus.Add(product.Sku))
            {
                Add(errors, "INVALID_MARKET_COIN_SINK", path,
                    $"Active MarketCoin SKU '{product.Sku}' counts as a long-term sink only with a positive " +
                    "price, finite positive personal limit, unique SKU/category, and approved positive item grants.");
                continue;
            }
            try
            {
                sinks.Add(new EconomyPermanentCurrencySinkAssessment(
                    product.Sku,
                    product.Category,
                    product.UnitPrice,
                    product.PurchaseLimitPerSeason.Value,
                    checked(product.UnitPrice * product.PurchaseLimitPerSeason.Value)));
            }
            catch (OverflowException)
            {
                Add(errors, "MARKET_COIN_SINK_OUTFLOW_OVERFLOW", path,
                    $"SKU '{product.Sku}' does not have a finite 64-bit per-player weekly outflow bound.");
            }
        }

        sinks.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Sku, right.Sku));
        if (sinks.Count < 2)
        {
            Add(errors, "MARKET_COIN_SINKS_REQUIRED", "/products",
                "Modern Scheme A content requires at least two active, positive-price, personally limited " +
                $"MarketCoin sinks with valid grants. Eligible sinks: {DescribeSinks(sinks)}.");
        }
        else if (sinks.Select(sink => sink.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
        {
            Add(warnings, "MARKET_COIN_SINK_CATEGORY_DIVERSITY_RECOMMENDED", "/products",
                $"Eligible MarketCoin sinks [{DescribeSinks(sinks)}] all use one category; distinct gameplay uses are recommended.");
        }

        long maximumOutflow = 0;
        try
        {
            maximumOutflow = sinks.Aggregate(
                0L,
                (total, sink) => checked(total + sink.MaximumSpendPerPlayerPerSeason));
        }
        catch (OverflowException)
        {
            Add(errors, "MARKET_COIN_WEEKLY_OUTFLOW_OVERFLOW", "/products",
                "Eligible MarketCoin sinks do not have a finite aggregate 64-bit weekly per-player outflow bound.");
        }

        return new EconomyPermanentCurrencyAnalysis(
            errors,
            warnings,
            sources,
            sinks,
            minimumInflow,
            maximumInflow,
            maximumOutflow,
            Complete: errors.Count == 0);
    }

    private static CadenceAssessment AssessCadence(
        ContentTaskCadence cadence,
        IReadOnlyList<string> pool,
        int selectedCount,
        IReadOnlyDictionary<string, ContentTaskDefinition> tasks)
    {
        var entries = pool
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(taskKey => tasks.TryGetValue(taskKey, out var task) && task.Cadence == cadence
                ? task
                : null)
            .ToArray();
        var count = Math.Clamp(selectedCount, 0, entries.Length);
        var rewards = entries.Select(task => task is not null &&
                                             task.Reward.Currency == ExtractionCurrency.MarketCoin &&
                                             task.Reward.Amount > 0
                ? task.Reward.Amount
                : 0L)
            .ToArray();
        var sources = entries
            .Where(task => task is not null &&
                           task.Reward.Currency == ExtractionCurrency.MarketCoin &&
                           task.Reward.Amount > 0)
            .Select(task => new EconomyPermanentCurrencySourceAssessment(
                task!.TaskKey,
                cadence,
                task.EventKind,
                task.Reward.Amount))
            .ToArray();
        return new CadenceAssessment(
            rewards.Order().Take(count).Aggregate(0L, CheckedAdd),
            rewards.OrderDescending().Take(count).Aggregate(0L, CheckedAdd),
            sources);
    }

    private static long CheckedAdd(long total, long amount) => checked(total + amount);

    private static string DescribeSources(IReadOnlyList<EconomyPermanentCurrencySourceAssessment> sources) =>
        sources.Count == 0
            ? "none"
            : string.Join(", ", sources.Select(source =>
                $"{source.TaskKey}({source.Cadence}, {source.EventKind}, +{source.RewardPerInstance})"));

    private static string DescribeSinks(IReadOnlyList<EconomyPermanentCurrencySinkAssessment> sinks) =>
        sinks.Count == 0
            ? "none"
            : string.Join(", ", sinks.Select(sink =>
                $"{sink.Sku}({sink.Category}, {sink.UnitPrice} x {sink.PurchaseLimitPerSeason} = {sink.MaximumSpendPerPlayerPerSeason})"));

    private static void Add(
        ICollection<EconomyPermanentCurrencyIssue> issues,
        string code,
        string path,
        string message) => issues.Add(new EconomyPermanentCurrencyIssue(code, path, message));

    private sealed record CadenceAssessment(
        long MinimumSelectedReward,
        long MaximumSelectedReward,
        IReadOnlyList<EconomyPermanentCurrencySourceAssessment> Sources);
}

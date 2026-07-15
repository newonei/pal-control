using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ControlApi.Content;

public interface IReliableTaskContentProvider
{
    Task<EconomyRuntimeContent> GetCurrentAsync(CancellationToken cancellationToken);

    Task<EconomyRuntimeContent> GetForEventAsync(
        DateTimeOffset occurredAt,
        Guid? contentVersionId,
        string? contentHash,
        CancellationToken cancellationToken);
}

public sealed class ReliableTaskContentProvider : IReliableTaskContentProvider
{
    private readonly EconomyContentRuntimeService _content;
    private readonly IEconomyContentStore _store;
    private readonly string _serverId;
    private readonly int _dailyRefreshHour;
    private readonly TimeZoneInfo _timeZone;

    public ReliableTaskContentProvider(
        EconomyContentRuntimeService content,
        IEconomyContentStore store,
        IOptions<ExtractionModeOptions> options)
    {
        _content = content;
        _store = store;
        _serverId = options.Value.ServerId;
        _dailyRefreshHour = options.Value.DailyRefreshHour;
        _timeZone = options.Value.ResolveTimeZone();
    }

    public Task<EconomyRuntimeContent> GetCurrentAsync(CancellationToken cancellationToken) =>
        _content.EnsureCurrentForBusinessDateAsync(cancellationToken);

    public async Task<EconomyRuntimeContent> GetForEventAsync(
        DateTimeOffset occurredAt,
        Guid? contentVersionId,
        string? contentHash,
        CancellationToken cancellationToken)
    {
        var local = TimeZoneInfo.ConvertTime(occurredAt, _timeZone);
        var businessDate = DateOnly.FromDateTime(
            local.AddHours(-_dailyRefreshHour).DateTime);
        EconomyContentVersion? version;
        if (contentVersionId is Guid exactVersionId)
        {
            version = await _store.GetVersionAsync(exactVersionId, cancellationToken);
            if (version is null ||
                !string.Equals(version.ServerId, _serverId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(version.ContentHash, contentHash, StringComparison.Ordinal) ||
                version.PublishedAt > occurredAt)
            {
                throw new ReliableTaskException(
                    "TASK_CONTENT_EVIDENCE_MISMATCH",
                    "The source event's frozen content version/hash is missing or inconsistent.");
            }
        }
        else
        {
            version = (await _store.ListVersionsAsync(_serverId, cancellationToken))
                .Where(candidate =>
                    candidate.BusinessDate == businessDate &&
                    candidate.PublishedAt <= occurredAt)
                .OrderByDescending(candidate => candidate.PublishedAt)
                .ThenByDescending(candidate => candidate.VersionNumber)
                .FirstOrDefault();
        }
        if (version is null)
        {
            throw new ReliableTaskException(
                "TASK_CONTENT_VERSION_NOT_FOUND",
                "No complete published content version proves the rules active when this event occurred.");
        }
        return new EconomyRuntimeContent(
            version,
            EconomyContentRotation.Create(version),
            version.Definition.Products.ToDictionary(
                product => product.Sku,
                StringComparer.OrdinalIgnoreCase),
            version.Definition.Resources.Where(resource => resource.Active).ToDictionary(
                resource => resource.ItemId,
                StringComparer.OrdinalIgnoreCase),
            version.Definition.ExchangeZones.Where(zone => zone.Active).ToArray(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Projects only durable server facts into version-pinned daily and weekly
/// task instances. Currency rewards are an idempotent wallet outbox; ranking
/// rewards are committed atomically with task completion in the task store.
/// </summary>
public sealed class ReliableTaskRuntimeService
{
    private const string RewardReferenceType = "reliable-task-reward";
    private const string RewardActor = "reliable-task-runtime";

    private readonly SqliteReliableTaskStore _store;
    private readonly ExtractionCommerceService _commerce;
    private readonly IReliableTaskContentProvider _content;

    public ReliableTaskRuntimeService(
        SqliteReliableTaskStore store,
        ExtractionCommerceService commerce,
        IReliableTaskContentProvider content)
    {
        _store = store;
        _commerce = commerce;
        _content = content;
    }

    public async Task<ReliableTaskSnapshot> GetSnapshotAsync(
        Guid accountId,
        Guid seasonId,
        string serverId,
        CancellationToken cancellationToken)
    {
        var runtime = await _content.GetCurrentAsync(cancellationToken);
        var sets = await EnsureTaskSetsAsync(
            accountId,
            seasonId,
            serverId,
            runtime,
            cancellationToken);
        await GrantPendingCurrencyRewardsAsync(accountId, seasonId, cancellationToken);
        var currentSetIds = sets.Select(set => set.TaskSetId).ToHashSet();
        var tasks = (await _store.ListTasksAsync(accountId, seasonId, cancellationToken))
            .Where(task => currentSetIds.Contains(task.TaskSetId))
            .ToArray();
        return new ReliableTaskSnapshot(
            accountId,
            seasonId,
            serverId,
            await _store.GetRankingPointsAsync(accountId, seasonId, cancellationToken),
            tasks);
    }

    public async Task<ReliableTaskEventResult> RecordResourceSettlementAsync(
        ExtractionSettlementRun run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.State != ExtractionSettlementState.Settled || run.SettledAt is null)
        {
            throw new ReliableTaskException(
                "TASK_EVENT_NOT_AUTHORITATIVE",
                "Only a durably settled resource exchange may advance reliable tasks.");
        }
        if (run.Items.Count == 0 || run.TotalValue <= 0 ||
            run.Items.Any(item => item.Quantity <= 0 || item.UnitValue <= 0 ||
                                  item.TotalValue != checked(item.Quantity * item.UnitValue)) ||
            run.TotalValue != run.Items.Sum(item => item.TotalValue))
        {
            throw new ReliableTaskException(
                "TASK_SETTLEMENT_SNAPSHOT_INVALID",
                "The settled resource exchange does not contain a consistent frozen value snapshot.");
        }
        var serverId = await ResolveServerIdAsync(cancellationToken);
        var economyEvent = new ReliableEconomyEvent(
            $"resource-settlement:{run.RunId:D}",
            ReliableEconomyEventSource.ResourceSettlement,
            run.AccountId,
            run.SeasonId,
            serverId,
            run.ZoneId,
            run.Items
                .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ReliableTaskItemAmount(
                    group.Key,
                    group.Sum(item => (long)item.Quantity)))
                .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            run.TotalValue,
            [],
            run.ContentVersionId,
            run.ContentHash,
            run.SettledAt.Value);
        return await RecordAsync(economyEvent, cancellationToken);
    }

    public async Task<ReliableTaskEventResult> RecordDeliveredOrderAsync(
        ShopOrder order,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.State != ShopOrderState.Delivered)
        {
            throw new ReliableTaskException(
                "TASK_EVENT_NOT_AUTHORITATIVE",
                "Only a shop order with a durable successful-delivery outcome may advance reliable tasks.");
        }
        var expectedCharges = order.Lines
            .GroupBy(line => line.PriceCurrency)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(line => line.LineTotal));
        var actualCharges = order.Charges
            .GroupBy(charge => charge.Currency)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(charge => charge.Amount));
        if (order.Lines.Count == 0 ||
            order.Lines.Any(line => line.Quantity <= 0 || line.UnitPrice <= 0 ||
                                    line.LineTotal != checked(line.Quantity * line.UnitPrice)) ||
            actualCharges.Count == 0 || actualCharges.Any(charge => charge.Value <= 0) ||
            expectedCharges.Count != actualCharges.Count ||
            expectedCharges.Any(charge =>
                actualCharges.GetValueOrDefault(charge.Key) != charge.Value))
        {
            throw new ReliableTaskException(
                "TASK_ORDER_CHARGES_INVALID",
                "The delivered order does not contain a valid frozen currency charge.");
        }
        var economyEvent = new ReliableEconomyEvent(
            $"shop-order-delivered:{order.OrderId:D}",
            ReliableEconomyEventSource.ShopOrderDelivery,
            order.AccountId,
            order.SeasonId,
            order.ServerId,
            null,
            [],
            0,
            order.Charges
                .GroupBy(charge => charge.Currency)
                .Select(group => new ReliableTaskCurrencyAmount(
                    group.Key,
                    group.Sum(charge => charge.Amount)))
                .OrderBy(charge => charge.Currency)
                .ToArray(),
            null,
            null,
            order.UpdatedAt);
        return await RecordAsync(economyEvent, cancellationToken);
    }

    private async Task<ReliableTaskEventResult> RecordAsync(
        ReliableEconomyEvent economyEvent,
        CancellationToken cancellationToken)
    {
        var runtime = await _content.GetForEventAsync(
            economyEvent.OccurredAt,
            economyEvent.ContentVersionId,
            economyEvent.ContentHash,
            cancellationToken);
        var sets = await EnsureTaskSetsAsync(
            economyEvent.AccountId,
            economyEvent.SeasonId,
            economyEvent.ServerId,
            runtime,
            cancellationToken);
        var setIds = sets.Select(set => set.TaskSetId).ToHashSet();
        var result = await _store.ApplyEventAsync(economyEvent, setIds, cancellationToken);
        await GrantPendingCurrencyRewardsAsync(
            economyEvent.AccountId,
            economyEvent.SeasonId,
            cancellationToken);
        var tasks = (await _store.ListTasksAsync(
                economyEvent.AccountId,
                economyEvent.SeasonId,
                cancellationToken))
            .Where(task => setIds.Contains(task.TaskSetId))
            .ToArray();
        return new ReliableTaskEventResult(result.Applied, result.Replayed, tasks);
    }

    public Task<bool> HasResourceSettlementEventAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        _store.HasEventAsync($"resource-settlement:{runId:D}", cancellationToken);

    public Task<bool> HasDeliveredOrderEventAsync(
        Guid orderId,
        CancellationToken cancellationToken) =>
        _store.HasEventAsync($"shop-order-delivered:{orderId:D}", cancellationToken);

    public async Task<int> RecoverPendingCurrencyRewardsAsync(
        CancellationToken cancellationToken)
    {
        var scopes = await _store.ListPendingCurrencyRewardScopesAsync(cancellationToken);
        foreach (var scope in scopes)
        {
            await GrantPendingCurrencyRewardsAsync(
                scope.AccountId,
                scope.SeasonId,
                cancellationToken);
        }
        return scopes.Count;
    }

    private async Task<IReadOnlyList<ReliableTaskSet>> EnsureTaskSetsAsync(
        Guid accountId,
        Guid seasonId,
        string serverId,
        EconomyRuntimeContent runtime,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty || seasonId == Guid.Empty)
        {
            throw new ArgumentException("Reliable tasks require non-empty account and season ids.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (!string.Equals(runtime.Version.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReliableTaskException(
                "TASK_SERVER_MISMATCH",
                "The authoritative event belongs to another server's content rotation.");
        }
        var daily = await EnsureTaskSetAsync(
            accountId,
            seasonId,
            runtime,
            ContentTaskCadence.Daily,
            runtime.Version.BusinessDate.ToString("yyyy-MM-dd"),
            runtime.Rotation.Seed,
            runtime.Version.Definition.Rotation.DailyTaskPool,
            runtime.Version.Definition.Rotation.DailyTaskCount,
            cancellationToken);

        var businessDay = runtime.Version.BusinessDate;
        var daysSinceMonday = ((int)businessDay.DayOfWeek + 6) % 7;
        var weekStart = businessDay.AddDays(-daysSinceMonday);
        var weeklySeed = Hash(string.Join('|',
            runtime.Version.ServerId,
            weekStart.ToString("yyyy-MM-dd"),
            runtime.Version.Definition.Rotation.RulesVersion,
            runtime.Version.Definition.Rotation.AlgorithmVersion,
            runtime.Version.Definition.Rotation.SeedNamespace,
            runtime.Version.ContentHash,
            "weekly"));
        var weekly = await EnsureTaskSetAsync(
            accountId,
            seasonId,
            runtime,
            ContentTaskCadence.Weekly,
            weekStart.ToString("yyyy-MM-dd"),
            weeklySeed,
            runtime.Version.Definition.Rotation.WeeklyTaskPool,
            runtime.Version.Definition.Rotation.WeeklyTaskCount,
            cancellationToken);
        return [daily, weekly];
    }

    private Task<ReliableTaskSet> EnsureTaskSetAsync(
        Guid accountId,
        Guid seasonId,
        EconomyRuntimeContent runtime,
        ContentTaskCadence cadence,
        string periodKey,
        string seed,
        IReadOnlyList<string> pool,
        int count,
        CancellationToken cancellationToken)
    {
        var definitions = runtime.Version.Definition.Tasks
            .Where(task => task.Active && task.Cadence == cadence)
            .ToDictionary(task => task.TaskKey, StringComparer.OrdinalIgnoreCase);
        var selected = pool
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(definitions.ContainsKey)
            .Select(taskKey => new
            {
                Task = definitions[taskKey],
                SortKey = Hash($"{seed}|task|{taskKey.ToLowerInvariant()}")
            })
            .OrderBy(item => item.SortKey, StringComparer.Ordinal)
            .Take(Math.Clamp(count, 0, pool.Count))
            .Select(item => item.Task)
            .ToArray();
        return _store.EnsureTaskSetAsync(
            new ReliableTaskSetDefinition(
                accountId,
                seasonId,
                runtime.Version.ServerId,
                cadence,
                periodKey,
                runtime.Version.VersionId,
                runtime.Version.ContentHash,
                runtime.Version.RulesVersion,
                seed,
                selected),
            cancellationToken);
    }

    private async Task GrantPendingCurrencyRewardsAsync(
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        var tasks = await _store.ListTasksAsync(accountId, seasonId, cancellationToken);
        foreach (var task in tasks.Where(task =>
                     task.Completed &&
                     task.Definition.Reward.Amount > 0 &&
                     task.CurrencyRewardLedgerEntryId is null))
        {
            var reward = task.Definition.Reward;
            var result = await _commerce.AdjustWalletAsync(
                new WalletAdjustmentRequest(
                    task.AccountId,
                    reward.Currency == ExtractionCurrency.SeasonVoucher
                        ? task.SeasonId
                        : null,
                    reward.Currency,
                    reward.Amount,
                    $"Reliable task reward: {task.Definition.TaskKey}",
                    RewardReferenceType,
                    task.InstanceId.ToString("N"),
                    RewardActor,
                    $"task-reward:{task.InstanceId:N}:{reward.Currency}"),
                cancellationToken);
            if (result.IdempotencyConflict || result.ErrorCode is not null || result.LedgerEntry is null)
            {
                throw new ReliableTaskException(
                    result.ErrorCode ?? "TASK_REWARD_GRANT_FAILED",
                    result.ErrorMessage ?? "The reliable task reward could not be durably credited.");
            }
            await _store.MarkCurrencyRewardGrantedAsync(
                task.InstanceId,
                result.LedgerEntry.EntryId,
                cancellationToken);
        }
    }

    private async Task<string> ResolveServerIdAsync(CancellationToken cancellationToken)
    {
        var runtime = await _content.GetCurrentAsync(cancellationToken);
        return runtime.Version.ServerId;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

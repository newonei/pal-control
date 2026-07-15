using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var directory = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-reliable-tasks-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    await VerifyReplayRestartAndUniqueRewardsAsync(directory, CancellationToken.None);
    Console.WriteLine(
        "PASS: six version-pinned daily/weekly reliable tasks, authoritative event gating, 20x replay, unique currency/ranking rewards, and restart persistence.");
    return 0;
}
finally
{
    for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException) when (attempt < 4)
        {
            await Task.Delay(50);
        }
        catch (UnauthorizedAccessException) when (attempt < 4)
        {
            await Task.Delay(50);
        }
    }
}

static async Task VerifyReplayRestartAndUniqueRewardsAsync(
    string directory,
    CancellationToken cancellationToken)
{
    var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
    var runtime = CreateRuntimeContent(DateOnly.FromDateTime(now.UtcDateTime));
    var provider = new FixedTaskContentProvider(runtime);
    var runId = Guid.NewGuid();
    var orderId = Guid.NewGuid();
    Guid accountId;
    Guid recoveryAccountId;
    Guid seasonId;

    using (var repository = new SqliteExtractionRepository(directory))
    using (var taskStore = new SqliteReliableTaskStore(directory))
    {
        var commerce = new ExtractionCommerceService(repository);
        var season = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                runtime.Version.ServerId,
                "WEEK-RELIABLE",
                "Reliable task week",
                Guid.NewGuid().ToString("N"),
                now.AddDays(-1),
                now.AddDays(6),
                ExtractionSeasonState.Active),
            null,
            cancellationToken);
        var account = await repository.GetOrCreateAccountAsync(
            "steam",
            "steam_reliable_tasks",
            "Reliable Tasks",
            cancellationToken);
        accountId = account.AccountId;
        seasonId = season.SeasonId;
        var service = new ReliableTaskRuntimeService(taskStore, commerce, provider);

        var initial = await service.GetSnapshotAsync(
            accountId,
            seasonId,
            runtime.Version.ServerId,
            cancellationToken);
        Assert(initial.Tasks.Count == 6, "Exactly three daily and three weekly instances were not created.");
        Assert(initial.Tasks.Select(task => task.InstanceId).Distinct().Count() == 6,
            "Task instance ids are not unique.");
        Assert(initial.Tasks.All(task =>
                task.ContentVersionId == runtime.Version.VersionId &&
                task.ContentHash == runtime.Version.ContentHash &&
                task.RulesVersion == runtime.Version.RulesVersion &&
                !string.IsNullOrWhiteSpace(task.RotationSeed)),
            "Task instances are not pinned to complete content/rotation evidence.");

        var settled = CreateSettledRun(runId, accountId, seasonId, now, runtime.Version);
        for (var delivery = 0; delivery < 20; delivery++)
        {
            var result = await service.RecordResourceSettlementAsync(settled, cancellationToken);
            Assert(delivery == 0 ? result.Applied : result.Replayed,
                "Resource event replay semantics are not stable.");
        }

        var deliveredOrder = CreateDeliveredOrder(
            orderId,
            accountId,
            seasonId,
            runtime.Version.ServerId,
            now);
        for (var delivery = 0; delivery < 20; delivery++)
        {
            var result = await service.RecordDeliveredOrderAsync(deliveredOrder, cancellationToken);
            Assert(delivery == 0 ? result.Applied : result.Replayed,
                "Delivered-order event replay semantics are not stable.");
        }

        var completed = await service.GetSnapshotAsync(
            accountId,
            seasonId,
            runtime.Version.ServerId,
            cancellationToken);
        Assert(completed.Tasks.Count == 6 && completed.Tasks.All(task => task.Completed),
            "The six authoritative task kinds did not reach their frozen targets.");
        Assert(completed.Tasks.All(task => task.Progress == task.Definition.TargetAmount),
            "Repeated events advanced progress beyond its target.");
        Assert(completed.Tasks.All(task => task.RewardGranted),
            "A completed task reward remained pending.");
        Assert(completed.RankingPoints == 21,
            "Ranking-point rewards were not credited exactly once.");
        await AssertWalletRewardsAsync(repository, accountId, seasonId, cancellationToken);

        await AssertReliableTaskFailureAsync(
            () => service.RecordResourceSettlementAsync(
                settled with { State = ExtractionSettlementState.Uncertain, SettledAt = null },
                cancellationToken),
            "TASK_EVENT_NOT_AUTHORITATIVE");
        await AssertReliableTaskFailureAsync(
            () => service.RecordDeliveredOrderAsync(
                deliveredOrder with { State = ShopOrderState.DeliveryUncertain },
                cancellationToken),
            "TASK_EVENT_NOT_AUTHORITATIVE");
        await AssertReliableTaskFailureAsync(
            () => service.RecordResourceSettlementAsync(
                settled with
                {
                    Items = [new ExtractionLootLine("Leather", "Leather", 6, 10, 60)],
                    ItemCount = 6,
                    TotalValue = 60
                },
                cancellationToken),
            "TASK_EVENT_IDEMPOTENCY_CONFLICT");

        // Model a crash after task completion/ranking commit but before the
        // independent wallet outbox is drained. The restarted runtime must
        // recover every pending currency reward without another source event.
        var recoveryAccount = await repository.GetOrCreateAccountAsync(
            "steam",
            "steam_reliable_reward_recovery",
            "Reliable Reward Recovery",
            cancellationToken);
        recoveryAccountId = recoveryAccount.AccountId;
        var recoverySnapshot = await service.GetSnapshotAsync(
            recoveryAccountId,
            seasonId,
            runtime.Version.ServerId,
            cancellationToken);
        var directEvent = new ReliableEconomyEvent(
            $"resource-settlement:{Guid.NewGuid():D}",
            ReliableEconomyEventSource.ResourceSettlement,
            recoveryAccountId,
            seasonId,
            runtime.Version.ServerId,
            "exchange-a",
            [new ReliableTaskItemAmount("Leather", 5)],
            50,
            [],
            runtime.Version.VersionId,
            runtime.Version.ContentHash,
            now);
        var storedOnly = await taskStore.ApplyEventAsync(
            directEvent,
            recoverySnapshot.Tasks.Select(task => task.TaskSetId).ToHashSet(),
            cancellationToken);
        Assert(storedOnly.Applied && storedOnly.Tasks.Count(task => task.Completed) == 4,
            "The simulated pre-wallet crash did not leave four completed task rewards pending.");
        Assert((await repository.GetLedgerAsync(
                recoveryAccountId,
                seasonId,
                100,
                cancellationToken)).Count == 0,
            "The store-only crash fixture unexpectedly credited the wallet.");
    }

    // Reopen both independent durable stores and replay each source event 20
    // more times. No instance, progress, wallet reward, or ranking reward may
    // be recreated after process restart.
    using (var repository = new SqliteExtractionRepository(directory))
    using (var taskStore = new SqliteReliableTaskStore(directory))
    {
        var service = new ReliableTaskRuntimeService(
            taskStore,
            new ExtractionCommerceService(repository),
            provider);
        Assert(await service.RecoverPendingCurrencyRewardsAsync(cancellationToken) == 1,
            "Restart did not discover the durable pending wallet-reward outbox.");
        Assert(await service.RecoverPendingCurrencyRewardsAsync(cancellationToken) == 0,
            "The recovered reward outbox remained pending after exact-once credit.");
        var recoveredLedger = await repository.GetLedgerAsync(
            recoveryAccountId,
            seasonId,
            100,
            cancellationToken);
        Assert(recoveredLedger.Count(entry => entry.ReferenceType == "reliable-task-reward") == 4,
            "Restart did not credit each pending task reward exactly once.");
        var recoveredWallet = await repository.GetWalletAsync(
            recoveryAccountId,
            seasonId,
            cancellationToken);
        Assert(recoveredWallet.MarketCoin.Balance == 90 &&
               recoveredWallet.SeasonVoucher.Balance == 30,
            "Recovered pending wallet rewards have incorrect balances.");
        var settled = CreateSettledRun(runId, accountId, seasonId, now, runtime.Version);
        var order = CreateDeliveredOrder(
            orderId,
            accountId,
            seasonId,
            runtime.Version.ServerId,
            now);
        for (var delivery = 0; delivery < 20; delivery++)
        {
            Assert((await service.RecordResourceSettlementAsync(settled, cancellationToken)).Replayed,
                "Resource replay was not remembered after restart.");
            Assert((await service.RecordDeliveredOrderAsync(order, cancellationToken)).Replayed,
                "Order replay was not remembered after restart.");
        }
        var snapshot = await service.GetSnapshotAsync(
            accountId,
            seasonId,
            runtime.Version.ServerId,
            cancellationToken);
        Assert(snapshot.Tasks.Count == 6, "Restart created duplicate task instances.");
        Assert(snapshot.RankingPoints == 21, "Restart duplicated ranking rewards.");
        await AssertWalletRewardsAsync(repository, accountId, seasonId, cancellationToken);
    }
}

static async Task AssertWalletRewardsAsync(
    SqliteExtractionRepository repository,
    Guid accountId,
    Guid seasonId,
    CancellationToken cancellationToken)
{
    var ledger = await repository.GetLedgerAsync(accountId, seasonId, 100, cancellationToken);
    var rewards = ledger.Where(entry => entry.ReferenceType == "reliable-task-reward").ToArray();
    Assert(rewards.Length == 6, "A task currency reward was duplicated or omitted.");
    Assert(rewards.Select(entry => entry.ReferenceId).Distinct(StringComparer.Ordinal).Count() == 6,
        "More than one wallet reward exists for a task instance.");
    var wallet = await repository.GetWalletAsync(accountId, seasonId, cancellationToken);
    Assert(wallet.MarketCoin.Balance == 180, "MarketCoin task rewards are not exact.");
    Assert(wallet.SeasonVoucher.Balance == 30, "SeasonVoucher task rewards are not exact.");
}

static EconomyRuntimeContent CreateRuntimeContent(DateOnly businessDate)
{
    var tasks = new[]
    {
        CreateTask("daily-exchange", ContentTaskCadence.Daily,
            ContentTaskEventKind.ResourceExchangeSettled, 1, null, null, 10, 1),
        CreateTask("daily-item", ContentTaskCadence.Daily,
            ContentTaskEventKind.ResourceItemSettled, 5, "Leather", null, 20, 2),
        CreateTask("daily-value", ContentTaskCadence.Daily,
            ContentTaskEventKind.ResourceValueSettled, 50, null, null, 30, 3,
            ExtractionCurrency.SeasonVoucher),
        CreateTask("weekly-orders", ContentTaskCadence.Weekly,
            ContentTaskEventKind.ShopOrderDelivered, 1, null, null, 40, 4),
        CreateTask("weekly-spend", ContentTaskCadence.Weekly,
            ContentTaskEventKind.CurrencySpent, 100, null,
            ExtractionCurrency.SeasonVoucher, 50, 5),
        CreateTask("weekly-item", ContentTaskCadence.Weekly,
            ContentTaskEventKind.ResourceItemSettled, 5, "Leather", null, 60, 6)
    };
    var definition = new EconomyContentDefinition(
        1,
        "local",
        "Reliable task fixture",
        new EconomyContentDependencies("weekly-economy-v1", "catalog", "game", "plugin"),
        "UTC",
        0,
        [],
        [],
        [],
        tasks,
        new ContentRotationPolicy(
            "weekly-economy-v1",
            1,
            "reliable-task-test",
            ["daily-exchange", "daily-item", "daily-value"],
            3,
            ["weekly-orders", "weekly-spend", "weekly-item"],
            3,
            [],
            0));
    var version = new EconomyContentVersion(
        Guid.NewGuid(),
        definition.ServerId,
        1,
        businessDate,
        definition.Dependencies.RulesVersion,
        new string('a', 64),
        definition,
        Guid.NewGuid(),
        "test",
        DateTimeOffset.UtcNow);
    return new EconomyRuntimeContent(
        version,
        EconomyContentRotation.Create(version),
        new Dictionary<string, ContentProductDefinition>(),
        new Dictionary<string, ContentResourceDefinition>(),
        [],
        new HashSet<string>());
}

static ContentTaskDefinition CreateTask(
    string key,
    ContentTaskCadence cadence,
    ContentTaskEventKind eventKind,
    long target,
    string? itemId,
    ExtractionCurrency? targetCurrency,
    long reward,
    int rankingPoints,
    ExtractionCurrency rewardCurrency = ExtractionCurrency.MarketCoin) => new(
    key,
    key,
    key,
    cadence,
    eventKind,
    target,
    itemId,
    targetCurrency,
    [],
    new ContentTaskReward(rewardCurrency, reward, rankingPoints),
    true);

static ExtractionSettlementRun CreateSettledRun(
    Guid runId,
    Guid accountId,
    Guid seasonId,
    DateTimeOffset now,
    EconomyContentVersion contentVersion) => new(
    runId,
    accountId,
    seasonId,
    "steam_reliable_tasks",
    "exchange-a",
    "Exchange A",
    ExtractionSettlementState.Settled,
    [new ExtractionLootLine("Leather", "Leather", 5, 10, 50)],
    5,
    50,
    new string('b', 64),
    null,
    $"settlement:{runId:N}",
    null,
    null,
    now.AddMinutes(-1),
    now.AddMinutes(1),
    now,
    now)
{
    ContentVersionId = contentVersion.VersionId,
    ContentHash = contentVersion.ContentHash,
    ContentBusinessDate = contentVersion.BusinessDate,
    ContentRulesVersion = contentVersion.RulesVersion,
    RotationSeed = EconomyContentRotation.Create(contentVersion).Seed
};

static ShopOrder CreateDeliveredOrder(
    Guid orderId,
    Guid accountId,
    Guid seasonId,
    string serverId,
    DateTimeOffset now) => new(
    orderId,
    accountId,
    seasonId,
    serverId,
    "steam_reliable_tasks",
    [new ShopOrderLine(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "TASK-ORDER",
        "Task order",
        1,
        ExtractionCurrency.SeasonVoucher,
        100,
        100,
        [])],
    [new ShopOrderCharge(ExtractionCurrency.SeasonVoucher, 100)],
    ShopOrderState.Delivered,
    Guid.NewGuid(),
    1,
    $"purchase:{orderId:N}",
    "test",
    "test",
    now.AddSeconds(-10),
    now);

static async Task AssertReliableTaskFailureAsync(
    Func<Task> action,
    string expectedCode)
{
    try
    {
        await action();
        throw new InvalidOperationException($"Expected reliable task error '{expectedCode}'.");
    }
    catch (ReliableTaskException exception)
    {
        Assert(exception.Code == expectedCode,
            $"Expected reliable task error '{expectedCode}', got '{exception.Code}'.");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class FixedTaskContentProvider : IReliableTaskContentProvider
{
    private readonly EconomyRuntimeContent _content;

    public FixedTaskContentProvider(EconomyRuntimeContent content)
    {
        _content = content;
    }

    public System.Threading.Tasks.Task<EconomyRuntimeContent> GetCurrentAsync(
        CancellationToken cancellationToken) =>
        System.Threading.Tasks.Task.FromResult(_content);

    public System.Threading.Tasks.Task<EconomyRuntimeContent> GetForEventAsync(
        DateTimeOffset occurredAt,
        Guid? contentVersionId,
        string? contentHash,
        CancellationToken cancellationToken) =>
        System.Threading.Tasks.Task.FromResult(_content);
}

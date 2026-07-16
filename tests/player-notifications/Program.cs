using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var directory = Path.Combine(
    Path.GetTempPath(),
    "pal-control-player-notifications",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    await VerifyNotificationsAsync(directory, CancellationToken.None);
    Console.WriteLine(
        "PASS: four notification source classes, 20x replay, restart recovery, " +
        "safe-default game delivery, projection crash-window recovery, source updates, " +
        "read idempotency and A/B isolation.");
    return 0;
}
finally
{
    SqliteConnection.ClearAllPools();
    for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException && attempt < 4)
        {
            await Task.Delay(50);
        }
    }
}

static async Task VerifyNotificationsAsync(
    string directory,
    CancellationToken cancellationToken)
{
    var now = new DateTimeOffset(2026, 7, 16, 4, 0, 0, TimeSpan.Zero);
    var clock = new MutableTimeProvider(now);
    var accountA = Guid.NewGuid();
    var accountB = Guid.NewGuid();
    var season = Guid.NewGuid();
    var store = new PlayerNotificationStore(directory, clock);

    var delivered = Source(
        accountA,
        season,
        "order-delivery",
        "delivered",
        "success",
        "order-a",
        "v1",
        now,
        requestGame: false);
    for (var replay = 0; replay < 20; replay++)
    {
        var result = await store.UpsertAsync(delivered, cancellationToken);
        Assert(result.Created == (replay == 0), "20x replay changed create semantics.");
        Assert(result.Changed == (replay == 0), "20x replay changed projection state.");
    }
    var firstFeed = await store.ListForAccountAsync(accountA, 100, cancellationToken);
    Assert(firstFeed.Items.Count == 1 && firstFeed.UnreadCount == 1,
        "Replay created duplicate feed rows.");

    store = new PlayerNotificationStore(directory, clock);
    var restartedFeed = await store.ListForAccountAsync(accountA, 100, cancellationToken);
    Assert(restartedFeed.Items.Single().NotificationId == firstFeed.Items.Single().NotificationId,
        "Restart changed the deterministic notification id.");

    var settlement = Source(
        accountA,
        season,
        "resource-settlement",
        "settled",
        "success",
        "run-a",
        "v1",
        now.AddMinutes(1),
        requestGame: false);
    var seasonFrozen = Source(
        accountA,
        season,
        "season-end",
        "frozen",
        "info",
        "season-a",
        "frozen-v1",
        now.AddMinutes(2),
        requestGame: false);
    var reconciliation = Source(
        accountA,
        season,
        "reconciliation",
        "uncertain",
        "warning",
        "reconcile-a",
        "v1",
        now.AddMinutes(3),
        requestGame: false);
    await store.UpsertAsync(settlement, cancellationToken);
    var frozen = await store.UpsertAsync(seasonFrozen, cancellationToken);
    await store.UpsertAsync(reconciliation, cancellationToken);
    var fourClasses = await store.ListForAccountAsync(accountA, 100, cancellationToken);
    Assert(fourClasses.Items.Select(item => item.SourceType).Distinct().Count() == 4,
        "The four versioned notification source classes were not projected.");

    await store.MarkReadAsync(accountA, frozen.Notification.NotificationId, cancellationToken);
    clock.Advance(TimeSpan.FromMinutes(5));
    var seasonCompleted = seasonFrozen with
    {
        SourceVersion = "completed-v2",
        SourceState = "completed",
        Severity = "success",
        Title = "本周结算已完成",
        Message = "冻结成绩、永久币奖励与周券清零均已核对。",
        OccurredAt = clock.GetUtcNow()
    };
    var updated = await store.UpsertAsync(seasonCompleted, cancellationToken);
    Assert(updated.Changed && !updated.Created &&
        updated.Notification.NotificationId == frozen.Notification.NotificationId &&
        updated.Notification.ReadAt is null,
        "Season milestones did not update and re-open the same feed row.");

    var bSource = Source(
        accountB,
        season,
        "order-delivery",
        "failed",
        "error",
        "order-b",
        "v1",
        now,
        requestGame: false);
    var bCreated = await store.UpsertAsync(bSource, cancellationToken);
    var bFeed = await store.ListForAccountAsync(accountB, 100, cancellationToken);
    Assert(bFeed.Items.Count == 1 && bFeed.Items.Single().NotificationId == bCreated.Notification.NotificationId,
        "Account B feed was not isolated.");
    Assert(!bFeed.Items.Any(item => item.NotificationId == deliveredId(firstFeed)),
        "Account B could read account A notifications.");
    Assert(await store.MarkReadAsync(
            accountB,
            frozen.Notification.NotificationId,
            cancellationToken) is null,
        "Cross-account mark-read did not fail closed.");

    var read = await store.MarkReadAsync(
        accountA,
        frozen.Notification.NotificationId,
        cancellationToken);
    var readReplay = await store.MarkReadAsync(
        accountA,
        frozen.Notification.NotificationId,
        cancellationToken);
    Assert(read is not null && readReplay?.ReadAt == read.ReadAt,
        "Mark-read was not idempotent.");
    var allRead = await store.MarkAllReadAsync(accountA, cancellationToken);
    Assert(allRead.UnreadCount == 0 &&
        (await store.ListForAccountAsync(accountA, 100, cancellationToken)).UnreadCount == 0,
        "Mark-all-read left unread notifications.");

    var serialized = JsonSerializer.Serialize(
        await store.ListForAccountAsync(accountA, 100, cancellationToken));
    Assert(!serialized.Contains("playerUid", StringComparison.OrdinalIgnoreCase) &&
        !serialized.Contains("steam_A", StringComparison.Ordinal),
        "The player feed leaked a game target identifier.");

    await VerifySourceTerminalSemanticsAsync(accountA, season, now);
    VerifyIdentityOverridesFailClosed();
    await VerifyGameDeliverySafeDefaultAsync(
        directory,
        clock,
        accountA,
        season,
        now,
        cancellationToken);
    await VerifyCrashWindowAsync(directory, clock, accountA, season, now, cancellationToken);
    VerifySchemaMigration(directory);
}

static void VerifyIdentityOverridesFailClosed()
{
    foreach (var name in new[]
             {
                 "accountId", "account_id", "userId", "user_id",
                 "steamId", "steam_id", "playerUid", "player_uid"
             })
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?{name}=attacker");
        try
        {
            PlayerNotificationEndpoints.RejectIdentityOverride(context);
        }
        catch (PlayerPortalException exception) when (
            exception.Code == "PLAYER_IDENTITY_OVERRIDE_FORBIDDEN" &&
            exception.StatusCode == StatusCodes.Status400BadRequest)
        {
            continue;
        }
        throw new InvalidOperationException($"Identity override '{name}' did not fail closed.");
    }
}

static async Task VerifySourceTerminalSemanticsAsync(
    Guid accountId,
    Guid seasonId,
    DateTimeOffset now)
{
    var pendingOrder = Order(accountId, seasonId, ShopOrderState.PendingDelivery, now);
    Assert(PlayerNotificationSourceProjector.FromOrder(pendingOrder) is null,
        "A non-terminal order emitted a delivered notification.");
    var deliveredOrder = pendingOrder with { State = ShopOrderState.Delivered };
    var deliveredSource = PlayerNotificationSourceProjector.FromOrder(deliveredOrder);
    Assert(deliveredSource?.SourceState == "delivered",
        "Delivered order did not emit success.");
    var uncertainOrder = pendingOrder with { State = ShopOrderState.DeliveryUncertain };
    var partialSource = PlayerNotificationSourceProjector.FromOrder(
        uncertainOrder,
        ExtractionDeliveryReceiptOutcome.Partial);
    Assert(partialSource?.SourceState == "partial",
        "Partial delivery was not distinct from uncertain delivery.");
    Assert(partialSource?.SourceEventKey != deliveredSource?.SourceEventKey,
        "A reconciled delivery would collide with its historical anomaly notification.");

    var creditedRun = Run(accountId, seasonId, ExtractionSettlementState.Credited, now);
    Assert(PlayerNotificationSourceProjector.FromRun(creditedRun) is null,
        "Credited-but-not-settled run emitted success.");
    var settledSource = PlayerNotificationSourceProjector.FromRun(
        creditedRun with { State = ExtractionSettlementState.Settled });
    Assert(settledSource?.SourceState == "settled",
        "Settled run did not emit success.");
    var uncertainSource = PlayerNotificationSourceProjector.FromRun(
        creditedRun with { State = ExtractionSettlementState.Uncertain });
    Assert(uncertainSource?.SourceType == "reconciliation",
        "Uncertain run did not emit reconciliation guidance.");
    Assert(uncertainSource?.SourceEventKey != settledSource?.SourceEventKey,
        "A reconciled run would collide with its historical anomaly notification.");
    await Task.CompletedTask;
}

static async Task VerifyCrashWindowAsync(
    string directory,
    MutableTimeProvider clock,
    Guid accountId,
    Guid seasonId,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    var dispatcher = new CrashOnceDispatcher();
    var source = Source(
        accountId,
        seasonId,
        "order-delivery",
        "delivered",
        "success",
        "crash-order",
        "v1",
        now.AddMinutes(10),
        requestGame: true);
    var firstStore = new PlayerNotificationStore(directory, clock);
    var enabledOptions = Options.Create(new PlayerNotificationOptions
    {
        GameDeliveryEnabled = true
    });
    var firstProjection = new PlayerNotificationProjectionService(
        firstStore,
        dispatcher,
        enabledOptions);
    await AssertThrowsAsync<IOException>(
        () => firstProjection.ProjectAsync(source, cancellationToken),
        "Injected crash after external acceptance did not surface.");
    var pending = (await firstStore.ListForAccountAsync(accountId, 100, cancellationToken))
        .Items.Single(item => item.Title == source.Title);
    Assert(pending.GameState == "pending", "Crash window did not leave durable pending work.");

    var restartedStore = new PlayerNotificationStore(directory, clock);
    var restartedProjection = new PlayerNotificationProjectionService(
        restartedStore,
        dispatcher,
        enabledOptions);
    var recovered = await restartedProjection.ProjectAsync(source, cancellationToken);
    Assert(recovered.GameState == "queued" && dispatcher.UniqueDispatches == 1,
        "Restart replay duplicated or lost the external game notification command.");
    for (var replay = 0; replay < 20; replay++)
    {
        await restartedProjection.ProjectAsync(source, cancellationToken);
    }
    Assert(dispatcher.UniqueDispatches == 1,
        "20x worker replay duplicated the game notification command.");
}

static async Task VerifyGameDeliverySafeDefaultAsync(
    string directory,
    MutableTimeProvider clock,
    Guid accountId,
    Guid seasonId,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    Assert(!new PlayerNotificationOptions().GameDeliveryEnabled,
        "Game delivery was not disabled by default.");
    var dispatcher = new RecordingDispatcher();
    var store = new PlayerNotificationStore(directory, clock);
    var disabledProjection = new PlayerNotificationProjectionService(
        store,
        dispatcher,
        Options.Create(new PlayerNotificationOptions()));
    var historical = Source(
        accountId,
        seasonId,
        "order-delivery",
        "delivered",
        "success",
        "safe-default-order",
        "v1",
        now.AddMinutes(20),
        requestGame: true);

    var created = await disabledProjection.ProjectAsync(historical, cancellationToken);
    Assert(created.GameState == "not-requested" && dispatcher.Calls == 0,
        "Default-disabled projection dispatched a newly discovered historical source.");
    var updatedWhileDisabled = historical with
    {
        SourceVersion = "v2",
        Message = "权威来源产生了第二个版本。",
        OccurredAt = now.AddMinutes(21)
    };
    var updated = await disabledProjection.ProjectAsync(
        updatedWhileDisabled,
        cancellationToken);
    Assert(updated.GameState == "not-requested" && dispatcher.Calls == 0,
        "Default-disabled projection dispatched a source update.");

    var enabledProjection = new PlayerNotificationProjectionService(
        store,
        dispatcher,
        Options.Create(new PlayerNotificationOptions
        {
            GameDeliveryEnabled = true
        }));
    var sameVersion = await enabledProjection.ProjectAsync(
        updatedWhileDisabled,
        cancellationToken);
    Assert(sameVersion.GameState == "not-requested" && dispatcher.Calls == 0,
        "Enabling delivery replayed an old not-requested source version.");

    var newVersion = updatedWhileDisabled with
    {
        SourceVersion = "v3",
        Message = "启用后权威来源产生了新版本。",
        OccurredAt = now.AddMinutes(22)
    };
    var dispatched = await enabledProjection.ProjectAsync(newVersion, cancellationToken);
    Assert(dispatched.GameState == "queued" && dispatcher.Calls == 1,
        "An explicitly enabled new source version was not dispatched exactly once.");
}

static void VerifySchemaMigration(string directory)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "extraction-commerce.db"),
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT COUNT(*)
        FROM economy_schema_migrations
        WHERE component = 'player-notifications' AND version = 1;
        """;
    Assert((long)(command.ExecuteScalar() ?? 0L) == 1,
        "Player notification schema migration was not recorded.");
}

static PlayerNotificationSource Source(
    Guid accountId,
    Guid seasonId,
    string sourceType,
    string state,
    string severity,
    string sourceId,
    string version,
    DateTimeOffset at,
    bool requestGame) => new(
        accountId,
        seasonId,
        "steam_A",
        sourceType,
        sourceId,
        $"test:{accountId:N}:{sourceType}:{sourceId}",
        version,
        state,
        severity,
        $"test-{sourceId}",
        state is "uncertain" or "partial"
            ? "请勿重复购买或重复结算，等待管理员核对。"
            : "状态已经由权威记录确认。",
        at,
        requestGame);

static ShopOrder Order(
    Guid accountId,
    Guid seasonId,
    ShopOrderState state,
    DateTimeOffset now) => new(
        Guid.NewGuid(),
        accountId,
        seasonId,
        "local",
        "steam_A",
        [],
        [],
        state,
        Guid.NewGuid(),
        1,
        "purchase-key",
        "harness",
        "notification terminal test",
        now,
        now);

static ExtractionSettlementRun Run(
    Guid accountId,
    Guid seasonId,
    ExtractionSettlementState state,
    DateTimeOffset now) => new(
        Guid.NewGuid(),
        accountId,
        seasonId,
        "steam_A",
        "zone-a",
        "Zone A",
        state,
        [],
        1,
        100,
        new string('a', 64),
        null,
        "settlement-key",
        null,
        null,
        now,
        now.AddMinutes(1),
        now,
        state == ExtractionSettlementState.Settled ? now : null)
    {
        Revision = 2,
        StateChangedAt = now
    };

static Guid deliveredId(PlayerNotificationFeed feed) => feed.Items.Single().NotificationId;

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now = _now.Add(duration);
}

sealed class CrashOnceDispatcher : IPlayerGameNotificationDispatcher
{
    private readonly Dictionary<string, (Guid NotificationId, Guid CommandId)> _accepted =
        new(StringComparer.Ordinal);
    private bool _crash = true;

    public int UniqueDispatches => _accepted.Count;

    public Task<PlayerGameNotificationDispatchResult> DispatchOrReconcileAsync(
        PlayerGameNotificationDispatch request,
        Guid? existingNotificationId,
        Guid? existingCommandId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = $"{request.SourceEventKey}\n{request.SourceVersion}";
        if (!_accepted.TryGetValue(key, out var accepted))
        {
            accepted = (Guid.NewGuid(), Guid.NewGuid());
            _accepted.Add(key, accepted);
        }
        if (_crash)
        {
            _crash = false;
            throw new IOException("Injected crash after game command acceptance.");
        }
        return Task.FromResult(new PlayerGameNotificationDispatchResult(
            "queued",
            accepted.NotificationId,
            accepted.CommandId,
            null));
    }
}

sealed class RecordingDispatcher : IPlayerGameNotificationDispatcher
{
    public int Calls { get; private set; }

    public Task<PlayerGameNotificationDispatchResult> DispatchOrReconcileAsync(
        PlayerGameNotificationDispatch request,
        Guid? existingNotificationId,
        Guid? existingCommandId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(new PlayerGameNotificationDispatchResult(
            "queued",
            existingNotificationId ?? Guid.NewGuid(),
            existingCommandId ?? Guid.NewGuid(),
            null));
    }
}

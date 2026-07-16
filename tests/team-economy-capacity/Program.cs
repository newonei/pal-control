using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

const int HistoricalWeekCount = 52;
const int AccountCount = 10_000;
const int ConcurrentSqliteWriters = 8;
const int RewardsPerWriter = 8;
const int ConcurrentTeamWriters = 16;
var root = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-team-economy-capacity-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var databasePath = Path.Combine(root, "extraction-commerce.db");
var serverId = "team-capacity";
var clock = new FixedTimeProvider(
    new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
var options = new TeamEconomyOptions
{
    Enabled = true,
    InvitePepper = "capacity-only-team-invitation-pepper-64-characters-000000000000",
    ProjectionIntervalSeconds = 5,
    InviteLifetimeMinutes = 60,
    InviteMaximumUses = 20,
    MinimumLeaderboardMembers = 1,
    MinimumResourceValue = 1,
    MinimumTaskPoints = 1,
    MinimumDeliveredOrders = 1,
    ResourceItemsGoal = 10,
    ResourceValueGoal = 100,
    ReliableTaskPointsGoal = 10,
    DeliveredOrdersGoal = 1,
    GoalTemplateVersion = "capacity-goals-v1"
};

try
{
    // Let the production repository create its complete SQLite schema first;
    // the bulk fixture then uses one transaction so the benchmark measures
    // projection/locking rather than 10,000 setup fsyncs.
    using (var bootstrap = new SqliteExtractionRepository(root, clock))
    {
    }
    InitializeProjectionSources(databasePath);

    var accounts = Enumerable.Range(0, AccountCount)
        .Select(_ => Guid.NewGuid())
        .ToArray();
    var historicalSeasons = Enumerable.Range(0, HistoricalWeekCount)
        .Select(_ => Guid.NewGuid())
        .ToArray();
    var activeSeason = Guid.NewGuid();
    SeedAuthoritativeHistory(
        databasePath,
        serverId,
        historicalSeasons,
        activeSeason,
        accounts,
        clock.GetUtcNow());
    _ = new PlayerIdentitySecurityStore(root);

    using var repository = new SqliteExtractionRepository(root, clock);
    var commerce = new ExtractionCommerceService(repository);
    using var store = new TeamEconomyStore(root, options, clock);

    // One historical scope is fully valid so the explicit administrative
    // rebuild path remains executable. The other 51 use integrity sentinels:
    // the legacy all-scope worker would touch them and fail immediately.
    await store.CreateTeamAsync(
        serverId,
        historicalSeasons[0],
        accounts[0],
        "Historical valid team",
        "capacity-history-valid-0001",
        default);
    await store.CreateTeamAsync(
        serverId,
        activeSeason,
        accounts[1],
        "Active capacity team",
        "capacity-active-team-0001",
        default);
    InsertHistoricalIntegritySentinels(
        databasePath,
        serverId,
        historicalSeasons.Skip(1).ToArray(),
        accounts.Skip(2).Take(HistoricalWeekCount - 1).ToArray(),
        clock.GetUtcNow());

    var scopes = await store.ListScopesAsync(default);
    Assert(
        scopes.Count == HistoricalWeekCount + 1,
        $"Expected {HistoricalWeekCount + 1} team scopes, found {scopes.Count}.");

    var manualStopwatch = Stopwatch.StartNew();
    await store.ProjectSeasonAsync(
        serverId,
        historicalSeasons[0],
        clock.GetUtcNow(),
        default);
    manualStopwatch.Stop();
    Assert(
        ProjectionExists(databasePath, serverId, historicalSeasons[0]),
        "The explicit historical ProjectSeasonAsync path did not persist its projection.");
    await AssertCodeAsync(
        () => store.ProjectActiveSeasonAsync(
            serverId,
            historicalSeasons[0],
            clock.GetUtcNow(),
            default),
        "TEAM_ACTIVE_SEASON_CHANGED",
        "a historical season passed the worker-only active-season fence");

    var extractionOptions = new ExtractionModeOptions
    {
        Enabled = true,
        ServerId = serverId
    };
    var worker = new TeamEconomyProjectionWorker(
        store,
        commerce,
        Options.Create(options),
        Options.Create(extractionOptions),
        clock,
        NullLogger<TeamEconomyProjectionWorker>.Instance);

    VerifyFailClosedSelection(serverId, activeSeason, clock.GetUtcNow());

    var activeStopwatch = Stopwatch.StartNew();
    await worker.ProjectOnceAsync(default);
    activeStopwatch.Stop();
    Assert(
        ProjectionExists(databasePath, serverId, activeSeason),
        "The worker did not project the unique bound active season.");
    Assert(
        CountProjectionStates(databasePath) == 2,
        "The worker projected a historical integrity-sentinel scope.");
    Assert(
        CountUnprojectedScopes(databasePath, serverId) == HistoricalWeekCount - 1,
        "A historical integrity-sentinel scope received worker state.");

    // Exercise real WAL writer competition. Each writer holds an independent
    // immediate transaction while the worker reads and commits its active-only
    // snapshot. A SQLITE_BUSY/LOCKED error fails the harness.
    var sqliteStart = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var concurrentProjection = Task.Run(async () =>
    {
        await sqliteStart.Task;
        var stopwatch = Stopwatch.StartNew();
        await worker.ProjectOnceAsync(default);
        return stopwatch.Elapsed;
    });
    var sqliteWriters = Enumerable.Range(0, ConcurrentSqliteWriters)
        .Select(writer => Task.Run(async () =>
        {
            await sqliteStart.Task;
            return InsertTaskRewardBatch(
                databasePath,
                activeSeason,
                accounts[1],
                writer,
                RewardsPerWriter,
                clock.GetUtcNow());
        }))
        .ToArray();
    sqliteStart.SetResult();
    var concurrentProjectionDuration = await concurrentProjection;
    var sqliteWriterDurations = await Task.WhenAll(sqliteWriters);

    // One more pass establishes a cutoff after every concurrent commit.
    await worker.ProjectOnceAsync(default);
    Assert(
        CountProjectedEvents(databasePath, serverId, activeSeason) ==
        ConcurrentSqliteWriters * RewardsPerWriter,
        "The post-contention active projection did not include every durable task fact exactly once.");

    // Exercise the store's in-process gate at 10,000-account scale. These
    // mutations must wait safely for an active projection rather than race it
    // through SQLite or expand work to 52 historical scopes.
    var teamStart = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var gatedProjection = Task.Run(async () =>
    {
        await teamStart.Task;
        var stopwatch = Stopwatch.StartNew();
        await worker.ProjectOnceAsync(default);
        return stopwatch.Elapsed;
    });
    var teamWriters = Enumerable.Range(0, ConcurrentTeamWriters)
        .Select(index => Task.Run(async () =>
        {
            await teamStart.Task;
            var stopwatch = Stopwatch.StartNew();
            await store.CreateTeamAsync(
                serverId,
                activeSeason,
                accounts[100 + index],
                $"Capacity team {index + 1:00}",
                $"capacity-concurrent-team-{index + 1:0000}",
                default);
            return stopwatch.Elapsed;
        }))
        .ToArray();
    teamStart.SetResult();
    var gatedProjectionDuration = await gatedProjection;
    var teamWriterDurations = await Task.WhenAll(teamWriters);

    await worker.ProjectOnceAsync(default);
    Assert(
        CountProjectionStates(databasePath) == 2 &&
        CountUnprojectedScopes(databasePath, serverId) == HistoricalWeekCount - 1,
        "Concurrent writes caused the worker to fall back to historical scopes.");

    var projectionLimit = TimeSpan.FromSeconds(30);
    var lockLimit = TimeSpan.FromSeconds(30);
    var measuredProjections = new[]
    {
        manualStopwatch.Elapsed,
        activeStopwatch.Elapsed,
        concurrentProjectionDuration,
        gatedProjectionDuration
    };
    Assert(
        measuredProjections.All(duration => duration <= projectionLimit),
        $"A measured projection exceeded the {projectionLimit.TotalSeconds:0}-second capacity ceiling.");
    Assert(
        sqliteWriterDurations.Max() <= lockLimit,
        $"A concurrent SQLite writer exceeded the {lockLimit.TotalSeconds:0}-second lock ceiling.");
    Assert(
        teamWriterDurations.Max() <= lockLimit,
        $"A gated team mutation exceeded the {lockLimit.TotalSeconds:0}-second lock ceiling.");

    Console.WriteLine(
        "BENCHMARK: " +
        $"historicalWeeks={HistoricalWeekCount}, accounts={AccountCount}, " +
        $"manualHistoryProjectionMs={manualStopwatch.Elapsed.TotalMilliseconds:F1}, " +
        $"activeProjectionMs={activeStopwatch.Elapsed.TotalMilliseconds:F1}, " +
        $"concurrentProjectionMs={concurrentProjectionDuration.TotalMilliseconds:F1}, " +
        $"sqliteWriterMaxMs={sqliteWriterDurations.Max().TotalMilliseconds:F1}, " +
        $"gatedProjectionMs={gatedProjectionDuration.TotalMilliseconds:F1}, " +
        $"teamWriterMaxMs={teamWriterDurations.Max().TotalMilliseconds:F1}, " +
        $"projectedEvents={CountProjectedEvents(databasePath, serverId, activeSeason)}.");
    Console.WriteLine(
        "PASS: the worker selected only the unique active weekly world, 51 history sentinels remained untouched, " +
        "manual history rebuild stayed available, and 10,000-account WAL/gate contention stayed within bounds.");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static void InitializeProjectionSources(string path)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS reliable_task_ranking_rewards(
            entry_id TEXT PRIMARY KEY,
            instance_id TEXT NOT NULL UNIQUE,
            account_id TEXT NOT NULL,
            season_id TEXT NOT NULL,
            points INTEGER NOT NULL,
            balance_after INTEGER NOT NULL,
            created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS season_leaderboard_exclusions(
            season_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            active INTEGER NOT NULL,
            PRIMARY KEY(season_id, account_id));
        """;
    command.ExecuteNonQuery();
}

static void SeedAuthoritativeHistory(
    string path,
    string serverId,
    IReadOnlyList<Guid> historicalSeasons,
    Guid activeSeason,
    IReadOnlyList<Guid> accounts,
    DateTimeOffset now)
{
    using var connection = Open(path);
    using var transaction = connection.BeginTransaction(deferred: false);
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        INSERT INTO extraction_events(event_id, event_type, occurred_at, payload)
        VALUES($eventId, $eventType, $occurredAt, $payload);
        """;
    command.Parameters.Add("$eventId", SqliteType.Text);
    command.Parameters.Add("$eventType", SqliteType.Text);
    command.Parameters.Add("$occurredAt", SqliteType.Text);
    command.Parameters.Add("$payload", SqliteType.Text);

    for (var index = 0; index < historicalSeasons.Count; index++)
    {
        var endsAt = now.AddDays(-7 * (historicalSeasons.Count - index));
        var startsAt = endsAt.AddDays(-7);
        var season = new ExtractionSeason(
            historicalSeasons[index],
            serverId,
            $"capacity-history-{index + 1:00}",
            $"Capacity historical week {index + 1:00}",
            (index + 1).ToString("x32"),
            startsAt,
            endsAt,
            ExtractionSeasonState.Closed,
            1,
            startsAt,
            endsAt);
        InsertEnvelope(command, "season.upserted", endsAt, season, null);
    }

    var activeStartsAt = now.AddDays(-2);
    var active = new ExtractionSeason(
        activeSeason,
        serverId,
        "capacity-active",
        "Capacity active week",
        new string('a', 32),
        activeStartsAt,
        now.AddDays(5),
        ExtractionSeasonState.Active,
        1,
        activeStartsAt,
        activeStartsAt);
    InsertEnvelope(command, "season.upserted", activeStartsAt, active, null);

    var accountAt = now.AddHours(-1);
    foreach (var accountId in accounts)
    {
        var account = new ExtractionAccount(
            accountId,
            "steam",
            $"steam_capacity-{accountId:N}",
            "Capacity account",
            1,
            accountAt,
            accountAt);
        InsertEnvelope(command, "account.created", accountAt, null, account);
    }
    transaction.Commit();
}

static void InsertEnvelope(
    SqliteCommand command,
    string eventType,
    DateTimeOffset at,
    ExtractionSeason? season,
    ExtractionAccount? account)
{
    var eventId = Guid.NewGuid();
    var payload = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        eventId,
        eventType,
        at,
        season,
        account
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    command.Parameters["$eventId"].Value = eventId.ToString("D");
    command.Parameters["$eventType"].Value = eventType;
    command.Parameters["$occurredAt"].Value = at.ToString("O");
    command.Parameters["$payload"].Value = payload;
    command.ExecuteNonQuery();
}

static void InsertHistoricalIntegritySentinels(
    string path,
    string serverId,
    IReadOnlyList<Guid> seasons,
    IReadOnlyList<Guid> owners,
    DateTimeOffset now)
{
    Assert(seasons.Count == owners.Count, "Historical sentinel fixture counts differ.");
    using var connection = Open(path);
    using var transaction = connection.BeginTransaction(deferred: false);
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        INSERT INTO team_economy_teams(
            team_id, server_id, season_id, display_name, normalized_name,
            owner_account_id, status, created_at, updated_at, dissolved_at, row_hash)
        VALUES(
            $teamId, $serverId, $seasonId, $displayName, $normalizedName,
            $ownerId, 'Active', $createdAt, $updatedAt, NULL, $rowHash);
        """;
    for (var index = 0; index < seasons.Count; index++)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$teamId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasons[index].ToString("D"));
        command.Parameters.AddWithValue("$displayName", $"History sentinel {index + 2:00}");
        command.Parameters.AddWithValue("$normalizedName", $"history sentinel {index + 2:00}");
        command.Parameters.AddWithValue("$ownerId", owners[index].ToString("D"));
        command.Parameters.AddWithValue("$createdAt", now.AddDays(-14).ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.AddDays(-14).ToString("O"));
        command.Parameters.AddWithValue("$rowHash", new string('0', 64));
        command.ExecuteNonQuery();
    }
    transaction.Commit();
}

static TimeSpan InsertTaskRewardBatch(
    string path,
    Guid seasonId,
    Guid accountId,
    int writer,
    int count,
    DateTimeOffset at)
{
    var stopwatch = Stopwatch.StartNew();
    using var connection = Open(path, busyTimeoutMilliseconds: 15_000);
    using var transaction = connection.BeginTransaction(deferred: false);
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        INSERT INTO reliable_task_ranking_rewards(
            entry_id, instance_id, account_id, season_id,
            points, balance_after, created_at)
        VALUES($entryId, $instanceId, $accountId, $seasonId, 1, $balanceAfter, $createdAt);
        """;
    for (var index = 0; index < count; index++)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$entryId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$instanceId", $"capacity-{writer:00}-{index:00}-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue("$balanceAfter", writer * count + index + 1);
        command.Parameters.AddWithValue("$createdAt", at.ToString("O"));
        command.ExecuteNonQuery();
    }
    transaction.Commit();
    stopwatch.Stop();
    return stopwatch.Elapsed;
}

static void VerifyFailClosedSelection(
    string serverId,
    Guid activeSeasonId,
    DateTimeOffset now)
{
    var closed = CapacitySeason(
        Guid.NewGuid(),
        serverId,
        ExtractionSeasonState.Closed,
        now,
        new string('b', 32));
    Assert(
        TeamEconomyProjectionWorker.SelectActiveScope(serverId, [closed], now) is null,
        "A closed-only source did not select the safe no-op state.");

    var first = CapacitySeason(
        activeSeasonId,
        serverId,
        ExtractionSeasonState.Active,
        now,
        new string('c', 32));
    var second = CapacitySeason(
        Guid.NewGuid(),
        serverId,
        ExtractionSeasonState.Active,
        now,
        new string('d', 32));
    AssertCode(
        () => TeamEconomyProjectionWorker.SelectActiveScope(serverId, [first, second], now),
        "TEAM_ACTIVE_SEASON_AMBIGUOUS",
        "two active seasons were accepted");

    var unbound = CapacitySeason(
        Guid.NewGuid(),
        serverId,
        ExtractionSeasonState.Active,
        now,
        null);
    AssertCode(
        () => TeamEconomyProjectionWorker.SelectActiveScope(serverId, [unbound], now),
        "TEAM_ACTIVE_SEASON_INVALID",
        "an unbound active season was accepted");
}

static ExtractionSeason CapacitySeason(
    Guid seasonId,
    string serverId,
    ExtractionSeasonState state,
    DateTimeOffset now,
    string? worldId) => new(
        seasonId,
        serverId,
        $"capacity-{seasonId:N}",
        "Capacity selector fixture",
        worldId,
        now.AddDays(-1),
        now.AddDays(1),
        state,
        1,
        now.AddDays(-1),
        now.AddDays(-1));

static bool ProjectionExists(string path, string serverId, Guid seasonId)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT EXISTS(
            SELECT 1 FROM team_economy_projection_state
            WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId);
        """;
    command.Parameters.AddWithValue("$serverId", serverId);
    command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
    return Convert.ToInt64(command.ExecuteScalar()) == 1;
}

static int CountProjectionStates(string path)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM team_economy_projection_state;";
    return Convert.ToInt32(command.ExecuteScalar());
}

static int CountUnprojectedScopes(string path, string serverId)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT COUNT(DISTINCT teams.season_id)
        FROM team_economy_teams AS teams
        LEFT JOIN team_economy_projection_state AS state
          ON state.server_id = teams.server_id COLLATE NOCASE
         AND state.season_id = teams.season_id
        WHERE teams.server_id = $serverId COLLATE NOCASE
          AND state.season_id IS NULL;
        """;
    command.Parameters.AddWithValue("$serverId", serverId);
    return Convert.ToInt32(command.ExecuteScalar());
}

static int CountProjectedEvents(string path, string serverId, Guid seasonId)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT COUNT(*) FROM team_economy_projection_events
        WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId;
        """;
    command.Parameters.AddWithValue("$serverId", serverId);
    command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
    return Convert.ToInt32(command.ExecuteScalar());
}

static SqliteConnection Open(string path, int busyTimeoutMilliseconds = 5_000)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false
    }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA busy_timeout={busyTimeoutMilliseconds}; PRAGMA foreign_keys=ON;";
    command.ExecuteNonQuery();
    return connection;
}

static async Task AssertCodeAsync(
    Func<Task> action,
    string expectedCode,
    string scenario)
{
    try
    {
        await action();
        throw new InvalidOperationException($"Expected {expectedCode}: {scenario}.");
    }
    catch (TeamEconomyException exception) when (exception.Code == expectedCode)
    {
    }
}

static void AssertCode(
    Action action,
    string expectedCode,
    string scenario)
{
    try
    {
        action();
        throw new InvalidOperationException($"Expected {expectedCode}: {scenario}.");
    }
    catch (TeamEconomyException exception) when (exception.Code == expectedCode)
    {
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

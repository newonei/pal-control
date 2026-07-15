using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Content;

public sealed record ReliableTaskSetDefinition(
    Guid AccountId,
    Guid SeasonId,
    string ServerId,
    ContentTaskCadence Cadence,
    string PeriodKey,
    Guid ContentVersionId,
    string ContentHash,
    string RulesVersion,
    string RotationSeed,
    IReadOnlyList<ContentTaskDefinition> Tasks);

public sealed record ReliableTaskStoreEventResult(
    bool Applied,
    bool Replayed,
    IReadOnlyList<ReliableTaskInstance> Tasks);

/// <summary>
/// Persistent task progress, event inbox and reward outbox. The tables share
/// extraction-commerce.db with wallet/order/settlement state so one SQLite
/// online backup is a consistent economy image.
/// </summary>
public sealed class SqliteReliableTaskStore : IDisposable, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public SqliteReliableTaskStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath), timeProvider)
    {
    }

    public SqliteReliableTaskStore(string dataDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            // Keep task inbox/outbox and progress in the authoritative economy
            // database so the existing SQLite online backup captures one
            // transactionally consistent wallet/order/settlement/task image.
            DataSource = Path.Combine(directory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        Initialize();
    }

    public async Task<ReliableTaskSet> EnsureTaskSetAsync(
        ReliableTaskSetDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateSetDefinition(definition);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var existing = await LoadTaskSetAsync(
                connection,
                (SqliteTransaction)transaction,
                definition.AccountId,
                definition.SeasonId,
                definition.Cadence,
                definition.PeriodKey,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            var now = _timeProvider.GetUtcNow();
            var taskSet = new ReliableTaskSet(
                DeterministicGuid($"set|{definition.AccountId:N}|{definition.SeasonId:N}|{definition.Cadence}|{definition.PeriodKey}"),
                definition.AccountId,
                definition.SeasonId,
                definition.ServerId.Trim(),
                definition.Cadence,
                definition.PeriodKey.Trim(),
                definition.ContentVersionId,
                definition.ContentHash.Trim().ToLowerInvariant(),
                definition.RulesVersion.Trim(),
                definition.RotationSeed.Trim().ToLowerInvariant(),
                definition.Tasks.Select(task => task.TaskKey).ToArray(),
                now);
            await InsertTaskSetAsync(connection, (SqliteTransaction)transaction, taskSet, cancellationToken);
            foreach (var task in definition.Tasks)
            {
                await InsertTaskInstanceAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    taskSet,
                    task,
                    now,
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return taskSet;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ReliableTaskStoreEventResult> ApplyEventAsync(
        ReliableEconomyEvent economyEvent,
        IReadOnlySet<Guid> eligibleTaskSetIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(economyEvent);
        ArgumentNullException.ThrowIfNull(eligibleTaskSetIds);
        ValidateEvent(economyEvent);
        var eventPayload = CanonicalEventPayload(economyEvent);
        var eventHash = Hash(eventPayload);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var replayHash = await GetEventHashAsync(
                connection,
                (SqliteTransaction)transaction,
                economyEvent.EventId,
                cancellationToken);
            if (replayHash is not null)
            {
                if (!string.Equals(replayHash, eventHash, StringComparison.Ordinal))
                {
                    throw new ReliableTaskException(
                        "TASK_EVENT_IDEMPOTENCY_CONFLICT",
                        "The reliable economy event id was reused for a different authoritative fact.");
                }
                var replayTasks = await ListTasksAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    economyEvent.AccountId,
                    economyEvent.SeasonId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ReliableTaskStoreEventResult(false, true, replayTasks);
            }

            await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                economyEvent,
                eventPayload,
                eventHash,
                cancellationToken);
            var tasks = await ListTasksAsync(
                connection,
                (SqliteTransaction)transaction,
                economyEvent.AccountId,
                economyEvent.SeasonId,
                cancellationToken);
            var now = _timeProvider.GetUtcNow();
            foreach (var task in tasks.Where(task =>
                         eligibleTaskSetIds.Contains(task.TaskSetId) && !task.Completed))
            {
                var increment = Contribution(task.Definition, economyEvent);
                if (increment <= 0)
                {
                    continue;
                }
                var nextProgress = Math.Min(
                    task.Definition.TargetAmount,
                    checked(task.Progress + increment));
                var completedAt = nextProgress >= task.Definition.TargetAmount
                    ? economyEvent.OccurredAt
                    : (DateTimeOffset?)null;
                await UpdateProgressAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    task,
                    nextProgress,
                    completedAt,
                    now,
                    cancellationToken);
                if (completedAt is not null && task.Definition.Reward.RankingPoints > 0)
                {
                    await GrantRankingRewardAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        task,
                        now,
                        cancellationToken);
                }
            }
            var updated = await ListTasksAsync(
                connection,
                (SqliteTransaction)transaction,
                economyEvent.AccountId,
                economyEvent.SeasonId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReliableTaskStoreEventResult(true, false, updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ReliableTaskInstance>> ListTasksAsync(
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            return await ListTasksAsync(connection, null, accountId, seasonId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasEventAsync(
        string eventId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM reliable_task_events WHERE event_id = $eventId);";
            command.Parameters.AddWithValue("$eventId", eventId.Trim());
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ReliableTaskRewardScope>>
        ListPendingCurrencyRewardScopesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT account_id, season_id, definition_json
                FROM reliable_task_instances
                WHERE completed_at IS NOT NULL
                  AND currency_reward_ledger_entry_id IS NULL;
                """;
            var scopes = new HashSet<ReliableTaskRewardScope>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var definition = JsonSerializer.Deserialize<ContentTaskDefinition>(
                    reader.GetString(2),
                    EconomyContentJson.Options)
                    ?? throw new InvalidDataException("Stored task definition is null.");
                if (definition.Reward.Amount > 0)
                {
                    scopes.Add(new ReliableTaskRewardScope(
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1))));
                }
            }
            return scopes.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetRankingPointsAsync(
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(points), 0)
                FROM reliable_task_ranking_rewards
                WHERE account_id = $accountId AND season_id = $seasonId;
                """;
            command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
            command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCurrencyRewardGrantedAsync(
        Guid instanceId,
        Guid walletLedgerEntryId,
        CancellationToken cancellationToken)
    {
        if (instanceId == Guid.Empty || walletLedgerEntryId == Guid.Empty)
        {
            throw new ArgumentException("Task and ledger ids must be non-empty.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var read = connection.CreateCommand();
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = """
                SELECT currency_reward_ledger_entry_id, completed_at
                FROM reliable_task_instances WHERE instance_id = $instanceId;
                """;
            read.Parameters.AddWithValue("$instanceId", instanceId.ToString("D"));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException($"Reliable task instance '{instanceId}' does not exist.");
            }
            Guid? existing = reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0));
            var completed = !reader.IsDBNull(1);
            await reader.DisposeAsync();
            if (!completed)
            {
                throw new ReliableTaskException(
                    "TASK_NOT_COMPLETED",
                    "An incomplete reliable task cannot receive a reward.");
            }
            if (existing is Guid existingId && existingId != walletLedgerEntryId)
            {
                throw new ReliableTaskException(
                    "TASK_REWARD_LEDGER_CONFLICT",
                    "The task reward is already linked to another wallet ledger entry.");
            }
            if (existing is null)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE reliable_task_instances
                    SET currency_reward_ledger_entry_id = $ledgerId, updated_at = $updatedAt
                    WHERE instance_id = $instanceId
                      AND currency_reward_ledger_entry_id IS NULL;
                    """;
                update.Parameters.AddWithValue("$ledgerId", walletLedgerEntryId.ToString("D"));
                update.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToString("O"));
                update.Parameters.AddWithValue("$instanceId", instanceId.ToString("D"));
                _ = await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS reliable_task_schema (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS reliable_task_sets (
                task_set_id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                season_id TEXT NOT NULL,
                server_id TEXT NOT NULL,
                cadence TEXT NOT NULL,
                period_key TEXT NOT NULL,
                content_version_id TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                rules_version TEXT NOT NULL,
                rotation_seed TEXT NOT NULL,
                selected_task_keys_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(account_id, season_id, cadence, period_key)
            );
            CREATE TABLE IF NOT EXISTS reliable_task_instances (
                instance_id TEXT PRIMARY KEY,
                task_set_id TEXT NOT NULL REFERENCES reliable_task_sets(task_set_id),
                account_id TEXT NOT NULL,
                season_id TEXT NOT NULL,
                server_id TEXT NOT NULL,
                cadence TEXT NOT NULL,
                period_key TEXT NOT NULL,
                content_version_id TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                rules_version TEXT NOT NULL,
                rotation_seed TEXT NOT NULL,
                task_key TEXT NOT NULL,
                definition_json TEXT NOT NULL,
                progress INTEGER NOT NULL,
                completed_at TEXT NULL,
                currency_reward_ledger_entry_id TEXT NULL,
                ranking_reward_entry_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(task_set_id, task_key)
            );
            CREATE TABLE IF NOT EXISTS reliable_task_events (
                event_id TEXT PRIMARY KEY,
                event_hash TEXT NOT NULL,
                account_id TEXT NOT NULL,
                season_id TEXT NOT NULL,
                server_id TEXT NOT NULL,
                source TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                received_at TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS reliable_task_ranking_rewards (
                entry_id TEXT PRIMARY KEY,
                instance_id TEXT NOT NULL UNIQUE REFERENCES reliable_task_instances(instance_id),
                account_id TEXT NOT NULL,
                season_id TEXT NOT NULL,
                points INTEGER NOT NULL,
                balance_after INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_reliable_tasks_account_season
                ON reliable_task_instances(account_id, season_id, cadence, period_key);
            """;
        command.ExecuteNonQuery();
        using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText = """
            INSERT INTO reliable_task_schema(component, version)
            VALUES ('reliable-tasks', $version)
            ON CONFLICT(component) DO NOTHING;
            """;
        migration.Parameters.AddWithValue("$version", SchemaVersion);
        migration.ExecuteNonQuery();
        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = "SELECT version FROM reliable_task_schema WHERE component = 'reliable-tasks';";
        var version = Convert.ToInt32(verify.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported reliable task schema version {version}; expected {SchemaVersion}.");
        }
        transaction.Commit();
    }

    private static void ValidateSetDefinition(ReliableTaskSetDefinition definition)
    {
        if (definition.AccountId == Guid.Empty || definition.SeasonId == Guid.Empty ||
            definition.ContentVersionId == Guid.Empty)
        {
            throw new ArgumentException("Task set account, season, and content version ids are required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ServerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.PeriodKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ContentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.RulesVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.RotationSeed);
        if (definition.Tasks.Any(task => !task.Active || task.Cadence != definition.Cadence))
        {
            throw new ArgumentException("Task sets may contain only active tasks of their declared cadence.");
        }
        var unsupported = definition.Tasks.FirstOrDefault(task => task.EventKind is not (
            ContentTaskEventKind.ResourceExchangeSettled or
            ContentTaskEventKind.ResourceItemSettled or
            ContentTaskEventKind.ResourceValueSettled or
            ContentTaskEventKind.ShopOrderDelivered or
            ContentTaskEventKind.CurrencySpent));
        if (unsupported is not null)
        {
            throw new ReliableTaskException(
                "TASK_EVENT_KIND_NOT_AUTHORITATIVE",
                $"Task '{unsupported.TaskKey}' uses an event kind without an authoritative server source.");
        }
    }

    private static void ValidateEvent(ReliableEconomyEvent economyEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(economyEvent.EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(economyEvent.ServerId);
        if (economyEvent.EventId.Length > 128 || economyEvent.EventId.Any(char.IsControl) ||
            economyEvent.AccountId == Guid.Empty || economyEvent.SeasonId == Guid.Empty)
        {
            throw new ArgumentException("Reliable economy event identity is invalid.");
        }
        if (economyEvent.Items.Any(item => string.IsNullOrWhiteSpace(item.ItemId) || item.Quantity <= 0) ||
            economyEvent.CurrencySpent.Any(charge => charge.Amount <= 0) ||
            economyEvent.ResourceValue < 0)
        {
            throw new ArgumentException("Reliable economy event amounts must be positive and bounded.");
        }
        if ((economyEvent.ContentVersionId is null) !=
            string.IsNullOrWhiteSpace(economyEvent.ContentHash))
        {
            throw new ArgumentException(
                "Reliable economy content version and hash evidence must be supplied together.");
        }
        if (economyEvent.Source == ReliableEconomyEventSource.ResourceSettlement &&
            (economyEvent.Items.Count == 0 || economyEvent.ResourceValue <= 0 ||
             economyEvent.CurrencySpent.Count != 0))
        {
            throw new ArgumentException("A resource-settlement task event requires settled items and value only.");
        }
        if (economyEvent.Source == ReliableEconomyEventSource.ShopOrderDelivery &&
            (economyEvent.CurrencySpent.Count == 0 || economyEvent.Items.Count != 0 ||
             economyEvent.ResourceValue != 0))
        {
            throw new ArgumentException("A delivered-order task event requires authoritative currency charges only.");
        }
    }

    private static long Contribution(ContentTaskDefinition task, ReliableEconomyEvent economyEvent)
    {
        if (task.ExchangeZoneIds.Count > 0 &&
            (economyEvent.ZoneId is null || !task.ExchangeZoneIds.Contains(
                economyEvent.ZoneId,
                StringComparer.OrdinalIgnoreCase)))
        {
            return 0;
        }
        return task.EventKind switch
        {
            ContentTaskEventKind.ResourceExchangeSettled
                when economyEvent.Source == ReliableEconomyEventSource.ResourceSettlement => 1,
            ContentTaskEventKind.ResourceItemSettled
                when economyEvent.Source == ReliableEconomyEventSource.ResourceSettlement =>
                economyEvent.Items.Where(item => string.Equals(
                        item.ItemId,
                        task.TargetItemId,
                        StringComparison.OrdinalIgnoreCase))
                    .Sum(item => item.Quantity),
            ContentTaskEventKind.ResourceValueSettled
                when economyEvent.Source == ReliableEconomyEventSource.ResourceSettlement =>
                economyEvent.ResourceValue,
            ContentTaskEventKind.ShopOrderDelivered
                when economyEvent.Source == ReliableEconomyEventSource.ShopOrderDelivery => 1,
            ContentTaskEventKind.CurrencySpent
                when economyEvent.Source == ReliableEconomyEventSource.ShopOrderDelivery &&
                     task.TargetCurrency is not null =>
                economyEvent.CurrencySpent.Where(charge => charge.Currency == task.TargetCurrency)
                    .Sum(charge => charge.Amount),
            _ => 0
        };
    }

    private static async Task InsertTaskSetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReliableTaskSet taskSet,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reliable_task_sets(
                task_set_id, account_id, season_id, server_id, cadence,
                period_key, content_version_id, content_hash, rules_version,
                rotation_seed, selected_task_keys_json, created_at)
            VALUES(
                $taskSetId, $accountId, $seasonId, $serverId, $cadence,
                $periodKey, $contentVersionId, $contentHash, $rulesVersion,
                $rotationSeed, $selectedTaskKeysJson, $createdAt);
            """;
        command.Parameters.AddWithValue("$taskSetId", taskSet.TaskSetId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", taskSet.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", taskSet.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", taskSet.ServerId);
        command.Parameters.AddWithValue("$cadence", taskSet.Cadence.ToString());
        command.Parameters.AddWithValue("$periodKey", taskSet.PeriodKey);
        command.Parameters.AddWithValue("$contentVersionId", taskSet.ContentVersionId.ToString("D"));
        command.Parameters.AddWithValue("$contentHash", taskSet.ContentHash);
        command.Parameters.AddWithValue("$rulesVersion", taskSet.RulesVersion);
        command.Parameters.AddWithValue("$rotationSeed", taskSet.RotationSeed);
        command.Parameters.AddWithValue(
            "$selectedTaskKeysJson",
            JsonSerializer.Serialize(taskSet.SelectedTaskKeys, EconomyContentJson.Options));
        command.Parameters.AddWithValue("$createdAt", taskSet.CreatedAt.ToString("O"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertTaskInstanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReliableTaskSet taskSet,
        ContentTaskDefinition task,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reliable_task_instances(
                instance_id, task_set_id, account_id, season_id, server_id,
                cadence, period_key, content_version_id, content_hash,
                rules_version, rotation_seed, task_key, definition_json,
                progress, completed_at, currency_reward_ledger_entry_id,
                ranking_reward_entry_id, created_at, updated_at)
            VALUES(
                $instanceId, $taskSetId, $accountId, $seasonId, $serverId,
                $cadence, $periodKey, $contentVersionId, $contentHash,
                $rulesVersion, $rotationSeed, $taskKey, $definitionJson,
                0, NULL, NULL, NULL, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue(
            "$instanceId",
            DeterministicGuid($"instance|{taskSet.TaskSetId:N}|{task.TaskKey.ToLowerInvariant()}").ToString("D"));
        command.Parameters.AddWithValue("$taskSetId", taskSet.TaskSetId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", taskSet.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", taskSet.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", taskSet.ServerId);
        command.Parameters.AddWithValue("$cadence", taskSet.Cadence.ToString());
        command.Parameters.AddWithValue("$periodKey", taskSet.PeriodKey);
        command.Parameters.AddWithValue("$contentVersionId", taskSet.ContentVersionId.ToString("D"));
        command.Parameters.AddWithValue("$contentHash", taskSet.ContentHash);
        command.Parameters.AddWithValue("$rulesVersion", taskSet.RulesVersion);
        command.Parameters.AddWithValue("$rotationSeed", taskSet.RotationSeed);
        command.Parameters.AddWithValue("$taskKey", task.TaskKey);
        command.Parameters.AddWithValue(
            "$definitionJson",
            JsonSerializer.Serialize(task, EconomyContentJson.Options));
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ReliableTaskSet?> LoadTaskSetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid accountId,
        Guid seasonId,
        ContentTaskCadence cadence,
        string periodKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT task_set_id, account_id, season_id, server_id, cadence,
                   period_key, content_version_id, content_hash, rules_version,
                   rotation_seed, selected_task_keys_json, created_at
            FROM reliable_task_sets
            WHERE account_id = $accountId AND season_id = $seasonId
              AND cadence = $cadence AND period_key = $periodKey;
            """;
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue("$cadence", cadence.ToString());
        command.Parameters.AddWithValue("$periodKey", periodKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new ReliableTaskSet(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            Enum.Parse<ContentTaskCadence>(reader.GetString(4)),
            reader.GetString(5),
            Guid.Parse(reader.GetString(6)),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            JsonSerializer.Deserialize<string[]>(reader.GetString(10), EconomyContentJson.Options)
                ?? throw new InvalidDataException("Stored task selection is null."),
            DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture));
    }

    private static async Task<IReadOnlyList<ReliableTaskInstance>> ListTasksAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT instance_id, task_set_id, account_id, season_id, server_id,
                   cadence, period_key, content_version_id, content_hash,
                   rules_version, rotation_seed, definition_json, progress,
                   completed_at, currency_reward_ledger_entry_id,
                   ranking_reward_entry_id, created_at, updated_at
            FROM reliable_task_instances
            WHERE account_id = $accountId AND season_id = $seasonId
            ORDER BY cadence, period_key DESC, task_key;
            """;
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        List<ReliableTaskInstance> tasks = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(new ReliableTaskInstance(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                Enum.Parse<ContentTaskCadence>(reader.GetString(5)),
                reader.GetString(6),
                Guid.Parse(reader.GetString(7)),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                JsonSerializer.Deserialize<ContentTaskDefinition>(
                    reader.GetString(11),
                    EconomyContentJson.Options)
                    ?? throw new InvalidDataException("Stored task definition is null."),
                reader.GetInt64(12),
                reader.IsDBNull(13)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
                reader.IsDBNull(14) ? null : Guid.Parse(reader.GetString(14)),
                reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)),
                DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(17), CultureInfo.InvariantCulture)));
        }
        return tasks;
    }

    private async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReliableEconomyEvent economyEvent,
        string eventPayload,
        string eventHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reliable_task_events(
                event_id, event_hash, account_id, season_id, server_id, source,
                occurred_at, received_at, payload_json)
            VALUES(
                $eventId, $eventHash, $accountId, $seasonId, $serverId, $source,
                $occurredAt, $receivedAt, $payloadJson);
            """;
        command.Parameters.AddWithValue("$eventId", economyEvent.EventId);
        command.Parameters.AddWithValue("$eventHash", eventHash);
        command.Parameters.AddWithValue("$accountId", economyEvent.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", economyEvent.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", economyEvent.ServerId);
        command.Parameters.AddWithValue("$source", economyEvent.Source.ToString());
        command.Parameters.AddWithValue("$occurredAt", economyEvent.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$receivedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$payloadJson", eventPayload);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> GetEventHashAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_hash FROM reliable_task_events WHERE event_id = $eventId;";
        command.Parameters.AddWithValue("$eventId", eventId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task UpdateProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReliableTaskInstance task,
        long progress,
        DateTimeOffset? completedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reliable_task_instances
            SET progress = $progress,
                completed_at = COALESCE(completed_at, $completedAt),
                updated_at = $updatedAt
            WHERE instance_id = $instanceId AND progress = $expectedProgress;
            """;
        command.Parameters.AddWithValue("$progress", progress);
        command.Parameters.AddWithValue("$completedAt", completedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$instanceId", task.InstanceId.ToString("D"));
        command.Parameters.AddWithValue("$expectedProgress", task.Progress);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new IOException("Reliable task progress changed concurrently.");
        }
    }

    private static async Task GrantRankingRewardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReliableTaskInstance task,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var entryId = DeterministicGuid($"ranking-reward|{task.InstanceId:N}");
        await using var balance = connection.CreateCommand();
        balance.Transaction = transaction;
        balance.CommandText = """
            SELECT COALESCE(SUM(points), 0)
            FROM reliable_task_ranking_rewards
            WHERE account_id = $accountId AND season_id = $seasonId;
            """;
        balance.Parameters.AddWithValue("$accountId", task.AccountId.ToString("D"));
        balance.Parameters.AddWithValue("$seasonId", task.SeasonId.ToString("D"));
        var before = Convert.ToInt32(
            await balance.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        var after = checked(before + task.Definition.Reward.RankingPoints);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO reliable_task_ranking_rewards(
                entry_id, instance_id, account_id, season_id, points,
                balance_after, created_at)
            VALUES(
                $entryId, $instanceId, $accountId, $seasonId, $points,
                $balanceAfter, $createdAt)
            ON CONFLICT(instance_id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$entryId", entryId.ToString("D"));
        insert.Parameters.AddWithValue("$instanceId", task.InstanceId.ToString("D"));
        insert.Parameters.AddWithValue("$accountId", task.AccountId.ToString("D"));
        insert.Parameters.AddWithValue("$seasonId", task.SeasonId.ToString("D"));
        insert.Parameters.AddWithValue("$points", task.Definition.Reward.RankingPoints);
        insert.Parameters.AddWithValue("$balanceAfter", after);
        insert.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        _ = await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE reliable_task_instances
            SET ranking_reward_entry_id = $entryId
            WHERE instance_id = $instanceId AND ranking_reward_entry_id IS NULL;
            """;
        update.Parameters.AddWithValue("$entryId", entryId.ToString("D"));
        update.Parameters.AddWithValue("$instanceId", task.InstanceId.ToString("D"));
        _ = await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CanonicalEventPayload(ReliableEconomyEvent economyEvent) =>
        JsonSerializer.Serialize(
            economyEvent with
            {
                EventId = economyEvent.EventId.Trim(),
                ServerId = economyEvent.ServerId.Trim(),
                ZoneId = string.IsNullOrWhiteSpace(economyEvent.ZoneId)
                    ? null
                    : economyEvent.ZoneId.Trim(),
                Items = economyEvent.Items
                    .GroupBy(item => item.ItemId.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(group => new ReliableTaskItemAmount(
                        group.Key,
                        group.Sum(item => item.Quantity)))
                    .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                CurrencySpent = economyEvent.CurrencySpent
                    .GroupBy(charge => charge.Currency)
                    .Select(group => new ReliableTaskCurrencyAmount(
                        group.Key,
                        group.Sum(charge => charge.Amount)))
                    .OrderBy(charge => charge.Currency)
                    .ToArray()
            },
            EconomyContentJson.Options);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guid = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guid);
        return new Guid(guid);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));
}

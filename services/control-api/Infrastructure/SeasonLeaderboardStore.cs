using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Persists immutable leaderboard snapshots and their complete source evidence
/// in extraction-commerce.db. The source read is deliberately unpaged so a
/// season with more than 1,000 settlements cannot be silently truncated.
/// </summary>
public sealed class SeasonLeaderboardStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public SeasonLeaderboardStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath), timeProvider)
    {
    }

    public SeasonLeaderboardStore(string dataDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullPath = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fullPath, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Initialize();
    }

    public async Task<SeasonLeaderboardRecord?> GetRecordAsync(
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            var package = await LoadPackageAsync(connection, seasonId, cancellationToken);
            if (package is null)
            {
                return null;
            }
            var audit = await ListAuditAsync(connection, seasonId, cancellationToken);
            return new SeasonLeaderboardRecord(package.Value.Snapshot, package.Value.Evidence, audit);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonLeaderboardSnapshot?> GetSnapshotAsync(
        Guid seasonId,
        CancellationToken cancellationToken) =>
        (await GetRecordAsync(seasonId, cancellationToken))?.Snapshot;

    public async Task<SeasonLeaderboardRecord?> GetLatestRecordAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT season_id
                FROM season_leaderboard_snapshots
                WHERE server_id = $serverId
                ORDER BY julianday(cutoff_at) DESC,
                         julianday(frozen_at) DESC,
                         snapshot_id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$serverId", serverId.Trim());
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not string seasonIdText)
            {
                return null;
            }
            var seasonId = Guid.Parse(seasonIdText);
            var package = await LoadPackageAsync(connection, seasonId, cancellationToken)
                ?? throw new InvalidDataException(
                    "The latest leaderboard index references a missing snapshot.");
            if (!string.Equals(
                    package.Snapshot.ServerId,
                    serverId.Trim(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The latest leaderboard server index conflicts with its frozen snapshot.");
            }
            var audit = await ListAuditAsync(connection, seasonId, cancellationToken);
            return new SeasonLeaderboardRecord(package.Snapshot, package.Evidence, audit);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<SeasonLeaderboardSourceData> ReadSourceDataAsync(
        Guid seasonId,
        DateTimeOffset cutoffAt,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            var storedRuns = new List<ExtractionSettlementRun>();
            if (await TableExistsAsync(connection, "extraction_settlement_runs", cancellationToken))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT payload
                    FROM extraction_settlement_runs
                    WHERE season_id = $seasonId AND state = 'Settled'
                    ORDER BY updated_at, run_id;
                    """;
                command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var run = JsonSerializer.Deserialize<ExtractionSettlementRun>(
                        reader.GetString(0),
                        JsonOptions)
                        ?? throw new InvalidDataException(
                            "A settled extraction run is null in leaderboard source evidence.");
                    if (run.SeasonId != seasonId ||
                        run.State != ExtractionSettlementState.Settled ||
                        run.SettledAt is null)
                    {
                        throw new InvalidDataException(
                            $"Extraction run '{run.RunId}' conflicts with its settled source index.");
                    }
                    storedRuns.Add(run);
                }
            }

            var contentCategories = await LoadContentCategoriesAsync(
                connection,
                storedRuns
                    .Where(run => run.ContentVersionId is not null)
                    .Select(run => run.ContentVersionId!.Value)
                    .Distinct()
                    .Order()
                    .ToArray(),
                cancellationToken);
            var settlements = new List<SeasonLeaderboardSettlementSource>();
            var lateSettlementIds = new List<Guid>();
            foreach (var run in storedRuns.OrderBy(run => run.SettledAt).ThenBy(run => run.RunId))
            {
                var settledAt = run.SettledAt
                    ?? throw new InvalidDataException(
                        $"Settled extraction run '{run.RunId}' has no settlement timestamp.");
                if (settledAt > cutoffAt)
                {
                    lateSettlementIds.Add(run.RunId);
                    continue;
                }
                IReadOnlyDictionary<string, string>? categories = null;
                if (run.ContentVersionId is Guid versionId &&
                    !contentCategories.TryGetValue(versionId, out categories))
                {
                    throw new InvalidDataException(
                        $"Settlement '{run.RunId}' references missing content version '{versionId}'.");
                }
                var lines = run.Items
                    .OrderBy(line => line.ItemId, StringComparer.OrdinalIgnoreCase)
                    .Select(line =>
                    {
                        if (line.Quantity <= 0 || line.UnitValue <= 0 || line.TotalValue <= 0 ||
                            checked((long)line.Quantity * line.UnitValue) != line.TotalValue)
                        {
                            throw new InvalidDataException(
                                $"Settlement '{run.RunId}' contains invalid resource evidence.");
                        }
                        var category = run.ContentVersionId is null
                            ? "legacy-unclassified"
                            : categories!.TryGetValue(line.ItemId, out var configured)
                                ? configured
                                : throw new InvalidDataException(
                                    $"Settlement '{run.RunId}' item '{line.ItemId}' is absent from its content version.");
                        return new SeasonLeaderboardSettlementLineSource(
                            line.ItemId,
                            category,
                            line.Quantity,
                            line.UnitValue,
                            line.TotalValue);
                    })
                    .ToArray();
                if (checked(lines.Sum(line => (long)line.Quantity)) != run.ItemCount ||
                    checked(lines.Sum(line => line.TotalValue)) != run.TotalValue)
                {
                    throw new InvalidDataException(
                        $"Settlement '{run.RunId}' totals conflict with its resource lines.");
                }
                settlements.Add(new SeasonLeaderboardSettlementSource(
                    run.RunId,
                    run.AccountId,
                    run.Revision,
                    settledAt,
                    run.ContentVersionId,
                    run.ContentHash,
                    run.ItemCount,
                    run.TotalValue,
                    lines));
            }

            var taskPoints = new List<SeasonLeaderboardTaskPointSource>();
            var lateTaskIds = new List<Guid>();
            if (await TableExistsAsync(
                    connection,
                    "reliable_task_ranking_rewards",
                    cancellationToken))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT entry_id, account_id, points, created_at
                    FROM reliable_task_ranking_rewards
                    WHERE season_id = $seasonId
                    ORDER BY created_at, entry_id;
                    """;
                command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var entryId = Guid.Parse(reader.GetString(0));
                    var accountId = Guid.Parse(reader.GetString(1));
                    var points = reader.GetInt32(2);
                    var createdAt = ParseTimestamp(reader.GetString(3));
                    if (points <= 0)
                    {
                        throw new InvalidDataException(
                            $"Task ranking entry '{entryId}' has non-positive points.");
                    }
                    if (createdAt > cutoffAt)
                    {
                        lateTaskIds.Add(entryId);
                    }
                    else
                    {
                        taskPoints.Add(new SeasonLeaderboardTaskPointSource(
                            entryId,
                            accountId,
                            points,
                            createdAt));
                    }
                }
            }

            return new SeasonLeaderboardSourceData(new SeasonLeaderboardEvidence(
                seasonId,
                cutoffAt,
                settlements,
                taskPoints,
                lateSettlementIds.Order().ToArray(),
                lateTaskIds.Order().ToArray()));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonLeaderboardRecord> SaveFrozenAsync(
        SeasonLeaderboardSnapshot snapshot,
        SeasonLeaderboardEvidence evidence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ValidateSnapshot(snapshot, evidence);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var existing = await LoadPackageAsync(
                connection,
                snapshot.SeasonId,
                cancellationToken,
                transaction);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                var audit = await ListAuditAsync(connection, snapshot.SeasonId, cancellationToken);
                return new SeasonLeaderboardRecord(
                    existing.Value.Snapshot,
                    existing.Value.Evidence,
                    audit);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO season_leaderboard_snapshots (
                        snapshot_id, season_id, server_id, rules_version,
                        cutoff_at, rules_hash, source_hash, snapshot_hash,
                        snapshot_json, evidence_json, frozen_by, frozen_at,
                        reward_state, updated_at)
                    VALUES (
                        $snapshotId, $seasonId, $serverId, $rulesVersion,
                        $cutoffAt, $rulesHash, $sourceHash, $snapshotHash,
                        $snapshotJson, $evidenceJson, $frozenBy, $frozenAt,
                        'not-prepared', $updatedAt);
                    """;
                insert.Parameters.AddWithValue("$snapshotId", snapshot.SnapshotId.ToString("D"));
                insert.Parameters.AddWithValue("$seasonId", snapshot.SeasonId.ToString("D"));
                insert.Parameters.AddWithValue("$serverId", snapshot.ServerId);
                insert.Parameters.AddWithValue("$rulesVersion", snapshot.Rules.RulesVersion);
                insert.Parameters.AddWithValue("$cutoffAt", snapshot.CutoffAt.ToString("O"));
                insert.Parameters.AddWithValue("$rulesHash", snapshot.RulesHash);
                insert.Parameters.AddWithValue("$sourceHash", snapshot.SourceHash);
                insert.Parameters.AddWithValue("$snapshotHash", snapshot.SnapshotHash);
                insert.Parameters.AddWithValue("$snapshotJson", JsonSerializer.Serialize(snapshot, JsonOptions));
                insert.Parameters.AddWithValue("$evidenceJson", JsonSerializer.Serialize(evidence, JsonOptions));
                insert.Parameters.AddWithValue("$frozenBy", snapshot.FrozenBy);
                insert.Parameters.AddWithValue("$frozenAt", snapshot.FrozenAt.ToString("O"));
                insert.Parameters.AddWithValue("$updatedAt", snapshot.FrozenAt.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertAuditAsync(
                connection,
                transaction,
                eventKey: $"freeze:{snapshot.SnapshotId:N}",
                snapshot.SeasonId,
                snapshot.SnapshotId,
                "leaderboard.frozen",
                snapshot.FrozenBy,
                "Leaderboard snapshot frozen after the configured late-settlement cutoff.",
                correlationId,
                snapshot.SnapshotHash,
                snapshot.FrozenAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var auditEvents = await ListAuditAsync(connection, snapshot.SeasonId, cancellationToken);
            return new SeasonLeaderboardRecord(snapshot, evidence, auditEvents);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonLeaderboardExclusion> SetExclusionAsync(
        Guid seasonId,
        Guid accountId,
        bool active,
        string reason,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ValidateAuditText(reason, 3, 500, nameof(reason));
        ValidateAuditText(actor, 1, 256, nameof(actor));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var current = await ReadExclusionAsync(
                connection,
                transaction,
                seasonId,
                accountId,
                cancellationToken);
            if (current is not null &&
                current.Active == active &&
                string.Equals(current.Reason, reason.Trim(), StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return current;
            }
            var createdAt = current?.CreatedAt ?? now;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO season_leaderboard_exclusions (
                        season_id, account_id, active, reason, actor, created_at, updated_at)
                    VALUES (
                        $seasonId, $accountId, $active, $reason, $actor, $createdAt, $updatedAt)
                    ON CONFLICT(season_id, account_id) DO UPDATE SET
                        active = excluded.active,
                        reason = excluded.reason,
                        actor = excluded.actor,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
                command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
                command.Parameters.AddWithValue("$active", active ? 1 : 0);
                command.Parameters.AddWithValue("$reason", reason.Trim());
                command.Parameters.AddWithValue("$actor", actor.Trim());
                command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
                command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            var snapshotId = await ReadSnapshotIdAsync(connection, transaction, seasonId, cancellationToken);
            await InsertAuditAsync(
                connection,
                transaction,
                $"exclusion:{Guid.NewGuid():N}",
                seasonId,
                snapshotId,
                snapshotId is null
                    ? active ? "account.excluded" : "account.reinstated"
                    : active ? "reward.cancelled" : "reward.reinstated",
                actor.Trim(),
                reason.Trim(),
                correlationId,
                SeasonLeaderboardHash.Of(new { accountId, active, reason = reason.Trim() }),
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SeasonLeaderboardExclusion(
                seasonId,
                accountId,
                active,
                reason.Trim(),
                actor.Trim(),
                createdAt,
                now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SeasonLeaderboardExclusion>> ListExclusionsAsync(
        Guid seasonId,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT season_id, account_id, active, reason, actor, created_at, updated_at
                FROM season_leaderboard_exclusions
                WHERE season_id = $seasonId AND ($activeOnly = 0 OR active = 1)
                ORDER BY account_id;
                """;
            command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            command.Parameters.AddWithValue("$activeOnly", activeOnly ? 1 : 0);
            var results = new List<SeasonLeaderboardExclusion>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(ReadExclusion(reader));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonLeaderboardSnapshot> AttachRewardJobAsync(
        Guid seasonId,
        Guid jobId,
        IReadOnlyList<SeasonLeaderboardRewardDecision> decisions,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var package = await LoadPackageAsync(connection, seasonId, cancellationToken, transaction)
                ?? throw new KeyNotFoundException(
                    $"Season leaderboard snapshot '{seasonId}' does not exist.");
            if (package.Snapshot.RewardJobId is Guid existing && existing != jobId)
            {
                throw new InvalidOperationException(
                    "The frozen leaderboard is already bound to another reward job.");
            }
            if (package.Snapshot.RewardJobId == jobId &&
                !string.Equals(
                    SeasonLeaderboardHash.Of(package.Snapshot.RewardDecisions ?? []),
                    SeasonLeaderboardHash.Of(decisions),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The frozen leaderboard reward job is bound to different reward decisions.");
            }
            if (package.Snapshot.RewardJobId is null)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE season_leaderboard_snapshots
                    SET reward_job_id = $jobId,
                        reward_state = 'prepared',
                        reward_decisions_json = $decisions,
                        reward_prepared_at = $now,
                        updated_at = $now
                    WHERE season_id = $seasonId AND reward_job_id IS NULL;
                    """;
                update.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
                update.Parameters.AddWithValue("$decisions", JsonSerializer.Serialize(decisions, JsonOptions));
                update.Parameters.AddWithValue("$now", now.ToString("O"));
                update.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException(
                        "The leaderboard reward job could not be attached atomically.");
                }
                await InsertAuditAsync(
                    connection,
                    transaction,
                    $"reward-prepared:{jobId:N}",
                    seasonId,
                    package.Snapshot.SnapshotId,
                    "reward.prepared",
                    actor,
                    "Standard leaderboard reward job prepared from the frozen snapshot.",
                    correlationId,
                    SeasonLeaderboardHash.Of(decisions),
                    now,
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return (await LoadPackageAsync(connection, seasonId, cancellationToken))!.Value.Snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonLeaderboardSnapshot> MarkRewardCompletedAsync(
        Guid seasonId,
        Guid jobId,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var package = await LoadPackageAsync(connection, seasonId, cancellationToken, transaction)
                ?? throw new KeyNotFoundException(
                    $"Season leaderboard snapshot '{seasonId}' does not exist.");
            if (package.Snapshot.RewardJobId != jobId)
            {
                throw new InvalidOperationException(
                    "The completed reward job is not the job bound to this snapshot.");
            }
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE season_leaderboard_snapshots
                    SET reward_state = 'completed',
                        reward_completed_at = COALESCE(reward_completed_at, $now),
                        updated_at = $now
                    WHERE season_id = $seasonId AND reward_job_id = $jobId;
                    """;
                update.Parameters.AddWithValue("$now", now.ToString("O"));
                update.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
                update.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertAuditAsync(
                connection,
                transaction,
                $"reward-completed:{jobId:N}",
                seasonId,
                package.Snapshot.SnapshotId,
                "reward.completed",
                actor,
                "Standard leaderboard reward job completed.",
                correlationId,
                SeasonLeaderboardHash.Of(new { jobId }),
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await LoadPackageAsync(connection, seasonId, cancellationToken))!.Value.Snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendManualRewardAuditAsync(
        Guid seasonId,
        Guid snapshotId,
        Guid jobId,
        Guid accountId,
        long amount,
        string manualKey,
        string reason,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await InsertAuditAsync(
                connection,
                transaction,
                $"manual-reward:{jobId:N}",
                seasonId,
                snapshotId,
                "reward.manual-supplement",
                actor,
                reason,
                correlationId,
                SeasonLeaderboardHash.Of(new
                {
                    jobId,
                    accountId,
                    amount,
                    manualKey = manualKey.Trim(),
                    reason = reason.Trim()
                }),
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS economy_schema_migrations (
                component TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                applied_at TEXT NOT NULL,
                PRIMARY KEY (component, version)
            );
            CREATE TABLE IF NOT EXISTS season_leaderboard_snapshots (
                snapshot_id TEXT PRIMARY KEY,
                season_id TEXT NOT NULL UNIQUE,
                server_id TEXT NOT NULL,
                rules_version TEXT NOT NULL,
                cutoff_at TEXT NOT NULL,
                rules_hash TEXT NOT NULL CHECK (length(rules_hash) = 64),
                source_hash TEXT NOT NULL CHECK (length(source_hash) = 64),
                snapshot_hash TEXT NOT NULL CHECK (length(snapshot_hash) = 64),
                snapshot_json TEXT NOT NULL CHECK (json_valid(snapshot_json)),
                evidence_json TEXT NOT NULL CHECK (json_valid(evidence_json)),
                frozen_by TEXT NOT NULL,
                frozen_at TEXT NOT NULL,
                reward_job_id TEXT NULL,
                reward_state TEXT NOT NULL CHECK (
                    reward_state IN ('not-prepared', 'prepared', 'completed')),
                reward_decisions_json TEXT NULL CHECK (
                    reward_decisions_json IS NULL OR json_valid(reward_decisions_json)),
                reward_prepared_at TEXT NULL,
                reward_completed_at TEXT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS season_leaderboard_exclusions (
                season_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                active INTEGER NOT NULL CHECK (active IN (0, 1)),
                reason TEXT NOT NULL,
                actor TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (season_id, account_id)
            );
            CREATE TABLE IF NOT EXISTS season_leaderboard_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                event_key TEXT NOT NULL UNIQUE,
                season_id TEXT NOT NULL,
                snapshot_id TEXT NULL,
                event_type TEXT NOT NULL,
                actor TEXT NOT NULL,
                reason TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                details_hash TEXT NOT NULL CHECK (length(details_hash) = 64),
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_season_leaderboard_audit_season
                ON season_leaderboard_audit (season_id, sequence);
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('season-leaderboards', $version, $appliedAt);
            """;
        command.Parameters.AddWithValue("$version", SchemaVersion);
        command.Parameters.AddWithValue("$appliedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
        using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT MAX(version)
            FROM economy_schema_migrations
            WHERE component = 'season-leaderboards';
            """;
        var storedVersion = Convert.ToInt32(
            verify.ExecuteScalar(),
            CultureInfo.InvariantCulture);
        if (storedVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported season leaderboard schema version {storedVersion}; " +
                $"expected {SchemaVersion}.");
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static async Task<Dictionary<Guid, IReadOnlyDictionary<string, string>>>
        LoadContentCategoriesAsync(
            SqliteConnection connection,
            IReadOnlyList<Guid> versionIds,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        if (versionIds.Count == 0)
        {
            return result;
        }
        if (!await TableExistsAsync(connection, "content_versions", cancellationToken))
        {
            throw new InvalidDataException(
                "Content-version evidence is missing for version-pinned settlements.");
        }
        await using var command = connection.CreateCommand();
        var parameters = new List<string>(versionIds.Count);
        for (var index = 0; index < versionIds.Count; index++)
        {
            var name = $"$version{index}";
            parameters.Add(name);
            command.Parameters.AddWithValue(name, versionIds[index].ToString("D"));
        }
        command.CommandText = $"""
            SELECT version_id, document_json
            FROM content_versions
            WHERE version_id IN ({string.Join(',', parameters)});
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var versionId = Guid.Parse(reader.GetString(0));
            using var document = JsonDocument.Parse(reader.GetString(1));
            if (!document.RootElement.TryGetProperty("resources", out var resources) ||
                resources.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"Content version '{versionId}' has no resource definitions.");
            }
            var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in resources.EnumerateArray())
            {
                var itemId = resource.GetProperty("itemId").GetString();
                var category = resource.GetProperty("category").GetString();
                if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(category) ||
                    !categories.TryAdd(itemId.Trim(), category.Trim()))
                {
                    throw new InvalidDataException(
                        $"Content version '{versionId}' has invalid resource category evidence.");
                }
            }
            result[versionId] = categories;
        }
        return result;
    }

    private static async Task<(SeasonLeaderboardSnapshot Snapshot, SeasonLeaderboardEvidence Evidence)?>
        LoadPackageAsync(
            SqliteConnection connection,
            Guid seasonId,
            CancellationToken cancellationToken,
            SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_json, evidence_json, reward_job_id,
                   reward_state, reward_decisions_json
            FROM season_leaderboard_snapshots
            WHERE season_id = $seasonId;
            """;
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var snapshot = JsonSerializer.Deserialize<SeasonLeaderboardSnapshot>(
            reader.GetString(0),
            JsonOptions)
            ?? throw new InvalidDataException("The leaderboard snapshot JSON is null.");
        var evidence = JsonSerializer.Deserialize<SeasonLeaderboardEvidence>(
            reader.GetString(1),
            JsonOptions)
            ?? throw new InvalidDataException("The leaderboard evidence JSON is null.");
        Guid? jobId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));
        var rewardState = reader.GetString(3);
        var decisions = reader.IsDBNull(4)
            ? Array.Empty<SeasonLeaderboardRewardDecision>()
            : JsonSerializer.Deserialize<SeasonLeaderboardRewardDecision[]>(
                reader.GetString(4),
                JsonOptions)
                ?? throw new InvalidDataException("The leaderboard reward decisions are null.");
        snapshot = snapshot with
        {
            RewardJobId = jobId,
            RewardState = rewardState,
            RewardDecisions = decisions
        };
        ValidateSnapshot(snapshot, evidence);
        return (snapshot, evidence);
    }

    private static async Task<IReadOnlyList<SeasonLeaderboardAuditEvent>> ListAuditAsync(
        SqliteConnection connection,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, event_id, event_key, season_id, snapshot_id,
                   event_type, actor, reason, correlation_id, details_hash, occurred_at
            FROM season_leaderboard_audit
            WHERE season_id = $seasonId
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        var results = new List<SeasonLeaderboardAuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SeasonLeaderboardAuditEvent(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                ParseTimestamp(reader.GetString(10))));
        }
        return results;
    }

    private static async Task InsertAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventKey,
        Guid seasonId,
        Guid? snapshotId,
        string eventType,
        string actor,
        string reason,
        string correlationId,
        string detailsHash,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ValidateAuditText(actor, 1, 256, nameof(actor));
        ValidateAuditText(reason, 3, 500, nameof(reason));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO season_leaderboard_audit (
                event_id, event_key, season_id, snapshot_id, event_type,
                actor, reason, correlation_id, details_hash, occurred_at)
            VALUES (
                $eventId, $eventKey, $seasonId, $snapshotId, $eventType,
                $actor, $reason, $correlationId, $detailsHash, $occurredAt);
            """;
        command.Parameters.AddWithValue(
            "$eventId",
            SeasonLeaderboardHash.DeterministicGuid($"audit|{eventKey}").ToString("D"));
        command.Parameters.AddWithValue("$eventKey", eventKey);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue(
            "$snapshotId",
            snapshotId is Guid value ? value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$actor", actor.Trim());
        command.Parameters.AddWithValue("$reason", reason.Trim());
        command.Parameters.AddWithValue("$correlationId", NormalizeCorrelationId(correlationId));
        command.Parameters.AddWithValue("$detailsHash", detailsHash);
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = """
            SELECT season_id, snapshot_id, event_type, details_hash
            FROM season_leaderboard_audit
            WHERE event_key = $eventKey;
            """;
        verify.Parameters.AddWithValue("$eventKey", eventKey);
        await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            Guid.Parse(reader.GetString(0)) != seasonId ||
            (reader.IsDBNull(1) ? (Guid?)null : Guid.Parse(reader.GetString(1))) != snapshotId ||
            !string.Equals(reader.GetString(2), eventType, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(3), detailsHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Leaderboard audit event '{eventKey}' conflicts with its durable replay.");
        }
    }

    private static async Task<SeasonLeaderboardExclusion?> ReadExclusionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seasonId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT season_id, account_id, active, reason, actor, created_at, updated_at
            FROM season_leaderboard_exclusions
            WHERE season_id = $seasonId AND account_id = $accountId;
            """;
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadExclusion(reader) : null;
    }

    private static SeasonLeaderboardExclusion ReadExclusion(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetInt32(2) == 1,
        reader.GetString(3),
        reader.GetString(4),
        ParseTimestamp(reader.GetString(5)),
        ParseTimestamp(reader.GetString(6)));

    private static async Task<Guid?> ReadSnapshotIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT snapshot_id FROM season_leaderboard_snapshots WHERE season_id = $seasonId;";
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken) is string value
            ? Guid.Parse(value)
            : null;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName);
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static void ValidateSnapshot(
        SeasonLeaderboardSnapshot snapshot,
        SeasonLeaderboardEvidence evidence)
    {
        if (snapshot.SeasonId != evidence.SeasonId || snapshot.CutoffAt != evidence.CutoffAt ||
            !string.Equals(snapshot.FrameworkVersion, SeasonLeaderboardPolicy.FrameworkVersion, StringComparison.Ordinal) ||
            !string.Equals(snapshot.RulesHash, SeasonLeaderboardHash.Of(snapshot.Rules), StringComparison.Ordinal) ||
            !string.Equals(snapshot.SourceHash, SeasonLeaderboardHash.Of(evidence), StringComparison.Ordinal) ||
            !string.Equals(snapshot.SnapshotHash, SeasonLeaderboardHash.Snapshot(snapshot), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The season leaderboard snapshot or its source evidence failed hash validation.");
        }
    }

    private static void ValidateAuditText(string value, int minimum, int maximum, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var trimmed = value.Trim();
        if (trimmed.Length < minimum || trimmed.Length > maximum || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{name} must contain {minimum}-{maximum} non-control characters.",
                name);
        }
    }

    private static string NormalizeCorrelationId(string value) =>
        Guid.TryParseExact(value, "D", out var parsed)
            ? parsed.ToString("D")
            : throw new ArgumentException(
                "Leaderboard mutations require a canonical correlation GUID.",
                nameof(value));

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind);

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));
}

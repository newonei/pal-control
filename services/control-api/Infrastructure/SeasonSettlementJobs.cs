using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public enum SeasonSettlementJobKind
{
    SeasonVoucherExpiry,
    Reward
}

public enum SeasonSettlementJobState
{
    Prepared,
    Running,
    Completed
}

public enum SeasonSettlementItemState
{
    Pending,
    Applied
}

public sealed record SeasonRewardGrant(
    Guid AccountId,
    ExtractionCurrency Currency,
    Guid? TargetSeasonId,
    long Amount,
    string RewardKey);

public sealed record SeasonSettlementJobItem(
    Guid ItemId,
    Guid JobId,
    string ItemKey,
    Guid AccountId,
    ExtractionCurrency Currency,
    Guid? TargetSeasonId,
    long Delta,
    string Reason,
    string ReferenceType,
    string ReferenceId,
    string IdempotencyKey,
    SeasonSettlementItemState State,
    Guid? LedgerEntryId,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SeasonSettlementJob(
    Guid JobId,
    SeasonSettlementJobKind Kind,
    Guid SourceSeasonId,
    string RulesVersion,
    string FrameworkVersion,
    string JobKey,
    string PayloadHash,
    SeasonSettlementJobState State,
    long Revision,
    string PreparedBy,
    DateTimeOffset PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt,
    string? LastError,
    IReadOnlyList<SeasonSettlementJobItem> Items);

/// <summary>
/// Stores the durable job envelope in the same SQLite database as the wallet
/// ledger. Wallet mutations still go through IExtractionRepository so its
/// idempotency and invariant checks remain the single money-moving authority.
/// </summary>
public sealed class SeasonSettlementJobStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public SeasonSettlementJobStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath), timeProvider)
    {
    }

    public SeasonSettlementJobStore(string dataDirectory, TimeProvider? timeProvider = null)
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

    public async Task<SeasonSettlementJob?> GetAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            return await LoadAsync(connection, jobId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonSettlementJob?> FindExpiryAsync(
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT job_id
                FROM season_settlement_jobs
                WHERE kind = 'season_voucher_expiry'
                  AND source_season_id = $seasonId
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string id
                ? await LoadAsync(connection, Guid.Parse(id), cancellationToken)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns only jobs that contain a settlement item for the requested
    /// account. Each returned envelope is additionally narrowed to that
    /// account before it leaves the store so player-facing projections cannot
    /// accidentally serialize another player's reward or expiry item.
    /// </summary>
    public async Task<IReadOnlyList<SeasonSettlementJob>> ListForAccountAsync(
        Guid sourceSeasonId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT job.job_id
                FROM season_settlement_jobs AS job
                INNER JOIN season_settlement_items AS item
                    ON item.job_id = job.job_id
                WHERE job.source_season_id = $seasonId
                  AND item.account_id = $accountId
                ORDER BY job.prepared_at, job.job_id;
                """;
            command.Parameters.AddWithValue("$seasonId", sourceSeasonId.ToString("D"));
            command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
            var ids = new List<Guid>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(Guid.Parse(reader.GetString(0)));
                }
            }

            var jobs = new List<SeasonSettlementJob>(ids.Count);
            foreach (var id in ids)
            {
                var job = await LoadAsync(connection, id, cancellationToken)
                    ?? throw new InvalidDataException(
                        $"Season settlement job '{id}' disappeared while building a player projection.");
                jobs.Add(job with
                {
                    Items = job.Items
                        .Where(item => item.AccountId == accountId)
                        .OrderBy(item => item.ItemKey, StringComparer.Ordinal)
                        .ToArray()
                });
            }
            return jobs;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonSettlementJob> PrepareAsync(
        Guid jobId,
        SeasonSettlementJobKind kind,
        Guid sourceSeasonId,
        string rulesVersion,
        string frameworkVersion,
        string jobKey,
        string payloadHash,
        string preparedBy,
        IReadOnlyList<SeasonSettlementJobItem> items,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var existing = await LoadAsync(connection, jobId, cancellationToken, (SqliteTransaction)transaction);
            if (existing is not null)
            {
                EnsureSamePayload(existing, payloadHash);
                await transaction.RollbackAsync(cancellationToken);
                return existing;
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO season_settlement_jobs (
                        job_id, kind, source_season_id, rules_version,
                        framework_version, job_key, payload_hash, state,
                        revision, prepared_by, prepared_at, updated_at)
                    VALUES (
                        $jobId, $kind, $sourceSeasonId, $rulesVersion,
                        $frameworkVersion, $jobKey, $payloadHash, 'prepared',
                        0, $preparedBy, $preparedAt, $updatedAt);
                    """;
                insert.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
                insert.Parameters.AddWithValue("$kind", KindToStorage(kind));
                insert.Parameters.AddWithValue("$sourceSeasonId", sourceSeasonId.ToString("D"));
                insert.Parameters.AddWithValue("$rulesVersion", rulesVersion);
                insert.Parameters.AddWithValue("$frameworkVersion", frameworkVersion);
                insert.Parameters.AddWithValue("$jobKey", jobKey);
                insert.Parameters.AddWithValue("$payloadHash", payloadHash);
                insert.Parameters.AddWithValue("$preparedBy", preparedBy);
                insert.Parameters.AddWithValue("$preparedAt", now.ToString("O"));
                insert.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var item in items)
            {
                await InsertItemAsync(connection, (SqliteTransaction)transaction, item, now, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return await LoadAsync(connection, jobId, cancellationToken)
                ?? throw new InvalidOperationException("The prepared season job could not be reloaded.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonSettlementJob> MarkRunningAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE season_settlement_jobs
                SET state = CASE WHEN state = 'prepared' THEN 'running' ELSE state END,
                    started_at = COALESCE(started_at, $now),
                    revision = CASE WHEN state = 'prepared' THEN revision + 1 ELSE revision END,
                    updated_at = CASE WHEN state = 'prepared' THEN $now ELSE updated_at END,
                    last_error = CASE WHEN state = 'prepared' THEN NULL ELSE last_error END
                WHERE job_id = $jobId;
                """;
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new KeyNotFoundException($"Season settlement job '{jobId}' does not exist.");
            }
            return await LoadAsync(connection, jobId, cancellationToken)
                ?? throw new InvalidOperationException("The season job could not be reloaded.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkItemAppliedAsync(
        Guid jobId,
        Guid itemId,
        Guid ledgerEntryId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE season_settlement_items
                SET state = 'applied', ledger_entry_id = $ledgerEntryId,
                    last_error = NULL, updated_at = $now
                WHERE job_id = $jobId AND item_id = $itemId
                  AND (state = 'pending' OR ledger_entry_id = $ledgerEntryId);
                """;
            command.Parameters.AddWithValue("$ledgerEntryId", ledgerEntryId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            command.Parameters.AddWithValue("$itemId", itemId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The settlement item was missing or had conflicting evidence.");
            }
            await using var updateJob = connection.CreateCommand();
            updateJob.Transaction = (SqliteTransaction)transaction;
            updateJob.CommandText = """
                UPDATE season_settlement_jobs
                SET revision = revision + 1, updated_at = $now, last_error = NULL
                WHERE job_id = $jobId;
                """;
            updateJob.Parameters.AddWithValue("$now", now.ToString("O"));
            updateJob.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            await updateJob.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkItemErrorAsync(
        Guid jobId,
        Guid itemId,
        string error,
        CancellationToken cancellationToken)
    {
        var bounded = string.IsNullOrWhiteSpace(error) ? "unknown_error" : error.Trim();
        if (bounded.Length > 500)
        {
            bounded = bounded[..500];
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var item = connection.CreateCommand();
            item.Transaction = (SqliteTransaction)transaction;
            item.CommandText = """
                UPDATE season_settlement_items
                SET last_error = $error, updated_at = $now
                WHERE job_id = $jobId AND item_id = $itemId AND state = 'pending';
                """;
            item.Parameters.AddWithValue("$error", bounded);
            item.Parameters.AddWithValue("$now", now.ToString("O"));
            item.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            item.Parameters.AddWithValue("$itemId", itemId.ToString("D"));
            await item.ExecuteNonQueryAsync(cancellationToken);
            await using var job = connection.CreateCommand();
            job.Transaction = (SqliteTransaction)transaction;
            job.CommandText = """
                UPDATE season_settlement_jobs
                SET last_error = $error, revision = revision + 1, updated_at = $now
                WHERE job_id = $jobId AND state <> 'completed';
                """;
            job.Parameters.AddWithValue("$error", bounded);
            job.Parameters.AddWithValue("$now", now.ToString("O"));
            job.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            await job.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonSettlementJob> CompleteIfDrainedAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE season_settlement_jobs
                SET state = 'completed', completed_at = COALESCE(completed_at, $now),
                    updated_at = $now, revision = revision + 1, last_error = NULL
                WHERE job_id = $jobId AND state <> 'completed'
                  AND NOT EXISTS (
                      SELECT 1 FROM season_settlement_items
                      WHERE job_id = $jobId AND state = 'pending');
                """;
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return await LoadAsync(connection, jobId, cancellationToken)
                ?? throw new KeyNotFoundException($"Season settlement job '{jobId}' does not exist.");
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
            CREATE TABLE IF NOT EXISTS season_settlement_jobs (
                job_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK (kind IN ('season_voucher_expiry', 'reward')),
                source_season_id TEXT NOT NULL,
                rules_version TEXT NOT NULL,
                framework_version TEXT NOT NULL,
                job_key TEXT NOT NULL,
                payload_hash TEXT NOT NULL CHECK (length(payload_hash) = 64),
                state TEXT NOT NULL CHECK (state IN ('prepared', 'running', 'completed')),
                revision INTEGER NOT NULL CHECK (revision >= 0),
                prepared_by TEXT NOT NULL,
                prepared_at TEXT NOT NULL,
                started_at TEXT NULL,
                completed_at TEXT NULL,
                updated_at TEXT NOT NULL,
                last_error TEXT NULL,
                UNIQUE (kind, source_season_id, rules_version, job_key)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_one_voucher_expiry_per_season
                ON season_settlement_jobs (source_season_id)
                WHERE kind = 'season_voucher_expiry';
            CREATE TABLE IF NOT EXISTS season_settlement_items (
                item_id TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                item_key TEXT NOT NULL,
                account_id TEXT NOT NULL,
                currency TEXT NOT NULL CHECK (currency IN ('market_coin', 'season_voucher')),
                target_season_id TEXT NULL,
                delta INTEGER NOT NULL CHECK (delta <> 0),
                reason TEXT NOT NULL,
                reference_type TEXT NOT NULL,
                reference_id TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('pending', 'applied')),
                ledger_entry_id TEXT NULL,
                last_error TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (job_id, item_key),
                UNIQUE (account_id, idempotency_key),
                FOREIGN KEY (job_id) REFERENCES season_settlement_jobs(job_id)
            );
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('season-settlement-jobs', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
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

    private static async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SeasonSettlementJobItem item,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO season_settlement_items (
                item_id, job_id, item_key, account_id, currency,
                target_season_id, delta, reason, reference_type,
                reference_id, idempotency_key, state, created_at, updated_at)
            VALUES (
                $itemId, $jobId, $itemKey, $accountId, $currency,
                $targetSeasonId, $delta, $reason, $referenceType,
                $referenceId, $idempotencyKey, 'pending', $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$itemId", item.ItemId.ToString("D"));
        command.Parameters.AddWithValue("$jobId", item.JobId.ToString("D"));
        command.Parameters.AddWithValue("$itemKey", item.ItemKey);
        command.Parameters.AddWithValue("$accountId", item.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$currency", CurrencyToStorage(item.Currency));
        command.Parameters.AddWithValue(
            "$targetSeasonId",
            item.TargetSeasonId is Guid seasonId ? seasonId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$delta", item.Delta);
        command.Parameters.AddWithValue("$reason", item.Reason);
        command.Parameters.AddWithValue("$referenceType", item.ReferenceType);
        command.Parameters.AddWithValue("$referenceId", item.ReferenceId);
        command.Parameters.AddWithValue("$idempotencyKey", item.IdempotencyKey);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SeasonSettlementJob?> LoadAsync(
        SqliteConnection connection,
        Guid jobId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind, source_season_id, rules_version, framework_version,
                   job_key, payload_hash, state, revision, prepared_by,
                   prepared_at, started_at, completed_at, updated_at, last_error
            FROM season_settlement_jobs WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var kind = KindFromStorage(reader.GetString(0));
        var sourceSeasonId = Guid.Parse(reader.GetString(1));
        var rulesVersion = reader.GetString(2);
        var frameworkVersion = reader.GetString(3);
        var jobKey = reader.GetString(4);
        var payloadHash = reader.GetString(5);
        var state = JobStateFromStorage(reader.GetString(6));
        var revision = reader.GetInt64(7);
        var preparedBy = reader.GetString(8);
        var preparedAt = DateTimeOffset.Parse(reader.GetString(9));
        DateTimeOffset? startedAt = reader.IsDBNull(10)
            ? null
            : DateTimeOffset.Parse(reader.GetString(10));
        DateTimeOffset? completedAt = reader.IsDBNull(11)
            ? null
            : DateTimeOffset.Parse(reader.GetString(11));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(12));
        var lastError = reader.IsDBNull(13) ? null : reader.GetString(13);
        await reader.DisposeAsync();

        var items = new List<SeasonSettlementJobItem>();
        await using var itemCommand = connection.CreateCommand();
        itemCommand.Transaction = transaction;
        itemCommand.CommandText = """
            SELECT item_id, item_key, account_id, currency, target_season_id,
                   delta, reason, reference_type, reference_id, idempotency_key,
                   state, ledger_entry_id, last_error, created_at, updated_at
            FROM season_settlement_items
            WHERE job_id = $jobId ORDER BY item_key;
            """;
        itemCommand.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            items.Add(new SeasonSettlementJobItem(
                Guid.Parse(itemReader.GetString(0)),
                jobId,
                itemReader.GetString(1),
                Guid.Parse(itemReader.GetString(2)),
                CurrencyFromStorage(itemReader.GetString(3)),
                itemReader.IsDBNull(4) ? null : Guid.Parse(itemReader.GetString(4)),
                itemReader.GetInt64(5),
                itemReader.GetString(6),
                itemReader.GetString(7),
                itemReader.GetString(8),
                itemReader.GetString(9),
                ItemStateFromStorage(itemReader.GetString(10)),
                itemReader.IsDBNull(11) ? null : Guid.Parse(itemReader.GetString(11)),
                itemReader.IsDBNull(12) ? null : itemReader.GetString(12),
                DateTimeOffset.Parse(itemReader.GetString(13)),
                DateTimeOffset.Parse(itemReader.GetString(14))));
        }
        return new SeasonSettlementJob(
            jobId, kind, sourceSeasonId, rulesVersion, frameworkVersion,
            jobKey, payloadHash, state, revision, preparedBy, preparedAt,
            startedAt, completedAt, updatedAt, lastError, items);
    }

    private static void EnsureSamePayload(SeasonSettlementJob existing, string payloadHash)
    {
        if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A deterministic season settlement job was replayed with different content.");
        }
    }

    private static string KindToStorage(SeasonSettlementJobKind kind) => kind switch
    {
        SeasonSettlementJobKind.SeasonVoucherExpiry => "season_voucher_expiry",
        SeasonSettlementJobKind.Reward => "reward",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static SeasonSettlementJobKind KindFromStorage(string value) => value switch
    {
        "season_voucher_expiry" => SeasonSettlementJobKind.SeasonVoucherExpiry,
        "reward" => SeasonSettlementJobKind.Reward,
        _ => throw new InvalidDataException($"Unknown season settlement job kind '{value}'.")
    };

    private static string CurrencyToStorage(ExtractionCurrency currency) => currency switch
    {
        ExtractionCurrency.MarketCoin => "market_coin",
        ExtractionCurrency.SeasonVoucher => "season_voucher",
        _ => throw new ArgumentOutOfRangeException(nameof(currency))
    };

    private static ExtractionCurrency CurrencyFromStorage(string value) => value switch
    {
        "market_coin" => ExtractionCurrency.MarketCoin,
        "season_voucher" => ExtractionCurrency.SeasonVoucher,
        _ => throw new InvalidDataException($"Unknown settlement currency '{value}'.")
    };

    private static SeasonSettlementJobState JobStateFromStorage(string value) => value switch
    {
        "prepared" => SeasonSettlementJobState.Prepared,
        "running" => SeasonSettlementJobState.Running,
        "completed" => SeasonSettlementJobState.Completed,
        _ => throw new InvalidDataException($"Unknown season settlement job state '{value}'.")
    };

    private static SeasonSettlementItemState ItemStateFromStorage(string value) => value switch
    {
        "pending" => SeasonSettlementItemState.Pending,
        "applied" => SeasonSettlementItemState.Applied,
        _ => throw new InvalidDataException($"Unknown season settlement item state '{value}'.")
    };

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));
}

public sealed class SeasonSettlementJobService
{
    public const string FrameworkVersion = "season-settlement-v1";
    private const long MaximumWebSafeInteger = 9_007_199_254_740_991;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly IExtractionRepository _repository;
    private readonly SeasonSettlementJobStore _store;
    private readonly TimeProvider _timeProvider;

    public SeasonSettlementJobService(
        IExtractionRepository repository,
        SeasonSettlementJobStore store,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<SeasonSettlementJob> PrepareVoucherExpiryAsync(
        Guid seasonId,
        string rulesVersion,
        string actor,
        ExtractionOperationGateState gate,
        int activeOperations,
        CancellationToken cancellationToken)
    {
        EnsureMaintenance(gate, activeOperations);
        ValidateVersionAndActor(rulesVersion, actor);
        var existing = await _store.FindExpiryAsync(seasonId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RulesVersion, rulesVersion.Trim(), StringComparison.Ordinal) ||
                !string.Equals(existing.FrameworkVersion, FrameworkVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Season voucher expiry is already frozen under a different rules/framework version.");
            }
            return existing;
        }
        _ = await _repository.GetSeasonAsync(seasonId, cancellationToken)
            ?? throw new KeyNotFoundException($"Extraction season '{seasonId}' does not exist.");
        var accounts = await _repository.ListAccountsAsync(cancellationToken);
        var jobId = DeterministicGuid($"{FrameworkVersion}|expiry|{seasonId:D}");
        var now = _timeProvider.GetUtcNow();
        var items = new List<SeasonSettlementJobItem>();
        foreach (var account in accounts.OrderBy(item => item.AccountId))
        {
            var balance = (await _repository.GetWalletAsync(
                account.AccountId,
                seasonId,
                cancellationToken)).SeasonVoucher.Balance;
            if (balance <= 0)
            {
                continue;
            }
            var stable = $"{seasonId:N}:{account.AccountId:N}";
            var itemKey = Hash($"season-voucher-expiry|{stable}");
            items.Add(NewItem(
                jobId,
                itemKey,
                account.AccountId,
                ExtractionCurrency.SeasonVoucher,
                seasonId,
                -balance,
                "Weekly SeasonVoucher expiry",
                "season_voucher_expiry",
                stable,
                $"season-expiry:{stable}",
                now));
        }
        var payloadHash = PayloadHash(
            SeasonSettlementJobKind.SeasonVoucherExpiry,
            seasonId,
            rulesVersion.Trim(),
            "season-voucher-expiry",
            items);
        return await _store.PrepareAsync(
            jobId,
            SeasonSettlementJobKind.SeasonVoucherExpiry,
            seasonId,
            rulesVersion.Trim(),
            FrameworkVersion,
            "season-voucher-expiry",
            payloadHash,
            actor.Trim(),
            items,
            cancellationToken);
    }

    public async Task<SeasonSettlementJob> PrepareRewardAsync(
        Guid sourceSeasonId,
        string rulesVersion,
        string rewardBatchKey,
        IReadOnlyList<SeasonRewardGrant> grants,
        string actor,
        ExtractionOperationGateState gate,
        int activeOperations,
        CancellationToken cancellationToken)
    {
        EnsureMaintenance(gate, activeOperations);
        ValidateVersionAndActor(rulesVersion, actor);
        ValidateBounded(rewardBatchKey, 1, 64, nameof(rewardBatchKey));
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Count > 100_000)
        {
            throw new ArgumentException("A reward batch cannot exceed 100000 grants.", nameof(grants));
        }
        _ = await _repository.GetSeasonAsync(sourceSeasonId, cancellationToken)
            ?? throw new KeyNotFoundException($"Extraction season '{sourceSeasonId}' does not exist.");
        var accounts = (await _repository.ListAccountsAsync(cancellationToken))
            .Select(item => item.AccountId)
            .ToHashSet();
        var jobId = DeterministicGuid(
            $"{FrameworkVersion}|reward|{sourceSeasonId:D}|{rulesVersion.Trim()}|{rewardBatchKey.Trim()}");
        var now = _timeProvider.GetUtcNow();
        var items = new List<SeasonSettlementJobItem>(grants.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            ValidateRewardGrant(grant, accounts);
            var target = grant.TargetSeasonId?.ToString("N") ?? "permanent";
            var stable = string.Join(':',
                sourceSeasonId.ToString("N"),
                grant.AccountId.ToString("N"),
                grant.Currency,
                target,
                grant.RewardKey.Trim());
            var itemKey = Hash($"season-reward|{stable}");
            if (!keys.Add(itemKey))
            {
                throw new ArgumentException("The reward batch contains a duplicate account/reward target.", nameof(grants));
            }
            var shortKey = Hash(grant.RewardKey.Trim())[..24];
            var reference = $"{sourceSeasonId:N}:{grant.AccountId:N}:{shortKey}";
            items.Add(NewItem(
                jobId,
                itemKey,
                grant.AccountId,
                grant.Currency,
                grant.TargetSeasonId,
                grant.Amount,
                $"Weekly reward {grant.RewardKey.Trim()}",
                "season_reward",
                reference,
                $"season-reward:{reference}",
                now));
        }
        items.Sort((left, right) => string.CompareOrdinal(left.ItemKey, right.ItemKey));
        var payloadHash = PayloadHash(
            SeasonSettlementJobKind.Reward,
            sourceSeasonId,
            rulesVersion.Trim(),
            rewardBatchKey.Trim(),
            items);
        return await _store.PrepareAsync(
            jobId,
            SeasonSettlementJobKind.Reward,
            sourceSeasonId,
            rulesVersion.Trim(),
            FrameworkVersion,
            rewardBatchKey.Trim(),
            payloadHash,
            actor.Trim(),
            items,
            cancellationToken);
    }

    public Task<SeasonSettlementJob?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        _store.GetAsync(jobId, cancellationToken);

    public async Task<SeasonSettlementJob> RunAsync(
        Guid jobId,
        ExtractionOperationGateState gate,
        int activeOperations,
        CancellationToken cancellationToken)
    {
        EnsureMaintenance(gate, activeOperations);
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            var job = await _store.MarkRunningAsync(jobId, cancellationToken);
            if (job.State == SeasonSettlementJobState.Completed)
            {
                return job;
            }
            foreach (var item in job.Items.Where(item => item.State == SeasonSettlementItemState.Pending))
            {
                WalletAdjustmentResult result;
                try
                {
                    result = await _repository.AdjustWalletAsync(
                        new WalletAdjustmentRequest(
                            item.AccountId,
                            item.TargetSeasonId,
                            item.Currency,
                            item.Delta,
                            item.Reason,
                            item.ReferenceType,
                            item.ReferenceId,
                            $"season-job:{job.JobId:N}",
                            item.IdempotencyKey),
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await _store.MarkItemErrorAsync(job.JobId, item.ItemId, exception.Message, cancellationToken);
                    throw;
                }
                if (result.IdempotencyConflict || result.LedgerEntry is null || result.ErrorCode is not null)
                {
                    var error = result.ErrorCode ?? "SEASON_JOB_LEDGER_MISSING";
                    await _store.MarkItemErrorAsync(job.JobId, item.ItemId, error, cancellationToken);
                    throw new InvalidOperationException(
                        $"Season settlement item '{item.ItemId}' failed: {error}.");
                }
                await _store.MarkItemAppliedAsync(
                    job.JobId,
                    item.ItemId,
                    result.LedgerEntry.EntryId,
                    cancellationToken);
            }
            return await _store.CompleteIfDrainedAsync(job.JobId, cancellationToken);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static SeasonSettlementJobItem NewItem(
        Guid jobId,
        string itemKey,
        Guid accountId,
        ExtractionCurrency currency,
        Guid? targetSeasonId,
        long delta,
        string reason,
        string referenceType,
        string referenceId,
        string idempotencyKey,
        DateTimeOffset now) => new(
            DeterministicGuid($"{jobId:D}|{itemKey}"),
            jobId,
            itemKey,
            accountId,
            currency,
            targetSeasonId,
            delta,
            reason,
            referenceType,
            referenceId,
            idempotencyKey,
            SeasonSettlementItemState.Pending,
            null,
            null,
            now,
            now);

    private static string PayloadHash(
        SeasonSettlementJobKind kind,
        Guid seasonId,
        string rulesVersion,
        string jobKey,
        IEnumerable<SeasonSettlementJobItem> items)
    {
        var text = new StringBuilder()
            .Append(FrameworkVersion).Append('|').Append(kind).Append('|')
            .Append(seasonId.ToString("D")).Append('|').Append(rulesVersion).Append('|')
            .Append(jobKey).Append('\n');
        foreach (var item in items.OrderBy(item => item.ItemKey, StringComparer.Ordinal))
        {
            text.Append(item.ItemKey).Append('|').Append(item.AccountId.ToString("D")).Append('|')
                .Append(item.Currency).Append('|').Append(item.TargetSeasonId?.ToString("D") ?? "-")
                .Append('|').Append(item.Delta).Append('|').Append(item.ReferenceId).Append('\n');
        }
        return Hash(text.ToString());
    }

    private static void EnsureMaintenance(ExtractionOperationGateState gate, int activeOperations)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (!gate.Maintenance || activeOperations != 0)
        {
            throw new InvalidOperationException(
                "Season settlement jobs require maintenance mode and zero active economy operations.");
        }
    }

    private static void ValidateVersionAndActor(string rulesVersion, string actor)
    {
        ValidateBounded(rulesVersion, 1, 128, nameof(rulesVersion));
        ValidateBounded(actor, 1, 128, nameof(actor));
    }

    private static void ValidateRewardGrant(SeasonRewardGrant grant, IReadOnlySet<Guid> accounts)
    {
        if (grant.AccountId == Guid.Empty || !accounts.Contains(grant.AccountId))
        {
            throw new ArgumentException("A reward grant references an unknown account.", nameof(grant));
        }
        if (grant.Amount is <= 0 or > MaximumWebSafeInteger)
        {
            throw new ArgumentException("A reward amount must be a positive web-safe integer.", nameof(grant));
        }
        ValidateBounded(grant.RewardKey, 1, 64, nameof(grant.RewardKey));
        if (grant.Currency == ExtractionCurrency.MarketCoin && grant.TargetSeasonId is not null)
        {
            throw new ArgumentException("Permanent MarketCoin rewards cannot target a season.", nameof(grant));
        }
        if (grant.Currency == ExtractionCurrency.SeasonVoucher && grant.TargetSeasonId is null)
        {
            throw new ArgumentException("SeasonVoucher rewards require a target season.", nameof(grant));
        }
    }

    private static void ValidateBounded(string value, int minimum, int maximum, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var trimmed = value.Trim();
        if (trimmed.Length < minimum || trimmed.Length > maximum || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} must contain {minimum}-{maximum} non-control characters.", name);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}

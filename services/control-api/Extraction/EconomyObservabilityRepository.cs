using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PalControl.ControlApi.Extraction;

public sealed record EconomyLatencyMetric(
    int SampleCount,
    double AverageSeconds,
    double MaximumSeconds,
    double P95Seconds);

public sealed record EconomyStateMetric(
    int Count,
    EconomyLatencyMetric Latency);

public sealed record EconomyRepositoryObservability(
    IReadOnlyDictionary<string, EconomyStateMetric> Orders,
    IReadOnlyDictionary<string, EconomyStateMetric> ResourceSettlements,
    IReadOnlyDictionary<string, EconomyStateMetric> Deliveries,
    int DeliveryBacklogCount,
    double? OldestDeliveryBacklogAgeSeconds,
    int DeliveryReceiptUncertainCount,
    int DeliveryReceiptPartialCount,
    int LedgerStreamCount,
    int LedgerInvariantMismatchCount,
    int SettlementCreditMismatchCount,
    int IdentityStructuralConflictCount,
    int IdentityConflictCount,
    int RecentIdentityConflictCount);

public sealed partial class SqliteExtractionRepository
{
    public async Task<EconomyRepositoryObservability> GetEconomyObservabilityAsync(
        DateTimeOffset observedAt,
        TimeSpan identityConflictWindow,
        CancellationToken cancellationToken)
    {
        if (identityConflictWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(identityConflictWindow));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var runs = ReadAllSettlementRuns(connection);
            var orderMetrics = BuildStateMetrics(
                Enum.GetValues<ShopOrderState>(),
                _orders.Values,
                order => order.State,
                order => order.CreatedAt,
                order => order.State is
                    ShopOrderState.PendingDelivery or ShopOrderState.Dispatching
                    ? observedAt
                    : order.UpdatedAt,
                observedAt);
            var runMetrics = BuildStateMetrics(
                Enum.GetValues<Infrastructure.ExtractionSettlementState>(),
                runs,
                run => run.State,
                run => run.QuotedAt,
                run => run.State is
                    Infrastructure.ExtractionSettlementState.Quoted or
                    Infrastructure.ExtractionSettlementState.Consuming or
                    Infrastructure.ExtractionSettlementState.Removed or
                    Infrastructure.ExtractionSettlementState.Credited
                    ? observedAt
                    : run.SettledAt ?? run.UpdatedAt,
                observedAt);
            var deliveryMetrics = BuildStateMetrics(
                Enum.GetValues<ShopDeliveryState>(),
                _deliveries.Values,
                delivery => delivery.State,
                delivery => delivery.CreatedAt,
                delivery => delivery.State is
                    ShopDeliveryState.Pending or ShopDeliveryState.Dispatching
                    ? observedAt
                    : delivery.CompletedAt ?? observedAt,
                observedAt);

            var deliveryBacklog = _orders.Values
                .Where(order => order.State is
                    ShopOrderState.PendingDelivery or
                    ShopOrderState.Dispatching or
                    ShopOrderState.DeliveryFailed or
                    ShopOrderState.DeliveryUncertain)
                .ToArray();
            var oldestBacklogAge = deliveryBacklog.Length == 0
                ? (double?)null
                : AgeSeconds(deliveryBacklog.Min(order => order.CreatedAt), observedAt);

            var (receiptUncertain, receiptPartial) = await ReadReceiptOutcomesAsync(
                connection,
                cancellationToken);
            var (ledgerStreams, ledgerMismatches) = ValidateLedgerProjection();
            var settlementCreditMismatches = await ValidateSettlementCreditsAsync(
                connection,
                runs,
                cancellationToken);
            var structuralConflicts = await CountIdentityStructuralConflictsAsync(
                connection,
                cancellationToken);
            var lifetimeConflicts = await CountScalarAsync(
                connection,
                "SELECT COUNT(*) FROM economy_identity_conflicts;",
                null,
                cancellationToken);
            var recentConflicts = await CountScalarAsync(
                connection,
                "SELECT COUNT(*) FROM economy_identity_conflicts WHERE occurred_at >= $since;",
                command => command.Parameters.AddWithValue(
                    "$since",
                    observedAt.Subtract(identityConflictWindow).ToString("O")),
                cancellationToken);

            return new EconomyRepositoryObservability(
                orderMetrics,
                runMetrics,
                deliveryMetrics,
                deliveryBacklog.Length,
                oldestBacklogAge,
                receiptUncertain,
                receiptPartial,
                ledgerStreams,
                ledgerMismatches,
                settlementCreditMismatches,
                structuralConflicts,
                lifetimeConflicts,
                recentConflicts);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void InitializeEconomyObservabilityDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS economy_schema_migrations (
                component TEXT NOT NULL,
                version INTEGER NOT NULL,
                applied_at TEXT NOT NULL,
                PRIMARY KEY (component, version)
            );
            CREATE TABLE IF NOT EXISTS economy_identity_conflicts (
                conflict_id TEXT PRIMARY KEY,
                error_code TEXT NOT NULL,
                subject_fingerprint TEXT NOT NULL CHECK (length(subject_fingerprint) = 64),
                account_fingerprint TEXT NOT NULL CHECK (length(account_fingerprint) = 64),
                world_fingerprint TEXT NOT NULL CHECK (length(world_fingerprint) = 64),
                player_uid_fingerprint TEXT NOT NULL CHECK (length(player_uid_fingerprint) = 64),
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_economy_identity_conflicts_occurred_at
                ON economy_identity_conflicts (occurred_at);
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('economy-observability', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private async Task RecordIdentityConflictAsync(
        string errorCode,
        string subject,
        Guid accountId,
        string worldId,
        string playerUid,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertIdentityConflictAsync(
            connection,
            transaction,
            errorCode,
            subject,
            accountId,
            worldId,
            playerUid,
            cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task InsertIdentityConflictAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string errorCode,
        string subject,
        Guid accountId,
        string worldId,
        string playerUid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO economy_identity_conflicts (
                conflict_id, error_code, subject_fingerprint, account_fingerprint,
                world_fingerprint, player_uid_fingerprint, occurred_at)
            VALUES (
                $conflictId, $errorCode, $subjectFingerprint, $accountFingerprint,
                $worldFingerprint, $playerUidFingerprint, $occurredAt);
            """;
        command.Parameters.AddWithValue("$conflictId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$errorCode", errorCode);
        command.Parameters.AddWithValue("$subjectFingerprint", Fingerprint(subject));
        command.Parameters.AddWithValue("$accountFingerprint", Fingerprint(accountId.ToString("D")));
        command.Parameters.AddWithValue("$worldFingerprint", Fingerprint(worldId));
        command.Parameters.AddWithValue("$playerUidFingerprint", Fingerprint(playerUid));
        command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private (int Streams, int Mismatches) ValidateLedgerProjection()
    {
        var streams = _balances.Values
            .Select(balance => new LedgerStreamKey(
                balance.AccountId,
                balance.Currency,
                balance.SeasonId))
            .Concat(_ledger.Values.Select(entry => new LedgerStreamKey(
                entry.AccountId,
                entry.Currency,
                entry.SeasonId)))
            .Distinct()
            .ToArray();
        var mismatches = 0;
        foreach (var stream in streams)
        {
            var running = 0L;
            var invalid = false;
            foreach (var entry in _ledger.Values.Where(entry =>
                         entry.AccountId == stream.AccountId &&
                         entry.Currency == stream.Currency &&
                         entry.SeasonId == stream.SeasonId))
            {
                try
                {
                    running = checked(running + entry.Delta);
                }
                catch (OverflowException)
                {
                    invalid = true;
                    break;
                }
            }

            var stored = _balances.TryGetValue(
                BalanceKey(stream.AccountId, stream.Currency, stream.SeasonId),
                out var balance)
                ? balance.Balance
                : 0L;
            if (invalid || running != stored)
            {
                mismatches++;
            }
        }
        return (streams.Length, mismatches);
    }

    private async Task<int> ValidateSettlementCreditsAsync(
        SqliteConnection connection,
        IReadOnlyList<Infrastructure.ExtractionSettlementRun> runs,
        CancellationToken cancellationToken)
    {
        var credits = new Dictionary<Guid, StoredRunCredit>();
        var malformedCredits = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT run_id, account_id, season_id, ledger_entry_id, amount
                FROM extraction_run_credits;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!Guid.TryParse(reader.GetString(0), out var runId) ||
                    !Guid.TryParse(reader.GetString(1), out var accountId) ||
                    !Guid.TryParse(reader.GetString(2), out var seasonId) ||
                    !Guid.TryParse(reader.GetString(3), out var ledgerEntryId))
                {
                    malformedCredits++;
                    continue;
                }
                credits[runId] = new StoredRunCredit(
                    accountId,
                    seasonId,
                    ledgerEntryId,
                    reader.GetInt64(4));
            }
        }

        var mismatches = 0;
        foreach (var run in runs)
        {
            var shouldHaveCredit = run.State is
                Infrastructure.ExtractionSettlementState.Credited or
                Infrastructure.ExtractionSettlementState.Settled;
            if (!credits.TryGetValue(run.RunId, out var credit))
            {
                if (shouldHaveCredit)
                {
                    mismatches++;
                }
                continue;
            }
            if (!shouldHaveCredit ||
                credit.AccountId != run.AccountId ||
                credit.SeasonId != run.SeasonId ||
                credit.Amount != run.TotalValue ||
                !_ledger.TryGetValue(credit.LedgerEntryId, out var ledger) ||
                ledger.AccountId != credit.AccountId ||
                ledger.SeasonId != credit.SeasonId ||
                ledger.Currency != ExtractionCurrency.SeasonVoucher ||
                ledger.Delta != credit.Amount ||
                !string.Equals(ledger.ReferenceType, "extraction_run", StringComparison.Ordinal) ||
                !string.Equals(ledger.ReferenceId, run.RunId.ToString("N"), StringComparison.Ordinal))
            {
                mismatches++;
            }
            credits.Remove(run.RunId);
        }
        return checked(mismatches + credits.Count + malformedCredits);
    }

    private static async Task<(int Uncertain, int Partial)> ReadReceiptOutcomesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(
                connection,
                "extraction_delivery_receipts",
                cancellationToken))
        {
            return (0, 0);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN lower(json_extract(receipt_json, '$.outcome')) = 'uncertain' THEN 1 ELSE 0 END),
                SUM(CASE WHEN lower(json_extract(receipt_json, '$.outcome')) = 'partial' THEN 1 ELSE 0 END)
            FROM extraction_delivery_receipts
            WHERE receipt_json IS NOT NULL;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }
        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM sqlite_master
                WHERE type = 'table' AND name = $tableName);
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<int> CountIdentityStructuralConflictsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE((SELECT SUM(count_value - 1) FROM (
                    SELECT COUNT(*) AS count_value
                    FROM player_identity_bindings
                    GROUP BY season_id, account_id
                    HAVING COUNT(*) > 1)), 0) +
                COALESCE((SELECT SUM(count_value - 1) FROM (
                    SELECT COUNT(*) AS count_value
                    FROM player_identity_bindings
                    GROUP BY season_id, platform_subject
                    HAVING COUNT(*) > 1)), 0) +
                COALESCE((SELECT SUM(count_value - 1) FROM (
                    SELECT COUNT(*) AS count_value
                    FROM player_identity_bindings
                    GROUP BY world_id, player_uid
                    HAVING COUNT(*) > 1)), 0) +
                COALESCE((SELECT COUNT(*)
                    FROM player_identity_binding_history history
                    LEFT JOIN player_identity_bindings binding
                      ON binding.binding_id = history.binding_id
                    WHERE binding.binding_id IS NULL), 0);
            """;
        return await CountScalarAsync(connection, sql, null, cancellationToken);
    }

    private static async Task<int> CountScalarAsync(
        SqliteConnection connection,
        string sql,
        Action<SqliteCommand>? bind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static IReadOnlyDictionary<string, EconomyStateMetric> BuildStateMetrics<
        TState,
        TItem>(
        IReadOnlyList<TState> states,
        IEnumerable<TItem> source,
        Func<TItem, TState> stateSelector,
        Func<TItem, DateTimeOffset> startedAtSelector,
        Func<TItem, DateTimeOffset> endedAtSelector,
        DateTimeOffset observedAt)
        where TState : struct, Enum
    {
        var items = source.ToArray();
        return states.ToDictionary(
            state => state.ToString().ToLowerInvariant(),
            state =>
            {
                var matching = items.Where(item =>
                    EqualityComparer<TState>.Default.Equals(stateSelector(item), state)).ToArray();
                var latencies = matching
                    .Select(item =>
                    {
                        var endedAt = endedAtSelector(item);
                        var effectiveEnd = endedAt <= observedAt ? endedAt : observedAt;
                        return Math.Max(
                            0d,
                            (effectiveEnd - startedAtSelector(item)).TotalSeconds);
                    })
                    .OrderBy(value => value)
                    .ToArray();
                return new EconomyStateMetric(
                    matching.Length,
                    ToLatency(latencies));
            },
            StringComparer.Ordinal);
    }

    private static EconomyLatencyMetric ToLatency(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return new EconomyLatencyMetric(0, 0, 0, 0);
        }
        var p95Index = Math.Clamp(
            (int)Math.Ceiling(values.Count * 0.95d) - 1,
            0,
            values.Count - 1);
        return new EconomyLatencyMetric(
            values.Count,
            values.Average(),
            values[^1],
            values[p95Index]);
    }

    private static double AgeSeconds(DateTimeOffset createdAt, DateTimeOffset observedAt) =>
        Math.Max(0d, (observedAt - createdAt).TotalSeconds);

    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));

    private sealed record LedgerStreamKey(
        Guid AccountId,
        ExtractionCurrency Currency,
        Guid? SeasonId);

    private sealed record StoredRunCredit(
        Guid AccountId,
        Guid SeasonId,
        Guid LedgerEntryId,
        long Amount);
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed record ExtractionDeliveryEvidence(
    Guid DeliveryId,
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, long> BaselineItemTotals,
    Guid? CommandId = null,
    DateTimeOffset? VerifiedAt = null,
    IReadOnlyDictionary<string, long>? VerifiedItemTotals = null);

/// <summary>
/// Authoritative delivery readback evidence stored in extraction-commerce.db.
/// The former whole-file JSON store is accepted once as a migration source and
/// is never read as authority after the migration marker commits.
/// </summary>
public sealed class ExtractionDeliveryEvidenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private readonly string _legacyPath;
    private readonly TimeProvider _timeProvider;

    public ExtractionDeliveryEvidenceStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath), timeProvider)
    {
    }

    public ExtractionDeliveryEvidenceStore(string dataDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _legacyPath = Path.Combine(directory, "delivery-inventory-evidence.json");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Initialize();
        ImportLegacyOnce();
    }

    public async Task<ExtractionDeliveryEvidence?> GetAsync(
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty)
        {
            throw new ArgumentException("Delivery id cannot be empty.", nameof(deliveryId));
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            return await LoadAsync(connection, deliveryId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionDeliveryEvidence> ReplaceBaselineAsync(
        Guid deliveryId,
        IReadOnlyDictionary<string, long> baselineItemTotals,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty)
        {
            throw new ArgumentException("Delivery id cannot be empty.", nameof(deliveryId));
        }
        var normalized = NormalizeTotals(baselineItemTotals, nameof(baselineItemTotals));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var existing = await LoadAsync(
                connection, deliveryId, cancellationToken, (SqliteTransaction)transaction);
            if (existing?.CommandId is not null)
            {
                throw new InvalidOperationException(
                    "A delivery baseline cannot be replaced after a command has been attached.");
            }
            var evidence = new ExtractionDeliveryEvidence(
                deliveryId,
                _timeProvider.GetUtcNow(),
                normalized);
            await UpsertAsync(
                connection, (SqliteTransaction)transaction, evidence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Clone(evidence);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionDeliveryEvidence> AttachCommandAsync(
        Guid deliveryId,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty || commandId == Guid.Empty)
        {
            throw new ArgumentException("Delivery and command ids must be non-empty.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var existing = await LoadAsync(
                connection, deliveryId, cancellationToken, (SqliteTransaction)transaction)
                ?? throw new InvalidOperationException(
                    "A delivery command cannot be attached without a persisted inventory baseline.");
            if (existing.CommandId == commandId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Clone(existing);
            }
            if (existing.CommandId is not null)
            {
                throw new InvalidOperationException(
                    "The delivery evidence is already attached to another command.");
            }
            var updated = existing with { CommandId = commandId };
            await UpsertAsync(
                connection, (SqliteTransaction)transaction, updated, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Clone(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionDeliveryEvidence> SaveVerificationAsync(
        Guid deliveryId,
        Guid commandId,
        IReadOnlyDictionary<string, long> verifiedItemTotals,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty || commandId == Guid.Empty)
        {
            throw new ArgumentException("Delivery and command ids must be non-empty.");
        }
        var normalized = NormalizeTotals(verifiedItemTotals, nameof(verifiedItemTotals));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var existing = await LoadAsync(
                connection, deliveryId, cancellationToken, (SqliteTransaction)transaction);
            if (existing?.CommandId != commandId)
            {
                throw new InvalidOperationException(
                    "A delivery readback can only be saved for its attached command.");
            }
            if (existing.VerifiedAt is not null)
            {
                if (TotalsEqual(existing.VerifiedItemTotals!, normalized))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Clone(existing);
                }
            }
            var updated = existing with
            {
                VerifiedAt = _timeProvider.GetUtcNow(),
                VerifiedItemTotals = normalized
            };
            await UpsertAsync(
                connection, (SqliteTransaction)transaction, updated, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Clone(updated);
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
            CREATE TABLE IF NOT EXISTS extraction_delivery_evidence (
                delivery_id TEXT PRIMARY KEY,
                captured_at TEXT NOT NULL,
                baseline_item_totals TEXT NOT NULL CHECK (json_valid(baseline_item_totals)),
                command_id TEXT NULL UNIQUE,
                verified_at TEXT NULL,
                verified_item_totals TEXT NULL
                    CHECK (verified_item_totals IS NULL OR json_valid(verified_item_totals)),
                revision INTEGER NOT NULL CHECK (revision > 0),
                CHECK ((verified_at IS NULL) = (verified_item_totals IS NULL)),
                CHECK (verified_at IS NULL OR command_id IS NOT NULL)
            );
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('delivery-evidence', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
    }

    private void ImportLegacyOnce()
    {
        using var connection = Open();
        using (var marker = connection.CreateCommand())
        {
            marker.CommandText = """
                SELECT 1 FROM economy_schema_migrations
                WHERE component = 'delivery-evidence-legacy-json' AND version = 1;
                """;
            if (marker.ExecuteScalar() is not null)
            {
                return;
            }
        }
        ExtractionDeliveryEvidence[] legacy = [];
        if (File.Exists(_legacyPath))
        {
            legacy = JsonSerializer.Deserialize<ExtractionDeliveryEvidence[]>(
                File.ReadAllBytes(_legacyPath), JsonOptions)
                ?? throw new InvalidDataException("The legacy delivery evidence store is invalid.");
        }
        var ids = new HashSet<Guid>();
        foreach (var item in legacy)
        {
            if (item.DeliveryId == Guid.Empty || !ids.Add(item.DeliveryId))
            {
                throw new InvalidDataException(
                    "The legacy delivery evidence store contains duplicate or invalid ids.");
            }
            _ = NormalizeTotals(item.BaselineItemTotals, nameof(item.BaselineItemTotals));
            if (item.VerifiedAt is not null)
            {
                if (item.CommandId is null || item.VerifiedItemTotals is null)
                {
                    throw new InvalidDataException(
                        "Legacy verified evidence is missing its command or inventory totals.");
                }
                _ = NormalizeTotals(item.VerifiedItemTotals, nameof(item.VerifiedItemTotals));
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var item in legacy.OrderBy(item => item.CapturedAt))
        {
            using var existingCommand = connection.CreateCommand();
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = """
                SELECT captured_at, baseline_item_totals, command_id,
                       verified_at, verified_item_totals
                FROM extraction_delivery_evidence WHERE delivery_id = $deliveryId;
                """;
            existingCommand.Parameters.AddWithValue("$deliveryId", item.DeliveryId.ToString("D"));
            using var reader = existingCommand.ExecuteReader();
            if (reader.Read())
            {
                var existing = Read(reader, item.DeliveryId);
                if (!EvidenceEqual(existing, item))
                {
                    throw new InvalidDataException(
                        $"Legacy delivery evidence '{item.DeliveryId}' conflicts with SQLite authority.");
                }
                continue;
            }
            reader.Close();
            Upsert(connection, transaction, item);
        }
        using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = """
                INSERT INTO economy_schema_migrations (component, version, applied_at)
                VALUES ('delivery-evidence-legacy-json', 1, $appliedAt);
                """;
            marker.Parameters.AddWithValue("$appliedAt", _timeProvider.GetUtcNow().ToString("O"));
            marker.ExecuteNonQuery();
        }
        transaction.Commit();
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

    private static async Task<ExtractionDeliveryEvidence?> LoadAsync(
        SqliteConnection connection,
        Guid deliveryId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT captured_at, baseline_item_totals, command_id,
                   verified_at, verified_item_totals
            FROM extraction_delivery_evidence WHERE delivery_id = $deliveryId;
            """;
        command.Parameters.AddWithValue("$deliveryId", deliveryId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader, deliveryId) : null;
    }

    private static ExtractionDeliveryEvidence Read(SqliteDataReader reader, Guid deliveryId) => new(
        deliveryId,
        DateTimeOffset.Parse(reader.GetString(0)),
        DeserializeTotals(reader.GetString(1)),
        reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
        reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : DeserializeTotals(reader.GetString(4)));

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExtractionDeliveryEvidence evidence,
        CancellationToken cancellationToken)
    {
        await using var command = CreateUpsert(connection, transaction, evidence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Upsert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExtractionDeliveryEvidence evidence)
    {
        using var command = CreateUpsert(connection, transaction, evidence);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand CreateUpsert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExtractionDeliveryEvidence evidence)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO extraction_delivery_evidence (
                delivery_id, captured_at, baseline_item_totals, command_id,
                verified_at, verified_item_totals, revision)
            VALUES (
                $deliveryId, $capturedAt, $baseline, $commandId,
                $verifiedAt, $verified, 1)
            ON CONFLICT(delivery_id) DO UPDATE SET
                captured_at = excluded.captured_at,
                baseline_item_totals = excluded.baseline_item_totals,
                command_id = excluded.command_id,
                verified_at = excluded.verified_at,
                verified_item_totals = excluded.verified_item_totals,
                revision = extraction_delivery_evidence.revision + 1;
            """;
        command.Parameters.AddWithValue("$deliveryId", evidence.DeliveryId.ToString("D"));
        command.Parameters.AddWithValue("$capturedAt", evidence.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$baseline", SerializeTotals(evidence.BaselineItemTotals));
        command.Parameters.AddWithValue(
            "$commandId", evidence.CommandId is Guid commandId ? commandId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue(
            "$verifiedAt", evidence.VerifiedAt is DateTimeOffset at ? at.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue(
            "$verified",
            evidence.VerifiedItemTotals is null
                ? DBNull.Value
                : SerializeTotals(evidence.VerifiedItemTotals));
        return command;
    }

    private static IReadOnlyDictionary<string, long> NormalizeTotals(
        IReadOnlyDictionary<string, long> totals,
        string name)
    {
        ArgumentNullException.ThrowIfNull(totals, name);
        if (totals.Count > 10_000)
        {
            throw new ArgumentException("Inventory evidence cannot exceed 10000 item ids.", name);
        }
        var normalized = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in totals)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Trim().Length > 128 ||
                pair.Key.Any(char.IsControl) || pair.Value < 0)
            {
                throw new ArgumentException(
                    "Inventory evidence requires bounded item ids and non-negative totals.", name);
            }
            if (!normalized.TryAdd(pair.Key.Trim(), pair.Value))
            {
                throw new ArgumentException("Inventory evidence contains duplicate item ids.", name);
            }
        }
        return normalized;
    }

    private static string SerializeTotals(IReadOnlyDictionary<string, long> totals) =>
        JsonSerializer.Serialize(NormalizeTotals(totals, "totals"), JsonOptions);

    private static IReadOnlyDictionary<string, long> DeserializeTotals(string json) =>
        NormalizeTotals(
            JsonSerializer.Deserialize<Dictionary<string, long>>(json, JsonOptions)
                ?? throw new InvalidDataException("Delivery inventory evidence JSON is invalid."),
            "totals");

    private static bool TotalsEqual(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool EvidenceEqual(
        ExtractionDeliveryEvidence left,
        ExtractionDeliveryEvidence right) =>
        left.DeliveryId == right.DeliveryId &&
        left.CapturedAt.ToUniversalTime() == right.CapturedAt.ToUniversalTime() &&
        left.CommandId == right.CommandId &&
        left.VerifiedAt?.ToUniversalTime() == right.VerifiedAt?.ToUniversalTime() &&
        TotalsEqual(left.BaselineItemTotals, right.BaselineItemTotals) &&
        ((left.VerifiedItemTotals is null && right.VerifiedItemTotals is null) ||
         (left.VerifiedItemTotals is not null && right.VerifiedItemTotals is not null &&
          TotalsEqual(left.VerifiedItemTotals, right.VerifiedItemTotals)));

    private static ExtractionDeliveryEvidence Clone(ExtractionDeliveryEvidence evidence) =>
        evidence with
        {
            BaselineItemTotals = new Dictionary<string, long>(
                evidence.BaselineItemTotals, StringComparer.OrdinalIgnoreCase),
            VerifiedItemTotals = evidence.VerifiedItemTotals is null
                ? null
                : new Dictionary<string, long>(
                    evidence.VerifiedItemTotals, StringComparer.OrdinalIgnoreCase)
        };

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));
}

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class ExtractionDeliveryReceiptContract
{
    public const int SchemaVersion = 1;
    public const string CommandVersion = "paldefender-give-items-single-v1";
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtractionDeliveryReceiptOutcome>))]
public enum ExtractionDeliveryReceiptOutcome
{
    Succeeded,
    Failed,
    Partial,
    Uncertain
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtractionDeliveryReceiptItemResult>))]
public enum ExtractionDeliveryReceiptItemResult
{
    Succeeded,
    FailedBeforeMutation,
    Partial,
    Uncertain,
    InvalidReceipt,
    CommandRecordMissing
}

public sealed record ExtractionDeliveryReceiptItem(
    string ItemId,
    int Requested,
    int? Granted,
    Guid? CommandId,
    ExtractionDeliveryReceiptItemResult Result,
    DateTimeOffset? CompletedAt);

public sealed record ExtractionDeliveryReceiptV1(
    int SchemaVersion,
    Guid DeliveryId,
    string IdempotencyKey,
    string RequestHash,
    Guid ResultId,
    string ServerId,
    string PlayerUid,
    string WorldId,
    string GameVersion,
    string AdapterVersion,
    string CommandVersion,
    DateTimeOffset? AcknowledgedAt,
    IReadOnlyList<ExtractionDeliveryReceiptItem> Items,
    ExtractionDeliveryReceiptOutcome Outcome,
    DateTimeOffset CreatedAt);

public sealed record ExtractionDeliveryReceiptRequest(
    Guid DeliveryId,
    string IdempotencyKey,
    string RequestHash,
    Guid ResultId,
    string ServerId,
    string PlayerUid,
    string WorldId,
    string GameVersion,
    string AdapterVersion,
    string CommandVersion,
    IReadOnlyList<ShopItemGrant> Items,
    DateTimeOffset CreatedAt);

public sealed record ExtractionDeliveryReceiptRegistration(
    ExtractionDeliveryReceiptRequest Request,
    ExtractionDeliveryReceiptV1? Receipt,
    bool Created,
    bool IdempotencyConflict);

/// <summary>
/// Stores the immutable request envelope and final v1 receipt in the extraction
/// SQLite database. A request is inserted before any game command is accepted.
/// Once receipt_json is populated it can only be replayed byte-for-byte.
/// </summary>
public sealed class ExtractionDeliveryReceiptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public ExtractionDeliveryReceiptStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath), timeProvider)
    {
    }

    public ExtractionDeliveryReceiptStore(
        string dataDirectory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Initialize();
    }

    public async Task<ExtractionDeliveryReceiptRegistration> RegisterAsync(
        ShopDeliveryWorkItem delivery,
        string gameVersion,
        string adapterVersion,
        CancellationToken cancellationToken)
    {
        var candidate = CreateRequest(
            delivery,
            gameVersion,
            adapterVersion,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow());
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var matches = await LoadMatchesAsync(
                connection,
                (SqliteTransaction)transaction,
                candidate.DeliveryId,
                candidate.IdempotencyKey,
                cancellationToken);
            if (matches.Count > 1)
            {
                throw new InvalidDataException(
                    "Delivery receipt id and idempotency key resolve to different persisted requests.");
            }
            if (matches.Count == 1)
            {
                var existing = matches[0];
                await transaction.RollbackAsync(cancellationToken);
                return new ExtractionDeliveryReceiptRegistration(
                    Clone(existing.Request),
                    existing.Receipt is null ? null : Clone(existing.Receipt),
                    Created: false,
                    IdempotencyConflict: !string.Equals(
                        existing.Request.RequestHash,
                        candidate.RequestHash,
                        StringComparison.Ordinal));
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO extraction_delivery_receipts (
                    delivery_id, idempotency_key, request_hash, result_id,
                    server_id, player_uid, world_id, game_version,
                    adapter_version, command_version, requested_items,
                    created_at, receipt_json, finalized_at)
                VALUES (
                    $deliveryId, $idempotencyKey, $requestHash, $resultId,
                    $serverId, $playerUid, $worldId, $gameVersion,
                    $adapterVersion, $commandVersion, $requestedItems,
                    $createdAt, NULL, NULL);
                """;
            AddRequestParameters(command, candidate);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ExtractionDeliveryReceiptRegistration(
                Clone(candidate),
                null,
                Created: true,
                IdempotencyConflict: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionDeliveryReceiptRegistration?> GetAsync(
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
            var matches = await LoadMatchesAsync(
                connection,
                null,
                deliveryId,
                idempotencyKey: null,
                cancellationToken);
            if (matches.Count == 0)
            {
                return null;
            }
            if (matches.Count != 1)
            {
                throw new InvalidDataException("A delivery id resolves to multiple receipt requests.");
            }
            var item = matches[0];
            return new ExtractionDeliveryReceiptRegistration(
                Clone(item.Request),
                item.Receipt is null ? null : Clone(item.Receipt),
                Created: false,
                IdempotencyConflict: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionDeliveryReceiptV1> SaveReceiptAsync(
        ExtractionDeliveryReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        ValidateReceipt(receipt);
        var serialized = JsonSerializer.Serialize(receipt, JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var matches = await LoadMatchesAsync(
                connection,
                (SqliteTransaction)transaction,
                receipt.DeliveryId,
                receipt.IdempotencyKey,
                cancellationToken);
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    "A final delivery receipt requires one registered immutable request.");
            }
            var existing = matches[0];
            EnsureReceiptMatchesRequest(receipt, existing.Request);
            if (existing.Receipt is not null)
            {
                var existingSerialized = JsonSerializer.Serialize(existing.Receipt, JsonOptions);
                if (!string.Equals(existingSerialized, serialized, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A final delivery receipt cannot be replaced with different evidence.");
                }
                await transaction.RollbackAsync(cancellationToken);
                return Clone(existing.Receipt);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE extraction_delivery_receipts
                SET receipt_json = $receipt, finalized_at = $finalizedAt
                WHERE delivery_id = $deliveryId AND receipt_json IS NULL;
                """;
            command.Parameters.AddWithValue("$receipt", serialized);
            command.Parameters.AddWithValue("$finalizedAt", _timeProvider.GetUtcNow().ToString("O"));
            command.Parameters.AddWithValue("$deliveryId", receipt.DeliveryId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The delivery receipt changed concurrently.");
            }
            await transaction.CommitAsync(cancellationToken);
            return Clone(receipt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string ComputeRequestHash(
        ShopDeliveryWorkItem delivery,
        string gameVersion,
        string adapterVersion)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var items = NormalizeItems(delivery.Items);
        var playerUid = NormalizeGuid(delivery.PlayerUid, "PlayerUID", lowerCase: true);
        var worldId = NormalizeGuid(delivery.WorldId, "worldId", lowerCase: false);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", "shop.delivery.grant.v1");
            writer.WriteString("deliveryId", delivery.DeliveryId);
            writer.WriteString("idempotencyKey", delivery.IdempotencyKey);
            writer.WriteString("serverId", delivery.ServerId.Trim());
            writer.WriteString("targetIdentifier", delivery.PlayerIdentifier.Trim().ToLowerInvariant());
            writer.WriteString("playerUid", playerUid);
            writer.WriteString("worldId", worldId);
            writer.WriteString("gameVersion", NormalizeVersion(gameVersion, nameof(gameVersion)));
            writer.WriteString("adapterVersion", NormalizeVersion(adapterVersion, nameof(adapterVersion)));
            writer.WriteString("commandVersion", ExtractionDeliveryReceiptContract.CommandVersion);
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("itemId", item.ItemId);
                writer.WriteNumber("requested", item.Quantity);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static ExtractionDeliveryReceiptRequest CreateRequest(
        ShopDeliveryWorkItem delivery,
        string gameVersion,
        string adapterVersion,
        Guid resultId,
        DateTimeOffset createdAt)
    {
        if (delivery.DeliveryId == Guid.Empty || delivery.OrderId == Guid.Empty || resultId == Guid.Empty)
        {
            throw new ArgumentException("Delivery, order, and result ids must be non-empty.", nameof(delivery));
        }
        var idempotencyKey = NormalizeBounded(
            delivery.IdempotencyKey,
            8,
            128,
            nameof(delivery.IdempotencyKey));
        var serverId = NormalizeBounded(delivery.ServerId, 1, 64, nameof(delivery.ServerId));
        var normalizedGameVersion = NormalizeVersion(gameVersion, nameof(gameVersion));
        var normalizedAdapterVersion = NormalizeVersion(adapterVersion, nameof(adapterVersion));
        var items = NormalizeItems(delivery.Items);
        return new ExtractionDeliveryReceiptRequest(
            delivery.DeliveryId,
            idempotencyKey,
            ComputeRequestHash(delivery, normalizedGameVersion, normalizedAdapterVersion),
            resultId,
            serverId,
            NormalizeGuid(delivery.PlayerUid, "PlayerUID", lowerCase: true),
            NormalizeGuid(delivery.WorldId, "worldId", lowerCase: false),
            normalizedGameVersion,
            normalizedAdapterVersion,
            ExtractionDeliveryReceiptContract.CommandVersion,
            items,
            createdAt.ToUniversalTime());
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
            CREATE TABLE IF NOT EXISTS extraction_delivery_receipts (
                delivery_id TEXT PRIMARY KEY,
                idempotency_key TEXT NOT NULL UNIQUE,
                request_hash TEXT NOT NULL CHECK (length(request_hash) = 64),
                result_id TEXT NOT NULL UNIQUE,
                server_id TEXT NOT NULL,
                player_uid TEXT NOT NULL CHECK (length(player_uid) = 32),
                world_id TEXT NOT NULL CHECK (length(world_id) = 32),
                game_version TEXT NOT NULL,
                adapter_version TEXT NOT NULL,
                command_version TEXT NOT NULL,
                requested_items TEXT NOT NULL CHECK (json_valid(requested_items)),
                created_at TEXT NOT NULL,
                receipt_json TEXT NULL CHECK (receipt_json IS NULL OR json_valid(receipt_json)),
                finalized_at TEXT NULL,
                CHECK ((receipt_json IS NULL) = (finalized_at IS NULL))
            );
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('delivery-receipt', 1, $appliedAt);
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

    private static async Task<List<StoredReceipt>> LoadMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid deliveryId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT delivery_id, idempotency_key, request_hash, result_id,
                   server_id, player_uid, world_id, game_version,
                   adapter_version, command_version, requested_items,
                   created_at, receipt_json
            FROM extraction_delivery_receipts
            WHERE delivery_id = $deliveryId
               OR ($idempotencyKey IS NOT NULL AND idempotency_key = $idempotencyKey);
            """;
        command.Parameters.AddWithValue("$deliveryId", deliveryId.ToString("D"));
        command.Parameters.AddWithValue(
            "$idempotencyKey",
            idempotencyKey is null ? DBNull.Value : idempotencyKey);
        var results = new List<StoredReceipt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var request = new ExtractionDeliveryReceiptRequest(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                DeserializeItems(reader.GetString(10)),
                DateTimeOffset.Parse(reader.GetString(11)));
            var receipt = reader.IsDBNull(12)
                ? null
                : JsonSerializer.Deserialize<ExtractionDeliveryReceiptV1>(
                    reader.GetString(12),
                    JsonOptions)
                  ?? throw new InvalidDataException("Persisted delivery receipt JSON is invalid.");
            results.Add(new StoredReceipt(request, receipt));
        }
        return results;
    }

    private static void AddRequestParameters(
        SqliteCommand command,
        ExtractionDeliveryReceiptRequest request)
    {
        command.Parameters.AddWithValue("$deliveryId", request.DeliveryId.ToString("D"));
        command.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
        command.Parameters.AddWithValue("$requestHash", request.RequestHash);
        command.Parameters.AddWithValue("$resultId", request.ResultId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", request.ServerId);
        command.Parameters.AddWithValue("$playerUid", request.PlayerUid);
        command.Parameters.AddWithValue("$worldId", request.WorldId);
        command.Parameters.AddWithValue("$gameVersion", request.GameVersion);
        command.Parameters.AddWithValue("$adapterVersion", request.AdapterVersion);
        command.Parameters.AddWithValue("$commandVersion", request.CommandVersion);
        command.Parameters.AddWithValue("$requestedItems", JsonSerializer.Serialize(request.Items, JsonOptions));
        command.Parameters.AddWithValue("$createdAt", request.CreatedAt.ToString("O"));
    }

    private static void ValidateReceipt(ExtractionDeliveryReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SchemaVersion != ExtractionDeliveryReceiptContract.SchemaVersion ||
            receipt.DeliveryId == Guid.Empty || receipt.ResultId == Guid.Empty ||
            receipt.RequestHash.Length != 64 ||
            (receipt.Outcome != ExtractionDeliveryReceiptOutcome.Uncertain &&
             receipt.AcknowledgedAt is null) ||
            receipt.AcknowledgedAt is { } acknowledgedAt && acknowledgedAt == default ||
            receipt.CreatedAt == default)
        {
            throw new ArgumentException("The delivery receipt envelope is invalid.", nameof(receipt));
        }
        _ = NormalizeBounded(receipt.IdempotencyKey, 8, 128, nameof(receipt.IdempotencyKey));
        _ = NormalizeGuid(receipt.PlayerUid, "PlayerUID", lowerCase: true);
        _ = NormalizeGuid(receipt.WorldId, "worldId", lowerCase: false);
        if (receipt.Items is null || receipt.Items.Count == 0 || receipt.Items.Count > 100)
        {
            throw new ArgumentException("A delivery receipt must contain 1 to 100 item lines.", nameof(receipt));
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in receipt.Items)
        {
            var itemId = NormalizeBounded(item.ItemId, 1, 128, nameof(item.ItemId));
            if (!ids.Add(itemId) || item.Requested <= 0 || item.Granted is < 0)
            {
                throw new ArgumentException("Delivery receipt item lines are invalid.", nameof(receipt));
            }
        }
    }

    private static void EnsureReceiptMatchesRequest(
        ExtractionDeliveryReceiptV1 receipt,
        ExtractionDeliveryReceiptRequest request)
    {
        if (receipt.DeliveryId != request.DeliveryId ||
            !string.Equals(receipt.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(receipt.RequestHash, request.RequestHash, StringComparison.Ordinal) ||
            receipt.ResultId != request.ResultId ||
            !string.Equals(receipt.ServerId, request.ServerId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PlayerUid, request.PlayerUid, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.WorldId, request.WorldId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.GameVersion, request.GameVersion, StringComparison.Ordinal) ||
            !string.Equals(receipt.AdapterVersion, request.AdapterVersion, StringComparison.Ordinal) ||
            !string.Equals(receipt.CommandVersion, request.CommandVersion, StringComparison.Ordinal) ||
            receipt.Items.Count != request.Items.Count)
        {
            throw new InvalidOperationException("The final receipt does not match its immutable request.");
        }
        for (var index = 0; index < request.Items.Count; index++)
        {
            if (!string.Equals(
                    receipt.Items[index].ItemId,
                    request.Items[index].ItemId,
                    StringComparison.Ordinal) ||
                receipt.Items[index].Requested != request.Items[index].Quantity)
            {
                throw new InvalidOperationException(
                    "The final receipt item lines do not match the immutable request.");
            }
        }
    }

    private static IReadOnlyList<ShopItemGrant> NormalizeItems(
        IReadOnlyList<ShopItemGrant> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count is < 1 or > 100)
        {
            throw new ArgumentException("A delivery must contain 1 to 100 distinct items.", nameof(source));
        }
        var items = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (item is null)
            {
                throw new ArgumentException("Delivery items cannot contain null values.", nameof(source));
            }
            var itemId = NormalizeBounded(item.ItemId, 1, 128, nameof(item.ItemId));
            if (item.Quantity <= 0 || !items.TryAdd(itemId, item.Quantity))
            {
                throw new ArgumentException(
                    "Delivery items require positive quantities and distinct item ids.",
                    nameof(source));
            }
        }
        return items.Select(item => new ShopItemGrant(item.Key, item.Value)).ToArray();
    }

    private static IReadOnlyList<ShopItemGrant> DeserializeItems(string json) =>
        NormalizeItems(
            JsonSerializer.Deserialize<ShopItemGrant[]>(json, JsonOptions)
            ?? throw new InvalidDataException("Persisted delivery request items are invalid."));

    private static string NormalizeGuid(string? value, string name, bool lowerCase)
    {
        if (!Guid.TryParseExact(value?.Trim(), "N", out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException($"{name} must be a complete 32-character GUID.", name);
        }
        var normalized = parsed.ToString("N");
        return lowerCase ? normalized.ToLowerInvariant() : normalized.ToUpperInvariant();
    }

    private static string NormalizeVersion(string value, string name) =>
        NormalizeBounded(value, 1, 128, name);

    private static string NormalizeBounded(
        string value,
        int minimum,
        int maximum,
        string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim();
        if (normalized.Length < minimum || normalized.Length > maximum || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{name} must contain {minimum} to {maximum} non-control characters.",
                name);
        }
        return normalized;
    }

    private static ExtractionDeliveryReceiptRequest Clone(
        ExtractionDeliveryReceiptRequest request) => request with
        {
            Items = request.Items.Select(item => item with { }).ToArray()
        };

    private static ExtractionDeliveryReceiptV1 Clone(ExtractionDeliveryReceiptV1 receipt) =>
        receipt with { Items = receipt.Items.Select(item => item with { }).ToArray() };

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));

    private sealed record StoredReceipt(
        ExtractionDeliveryReceiptRequest Request,
        ExtractionDeliveryReceiptV1? Receipt);
}

public sealed record PalDefenderItemGrantDispatchResult(
    IReadOnlyList<Guid> CommandIds,
    bool AllAccepted,
    bool IdempotencyConflict,
    bool CapacityExceeded);

public sealed record PalDefenderItemGrantReceiptResult(
    bool Pending,
    ExtractionDeliveryReceiptV1? Receipt);

/// <summary>
/// Adapts PalDefender's aggregate Granted.Items value into an attributable
/// per-item result by issuing exactly one ItemID in each durable command.
/// </summary>
public sealed class PalDefenderItemGrantAdapter
{
    private readonly PalDefenderCommandQueue _commands;
    private readonly TimeProvider _timeProvider;

    public PalDefenderItemGrantAdapter(
        PalDefenderCommandQueue commands,
        TimeProvider timeProvider)
    {
        _commands = commands;
        _timeProvider = timeProvider;
    }

    public async Task<PalDefenderItemGrantDispatchResult> EnsureEnqueuedAsync(
        ShopDeliveryWorkItem delivery,
        ExtractionDeliveryReceiptRequest request,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        var commandIds = new List<Guid>(request.Items.Count);
        for (var index = 0; index < request.Items.Count; index++)
        {
            var item = request.Items[index];
            var body = new JsonObject
            {
                ["Items"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["ItemID"] = item.ItemId,
                        ["Count"] = item.Quantity
                    }
                }
            };
            var result = await _commands.EnqueueAsync(
                request.ServerId,
                delivery.UpstreamPath,
                body,
                LineIdempotencyKey(request.DeliveryId, index),
                $"{reason}；物品行 {index + 1}/{request.Items.Count}",
                actor,
                cancellationToken);
            if (result.IdempotencyConflict)
            {
                return new PalDefenderItemGrantDispatchResult(
                    commandIds,
                    AllAccepted: false,
                    IdempotencyConflict: true,
                    CapacityExceeded: false);
            }
            if (result.CapacityExceeded || result.Command is null)
            {
                return new PalDefenderItemGrantDispatchResult(
                    commandIds,
                    AllAccepted: false,
                    IdempotencyConflict: false,
                    CapacityExceeded: result.CapacityExceeded);
            }
            commandIds.Add(result.Command.CommandId);
        }
        return new PalDefenderItemGrantDispatchResult(
            commandIds,
            AllAccepted: true,
            IdempotencyConflict: false,
            CapacityExceeded: false);
    }

    public async Task<PalDefenderItemGrantReceiptResult> TryBuildReceiptAsync(
        ExtractionDeliveryReceiptRequest request,
        bool finalizeMissingCommands,
        CancellationToken cancellationToken)
    {
        var lines = new List<ExtractionDeliveryReceiptItem>(request.Items.Count);
        var pending = false;
        for (var index = 0; index < request.Items.Count; index++)
        {
            var requested = request.Items[index];
            var command = await _commands.GetSnapshotByIdempotencyKeyAsync(
                request.ServerId,
                LineIdempotencyKey(request.DeliveryId, index),
                cancellationToken);
            if (command is null)
            {
                if (!finalizeMissingCommands)
                {
                    pending = true;
                    continue;
                }
                lines.Add(new ExtractionDeliveryReceiptItem(
                    requested.ItemId,
                    requested.Quantity,
                    null,
                    null,
                    ExtractionDeliveryReceiptItemResult.CommandRecordMissing,
                    null));
                continue;
            }
            if (command.State is "accepted" or "dispatched")
            {
                pending = true;
                continue;
            }
            lines.Add(ToReceiptItem(requested, command));
        }
        if (pending)
        {
            return new PalDefenderItemGrantReceiptResult(true, null);
        }
        if (lines.Count != request.Items.Count)
        {
            throw new InvalidDataException("Terminal item-grant commands did not produce all receipt lines.");
        }

        var outcome = Classify(lines);
        var completionTimes = lines
            .Where(line => line.CompletedAt is not null)
            .Select(line => line.CompletedAt!.Value)
            .ToArray();
        var acknowledgedAt = completionTimes.Length == 0
            ? (DateTimeOffset?)null
            : completionTimes.Max();
        var receipt = new ExtractionDeliveryReceiptV1(
            ExtractionDeliveryReceiptContract.SchemaVersion,
            request.DeliveryId,
            request.IdempotencyKey,
            request.RequestHash,
            request.ResultId,
            request.ServerId,
            request.PlayerUid,
            request.WorldId,
            request.GameVersion,
            request.AdapterVersion,
            request.CommandVersion,
            acknowledgedAt,
            lines,
            outcome,
            _timeProvider.GetUtcNow());
        return new PalDefenderItemGrantReceiptResult(false, receipt);
    }

    public static string LineIdempotencyKey(Guid deliveryId, int index)
    {
        if (deliveryId == Guid.Empty || index is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return $"shop-grant:{deliveryId:N}:{index:D3}";
    }

    private static ExtractionDeliveryReceiptItem ToReceiptItem(
        ShopItemGrant requested,
        PalDefenderCommandSnapshot command)
    {
        if (string.Equals(command.State, "succeeded", StringComparison.Ordinal))
        {
            if (!TryReadGrantedItems(command.ResponseJson, out var granted))
            {
                return new ExtractionDeliveryReceiptItem(
                    requested.ItemId,
                    requested.Quantity,
                    null,
                    command.CommandId,
                    ExtractionDeliveryReceiptItemResult.InvalidReceipt,
                    command.CompletedAt);
            }
            var result = granted == requested.Quantity
                ? ExtractionDeliveryReceiptItemResult.Succeeded
                : granted < requested.Quantity
                    ? ExtractionDeliveryReceiptItemResult.Partial
                    : ExtractionDeliveryReceiptItemResult.InvalidReceipt;
            return new ExtractionDeliveryReceiptItem(
                requested.ItemId,
                requested.Quantity,
                granted,
                command.CommandId,
                result,
                command.CompletedAt);
        }
        if (string.Equals(command.State, "failed", StringComparison.Ordinal))
        {
            return new ExtractionDeliveryReceiptItem(
                requested.ItemId,
                requested.Quantity,
                0,
                command.CommandId,
                ExtractionDeliveryReceiptItemResult.FailedBeforeMutation,
                command.CompletedAt);
        }
        return new ExtractionDeliveryReceiptItem(
            requested.ItemId,
            requested.Quantity,
            null,
            command.CommandId,
            ExtractionDeliveryReceiptItemResult.Uncertain,
            command.CompletedAt);
    }

    public static bool TryReadGrantedItems(JsonNode? response, out int granted)
    {
        granted = 0;
        if (response is not JsonObject root ||
            GetProperty(root, "Granted") is not JsonObject grantedObject ||
            GetProperty(grantedObject, "Items") is not JsonValue value)
        {
            return false;
        }
        if (value.TryGetValue<int>(out var intValue) && intValue >= 0)
        {
            granted = intValue;
            return true;
        }
        if (value.TryGetValue<long>(out var longValue) &&
            longValue is >= 0 and <= int.MaxValue)
        {
            granted = (int)longValue;
            return true;
        }
        return false;
    }

    private static ExtractionDeliveryReceiptOutcome Classify(
        IReadOnlyCollection<ExtractionDeliveryReceiptItem> lines)
    {
        if (lines.Any(line => line.Result is
                ExtractionDeliveryReceiptItemResult.Uncertain or
                ExtractionDeliveryReceiptItemResult.InvalidReceipt or
                ExtractionDeliveryReceiptItemResult.CommandRecordMissing))
        {
            return ExtractionDeliveryReceiptOutcome.Uncertain;
        }
        if (lines.All(line => line.Result == ExtractionDeliveryReceiptItemResult.Succeeded))
        {
            return ExtractionDeliveryReceiptOutcome.Succeeded;
        }
        if (lines.All(line => line.Result == ExtractionDeliveryReceiptItemResult.FailedBeforeMutation))
        {
            return ExtractionDeliveryReceiptOutcome.Failed;
        }
        return ExtractionDeliveryReceiptOutcome.Partial;
    }

    private static JsonNode? GetProperty(JsonObject value, string name) =>
        value.FirstOrDefault(property =>
            string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

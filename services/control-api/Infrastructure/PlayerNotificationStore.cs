using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Durable, read-model-only notification projection. It never participates in
/// a wallet, purchase, delivery or settlement transaction and therefore can be
/// replayed without repeating an economy action.
/// </summary>
public sealed class PlayerNotificationStore
{
    private const int SchemaMigrationVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public PlayerNotificationStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath), timeProvider)
    {
    }

    public PlayerNotificationStore(string dataDirectory, TimeProvider? timeProvider = null)
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

    public async Task<PlayerNotificationUpsertResult> UpsertAsync(
        PlayerNotificationSource source,
        CancellationToken cancellationToken)
    {
        Validate(source);
        var notificationId = DeterministicGuid($"player-notification-v1\n{source.SourceEventKey}");
        var now = _timeProvider.GetUtcNow();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var existing = await GetBySourceEventKeyAsync(
                connection,
                transaction,
                source.SourceEventKey,
                cancellationToken);
            if (existing is not null)
            {
                EnsureSameScope(existing, source, notificationId);
                if (string.Equals(existing.SourceVersion, source.SourceVersion, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new PlayerNotificationUpsertResult(existing, false, false);
                }

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE player_notifications
                    SET source_version = $sourceVersion,
                        source_state = $sourceState,
                        severity = $severity,
                        title = $title,
                        message = $message,
                        occurred_at = $occurredAt,
                        updated_at = $updatedAt,
                        read_at = NULL,
                        game_state = $gameState,
                        game_notification_id = NULL,
                        game_command_id = NULL,
                        game_error_code = NULL
                    WHERE notification_id = $notificationId;
                    """;
                AddSourceUpdateParameters(update, source, now);
                update.Parameters.AddWithValue(
                    "$gameState",
                    source.RequestGameDelivery ? "pending" : "not-requested");
                update.Parameters.AddWithValue("$notificationId", notificationId.ToString("D"));
                await update.ExecuteNonQueryAsync(cancellationToken);
                await AppendEventAsync(
                    connection,
                    transaction,
                    notificationId,
                    source.SourceVersion,
                    "source-updated",
                    source.RequestGameDelivery ? "pending" : "not-requested",
                    now,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                var changed = existing with
                {
                    SourceVersion = source.SourceVersion,
                    SourceState = source.SourceState,
                    Severity = source.Severity,
                    Title = source.Title,
                    Message = source.Message,
                    OccurredAt = source.OccurredAt.ToUniversalTime(),
                    UpdatedAt = now,
                    ReadAt = null,
                    GameState = source.RequestGameDelivery ? "pending" : "not-requested",
                    GameNotificationId = null,
                    GameCommandId = null,
                    GameErrorCode = null
                };
                return new PlayerNotificationUpsertResult(changed, false, true);
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO player_notifications (
                    notification_id, schema_version, account_id, season_id,
                    source_type, source_id, source_event_key, source_version,
                    source_state, severity, title, message, occurred_at,
                    updated_at, read_at, game_state, game_notification_id,
                    game_command_id, game_error_code)
                VALUES (
                    $notificationId, $schemaVersion, $accountId, $seasonId,
                    $sourceType, $sourceId, $sourceEventKey, $sourceVersion,
                    $sourceState, $severity, $title, $message, $occurredAt,
                    $updatedAt, NULL, $gameState, NULL, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$notificationId", notificationId.ToString("D"));
            insert.Parameters.AddWithValue("$schemaVersion", PlayerNotificationContract.SchemaVersion);
            insert.Parameters.AddWithValue("$accountId", source.AccountId.ToString("D"));
            insert.Parameters.AddWithValue("$seasonId", source.SeasonId.ToString("D"));
            insert.Parameters.AddWithValue("$sourceType", source.SourceType);
            insert.Parameters.AddWithValue("$sourceId", source.SourceId);
            insert.Parameters.AddWithValue("$sourceEventKey", source.SourceEventKey);
            AddSourceUpdateParameters(insert, source, now);
            insert.Parameters.AddWithValue(
                "$gameState",
                source.RequestGameDelivery ? "pending" : "not-requested");
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await AppendEventAsync(
                connection,
                transaction,
                notificationId,
                source.SourceVersion,
                "source-created",
                source.RequestGameDelivery ? "pending" : "not-requested",
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var created = new PlayerNotificationRecord(
                notificationId,
                PlayerNotificationContract.SchemaVersion,
                source.AccountId,
                source.SeasonId,
                source.SourceType,
                source.SourceId,
                source.SourceEventKey,
                source.SourceVersion,
                source.SourceState,
                source.Severity,
                source.Title,
                source.Message,
                source.OccurredAt.ToUniversalTime(),
                now,
                null,
                source.RequestGameDelivery ? "pending" : "not-requested",
                null,
                null,
                null);
            return new PlayerNotificationUpsertResult(created, true, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlayerNotificationFeed> ListForAccountAsync(
        Guid accountId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            var items = new List<PlayerNotificationFeedItem>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT notification_id, schema_version, season_id, source_type,
                           source_state, severity, title, message, occurred_at,
                           updated_at, read_at, game_state
                    FROM player_notifications
                    WHERE account_id = $accountId
                    ORDER BY julianday(updated_at) DESC, notification_id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
                command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(new PlayerNotificationFeedItem(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        Guid.Parse(reader.GetString(2)),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        DateTimeOffset.Parse(reader.GetString(8)),
                        DateTimeOffset.Parse(reader.GetString(9)),
                        reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
                        reader.GetString(11),
                        SafetyAction(reader.GetString(3), reader.GetString(4))));
                }
            }

            await using var counts = connection.CreateCommand();
            counts.CommandText = """
                SELECT
                    SUM(CASE WHEN read_at IS NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN game_state IN ('pending', 'queued') THEN 1 ELSE 0 END)
                FROM player_notifications
                WHERE account_id = $accountId;
                """;
            counts.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
            await using var countReader = await counts.ExecuteReaderAsync(cancellationToken);
            await countReader.ReadAsync(cancellationToken);
            var unread = countReader.IsDBNull(0) ? 0 : checked((int)countReader.GetInt64(0));
            var active = !countReader.IsDBNull(1) && countReader.GetInt64(1) > 0;
            return new PlayerNotificationFeed(
                PlayerNotificationContract.SchemaVersion,
                unread,
                active,
                items);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlayerNotificationReadResult?> MarkReadAsync(
        Guid accountId,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE player_notifications
                SET read_at = $readAt
                WHERE notification_id = $notificationId
                  AND account_id = $accountId
                  AND read_at IS NULL
                RETURNING read_at;
                """;
            command.Parameters.AddWithValue("$readAt", now.ToString("O"));
            command.Parameters.AddWithValue("$notificationId", notificationId.ToString("D"));
            command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
            var stored = await command.ExecuteScalarAsync(cancellationToken);
            if (stored is not string readAtText)
            {
                await using var existing = connection.CreateCommand();
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT read_at
                    FROM player_notifications
                    WHERE notification_id = $notificationId AND account_id = $accountId;
                    """;
                existing.Parameters.AddWithValue("$notificationId", notificationId.ToString("D"));
                existing.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
                var existingReadAt = await existing.ExecuteScalarAsync(cancellationToken);
                if (existingReadAt is not string existingReadAtText)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }
                var existingUnread = await CountUnreadAsync(
                    connection,
                    transaction,
                    accountId,
                    cancellationToken);
                await transaction.RollbackAsync(cancellationToken);
                return new PlayerNotificationReadResult(
                    notificationId,
                    DateTimeOffset.Parse(existingReadAtText),
                    existingUnread);
            }
            await AppendEventAsync(
                connection,
                transaction,
                notificationId,
                null,
                "read",
                "read",
                now,
                cancellationToken);
            var unread = await CountUnreadAsync(connection, transaction, accountId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PlayerNotificationReadResult(
                notificationId,
                DateTimeOffset.Parse(readAtText),
                unread);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlayerNotificationReadAllResult> MarkAllReadAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE player_notifications
                SET read_at = $readAt
                WHERE account_id = $accountId AND read_at IS NULL;
                """;
            command.Parameters.AddWithValue("$readAt", now.ToString("O"));
            command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
            var marked = await command.ExecuteNonQueryAsync(cancellationToken);
            if (marked > 0)
            {
                await AppendEventAsync(
                    connection,
                    transaction,
                    DeterministicGuid($"player-notification-read-all-v1\n{accountId:D}\n{now:O}"),
                    null,
                    "read-all",
                    "read",
                    now,
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new PlayerNotificationReadAllResult(marked, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateGameStateAsync(
        Guid notificationId,
        string expectedSourceVersion,
        PlayerGameNotificationDispatchResult result,
        CancellationToken cancellationToken)
    {
        if (!PlayerNotificationContract.GameStates.Contains(result.State))
        {
            throw new ArgumentException("The game notification state is invalid.", nameof(result));
        }
        var now = _timeProvider.GetUtcNow();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE player_notifications
                SET game_state = $gameState,
                    game_notification_id = $gameNotificationId,
                    game_command_id = $gameCommandId,
                    game_error_code = $gameErrorCode,
                    updated_at = $updatedAt
                WHERE notification_id = $notificationId
                  AND source_version = $sourceVersion
                  AND (
                      game_state <> $gameState OR
                      COALESCE(game_notification_id, '') <>
                          COALESCE(CAST($gameNotificationId AS TEXT), '') OR
                      COALESCE(game_command_id, '') <>
                          COALESCE(CAST($gameCommandId AS TEXT), '') OR
                      COALESCE(game_error_code, '') <>
                          COALESCE(CAST($gameErrorCode AS TEXT), '')
                  );
                """;
            command.Parameters.AddWithValue("$gameState", result.State);
            command.Parameters.AddWithValue(
                "$gameNotificationId",
                result.GameNotificationId is Guid gameNotificationId
                    ? gameNotificationId.ToString("D")
                    : DBNull.Value);
            command.Parameters.AddWithValue(
                "$gameCommandId",
                result.GameCommandId is Guid gameCommandId
                    ? gameCommandId.ToString("D")
                    : DBNull.Value);
            command.Parameters.AddWithValue(
                "$gameErrorCode",
                string.IsNullOrWhiteSpace(result.ErrorCode) ? DBNull.Value : result.ErrorCode);
            command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            command.Parameters.AddWithValue("$notificationId", notificationId.ToString("D"));
            command.Parameters.AddWithValue("$sourceVersion", expectedSourceVersion);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (changed)
            {
                await AppendEventAsync(
                    connection,
                    transaction,
                    notificationId,
                    expectedSourceVersion,
                    "game-state-changed",
                    result.State,
                    now,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<PlayerNotificationRecord?> GetAsync(
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM player_notifications WHERE notification_id = $id;";
            command.Parameters.AddWithValue("$id", notificationId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
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
            CREATE TABLE IF NOT EXISTS player_notifications (
                notification_id TEXT PRIMARY KEY,
                schema_version TEXT NOT NULL CHECK (schema_version = '1'),
                account_id TEXT NOT NULL,
                season_id TEXT NOT NULL,
                source_type TEXT NOT NULL CHECK (source_type IN (
                    'order-delivery', 'resource-settlement', 'season-end', 'reconciliation')),
                source_id TEXT NOT NULL,
                source_event_key TEXT NOT NULL UNIQUE,
                source_version TEXT NOT NULL,
                source_state TEXT NOT NULL,
                severity TEXT NOT NULL CHECK (severity IN ('success', 'info', 'warning', 'error')),
                title TEXT NOT NULL,
                message TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                read_at TEXT NULL,
                game_state TEXT NOT NULL CHECK (game_state IN (
                    'pending', 'queued', 'sent', 'blocked', 'failed', 'uncertain', 'not-requested')),
                game_notification_id TEXT NULL,
                game_command_id TEXT NULL,
                game_error_code TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_player_notifications_account_updated
                ON player_notifications (account_id, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_player_notifications_account_unread
                ON player_notifications (account_id, read_at);
            CREATE TABLE IF NOT EXISTS player_notification_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                notification_id TEXT NOT NULL,
                source_version TEXT NULL,
                event_type TEXT NOT NULL,
                state TEXT NOT NULL,
                occurred_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('player-notifications', $version, $appliedAt);
            """;
        command.Parameters.AddWithValue("$version", SchemaMigrationVersion);
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

    private static async Task<PlayerNotificationRecord?> GetBySourceEventKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceEventKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM player_notifications WHERE source_event_key = $key;";
        command.Parameters.AddWithValue("$key", sourceEventKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    private static PlayerNotificationRecord ReadRecord(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("notification_id"))),
        reader.GetString(reader.GetOrdinal("schema_version")),
        Guid.Parse(reader.GetString(reader.GetOrdinal("account_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("season_id"))),
        reader.GetString(reader.GetOrdinal("source_type")),
        reader.GetString(reader.GetOrdinal("source_id")),
        reader.GetString(reader.GetOrdinal("source_event_key")),
        reader.GetString(reader.GetOrdinal("source_version")),
        reader.GetString(reader.GetOrdinal("source_state")),
        reader.GetString(reader.GetOrdinal("severity")),
        reader.GetString(reader.GetOrdinal("title")),
        reader.GetString(reader.GetOrdinal("message")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("occurred_at"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        reader.IsDBNull(reader.GetOrdinal("read_at"))
            ? null
            : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("read_at"))),
        reader.GetString(reader.GetOrdinal("game_state")),
        reader.IsDBNull(reader.GetOrdinal("game_notification_id"))
            ? null
            : Guid.Parse(reader.GetString(reader.GetOrdinal("game_notification_id"))),
        reader.IsDBNull(reader.GetOrdinal("game_command_id"))
            ? null
            : Guid.Parse(reader.GetString(reader.GetOrdinal("game_command_id"))),
        reader.IsDBNull(reader.GetOrdinal("game_error_code"))
            ? null
            : reader.GetString(reader.GetOrdinal("game_error_code")));

    private static void AddSourceUpdateParameters(
        SqliteCommand command,
        PlayerNotificationSource source,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$sourceVersion", source.SourceVersion);
        command.Parameters.AddWithValue("$sourceState", source.SourceState);
        command.Parameters.AddWithValue("$severity", source.Severity);
        command.Parameters.AddWithValue("$title", source.Title);
        command.Parameters.AddWithValue("$message", source.Message);
        command.Parameters.AddWithValue("$occurredAt", source.OccurredAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
    }

    private static async Task AppendEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid notificationId,
        string? sourceVersion,
        string eventType,
        string state,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO player_notification_events (
                event_id, notification_id, source_version, event_type, state, occurred_at)
            VALUES ($eventId, $notificationId, $sourceVersion, $eventType, $state, $occurredAt);
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$notificationId", notificationId.ToString("D"));
        command.Parameters.AddWithValue(
            "$sourceVersion",
            string.IsNullOrWhiteSpace(sourceVersion) ? DBNull.Value : sourceVersion);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountUnreadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM player_notifications WHERE account_id = $accountId AND read_at IS NULL;";
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L));
    }

    private static void EnsureSameScope(
        PlayerNotificationRecord existing,
        PlayerNotificationSource source,
        Guid notificationId)
    {
        if (existing.NotificationId != notificationId ||
            existing.AccountId != source.AccountId ||
            existing.SeasonId != source.SeasonId ||
            !string.Equals(existing.SourceType, source.SourceType, StringComparison.Ordinal) ||
            !string.Equals(existing.SourceId, source.SourceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A player notification source event key was reused for a different scope.");
        }
    }

    private static void Validate(PlayerNotificationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.AccountId == Guid.Empty || source.SeasonId == Guid.Empty ||
            !PlayerNotificationContract.SourceTypes.Contains(source.SourceType) ||
            !PlayerNotificationContract.Severities.Contains(source.Severity) ||
            string.IsNullOrWhiteSpace(source.TargetPlayerId) || source.TargetPlayerId.Length > 128 ||
            string.IsNullOrWhiteSpace(source.SourceId) || source.SourceId.Length > 160 ||
            string.IsNullOrWhiteSpace(source.SourceEventKey) || source.SourceEventKey.Length > 320 ||
            string.IsNullOrWhiteSpace(source.SourceVersion) || source.SourceVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(source.SourceState) || source.SourceState.Length > 64 ||
            string.IsNullOrWhiteSpace(source.Title) || source.Title.Length > 100 ||
            string.IsNullOrWhiteSpace(source.Message) || source.Message.Length > 500)
        {
            throw new ArgumentException("The player notification source is invalid.", nameof(source));
        }
    }

    private static string SafetyAction(string sourceType, string sourceState) =>
        sourceType == "reconciliation" ||
        sourceState is "partial" or "uncertain" or "reconciliation-required"
            ? "do-not-repeat-contact-support"
            : "none";

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));
}

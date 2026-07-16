using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class PlayerIdentitySecurityEvents
{
    public const string CodeRequest = "login_code_request";
    public const string CodeVerification = "login_code_verification";
    public const string SessionRejected = "session_rejected";
    public const string SessionLogout = "session_logout";
    public const string SteamOpenIdStart = "steam_openid_start";
    public const string SteamOpenIdCallback = "steam_openid_callback";
    public const string SteamOpenIdBinding = "steam_openid_binding";
    public const string AdministrativeSessionRevocation = "administrative_session_revocation";
    public const string AdministrativeBan = "administrative_ban";
    public const string AdministrativeUnban = "administrative_unban";
}

public sealed record PlayerIdentitySecurityAuditEvent(
    long Sequence,
    Guid EventId,
    string CorrelationId,
    string EventType,
    string Outcome,
    string ReasonCode,
    string RiskLevel,
    string? SubjectFingerprint,
    string? SourceIpFingerprint,
    int AffectedSessions,
    DateTimeOffset OccurredAt);

/// <summary>
/// Durable security state and a deliberately metadata-only login audit. The
/// schema has no field for OTPs, challenge IDs, cookie values, CSRF values, or
/// request bodies. Player and IP identifiers are stored only as SHA-256
/// fingerprints.
/// </summary>
public sealed class PlayerIdentitySecurityStore
{
    private readonly object _sync = new();
    private readonly string _connectionString;
    private readonly string _legacyDatabasePath;

    public PlayerIdentitySecurityStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath))
    {
    }

    public PlayerIdentitySecurityStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullPath = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullPath);
        _legacyDatabasePath = Path.Combine(fullPath, "player-identity-security.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fullPath, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        Initialize();
        MigrateLegacyDatabase();
    }

    public bool IsBanned(string normalizedSubject)
    {
        var fingerprint = FingerprintSubject(normalizedSubject);
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM player_identity_bans
                WHERE subject_fingerprint = $subjectFingerprint
                  AND revoked_at IS NULL
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$subjectFingerprint", fingerprint);
            return command.ExecuteScalar() is not null;
        }
    }

    public void SetBan(
        string normalizedSubject,
        bool banned,
        string correlationId,
        string actorFingerprint,
        DateTimeOffset occurredAt)
    {
        var fingerprint = FingerprintSubject(normalizedSubject);
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = banned
                ? """
                    INSERT INTO player_identity_bans (
                        subject_fingerprint, banned_at, revoked_at,
                        correlation_id, actor_fingerprint)
                    VALUES (
                        $subjectFingerprint, $occurredAt, NULL,
                        $correlationId, $actorFingerprint)
                    ON CONFLICT(subject_fingerprint) DO UPDATE SET
                        banned_at = excluded.banned_at,
                        revoked_at = NULL,
                        correlation_id = excluded.correlation_id,
                        actor_fingerprint = excluded.actor_fingerprint;
                    """
                : """
                    UPDATE player_identity_bans
                    SET revoked_at = $occurredAt,
                        correlation_id = $correlationId,
                        actor_fingerprint = $actorFingerprint
                    WHERE subject_fingerprint = $subjectFingerprint
                      AND revoked_at IS NULL;
                    """;
            command.Parameters.AddWithValue("$subjectFingerprint", fingerprint);
            command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O"));
            command.Parameters.AddWithValue("$correlationId", NormalizeCorrelationId(correlationId));
            command.Parameters.AddWithValue("$actorFingerprint", actorFingerprint);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public void Append(
        string eventType,
        string outcome,
        string reasonCode,
        string riskLevel,
        string? normalizedSubject,
        string? sourceIp,
        string? correlationId,
        int affectedSessions,
        DateTimeOffset occurredAt)
    {
        var auditEvent = new PlayerIdentitySecurityAuditEvent(
            Sequence: 0,
            Guid.NewGuid(),
            NormalizeCorrelationId(correlationId),
            NormalizeMetadata(eventType, 64, nameof(eventType)),
            NormalizeMetadata(outcome, 32, nameof(outcome)),
            NormalizeMetadata(reasonCode, 64, nameof(reasonCode)),
            NormalizeMetadata(riskLevel, 16, nameof(riskLevel)),
            string.IsNullOrWhiteSpace(normalizedSubject)
                ? null
                : FingerprintSubject(normalizedSubject),
            string.IsNullOrWhiteSpace(sourceIp) || sourceIp == "unavailable"
                ? null
                : Fingerprint(sourceIp.Trim()),
            Math.Max(0, affectedSessions),
            occurredAt);

        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO player_identity_security_events (
                    event_id, correlation_id, event_type, outcome, reason_code,
                    risk_level, subject_fingerprint, source_ip_fingerprint,
                    affected_sessions, occurred_at)
                VALUES (
                    $eventId, $correlationId, $eventType, $outcome, $reasonCode,
                    $riskLevel, $subjectFingerprint, $sourceIpFingerprint,
                    $affectedSessions, $occurredAt);
                """;
            AddAuditParameters(command, auditEvent);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<PlayerIdentitySecurityAuditEvent> List(int limit)
    {
        var results = new List<PlayerIdentitySecurityAuditEvent>();
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, event_id, correlation_id, event_type, outcome,
                       reason_code, risk_level, subject_fingerprint,
                       source_ip_fingerprint, affected_sessions, occurred_at
                FROM player_identity_security_events
                ORDER BY sequence DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new PlayerIdentitySecurityAuditEvent(
                    reader.GetInt64(0),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetInt32(9),
                    DateTimeOffset.Parse(
                        reader.GetString(10),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)));
            }
        }
        return results;
    }

    public static string FingerprintSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return Fingerprint(subject.Trim().ToLowerInvariant());
    }

    public static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ResolveDataDirectory(string configured, string contentRootPath) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRootPath, configured));

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS player_identity_bans (
                    subject_fingerprint TEXT PRIMARY KEY
                        CHECK (length(subject_fingerprint) = 64),
                    banned_at TEXT NOT NULL,
                    revoked_at TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    actor_fingerprint TEXT NOT NULL
                        CHECK (length(actor_fingerprint) = 64)
                );
                CREATE TABLE IF NOT EXISTS player_identity_security_events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL UNIQUE,
                    correlation_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    reason_code TEXT NOT NULL,
                    risk_level TEXT NOT NULL,
                    subject_fingerprint TEXT NULL
                        CHECK (subject_fingerprint IS NULL OR length(subject_fingerprint) = 64),
                    source_ip_fingerprint TEXT NULL
                        CHECK (source_ip_fingerprint IS NULL OR length(source_ip_fingerprint) = 64),
                    affected_sessions INTEGER NOT NULL CHECK (affected_sessions >= 0),
                    occurred_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_player_identity_security_subject
                    ON player_identity_security_events (subject_fingerprint, sequence DESC);
                CREATE INDEX IF NOT EXISTS ix_player_identity_security_outcome
                    ON player_identity_security_events (outcome, risk_level, sequence DESC);
                CREATE TABLE IF NOT EXISTS player_identity_security_migrations (
                    migration_key TEXT PRIMARY KEY,
                    source_fingerprint TEXT NOT NULL,
                    imported_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }
    }

    private void MigrateLegacyDatabase()
    {
        if (!File.Exists(_legacyDatabasePath) ||
            PathsEqual(_legacyDatabasePath, new SqliteConnectionStringBuilder(_connectionString).DataSource))
        {
            return;
        }

        const string migrationKey = "player-identity-security-standalone-v1";
        lock (_sync)
        {
            using var connection = OpenConnection();
            using (var alreadyImported = connection.CreateCommand())
            {
                alreadyImported.CommandText = """
                    SELECT 1 FROM player_identity_security_migrations
                    WHERE migration_key = $migrationKey LIMIT 1;
                    """;
                alreadyImported.Parameters.AddWithValue("$migrationKey", migrationKey);
                if (alreadyImported.ExecuteScalar() is not null)
                {
                    return;
                }
            }

            var legacyFingerprint = FingerprintFile(_legacyDatabasePath);
            using var attach = connection.CreateCommand();
            attach.CommandText = "ATTACH DATABASE $legacyPath AS legacy_identity;";
            attach.Parameters.AddWithValue("$legacyPath", _legacyDatabasePath);
            attach.ExecuteNonQuery();
            try
            {
                using var transaction = connection.BeginTransaction();
                if (TableExists(connection, transaction, "legacy_identity", "player_identity_bans"))
                {
                    using var importBans = connection.CreateCommand();
                    importBans.Transaction = transaction;
                    importBans.CommandText = """
                        INSERT INTO player_identity_bans (
                            subject_fingerprint, banned_at, revoked_at,
                            correlation_id, actor_fingerprint)
                        SELECT subject_fingerprint, banned_at, revoked_at,
                               correlation_id, actor_fingerprint
                        FROM legacy_identity.player_identity_bans
                        ON CONFLICT(subject_fingerprint) DO UPDATE SET
                            banned_at = CASE
                                WHEN excluded.banned_at > player_identity_bans.banned_at
                                THEN excluded.banned_at ELSE player_identity_bans.banned_at END,
                            revoked_at = CASE
                                WHEN excluded.banned_at > player_identity_bans.banned_at
                                THEN excluded.revoked_at ELSE player_identity_bans.revoked_at END,
                            correlation_id = CASE
                                WHEN excluded.banned_at > player_identity_bans.banned_at
                                THEN excluded.correlation_id ELSE player_identity_bans.correlation_id END,
                            actor_fingerprint = CASE
                                WHEN excluded.banned_at > player_identity_bans.banned_at
                                THEN excluded.actor_fingerprint ELSE player_identity_bans.actor_fingerprint END;
                        """;
                    importBans.ExecuteNonQuery();
                }
                if (TableExists(
                        connection,
                        transaction,
                        "legacy_identity",
                        "player_identity_security_events"))
                {
                    using var importEvents = connection.CreateCommand();
                    importEvents.Transaction = transaction;
                    importEvents.CommandText = """
                        INSERT OR IGNORE INTO player_identity_security_events (
                            event_id, correlation_id, event_type, outcome, reason_code,
                            risk_level, subject_fingerprint, source_ip_fingerprint,
                            affected_sessions, occurred_at)
                        SELECT event_id, correlation_id, event_type, outcome, reason_code,
                               risk_level, subject_fingerprint, source_ip_fingerprint,
                               affected_sessions, occurred_at
                        FROM legacy_identity.player_identity_security_events;
                        """;
                    importEvents.ExecuteNonQuery();
                }
                using var marker = connection.CreateCommand();
                marker.Transaction = transaction;
                marker.CommandText = """
                    INSERT INTO player_identity_security_migrations (
                        migration_key, source_fingerprint, imported_at)
                    VALUES ($migrationKey, $sourceFingerprint, $importedAt);
                    """;
                marker.Parameters.AddWithValue("$migrationKey", migrationKey);
                marker.Parameters.AddWithValue("$sourceFingerprint", legacyFingerprint);
                marker.Parameters.AddWithValue("$importedAt", DateTimeOffset.UtcNow.ToString("O"));
                marker.ExecuteNonQuery();
                transaction.Commit();
            }
            finally
            {
                using var detach = connection.CreateCommand();
                detach.CommandText = "DETACH DATABASE legacy_identity;";
                detach.ExecuteNonQuery();
            }
        }
    }

    private static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string schema,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT 1 FROM {schema}.sqlite_master WHERE type = 'table' AND name = $table LIMIT 1;";
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is not null;
    }

    private static string FingerprintFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void AddAuditParameters(
        SqliteCommand command,
        PlayerIdentitySecurityAuditEvent auditEvent)
    {
        command.Parameters.AddWithValue("$eventId", auditEvent.EventId.ToString("D"));
        command.Parameters.AddWithValue("$correlationId", auditEvent.CorrelationId);
        command.Parameters.AddWithValue("$eventType", auditEvent.EventType);
        command.Parameters.AddWithValue("$outcome", auditEvent.Outcome);
        command.Parameters.AddWithValue("$reasonCode", auditEvent.ReasonCode);
        command.Parameters.AddWithValue("$riskLevel", auditEvent.RiskLevel);
        command.Parameters.AddWithValue(
            "$subjectFingerprint",
            (object?)auditEvent.SubjectFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$sourceIpFingerprint",
            (object?)auditEvent.SourceIpFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$affectedSessions", auditEvent.AffectedSessions);
        command.Parameters.AddWithValue("$occurredAt", auditEvent.OccurredAt.ToString("O"));
    }

    private static string NormalizeCorrelationId(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim();
        return normalized.Length <= 128 ? normalized : Fingerprint(normalized);
    }

    private static string NormalizeMetadata(string value, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > maxLength || normalized.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
        {
            throw new ArgumentException("Security audit metadata must use safe identifier characters.", name);
        }
        return normalized;
    }
}

/// <summary>
/// Serializes session creation with moderation changes. This closes the race in
/// which a code verification could otherwise create a new session immediately
/// after an administrator revoked the player's existing sessions.
/// </summary>
public sealed class PlayerIdentitySecurityService(
    PlayerIdentitySecurityStore store,
    PlayerPortalSessionRegistry sessions,
    TimeProvider timeProvider)
{
    private readonly object _sync = new();
    private readonly HashSet<string> _volatileBans = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBanned(string normalizedSubject)
    {
        lock (_sync)
        {
            return _volatileBans.Contains(normalizedSubject) || store.IsBanned(normalizedSubject);
        }
    }

    internal bool IsVolatileBanned(string normalizedSubject)
    {
        lock (_sync)
        {
            return _volatileBans.Contains(normalizedSubject);
        }
    }

    public PlayerPortalSessionCreation? CreateSessionIfAllowed(
        string normalizedSubject,
        string? playerUid,
        TimeSpan lifetime)
    {
        lock (_sync)
        {
            if (_volatileBans.Contains(normalizedSubject) || store.IsBanned(normalizedSubject))
            {
                return null;
            }
            return sessions.Create(
                normalizedSubject,
                playerUid,
                timeProvider.GetUtcNow(),
                lifetime);
        }
    }

    public PlayerPortalSession? Authenticate(string rawSessionToken)
    {
        lock (_sync)
        {
            var session = sessions.Authenticate(rawSessionToken, timeProvider.GetUtcNow());
            if (session is null ||
                (!_volatileBans.Contains(session.UserId) && !store.IsBanned(session.UserId)))
            {
                return session;
            }
            sessions.RevokeAll(session.UserId);
            return null;
        }
    }

    public bool Revoke(string rawSessionToken) => sessions.Revoke(rawSessionToken);

    public int RevokeAll(string normalizedSubject)
    {
        lock (_sync)
        {
            return sessions.RevokeAll(normalizedSubject);
        }
    }

    public IReadOnlyList<string> FindSessionSubjects(string playerIdentifier) =>
        sessions.FindSubjects(playerIdentifier);

    public int ApplyModeration(
        string normalizedSubject,
        bool banned,
        string correlationId,
        string actorFingerprint)
    {
        lock (_sync)
        {
            var now = timeProvider.GetUtcNow();
            if (banned)
            {
                // Fail closed: revoke and remember the ban in memory before
                // durable I/O. If the disk write fails, this process still
                // cannot mint or accept a session for the banned subject.
                _volatileBans.Add(normalizedSubject);
                var affected = sessions.RevokeAll(normalizedSubject);
                store.SetBan(
                    normalizedSubject,
                    banned: true,
                    correlationId,
                    actorFingerprint,
                    now);
                return affected;
            }

            // Clear the in-memory fence only after the durable ban has been
            // revoked. A failed unban therefore remains fail closed.
            store.SetBan(
                normalizedSubject,
                banned: false,
                correlationId,
                actorFingerprint,
                now);
            _volatileBans.Remove(normalizedSubject);
            return 0;
        }
    }

    public void Audit(
        string eventType,
        string outcome,
        string reasonCode,
        string riskLevel,
        string? normalizedSubject,
        string? sourceIp,
        string? correlationId,
        int affectedSessions = 0) => store.Append(
            eventType,
            outcome,
            reasonCode,
            riskLevel,
            normalizedSubject,
            sourceIp,
            correlationId,
            affectedSessions,
            timeProvider.GetUtcNow());
}

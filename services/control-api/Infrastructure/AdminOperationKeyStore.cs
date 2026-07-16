using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed class AdminOperationKeyConflictException : Exception
{
    public AdminOperationKeyConflictException()
        : base("The administrator idempotency key is already bound to another request or subject.")
    {
    }
}

public sealed record AdminOperationKeyRegistration(
    string IdempotencyKey,
    string OperationScope,
    string RequestHash,
    bool Replayed,
    DateTimeOffset CreatedAt);

/// <summary>
/// Binds every high-risk administrator Idempotency-Key to one canonical request
/// and authenticated subject in the authoritative economy SQLite database.
/// The request body and subject are hashed; TOTP values and credentials are not
/// accepted by this store and therefore cannot be persisted accidentally.
/// </summary>
public sealed class AdminOperationKeyStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public AdminOperationKeyStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath), timeProvider)
    {
    }

    public AdminOperationKeyStore(
        string dataDirectory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Initialize();
    }

    public async Task<AdminOperationKeyRegistration> RegisterAsync(
        string idempotencyKey,
        string operationScope,
        string canonicalRequest,
        string subject,
        CancellationToken cancellationToken)
    {
        var key = Normalize(idempotencyKey, 8, 128, nameof(idempotencyKey));
        var scope = Normalize(operationScope, 3, 128, nameof(operationScope));
        var request = Normalize(
            canonicalRequest,
            3,
            4096,
            nameof(canonicalRequest),
            allowLineFeed: true);
        var actor = Normalize(subject, 1, 256, nameof(subject));
        var requestHash = Sha256($"admin-operation-v1\n{scope}\n{request}");
        var subjectHash = Sha256($"admin-subject-v1\n{actor}");
        var now = _timeProvider.GetUtcNow();

        await using var connection = Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        bool inserted;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO admin_operation_keys (
                    idempotency_key, operation_scope, request_hash,
                    subject_hash, created_at)
                VALUES ($key, $scope, $requestHash, $subjectHash, $createdAt);
                """;
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$requestHash", requestHash);
            insert.Parameters.AddWithValue("$subjectHash", subjectHash);
            insert.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        string storedScope;
        string storedRequestHash;
        string storedSubjectHash;
        DateTimeOffset createdAt;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = """
                SELECT operation_scope, request_hash, subject_hash, created_at
                FROM admin_operation_keys
                WHERE idempotency_key = $key;
                """;
            read.Parameters.AddWithValue("$key", key);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The administrator operation key could not be reloaded.");
            }
            storedScope = reader.GetString(0);
            storedRequestHash = reader.GetString(1);
            storedSubjectHash = reader.GetString(2);
            createdAt = DateTimeOffset.Parse(
                reader.GetString(3),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind);
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(storedRequestHash),
                Encoding.ASCII.GetBytes(requestHash)) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(storedSubjectHash),
                Encoding.ASCII.GetBytes(subjectHash)) ||
            !string.Equals(storedScope, scope, StringComparison.Ordinal))
        {
            throw new AdminOperationKeyConflictException();
        }

        var replayed = !inserted;
        await transaction.CommitAsync(cancellationToken);
        return new AdminOperationKeyRegistration(key, scope, requestHash, replayed, createdAt);
    }

    private SqliteConnection Open()
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
            CREATE TABLE IF NOT EXISTS admin_operation_keys (
                idempotency_key TEXT PRIMARY KEY,
                operation_scope TEXT NOT NULL,
                request_hash TEXT NOT NULL CHECK (length(request_hash) = 64),
                subject_hash TEXT NOT NULL CHECK (length(subject_hash) = 64),
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_admin_operation_keys_created
                ON admin_operation_keys (created_at);
            INSERT OR IGNORE INTO economy_schema_migrations(component, version, applied_at)
            VALUES ('admin-operation-keys', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string Normalize(
        string value,
        int minimum,
        int maximum,
        string parameter,
        bool allowLineFeed = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var normalized = value.Trim();
        if (normalized.Length < minimum || normalized.Length > maximum ||
            normalized.Any(character => char.IsControl(character) &&
                !(allowLineFeed && character == '\n')))
        {
            throw new ArgumentException(
                $"{parameter} must contain {minimum} to {maximum} non-control characters.",
                parameter);
        }
        return normalized;
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ResolveDataDirectory(string configured, string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configured);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));
    }
}

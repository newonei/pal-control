using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed record AdminAuditEvent(
    Guid AuditId,
    string CorrelationId,
    string Phase,
    string Subject,
    IReadOnlyList<string> Roles,
    string SourceIp,
    string Method,
    string Path,
    string RequestHash,
    string? Reason,
    string? BeforeJson,
    string? AfterJson,
    string ServiceVersion,
    int? ResultStatus,
    DateTimeOffset OccurredAt);

public sealed class AdminAuditStore
{
    private readonly string _connectionString;

    public AdminAuditStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var dataDirectory = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "extraction-commerce.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        Initialize();
    }

    public async Task AppendAsync(AdminAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_audit_events (
                audit_id, correlation_id, phase, subject, roles_json, source_ip,
                method, path, request_hash, reason, before_json, after_json,
                service_version, result_status, occurred_at)
            VALUES (
                $auditId, $correlationId, $phase, $subject, $rolesJson, $sourceIp,
                $method, $path, $requestHash, $reason, $beforeJson, $afterJson,
                $serviceVersion, $resultStatus, $occurredAt);
            """;
        command.Parameters.AddWithValue("$auditId", auditEvent.AuditId.ToString("D"));
        command.Parameters.AddWithValue("$correlationId", auditEvent.CorrelationId);
        command.Parameters.AddWithValue("$phase", auditEvent.Phase);
        command.Parameters.AddWithValue("$subject", auditEvent.Subject);
        command.Parameters.AddWithValue("$rolesJson", JsonSerializer.Serialize(auditEvent.Roles));
        command.Parameters.AddWithValue("$sourceIp", auditEvent.SourceIp);
        command.Parameters.AddWithValue("$method", auditEvent.Method);
        command.Parameters.AddWithValue("$path", auditEvent.Path);
        command.Parameters.AddWithValue("$requestHash", auditEvent.RequestHash);
        command.Parameters.AddWithValue("$reason", (object?)auditEvent.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$beforeJson", (object?)auditEvent.BeforeJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterJson", (object?)auditEvent.AfterJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$serviceVersion", auditEvent.ServiceVersion);
        command.Parameters.AddWithValue("$resultStatus", (object?)auditEvent.ResultStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$occurredAt", auditEvent.OccurredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AdminAuditEvent>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var results = new List<AdminAuditEvent>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT audit_id, correlation_id, phase, subject, roles_json, source_ip,
                   method, path, request_hash, reason, before_json, after_json,
                   service_version, result_status, occurred_at
            FROM admin_audit_events
            ORDER BY sequence DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AdminAuditEvent(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [],
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetInt32(13),
                DateTimeOffset.Parse(reader.GetString(14), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return results;
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS economy_schema_migrations (
                component TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                applied_at TEXT NOT NULL,
                PRIMARY KEY (component, version)
            );
            CREATE TABLE IF NOT EXISTS admin_audit_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                audit_id TEXT NOT NULL UNIQUE,
                correlation_id TEXT NOT NULL,
                phase TEXT NOT NULL CHECK (phase IN ('started', 'completed', 'failed')),
                subject TEXT NOT NULL,
                roles_json TEXT NOT NULL,
                source_ip TEXT NOT NULL,
                method TEXT NOT NULL,
                path TEXT NOT NULL,
                request_hash TEXT NOT NULL,
                reason TEXT NULL,
                before_json TEXT NULL,
                after_json TEXT NULL,
                service_version TEXT NOT NULL,
                result_status INTEGER NULL,
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_admin_audit_correlation
                ON admin_audit_events (correlation_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_admin_audit_subject
                ON admin_audit_events (subject, sequence DESC);
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('admin-audit', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}

public sealed class AdminAuditMiddleware(
    RequestDelegate next,
    AdminAuditStore store,
    TimeProvider timeProvider,
    ILogger<AdminAuditMiddleware> logger)
{
    private const int MaximumAuditedRequestBodyBytes = 1_048_576;
    private static readonly string ServiceVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            !context.Request.Path.StartsWithSegments("/api/v1"))
        {
            await next(context);
            return;
        }

        var correlationId = GetCorrelationId(context);
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        var requestHash = await HashRequestAsync(context.Request, context.RequestAborted);
        var subject = AdminIdentity.RequireSubject(context);
        var roles = AdminIdentity.Roles(context);
        var sourceIp = context.Connection.RemoteIpAddress?.ToString() ?? "unavailable";
        var reason = NormalizeReason(context.Request.Headers["X-Pal-Admin-Reason"]);
        var started = CreateEvent(
            context,
            correlationId,
            "started",
            subject,
            roles,
            sourceIp,
            requestHash,
            reason,
            resultStatus: null);
        await store.AppendAsync(started, context.RequestAborted);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["AdminSubject"] = subject
        });
        Exception? failure = null;
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            var completed = CreateEvent(
                context,
                correlationId,
                failure is null ? "completed" : "failed",
                subject,
                roles,
                sourceIp,
                requestHash,
                reason,
                context.Response.StatusCode);
            try
            {
                // Once a write begins, client cancellation cannot be allowed to
                // suppress its completion audit record.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await store.AppendAsync(completed, timeout.Token);
            }
            catch (Exception auditException)
            {
                logger.LogCritical(
                    auditException,
                    "Failed to persist completion audit for correlation {CorrelationId} and administrator {AdminSubject}.",
                    correlationId,
                    subject);
            }
        }
    }

    private AdminAuditEvent CreateEvent(
        HttpContext context,
        string correlationId,
        string phase,
        string subject,
        IReadOnlyList<string> roles,
        string sourceIp,
        string requestHash,
        string? reason,
        int? resultStatus) => new(
            Guid.NewGuid(),
            correlationId,
            phase,
            subject,
            roles,
            sourceIp,
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            requestHash,
            reason,
            AdminAuditEnrichment.GetBefore(context),
            AdminAuditEnrichment.GetAfter(context),
            ServiceVersion,
            resultStatus,
            timeProvider.GetUtcNow());

    private static string GetCorrelationId(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Correlation-ID"];
        return supplied.Count == 1 && Guid.TryParseExact(supplied[0], "D", out var parsed)
            ? parsed.ToString("D")
            : Guid.NewGuid().ToString("D");
    }

    private static string? NormalizeReason(Microsoft.Extensions.Primitives.StringValues values)
    {
        if (values.Count != 1 || values[0] is not { } supplied)
        {
            return null;
        }
        var reason = supplied.Trim();
        return reason.Length is > 0 and <= 512 && !reason.Any(char.IsControl)
            ? reason
            : null;
    }

    private static async Task<string> HashRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, request.Method);
        Append(hash, "\n");
        Append(hash, request.Path.Value ?? "/");
        Append(hash, "\n");
        Append(hash, request.QueryString.Value ?? string.Empty);
        Append(hash, "\n");

        if (request.ContentLength is > MaximumAuditedRequestBodyBytes)
        {
            throw new BadHttpRequestException(
                "The administrator request body exceeds the auditable limit.",
                StatusCodes.Status413PayloadTooLarge);
        }
        if (request.Body.CanRead && request.ContentLength is not 0)
        {
            request.EnableBuffering(
                bufferThreshold: 64 * 1024,
                bufferLimit: MaximumAuditedRequestBodyBytes);
            var buffer = new byte[16 * 1024];
            int read;
            var total = 0;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total = checked(total + read);
                if (total > MaximumAuditedRequestBodyBytes)
                {
                    throw new BadHttpRequestException(
                        "The administrator request body exceeds the auditable limit.",
                        StatusCodes.Status413PayloadTooLarge);
                }
                hash.AppendData(buffer, 0, read);
            }
            request.Body.Position = 0;
            CryptographicOperations.ZeroMemory(buffer);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}

public static class AdminAuditEnrichment
{
    private const string BeforeKey = "pal-control.admin-audit.before";
    private const string AfterKey = "pal-control.admin-audit.after";

    public static void SetBefore(HttpContext context, object? value) =>
        context.Items[BeforeKey] = Serialize(value);

    public static void SetAfter(HttpContext context, object? value) =>
        context.Items[AfterKey] = Serialize(value);

    internal static string? GetBefore(HttpContext context) =>
        context.Items.TryGetValue(BeforeKey, out var value) ? value as string : null;

    internal static string? GetAfter(HttpContext context) =>
        context.Items.TryGetValue(AfterKey, out var value) ? value as string : null;

    private static string? Serialize(object? value) => value is null
        ? null
        : JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

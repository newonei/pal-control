using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed record PalDefenderCommandEnqueueResult(
    CommandStatus? Command,
    bool Created,
    bool IdempotencyConflict,
    bool CapacityExceeded = false);

public sealed record PalDefenderCommandSnapshot(
    Guid CommandId,
    string ServerId,
    string UpstreamPath,
    string IdempotencyKey,
    string RequestHash,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    int? HttpStatus,
    JsonNode? ResponseJson,
    string? ResponseText,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record PalDefenderCommandAuditEvent(
    Guid EventId,
    Guid CommandId,
    string EventType,
    string State,
    DateTimeOffset At,
    string ServerId,
    string UpstreamPath,
    string IdempotencyKey,
    string RequestHash,
    string Reason,
    string Actor,
    int? HttpStatus,
    string? ErrorCode,
    string? ErrorMessage);

internal enum PalDefenderCommandFaultPoint
{
    BeforeDurableDispatch,
    AfterDurableDispatch,
    BeforeTerminalPersistence,
    AfterTerminalPersistence
}

internal interface IPalDefenderCommandFaultInjector
{
    void Inject(PalDefenderCommandFaultPoint point, Guid commandId);
}

public sealed partial class PalDefenderCommandQueue : BackgroundService
{
    private const int MaximumPreDispatchFailures = 5;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Dictionary<Guid, StoredCommand> _commands = [];
    private readonly Dictionary<string, (string Hash, Guid CommandId)> _idempotency =
        new(StringComparer.Ordinal);
    private readonly List<PalDefenderCommandAuditEvent> _audit = [];
    private readonly PalDefenderRestClient _client;
    private readonly EconomySafetyGate _economySafety;
    private readonly ILogger<PalDefenderCommandQueue> _logger;
    private readonly IPalDefenderCommandFaultInjector? _faultInjector;
    private readonly string _connectionString;
    private readonly string _legacyEventPath;
    private readonly string _workerId = Guid.NewGuid().ToString("N");
    private readonly FileStream _instanceLock;
    private readonly int _capacity;
    private volatile bool _storeReady;
    private volatile bool _workerRunning;

    public PalDefenderCommandQueue(
        IOptions<CommandPersistenceOptions> persistenceOptions,
        IOptions<ExtractionPersistenceOptions> extractionPersistenceOptions,
        IHostEnvironment environment,
        PalDefenderRestClient client,
        EconomySafetyGate economySafety,
        ILogger<PalDefenderCommandQueue> logger)
        : this(
            persistenceOptions,
            extractionPersistenceOptions,
            environment,
            client,
            economySafety,
            logger,
            faultInjector: null)
    {
    }

    internal PalDefenderCommandQueue(
        IOptions<CommandPersistenceOptions> persistenceOptions,
        IOptions<ExtractionPersistenceOptions> extractionPersistenceOptions,
        IHostEnvironment environment,
        PalDefenderRestClient client,
        EconomySafetyGate economySafety,
        ILogger<PalDefenderCommandQueue> logger,
        IPalDefenderCommandFaultInjector? faultInjector)
    {
        _client = client;
        _economySafety = economySafety;
        _logger = logger;
        _faultInjector = faultInjector;
        var configuredDirectory = persistenceOptions.Value.DataDirectory;
        _capacity = persistenceOptions.Value.PalDefenderQueueCapacity;
        var dataDirectory = Path.GetFullPath(Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(environment.ContentRootPath, configuredDirectory));
        Directory.CreateDirectory(dataDirectory);
        _legacyEventPath = Path.Combine(dataDirectory, "paldefender-command-audit.jsonl");
        var configuredEconomyDirectory = extractionPersistenceOptions.Value.DataDirectory;
        var economyDirectory = Path.GetFullPath(Path.IsPathRooted(configuredEconomyDirectory)
            ? configuredEconomyDirectory
            : Path.Combine(environment.ContentRootPath, configuredEconomyDirectory));
        Directory.CreateDirectory(economyDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(economyDirectory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _instanceLock = new FileStream(
            Path.Combine(dataDirectory, "paldefender-command-queue.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        try
        {
            InitializeStore();
            ImportLegacyEventsOnce();
            LoadProjection();
            EnsureWritable();
        }
        catch
        {
            _instanceLock.Dispose();
            throw;
        }
    }

    public bool IsReady => _storeReady && _workerRunning;

    public async Task<EconomyQueueSnapshot> GetEconomyLoadAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = _commands.Values.Count(command =>
                command.State is "accepted" or "dispatched");
            return new EconomyQueueSnapshot(IsReady, pending, _capacity);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PalDefenderCommandEnqueueResult> EnqueueAsync(
        string serverId,
        string upstreamPath,
        JsonNode? body,
        string idempotencyKey,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedReason = reason.Trim();
        var requestHash = HashRequest(serverId, upstreamPath, body, normalizedReason);
        var scopedKey = ScopedKey(serverId, idempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_storeReady)
            {
                throw new IOException("The PalDefender command audit store is not writable.");
            }
            if (_idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
                {
                    return new PalDefenderCommandEnqueueResult(null, false, true);
                }
                return new PalDefenderCommandEnqueueResult(
                    ToStatus(_commands[existing.CommandId]),
                    false,
                    false);
            }
            var activeCount = _commands.Values.Count(command =>
                command.State is "accepted" or "dispatched");
            if (activeCount >= _capacity)
            {
                return new PalDefenderCommandEnqueueResult(
                    null,
                    Created: false,
                    IdempotencyConflict: false,
                    CapacityExceeded: true);
            }

            var createdAt = DateTimeOffset.UtcNow;
            var command = new StoredCommand
            {
                CommandId = Guid.NewGuid(),
                ServerId = serverId,
                UpstreamPath = upstreamPath,
                Body = body?.DeepClone(),
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                Reason = normalizedReason,
                Actor = actor,
                State = "accepted",
                CreatedAt = createdAt,
                AvailableAt = createdAt
            };
            var accepted = CreateEvent(
                command,
                "accepted",
                "accepted",
                includeRequestBody: true,
                at: createdAt);
            await PersistAcceptedAsync(command, accepted, cancellationToken);
            ApplyEvent(accepted);
            SignalWorker();
            return new PalDefenderCommandEnqueueResult(ToStatus(command), true, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandStatus?> GetStatusAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _commands.TryGetValue(commandId, out var command)
                ? ToStatus(command)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandStatus?> GetStatusByIdempotencyKeyAsync(
        string serverId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var scopedKey = ScopedKey(serverId, idempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _idempotency.TryGetValue(scopedKey, out var existing) &&
                   _commands.TryGetValue(existing.CommandId, out var command)
                ? ToStatus(command)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PalDefenderCommandSnapshot?> GetSnapshotAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _commands.TryGetValue(commandId, out var command)
                ? ToSnapshot(command)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PalDefenderCommandSnapshot?> GetSnapshotByIdempotencyKeyAsync(
        string serverId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var scopedKey = ScopedKey(serverId, idempotencyKey);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _idempotency.TryGetValue(scopedKey, out var existing) &&
                   _commands.TryGetValue(existing.CommandId, out var command)
                ? ToSnapshot(command)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PalDefenderCommandAuditEvent>> GetAuditAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _audit
                .TakeLast(Math.Clamp(limit, 1, 1000))
                .Reverse()
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _workerRunning = true;
        try
        {
            await RecoverInterruptedCommandsAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var command = await LeaseNextAcceptedCommandAsync(stoppingToken);
                if (command is null)
                {
                    _ = await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }
                try
                {
                    await ProcessAsync(command, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await HandlePreDispatchFailureAsync(
                        command.CommandId,
                        command.LeaseOwner,
                        exception,
                        CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _storeReady = false;
            _logger.LogCritical(exception, "The PalDefender command worker stopped unexpectedly.");
            throw;
        }
        finally
        {
            _workerRunning = false;
        }
    }

    public override void Dispose()
    {
        _instanceLock.Dispose();
        _gate.Dispose();
        _wakeSignal.Dispose();
        base.Dispose();
    }

    private async Task RecoverInterruptedCommandsAsync(CancellationToken cancellationToken)
    {
        Guid[] interrupted;
        Guid[] leasedAccepted;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            interrupted = _commands.Values
                .Where(command => string.Equals(command.State, "dispatched", StringComparison.Ordinal))
                .Select(command => command.CommandId)
                .ToArray();
            leasedAccepted = _commands.Values
                .Where(command =>
                    string.Equals(command.State, "accepted", StringComparison.Ordinal) &&
                    command.LeaseOwner is not null)
                .Select(command => command.CommandId)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var commandId in interrupted)
        {
            await TransitionAsync(
                commandId,
                "recovered-uncertain",
                "uncertain",
                null,
                null,
                null,
                "COMMAND_OUTCOME_UNCERTAIN",
                "The service restarted after dispatch. The PalDefender operation was not sent again automatically.",
                cancellationToken);
        }
        foreach (var commandId in leasedAccepted)
        {
            await ReleaseAcceptedLeaseAsync(
                commandId,
                "recovered-accepted",
                "The service restarted before dispatch; the accepted command is safe to lease again.",
                TimeSpan.Zero,
                cancellationToken);
        }
    }

    private async Task<StoredCommand?> LeaseNextAcceptedCommandAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var command = _commands.Values
                .Where(command =>
                    string.Equals(command.State, "accepted", StringComparison.Ordinal) &&
                    command.AvailableAt <= now &&
                    (command.LeaseOwner is null || command.LeaseUntil <= now))
                .OrderBy(command => command.CreatedAt)
                .FirstOrDefault();
            if (command is null)
            {
                return null;
            }

            var leasedUntil = now.Add(LeaseDuration);
            var leased = CreateEvent(
                command,
                "leased",
                "accepted",
                includeRequestBody: false);
            if (!await PersistLeaseAsync(command, leased, leasedUntil, cancellationToken))
            {
                return null;
            }
            command.LeaseOwner = _workerId;
            command.LeaseUntil = leasedUntil;
            command.AttemptCount++;
            _audit.Add(ToAudit(leased));
            return Clone(command);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessAsync(StoredCommand command, CancellationToken stoppingToken)
    {
        if (!string.Equals(command.State, "accepted", StringComparison.Ordinal) ||
            !string.Equals(command.LeaseOwner, _workerId, StringComparison.Ordinal))
        {
            return;
        }

        IDisposable? economyLease = null;
        if (TryGetEconomyDeliveryPlayer(command, out var playerIdentifier))
        {
            try
            {
                economyLease = await _economySafety.AcquireAsync(
                    EconomyWriteFeature.Purchase,
                    EconomySafetyContext.ForDelivery(playerIdentifier),
                    queue: null,
                    stoppingToken);
            }
            catch (ExtractionModeException exception)
            {
                _logger.LogWarning(
                    "Economy safety gate deferred delivery command {CommandId}: {Code}.",
                    command.CommandId,
                    exception.Code);
                await ReleaseAcceptedLeaseAsync(
                    command.CommandId,
                    "safety-deferred",
                    $"Economy safety gate deferred dispatch ({exception.Code}).",
                    TimeSpan.FromSeconds(1),
                    stoppingToken);
                return;
            }
        }

        using (economyLease)
        {
            await ProcessApprovedCommandAsync(command, stoppingToken);
        }
    }

    private async Task ProcessApprovedCommandAsync(
        StoredCommand command,
        CancellationToken stoppingToken)
    {
        var commandId = command.CommandId;

        // This event is forced to durable storage before the upstream write is attempted.
        _faultInjector?.Inject(PalDefenderCommandFaultPoint.BeforeDurableDispatch, commandId);
        var dispatchPersisted = await TransitionAsync(
            commandId,
            "dispatched",
            "dispatched",
            null,
            null,
            null,
            null,
            null,
            stoppingToken,
            expectedLeaseOwner: command.LeaseOwner);
        if (!dispatchPersisted)
        {
            return;
        }
        _faultInjector?.Inject(PalDefenderCommandFaultPoint.AfterDurableDispatch, commandId);

        PalDefenderApiResponse response;
        try
        {
            response = await _client.PostAsync(
                command.UpstreamPath,
                command.Body,
                stoppingToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "PalDefender command {CommandId} ended unexpectedly after dispatch ({ExceptionType}).",
                commandId,
                exception.GetType().Name);
            await PersistTerminalWithFaultInjectionAsync(
                commandId,
                "uncertain",
                "uncertain",
                null,
                null,
                null,
                "COMMAND_OUTCOME_UNCERTAIN",
                "The PalDefender operation ended unexpectedly after dispatch and will not be retried automatically.",
                CancellationToken.None);
            return;
        }

        var persistenceToken = stoppingToken.IsCancellationRequested
            ? CancellationToken.None
            : stoppingToken;
        if (response.IsSuccess)
        {
            await PersistTerminalWithFaultInjectionAsync(
                commandId,
                "succeeded",
                "succeeded",
                response.StatusCode,
                response.Json,
                response.Text,
                null,
                null,
                persistenceToken);
            return;
        }

        if (response.OutcomeUncertain ||
            (!response.TransportError && response.StatusCode >= 500))
        {
            await PersistTerminalWithFaultInjectionAsync(
                commandId,
                "uncertain",
                "uncertain",
                response.TransportError ? null : response.StatusCode,
                response.Json,
                response.Text,
                response.ErrorCode ?? "COMMAND_OUTCOME_UNCERTAIN",
                response.ErrorMessage ?? "PalDefender returned a server error after dispatch. The operation has an uncertain outcome and will not be retried automatically.",
                persistenceToken);
            return;
        }

        await PersistTerminalWithFaultInjectionAsync(
            commandId,
            "failed",
            "failed",
            response.TransportError ? null : response.StatusCode,
            response.Json,
            response.Text,
            response.ErrorCode ?? "PALDEFENDER_REQUEST_REJECTED",
            response.ErrorMessage ?? $"PalDefender rejected the operation with HTTP status {response.StatusCode}.",
            persistenceToken);
    }

    private async Task PersistTerminalWithFaultInjectionAsync(
        Guid commandId,
        string eventType,
        string state,
        int? httpStatus,
        JsonNode? responseJson,
        string? responseText,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        _faultInjector?.Inject(
            PalDefenderCommandFaultPoint.BeforeTerminalPersistence,
            commandId);
        _ = await TransitionAsync(
            commandId,
            eventType,
            state,
            httpStatus,
            responseJson,
            responseText,
            errorCode,
            errorMessage,
            cancellationToken);
        _faultInjector?.Inject(
            PalDefenderCommandFaultPoint.AfterTerminalPersistence,
            commandId);
    }

    private async Task<bool> TransitionAsync(
        Guid commandId,
        string eventType,
        string state,
        int? httpStatus,
        JsonNode? responseJson,
        string? responseText,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken,
        string? expectedLeaseOwner = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(commandId, out var command))
            {
                return false;
            }
            if (expectedLeaseOwner is not null &&
                !string.Equals(command.LeaseOwner, expectedLeaseOwner, StringComparison.Ordinal))
            {
                return false;
            }
            var stored = CreateEvent(
                command,
                eventType,
                state,
                includeRequestBody: false,
                httpStatus,
                responseJson,
                responseText,
                errorCode,
                errorMessage);
            if (!await PersistTransitionAsync(
                    command,
                    stored,
                    expectedLeaseOwner,
                    cancellationToken))
            {
                return false;
            }
            ApplyEvent(stored);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryGetEconomyDeliveryPlayer(
        StoredCommand command,
        out string playerIdentifier)
    {
        const string prefix = "give/items/";
        if (string.Equals(
                command.Actor,
                "extraction-delivery-worker",
                StringComparison.Ordinal) &&
            command.UpstreamPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            command.UpstreamPath.Length > prefix.Length)
        {
            playerIdentifier = Uri.UnescapeDataString(command.UpstreamPath[prefix.Length..]);
            return !string.IsNullOrWhiteSpace(playerIdentifier);
        }
        playerIdentifier = string.Empty;
        return false;
    }

    private void InitializeStore()
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
            CREATE TABLE IF NOT EXISTS paldefender_commands (
                command_id TEXT PRIMARY KEY,
                server_id TEXT NOT NULL,
                upstream_path TEXT NOT NULL,
                request_body TEXT NULL
                    CHECK (request_body IS NULL OR json_valid(request_body)),
                idempotency_key TEXT NOT NULL,
                request_hash TEXT NOT NULL CHECK (length(request_hash) = 64),
                reason TEXT NOT NULL,
                actor TEXT NOT NULL,
                state TEXT NOT NULL CHECK (
                    state IN ('accepted', 'dispatched', 'succeeded', 'failed', 'uncertain')),
                created_at TEXT NOT NULL,
                available_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                dispatched_at TEXT NULL,
                completed_at TEXT NULL,
                http_status INTEGER NULL,
                response_json TEXT NULL
                    CHECK (response_json IS NULL OR json_valid(response_json)),
                response_text TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                lease_owner TEXT NULL,
                lease_until TEXT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                failure_count INTEGER NOT NULL DEFAULT 0 CHECK (failure_count >= 0),
                dead_lettered_at TEXT NULL,
                UNIQUE (server_id, idempotency_key),
                CHECK ((lease_owner IS NULL) = (lease_until IS NULL)),
                CHECK (state = 'accepted' OR lease_owner IS NULL),
                CHECK ((state IN ('succeeded', 'failed', 'uncertain')) = (completed_at IS NOT NULL)),
                CHECK (dead_lettered_at IS NULL OR state = 'failed')
            );
            CREATE INDEX IF NOT EXISTS ix_paldefender_commands_dispatch
            ON paldefender_commands (state, available_at, lease_until, created_at);
            CREATE TRIGGER IF NOT EXISTS paldefender_commands_state_transition
            BEFORE UPDATE OF state ON paldefender_commands
            WHEN NOT (
                OLD.state = NEW.state OR
                (OLD.state = 'accepted' AND NEW.state IN ('dispatched', 'failed')) OR
                (OLD.state = 'dispatched' AND NEW.state IN ('succeeded', 'failed', 'uncertain'))
            )
            BEGIN
                SELECT RAISE(ABORT, 'invalid paldefender command state transition');
            END;
            CREATE TRIGGER IF NOT EXISTS paldefender_commands_envelope_immutable
            BEFORE UPDATE OF command_id, server_id, upstream_path, request_body,
                             idempotency_key, request_hash, reason, actor, created_at
            ON paldefender_commands
            BEGIN
                SELECT RAISE(ABORT, 'paldefender command envelope is immutable');
            END;
            CREATE TABLE IF NOT EXISTS paldefender_command_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                command_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                state TEXT NOT NULL,
                at TEXT NOT NULL,
                server_id TEXT NOT NULL,
                upstream_path TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                request_hash TEXT NOT NULL,
                reason TEXT NOT NULL,
                actor TEXT NOT NULL,
                request_body TEXT NULL
                    CHECK (request_body IS NULL OR json_valid(request_body)),
                http_status INTEGER NULL,
                response_json TEXT NULL
                    CHECK (response_json IS NULL OR json_valid(response_json)),
                response_text TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                FOREIGN KEY (command_id) REFERENCES paldefender_commands(command_id)
            );
            CREATE INDEX IF NOT EXISTS ix_paldefender_command_events_command
            ON paldefender_command_events (command_id, sequence);
            CREATE TABLE IF NOT EXISTS paldefender_command_migrations (
                migration_key TEXT PRIMARY KEY,
                source_file TEXT NOT NULL,
                source_sha256 TEXT NOT NULL,
                source_bytes INTEGER NOT NULL CHECK (source_bytes >= 0),
                imported_event_count INTEGER NOT NULL CHECK (imported_event_count >= 0),
                imported_at TEXT NOT NULL
            );
            CREATE TRIGGER IF NOT EXISTS paldefender_command_events_no_update
            BEFORE UPDATE ON paldefender_command_events
            BEGIN
                SELECT RAISE(ABORT, 'paldefender command events are immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS paldefender_command_events_no_delete
            BEFORE DELETE ON paldefender_command_events
            BEGIN
                SELECT RAISE(ABORT, 'paldefender command events are immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS paldefender_command_events_envelope_match
            BEFORE INSERT ON paldefender_command_events
            WHEN NOT EXISTS (
                SELECT 1 FROM paldefender_commands AS command
                WHERE command.command_id = NEW.command_id
                  AND command.server_id = NEW.server_id
                  AND command.upstream_path = NEW.upstream_path
                  AND command.idempotency_key = NEW.idempotency_key
                  AND command.request_hash = NEW.request_hash
                  AND command.reason = NEW.reason
                  AND command.actor = NEW.actor
            )
            BEGIN
                SELECT RAISE(ABORT, 'paldefender event envelope does not match command');
            END;
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('paldefender-command-outbox', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void ImportLegacyEventsOnce()
    {
        using var connection = Open();
        using (var marker = connection.CreateCommand())
        {
            marker.CommandText = """
                SELECT source_sha256 FROM paldefender_command_migrations
                WHERE migration_key = 'paldefender-jsonl-v1' LIMIT 1;
                """;
            if (marker.ExecuteScalar() is string importedSourceHash)
            {
                ArchiveLegacyEventFile(importedSourceHash);
                return;
            }
        }

        using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT COUNT(*) FROM paldefender_commands;";
            if (Convert.ToInt64(existing.ExecuteScalar()) != 0)
            {
                throw new InvalidDataException(
                    "PalDefender SQLite commands exist without the completed legacy migration marker.");
            }
        }

        var sourceBytes = File.Exists(_legacyEventPath)
            ? File.ReadAllBytes(_legacyEventPath)
            : [];
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var legacyEvents = ParseLegacyEvents(sourceBytes);
        var projection = BuildLegacyProjection(legacyEvents);
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var command in projection.Values.OrderBy(item => item.CreatedAt))
        {
            InsertCommand(connection, transaction, command);
        }
        foreach (var stored in legacyEvents)
        {
            InsertEvent(connection, transaction, stored);
        }
        using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = """
                INSERT INTO paldefender_command_migrations (
                    migration_key, source_file, source_sha256, source_bytes,
                    imported_event_count, imported_at)
                VALUES ('paldefender-jsonl-v1', $sourceFile, $sourceSha256,
                        $sourceBytes, $eventCount, $importedAt);
                """;
            marker.Parameters.AddWithValue("$sourceFile", Path.GetFileName(_legacyEventPath));
            marker.Parameters.AddWithValue(
                "$sourceSha256",
                sourceHash);
            marker.Parameters.AddWithValue("$sourceBytes", sourceBytes.LongLength);
            marker.Parameters.AddWithValue("$eventCount", legacyEvents.Count);
            marker.Parameters.AddWithValue("$importedAt", DateTimeOffset.UtcNow.ToString("O"));
            marker.ExecuteNonQuery();
        }
        transaction.Commit();
        ArchiveLegacyEventFile(sourceHash);
    }

    private void ArchiveLegacyEventFile(string expectedHash)
    {
        var archivePath = _legacyEventPath + ".migrated-to-sqlite-v1";
        if (File.Exists(archivePath))
        {
            if (!string.Equals(HashFile(archivePath), expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The archived legacy PalDefender JSONL does not match its SQLite migration marker.");
            }
            if (File.Exists(_legacyEventPath))
            {
                throw new InvalidDataException(
                    "Both active and archived legacy PalDefender JSONL files exist after migration.");
            }
            return;
        }
        if (!File.Exists(_legacyEventPath))
        {
            return;
        }
        if (!string.Equals(HashFile(_legacyEventPath), expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The legacy PalDefender JSONL changed after its SQLite migration committed.");
        }
        File.Move(_legacyEventPath, archivePath);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private IReadOnlyList<QueueEvent> ParseLegacyEvents(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return [];
        }
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');
        var hasCompleteFinalLine = bytes[^1] == (byte)'\n';
        var events = new List<QueueEvent>();
        var eventIds = new HashSet<Guid>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (index == 0)
            {
                line = line.TrimStart('\uFEFF');
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            QueueEvent? stored;
            try
            {
                stored = JsonSerializer.Deserialize<QueueEvent>(line, JsonOptions);
            }
            catch (JsonException exception) when (
                index == lines.Length - 1 && !hasCompleteFinalLine)
            {
                _logger.LogWarning(
                    "Ignoring a partial final legacy PalDefender event during one-time SQLite import: {ErrorType}.",
                    exception.GetType().Name);
                break;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Legacy PalDefender command event {index + 1} is invalid JSON.",
                    exception);
            }
            if (stored is null || stored.EventId == Guid.Empty || stored.CommandId == Guid.Empty ||
                !eventIds.Add(stored.EventId))
            {
                throw new InvalidDataException(
                    $"Legacy PalDefender command event {index + 1} is empty or duplicated.");
            }
            ValidateEventEnvelope(stored, index + 1);
            events.Add(stored);
        }
        return events;
    }

    private static Dictionary<Guid, StoredCommand> BuildLegacyProjection(
        IReadOnlyList<QueueEvent> events)
    {
        var commands = new Dictionary<Guid, StoredCommand>();
        var idempotency = new Dictionary<string, (string Hash, Guid CommandId)>(StringComparer.Ordinal);
        foreach (var stored in events)
        {
            if (string.Equals(stored.EventType, "accepted", StringComparison.Ordinal))
            {
                if (commands.ContainsKey(stored.CommandId))
                {
                    throw new InvalidDataException(
                        $"Legacy command '{stored.CommandId}' contains multiple accepted events.");
                }
                var scopedKey = ScopedKey(stored.ServerId, stored.IdempotencyKey);
                if (idempotency.TryGetValue(scopedKey, out var existing))
                {
                    throw new InvalidDataException(
                        $"Legacy idempotency key '{stored.IdempotencyKey}' maps to commands '{existing.CommandId}' and '{stored.CommandId}'.");
                }
                var acceptedCommand = new StoredCommand
                {
                    CommandId = stored.CommandId,
                    ServerId = stored.ServerId,
                    UpstreamPath = stored.UpstreamPath,
                    Body = stored.Body?.DeepClone(),
                    IdempotencyKey = stored.IdempotencyKey,
                    RequestHash = stored.RequestHash,
                    Reason = stored.Reason,
                    Actor = stored.Actor,
                    State = "accepted",
                    CreatedAt = stored.At,
                    AvailableAt = stored.At
                };
                commands.Add(acceptedCommand.CommandId, acceptedCommand);
                idempotency.Add(
                    scopedKey,
                    (acceptedCommand.RequestHash, acceptedCommand.CommandId));
                continue;
            }
            if (!commands.TryGetValue(stored.CommandId, out var command))
            {
                throw new InvalidDataException(
                    $"Legacy PalDefender event '{stored.EventId}' references an unknown command.");
            }
            if (!EventMatchesCommand(stored, command))
            {
                throw new InvalidDataException(
                    $"Legacy PalDefender event '{stored.EventId}' changes its immutable command envelope.");
            }
            if (command.State is "succeeded" or "failed" or "uncertain")
            {
                throw new InvalidDataException(
                    $"Legacy PalDefender command '{command.CommandId}' changes after a terminal state.");
            }
            command.State = stored.State;
            command.HttpStatus = stored.HttpStatus;
            command.ResponseJson = stored.ResponseJson?.DeepClone();
            command.ResponseText = stored.ResponseText;
            command.ErrorCode = stored.ErrorCode;
            command.ErrorMessage = stored.ErrorMessage;
            if (stored.State == "dispatched")
            {
                command.DispatchedAt = stored.At;
                command.AttemptCount = Math.Max(1, command.AttemptCount);
            }
            if (stored.State is "succeeded" or "failed" or "uncertain")
            {
                command.CompletedAt = stored.At;
            }
        }
        return commands;
    }

    private void LoadProjection()
    {
        _commands.Clear();
        _idempotency.Clear();
        _audit.Clear();
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT command_id, server_id, upstream_path, request_body,
                       idempotency_key, request_hash, reason, actor, state,
                       created_at, available_at, dispatched_at, completed_at,
                       http_status, response_json, response_text, error_code,
                       error_message, lease_owner, lease_until, attempt_count,
                       failure_count, dead_lettered_at
                FROM paldefender_commands ORDER BY created_at, command_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var stored = new StoredCommand
                {
                    CommandId = Guid.Parse(reader.GetString(0)),
                    ServerId = reader.GetString(1),
                    UpstreamPath = reader.GetString(2),
                    Body = ParseJson(reader, 3),
                    IdempotencyKey = reader.GetString(4),
                    RequestHash = reader.GetString(5),
                    Reason = reader.GetString(6),
                    Actor = reader.GetString(7),
                    State = reader.GetString(8),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(9)),
                    AvailableAt = DateTimeOffset.Parse(reader.GetString(10)),
                    DispatchedAt = ParseDateTime(reader, 11),
                    CompletedAt = ParseDateTime(reader, 12),
                    HttpStatus = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                    ResponseJson = ParseJson(reader, 14),
                    ResponseText = reader.IsDBNull(15) ? null : reader.GetString(15),
                    ErrorCode = reader.IsDBNull(16) ? null : reader.GetString(16),
                    ErrorMessage = reader.IsDBNull(17) ? null : reader.GetString(17),
                    LeaseOwner = reader.IsDBNull(18) ? null : reader.GetString(18),
                    LeaseUntil = ParseDateTime(reader, 19),
                    AttemptCount = reader.GetInt32(20),
                    FailureCount = reader.GetInt32(21),
                    DeadLetteredAt = ParseDateTime(reader, 22)
                };
                ValidateLoadedCommand(stored);
                _commands.Add(stored.CommandId, stored);
                _idempotency.Add(
                    ScopedKey(stored.ServerId, stored.IdempotencyKey),
                    (stored.RequestHash, stored.CommandId));
            }
        }
        using (var events = connection.CreateCommand())
        {
            events.CommandText = """
                SELECT event_id, command_id, event_type, state, at, server_id,
                       upstream_path, idempotency_key, request_hash, reason,
                       actor, http_status, error_code, error_message
                FROM paldefender_command_events ORDER BY sequence;
                """;
            using var reader = events.ExecuteReader();
            while (reader.Read())
            {
                _audit.Add(new PalDefenderCommandAuditEvent(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4)),
                    reader.GetString(5),
                    AuditSafePath(reader.GetString(6)),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13)));
            }
        }
    }

    private void ApplyEvent(QueueEvent stored)
    {
        if (string.Equals(stored.EventType, "accepted", StringComparison.Ordinal))
        {
            var scopedKey = ScopedKey(stored.ServerId, stored.IdempotencyKey);
            if (_idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, stored.RequestHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"PalDefender idempotency key '{stored.IdempotencyKey}' has conflicting hashes.");
                }
                _audit.Add(ToAudit(stored));
                return;
            }

            var command = new StoredCommand
            {
                CommandId = stored.CommandId,
                ServerId = stored.ServerId,
                UpstreamPath = stored.UpstreamPath,
                Body = stored.Body,
                IdempotencyKey = stored.IdempotencyKey,
                RequestHash = stored.RequestHash,
                Reason = stored.Reason,
                Actor = stored.Actor,
                State = stored.State,
                CreatedAt = stored.At,
                AvailableAt = stored.At
            };
            _commands[command.CommandId] = command;
            _idempotency[scopedKey] = (command.RequestHash, command.CommandId);
        }
        else if (_commands.TryGetValue(stored.CommandId, out var command))
        {
            command.State = stored.State;
            command.HttpStatus = stored.HttpStatus;
            command.ResponseJson = stored.ResponseJson;
            command.ResponseText = stored.ResponseText;
            command.ErrorCode = stored.ErrorCode;
            command.ErrorMessage = stored.ErrorMessage;
            if (stored.State == "dispatched")
            {
                command.DispatchedAt = stored.At;
                command.LeaseOwner = null;
                command.LeaseUntil = null;
            }
            if (stored.State is "succeeded" or "failed" or "uncertain")
            {
                command.CompletedAt = stored.At;
                command.LeaseOwner = null;
                command.LeaseUntil = null;
            }
        }
        else
        {
            throw new InvalidDataException(
                $"PalDefender command event {stored.EventId} references an unknown command.");
        }

        _audit.Add(ToAudit(stored));
    }

    private async Task PersistAcceptedAsync(
        StoredCommand command,
        QueueEvent accepted,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await InsertCommandAsync(connection, transaction, command, cancellationToken);
            await InsertEventAsync(connection, transaction, accepted, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _storeReady = true;
        }
        catch (SqliteException)
        {
            _storeReady = false;
            throw;
        }
    }

    private async Task<bool> PersistTransitionAsync(
        StoredCommand command,
        QueueEvent stored,
        string? expectedLeaseOwner,
        CancellationToken cancellationToken)
    {
        var expectedState = stored.State == "dispatched" ? "accepted" : "dispatched";
        try
        {
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE paldefender_commands
                SET state = $state,
                    updated_at = $at,
                    dispatched_at = CASE
                        WHEN $state = 'dispatched' THEN $at ELSE dispatched_at END,
                    completed_at = CASE
                        WHEN $terminal = 1 THEN $at ELSE NULL END,
                    http_status = $httpStatus,
                    response_json = $responseJson,
                    response_text = $responseText,
                    error_code = $errorCode,
                    error_message = $errorMessage,
                    lease_owner = NULL,
                    lease_until = NULL
                WHERE command_id = $commandId
                  AND state = $expectedState
                  AND ($expectedLeaseOwner IS NULL OR lease_owner = $expectedLeaseOwner);
                """;
            update.Parameters.AddWithValue("$state", stored.State);
            update.Parameters.AddWithValue("$at", stored.At.ToString("O"));
            update.Parameters.AddWithValue(
                "$terminal",
                stored.State is "succeeded" or "failed" or "uncertain" ? 1 : 0);
            AddNullable(update, "$httpStatus", stored.HttpStatus);
            AddNullable(update, "$responseJson", SerializeJson(stored.ResponseJson));
            AddNullable(update, "$responseText", stored.ResponseText);
            AddNullable(update, "$errorCode", stored.ErrorCode);
            AddNullable(update, "$errorMessage", stored.ErrorMessage);
            update.Parameters.AddWithValue("$commandId", command.CommandId.ToString("D"));
            update.Parameters.AddWithValue("$expectedState", expectedState);
            AddNullable(update, "$expectedLeaseOwner", expectedLeaseOwner);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
            await InsertEventAsync(connection, transaction, stored, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _storeReady = true;
            return true;
        }
        catch (SqliteException)
        {
            _storeReady = false;
            throw;
        }
    }

    private async Task<bool> PersistLeaseAsync(
        StoredCommand command,
        QueueEvent leased,
        DateTimeOffset leasedUntil,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE paldefender_commands
                SET lease_owner = $leaseOwner,
                    lease_until = $leaseUntil,
                    attempt_count = attempt_count + 1,
                    updated_at = $at
                WHERE command_id = $commandId
                  AND state = 'accepted'
                  AND available_at <= $at
                  AND (lease_owner IS NULL OR lease_until <= $at);
                """;
            update.Parameters.AddWithValue("$leaseOwner", _workerId);
            update.Parameters.AddWithValue("$leaseUntil", leasedUntil.ToString("O"));
            update.Parameters.AddWithValue("$at", leased.At.ToString("O"));
            update.Parameters.AddWithValue("$commandId", command.CommandId.ToString("D"));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
            await InsertEventAsync(connection, transaction, leased, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _storeReady = true;
            return true;
        }
        catch (SqliteException)
        {
            _storeReady = false;
            throw;
        }
    }

    private async Task ReleaseAcceptedLeaseAsync(
        Guid commandId,
        string eventType,
        string message,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(commandId, out var command) ||
                command.State != "accepted" || command.LeaseOwner is null)
            {
                return;
            }
            var availableAt = DateTimeOffset.UtcNow.Add(delay);
            var stored = CreateEvent(
                command,
                eventType,
                "accepted",
                includeRequestBody: false,
                errorCode: eventType == "safety-deferred" ? "ECONOMY_SAFETY_DEFERRED" : null,
                errorMessage: message);
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE paldefender_commands
                SET lease_owner = NULL, lease_until = NULL,
                    available_at = $availableAt, updated_at = $at,
                    error_code = $errorCode, error_message = $errorMessage
                WHERE command_id = $commandId AND state = 'accepted'
                  AND lease_owner = $leaseOwner;
                """;
            update.Parameters.AddWithValue("$availableAt", availableAt.ToString("O"));
            update.Parameters.AddWithValue("$at", stored.At.ToString("O"));
            AddNullable(update, "$errorCode", stored.ErrorCode);
            AddNullable(update, "$errorMessage", stored.ErrorMessage);
            update.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            update.Parameters.AddWithValue("$leaseOwner", command.LeaseOwner);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
            await InsertEventAsync(connection, transaction, stored, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            command.LeaseOwner = null;
            command.LeaseUntil = null;
            command.AvailableAt = availableAt;
            command.ErrorCode = stored.ErrorCode;
            command.ErrorMessage = stored.ErrorMessage;
            _audit.Add(ToAudit(stored));
            _storeReady = true;
            SignalWorker();
        }
        catch (SqliteException)
        {
            _storeReady = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandlePreDispatchFailureAsync(
        Guid commandId,
        string? leaseOwner,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(commandId, out var command))
            {
                return;
            }
            if (command.State == "dispatched")
            {
                var uncertain = CreateEvent(
                    command,
                    "persistence-interrupted-uncertain",
                    "uncertain",
                    includeRequestBody: false,
                    errorCode: "COMMAND_OUTCOME_UNCERTAIN",
                    errorMessage: "Command processing was interrupted after durable dispatch; it will not be sent again automatically.");
                if (await PersistTransitionAsync(
                        command,
                        uncertain,
                        expectedLeaseOwner: null,
                        cancellationToken))
                {
                    ApplyEvent(uncertain);
                }
                return;
            }
            if (command.State != "accepted" || leaseOwner is null ||
                !string.Equals(command.LeaseOwner, leaseOwner, StringComparison.Ordinal))
            {
                return;
            }

            var failureCount = command.FailureCount + 1;
            var deadLetter = failureCount >= MaximumPreDispatchFailures;
            var availableAt = DateTimeOffset.UtcNow.AddSeconds(
                Math.Min(30, Math.Pow(2, failureCount)));
            var stored = CreateEvent(
                command,
                deadLetter ? "dead-lettered" : "retry-scheduled",
                deadLetter ? "failed" : "accepted",
                includeRequestBody: false,
                errorCode: deadLetter
                    ? "COMMAND_DEAD_LETTERED"
                    : "COMMAND_PRE_DISPATCH_RETRY",
                errorMessage: deadLetter
                    ? "The command reached the pre-dispatch failure limit and requires manual review."
                    : $"A pre-dispatch {exception.GetType().Name} scheduled a safe retry.");
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE paldefender_commands
                SET state = $state,
                    lease_owner = NULL,
                    lease_until = NULL,
                    available_at = $availableAt,
                    updated_at = $at,
                    completed_at = $completedAt,
                    failure_count = $failureCount,
                    dead_lettered_at = $deadLetteredAt,
                    error_code = $errorCode,
                    error_message = $errorMessage
                WHERE command_id = $commandId AND state = 'accepted'
                  AND lease_owner = $leaseOwner;
                """;
            update.Parameters.AddWithValue("$state", stored.State);
            update.Parameters.AddWithValue("$availableAt", availableAt.ToString("O"));
            update.Parameters.AddWithValue("$at", stored.At.ToString("O"));
            AddNullable(update, "$completedAt", deadLetter ? stored.At.ToString("O") : null);
            update.Parameters.AddWithValue("$failureCount", failureCount);
            AddNullable(update, "$deadLetteredAt", deadLetter ? stored.At.ToString("O") : null);
            update.Parameters.AddWithValue("$errorCode", stored.ErrorCode!);
            update.Parameters.AddWithValue("$errorMessage", stored.ErrorMessage!);
            update.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            update.Parameters.AddWithValue("$leaseOwner", leaseOwner);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
            await InsertEventAsync(connection, transaction, stored, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            command.FailureCount = failureCount;
            command.AvailableAt = availableAt;
            command.LeaseOwner = null;
            command.LeaseUntil = null;
            if (deadLetter)
            {
                command.DeadLetteredAt = stored.At;
            }
            ApplyEvent(stored);
            _storeReady = true;
            if (!deadLetter)
            {
                SignalWorker();
            }
            _logger.LogWarning(
                "PalDefender command {CommandId} had pre-dispatch failure {FailureCount}/{FailureLimit}; dead-letter={DeadLetter}.",
                commandId,
                failureCount,
                MaximumPreDispatchFailures,
                deadLetter);
        }
        catch (SqliteException)
        {
            _storeReady = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureWritable()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM paldefender_commands;";
        _ = command.ExecuteScalar();
        transaction.Rollback();
        _storeReady = true;
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

    private static void InsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredCommand stored)
    {
        using var command = CreateInsertCommand(connection, transaction, stored);
        command.ExecuteNonQuery();
    }

    private static async Task InsertCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredCommand stored,
        CancellationToken cancellationToken)
    {
        await using var command = CreateInsertCommand(connection, transaction, stored);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateInsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredCommand stored)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO paldefender_commands (
                command_id, server_id, upstream_path, request_body,
                idempotency_key, request_hash, reason, actor, state,
                created_at, available_at, updated_at, dispatched_at,
                completed_at, http_status, response_json, response_text,
                error_code, error_message, lease_owner, lease_until,
                attempt_count, failure_count, dead_lettered_at)
            VALUES (
                $commandId, $serverId, $upstreamPath, $requestBody,
                $idempotencyKey, $requestHash, $reason, $actor, $state,
                $createdAt, $availableAt, $updatedAt, $dispatchedAt,
                $completedAt, $httpStatus, $responseJson, $responseText,
                $errorCode, $errorMessage, $leaseOwner, $leaseUntil,
                $attemptCount, $failureCount, $deadLetteredAt);
            """;
        command.Parameters.AddWithValue("$commandId", stored.CommandId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", stored.ServerId);
        command.Parameters.AddWithValue("$upstreamPath", stored.UpstreamPath);
        AddNullable(command, "$requestBody", SerializeJson(stored.Body));
        command.Parameters.AddWithValue("$idempotencyKey", stored.IdempotencyKey);
        command.Parameters.AddWithValue("$requestHash", stored.RequestHash);
        command.Parameters.AddWithValue("$reason", stored.Reason);
        command.Parameters.AddWithValue("$actor", stored.Actor);
        command.Parameters.AddWithValue("$state", stored.State);
        command.Parameters.AddWithValue("$createdAt", stored.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$availableAt", stored.AvailableAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$updatedAt",
            (stored.CompletedAt ?? stored.DispatchedAt ?? stored.CreatedAt).ToString("O"));
        AddNullable(command, "$dispatchedAt", stored.DispatchedAt?.ToString("O"));
        AddNullable(command, "$completedAt", stored.CompletedAt?.ToString("O"));
        AddNullable(command, "$httpStatus", stored.HttpStatus);
        AddNullable(command, "$responseJson", SerializeJson(stored.ResponseJson));
        AddNullable(command, "$responseText", stored.ResponseText);
        AddNullable(command, "$errorCode", stored.ErrorCode);
        AddNullable(command, "$errorMessage", stored.ErrorMessage);
        AddNullable(command, "$leaseOwner", stored.LeaseOwner);
        AddNullable(command, "$leaseUntil", stored.LeaseUntil?.ToString("O"));
        command.Parameters.AddWithValue("$attemptCount", stored.AttemptCount);
        command.Parameters.AddWithValue("$failureCount", stored.FailureCount);
        AddNullable(command, "$deadLetteredAt", stored.DeadLetteredAt?.ToString("O"));
        return command;
    }

    private static void InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QueueEvent stored)
    {
        using var command = CreateInsertEvent(connection, transaction, stored);
        command.ExecuteNonQuery();
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QueueEvent stored,
        CancellationToken cancellationToken)
    {
        await using var command = CreateInsertEvent(connection, transaction, stored);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateInsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QueueEvent stored)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO paldefender_command_events (
                event_id, command_id, event_type, state, at, server_id,
                upstream_path, idempotency_key, request_hash, reason, actor,
                request_body, http_status, response_json, response_text,
                error_code, error_message)
            VALUES (
                $eventId, $commandId, $eventType, $state, $at, $serverId,
                $upstreamPath, $idempotencyKey, $requestHash, $reason, $actor,
                $requestBody, $httpStatus, $responseJson, $responseText,
                $errorCode, $errorMessage);
            """;
        command.Parameters.AddWithValue("$eventId", stored.EventId.ToString("D"));
        command.Parameters.AddWithValue("$commandId", stored.CommandId.ToString("D"));
        command.Parameters.AddWithValue("$eventType", stored.EventType);
        command.Parameters.AddWithValue("$state", stored.State);
        command.Parameters.AddWithValue("$at", stored.At.ToString("O"));
        command.Parameters.AddWithValue("$serverId", stored.ServerId);
        command.Parameters.AddWithValue("$upstreamPath", stored.UpstreamPath);
        command.Parameters.AddWithValue("$idempotencyKey", stored.IdempotencyKey);
        command.Parameters.AddWithValue("$requestHash", stored.RequestHash);
        command.Parameters.AddWithValue("$reason", stored.Reason);
        command.Parameters.AddWithValue("$actor", stored.Actor);
        AddNullable(command, "$requestBody", SerializeJson(stored.Body));
        AddNullable(command, "$httpStatus", stored.HttpStatus);
        AddNullable(command, "$responseJson", SerializeJson(stored.ResponseJson));
        AddNullable(command, "$responseText", stored.ResponseText);
        AddNullable(command, "$errorCode", stored.ErrorCode);
        AddNullable(command, "$errorMessage", stored.ErrorMessage);
        return command;
    }

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? SerializeJson(JsonNode? value) =>
        value?.ToJsonString(JsonOptions);

    private static JsonNode? ParseJson(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : JsonNode.Parse(reader.GetString(ordinal));

    private static DateTimeOffset? ParseDateTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static void ValidateEventEnvelope(QueueEvent stored, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(stored.EventType) ||
            string.IsNullOrWhiteSpace(stored.ServerId) ||
            string.IsNullOrWhiteSpace(stored.UpstreamPath) ||
            string.IsNullOrWhiteSpace(stored.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(stored.Reason) ||
            string.IsNullOrWhiteSpace(stored.Actor) ||
            !IsValidState(stored.State) ||
            stored.RequestHash.Length != 64 ||
            stored.RequestHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"Legacy PalDefender command event {lineNumber} has an invalid envelope.");
        }
        if (string.Equals(stored.EventType, "accepted", StringComparison.Ordinal) !=
            string.Equals(stored.State, "accepted", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Legacy PalDefender command event {lineNumber} has an invalid accepted state.");
        }
    }

    private static bool EventMatchesCommand(QueueEvent stored, StoredCommand command) =>
        stored.CommandId == command.CommandId &&
        string.Equals(stored.ServerId, command.ServerId, StringComparison.Ordinal) &&
        string.Equals(stored.UpstreamPath, command.UpstreamPath, StringComparison.Ordinal) &&
        string.Equals(stored.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(stored.RequestHash, command.RequestHash, StringComparison.Ordinal) &&
        string.Equals(stored.Reason, command.Reason, StringComparison.Ordinal) &&
        string.Equals(stored.Actor, command.Actor, StringComparison.Ordinal);

    private static void ValidateLoadedCommand(StoredCommand command)
    {
        if (command.CommandId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.ServerId) ||
            string.IsNullOrWhiteSpace(command.IdempotencyKey) ||
            !IsValidState(command.State) ||
            command.RequestHash.Length != 64 ||
            command.RequestHash.Any(character => !Uri.IsHexDigit(character)) ||
            (command.State == "dispatched" && command.DispatchedAt is null) ||
            (command.State is "succeeded" or "failed" or "uncertain") !=
                (command.CompletedAt is not null))
        {
            throw new InvalidDataException(
                $"Persisted PalDefender command '{command.CommandId}' is invalid.");
        }
    }

    private static bool IsValidState(string state) =>
        state is "accepted" or "dispatched" or "succeeded" or "failed" or "uncertain";

    private void SignalWorker()
    {
        if (_wakeSignal.CurrentCount == 0)
        {
            _wakeSignal.Release();
        }
    }

    private static QueueEvent CreateEvent(
        StoredCommand command,
        string eventType,
        string state,
        bool includeRequestBody,
        int? httpStatus = null,
        JsonNode? responseJson = null,
        string? responseText = null,
        string? errorCode = null,
        string? errorMessage = null,
        DateTimeOffset? at = null) => new(
            EventId: Guid.NewGuid(),
            CommandId: command.CommandId,
            EventType: eventType,
            State: state,
            At: at ?? DateTimeOffset.UtcNow,
            ServerId: command.ServerId,
            UpstreamPath: command.UpstreamPath,
            IdempotencyKey: command.IdempotencyKey,
            RequestHash: command.RequestHash,
            Reason: command.Reason,
            Actor: command.Actor,
            Body: includeRequestBody ? command.Body : null,
            HttpStatus: httpStatus,
            ResponseJson: responseJson,
            ResponseText: responseText,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage);

    private static PalDefenderCommandAuditEvent ToAudit(QueueEvent stored) => new(
        stored.EventId,
        stored.CommandId,
        stored.EventType,
        stored.State,
        stored.At,
        stored.ServerId,
        AuditSafePath(stored.UpstreamPath),
        stored.IdempotencyKey,
        stored.RequestHash,
        stored.Reason,
        stored.Actor,
        stored.HttpStatus,
        stored.ErrorCode,
        stored.ErrorMessage);

    private static CommandStatus ToStatus(StoredCommand command)
    {
        object? result = command.HttpStatus is null
            ? null
            : new
            {
                upstreamPath = AuditSafePath(command.UpstreamPath),
                httpStatus = command.HttpStatus,
                body = command.ResponseJson,
                text = command.ResponseJson is null ? command.ResponseText : null
            };
        return new CommandStatus(
            command.CommandId,
            command.State,
            command.CreatedAt,
            command.CompletedAt,
            result,
            command.ErrorCode is null
                ? null
                : new ApiError(
                    command.ErrorCode,
                    command.ErrorMessage ?? "The PalDefender command failed."),
            $"/api/v1/paldefender-commands/{command.CommandId}");
    }

    private static PalDefenderCommandSnapshot ToSnapshot(StoredCommand command) => new(
        command.CommandId,
        command.ServerId,
        command.UpstreamPath,
        command.IdempotencyKey,
        command.RequestHash,
        command.State,
        command.CreatedAt,
        command.CompletedAt,
        command.HttpStatus,
        command.ResponseJson?.DeepClone(),
        command.ResponseText,
        command.ErrorCode,
        command.ErrorMessage);

    private static string AuditSafePath(string upstreamPath)
    {
        const string playerItemPrefix = "give/items/";
        return upstreamPath.StartsWith(playerItemPrefix, StringComparison.OrdinalIgnoreCase) &&
               upstreamPath.Length > playerItemPrefix.Length
            ? $"{playerItemPrefix}[redacted]"
            : upstreamPath;
    }

    private static string HashRequest(
        string serverId,
        string upstreamPath,
        JsonNode? body,
        string reason)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", "paldefender.post");
            writer.WriteString("serverId", serverId);
            writer.WriteString("upstreamPath", upstreamPath);
            writer.WriteString("reason", reason);
            writer.WritePropertyName("body");
            WriteCanonicalJson(writer, body);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject jsonObject:
                writer.WriteStartObject();
                foreach (var property in jsonObject.OrderBy(
                             property => property.Key,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray jsonArray:
                writer.WriteStartArray();
                foreach (var item in jsonArray)
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer, JsonOptions);
                break;
        }
    }

    private static StoredCommand Clone(StoredCommand command) => new()
    {
        CommandId = command.CommandId,
        ServerId = command.ServerId,
        UpstreamPath = command.UpstreamPath,
        Body = command.Body?.DeepClone(),
        IdempotencyKey = command.IdempotencyKey,
        RequestHash = command.RequestHash,
        Reason = command.Reason,
        Actor = command.Actor,
        State = command.State,
        CreatedAt = command.CreatedAt,
        AvailableAt = command.AvailableAt,
        DispatchedAt = command.DispatchedAt,
        CompletedAt = command.CompletedAt,
        HttpStatus = command.HttpStatus,
        ResponseJson = command.ResponseJson?.DeepClone(),
        ResponseText = command.ResponseText,
        ErrorCode = command.ErrorCode,
        ErrorMessage = command.ErrorMessage,
        LeaseOwner = command.LeaseOwner,
        LeaseUntil = command.LeaseUntil,
        AttemptCount = command.AttemptCount,
        FailureCount = command.FailureCount,
        DeadLetteredAt = command.DeadLetteredAt
    };

    private static string ScopedKey(string serverId, string idempotencyKey) =>
        $"{serverId}\n{idempotencyKey}";

    private sealed class StoredCommand
    {
        public required Guid CommandId { get; init; }
        public required string ServerId { get; init; }
        public required string UpstreamPath { get; init; }
        public JsonNode? Body { get; init; }
        public required string IdempotencyKey { get; init; }
        public required string RequestHash { get; init; }
        public required string Reason { get; init; }
        public required string Actor { get; init; }
        public required string State { get; set; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset AvailableAt { get; set; }
        public DateTimeOffset? DispatchedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int? HttpStatus { get; set; }
        public JsonNode? ResponseJson { get; set; }
        public string? ResponseText { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? LeaseOwner { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
        public int AttemptCount { get; set; }
        public int FailureCount { get; set; }
        public DateTimeOffset? DeadLetteredAt { get; set; }
    }

    private sealed record QueueEvent(
        Guid EventId,
        Guid CommandId,
        string EventType,
        string State,
        DateTimeOffset At,
        string ServerId,
        string UpstreamPath,
        string IdempotencyKey,
        string RequestHash,
        string Reason,
        string Actor,
        JsonNode? Body,
        int? HttpStatus,
        JsonNode? ResponseJson,
        string? ResponseText,
        string? ErrorCode,
        string? ErrorMessage);
}

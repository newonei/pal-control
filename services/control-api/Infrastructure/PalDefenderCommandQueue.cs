using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed record PalDefenderCommandEnqueueResult(
    CommandStatus? Command,
    bool Created,
    bool IdempotencyConflict);

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

public sealed class PalDefenderCommandQueue : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Dictionary<Guid, StoredCommand> _commands = [];
    private readonly Dictionary<string, (string Hash, Guid CommandId)> _idempotency =
        new(StringComparer.Ordinal);
    private readonly List<PalDefenderCommandAuditEvent> _audit = [];
    private readonly PalDefenderRestClient _client;
    private readonly ILogger<PalDefenderCommandQueue> _logger;
    private readonly string _eventPath;
    private readonly FileStream _instanceLock;
    private volatile bool _storeReady;
    private volatile bool _workerRunning;

    public PalDefenderCommandQueue(
        IOptions<CommandPersistenceOptions> persistenceOptions,
        IHostEnvironment environment,
        PalDefenderRestClient client,
        ILogger<PalDefenderCommandQueue> logger)
    {
        _client = client;
        _logger = logger;
        var configuredDirectory = persistenceOptions.Value.DataDirectory;
        var dataDirectory = Path.GetFullPath(Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(environment.ContentRootPath, configuredDirectory));
        Directory.CreateDirectory(dataDirectory);
        _eventPath = Path.Combine(dataDirectory, "paldefender-command-audit.jsonl");
        _instanceLock = new FileStream(
            Path.Combine(dataDirectory, "paldefender-command-queue.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        LoadEvents();
        EnsureWritable();
    }

    public bool IsReady => _storeReady && _workerRunning;

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
                CreatedAt = DateTimeOffset.UtcNow
            };
            var accepted = CreateEvent(command, "accepted", "accepted", includeRequestBody: true);
            await AppendEventAsync(accepted);
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
                var commandId = await GetNextAcceptedCommandAsync(stoppingToken);
                if (commandId is null)
                {
                    await _wakeSignal.WaitAsync(stoppingToken);
                    continue;
                }
                await ProcessAsync(commandId.Value, stoppingToken);
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            interrupted = _commands.Values
                .Where(command => string.Equals(command.State, "dispatched", StringComparison.Ordinal))
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
    }

    private async Task<Guid?> GetNextAcceptedCommandAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _commands.Values
                .Where(command => string.Equals(command.State, "accepted", StringComparison.Ordinal))
                .OrderBy(command => command.CreatedAt)
                .Select(command => (Guid?)command.CommandId)
                .FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessAsync(Guid commandId, CancellationToken stoppingToken)
    {
        StoredCommand? command;
        await _gate.WaitAsync(stoppingToken);
        try
        {
            command = _commands.TryGetValue(commandId, out var stored)
                ? Clone(stored)
                : null;
        }
        finally
        {
            _gate.Release();
        }
        if (command is null || !string.Equals(command.State, "accepted", StringComparison.Ordinal))
        {
            return;
        }

        // This event is forced to durable storage before the upstream write is attempted.
        await TransitionAsync(
            commandId,
            "dispatched",
            "dispatched",
            null,
            null,
            null,
            null,
            null,
            stoppingToken);

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
                exception,
                "PalDefender command {CommandId} ended unexpectedly after dispatch.",
                commandId);
            await TransitionAsync(
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
            await TransitionAsync(
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
            await TransitionAsync(
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

        await TransitionAsync(
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

    private async Task TransitionAsync(
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(commandId, out var command))
            {
                return;
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
            await AppendEventAsync(stored);
            ApplyEvent(stored);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadEvents()
    {
        if (!File.Exists(_eventPath))
        {
            return;
        }

        var lines = File.ReadAllLines(_eventPath, Encoding.UTF8);
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            QueueEvent? stored;
            try
            {
                stored = JsonSerializer.Deserialize<QueueEvent>(lines[index], JsonOptions);
            }
            catch (JsonException) when (index == lines.Length - 1 && HasPartialFinalLine())
            {
                _logger.LogWarning("Ignoring a partial final PalDefender command event after an interrupted write.");
                TruncatePartialFinalLine();
                break;
            }

            if (stored is null)
            {
                throw new InvalidDataException($"PalDefender command event {index + 1} is empty.");
            }
            ApplyEvent(stored);
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
                CreatedAt = stored.At
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
            if (stored.State is "succeeded" or "failed" or "uncertain")
            {
                command.CompletedAt = stored.At;
            }
        }
        else
        {
            throw new InvalidDataException(
                $"PalDefender command event {stored.EventId} references an unknown command.");
        }

        _audit.Add(ToAudit(stored));
    }

    private async Task AppendEventAsync(QueueEvent stored)
    {
        try
        {
            var line = JsonSerializer.Serialize(stored, JsonOptions) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(line);
            await using var stream = new FileStream(
                _eventPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
            stream.Flush(flushToDisk: true);
            _storeReady = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _storeReady = false;
            throw;
        }
    }

    private void EnsureWritable()
    {
        using var stream = new FileStream(
            _eventPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read);
        stream.Flush(flushToDisk: true);
        _storeReady = true;
    }

    private bool HasPartialFinalLine()
    {
        var bytes = File.ReadAllBytes(_eventPath);
        return bytes.Length > 0 && bytes[^1] != (byte)'\n';
    }

    private void TruncatePartialFinalLine()
    {
        var bytes = File.ReadAllBytes(_eventPath);
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        using var stream = new FileStream(
            _eventPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        stream.SetLength(lastNewline < 0 ? 0 : lastNewline + 1);
        stream.Flush(flushToDisk: true);
    }

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
        string? errorMessage = null) => new(
            EventId: Guid.NewGuid(),
            CommandId: command.CommandId,
            EventType: eventType,
            State: state,
            At: DateTimeOffset.UtcNow,
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
        stored.UpstreamPath,
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
                upstreamPath = command.UpstreamPath,
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
        CompletedAt = command.CompletedAt,
        HttpStatus = command.HttpStatus,
        ResponseJson = command.ResponseJson?.DeepClone(),
        ResponseText = command.ResponseText,
        ErrorCode = command.ErrorCode,
        ErrorMessage = command.ErrorMessage
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
        public DateTimeOffset? CompletedAt { get; set; }
        public int? HttpStatus { get; set; }
        public JsonNode? ResponseJson { get; set; }
        public string? ResponseText { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
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

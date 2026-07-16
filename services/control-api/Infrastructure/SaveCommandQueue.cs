using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed record SaveCommandEnqueueResult(
    SaveCommandStatus? Command,
    bool Created,
    bool IdempotencyConflict);

public sealed class SaveCommandQueue : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SaveManagementService _saves;
    private readonly PalworldRestClient _palworld;
    private readonly ILogger<SaveCommandQueue> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Dictionary<Guid, StoredSaveCommand> _commands = [];
    private readonly Dictionary<string, (string Hash, Guid CommandId)> _idempotency =
        new(StringComparer.Ordinal);
    private readonly List<SaveCommandAuditEvent> _audit = [];
    private readonly string _eventPath;
    private readonly FileStream _instanceLock;
    private bool _ready;

    public SaveCommandQueue(
        IOptions<CommandPersistenceOptions> persistenceOptions,
        IHostEnvironment environment,
        SaveManagementService saves,
        PalworldRestClient palworld,
        ILogger<SaveCommandQueue> logger)
    {
        _saves = saves;
        _palworld = palworld;
        _logger = logger;
        var configuredDirectory = persistenceOptions.Value.DataDirectory;
        var dataDirectory = Path.GetFullPath(Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(environment.ContentRootPath, configuredDirectory));
        Directory.CreateDirectory(dataDirectory);
        _eventPath = Path.Combine(dataDirectory, "save-command-audit.jsonl");
        var lockPath = Path.Combine(dataDirectory, "save-command-queue.lock");
        _instanceLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        using (ControlPlaneLog.BeginOperation(
                   _logger,
                   nameof(SaveCommandQueue),
                   "persistence.load",
                   "save-command-audit"))
        {
            LoadEvents();
        }
        EnsureWritable();
    }

    public bool IsReady => _ready;

    public async Task<SaveCommandEnqueueResult> EnqueueAsync(
        string serverId,
        string type,
        string idempotencyKey,
        string reason,
        string actor,
        string? label,
        string? backupId,
        CancellationToken cancellationToken)
    {
        var normalizedReason = reason.Trim();
        var normalizedLabel = label?.Trim();
        var commandBackupId = string.Equals(type, "create-backup", StringComparison.Ordinal)
            ? Guid.NewGuid().ToString("N")
            : backupId;
        var requestHash = HashRequest(
            serverId,
            type,
            normalizedReason,
            normalizedLabel,
            backupId);
        var scopedKey = ScopedKey(serverId, idempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
                {
                    return new SaveCommandEnqueueResult(null, false, true);
                }
                return new SaveCommandEnqueueResult(
                    ToStatus(_commands[existing.CommandId]),
                    false,
                    false);
            }

            var command = new StoredSaveCommand
            {
                CommandId = Guid.NewGuid(),
                Type = type,
                ServerId = serverId,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                Reason = normalizedReason,
                Actor = actor,
                Label = normalizedLabel,
                BackupId = commandBackupId,
                State = "accepted",
                Stage = "queued",
                CreatedAt = DateTimeOffset.UtcNow
            };
            var accepted = CreateEvent(command, "accepted", "accepted", "queued");
            await AppendEventAsync(accepted);
            ApplyEvent(accepted);
            SignalWorker();
            return new SaveCommandEnqueueResult(ToStatus(command), true, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SaveCommandStatus?> GetStatusAsync(
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

    public async Task<IReadOnlyList<SaveCommandAuditEvent>> GetAuditAsync(
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
        await RecoverInterruptedCommandsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = await GetNextAcceptedCommandAsync(stoppingToken);
            if (next is null)
            {
                await _wakeSignal.WaitAsync(stoppingToken);
                continue;
            }

            using var scope = ControlPlaneLog.BeginWorker(
                _logger,
                nameof(SaveCommandQueue),
                "save.execute",
                next.CommandId,
                next.ServerId);
            try
            {
                await ProcessAsync(next.CommandId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogSafeError(exception, "Unhandled save command worker failure for {CommandId}.", next.CommandId);
                await TransitionAsync(
                    next.CommandId,
                    "failed",
                    "completed",
                    null,
                    "SAVE_COMMAND_FAILED",
                    "The save command failed unexpectedly.",
                    stoppingToken);
            }
        }
    }

    private async Task RecoverInterruptedCommandsAsync(CancellationToken cancellationToken)
    {
        StoredSaveCommand[] interrupted;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            interrupted = _commands.Values
                .Where(command => string.Equals(command.State, "dispatched", StringComparison.Ordinal))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var command in interrupted)
        {
            using var scope = ControlPlaneLog.BeginWorker(
                _logger,
                nameof(SaveCommandQueue),
                "save.reconcile",
                command.CommandId,
                command.ServerId);
            if (command.Type is "create-backup" && command.BackupId is { } createdBackupId)
            {
                try
                {
                    var backup = await _saves.VerifyManagedBackupAsync(
                        command.ServerId,
                        createdBackupId,
                        cancellationToken);
                    if (string.Equals(backup.Integrity, "verified", StringComparison.Ordinal))
                    {
                        await TransitionAsync(
                            command.CommandId,
                            "succeeded",
                            "completed",
                            new { backup },
                            null,
                            null,
                            cancellationToken,
                            eventType: "reconciled");
                        continue;
                    }
                }
                catch (SaveManagementException)
                {
                    // No published, verifiable backup exists for reconciliation.
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    _logger.LogSafeWarning(
                        exception,
                        "Could not reconcile interrupted managed backup {BackupId}.",
                        createdBackupId);
                }
                _saves.CleanupPartialForCommand(command.ServerId, createdBackupId);
            }
            else if (command.Type is "verify-backup" && command.BackupId is { } verifiedBackupId)
            {
                try
                {
                    var backup = await _saves.VerifyManagedBackupAsync(
                        command.ServerId,
                        verifiedBackupId,
                        cancellationToken);
                    var result = new { backup, verifiedAt = DateTimeOffset.UtcNow };
                    await TransitionAsync(
                        command.CommandId,
                        string.Equals(backup.Integrity, "verified", StringComparison.Ordinal)
                            ? "succeeded"
                            : "failed",
                        "completed",
                        result,
                        string.Equals(backup.Integrity, "verified", StringComparison.Ordinal)
                            ? null
                            : "BACKUP_INTEGRITY_FAILED",
                        string.Equals(backup.Integrity, "verified", StringComparison.Ordinal)
                            ? null
                            : "The managed backup does not match its SHA-256 manifest.",
                        cancellationToken,
                        eventType: "reconciled");
                    continue;
                }
                catch (SaveManagementException)
                {
                    // Fall through to conservative uncertain recovery.
                }
            }

            await TransitionAsync(
                command.CommandId,
                "uncertain",
                "completed",
                null,
                "SAVE_COMMAND_INTERRUPTED",
                "The service restarted after dispatch. The operation was not sent again automatically and its final outcome could not be proven.",
                cancellationToken,
                eventType: "recovered");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_commands.Values.Any(command => string.Equals(
                command.State,
                "accepted",
                StringComparison.Ordinal)))
            {
                SignalWorker();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessAsync(Guid commandId, CancellationToken cancellationToken)
    {
        var command = await GetStoredCommandAsync(commandId, cancellationToken);
        if (command is null || command.State != "accepted")
        {
            return;
        }

        try
        {
            switch (command.Type)
            {
                case "flush":
                    await ProcessFlushAsync(command, cancellationToken);
                    break;
                case "create-backup":
                    await ProcessCreateBackupAsync(command, cancellationToken);
                    break;
                case "verify-backup":
                    await ProcessVerifyBackupAsync(command, cancellationToken);
                    break;
                default:
                    await TransitionAsync(
                        command.CommandId,
                        "failed",
                        "completed",
                        null,
                        "SAVE_COMMAND_TYPE_UNSUPPORTED",
                        "The save command type is unsupported.",
                        cancellationToken);
                    break;
            }
        }
        catch (SaveManagementException exception)
        {
            await TransitionAsync(
                command.CommandId,
                exception.Uncertain ? "uncertain" : "failed",
                "completed",
                null,
                exception.Code,
                exception.Message,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            _logger.LogSafeError(exception, "Save command {CommandId} failed during local I/O.", command.CommandId);
            await TransitionAsync(
                command.CommandId,
                "failed",
                "completed",
                null,
                "SAVE_STORAGE_OPERATION_FAILED",
                "The save command could not complete its local storage operation safely.",
                cancellationToken);
        }
    }

    private async Task ProcessFlushAsync(
        StoredSaveCommand command,
        CancellationToken cancellationToken)
    {
        _ = await _saves.ResolveActiveWorldAsync(command.ServerId, cancellationToken);
        await TransitionAsync(
            command.CommandId,
            "dispatched",
            "saving-world",
            null,
            null,
            null,
            cancellationToken);
        var result = await _palworld.SaveWorldAsync(cancellationToken);
        if (result.Success)
        {
            await TransitionAsync(
                command.CommandId,
                "succeeded",
                "completed",
                new { savedAt = DateTimeOffset.UtcNow, httpStatus = result.HttpStatus },
                null,
                null,
                cancellationToken);
            return;
        }
        await TransitionAsync(
            command.CommandId,
            result.Uncertain ? "uncertain" : "failed",
            "completed",
            null,
            result.ErrorCode,
            result.ErrorMessage,
            cancellationToken);
    }

    private async Task ProcessCreateBackupAsync(
        StoredSaveCommand command,
        CancellationToken cancellationToken)
    {
        var world = await _saves.ResolveActiveWorldAsync(command.ServerId, cancellationToken);
        var identitiesBeforeSave = _saves.GetNativeSnapshotIdentities(world);
        await TransitionAsync(
            command.CommandId,
            "dispatched",
            "saving-world",
            null,
            null,
            null,
            cancellationToken);
        var saveResult = await _palworld.SaveWorldAsync(cancellationToken);
        if (!saveResult.Success)
        {
            await TransitionAsync(
                command.CommandId,
                saveResult.Uncertain ? "uncertain" : "failed",
                "completed",
                null,
                saveResult.ErrorCode,
                saveResult.ErrorMessage,
                cancellationToken);
            return;
        }

        await TransitionAsync(
            command.CommandId,
            "dispatched",
            "waiting-snapshot",
            null,
            null,
            null,
            cancellationToken);
        var snapshot = await _saves.WaitForNewStableNativeSnapshotAsync(
            world,
            identitiesBeforeSave,
            cancellationToken);
        await TransitionAsync(
            command.CommandId,
            "dispatched",
            "copying",
            null,
            null,
            null,
            cancellationToken);
        var backup = await _saves.CreateManagedBackupAsync(
            world,
            snapshot,
            command.BackupId
                ?? throw new InvalidDataException("A create-backup command has no backup ID."),
            command.Label
                ?? throw new InvalidDataException("A create-backup command has no label."),
            command.Actor,
            command.Reason,
            cancellationToken);
        await TransitionAsync(
            command.CommandId,
            "dispatched",
            "verifying",
            null,
            null,
            null,
            cancellationToken);
        var verified = await _saves.VerifyManagedBackupAsync(
            command.ServerId,
            backup.BackupId,
            cancellationToken);
        if (!string.Equals(verified.Integrity, "verified", StringComparison.Ordinal))
        {
            await TransitionAsync(
                command.CommandId,
                "failed",
                "completed",
                new { backup = verified },
                "BACKUP_INTEGRITY_FAILED",
                "The managed backup does not match its SHA-256 manifest.",
                cancellationToken);
            return;
        }
        await TransitionAsync(
            command.CommandId,
            "succeeded",
            "completed",
            new { backup = verified },
            null,
            null,
            cancellationToken);
    }

    private async Task ProcessVerifyBackupAsync(
        StoredSaveCommand command,
        CancellationToken cancellationToken)
    {
        await TransitionAsync(
            command.CommandId,
            "dispatched",
            "verifying",
            null,
            null,
            null,
            cancellationToken);
        var backup = await _saves.VerifyManagedBackupAsync(
            command.ServerId,
            command.BackupId
                ?? throw new InvalidDataException("A verify-backup command has no backup ID."),
            cancellationToken);
        var result = new { backup, verifiedAt = DateTimeOffset.UtcNow };
        if (!string.Equals(backup.Integrity, "verified", StringComparison.Ordinal))
        {
            await TransitionAsync(
                command.CommandId,
                "failed",
                "completed",
                result,
                "BACKUP_INTEGRITY_FAILED",
                "The managed backup does not match its SHA-256 manifest.",
                cancellationToken);
            return;
        }
        await TransitionAsync(
            command.CommandId,
            "succeeded",
            "completed",
            result,
            null,
            null,
            cancellationToken);
    }

    private async Task<StoredSaveCommand?> GetNextAcceptedCommandAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var command = _commands.Values
                .Where(item => string.Equals(item.State, "accepted", StringComparison.Ordinal))
                .OrderBy(item => item.CreatedAt)
                .FirstOrDefault();
            return command is null ? null : Clone(command);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoredSaveCommand?> GetStoredCommandAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _commands.TryGetValue(commandId, out var command)
                ? Clone(command)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TransitionAsync(
        Guid commandId,
        string state,
        string stage,
        object? result,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken,
        string eventType = "transition")
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
                stage,
                result,
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
                _logger.LogWarning("Ignoring a partial final save command audit event after an interrupted write.");
                TruncatePartialFinalLine();
                break;
            }
            if (stored is null)
            {
                throw new InvalidDataException($"Save command audit event {index + 1} is empty.");
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
                        $"Save command idempotency key '{stored.IdempotencyKey}' has conflicting hashes.");
                }
                _audit.Add(ToAudit(stored));
                return;
            }
            var command = new StoredSaveCommand
            {
                CommandId = stored.CommandId,
                Type = stored.Type,
                ServerId = stored.ServerId,
                IdempotencyKey = stored.IdempotencyKey,
                RequestHash = stored.RequestHash,
                Reason = stored.Reason,
                Actor = stored.Actor,
                Label = stored.Label,
                BackupId = stored.BackupId,
                State = stored.State,
                Stage = stored.Stage,
                CreatedAt = stored.At
            };
            _commands[command.CommandId] = command;
            _idempotency[scopedKey] = (command.RequestHash, command.CommandId);
        }
        else if (_commands.TryGetValue(stored.CommandId, out var current))
        {
            current.State = stored.State;
            current.Stage = stored.Stage;
            current.ErrorCode = stored.ErrorCode;
            current.ErrorMessage = stored.ErrorMessage;
            current.Result = stored.Result;
            if (stored.State is "succeeded" or "failed" or "uncertain")
            {
                current.CompletedAt = stored.At;
            }
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
            stream.Flush(true);
            _ready = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ready = false;
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
        stream.Flush(true);
        _ready = true;
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
        stream.Flush(true);
    }

    private void SignalWorker()
    {
        if (_wakeSignal.CurrentCount == 0)
        {
            _wakeSignal.Release();
        }
    }

    private static QueueEvent CreateEvent(
        StoredSaveCommand command,
        string eventType,
        string state,
        string stage,
        object? result = null,
        string? errorCode = null,
        string? errorMessage = null) => new(
            Guid.NewGuid(),
            command.CommandId,
            command.Type,
            eventType,
            state,
            stage,
            DateTimeOffset.UtcNow,
            command.ServerId,
            command.IdempotencyKey,
            command.RequestHash,
            command.Reason,
            command.Actor,
            command.BackupId,
            command.Label,
            result is null ? null : JsonSerializer.SerializeToElement(result, JsonOptions),
            errorCode,
            errorMessage);

    private static SaveCommandAuditEvent ToAudit(QueueEvent stored) => new(
        stored.EventId,
        stored.CommandId,
        stored.Type,
        stored.EventType,
        stored.State,
        stored.Stage,
        stored.At,
        stored.ServerId,
        stored.IdempotencyKey,
        stored.RequestHash,
        stored.Reason,
        stored.Actor,
        stored.BackupId,
        stored.Label,
        stored.ErrorCode,
        stored.ErrorMessage);

    private static SaveCommandStatus ToStatus(StoredSaveCommand command) => new(
        command.CommandId,
        command.Type,
        command.State,
        command.Stage,
        command.CreatedAt,
        command.CompletedAt,
        $"/api/v1/save-commands/{command.CommandId}",
        command.BackupId,
        command.Result,
        command.ErrorCode is null
            ? null
            : new ApiError(
                command.ErrorCode,
                command.ErrorMessage ?? "The save command failed."));

    private static StoredSaveCommand Clone(StoredSaveCommand command) => new()
    {
        CommandId = command.CommandId,
        Type = command.Type,
        ServerId = command.ServerId,
        IdempotencyKey = command.IdempotencyKey,
        RequestHash = command.RequestHash,
        Reason = command.Reason,
        Actor = command.Actor,
        Label = command.Label,
        BackupId = command.BackupId,
        State = command.State,
        Stage = command.Stage,
        CreatedAt = command.CreatedAt,
        CompletedAt = command.CompletedAt,
        Result = command.Result,
        ErrorCode = command.ErrorCode,
        ErrorMessage = command.ErrorMessage
    };

    private static string HashRequest(
        string serverId,
        string type,
        string reason,
        string? label,
        string? backupId)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            operation = type,
            serverId,
            reason,
            label,
            backupId
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static string ScopedKey(string serverId, string idempotencyKey) =>
        $"{serverId}\n{idempotencyKey}";

    public override void Dispose()
    {
        _instanceLock.Dispose();
        _gate.Dispose();
        _wakeSignal.Dispose();
        base.Dispose();
    }

    private sealed class StoredSaveCommand
    {
        public required Guid CommandId { get; init; }
        public required string Type { get; init; }
        public required string ServerId { get; init; }
        public required string IdempotencyKey { get; init; }
        public required string RequestHash { get; init; }
        public required string Reason { get; init; }
        public required string Actor { get; init; }
        public string? Label { get; init; }
        public string? BackupId { get; init; }
        public required string State { get; set; }
        public required string Stage { get; set; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
        public JsonElement? Result { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed record QueueEvent(
        Guid EventId,
        Guid CommandId,
        string Type,
        string EventType,
        string State,
        string Stage,
        DateTimeOffset At,
        string ServerId,
        string IdempotencyKey,
        string RequestHash,
        string Reason,
        string Actor,
        string? BackupId,
        string? Label,
        JsonElement? Result,
        string? ErrorCode,
        string? ErrorMessage);
}

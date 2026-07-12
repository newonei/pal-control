using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed class InGameNotificationCommandQueue : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Dictionary<Guid, StoredCommand> _commands = [];
    private readonly Dictionary<string, (string Hash, Guid CommandId)> _idempotency =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Guid> _notificationCommands = [];
    private readonly List<InGameNotificationCommandAuditEvent> _audit = [];
    private readonly InGameNotificationStore _notifications;
    private readonly InGameNotificationCapabilityService _capabilities;
    private readonly NativeBridgeState _nativeBridgeState;
    private readonly NativeBridgeClient _nativeBridge;
    private readonly ILogger<InGameNotificationCommandQueue> _logger;
    private readonly string _eventPath;
    private readonly FileStream _leaseStream;
    private volatile bool _isReady;
    private volatile bool _workerRunning;

    public InGameNotificationCommandQueue(
        IHostEnvironment environment,
        IOptions<CommandPersistenceOptions> options,
        InGameNotificationStore notifications,
        InGameNotificationCapabilityService capabilities,
        NativeBridgeState nativeBridgeState,
        NativeBridgeClient nativeBridge,
        ILogger<InGameNotificationCommandQueue> logger)
    {
        _notifications = notifications;
        _capabilities = capabilities;
        _nativeBridgeState = nativeBridgeState;
        _nativeBridge = nativeBridge;
        _logger = logger;
        var dataDirectory = Path.GetFullPath(
            Path.IsPathRooted(options.Value.DataDirectory)
                ? options.Value.DataDirectory
                : Path.Combine(environment.ContentRootPath, options.Value.DataDirectory));
        Directory.CreateDirectory(dataDirectory);
        _leaseStream = new FileStream(
            Path.Combine(dataDirectory, "in-game-notification-command-queue.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        _eventPath = Path.Combine(dataDirectory, "in-game-notification-command-audit.jsonl");
        EnsureWritable();
        LoadEvents();
    }

    public bool IsReady => _isReady && _workerRunning && _notifications.IsReady;

    public async Task<InGameNotificationEnqueueResult> EnqueueAsync(
        string serverId,
        InGameNotification notification,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? scheduledFor = notification.DisplayAt is { } displayAt && displayAt > now
            ? displayAt.ToUniversalTime()
            : null;
        var requestHash = InGameNotificationContract.HashDispatch(serverId, notification);
        var scopedKey = ScopedKey(serverId, idempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_isReady)
            {
                throw new IOException("The in-game notification command audit store is not writable.");
            }
            if (_idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
                {
                    return new InGameNotificationEnqueueResult(null, false, true, false);
                }
                return new InGameNotificationEnqueueResult(
                    ToStatus(_commands[existing.CommandId]),
                    false,
                    false,
                    false);
            }
            if (_notificationCommands.TryGetValue(notification.NotificationId, out var existingCommandId) &&
                _commands.TryGetValue(existingCommandId, out var existingCommand) &&
                !CanReplace(existingCommand))
            {
                return new InGameNotificationEnqueueResult(
                    ToStatus(existingCommand),
                    false,
                    false,
                    true);
            }

            var command = new StoredCommand
            {
                CommandId = Guid.NewGuid(),
                Notification = notification,
                ServerId = serverId,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                Actor = actor,
                State = "accepted",
                CreatedAt = now,
                ScheduledFor = scheduledFor,
                ExpiresAt = notification.ExpiresAt?.ToUniversalTime()
            };
            var accepted = CreateEvent(command, "accepted", "accepted", now);
            await AppendEventAsync(accepted);
            ApplyEvent(accepted);
            SignalWorker();
            return new InGameNotificationEnqueueResult(ToStatus(command), true, false, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InGameNotificationEnqueueResult?> FindExistingAsync(
        string serverId,
        InGameNotification notification,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var requestHash = InGameNotificationContract.HashDispatch(serverId, notification);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_idempotency.TryGetValue(ScopedKey(serverId, idempotencyKey), out var existing))
            {
                return null;
            }
            if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
            {
                return new InGameNotificationEnqueueResult(null, false, true, false);
            }
            return new InGameNotificationEnqueueResult(
                ToStatus(_commands[existing.CommandId]),
                false,
                false,
                false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandStatus?> GetStatusAsync(Guid commandId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _commands.TryGetValue(commandId, out var command) ? ToStatus(command) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InGameNotificationCommandAuditEvent>> GetAuditAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _audit
                .TakeLast(Math.Clamp(limit, 1, 500))
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
            await RecoverCommandsAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                StoredCommand? next;
                DateTimeOffset? nextScheduled;
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    next = _commands.Values
                        .Where(command => command.State == "accepted" &&
                            (command.ScheduledFor is null || command.ScheduledFor <= now))
                        .OrderBy(command => command.ScheduledFor ?? command.CreatedAt)
                        .ThenBy(command => command.CreatedAt)
                        .FirstOrDefault();
                    nextScheduled = _commands.Values
                        .Where(command => command.State == "accepted" && command.ScheduledFor > now)
                        .Select(command => command.ScheduledFor)
                        .Min();
                }
                finally
                {
                    _gate.Release();
                }

                if (next is not null)
                {
                    await DispatchAsync(next.CommandId, stoppingToken);
                    continue;
                }

                var wait = nextScheduled is { } due
                    ? due - DateTimeOffset.UtcNow
                    : TimeSpan.FromSeconds(30);
                wait = wait <= TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(50)
                    : TimeSpan.FromTicks(Math.Min(wait.Ticks, TimeSpan.FromSeconds(30).Ticks));
                await _wakeSignal.WaitAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _isReady = false;
            _logger.LogCritical(exception, "The in-game notification command worker stopped unexpectedly.");
            throw;
        }
        finally
        {
            _workerRunning = false;
        }
    }

    public override void Dispose()
    {
        _leaseStream.Dispose();
        _gate.Dispose();
        _wakeSignal.Dispose();
        base.Dispose();
    }

    private async Task RecoverCommandsAsync(CancellationToken cancellationToken)
    {
        StoredCommand[] commands;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            commands = _commands.Values.ToArray();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var command in commands)
        {
            switch (command.State)
            {
                case "dispatched":
                    await TransitionAsync(
                        command.CommandId,
                        "recovered-uncertain",
                        "uncertain",
                        "NOTIFICATION_OUTCOME_UNCERTAIN",
                        "The Control API restarted after native dispatch; this notification will not be sent again automatically.",
                        command.AttemptedRecipients,
                        command.DeliveredRecipients,
                        command.DeliveryAcknowledged,
                        cancellationToken);
                    await _notifications.SetStateAsync(
                        command.Notification.NotificationId,
                        "uncertain",
                        "in-game-notification-command-worker",
                        cancellationToken);
                    break;
                case "succeeded":
                    await _notifications.SetStateAsync(
                        command.Notification.NotificationId,
                        "sent",
                        "in-game-notification-command-worker",
                        cancellationToken);
                    break;
                case "uncertain":
                    await _notifications.SetStateAsync(
                        command.Notification.NotificationId,
                        "uncertain",
                        "in-game-notification-command-worker",
                        cancellationToken);
                    break;
                case "failed":
                    await _notifications.SetStateAsync(
                        command.Notification.NotificationId,
                        "failed",
                        "in-game-notification-command-worker",
                        cancellationToken);
                    break;
            }
        }
    }

    private async Task DispatchAsync(Guid commandId, CancellationToken stoppingToken)
    {
        StoredCommand? command;
        await _gate.WaitAsync(stoppingToken);
        try
        {
            _commands.TryGetValue(commandId, out command);
        }
        finally
        {
            _gate.Release();
        }
        if (command is null || command.State != "accepted")
        {
            return;
        }

        if (command.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            await TransitionAsync(
                commandId,
                "expired",
                "failed",
                "NOTIFICATION_EXPIRED",
                "The in-game notification expired before native dispatch.",
                null,
                null,
                null,
                stoppingToken);
            await _notifications.SetStateAsync(
                command.Notification.NotificationId,
                "expired",
                "in-game-notification-command-worker",
                stoppingToken);
            return;
        }

        var snapshot = _nativeBridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("ui.notifications.write"))
        {
            await FailBeforeDispatchAsync(
                command,
                "NATIVE_NOTIFICATION_PRESET_UNAVAILABLE",
                "The Native Bridge server-native notification capability was unavailable before dispatch.",
                stoppingToken);
            return;
        }

        var probeOutcome = await _capabilities.ProbeAsync(command.ServerId, stoppingToken);
        if (!probeOutcome.Ready || probeOutcome.Probe is null)
        {
            await FailBeforeDispatchAsync(
                command,
                probeOutcome.Error?.Code ?? "NATIVE_NOTIFICATION_PRESET_UNAVAILABLE",
                probeOutcome.Error?.Message ?? "Native server-native notification presets were not ready before dispatch.",
                stoppingToken);
            return;
        }
        var validation = InGameNotificationContract.ValidateAgainstProbe(
            InGameNotificationContract.ToInput(command.Notification),
            probeOutcome.Probe);
        if (validation is not null)
        {
            await FailBeforeDispatchAsync(command, validation.Code, validation.Message, stoppingToken);
            return;
        }

        try
        {
            command = await TransitionAsync(
                commandId,
                "dispatched",
                "dispatched",
                null,
                null,
                null,
                null,
                null,
                stoppingToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _isReady = false;
            _logger.LogError(
                exception,
                "Could not persist native dispatch for in-game notification command {CommandId}.",
                commandId);
            throw;
        }
        if (command is null)
        {
            return;
        }

        DispatchResult dispatchResult;
        try
        {
            var nativeResult = await _nativeBridge.SendCommandAsync(
                command.ServerId,
                "ui.notifications.send",
                new
                {
                    deliveryId = command.CommandId,
                    schemaVersion = command.Notification.SchemaVersion,
                    preset = command.Notification.Template.Preset,
                    parameters = command.Notification.Template.Parameters,
                    audience = new
                    {
                        type = command.Notification.Audience.Type,
                        ids = command.Notification.Audience.Ids
                    }
                },
                command.Notification.Reason,
                stoppingToken,
                idempotencyKey: $"in-game-notification-{command.CommandId:N}");
            dispatchResult = FromNativeResult(nativeResult, command);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            dispatchResult = DispatchResult.Uncertain(
                "NOTIFICATION_OUTCOME_UNCERTAIN",
                "The native notification request ended after dispatch; it will not be sent again automatically.");
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Unexpected native notification dispatch failure for command {CommandId}.",
                commandId);
            dispatchResult = DispatchResult.Uncertain(
                "NOTIFICATION_DISPATCH_INTERRUPTED",
                "The native notification request ended unexpectedly after dispatch.");
        }

        var persistenceToken = stoppingToken.IsCancellationRequested
            ? CancellationToken.None
            : stoppingToken;
        var finalState = dispatchResult.Success
            ? "succeeded"
            : dispatchResult.OutcomeUncertain ? "uncertain" : "failed";
        await TransitionAsync(
            commandId,
            finalState,
            finalState,
            dispatchResult.ErrorCode,
            dispatchResult.ErrorMessage,
            dispatchResult.AttemptedRecipients,
            dispatchResult.DeliveredRecipients,
            dispatchResult.DeliveryAcknowledged,
            persistenceToken);
        await _notifications.SetStateAsync(
            command.Notification.NotificationId,
            dispatchResult.Success
                ? "sent"
                : dispatchResult.OutcomeUncertain ? "uncertain" : "failed",
            "in-game-notification-command-worker",
            persistenceToken);
    }

    private async Task FailBeforeDispatchAsync(
        StoredCommand command,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await TransitionAsync(
            command.CommandId,
            "failed",
            "failed",
            errorCode,
            errorMessage,
            null,
            null,
            null,
            cancellationToken);
        await _notifications.SetStateAsync(
            command.Notification.NotificationId,
            "failed",
            "in-game-notification-command-worker",
            cancellationToken);
    }

    private async Task<StoredCommand?> TransitionAsync(
        Guid commandId,
        string eventType,
        string state,
        string? errorCode,
        string? errorMessage,
        int? attemptedRecipients,
        int? deliveredRecipients,
        bool? deliveryAcknowledged,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(commandId, out var command))
            {
                return null;
            }
            var stored = CreateEvent(
                command,
                eventType,
                state,
                DateTimeOffset.UtcNow,
                errorCode,
                errorMessage,
                attemptedRecipients,
                deliveredRecipients,
                deliveryAcknowledged);
            await AppendEventAsync(stored);
            ApplyEvent(stored);
            return command;
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
                _logger.LogWarning("Ignoring a partial final native notification command event after an interrupted write.");
                TruncatePartialFinalLine();
                break;
            }
            if (stored is null)
            {
                throw new InvalidDataException($"In-game notification command event {index + 1} is empty.");
            }
            ApplyEvent(stored);
        }
    }

    private void ApplyEvent(QueueEvent stored)
    {
        if (stored.EventType == "accepted")
        {
            var notification = stored.Notification
                ?? throw new InvalidDataException($"Accepted notification command {stored.CommandId} has no payload.");
            var scopedKey = ScopedKey(stored.ServerId, stored.IdempotencyKey);
            if (_idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, stored.RequestHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Notification dispatch idempotency key '{stored.IdempotencyKey}' has conflicting hashes.");
                }
                _audit.Add(ToAudit(stored));
                return;
            }
            var command = new StoredCommand
            {
                CommandId = stored.CommandId,
                Notification = notification,
                ServerId = stored.ServerId,
                IdempotencyKey = stored.IdempotencyKey,
                RequestHash = stored.RequestHash,
                Actor = stored.Actor,
                State = stored.State,
                CreatedAt = stored.At,
                ScheduledFor = stored.ScheduledFor,
                ExpiresAt = stored.ExpiresAt
            };
            _commands[command.CommandId] = command;
            _idempotency[scopedKey] = (command.RequestHash, command.CommandId);
            _notificationCommands[notification.NotificationId] = command.CommandId;
        }
        else if (_commands.TryGetValue(stored.CommandId, out var command))
        {
            command.State = stored.State;
            command.ErrorCode = stored.ErrorCode;
            command.ErrorMessage = stored.ErrorMessage;
            command.AttemptedRecipients = stored.AttemptedRecipients;
            command.DeliveredRecipients = stored.DeliveredRecipients;
            command.DeliveryAcknowledged = stored.DeliveryAcknowledged;
            if (stored.State is "succeeded" or "failed" or "uncertain" or "cancelled")
            {
                command.CompletedAt = stored.At;
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
            _isReady = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _isReady = false;
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
        _isReady = true;
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
        StoredCommand command,
        string eventType,
        string state,
        DateTimeOffset at,
        string? errorCode = null,
        string? errorMessage = null,
        int? attemptedRecipients = null,
        int? deliveredRecipients = null,
        bool? deliveryAcknowledged = null) => new(
            EventId: Guid.NewGuid(),
            CommandId: command.CommandId,
            NotificationId: command.Notification.NotificationId,
            EventType: eventType,
            State: state,
            At: at,
            ServerId: command.ServerId,
            IdempotencyKey: command.IdempotencyKey,
            RequestHash: command.RequestHash,
            Reason: command.Notification.Reason,
            Actor: command.Actor,
            ScheduledFor: command.ScheduledFor,
            ExpiresAt: command.ExpiresAt,
            Notification: eventType == "accepted" ? command.Notification : null,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage,
            AttemptedRecipients: attemptedRecipients,
            DeliveredRecipients: deliveredRecipients,
            DeliveryAcknowledged: deliveryAcknowledged);

    private static InGameNotificationCommandAuditEvent ToAudit(QueueEvent stored) => new(
        stored.EventId,
        stored.CommandId,
        stored.NotificationId,
        stored.EventType,
        stored.State,
        stored.At,
        stored.ServerId,
        stored.IdempotencyKey,
        stored.RequestHash,
        stored.Reason,
        stored.Actor,
        stored.ScheduledFor,
        stored.ErrorCode,
        stored.ErrorMessage,
        stored.AttemptedRecipients,
        stored.DeliveredRecipients);

    private static CommandStatus ToStatus(StoredCommand command)
    {
        object result = new
        {
            notificationId = command.Notification.NotificationId,
            preset = command.Notification.Template.Preset,
            attemptedRecipients = command.AttemptedRecipients,
            deliveredRecipients = command.DeliveredRecipients,
            deliveryAcknowledged = command.DeliveryAcknowledged
        };
        return new CommandStatus(
            command.CommandId,
            command.State,
            command.CreatedAt,
            command.CompletedAt,
            result,
            command.ErrorCode is null
                ? null
                : new ApiError(command.ErrorCode, command.ErrorMessage ?? "In-game notification dispatch failed."),
            $"/api/v1/in-game-notification-commands/{command.CommandId}");
    }

    private static DispatchResult FromNativeResult(
        NativeBridgeResult result,
        StoredCommand command)
    {
        var attempted = ReadRecipientCount(result.Data, "attemptedRecipients");
        var delivered = ReadRecipientCount(result.Data, "deliveredRecipients");
        var acknowledged = ReadBoolean(result.Data, "deliveryAcknowledged");
        if (string.Equals(result.State, "succeeded", StringComparison.Ordinal))
        {
            if (result.Data is not { ValueKind: JsonValueKind.Object } data ||
                !data.TryGetProperty("dispatched", out var dispatched) ||
                dispatched.ValueKind != JsonValueKind.True ||
                !data.TryGetProperty("deliveryId", out var deliveryId) ||
                deliveryId.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    deliveryId.GetString(),
                    command.CommandId.ToString("D"),
                    StringComparison.Ordinal) ||
                !data.TryGetProperty("preset", out var preset) ||
                preset.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    preset.GetString(),
                    command.Notification.Template.Preset,
                    StringComparison.Ordinal) ||
                attempted is null ||
                !data.TryGetProperty("deliveredRecipients", out var deliveredProperty) ||
                deliveredProperty.ValueKind != JsonValueKind.Null ||
                !data.TryGetProperty("deliveryAcknowledged", out var acknowledgedProperty) ||
                acknowledgedProperty.ValueKind != JsonValueKind.False ||
                !data.TryGetProperty("transport", out var transport) ||
                transport.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    transport.GetString(),
                    "reliable-client-rpc",
                    StringComparison.Ordinal))
            {
                return DispatchResult.Uncertain(
                    "NATIVE_NOTIFICATION_RESULT_INVALID",
                    "Native dispatch returned a malformed success result after the command entered the dispatched state.",
                    attempted,
                    delivered,
                    acknowledged);
            }

            return DispatchResult.Succeeded(attempted, null, false);
        }
        return result.State switch
        {
            "uncertain" => DispatchResult.Uncertain(
                result.Error?.Code ?? "NOTIFICATION_OUTCOME_UNCERTAIN",
                result.Error?.Message ?? "Native dispatch could not confirm the notification outcome.",
                attempted,
                delivered,
                acknowledged),
            _ => DispatchResult.Failed(
                result.Error?.Code ?? "NOTIFICATION_DISPATCH_FAILED",
                result.Error?.Message ?? "Native notification dispatch failed before a successful result.",
                attempted,
                delivered,
                acknowledged)
        };
    }

    private static int? ReadRecipientCount(JsonElement? data, string propertyName)
    {
        if (data is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var count) ||
            count < 0)
        {
            return null;
        }
        return count;
    }

    private static bool? ReadBoolean(JsonElement? data, string propertyName)
    {
        if (data is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool CanReplace(StoredCommand command) =>
        command.State is "failed" or "cancelled";

    private static string ScopedKey(string serverId, string idempotencyKey) =>
        $"{serverId}\nin-game-notification.dispatch\n{idempotencyKey}";

    private sealed class StoredCommand
    {
        public required Guid CommandId { get; init; }
        public required InGameNotification Notification { get; init; }
        public required string ServerId { get; init; }
        public required string IdempotencyKey { get; init; }
        public required string RequestHash { get; init; }
        public required string Actor { get; init; }
        public required string State { get; set; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ScheduledFor { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public int? AttemptedRecipients { get; set; }
        public int? DeliveredRecipients { get; set; }
        public bool? DeliveryAcknowledged { get; set; }
    }

    private sealed record DispatchResult(
        bool Success,
        bool OutcomeUncertain,
        string? ErrorCode,
        string? ErrorMessage,
        int? AttemptedRecipients,
        int? DeliveredRecipients,
        bool? DeliveryAcknowledged)
    {
        public static DispatchResult Succeeded(int? attempted, int? delivered, bool? acknowledged) =>
            new(true, false, null, null, attempted, delivered, acknowledged);

        public static DispatchResult Failed(
            string code,
            string message,
            int? attempted = null,
            int? delivered = null,
            bool? acknowledged = null) =>
            new(false, false, code, message, attempted, delivered, acknowledged);

        public static DispatchResult Uncertain(
            string code,
            string message,
            int? attempted = null,
            int? delivered = null,
            bool? acknowledged = null) =>
            new(false, true, code, message, attempted, delivered, acknowledged);
    }

    private sealed record QueueEvent(
        Guid EventId,
        Guid CommandId,
        Guid NotificationId,
        string EventType,
        string State,
        DateTimeOffset At,
        string ServerId,
        string IdempotencyKey,
        string RequestHash,
        string Reason,
        string Actor,
        DateTimeOffset? ScheduledFor,
        DateTimeOffset? ExpiresAt,
        InGameNotification? Notification,
        string? ErrorCode,
        string? ErrorMessage,
        int? AttemptedRecipients,
        int? DeliveredRecipients,
        bool? DeliveryAcknowledged);
}

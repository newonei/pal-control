using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed record AnnouncementEnqueueResult(
    CommandStatus? Command,
    bool Created,
    bool IdempotencyConflict,
    bool AnnouncementConflict);

public sealed class AnnouncementCommandQueue : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Dictionary<Guid, StoredCommand> _commands = [];
    private readonly Dictionary<string, (string Hash, Guid CommandId)> _idempotency =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Guid> _announcementCommands = [];
    private readonly List<CommandAuditEvent> _audit = [];
    private readonly AnnouncementStore _announcements;
    private readonly PalworldRestClient _palworld;
    private readonly NativeBridgeState _nativeBridgeState;
    private readonly NativeBridgeClient _nativeBridge;
    private readonly ILogger<AnnouncementCommandQueue> _logger;
    private readonly string _eventPath;
    private readonly FileStream _leaseStream;
    private volatile bool _isReady;
    private volatile bool _workerRunning;

    public AnnouncementCommandQueue(
        IHostEnvironment environment,
        IOptions<CommandPersistenceOptions> options,
        AnnouncementStore announcements,
        PalworldRestClient palworld,
        NativeBridgeState nativeBridgeState,
        NativeBridgeClient nativeBridge,
        ILogger<AnnouncementCommandQueue> logger)
    {
        _announcements = announcements;
        _palworld = palworld;
        _nativeBridgeState = nativeBridgeState;
        _nativeBridge = nativeBridge;
        _logger = logger;
        var dataDirectory = Path.GetFullPath(
            Path.IsPathRooted(options.Value.DataDirectory)
                ? options.Value.DataDirectory
                : Path.Combine(environment.ContentRootPath, options.Value.DataDirectory));
        Directory.CreateDirectory(dataDirectory);
        _leaseStream = new FileStream(
            Path.Combine(dataDirectory, "command-queue.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        _eventPath = Path.Combine(dataDirectory, "command-audit.jsonl");
        EnsureWritable();
        LoadEvents();
    }

    public bool IsReady =>
        _isReady && _workerRunning && _announcements.IsReady;

    public async Task<AnnouncementEnqueueResult> EnqueueAsync(
        string serverId,
        Announcement announcement,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var requestedPublishAt = announcement.PublishAt?.ToUniversalTime();
        DateTimeOffset? scheduledFor = announcement.PublishAt is { } publishAt && publishAt > now
            ? publishAt.ToUniversalTime()
            : null;
        var message = $"【{announcement.Title}】\n{announcement.Body}";
        var reason = $"发布公告：{announcement.Title}";
        var requestHash = HashRequest(
            serverId,
            announcement.AnnouncementId,
            message,
            announcement.Channels,
            requestedPublishAt,
            announcement.ExpiresAt?.ToUniversalTime());
        var scopedKey = ScopedKey(serverId, idempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_isReady)
            {
                throw new IOException("The command audit store is not writable.");
            }
            if (_idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
                {
                    return new AnnouncementEnqueueResult(null, false, true, false);
                }

                return new AnnouncementEnqueueResult(
                    ToStatus(_commands[existing.CommandId]),
                    false,
                    false,
                    false);
            }
            if (_announcementCommands.TryGetValue(
                    announcement.AnnouncementId,
                    out var existingAnnouncementCommandId) &&
                _commands.TryGetValue(existingAnnouncementCommandId, out var existingAnnouncementCommand) &&
                !CanReplace(existingAnnouncementCommand))
            {
                return new AnnouncementEnqueueResult(
                    ToStatus(existingAnnouncementCommand),
                    false,
                    false,
                    true);
            }

            var command = new StoredCommand
            {
                CommandId = Guid.NewGuid(),
                AnnouncementId = announcement.AnnouncementId,
                ServerId = serverId,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                Title = announcement.Title,
                Body = announcement.Body,
                Message = message,
                Reason = reason,
                Actor = actor,
                State = "accepted",
                CreatedAt = now,
                ScheduledFor = scheduledFor,
                ExpiresAt = announcement.ExpiresAt?.ToUniversalTime(),
                Deliveries = CreateDeliveries(announcement.Channels)
            };
            var accepted = CreateEvent(command, "accepted", "accepted", now);
            await AppendEventAsync(accepted);
            ApplyEvent(accepted);
            SignalWorker();
            return new AnnouncementEnqueueResult(ToStatus(command), true, false, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AnnouncementEnqueueResult?> FindExistingAsync(
        string serverId,
        Announcement announcement,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var message = $"【{announcement.Title}】\n{announcement.Body}";
        var requestHash = HashRequest(
            serverId,
            announcement.AnnouncementId,
            message,
            announcement.Channels,
            announcement.PublishAt?.ToUniversalTime(),
            announcement.ExpiresAt?.ToUniversalTime());

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_idempotency.TryGetValue(ScopedKey(serverId, idempotencyKey), out var existing))
            {
                return null;
            }
            if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
            {
                return new AnnouncementEnqueueResult(null, false, true, false);
            }
            return new AnnouncementEnqueueResult(
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

    public async Task<IReadOnlyList<CommandAuditEvent>> GetAuditAsync(
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
            await RecoverInterruptedCommandsAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                StoredCommand? next;
                DateTimeOffset? nextScheduled;
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    next = _commands.Values
                        .Where(command =>
                            string.Equals(command.State, "accepted", StringComparison.Ordinal) &&
                            (command.ScheduledFor is null || command.ScheduledFor <= now))
                        .OrderBy(command => command.ScheduledFor ?? command.CreatedAt)
                        .ThenBy(command => command.CreatedAt)
                        .FirstOrDefault();
                    nextScheduled = _commands.Values
                        .Where(command =>
                            string.Equals(command.State, "accepted", StringComparison.Ordinal) &&
                            command.ScheduledFor > now)
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
            _logger.LogCritical(exception, "The announcement command worker stopped unexpectedly.");
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

    private async Task RecoverInterruptedCommandsAsync(CancellationToken cancellationToken)
    {
        (Guid CommandId, Guid AnnouncementId, string ServerId)[] interrupted;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            interrupted = _commands.Values
                .Where(command => string.Equals(command.State, "dispatched", StringComparison.Ordinal))
                .Select(command => (
                    command.CommandId,
                    command.AnnouncementId,
                    command.ServerId))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var interruptedCommand in interrupted)
        {
            var announcement = await _announcements.GetAsync(
                interruptedCommand.ServerId,
                interruptedCommand.AnnouncementId,
                cancellationToken);
            if (string.Equals(announcement?.State, "published", StringComparison.Ordinal))
            {
                StoredCommand? publishedCommand;
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    _commands.TryGetValue(interruptedCommand.CommandId, out publishedCommand);
                }
                finally
                {
                    _gate.Release();
                }
                if (publishedCommand is not null)
                {
                    foreach (var delivery in publishedCommand.Deliveries
                                 .Where(delivery => delivery.State == "dispatched")
                                 .ToArray())
                    {
                        await TransitionChannelAsync(
                            publishedCommand.CommandId,
                            delivery.Channel,
                            "channel-recovered-succeeded",
                            "succeeded",
                            null,
                            null,
                            null,
                            cancellationToken);
                    }
                }
                await TransitionAsync(
                    interruptedCommand.CommandId,
                    "recovered-succeeded",
                    "succeeded",
                    null,
                    null,
                    null,
                    cancellationToken);
                continue;
            }

            StoredCommand? command;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _commands.TryGetValue(interruptedCommand.CommandId, out command);
            }
            finally
            {
                _gate.Release();
            }

            if (command is null)
            {
                continue;
            }
            foreach (var delivery in command.Deliveries
                         .Where(delivery => string.Equals(delivery.State, "dispatched", StringComparison.Ordinal))
                         .ToArray())
            {
                await TransitionChannelAsync(
                    command.CommandId,
                    delivery.Channel,
                    "channel-recovered-uncertain",
                    "uncertain",
                    null,
                    "COMMAND_OUTCOME_UNCERTAIN",
                    "The service restarted after dispatch; this channel will not be sent again automatically.",
                    cancellationToken);
            }

            await DispatchAsync(command.CommandId, cancellationToken);
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
        if (command is null || command.State is not ("accepted" or "dispatched"))
        {
            return;
        }
        if (command.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            await _announcements.SetStateAsync(
                command.AnnouncementId,
                "expired",
                "announcement-command-worker",
                stoppingToken);
            await TransitionAsync(
                commandId,
                "expired",
                "failed",
                null,
                "ANNOUNCEMENT_EXPIRED",
                "The announcement expired before the queued command could be dispatched.",
                stoppingToken);
            return;
        }

        foreach (var delivery in command.Deliveries
                     .Where(delivery => string.Equals(delivery.State, "accepted", StringComparison.Ordinal))
                     .ToArray())
        {
            if (string.Equals(delivery.Channel, "client-overlay", StringComparison.Ordinal) &&
                !ClientOverlayReady())
            {
                await TransitionChannelAsync(
                    commandId,
                    delivery.Channel,
                    "channel-failed",
                    "failed",
                    null,
                    "ANNOUNCEMENT_CLIENT_OVERLAY_UNAVAILABLE",
                    "The Native Bridge client-overlay capability was unavailable before dispatch.",
                    stoppingToken);
                continue;
            }
            if (string.Equals(delivery.Channel, "top-banner", StringComparison.Ordinal) &&
                !TopBannerReady())
            {
                await TransitionChannelAsync(
                    commandId,
                    delivery.Channel,
                    "channel-failed",
                    "failed",
                    null,
                    "ANNOUNCEMENT_TOP_BANNER_UNAVAILABLE",
                    "The Native Bridge top-banner capability was unavailable before dispatch.",
                    stoppingToken);
                continue;
            }

            try
            {
                command = await TransitionChannelAsync(
                    commandId,
                    delivery.Channel,
                    "channel-dispatched",
                    "dispatched",
                    null,
                    null,
                    null,
                    stoppingToken);
                if (command is null)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    exception,
                    "Could not persist dispatch for announcement command {CommandId} channel {Channel}.",
                    commandId,
                    delivery.Channel);
                _isReady = false;
                throw;
            }

            ChannelDispatchResult result;
            try
            {
                result = await DispatchChannelAsync(command, delivery.Channel, stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Unexpected announcement dispatch failure for command {CommandId} channel {Channel}.",
                    commandId,
                    delivery.Channel);
                result = ChannelDispatchResult.OutcomeUncertain(
                    "ANNOUNCEMENT_DISPATCH_INTERRUPTED",
                    "The announcement request ended unexpectedly after dispatch.");
            }

            var persistenceToken = stoppingToken.IsCancellationRequested
                ? CancellationToken.None
                : stoppingToken;
            await TransitionChannelAsync(
                commandId,
                delivery.Channel,
                result.Success ? "channel-succeeded" : result.Uncertain ? "channel-uncertain" : "channel-failed",
                result.Success ? "succeeded" : result.Uncertain ? "uncertain" : "failed",
                result.HttpStatus,
                result.ErrorCode,
                result.ErrorMessage,
                persistenceToken,
                result.AttemptedRecipients,
                result.DeliveredRecipients);
        }

        await FinalizeCommandAsync(
            commandId,
            stoppingToken.IsCancellationRequested ? CancellationToken.None : stoppingToken);
    }

    private async Task<StoredCommand?> TransitionAsync(
        Guid commandId,
        string eventType,
        string state,
        int? httpStatus,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(commandId, out var command))
            {
                return null;
            }

            var changed = CreateEvent(
                command,
                eventType,
                state,
                DateTimeOffset.UtcNow,
                httpStatus,
                errorCode,
                errorMessage);
            await AppendEventAsync(changed);
            ApplyEvent(changed);
            return command;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoredCommand?> TransitionChannelAsync(
        Guid commandId,
        string channel,
        string eventType,
        string state,
        int? httpStatus,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken,
        int? attemptedRecipients = null,
        int? deliveredRecipients = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(commandId, out var command) ||
                command.Deliveries.All(delivery =>
                    !string.Equals(delivery.Channel, channel, StringComparison.Ordinal)))
            {
                return null;
            }

            var changed = CreateEvent(
                command,
                eventType,
                state,
                DateTimeOffset.UtcNow,
                httpStatus,
                errorCode,
                errorMessage,
                channel,
                attemptedRecipients,
                deliveredRecipients);
            await AppendEventAsync(changed);
            ApplyEvent(changed);
            return command;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ChannelDispatchResult> DispatchChannelAsync(
        StoredCommand command,
        string channel,
        CancellationToken cancellationToken)
    {
        if (string.Equals(channel, "chat", StringComparison.Ordinal))
        {
            var restResult = await _palworld.AnnounceAsync(command.Message, cancellationToken);
            return new ChannelDispatchResult(
                restResult.Success,
                restResult.Uncertain,
                restResult.HttpStatus,
                null,
                null,
                restResult.ErrorCode,
                restResult.ErrorMessage);
        }

        if (string.Equals(channel, "client-overlay", StringComparison.Ordinal))
        {
            return await DispatchNativeAnnouncementAsync(
                command,
                "announcements.overlay.send",
                "overlay",
                "client overlay",
                "ANNOUNCEMENT_CLIENT_OVERLAY",
                cancellationToken);
        }

        if (string.Equals(channel, "top-banner", StringComparison.Ordinal))
        {
            return await DispatchNativeAnnouncementAsync(
                command,
                "announcements.banner.send",
                "banner",
                "top banner",
                "ANNOUNCEMENT_TOP_BANNER",
                cancellationToken);
        }

        return ChannelDispatchResult.Failed(
            "UNSUPPORTED_ANNOUNCEMENT_CHANNEL",
            $"Announcement channel '{channel}' is not supported.");
    }

    private async Task<ChannelDispatchResult> DispatchNativeAnnouncementAsync(
        StoredCommand command,
        string operation,
        string idempotencyName,
        string displayName,
        string errorCodePrefix,
        CancellationToken cancellationToken)
    {
        var result = await _nativeBridge.SendCommandAsync(
            command.ServerId,
            operation,
            new
            {
                title = command.Title,
                body = command.Body,
                message = command.Message,
                audience = "global"
            },
            command.Reason,
            cancellationToken,
            idempotencyKey: $"announcement-{idempotencyName}-{command.CommandId:N}");
        var attemptedRecipients = ReadRecipientCount(result.Data, "attemptedRecipients");
        var deliveredRecipients = ReadRecipientCount(result.Data, "deliveredRecipients");
        if (string.Equals(result.State, "succeeded", StringComparison.Ordinal))
        {
            return ChannelDispatchResult.Delivered(attemptedRecipients, deliveredRecipients);
        }
        if (string.Equals(result.State, "failed", StringComparison.Ordinal))
        {
            return ChannelDispatchResult.Failed(
                result.Error?.Code ?? $"{errorCodePrefix}_FAILED",
                result.Error?.Message ?? $"The Native Bridge rejected the {displayName}.",
                attemptedRecipients,
                deliveredRecipients);
        }
        return ChannelDispatchResult.OutcomeUncertain(
            result.Error?.Code ?? $"{errorCodePrefix}_OUTCOME_UNCERTAIN",
            result.Error?.Message ?? $"The Native Bridge could not confirm the {displayName} outcome.",
            attemptedRecipients,
            deliveredRecipients);
    }

    private async Task FinalizeCommandAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        StoredCommand? command;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _commands.TryGetValue(commandId, out command);
        }
        finally
        {
            _gate.Release();
        }
        if (command is null || command.Deliveries.Any(delivery =>
                delivery.State is "accepted" or "dispatched"))
        {
            return;
        }

        var anySucceeded = command.Deliveries.Any(delivery => delivery.State == "succeeded");
        var anyUncertain = command.Deliveries.Any(delivery => delivery.State == "uncertain");
        var allSucceeded = command.Deliveries.All(delivery => delivery.State == "succeeded");
        if (allSucceeded)
        {
            await _announcements.SetStateAsync(
                command.AnnouncementId,
                "published",
                "announcement-command-worker",
                cancellationToken);
            await TransitionAsync(
                commandId,
                "succeeded",
                "succeeded",
                null,
                null,
                null,
                cancellationToken);
            return;
        }

        if (anyUncertain)
        {
            await TransitionAsync(
                commandId,
                "uncertain",
                "uncertain",
                null,
                "ANNOUNCEMENT_OUTCOME_UNCERTAIN",
                "At least one announcement channel has an uncertain delivery outcome; no channel will be resent automatically.",
                cancellationToken);
            return;
        }

        await TransitionAsync(
            commandId,
            "failed",
            "failed",
            null,
            anySucceeded ? "ANNOUNCEMENT_PARTIAL_DELIVERY" : "ANNOUNCEMENT_DELIVERY_FAILED",
            anySucceeded
                ? "The announcement was delivered to only some requested channels; delivered channels will not be resent automatically."
                : "All requested announcement channels failed with a definite non-delivery result.",
            cancellationToken);
    }

    private bool ClientOverlayReady()
    {
        var snapshot = _nativeBridgeState.GetSnapshot();
        return snapshot.Connected &&
               snapshot.Capabilities.Contains("announcements.overlay.write");
    }

    private bool TopBannerReady()
    {
        var snapshot = _nativeBridgeState.GetSnapshot();
        return snapshot.Connected &&
               snapshot.Capabilities.Contains("announcements.banner.write");
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
                _logger.LogWarning("Ignoring a partial final command audit event after an interrupted write.");
                TruncatePartialFinalLine();
                break;
            }

            if (stored is null)
            {
                throw new InvalidDataException($"Command audit event {index + 1} is empty.");
            }
            ApplyEvent(stored);
        }
    }

    private void ApplyEvent(QueueEvent stored)
    {
        if (string.Equals(stored.EventType, "accepted", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(stored.Message))
            {
                throw new InvalidDataException($"Accepted command {stored.CommandId} has no message.");
            }
            var scopedKey = ScopedKey(stored.ServerId, stored.IdempotencyKey);
            if (_idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, stored.RequestHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Command idempotency key '{stored.IdempotencyKey}' has conflicting hashes.");
                }
                _audit.Add(ToAudit(stored));
                return;
            }
            var command = new StoredCommand
            {
                CommandId = stored.CommandId,
                AnnouncementId = stored.AnnouncementId,
                ServerId = stored.ServerId,
                IdempotencyKey = stored.IdempotencyKey,
                RequestHash = stored.RequestHash,
                Title = stored.Title ?? string.Empty,
                Body = stored.Body ?? stored.Message,
                Message = stored.Message,
                Reason = stored.Reason,
                Actor = stored.Actor,
                State = stored.State,
                CreatedAt = stored.At,
                ScheduledFor = stored.ScheduledFor,
                ExpiresAt = stored.ExpiresAt,
                Deliveries = CreateDeliveries(stored.Channels ?? ["chat"])
            };
            _commands[command.CommandId] = command;
            _idempotency[scopedKey] =
                (command.RequestHash, command.CommandId);
            _announcementCommands[command.AnnouncementId] = command.CommandId;
        }
        else if (_commands.TryGetValue(stored.CommandId, out var current))
        {
            if (stored.Channel is { Length: > 0 } channel)
            {
                var delivery = current.Deliveries.FirstOrDefault(item =>
                    string.Equals(item.Channel, channel, StringComparison.Ordinal));
                if (delivery is null)
                {
                    throw new InvalidDataException(
                        $"Command {stored.CommandId} has an event for unknown channel '{channel}'.");
                }
                delivery.State = stored.State;
                delivery.HttpStatus = stored.HttpStatus;
                delivery.ErrorCode = stored.ErrorCode;
                delivery.ErrorMessage = stored.ErrorMessage;
                delivery.AttemptedRecipients = stored.AttemptedRecipients;
                delivery.DeliveredRecipients = stored.DeliveredRecipients;
                current.State = "dispatched";
            }
            else
            {
                current.State = stored.State;
                current.HttpStatus = stored.HttpStatus;
                current.ErrorCode = stored.ErrorCode;
                current.ErrorMessage = stored.ErrorMessage;
                if (stored.Channels is null && current.Deliveries.Count == 1)
                {
                    var legacyDelivery = current.Deliveries[0];
                    legacyDelivery.State = stored.State;
                    legacyDelivery.HttpStatus = stored.HttpStatus;
                    legacyDelivery.ErrorCode = stored.ErrorCode;
                    legacyDelivery.ErrorMessage = stored.ErrorMessage;
                }
                if (stored.State is "succeeded" or "failed" or "uncertain" or "cancelled")
                {
                    current.CompletedAt = stored.At;
                }
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
        int? httpStatus = null,
        string? errorCode = null,
        string? errorMessage = null,
        string? channel = null,
        int? attemptedRecipients = null,
        int? deliveredRecipients = null) => new(
            EventId: Guid.NewGuid(),
            CommandId: command.CommandId,
            AnnouncementId: command.AnnouncementId,
            EventType: eventType,
            State: state,
            At: at,
            ServerId: command.ServerId,
            IdempotencyKey: command.IdempotencyKey,
            RequestHash: command.RequestHash,
            Message: string.Equals(eventType, "accepted", StringComparison.Ordinal)
                ? command.Message
                : null,
            Reason: command.Reason,
            Actor: command.Actor,
            ScheduledFor: command.ScheduledFor,
            ExpiresAt: command.ExpiresAt,
            HttpStatus: httpStatus,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage,
            Channels: command.Deliveries.Select(delivery => delivery.Channel).ToArray(),
            Channel: channel,
            Transport: channel is null ? null : TransportFor(channel),
            Title: string.Equals(eventType, "accepted", StringComparison.Ordinal)
                ? command.Title
                : null,
            Body: string.Equals(eventType, "accepted", StringComparison.Ordinal)
                ? command.Body
                : null,
            AttemptedRecipients: attemptedRecipients,
            DeliveredRecipients: deliveredRecipients);

    private static CommandAuditEvent ToAudit(QueueEvent stored) => new(
        stored.EventId,
        stored.CommandId,
        stored.AnnouncementId,
        stored.EventType,
        stored.State,
        stored.At,
        stored.ServerId,
        stored.IdempotencyKey,
        stored.RequestHash,
        stored.Reason,
        stored.Actor,
        stored.ScheduledFor,
        stored.HttpStatus,
        stored.ErrorCode,
        stored.ErrorMessage,
        stored.Channel,
        stored.Transport,
        stored.AttemptedRecipients,
        stored.DeliveredRecipients);

    private static CommandStatus ToStatus(StoredCommand command)
    {
        object result = new
        {
            announcementId = command.AnnouncementId,
            delivered = command.Deliveries.All(delivery => delivery.State == "succeeded"),
            channels = command.Deliveries.Select(delivery => new
            {
                channel = delivery.Channel,
                state = delivery.State,
                httpStatus = delivery.HttpStatus,
                attemptedRecipients = delivery.AttemptedRecipients,
                deliveredRecipients = delivery.DeliveredRecipients,
                error = delivery.ErrorCode is null
                    ? null
                    : new
                    {
                        code = delivery.ErrorCode,
                        message = delivery.ErrorMessage ?? "Announcement channel delivery failed."
                    }
            }).ToArray()
        };
        var error = command.ErrorCode is null
            ? null
            : new ApiError(command.ErrorCode, command.ErrorMessage ?? "Announcement command failed.");
        return new CommandStatus(
            command.CommandId,
            command.State,
            command.CreatedAt,
            command.CompletedAt,
            result,
            error,
            $"/api/v1/commands/{command.CommandId}");
    }

    private static string HashRequest(
        string serverId,
        Guid announcementId,
        string message,
        IReadOnlyList<string> channels,
        DateTimeOffset? scheduledFor,
        DateTimeOffset? expiresAt)
    {
        var normalizedChannels = channels
            .Select(channel => channel.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(channel => channel, StringComparer.Ordinal)
            .ToArray();
        var canonical = normalizedChannels.Length == 1 && normalizedChannels[0] == "chat"
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                operation = "announcement.publish",
                serverId,
                announcementId,
                message,
                scheduledFor,
                expiresAt
            }, JsonOptions)
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                operation = "announcement.publish.v2",
                serverId,
                announcementId,
                message,
                channels = normalizedChannels,
                scheduledFor,
                expiresAt
            }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static List<StoredChannelDelivery> CreateDeliveries(IEnumerable<string> channels) =>
        channels
            .Select(channel => channel.Trim().ToLowerInvariant())
            .Where(channel => channel.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(channel => channel, StringComparer.Ordinal)
            .Select(channel => new StoredChannelDelivery { Channel = channel })
            .ToList();

    private static string TransportFor(string channel) => channel switch
    {
        "chat" => "official-rest",
        "client-overlay" => "native-bridge",
        "top-banner" => "native-bridge",
        _ => "unsupported"
    };

    private static bool CanReplace(StoredCommand command) =>
        command.State is "failed" or "cancelled" &&
        command.Deliveries.All(delivery => delivery.State is "accepted" or "failed" or "cancelled");

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

    private static string ScopedKey(string serverId, string idempotencyKey) =>
        $"{serverId}\n{idempotencyKey}";

    private sealed class StoredCommand
    {
        public required Guid CommandId { get; init; }
        public required Guid AnnouncementId { get; init; }
        public required string ServerId { get; init; }
        public required string IdempotencyKey { get; init; }
        public required string RequestHash { get; init; }
        public required string Title { get; init; }
        public required string Body { get; init; }
        public required string Message { get; init; }
        public required string Reason { get; init; }
        public required string Actor { get; init; }
        public required string State { get; set; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ScheduledFor { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int? HttpStatus { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public int? AttemptedRecipients { get; set; }
        public int? DeliveredRecipients { get; set; }
        public required List<StoredChannelDelivery> Deliveries { get; init; }
    }

    private sealed class StoredChannelDelivery
    {
        public required string Channel { get; init; }
        public string State { get; set; } = "accepted";
        public int? HttpStatus { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public int? AttemptedRecipients { get; set; }
        public int? DeliveredRecipients { get; set; }
    }

    private sealed record ChannelDispatchResult(
        bool Success,
        bool Uncertain,
        int? HttpStatus,
        int? AttemptedRecipients,
        int? DeliveredRecipients,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static ChannelDispatchResult Delivered(
            int? attemptedRecipients,
            int? deliveredRecipients) =>
            new(true, false, null, attemptedRecipients, deliveredRecipients, null, null);

        public static ChannelDispatchResult Failed(
            string code,
            string message,
            int? attemptedRecipients = null,
            int? deliveredRecipients = null) =>
            new(false, false, null, attemptedRecipients, deliveredRecipients, code, message);

        public static ChannelDispatchResult OutcomeUncertain(
            string code,
            string message,
            int? attemptedRecipients = null,
            int? deliveredRecipients = null) =>
            new(false, true, null, attemptedRecipients, deliveredRecipients, code, message);
    }

    private sealed record QueueEvent(
        Guid EventId,
        Guid CommandId,
        Guid AnnouncementId,
        string EventType,
        string State,
        DateTimeOffset At,
        string ServerId,
        string IdempotencyKey,
        string RequestHash,
        string? Message,
        string Reason,
        string Actor,
        DateTimeOffset? ScheduledFor,
        DateTimeOffset? ExpiresAt,
        int? HttpStatus,
        string? ErrorCode,
        string? ErrorMessage,
        IReadOnlyList<string>? Channels = null,
        string? Channel = null,
        string? Transport = null,
        string? Title = null,
        string? Body = null,
        int? AttemptedRecipients = null,
        int? DeliveredRecipients = null);
}

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed class InGameNotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, InGameNotification> _notifications = [];
    private readonly Dictionary<Guid, string> _serverIds = [];
    private readonly Dictionary<string, (string Hash, Guid NotificationId)> _idempotency =
        new(StringComparer.Ordinal);
    private readonly string _eventPath;
    private readonly ILogger<InGameNotificationStore> _logger;
    private volatile bool _isReady;

    public InGameNotificationStore(
        IHostEnvironment environment,
        IOptions<CommandPersistenceOptions> options,
        ILogger<InGameNotificationStore> logger)
    {
        _logger = logger;
        var dataDirectory = Path.GetFullPath(
            Path.IsPathRooted(options.Value.DataDirectory)
                ? options.Value.DataDirectory
                : Path.Combine(environment.ContentRootPath, options.Value.DataDirectory));
        Directory.CreateDirectory(dataDirectory);
        _eventPath = Path.Combine(dataDirectory, "in-game-notification-events.jsonl");
        EnsureWritable();
        using (ControlPlaneLog.BeginOperation(
                   _logger,
                   nameof(InGameNotificationStore),
                   "persistence.load",
                   "in-game-notification-events"))
        {
            LoadEvents();
        }
    }

    public bool IsReady => _isReady;

    public async Task<InGameNotificationCreateResult> CreateAsync(
        string serverId,
        string idempotencyKey,
        InGameNotificationInput input,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = InGameNotificationContract.Normalize(input);
        var requestHash = InGameNotificationContract.HashInput(serverId, normalized);
        var scopedKey = ScopedKey(serverId, idempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_isReady)
            {
                throw new IOException("The in-game notification event store is not writable.");
            }
            if (_idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
                {
                    return new InGameNotificationCreateResult(null, false, true);
                }
                return new InGameNotificationCreateResult(
                    _notifications[existing.NotificationId],
                    false,
                    false);
            }

            var now = DateTimeOffset.UtcNow;
            var notification = new InGameNotification(
                NotificationId: Guid.NewGuid(),
                SchemaVersion: normalized.SchemaVersion,
                Template: normalized.Template,
                Audience: normalized.Audience,
                DisplayAt: normalized.DisplayAt,
                ExpiresAt: normalized.ExpiresAt,
                Reason: normalized.Reason,
                State: "draft",
                CreatedAt: now,
                UpdatedAt: now);
            var created = new NotificationEvent(
                EventId: Guid.NewGuid(),
                EventType: "created",
                At: now,
                ServerId: serverId,
                NotificationId: notification.NotificationId,
                IdempotencyKey: idempotencyKey,
                RequestHash: requestHash,
                Actor: actor,
                Notification: notification,
                State: notification.State);

            await AppendEventAsync(created);
            ApplyEvent(created);
            return new InGameNotificationCreateResult(notification, true, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InGameNotificationCreateResult?> FindExistingAsync(
        string serverId,
        string idempotencyKey,
        InGameNotificationInput input,
        CancellationToken cancellationToken)
    {
        var requestHash = InGameNotificationContract.HashInput(serverId, input);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_idempotency.TryGetValue(ScopedKey(serverId, idempotencyKey), out var existing))
            {
                return null;
            }
            if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
            {
                return new InGameNotificationCreateResult(null, false, true);
            }
            return new InGameNotificationCreateResult(
                _notifications[existing.NotificationId],
                false,
                false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InGameNotification?> GetAsync(
        string serverId,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _notifications.TryGetValue(notificationId, out var notification) &&
                   string.Equals(GetServerId(notificationId), serverId, StringComparison.Ordinal)
                ? notification
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InGameNotification>> ListAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _notifications.Values
                .Where(item => string.Equals(GetServerId(item.NotificationId), serverId, StringComparison.Ordinal))
                .OrderByDescending(item => item.CreatedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetStateAsync(
        Guid notificationId,
        string state,
        string actor,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_notifications.TryGetValue(notificationId, out var current) ||
                string.Equals(current.State, state, StringComparison.Ordinal))
            {
                return;
            }
            if (!CanTransition(current.State, state))
            {
                _logger.LogWarning(
                    "Ignoring non-monotonic in-game notification state change {CurrentState} -> {NextState} for {NotificationId}.",
                    current.State,
                    state,
                    notificationId);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var changed = new NotificationEvent(
                EventId: Guid.NewGuid(),
                EventType: "state-changed",
                At: now,
                ServerId: GetServerId(notificationId),
                NotificationId: notificationId,
                IdempotencyKey: null,
                RequestHash: null,
                Actor: actor,
                Notification: null,
                State: state);
            await AppendEventAsync(changed);
            ApplyEvent(changed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetServerId(Guid notificationId) =>
        _serverIds.TryGetValue(notificationId, out var serverId) ? serverId : string.Empty;

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

            NotificationEvent? stored;
            try
            {
                stored = JsonSerializer.Deserialize<NotificationEvent>(lines[index], JsonOptions);
            }
            catch (JsonException) when (index == lines.Length - 1 && HasPartialFinalLine())
            {
                _logger.LogWarning("Ignoring a partial final in-game notification event after an interrupted write.");
                TruncatePartialFinalLine();
                break;
            }

            if (stored is null)
            {
                throw new InvalidDataException($"In-game notification event {index + 1} is empty.");
            }
            ApplyEvent(stored);
        }
    }

    private void ApplyEvent(NotificationEvent stored)
    {
        if (string.Equals(stored.EventType, "created", StringComparison.Ordinal))
        {
            var notification = stored.Notification
                ?? throw new InvalidDataException($"Created notification {stored.NotificationId} has no payload.");
            var scopedKey = stored.IdempotencyKey is null
                ? null
                : ScopedKey(stored.ServerId, stored.IdempotencyKey);
            if (scopedKey is not null && stored.RequestHash is not null &&
                _idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, stored.RequestHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Notification idempotency key '{stored.IdempotencyKey}' has conflicting hashes.");
                }
                return;
            }

            _notifications[notification.NotificationId] = notification;
            _serverIds[notification.NotificationId] = stored.ServerId;
            if (scopedKey is not null && stored.RequestHash is not null)
            {
                _idempotency[scopedKey] = (stored.RequestHash, notification.NotificationId);
            }
            return;
        }

        if (string.Equals(stored.EventType, "state-changed", StringComparison.Ordinal) &&
            _notifications.TryGetValue(stored.NotificationId, out var current))
        {
            _notifications[stored.NotificationId] = current with
            {
                State = stored.State,
                UpdatedAt = stored.At
            };
        }
    }

    private async Task AppendEventAsync(NotificationEvent stored)
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

    private static string ScopedKey(string serverId, string idempotencyKey) =>
        $"{serverId}\nin-game-notification.create\n{idempotencyKey}";

    private static bool CanTransition(string current, string next) => current switch
    {
        "draft" => next is "scheduled" or "sent" or "failed" or "uncertain" or "expired" or "cancelled",
        "scheduled" => next is "sent" or "failed" or "uncertain" or "expired" or "cancelled",
        "failed" => next is "scheduled" or "sent" or "failed" or "uncertain" or "expired" or "cancelled",
        _ => false
    };

    private sealed record NotificationEvent(
        Guid EventId,
        string EventType,
        DateTimeOffset At,
        string ServerId,
        Guid NotificationId,
        string? IdempotencyKey,
        string? RequestHash,
        string Actor,
        InGameNotification? Notification,
        string State);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed class CommandPersistenceOptions
{
    public string DataDirectory { get; init; } = "data";
    public int PalDefenderQueueCapacity { get; init; } = 256;

    public bool IsValid(out string? error)
    {
        if (string.IsNullOrWhiteSpace(DataDirectory) ||
            DataDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = "CommandPersistence:DataDirectory must be a valid non-empty path.";
            return false;
        }
        if (PalDefenderQueueCapacity is < 1 or > 10_000)
        {
            error = "CommandPersistence:PalDefenderQueueCapacity must be between 1 and 10000.";
            return false;
        }
        error = null;
        return true;
    }
}

public sealed record AnnouncementCreateResult(
    Announcement? Announcement,
    bool Created,
    bool IdempotencyConflict);

public sealed class AnnouncementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Announcement> _announcements = [];
    private readonly Dictionary<string, (string Hash, Guid AnnouncementId)> _idempotency =
        new(StringComparer.Ordinal);
    private readonly string _eventPath;
    private readonly ILogger<AnnouncementStore> _logger;
    private volatile bool _isReady;

    public AnnouncementStore(
        IHostEnvironment environment,
        IOptions<CommandPersistenceOptions> options,
        ILogger<AnnouncementStore> logger)
    {
        _logger = logger;
        var dataDirectory = Path.GetFullPath(
            Path.IsPathRooted(options.Value.DataDirectory)
                ? options.Value.DataDirectory
                : Path.Combine(environment.ContentRootPath, options.Value.DataDirectory));
        Directory.CreateDirectory(dataDirectory);
        _eventPath = Path.Combine(dataDirectory, "announcement-events.jsonl");
        EnsureWritable();
        using (ControlPlaneLog.BeginOperation(
                   _logger,
                   nameof(AnnouncementStore),
                   "persistence.load",
                   "announcement-events"))
        {
            LoadEvents();
        }
    }

    public bool IsReady => _isReady;

    public async Task<AnnouncementCreateResult> CreateAsync(
        string serverId,
        string idempotencyKey,
        AnnouncementInput input,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(input);
        var requestHash = HashRequest(serverId, normalized);
        var scopedKey = ScopedKey(serverId, idempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_isReady)
            {
                throw new IOException("The announcement event store is not writable.");
            }
            if (_idempotency.TryGetValue(scopedKey, out var existing))
            {
                if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
                {
                    return new AnnouncementCreateResult(null, false, true);
                }

                return new AnnouncementCreateResult(
                    _announcements[existing.AnnouncementId],
                    false,
                    false);
            }

            var now = DateTimeOffset.UtcNow;
            var announcement = new Announcement(
                AnnouncementId: Guid.NewGuid(),
                Title: normalized.Title,
                Body: normalized.Body,
                Audience: normalized.Audience,
                Channels: normalized.Channels,
                PublishAt: normalized.PublishAt,
                ExpiresAt: normalized.ExpiresAt,
                State: "draft",
                CreatedAt: now,
                UpdatedAt: now);
            var created = new AnnouncementEvent(
                EventId: Guid.NewGuid(),
                EventType: "created",
                At: now,
                ServerId: serverId,
                AnnouncementId: announcement.AnnouncementId,
                IdempotencyKey: idempotencyKey,
                RequestHash: requestHash,
                Actor: actor,
                Announcement: announcement,
                State: announcement.State);

            await AppendEventAsync(created);
            _announcements.Add(announcement.AnnouncementId, announcement);
            _serverIds.Add(announcement.AnnouncementId, serverId);
            _idempotency.Add(scopedKey, (requestHash, announcement.AnnouncementId));
            return new AnnouncementCreateResult(announcement, true, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Announcement?> GetAsync(
        string serverId,
        Guid announcementId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _announcements.TryGetValue(announcementId, out var announcement) &&
                   string.Equals(GetServerId(announcementId), serverId, StringComparison.Ordinal)
                ? announcement
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Announcement>> ListAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _announcements.Values
                .Where(item => string.Equals(GetServerId(item.AnnouncementId), serverId, StringComparison.Ordinal))
                .OrderByDescending(item => item.CreatedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetStateAsync(
        Guid announcementId,
        string state,
        string actor,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_announcements.TryGetValue(announcementId, out var current) ||
                string.Equals(current.State, state, StringComparison.Ordinal))
            {
                return;
            }
            if (!CanTransition(current.State, state))
            {
                _logger.LogWarning(
                    "Ignoring non-monotonic announcement state change {CurrentState} -> {NextState} for {AnnouncementId}.",
                    current.State,
                    state,
                    announcementId);
                return;
            }

            var updated = current with { State = state, UpdatedAt = DateTimeOffset.UtcNow };
            var changed = new AnnouncementEvent(
                EventId: Guid.NewGuid(),
                EventType: "state-changed",
                At: updated.UpdatedAt,
                ServerId: GetServerId(announcementId),
                AnnouncementId: announcementId,
                IdempotencyKey: null,
                RequestHash: null,
                Actor: actor,
                Announcement: null,
                State: state);
            await AppendEventAsync(changed);
            _announcements[announcementId] = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private readonly Dictionary<Guid, string> _serverIds = [];

    private string GetServerId(Guid announcementId) =>
        _serverIds.TryGetValue(announcementId, out var serverId) ? serverId : string.Empty;

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

            AnnouncementEvent? stored;
            try
            {
                stored = JsonSerializer.Deserialize<AnnouncementEvent>(lines[index], JsonOptions);
            }
            catch (JsonException) when (index == lines.Length - 1 && HasPartialFinalLine())
            {
                _logger.LogWarning("Ignoring a partial final announcement event after an interrupted write.");
                TruncatePartialFinalLine();
                break;
            }

            if (stored is null)
            {
                throw new InvalidDataException($"Announcement event {index + 1} is empty.");
            }

            if (string.Equals(stored.EventType, "created", StringComparison.Ordinal))
            {
                var announcement = stored.Announcement
                    ?? throw new InvalidDataException($"Announcement event {index + 1} has no payload.");
                if (stored.IdempotencyKey is not null && stored.RequestHash is not null &&
                    _idempotency.TryGetValue(
                        ScopedKey(stored.ServerId, stored.IdempotencyKey),
                        out var existing))
                {
                    if (!string.Equals(existing.Hash, stored.RequestHash, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Announcement idempotency key '{stored.IdempotencyKey}' has conflicting hashes.");
                    }
                    continue;
                }
                _announcements[announcement.AnnouncementId] = announcement;
                _serverIds[announcement.AnnouncementId] = stored.ServerId;
                if (stored.IdempotencyKey is not null && stored.RequestHash is not null)
                {
                    _idempotency[ScopedKey(stored.ServerId, stored.IdempotencyKey)] =
                        (stored.RequestHash, announcement.AnnouncementId);
                }
            }
            else if (string.Equals(stored.EventType, "state-changed", StringComparison.Ordinal) &&
                     _announcements.TryGetValue(stored.AnnouncementId, out var current))
            {
                _announcements[stored.AnnouncementId] = current with
                {
                    State = stored.State,
                    UpdatedAt = stored.At
                };
            }
        }
    }

    private async Task AppendEventAsync(AnnouncementEvent stored)
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

    private static AnnouncementInput Normalize(AnnouncementInput input) => new(
        Title: input.Title.Trim(),
        Body: input.Body.Trim(),
        Audience: new AnnouncementAudience(
            input.Audience.Type.Trim().ToLowerInvariant(),
            input.Audience.Ids?.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray()),
        Channels: input.Channels
            .Select(item => item.Trim().ToLowerInvariant())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray(),
        PublishAt: input.PublishAt?.ToUniversalTime(),
        ExpiresAt: input.ExpiresAt?.ToUniversalTime());

    private static string HashRequest(string serverId, AnnouncementInput input)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            serverId,
            input.Title,
            input.Body,
            audience = input.Audience,
            input.Channels,
            input.PublishAt,
            input.ExpiresAt
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static string ScopedKey(string serverId, string idempotencyKey) =>
        $"{serverId}\n{idempotencyKey}";

    private static bool CanTransition(string current, string next) => current switch
    {
        "draft" => next is "scheduled" or "published" or "expired" or "cancelled",
        "scheduled" => next is "published" or "expired" or "cancelled",
        _ => false
    };

    private sealed record AnnouncementEvent(
        Guid EventId,
        string EventType,
        DateTimeOffset At,
        string ServerId,
        Guid AnnouncementId,
        string? IdempotencyKey,
        string? RequestHash,
        string Actor,
        Announcement? Announcement,
        string State);
}

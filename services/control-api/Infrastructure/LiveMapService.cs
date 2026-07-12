using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed class LiveMapOptions
{
    public int SampleIntervalMs { get; init; } = 1000;
    public int StaleAfterMs { get; init; } = 5000;
    public int UnavailableAfterMs { get; init; } = 30000;
    public int HeartbeatIntervalMs { get; init; } = 15000;
    public int MaxSubscribers { get; init; } = 64;
    public LiveMapCoordinateSpaceOptions CoordinateSpace { get; init; } = new();
}

public sealed class LiveMapCoordinateSpaceOptions
{
    public string MapId { get; init; } = "MainWorld";
    public string Units { get; init; } = "unreal-centimeters";
    public string? BackgroundUrl { get; init; } = "/palworld-map/full-map-z4.png";
    public LiveMapBoundsOptions Bounds { get; init; } = new();
    public LiveMapProjectionOptions Projection { get; init; } = new();
}

public sealed class LiveMapBoundsOptions
{
    public double MinX { get; init; } = -999940;
    public double MaxX { get; init; } = 447900;
    public double MinY { get; init; } = -738920;
    public double MaxY { get; init; } = 708920;
}

public sealed class LiveMapProjectionOptions
{
    public bool AxisSwap { get; init; } = true;
    public bool InvertX { get; init; } = true;
}

public sealed class LiveMapService : BackgroundService
{
    public const string SourceName = "palworld-official-rest.players";

    private readonly PalworldRestClient _palworld;
    private readonly ILogger<LiveMapService> _logger;
    private readonly ConcurrentDictionary<Guid, Channel<LiveMapSnapshot>> _subscribers = new();
    private readonly string _serverId;
    private readonly string _streamId = Guid.NewGuid().ToString("D");
    private readonly int _sampleIntervalMs;
    private readonly int _staleAfterMs;
    private readonly int _unavailableAfterMs;
    private readonly int _heartbeatIntervalMs;
    private readonly int _maxSubscribers;
    private readonly LiveMapCoordinateSpace _coordinateSpace;
    private LiveMapSnapshot _current;
    private int _subscriberCount;

    public LiveMapService(
        PalworldRestClient palworld,
        IOptions<LiveMapOptions> options,
        IConfiguration configuration,
        ILogger<LiveMapService> logger)
    {
        _palworld = palworld;
        _logger = logger;
        _serverId = configuration["Palworld:ServerId"] ?? "local";

        var value = options.Value;
        _sampleIntervalMs = Math.Clamp(value.SampleIntervalMs, 500, 5000);
        _staleAfterMs = Math.Max(_sampleIntervalMs, value.StaleAfterMs);
        _unavailableAfterMs = Math.Max(_staleAfterMs, value.UnavailableAfterMs);
        _heartbeatIntervalMs = Math.Clamp(value.HeartbeatIntervalMs, 1000, 60000);
        _maxSubscribers = Math.Clamp(value.MaxSubscribers, 1, 10000);

        var map = value.CoordinateSpace;
        _coordinateSpace = new LiveMapCoordinateSpace(
            MapId: string.IsNullOrWhiteSpace(map.MapId) ? "MainWorld" : map.MapId.Trim(),
            Units: string.IsNullOrWhiteSpace(map.Units) ? "unreal-centimeters" : map.Units.Trim(),
            Bounds: new LiveMapBounds(
                map.Bounds.MinX,
                map.Bounds.MaxX,
                map.Bounds.MinY,
                map.Bounds.MaxY),
            Projection: new LiveMapProjection(
                map.Projection.AxisSwap,
                map.Projection.InvertX),
            BackgroundUrl: string.IsNullOrWhiteSpace(map.BackgroundUrl)
                ? null
                : map.BackgroundUrl.Trim());

        var now = DateTimeOffset.UtcNow;
        _current = new LiveMapSnapshot(
            ServerId: _serverId,
            StreamId: _streamId,
            Sequence: 0,
            Status: "unavailable",
            Source: SourceName,
            ObservedAt: null,
            GeneratedAt: now,
            SampleIntervalMs: _sampleIntervalMs,
            StaleAfterMs: _staleAfterMs,
            UnavailableAfterMs: _unavailableAfterMs,
            CoordinateSpace: _coordinateSpace,
            Items: []);
    }

    public int HeartbeatIntervalMilliseconds => _heartbeatIntervalMs;

    public LiveMapSnapshot GetSnapshot() => Volatile.Read(ref _current);

    public LiveMapSubscription Subscribe()
    {
        if (Interlocked.Increment(ref _subscriberCount) > _maxSubscribers)
        {
            Interlocked.Decrement(ref _subscriberCount);
            throw new InvalidOperationException("The live-map subscriber limit has been reached.");
        }

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<LiveMapSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        if (!_subscribers.TryAdd(id, channel))
        {
            Interlocked.Decrement(ref _subscriberCount);
            throw new InvalidOperationException("Unable to create a live-map subscription.");
        }

        channel.Writer.TryWrite(GetSnapshot());
        return new LiveMapSubscription(this, id, channel.Reader);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SampleAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_sampleIntervalMs));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SampleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Writer.TryComplete();
            }
            _subscribers.Clear();
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        IReadOnlyList<PalworldPlayerLocation>? locations = null;
        try
        {
            locations = await _palworld.TryGetPlayerLocationsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unexpected live-map sampling failure.");
        }

        var previous = GetSnapshot();
        LiveMapSnapshot next;
        if (locations is not null)
        {
            var items = locations
                .Where(player => double.IsFinite(player.LocationX) && double.IsFinite(player.LocationY))
                .Select(player => new LiveMapPlayer(
                    PlayerId: player.PlayerId,
                    Uid: player.Uid,
                    Name: player.Name,
                    Level: player.Level,
                    Online: true,
                    ObservedAt: generatedAt,
                    Position: new LiveMapPosition(player.LocationX, player.LocationY)))
                .ToArray();
            next = CreateSnapshot(
                sequence: previous.Sequence + 1,
                status: "live",
                observedAt: generatedAt,
                generatedAt: generatedAt,
                items: items);
        }
        else
        {
            var ageMs = previous.ObservedAt is { } observedAt
                ? Math.Max(0, (generatedAt - observedAt).TotalMilliseconds)
                : double.PositiveInfinity;
            var status = ageMs >= _unavailableAfterMs
                ? "unavailable"
                : ageMs >= _staleAfterMs
                    ? "stale"
                    : "live";
            next = CreateSnapshot(
                sequence: previous.Sequence + 1,
                status: status,
                observedAt: previous.ObservedAt,
                generatedAt: generatedAt,
                items: previous.Items);
        }

        Volatile.Write(ref _current, next);
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(next);
        }
    }

    private LiveMapSnapshot CreateSnapshot(
        long sequence,
        string status,
        DateTimeOffset? observedAt,
        DateTimeOffset generatedAt,
        IReadOnlyList<LiveMapPlayer> items) => new(
            ServerId: _serverId,
            StreamId: _streamId,
            Sequence: sequence,
            Status: status,
            Source: SourceName,
            ObservedAt: observedAt,
            GeneratedAt: generatedAt,
            SampleIntervalMs: _sampleIntervalMs,
            StaleAfterMs: _staleAfterMs,
            UnavailableAfterMs: _unavailableAfterMs,
            CoordinateSpace: _coordinateSpace,
            Items: items);

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
            Interlocked.Decrement(ref _subscriberCount);
        }
    }

    public sealed class LiveMapSubscription : IAsyncDisposable
    {
        private readonly LiveMapService _owner;
        private readonly Guid _id;
        private int _disposed;

        internal LiveMapSubscription(
            LiveMapService owner,
            Guid id,
            ChannelReader<LiveMapSnapshot> reader)
        {
            _owner = owner;
            _id = id;
            Reader = reader;
        }

        public ChannelReader<LiveMapSnapshot> Reader { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unsubscribe(_id);
            }
            return ValueTask.CompletedTask;
        }
    }
}

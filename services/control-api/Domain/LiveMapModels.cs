namespace PalControl.ControlApi.Domain;

public sealed record LiveMapSnapshot(
    string ServerId,
    string StreamId,
    long Sequence,
    string Status,
    string Source,
    DateTimeOffset? ObservedAt,
    DateTimeOffset GeneratedAt,
    int SampleIntervalMs,
    int StaleAfterMs,
    int UnavailableAfterMs,
    LiveMapCoordinateSpace CoordinateSpace,
    IReadOnlyList<LiveMapPlayer> Items);

public sealed record LiveMapCoordinateSpace(
    string MapId,
    string Units,
    LiveMapBounds Bounds,
    LiveMapProjection Projection,
    string? BackgroundUrl);

public sealed record LiveMapBounds(
    double MinX,
    double MaxX,
    double MinY,
    double MaxY);

public sealed record LiveMapProjection(
    bool AxisSwap,
    bool InvertX);

public sealed record LiveMapPlayer(
    string PlayerId,
    string? Uid,
    string Name,
    int? Level,
    bool Online,
    DateTimeOffset ObservedAt,
    LiveMapPosition Position);

public sealed record LiveMapPosition(
    double X,
    double Y);

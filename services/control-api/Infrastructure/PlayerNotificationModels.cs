using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Infrastructure;

public static class PlayerNotificationContract
{
    public const string SchemaVersion = "1";

    public static readonly IReadOnlySet<string> SourceTypes = new HashSet<string>(
        ["order-delivery", "resource-settlement", "season-end", "reconciliation"],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Severities = new HashSet<string>(
        ["success", "info", "warning", "error"],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> GameStates = new HashSet<string>(
        ["pending", "queued", "sent", "blocked", "failed", "uncertain", "not-requested"],
        StringComparer.Ordinal);
}

public sealed class PlayerNotificationOptions
{
    public bool GameDeliveryEnabled { get; init; }
}

public sealed record PlayerNotificationSource(
    Guid AccountId,
    Guid SeasonId,
    string TargetPlayerId,
    string SourceType,
    string SourceId,
    string SourceEventKey,
    string SourceVersion,
    string SourceState,
    string Severity,
    string Title,
    string Message,
    DateTimeOffset OccurredAt,
    bool RequestGameDelivery = true);

public sealed record PlayerNotificationRecord(
    Guid NotificationId,
    string SchemaVersion,
    Guid AccountId,
    Guid SeasonId,
    string SourceType,
    string SourceId,
    string SourceEventKey,
    string SourceVersion,
    string SourceState,
    string Severity,
    string Title,
    string Message,
    DateTimeOffset OccurredAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReadAt,
    string GameState,
    Guid? GameNotificationId,
    Guid? GameCommandId,
    string? GameErrorCode);

public sealed record PlayerNotificationUpsertResult(
    PlayerNotificationRecord Notification,
    bool Created,
    bool Changed);

public sealed record PlayerNotificationFeed(
    string SchemaVersion,
    int UnreadCount,
    bool HasActiveDelivery,
    IReadOnlyList<PlayerNotificationFeedItem> Items);

public sealed record PlayerNotificationFeedItem(
    Guid NotificationId,
    string SchemaVersion,
    Guid SeasonId,
    string SourceType,
    string SourceState,
    string Severity,
    string Title,
    string Message,
    DateTimeOffset OccurredAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReadAt,
    string GameState,
    string SafetyAction);

public sealed record PlayerNotificationReadResult(
    Guid NotificationId,
    DateTimeOffset ReadAt,
    int UnreadCount);

public sealed record PlayerNotificationReadAllResult(
    int MarkedRead,
    int UnreadCount);

public sealed record PlayerGameNotificationDispatch(
    Guid NotificationId,
    string SourceVersion,
    string SourceEventKey,
    string TargetPlayerId,
    string Title,
    string Message);

public sealed record PlayerGameNotificationDispatchResult(
    string State,
    Guid? GameNotificationId,
    Guid? GameCommandId,
    string? ErrorCode);

public interface IPlayerGameNotificationDispatcher
{
    Task<PlayerGameNotificationDispatchResult> DispatchOrReconcileAsync(
        PlayerGameNotificationDispatch request,
        Guid? existingNotificationId,
        Guid? existingCommandId,
        CancellationToken cancellationToken);
}

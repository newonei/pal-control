using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Domain;

public sealed record ServerCapabilities(
    string ServerId,
    bool OfficialRestConnected,
    bool PublishAnnouncements,
    bool PublishChatAnnouncements,
    bool PublishClientOverlay,
    bool PublishTopBanner,
    bool SendInGameNotifications,
    bool CommandQueueReady,
    bool AuditReady,
    bool BridgeConnected,
    bool ReadPlayers,
    bool ReadPlayerProgression,
    bool WritePlayerProgression,
    bool ReadInventory,
    bool WriteInventory,
    bool ReadPals,
    bool WritePals,
    string Mode,
    IReadOnlyList<string> Reasons);

public sealed record PlayerSummary(
    string PlayerId,
    string? Uid,
    string Name,
    bool Online,
    int? Level);

public sealed record PlayerProgressionMutationRequest(
    string ExpectedRevision,
    string Reason,
    bool DryRun,
    PlayerProgressionPatch Patch);

public sealed record PlayerProgressionPatch(
    int? AddExperience,
    int? TargetLevel,
    int? GrantStatusPoints,
    int? GrantTechnologyPoints,
    int? GrantAncientTechnologyPoints,
    string? AllocateStatusId,
    int? AllocateStatusPoints);

public sealed record InventorySnapshot(
    long Revision,
    string PlayerSessionId,
    IReadOnlyList<InventoryContainerSnapshot> Containers);

public sealed record InventoryContainerSnapshot(
    string ContainerId,
    string Kind,
    IReadOnlyList<InventorySlotSnapshot> Slots);

public sealed record InventorySlotSnapshot(
    string SlotId,
    string ItemId,
    int Quantity,
    int? Durability);

public sealed record InventoryOperation(
    string Type,
    string ItemId,
    int Quantity,
    string? SlotId,
    int? Durability,
    string? ContainerId,
    int? ExpectedQuantity);

public sealed record InventoryTransactionRequest(
    long ExpectedRevision,
    string Reason,
    bool DryRun,
    IReadOnlyList<InventoryOperation> Operations);

public sealed record PalMutationRequest(
    string ExpectedRevision,
    string Reason,
    string RequireState,
    bool DryRun,
    PalPatch Patch);

public sealed record PalPatch(
    string? Nickname,
    bool? Favorite,
    PassiveSkillPatch? PassiveSkill,
    IReadOnlyList<string>? ExpectedPassiveSkills,
    IReadOnlyList<string>? PassiveSkills,
    IReadOnlyList<string>? EquippedActiveSkills);

public sealed record PassiveSkillPatch(
    int Index,
    string ExpectedSkillId,
    string SkillId);

public sealed record AnnouncementAudience(
    string Type,
    IReadOnlyList<string>? Ids);

public sealed record AnnouncementInput(
    string Title,
    string Body,
    AnnouncementAudience Audience,
    IReadOnlyList<string> Channels,
    DateTimeOffset? PublishAt,
    DateTimeOffset? ExpiresAt);

public sealed record Announcement(
    Guid AnnouncementId,
    string Title,
    string Body,
    AnnouncementAudience Audience,
    IReadOnlyList<string> Channels,
    DateTimeOffset? PublishAt,
    DateTimeOffset? ExpiresAt,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CommandStatus(
    Guid CommandId,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    object? Result,
    ApiError? Error,
    string StatusUrl);

public sealed record CommandAuditEvent(
    Guid EventId,
    Guid CommandId,
    Guid AnnouncementId,
    string EventType,
    string State,
    DateTimeOffset At,
    string ServerId,
    string IdempotencyKey,
    string RequestHash,
    string Reason,
    string Actor,
    DateTimeOffset? ScheduledFor,
    int? HttpStatus,
    string? ErrorCode,
    string? ErrorMessage,
    string? Channel,
    string? Transport,
    int? AttemptedRecipients,
    int? DeliveredRecipients);

public sealed record AcceptedCommand(
    Guid CommandId,
    string State,
    DateTimeOffset AcceptedAt,
    string StatusUrl);

public sealed record ApiError(string Code, string Message, string? TraceId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? NextOpensAt { get; init; }
}

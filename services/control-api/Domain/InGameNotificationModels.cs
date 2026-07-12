using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Domain;

public sealed record InGameNotificationAudience
{
    public required string Type { get; init; }
    public IReadOnlyList<string>? Ids { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record InGameNotificationTemplate
{
    public required string Preset { get; init; }
    public JsonElement Parameters { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record InGameNotificationInput
{
    public required string SchemaVersion { get; init; }
    public required InGameNotificationTemplate Template { get; init; }
    public required InGameNotificationAudience Audience { get; init; }
    public DateTimeOffset? DisplayAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required string Reason { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record InGameNotification(
    Guid NotificationId,
    string SchemaVersion,
    InGameNotificationTemplate Template,
    InGameNotificationAudience Audience,
    DateTimeOffset? DisplayAt,
    DateTimeOffset? ExpiresAt,
    string Reason,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InGameNotificationParameterCapability
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public string? Pattern { get; init; }
    public JsonElement? Default { get; init; }

    [JsonPropertyName("enum")]
    public JsonElement? AllowedValues { get; init; }
}

public sealed record InGameNotificationPresetCapability
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string Function { get; init; }
    public long FunctionFlags { get; init; }
    public int PropertiesSize { get; init; }
    public required InGameNotificationDisplayPolicy PositionPolicy { get; init; }
    public required InGameNotificationDisplayPolicy DurationPolicy { get; init; }
    public IReadOnlyList<InGameNotificationParameterCapability> Parameters { get; init; } = [];
}

public sealed record InGameNotificationDisplayPolicy
{
    public required string Mode { get; init; }
    public bool Configurable { get; init; }
    public required string Note { get; init; }
}

public sealed record InGameNotificationCapabilityProbe
{
    public bool Ready { get; init; }
    public bool Dispatched { get; init; }
    public required string Mode { get; init; }
    public IReadOnlyList<string> SchemaVersions { get; init; } = [];
    public IReadOnlyList<string> SupportedAudiences { get; init; } = [];
    public IReadOnlyList<InGameNotificationPresetCapability> SupportedPresets { get; init; } = [];
}

public sealed record InGameNotificationProbeOutcome(
    InGameNotificationCapabilityProbe? Probe,
    ApiError? Error)
{
    public bool Ready => Probe is
    {
        Ready: true,
        Dispatched: false,
        Mode: "server-native-presets"
    };
}

public sealed record InGameNotificationCreateResult(
    InGameNotification? Notification,
    bool Created,
    bool IdempotencyConflict);

public sealed record InGameNotificationEnqueueResult(
    CommandStatus? Command,
    bool Created,
    bool IdempotencyConflict,
    bool NotificationConflict);

public sealed record InGameNotificationCommandAuditEvent(
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
    string? ErrorCode,
    string? ErrorMessage,
    int? AttemptedRecipients,
    int? DeliveredRecipients);

namespace PalControl.ControlApi.Domain;

public sealed record SaveOperationInput(string Reason);

public sealed record CreateBackupInput(string Label, string Reason);

public sealed record SaveFileStatistics(
    int FileCount,
    int PlayerFileCount,
    long TotalBytes,
    DateTimeOffset? LastModifiedAt);

public sealed record SaveDiskStatistics(long AvailableBytes, long TotalBytes);

public sealed record SaveBackupStatistics(
    int Count,
    long TotalBytes,
    int? VerifiedCount,
    DateTimeOffset? LatestCreatedAt);

public sealed record SaveValidationStatus(
    bool ProcessPathMatched,
    bool ServerNameMatched,
    bool WorldGuidMatched);

public sealed record SaveStatus(
    string ServerId,
    bool Ready,
    DateTimeOffset CheckedAt,
    string? WorldGuid,
    string? WorldName,
    string? GameVersion,
    int? OnlinePlayerCount,
    SaveFileStatistics Save,
    SaveDiskStatistics Disk,
    SaveBackupStatistics NativeBackups,
    SaveBackupStatistics ManagedBackups,
    SaveValidationStatus Validation,
    ApiError? Error);

public sealed record SaveBackupSummary(
    string BackupId,
    string Kind,
    string? Label,
    string? WorldGuid,
    string? GameVersion,
    DateTimeOffset CreatedAt,
    int FileCount,
    long TotalBytes,
    string Integrity,
    string Consistency,
    string? Actor,
    string? Reason,
    string? ManifestSha256);

public sealed record SaveCommandStatus(
    Guid CommandId,
    string Type,
    string State,
    string Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string StatusUrl,
    string? BackupId,
    object? Result,
    ApiError? Error);

public sealed record SaveCommandAuditEvent(
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
    string? ErrorCode,
    string? ErrorMessage);

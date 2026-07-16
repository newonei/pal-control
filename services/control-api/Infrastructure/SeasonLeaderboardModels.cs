using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PalControl.ControlApi.Infrastructure;

public static class SeasonLeaderboardPolicy
{
    public const string FrameworkVersion = "season-leaderboard-v1";

    public static SeasonLeaderboardRules Current { get; } = new(
        RulesVersion: "weekly-resource-ranking-v1",
        LateSettlementGraceMinutes: 15,
        MinimumSettledExchanges: 1,
        MinimumResourceValue: 100,
        MinimumTaskPoints: 10,
        ResourceTieBreakRule:
            "resourceValue desc, resourceQuantity desc, firstSettledAt asc, accountId canonical text asc",
        TaskTieBreakRule:
            "taskPoints desc, firstTaskPointAt asc, accountId canonical text asc",
        RewardTiers:
        [
            new("resource-value", 1, 500, "resource-rank-1"),
            new("resource-value", 2, 300, "resource-rank-2"),
            new("resource-value", 3, 150, "resource-rank-3"),
            new("task-points", 1, 300, "task-rank-1"),
            new("task-points", 2, 200, "task-rank-2"),
            new("task-points", 3, 100, "task-rank-3")
        ]);
}

public sealed record SeasonLeaderboardRules(
    string RulesVersion,
    int LateSettlementGraceMinutes,
    int MinimumSettledExchanges,
    long MinimumResourceValue,
    int MinimumTaskPoints,
    string ResourceTieBreakRule,
    string TaskTieBreakRule,
    IReadOnlyList<SeasonLeaderboardRewardTier> RewardTiers);

public sealed record SeasonLeaderboardRewardTier(
    string Board,
    int Rank,
    long MarketCoin,
    string RewardKey);

public sealed record SeasonLeaderboardResourceAggregate(
    string ItemId,
    string Category,
    long Quantity,
    long Value);

public sealed record SeasonLeaderboardCategoryAggregate(
    string Category,
    long Quantity,
    long Value);

public sealed record SeasonLeaderboardEntry(
    Guid AccountId,
    string AccountFingerprint,
    bool IdentityBannedAtFreeze,
    bool ManuallyExcludedAtFreeze,
    string? RankingExclusionCode,
    bool ResourceEligible,
    int? ResourceRank,
    bool TaskEligible,
    int? TaskRank,
    int SettledExchanges,
    long ResourceQuantity,
    long ResourceValue,
    int TaskPoints,
    DateTimeOffset? FirstSettledAt,
    DateTimeOffset? FirstTaskPointAt,
    IReadOnlyList<SeasonLeaderboardResourceAggregate> Items,
    IReadOnlyList<SeasonLeaderboardCategoryAggregate> Categories);

public sealed record SeasonLeaderboardRewardDecision(
    Guid AccountId,
    string Board,
    int Rank,
    long MarketCoin,
    string RewardKey,
    string State,
    string? ReasonCode);

public sealed record SeasonLeaderboardSnapshot(
    Guid SnapshotId,
    Guid SeasonId,
    string ServerId,
    string SeasonCode,
    string FrameworkVersion,
    SeasonLeaderboardRules Rules,
    string RulesHash,
    DateTimeOffset CutoffAt,
    string SourceHash,
    string SnapshotHash,
    DateTimeOffset FrozenAt,
    string FrozenBy,
    IReadOnlyList<SeasonLeaderboardEntry> Entries,
    IReadOnlyList<SeasonLeaderboardResourceAggregate> GlobalItems,
    IReadOnlyList<SeasonLeaderboardCategoryAggregate> GlobalCategories,
    int LateSettlementCountAtFreeze,
    int LateTaskPointCountAtFreeze,
    Guid? RewardJobId = null,
    string RewardState = "not-prepared",
    IReadOnlyList<SeasonLeaderboardRewardDecision>? RewardDecisions = null);

public sealed record SeasonLeaderboardSettlementLineSource(
    string ItemId,
    string Category,
    int Quantity,
    long UnitValue,
    long TotalValue);

public sealed record SeasonLeaderboardSettlementSource(
    Guid RunId,
    Guid AccountId,
    long Revision,
    DateTimeOffset SettledAt,
    Guid? ContentVersionId,
    string? ContentHash,
    int ItemCount,
    long TotalValue,
    IReadOnlyList<SeasonLeaderboardSettlementLineSource> Items);

public sealed record SeasonLeaderboardTaskPointSource(
    Guid EntryId,
    Guid AccountId,
    int Points,
    DateTimeOffset CreatedAt);

public sealed record SeasonLeaderboardEvidence(
    Guid SeasonId,
    DateTimeOffset CutoffAt,
    IReadOnlyList<SeasonLeaderboardSettlementSource> Settlements,
    IReadOnlyList<SeasonLeaderboardTaskPointSource> TaskPoints,
    IReadOnlyList<Guid> LateSettlementIdsObservedAtFreeze,
    IReadOnlyList<Guid> LateTaskPointEntryIdsObservedAtFreeze);

public sealed record SeasonLeaderboardAuditEvent(
    long Sequence,
    Guid EventId,
    string EventKey,
    Guid SeasonId,
    Guid? SnapshotId,
    string EventType,
    string Actor,
    string Reason,
    string CorrelationId,
    string DetailsHash,
    DateTimeOffset OccurredAt);

public sealed record SeasonLeaderboardExclusion(
    Guid SeasonId,
    Guid AccountId,
    bool Active,
    string Reason,
    string Actor,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SeasonLeaderboardRecord(
    SeasonLeaderboardSnapshot Snapshot,
    SeasonLeaderboardEvidence Evidence,
    IReadOnlyList<SeasonLeaderboardAuditEvent> Audit);

internal sealed record SeasonLeaderboardSourceData(
    SeasonLeaderboardEvidence Evidence);

internal static class SeasonLeaderboardHash
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string Of<T>(T value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions))));

    public static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }

    public static string Snapshot(SeasonLeaderboardSnapshot snapshot) => Of(new
    {
        snapshot.SnapshotId,
        snapshot.SeasonId,
        snapshot.ServerId,
        snapshot.SeasonCode,
        snapshot.FrameworkVersion,
        snapshot.Rules,
        snapshot.RulesHash,
        snapshot.CutoffAt,
        snapshot.SourceHash,
        snapshot.Entries,
        snapshot.GlobalItems,
        snapshot.GlobalCategories,
        snapshot.LateSettlementCountAtFreeze,
        snapshot.LateTaskPointCountAtFreeze
    });
}

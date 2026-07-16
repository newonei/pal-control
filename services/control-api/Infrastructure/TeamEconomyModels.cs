using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Infrastructure;

public sealed class TeamEconomyOptions
{
    public bool Enabled { get; set; }
    public string InvitePepper { get; set; } = string.Empty;
    public int ProjectionIntervalSeconds { get; set; } = 15;
    public int InviteLifetimeMinutes { get; set; } = 1_440;
    public int InviteMaximumUses { get; set; } = 10;
    public int MinimumLeaderboardMembers { get; set; } = 1;
    public long MinimumResourceValue { get; set; } = 1;
    public long MinimumTaskPoints { get; set; } = 1;
    public long MinimumDeliveredOrders { get; set; } = 1;
    public long ResourceItemsGoal { get; set; } = 500;
    public long ResourceValueGoal { get; set; } = 2_500;
    public long ReliableTaskPointsGoal { get; set; } = 100;
    public long DeliveredOrdersGoal { get; set; } = 10;
    public string GoalTemplateVersion { get; set; } = "team-goals-v1";

    public bool IsValid(out string error)
    {
        if (!Enabled)
        {
            error = string.Empty;
            return true;
        }
        if (InvitePepper.Length < 32 || InvitePepper.Length > 512 ||
            InvitePepper.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
            InvitePepper.Any(char.IsControl))
        {
            error = "Enabled team economy requires a non-placeholder 32-512 character invitation pepper.";
            return false;
        }
        if (ProjectionIntervalSeconds is < 5 or > 3_600 ||
            InviteLifetimeMinutes is < 5 or > 43_200 ||
            InviteMaximumUses is < 1 or > 100 ||
            MinimumLeaderboardMembers is < 1 or > 100)
        {
            error = "Team projection, invitation, and leaderboard limits are invalid.";
            return false;
        }
        var values = new[]
        {
            MinimumResourceValue, MinimumTaskPoints, MinimumDeliveredOrders,
            ResourceItemsGoal, ResourceValueGoal, ReliableTaskPointsGoal,
            DeliveredOrdersGoal
        };
        if (values.Any(value => value is < 1 or > TeamEconomyLimits.WebSafeInteger) ||
            string.IsNullOrWhiteSpace(GoalTemplateVersion) ||
            GoalTemplateVersion.Trim().Length > 64 ||
            GoalTemplateVersion.Any(char.IsControl))
        {
            error = "Team goal and leaderboard thresholds must be positive web-safe integers with a valid template version.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}

public static class TeamEconomyLimits
{
    public const long WebSafeInteger = 9_007_199_254_740_991;
    public const int MaximumPageSize = 100;
}

[JsonConverter(typeof(JsonStringEnumConverter<TeamEconomyStatus>))]
public enum TeamEconomyStatus
{
    Active,
    Dissolved
}

[JsonConverter(typeof(JsonStringEnumConverter<TeamEconomyMetric>))]
public enum TeamEconomyMetric
{
    ResourceValue,
    TaskPoints,
    DeliveredOrders
}

[JsonConverter(typeof(JsonStringEnumConverter<TeamEconomyGoalKind>))]
public enum TeamEconomyGoalKind
{
    ResourceItems,
    ResourceValue,
    TaskPoints,
    DeliveredOrders
}

public sealed record TeamEconomyGoalDefinition(
    TeamEconomyGoalKind Kind,
    string DisplayName,
    long Target,
    string Unit);

public sealed record TeamEconomyGoalSnapshot(
    Guid SnapshotId,
    Guid TeamId,
    string ServerId,
    Guid SeasonId,
    string TemplateVersion,
    IReadOnlyList<TeamEconomyGoalDefinition> Goals,
    string SnapshotHash,
    DateTimeOffset CreatedAt);

public sealed record TeamEconomyGoalProgress(
    TeamEconomyGoalKind Kind,
    string DisplayName,
    long Progress,
    long Target,
    string Unit,
    bool Achieved,
    DateTimeOffset? ReachedAt);

public sealed record TeamEconomyContribution(
    long ResourceItems,
    long ResourceValue,
    long TaskPoints,
    long DeliveredOrders,
    long ActualCurrencySpent);

public sealed record TeamEconomyProjectionHealth(
    bool Ready,
    bool Stale,
    DateTimeOffset? CutoffAt,
    DateTimeOffset? UpdatedAt,
    string? SourceHash,
    string? SnapshotHash,
    string? LastErrorCode);

public sealed record TeamEconomyTransferCandidate(
    string MemberHandle,
    string Label,
    DateTimeOffset JoinedAt);

public sealed record TeamEconomyDashboard(
    bool Enabled,
    bool HasTeam,
    Guid? TeamId,
    string? Name,
    TeamEconomyStatus? Status,
    bool IsOwner,
    int MemberCount,
    DateTimeOffset? JoinedAt,
    IReadOnlyList<TeamEconomyGoalProgress> Goals,
    TeamEconomyContribution? TeamContribution,
    TeamEconomyContribution? MyContribution,
    IReadOnlyList<TeamEconomyTransferCandidate> TransferCandidates,
    TeamEconomyProjectionHealth Projection,
    string PolicyNotice);

public sealed record TeamEconomyMutationResponse(
    Guid TeamId,
    string Name,
    TeamEconomyStatus Status,
    int MemberCount,
    bool IsOwner,
    bool Replayed,
    DateTimeOffset UpdatedAt);

public sealed record TeamEconomyInviteResponse(
    Guid TeamId,
    Guid InviteId,
    string? Token,
    bool TokenShown,
    DateTimeOffset ExpiresAt,
    int MaximumUses,
    int RemainingUses,
    bool Replayed);

public sealed record TeamEconomyLeaderboardEntry(
    int Rank,
    Guid TeamId,
    string TeamName,
    int MemberCount,
    long Value,
    DateTimeOffset ReachedAt,
    bool IsMyTeam);

public sealed record TeamEconomyLeaderboardPage(
    TeamEconomyMetric Metric,
    DateTimeOffset CutoffAt,
    int Offset,
    int Limit,
    int Total,
    string? NextCursor,
    IReadOnlyList<TeamEconomyLeaderboardEntry> Items,
    string TieBreakPolicy,
    string EligibilityPolicy,
    TeamEconomyProjectionHealth Projection);

public sealed record TeamEconomyProjectionResult(
    string ServerId,
    Guid SeasonId,
    DateTimeOffset CutoffAt,
    int SourceEvents,
    int ProjectedEvents,
    bool Changed,
    string SourceHash,
    string SnapshotHash);

public sealed record TeamEconomyScope(string ServerId, Guid SeasonId);

public sealed class TeamEconomyException : Exception
{
    public TeamEconomyException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}

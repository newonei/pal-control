using System.Text.Json.Serialization;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Content;

[JsonConverter(typeof(JsonStringEnumConverter<ReliableEconomyEventSource>))]
public enum ReliableEconomyEventSource
{
    ResourceSettlement,
    ShopOrderDelivery
}

public sealed record ReliableTaskItemAmount(string ItemId, long Quantity);

public sealed record ReliableTaskCurrencyAmount(ExtractionCurrency Currency, long Amount);

/// <summary>
/// An immutable, server-authored fact used by the task runtime. There is no
/// public/client supplied event ingestion endpoint. EventId is derived from
/// the durable settlement run or delivered order identity.
/// </summary>
public sealed record ReliableEconomyEvent(
    string EventId,
    ReliableEconomyEventSource Source,
    Guid AccountId,
    Guid SeasonId,
    string ServerId,
    string? ZoneId,
    IReadOnlyList<ReliableTaskItemAmount> Items,
    long ResourceValue,
    IReadOnlyList<ReliableTaskCurrencyAmount> CurrencySpent,
    Guid? ContentVersionId,
    string? ContentHash,
    DateTimeOffset OccurredAt);

public sealed record ReliableTaskSet(
    Guid TaskSetId,
    Guid AccountId,
    Guid SeasonId,
    string ServerId,
    ContentTaskCadence Cadence,
    string PeriodKey,
    Guid ContentVersionId,
    string ContentHash,
    string RulesVersion,
    string RotationSeed,
    IReadOnlyList<string> SelectedTaskKeys,
    DateTimeOffset CreatedAt);

public sealed record ReliableTaskInstance(
    Guid InstanceId,
    Guid TaskSetId,
    Guid AccountId,
    Guid SeasonId,
    string ServerId,
    ContentTaskCadence Cadence,
    string PeriodKey,
    Guid ContentVersionId,
    string ContentHash,
    string RulesVersion,
    string RotationSeed,
    ContentTaskDefinition Definition,
    long Progress,
    DateTimeOffset? CompletedAt,
    Guid? CurrencyRewardLedgerEntryId,
    Guid? RankingRewardEntryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool Completed => CompletedAt is not null;

    public bool RewardGranted =>
        (Definition.Reward.Amount == 0 || CurrencyRewardLedgerEntryId is not null) &&
        (Definition.Reward.RankingPoints == 0 || RankingRewardEntryId is not null);
}

public sealed record ReliableTaskRankingRewardEntry(
    Guid EntryId,
    Guid InstanceId,
    Guid AccountId,
    Guid SeasonId,
    int Points,
    int BalanceAfter,
    DateTimeOffset CreatedAt);

public sealed record ReliableTaskSnapshot(
    Guid AccountId,
    Guid SeasonId,
    string ServerId,
    int RankingPoints,
    IReadOnlyList<ReliableTaskInstance> Tasks);

public sealed record ReliableTaskEventResult(
    bool Applied,
    bool Replayed,
    IReadOnlyList<ReliableTaskInstance> Tasks);

public sealed record ReliableTaskRewardScope(Guid AccountId, Guid SeasonId);

public sealed class ReliableTaskException : Exception
{
    public ReliableTaskException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

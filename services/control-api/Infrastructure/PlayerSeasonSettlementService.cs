using System.Security.Cryptography;
using System.Text;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed record PlayerSeasonSettlementResponse(
    bool Available,
    string Status,
    PlayerSeasonSettlement? Settlement);

public sealed record PlayerSeasonSettlement(
    Guid SeasonId,
    string SeasonCode,
    DateTimeOffset CutoffAt,
    DateTimeOffset FrozenAt,
    string RewardState,
    PlayerSeasonSettlementRules Rules,
    PlayerSeasonParticipation Participation,
    PlayerSeasonVoucherExpiry VoucherExpiry,
    IReadOnlyList<PlayerPermanentReward> PermanentRewards);

public sealed record PlayerSeasonSettlementRules(
    string RulesVersion,
    int LateSettlementGraceMinutes,
    int MinimumSettledExchanges,
    long MinimumResourceValue,
    int MinimumTaskPoints,
    string ResourceTieBreakRule,
    string TaskTieBreakRule);

public sealed record PlayerSeasonParticipation(
    bool Participating,
    string ReasonCode,
    PlayerSeasonBoardResult Resource,
    PlayerSeasonBoardResult Task,
    IReadOnlyList<SeasonLeaderboardResourceAggregate> Items,
    IReadOnlyList<SeasonLeaderboardCategoryAggregate> Categories);

public sealed record PlayerSeasonBoardResult(
    string Board,
    bool Eligible,
    int? Rank,
    string ReasonCode,
    int SettledExchanges,
    long ResourceQuantity,
    long ResourceValue,
    int TaskPoints);

public sealed record PlayerSeasonVoucherExpiry(
    string JobState,
    string ItemState,
    long ScheduledAmount,
    long ExpiredAmount,
    bool LedgerRecorded,
    DateTimeOffset? CompletedAt);

public sealed record PlayerPermanentReward(
    string Source,
    string Board,
    int? Rank,
    long MarketCoin,
    string RewardKey,
    string DecisionState,
    string DeliveryState,
    string? ReasonCode,
    bool LedgerRecorded,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Builds the player-only end-of-week projection. Ranking values come from the
/// immutable snapshot, while voucher expiry and permanent reward completion
/// are reconciled against the durable job store and authoritative wallet
/// ledger on every read.
/// </summary>
public sealed class PlayerSeasonSettlementService
{
    private readonly IExtractionRepository _repository;
    private readonly SeasonLeaderboardService _leaderboards;
    private readonly SeasonSettlementJobStore _jobs;

    public PlayerSeasonSettlementService(
        IExtractionRepository repository,
        SeasonLeaderboardService leaderboards,
        SeasonSettlementJobStore jobs)
    {
        _repository = repository;
        _leaderboards = leaderboards;
        _jobs = jobs;
    }

    public async Task<PlayerSeasonSettlementResponse> GetLatestAsync(
        string serverId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var record = await _leaderboards.GetLatestAsync(serverId, cancellationToken);
        return record is null
            ? new PlayerSeasonSettlementResponse(false, "not-frozen", null)
            : new PlayerSeasonSettlementResponse(
                true,
                "frozen",
                await BuildAsync(record.Snapshot, accountId, cancellationToken));
    }

    public async Task<PlayerSeasonSettlementResponse?> GetAsync(
        Guid seasonId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var record = await _leaderboards.GetAsync(seasonId, cancellationToken);
        return record is null
            ? null
            : new PlayerSeasonSettlementResponse(
                true,
                "frozen",
                await BuildAsync(record.Snapshot, accountId, cancellationToken));
    }

    private async Task<PlayerSeasonSettlement> BuildAsync(
        SeasonLeaderboardSnapshot snapshot,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var entry = snapshot.Entries.SingleOrDefault(candidate => candidate.AccountId == accountId);
        var participation = BuildParticipation(snapshot.Rules, entry);
        var accountJobs = await _jobs.ListForAccountAsync(
            snapshot.SeasonId,
            accountId,
            cancellationToken);
        var expiryJob = await _jobs.FindExpiryAsync(snapshot.SeasonId, cancellationToken);
        var expiry = await BuildVoucherExpiryAsync(
            expiryJob,
            accountId,
            cancellationToken);
        var rewards = await BuildRewardsAsync(
            snapshot,
            accountId,
            accountJobs,
            cancellationToken);

        return new PlayerSeasonSettlement(
            snapshot.SeasonId,
            snapshot.SeasonCode,
            snapshot.CutoffAt,
            snapshot.FrozenAt,
            snapshot.RewardState,
            new PlayerSeasonSettlementRules(
                snapshot.Rules.RulesVersion,
                snapshot.Rules.LateSettlementGraceMinutes,
                snapshot.Rules.MinimumSettledExchanges,
                snapshot.Rules.MinimumResourceValue,
                snapshot.Rules.MinimumTaskPoints,
                snapshot.Rules.ResourceTieBreakRule,
                snapshot.Rules.TaskTieBreakRule),
            participation,
            expiry,
            rewards);
    }

    private static PlayerSeasonParticipation BuildParticipation(
        SeasonLeaderboardRules rules,
        SeasonLeaderboardEntry? entry)
    {
        if (entry is null)
        {
            return new PlayerSeasonParticipation(
                false,
                "no-frozen-contribution",
                EmptyBoard("resource-value", "no-frozen-contribution"),
                EmptyBoard("task-points", "no-frozen-contribution"),
                [],
                []);
        }

        var participationReason = entry.RankingExclusionCode ?? "frozen-contribution-recorded";
        var resourceReason = entry.RankingExclusionCode
            ?? (entry.SettledExchanges < rules.MinimumSettledExchanges
                ? "below-minimum-settled-exchanges"
                : entry.ResourceValue < rules.MinimumResourceValue
                    ? "below-minimum-resource-value"
                    : "eligible");
        var taskReason = entry.RankingExclusionCode
            ?? (entry.TaskPoints < rules.MinimumTaskPoints
                ? "below-minimum-task-points"
                : "eligible");
        return new PlayerSeasonParticipation(
            true,
            participationReason,
            new PlayerSeasonBoardResult(
                "resource-value",
                entry.ResourceEligible,
                entry.ResourceRank,
                resourceReason,
                entry.SettledExchanges,
                entry.ResourceQuantity,
                entry.ResourceValue,
                entry.TaskPoints),
            new PlayerSeasonBoardResult(
                "task-points",
                entry.TaskEligible,
                entry.TaskRank,
                taskReason,
                entry.SettledExchanges,
                entry.ResourceQuantity,
                entry.ResourceValue,
                entry.TaskPoints),
            entry.Items,
            entry.Categories);
    }

    private static PlayerSeasonBoardResult EmptyBoard(string board, string reasonCode) => new(
        board,
        false,
        null,
        reasonCode,
        0,
        0,
        0,
        0);

    private async Task<PlayerSeasonVoucherExpiry> BuildVoucherExpiryAsync(
        SeasonSettlementJob? job,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (job is null)
        {
            return new PlayerSeasonVoucherExpiry(
                "not-prepared",
                "not-prepared",
                0,
                0,
                false,
                null);
        }
        if (job.Kind != SeasonSettlementJobKind.SeasonVoucherExpiry)
        {
            throw new InvalidDataException("The voucher expiry index references a non-expiry job.");
        }
        var item = job.Items.SingleOrDefault(candidate => candidate.AccountId == accountId);
        if (item is null)
        {
            return new PlayerSeasonVoucherExpiry(
                JobState(job.State),
                "not-applicable",
                0,
                0,
                false,
                job.CompletedAt);
        }
        if (item.Currency != ExtractionCurrency.SeasonVoucher ||
            item.TargetSeasonId != job.SourceSeasonId ||
            item.Delta >= 0)
        {
            throw new InvalidDataException("The player voucher expiry item is invalid.");
        }
        var ledger = await FindAndValidateLedgerAsync(item, cancellationToken);
        return new PlayerSeasonVoucherExpiry(
            JobState(job.State),
            ledger is null ? "pending" : "expired",
            checked(-item.Delta),
            ledger is null ? 0 : checked(-ledger.Delta),
            ledger is not null,
            ledger?.CreatedAt ?? job.CompletedAt);
    }

    private async Task<IReadOnlyList<PlayerPermanentReward>> BuildRewardsAsync(
        SeasonLeaderboardSnapshot snapshot,
        Guid accountId,
        IReadOnlyList<SeasonSettlementJob> accountJobs,
        CancellationToken cancellationToken)
    {
        var rewards = new List<PlayerPermanentReward>();
        var standardJob = snapshot.RewardJobId is Guid standardJobId
            ? await _jobs.GetAsync(standardJobId, cancellationToken)
            : null;
        if (standardJob is null && snapshot.RewardState == "completed")
        {
            throw new InvalidDataException(
                "A completed frozen reward decision has no durable settlement job.");
        }
        foreach (var decision in (snapshot.RewardDecisions ?? [])
                     .Where(candidate => candidate.AccountId == accountId)
                     .OrderBy(candidate => candidate.Board, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Rank))
        {
            if (decision.State == "cancelled")
            {
                rewards.Add(new PlayerPermanentReward(
                    "standard",
                    decision.Board,
                    decision.Rank,
                    decision.MarketCoin,
                    decision.RewardKey,
                    "cancelled",
                    "cancelled",
                    decision.ReasonCode,
                    false,
                    null));
                continue;
            }
            if (decision.State != "granted")
            {
                throw new InvalidDataException(
                    "The frozen leaderboard contains an unknown player reward decision.");
            }
            var delivery = standardJob is null
                ? (State: "pending", Ledger: (WalletLedgerEntry?)null)
                : await RewardDeliveryAsync(
                    FindStandardRewardItem(snapshot.SeasonId, decision, standardJob),
                    cancellationToken);
            rewards.Add(new PlayerPermanentReward(
                "standard",
                decision.Board,
                decision.Rank,
                decision.MarketCoin,
                decision.RewardKey,
                "granted",
                delivery.State,
                null,
                delivery.Ledger is not null,
                delivery.Ledger?.CreatedAt ?? standardJob?.CompletedAt));
        }

        foreach (var job in accountJobs
                     .Where(candidate => candidate.Kind == SeasonSettlementJobKind.Reward &&
                         candidate.JobId != snapshot.RewardJobId)
                     .OrderBy(candidate => candidate.PreparedAt)
                     .ThenBy(candidate => candidate.JobId))
        {
            foreach (var item in job.Items
                         .Where(candidate => candidate.Currency == ExtractionCurrency.MarketCoin &&
                             candidate.TargetSeasonId is null)
                         .OrderBy(candidate => candidate.ItemKey, StringComparer.Ordinal))
            {
                if (item.Delta <= 0)
                {
                    throw new InvalidDataException(
                        "A permanent player reward has a non-positive amount.");
                }
                var delivery = await RewardDeliveryAsync(item, cancellationToken);
                rewards.Add(new PlayerPermanentReward(
                    "supplement",
                    "manual",
                    null,
                    item.Delta,
                    job.JobKey,
                    "granted",
                    delivery.State,
                    null,
                    delivery.Ledger is not null,
                    delivery.Ledger?.CreatedAt ?? job.CompletedAt));
            }
        }
        return rewards;
    }

    private static SeasonSettlementJobItem FindStandardRewardItem(
        Guid seasonId,
        SeasonLeaderboardRewardDecision decision,
        SeasonSettlementJob job)
    {
        if (job.Kind != SeasonSettlementJobKind.Reward || job.SourceSeasonId != seasonId)
        {
            throw new InvalidDataException(
                "The frozen reward decision references an invalid settlement job.");
        }
        var rewardHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(decision.RewardKey)))[..24];
        var reference = $"{seasonId:N}:{decision.AccountId:N}:{rewardHash}";
        var item = job.Items.SingleOrDefault(candidate =>
            candidate.AccountId == decision.AccountId &&
            candidate.Currency == ExtractionCurrency.MarketCoin &&
            candidate.TargetSeasonId is null &&
            string.Equals(candidate.ReferenceType, "season_reward", StringComparison.Ordinal) &&
            string.Equals(candidate.ReferenceId, reference, StringComparison.Ordinal));
        if (item is null || item.Delta != decision.MarketCoin)
        {
            throw new InvalidDataException(
                "A granted frozen reward decision does not match its durable job item.");
        }
        return item;
    }

    private async Task<(string State, WalletLedgerEntry? Ledger)> RewardDeliveryAsync(
        SeasonSettlementJobItem item,
        CancellationToken cancellationToken)
    {
        if (item.Currency != ExtractionCurrency.MarketCoin ||
            item.TargetSeasonId is not null ||
            item.Delta <= 0)
        {
            throw new InvalidDataException("The permanent reward item is invalid.");
        }
        var ledger = await FindAndValidateLedgerAsync(item, cancellationToken);
        return ledger is null ? ("pending", null) : ("paid", ledger);
    }

    private async Task<WalletLedgerEntry?> FindAndValidateLedgerAsync(
        SeasonSettlementJobItem item,
        CancellationToken cancellationToken)
    {
        var ledger = await _repository.FindLedgerEntryByReferenceAsync(
            item.AccountId,
            item.Currency,
            item.TargetSeasonId,
            item.ReferenceType,
            item.ReferenceId,
            cancellationToken);
        if (ledger is null)
        {
            if (item.State == SeasonSettlementItemState.Applied || item.LedgerEntryId is not null)
            {
                throw new InvalidDataException(
                    "A completed season settlement item has no authoritative ledger entry.");
            }
            return null;
        }
        if (ledger.EntryId != item.LedgerEntryId && item.LedgerEntryId is not null ||
            ledger.AccountId != item.AccountId ||
            ledger.Currency != item.Currency ||
            ledger.SeasonId != item.TargetSeasonId ||
            ledger.Delta != item.Delta ||
            !string.Equals(ledger.ReferenceType, item.ReferenceType, StringComparison.Ordinal) ||
            !string.Equals(ledger.ReferenceId, item.ReferenceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The season settlement job and authoritative ledger evidence conflict.");
        }
        return ledger;
    }

    private static string JobState(SeasonSettlementJobState state) => state switch
    {
        SeasonSettlementJobState.Prepared => "prepared",
        SeasonSettlementJobState.Running => "running",
        SeasonSettlementJobState.Completed => "completed",
        _ => throw new InvalidDataException("Unknown season settlement job state.")
    };
}

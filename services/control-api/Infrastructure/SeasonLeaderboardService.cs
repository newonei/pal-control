using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed record SeasonLeaderboardManualRewardResult(
    SeasonSettlementJob Job,
    Guid LedgerEntryId);

public sealed class SeasonLeaderboardService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IExtractionRepository _repository;
    private readonly PlayerIdentitySecurityStore _identitySecurity;
    private readonly SeasonLeaderboardStore _store;
    private readonly SeasonSettlementJobService _jobs;
    private readonly TimeProvider _timeProvider;

    public SeasonLeaderboardService(
        IExtractionRepository repository,
        PlayerIdentitySecurityStore identitySecurity,
        SeasonLeaderboardStore store,
        SeasonSettlementJobService jobs,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _identitySecurity = identitySecurity;
        _store = store;
        _jobs = jobs;
        _timeProvider = timeProvider;
    }

    public Task<SeasonLeaderboardRecord?> GetAsync(
        Guid seasonId,
        CancellationToken cancellationToken) =>
        _store.GetRecordAsync(seasonId, cancellationToken);

    public Task<SeasonLeaderboardRecord?> GetLatestAsync(
        string serverId,
        CancellationToken cancellationToken) =>
        _store.GetLatestRecordAsync(serverId, cancellationToken);

    public async Task<SeasonLeaderboardRecord> FreezeAsync(
        Guid seasonId,
        string actor,
        string correlationId,
        ExtractionOperationGateState gate,
        int activeOperations,
        CancellationToken cancellationToken)
    {
        var existing = await _store.GetRecordAsync(seasonId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }
        EnsureMaintenance(gate, activeOperations);
        ValidateText(actor, 1, 256, nameof(actor));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            existing = await _store.GetRecordAsync(seasonId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
            var season = await _repository.GetSeasonAsync(seasonId, cancellationToken)
                ?? throw new KeyNotFoundException($"Extraction season '{seasonId}' does not exist.");
            if (season.State is not (
                    ExtractionSeasonState.Closed or ExtractionSeasonState.Archived))
            {
                throw new InvalidOperationException(
                    "Only a closed or archived season can be frozen for ranking.");
            }
            var rules = SeasonLeaderboardPolicy.Current;
            var cutoffAt = season.EndsAt.AddMinutes(rules.LateSettlementGraceMinutes);
            var frozenAt = _timeProvider.GetUtcNow();
            if (frozenAt < cutoffAt)
            {
                throw new InvalidOperationException(
                    $"The leaderboard cutoff '{cutoffAt:O}' has not been reached.");
            }

            var source = await _store.ReadSourceDataAsync(
                seasonId,
                cutoffAt,
                cancellationToken);
            var accounts = (await _repository.ListAccountsAsync(cancellationToken))
                .ToDictionary(account => account.AccountId);
            var exclusions = (await _store.ListExclusionsAsync(
                    seasonId,
                    activeOnly: true,
                    cancellationToken))
                .ToDictionary(exclusion => exclusion.AccountId);
            var entries = BuildEntries(source.Evidence, accounts, exclusions, rules);
            var globalItems = entries
                .Where(entry => entry.RankingExclusionCode is null)
                .SelectMany(entry => entry.Items)
                .GroupBy(
                    item => (item.ItemId.ToLowerInvariant(), item.Category.ToLowerInvariant()),
                    item => item)
                .Select(group => new SeasonLeaderboardResourceAggregate(
                    group.First().ItemId,
                    group.First().Category,
                    checked(group.Sum(item => item.Quantity)),
                    checked(group.Sum(item => item.Value))))
                .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var globalCategories = globalItems
                .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SeasonLeaderboardCategoryAggregate(
                    group.Key,
                    checked(group.Sum(item => item.Quantity)),
                    checked(group.Sum(item => item.Value))))
                .OrderBy(category => category.Category, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var snapshotId = SeasonLeaderboardHash.DeterministicGuid(
                $"{SeasonLeaderboardPolicy.FrameworkVersion}|{seasonId:D}|{rules.RulesVersion}");
            var rulesHash = SeasonLeaderboardHash.Of(rules);
            var sourceHash = SeasonLeaderboardHash.Of(source.Evidence);
            var snapshot = new SeasonLeaderboardSnapshot(
                snapshotId,
                seasonId,
                season.ServerId,
                season.Code,
                SeasonLeaderboardPolicy.FrameworkVersion,
                rules,
                rulesHash,
                cutoffAt,
                sourceHash,
                SnapshotHash: string.Empty,
                frozenAt,
                actor.Trim(),
                entries,
                globalItems,
                globalCategories,
                source.Evidence.LateSettlementIdsObservedAtFreeze.Count,
                source.Evidence.LateTaskPointEntryIdsObservedAtFreeze.Count,
                RewardDecisions: []);
            snapshot = snapshot with
            {
                SnapshotHash = SeasonLeaderboardHash.Snapshot(snapshot)
            };
            return await _store.SaveFrozenAsync(
                snapshot,
                source.Evidence,
                correlationId,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonLeaderboardExclusion> SetExclusionAsync(
        Guid seasonId,
        Guid accountId,
        bool active,
        string reason,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ = await _repository.GetSeasonAsync(seasonId, cancellationToken)
                ?? throw new KeyNotFoundException($"Extraction season '{seasonId}' does not exist.");
            _ = await _repository.GetAccountAsync(accountId, cancellationToken)
                ?? throw new KeyNotFoundException($"Extraction account '{accountId}' does not exist.");
            var snapshot = await _store.GetSnapshotAsync(seasonId, cancellationToken);
            if (snapshot?.RewardJobId is not null)
            {
                throw new InvalidOperationException(
                    "Reward exclusions are immutable after the standard reward job is prepared.");
            }
            return await _store.SetExclusionAsync(
                seasonId,
                accountId,
                active,
                reason,
                actor,
                correlationId,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonSettlementJob> PrepareRewardsAsync(
        Guid seasonId,
        string actor,
        string correlationId,
        ExtractionOperationGateState gate,
        int activeOperations,
        CancellationToken cancellationToken)
    {
        EnsureMaintenance(gate, activeOperations);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await _store.GetRecordAsync(seasonId, cancellationToken)
                ?? throw new KeyNotFoundException(
                    $"Season leaderboard snapshot '{seasonId}' does not exist.");
            if (record.Snapshot.RewardJobId is Guid existingJobId)
            {
                return await RecoverPreparedRewardJobAsync(
                    record,
                    existingJobId,
                    actor,
                    gate,
                    activeOperations,
                    cancellationToken);
            }
            var accounts = (await _repository.ListAccountsAsync(cancellationToken))
                .ToDictionary(account => account.AccountId);
            var currentExclusions = (await _store.ListExclusionsAsync(
                    seasonId,
                    activeOnly: true,
                    cancellationToken))
                .Select(exclusion => exclusion.AccountId)
                .ToHashSet();
            var grants = new List<SeasonRewardGrant>();
            var decisions = new List<SeasonLeaderboardRewardDecision>();
            foreach (var tier in record.Snapshot.Rules.RewardTiers
                         .OrderBy(tier => tier.Board, StringComparer.Ordinal)
                         .ThenBy(tier => tier.Rank))
            {
                var entry = tier.Board switch
                {
                    "resource-value" => record.Snapshot.Entries.SingleOrDefault(
                        candidate => candidate.ResourceRank == tier.Rank),
                    "task-points" => record.Snapshot.Entries.SingleOrDefault(
                        candidate => candidate.TaskRank == tier.Rank),
                    _ => throw new InvalidDataException(
                        $"Unknown leaderboard reward board '{tier.Board}'.")
                };
                if (entry is null)
                {
                    continue;
                }
                if (!accounts.TryGetValue(entry.AccountId, out var account))
                {
                    throw new InvalidDataException(
                        $"Leaderboard account '{entry.AccountId}' no longer exists.");
                }
                string? cancellationReason = null;
                if (_identitySecurity.IsBanned(account.ExternalUserId))
                {
                    cancellationReason = "identity-banned-before-reward";
                }
                else if (currentExclusions.Contains(entry.AccountId))
                {
                    cancellationReason = "manual-reward-cancellation";
                }
                var rewardKey =
                    $"leaderboard:{record.Snapshot.SnapshotId:N}:{tier.RewardKey}";
                decisions.Add(new SeasonLeaderboardRewardDecision(
                    entry.AccountId,
                    tier.Board,
                    tier.Rank,
                    tier.MarketCoin,
                    rewardKey,
                    cancellationReason is null ? "granted" : "cancelled",
                    cancellationReason));
                if (cancellationReason is null)
                {
                    grants.Add(new SeasonRewardGrant(
                        entry.AccountId,
                        ExtractionCurrency.MarketCoin,
                        TargetSeasonId: null,
                        tier.MarketCoin,
                        rewardKey));
                }
            }
            var rewardBatchKey = RewardBatchKey(record.Snapshot);
            var expectedJobId = RewardJobId(record.Snapshot, rewardBatchKey);
            _ = await _store.AttachRewardJobAsync(
                seasonId,
                expectedJobId,
                decisions,
                actor,
                correlationId,
                cancellationToken);
            var job = await _jobs.PrepareRewardAsync(
                seasonId,
                record.Snapshot.Rules.RulesVersion,
                rewardBatchKey,
                grants,
                actor,
                gate,
                activeOperations,
                cancellationToken);
            if (job.JobId != expectedJobId)
            {
                throw new InvalidDataException(
                    "The season reward service returned a non-deterministic leaderboard job id.");
            }
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SeasonSettlementJob> RunRewardsAsync(
        Guid seasonId,
        string actor,
        string correlationId,
        ExtractionOperationGateState gate,
        int activeOperations,
        CancellationToken cancellationToken)
    {
        EnsureMaintenance(gate, activeOperations);
        var snapshot = await _store.GetSnapshotAsync(seasonId, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Season leaderboard snapshot '{seasonId}' does not exist.");
        var jobId = snapshot.RewardJobId
            ?? throw new InvalidOperationException(
                "The standard leaderboard reward job has not been prepared.");
        if (await _jobs.GetAsync(jobId, cancellationToken) is null)
        {
            _ = await PrepareRewardsAsync(
                seasonId,
                actor,
                correlationId,
                gate,
                activeOperations,
                cancellationToken);
        }
        var job = await _jobs.RunAsync(jobId, gate, activeOperations, cancellationToken);
        if (job.State == SeasonSettlementJobState.Completed)
        {
            _ = await _store.MarkRewardCompletedAsync(
                seasonId,
                jobId,
                actor,
                correlationId,
                cancellationToken);
        }
        return job;
    }

    public async Task<SeasonLeaderboardManualRewardResult> GrantManualRewardAsync(
        Guid seasonId,
        Guid accountId,
        long amount,
        string manualKey,
        string reason,
        string actor,
        string correlationId,
        ExtractionOperationGateState gate,
        int activeOperations,
        CancellationToken cancellationToken)
    {
        EnsureMaintenance(gate, activeOperations);
        if (amount is <= 0 or > 9_007_199_254_740_991)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "A manual reward must be a positive web-safe integer.");
        }
        ValidateText(manualKey, 3, 32, nameof(manualKey));
        ValidateText(reason, 3, 500, nameof(reason));
        var record = await _store.GetRecordAsync(seasonId, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Season leaderboard snapshot '{seasonId}' does not exist.");
        if (!record.Snapshot.Entries.Any(entry => entry.AccountId == accountId))
        {
            throw new ArgumentException(
                "Manual leaderboard supplements require an account present in the frozen snapshot.",
                nameof(accountId));
        }
        var shortKey = SeasonLeaderboardHash.Of(manualKey.Trim())[..16];
        var job = await _jobs.PrepareRewardAsync(
            seasonId,
            record.Snapshot.Rules.RulesVersion,
            $"leaderboard-manual-{shortKey}",
            [new SeasonRewardGrant(
                accountId,
                ExtractionCurrency.MarketCoin,
                TargetSeasonId: null,
                amount,
                $"manual-{shortKey}")],
            actor,
            gate,
            activeOperations,
            cancellationToken);
        job = await _jobs.RunAsync(job.JobId, gate, activeOperations, cancellationToken);
        var item = job.Items.Single();
        if (job.State != SeasonSettlementJobState.Completed || item.LedgerEntryId is not Guid ledgerId)
        {
            throw new InvalidOperationException(
                "The manual leaderboard supplement did not produce durable ledger evidence.");
        }
        await _store.AppendManualRewardAuditAsync(
            seasonId,
            record.Snapshot.SnapshotId,
            job.JobId,
            accountId,
            amount,
            manualKey.Trim(),
            reason,
            actor,
            correlationId,
            cancellationToken);
        return new SeasonLeaderboardManualRewardResult(job, ledgerId);
    }

    private IReadOnlyList<SeasonLeaderboardEntry> BuildEntries(
        SeasonLeaderboardEvidence evidence,
        IReadOnlyDictionary<Guid, ExtractionAccount> accounts,
        IReadOnlyDictionary<Guid, SeasonLeaderboardExclusion> exclusions,
        SeasonLeaderboardRules rules)
    {
        var accumulators = new Dictionary<Guid, EntryAccumulator>();
        foreach (var settlement in evidence.Settlements)
        {
            var accumulator = GetAccumulator(accumulators, settlement.AccountId);
            accumulator.SettledExchanges = checked(accumulator.SettledExchanges + 1);
            accumulator.ResourceQuantity = checked(accumulator.ResourceQuantity + settlement.ItemCount);
            accumulator.ResourceValue = checked(accumulator.ResourceValue + settlement.TotalValue);
            accumulator.FirstSettledAt = Min(accumulator.FirstSettledAt, settlement.SettledAt);
            foreach (var line in settlement.Items)
            {
                var key = $"{line.ItemId.ToLowerInvariant()}\n{line.Category.ToLowerInvariant()}";
                if (!accumulator.Items.TryGetValue(key, out var aggregate))
                {
                    aggregate = new SeasonLeaderboardResourceAggregate(
                        line.ItemId,
                        line.Category,
                        0,
                        0);
                }
                accumulator.Items[key] = aggregate with
                {
                    Quantity = checked(aggregate.Quantity + line.Quantity),
                    Value = checked(aggregate.Value + line.TotalValue)
                };
            }
        }
        foreach (var taskPoint in evidence.TaskPoints)
        {
            var accumulator = GetAccumulator(accumulators, taskPoint.AccountId);
            accumulator.TaskPoints = checked(accumulator.TaskPoints + taskPoint.Points);
            accumulator.FirstTaskPointAt = Min(
                accumulator.FirstTaskPointAt,
                taskPoint.CreatedAt);
        }

        var entries = new Dictionary<Guid, SeasonLeaderboardEntry>();
        foreach (var accumulator in accumulators.Values.OrderBy(
                     value => value.AccountId.ToString("N"),
                     StringComparer.Ordinal))
        {
            if (!accounts.TryGetValue(accumulator.AccountId, out var account))
            {
                throw new InvalidDataException(
                    $"Leaderboard source references missing account '{accumulator.AccountId}'.");
            }
            var banned = _identitySecurity.IsBanned(account.ExternalUserId);
            var manuallyExcluded = exclusions.ContainsKey(account.AccountId);
            var exclusionCode = banned
                ? "identity-banned-at-freeze"
                : manuallyExcluded ? "manual-exclusion-at-freeze" : null;
            var items = accumulator.Items.Values
                .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var categories = items
                .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SeasonLeaderboardCategoryAggregate(
                    group.Key,
                    checked(group.Sum(item => item.Quantity)),
                    checked(group.Sum(item => item.Value))))
                .OrderBy(category => category.Category, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            entries[account.AccountId] = new SeasonLeaderboardEntry(
                account.AccountId,
                PlayerIdentitySecurityStore.FingerprintSubject(account.AccountId.ToString("N")),
                banned,
                manuallyExcluded,
                exclusionCode,
                ResourceEligible: exclusionCode is null &&
                    accumulator.SettledExchanges >= rules.MinimumSettledExchanges &&
                    accumulator.ResourceValue >= rules.MinimumResourceValue,
                ResourceRank: null,
                TaskEligible: exclusionCode is null &&
                    accumulator.TaskPoints >= rules.MinimumTaskPoints,
                TaskRank: null,
                accumulator.SettledExchanges,
                accumulator.ResourceQuantity,
                accumulator.ResourceValue,
                accumulator.TaskPoints,
                accumulator.FirstSettledAt,
                accumulator.FirstTaskPointAt,
                items,
                categories);
        }

        var resourceRank = 0;
        foreach (var candidate in entries.Values
                     .Where(entry => entry.ResourceEligible)
                     .OrderByDescending(entry => entry.ResourceValue)
                     .ThenByDescending(entry => entry.ResourceQuantity)
                     .ThenBy(entry => entry.FirstSettledAt)
                     .ThenBy(
                         entry => entry.AccountId.ToString("N"),
                         StringComparer.Ordinal))
        {
            entries[candidate.AccountId] = candidate with
            {
                ResourceRank = checked(++resourceRank)
            };
        }
        var taskRank = 0;
        foreach (var candidate in entries.Values
                     .Where(entry => entry.TaskEligible)
                     .OrderByDescending(entry => entry.TaskPoints)
                     .ThenBy(entry => entry.FirstTaskPointAt)
                     .ThenBy(
                         entry => entry.AccountId.ToString("N"),
                         StringComparer.Ordinal))
        {
            entries[candidate.AccountId] = candidate with
            {
                TaskRank = checked(++taskRank)
            };
        }
        return entries.Values
            .OrderBy(entry => entry.ResourceRank ?? int.MaxValue)
            .ThenBy(entry => entry.TaskRank ?? int.MaxValue)
            .ThenBy(
                entry => entry.AccountId.ToString("N"),
                StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<SeasonSettlementJob> RecoverPreparedRewardJobAsync(
        SeasonLeaderboardRecord record,
        Guid expectedJobId,
        string actor,
        ExtractionOperationGateState gate,
        int activeOperations,
        CancellationToken cancellationToken)
    {
        var existing = await _jobs.GetAsync(expectedJobId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }
        var decisions = record.Snapshot.RewardDecisions
            ?? throw new InvalidDataException(
                "The prepared leaderboard snapshot has no reward decisions.");
        var grants = decisions
            .Where(decision => string.Equals(decision.State, "granted", StringComparison.Ordinal))
            .Select(decision => new SeasonRewardGrant(
                decision.AccountId,
                ExtractionCurrency.MarketCoin,
                TargetSeasonId: null,
                decision.MarketCoin,
                decision.RewardKey))
            .ToArray();
        if (decisions.Any(decision =>
                decision.State is not ("granted" or "cancelled")))
        {
            throw new InvalidDataException(
                "The prepared leaderboard contains an unknown reward decision state.");
        }
        var recovered = await _jobs.PrepareRewardAsync(
            record.Snapshot.SeasonId,
            record.Snapshot.Rules.RulesVersion,
            RewardBatchKey(record.Snapshot),
            grants,
            actor,
            gate,
            activeOperations,
            cancellationToken);
        if (recovered.JobId != expectedJobId)
        {
            throw new InvalidDataException(
                "The recovered leaderboard reward job id does not match the frozen decision.");
        }
        return recovered;
    }

    private static string RewardBatchKey(SeasonLeaderboardSnapshot snapshot) =>
        $"leaderboard-{snapshot.SnapshotId:N}";

    private static Guid RewardJobId(
        SeasonLeaderboardSnapshot snapshot,
        string rewardBatchKey) => SeasonLeaderboardHash.DeterministicGuid(
        $"{SeasonSettlementJobService.FrameworkVersion}|reward|{snapshot.SeasonId:D}|" +
        $"{snapshot.Rules.RulesVersion}|{rewardBatchKey}");

    private static EntryAccumulator GetAccumulator(
        IDictionary<Guid, EntryAccumulator> values,
        Guid accountId)
    {
        if (!values.TryGetValue(accountId, out var accumulator))
        {
            accumulator = new EntryAccumulator(accountId);
            values.Add(accountId, accumulator);
        }
        return accumulator;
    }

    private static DateTimeOffset? Min(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is null || candidate < current ? candidate : current;

    private static void EnsureMaintenance(
        ExtractionOperationGateState gate,
        int activeOperations)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (!gate.Maintenance || activeOperations != 0)
        {
            throw new InvalidOperationException(
                "Leaderboard freeze and reward jobs require maintenance mode and zero active economy operations.");
        }
    }

    private static void ValidateText(string value, int minimum, int maximum, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var trimmed = value.Trim();
        if (trimmed.Length < minimum || trimmed.Length > maximum || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{name} must contain {minimum}-{maximum} non-control characters.",
                name);
        }
    }

    private sealed class EntryAccumulator(Guid accountId)
    {
        public Guid AccountId { get; } = accountId;
        public int SettledExchanges { get; set; }
        public long ResourceQuantity { get; set; }
        public long ResourceValue { get; set; }
        public int TaskPoints { get; set; }
        public DateTimeOffset? FirstSettledAt { get; set; }
        public DateTimeOffset? FirstTaskPointAt { get; set; }
        public Dictionary<string, SeasonLeaderboardResourceAggregate> Items { get; } =
            new(StringComparer.Ordinal);
    }
}

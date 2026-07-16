using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Content;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var directory = Path.Combine(
    Path.GetTempPath(),
    "pal-control-season-leaderboard",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    await VerifyFrozenLeaderboardAndRewardsAsync(directory, CancellationToken.None);
    Console.WriteLine(
        "PASS: auditable cutoff snapshot, deterministic ties/minimums/exclusions, " +
        "20x freeze/reward/manual replay, self-only latest/by-season player results, " +
        "voucher/reward ledger reconciliation, and restart persistence.");
    return 0;
}
finally
{
    for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException) when (attempt < 4)
        {
            await Task.Delay(50);
        }
        catch (UnauthorizedAccessException) when (attempt < 4)
        {
            await Task.Delay(50);
        }
    }
}

static async Task VerifyFrozenLeaderboardAndRewardsAsync(
    string directory,
    CancellationToken cancellationToken)
{
    var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    var timeProvider = new FixedTimeProvider(now);
    var maintenance = new ExtractionOperationGateState(
        Maintenance: true,
        Reason: "weekly leaderboard settlement",
        Actor: "harness",
        UpdatedAt: now);
    var contentVersionId = Guid.NewGuid();
    var contentHash = Hash("leaderboard-content-v1");
    Guid seasonId;
    Guid expectedSnapshotId;
    string expectedSnapshotHash;
    Guid expectedStandardJobId;
    Guid expectedManualJobId;
    Guid expectedManualLedgerId;
    Guid expectedExpiryJobId;
    Guid accountAId;
    Guid accountBId;
    Guid accountEId;

    using (var repository = new SqliteExtractionRepository(directory, timeProvider))
    {
        using (var contentStore = new SqliteEconomyContentStore(directory, timeProvider))
        {
            await SeedContentVersionAsync(
                directory,
                contentVersionId,
                contentHash,
                now,
                cancellationToken);
        }
        using (var taskStore = new SqliteReliableTaskStore(directory))
        {
            // Construction creates the authoritative ranking-reward table.
        }

        var season = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local",
                "WEEK-LEADERBOARD-01",
                "Leaderboard week 1",
                Guid.NewGuid().ToString("N"),
                now.AddDays(-7),
                now.AddHours(-2),
                ExtractionSeasonState.Closed),
            null,
            cancellationToken);
        seasonId = season.SeasonId;
        var cutoffAt = season.EndsAt.AddMinutes(
            SeasonLeaderboardPolicy.Current.LateSettlementGraceMinutes);

        var accountA = await AccountAsync(repository, "steam_leaderboard_a", "Player A", cancellationToken);
        var accountB = await AccountAsync(repository, "steam_leaderboard_b", "Player B", cancellationToken);
        var accountC = await AccountAsync(repository, "steam_leaderboard_c", "Player C", cancellationToken);
        var accountD = await AccountAsync(repository, "steam_leaderboard_d", "Player D", cancellationToken);
        var accountE = await AccountAsync(repository, "steam_leaderboard_e", "Player E", cancellationToken);
        var accountF = await AccountAsync(repository, "steam_leaderboard_f", "Player F", cancellationToken);
        accountAId = accountA.AccountId;
        accountBId = accountB.AccountId;
        accountEId = accountE.AccountId;
        var voucherSeed = await repository.AdjustWalletAsync(
            new WalletAdjustmentRequest(
                accountA.AccountId,
                seasonId,
                ExtractionCurrency.SeasonVoucher,
                300,
                "leaderboard player settlement fixture",
                "harness_seed",
                $"voucher:{seasonId:N}:{accountA.AccountId:N}",
                "harness",
                $"voucher-seed:{seasonId:N}:{accountA.AccountId:N}"),
            cancellationToken);
        Assert(voucherSeed is { Created: true, LedgerEntry.Delta: 300 },
            "player settlement fixture did not create the expiring voucher balance");

        var validRuns = new List<ExtractionSettlementRun>
        {
            SettledRun(accountA, seasonId, contentVersionId, contentHash,
                cutoffAt.AddMinutes(-90), "Crystal", 10, 20),
            SettledRun(accountB, seasonId, contentVersionId, contentHash,
                cutoffAt.AddMinutes(-80), "Wood", 20, 10),
            SettledRun(accountC, seasonId, contentVersionId, contentHash,
                cutoffAt.AddMinutes(-70), "Stone", 5, 10),
            SettledRun(accountD, seasonId, contentVersionId, contentHash,
                cutoffAt.AddMinutes(-60), "Wood", 50, 10),
            SettledRun(accountE, seasonId, contentVersionId, contentHash,
                cutoffAt.AddMinutes(-50), "Crystal", 25, 20)
        };
        validRuns.AddRange(Enumerable.Range(1, 1001).Select(offset => SettledRun(
            accountF,
            seasonId,
            contentVersionId,
            contentHash,
            cutoffAt.AddSeconds(offset),
            "Crystal",
            1,
            20)));
        await ((IExtractionSettlementPersistence)repository).PersistSettlementRunWritesAsync(
            validRuns.Select(ExtractionSettlementRunWrite.Insert).ToArray(),
            cancellationToken);
        await SeedTaskPointsAsync(
            directory,
            seasonId,
            [
                (accountA.AccountId, 20, cutoffAt.AddMinutes(-40)),
                (accountB.AccountId, 20, cutoffAt.AddMinutes(-30)),
                (accountC.AccountId, 5, cutoffAt.AddMinutes(-20)),
                (accountD.AccountId, 50, cutoffAt.AddMinutes(-10)),
                (accountE.AccountId, 40, cutoffAt),
                (accountF.AccountId, 100, cutoffAt.AddSeconds(1))
            ],
            cancellationToken);

        var identitySecurity = new PlayerIdentitySecurityStore(directory);
        identitySecurity.SetBan(
            accountD.ExternalUserId,
            banned: true,
            Guid.NewGuid().ToString("D"),
            PlayerIdentitySecurityStore.FingerprintSubject("harness-owner"),
            now);
        var leaderboardStore = new SeasonLeaderboardStore(directory, timeProvider);
        var jobStore = new SeasonSettlementJobStore(directory, timeProvider);
        var jobService = new SeasonSettlementJobService(repository, jobStore, timeProvider);
        var service = new SeasonLeaderboardService(
            repository,
            identitySecurity,
            leaderboardStore,
            jobService,
            timeProvider);
        var playerSettlement = new PlayerSeasonSettlementService(
            repository,
            service,
            jobStore);
        var beforeFreeze = await playerSettlement.GetLatestAsync(
            "local",
            accountA.AccountId,
            cancellationToken);
        Assert(!beforeFreeze.Available && beforeFreeze.Status == "not-frozen" &&
               beforeFreeze.Settlement is null,
            "latest player settlement did not fail closed before a snapshot was frozen");
        var latestBeforeFreezeResult = SeasonLeaderboardEndpoints.LatestPlayerResult(beforeFreeze);
        Assert(latestBeforeFreezeResult is IStatusCodeHttpResult { StatusCode: 200 } &&
               latestBeforeFreezeResult is IValueHttpResult
                   { Value: PlayerSeasonSettlementResponse { Available: false, Status: "not-frozen" } },
            "latest player endpoint did not return HTTP 200/not-frozen before a snapshot exists");
        var missingSeasonResult = SeasonLeaderboardEndpoints.PlayerSeasonResult(null);
        Assert(missingSeasonResult is IStatusCodeHttpResult { StatusCode: 404 } &&
               missingSeasonResult is IValueHttpResult
                   { Value: ApiError { Code: "SEASON_LEADERBOARD_NOT_FOUND" } },
            "season-specific player endpoint did not preserve its stable 404 contract");
        var identityOverride = new DefaultHttpContext();
        identityOverride.Request.QueryString = QueryString.Create(
            "accountId",
            accountB.AccountId.ToString("D"));
        try
        {
            SeasonLeaderboardEndpoints.RejectPlayerIdentityOverride(identityOverride);
            throw new InvalidOperationException(
                "player endpoint accepted another account id through a query parameter");
        }
        catch (PlayerPortalException exception) when (
            exception.Code == "PLAYER_IDENTITY_OVERRIDE_FORBIDDEN" &&
            exception.StatusCode == StatusCodes.Status400BadRequest)
        {
            // Expected: player identity always comes from the authenticated session.
        }
        _ = await service.SetExclusionAsync(
            seasonId,
            accountE.AccountId,
            active: true,
            "confirmed anti-cheat exclusion before freeze",
            "harness-owner",
            Guid.NewGuid().ToString("D"),
            cancellationToken);

        SeasonLeaderboardRecord? firstFreeze = null;
        for (var replay = 0; replay < 20; replay++)
        {
            if (replay == 1)
            {
                var lateRun = SettledRun(
                    accountA,
                    seasonId,
                    contentVersionId,
                    contentHash,
                    cutoffAt.AddHours(1),
                    "Crystal",
                    500,
                    20);
                validRuns.Add(lateRun);
                await ((IExtractionSettlementPersistence)repository).PersistSettlementRunWritesAsync(
                    [ExtractionSettlementRunWrite.Insert(lateRun)],
                    cancellationToken);
            }
            leaderboardStore = new SeasonLeaderboardStore(directory, timeProvider);
            service = new SeasonLeaderboardService(
                repository,
                identitySecurity,
                leaderboardStore,
                jobService,
                timeProvider);
            var frozen = await service.FreezeAsync(
                seasonId,
                "harness-owner",
                Guid.NewGuid().ToString("D"),
                maintenance,
                activeOperations: 0,
                cancellationToken);
            firstFreeze ??= frozen;
            Assert(frozen.Snapshot.SnapshotId == firstFreeze.Snapshot.SnapshotId &&
                   frozen.Snapshot.SnapshotHash == firstFreeze.Snapshot.SnapshotHash &&
                   frozen.Snapshot.SourceHash == firstFreeze.Snapshot.SourceHash,
                "freeze replay changed the immutable snapshot after new late data arrived");
        }
        var frozenRecord = firstFreeze
            ?? throw new InvalidOperationException("the leaderboard was never frozen");
        expectedSnapshotId = frozenRecord.Snapshot.SnapshotId;
        expectedSnapshotHash = frozenRecord.Snapshot.SnapshotHash;
        VerifyFrozenSnapshot(
            frozenRecord,
            accountA,
            accountB,
            accountC,
            accountD,
            accountE,
            accountF);

        // A newly detected ban after the immutable rank freeze must cancel
        // payout without rewriting rank history.
        identitySecurity.SetBan(
            accountB.ExternalUserId,
            banned: true,
            Guid.NewGuid().ToString("D"),
            PlayerIdentitySecurityStore.FingerprintSubject("harness-owner"),
            now);

        // Persist the deterministic reward decision before creating the
        // season job, then model process death in that exact window.
        var crashDecisions = BuildRewardDecisions(
            frozenRecord.Snapshot,
            accountB.AccountId);
        var rewardBatchKey = $"leaderboard-{frozenRecord.Snapshot.SnapshotId:N}";
        var crashJobId = DeterministicGuid(
            $"{SeasonSettlementJobService.FrameworkVersion}|reward|{seasonId:D}|" +
            $"{frozenRecord.Snapshot.Rules.RulesVersion}|{rewardBatchKey}");
        _ = await leaderboardStore.AttachRewardJobAsync(
            seasonId,
            crashJobId,
            crashDecisions,
            "harness-owner",
            Guid.NewGuid().ToString("D"),
            cancellationToken);
        Assert(await jobStore.GetAsync(crashJobId, cancellationToken) is null,
            "crash-window fixture unexpectedly created the reward job");
        var crashWindowPlayerA = await playerSettlement.GetAsync(
            seasonId,
            accountA.AccountId,
            cancellationToken)
            ?? throw new InvalidOperationException("crash-window player result disappeared");
        Assert(crashWindowPlayerA.Settlement is not null &&
               crashWindowPlayerA.Settlement.RewardState == "prepared" &&
               crashWindowPlayerA.Settlement.PermanentRewards.Count == 2 &&
               crashWindowPlayerA.Settlement.PermanentRewards.All(reward =>
                   reward.DecisionState == "granted" &&
                   reward.DeliveryState == "pending" &&
                   !reward.LedgerRecorded),
            "durable reward decisions were not shown as pending in the job-creation crash window");
        await AssertThrowsAsync<InvalidOperationException>(() =>
            leaderboardStore.AttachRewardJobAsync(
                seasonId,
                crashJobId,
                crashDecisions.Select((decision, index) => index == 0
                    ? decision with { MarketCoin = decision.MarketCoin + 1 }
                    : decision).ToArray(),
                "harness-owner",
                Guid.NewGuid().ToString("D"),
                cancellationToken),
            "reward-decision reservation accepted a conflicting replay");

        SeasonSettlementJob? standardJob = null;
        for (var replay = 0; replay < 20; replay++)
        {
            leaderboardStore = new SeasonLeaderboardStore(directory, timeProvider);
            jobStore = new SeasonSettlementJobStore(directory, timeProvider);
            jobService = new SeasonSettlementJobService(repository, jobStore, timeProvider);
            service = new SeasonLeaderboardService(
                repository,
                identitySecurity,
                leaderboardStore,
                jobService,
                timeProvider);
            var prepared = await service.PrepareRewardsAsync(
                seasonId,
                "harness-owner",
                Guid.NewGuid().ToString("D"),
                maintenance,
                activeOperations: 0,
                cancellationToken);
            standardJob ??= prepared;
            Assert(prepared.JobId == standardJob.JobId &&
                   prepared.PayloadHash == standardJob.PayloadHash &&
                   prepared.Items.Select(item => item.ItemId)
                       .SequenceEqual(standardJob.Items.Select(item => item.ItemId)),
                "standard reward preparation was not an idempotent replay");
        }
        var preparedJob = standardJob
            ?? throw new InvalidOperationException("the standard reward job was never prepared");
        expectedStandardJobId = preparedJob.JobId;
        Assert(preparedJob.JobId == crashJobId,
            "crash-window recovery created a different deterministic reward job");
        Assert(preparedJob.Items.Count == 2 && preparedJob.Items.Sum(item => item.Delta) == 600,
            "post-freeze ban did not cancel both Player B rewards or changed Player A rewards");

        var preparedSnapshot = await leaderboardStore.GetSnapshotAsync(seasonId, cancellationToken)
            ?? throw new InvalidOperationException("prepared snapshot disappeared");
        Assert(preparedSnapshot.ResourceRank(accountB.AccountId) == 1 &&
               preparedSnapshot.TaskRank(accountB.AccountId) == 2,
            "reward cancellation rewrote the immutable frozen ranks");
        var playerBDecisions = preparedSnapshot.RewardDecisions!
            .Where(decision => decision.AccountId == accountB.AccountId)
            .ToArray();
        Assert(playerBDecisions.Length == 2 &&
               playerBDecisions.All(decision => decision.State == "cancelled" &&
                   decision.ReasonCode == "identity-banned-before-reward"),
            "post-freeze identity ban was not recorded as two cancelled reward decisions");

        await AssertThrowsAsync<InvalidOperationException>(() => service.SetExclusionAsync(
            seasonId,
            accountA.AccountId,
            active: true,
            "attempted mutation after reward preparation",
            "harness-owner",
            Guid.NewGuid().ToString("D"),
            cancellationToken),
            "reward exclusions remained mutable after standard job preparation");

        for (var replay = 0; replay < 20; replay++)
        {
            leaderboardStore = new SeasonLeaderboardStore(directory, timeProvider);
            jobStore = new SeasonSettlementJobStore(directory, timeProvider);
            jobService = new SeasonSettlementJobService(repository, jobStore, timeProvider);
            service = new SeasonLeaderboardService(
                repository,
                identitySecurity,
                leaderboardStore,
                jobService,
                timeProvider);
            var completed = await service.RunRewardsAsync(
                seasonId,
                "harness-owner",
                Guid.NewGuid().ToString("D"),
                maintenance,
                activeOperations: 0,
                cancellationToken);
            Assert(completed.JobId == preparedJob.JobId &&
                   completed.State == SeasonSettlementJobState.Completed,
                "standard reward replay did not remain completed");
        }
        await AssertEachJobItemHasOneLedgerAsync(repository, preparedJob, cancellationToken);
        Assert((await repository.GetWalletAsync(
                    accountA.AccountId,
                    seasonId,
                    cancellationToken)).MarketCoin.Balance == 600,
            "Player A permanent rewards have the wrong total");
        Assert((await repository.GetWalletAsync(
                    accountB.AccountId,
                    seasonId,
                    cancellationToken)).MarketCoin.Balance == 0,
            "banned Player B received a permanent leaderboard reward");

        SeasonLeaderboardManualRewardResult? manualResult = null;
        for (var replay = 0; replay < 20; replay++)
        {
            leaderboardStore = new SeasonLeaderboardStore(directory, timeProvider);
            jobStore = new SeasonSettlementJobStore(directory, timeProvider);
            jobService = new SeasonSettlementJobService(repository, jobStore, timeProvider);
            service = new SeasonLeaderboardService(
                repository,
                identitySecurity,
                leaderboardStore,
                jobService,
                timeProvider);
            var current = await service.GrantManualRewardAsync(
                seasonId,
                accountE.AccountId,
                amount: 77,
                manualKey: "appeal-case-2026-07-16",
                reason: "appeal reviewed with external evidence",
                actor: "harness-owner",
                correlationId: Guid.NewGuid().ToString("D"),
                maintenance,
                activeOperations: 0,
                cancellationToken);
            manualResult ??= current;
            Assert(current.Job.JobId == manualResult.Job.JobId &&
                   current.LedgerEntryId == manualResult.LedgerEntryId,
                "manual supplement replay created a different job or ledger entry");
        }
        var manual = manualResult
            ?? throw new InvalidOperationException("the manual reward was never applied");
        expectedManualJobId = manual.Job.JobId;
        expectedManualLedgerId = manual.LedgerEntryId;
        await AssertEachJobItemHasOneLedgerAsync(repository, manual.Job, cancellationToken);
        await AssertThrowsAsync<InvalidOperationException>(() =>
            service.GrantManualRewardAsync(
                seasonId,
                accountE.AccountId,
                amount: 77,
                manualKey: "appeal-case-2026-07-16",
                reason: "conflicting reason for the same manual operation",
                actor: "harness-owner",
                correlationId: Guid.NewGuid().ToString("D"),
                maintenance,
                activeOperations: 0,
                cancellationToken),
            "manual supplement key accepted conflicting audit evidence");
        Assert((await repository.GetWalletAsync(
                    accountE.AccountId,
                    seasonId,
                    cancellationToken)).MarketCoin.Balance == 77,
            "manual supplement was not credited exactly once");

        SeasonSettlementJob? completedExpiry = null;
        for (var replay = 0; replay < 3; replay++)
        {
            jobStore = new SeasonSettlementJobStore(directory, timeProvider);
            jobService = new SeasonSettlementJobService(repository, jobStore, timeProvider);
            var expiry = await jobService.PrepareVoucherExpiryAsync(
                seasonId,
                preparedSnapshot.Rules.RulesVersion,
                "harness-owner",
                maintenance,
                activeOperations: 0,
                cancellationToken);
            completedExpiry = await jobService.RunAsync(
                expiry.JobId,
                maintenance,
                activeOperations: 0,
                cancellationToken);
            Assert(completedExpiry.State == SeasonSettlementJobState.Completed &&
                   completedExpiry.Items.Count == 1 &&
                   completedExpiry.Items.Single().AccountId == accountA.AccountId &&
                   completedExpiry.Items.Single().Delta == -300,
                "voucher expiry did not retain the frozen player amount and completion evidence");
        }
        expectedExpiryJobId = completedExpiry?.JobId
            ?? throw new InvalidOperationException("voucher expiry was never completed");
        Assert((await repository.GetWalletAsync(
                    accountA.AccountId,
                    seasonId,
                    cancellationToken)).SeasonVoucher.Balance == 0,
            "Player A weekly vouchers did not expire exactly once");

        var completedRecord = await leaderboardStore.GetRecordAsync(seasonId, cancellationToken)
            ?? throw new InvalidOperationException("completed leaderboard disappeared");
        Assert(completedRecord.Snapshot.RewardState == "completed" &&
               completedRecord.Snapshot.RewardJobId == preparedJob.JobId,
            "completed standard reward state was not attached to the snapshot");
        var eventTypes = completedRecord.Audit.Select(item => item.EventType).ToHashSet();
        Assert(eventTypes.IsSupersetOf([
                "account.excluded",
                "leaderboard.frozen",
                "reward.prepared",
                "reward.completed",
                "reward.manual-supplement"
            ]),
            "leaderboard audit trail is missing a freeze, exclusion, payout, or supplement event");
        Assert(completedRecord.Audit.All(item =>
                Guid.TryParseExact(item.CorrelationId, "D", out _) &&
                item.DetailsHash.Length == 64),
            "leaderboard audit evidence lacks canonical correlations or detail hashes");
        await VerifyCanonicalAccountTieBreakAsync(
            directory,
            repository,
            identitySecurity,
            contentVersionId,
            contentHash,
            timeProvider,
            maintenance,
            now,
            cancellationToken);

        _ = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local",
                "WEEK-ACTIVE-AFTER-FREEZE",
                "Current week after frozen result",
                Guid.NewGuid().ToString("N"),
                now.AddMinutes(-30),
                now.AddDays(6),
                ExtractionSeasonState.Active),
            null,
            cancellationToken);
        leaderboardStore = new SeasonLeaderboardStore(directory, timeProvider);
        jobStore = new SeasonSettlementJobStore(directory, timeProvider);
        jobService = new SeasonSettlementJobService(repository, jobStore, timeProvider);
        service = new SeasonLeaderboardService(
            repository,
            identitySecurity,
            leaderboardStore,
            jobService,
            timeProvider);
        playerSettlement = new PlayerSeasonSettlementService(repository, service, jobStore);

        var latestA = await playerSettlement.GetLatestAsync(
            "local",
            accountA.AccountId,
            cancellationToken);
        Assert(latestA is { Available: true, Status: "frozen", Settlement: not null } &&
               latestA.Settlement.SeasonId == seasonId,
            "latest player result followed the new active week instead of the latest frozen week");
        var settlementA = latestA.Settlement
            ?? throw new InvalidOperationException("latest Player A settlement is null");
        Assert(settlementA.Participation is
                   { Participating: true, Resource.Rank: 2, Task.Rank: 1 } &&
               settlementA.VoucherExpiry is
                   { JobState: "completed", ItemState: "expired", ScheduledAmount: 300,
                     ExpiredAmount: 300, LedgerRecorded: true } &&
               settlementA.PermanentRewards.Count == 2 &&
               settlementA.PermanentRewards.All(reward =>
                   reward.DeliveryState == "paid" && reward.LedgerRecorded) &&
               settlementA.PermanentRewards.Sum(reward => reward.MarketCoin) == 600,
            "Player A result is not reconciled from frozen ranks, expiry job, and permanent ledgers");

        var playerB = await playerSettlement.GetAsync(
            seasonId,
            accountB.AccountId,
            cancellationToken)
            ?? throw new InvalidOperationException("Player B frozen result disappeared");
        Assert(playerB.Settlement is not null &&
               playerB.Settlement.PermanentRewards.Count == 2 &&
               playerB.Settlement.PermanentRewards.All(reward =>
                   reward.DecisionState == "cancelled" &&
                   reward.DeliveryState == "cancelled" &&
                   !reward.LedgerRecorded) &&
               playerB.Settlement.VoucherExpiry.ItemState == "not-applicable",
            "Player B did not receive the frozen-rank/cancelled-reward projection");

        var playerE = await playerSettlement.GetAsync(
            seasonId,
            accountE.AccountId,
            cancellationToken)
            ?? throw new InvalidOperationException("Player E frozen result disappeared");
        Assert(playerE.Settlement is not null &&
               playerE.Settlement.Participation.ReasonCode == "manual-exclusion-at-freeze" &&
               playerE.Settlement.PermanentRewards is
                   [{ Source: "supplement", MarketCoin: 77, DeliveryState: "paid", LedgerRecorded: true }],
            "manual exclusion and supplement evidence are missing from Player E's result");

        var playerAJson = JsonSerializer.Serialize(latestA);
        Assert(!playerAJson.Contains(accountB.AccountId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
               !playerAJson.Contains(accountE.AccountId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
               !playerAJson.Contains(accountB.ExternalUserId, StringComparison.OrdinalIgnoreCase) &&
               !playerAJson.Contains(accountE.ExternalUserId, StringComparison.OrdinalIgnoreCase),
            "Player A projection leaked another player's account or platform identity");
        Assert(await playerSettlement.GetAsync(
                   Guid.NewGuid(),
                   accountA.AccountId,
                   cancellationToken) is null,
            "unknown season query did not preserve the stable not-found contract");
    }

    // Reopen every durable component after the repository's exclusive process
    // lock has been released.
    using (var restartedRepository = new SqliteExtractionRepository(directory, timeProvider))
    {
        var restartedSecurity = new PlayerIdentitySecurityStore(directory);
        var restartedLeaderboardStore = new SeasonLeaderboardStore(directory, timeProvider);
        var restartedJobStore = new SeasonSettlementJobStore(directory, timeProvider);
        var restartedJobService = new SeasonSettlementJobService(
            restartedRepository,
            restartedJobStore,
            timeProvider);
        var restartedService = new SeasonLeaderboardService(
            restartedRepository,
            restartedSecurity,
            restartedLeaderboardStore,
            restartedJobService,
            timeProvider);
        var restarted = await restartedService.GetAsync(seasonId, cancellationToken)
            ?? throw new InvalidOperationException("snapshot did not survive restart");
        Assert(restarted.Snapshot.SnapshotId == expectedSnapshotId &&
               restarted.Snapshot.SnapshotHash == expectedSnapshotHash &&
               restarted.Snapshot.RewardJobId == expectedStandardJobId &&
               restarted.Snapshot.RewardState == "completed",
            "restart changed frozen or reward-completion evidence");
        Assert((await restartedJobStore.GetAsync(expectedManualJobId, cancellationToken))?
                   .Items.Single().LedgerEntryId == expectedManualLedgerId,
            "manual reward ledger evidence did not survive restart");
        Assert((await restartedJobStore.GetAsync(expectedExpiryJobId, cancellationToken))?
                   .Items.Single() is { Delta: -300, State: SeasonSettlementItemState.Applied },
            "voucher expiry evidence did not survive restart");
        Assert((await restartedRepository.GetWalletAsync(
                    accountAId,
                    seasonId,
                    cancellationToken)).MarketCoin.Balance == 600 &&
               (await restartedRepository.GetWalletAsync(
                    accountBId,
                    seasonId,
                    cancellationToken)).MarketCoin.Balance == 0 &&
               (await restartedRepository.GetWalletAsync(
                    accountEId,
                    seasonId,
                    cancellationToken)).MarketCoin.Balance == 77,
            "permanent reward balances changed after restart");
        var restartedPlayerSettlement = new PlayerSeasonSettlementService(
            restartedRepository,
            restartedService,
            restartedJobStore);
        var restartedPlayerA = await restartedPlayerSettlement.GetLatestAsync(
            "local",
            accountAId,
            cancellationToken);
        Assert(restartedPlayerA.Settlement is not null &&
               restartedPlayerA.Settlement.SeasonId == seasonId &&
               restartedPlayerA.Settlement.VoucherExpiry.ExpiredAmount == 300 &&
               restartedPlayerA.Settlement.PermanentRewards.Sum(reward => reward.MarketCoin) == 600 &&
               restartedPlayerA.Settlement.PermanentRewards.All(reward => reward.LedgerRecorded),
            "player-only weekly result changed after every durable store restarted");
    }
}

static async Task VerifyCanonicalAccountTieBreakAsync(
    string directory,
    SqliteExtractionRepository repository,
    PlayerIdentitySecurityStore identitySecurity,
    Guid contentVersionId,
    string contentHash,
    TimeProvider timeProvider,
    ExtractionOperationGateState maintenance,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    var season = await repository.UpsertSeasonAsync(
        null,
        new ExtractionSeasonDefinition(
            "local",
            "WEEK-LEADERBOARD-TIE",
            "Canonical account tie fixture",
            Guid.NewGuid().ToString("N"),
            now.AddDays(-8),
            now.AddHours(-3),
            ExtractionSeasonState.Closed),
        null,
        cancellationToken);
    var first = await AccountAsync(
        repository,
        "steam_leaderboard_tie_a",
        "Tie A",
        cancellationToken);
    var second = await AccountAsync(
        repository,
        "steam_leaderboard_tie_b",
        "Tie B",
        cancellationToken);
    var cutoffAt = season.EndsAt.AddMinutes(
        SeasonLeaderboardPolicy.Current.LateSettlementGraceMinutes);
    var occurredAt = cutoffAt.AddMinutes(-30);
    await ((IExtractionSettlementPersistence)repository).PersistSettlementRunWritesAsync(
        [
            ExtractionSettlementRunWrite.Insert(SettledRun(
                first, season.SeasonId, contentVersionId, contentHash,
                occurredAt, "Wood", 10, 10)),
            ExtractionSettlementRunWrite.Insert(SettledRun(
                second, season.SeasonId, contentVersionId, contentHash,
                occurredAt, "Wood", 10, 10))
        ],
        cancellationToken);
    await SeedTaskPointsAsync(
        directory,
        season.SeasonId,
        [
            (first.AccountId, 10, occurredAt),
            (second.AccountId, 10, occurredAt)
        ],
        cancellationToken);
    var store = new SeasonLeaderboardStore(directory, timeProvider);
    var jobs = new SeasonSettlementJobService(
        repository,
        new SeasonSettlementJobStore(directory, timeProvider),
        timeProvider);
    var service = new SeasonLeaderboardService(
        repository,
        identitySecurity,
        store,
        jobs,
        timeProvider);
    var frozen = await service.FreezeAsync(
        season.SeasonId,
        "harness-owner",
        Guid.NewGuid().ToString("D"),
        maintenance,
        activeOperations: 0,
        cancellationToken);
    var expectedFirst = new[] { first.AccountId, second.AccountId }
        .OrderBy(accountId => accountId.ToString("N"), StringComparer.Ordinal)
        .First();
    Assert(frozen.Snapshot.ResourceRank(expectedFirst) == 1 &&
           frozen.Snapshot.TaskRank(expectedFirst) == 1,
        "the final tie fallback did not use canonical account-id text ordering");
}

static void VerifyFrozenSnapshot(
    SeasonLeaderboardRecord record,
    ExtractionAccount accountA,
    ExtractionAccount accountB,
    ExtractionAccount accountC,
    ExtractionAccount accountD,
    ExtractionAccount accountE,
    ExtractionAccount accountF)
{
    var snapshot = record.Snapshot;
    Assert(snapshot.ResourceRank(accountB.AccountId) == 1 &&
           snapshot.ResourceRank(accountA.AccountId) == 2,
        "resource-value tie was not broken by quantity before time/account id");
    Assert(snapshot.TaskRank(accountA.AccountId) == 1 &&
           snapshot.TaskRank(accountB.AccountId) == 2,
        "task-point tie was not broken by the earliest authoritative task reward");
    var minimum = snapshot.Entry(accountC.AccountId);
    Assert(!minimum.ResourceEligible && minimum.ResourceRank is null &&
           !minimum.TaskEligible && minimum.TaskRank is null,
        "minimum resource/task contribution thresholds were not enforced");
    var banned = snapshot.Entry(accountD.AccountId);
    Assert(banned.IdentityBannedAtFreeze &&
           banned.RankingExclusionCode == "identity-banned-at-freeze" &&
           banned.ResourceRank is null && banned.TaskRank is null,
        "identity banned before freeze remained rank eligible");
    var excluded = snapshot.Entry(accountE.AccountId);
    Assert(excluded.ManuallyExcludedAtFreeze &&
           excluded.RankingExclusionCode == "manual-exclusion-at-freeze" &&
           excluded.ResourceRank is null && excluded.TaskRank is null,
        "manual anti-cheat exclusion remained rank eligible");
    Assert(snapshot.Entries.All(entry => entry.AccountId != accountF.AccountId) &&
           snapshot.LateSettlementCountAtFreeze == 1001 &&
           snapshot.LateTaskPointCountAtFreeze == 1 &&
           record.Evidence.LateSettlementIdsObservedAtFreeze.Count == 1001 &&
           record.Evidence.LateTaskPointEntryIdsObservedAtFreeze.Count == 1,
        "post-cutoff settlement or task points leaked into or were paged out of the frozen snapshot");
    Assert(record.Evidence.Settlements.Count == 5 &&
           record.Evidence.TaskPoints.Count == 5,
        "complete pre-cutoff source evidence was not persisted");
    Assert(snapshot.GlobalItems.Count == 3 &&
           snapshot.GlobalItems.Single(item => item.ItemId == "Wood").Value == 200 &&
           snapshot.GlobalItems.Single(item => item.ItemId == "Stone").Value == 50 &&
           snapshot.GlobalItems.Single(item => item.ItemId == "Crystal").Value == 200,
        "item-level effective quantity/value aggregates are incorrect");
    Assert(snapshot.GlobalCategories.Count == 2 &&
           snapshot.GlobalCategories.Single(item => item.Category == "basic") is
               { Quantity: 25, Value: 250 } &&
           snapshot.GlobalCategories.Single(item => item.Category == "rare") is
               { Quantity: 10, Value: 200 },
        "category-level effective quantity/value aggregates are incorrect");
    Assert(snapshot.RulesHash.Length == 64 && snapshot.SourceHash.Length == 64 &&
           snapshot.SnapshotHash.Length == 64 && record.Audit.Count == 2,
        "snapshot hashes or initial audit evidence are incomplete");
}

static async Task<ExtractionAccount> AccountAsync(
    IExtractionRepository repository,
    string externalUserId,
    string displayName,
    CancellationToken cancellationToken) => await repository.GetOrCreateAccountAsync(
        "steam",
        externalUserId,
        displayName,
        cancellationToken);

static IReadOnlyList<SeasonLeaderboardRewardDecision> BuildRewardDecisions(
    SeasonLeaderboardSnapshot snapshot,
    Guid bannedAfterFreezeAccountId)
{
    var decisions = new List<SeasonLeaderboardRewardDecision>();
    foreach (var tier in snapshot.Rules.RewardTiers
                 .OrderBy(tier => tier.Board, StringComparer.Ordinal)
                 .ThenBy(tier => tier.Rank))
    {
        var entry = tier.Board switch
        {
            "resource-value" => snapshot.Entries.SingleOrDefault(
                candidate => candidate.ResourceRank == tier.Rank),
            "task-points" => snapshot.Entries.SingleOrDefault(
                candidate => candidate.TaskRank == tier.Rank),
            _ => throw new InvalidOperationException("unknown fixture board")
        };
        if (entry is null)
        {
            continue;
        }
        var cancelled = entry.AccountId == bannedAfterFreezeAccountId;
        decisions.Add(new SeasonLeaderboardRewardDecision(
            entry.AccountId,
            tier.Board,
            tier.Rank,
            tier.MarketCoin,
            $"leaderboard:{snapshot.SnapshotId:N}:{tier.RewardKey}",
            cancelled ? "cancelled" : "granted",
            cancelled ? "identity-banned-before-reward" : null));
    }
    return decisions;
}

static ExtractionSettlementRun SettledRun(
    ExtractionAccount account,
    Guid seasonId,
    Guid contentVersionId,
    string contentHash,
    DateTimeOffset settledAt,
    string itemId,
    int quantity,
    long unitValue)
{
    var total = checked(quantity * unitValue);
    return new ExtractionSettlementRun(
        Guid.NewGuid(),
        account.AccountId,
        seasonId,
        account.ExternalUserId,
        "weekly-market",
        "Weekly Market",
        ExtractionSettlementState.Settled,
        [new ExtractionLootLine(itemId, itemId, quantity, unitValue, total)],
        quantity,
        total,
        Hash($"quote|{account.AccountId:N}|{settledAt:O}|{itemId}"),
        null,
        $"settled-{Guid.NewGuid():N}",
        null,
        null,
        settledAt.AddMinutes(-5),
        settledAt.AddMinutes(5),
        settledAt,
        settledAt)
    {
        Revision = 1,
        StateChangedAt = settledAt,
        ContentVersionId = contentVersionId,
        ContentHash = contentHash,
        ContentBusinessDate = DateOnly.FromDateTime(settledAt.UtcDateTime),
        ContentRulesVersion = "leaderboard-content-v1",
        RotationSeed = "leaderboard-seed"
    };
}

static async Task SeedContentVersionAsync(
    string directory,
    Guid versionId,
    string contentHash,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    await using var connection = OpenDatabase(directory);
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO content_versions (
            version_id, server_id, version_number, business_date,
            rules_version, content_hash, document_json, source_draft_id,
            published_by, published_at)
        VALUES (
            $versionId, 'local', 1, $businessDate,
            'leaderboard-content-v1', $contentHash, $documentJson, $draftId,
            'harness', $publishedAt);
        """;
    command.Parameters.AddWithValue("$versionId", versionId.ToString("D"));
    command.Parameters.AddWithValue("$businessDate", DateOnly.FromDateTime(now.UtcDateTime).ToString("yyyy-MM-dd"));
    command.Parameters.AddWithValue("$contentHash", contentHash);
    command.Parameters.AddWithValue("$documentJson", JsonSerializer.Serialize(new
    {
        resources = new[]
        {
            new { itemId = "Wood", category = "basic" },
            new { itemId = "Stone", category = "basic" },
            new { itemId = "Crystal", category = "rare" }
        }
    }));
    command.Parameters.AddWithValue("$draftId", Guid.NewGuid().ToString("D"));
    command.Parameters.AddWithValue("$publishedAt", now.ToString("O"));
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task SeedTaskPointsAsync(
    string directory,
    Guid seasonId,
    IReadOnlyList<(Guid AccountId, int Points, DateTimeOffset CreatedAt)> entries,
    CancellationToken cancellationToken)
{
    await using var connection = OpenDatabase(directory);
    await connection.OpenAsync(cancellationToken);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    var balances = new Dictionary<Guid, int>();
    foreach (var entry in entries)
    {
        var after = checked(balances.GetValueOrDefault(entry.AccountId) + entry.Points);
        balances[entry.AccountId] = after;
        var rewardEntryId = Guid.NewGuid();
        var taskSetId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var fixtureKey = $"leaderboard-fixture-{rewardEntryId:N}";
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO reliable_task_sets (
                task_set_id, account_id, season_id, server_id, cadence,
                period_key, content_version_id, content_hash, rules_version,
                rotation_seed, selected_task_keys_json, created_at)
            VALUES (
                $taskSetId, $accountId, $seasonId, 'local', 'weekly',
                $fixtureKey, $contentVersionId, $contentHash, 'leaderboard-content-v1',
                $fixtureKey, $selectedTaskKeys, $createdAt);
            INSERT INTO reliable_task_instances (
                instance_id, task_set_id, account_id, season_id, server_id,
                cadence, period_key, content_version_id, content_hash,
                rules_version, rotation_seed, task_key, definition_json,
                progress, completed_at, ranking_reward_entry_id,
                created_at, updated_at)
            VALUES (
                $instanceId, $taskSetId, $accountId, $seasonId, 'local',
                'weekly', $fixtureKey, $contentVersionId, $contentHash,
                'leaderboard-content-v1', $fixtureKey, $fixtureKey, '{}',
                1, $createdAt, $entryId,
                $createdAt, $createdAt);
            INSERT INTO reliable_task_ranking_rewards (
                entry_id, instance_id, account_id, season_id,
                points, balance_after, created_at)
            VALUES (
                $entryId, $instanceId, $accountId, $seasonId,
                $points, $balanceAfter, $createdAt);
            """;
        command.Parameters.AddWithValue("$entryId", rewardEntryId.ToString("D"));
        command.Parameters.AddWithValue("$taskSetId", taskSetId.ToString("D"));
        command.Parameters.AddWithValue("$instanceId", instanceId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", entry.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue("$fixtureKey", fixtureKey);
        command.Parameters.AddWithValue("$contentVersionId", Guid.Empty.ToString("D"));
        command.Parameters.AddWithValue("$contentHash", Hash("leaderboard-task-fixture"));
        command.Parameters.AddWithValue("$selectedTaskKeys", JsonSerializer.Serialize(new[] { fixtureKey }));
        command.Parameters.AddWithValue("$points", entry.Points);
        command.Parameters.AddWithValue("$balanceAfter", after);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    await transaction.CommitAsync(cancellationToken);
}

static async Task AssertEachJobItemHasOneLedgerAsync(
    IExtractionRepository repository,
    SeasonSettlementJob job,
    CancellationToken cancellationToken)
{
    foreach (var item in job.Items)
    {
        var matches = (await repository.GetLedgerAsync(
                item.AccountId,
                seasonId: null,
                limit: 1000,
                cancellationToken))
            .Count(entry => entry.ReferenceType == item.ReferenceType &&
                entry.ReferenceId == item.ReferenceId);
        Assert(matches == 1,
            $"reward item '{item.ItemId}' created {matches} ledger entries instead of one");
    }
}

static SqliteConnection OpenDatabase(string directory) => new(
    new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "extraction-commerce.db"),
        Mode = SqliteOpenMode.ReadWrite,
        ForeignKeys = true,
        Pooling = false
    }.ToString());

static string Hash(string value) => Convert.ToHexStringLower(
    SHA256.HashData(Encoding.UTF8.GetBytes(value)));

static Guid DeterministicGuid(string value)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    Span<byte> bytes = stackalloc byte[16];
    hash.AsSpan(0, 16).CopyTo(bytes);
    return new Guid(bytes);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}

static class SnapshotAssertions
{
    public static SeasonLeaderboardEntry Entry(
        this SeasonLeaderboardSnapshot snapshot,
        Guid accountId) => snapshot.Entries.Single(entry => entry.AccountId == accountId);

    public static int? ResourceRank(
        this SeasonLeaderboardSnapshot snapshot,
        Guid accountId) => snapshot.Entry(accountId).ResourceRank;

    public static int? TaskRank(
        this SeasonLeaderboardSnapshot snapshot,
        Guid accountId) => snapshot.Entry(accountId).TaskRank;
}

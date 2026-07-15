using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var cancellationToken = CancellationToken.None;
await VerifyUnpagedStatisticsAndLegacyJsonAsync(cancellationToken);
await VerifyCasAndTerminalMonotonicityAsync(cancellationToken);
await VerifyConcurrentManualResolutionAsync(cancellationToken);
await VerifyExpiredLeaseTakeoverAsync(cancellationToken);
await VerifyWalletCreditIdempotencyAsync(cancellationToken);
await VerifyAtomicCreditAndCrashRecoveryAsync(cancellationToken);
await VerifyManualCreditActorAsync(cancellationToken);
await VerifyBoundedSettlementQueueAsync(cancellationToken);
Console.WriteLine("PASS: settlement CAS, heartbeat, atomic credit, crash recovery, queue, and statistics checks.");

static async Task VerifyUnpagedStatisticsAndLegacyJsonAsync(CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var runs = new List<ExtractionSettlementRun>();
        for (var index = 0; index < 1_005; index++)
        {
            runs.Add(NewRun(accountId, seasonId, ExtractionSettlementState.Settled, 3, now.AddSeconds(index)));
        }
        for (var index = 0; index < 7; index++)
        {
            runs.Add(NewRun(accountId, seasonId, ExtractionSettlementState.Failed, 5, now.AddMinutes(30).AddSeconds(index)));
        }
        for (var index = 0; index < 4; index++)
        {
            runs.Add(NewRun(accountId, seasonId, ExtractionSettlementState.Uncertain, 7, now.AddMinutes(31).AddSeconds(index)));
        }
        runs.Add(NewRun(otherAccountId, seasonId, ExtractionSettlementState.Settled, 999, now.AddHours(1)));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var json = JsonSerializer.SerializeToNode(runs, options)!.AsArray();
        foreach (var node in json.OfType<JsonObject>())
        {
            // Simulate the original on-disk schema. New concurrency metadata must
            // remain optional when an existing installation starts after upgrade.
            node.Remove("revision");
            node.Remove("stateChangedAt");
            node.Remove("leaseId");
            node.Remove("leaseOwner");
            node.Remove("leaseExpiresAt");
            node.Remove("attemptCount");
            node.Remove("lastHeartbeatAt");
            node.Remove("reconciliationActor");
        }
        await File.WriteAllTextAsync(
            Path.Combine(directory, "extraction-runs.json"),
            json.ToJsonString(options),
            cancellationToken);

        using var fixture = CreateStore(directory);
        var store = fixture.Store;
        var firstPage = await store.ListAsync(accountId, seasonId, 1_000, cancellationToken);
        Assert(firstPage.Count == 1_000, "The compatibility list must remain capped at 1,000 rows.");
        Assert(firstPage.All(run => run.Revision == 0), "Legacy rows must load with revision zero.");

        var statistics = await store.GetSeasonStatisticsAsync(accountId, seasonId, cancellationToken);
        Assert(statistics.SettledCount == 1_005, "Statistics lost settled rows after the first 1,000.");
        Assert(statistics.FailedCount == 7, "Failed settlement count is incorrect.");
        Assert(statistics.UncertainCount == 4, "Uncertain settlement count is incorrect.");
        Assert(statistics.SettledTotalValue == 3_015, "Settled total value is incorrect.");
    });
}

static async Task VerifyCasAndTerminalMonotonicityAsync(CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        using var fixture = CreateStore(directory);
        var store = fixture.Store;
        var identity = await CreateEconomyIdentityAsync(fixture.Repository, cancellationToken);
        var accountId = identity.AccountId;
        var seasonId = identity.SeasonId;
        var quote = await store.CreateQuoteAsync(
            accountId,
            seasonId,
            "steam_123",
            "zone-1",
            "回收点",
            [new ExtractionLootLine("Leather", "皮革", 5, 2, 10)],
            "snapshot",
            DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken);
        var start = await store.StartConsumptionAsync(
            quote.RunId,
            quote.UserId,
            "settlement-key-123",
            new Dictionary<string, long> { ["Leather"] = 5 },
            cancellationToken);
        Assert(start.Started, "The quote did not enter Consuming.");
        var leaseId = start.Run.LeaseId ?? throw new InvalidOperationException("HTTP settlement lease missing.");
        Assert(start.Run.AttemptCount == 1 && start.Run.LastHeartbeatAt is not null,
            "Initial consumption attempt and heartbeat were not persisted.");

        var heartbeat = await store.TryHeartbeatLeaseAsync(
            start.Run.RunId,
            start.Run.Revision,
            leaseId,
            "http-settlement",
            cancellationToken);
        Assert(heartbeat.Applied && heartbeat.Run.Revision == start.Run.Revision,
            "Lease heartbeat either failed or changed the state CAS revision.");

        var recoveryWhileOwned = await store.TryAcquireRecoveryLeaseAsync(
            start.Run.RunId,
            start.Run.Revision,
            "test-recovery",
            cancellationToken);
        Assert(!recoveryWhileOwned.Applied, "Recovery stole an active HTTP lease.");

        var removed = await store.TryMarkRemovedAsync(
            start.Run.RunId,
            start.Run.Revision,
            leaseId,
            cancellationToken);
        Assert(removed.Applied && removed.Run.State == ExtractionSettlementState.Removed, "Consuming -> Removed failed.");

        var staleUncertain = await store.TryMarkUncertainAsync(
            start.Run.RunId,
            start.Run.Revision,
            leaseId,
            "STALE",
            "stale recovery result",
            cancellationToken);
        Assert(!staleUncertain.Applied && staleUncertain.Run.State == ExtractionSettlementState.Removed,
            "A stale Consuming snapshot overwrote Removed.");

        var credited = await store.TryCreditRemovedAsync(
            removed.Run.RunId,
            removed.Run.Revision,
            leaseId,
            cancellationToken);
        Assert(credited.Applied && credited.Run.State == ExtractionSettlementState.Credited,
            "Removed -> Credited atomic commit failed.");
        var settled = await store.TryMarkSettledAsync(
            credited.Run.RunId,
            credited.Run.Revision,
            leaseId,
            null,
            cancellationToken);
        Assert(settled.Applied && settled.Run.State == ExtractionSettlementState.Settled, "Credited -> Settled failed.");

        var staleFailed = await store.TryMarkFailedAsync(
            start.Run.RunId,
            start.Run.Revision,
            leaseId,
            "STALE",
            "stale HTTP result",
            cancellationToken);
        Assert(!staleFailed.Applied && staleFailed.Run.State == ExtractionSettlementState.Settled,
            "A terminal Settled run regressed to Failed.");

        // Persistence is part of the synchronization contract, not just an
        // in-memory guard. Reopen the same JSON store and verify the terminal row.
        fixture.Dispose();
        using var reopenedFixture = CreateStore(directory);
        var reopened = reopenedFixture.Store;
        var persisted = await reopened.GetAsync(quote.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Settled run was not durably persisted.");
        Assert(persisted.State == ExtractionSettlementState.Settled, "Settled state was not durably persisted.");
        Assert(persisted.Revision == settled.Run.Revision, "Persisted revision does not match the committed revision.");
        Assert(persisted.LeaseId is null, "Terminal state retained a settlement lease.");
    });
}

static async Task VerifyConcurrentManualResolutionAsync(CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        using var fixture = CreateStore(directory);
        var store = fixture.Store;
        var quote = await store.CreateQuoteAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "steam_456",
            "zone-1",
            "回收点",
            [new ExtractionLootLine("Bone", "骨头", 2, 2, 4)],
            "snapshot",
            DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken);
        var start = await store.StartConsumptionAsync(
            quote.RunId,
            quote.UserId,
            "settlement-key-456",
            new Dictionary<string, long> { ["Bone"] = 2 },
            cancellationToken);
        var leaseId = start.Run.LeaseId ?? throw new InvalidOperationException("HTTP settlement lease missing.");
        var uncertain = await store.TryMarkUncertainAsync(
            start.Run.RunId,
            start.Run.Revision,
            leaseId,
            "DELETE_UNCERTAIN",
            "requires operator review",
            cancellationToken);
        Assert(uncertain.Applied, "Test run did not enter Uncertain.");

        var settleTask = store.TryBeginManualSettlementAsync(
            uncertain.Run.RunId,
            uncertain.Run.Revision,
            "operator confirmed removal",
            "admin:test-settler",
            cancellationToken);
        var failTask = store.TryMarkManuallyFailedAsync(
            uncertain.Run.RunId,
            uncertain.Run.Revision,
            "operator confirmed no removal",
            "admin:test-failer",
            cancellationToken);
        var results = await Task.WhenAll(settleTask, failTask);
        Assert(results.Count(result => result.Applied) == 1, "Two competing manual resolutions both committed.");
        var current = await store.GetAsync(uncertain.Run.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Manual test run disappeared.");
        Assert(current.State is ExtractionSettlementState.Removed or ExtractionSettlementState.Failed,
            "Manual resolution ended in an invalid state.");
        Assert(current.ReconciliationActor is "admin:test-settler" or "admin:test-failer",
            "Manual resolution did not persist the authenticated administrator subject.");
    });
}

static async Task VerifyExpiredLeaseTakeoverAsync(CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        var now = DateTimeOffset.UtcNow;
        var oldLeaseId = Guid.NewGuid();
        var run = NewRun(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ExtractionSettlementState.Consuming,
            10,
            now.AddMinutes(-3)) with
        {
            Revision = 9,
            LeaseId = oldLeaseId,
            LeaseOwner = "dead-http-request",
            LeaseExpiresAt = now.AddMinutes(-1)
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "extraction-runs.json"),
            JsonSerializer.Serialize(new[] { run }, options),
            cancellationToken);

        using var fixture = CreateStore(directory);
        var store = fixture.Store;
        var recoverable = await store.ListRecoverableAsync(25, cancellationToken);
        Assert(recoverable.Count == 1, "Expired lease was not recoverable.");
        var takeover = await store.TryAcquireRecoveryLeaseAsync(
            run.RunId,
            run.Revision,
            "new-recovery",
            cancellationToken);
        Assert(takeover.Applied && takeover.Run.LeaseId != oldLeaseId, "Recovery did not replace the expired lease.");
        Assert(takeover.Run.StateChangedAt == run.UpdatedAt,
            "Legacy state age was reset while acquiring a recovery lease.");
        Assert(takeover.Run.AttemptCount == run.AttemptCount + 1 &&
            takeover.Run.LastHeartbeatAt is not null,
            "Recovery takeover did not persist its attempt and heartbeat.");

        var recoveryCannotConfirmRemoval = await store.TryMarkRemovedAsync(
            run.RunId,
            takeover.Run.Revision,
            takeover.Run.LeaseId ?? throw new InvalidOperationException("Recovery lease missing."),
            cancellationToken);
        Assert(!recoveryCannotConfirmRemoval.Applied &&
            recoveryCannotConfirmRemoval.Run.State == ExtractionSettlementState.Consuming,
            "A recovery lease converted an unknown RCON dispatch into an automatic credit path.");

        var staleOwner = await store.TryMarkFailedAsync(
            run.RunId,
            run.Revision,
            oldLeaseId,
            "STALE_OWNER",
            "old request completed late",
            cancellationToken);
        Assert(!staleOwner.Applied && staleOwner.Run.State == ExtractionSettlementState.Consuming,
            "Expired lease owner mutated the run after takeover.");
    });
}

static async Task VerifyWalletCreditIdempotencyAsync(CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        Guid accountId;
        Guid seasonId;
        var runId = Guid.NewGuid();
        using (var repository = new SqliteExtractionRepository(directory))
        {
            var now = DateTimeOffset.UtcNow;
            var season = await repository.UpsertSeasonAsync(
                null,
                new ExtractionSeasonDefinition(
                    "local",
                    "S-TEST",
                    "结算测试周",
                    "world-test",
                    now.AddDays(-1),
                    now.AddDays(1),
                    ExtractionSeasonState.Active),
                null,
                cancellationToken);
            var account = await repository.GetOrCreateAccountAsync(
                "steam",
                "steam_789",
                "Wallet Test",
                cancellationToken);
            accountId = account.AccountId;
            seasonId = season.SeasonId;
            var commerce = new ExtractionCommerceService(repository);

            var concurrentCredits = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
                commerce.GrantSeasonVoucherAsync(
                    account.AccountId,
                    season.SeasonId,
                    42,
                    "extraction_run",
                    runId.ToString("N"),
                    $"extraction-credit-{runId:N}",
                    "extraction-settlement",
                    $"撤离结算 {runId:N}",
                    cancellationToken)));
            Assert(concurrentCredits.Count(result => result.Created) == 1,
                "Concurrent credit retries created more than one ledger entry.");
            Assert(concurrentCredits.All(result =>
                    !result.IdempotencyConflict && result.ErrorCode is null),
                "An exact credit retry was not treated as an idempotent replay.");

            var conflictingPayload = await commerce.GrantSeasonVoucherAsync(
                account.AccountId,
                season.SeasonId,
                42,
                "extraction_run",
                runId.ToString("N"),
                $"extraction-credit-{runId:N}",
                "manual-reconciliation",
                "different payload",
                cancellationToken);
            Assert(conflictingPayload.IdempotencyConflict,
                "A changed credit payload unexpectedly reused the settlement key.");

            var legacyManualRunId = Guid.NewGuid();
            var legacyManualCredit = await commerce.GrantSeasonVoucherAsync(
                account.AccountId,
                season.SeasonId,
                5,
                "extraction_run",
                legacyManualRunId.ToString("N"),
                $"extraction-credit-{legacyManualRunId:N}",
                "manual-reconciliation",
                "legacy operator note",
                cancellationToken);
            Assert(legacyManualCredit.Created, "Legacy manual credit setup failed.");
            var discoveredLegacyCredit = await commerce.FindLedgerEntryByReferenceAsync(
                account.AccountId,
                ExtractionCurrency.SeasonVoucher,
                season.SeasonId,
                "extraction_run",
                legacyManualRunId.ToString("N"),
                cancellationToken);
            Assert(discoveredLegacyCredit?.Delta == 5,
                "Legacy manual credit could not be recovered by immutable source reference.");

            var ledger = await repository.GetLedgerAsync(
                account.AccountId,
                season.SeasonId,
                100,
                cancellationToken);
            Assert(ledger.Count == 2 && ledger.Count(entry => entry.Delta == 42) == 1,
                "Idempotent credit produced an incorrect ledger.");
        }

        using var reopened = new SqliteExtractionRepository(directory);
        var replayAfterRestart = await new ExtractionCommerceService(reopened).GrantSeasonVoucherAsync(
            accountId,
            seasonId,
            42,
            "extraction_run",
            runId.ToString("N"),
            $"extraction-credit-{runId:N}",
            "extraction-settlement",
            $"撤离结算 {runId:N}",
            cancellationToken);
        Assert(!replayAfterRestart.Created && !replayAfterRestart.IdempotencyConflict,
            "Durable wallet idempotency did not survive restart.");
        var wallet = await reopened.GetWalletAsync(accountId, seasonId, cancellationToken);
        Assert(wallet.SeasonVoucher.Balance == 47, "Wallet was credited more than once after restart.");
    });
}

static async Task VerifyAtomicCreditAndCrashRecoveryAsync(CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        using var fixture = CreateStore(directory);
        var identity = await CreateEconomyIdentityAsync(fixture.Repository, cancellationToken);
        var quote = await fixture.Store.CreateQuoteAsync(
            identity.AccountId,
            identity.SeasonId,
            "steam_atomic",
            "zone-1",
            "回收点",
            [new ExtractionLootLine("Leather", "皮革", 5, 2, 10)],
            "snapshot",
            DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken);
        var start = await fixture.Store.StartConsumptionAsync(
            quote.RunId,
            quote.UserId,
            "atomic-credit-key",
            new Dictionary<string, long> { ["Leather"] = 5 },
            cancellationToken);
        var leaseId = start.Run.LeaseId ?? throw new InvalidOperationException("Atomic test lease missing.");
        var removed = await fixture.Store.TryMarkRemovedAsync(
            start.Run.RunId,
            start.Run.Revision,
            leaseId,
            cancellationToken);
        Assert(removed.Applied, "Atomic test run did not reach Removed.");

        var databasePath = Path.Combine(directory, "extraction-commerce.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = $"""
                CREATE TRIGGER fail_atomic_credit
                BEFORE UPDATE ON extraction_settlement_runs
                WHEN OLD.run_id = '{quote.RunId:D}' AND NEW.state = 'Credited'
                BEGIN
                    SELECT RAISE(ABORT, 'injected atomic credit crash');
                END;
                """;
            await trigger.ExecuteNonQueryAsync(cancellationToken);
        }

        var injectedFailure = false;
        try
        {
            _ = await fixture.Store.TryCreditRemovedAsync(
                removed.Run.RunId,
                removed.Run.Revision,
                leaseId,
                cancellationToken);
        }
        catch (SqliteException)
        {
            injectedFailure = true;
        }
        Assert(injectedFailure, "SQLite trigger did not inject the atomic credit failure.");

        fixture.Dispose();
        using var afterFailure = CreateStore(directory);
        var rolledBackRun = await afterFailure.Store.GetAsync(quote.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Atomic test run disappeared after rollback.");
        var rolledBackWallet = await afterFailure.Repository.GetWalletAsync(
            identity.AccountId,
            identity.SeasonId,
            cancellationToken);
        var rolledBackLedger = await afterFailure.Repository.GetLedgerAsync(
            identity.AccountId,
            identity.SeasonId,
            100,
            cancellationToken);
        Assert(rolledBackRun.State == ExtractionSettlementState.Removed,
            "Failed atomic transaction persisted Credited without commit.");
        Assert(rolledBackWallet.SeasonVoucher.Balance == 0 && rolledBackLedger.Count == 0,
            "Failed atomic transaction leaked a wallet credit or ledger entry.");
        Assert(await ScalarCountAsync(databasePath, "extraction_run_credits", cancellationToken) == 0,
            "Failed atomic transaction leaked its unique credit row.");

        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER fail_atomic_credit;";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        var concurrentCredits = await Task.WhenAll(Enumerable.Range(0, 1_000).Select(_ =>
            afterFailure.Store.TryCreditRemovedAsync(
                rolledBackRun.RunId,
                rolledBackRun.Revision,
                rolledBackRun.LeaseId ?? throw new InvalidOperationException("Recovered Removed lease missing."),
                cancellationToken)));
        Assert(concurrentCredits.Count(result => result.Applied) == 1,
            "Concurrent atomic credit attempts committed more than once.");
        var credited = concurrentCredits.Single(result => result.Applied).Run;
        Assert(credited.State == ExtractionSettlementState.Credited,
            "Atomic credit did not durably enter Credited.");
        var wallet = await afterFailure.Repository.GetWalletAsync(
            identity.AccountId,
            identity.SeasonId,
            cancellationToken);
        var ledger = await afterFailure.Repository.GetLedgerAsync(
            identity.AccountId,
            identity.SeasonId,
            100,
            cancellationToken);
        Assert(wallet.SeasonVoucher.Balance == 10 && ledger.Count == 1,
            "Atomic credit did not create exactly one wallet ledger entry.");
        Assert(await ScalarCountAsync(databasePath, "extraction_run_credits", cancellationToken) == 1,
            "Atomic credit unique row count is not one.");

        afterFailure.Dispose();
        using var afterCrash = CreateStore(directory);
        var recoveredCredited = await afterCrash.Store.GetAsync(quote.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Credited run was not restored after restart.");
        Assert(recoveredCredited.State == ExtractionSettlementState.Credited,
            "Restart did not recover the durable Credited state.");
        var settled = await afterCrash.Store.TryMarkSettledAsync(
            recoveredCredited.RunId,
            recoveredCredited.Revision,
            recoveredCredited.LeaseId ?? throw new InvalidOperationException("Credited lease missing."),
            null,
            cancellationToken);
        Assert(settled.Applied && settled.Run.State == ExtractionSettlementState.Settled,
            "Credited -> Settled recovery failed.");
        var finalWallet = await afterCrash.Repository.GetWalletAsync(
            identity.AccountId,
            identity.SeasonId,
            cancellationToken);
        Assert(finalWallet.SeasonVoucher.Balance == 10,
            "Credited recovery duplicated the wallet credit.");
    });
}

static async Task VerifyManualCreditActorAsync(CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        using var fixture = CreateStore(directory);
        var identity = await CreateEconomyIdentityAsync(fixture.Repository, cancellationToken);
        var quote = await fixture.Store.CreateQuoteAsync(
            identity.AccountId,
            identity.SeasonId,
            "steam_manual_actor",
            "zone-1",
            "回收点",
            [new ExtractionLootLine("Bone", "骨头", 4, 3, 12)],
            "manual-actor-snapshot",
            DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken);
        var consuming = await fixture.Store.StartConsumptionAsync(
            quote.RunId,
            quote.UserId,
            "manual-actor-key",
            new Dictionary<string, long> { ["Bone"] = 4 },
            cancellationToken);
        var uncertain = await fixture.Store.TryMarkUncertainAsync(
            consuming.Run.RunId,
            consuming.Run.Revision,
            consuming.Run.LeaseId ?? throw new InvalidOperationException("Manual actor lease missing."),
            "DELETE_UNCERTAIN",
            "operator review required",
            cancellationToken);
        Assert(uncertain.Applied, "Manual actor test did not enter Uncertain.");
        var removed = await fixture.Store.TryBeginManualSettlementAsync(
            uncertain.Run.RunId,
            uncertain.Run.Revision,
            "operator confirmed exact removal",
            "admin:economy-a",
            cancellationToken);
        Assert(removed.Applied && removed.Run.ReconciliationActor == "admin:economy-a",
            "Manual actor was not durably attached before credit.");
        var credited = await fixture.Store.TryCreditRemovedAsync(
            removed.Run.RunId,
            removed.Run.Revision,
            removed.Run.LeaseId ?? throw new InvalidOperationException("Manual actor Removed lease missing."),
            cancellationToken);
        Assert(credited.Applied, "Manual actor credit did not commit.");
        var ledger = await fixture.Repository.GetLedgerAsync(
            identity.AccountId,
            identity.SeasonId,
            10,
            cancellationToken);
        Assert(ledger.Count == 1 && ledger[0].Actor == "admin:economy-a",
            "Manual wallet credit ledger did not preserve the authenticated administrator subject.");
        Assert(ledger[0].Reason.Contains("operator confirmed exact removal", StringComparison.Ordinal),
            "Manual wallet credit ledger omitted the operator reconciliation reason.");
    });
}

static async Task VerifyBoundedSettlementQueueAsync(CancellationToken cancellationToken)
{
    var executor = new ControlledSettlementExecutor();
    using var queueFixture = CreateQueue(executor, capacity: 2, workers: 1);
    var queue = queueFixture.Queue;
    await queue.StartAsync(cancellationToken);
    try
    {
        var runA = Guid.NewGuid();
        var taskA = queue.EnqueueAsync(runA, "steam_a", "queue-key-a", cancellationToken);
        _ = await executor.Started.Reader.ReadAsync(cancellationToken);
        var coalesced = queue.EnqueueAsync(runA, "STEAM_A", "queue-key-a", cancellationToken);
        var runB = Guid.NewGuid();
        var taskB = queue.EnqueueAsync(runB, "steam_b", "queue-key-b", cancellationToken);
        Assert(queue.AdmittedCount == 2, "Queue admission count did not include running and pending work.");

        var playerConflict = await CaptureExtractionErrorAsync(() =>
            queue.EnqueueAsync(Guid.NewGuid(), "steam_a", "different-key", cancellationToken));
        Assert(playerConflict?.Code == "PLAYER_SETTLEMENT_IN_PROGRESS",
            "Different work for one player was not serialized/rejected.");
        var full = await CaptureExtractionErrorAsync(() =>
            queue.EnqueueAsync(Guid.NewGuid(), "steam_c", "queue-key-c", cancellationToken));
        Assert(full?.Code == "SETTLEMENT_QUEUE_FULL" && full.StatusCode == 429,
            "Full settlement queue did not apply deterministic backpressure.");

        executor.Release.Writer.TryWrite(true);
        var firstResults = await Task.WhenAll(taskA, coalesced);
        Assert(firstResults.All(result => result.RunId == runA) && executor.GetCallCount(runA) == 1,
            "Same run/idempotency key was not coalesced to one execution.");
        var startedB = await executor.Started.Reader.ReadAsync(cancellationToken);
        Assert(startedB == runB, "Second player's queued work was not dispatched.");
        executor.Release.Writer.TryWrite(true);
        _ = await taskB;
        Assert(queue.AdmittedCount == 0, "Queue did not release admission after completion.");
    }
    finally
    {
        await queue.StopAsync(cancellationToken);
    }

    var parallelExecutor = new ControlledSettlementExecutor();
    using var parallelFixture = CreateQueue(parallelExecutor, capacity: 4, workers: 2);
    var parallelQueue = parallelFixture.Queue;
    await parallelQueue.StartAsync(cancellationToken);
    try
    {
        var first = parallelQueue.EnqueueAsync(
            Guid.NewGuid(), "steam_parallel_a", "parallel-a", cancellationToken);
        var second = parallelQueue.EnqueueAsync(
            Guid.NewGuid(), "steam_parallel_b", "parallel-b", cancellationToken);
        _ = await parallelExecutor.Started.Reader.ReadAsync(cancellationToken);
        _ = await parallelExecutor.Started.Reader.ReadAsync(cancellationToken);
        Assert(parallelExecutor.MaxActive == 2,
            "Independent players were globally serialized instead of using bounded workers.");
        parallelExecutor.Release.Writer.TryWrite(true);
        parallelExecutor.Release.Writer.TryWrite(true);
        _ = await Task.WhenAll(first, second);
    }
    finally
    {
        await parallelQueue.StopAsync(cancellationToken);
    }
}

static QueueFixture CreateQueue(
    IExtractionSettlementExecutor executor,
    int capacity,
    int workers)
{
    var directory = Path.Combine(Path.GetTempPath(), $"pal-control-queue-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var persistenceOptions = Options.Create(
        new ExtractionPersistenceOptions { DataDirectory = directory });
    var operationGate = new ExtractionOperationGate(
        persistenceOptions,
        new TestWebHostEnvironment(directory));
    var queue = new ExtractionSettlementQueue(
        executor,
        operationGate,
        Options.Create(new ExtractionModeOptions
        {
            SettlementQueueCapacity = capacity,
            SettlementWorkerCount = workers,
            SettlementQueueOperationTimeoutSeconds = 30
        }),
        NullLogger<ExtractionSettlementQueue>.Instance);
    return new QueueFixture(queue, directory);
}

static async Task<ExtractionModeException?> CaptureExtractionErrorAsync(Func<Task> action)
{
    try
    {
        await action();
        return null;
    }
    catch (ExtractionModeException exception)
    {
        return exception;
    }
}

static async Task<long> ScalarCountAsync(
    string databasePath,
    string table,
    CancellationToken cancellationToken)
{
    await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT COUNT(*) FROM {table};";
    return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
}

static ExtractionSettlementRun NewRun(
    Guid accountId,
    Guid seasonId,
    ExtractionSettlementState state,
    long totalValue,
    DateTimeOffset timestamp) =>
    new(
        Guid.NewGuid(),
        accountId,
        seasonId,
        $"steam_{accountId:N}",
        "zone-1",
        "回收点",
        state,
        [new ExtractionLootLine("Leather", "皮革", 1, totalValue, totalValue)],
        1,
        totalValue,
        "snapshot",
        state == ExtractionSettlementState.Quoted ? null : new Dictionary<string, long> { ["Leather"] = 1 },
        state == ExtractionSettlementState.Quoted ? null : $"key-{Guid.NewGuid():N}",
        null,
        null,
        timestamp,
        timestamp.AddMinutes(1),
        timestamp,
        state == ExtractionSettlementState.Settled ? timestamp : null);

static StoreFixture CreateStore(string directory)
{
    var repository = new SqliteExtractionRepository(directory);
    var store = new ExtractionRunStore(
        Options.Create(new ExtractionPersistenceOptions { DataDirectory = directory }),
        new TestWebHostEnvironment(directory),
        repository);
    return new StoreFixture(repository, store);
}

static async Task<(Guid AccountId, Guid SeasonId)> CreateEconomyIdentityAsync(
    SqliteExtractionRepository repository,
    CancellationToken cancellationToken)
{
    var now = DateTimeOffset.UtcNow;
    var season = await repository.UpsertSeasonAsync(
        null,
        new ExtractionSeasonDefinition(
            "local",
            $"S-{Guid.NewGuid():N}",
            "结算测试周",
            "world-test",
            now.AddDays(-1),
            now.AddDays(1),
            ExtractionSeasonState.Active),
        null,
        cancellationToken);
    var account = await repository.GetOrCreateAccountAsync(
        "steam",
        $"steam_{Guid.NewGuid():N}",
        "Settlement Test",
        cancellationToken);
    return (account.AccountId, season.SeasonId);
}

static async Task WithStoreDirectoryAsync(Func<string, Task> test)
{
    var directory = Path.Combine(Path.GetTempPath(), $"pal-control-settlement-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        await test(directory);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "PalControl.SettlementSagaHarness";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRootPath;
    public string EnvironmentName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = contentRootPath;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class StoreFixture(
    SqliteExtractionRepository repository,
    ExtractionRunStore store) : IDisposable
{
    private bool _disposed;

    public SqliteExtractionRepository Repository { get; } = repository;
    public ExtractionRunStore Store { get; } = store;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Store.Dispose();
        Repository.Dispose();
    }
}

internal sealed class ControlledSettlementExecutor : IExtractionSettlementExecutor
{
    private int _active;
    private int _callCount;
    private int _maxActive;
    private readonly ConcurrentDictionary<Guid, int> _callsByRun = [];

    public Channel<Guid> Started { get; } = Channel.CreateUnbounded<Guid>();
    public Channel<bool> Release { get; } = Channel.CreateUnbounded<bool>();
    public int CallCount => Volatile.Read(ref _callCount);
    public int MaxActive => Volatile.Read(ref _maxActive);
    public int GetCallCount(Guid runId) => _callsByRun.GetValueOrDefault(runId);

    public async Task<ExtractionSettlementRun> ExecuteSettlementAsync(
        Guid runId,
        string userId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        _callsByRun.AddOrUpdate(runId, 1, static (_, count) => checked(count + 1));
        var active = Interlocked.Increment(ref _active);
        while (true)
        {
            var maximum = Volatile.Read(ref _maxActive);
            if (active <= maximum ||
                Interlocked.CompareExchange(ref _maxActive, active, maximum) == maximum)
            {
                break;
            }
        }
        Started.Writer.TryWrite(runId);
        try
        {
            _ = await Release.Reader.ReadAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            return new ExtractionSettlementRun(
                runId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                userId,
                "zone-1",
                "回收点",
                ExtractionSettlementState.Settled,
                [new ExtractionLootLine("Leather", "皮革", 1, 1, 1)],
                1,
                1,
                "queue",
                null,
                idempotencyKey,
                null,
                null,
                now,
                now,
                now,
                now);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

internal sealed class QueueFixture(
    ExtractionSettlementQueue queue,
    string directory) : IDisposable
{
    private bool _disposed;

    public ExtractionSettlementQueue Queue { get; } = queue;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Queue.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}

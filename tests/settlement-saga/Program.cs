using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var cancellationToken = CancellationToken.None;
await VerifyUnpagedStatisticsAndLegacyJsonAsync(cancellationToken);
await VerifyCasAndTerminalMonotonicityAsync(cancellationToken);
await VerifyConcurrentManualResolutionAsync(cancellationToken);
await VerifyExpiredLeaseTakeoverAsync(cancellationToken);
await VerifyWalletCreditIdempotencyAsync(cancellationToken);
Console.WriteLine("PASS: extraction settlement revision, lease, CAS, monotonicity, ledger idempotency, and statistics checks.");

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
        }
        await File.WriteAllTextAsync(
            Path.Combine(directory, "extraction-runs.json"),
            json.ToJsonString(options),
            cancellationToken);

        using var store = CreateStore(directory);
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
        using var store = CreateStore(directory);
        var accountId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
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

        var settled = await store.TryMarkSettledAsync(
            removed.Run.RunId,
            removed.Run.Revision,
            leaseId,
            null,
            cancellationToken);
        Assert(settled.Applied && settled.Run.State == ExtractionSettlementState.Settled, "Removed -> Settled failed.");

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
        store.Dispose();
        using var reopened = CreateStore(directory);
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
        using var store = CreateStore(directory);
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
            cancellationToken);
        var failTask = store.TryMarkManuallyFailedAsync(
            uncertain.Run.RunId,
            uncertain.Run.Revision,
            "operator confirmed no removal",
            cancellationToken);
        var results = await Task.WhenAll(settleTask, failTask);
        Assert(results.Count(result => result.Applied) == 1, "Two competing manual resolutions both committed.");
        var current = await store.GetAsync(uncertain.Run.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Manual test run disappeared.");
        Assert(current.State is ExtractionSettlementState.Removed or ExtractionSettlementState.Failed,
            "Manual resolution ended in an invalid state.");
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

        using var store = CreateStore(directory);
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

static ExtractionRunStore CreateStore(string directory) =>
    new(
        Options.Create(new ExtractionPersistenceOptions { DataDirectory = directory }),
        new TestWebHostEnvironment(directory));

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

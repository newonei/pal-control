using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

const int HistoryRunCount = 10_000;
const int HistoricalWeekCount = 52;
const int ConcurrentSettlementCount = 256;

var cancellationToken = CancellationToken.None;
var now = DateTimeOffset.UtcNow;
var seasons = Enumerable.Range(1, HistoricalWeekCount)
    .Select(index => DeterministicGuid(1, index))
    .ToArray();
var initialRuns = Enumerable.Range(0, HistoryRunCount)
    .Select(index => NewHistoricalRun(index, seasons, now))
    .ToArray();
var settlementRuns = initialRuns.Take(ConcurrentSettlementCount).ToArray();
var selectableSource = initialRuns[^1];

Assert(initialRuns.Length == HistoryRunCount, "The capacity fixture does not contain 10,000 runs.");
Assert(initialRuns.Select(run => run.SeasonId).Distinct().Count() == HistoricalWeekCount,
    "The capacity fixture does not span 52 weekly seasons.");
Assert(initialRuns.Max(run => run.QuotedAt) - initialRuns.Min(run => run.QuotedAt) >=
       TimeSpan.FromDays(7 * (HistoricalWeekCount - 1)),
    "The capacity fixture does not span at least 52 weekly timestamps.");
Assert(settlementRuns.All(run => run.State == ExtractionSettlementState.Removed),
    "Concurrent capacity targets must start in Removed.");
Assert(selectableSource.State == ExtractionSettlementState.Quoted,
    "The atomic batch fixture must contain one selectable quote.");

var directory = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-run-store-capacity-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    var persistence = new CommitTrackingPersistence(initialRuns);
    using var store = new ExtractionRunStore(
        Options.Create(new ExtractionPersistenceOptions { DataDirectory = directory }),
        new CapacityWebHostEnvironment(directory),
        persistence);

    var cache = GetRunCache(store);
    var originalCache = cache.ToDictionary(pair => pair.Key, pair => pair.Value);
    Assert(cache.Count == HistoryRunCount, "The run store did not load all 10,000 historical runs.");

    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var stopwatch = Stopwatch.StartNew();

    // Launch each phase concurrently. ExtractionRunStore must serialize the durable
    // operations through its gate without cloning or writing unrelated history.
    var acquired = await Task.WhenAll(settlementRuns.Select(run =>
        store.TryAcquireRecoveryLeaseAsync(
            run.RunId,
            run.Revision,
            "capacity-harness",
            cancellationToken)));
    Assert(acquired.All(result => result.Applied),
        "At least one concurrent recovery lease was not acquired.");

    var credited = await Task.WhenAll(acquired.Select(result =>
        store.TryCreditRemovedAsync(
            result.Run.RunId,
            result.Run.Revision,
            result.Run.LeaseId ?? throw new InvalidOperationException(
                "A capacity recovery lease was not returned."),
            cancellationToken)));
    Assert(credited.All(result =>
            result.Applied && result.Run.State == ExtractionSettlementState.Credited),
        "At least one concurrent atomic credit did not commit.");

    var settled = await Task.WhenAll(credited.Select(result =>
        store.TryMarkSettledAsync(
            result.Run.RunId,
            result.Run.Revision,
            result.Run.LeaseId ?? throw new InvalidOperationException(
                "An atomic credit did not retain its settlement lease."),
            reconciliationReason: null,
            cancellationToken)));
    stopwatch.Stop();
    var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

    Assert(settled.All(result =>
            result.Applied &&
            result.Run.State == ExtractionSettlementState.Settled &&
            result.Run.Revision == 4 &&
            result.Run.LeaseId is null),
        "At least one concurrent settlement did not reach its terminal state.");
    Assert(ReferenceEquals(cache, GetRunCache(store)),
        "The run-store dictionary was replaced instead of being updated incrementally.");
    Assert(cache.Count == HistoryRunCount,
        "Concurrent updates unexpectedly changed the historical run count.");

    var settlementIds = settlementRuns.Select(run => run.RunId).ToHashSet();
    var untouched = originalCache
        .Where(pair => !settlementIds.Contains(pair.Key))
        .ToArray();
    Assert(untouched.Length == HistoryRunCount - ConcurrentSettlementCount,
        "The untouched-reference proof covers the wrong number of historical runs.");
    Assert(untouched.All(pair => ReferenceEquals(pair.Value, cache[pair.Key])),
        "A concurrent single-run write cloned at least one unrelated historical run.");
    Assert(settlementRuns.All(run => !ReferenceEquals(originalCache[run.RunId], cache[run.RunId])),
        "A changed run did not replace its own cache value.");

    var singleRunBatches = persistence.SuccessfulWriteBatches.ToArray();
    Assert(singleRunBatches.Length == ConcurrentSettlementCount * 2,
        "Concurrent settlements produced an unexpected number of normal write batches.");
    Assert(singleRunBatches.All(batch =>
            batch.WriteCount == 1 && batch.UpdateCount == 1 && batch.InsertCount == 0),
        "A single-run settlement wrote more than its one changed row.");
    Assert(persistence.CreditCommitCount == ConcurrentSettlementCount,
        "Concurrent settlement did not issue exactly one atomic credit per run.");
    Assert(persistence.MaxConcurrentCalls == 1,
        "The run-store gate allowed persistence calls to overlap.");

    // A failed two-row selection batch must leave the cache entirely unchanged.
    // Capacity is reserved before persistence, so retrying an insert has no
    // allocation/failure window after the durable commit.
    var sourceBeforeFailure = cache[selectableSource.RunId];
    var countBeforeFailure = cache.Count;
    persistence.FailNextWrite();
    try
    {
        _ = await store.CreateSelectedQuoteAsync(
            selectableSource.RunId,
            selectableSource.Revision,
            selectableSource.AccountId,
            selectableSource.SeasonId,
            selectableSource.UserId,
            "capacity-selection-0001",
            [new ExtractionQuoteSelectionLine("Leather", 1)],
            currentContentVersionId: null,
            currentContentHash: null,
            cancellationToken);
        throw new InvalidOperationException("The injected persistence failure was not observed.");
    }
    catch (InjectedCommitFailureException)
    {
        // Expected: no SQLite commit means no cache update.
    }

    Assert(cache.Count == countBeforeFailure,
        "A failed selection batch inserted a child run into memory.");
    Assert(ReferenceEquals(sourceBeforeFailure, cache[selectableSource.RunId]),
        "A failed selection batch changed its source run in memory.");
    Assert(persistence.FailedWriteCount == 1 && persistence.LastFailedWriteCount == 2,
        "The injected failure did not cover the complete two-row batch.");

    var selection = await store.CreateSelectedQuoteAsync(
        selectableSource.RunId,
        selectableSource.Revision,
        selectableSource.AccountId,
        selectableSource.SeasonId,
        selectableSource.UserId,
        "capacity-selection-0001",
        [new ExtractionQuoteSelectionLine("Leather", 1)],
        currentContentVersionId: null,
        currentContentHash: null,
        cancellationToken);
    Assert(selection.Created && !selection.IdempotentReplay && !selection.IdempotencyConflict,
        "The successful retry did not create a selected quote.");
    Assert(ReferenceEquals(cache, GetRunCache(store)),
        "The successful insert replaced the run-store dictionary.");
    Assert(cache.Count == HistoryRunCount + 1,
        "The successful selection batch did not add exactly one child run.");
    Assert(!ReferenceEquals(sourceBeforeFailure, cache[selectableSource.RunId]),
        "The committed selection batch did not replace its source run.");
    Assert(cache.ContainsKey(selection.Run.RunId),
        "The committed selection batch did not cache its child run.");

    var allSuccessfulBatches = persistence.SuccessfulWriteBatches.ToArray();
    var selectionBatch = allSuccessfulBatches[^1];
    Assert(selectionBatch.WriteCount == 2 &&
           selectionBatch.UpdateCount == 1 &&
           selectionBatch.InsertCount == 1,
        "Source cancellation and child insertion were not one atomic two-row batch.");
    Assert(untouched
            .Where(pair => pair.Key != selectableSource.RunId)
            .All(pair => ReferenceEquals(pair.Value, cache[pair.Key])),
        "The two-row selection batch cloned unrelated historical runs.");

    // Public reads must remain detached even though the internal write path is now
    // incremental and reuses the prepared changed-run value after commit.
    var exposed = await store.GetAsync(selection.Run.RunId, cancellationToken)
        ?? throw new InvalidOperationException("The selected child run disappeared.");
    var exposedItems = exposed.Items as ExtractionLootLine[]
        ?? throw new InvalidOperationException(
            "The public run clone did not expose its expected detached array.");
    exposedItems[0] = exposedItems[0] with { DisplayName = "mutated outside store" };
    var reread = await store.GetAsync(selection.Run.RunId, cancellationToken)
        ?? throw new InvalidOperationException("The selected child run disappeared after mutation.");
    Assert(reread.Items[0].DisplayName != "mutated outside store",
        "A caller mutated the run-store cache through a public read.");

    Console.WriteLine(
        "PASS: incremental run store kept {0:N0} runs across {1} weeks; " +
        "completed {2:N0} concurrent settlements as {3:N0} one-row writes plus " +
        "{2:N0} atomic credits in {4:N1} ms ({5:N1} MiB allocated); " +
        "preserved {6:N0} untouched references and atomic two-row rollback/retry.",
        HistoryRunCount,
        HistoricalWeekCount,
        ConcurrentSettlementCount,
        ConcurrentSettlementCount * 2,
        stopwatch.Elapsed.TotalMilliseconds,
        allocatedBytes / 1024d / 1024d,
        untouched.Length);
}
finally
{
    Directory.Delete(directory, recursive: true);
}

static ExtractionSettlementRun NewHistoricalRun(
    int index,
    IReadOnlyList<Guid> seasons,
    DateTimeOffset now)
{
    var accountId = DeterministicGuid(2, index % 1_000 + 1);
    var seasonId = seasons[index % seasons.Count];
    var isSettlementTarget = index < ConcurrentSettlementCount;
    var isSelectableSource = index == HistoryRunCount - 1;
    var state = isSettlementTarget
        ? ExtractionSettlementState.Removed
        : isSelectableSource
            ? ExtractionSettlementState.Quoted
            : ExtractionSettlementState.Settled;
    var weeklyTimestamp = now
        .AddDays(-7 * (index % HistoricalWeekCount))
        .AddTicks(-(index / HistoricalWeekCount));
    var quotedAt = isSelectableSource ? now : weeklyTimestamp;
    var items = new[]
    {
        new ExtractionLootLine("Leather", "Leather", 2, 5, 10)
    };
    return new ExtractionSettlementRun(
        DeterministicGuid(3, index + 1),
        accountId,
        seasonId,
        $"steam_{accountId:N}",
        "zone-capacity",
        "Capacity Zone",
        state,
        items,
        2,
        10,
        $"snapshot-{index:D5}",
        state == ExtractionSettlementState.Quoted
            ? null
            : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["Leather"] = 2
            },
        state == ExtractionSettlementState.Quoted ? null : $"settlement-{index:D5}",
        null,
        null,
        quotedAt,
        isSelectableSource ? now.AddHours(1) : quotedAt.AddMinutes(5),
        quotedAt,
        state == ExtractionSettlementState.Settled ? quotedAt : null)
    {
        Revision = 1,
        StateChangedAt = quotedAt
    };
}

static Guid DeterministicGuid(int kind, int value) =>
    new(
        kind,
        unchecked((short)(value >> 16)),
        unchecked((short)value),
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        1);

static Dictionary<Guid, ExtractionSettlementRun> GetRunCache(ExtractionRunStore store)
{
    var field = typeof(ExtractionRunStore).GetField(
            "_runs",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ExtractionRunStore._runs was not found.");
    return field.GetValue(store) as Dictionary<Guid, ExtractionSettlementRun>
        ?? throw new InvalidOperationException("ExtractionRunStore._runs has an unexpected type.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record WriteBatchObservation(
    int WriteCount,
    int UpdateCount,
    int InsertCount);

internal sealed class CommitTrackingPersistence(
    IReadOnlyList<ExtractionSettlementRun> initialRuns) : IExtractionSettlementPersistence
{
    private readonly IReadOnlyList<ExtractionSettlementRun> _initialRuns = initialRuns;
    private readonly ConcurrentQueue<WriteBatchObservation> _successfulWriteBatches = [];
    private int _activeCalls;
    private int _creditCommitCount;
    private int _failNextWrite;
    private int _failedWriteCount;
    private int _lastFailedWriteCount;
    private int _maxConcurrentCalls;

    public IReadOnlyCollection<WriteBatchObservation> SuccessfulWriteBatches =>
        _successfulWriteBatches.ToArray();
    public int CreditCommitCount => Volatile.Read(ref _creditCommitCount);
    public int FailedWriteCount => Volatile.Read(ref _failedWriteCount);
    public int LastFailedWriteCount => Volatile.Read(ref _lastFailedWriteCount);
    public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

    public IReadOnlyList<ExtractionSettlementRun> LoadAndMigrateSettlementRuns(
        string legacyJsonPath) => _initialRuns;

    public async Task PersistSettlementRunWritesAsync(
        IReadOnlyCollection<ExtractionSettlementRunWrite> writes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        cancellationToken.ThrowIfCancellationRequested();
        var active = Interlocked.Increment(ref _activeCalls);
        UpdateMaximum(ref _maxConcurrentCalls, active);
        try
        {
            await Task.Yield();
            if (Interlocked.Exchange(ref _failNextWrite, 0) == 1)
            {
                Interlocked.Increment(ref _failedWriteCount);
                Volatile.Write(ref _lastFailedWriteCount, writes.Count);
                throw new InjectedCommitFailureException();
            }
            _successfulWriteBatches.Enqueue(new WriteBatchObservation(
                writes.Count,
                writes.Count(write => write.Expected is not null),
                writes.Count(write => write.Expected is null)));
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    public async Task<ExtractionRunCreditCommit> CreditRemovedRunAsync(
        ExtractionSettlementRun expectedRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedRun);
        cancellationToken.ThrowIfCancellationRequested();
        var active = Interlocked.Increment(ref _activeCalls);
        UpdateMaximum(ref _maxConcurrentCalls, active);
        try
        {
            await Task.Yield();
            if (expectedRun.State != ExtractionSettlementState.Removed ||
                expectedRun.LeaseId is null)
            {
                throw new InvalidOperationException(
                    "The capacity persistence received an invalid atomic credit request.");
            }
            var timestamp = DateTimeOffset.UtcNow;
            var credited = expectedRun with
            {
                State = ExtractionSettlementState.Credited,
                Revision = checked(expectedRun.Revision + 1),
                StateChangedAt = timestamp,
                UpdatedAt = timestamp,
                LastHeartbeatAt = timestamp,
                LeaseExpiresAt = timestamp.Add(ExtractionRunStore.SettlementLeaseDuration)
            };
            var creditIndex = Interlocked.Increment(ref _creditCommitCount);
            var ledger = new WalletLedgerEntry(
                DeterministicCreditGuid(creditIndex),
                expectedRun.AccountId,
                ExtractionCurrency.SeasonVoucher,
                expectedRun.SeasonId,
                expectedRun.TotalValue,
                expectedRun.TotalValue,
                "capacity settlement",
                "extraction_run",
                expectedRun.RunId.ToString("N"),
                "system:capacity-harness",
                timestamp);
            return new ExtractionRunCreditCommit(credited, ledger, CreditCreated: true);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    public void FailNextWrite() => Volatile.Write(ref _failNextWrite, 1);

    private static Guid DeterministicCreditGuid(int value) =>
        new(
            4,
            unchecked((short)(value >> 16)),
            unchecked((short)value),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            1);

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref target);
            if (candidate <= observed ||
                Interlocked.CompareExchange(ref target, candidate, observed) == observed)
            {
                return;
            }
        }
    }
}

internal sealed class InjectedCommitFailureException : IOException
{
    public InjectedCommitFailureException()
        : base("Injected persistence failure before the commit point.")
    {
    }
}

internal sealed class CapacityWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "PalControl.RunStoreCapacity.Harness";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRootPath;
    public string EnvironmentName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = contentRootPath;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

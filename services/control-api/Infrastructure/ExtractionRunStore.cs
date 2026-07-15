using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

[JsonConverter(typeof(JsonStringEnumConverter<ExtractionSettlementState>))]
public enum ExtractionSettlementState
{
    Quoted,
    Consuming,
    Removed,
    Credited,
    Settled,
    Failed,
    Uncertain,
    Expired,
    Cancelled
}

public sealed record ExtractionLootLine(
    string ItemId,
    string DisplayName,
    int Quantity,
    long UnitValue,
    long TotalValue);

public sealed record ExtractionSettlementRun(
    Guid RunId,
    Guid AccountId,
    Guid SeasonId,
    string UserId,
    string ZoneId,
    string ZoneName,
    ExtractionSettlementState State,
    IReadOnlyList<ExtractionLootLine> Items,
    int ItemCount,
    long TotalValue,
    string QuoteSnapshotHash,
    IReadOnlyDictionary<string, long>? PreDeleteTotals,
    string? SettlementIdempotencyKey,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset QuotedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SettledAt)
{
    // These properties were added after the original JSON format shipped.
    // Missing properties deserialize to zero/null so existing stores remain valid.
    public long Revision { get; init; }
    public DateTimeOffset? StateChangedAt { get; init; }
    public Guid? LeaseId { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public int AttemptCount { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public string? ReconciliationActor { get; init; }
    public ExtractionNativeInventoryQuoteSnapshot? NativeInventorySnapshot { get; init; }
    public string? SettlementRequestHash { get; init; }
    public ExtractionNativeConsumeReceipt? NativeConsumeReceipt { get; init; }
    public Guid? ContentVersionId { get; init; }
    public string? ContentHash { get; init; }
    public DateOnly? ContentBusinessDate { get; init; }
    public string? ContentRulesVersion { get; init; }
    public string? RotationSeed { get; init; }
    public int ZoneYieldMultiplierBasisPoints { get; init; } = 10_000;
    public bool Hotspot { get; init; }
}

public sealed record ExtractionConsumptionStart(
    ExtractionSettlementRun Run,
    bool Started,
    bool IdempotencyConflict);

public sealed record ExtractionRunMutation(
    ExtractionSettlementRun Run,
    bool Applied);

public sealed record ExtractionSeasonStatistics(
    int SettledCount,
    int FailedCount,
    int UncertainCount,
    long SettledTotalValue);

public sealed record ExtractionRunCreditCommit(
    ExtractionSettlementRun Run,
    WalletLedgerEntry LedgerEntry,
    bool CreditCreated);

public interface IExtractionSettlementPersistence
{
    IReadOnlyList<ExtractionSettlementRun> LoadAndMigrateSettlementRuns(string legacyJsonPath);

    Task PersistSettlementRunsAsync(
        IReadOnlyCollection<ExtractionSettlementRun> runs,
        CancellationToken cancellationToken);

    Task<ExtractionRunCreditCommit> CreditRemovedRunAsync(
        ExtractionSettlementRun expectedRun,
        CancellationToken cancellationToken);
}

public sealed class ExtractionRunStore : IDisposable
{
    public static readonly TimeSpan SettlementLeaseDuration = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, ExtractionSettlementRun> _runs = [];
    private readonly IExtractionSettlementPersistence _persistence;
    private readonly string _legacyPath;
    private bool _disposed;

    public ExtractionRunStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        IExtractionSettlementPersistence persistence)
    {
        _persistence = persistence;
        var configured = options.Value.DataDirectory;
        var directory = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(directory);
        _legacyPath = Path.Combine(directory, "extraction-runs.json");
        Load();
    }

    public async Task<ExtractionSettlementRun> CreateQuoteAsync(
        Guid accountId,
        Guid seasonId,
        string userId,
        string zoneId,
        string zoneName,
        IReadOnlyList<ExtractionLootLine> items,
        string snapshotHash,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken,
        ExtractionNativeInventoryQuoteSnapshot? nativeInventorySnapshot = null,
        EconomyRuntimeContent? runtimeContent = null,
        int zoneYieldMultiplierBasisPoints = 10_000,
        bool hotspot = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (nativeInventorySnapshot is not null &&
            !string.Equals(
                snapshotHash,
                ExtractionNativeInventoryCanonicalizer.Hash(nativeInventorySnapshot),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Native quote snapshot does not match the supplied snapshot hash.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var snapshot = CloneSnapshot();
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in snapshot.Values.Where(run =>
                         run.AccountId == accountId &&
                         run.State == ExtractionSettlementState.Quoted &&
                         run.ExpiresAt <= now).ToArray())
            {
                snapshot[expired.RunId] = expired with
                {
                    State = ExtractionSettlementState.Expired,
                    Revision = checked(expired.Revision + 1),
                    StateChangedAt = now,
                    UpdatedAt = now,
                    ErrorCode = "QUOTE_EXPIRED",
                    ErrorMessage = "资源兑换报价已过期。"
                };
            }
            var blocking = snapshot.Values.FirstOrDefault(run =>
                run.AccountId == accountId &&
                run.State is ExtractionSettlementState.Consuming or
                    ExtractionSettlementState.Removed or
                    ExtractionSettlementState.Credited or
                    ExtractionSettlementState.Uncertain);
            if (blocking is not null)
            {
                throw new ExtractionModeException(
                    "EXTRACTION_RECONCILIATION_REQUIRED",
                    $"已有资源兑换记录 {blocking.RunId:N} 等待结算或人工对账。",
                    StatusCodes.Status409Conflict);
            }
            foreach (var previous in snapshot.Values.Where(run =>
                         run.AccountId == accountId &&
                         run.State == ExtractionSettlementState.Quoted).ToArray())
            {
                snapshot[previous.RunId] = previous with
                {
                    State = ExtractionSettlementState.Cancelled,
                    Revision = checked(previous.Revision + 1),
                    StateChangedAt = now,
                    UpdatedAt = now,
                    ErrorCode = "QUOTE_REPLACED",
                    ErrorMessage = "已生成新的资源兑换报价。"
                };
            }
            var totalValue = checked(items.Sum(item => item.TotalValue));
            var itemCount = checked(items.Sum(item => item.Quantity));
            var run = new ExtractionSettlementRun(
                Guid.NewGuid(),
                accountId,
                seasonId,
                userId,
                zoneId,
                zoneName,
                ExtractionSettlementState.Quoted,
                items.ToArray(),
                itemCount,
                totalValue,
                snapshotHash,
                null,
                null,
                null,
                null,
                now,
                expiresAt,
                now,
                null)
            {
                Revision = 1,
                StateChangedAt = now,
                NativeInventorySnapshot = CloneNativeSnapshot(nativeInventorySnapshot),
                ContentVersionId = runtimeContent?.Version.VersionId,
                ContentHash = runtimeContent?.Version.ContentHash,
                ContentBusinessDate = runtimeContent?.Version.BusinessDate,
                ContentRulesVersion = runtimeContent?.Version.RulesVersion,
                RotationSeed = runtimeContent?.Rotation.Seed,
                ZoneYieldMultiplierBasisPoints = zoneYieldMultiplierBasisPoints,
                Hotspot = hotspot
            };
            snapshot[run.RunId] = run;
            await PersistAndSwapSnapshotAsync(snapshot, cancellationToken);
            return Clone(run);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionSettlementRun?> GetAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _runs.TryGetValue(runId, out var run) ? Clone(run) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ExtractionSettlementRun>> ListAsync(
        Guid? accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _runs.Values
                .Where(run => (accountId is null || run.AccountId == accountId) &&
                    (seasonId is null || run.SeasonId == seasonId))
                .OrderByDescending(run => run.QuotedAt)
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionSeasonStatistics> GetSeasonStatisticsAsync(
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var settled = 0;
            var failed = 0;
            var uncertain = 0;
            long settledTotalValue = 0;
            foreach (var run in _runs.Values.Where(run =>
                         run.AccountId == accountId && run.SeasonId == seasonId))
            {
                checked
                {
                    switch (run.State)
                    {
                        case ExtractionSettlementState.Settled:
                            settled++;
                            settledTotalValue += run.TotalValue;
                            break;
                        case ExtractionSettlementState.Failed:
                            failed++;
                            break;
                        case ExtractionSettlementState.Uncertain:
                            uncertain++;
                            break;
                    }
                }
            }
            return new ExtractionSeasonStatistics(
                settled,
                failed,
                uncertain,
                settledTotalValue);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ExtractionSettlementRun>> ListRolloverBlockingAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var now = DateTimeOffset.UtcNow;
            return _runs.Values
                .Where(run =>
                    run.State is ExtractionSettlementState.Consuming or
                        ExtractionSettlementState.Removed or
                        ExtractionSettlementState.Credited or
                        ExtractionSettlementState.Uncertain ||
                    run.State == ExtractionSettlementState.Quoted && run.ExpiresAt > now)
                .OrderBy(run => run.UpdatedAt)
                .ThenBy(run => run.RunId)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ExtractionSettlementRun>> ListRecoverableAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var now = DateTimeOffset.UtcNow;
            return _runs.Values
                .Where(run => run.State is
                        (ExtractionSettlementState.Consuming or
                            ExtractionSettlementState.Removed or
                            ExtractionSettlementState.Credited) &&
                    !HasActiveLease(run, now))
                .OrderBy(run => run.StateChangedAt ?? run.UpdatedAt)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionConsumptionStart> StartConsumptionAsync(
        Guid runId,
        string userId,
        string idempotencyKey,
        IReadOnlyDictionary<string, long> preDeleteTotals,
        CancellationToken cancellationToken,
        string? requestHash = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_runs.TryGetValue(runId, out var run))
            {
                throw new ExtractionModeException(
                    "EXTRACTION_RUN_NOT_FOUND",
                    "资源兑换报价不存在。",
                    StatusCodes.Status404NotFound);
            }
            if (!string.Equals(run.UserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionModeException(
                    "EXTRACTION_RUN_OWNER_MISMATCH",
                    "资源兑换报价不属于该玩家。",
                    StatusCodes.Status403Forbidden);
            }
            var keyOwner = _runs.Values.FirstOrDefault(candidate =>
                candidate.RunId != runId &&
                string.Equals(
                    candidate.SettlementIdempotencyKey,
                    idempotencyKey,
                    StringComparison.Ordinal));
            if (keyOwner is not null)
            {
                return new ExtractionConsumptionStart(Clone(run), false, true);
            }
            if (run.SettlementIdempotencyKey is not null)
            {
                var conflict = !string.Equals(
                        run.SettlementIdempotencyKey,
                        idempotencyKey,
                        StringComparison.Ordinal) ||
                    requestHash is not null &&
                    run.SettlementRequestHash is not null &&
                    !string.Equals(
                        run.SettlementRequestHash,
                        requestHash,
                        StringComparison.Ordinal);
                return new ExtractionConsumptionStart(Clone(run), false, conflict);
            }
            var now = DateTimeOffset.UtcNow;
            if (run.State != ExtractionSettlementState.Quoted || now >= run.ExpiresAt)
            {
                if (run.State == ExtractionSettlementState.Quoted)
                {
                    var snapshot = CloneSnapshot();
                    var expired = snapshot[runId] with
                    {
                        State = ExtractionSettlementState.Expired,
                        Revision = checked(run.Revision + 1),
                        StateChangedAt = now,
                        UpdatedAt = now,
                        ErrorCode = "QUOTE_EXPIRED",
                        ErrorMessage = "资源兑换报价已过期。"
                    };
                    snapshot[runId] = expired;
                    await PersistAndSwapSnapshotAsync(snapshot, cancellationToken);
                }
                throw new ExtractionModeException(
                    "EXTRACTION_QUOTE_NOT_SETTLEABLE",
                    "资源兑换报价已过期或不处于可结算状态。",
                    StatusCodes.Status409Conflict);
            }
            var updatedSnapshot = CloneSnapshot();
            var leaseId = Guid.NewGuid();
            var updated = updatedSnapshot[runId] with
            {
                State = ExtractionSettlementState.Consuming,
                PreDeleteTotals = new Dictionary<string, long>(
                    preDeleteTotals,
                    StringComparer.OrdinalIgnoreCase),
                SettlementIdempotencyKey = idempotencyKey,
                SettlementRequestHash = requestHash,
                Revision = checked(run.Revision + 1),
                StateChangedAt = now,
                UpdatedAt = now,
                LeaseId = leaseId,
                LeaseOwner = "http-settlement",
                LeaseExpiresAt = now.Add(SettlementLeaseDuration),
                AttemptCount = checked(run.AttemptCount + 1),
                LastHeartbeatAt = now
            };
            updatedSnapshot[runId] = updated;
            await PersistAndSwapSnapshotAsync(updatedSnapshot, cancellationToken);
            return new ExtractionConsumptionStart(Clone(updated), true, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionRunMutation> TryRecordNativeConsumeReceiptAsync(
        Guid runId,
        long expectedRevision,
        Guid leaseId,
        ExtractionNativeConsumeReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_runs.TryGetValue(runId, out var run))
            {
                throw new KeyNotFoundException($"Extraction run '{runId}' does not exist.");
            }
            if (run.Revision != expectedRevision ||
                run.State != ExtractionSettlementState.Consuming ||
                run.LeaseId != leaseId ||
                !string.Equals(
                    run.SettlementRequestHash,
                    receipt.RequestHash,
                    StringComparison.Ordinal))
            {
                return new ExtractionRunMutation(Clone(run), false);
            }
            if (run.NativeConsumeReceipt is not null)
            {
                var sameResult = string.Equals(
                        run.NativeConsumeReceipt.RequestHash,
                        receipt.RequestHash,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        run.NativeConsumeReceipt.ResponseHash,
                        receipt.ResponseHash,
                        StringComparison.Ordinal);
                if (!sameResult)
                {
                    throw new InvalidDataException(
                        $"Extraction run '{runId}' already contains a conflicting Native consume receipt.");
                }
                return new ExtractionRunMutation(Clone(run), false);
            }

            var snapshot = CloneSnapshot();
            var updated = snapshot[runId] with
            {
                NativeConsumeReceipt = CloneNativeReceipt(receipt),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            snapshot[runId] = updated;
            await PersistAndSwapSnapshotAsync(snapshot, cancellationToken);
            return new ExtractionRunMutation(Clone(updated), true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ExtractionRunMutation> TryAcquireRecoveryLeaseAsync(
        Guid runId,
        long expectedRevision,
        string leaseOwner,
        CancellationToken cancellationToken) =>
        TryAcquireLeaseAsync(
            runId,
            expectedRevision,
            leaseOwner,
            static state => state is
                ExtractionSettlementState.Consuming or
                    ExtractionSettlementState.Removed or
                    ExtractionSettlementState.Credited,
            cancellationToken);

    public Task<ExtractionRunMutation> TryBeginManualSettlementAsync(
        Guid runId,
        long expectedRevision,
        string reason,
        string actor,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            runId,
            expectedRevision,
            null,
            expectedLeaseOwner: null,
            ExtractionSettlementState.Uncertain,
            ExtractionSettlementState.Removed,
            "MANUALLY_RECONCILED_REMOVED",
            reason,
            acquireLeaseOwner: "manual-reconciliation",
            cancellationToken,
            reconciliationActor: actor);

    public Task<ExtractionRunMutation> TryMarkRemovedAsync(
        Guid runId,
        long expectedRevision,
        Guid leaseId,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            runId,
            expectedRevision,
            leaseId,
            expectedLeaseOwner: "http-settlement",
            ExtractionSettlementState.Consuming,
            ExtractionSettlementState.Removed,
            null,
            null,
            acquireLeaseOwner: null,
            cancellationToken);

    public Task<ExtractionRunMutation> TryMarkRemovedFromRecordedNativeAsync(
        Guid runId,
        long expectedRevision,
        Guid leaseId,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            runId,
            expectedRevision,
            leaseId,
            expectedLeaseOwner: "settlement-recovery",
            ExtractionSettlementState.Consuming,
            ExtractionSettlementState.Removed,
            null,
            null,
            acquireLeaseOwner: null,
            cancellationToken);

    public Task<ExtractionRunMutation> TryMarkFailedAsync(
        Guid runId,
        long expectedRevision,
        Guid leaseId,
        string code,
        string message,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            runId,
            expectedRevision,
            leaseId,
            expectedLeaseOwner: null,
            ExtractionSettlementState.Consuming,
            ExtractionSettlementState.Failed,
            code,
            message,
            acquireLeaseOwner: null,
            cancellationToken);

    public Task<ExtractionRunMutation> TryMarkManuallyFailedAsync(
        Guid runId,
        long expectedRevision,
        string reason,
        string actor,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            runId,
            expectedRevision,
            null,
            expectedLeaseOwner: null,
            ExtractionSettlementState.Uncertain,
            ExtractionSettlementState.Failed,
            "MANUALLY_RECONCILED_FAILED",
            reason,
            acquireLeaseOwner: null,
            cancellationToken,
            reconciliationActor: actor);

    public Task<ExtractionRunMutation> TryMarkUncertainAsync(
        Guid runId,
        long expectedRevision,
        Guid leaseId,
        string code,
        string message,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            runId,
            expectedRevision,
            leaseId,
            expectedLeaseOwner: null,
            ExtractionSettlementState.Consuming,
            ExtractionSettlementState.Uncertain,
            code,
            message,
            acquireLeaseOwner: null,
            cancellationToken);

    public Task<ExtractionRunMutation> TryMarkSettledAsync(
        Guid runId,
        long expectedRevision,
        Guid leaseId,
        string? reconciliationReason,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            runId,
            expectedRevision,
            leaseId,
            expectedLeaseOwner: null,
            ExtractionSettlementState.Credited,
            ExtractionSettlementState.Settled,
            reconciliationReason is null ? null : "MANUALLY_RECONCILED_SETTLED",
            reconciliationReason,
            acquireLeaseOwner: null,
            cancellationToken);

    public async Task<ExtractionRunMutation> TryCreditRemovedAsync(
        Guid runId,
        long expectedRevision,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_runs.TryGetValue(runId, out var run))
            {
                throw new KeyNotFoundException($"Extraction run '{runId}' does not exist.");
            }
            if (run.Revision != expectedRevision ||
                run.State != ExtractionSettlementState.Removed ||
                run.LeaseId != leaseId)
            {
                return new ExtractionRunMutation(Clone(run), false);
            }

            var commit = await _persistence.CreditRemovedRunAsync(run, cancellationToken);
            var snapshot = CloneSnapshot();
            snapshot[runId] = Clone(commit.Run);
            _runs = snapshot;
            return new ExtractionRunMutation(Clone(commit.Run), true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionRunMutation> TryHeartbeatLeaseAsync(
        Guid runId,
        long expectedRevision,
        Guid leaseId,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_runs.TryGetValue(runId, out var run))
            {
                throw new KeyNotFoundException($"Extraction run '{runId}' does not exist.");
            }
            if (run.Revision != expectedRevision ||
                run.LeaseId != leaseId ||
                !string.Equals(run.LeaseOwner, leaseOwner, StringComparison.Ordinal) ||
                run.State is not
                    (ExtractionSettlementState.Consuming or
                        ExtractionSettlementState.Removed or
                        ExtractionSettlementState.Credited))
            {
                return new ExtractionRunMutation(Clone(run), false);
            }

            var now = DateTimeOffset.UtcNow;
            var snapshot = CloneSnapshot();
            var updated = snapshot[runId] with
            {
                UpdatedAt = now,
                LastHeartbeatAt = now,
                LeaseExpiresAt = now.Add(SettlementLeaseDuration)
            };
            snapshot[runId] = updated;
            await PersistAndSwapSnapshotAsync(snapshot, cancellationToken);
            return new ExtractionRunMutation(Clone(updated), true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionRunMutation> TryReleaseLeaseAsync(
        Guid runId,
        long expectedRevision,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_runs.TryGetValue(runId, out var run))
            {
                throw new KeyNotFoundException($"Extraction run '{runId}' does not exist.");
            }
            if (run.Revision != expectedRevision ||
                run.LeaseId != leaseId ||
                run.State is not
                    (ExtractionSettlementState.Consuming or
                        ExtractionSettlementState.Removed or
                        ExtractionSettlementState.Credited))
            {
                return new ExtractionRunMutation(Clone(run), false);
            }

            var now = DateTimeOffset.UtcNow;
            var snapshot = CloneSnapshot();
            var updated = snapshot[runId] with
            {
                Revision = checked(run.Revision + 1),
                UpdatedAt = now,
                LeaseId = null,
                LeaseOwner = null,
                LeaseExpiresAt = null
            };
            snapshot[runId] = updated;
            await PersistAndSwapSnapshotAsync(snapshot, cancellationToken);
            return new ExtractionRunMutation(Clone(updated), true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<ExtractionRunMutation> TryAcquireLeaseAsync(
        Guid runId,
        long expectedRevision,
        string leaseOwner,
        Func<ExtractionSettlementState, bool> stateAllowed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_runs.TryGetValue(runId, out var run))
            {
                throw new KeyNotFoundException($"Extraction run '{runId}' does not exist.");
            }

            var now = DateTimeOffset.UtcNow;
            if (run.Revision != expectedRevision ||
                !stateAllowed(run.State) ||
                HasActiveLease(run, now))
            {
                return new ExtractionRunMutation(Clone(run), false);
            }

            var snapshot = CloneSnapshot();
            var updated = snapshot[runId] with
            {
                Revision = checked(run.Revision + 1),
                StateChangedAt = run.StateChangedAt ?? run.UpdatedAt,
                UpdatedAt = now,
                LeaseId = Guid.NewGuid(),
                LeaseOwner = leaseOwner.Trim(),
                LeaseExpiresAt = now.Add(SettlementLeaseDuration),
                AttemptCount = checked(run.AttemptCount + 1),
                LastHeartbeatAt = now
            };
            snapshot[runId] = updated;
            await PersistAndSwapSnapshotAsync(snapshot, cancellationToken);
            return new ExtractionRunMutation(Clone(updated), true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ExtractionRunMutation> TransitionAsync(
        Guid runId,
        long expectedRevision,
        Guid? expectedLeaseId,
        string? expectedLeaseOwner,
        ExtractionSettlementState expectedState,
        ExtractionSettlementState state,
        string? errorCode,
        string? errorMessage,
        string? acquireLeaseOwner,
        CancellationToken cancellationToken,
        string? reconciliationActor = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_runs.TryGetValue(runId, out var run))
            {
                throw new KeyNotFoundException($"Extraction run '{runId}' does not exist.");
            }
            var now = DateTimeOffset.UtcNow;
            if (run.Revision != expectedRevision || run.State != expectedState)
            {
                return new ExtractionRunMutation(Clone(run), false);
            }
            if (expectedLeaseId is Guid leaseId)
            {
                if (run.LeaseId != leaseId)
                {
                    return new ExtractionRunMutation(Clone(run), false);
                }
            }
            else if (HasActiveLease(run, now))
            {
                return new ExtractionRunMutation(Clone(run), false);
            }
            if (expectedLeaseOwner is not null &&
                !string.Equals(run.LeaseOwner, expectedLeaseOwner, StringComparison.Ordinal))
            {
                return new ExtractionRunMutation(Clone(run), false);
            }
            if (!IsAllowedTransition(run.State, state))
            {
                throw new InvalidOperationException(
                    $"Extraction run transition {run.State} -> {state} is not allowed.");
            }
            if (run.State == ExtractionSettlementState.Consuming &&
                state == ExtractionSettlementState.Removed &&
                run.NativeInventorySnapshot is not null &&
                run.NativeConsumeReceipt?.Disposition !=
                    ExtractionNativeConsumeDisposition.Succeeded)
            {
                return new ExtractionRunMutation(Clone(run), false);
            }

            var keepLease = state is
                ExtractionSettlementState.Removed or ExtractionSettlementState.Credited;
            var newLeaseId = acquireLeaseOwner is null
                ? keepLease ? run.LeaseId : null
                : Guid.NewGuid();
            var newLeaseOwner = acquireLeaseOwner ?? (keepLease ? run.LeaseOwner : null);
            DateTimeOffset? newLeaseExpiry = newLeaseId is null
                ? null
                : now.Add(SettlementLeaseDuration);
            var snapshot = CloneSnapshot();
            var updated = snapshot[runId] with
            {
                State = state,
                Revision = checked(run.Revision + 1),
                StateChangedAt = now,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                UpdatedAt = now,
                SettledAt = state == ExtractionSettlementState.Settled ? now : run.SettledAt,
                LeaseId = newLeaseId,
                LeaseOwner = newLeaseOwner,
                LeaseExpiresAt = newLeaseExpiry,
                AttemptCount = acquireLeaseOwner is null
                    ? run.AttemptCount
                    : checked(run.AttemptCount + 1),
                LastHeartbeatAt = newLeaseId is null ? run.LastHeartbeatAt : now,
                ReconciliationActor = reconciliationActor ?? run.ReconciliationActor
            };
            snapshot[runId] = updated;
            await PersistAndSwapSnapshotAsync(snapshot, cancellationToken);
            return new ExtractionRunMutation(Clone(updated), true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool HasActiveLease(
        ExtractionSettlementRun run,
        DateTimeOffset now) =>
        run.LeaseId is not null && run.LeaseExpiresAt is not null && run.LeaseExpiresAt > now;

    private static bool IsAllowedTransition(
        ExtractionSettlementState source,
        ExtractionSettlementState target) =>
        (source, target) switch
        {
            (ExtractionSettlementState.Consuming, ExtractionSettlementState.Removed) => true,
            (ExtractionSettlementState.Consuming, ExtractionSettlementState.Failed) => true,
            (ExtractionSettlementState.Consuming, ExtractionSettlementState.Uncertain) => true,
            (ExtractionSettlementState.Uncertain, ExtractionSettlementState.Removed) => true,
            (ExtractionSettlementState.Uncertain, ExtractionSettlementState.Failed) => true,
            (ExtractionSettlementState.Credited, ExtractionSettlementState.Settled) => true,
            _ => false
        };

    private void Load()
    {
        var runs = _persistence.LoadAndMigrateSettlementRuns(_legacyPath);
        foreach (var run in runs)
        {
            if (run.RunId == Guid.Empty || !_runs.TryAdd(run.RunId, Clone(run)))
            {
                throw new InvalidDataException("The extraction run store contains duplicate or invalid ids.");
            }
        }
    }

    private async Task PersistSnapshotAsync(
        IReadOnlyDictionary<Guid, ExtractionSettlementRun> snapshot,
        CancellationToken cancellationToken) =>
        await _persistence.PersistSettlementRunsAsync(
            snapshot.Values.OrderBy(run => run.QuotedAt).ToArray(),
            cancellationToken);

    private async Task PersistAndSwapSnapshotAsync(
        Dictionary<Guid, ExtractionSettlementRun> snapshot,
        CancellationToken cancellationToken)
    {
        // The SQLite commit is the persistence point. No cancellable or fallible
        // work may be introduced between it and the in-memory reference swap.
        await PersistSnapshotAsync(snapshot, cancellationToken);
        _runs = snapshot;
    }

    private Dictionary<Guid, ExtractionSettlementRun> CloneSnapshot() =>
        _runs.ToDictionary(
            pair => pair.Key,
            pair => Clone(pair.Value));

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static ExtractionSettlementRun Clone(ExtractionSettlementRun run) => run with
    {
        Items = run.Items.ToArray(),
        PreDeleteTotals = run.PreDeleteTotals is null
            ? null
            : new Dictionary<string, long>(run.PreDeleteTotals, StringComparer.OrdinalIgnoreCase),
        NativeInventorySnapshot = CloneNativeSnapshot(run.NativeInventorySnapshot),
        NativeConsumeReceipt = CloneNativeReceipt(run.NativeConsumeReceipt)
    };

    private static ExtractionNativeInventoryQuoteSnapshot? CloneNativeSnapshot(
        ExtractionNativeInventoryQuoteSnapshot? snapshot) =>
        snapshot is null
            ? null
            : snapshot with
            {
                Containers = snapshot.Containers.Select(container => container with
                {
                    Slots = container.Slots.ToArray()
                }).ToArray()
            };

    private static ExtractionNativeConsumeReceipt? CloneNativeReceipt(
        ExtractionNativeConsumeReceipt? receipt) =>
        receipt is null ? null : receipt with { Items = receipt.Items.ToArray() };
}

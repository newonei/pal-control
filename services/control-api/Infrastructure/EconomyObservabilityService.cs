using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed class EconomyObservabilityOptions
{
    public bool Enabled { get; init; } = true;
    public bool AutoCircuitBreakEnabled { get; init; }
    public int EvaluationIntervalSeconds { get; init; } = 30;
    public int QueueWarningPercent { get; init; } = 75;
    public int QueueCriticalPercent { get; init; } = 100;
    public int MaximumPendingDeliveryAgeSeconds { get; init; } = 300;
    public int MaximumOutboxAgeSeconds { get; init; } = 180;
    public int MaximumUncertainPurchaseCount { get; init; }
    public int MaximumUncertainSettlementCount { get; init; }
    public int IdentityConflictWindowMinutes { get; init; } = 15;
    public int IdentityConflictCircuitThreshold { get; init; } = 5;
    public bool RequireRecentGameBackupForWrites { get; init; }
    public int MaximumGameBackupAgeMinutes { get; init; } = 60;
    public bool RequireRecentEconomyBackupForWrites { get; init; }
    public int MaximumEconomyBackupAgeMinutes { get; init; } = 15;

    public bool IsValid(out string? error)
    {
        if (EvaluationIntervalSeconds is < 5 or > 3600 ||
            QueueWarningPercent is < 1 or > 99 ||
            QueueCriticalPercent is < 2 or > 100 ||
            QueueWarningPercent >= QueueCriticalPercent)
        {
            error = "Economy observability interval and queue percentages are invalid.";
            return false;
        }
        if (MaximumPendingDeliveryAgeSeconds is < 10 or > 86_400 ||
            MaximumOutboxAgeSeconds is < 10 or > 86_400 ||
            MaximumUncertainPurchaseCount is < 0 or > 100_000 ||
            MaximumUncertainSettlementCount is < 0 or > 100_000)
        {
            error = "Economy observability age or uncertain thresholds are invalid.";
            return false;
        }
        if (IdentityConflictWindowMinutes is < 1 or > 1440 ||
            IdentityConflictCircuitThreshold is < 1 or > 100_000 ||
            MaximumGameBackupAgeMinutes is < 1 or > 10_080 ||
            MaximumEconomyBackupAgeMinutes is < 1 or > 10_080)
        {
            error = "Economy observability identity or backup thresholds are invalid.";
            return false;
        }
        error = null;
        return true;
    }
}

public sealed record EconomyQueueObservability(
    bool Ready,
    int Pending,
    int Capacity,
    double UtilizationPercent,
    double? OldestAgeSeconds,
    IReadOnlyDictionary<string, int>? States = null);

public sealed record EconomyUncertainObservability(
    int Orders,
    int Deliveries,
    int DeliveryReceipts,
    int PartialDeliveryReceipts,
    int ResourceSettlements,
    int Outbox);

public sealed record EconomyInvariantObservability(
    int LedgerStreamCount,
    int LedgerMismatchCount,
    int SettlementCreditMismatchCount,
    bool Conserved);

public sealed record EconomyIdentityObservability(
    int StructuralConflictCount,
    int LifetimeRejectedConflictCount,
    int RecentRejectedConflictCount,
    int WindowMinutes,
    bool Consistent);

public sealed record EconomyConsistencyObservability(
    bool Consistent,
    IReadOnlyList<string> BlockerCodes,
    IReadOnlyList<string> PurchaseBlockerCodes,
    IReadOnlyList<string> ResourceExchangeBlockerCodes,
    string? GameVersion,
    bool NativeConnected,
    string? NativeProtocolVersion,
    string? NativeGameBuild,
    string? NativeModVersion);

public sealed record EconomyRuntimeDependencyObservability(
    bool Consistent,
    IReadOnlyList<string> PurchaseBlockerCodes,
    IReadOnlyList<string> ResourceExchangeBlockerCodes);

public sealed record EconomyBackupAgeObservability(
    bool Available,
    DateTimeOffset? LatestCreatedAt,
    double? AgeSeconds,
    double MaximumAgeSeconds,
    bool RequiredForWrites,
    bool Fresh);

public sealed record EconomyObservabilityAlert(
    string Code,
    string Severity,
    bool Active,
    bool AutoCircuit,
    IReadOnlyList<string> Affects,
    double? Value,
    double? Threshold,
    string Message);

public sealed record EconomyCircuitObservability(
    bool WritesEnabled,
    DateTimeOffset UpdatedAt,
    string Source);

public sealed record EconomyCircuitsObservability(
    EconomyCircuitObservability Purchase,
    EconomyCircuitObservability ResourceExchange);

public sealed record EconomyObservabilitySnapshot(
    int SchemaVersion,
    string Status,
    DateTimeOffset CollectedAt,
    string GameplayMode,
    IReadOnlyDictionary<string, EconomyStateMetric> Orders,
    IReadOnlyDictionary<string, EconomyStateMetric> ResourceSettlements,
    IReadOnlyDictionary<string, EconomyStateMetric> Deliveries,
    EconomyQueueObservability DeliveryQueue,
    EconomyQueueObservability ResourceSettlementQueue,
    EconomyQueueObservability Outbox,
    EconomyUncertainObservability Uncertain,
    EconomyInvariantObservability Ledger,
    EconomyIdentityObservability Identity,
    EconomyRuntimeDependencyObservability DependencyConsistency,
    EconomyConsistencyObservability VersionConsistency,
    EconomyConsistencyObservability WorldConsistency,
    EconomyBackupAgeObservability GameBackup,
    EconomyBackupAgeObservability EconomyBackup,
    EconomyCircuitsObservability Circuits,
    IReadOnlyList<EconomyObservabilityAlert> Alerts,
    string? CollectionErrorCode);

public sealed class EconomyObservabilityService : BackgroundService
{
    public const int SchemaVersion = 1;
    private const string PurchaseFeature = "purchase";
    private const string ResourceFeature = "resourceExchange";
    private static readonly JsonSerializerOptions ManifestJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _collectionGate = new(1, 1);
    private readonly SqliteExtractionRepository _repository;
    private readonly PalDefenderCommandQueue _outbox;
    private readonly ExtractionSettlementQueue _settlementQueue;
    private readonly SaveManagementService _saves;
    private readonly IEconomySafetyDependencyProbe _dependencies;
    private readonly EconomySafetyGate _safetyGate;
    private readonly NativeBridgeState _native;
    private readonly EconomyObservabilityOptions _options;
    private readonly EconomySafetyOptions _safetyOptions;
    private readonly ExtractionModeOptions _modeOptions;
    private readonly string _economyBackupRoot;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EconomyObservabilityService> _logger;
    private EconomyObservabilitySnapshot? _latest;

    public EconomyObservabilityService(
        SqliteExtractionRepository repository,
        PalDefenderCommandQueue outbox,
        ExtractionSettlementQueue settlementQueue,
        SaveManagementService saves,
        IEconomySafetyDependencyProbe dependencies,
        EconomySafetyGate safetyGate,
        NativeBridgeState native,
        IOptions<EconomyObservabilityOptions> options,
        IOptions<EconomySafetyOptions> safetyOptions,
        IOptions<ExtractionModeOptions> modeOptions,
        IOptions<EconomyContinuityOptions> continuityOptions,
        IWebHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<EconomyObservabilityService> logger)
    {
        _repository = repository;
        _outbox = outbox;
        _settlementQueue = settlementQueue;
        _saves = saves;
        _dependencies = dependencies;
        _safetyGate = safetyGate;
        _native = native;
        _options = options.Value;
        _safetyOptions = safetyOptions.Value;
        _modeOptions = modeOptions.Value;
        var configuredRoot = continuityOptions.Value.BackupRoot;
        _economyBackupRoot = Path.GetFullPath(Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(environment.ContentRootPath, configuredRoot));
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public EconomyObservabilitySnapshot? Latest => Volatile.Read(ref _latest);

    public async Task<EconomyObservabilitySnapshot> CollectAsync(
        bool applyAutomaticCircuits,
        CancellationToken cancellationToken)
    {
        await _collectionGate.WaitAsync(cancellationToken);
        try
        {
            var observedAt = _timeProvider.GetUtcNow();
            EconomyObservabilitySnapshot snapshot;
            try
            {
                snapshot = await CollectCoreAsync(observedAt, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var correlationId = Guid.NewGuid().ToString("N");
                _logger.LogError(
                    exception,
                    "Economy metrics collection failed; correlation {CorrelationId}.",
                    correlationId);
                snapshot = FailureSnapshot(observedAt, "ECONOMY_METRICS_COLLECTION_FAILED");
            }

            Volatile.Write(ref _latest, snapshot);
            if (applyAutomaticCircuits && _options.Enabled &&
                _options.AutoCircuitBreakEnabled && _modeOptions.Enabled)
            {
                await ApplyAutomaticCircuitsAsync(snapshot, cancellationToken);
                snapshot = snapshot with { Circuits = SafeCircuits(_safetyGate.Current) };
                Volatile.Write(ref _latest, snapshot);
            }
            return snapshot;
        }
        finally
        {
            _collectionGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds),
            _timeProvider);
        do
        {
            try
            {
                await CollectAsync(applyAutomaticCircuits: true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var correlationId = Guid.NewGuid().ToString("N");
                _logger.LogError(
                    exception,
                    "Economy observability loop failed; correlation {CorrelationId}.",
                    correlationId);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<EconomyObservabilitySnapshot> CollectCoreAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var repository = await _repository.GetEconomyObservabilityAsync(
            observedAt,
            TimeSpan.FromMinutes(_options.IdentityConflictWindowMinutes),
            cancellationToken);
        var outbox = await _outbox.GetObservabilityAsync(observedAt, cancellationToken);
        var gameStatus = await _saves.GetStatusAsync(_modeOptions.ServerId, cancellationToken);
        var purchaseBlockers = await ProbeDependenciesAsync(
            EconomyWriteFeature.Purchase,
            cancellationToken);
        var resourceBlockers = await ProbeDependenciesAsync(
            EconomyWriteFeature.ResourceExchange,
            cancellationToken);
        var native = _native.GetSnapshot();
        var purchaseCodes = purchaseBlockers
            .Select(item => SafeBlockerCode(item.Code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var resourceCodes = resourceBlockers
            .Select(item => SafeBlockerCode(item.Code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var purchaseVersionCodes = purchaseCodes
            .Where(IsVersionBlocker)
            .Concat(_safetyOptions.PalDefenderGrantReceiptSemanticsVerified
                ? []
                : ["PALDEFENDER_GRANT_RECEIPT_UNVERIFIED"])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var resourceVersionCodes = resourceCodes
            .Where(IsVersionBlocker)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var versionCodes = purchaseVersionCodes
            .Concat(resourceVersionCodes)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var purchaseWorldCodes = purchaseCodes
            .Where(IsWorldBlocker)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var resourceWorldCodes = resourceCodes
            .Where(IsWorldBlocker)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var worldCodes = purchaseWorldCodes
            .Concat(resourceWorldCodes)
            .Append(gameStatus.Ready ? null : "SAVE_STATUS_UNAVAILABLE")
            .Where(code => code is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var purchaseRuntimeCodes = purchaseCodes
            .Where(code => !IsVersionBlocker(code) && !IsWorldBlocker(code))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var resourceRuntimeCodes = resourceCodes
            .Where(code => !IsVersionBlocker(code) && !IsWorldBlocker(code))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var gameBackup = BackupAge(
            gameStatus.ManagedBackups.LatestCreatedAt,
            observedAt,
            TimeSpan.FromMinutes(_options.MaximumGameBackupAgeMinutes),
            _options.RequireRecentGameBackupForWrites);
        var latestEconomyBackup = ReadLatestEconomyBackup(_modeOptions.ServerId);
        var economyBackup = BackupAge(
            latestEconomyBackup,
            observedAt,
            TimeSpan.FromMinutes(_options.MaximumEconomyBackupAgeMinutes),
            _options.RequireRecentEconomyBackupForWrites);
        var deliveryQueue = Queue(
            ready: _repository.IsReady,
            repository.DeliveryBacklogCount,
            _safetyGate.DeliveryBacklogCapacity,
            repository.OldestDeliveryBacklogAgeSeconds);
        var settlementQueue = Queue(
            _settlementQueue.IsAccepting,
            _settlementQueue.AdmittedCount,
            _settlementQueue.Capacity,
            null);
        var outboxQueue = Queue(
            outbox.Ready,
            outbox.Pending,
            outbox.Capacity,
            outbox.OldestPendingAgeSeconds,
            outbox.States);
        var uncertain = new EconomyUncertainObservability(
            StateCount(repository.Orders, "deliveryuncertain"),
            StateCount(repository.Deliveries, "uncertain"),
            repository.DeliveryReceiptUncertainCount,
            repository.DeliveryReceiptPartialCount,
            StateCount(repository.ResourceSettlements, "uncertain"),
            outbox.Uncertain);
        var ledger = new EconomyInvariantObservability(
            repository.LedgerStreamCount,
            repository.LedgerInvariantMismatchCount,
            repository.SettlementCreditMismatchCount,
            repository.LedgerInvariantMismatchCount == 0 &&
            repository.SettlementCreditMismatchCount == 0);
        var identity = new EconomyIdentityObservability(
            repository.IdentityStructuralConflictCount,
            repository.IdentityConflictCount,
            repository.RecentIdentityConflictCount,
            _options.IdentityConflictWindowMinutes,
            repository.IdentityStructuralConflictCount == 0);
        var version = new EconomyConsistencyObservability(
            versionCodes.Length == 0,
            versionCodes,
            purchaseVersionCodes,
            resourceVersionCodes,
            SafeVersion(gameStatus.GameVersion),
            native.Connected,
            SafeVersion(native.ProtocolVersion),
            SafeVersion(native.GameBuild),
            SafeVersion(native.ModVersion));
        var dependency = new EconomyRuntimeDependencyObservability(
            purchaseRuntimeCodes.Length == 0 && resourceRuntimeCodes.Length == 0,
            purchaseRuntimeCodes,
            resourceRuntimeCodes);
        var world = new EconomyConsistencyObservability(
            gameStatus.Ready && worldCodes.Length == 0,
            worldCodes,
            purchaseWorldCodes,
            resourceWorldCodes,
            SafeVersion(gameStatus.GameVersion),
            native.Connected,
            SafeVersion(native.ProtocolVersion),
            SafeVersion(native.GameBuild),
            SafeVersion(native.ModVersion));
        var alerts = EconomyObservabilityPolicy.Evaluate(
            _options,
            deliveryQueue,
            settlementQueue,
            outboxQueue,
            uncertain,
            ledger,
            identity,
            dependency,
            version,
            world,
            gameBackup,
            economyBackup);

        return new EconomyObservabilitySnapshot(
            SchemaVersion,
            Status(alerts),
            observedAt,
            ExtractionModeCoordinator.GameplayMode,
            repository.Orders,
            repository.ResourceSettlements,
            repository.Deliveries,
            deliveryQueue,
            settlementQueue,
            outboxQueue,
            uncertain,
            ledger,
            identity,
            dependency,
            version,
            world,
            gameBackup,
            economyBackup,
            SafeCircuits(_safetyGate.Current),
            alerts,
            null);
    }

    private async Task<IReadOnlyList<ApiError>> ProbeDependenciesAsync(
        EconomyWriteFeature feature,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _dependencies.ProbeAsync(feature, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [new ApiError(
                feature == EconomyWriteFeature.Purchase
                    ? "PURCHASE_DEPENDENCY_PROBE_FAILED"
                    : "RESOURCE_EXCHANGE_DEPENDENCY_PROBE_FAILED",
                "Economy dependency probe failed.")];
        }
    }

    private async Task ApplyAutomaticCircuitsAsync(
        EconomyObservabilitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var feature in new[]
                 {
                     (Name: PurchaseFeature, Value: EconomyWriteFeature.Purchase),
                     (Name: ResourceFeature, Value: EconomyWriteFeature.ResourceExchange)
                 })
        {
            var active = snapshot.Alerts
                .Where(alert => alert.Active && alert.AutoCircuit &&
                    alert.Affects.Contains(feature.Name, StringComparer.Ordinal))
                .Select(alert => alert.Code)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (active.Length == 0)
            {
                continue;
            }
            var current = feature.Value == EconomyWriteFeature.Purchase
                ? _safetyGate.Current.Purchase
                : _safetyGate.Current.ResourceExchange;
            if (!current.WritesEnabled)
            {
                continue;
            }
            var correlationId = Guid.NewGuid().ToString("N");
            var reason =
                $"Automatic economy circuit ({correlationId}): {string.Join(',', active)}";
            await _safetyGate.SetCircuitAsync(
                feature.Value,
                writesEnabled: false,
                reason,
                "system:economy-observability",
                cancellationToken);
            _logger.LogCritical(
                "Economy circuit {Feature} opened for {AlertCodes}; correlation {CorrelationId}.",
                feature.Name,
                string.Join(',', active),
                correlationId);
        }
    }

    private EconomyObservabilitySnapshot FailureSnapshot(
        DateTimeOffset observedAt,
        string errorCode)
    {
        var emptyStates = new Dictionary<string, EconomyStateMetric>(StringComparer.Ordinal);
        var emptyQueue = new EconomyQueueObservability(false, 0, 0, 0, null);
        var alert = new EconomyObservabilityAlert(
            errorCode,
            "critical",
            true,
            true,
            [PurchaseFeature, ResourceFeature],
            null,
            null,
            "Economy metrics could not be collected; writes must remain closed until inspected.");
        return new EconomyObservabilitySnapshot(
            SchemaVersion,
            "critical",
            observedAt,
            ExtractionModeCoordinator.GameplayMode,
            emptyStates,
            emptyStates,
            emptyStates,
            emptyQueue,
            emptyQueue,
            emptyQueue,
            new EconomyUncertainObservability(0, 0, 0, 0, 0, 0),
            new EconomyInvariantObservability(0, 0, 0, false),
            new EconomyIdentityObservability(0, 0, 0, _options.IdentityConflictWindowMinutes, false),
            new EconomyRuntimeDependencyObservability(
                false, [errorCode], [errorCode]),
            new EconomyConsistencyObservability(
                false, [errorCode], [errorCode], [errorCode], null, false, null, null, null),
            new EconomyConsistencyObservability(
                false, [errorCode], [errorCode], [errorCode], null, false, null, null, null),
            BackupAge(null, observedAt, TimeSpan.FromMinutes(
                _options.MaximumGameBackupAgeMinutes), _options.RequireRecentGameBackupForWrites),
            BackupAge(null, observedAt, TimeSpan.FromMinutes(
                _options.MaximumEconomyBackupAgeMinutes), _options.RequireRecentEconomyBackupForWrites),
            SafeCircuits(_safetyGate.Current),
            [alert],
            errorCode);
    }

    private DateTimeOffset? ReadLatestEconomyBackup(string serverId)
    {
        try
        {
            var serverRoot = Path.GetFullPath(Path.Combine(_economyBackupRoot, serverId));
            if (!serverRoot.StartsWith(
                    _economyBackupRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(serverRoot))
            {
                return null;
            }
            DateTimeOffset? latest = null;
            foreach (var directory in Directory.EnumerateDirectories(serverRoot))
            {
                var info = new DirectoryInfo(directory);
                if (info.Name.StartsWith(".", StringComparison.Ordinal) ||
                    info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
                var manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath) ||
                    File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
                using var stream = new FileStream(
                    manifestPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);
                var manifest = JsonSerializer.Deserialize<EconomySnapshotManifest>(
                    stream,
                    ManifestJsonOptions);
                if (manifest is null ||
                    !string.Equals(manifest.ServerId, serverId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (latest is null || manifest.CreatedAt > latest)
                {
                    latest = manifest.CreatedAt;
                }
            }
            return latest;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static EconomyQueueObservability Queue(
        bool ready,
        int pending,
        int capacity,
        double? oldestAgeSeconds,
        IReadOnlyDictionary<string, int>? states = null) => new(
            ready,
            Math.Max(0, pending),
            Math.Max(0, capacity),
            capacity <= 0 ? 100d : Math.Max(0d, pending * 100d / capacity),
            oldestAgeSeconds,
            states);

    private static EconomyBackupAgeObservability BackupAge(
        DateTimeOffset? latest,
        DateTimeOffset observedAt,
        TimeSpan maximumAge,
        bool required)
    {
        var age = latest is null
            ? (double?)null
            : Math.Max(0d, (observedAt - latest.Value).TotalSeconds);
        return new EconomyBackupAgeObservability(
            latest is not null,
            latest,
            age,
            maximumAge.TotalSeconds,
            required,
            latest is not null &&
            latest.Value <= observedAt &&
            age <= maximumAge.TotalSeconds);
    }

    private static int StateCount(
        IReadOnlyDictionary<string, EconomyStateMetric> states,
        string state) => states.TryGetValue(state, out var metric) ? metric.Count : 0;

    private static EconomyCircuitsObservability SafeCircuits(EconomySafetyGateState state) => new(
        SafeCircuit(state.Purchase),
        SafeCircuit(state.ResourceExchange));

    private static EconomyCircuitObservability SafeCircuit(EconomyCircuitState circuit) => new(
        circuit.WritesEnabled,
        circuit.UpdatedAt,
        string.Equals(
            circuit.Actor,
            "system:economy-observability",
            StringComparison.Ordinal)
            ? "automatic"
            : "manual");

    private static string? SafeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var normalized = value.Trim();
        return normalized.Length <= 128 && normalized.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or '+')
            ? normalized
            : null;
    }

    private static string SafeBlockerCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "ECONOMY_DEPENDENCY_BLOCKED";
        }
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Contains("TOKEN", StringComparison.Ordinal) ||
            normalized.Contains("COOKIE", StringComparison.Ordinal) ||
            normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
            normalized.Contains("SECRET", StringComparison.Ordinal))
        {
            return normalized.StartsWith("PALDEFENDER_", StringComparison.Ordinal)
                ? "PALDEFENDER_CREDENTIAL_UNAVAILABLE"
                : "ECONOMY_CREDENTIAL_UNAVAILABLE";
        }
        return normalized.Length <= 128 && normalized.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_')
            ? normalized
            : "ECONOMY_DEPENDENCY_BLOCKED";
    }

    private static bool IsVersionBlocker(string code) =>
        code.Contains("VERSION", StringComparison.Ordinal) ||
        code is "NATIVE_ECONOMY_ADAPTER_NOT_CONNECTED" or
            "NATIVE_ECONOMY_CAPABILITY_MISSING" or
            "PALDEFENDER_DISABLED" or
            "RCON_DISABLED" or
            "RCON_DELITEMS_CAPABILITY_MISSING";

    private static bool IsWorldBlocker(string code) => code is
        "ACTIVE_WORLD_VERIFICATION_FAILED" or
        "SEASON_WORLD_MISMATCH" or
        "SEASON_WORLD_UNBOUND" or
        "ACTIVE_SEASON_READ_FAILED" or
        "ACTIVE_SEASON_UNAVAILABLE" or
        "SEASON_ROLLOVER_REQUIRED";

    private static string Status(IReadOnlyList<EconomyObservabilityAlert> alerts) =>
        alerts.Any(alert => alert.Active && alert.Severity == "critical")
            ? "critical"
            : alerts.Any(alert => alert.Active && alert.Severity == "warning")
                ? "warning"
                : "healthy";
}

public static class EconomyObservabilityPolicy
{
    private const string Purchase = "purchase";
    private const string Resource = "resourceExchange";

    public static IReadOnlyList<EconomyObservabilityAlert> Evaluate(
        EconomyObservabilityOptions options,
        EconomyQueueObservability deliveryQueue,
        EconomyQueueObservability settlementQueue,
        EconomyQueueObservability outbox,
        EconomyUncertainObservability uncertain,
        EconomyInvariantObservability ledger,
        EconomyIdentityObservability identity,
        EconomyRuntimeDependencyObservability dependency,
        EconomyConsistencyObservability version,
        EconomyConsistencyObservability world,
        EconomyBackupAgeObservability gameBackup,
        EconomyBackupAgeObservability economyBackup)
    {
        List<EconomyObservabilityAlert> alerts = [];
        AddThreshold(
            alerts,
            "DELIVERY_QUEUE_HIGH",
            "warning",
            deliveryQueue.UtilizationPercent >= options.QueueWarningPercent,
            false,
            [Purchase],
            deliveryQueue.UtilizationPercent,
            options.QueueWarningPercent,
            "Shop delivery queue utilization is high.");
        AddThreshold(
            alerts,
            "DELIVERY_QUEUE_SATURATED",
            "critical",
            !deliveryQueue.Ready || deliveryQueue.UtilizationPercent >= options.QueueCriticalPercent,
            true,
            [Purchase],
            deliveryQueue.UtilizationPercent,
            options.QueueCriticalPercent,
            "Shop delivery queue is unavailable or saturated.");
        AddThreshold(
            alerts,
            "DELIVERY_QUEUE_STALLED",
            "critical",
            deliveryQueue.OldestAgeSeconds >= options.MaximumPendingDeliveryAgeSeconds,
            true,
            [Purchase],
            deliveryQueue.OldestAgeSeconds,
            options.MaximumPendingDeliveryAgeSeconds,
            "The oldest shop delivery exceeded the maximum queue age.");
        AddThreshold(
            alerts,
            "OUTBOX_HIGH",
            "warning",
            outbox.UtilizationPercent >= options.QueueWarningPercent,
            false,
            [Purchase],
            outbox.UtilizationPercent,
            options.QueueWarningPercent,
            "The item-grant outbox utilization is high.");
        AddThreshold(
            alerts,
            "OUTBOX_SATURATED",
            "critical",
            !outbox.Ready || outbox.UtilizationPercent >= options.QueueCriticalPercent,
            true,
            [Purchase],
            outbox.UtilizationPercent,
            options.QueueCriticalPercent,
            "The item-grant outbox is unavailable or saturated.");
        AddThreshold(
            alerts,
            "OUTBOX_STALLED",
            "critical",
            outbox.OldestAgeSeconds >= options.MaximumOutboxAgeSeconds,
            true,
            [Purchase],
            outbox.OldestAgeSeconds,
            options.MaximumOutboxAgeSeconds,
            "The oldest item-grant outbox command exceeded the maximum age.");
        var deadLettered = outbox.States is not null &&
            outbox.States.TryGetValue("deadLettered", out var deadLetterCount)
                ? deadLetterCount
                : 0;
        AddThreshold(
            alerts,
            "OUTBOX_DEAD_LETTER_PRESENT",
            "critical",
            deadLettered > 0,
            true,
            [Purchase],
            deadLettered,
            0,
            "A pre-dispatch item-grant command requires manual dead-letter review.");
        AddThreshold(
            alerts,
            "SETTLEMENT_QUEUE_HIGH",
            "warning",
            settlementQueue.UtilizationPercent >= options.QueueWarningPercent,
            false,
            [Resource],
            settlementQueue.UtilizationPercent,
            options.QueueWarningPercent,
            "Resource settlement queue utilization is high.");
        AddThreshold(
            alerts,
            "SETTLEMENT_QUEUE_SATURATED",
            "critical",
            !settlementQueue.Ready || settlementQueue.UtilizationPercent >= options.QueueCriticalPercent,
            true,
            [Resource],
            settlementQueue.UtilizationPercent,
            options.QueueCriticalPercent,
            "Resource settlement queue is unavailable or saturated.");

        var purchaseUncertain = checked(
            uncertain.Orders + uncertain.Deliveries + uncertain.DeliveryReceipts +
            uncertain.PartialDeliveryReceipts + uncertain.Outbox);
        AddThreshold(
            alerts,
            "PURCHASE_UNCERTAIN_PRESENT",
            "critical",
            purchaseUncertain > options.MaximumUncertainPurchaseCount,
            true,
            [Purchase],
            purchaseUncertain,
            options.MaximumUncertainPurchaseCount,
            "Uncertain or partial item delivery requires reconciliation.");
        AddThreshold(
            alerts,
            "RESOURCE_SETTLEMENT_UNCERTAIN_PRESENT",
            "critical",
            uncertain.ResourceSettlements > options.MaximumUncertainSettlementCount,
            true,
            [Resource],
            uncertain.ResourceSettlements,
            options.MaximumUncertainSettlementCount,
            "Uncertain resource settlement requires reconciliation.");
        AddThreshold(
            alerts,
            "LEDGER_INVARIANT_VIOLATION",
            "critical",
            !ledger.Conserved,
            true,
            [Purchase, Resource],
            ledger.LedgerMismatchCount + ledger.SettlementCreditMismatchCount,
            0,
            "Wallet balances, ledger entries, or settlement credits do not conserve value.");
        AddThreshold(
            alerts,
            "IDENTITY_BINDING_INVARIANT_VIOLATION",
            "critical",
            !identity.Consistent,
            true,
            [Purchase, Resource],
            identity.StructuralConflictCount,
            0,
            "Stored player identity bindings are structurally inconsistent.");
        AddThreshold(
            alerts,
            "IDENTITY_BINDING_CONFLICT_SPIKE",
            "critical",
            identity.RecentRejectedConflictCount >= options.IdentityConflictCircuitThreshold,
            true,
            [Purchase, Resource],
            identity.RecentRejectedConflictCount,
            options.IdentityConflictCircuitThreshold,
            "Rejected identity-binding conflicts exceeded the configured window threshold.");
        AddThreshold(
            alerts,
            "PURCHASE_DEPENDENCY_UNAVAILABLE",
            "critical",
            dependency.PurchaseBlockerCodes.Count > 0,
            true,
            [Purchase],
            dependency.PurchaseBlockerCodes.Count,
            0,
            "The purchase storage or runtime dependency check failed.");
        AddThreshold(
            alerts,
            "RESOURCE_DEPENDENCY_UNAVAILABLE",
            "critical",
            dependency.ResourceExchangeBlockerCodes.Count > 0,
            true,
            [Resource],
            dependency.ResourceExchangeBlockerCodes.Count,
            0,
            "The resource settlement storage or runtime dependency check failed.");
        AddThreshold(
            alerts,
            "PURCHASE_VERSION_INCONSISTENT",
            "critical",
            version.PurchaseBlockerCodes.Count > 0,
            true,
            [Purchase],
            version.PurchaseBlockerCodes.Count,
            0,
            "The purchase adapter version/capability consistency check failed.");
        AddThreshold(
            alerts,
            "RESOURCE_VERSION_INCONSISTENT",
            "critical",
            version.ResourceExchangeBlockerCodes.Count > 0,
            true,
            [Resource],
            version.ResourceExchangeBlockerCodes.Count,
            0,
            "The resource settlement adapter version/capability consistency check failed.");
        AddThreshold(
            alerts,
            "WORLD_INCONSISTENT",
            "critical",
            !world.Consistent,
            true,
            [Purchase, Resource],
            world.BlockerCodes.Count,
            0,
            "Active Palworld save and economy season world consistency failed.");
        AddThreshold(
            alerts,
            "GAME_BACKUP_STALE",
            "critical",
            gameBackup.RequiredForWrites && !gameBackup.Fresh,
            true,
            [Purchase, Resource],
            gameBackup.AgeSeconds,
            gameBackup.MaximumAgeSeconds,
            "No sufficiently recent managed game backup is available.");
        AddThreshold(
            alerts,
            "ECONOMY_BACKUP_STALE",
            "critical",
            economyBackup.RequiredForWrites && !economyBackup.Fresh,
            true,
            [Purchase, Resource],
            economyBackup.AgeSeconds,
            economyBackup.MaximumAgeSeconds,
            "No sufficiently recent economy snapshot is available.");
        return alerts;
    }

    private static void AddThreshold(
        ICollection<EconomyObservabilityAlert> alerts,
        string code,
        string severity,
        bool active,
        bool autoCircuit,
        IReadOnlyList<string> affects,
        double? value,
        double? threshold,
        string message) => alerts.Add(new EconomyObservabilityAlert(
            code,
            severity,
            active,
            autoCircuit,
            affects,
            value,
            threshold,
            message));
}

using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var directory = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-economy-observability-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    await VerifyRepositoryProjectionAsync(directory);
    VerifyAlertPolicyAndMetrics();
    Console.WriteLine(
        "PASS: economy state/latency projection, ledger conservation, privacy-safe identity conflict counts, alerts, automatic-circuit targeting, and Prometheus export.");
}
finally
{
    try
    {
        Directory.Delete(directory, recursive: true);
    }
    catch (IOException)
    {
    }
}

static async Task VerifyRepositoryProjectionAsync(string directory)
{
    using var repository = new SqliteExtractionRepository(directory);
    var now = DateTimeOffset.UtcNow;
    var worldId = Guid.NewGuid().ToString("N");
    var season = await repository.UpsertSeasonAsync(
        null,
        new ExtractionSeasonDefinition(
            "local",
            "observability-week",
            "Observability week",
            worldId,
            now.AddHours(-1),
            now.AddDays(1),
            ExtractionSeasonState.Active),
        null,
        CancellationToken.None);
    var first = await repository.GetOrCreateAccountAsync(
        "steam",
        "steam_observability_a",
        "Player A",
        CancellationToken.None);
    var second = await repository.GetOrCreateAccountAsync(
        "steam",
        "steam_observability_b",
        "Player B",
        CancellationToken.None);
    var playerUid = Guid.NewGuid().ToString("N");
    var bound = await repository.BindOrVerifyPlayerIdentityAsync(
        new PlayerIdentityBindingRequest(
            first.ExternalUserId,
            season.SeasonId,
            worldId,
            playerUid,
            first.AccountId),
        CancellationToken.None);
    Assert(bound.Verified, "Test identity was not bound.");
    var conflict = await repository.BindOrVerifyPlayerIdentityAsync(
        new PlayerIdentityBindingRequest(
            second.ExternalUserId,
            season.SeasonId,
            worldId,
            playerUid,
            second.AccountId),
        CancellationToken.None);
    Assert(!conflict.Verified && conflict.ErrorCode == "PLAYER_UID_ALREADY_BOUND",
        "Identity conflict fixture was not rejected.");

    var credit = await repository.AdjustWalletAsync(
        new WalletAdjustmentRequest(
            first.AccountId,
            season.SeasonId,
            ExtractionCurrency.SeasonVoucher,
            25,
            "observability seed",
            "observability-test",
            "credit",
            "test-harness",
            "economy-observability-credit"),
        CancellationToken.None);
    var debit = await repository.AdjustWalletAsync(
        new WalletAdjustmentRequest(
            first.AccountId,
            season.SeasonId,
            ExtractionCurrency.SeasonVoucher,
            -7,
            "observability debit",
            "observability-test",
            "debit",
            "test-harness",
            "economy-observability-debit"),
        CancellationToken.None);
    Assert(credit.Created && debit.Created, "Ledger fixture could not be created.");

    var projection = await repository.GetEconomyObservabilityAsync(
        now.AddMinutes(1),
        TimeSpan.FromMinutes(15),
        CancellationToken.None);
    Assert(projection.LedgerStreamCount == 1 &&
           projection.LedgerInvariantMismatchCount == 0 &&
           projection.SettlementCreditMismatchCount == 0,
        "A valid wallet ledger did not conserve value.");
    Assert(projection.IdentityStructuralConflictCount == 0 &&
           projection.IdentityConflictCount == 1 &&
           projection.RecentIdentityConflictCount == 1,
        "Privacy-safe identity conflict counters were not projected.");
    Assert(projection.Orders.Keys.Contains("pendingdelivery") &&
           projection.ResourceSettlements.Keys.Contains("uncertain") &&
           projection.Deliveries.Keys.Contains("partial") == false,
        "State metric projection is incomplete or invented an invalid delivery state.");
}

static void VerifyAlertPolicyAndMetrics()
{
    var options = new EconomyObservabilityOptions
    {
        AutoCircuitBreakEnabled = true,
        QueueWarningPercent = 75,
        QueueCriticalPercent = 100,
        MaximumPendingDeliveryAgeSeconds = 300,
        MaximumOutboxAgeSeconds = 180,
        IdentityConflictCircuitThreshold = 5
    };
    Assert(options.IsValid(out _), "Valid observability options were rejected.");
    Assert(!new EconomyObservabilityOptions
    {
        QueueWarningPercent = 100,
        QueueCriticalPercent = 100
    }.IsValid(out _), "Invalid queue thresholds were accepted.");

    var ready = new EconomyQueueObservability(true, 0, 10, 0, null);
    var uncertain = new EconomyUncertainObservability(0, 0, 0, 0, 0, 0);
    var ledger = new EconomyInvariantObservability(1, 0, 0, true);
    var identity = new EconomyIdentityObservability(0, 0, 0, 15, true);
    var dependency = new EconomyRuntimeDependencyObservability(true, [], []);
    var consistency = new EconomyConsistencyObservability(
        true, [], [], [], "1.0.0", true, "1.0", "game-build", "mod-version");
    var backup = new EconomyBackupAgeObservability(
        true,
        DateTimeOffset.UtcNow,
        1,
        3600,
        true,
        true);
    var healthy = EconomyObservabilityPolicy.Evaluate(
        options,
        ready,
        ready,
        ready,
        uncertain,
        ledger,
        identity,
        dependency,
        consistency,
        consistency,
        backup,
        backup);
    Assert(!healthy.Any(alert => alert.Active), "Healthy inputs raised an economy alert.");

    var saturated = ready with { Pending = 10, UtilizationPercent = 100 };
    var queueAlerts = EconomyObservabilityPolicy.Evaluate(
        options,
        saturated,
        ready,
        ready,
        uncertain,
        ledger,
        identity,
        dependency,
        consistency,
        consistency,
        backup,
        backup);
    var deliveryCircuit = queueAlerts.Single(alert => alert.Code == "DELIVERY_QUEUE_SATURATED");
    Assert(deliveryCircuit.Active && deliveryCircuit.AutoCircuit &&
           deliveryCircuit.Affects.SequenceEqual(["purchase"]),
        "Delivery saturation did not target only the purchase circuit.");

    var resourceUncertain = uncertain with { ResourceSettlements = 1 };
    var settlementAlerts = EconomyObservabilityPolicy.Evaluate(
        options,
        ready,
        ready,
        ready,
        resourceUncertain,
        ledger,
        identity,
        dependency,
        consistency,
        consistency,
        backup,
        backup);
    var settlementCircuit = settlementAlerts.Single(alert =>
        alert.Code == "RESOURCE_SETTLEMENT_UNCERTAIN_PRESENT");
    Assert(settlementCircuit.Active &&
           settlementCircuit.Affects.SequenceEqual(["resourceExchange"]),
        "Uncertain settlement did not target only resource exchange.");

    var version = consistency with
    {
        Consistent = false,
        BlockerCodes = ["NATIVE_ECONOMY_CAPABILITY_MISSING"],
        ResourceExchangeBlockerCodes = ["NATIVE_ECONOMY_CAPABILITY_MISSING"]
    };
    var versionAlerts = EconomyObservabilityPolicy.Evaluate(
        options,
        ready,
        ready,
        ready,
        uncertain,
        ledger,
        identity,
        dependency,
        version,
        consistency,
        backup,
        backup);
    Assert(versionAlerts.Single(alert => alert.Code == "RESOURCE_VERSION_INCONSISTENT").Active &&
           !versionAlerts.Single(alert => alert.Code == "PURCHASE_VERSION_INCONSISTENT").Active,
        "Resource-only Native drift incorrectly targeted purchase.");

    var runtimeDependency = dependency with
    {
        Consistent = false,
        ResourceExchangeBlockerCodes = ["ECONOMY_STORE_NOT_WRITABLE"]
    };
    var dependencyAlerts = EconomyObservabilityPolicy.Evaluate(
        options,
        ready,
        ready,
        ready,
        uncertain,
        ledger,
        identity,
        runtimeDependency,
        consistency,
        consistency,
        backup,
        backup);
    Assert(dependencyAlerts.Single(alert => alert.Code == "RESOURCE_DEPENDENCY_UNAVAILABLE").Active &&
           !dependencyAlerts.Single(alert => alert.Code == "PURCHASE_DEPENDENCY_UNAVAILABLE").Active,
        "Resource-only runtime failure incorrectly targeted purchase.");

    var deadLetterOutbox = ready with
    {
        States = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["accepted"] = 0,
            ["deadLettered"] = 1,
            ["leased"] = 0
        }
    };
    var deadLetterAlerts = EconomyObservabilityPolicy.Evaluate(
        options,
        ready,
        ready,
        deadLetterOutbox,
        uncertain,
        ledger,
        identity,
        dependency,
        consistency,
        consistency,
        backup,
        backup);
    var deadLetterAlert = deadLetterAlerts.Single(alert =>
        alert.Code == "OUTBOX_DEAD_LETTER_PRESENT");
    Assert(deadLetterAlert.Active && deadLetterAlert.AutoCircuit &&
           deadLetterAlert.Affects.SequenceEqual(["purchase"]),
        "A PalDefender dead-letter did not target the purchase circuit.");

    var state = new Dictionary<string, EconomyStateMetric>(StringComparer.Ordinal)
    {
        ["delivered"] = new(2, new EconomyLatencyMetric(2, 1.5, 2, 2))
    };
    var openCircuit = new EconomyCircuitState(
        false,
        "test",
        "test",
        DateTimeOffset.UtcNow);
    var closedCircuit = openCircuit with { WritesEnabled = true };
    var snapshot = new EconomyObservabilitySnapshot(
        1,
        "critical",
        DateTimeOffset.UtcNow,
        "weekly-resource-economy",
        state,
        state,
        state,
        saturated,
        ready,
        deadLetterOutbox,
        uncertain,
        ledger,
        identity,
        dependency,
        version,
        consistency,
        backup,
        backup,
        new EconomyCircuitsObservability(
            new EconomyCircuitObservability(false, openCircuit.UpdatedAt, "manual"),
            new EconomyCircuitObservability(true, closedCircuit.UpdatedAt, "manual")),
        deadLetterAlerts,
        null);
    var metrics = EconomyPrometheusFormatter.Format(snapshot);
    Assert(metrics.Contains("pal_control_economy_state_total{kind=\"order\",state=\"delivered\"} 2", StringComparison.Ordinal) &&
           metrics.Contains("pal_control_economy_circuit_open{feature=\"purchase\"} 1", StringComparison.Ordinal) &&
           metrics.Contains("pal_control_economy_dependency_consistent{feature=\"purchase\"} 1", StringComparison.Ordinal) &&
           metrics.Contains("pal_control_economy_queue_state_total{queue=\"outbox\",state=\"deadLettered\"} 1", StringComparison.Ordinal) &&
           metrics.Contains("pal_control_economy_alert_active{code=\"OUTBOX_DEAD_LETTER_PRESENT\"", StringComparison.Ordinal),
        "Prometheus export omitted state, dependency, circuit, dead-letter, or alert metrics.");
    Assert(!metrics.Contains("steam_observability", StringComparison.OrdinalIgnoreCase) &&
           !metrics.Contains("cookie", StringComparison.OrdinalIgnoreCase) &&
           !metrics.Contains("token", StringComparison.OrdinalIgnoreCase) &&
           !metrics.Contains("password", StringComparison.OrdinalIgnoreCase),
        "Prometheus export contained a forbidden sensitive field.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

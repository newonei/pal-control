using System.Globalization;
using System.Text;

namespace PalControl.ControlApi.Infrastructure;

public static class EconomyObservabilityEndpoints
{
    public static RouteGroupBuilder MapEconomyObservabilityEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
                "/economy/observability",
                async (
                    bool? refresh,
                    EconomyObservabilityService service,
                    CancellationToken cancellationToken) =>
                {
                    var snapshot = refresh == true || service.Latest is null
                        ? await service.CollectAsync(
                            applyAutomaticCircuits: false,
                            cancellationToken)
                        : service.Latest;
                    return Results.Ok(snapshot);
                })
            .RequireAuthorization(AdminPolicies.Viewer);

        api.MapGet(
                "/economy/metrics",
                async (
                    EconomyObservabilityService service,
                    CancellationToken cancellationToken) =>
                {
                    var snapshot = service.Latest ?? await service.CollectAsync(
                        applyAutomaticCircuits: false,
                        cancellationToken);
                    return Results.Text(
                        EconomyPrometheusFormatter.Format(snapshot),
                        "text/plain; version=0.0.4; charset=utf-8",
                        Encoding.UTF8);
                })
            .RequireAuthorization(AdminPolicies.Viewer);
        return api;
    }
}

public static class EconomyPrometheusFormatter
{
    public static string Format(EconomyObservabilitySnapshot snapshot)
    {
        var output = new StringBuilder(8192);
        Help(output, "pal_control_economy_snapshot_success", "Whether the latest metrics snapshot completed successfully.");
        Gauge(output, "pal_control_economy_snapshot_success", snapshot.CollectionErrorCode is null ? 1 : 0);
        Help(output, "pal_control_economy_snapshot_timestamp_seconds", "Unix timestamp of the latest economy metrics snapshot.");
        Gauge(output, "pal_control_economy_snapshot_timestamp_seconds", snapshot.CollectedAt.ToUnixTimeMilliseconds() / 1000d);

        AppendStates(output, "order", snapshot.Orders);
        AppendStates(output, "resource_settlement", snapshot.ResourceSettlements);
        AppendStates(output, "delivery", snapshot.Deliveries);
        Help(output, "pal_control_economy_queue_state_total", "Current queue rows by queue and durable state.");
        AppendQueue(output, "delivery", snapshot.DeliveryQueue);
        AppendQueue(output, "resource_settlement", snapshot.ResourceSettlementQueue);
        AppendQueue(output, "outbox", snapshot.Outbox);

        Help(output, "pal_control_economy_uncertain_total", "Current uncertain or partial outcomes by economy subsystem.");
        LabeledGauge(output, "pal_control_economy_uncertain_total", "kind", "order", snapshot.Uncertain.Orders);
        LabeledGauge(output, "pal_control_economy_uncertain_total", "kind", "delivery", snapshot.Uncertain.Deliveries);
        LabeledGauge(output, "pal_control_economy_uncertain_total", "kind", "delivery_receipt", snapshot.Uncertain.DeliveryReceipts);
        LabeledGauge(output, "pal_control_economy_uncertain_total", "kind", "partial_delivery_receipt", snapshot.Uncertain.PartialDeliveryReceipts);
        LabeledGauge(output, "pal_control_economy_uncertain_total", "kind", "resource_settlement", snapshot.Uncertain.ResourceSettlements);
        LabeledGauge(output, "pal_control_economy_uncertain_total", "kind", "outbox", snapshot.Uncertain.Outbox);

        Help(output, "pal_control_economy_ledger_stream_total", "Number of wallet ledger streams checked for conservation.");
        Gauge(output, "pal_control_economy_ledger_stream_total", snapshot.Ledger.LedgerStreamCount);
        Help(output, "pal_control_economy_ledger_invariant_mismatch_total", "Current balance/ledger projection mismatches.");
        Gauge(output, "pal_control_economy_ledger_invariant_mismatch_total", snapshot.Ledger.LedgerMismatchCount);
        Help(output, "pal_control_economy_settlement_credit_mismatch_total", "Current resource-settlement credit/ledger mismatches.");
        Gauge(output, "pal_control_economy_settlement_credit_mismatch_total", snapshot.Ledger.SettlementCreditMismatchCount);
        Help(output, "pal_control_economy_identity_conflict_total", "Identity conflicts; identifiers are never exported.");
        LabeledGauge(output, "pal_control_economy_identity_conflict_total", "kind", "structural", snapshot.Identity.StructuralConflictCount);
        LabeledGauge(output, "pal_control_economy_identity_conflict_total", "kind", "rejected_lifetime", snapshot.Identity.LifetimeRejectedConflictCount);
        LabeledGauge(output, "pal_control_economy_identity_conflict_total", "kind", "rejected_window", snapshot.Identity.RecentRejectedConflictCount);

        Help(output, "pal_control_economy_version_consistent", "Whether approved game and adapter versions/capabilities are consistent.");
        Gauge(output, "pal_control_economy_version_consistent", snapshot.VersionConsistency.Consistent ? 1 : 0);
        Help(output, "pal_control_economy_dependency_consistent", "Whether storage and runtime dependencies are available for each economy feature.");
        LabeledGauge(
            output,
            "pal_control_economy_dependency_consistent",
            "feature",
            "purchase",
            snapshot.DependencyConsistency.PurchaseBlockerCodes.Count == 0 ? 1 : 0);
        LabeledGauge(
            output,
            "pal_control_economy_dependency_consistent",
            "feature",
            "resource_exchange",
            snapshot.DependencyConsistency.ResourceExchangeBlockerCodes.Count == 0 ? 1 : 0);
        Help(output, "pal_control_economy_world_consistent", "Whether the active save and economy season world are consistent.");
        Gauge(output, "pal_control_economy_world_consistent", snapshot.WorldConsistency.Consistent ? 1 : 0);
        output.Append("# HELP pal_control_economy_runtime_info Approved runtime version evidence (no player identifiers).\n")
            .Append("# TYPE pal_control_economy_runtime_info gauge\n")
            .Append("pal_control_economy_runtime_info{game_version=\"")
            .Append(Label(snapshot.VersionConsistency.GameVersion))
            .Append("\",native_protocol=\"")
            .Append(Label(snapshot.VersionConsistency.NativeProtocolVersion))
            .Append("\",native_game_build=\"")
            .Append(Label(snapshot.VersionConsistency.NativeGameBuild))
            .Append("\",native_mod_version=\"")
            .Append(Label(snapshot.VersionConsistency.NativeModVersion))
            .Append("\"} 1\n");

        AppendBackup(output, "game", snapshot.GameBackup);
        AppendBackup(output, "economy", snapshot.EconomyBackup);
        Help(output, "pal_control_economy_circuit_open", "Whether the economy write circuit is open (1 means writes are blocked).\n");
        LabeledGauge(output, "pal_control_economy_circuit_open", "feature", "purchase", snapshot.Circuits.Purchase.WritesEnabled ? 0 : 1);
        LabeledGauge(output, "pal_control_economy_circuit_open", "feature", "resource_exchange", snapshot.Circuits.ResourceExchange.WritesEnabled ? 0 : 1);

        Help(output, "pal_control_economy_alert_active", "Active machine-readable economy alerts by stable code and affected feature.");
        foreach (var alert in snapshot.Alerts.OrderBy(alert => alert.Code, StringComparer.Ordinal))
        {
            foreach (var feature in alert.Affects.Order(StringComparer.Ordinal))
            {
                output.Append("pal_control_economy_alert_active{code=\"")
                    .Append(Label(alert.Code))
                    .Append("\",severity=\"")
                    .Append(Label(alert.Severity))
                    .Append("\",feature=\"")
                    .Append(Label(feature))
                    .Append("\"} ")
                    .Append(alert.Active ? '1' : '0')
                    .Append('\n');
            }
        }
        return output.ToString();
    }

    private static void AppendStates(
        StringBuilder output,
        string kind,
        IReadOnlyDictionary<string, Extraction.EconomyStateMetric> states)
    {
        foreach (var state in states.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            output.Append("pal_control_economy_state_total{kind=\"")
                .Append(Label(kind))
                .Append("\",state=\"")
                .Append(Label(state.Key))
                .Append("\"} ")
                .Append(state.Value.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            AppendLatency(output, kind, state.Key, "average", state.Value.Latency.AverageSeconds);
            AppendLatency(output, kind, state.Key, "maximum", state.Value.Latency.MaximumSeconds);
            AppendLatency(output, kind, state.Key, "p95", state.Value.Latency.P95Seconds);
        }
    }

    private static void AppendLatency(
        StringBuilder output,
        string kind,
        string state,
        string statistic,
        double value) => output
        .Append("pal_control_economy_state_latency_seconds{kind=\"")
        .Append(Label(kind))
        .Append("\",state=\"")
        .Append(Label(state))
        .Append("\",statistic=\"")
        .Append(Label(statistic))
        .Append("\"} ")
        .Append(value.ToString("R", CultureInfo.InvariantCulture))
        .Append('\n');

    private static void AppendQueue(
        StringBuilder output,
        string queue,
        EconomyQueueObservability value)
    {
        LabeledGauge(output, "pal_control_economy_queue_ready", "queue", queue, value.Ready ? 1 : 0);
        LabeledGauge(output, "pal_control_economy_queue_pending", "queue", queue, value.Pending);
        LabeledGauge(output, "pal_control_economy_queue_capacity", "queue", queue, value.Capacity);
        LabeledGauge(output, "pal_control_economy_queue_utilization_percent", "queue", queue, value.UtilizationPercent);
        LabeledGauge(output, "pal_control_economy_queue_oldest_age_seconds", "queue", queue, value.OldestAgeSeconds ?? 0);
        if (value.States is not null)
        {
            foreach (var state in value.States.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                output.Append("pal_control_economy_queue_state_total{queue=\"")
                    .Append(Label(queue))
                    .Append("\",state=\"")
                    .Append(Label(state.Key))
                    .Append("\"} ")
                    .Append(state.Value.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }
        }
    }

    private static void AppendBackup(
        StringBuilder output,
        string kind,
        EconomyBackupAgeObservability backup)
    {
        LabeledGauge(output, "pal_control_economy_backup_available", "kind", kind, backup.Available ? 1 : 0);
        LabeledGauge(output, "pal_control_economy_backup_age_seconds", "kind", kind, backup.AgeSeconds ?? -1);
        LabeledGauge(output, "pal_control_economy_backup_maximum_age_seconds", "kind", kind, backup.MaximumAgeSeconds);
        LabeledGauge(output, "pal_control_economy_backup_fresh", "kind", kind, backup.Fresh ? 1 : 0);
    }

    private static void Help(StringBuilder output, string name, string help) => output
        .Append("# HELP ")
        .Append(name)
        .Append(' ')
        .Append(help.Replace('\r', ' ').Replace('\n', ' '))
        .Append('\n')
        .Append("# TYPE ")
        .Append(name)
        .Append(" gauge\n");

    private static void Gauge(StringBuilder output, string name, double value) => output
        .Append(name)
        .Append(' ')
        .Append(value.ToString("R", CultureInfo.InvariantCulture))
        .Append('\n');

    private static void LabeledGauge(
        StringBuilder output,
        string name,
        string labelName,
        string labelValue,
        double value) => output
        .Append(name)
        .Append('{')
        .Append(labelName)
        .Append("=\"")
        .Append(Label(labelValue))
        .Append("\"} ")
        .Append(value.ToString("R", CultureInfo.InvariantCulture))
        .Append('\n');

    private static string Label(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unavailable";
        }
        var safe = new string(value.Trim().Take(128)
            .Where(character => !char.IsControl(character))
            .ToArray());
        return safe.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}

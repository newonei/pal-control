using System.Text;
using PalControl.Soak;

var generatedAt = new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);
var run = new SoakRunConfiguration(
    DurationSeconds: 180,
    SampleIntervalSeconds: 60,
    RecoverySeconds: 60,
    RequestsPerSecond: 1,
    MinimumSamples: 5,
    MinimumWorkloadRequests: 170,
    WorkloadPath: "/health/live");
var stable = Samples();
var report = SoakAnalyzer.Analyze(stable, run, new SoakThresholds(), generatedAt);
Assert(report.Analysis.Passed, "A stable synthetic soak was rejected.");
Assert(report.Status == "passed", "Stable report status is not passed.");
Assert(report.Analysis.CriticalSampleFailures == 0, "Stable samples were marked unavailable.");

var firstBytes = CanonicalJson.Serialize(report);
var secondBytes = CanonicalJson.Serialize(report);
Assert(firstBytes.SequenceEqual(secondBytes), "Canonical report bytes changed for identical input.");
Assert(
    CanonicalJson.Sha256Hex(firstBytes) == CanonicalJson.Sha256Hex(secondBytes),
    "Canonical report hash changed for identical input.");
var evidenceRoot = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-soak-evidence-{Guid.NewGuid():N}");
try
{
    CanonicalJson.WriteReport(evidenceRoot, report);
    var originalReport = File.ReadAllBytes(Path.Combine(evidenceRoot, "report.json"));
    AssertThrows(
        () => CanonicalJson.WriteReport(evidenceRoot, report),
        "A repeated soak run overwrote an existing evidence directory.");
    Assert(originalReport.SequenceEqual(
            File.ReadAllBytes(Path.Combine(evidenceRoot, "report.json"))),
        "A conflicting soak write changed prior evidence bytes.");
}
finally
{
    if (Directory.Exists(evidenceRoot))
    {
        Directory.Delete(evidenceRoot, recursive: true);
    }
}
var stableJson = Encoding.UTF8.GetString(firstBytes);
foreach (var forbidden in new[]
         {
             "super-secret-soak-api-key",
             "steam_76561198000000000",
             "pal_player_session",
             "C:\\private\\production"
         })
{
    Assert(!stableJson.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
        $"Canonical report leaked synthetic sensitive material '{forbidden}'.");
}

var slopeThresholds = new SoakThresholds
{
    MaximumWorkingSetSlopeBytesPerHour = 1
};
var leaky = Samples((index, sample) => sample with
{
    Process = sample.Process with
    {
        WorkingSetBytes = sample.Process.WorkingSetBytes + index * 4096
    }
});
var slopeReport = SoakAnalyzer.Analyze(leaky, run, slopeThresholds, generatedAt);
AssertViolation(slopeReport, "slope_threshold_exceeded", "process.workingSetBytes");

var peakThresholds = new SoakThresholds
{
    MaximumWorkingSetSlopeBytesPerHour = double.MaxValue,
    MaximumWorkingSetPeakGrowthBytes = 1024
};
var peakReport = SoakAnalyzer.Analyze(leaky, run, peakThresholds, generatedAt);
AssertViolation(peakReport, "peak_growth_threshold_exceeded", "process.workingSetBytes");

var slowThresholds = new SoakThresholds
{
    MaximumWorkloadP95Milliseconds = 1
};
var slowReport = SoakAnalyzer.Analyze(stable, run, slowThresholds, generatedAt);
AssertViolation(slowReport, "workload_p95_threshold_exceeded", "workload.p95Milliseconds");

var missedWorkload = Samples((_, sample) => sample with
{
    Workload = sample.Workload with
    {
        Attempted = sample.Workload.Attempted / 2,
        Succeeded = sample.Workload.Succeeded / 2,
        Failed = 0
    }
});
var missedWorkloadReport = SoakAnalyzer.Analyze(
    missedWorkload,
    run,
    new SoakThresholds(),
    generatedAt);
AssertViolation(
    missedWorkloadReport,
    "workload_request_count_below_minimum",
    "workload.attempted");

var invalidPhase = Samples((index, sample) => index == 2
    ? sample with { Phase = "ignored-padding" }
    : sample);
AssertViolation(
    SoakAnalyzer.Analyze(invalidPhase, run, new SoakThresholds(), generatedAt),
    "sample_phase_invalid",
    "samples.phase");

var nonContiguousSequence = Samples((index, sample) => index == 2
    ? sample with { Sequence = 9 }
    : sample);
AssertViolation(
    SoakAnalyzer.Analyze(nonContiguousSequence, run, new SoakThresholds(), generatedAt),
    "sample_order_invalid",
    "samples");

var cadenceGap = Samples((index, sample) => index >= 2
    ? sample with
    {
        ElapsedSeconds = sample.ElapsedSeconds + 120,
        TimestampUtc = sample.TimestampUtc.AddSeconds(120)
    }
    : sample);
AssertViolation(
    SoakAnalyzer.Analyze(cadenceGap, run, new SoakThresholds(), generatedAt),
    "sample_cadence_invalid",
    "samples.elapsedSeconds");

var accountingMismatch = Samples((index, sample) => index == 1
    ? sample with
    {
        Workload = new WorkloadMeasurement(60, 59, 0, 4)
    }
    : sample);
AssertViolation(
    SoakAnalyzer.Analyze(accountingMismatch, run, new SoakThresholds(), generatedAt),
    "workload_accounting_invalid",
    "workload");

var missingLatency = Samples((index, sample) => index == 1
    ? sample with { Workload = sample.Workload with { P95Milliseconds = null } }
    : sample);
AssertViolation(
    SoakAnalyzer.Analyze(missingLatency, run, new SoakThresholds(), generatedAt),
    "workload_latency_invalid",
    "workload.p95Milliseconds");

var compressedWorkload = Samples((index, sample) => index == 1
    ? sample with { Workload = new WorkloadMeasurement(82_080, 82_080, 0, 4) }
    : index is 2 or 3
        ? sample with { Workload = new WorkloadMeasurement(0, 0, 0, null) }
        : sample);
AssertViolation(
    SoakAnalyzer.Analyze(compressedWorkload, run, new SoakThresholds(), generatedAt),
    "workload_window_invalid",
    "workload.attempted");

var recoveryWorkload = Samples((index, sample) => index == 4
    ? sample with { Workload = new WorkloadMeasurement(1, 1, 0, 4) }
    : sample);
AssertViolation(
    SoakAnalyzer.Analyze(recoveryWorkload, run, new SoakThresholds(), generatedAt),
    "workload_window_invalid",
    "workload.attempted");

var recoveryThresholds = new SoakThresholds
{
    MaximumWorkingSetSlopeBytesPerHour = double.MaxValue,
    MaximumWorkingSetPeakGrowthBytes = long.MaxValue,
    MaximumWorkingSetRecoveryGrowthBytes = 0
};
var failedRecovery = Samples((index, sample) =>
    sample.Phase == "recovery"
        ? sample with
        {
            Process = sample.Process with
            {
                WorkingSetBytes = sample.Process.WorkingSetBytes + 4096
            }
        }
        : sample);
var recoveryReport = SoakAnalyzer.Analyze(
    failedRecovery, run, recoveryThresholds, generatedAt);
AssertViolation(
    recoveryReport,
    "recovery_growth_threshold_exceeded",
    "process.workingSetBytes");

var unavailable = Samples((index, sample) => index == 2
    ? sample with
    {
        Api = sample.Api with
        {
            Sessions = new SessionMeasurement(false, null, "operations_probe_failed")
        }
    }
    : sample);
var unavailableReport = SoakAnalyzer.Analyze(
    unavailable, run, new SoakThresholds(), generatedAt);
AssertViolation(unavailableReport, "critical_metric_unavailable", "samples");

var withoutGc = Samples((_, sample) => sample with
{
    Gc = new GcMeasurement(
        false, null, null, null, null, null, "gc_metrics_unavailable")
});
var withoutGcReport = SoakAnalyzer.Analyze(
    withoutGc, run, new SoakThresholds(), generatedAt);
Assert(withoutGcReport.Analysis.Passed, "Optional external GC metrics caused failure.");
Assert(withoutGcReport.Analysis.Warnings.Contains("gc_metrics_unavailable"),
    "Missing optional GC metrics were not called out.");

var interrupted = SoakAnalyzer.Analyze(
    stable.Take(3).ToArray(), run, new SoakThresholds(), generatedAt, runnerFailed: true);
AssertViolation(interrupted, "runner_error", "runner");
AssertViolation(interrupted, "load_duration_incomplete", "run.durationSeconds");
AssertViolation(interrupted, "sample_count_below_minimum", "samples");

Console.WriteLine(
    "PASS: canonical soak analysis fails closed on phase/sequence/cadence padding, " +
    "workload accounting/window/latency fraud, slope, peak, recovery and interruption.");
return;

static SoakSample[] Samples(
    Func<int, SoakSample, SoakSample>? transform = null)
{
    var started = new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);
    var samples = new List<SoakSample>();
    for (var index = 0; index < 5; index++)
    {
        var phase = index == 4 ? "recovery" : "load";
        var sample = new SoakSample(
            index,
            phase,
            started.AddSeconds(index * 60),
            index * 60,
            new ProcessMeasurement(true, 64 * 1024 * 1024, 80 * 1024 * 1024, 120, 24, null),
            new GcMeasurement(true, 16 * 1024 * 1024, 32 * 1024 * 1024, 2, 1, 0, null),
            new SqliteMeasurement(true, 1, 4 * 1024 * 1024, 0, 0, null),
            new LogMeasurement(true, 1, 4096, null),
            new ApiMeasurement(
                new ProbeMeasurement(true, 200, 2, null),
                new ProbeMeasurement(true, 200, 3, null),
                new ProbeMeasurement(true, 200, 4, null),
                new InstanceMeasurement(true, new string('a', 64), null),
                new SessionMeasurement(true, 2, null),
                new QueueMeasurement(
                    true,
                    new QueueValue(0, 128),
                    new QueueValue(0, 32),
                    new QueueValue(0, 256),
                    null)),
            phase == "load" && index > 0
                ? new WorkloadMeasurement(60, 60, 0, 4)
                : new WorkloadMeasurement(0, 0, 0, null));
        samples.Add(transform?.Invoke(index, sample) ?? sample);
    }
    return samples.ToArray();
}

static void AssertViolation(SoakReport report, string code, string metric) =>
    Assert(
        report.Analysis.Violations.Any(item =>
            item.Code == code && item.Metric == metric),
        $"Expected violation '{code}' for '{metric}' was absent.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows(Action action, string message)
{
    try
    {
        action();
    }
    catch (IOException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

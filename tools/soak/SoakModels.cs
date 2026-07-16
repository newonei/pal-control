using System.Text.Json.Serialization;

namespace PalControl.Soak;

public sealed record SoakRunConfiguration(
    int DurationSeconds,
    int SampleIntervalSeconds,
    int RecoverySeconds,
    double RequestsPerSecond,
    int MinimumSamples,
    int MinimumWorkloadRequests,
    string WorkloadPath);

public sealed record ProbeMeasurement(
    bool Success,
    int? StatusCode,
    double ElapsedMilliseconds,
    string? ErrorCode);

public sealed record WorkloadMeasurement(
    int Attempted,
    int Succeeded,
    int Failed,
    double? P95Milliseconds);

public sealed record ProcessMeasurement(
    bool Available,
    long? WorkingSetBytes,
    long? PrivateBytes,
    int? HandleCount,
    int? ThreadCount,
    string? ErrorCode);

public sealed record GcMeasurement(
    bool Available,
    long? HeapSizeBytes,
    long? TotalAllocatedBytes,
    int? Gen0Collections,
    int? Gen1Collections,
    int? Gen2Collections,
    string? ErrorCode);

public sealed record SqliteMeasurement(
    bool Available,
    int DatabaseFileCount,
    long? DatabaseBytes,
    long? WalBytes,
    long? ShmBytes,
    string? ErrorCode);

public sealed record LogMeasurement(
    bool Available,
    int FileCount,
    long? TotalBytes,
    string? ErrorCode);

public sealed record SessionMeasurement(
    bool Available,
    int? Active,
    string? ErrorCode);

public sealed record QueueValue(int Pending, int Capacity);

public sealed record QueueMeasurement(
    bool Available,
    QueueValue? Delivery,
    QueueValue? Settlement,
    QueueValue? Outbox,
    string? ErrorCode)
{
    [JsonIgnore]
    public int? TotalPending => Available &&
        Delivery is not null && Settlement is not null && Outbox is not null
            ? checked(Delivery.Pending + Settlement.Pending + Outbox.Pending)
            : null;
}

public sealed record InstanceMeasurement(
    bool Available,
    string? BindingSha256,
    string? ErrorCode);

public sealed record ApiMeasurement(
    ProbeMeasurement Live,
    ProbeMeasurement Ready,
    ProbeMeasurement Operations,
    InstanceMeasurement Instance,
    SessionMeasurement Sessions,
    QueueMeasurement Queues);

public sealed record SoakSample(
    int Sequence,
    string Phase,
    DateTimeOffset TimestampUtc,
    double ElapsedSeconds,
    ProcessMeasurement Process,
    GcMeasurement Gc,
    SqliteMeasurement Sqlite,
    LogMeasurement Logs,
    ApiMeasurement Api,
    WorkloadMeasurement Workload);

public sealed class SoakThresholds
{
    public double MaximumProbeFailurePercent { get; init; } = 0;
    public double MaximumWorkloadFailurePercent { get; init; } = 0;
    public double MaximumWorkloadP95Milliseconds { get; init; } = 2_000;

    public double MaximumWorkingSetSlopeBytesPerHour { get; init; } = 16 * 1024 * 1024;
    public double MaximumPrivateBytesSlopeBytesPerHour { get; init; } = 16 * 1024 * 1024;
    public double MaximumGcHeapSlopeBytesPerHour { get; init; } = 8 * 1024 * 1024;
    public double MaximumHandleSlopePerHour { get; init; } = 2;
    public double MaximumThreadSlopePerHour { get; init; } = 1;
    public double MaximumDatabaseSlopeBytesPerHour { get; init; } = 64 * 1024 * 1024;
    public double MaximumWalSlopeBytesPerHour { get; init; } = 4 * 1024 * 1024;
    public double MaximumShmSlopeBytesPerHour { get; init; } = 1024 * 1024;
    public double MaximumLogSlopeBytesPerHour { get; init; } = 64 * 1024 * 1024;
    public double MaximumSessionSlopePerHour { get; init; } = 0.5;
    public double MaximumQueueSlopePerHour { get; init; } = 0.5;

    public long MaximumWorkingSetPeakGrowthBytes { get; init; } = 256L * 1024 * 1024;
    public long MaximumPrivateBytesPeakGrowthBytes { get; init; } = 256L * 1024 * 1024;
    public long MaximumGcHeapPeakGrowthBytes { get; init; } = 128L * 1024 * 1024;
    public int MaximumHandlePeakGrowth { get; init; } = 64;
    public int MaximumThreadPeakGrowth { get; init; } = 16;
    public long MaximumDatabasePeakGrowthBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaximumWalPeakBytes { get; init; } = 256L * 1024 * 1024;
    public long MaximumShmPeakBytes { get; init; } = 16L * 1024 * 1024;
    public long MaximumLogPeakGrowthBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaximumSessionPeakGrowth { get; init; } = 4;
    public int MaximumQueuePeakGrowth { get; init; } = 8;

    public long MaximumWorkingSetRecoveryGrowthBytes { get; init; } = 128L * 1024 * 1024;
    public long MaximumPrivateBytesRecoveryGrowthBytes { get; init; } = 128L * 1024 * 1024;
    public long MaximumGcHeapRecoveryGrowthBytes { get; init; } = 64L * 1024 * 1024;
    public int MaximumHandleRecoveryGrowth { get; init; } = 16;
    public int MaximumThreadRecoveryGrowth { get; init; } = 8;
    public long MaximumWalRecoveryBytes { get; init; } = 32L * 1024 * 1024;
    public long MaximumShmRecoveryBytes { get; init; } = 8L * 1024 * 1024;
    public int MaximumSessionRecoveryGrowth { get; init; } = 0;
    public int MaximumQueueRecoveryGrowth { get; init; } = 0;

    public int BaselineSampleCount { get; init; } = 3;

    public void Validate()
    {
        var nonNegative = new[]
        {
            MaximumProbeFailurePercent,
            MaximumWorkloadFailurePercent,
            MaximumWorkloadP95Milliseconds,
            MaximumWorkingSetSlopeBytesPerHour,
            MaximumPrivateBytesSlopeBytesPerHour,
            MaximumGcHeapSlopeBytesPerHour,
            MaximumHandleSlopePerHour,
            MaximumThreadSlopePerHour,
            MaximumDatabaseSlopeBytesPerHour,
            MaximumWalSlopeBytesPerHour,
            MaximumShmSlopeBytesPerHour,
            MaximumLogSlopeBytesPerHour,
            MaximumSessionSlopePerHour,
            MaximumQueueSlopePerHour
        };
        if (nonNegative.Any(value => !double.IsFinite(value) || value < 0) ||
            MaximumProbeFailurePercent > 100 ||
            MaximumWorkloadFailurePercent > 100 ||
            MaximumWorkingSetPeakGrowthBytes < 0 ||
            MaximumPrivateBytesPeakGrowthBytes < 0 ||
            MaximumGcHeapPeakGrowthBytes < 0 ||
            MaximumHandlePeakGrowth < 0 ||
            MaximumThreadPeakGrowth < 0 ||
            MaximumDatabasePeakGrowthBytes < 0 ||
            MaximumWalPeakBytes < 0 ||
            MaximumShmPeakBytes < 0 ||
            MaximumLogPeakGrowthBytes < 0 ||
            MaximumSessionPeakGrowth < 0 ||
            MaximumQueuePeakGrowth < 0 ||
            MaximumWorkingSetRecoveryGrowthBytes < 0 ||
            MaximumPrivateBytesRecoveryGrowthBytes < 0 ||
            MaximumGcHeapRecoveryGrowthBytes < 0 ||
            MaximumHandleRecoveryGrowth < 0 ||
            MaximumThreadRecoveryGrowth < 0 ||
            MaximumWalRecoveryBytes < 0 ||
            MaximumShmRecoveryBytes < 0 ||
            MaximumSessionRecoveryGrowth < 0 ||
            MaximumQueueRecoveryGrowth < 0 ||
            BaselineSampleCount is < 1 or > 60)
        {
            throw new ArgumentException("Soak thresholds are outside their supported fail-closed range.");
        }
    }
}

public sealed record MetricAnalysis(
    string Name,
    bool Available,
    double? Baseline,
    double? SlopePerHour,
    double? Peak,
    double? PeakGrowth,
    double? Recovery,
    double? RecoveryGrowth);

public sealed record SoakViolation(
    string Code,
    string Metric,
    double? Observed,
    double? Threshold);

public sealed record SoakAnalysis(
    bool Passed,
    int CriticalSampleFailures,
    double ProbeFailurePercent,
    double WorkloadFailurePercent,
    IReadOnlyList<MetricAnalysis> Metrics,
    IReadOnlyList<SoakViolation> Violations,
    IReadOnlyList<string> Warnings);

public sealed record SoakReport(
    int SchemaVersion,
    string Canonicalization,
    string EvidenceProfile,
    string ThresholdsSha256,
    string Status,
    DateTimeOffset GeneratedAtUtc,
    SoakRunConfiguration Run,
    SoakThresholds Thresholds,
    SoakAnalysis Analysis,
    IReadOnlyList<SoakSample> Samples);

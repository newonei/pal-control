namespace PalControl.Soak;

public static class SoakAnalyzer
{
    private sealed record MetricRule(
        string Name,
        Func<SoakSample, double?> Selector,
        double MaximumSlope,
        double? MaximumPeakGrowth,
        double? MaximumAbsolutePeak,
        double? MaximumRecoveryGrowth,
        double? MaximumAbsoluteRecovery,
        bool Optional = false);

    private sealed record WorkloadSummary(
        long Attempted,
        long Succeeded,
        long Failed,
        double P95Milliseconds);

    public static SoakReport Analyze(
        IReadOnlyList<SoakSample> samples,
        SoakRunConfiguration run,
        SoakThresholds thresholds,
        DateTimeOffset generatedAtUtc,
        bool runnerFailed = false,
        string evidenceProfile = "unit-test-non-acceptance",
        string? thresholdsSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(thresholds);
        thresholds.Validate();

        var ordered = samples
            .OrderBy(sample => sample.Sequence)
            .ToArray();
        var violations = new List<SoakViolation>();
        var warnings = new SortedSet<string>(StringComparer.Ordinal);

        if (runnerFailed)
        {
            AddViolation(violations, "runner_error", "runner", null, null);
        }
        if (ordered.Length < run.MinimumSamples)
        {
            AddViolation(
                violations,
                "sample_count_below_minimum",
                "samples",
                ordered.Length,
                run.MinimumSamples);
        }
        if (ordered.Where((sample, index) => sample.Sequence != index).Any() ||
            ordered.Where((sample, index) => index > 0 &&
                sample.TimestampUtc <= ordered[index - 1].TimestampUtc).Any())
        {
            AddViolation(violations, "sample_order_invalid", "samples", null, null);
        }

        var loadSamples = ordered
            .Where(sample => string.Equals(sample.Phase, "load", StringComparison.Ordinal))
            .ToArray();
        var recoverySamples = ordered
            .Where(sample => string.Equals(sample.Phase, "recovery", StringComparison.Ordinal))
            .ToArray();
        var finalLoadElapsed = loadSamples.Length == 0
            ? 0
            : loadSamples.Max(sample => sample.ElapsedSeconds);
        var durationTolerance = Math.Max(0.25, run.SampleIntervalSeconds * 0.1);
        if (Math.Abs(finalLoadElapsed - run.DurationSeconds) > durationTolerance)
        {
            AddViolation(
                violations,
                "load_duration_incomplete",
                "run.durationSeconds",
                finalLoadElapsed,
                run.DurationSeconds);
        }
        var runShapeValid = run.DurationSeconds > 0 &&
            run.SampleIntervalSeconds > 0 &&
            run.RecoverySeconds >= 0 &&
            double.IsFinite(run.RequestsPerSecond) &&
            run.RequestsPerSecond > 0;
        if (!runShapeValid)
        {
            AddViolation(violations, "run_configuration_invalid", "run", null, null);
        }
        if (runShapeValid)
        {
            ValidateSampleTimeline(
                ordered,
                loadSamples,
                recoverySamples,
                run,
                durationTolerance,
                violations);
        }

        var criticalFailures = 0;
        foreach (var sample in ordered)
        {
            var coreAvailable = sample.Process.Available &&
                sample.Process.WorkingSetBytes is >= 0 &&
                sample.Process.PrivateBytes is >= 0 &&
                sample.Process.HandleCount is >= 0 &&
                sample.Process.ThreadCount is >= 0 &&
                sample.Sqlite.Available &&
                sample.Sqlite.DatabaseFileCount > 0 &&
                sample.Sqlite.DatabaseBytes is >= 0 &&
                sample.Sqlite.WalBytes is >= 0 &&
                sample.Sqlite.ShmBytes is >= 0 &&
                sample.Logs.Available &&
                sample.Logs.TotalBytes is >= 0 &&
                sample.Api.Instance.Available &&
                sample.Api.Instance.BindingSha256 is { Length: 64 } &&
                sample.Api.Sessions.Available &&
                sample.Api.Sessions.Active is >= 0 &&
                QueueIsValid(sample.Api.Queues);
            if (!coreAvailable)
            {
                criticalFailures += 1;
            }
        }
        if (criticalFailures > 0)
        {
            AddViolation(
                violations,
                "critical_metric_unavailable",
                "samples",
                criticalFailures,
                0);
        }

        var probes = ordered.SelectMany(sample => new[]
        {
            sample.Api.Live,
            sample.Api.Ready,
            sample.Api.Operations
        }).ToArray();
        var probeFailurePercent = Percent(
            probes.Count(probe => !probe.Success),
            probes.Length);
        if (probeFailurePercent > thresholds.MaximumProbeFailurePercent)
        {
            AddViolation(
                violations,
                "probe_failure_threshold_exceeded",
                "api.probes",
                probeFailurePercent,
                thresholds.MaximumProbeFailurePercent);
        }

        var workload = ValidateWorkload(ordered, run, violations);
        var workloadAttempted = workload.Attempted;
        var workloadFailed = workload.Failed;
        var workloadFailurePercent = Percent(workloadFailed, workloadAttempted);
        if (run.RequestsPerSecond > 0 && workloadAttempted == 0)
        {
            AddViolation(violations, "workload_not_executed", "workload", 0, 1);
        }
        if (workloadAttempted < run.MinimumWorkloadRequests)
        {
            AddViolation(
                violations,
                "workload_request_count_below_minimum",
                "workload.attempted",
                workloadAttempted,
                run.MinimumWorkloadRequests);
        }
        var workloadP95 = workload.P95Milliseconds;
        if (workloadP95 > thresholds.MaximumWorkloadP95Milliseconds)
        {
            AddViolation(
                violations,
                "workload_p95_threshold_exceeded",
                "workload.p95Milliseconds",
                workloadP95,
                thresholds.MaximumWorkloadP95Milliseconds);
        }
        if (workloadFailurePercent > thresholds.MaximumWorkloadFailurePercent)
        {
            AddViolation(
                violations,
                "workload_failure_threshold_exceeded",
                "workload",
                workloadFailurePercent,
                thresholds.MaximumWorkloadFailurePercent);
        }

        var rules = new[]
        {
            new MetricRule(
                "process.workingSetBytes",
                sample => sample.Process.WorkingSetBytes,
                thresholds.MaximumWorkingSetSlopeBytesPerHour,
                thresholds.MaximumWorkingSetPeakGrowthBytes,
                null,
                thresholds.MaximumWorkingSetRecoveryGrowthBytes,
                null),
            new MetricRule(
                "process.privateBytes",
                sample => sample.Process.PrivateBytes,
                thresholds.MaximumPrivateBytesSlopeBytesPerHour,
                thresholds.MaximumPrivateBytesPeakGrowthBytes,
                null,
                thresholds.MaximumPrivateBytesRecoveryGrowthBytes,
                null),
            new MetricRule(
                "process.handleCount",
                sample => sample.Process.HandleCount,
                thresholds.MaximumHandleSlopePerHour,
                thresholds.MaximumHandlePeakGrowth,
                null,
                thresholds.MaximumHandleRecoveryGrowth,
                null),
            new MetricRule(
                "process.threadCount",
                sample => sample.Process.ThreadCount,
                thresholds.MaximumThreadSlopePerHour,
                thresholds.MaximumThreadPeakGrowth,
                null,
                thresholds.MaximumThreadRecoveryGrowth,
                null),
            new MetricRule(
                "gc.heapSizeBytes",
                sample => sample.Gc.Available ? sample.Gc.HeapSizeBytes : null,
                thresholds.MaximumGcHeapSlopeBytesPerHour,
                thresholds.MaximumGcHeapPeakGrowthBytes,
                null,
                thresholds.MaximumGcHeapRecoveryGrowthBytes,
                null,
                Optional: true),
            new MetricRule(
                "sqlite.databaseBytes",
                sample => sample.Sqlite.DatabaseBytes,
                thresholds.MaximumDatabaseSlopeBytesPerHour,
                thresholds.MaximumDatabasePeakGrowthBytes,
                null,
                null,
                null),
            new MetricRule(
                "sqlite.walBytes",
                sample => sample.Sqlite.WalBytes,
                thresholds.MaximumWalSlopeBytesPerHour,
                null,
                thresholds.MaximumWalPeakBytes,
                null,
                thresholds.MaximumWalRecoveryBytes),
            new MetricRule(
                "sqlite.shmBytes",
                sample => sample.Sqlite.ShmBytes,
                thresholds.MaximumShmSlopeBytesPerHour,
                null,
                thresholds.MaximumShmPeakBytes,
                null,
                thresholds.MaximumShmRecoveryBytes),
            new MetricRule(
                "logs.totalBytes",
                sample => sample.Logs.TotalBytes,
                thresholds.MaximumLogSlopeBytesPerHour,
                thresholds.MaximumLogPeakGrowthBytes,
                null,
                null,
                null),
            new MetricRule(
                "sessions.active",
                sample => sample.Api.Sessions.Active,
                thresholds.MaximumSessionSlopePerHour,
                thresholds.MaximumSessionPeakGrowth,
                null,
                thresholds.MaximumSessionRecoveryGrowth,
                null),
            new MetricRule(
                "queues.totalPending",
                sample => sample.Api.Queues.TotalPending,
                thresholds.MaximumQueueSlopePerHour,
                thresholds.MaximumQueuePeakGrowth,
                null,
                thresholds.MaximumQueueRecoveryGrowth,
                null)
        };

        var analyses = new List<MetricAnalysis>(rules.Length);
        foreach (var rule in rules)
        {
            var analysis = AnalyzeMetric(loadSamples, recoverySamples, rule, thresholds.BaselineSampleCount);
            analyses.Add(analysis);
            if (!analysis.Available)
            {
                if (rule.Optional)
                {
                    warnings.Add("gc_metrics_unavailable");
                }
                else
                {
                    AddViolation(
                        violations,
                        "metric_analysis_unavailable",
                        rule.Name,
                        null,
                        null);
                }
                continue;
            }
            if (analysis.SlopePerHour > rule.MaximumSlope)
            {
                AddViolation(
                    violations,
                    "slope_threshold_exceeded",
                    rule.Name,
                    analysis.SlopePerHour,
                    rule.MaximumSlope);
            }
            if (rule.MaximumPeakGrowth is { } maximumPeakGrowth &&
                analysis.PeakGrowth > maximumPeakGrowth)
            {
                AddViolation(
                    violations,
                    "peak_growth_threshold_exceeded",
                    rule.Name,
                    analysis.PeakGrowth,
                    maximumPeakGrowth);
            }
            if (rule.MaximumAbsolutePeak is { } maximumAbsolutePeak &&
                analysis.Peak > maximumAbsolutePeak)
            {
                AddViolation(
                    violations,
                    "absolute_peak_threshold_exceeded",
                    rule.Name,
                    analysis.Peak,
                    maximumAbsolutePeak);
            }
            if (run.RecoverySeconds > 0 &&
                rule.MaximumRecoveryGrowth is { } maximumRecoveryGrowth &&
                analysis.RecoveryGrowth > maximumRecoveryGrowth)
            {
                AddViolation(
                    violations,
                    "recovery_growth_threshold_exceeded",
                    rule.Name,
                    analysis.RecoveryGrowth,
                    maximumRecoveryGrowth);
            }
            if (run.RecoverySeconds > 0 &&
                rule.MaximumAbsoluteRecovery is { } maximumAbsoluteRecovery &&
                analysis.Recovery > maximumAbsoluteRecovery)
            {
                AddViolation(
                    violations,
                    "absolute_recovery_threshold_exceeded",
                    rule.Name,
                    analysis.Recovery,
                    maximumAbsoluteRecovery);
            }
        }

        var sortedViolations = violations
            .OrderBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Metric, StringComparer.Ordinal)
            .ThenBy(item => item.Observed)
            .ToArray();
        var analysisResult = new SoakAnalysis(
            sortedViolations.Length == 0,
            criticalFailures,
            probeFailurePercent,
            workloadFailurePercent,
            analyses.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            sortedViolations,
            warnings.ToArray());
        return new SoakReport(
            1,
            CanonicalJson.CanonicalizationId,
            evidenceProfile,
            thresholdsSha256 ?? CanonicalJson.Sha256Hex(CanonicalJson.Serialize(thresholds)),
            analysisResult.Passed ? "passed" : "failed",
            generatedAtUtc.ToUniversalTime(),
            run,
            thresholds,
            analysisResult,
            ordered);
    }

    private static void ValidateSampleTimeline(
        IReadOnlyList<SoakSample> ordered,
        IReadOnlyList<SoakSample> loadSamples,
        IReadOnlyList<SoakSample> recoverySamples,
        SoakRunConfiguration run,
        double tolerance,
        List<SoakViolation> violations)
    {
        var invalidPhaseCount = ordered.Count(sample =>
            sample.Phase is not "load" and not "recovery");
        if (invalidPhaseCount > 0)
        {
            AddViolation(
                violations,
                "sample_phase_invalid",
                "samples.phase",
                invalidPhaseCount,
                0);
        }
        var recoverySeen = false;
        var phaseOrderInvalid = false;
        foreach (var sample in ordered)
        {
            if (sample.Phase == "recovery")
            {
                recoverySeen = true;
            }
            else if (sample.Phase == "load" && recoverySeen)
            {
                phaseOrderInvalid = true;
            }
        }
        if (phaseOrderInvalid)
        {
            AddViolation(
                violations,
                "sample_phase_order_invalid",
                "samples.phase",
                null,
                null);
        }

        var expectedLoadSamples = checked(
            (int)Math.Ceiling(run.DurationSeconds / (double)run.SampleIntervalSeconds) + 1);
        var expectedRecoverySamples = checked(
            (int)Math.Ceiling(run.RecoverySeconds / (double)run.SampleIntervalSeconds));
        if (loadSamples.Count != expectedLoadSamples)
        {
            AddViolation(
                violations,
                "load_sample_count_invalid",
                "samples.load",
                loadSamples.Count,
                expectedLoadSamples);
        }
        if (recoverySamples.Count != expectedRecoverySamples)
        {
            AddViolation(
                violations,
                "recovery_sample_count_invalid",
                "samples.recovery",
                recoverySamples.Count,
                expectedRecoverySamples);
        }
        if (ordered.Count == 0)
        {
            return;
        }

        var cadenceFailures = 0;
        var originTimestamp = ordered[0].TimestampUtc;
        var originElapsed = ordered[0].ElapsedSeconds;
        if (!double.IsFinite(originElapsed) || Math.Abs(originElapsed) > tolerance)
        {
            cadenceFailures += 1;
        }
        for (var index = 0; index < ordered.Count; index++)
        {
            var sample = ordered[index];
            if (!double.IsFinite(sample.ElapsedSeconds) || sample.ElapsedSeconds < 0 ||
                sample.TimestampUtc.Offset != TimeSpan.Zero)
            {
                cadenceFailures += 1;
                continue;
            }
            var wallElapsed = (sample.TimestampUtc - originTimestamp).TotalSeconds;
            if (Math.Abs(wallElapsed - (sample.ElapsedSeconds - originElapsed)) > tolerance)
            {
                cadenceFailures += 1;
            }
            if (index == 0)
            {
                continue;
            }
            var elapsedGap = sample.ElapsedSeconds - ordered[index - 1].ElapsedSeconds;
            if (elapsedGap <= 0 || elapsedGap > run.SampleIntervalSeconds + tolerance)
            {
                cadenceFailures += 1;
            }
        }
        if (loadSamples.Count > 0 &&
            Math.Abs(loadSamples[0].ElapsedSeconds) > tolerance)
        {
            cadenceFailures += 1;
        }
        for (var index = 0; index < loadSamples.Count; index++)
        {
            var expectedElapsed = Math.Min(
                run.DurationSeconds,
                index * (double)run.SampleIntervalSeconds);
            if (Math.Abs(loadSamples[index].ElapsedSeconds - expectedElapsed) > tolerance)
            {
                cadenceFailures += 1;
            }
        }
        if (run.RecoverySeconds > 0 && loadSamples.Count > 0 && recoverySamples.Count > 0)
        {
            var observedRecovery = recoverySamples[^1].ElapsedSeconds -
                loadSamples[^1].ElapsedSeconds;
            if (Math.Abs(observedRecovery - run.RecoverySeconds) > tolerance)
            {
                cadenceFailures += 1;
            }
            for (var index = 0; index < recoverySamples.Count; index++)
            {
                var expectedElapsed = loadSamples[^1].ElapsedSeconds + Math.Min(
                    run.RecoverySeconds,
                    (index + 1) * (double)run.SampleIntervalSeconds);
                if (Math.Abs(recoverySamples[index].ElapsedSeconds - expectedElapsed) > tolerance)
                {
                    cadenceFailures += 1;
                }
            }
        }
        if (cadenceFailures > 0)
        {
            AddViolation(
                violations,
                "sample_cadence_invalid",
                "samples.elapsedSeconds",
                cadenceFailures,
                0);
        }
    }

    private static WorkloadSummary ValidateWorkload(
        IReadOnlyList<SoakSample> ordered,
        SoakRunConfiguration run,
        List<SoakViolation> violations)
    {
        long attempted = 0;
        long succeeded = 0;
        long failed = 0;
        var accountingFailures = 0;
        var latencyFailures = 0;
        var windowFailures = 0;
        double p95 = 0;
        SoakSample? priorLoad = null;
        foreach (var sample in ordered)
        {
            var workload = sample.Workload;
            if (workload.Attempted < 0 || workload.Succeeded < 0 || workload.Failed < 0 ||
                workload.Attempted != (long)workload.Succeeded + workload.Failed)
            {
                accountingFailures += 1;
            }
            attempted += workload.Attempted;
            succeeded += workload.Succeeded;
            failed += workload.Failed;
            if (workload.Attempted > 0)
            {
                if (workload.P95Milliseconds is not double latency ||
                    !double.IsFinite(latency) || latency < 0)
                {
                    latencyFailures += 1;
                }
                else
                {
                    p95 = Math.Max(p95, latency);
                }
            }
            else if (workload.P95Milliseconds is not null)
            {
                latencyFailures += 1;
            }

            if (sample.Phase == "recovery" &&
                (workload.Attempted != 0 || workload.Succeeded != 0 ||
                 workload.Failed != 0 || workload.P95Milliseconds is not null))
            {
                windowFailures += 1;
            }
            if (sample.Phase != "load")
            {
                continue;
            }
            if (priorLoad is null)
            {
                if (workload.Attempted != 0)
                {
                    windowFailures += 1;
                }
            }
            else
            {
                var elapsedWindow = sample.ElapsedSeconds - priorLoad.ElapsedSeconds;
                var maximumAttempts = double.IsFinite(elapsedWindow) &&
                    double.IsFinite(run.RequestsPerSecond) && elapsedWindow > 0 &&
                    run.RequestsPerSecond > 0
                        ? (long)Math.Ceiling(elapsedWindow * run.RequestsPerSecond) + 1
                        : 0;
                if (workload.Attempted > maximumAttempts)
                {
                    windowFailures += 1;
                }
            }
            priorLoad = sample;
        }
        if (attempted != succeeded + failed)
        {
            accountingFailures += 1;
        }
        if (accountingFailures > 0)
        {
            AddViolation(
                violations,
                "workload_accounting_invalid",
                "workload",
                accountingFailures,
                0);
        }
        if (latencyFailures > 0)
        {
            AddViolation(
                violations,
                "workload_latency_invalid",
                "workload.p95Milliseconds",
                latencyFailures,
                0);
        }
        if (windowFailures > 0)
        {
            AddViolation(
                violations,
                "workload_window_invalid",
                "workload.attempted",
                windowFailures,
                0);
        }
        return new WorkloadSummary(attempted, succeeded, failed, p95);
    }

    private static MetricAnalysis AnalyzeMetric(
        IReadOnlyList<SoakSample> loadSamples,
        IReadOnlyList<SoakSample> recoverySamples,
        MetricRule rule,
        int baselineSampleCount)
    {
        var points = loadSamples
            .Select(sample => (sample.ElapsedSeconds / 3600d, Value: rule.Selector(sample)))
            .Where(point => point.Value is not null && double.IsFinite(point.Value.Value))
            .Select(point => (Hours: point.Item1, Value: point.Value!.Value))
            .ToArray();
        if (points.Length < 3)
        {
            return new MetricAnalysis(rule.Name, false, null, null, null, null, null, null);
        }

        var baseline = Median(points.Take(Math.Min(baselineSampleCount, points.Length))
            .Select(point => point.Value));
        var peak = points.Max(point => point.Value);
        var recovery = recoverySamples
            .Select(rule.Selector)
            .LastOrDefault(value => value is not null && double.IsFinite(value.Value));
        return new MetricAnalysis(
            rule.Name,
            true,
            baseline,
            OrdinaryLeastSquaresSlope(points),
            peak,
            peak - baseline,
            recovery,
            recovery is null ? null : recovery.Value - baseline);
    }

    private static double OrdinaryLeastSquaresSlope(
        IReadOnlyList<(double Hours, double Value)> points)
    {
        var meanX = points.Average(point => point.Hours);
        var meanY = points.Average(point => point.Value);
        var numerator = 0d;
        var denominator = 0d;
        foreach (var point in points)
        {
            var x = point.Hours - meanX;
            numerator += x * (point.Value - meanY);
            denominator += x * x;
        }
        return denominator <= double.Epsilon ? 0 : numerator / denominator;
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        var midpoint = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[midpoint - 1] + values[midpoint]) / 2d
            : values[midpoint];
    }

    private static bool QueueIsValid(QueueMeasurement queues) =>
        queues.Available &&
        IsValid(queues.Delivery) &&
        IsValid(queues.Settlement) &&
        IsValid(queues.Outbox);

    private static bool IsValid(QueueValue? queue) =>
        queue is not null && queue.Pending >= 0 &&
        queue.Capacity > 0 && queue.Pending <= queue.Capacity;

    private static double Percent(long numerator, long denominator) =>
        denominator <= 0 ? 0 : numerator * 100d / denominator;

    private static void AddViolation(
        ICollection<SoakViolation> violations,
        string code,
        string metric,
        double? observed,
        double? threshold) =>
        violations.Add(new SoakViolation(code, metric, observed, threshold));
}

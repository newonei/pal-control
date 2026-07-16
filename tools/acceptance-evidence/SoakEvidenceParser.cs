using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalControl.Soak;

namespace PalControl.AcceptanceEvidence;

internal static class SoakEvidenceParser
{
    private const string GateId = "p1-07-24-hour-soak";
    private static readonly HashSet<string> AllowedWorkloadPaths = new(StringComparer.Ordinal)
    {
        "/health/live",
        "/health/ready",
        "/api/v1/economy/observability",
        "/api/v1/extraction/admin/operations/overview?limit=1&refresh=false"
    };
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void Validate(
        AcceptanceManifest manifest,
        string evidenceRoot,
        IReadOnlyDictionary<string, EvidenceArtifact> artifacts,
        VerificationFileLease lease)
    {
        if (!string.Equals(manifest.GateId, GateId, StringComparison.Ordinal))
        {
            return;
        }

        var reportArtifact = SingleRole(artifacts, "soak-metric-series");
        var hashArtifact = SingleRole(artifacts, "soak-report-hash");
        if (reportArtifact.MediaType != "application/json" ||
            hashArtifact.MediaType != "text/plain")
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_ARTIFACT_MEDIA_TYPE_INVALID",
                "The production soak report and hash sidecar must use application/json and text/plain.");
        }

        var reportPath = EvidenceVerifier.ResolveContainedFile(
            evidenceRoot,
            reportArtifact.Path,
            "ACCEPTANCE_SOAK_REPORT_PATH_INVALID");
        var hashPath = EvidenceVerifier.ResolveContainedFile(
            evidenceRoot,
            hashArtifact.Path,
            "ACCEPTANCE_SOAK_HASH_PATH_INVALID");
        var reportBytes = lease.ReadAllBytes(
            reportPath,
            64 * 1024 * 1024,
            "ACCEPTANCE_SOAK_REPORT_SIZE_INVALID");
        if (reportBytes.Length == 0 || reportBytes.Length > 64 * 1024 * 1024)
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_REPORT_SIZE_INVALID",
                "The production soak report is empty or exceeds the 64 MiB parser limit.");
        }
        var observedReportHash = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(reportBytes));
        if (reportBytes.LongLength != reportArtifact.SizeBytes ||
            observedReportHash != reportArtifact.Sha256)
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_REPORT_CHANGED",
                "The soak report changed between artifact validation and machine parsing.");
        }

        SoakReport report;
        try
        {
            report = JsonSerializer.Deserialize<SoakReport>(reportBytes, StrictJson)
                ?? throw new JsonException("The report is empty.");
        }
        catch (JsonException exception)
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_REPORT_INVALID_JSON",
                $"The soak artifact is not a strict v1 report: {exception.Message}");
        }

        var canonical = CanonicalJson.Serialize(report);
        if (!reportBytes.AsSpan().SequenceEqual(canonical))
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_REPORT_NOT_CANONICAL",
                "The soak report bytes do not match pal-control-soak-canonical-json-v1.");
        }
        var reportHash = CanonicalJson.Sha256Hex(canonical);
        byte[] sidecar;
        try
        {
            sidecar = lease.ReadAllBytes(
                hashPath,
                1024,
                "ACCEPTANCE_SOAK_HASH_INVALID");
        }
        catch (IOException exception)
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_HASH_INVALID",
                $"The soak hash sidecar could not be read: {exception.Message}");
        }
        if (!sidecar.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(reportHash + "\n")))
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_HASH_INVALID",
                "The soak hash sidecar does not exactly match the canonical report SHA-256.");
        }
        if (sidecar.LongLength != hashArtifact.SizeBytes ||
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(sidecar)) != hashArtifact.Sha256)
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_HASH_CHANGED",
                "The soak hash sidecar changed between artifact validation and machine parsing.");
        }

        var productionThresholdBytes = ReadProductionThresholds();
        var productionThresholdHash = Convert.ToHexStringLower(
            SHA256.HashData(productionThresholdBytes));
        var productionThresholds = JsonSerializer.Deserialize<SoakThresholds>(
            productionThresholdBytes,
            StrictJson) ?? throw Invalid(
                "ACCEPTANCE_SOAK_TRUSTED_THRESHOLDS_INVALID",
                "The embedded production thresholds could not be loaded.");
        if (report.SchemaVersion != 1 ||
            report.Canonicalization != CanonicalJson.CanonicalizationId ||
            report.EvidenceProfile != "production-24h-v1" ||
            report.ThresholdsSha256 != productionThresholdHash ||
            !CanonicalJson.Serialize(report.Thresholds).AsSpan()
                .SequenceEqual(CanonicalJson.Serialize(productionThresholds)))
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_PROFILE_REJECTED",
                "Only the embedded production-24h-v1 threshold profile can satisfy the soak gate; CI and custom profiles fail closed.");
        }

        ValidateRunConfiguration(report.Run);
        var recomputed = SoakAnalyzer.Analyze(
            report.Samples,
            report.Run,
            report.Thresholds,
            report.GeneratedAtUtc,
            runnerFailed: false,
            report.EvidenceProfile,
            report.ThresholdsSha256);
        if (!CanonicalJson.Serialize(recomputed).AsSpan().SequenceEqual(canonical) ||
            report.Status != "passed" ||
            !report.Analysis.Passed ||
            report.Analysis.Violations.Count != 0)
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_ANALYSIS_MISMATCH",
                "The report status or analysis cannot be reproduced from its samples and frozen thresholds.");
        }

        ValidateTimeline(manifest, report, reportArtifact);
        RequireManifestMetric(
            manifest,
            reportArtifact.Id,
            "observation-duration-hours",
            report.Run.DurationSeconds / 3600m,
            "hours");
        RequireManifestMetric(
            manifest,
            reportArtifact.Id,
            "metric-sample-count",
            report.Samples.Count,
            "count");
        RequireManifestMetric(
            manifest,
            reportArtifact.Id,
            "sustained-growth-finding-count",
            report.Analysis.Violations.Count,
            "count");
        foreach (var check in manifest.Checks.Where(check =>
                     check.Id is "memory-no-sustained-growth" or
                         "handles-no-sustained-growth" or
                         "logs-within-budget" or
                         "queues-no-sustained-growth" or
                         "sessions-no-sustained-growth"))
        {
            if (!check.ArtifactIds.Contains(reportArtifact.Id, StringComparer.Ordinal))
            {
                throw Invalid(
                    "ACCEPTANCE_SOAK_CHECK_NOT_REPORT_BOUND",
                    $"Soak check '{check.Id}' must reference the parsed canonical report.");
            }
        }
    }

    private static EvidenceArtifact SingleRole(
        IReadOnlyDictionary<string, EvidenceArtifact> artifacts,
        string role)
    {
        var matching = artifacts.Values.Where(item => item.Role == role).ToArray();
        return matching.Length == 1
            ? matching[0]
            : throw Invalid(
                "ACCEPTANCE_SOAK_ARTIFACT_AMBIGUOUS",
                $"The soak gate requires exactly one '{role}' artifact.");
    }

    private static void ValidateRunConfiguration(SoakRunConfiguration run)
    {
        var expectedSamples = checked(
            (int)Math.Ceiling(run.DurationSeconds / (double)run.SampleIntervalSeconds) + 1 +
            (int)Math.Ceiling(run.RecoverySeconds / (double)run.SampleIntervalSeconds));
        var expectedWorkload = Math.Max(
            1,
            (int)Math.Floor(run.DurationSeconds * run.RequestsPerSecond * 0.95d));
        if (run.DurationSeconds is < 86400 or > 604800 ||
            run.SampleIntervalSeconds is < 10 or > 300 ||
            run.RecoverySeconds is < 60 or > 3600 ||
            !double.IsFinite(run.RequestsPerSecond) ||
            run.RequestsPerSecond is <= 0 or > 50 ||
            run.MinimumSamples != expectedSamples ||
            run.MinimumWorkloadRequests != expectedWorkload ||
            !AllowedWorkloadPaths.Contains(run.WorkloadPath))
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_RUN_CONFIGURATION_INVALID",
                "The report does not contain the bounded production runner configuration.");
        }
    }

    private static void ValidateTimeline(
        AcceptanceManifest manifest,
        SoakReport report,
        EvidenceArtifact reportArtifact)
    {
        if (report.Samples.Count == 0 || report.GeneratedAtUtc.Offset != TimeSpan.Zero ||
            report.Samples.Any(sample => sample.TimestampUtc.Offset != TimeSpan.Zero))
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_TIMELINE_INVALID",
                "Soak samples and generation time must be a non-empty UTC timeline.");
        }
        var ordered = report.Samples.OrderBy(sample => sample.Sequence).ToArray();
        var origin = ordered[0].TimestampUtc;
        foreach (var sample in ordered)
        {
            var wallElapsed = (sample.TimestampUtc - origin).TotalSeconds;
            var tolerance = Math.Max(10d, Math.Abs(sample.ElapsedSeconds) * 0.001d);
            if (Math.Abs(wallElapsed - sample.ElapsedSeconds) > tolerance)
            {
                throw Invalid(
                    "ACCEPTANCE_SOAK_TIMELINE_INVALID",
                    "Sample elapsed seconds are inconsistent with their signed UTC timestamps.");
            }
        }
        var finalLoad = ordered.Where(sample => sample.Phase == "load")
            .OrderBy(sample => sample.ElapsedSeconds)
            .LastOrDefault() ?? throw Invalid(
                "ACCEPTANCE_SOAK_TIMELINE_INVALID",
                "The report contains no load-phase sample.");
        if ((finalLoad.TimestampUtc - origin).TotalSeconds < report.Run.DurationSeconds - 10 ||
            origin < manifest.Execution.StartedAt ||
            ordered[^1].TimestampUtc > manifest.Execution.EndedAt ||
            report.GeneratedAtUtc < ordered[^1].TimestampUtc ||
            report.GeneratedAtUtc > reportArtifact.CapturedAt.AddMinutes(5))
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_TIMELINE_INVALID",
                "The recomputed 24-hour sample timeline is outside the signed execution and capture window.");
        }
    }

    private static void RequireManifestMetric(
        AcceptanceManifest manifest,
        string reportArtifactId,
        string id,
        decimal expected,
        string unit)
    {
        var matching = manifest.Metrics.Where(metric => metric.Id == id).ToArray();
        if (matching.Length != 1 || matching[0].Value != expected ||
            matching[0].Unit != unit ||
            !matching[0].ArtifactIds.Contains(reportArtifactId, StringComparer.Ordinal))
        {
            throw Invalid(
                "ACCEPTANCE_SOAK_MANIFEST_METRIC_MISMATCH",
                $"Manifest metric '{id}' must exactly equal the value recomputed from the canonical soak report.");
        }
    }

    private static byte[] ReadProductionThresholds()
    {
        using var stream = typeof(SoakReport).Assembly.GetManifestResourceStream(
            "PalControl.Soak.thresholds.production.json") ?? throw Invalid(
                "ACCEPTANCE_SOAK_TRUSTED_THRESHOLDS_INVALID",
                "The embedded production threshold profile is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static EvidenceValidationException Invalid(string code, string message) =>
        new(code, message);
}

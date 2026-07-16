using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net;
using System.Reflection;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ZoneCalibration;

internal static partial class ZoneCalibrationVerifier
{
    private const int MaximumDocumentBytes = 2 * 1024 * 1024;
    private const int MaximumArtifactBytes = 1024 * 1024;
    private const double NumericTolerance = 1e-9;
    private const string EvidenceSchemaResource =
        "PalControl.ZoneCalibration.Schemas.zone-calibration-evidence.schema.v1.json";
    private const string ReportSchemaResource =
        "PalControl.ZoneCalibration.Schemas.zone-calibration-report.schema.v1.json";

    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private static readonly HashSet<string> ArtifactRoles = new(StringComparer.Ordinal)
    {
        "server-build-binding",
        "content-zone-binding",
        "position-capture",
        "route-capture",
        "quote-request-capture",
        "quote-response-capture",
        "accessibility-capture",
        "risk-capture"
    };

    private static readonly HashSet<string> RiskLevels = new(StringComparer.Ordinal)
    {
        "low",
        "medium",
        "high"
    };

    public static JsonSerializerOptions SerializerOptions => StrictJson;

    // Dedicated friend-test hook; production callers cannot configure it.
    internal static Action<string>? TestBeforeFinalSnapshotCheck { get; set; }

    private sealed class CaptureValidationContext
    {
        public CaptureValidationContext(ZoneCalibrationEvidence evidence, LoadedTrustStore trustStore)
        {
            Evidence = evidence;
            TrustStore = trustStore;
        }

        public ZoneCalibrationEvidence Evidence { get; }
        public LoadedTrustStore TrustStore { get; }
        public HashSet<string> Nonces { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CaptureKeyIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ValidatedBundle(
        ZoneCalibrationEvidence Evidence,
        FileSnapshot EvidenceSnapshot,
        LoadedSchema EvidenceSchema,
        LoadedSchema ReportSchema,
        LoadedTrustStore TrustStore,
        IReadOnlyDictionary<string, VerifiedArtifact> Artifacts,
        IReadOnlyList<CanonicalArtifactHash> CanonicalArtifacts,
        string ArtifactManifestSha256,
        double CenterDistance,
        IReadOnlyList<BoundaryCalculation> BoundaryCalculations,
        IReadOnlyList<RouteCalculation> RouteCalculations,
        IReadOnlyList<string> CaptureKeyIds,
        ReviewChallenge ReviewChallenge);

    public static IReadOnlyDictionary<string, string> GetEmbeddedSchemaHashes()
    {
        var evidence = LoadEmbeddedSchema(
            EvidenceSchemaResource,
            ZoneCalibrationConstants.EvidenceSchemaId,
            "ZONE_EVIDENCE_SCHEMA_INVALID");
        var report = LoadEmbeddedSchema(
            ReportSchemaResource,
            ZoneCalibrationConstants.ReportSchemaId,
            "ZONE_REPORT_SCHEMA_INVALID");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["evidenceSchema"] = evidence.Sha256,
            ["reportSchema"] = report.Sha256
        };
    }

    public static ReviewChallenge PrepareReview(
        string evidencePath,
        string trustStorePath,
        string pinnedTrustStoreSha256,
        ZoneCalibrationExpectedBinding expected,
        DateTimeOffset now)
    {
        var bundle = ValidateBundle(
            evidencePath,
            trustStorePath,
            pinnedTrustStoreSha256,
            expected,
            now,
            verifyReviewSignature: false);
        TestBeforeFinalSnapshotCheck?.Invoke("prepare-review");
        RevalidateBundleSnapshots(bundle);
        return bundle.ReviewChallenge;
    }

    public static VerificationResult VerifyAndWriteReport(
        string evidencePath,
        string trustStorePath,
        string pinnedTrustStoreSha256,
        ZoneCalibrationExpectedBinding expected,
        string reportPath,
        string reportHashPath,
        DateTimeOffset now)
    {
        var bundle = ValidateBundle(
            evidencePath,
            trustStorePath,
            pinnedTrustStoreSha256,
            expected,
            now,
            verifyReviewSignature: true);
        var evidence = bundle.Evidence;
        var report = BuildCanonicalReport(bundle);

        var reportBytes = AppendNewline(JsonSerializer.SerializeToUtf8Bytes(report, StrictJson));
        var reportSha256 = HashBytes(reportBytes);
        TestBeforeFinalSnapshotCheck?.Invoke("verify-before-write");
        RevalidateBundleSnapshots(bundle);
        WriteReportPair(reportPath, reportHashPath, reportBytes, reportSha256);
        var writtenReport = ReadFileSnapshot(
            ExistingRegularFile(reportPath, "ZONE_REPORT_NOT_FOUND_AFTER_WRITE"),
            MaximumDocumentBytes,
            "ZONE_REPORT_TOO_LARGE_AFTER_WRITE");
        var writtenSidecar = ReadFileSnapshot(
            ExistingRegularFile(reportHashPath, "ZONE_REPORT_HASH_NOT_FOUND_AFTER_WRITE"),
            256,
            "ZONE_REPORT_HASH_TOO_LARGE_AFTER_WRITE");
        ValidateWrittenReportPair(writtenReport, writtenSidecar, reportBytes, reportSha256);
        TestBeforeFinalSnapshotCheck?.Invoke("verify-after-write");
        RevalidateBundleSnapshots(bundle);
        EnsureSnapshotUnchanged(
            writtenReport,
            MaximumDocumentBytes,
            "ZONE_REPORT_CHANGED_DURING_VERIFICATION");
        EnsureSnapshotUnchanged(
            writtenSidecar,
            256,
            "ZONE_REPORT_HASH_CHANGED_DURING_VERIFICATION");
        return new VerificationResult(
            true,
            evidence.CalibrationId,
            evidence.Zone.ZoneId,
            bundle.BoundaryCalculations.Count,
            bundle.Artifacts.Count,
            bundle.EvidenceSnapshot.Sha256,
            reportSha256,
            Path.GetFullPath(reportPath),
            Path.GetFullPath(reportHashPath));
    }

    private static CanonicalZoneCalibrationReport BuildCanonicalReport(ValidatedBundle bundle)
    {
        var evidence = bundle.Evidence;
        return new CanonicalZoneCalibrationReport(
            ZoneCalibrationConstants.ReportSchemaId,
            ZoneCalibrationConstants.EvidenceSchemaVersion,
            bundle.ReportSchema.Sha256,
            ZoneCalibrationConstants.CanonicalizationId,
            "pass",
            bundle.EvidenceSnapshot.Sha256,
            bundle.EvidenceSchema.Sha256,
            bundle.TrustStore.Sha256,
            evidence.CalibrationId,
            evidence.ExpiresAt,
            evidence.Server.ServerId,
            evidence.Server.GameBuild,
            evidence.Server.SteamBuild,
            evidence.Content.VersionId,
            evidence.Content.VersionNumber,
            evidence.Content.ContentHash,
            evidence.Zone.ZoneId,
            evidence.Zone.Authority,
            ExtractionZoneGeometry.FormulaId,
            evidence.Zone.Center,
            evidence.Zone.Radius,
            evidence.Zone.CenterTolerance,
            RoundMetric(bundle.CenterDistance),
            evidence.Zone.InsideSafetyMargin,
            evidence.Zone.OutsideSafetyMargin,
            evidence.Sampling.StartedAt,
            evidence.Sampling.EndedAt,
            evidence.Sampling.CoordinateSource,
            evidence.Participants.Executor.SubjectId,
            evidence.Participants.Reviewer.SubjectId,
            bundle.CaptureKeyIds,
            evidence.Review.ReviewerKeyId,
            evidence.Review.EvidencePayloadSha256,
            evidence.Review.ArtifactManifestSha256,
            evidence.Review.SignatureAlgorithm,
            evidence.Review.SignatureBase64,
            bundle.BoundaryCalculations,
            bundle.RouteCalculations,
            evidence.AccessibilityCheck.Accessible && evidence.AccessibilityCheck.ObstructionFree,
            evidence.AccessibilityCheck.ReturnPathConfirmed,
            evidence.RiskCheck.RiskLevel,
            evidence.RiskCheck.Disposition,
            evidence.RiskCheck.TerrainHazardChecked,
            evidence.RiskCheck.HostileExposureChecked,
            evidence.RiskCheck.RespawnRouteAvailable,
            bundle.ArtifactManifestSha256,
            bundle.CanonicalArtifacts,
            evidence.Review.ReviewedAt);
    }

    public static ReportVerificationResult VerifyCanonicalReport(
        string evidencePath,
        string reportPath,
        string reportHashPath,
        string trustStorePath,
        string pinnedTrustStoreSha256,
        ZoneCalibrationExpectedBinding expected,
        DateTimeOffset now)
    {
        now = RequireUtc(now, "now");
        var reportSchema = LoadEmbeddedSchema(
            ReportSchemaResource,
            ZoneCalibrationConstants.ReportSchemaId,
            "ZONE_REPORT_SCHEMA_INVALID");
        var evidenceSchema = LoadEmbeddedSchema(
            EvidenceSchemaResource,
            ZoneCalibrationConstants.EvidenceSchemaId,
            "ZONE_EVIDENCE_SCHEMA_INVALID");
        var snapshot = ReadFileSnapshot(
            ExistingRegularFile(reportPath, "ZONE_REPORT_NOT_FOUND"),
            MaximumDocumentBytes,
            "ZONE_REPORT_TOO_LARGE");
        RejectDuplicateProperties(snapshot.Bytes, "ZONE_REPORT_INVALID_JSON");
        RejectUnsafeJson(snapshot.Bytes, "report");
        var report = DeserializeStrict<CanonicalZoneCalibrationReport>(
            snapshot.Bytes,
            "ZONE_REPORT_INVALID_JSON");
        var expectedBytes = AppendNewline(JsonSerializer.SerializeToUtf8Bytes(report, StrictJson));
        if (!CryptographicOperations.FixedTimeEquals(snapshot.Bytes, expectedBytes))
        {
            throw Invalid(
                "ZONE_REPORT_NOT_CANONICAL",
                "The report must be the exact minified canonical JSON plus one LF byte.");
        }
        var sidecar = ReadFileSnapshot(
            ExistingRegularFile(reportHashPath, "ZONE_REPORT_HASH_NOT_FOUND"),
            256,
            "ZONE_REPORT_HASH_TOO_LARGE");
        var expectedSidecar = Encoding.UTF8.GetBytes(snapshot.Sha256 + "\n");
        if (!CryptographicOperations.FixedTimeEquals(sidecar.Bytes, expectedSidecar))
        {
            throw Invalid("ZONE_REPORT_HASH_MISMATCH", "The report hash sidecar does not match exact report bytes.");
        }
        ValidateReport(report, reportSchema, evidenceSchema, expected, now);
        var bundle = ValidateBundle(
            evidencePath,
            trustStorePath,
            pinnedTrustStoreSha256,
            expected,
            now,
            verifyReviewSignature: true);
        if (report.TrustStoreSha256 != bundle.TrustStore.Sha256)
        {
            throw Invalid("ZONE_REPORT_TRUST_STORE_MISMATCH", "Report trust-store SHA differs from the externally pinned store.");
        }
        var regenerated = AppendNewline(JsonSerializer.SerializeToUtf8Bytes(BuildCanonicalReport(bundle), StrictJson));
        if (!CryptographicOperations.FixedTimeEquals(snapshot.Bytes, regenerated))
        {
            throw Invalid(
                "ZONE_REPORT_SOURCE_MISMATCH",
                "Canonical report does not exactly match the reverified signed source evidence and artifacts.");
        }
        VerifyReportReviewSignature(report, bundle.TrustStore);
        TestBeforeFinalSnapshotCheck?.Invoke("verify-report");
        RevalidateBundleSnapshots(bundle);
        EnsureSnapshotUnchanged(snapshot, MaximumDocumentBytes, "ZONE_REPORT_CHANGED_DURING_VERIFICATION");
        EnsureSnapshotUnchanged(sidecar, 256, "ZONE_REPORT_HASH_CHANGED_DURING_VERIFICATION");
        return new ReportVerificationResult(true, report.CalibrationId, report.ZoneId, snapshot.Sha256);
    }

    private static ValidatedBundle ValidateBundle(
        string evidencePath,
        string trustStorePath,
        string pinnedTrustStoreSha256,
        ZoneCalibrationExpectedBinding expected,
        DateTimeOffset now,
        bool verifyReviewSignature)
    {
        now = RequireUtc(now, "now");
        var evidenceSchema = LoadEmbeddedSchema(
            EvidenceSchemaResource,
            ZoneCalibrationConstants.EvidenceSchemaId,
            "ZONE_EVIDENCE_SCHEMA_INVALID");
        var reportSchema = LoadEmbeddedSchema(
            ReportSchemaResource,
            ZoneCalibrationConstants.ReportSchemaId,
            "ZONE_REPORT_SCHEMA_INVALID");
        var trustStore = LoadTrustStore(trustStorePath, pinnedTrustStoreSha256);
        var evidenceSnapshot = ReadFileSnapshot(
            ExistingRegularFile(evidencePath, "ZONE_EVIDENCE_NOT_FOUND"),
            MaximumDocumentBytes,
            "ZONE_EVIDENCE_TOO_LARGE");
        RejectDuplicateProperties(evidenceSnapshot.Bytes, "ZONE_EVIDENCE_INVALID_JSON");
        RejectUnsafeJson(evidenceSnapshot.Bytes, "evidence");
        var evidence = DeserializeStrict<ZoneCalibrationEvidence>(
            evidenceSnapshot.Bytes,
            "ZONE_EVIDENCE_INVALID_JSON");

        ValidateRequiredShape(evidence);
        ValidateHeader(evidence, evidenceSchema);
        ValidateServerAndContent(evidence);
        ValidateExpectedBinding(evidence, expected, now);
        ValidateZone(evidence.Zone);
        ValidateSamplingAndParticipants(evidence, now, expected.MaximumEvidenceAge);

        var evidenceRoot = Path.GetDirectoryName(evidenceSnapshot.FullPath)
            ?? throw Invalid("ZONE_EVIDENCE_ROOT_INVALID", "The evidence document has no parent directory.");
        var artifacts = ValidateArtifacts(evidence, evidenceRoot, now);
        var references = new HashSet<string>(StringComparer.Ordinal);
        var captureContext = new CaptureValidationContext(evidence, trustStore);

        ValidateServerBuildCapture(evidence, artifacts, references, captureContext);
        ValidateContentZoneCapture(evidence, artifacts, references, captureContext);
        var centerDistance = ValidateCenterCapture(evidence, artifacts, references, captureContext);
        var boundaryCalculations = ValidateBoundaryCaptures(evidence, artifacts, references, captureContext);
        var routeCalculations = ValidateRoutes(evidence, artifacts, references, captureContext);
        ValidateAccessibility(evidence, artifacts, references, captureContext);
        ValidateRisk(evidence, artifacts, references, captureContext);

        if (references.Count != artifacts.Count || artifacts.Keys.Any(id => !references.Contains(id)))
        {
            var unreferenced = artifacts.Keys.Where(id => !references.Contains(id)).Order(StringComparer.Ordinal);
            throw Invalid(
                "ZONE_ARTIFACT_UNREFERENCED",
                $"Every signed raw artifact must support exactly one verified fact; unreferenced: {string.Join(", ", unreferenced)}.");
        }

        var canonicalArtifacts = artifacts.Values
            .OrderBy(value => value.Metadata.Id, StringComparer.Ordinal)
            .Select(value => new CanonicalArtifactHash(
                value.Metadata.Id,
                value.Metadata.Role,
                value.Metadata.Sha256,
                value.Metadata.SizeBytes))
            .ToArray();
        var artifactManifestSha256 = ComputeArtifactManifestSha256(canonicalArtifacts);
        var challenge = BuildReviewChallenge(evidence, canonicalArtifacts, artifactManifestSha256);
        if (verifyReviewSignature)
        {
            ValidateReview(evidence, trustStore, captureContext.CaptureKeyIds, challenge, now);
        }

        return new ValidatedBundle(
            evidence,
            evidenceSnapshot,
            evidenceSchema,
            reportSchema,
            trustStore,
            artifacts,
            canonicalArtifacts,
            artifactManifestSha256,
            centerDistance,
            boundaryCalculations,
            routeCalculations,
            captureContext.CaptureKeyIds.Order(StringComparer.Ordinal).ToArray(),
            challenge);
    }

    private static void RevalidateBundleSnapshots(ValidatedBundle bundle)
    {
        EnsureSnapshotUnchanged(
            bundle.EvidenceSnapshot,
            MaximumDocumentBytes,
            "ZONE_EVIDENCE_CHANGED_DURING_VERIFICATION");
        EnsureSnapshotUnchanged(
            bundle.TrustStore.Snapshot,
            MaximumDocumentBytes,
            "ZONE_TRUST_STORE_CHANGED_DURING_VERIFICATION");
        foreach (var artifact in bundle.Artifacts.Values.OrderBy(
                     value => value.Metadata.Id,
                     StringComparer.Ordinal))
        {
            EnsureSnapshotUnchanged(
                artifact.Snapshot,
                MaximumArtifactBytes,
                "ZONE_ARTIFACT_CHANGED_DURING_VERIFICATION");
        }
    }

    private static void EnsureSnapshotUnchanged(
        FileSnapshot expected,
        long maximumBytes,
        string code)
    {
        var currentPath = ExistingRegularFile(expected.FullPath, code);
        if (!PathComparer().Equals(Path.GetFullPath(expected.FullPath), currentPath))
        {
            throw Invalid(code, $"Snapshot path identity changed: '{expected.FullPath}'.");
        }
        var actual = ReadFileSnapshot(currentPath, maximumBytes, code);
        if (actual.SizeBytes != expected.SizeBytes ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual.Sha256),
                Encoding.ASCII.GetBytes(expected.Sha256)))
        {
            throw Invalid(code, $"Snapshot size or SHA-256 changed: '{expected.FullPath}'.");
        }
    }

    private static void ValidateWrittenReportPair(
        FileSnapshot report,
        FileSnapshot sidecar,
        byte[] expectedReportBytes,
        string expectedReportSha256)
    {
        var expectedSidecar = Encoding.UTF8.GetBytes(expectedReportSha256 + "\n");
        if (report.SizeBytes != expectedReportBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(report.Bytes, expectedReportBytes) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(report.Sha256),
                Encoding.ASCII.GetBytes(expectedReportSha256)) ||
            sidecar.SizeBytes != expectedSidecar.Length ||
            !CryptographicOperations.FixedTimeEquals(sidecar.Bytes, expectedSidecar))
        {
            throw Invalid(
                "ZONE_REPORT_WRITE_VERIFICATION_FAILED",
                "Written canonical report or sidecar differs from the verified in-memory result.");
        }
    }

    private static LoadedSchema LoadEmbeddedSchema(string resourceName, string expectedId, string code)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw Invalid(code, $"Embedded schema resource is missing: {resourceName}.");
        if (stream.Length <= 0 || stream.Length > MaximumDocumentBytes)
        {
            throw Invalid(code, "Embedded schema is empty or too large.");
        }
        using var memory = new MemoryStream((int)stream.Length);
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        RejectDuplicateProperties(bytes, code);
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("$id", out var id) || id.GetString() != expectedId ||
                !root.TryGetProperty("x-pal-control-schema-version", out var version) ||
                version.GetString() != ZoneCalibrationConstants.EvidenceSchemaVersion ||
                !root.TryGetProperty("additionalProperties", out var additional) ||
                additional.ValueKind != JsonValueKind.False)
            {
                throw Invalid(code, "Embedded schema id/version/root strictness is invalid.");
            }
        }
        catch (JsonException exception)
        {
            throw Invalid(code, exception.Message);
        }
        return new LoadedSchema(resourceName, HashBytes(bytes), bytes);
    }

    private static LoadedTrustStore LoadTrustStore(string path, string pinnedSha256)
    {
        RequireSha256(pinnedSha256, "trustStoreSha256");
        var snapshot = ReadFileSnapshot(
            ExistingRegularFile(path, "ZONE_TRUST_STORE_NOT_FOUND"),
            MaximumDocumentBytes,
            "ZONE_TRUST_STORE_TOO_LARGE");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(snapshot.Sha256),
                Encoding.ASCII.GetBytes(pinnedSha256)))
        {
            throw Invalid(
                "ZONE_TRUST_STORE_PIN_MISMATCH",
                "Trust-store bytes do not match the externally supplied SHA-256 pin.");
        }
        RejectDuplicateProperties(snapshot.Bytes, "ZONE_TRUST_STORE_INVALID_JSON");
        RejectUnsafeJson(snapshot.Bytes, "trust-store");
        var store = DeserializeStrict<ZoneCalibrationTrustStore>(
            snapshot.Bytes,
            "ZONE_TRUST_STORE_INVALID_JSON");
        if (store.Schema != ZoneCalibrationConstants.TrustStoreSchemaId ||
            store.SchemaVersion != ZoneCalibrationConstants.TrustStoreSchemaVersion ||
            store.CaptureKeys is null || store.ReviewerKeys is null ||
            store.CaptureKeys.Count is < 1 or > 64 || store.ReviewerKeys.Count is < 1 or > 64)
        {
            throw Invalid("ZONE_TRUST_STORE_INVALID", "Trust store schema and non-empty bounded key sets are required.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var capture = ValidateTrustedKeys(store.CaptureKeys, "capture", ids, fingerprints);
        var reviewer = ValidateTrustedKeys(store.ReviewerKeys, "reviewer", ids, fingerprints);
        return new LoadedTrustStore(snapshot, snapshot.Sha256, capture, reviewer, fingerprints);
    }

    private static IReadOnlyDictionary<string, TrustedEvidenceKey> ValidateTrustedKeys(
        IReadOnlyList<TrustedEvidenceKey> keys,
        string role,
        HashSet<string> allIds,
        Dictionary<string, string> fingerprints)
    {
        var result = new Dictionary<string, TrustedEvidenceKey>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (key is null)
            {
                throw Invalid("ZONE_TRUST_STORE_INVALID", $"Null {role} keys are forbidden.");
            }
            RequireIdentifier(key.KeyId, $"{role}Keys[].keyId");
            if (!allIds.Add(key.KeyId))
            {
                throw Invalid("ZONE_TRUST_KEY_DUPLICATE", "Capture and reviewer key ids must be globally unique.");
            }
            if (string.IsNullOrWhiteSpace(key.SubjectId) || !SubjectPattern().IsMatch(key.SubjectId) ||
                IsWeakDigest(key.SubjectId) ||
                string.IsNullOrWhiteSpace(key.PseudonymDomain) ||
                !key.PseudonymDomain.StartsWith("zone-calibration:", StringComparison.Ordinal) ||
                key.Algorithm != ZoneCalibrationConstants.SignatureAlgorithm)
            {
                throw Invalid("ZONE_TRUST_KEY_INVALID", $"Trusted {role} key subject/domain/algorithm is invalid.");
            }
            var validFrom = RequireUtc(key.ValidFrom, $"{role}Keys[].validFrom");
            var expiresAt = RequireUtc(key.ExpiresAt, $"{role}Keys[].expiresAt");
            if (expiresAt <= validFrom || expiresAt - validFrom > TimeSpan.FromDays(730))
            {
                throw Invalid("ZONE_TRUST_KEY_INVALID", "Trusted key validity must be positive and at most 730 days.");
            }
            byte[] canonicalSpki;
            try
            {
                if (string.IsNullOrWhiteSpace(key.PublicKeySpkiBase64) ||
                    key.PublicKeySpkiBase64.Length is < 80 or > 512)
                {
                    throw new FormatException("SPKI base64 length is invalid.");
                }
                var spki = Convert.FromBase64String(key.PublicKeySpkiBase64);
                if (Convert.ToBase64String(spki) != key.PublicKeySpkiBase64)
                {
                    throw new FormatException("SPKI must use canonical base64 without whitespace.");
                }
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(spki, out var read);
                var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
                if (read != spki.Length || ecdsa.KeySize != 256 ||
                    parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                {
                    throw new CryptographicException("Key is not an ECDSA P-256 SPKI key.");
                }
                canonicalSpki = ecdsa.ExportSubjectPublicKeyInfo();
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                throw Invalid("ZONE_TRUST_KEY_INVALID", $"Trusted {role} public key is invalid: {exception.Message}");
            }
            var fingerprint = HashBytes(canonicalSpki);
            if (fingerprints.TryGetValue(fingerprint, out var existingRole))
            {
                throw Invalid(
                    "ZONE_TRUST_KEY_REUSED",
                    $"The same public key cannot be reused across evidence roles ({existingRole}, {role}).");
            }
            fingerprints.Add(fingerprint, role);
            result.Add(key.KeyId, key);
        }
        return result;
    }

    private static string ComputeArtifactManifestSha256(IReadOnlyList<CanonicalArtifactHash> artifacts) =>
        HashUtf8(string.Join('\n', artifacts.Select(artifact =>
            $"{artifact.Id}\n{artifact.Role}\n{artifact.Sha256}\n{artifact.SizeBytes.ToString(CultureInfo.InvariantCulture)}")));

    private static ReviewChallenge BuildReviewChallenge(
        ZoneCalibrationEvidence evidence,
        IReadOnlyList<CanonicalArtifactHash> artifacts,
        string artifactManifestSha256)
    {
        var completeArtifactRecords = evidence.Artifacts
            .OrderBy(artifact => artifact.Id, StringComparer.Ordinal)
            .ToArray();
        var canonicalEvidence = new
        {
            evidence.Schema,
            evidence.SchemaVersion,
            evidence.SchemaSha256,
            evidence.EvidenceMode,
            evidence.CalibrationId,
            evidence.ExpiresAt,
            evidence.Server,
            evidence.Content,
            evidence.Zone,
            evidence.Sampling,
            evidence.Participants,
            evidence.CenterMeasurement,
            evidence.BoundaryPairs,
            evidence.RouteChecks,
            evidence.AccessibilityCheck,
            evidence.RiskCheck,
            reviewIntent = new
            {
                evidence.Review.ReviewedAt,
                evidence.Review.Result,
                evidence.Review.ReviewerKeyId
            },
            artifactManifestSha256,
            artifacts,
            completeArtifactRecords
        };
        var payloadSha256 = HashBytes(JsonSerializer.SerializeToUtf8Bytes(canonicalEvidence, StrictJson));
        var statement = BuildReviewSignatureStatement(
            evidence.SchemaSha256,
            evidence.CalibrationId,
            evidence.ExpiresAt,
            evidence.Server.ServerId,
            evidence.Server.GameBuild,
            evidence.Server.SteamBuild,
            evidence.Content.VersionId,
            evidence.Content.VersionNumber,
            evidence.Content.ContentHash,
            evidence.Zone.ZoneId,
            evidence.Participants.Executor.SubjectId,
            evidence.Participants.Reviewer.SubjectId,
            evidence.Sampling.StartedAt,
            evidence.Sampling.EndedAt,
            evidence.Review.ReviewedAt,
            evidence.Review.Result,
            evidence.Review.ReviewerKeyId,
            payloadSha256,
            artifactManifestSha256);
        return new ReviewChallenge(
            evidence.CalibrationId,
            evidence.Review.ReviewerKeyId,
            payloadSha256,
            artifactManifestSha256,
            Convert.ToBase64String(statement));
    }

    private static byte[] BuildReviewSignatureStatement(
        string evidenceSchemaSha256,
        string calibrationId,
        DateTimeOffset expiresAt,
        string serverId,
        string gameBuild,
        string steamBuild,
        Guid contentVersionId,
        long contentVersionNumber,
        string contentHash,
        string zoneId,
        string executorSubjectId,
        string reviewerSubjectId,
        DateTimeOffset samplingStartedAt,
        DateTimeOffset samplingEndedAt,
        DateTimeOffset reviewedAt,
        string result,
        string reviewerKeyId,
        string evidencePayloadSha256,
        string artifactManifestSha256)
    {
        var lines = new[]
        {
            ZoneCalibrationConstants.ReviewSignatureContext,
            ZoneCalibrationConstants.EvidenceSchemaId,
            ZoneCalibrationConstants.EvidenceSchemaVersion,
            evidenceSchemaSha256,
            calibrationId,
            expiresAt.ToString("O", CultureInfo.InvariantCulture),
            serverId,
            gameBuild,
            steamBuild,
            contentVersionId.ToString("D", CultureInfo.InvariantCulture),
            contentVersionNumber.ToString(CultureInfo.InvariantCulture),
            contentHash,
            zoneId,
            executorSubjectId,
            reviewerSubjectId,
            samplingStartedAt.ToString("O", CultureInfo.InvariantCulture),
            samplingEndedAt.ToString("O", CultureInfo.InvariantCulture),
            reviewedAt.ToString("O", CultureInfo.InvariantCulture),
            result,
            reviewerKeyId,
            evidencePayloadSha256,
            artifactManifestSha256
        };
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    private static void ValidateReport(
        CanonicalZoneCalibrationReport report,
        LoadedSchema reportSchema,
        LoadedSchema evidenceSchema,
        ZoneCalibrationExpectedBinding expected,
        DateTimeOffset now)
    {
        if (report.Center is null || report.Artifacts is null || report.BoundaryCalculations is null ||
            report.RouteCalculations is null || report.CaptureKeyIds is null ||
            report.Artifacts.Any(artifact => artifact is null) ||
            report.BoundaryCalculations.Any(calculation => calculation is null) ||
            report.RouteCalculations.Any(route => route is null))
        {
            throw Invalid("ZONE_REPORT_REQUIRED_FIELD_MISSING", "Canonical report objects and arrays cannot be null.");
        }
        if (report.Schema != ZoneCalibrationConstants.ReportSchemaId ||
            report.SchemaVersion != ZoneCalibrationConstants.EvidenceSchemaVersion ||
            report.SchemaSha256 != reportSchema.Sha256 ||
            report.EvidenceSchemaSha256 != evidenceSchema.Sha256 ||
            report.Canonicalization != ZoneCalibrationConstants.CanonicalizationId ||
            report.Result != "pass")
        {
            throw Invalid("ZONE_REPORT_SCHEMA_MISMATCH", "Report does not bind the embedded official report schema.");
        }
        RequireSha256(report.SourceEvidenceSha256, "report.sourceEvidenceSha256");
        RequireSha256(report.EvidenceSchemaSha256, "report.evidenceSchemaSha256");
        RequireSha256(report.TrustStoreSha256, "report.trustStoreSha256");
        RequireSha256(report.ArtifactSetSha256, "report.artifactSetSha256");
        RequireSha256(report.ReviewEvidencePayloadSha256, "report.reviewEvidencePayloadSha256");
        RequireSha256(report.ReviewArtifactManifestSha256, "report.reviewArtifactManifestSha256");
        RequireIdentifier(report.CalibrationId, "report.calibrationId");
        RequireIdentifier(report.ServerId, "report.serverId");
        RequireConcreteBuild(report.GameBuild, "report.gameBuild");
        RequireRawContentHash(report.ContentHash, "report.contentHash");
        RequireIdentifier(report.ZoneId, "report.zoneId");
        if (string.IsNullOrWhiteSpace(report.ExecutorSubjectId) ||
            string.IsNullOrWhiteSpace(report.ReviewerSubjectId) ||
            !SubjectPattern().IsMatch(report.ExecutorSubjectId) ||
            !SubjectPattern().IsMatch(report.ReviewerSubjectId) ||
            report.ExecutorSubjectId == report.ReviewerSubjectId ||
            !RiskLevels.Contains(report.RiskLevel))
        {
            throw Invalid("ZONE_REPORT_SUBJECT_OR_RISK_INVALID", "Report subjects must be distinct keyed pseudonyms and risk must be classified.");
        }
        if (report.ServerId != expected.ServerId || report.GameBuild != expected.GameBuild ||
            report.SteamBuild != expected.SteamBuild || report.ContentVersionId != expected.ContentVersionId ||
            report.ContentVersionNumber != expected.ContentVersionNumber || report.ContentHash != expected.ContentHash ||
            report.ZoneId != expected.ZoneId)
        {
            throw Invalid("ZONE_REPORT_EXPECTED_BINDING_MISMATCH", "Report differs from the controlled expected binding.");
        }
        if (!ZoneGeometryLimits.IsValid(report.Center.X, report.Center.Y, report.Radius) ||
            report.DistanceFormula != ExtractionZoneGeometry.FormulaId ||
            report.SamplingEndedAt <= report.SamplingStartedAt ||
            report.ReviewedAt < report.SamplingEndedAt || report.ReviewedAt > report.EvidenceExpiresAt ||
            now > report.EvidenceExpiresAt || now - report.SamplingEndedAt > expected.MaximumEvidenceAge)
        {
            throw Invalid("ZONE_REPORT_POLICY_INVALID", "Report geometry, time window, expiry or formula is invalid.");
        }
        if (report.Artifacts.Count is < 31 or > 128 ||
            report.Artifacts.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != report.Artifacts.Count ||
            report.Artifacts.Any(item => !ArtifactRoles.Contains(item.Role) || item.SizeBytes is < 1 or > MaximumArtifactBytes ||
                                         !Sha256Pattern().IsMatch(item.Sha256)) ||
            report.ArtifactSetSha256 != ComputeArtifactManifestSha256(report.Artifacts) ||
            report.ReviewArtifactManifestSha256 != report.ArtifactSetSha256)
        {
            throw Invalid("ZONE_REPORT_ARTIFACT_MANIFEST_INVALID", "Report artifact manifest is incomplete, duplicated or hash-mismatched.");
        }
        if (report.BoundaryCalculations is null || report.BoundaryCalculations.Count < 8 ||
            report.RouteCalculations is null ||
            !report.RouteCalculations.Any(route => route.Kind == "ingress" && route.TransitionVerified &&
                                                  route.InsideQuoteSucceeded && route.OutsideQuoteRejected) ||
            !report.RouteCalculations.Any(route => route.Kind == "egress" && route.TransitionVerified &&
                                                  route.InsideQuoteSucceeded && route.OutsideQuoteRejected) ||
            report.CaptureKeyIds is null || report.CaptureKeyIds.Count == 0 ||
            report.CaptureKeyIds.Any(string.IsNullOrWhiteSpace) ||
            report.CaptureKeyIds.Distinct(StringComparer.Ordinal).Count() != report.CaptureKeyIds.Count ||
            report.CaptureKeyIds.Contains(report.ReviewerKeyId, StringComparer.Ordinal) ||
            report.RiskDisposition is not "approved" and not "acceptable" ||
            !report.AccessibilityVerified || !report.ReturnPathConfirmed ||
            !report.TerrainHazardChecked || !report.HostileExposureChecked || !report.RespawnRouteAvailable)
        {
            throw Invalid("ZONE_REPORT_ASSERTION_INVALID", "Report lacks required boundary, route, quote, risk or independent-key assertions.");
        }
    }

    private static void VerifyReportReviewSignature(
        CanonicalZoneCalibrationReport report,
        LoadedTrustStore trustStore)
    {
        if (!trustStore.ReviewerKeys.TryGetValue(report.ReviewerKeyId, out var reviewerKey) || reviewerKey.Revoked ||
            reviewerKey.SubjectId != report.ReviewerSubjectId ||
            reviewerKey.PseudonymDomain != "zone-calibration:" + report.CalibrationId ||
            report.ReviewedAt < reviewerKey.ValidFrom || report.ReviewedAt > reviewerKey.ExpiresAt ||
            report.ReviewSignatureAlgorithm != ZoneCalibrationConstants.SignatureAlgorithm)
        {
            throw Invalid("ZONE_REPORT_REVIEW_KEY_UNTRUSTED", "Report reviewer key is untrusted, expired or campaign-mismatched.");
        }
        foreach (var keyId in report.CaptureKeyIds)
        {
            if (!trustStore.CaptureKeys.TryGetValue(keyId, out var captureKey) || captureKey.Revoked ||
                captureKey.SubjectId != report.ExecutorSubjectId ||
                captureKey.PseudonymDomain != "zone-calibration:" + report.CalibrationId ||
                captureKey.KeyId == reviewerKey.KeyId)
            {
                throw Invalid("ZONE_REPORT_CAPTURE_KEY_UNTRUSTED", "Report references an untrusted or non-independent capture key.");
            }
        }
        var statement = BuildReviewSignatureStatement(
            report.EvidenceSchemaSha256,
            report.CalibrationId,
            report.EvidenceExpiresAt,
            report.ServerId,
            report.GameBuild,
            report.SteamBuild,
            report.ContentVersionId,
            report.ContentVersionNumber,
            report.ContentHash,
            report.ZoneId,
            report.ExecutorSubjectId,
            report.ReviewerSubjectId,
            report.SamplingStartedAt,
            report.SamplingEndedAt,
            report.ReviewedAt,
            report.Result,
            report.ReviewerKeyId,
            report.ReviewEvidencePayloadSha256,
            report.ReviewArtifactManifestSha256);
        VerifySignature(reviewerKey, statement, report.ReviewSignatureBase64, "ZONE_REPORT_REVIEW_SIGNATURE_INVALID");
    }

    public static string ComputeFileSha256(string path)
    {
        var fullPath = ExistingRegularFile(path, "ZONE_HASH_INPUT_NOT_FOUND");
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            hasher.AppendData(buffer, 0, read);
        }
        return "sha256:" + Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static void ValidateRequiredShape(ZoneCalibrationEvidence evidence)
    {
        if (evidence.Server is null || evidence.Content is null || evidence.Zone is null ||
            evidence.Zone.Center is null || evidence.Sampling is null ||
            evidence.Participants is null || evidence.Participants.Executor is null ||
            evidence.Participants.Reviewer is null || evidence.CenterMeasurement is null ||
            evidence.BoundaryPairs is null || evidence.RouteChecks is null ||
            evidence.AccessibilityCheck is null || evidence.RiskCheck is null ||
            evidence.Review is null || evidence.Artifacts is null ||
            evidence.BoundaryPairs.Any(pair => pair is null || pair.Inside is null || pair.Outside is null) ||
            evidence.RouteChecks.Any(route => route is null || route.InsideQuote is null || route.OutsideQuote is null) ||
            evidence.Artifacts.Any(artifact => artifact is null))
        {
            throw Invalid(
                "ZONE_EVIDENCE_REQUIRED_FIELD_MISSING",
                "All v1 evidence objects, arrays and measurement pairs are required and cannot be null.");
        }
    }

    private static void ValidateHeader(ZoneCalibrationEvidence evidence, LoadedSchema schema)
    {
        if (evidence.Schema != ZoneCalibrationConstants.EvidenceSchemaId ||
            evidence.SchemaVersion != ZoneCalibrationConstants.EvidenceSchemaVersion ||
            evidence.SchemaSha256 != schema.Sha256)
        {
            throw Invalid(
                "ZONE_EVIDENCE_SCHEMA_MISMATCH",
                "Evidence must bind the exact loaded zone-calibration v1 schema and SHA-256.");
        }
        RequireSha256(evidence.SchemaSha256, "schemaSha256");
        if (evidence.EvidenceMode != ZoneCalibrationConstants.EvidenceMode)
        {
            throw Invalid(
                "ZONE_EVIDENCE_NOT_LIVE",
                "Only evidenceMode 'live' is accepted; simulated or template data cannot calibrate a production zone.");
        }
        RequireIdentifier(evidence.CalibrationId, "calibrationId");
    }

    private static void ValidateServerAndContent(ZoneCalibrationEvidence evidence)
    {
        RequireIdentifier(evidence.Server.ServerId, "server.serverId");
        RequireConcreteBuild(evidence.Server.GameBuild, "server.gameBuild");
        if (string.IsNullOrWhiteSpace(evidence.Server.SteamBuild) ||
            !SteamBuildPattern().IsMatch(evidence.Server.SteamBuild))
        {
            throw Invalid(
                "ZONE_STEAM_BUILD_INVALID",
                "server.steamBuild must be the observed 6..18 digit Steam build id.");
        }
        RequireIdentifier(evidence.Server.ArtifactId, "server.artifactId");
        if (evidence.Content.VersionId == Guid.Empty || evidence.Content.VersionNumber <= 0)
        {
            throw Invalid(
                "ZONE_CONTENT_VERSION_INVALID",
                "content.versionId and content.versionNumber must identify a published content version.");
        }
        RequireRawContentHash(evidence.Content.ContentHash, "content.contentHash");
        RequireIdentifier(evidence.Content.ArtifactId, "content.artifactId");
    }

    private static void ValidateExpectedBinding(
        ZoneCalibrationEvidence evidence,
        ZoneCalibrationExpectedBinding expected,
        DateTimeOffset now)
    {
        if (expected is null || expected.ContentVersionId == Guid.Empty ||
            expected.ContentVersionNumber <= 0 ||
            expected.MaximumEvidenceAge < TimeSpan.FromMinutes(1) ||
            expected.MaximumEvidenceAge > TimeSpan.FromDays(30))
        {
            throw Invalid(
                "ZONE_EXPECTED_POLICY_INVALID",
                "A controlled expected binding and a maximum evidence age from one minute through 30 days are required.");
        }
        RequireIdentifier(expected.ServerId, "expected.serverId");
        RequireConcreteBuild(expected.GameBuild, "expected.gameBuild");
        if (string.IsNullOrWhiteSpace(expected.SteamBuild) || !SteamBuildPattern().IsMatch(expected.SteamBuild))
        {
            throw Invalid("ZONE_EXPECTED_POLICY_INVALID", "Expected Steam build must be a 6..18 digit build id.");
        }
        RequireRawContentHash(expected.ContentHash, "expected.contentHash");
        RequireIdentifier(expected.ZoneId, "expected.zoneId");
        if (evidence.Server.ServerId != expected.ServerId ||
            evidence.Server.GameBuild != expected.GameBuild ||
            evidence.Server.SteamBuild != expected.SteamBuild ||
            evidence.Content.VersionId != expected.ContentVersionId ||
            evidence.Content.VersionNumber != expected.ContentVersionNumber ||
            evidence.Content.ContentHash != expected.ContentHash ||
            evidence.Zone.ZoneId != expected.ZoneId)
        {
            throw Invalid(
                "ZONE_EXPECTED_BINDING_MISMATCH",
                "Evidence does not exactly match the externally controlled server/game/Steam/content/zone binding.");
        }
        var expiresAt = RequireUtc(evidence.ExpiresAt, "expiresAt");
        if (expiresAt <= evidence.Sampling.EndedAt || expiresAt > evidence.Sampling.EndedAt + expected.MaximumEvidenceAge ||
            now > expiresAt || now - evidence.Sampling.EndedAt > expected.MaximumEvidenceAge)
        {
            throw Invalid(
                "ZONE_EVIDENCE_EXPIRED",
                "Evidence is expired, too old for the controlled maximum age, or declares an excessive expiry window.");
        }
    }

    private static void ValidateZone(ZoneAuthority zone)
    {
        RequireIdentifier(zone.ZoneId, "zone.zoneId");
        if (zone.Authority != "published-content-version")
        {
            throw Invalid(
                "ZONE_AUTHORITY_INVALID",
                "zone.authority must be 'published-content-version'.");
        }
        if (!ZoneGeometryLimits.IsValid(zone.Center.X, zone.Center.Y, zone.Radius))
        {
            throw Invalid(
                "ZONE_RADIUS_INVALID",
                $"Zone center must be finite and radius must be in (0, {ZoneGeometryLimits.MaximumRadius:R}].");
        }
        if (!double.IsFinite(zone.CenterTolerance) || zone.CenterTolerance <= 0d ||
            zone.CenterTolerance > zone.Radius * 0.1d)
        {
            throw Invalid(
                "ZONE_CENTER_TOLERANCE_INVALID",
                "zone.centerTolerance must be positive and no greater than 10% of radius.");
        }
        ValidateMargin(zone.InsideSafetyMargin, zone.Radius, "inside");
        ValidateMargin(zone.OutsideSafetyMargin, zone.Radius, "outside");
    }

    private static void ValidateMargin(double margin, double radius, string kind)
    {
        var minimum = Math.Max(1d, radius * 0.02d);
        if (!double.IsFinite(margin) || margin < minimum || margin > radius * 0.25d)
        {
            throw Invalid(
                "ZONE_SAFETY_MARGIN_INVALID",
                $"zone.{kind}SafetyMargin must be at least max(1, 2% of radius) and no more than 25% of radius.");
        }
    }

    private static void ValidateSamplingAndParticipants(
        ZoneCalibrationEvidence evidence,
        DateTimeOffset now,
        TimeSpan maximumEvidenceAge)
    {
        var started = RequireUtc(evidence.Sampling.StartedAt, "sampling.startedAt");
        var ended = RequireUtc(evidence.Sampling.EndedAt, "sampling.endedAt");
        if (ended <= started || ended - started > TimeSpan.FromHours(8) || ended > now.AddMinutes(5))
        {
            throw Invalid(
                "ZONE_SAMPLING_WINDOW_INVALID",
                "Sampling must be a completed UTC window no longer than eight hours and not in the future.");
        }
        if (evidence.Sampling.CoordinateSource != ZoneCalibrationConstants.AuthoritativePositionSource)
        {
            throw Invalid(
                "ZONE_COORDINATE_SOURCE_INVALID",
                "sampling.coordinateSource must be the authoritative live Palworld position source.");
        }
        ValidateSubject(evidence.Participants.Executor, "participants.executor", "executor");
        ValidateSubject(evidence.Participants.Reviewer, "participants.reviewer", "reviewer");
        RequireIdentifier(evidence.Review.ReviewerKeyId, "review.reviewerKeyId");
        var requiredDomain = "zone-calibration:" + evidence.CalibrationId;
        if (evidence.Participants.Executor.PseudonymDomain != requiredDomain ||
            evidence.Participants.Reviewer.PseudonymDomain != requiredDomain)
        {
            throw Invalid(
                "ZONE_SUBJECT_DOMAIN_MISMATCH",
                "Executor and reviewer pseudonyms must be isolated to zone-calibration:<calibrationId>.");
        }
        if (evidence.Participants.Executor.SubjectId == evidence.Participants.Reviewer.SubjectId)
        {
            throw Invalid(
                "ZONE_REVIEW_NOT_INDEPENDENT",
                "Executor and reviewer must be different keyed pseudonymous subjects.");
        }
        if (now - ended > maximumEvidenceAge)
        {
            throw Invalid("ZONE_EVIDENCE_TOO_OLD", "The completed sampling window exceeds the controlled maximum evidence age.");
        }
    }

    private static Dictionary<string, VerifiedArtifact> ValidateArtifacts(
        ZoneCalibrationEvidence evidence,
        string evidenceRoot,
        DateTimeOffset now)
    {
        if (evidence.Artifacts.Count is < 31 or > 128)
        {
            throw Invalid(
                "ZONE_ARTIFACT_COUNT_INVALID",
                "A calibration bundle must contain 31..128 signed raw JSON artifacts.");
        }
        var result = new Dictionary<string, VerifiedArtifact>(StringComparer.Ordinal);
        var paths = new HashSet<string>(PathComparer());
        foreach (var artifact in evidence.Artifacts)
        {
            RequireIdentifier(artifact.Id, "artifacts[].id");
            if (!ArtifactRoles.Contains(artifact.Role))
            {
                throw Invalid(
                    "ZONE_ARTIFACT_ROLE_INVALID",
                    $"Artifact '{artifact.Id}' has unsupported role '{artifact.Role}'.");
            }
            if (artifact.MediaType != "application/json")
            {
                throw Invalid(
                    "ZONE_ARTIFACT_MEDIA_TYPE_INVALID",
                    "Only strict, machine-readable application/json artifacts are accepted so identity leakage can be checked deterministically.");
            }
            RequireIdentifier(artifact.Producer, $"artifacts[{artifact.Id}].producer");
            if (artifact.CaptureMode != "live")
            {
                throw Invalid(
                    "ZONE_ARTIFACT_NOT_LIVE",
                    $"Artifact '{artifact.Id}' must be a live capture.");
            }
            RequireUtc(artifact.CapturedAt, $"artifacts[{artifact.Id}].capturedAt");
            if (artifact.CapturedAt > evidence.Review.ReviewedAt || artifact.CapturedAt > now.AddMinutes(5))
            {
                throw Invalid(
                    "ZONE_ARTIFACT_TIME_INVALID",
                    $"Artifact '{artifact.Id}' was captured after review or in the future.");
            }
            RequirePortablePath(artifact.Path, $"artifacts[{artifact.Id}].path");
            var fullPath = ResolveContainedFile(evidenceRoot, artifact.Path);
            if (!paths.Add(fullPath))
            {
                throw Invalid(
                    "ZONE_ARTIFACT_PATH_DUPLICATE",
                    $"Artifact path '{artifact.Path}' is used more than once.");
            }
            var snapshot = ReadFileSnapshot(fullPath, MaximumArtifactBytes, "ZONE_ARTIFACT_TOO_LARGE");
            if (snapshot.SizeBytes <= 0 || snapshot.SizeBytes != artifact.SizeBytes)
            {
                throw Invalid(
                    "ZONE_ARTIFACT_SIZE_MISMATCH",
                    $"Artifact '{artifact.Id}' size is empty or differs from the evidence record.");
            }
            RequireSha256(artifact.Sha256, $"artifacts[{artifact.Id}].sha256");
            if (snapshot.Sha256 != artifact.Sha256)
            {
                throw Invalid(
                    "ZONE_ARTIFACT_HASH_MISMATCH",
                    $"Artifact '{artifact.Id}' SHA-256 differs from the raw file.");
            }
            RejectDuplicateProperties(snapshot.Bytes, "ZONE_RAW_ARTIFACT_INVALID_JSON");
            RejectUnsafeJson(snapshot.Bytes, $"artifact:{artifact.Id}");
            if (!result.TryAdd(artifact.Id, new VerifiedArtifact(artifact, snapshot)))
            {
                throw Invalid(
                    "ZONE_ARTIFACT_ID_DUPLICATE",
                    $"Artifact id '{artifact.Id}' occurs more than once.");
            }
        }
        return result;
    }

    private static void ValidateServerBuildCapture(
        ZoneCalibrationEvidence evidence,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references,
        CaptureValidationContext captureContext)
    {
        var artifact = RequireArtifact(
            evidence.Server.ArtifactId,
            "server-build-binding",
            artifacts,
            references);
        var raw = DeserializeSignedCapture<RawServerBuildCapture>(artifact, captureContext, "ZONE_RAW_SERVER_INVALID");
        if (raw.RecordType != "pal-control-live-server-build-v1" ||
            raw.ServerId != evidence.Server.ServerId ||
            raw.GameBuild != evidence.Server.GameBuild ||
            raw.SteamBuild != evidence.Server.SteamBuild ||
            raw.Source != ZoneCalibrationConstants.AuthoritativeServerBuildSource ||
            raw.CapturedAt != artifact.Metadata.CapturedAt)
        {
            throw Invalid(
                "ZONE_SERVER_BINDING_MISMATCH",
                "The raw runtime build artifact does not exactly bind serverId, game build and Steam build.");
        }
        RequireWithinSampling(raw.CapturedAt, evidence.Sampling, "server build capture");
    }

    private static void ValidateContentZoneCapture(
        ZoneCalibrationEvidence evidence,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references,
        CaptureValidationContext captureContext)
    {
        var artifact = RequireArtifact(
            evidence.Content.ArtifactId,
            "content-zone-binding",
            artifacts,
            references);
        var raw = DeserializeSignedCapture<RawContentZoneCapture>(artifact, captureContext, "ZONE_RAW_CONTENT_INVALID");
        if (raw.Center is null || raw.RecordType != "pal-control-live-content-zone-v1" ||
            raw.ServerId != evidence.Server.ServerId ||
            raw.VersionId != evidence.Content.VersionId ||
            raw.VersionNumber != evidence.Content.VersionNumber ||
            raw.ContentHash != evidence.Content.ContentHash ||
            raw.ZoneId != evidence.Zone.ZoneId ||
            raw.Source != ZoneCalibrationConstants.AuthoritativeContentSource ||
            raw.CapturedAt != artifact.Metadata.CapturedAt ||
            !ApproximatelyEqual(raw.Center.X, evidence.Zone.Center.X) ||
            !ApproximatelyEqual(raw.Center.Y, evidence.Zone.Center.Y) ||
            !ApproximatelyEqual(raw.Radius, evidence.Zone.Radius))
        {
            throw Invalid(
                "ZONE_CONTENT_BINDING_MISMATCH",
                "The current-content artifact does not exactly bind the published version/hash and authoritative zone geometry.");
        }
        RequireWithinSampling(raw.CapturedAt, evidence.Sampling, "content zone capture");
    }

    private static double ValidateCenterCapture(
        ZoneCalibrationEvidence evidence,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references,
        CaptureValidationContext captureContext)
    {
        var raw = ValidatePositionCapture(
            evidence,
            evidence.CenterMeasurement,
            artifacts,
            references,
            captureContext);
        var distance = ExtractionZoneGeometry.Distance(
            raw.X,
            raw.Y,
            evidence.Zone.Center.X,
            evidence.Zone.Center.Y);
        if (distance > evidence.Zone.CenterTolerance)
        {
            throw Invalid(
                "ZONE_CENTER_MEASUREMENT_OUT_OF_TOLERANCE",
                $"Center measurement is {distance:R} map units from the authoritative center; tolerance is {evidence.Zone.CenterTolerance:R}.");
        }
        return distance;
    }

    private static IReadOnlyList<BoundaryCalculation> ValidateBoundaryCaptures(
        ZoneCalibrationEvidence evidence,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references,
        CaptureValidationContext captureContext)
    {
        if (evidence.BoundaryPairs.Count is < 8 or > 36)
        {
            throw Invalid(
                "ZONE_BOUNDARY_DIRECTION_COUNT_INVALID",
                "At least 8 and at most 36 inside/outside boundary direction pairs are required.");
        }
        var ordered = evidence.BoundaryPairs.OrderBy(pair => pair.DirectionDegrees).ToArray();
        if (ordered.Any(pair => !double.IsFinite(pair.DirectionDegrees) ||
                                pair.DirectionDegrees < 0d || pair.DirectionDegrees >= 360d))
        {
            throw Invalid(
                "ZONE_BOUNDARY_DIRECTION_INVALID",
                "directionDegrees must be finite and in [0, 360).");
        }
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].DirectionDegrees - ordered[index - 1].DirectionDegrees < 1d)
            {
                throw Invalid(
                    "ZONE_BOUNDARY_DIRECTION_DUPLICATE",
                    "Boundary directions must be distinct by at least one degree.");
            }
        }
        var expectedGap = 360d / ordered.Length;
        var allowedGapError = Math.Min(5d, expectedGap * 0.15d);
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = ordered[index].DirectionDegrees;
            var next = index == ordered.Length - 1
                ? ordered[0].DirectionDegrees + 360d
                : ordered[index + 1].DirectionDegrees;
            if (Math.Abs((next - current) - expectedGap) > allowedGapError)
            {
                throw Invalid(
                    "ZONE_BOUNDARY_DIRECTIONS_NOT_UNIFORM",
                    $"Boundary directions must cover 360 degrees uniformly; expected gap {expectedGap:R} ± {allowedGapError:R} degrees.");
            }
        }

        var calculations = new List<BoundaryCalculation>(ordered.Length);
        foreach (var pair in ordered)
        {
            var insideArtifact = ValidatePositionCapture(
                evidence,
                pair.Inside,
                artifacts,
                references,
                captureContext);
            var outsideArtifact = ValidatePositionCapture(
                evidence,
                pair.Outside,
                artifacts,
                references,
                captureContext);
            var insideDistance = ExtractionZoneGeometry.Distance(
                pair.Inside.X,
                pair.Inside.Y,
                evidence.Zone.Center.X,
                evidence.Zone.Center.Y);
            var outsideDistance = ExtractionZoneGeometry.Distance(
                pair.Outside.X,
                pair.Outside.Y,
                evidence.Zone.Center.X,
                evidence.Zone.Center.Y);
            var insideMinimum = evidence.Zone.Radius - 2d * evidence.Zone.InsideSafetyMargin;
            var insideMaximum = evidence.Zone.Radius - evidence.Zone.InsideSafetyMargin;
            var outsideMinimum = evidence.Zone.Radius + evidence.Zone.OutsideSafetyMargin;
            var outsideMaximum = evidence.Zone.Radius + 2d * evidence.Zone.OutsideSafetyMargin;
            if (!ExtractionZoneGeometry.IsInside(
                    pair.Inside.X,
                    pair.Inside.Y,
                    evidence.Zone.Center.X,
                    evidence.Zone.Center.Y,
                    evidence.Zone.Radius) ||
                insideDistance < insideMinimum || insideDistance > insideMaximum)
            {
                throw Invalid(
                    "ZONE_INSIDE_SAFETY_MARGIN_FAILED",
                    $"Inside point at {pair.DirectionDegrees:R} degrees must lie in the boundary band [{insideMinimum:R}, {insideMaximum:R}].");
            }
            if (ExtractionZoneGeometry.IsInside(
                    pair.Outside.X,
                    pair.Outside.Y,
                    evidence.Zone.Center.X,
                    evidence.Zone.Center.Y,
                    evidence.Zone.Radius) ||
                outsideDistance < outsideMinimum || outsideDistance > outsideMaximum)
            {
                throw Invalid(
                    "ZONE_OUTSIDE_SAFETY_MARGIN_FAILED",
                    $"Outside point at {pair.DirectionDegrees:R} degrees must lie in the boundary band [{outsideMinimum:R}, {outsideMaximum:R}].");
            }
            ValidateBearing(evidence.Zone, pair.DirectionDegrees, pair.Inside, "inside");
            ValidateBearing(evidence.Zone, pair.DirectionDegrees, pair.Outside, "outside");
            if (pair.Outside.CapturedAt < pair.Inside.CapturedAt ||
                pair.Outside.CapturedAt - pair.Inside.CapturedAt > TimeSpan.FromMinutes(10))
            {
                throw Invalid(
                    "ZONE_BOUNDARY_PAIR_TIME_INVALID",
                    "Each outside capture must follow its paired inside capture within ten minutes.");
            }
            calculations.Add(new BoundaryCalculation(
                RoundMetric(pair.DirectionDegrees),
                RoundMetric(insideDistance),
                RoundMetric(evidence.Zone.Radius - insideDistance),
                RoundMetric(outsideDistance),
                RoundMetric(outsideDistance - evidence.Zone.Radius),
                artifacts[pair.Inside.ArtifactId].Metadata.Sha256,
                artifacts[pair.Outside.ArtifactId].Metadata.Sha256));
        }
        return calculations;
    }

    private static RawPositionCapture ValidatePositionCapture(
        ZoneCalibrationEvidence evidence,
        PositionMeasurement measurement,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references,
        CaptureValidationContext captureContext)
    {
        RequireCoordinate(measurement.X, "position.x");
        RequireCoordinate(measurement.Y, "position.y");
        RequireWithinSampling(measurement.CapturedAt, evidence.Sampling, "position capture");
        var artifact = RequireArtifact(
            measurement.ArtifactId,
            "position-capture",
            artifacts,
            references);
        var raw = DeserializeSignedCapture<RawPositionCapture>(artifact, captureContext, "ZONE_RAW_POSITION_INVALID");
        if (raw.RecordType != "pal-control-live-position-v1" ||
            raw.ServerId != evidence.Server.ServerId ||
            raw.ZoneId != evidence.Zone.ZoneId ||
            raw.Source != ZoneCalibrationConstants.AuthoritativePositionSource ||
            raw.CapturedAt != measurement.CapturedAt ||
            raw.CapturedAt != artifact.Metadata.CapturedAt ||
            !ApproximatelyEqual(raw.X, measurement.X) ||
            !ApproximatelyEqual(raw.Y, measurement.Y))
        {
            throw Invalid(
                "ZONE_POSITION_ARTIFACT_MISMATCH",
                $"Position artifact '{measurement.ArtifactId}' does not exactly match its sanitized authoritative measurement.");
        }
        return raw;
    }

    private static IReadOnlyList<RouteCalculation> ValidateRoutes(
        ZoneCalibrationEvidence evidence,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references,
        CaptureValidationContext captureContext)
    {
        if (evidence.RouteChecks.Count is < 2 or > 8)
        {
            throw Invalid(
                "ZONE_ROUTE_COUNT_INVALID",
                "Two to eight live route checks are required.");
        }
        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var calculations = new List<RouteCalculation>(evidence.RouteChecks.Count);
        foreach (var route in evidence.RouteChecks)
        {
            RequireIdentifier(route.RouteId, "routeChecks[].routeId");
            if (!routeIds.Add(route.RouteId) || route.Kind is not "ingress" and not "egress")
            {
                throw Invalid(
                    "ZONE_ROUTE_ID_OR_KIND_INVALID",
                    "Route ids must be unique and route kind must be ingress or egress.");
            }
            kinds.Add(route.Kind);
            RequireWithinSampling(route.StartedAt, evidence.Sampling, "route start");
            RequireWithinSampling(route.EndedAt, evidence.Sampling, "route end");
            if (route.EndedAt <= route.StartedAt || route.EndedAt - route.StartedAt > TimeSpan.FromHours(2) ||
                !route.Reachable || !route.Uninterrupted)
            {
                throw Invalid(
                    "ZONE_ROUTE_CHECK_FAILED",
                    $"Route '{route.RouteId}' must be reachable, uninterrupted and complete within two hours.");
            }
            var artifact = RequireArtifact(route.ArtifactId, "route-capture", artifacts, references);
            var raw = DeserializeSignedCapture<RawRouteCapture>(artifact, captureContext, "ZONE_RAW_ROUTE_INVALID");
            if (raw.RecordType != "pal-control-live-route-v1" ||
                raw.ServerId != evidence.Server.ServerId || raw.ZoneId != evidence.Zone.ZoneId ||
                raw.RouteId != route.RouteId || raw.Kind != route.Kind ||
                raw.StartedAt != route.StartedAt || raw.EndedAt != route.EndedAt ||
                raw.Reachable != route.Reachable || raw.Uninterrupted != route.Uninterrupted ||
                raw.Source != ZoneCalibrationConstants.AuthoritativeObservationSource ||
                artifact.Metadata.CapturedAt != route.EndedAt)
            {
                throw Invalid(
                    "ZONE_ROUTE_ARTIFACT_MISMATCH",
                    $"Route artifact '{route.ArtifactId}' does not match the verified route check.");
            }
            if (raw.Trace is null || raw.Trace.Count is < 3 or > 512)
            {
                throw Invalid("ZONE_ROUTE_TRACE_INVALID", "Each route must contain 3..512 authoritative coordinate samples.");
            }
            for (var index = 0; index < raw.Trace.Count; index++)
            {
                var point = raw.Trace[index];
                if (point is null)
                {
                    throw Invalid("ZONE_ROUTE_TRACE_INVALID", "Route trace points cannot be null.");
                }
                RequireCoordinate(point.X, "route.trace[].x");
                RequireCoordinate(point.Y, "route.trace[].y");
                RequireWithinSampling(point.CapturedAt, evidence.Sampling, "route trace");
                if (point.CapturedAt < route.StartedAt || point.CapturedAt > route.EndedAt ||
                    (index > 0 && point.CapturedAt <= raw.Trace[index - 1].CapturedAt))
                {
                    throw Invalid(
                        "ZONE_ROUTE_TRACE_TIME_INVALID",
                        "Route trace timestamps must be strictly increasing inside the route window.");
                }
            }
            var first = raw.Trace[0];
            var last = raw.Trace[^1];
            var firstInside = ExtractionZoneGeometry.IsInside(
                first.X, first.Y, evidence.Zone.Center.X, evidence.Zone.Center.Y, evidence.Zone.Radius);
            var lastInside = ExtractionZoneGeometry.IsInside(
                last.X, last.Y, evidence.Zone.Center.X, evidence.Zone.Center.Y, evidence.Zone.Radius);
            var transitionValid = route.Kind == "ingress"
                ? !firstInside && lastInside
                : firstInside && !lastInside;
            if (!transitionValid)
            {
                throw Invalid(
                    "ZONE_ROUTE_TRANSITION_INVALID",
                    $"Route '{route.RouteId}' does not prove the required {route.Kind} outside/inside transition.");
            }
            var insideAnchor = route.Kind == "ingress" ? last : first;
            var outsideAnchor = route.Kind == "ingress" ? first : last;
            ValidateQuoteAttempt(
                evidence, route.RouteId, route.InsideQuote, insideAnchor, true,
                artifacts, references, captureContext);
            ValidateQuoteAttempt(
                evidence, route.RouteId, route.OutsideQuote, outsideAnchor, false,
                artifacts, references, captureContext);
            calculations.Add(new RouteCalculation(
                route.RouteId,
                route.Kind,
                raw.Trace.Count,
                true,
                true,
                true));
        }
        if (!kinds.SetEquals(["ingress", "egress"]))
        {
            throw Invalid(
                "ZONE_ROUTE_COVERAGE_INCOMPLETE",
                "At least one successful ingress and one successful egress route are required.");
        }
        return calculations.OrderBy(value => value.RouteId, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateQuoteAttempt(
        ZoneCalibrationEvidence evidence,
        string routeId,
        QuoteAttemptBinding binding,
        RouteTracePoint anchor,
        bool expectInside,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references,
        CaptureValidationContext captureContext)
    {
        var requestArtifact = RequireArtifact(
            binding.RequestArtifactId,
            "quote-request-capture",
            artifacts,
            references);
        var responseArtifact = RequireArtifact(
            binding.ResponseArtifactId,
            "quote-response-capture",
            artifacts,
            references);
        var request = DeserializeSignedCapture<RawQuoteRequestCapture>(
            requestArtifact,
            captureContext,
            "ZONE_RAW_QUOTE_REQUEST_INVALID");
        var response = DeserializeSignedCapture<RawQuoteResponseCapture>(
            responseArtifact,
            captureContext,
            "ZONE_RAW_QUOTE_RESPONSE_INVALID");
        if (request.Position is null || request.RecordType != "pal-control-live-quote-request-v1" ||
            response.RecordType != "pal-control-live-quote-response-v1" ||
            request.ServerId != evidence.Server.ServerId || response.ServerId != evidence.Server.ServerId ||
            request.ZoneId != evidence.Zone.ZoneId || response.ZoneId != evidence.Zone.ZoneId ||
            request.AttemptId != response.AttemptId || !request.AttemptId.StartsWith(routeId + "-", StringComparison.Ordinal) ||
            request.Source != ZoneCalibrationConstants.AuthoritativeQuoteSource ||
            response.Source != ZoneCalibrationConstants.AuthoritativeQuoteSource ||
            request.RequestedAt != requestArtifact.Metadata.CapturedAt ||
            response.RespondedAt != responseArtifact.Metadata.CapturedAt ||
            response.RespondedAt < request.RequestedAt ||
            response.RespondedAt - request.RequestedAt > TimeSpan.FromMinutes(2) ||
            !ApproximatelyEqual(request.Position.X, anchor.X) ||
            !ApproximatelyEqual(request.Position.Y, anchor.Y))
        {
            throw Invalid(
                "ZONE_QUOTE_BINDING_MISMATCH",
                "Signed quote request/response must bind the route anchor, attempt, server, zone and response time.");
        }
        RequireWithinSampling(request.RequestedAt, evidence.Sampling, "quote request");
        RequireWithinSampling(response.RespondedAt, evidence.Sampling, "quote response");
        var observedInside = ExtractionZoneGeometry.IsInside(
            request.Position.X,
            request.Position.Y,
            evidence.Zone.Center.X,
            evidence.Zone.Center.Y,
            evidence.Zone.Radius);
        if (observedInside != expectInside)
        {
            throw Invalid("ZONE_QUOTE_POSITION_INVALID", "Quote position does not match the required inside/outside assertion.");
        }
        if (expectInside)
        {
            if (response.HttpStatus is < 200 or > 299 || response.Result != "success" || response.ErrorCode is not null)
            {
                throw Invalid("ZONE_INSIDE_QUOTE_FAILED", "The inside quote must succeed without an error code.");
            }
        }
        else if (response.HttpStatus != 409 || response.Result != "rejected" ||
                 response.ErrorCode != "PLAYER_OUTSIDE_EXTRACTION_ZONE")
        {
            throw Invalid(
                "ZONE_OUTSIDE_QUOTE_NOT_REJECTED",
                "The outside quote must fail closed with PLAYER_OUTSIDE_EXTRACTION_ZONE and HTTP 409.");
        }
    }

    private static void ValidateAccessibility(
        ZoneCalibrationEvidence evidence,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references,
        CaptureValidationContext captureContext)
    {
        var check = evidence.AccessibilityCheck;
        RequireWithinSampling(check.CheckedAt, evidence.Sampling, "accessibility check");
        if (!check.Accessible || !check.ObstructionFree || !check.ReturnPathConfirmed)
        {
            throw Invalid(
                "ZONE_ACCESSIBILITY_CHECK_FAILED",
                "Accessibility must prove entry, an obstruction-free operating area and a confirmed return path.");
        }
        var artifact = RequireArtifact(check.ArtifactId, "accessibility-capture", artifacts, references);
        var raw = DeserializeSignedCapture<RawAccessibilityCapture>(artifact, captureContext, "ZONE_RAW_ACCESSIBILITY_INVALID");
        if (raw.RecordType != "pal-control-live-accessibility-v1" ||
            raw.ServerId != evidence.Server.ServerId || raw.ZoneId != evidence.Zone.ZoneId ||
            raw.CheckedAt != check.CheckedAt || raw.CheckedAt != artifact.Metadata.CapturedAt ||
            raw.Accessible != check.Accessible || raw.ObstructionFree != check.ObstructionFree ||
            raw.ReturnPathConfirmed != check.ReturnPathConfirmed ||
            raw.Source != ZoneCalibrationConstants.AuthoritativeObservationSource)
        {
            throw Invalid(
                "ZONE_ACCESSIBILITY_ARTIFACT_MISMATCH",
                "Accessibility artifact does not match the verified live observation.");
        }
    }

    private static void ValidateRisk(
        ZoneCalibrationEvidence evidence,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references,
        CaptureValidationContext captureContext)
    {
        var check = evidence.RiskCheck;
        RequireWithinSampling(check.CheckedAt, evidence.Sampling, "risk check");
        if (!RiskLevels.Contains(check.RiskLevel) || check.Disposition is not "approved" and not "acceptable" ||
            !check.TerrainHazardChecked ||
            !check.HostileExposureChecked || !check.RespawnRouteAvailable)
        {
            throw Invalid(
                "ZONE_RISK_CHECK_FAILED",
                "Risk review must classify risk and confirm terrain, hostile exposure and a respawn route were checked.");
        }
        var artifact = RequireArtifact(check.ArtifactId, "risk-capture", artifacts, references);
        var raw = DeserializeSignedCapture<RawRiskCapture>(artifact, captureContext, "ZONE_RAW_RISK_INVALID");
        if (raw.RecordType != "pal-control-live-risk-v1" ||
            raw.ServerId != evidence.Server.ServerId || raw.ZoneId != evidence.Zone.ZoneId ||
            raw.CheckedAt != check.CheckedAt || raw.CheckedAt != artifact.Metadata.CapturedAt ||
            raw.RiskLevel != check.RiskLevel ||
            raw.Disposition != check.Disposition ||
            raw.TerrainHazardChecked != check.TerrainHazardChecked ||
            raw.HostileExposureChecked != check.HostileExposureChecked ||
            raw.RespawnRouteAvailable != check.RespawnRouteAvailable ||
            raw.Source != ZoneCalibrationConstants.AuthoritativeObservationSource)
        {
            throw Invalid(
                "ZONE_RISK_ARTIFACT_MISMATCH",
                "Risk artifact does not match the verified live observation.");
        }
    }

    private static void ValidateReview(
        ZoneCalibrationEvidence evidence,
        LoadedTrustStore trustStore,
        IReadOnlySet<string> captureKeyIds,
        ReviewChallenge challenge,
        DateTimeOffset now)
    {
        var reviewedAt = RequireUtc(evidence.Review.ReviewedAt, "review.reviewedAt");
        if (evidence.Review.Result != "pass" || reviewedAt < evidence.Sampling.EndedAt ||
            reviewedAt > now.AddMinutes(5) || reviewedAt > evidence.ExpiresAt)
        {
            throw Invalid(
                "ZONE_REVIEW_INVALID",
                "An independent passing review must occur after sampling and before evidence expiry.");
        }
        if (evidence.Review.SignatureAlgorithm != ZoneCalibrationConstants.SignatureAlgorithm ||
            evidence.Review.EvidencePayloadSha256 != challenge.EvidencePayloadSha256 ||
            evidence.Review.ArtifactManifestSha256 != challenge.ArtifactManifestSha256)
        {
            throw Invalid(
                "ZONE_REVIEW_BINDING_MISMATCH",
                "Review must bind the complete canonical evidence payload and all-artifact manifest.");
        }
        if (!trustStore.ReviewerKeys.TryGetValue(evidence.Review.ReviewerKeyId, out var reviewerKey) || reviewerKey.Revoked ||
            reviewedAt < reviewerKey.ValidFrom || reviewedAt > reviewerKey.ExpiresAt)
        {
            throw Invalid("ZONE_REVIEW_KEY_UNTRUSTED", "Review key is absent, revoked or invalid at review time.");
        }
        if (captureKeyIds.Contains(reviewerKey.KeyId) ||
            reviewerKey.SubjectId != evidence.Participants.Reviewer.SubjectId ||
            reviewerKey.PseudonymDomain != evidence.Participants.Reviewer.PseudonymDomain ||
            reviewerKey.SubjectId == evidence.Participants.Executor.SubjectId)
        {
            throw Invalid(
                "ZONE_REVIEW_NOT_INDEPENDENT",
                "Reviewer key and subject must be independently trusted and distinct from every capture key/executor.");
        }
        VerifySignature(
            reviewerKey,
            Convert.FromBase64String(challenge.StatementBase64),
            evidence.Review.SignatureBase64,
            "ZONE_REVIEW_SIGNATURE_INVALID");
    }

    private static VerifiedArtifact RequireArtifact(
        string artifactId,
        string expectedRole,
        IReadOnlyDictionary<string, VerifiedArtifact> artifacts,
        HashSet<string> references)
    {
        RequireIdentifier(artifactId, "artifactId");
        if (!artifacts.TryGetValue(artifactId, out var artifact) ||
            artifact.Metadata.Role != expectedRole)
        {
            throw Invalid(
                "ZONE_ARTIFACT_REFERENCE_INVALID",
                $"Artifact '{artifactId}' is missing or does not have role '{expectedRole}'.");
        }
        if (!references.Add(artifactId))
        {
            throw Invalid(
                "ZONE_ARTIFACT_REFERENCE_DUPLICATE",
                $"Raw artifact '{artifactId}' cannot be reused for multiple measurements or checks.");
        }
        return artifact;
    }

    private static void ValidateBearing(
        ZoneAuthority zone,
        double declaredDirection,
        PositionMeasurement point,
        string kind)
    {
        var radians = Math.Atan2(point.Y - zone.Center.Y, point.X - zone.Center.X);
        var observed = radians * 180d / Math.PI;
        if (observed < 0d)
        {
            observed += 360d;
        }
        var delta = Math.Abs(observed - declaredDirection);
        delta = Math.Min(delta, 360d - delta);
        if (delta > 2d)
        {
            throw Invalid(
                "ZONE_BOUNDARY_BEARING_MISMATCH",
                $"The {kind} point bearing differs from declared direction by {delta:R} degrees; maximum is 2 degrees.");
        }
    }

    private static void RequireWithinSampling(
        DateTimeOffset timestamp,
        SamplingWindow sampling,
        string field)
    {
        timestamp = RequireUtc(timestamp, field);
        if (timestamp < sampling.StartedAt || timestamp > sampling.EndedAt)
        {
            throw Invalid(
                "ZONE_CAPTURE_OUTSIDE_WINDOW",
                $"{field} must fall inside the declared sampling window.");
        }
    }

    private static void ValidateSubject(EvidenceSubject subject, string field, string role)
    {
        if (string.IsNullOrWhiteSpace(subject.SubjectId) ||
            !SubjectPattern().IsMatch(subject.SubjectId) || IsWeakDigest(subject.SubjectId))
        {
            throw Invalid(
                "ZONE_SUBJECT_ID_INVALID",
                $"{field}.subjectId must be a non-trivial keyed pseudonym subj:hmac-sha256:<64 lowercase hex>.");
        }
        RequireIdentifier(subject.IdentityProvider, $"{field}.identityProvider");
        if (subject.Role != role)
        {
            throw Invalid(
                "ZONE_SUBJECT_ROLE_INVALID",
                $"{field}.role must be '{role}'.");
        }
    }

    private static T DeserializeSignedCapture<T>(
        VerifiedArtifact artifact,
        CaptureValidationContext context,
        string bodyErrorCode)
    {
        var envelope = DeserializeStrict<SignedCaptureEnvelope>(
            artifact.Snapshot.Bytes,
            "ZONE_CAPTURE_ENVELOPE_INVALID");
        if (envelope.Attestation is null || envelope.Body.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("ZONE_CAPTURE_ENVELOPE_INVALID", "Signed capture requires an object body and attestation.");
        }
        var bodyBytes = Encoding.UTF8.GetBytes(envelope.Body.GetRawText());
        RejectDuplicateProperties(bodyBytes, bodyErrorCode);
        RejectUnsafeJson(bodyBytes, $"artifact:{artifact.Metadata.Id}.body");
        var bodySha256 = HashBytes(bodyBytes);
        var attestation = envelope.Attestation;
        if (attestation.Algorithm != ZoneCalibrationConstants.SignatureAlgorithm)
        {
            throw Invalid(
                "ZONE_CAPTURE_ATTESTATION_INVALID",
                "Capture attestation must use ECDSA P-256.");
        }
        if (attestation.SignedAt != artifact.Metadata.CapturedAt)
        {
            throw Invalid(
                "ZONE_CAPTURE_ATTESTATION_INVALID",
                "Capture attestation signedAt must equal artifact capturedAt.");
        }
        if (attestation.BodySha256 != bodySha256)
        {
            throw Invalid(
                "ZONE_CAPTURE_BODY_HASH_MISMATCH",
                $"Capture attestation does not bind exact raw body JSON bytes (expected {bodySha256}, observed {attestation.BodySha256}).");
        }
        if (string.IsNullOrWhiteSpace(attestation.Nonce) || !NoncePattern().IsMatch(attestation.Nonce) ||
            !context.Nonces.Add(attestation.Nonce))
        {
            throw Invalid(
                "ZONE_CAPTURE_NONCE_INVALID",
                "Capture nonce must be unique lower-case hex with at least 128 bits of entropy.");
        }
        if (string.IsNullOrWhiteSpace(attestation.KeyId) ||
            !context.TrustStore.CaptureKeys.TryGetValue(attestation.KeyId, out var key) || key.Revoked ||
            attestation.SignedAt < key.ValidFrom || attestation.SignedAt > key.ExpiresAt ||
            key.SubjectId != context.Evidence.Participants.Executor.SubjectId ||
            key.PseudonymDomain != context.Evidence.Participants.Executor.PseudonymDomain)
        {
            throw Invalid(
                "ZONE_CAPTURE_KEY_UNTRUSTED",
                "Capture key is absent, revoked, invalid at capture time, or not assigned to this campaign executor.");
        }
        var statement = BuildCaptureSignatureStatement(
            context.Evidence,
            artifact.Metadata,
            attestation.SignedAt,
            attestation.Nonce,
            bodySha256);
        VerifySignature(key, statement, attestation.SignatureBase64, "ZONE_CAPTURE_SIGNATURE_INVALID");
        context.CaptureKeyIds.Add(key.KeyId);
        return DeserializeStrict<T>(bodyBytes, bodyErrorCode);
    }

    private static byte[] BuildCaptureSignatureStatement(
        ZoneCalibrationEvidence evidence,
        ArtifactRecord artifact,
        DateTimeOffset signedAt,
        string nonce,
        string bodySha256)
    {
        var lines = new[]
        {
            ZoneCalibrationConstants.CaptureSignatureContext,
            evidence.Schema,
            evidence.SchemaVersion,
            evidence.SchemaSha256,
            evidence.CalibrationId,
            evidence.Server.ServerId,
            evidence.Server.GameBuild,
            evidence.Server.SteamBuild,
            evidence.Content.VersionId.ToString("D", CultureInfo.InvariantCulture),
            evidence.Content.VersionNumber.ToString(CultureInfo.InvariantCulture),
            evidence.Content.ContentHash,
            evidence.Zone.ZoneId,
            artifact.Id,
            artifact.Role,
            signedAt.ToString("O", CultureInfo.InvariantCulture),
            nonce,
            bodySha256
        };
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    private static void VerifySignature(
        TrustedEvidenceKey key,
        byte[] statement,
        string signatureBase64,
        string code)
    {
        byte[] publicKey;
        byte[] signature;
        try
        {
            if (string.IsNullOrWhiteSpace(signatureBase64) || signatureBase64.Length is < 84 or > 88)
            {
                throw new FormatException("Signature base64 length is invalid.");
            }
            publicKey = Convert.FromBase64String(key.PublicKeySpkiBase64);
            signature = Convert.FromBase64String(signatureBase64);
            if (Convert.ToBase64String(signature) != signatureBase64)
            {
                throw new FormatException("Signature must use canonical base64 without whitespace.");
            }
        }
        catch (FormatException)
        {
            throw Invalid(code, "Public key or signature is not canonical base64.");
        }
        if (signature.Length != 64)
        {
            throw Invalid(code, "ECDSA P-256 signature must use the 64-byte IEEE-P1363 encoding.");
        }
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || ecdsa.KeySize != 256 ||
                !ecdsa.VerifyData(
                    statement,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw Invalid(code, "ECDSA P-256 signature verification failed.");
            }
        }
        catch (CryptographicException exception)
        {
            throw Invalid(code, $"ECDSA P-256 key/signature is invalid: {exception.Message}");
        }
    }

    private static T DeserializeStrict<T>(byte[] bytes, string code)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, StrictJson)
                ?? throw Invalid(code, "The JSON document is empty.");
        }
        catch (JsonException exception)
        {
            throw Invalid(code, $"Strict JSON deserialization failed: {exception.Message}");
        }
    }

    private static void RejectDuplicateProperties(byte[] bytes, string code)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions());
            Walk(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw Invalid(code, exception.Message);
        }

        void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw Invalid(code, $"Duplicate JSON property '{property.Name}' is forbidden.");
                    }
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item);
                }
            }
        }
    }

    private static void RejectUnsafeJson(byte[] bytes, string source)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions());
            Walk(document.RootElement, "$");
        }
        catch (JsonException exception)
        {
            throw Invalid("ZONE_JSON_PRIVACY_SCAN_FAILED", exception.Message);
        }

        void Walk(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (SensitiveFieldPattern().IsMatch(property.Name))
                    {
                        throw Invalid(
                            "ZONE_IDENTITY_FIELD_FORBIDDEN",
                            $"{source}{path}.{property.Name} exposes a player/account/credential identity field.");
                    }
                    if (property.Name is "signatureBase64" or "publicKeySpkiBase64")
                    {
                        continue;
                    }
                    Walk(property.Value, $"{path}.{property.Name}");
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index++}]");
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString() ?? string.Empty;
                if (PlaceholderPattern().IsMatch(value))
                {
                    throw Invalid(
                        "ZONE_PLACEHOLDER_VALUE_REJECTED",
                        $"{source}{path} contains mock, template or placeholder provenance.");
                }
                if (EmailPattern().IsMatch(value) || SteamIdPattern().IsMatch(value) ||
                    JwtPattern().IsMatch(value) || WindowsProfilePattern().IsMatch(value) ||
                    ContainsIpAddress(value))
                {
                    throw Invalid(
                        "ZONE_IDENTITY_VALUE_FORBIDDEN",
                        $"{source}{path} appears to contain personal identity, network address or credential material.");
                }
            }
        }
    }

    private static bool ContainsIpAddress(string value)
    {
        if (Ipv4AnywherePattern().IsMatch(value))
        {
            return true;
        }
        foreach (Match match in Ipv6CandidatePattern().Matches(value))
        {
            var candidate = match.Value.Trim('[', ']');
            if (candidate.Count(character => character == ':') >= 2 &&
                IPAddress.TryParse(candidate, out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return true;
            }
        }
        return false;
    }

    private static string ExistingRegularFile(string path, string code)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid(code, "A file path is required.");
        }
        var fullPath = Path.GetFullPath(path);
        RejectUnsafeFullPath(fullPath, code);
        if (!File.Exists(fullPath))
        {
            throw Invalid(code, $"File does not exist: {fullPath}");
        }
        RejectReparseChain(fullPath, code);
        return fullPath;
    }

    private static string ResolveContainedFile(string root, string relativePath)
    {
        var segments = relativePath.Split('/');
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(segments)));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathComparison()) || !File.Exists(fullPath))
        {
            throw Invalid(
                "ZONE_ARTIFACT_PATH_INVALID",
                $"Artifact path leaves the evidence root or does not exist: '{relativePath}'.");
        }
        RejectReparseChain(fullPath, "ZONE_ARTIFACT_REPARSE_POINT");
        return fullPath;
    }

    private static void RequirePortablePath(string path, string field)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\', StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal) || Path.IsPathFullyQualified(path) ||
            PlaceholderPattern().IsMatch(path))
        {
            throw Invalid(
                "ZONE_ARTIFACT_PATH_INVALID",
                $"{field} must be a concrete portable relative path using '/'.");
        }
        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
                                    segment.EndsWith('.') || segment.EndsWith(' ') ||
                                    IsWindowsDeviceName(segment)))
        {
            throw Invalid(
                "ZONE_ARTIFACT_PATH_INVALID",
                $"{field} contains traversal or an empty segment.");
        }
    }

    private static void RejectUnsafeFullPath(string fullPath, string code)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var remainder = fullPath[root.Length..];
        if (remainder.Contains(':', StringComparison.Ordinal))
        {
            throw Invalid(code, $"Alternate data streams/colon path segments are forbidden: '{fullPath}'.");
        }
        var segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.EndsWith('.') || segment.EndsWith(' ') || IsWindowsDeviceName(segment)))
        {
            throw Invalid(code, $"Device names and trailing dot/space path segments are forbidden: '{fullPath}'.");
        }
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9');
    }

    private static void RejectReparseChain(string fullPath, string code)
    {
        var current = new FileInfo(fullPath);
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid(code, $"Reparse points are forbidden: '{fullPath}'.");
        }
        for (var directory = current.Directory; directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid(code, $"Reparse points are forbidden in the evidence path: '{directory.FullName}'.");
            }
        }
    }

    private static FileSnapshot ReadFileSnapshot(string path, long maximumBytes, string code)
    {
        RejectReparseChain(path, code);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        RejectReparseChain(path, code);
        var initialLength = stream.Length;
        if (initialLength > maximumBytes)
        {
            throw Invalid(code, $"File exceeds {maximumBytes} bytes: '{path}'.");
        }
        using var memory = new MemoryStream((int)Math.Min(initialLength, int.MaxValue));
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            hasher.AppendData(buffer, 0, read);
            memory.Write(buffer, 0, read);
        }
        if (stream.Length != initialLength || memory.Length != initialLength)
        {
            throw Invalid(code, $"File changed while its immutable snapshot was being read: '{path}'.");
        }
        return new FileSnapshot(
            path,
            "sha256:" + Convert.ToHexStringLower(hasher.GetHashAndReset()),
            initialLength,
            memory.ToArray());
    }

    private static void WriteReportPair(
        string reportPath,
        string hashPath,
        byte[] reportBytes,
        string reportSha256)
    {
        var report = PrepareOutputPath(reportPath, "report");
        var hash = PrepareOutputPath(hashPath, "report hash");
        if (PathComparer().Equals(report, hash))
        {
            throw Invalid("ZONE_OUTPUT_PATH_COLLISION", "Report and report-hash paths must differ.");
        }
        if (File.Exists(report) || File.Exists(hash))
        {
            throw Invalid(
                "ZONE_OUTPUT_ALREADY_EXISTS",
                "Canonical outputs are immutable; choose report and hash paths that do not exist.");
        }
        var reportTemp = report + ".tmp-" + Guid.NewGuid().ToString("N");
        var hashTemp = hash + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(reportTemp, reportBytes);
            File.WriteAllText(hashTemp, reportSha256 + "\n", new UTF8Encoding(false));
            File.Move(reportTemp, report);
            try
            {
                File.Move(hashTemp, hash);
            }
            catch
            {
                File.Delete(report);
                throw;
            }
        }
        finally
        {
            File.Delete(reportTemp);
            File.Delete(hashTemp);
        }
    }

    private static string PrepareOutputPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid("ZONE_OUTPUT_PATH_INVALID", $"A {label} path is required.");
        }
        var fullPath = Path.GetFullPath(path);
        RejectUnsafeFullPath(fullPath, "ZONE_OUTPUT_PATH_INVALID");
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw Invalid("ZONE_OUTPUT_PATH_INVALID", $"The {label} path has no parent directory.");
        Directory.CreateDirectory(parent);
        var probe = new DirectoryInfo(parent);
        for (var current = probe; current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid(
                    "ZONE_OUTPUT_REPARSE_POINT",
                    $"The {label} path contains a reparse point: '{current.FullName}'.");
            }
        }
        return fullPath;
    }

    private static void RequireIdentifier(string value, string field)
    {
        if (value is null || !IdentifierPattern().IsMatch(value) || PlaceholderPattern().IsMatch(value))
        {
            throw Invalid(
                "ZONE_IDENTIFIER_INVALID",
                $"{field} must be a concrete lower-case stable identifier.");
        }
    }

    private static void RequireConcreteBuild(string value, string field)
    {
        if (value is null || !BuildPattern().IsMatch(value) || PlaceholderPattern().IsMatch(value))
        {
            throw Invalid(
                "ZONE_BUILD_INVALID",
                $"{field} must be a concrete observed build string.");
        }
    }

    private static void RequireCoordinate(double value, string field)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > ZoneGeometryLimits.MaximumAbsoluteCoordinate)
        {
            throw Invalid(
                "ZONE_COORDINATE_INVALID",
                $"{field} must be finite and within +/-{ZoneGeometryLimits.MaximumAbsoluteCoordinate:R} map units.");
        }
    }

    private static void RequireSha256(string value, string field)
    {
        if (value is null || !Sha256Pattern().IsMatch(value) || IsWeakDigest(value))
        {
            throw Invalid(
                "ZONE_SHA256_INVALID",
                $"{field} must be a non-trivial 'sha256:' digest with 64 lowercase hex characters.");
        }
    }

    private static void RequireRawContentHash(string value, string field)
    {
        if (value is null || !RawHashPattern().IsMatch(value) || IsWeakDigest(value))
        {
            throw Invalid(
                "ZONE_CONTENT_HASH_INVALID",
                $"{field} must be the non-trivial 64-character lower-case content hash emitted by Control API.");
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string field)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw Invalid("ZONE_TIMESTAMP_INVALID", $"{field} must be an explicit UTC timestamp.");
        }
        return value;
    }

    private static bool ApproximatelyEqual(double left, double right) =>
        double.IsFinite(left) && double.IsFinite(right) &&
        Math.Abs(left - right) <= NumericTolerance * Math.Max(1d, Math.Max(Math.Abs(left), Math.Abs(right)));

    private static bool IsWeakDigest(string value)
    {
        var digest = value[(value.LastIndexOf(':') + 1)..];
        return digest.Distinct().Count() < 8;
    }

    private static double RoundMetric(double value) =>
        Math.Round(value, 9, MidpointRounding.ToEven);

    private static byte[] AppendNewline(byte[] bytes)
    {
        var result = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        result[^1] = (byte)'\n';
        return result;
    }

    private static string HashUtf8(string value) =>
        HashBytes(Encoding.UTF8.GetBytes(value));

    private static string HashBytes(byte[] value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(value));

    private static JsonDocumentOptions DocumentOptions() => new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static ZoneCalibrationException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{2,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+:-]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildPattern();

    [GeneratedRegex("^[0-9]{6,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildPattern();

    [GeneratedRegex("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RawHashPattern();

    [GeneratedRegex("^subj:hmac-sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SubjectPattern();

    [GeneratedRegex("^[a-f0-9]{32,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex NoncePattern();

    [GeneratedRegex("(^|[^a-z0-9])(mock|fake|fixture|placeholder|dummy|sample|example|synthetic|template|todo|unknown)([^a-z0-9]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("^(player(name|uid|id)?|steam(id|userid)?|user(id|name)?|account(id)?|email|ip(address)?|password|passwd|secret|token|cookie|authorization|session(id)?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveFieldPattern();

    [GeneratedRegex("[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();

    [GeneratedRegex("(?<![0-9])7656119[0-9]{10}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SteamIdPattern();

    [GeneratedRegex("(^|[^A-Za-z0-9_-])[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}($|[^A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex("[A-Za-z]:\\\\Users\\\\[^\\\\/]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsProfilePattern();

    [GeneratedRegex("(?<![0-9])(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9]?[0-9])\\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9]?[0-9])(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4AnywherePattern();

    [GeneratedRegex("\\[?(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%[A-Za-z0-9._-]+)?\\]?", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6CandidatePattern();
}

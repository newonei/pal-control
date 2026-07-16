using System.Text.Json;

namespace PalControl.ZoneCalibration;

internal static class ZoneCalibrationConstants
{
    public const string EvidenceSchemaId =
        "https://schemas.pal-control.dev/zone-calibration-evidence.schema.v1.json";
    public const string EvidenceSchemaVersion = "1.0.0";
    public const string ReportSchemaId =
        "https://schemas.pal-control.dev/zone-calibration-report.schema.v1.json";
    public const string TrustStoreSchemaId =
        "https://schemas.pal-control.dev/zone-calibration-trust-store.v1.json";
    public const string TrustStoreSchemaVersion = "1.0.0";
    public const string EvidenceMode = "live";
    public const string CanonicalizationId = "pal-control-zone-calibration-report-v1";
    public const string CaptureSignatureContext = "pal-control-zone-capture-signature-v1";
    public const string ReviewSignatureContext = "pal-control-zone-review-signature-v1";
    public const string SignatureAlgorithm = "ecdsa-p256-sha256";
    public const string AuthoritativePositionSource =
        "palworld-authoritative-live-position";
    public const string AuthoritativeServerBuildSource =
        "palworld-dedicated-server-runtime";
    public const string AuthoritativeContentSource =
        "control-api-current-content";
    public const string AuthoritativeObservationSource =
        "palworld-live-observation";
    public const string AuthoritativeQuoteSource =
        "control-api-extraction-quote";
}

internal sealed record ZoneCalibrationEvidence(
    string Schema,
    string SchemaVersion,
    string SchemaSha256,
    string EvidenceMode,
    string CalibrationId,
    DateTimeOffset ExpiresAt,
    ServerBinding Server,
    ContentBinding Content,
    ZoneAuthority Zone,
    SamplingWindow Sampling,
    ParticipantPair Participants,
    PositionMeasurement CenterMeasurement,
    IReadOnlyList<BoundaryMeasurementPair> BoundaryPairs,
    IReadOnlyList<RouteCheck> RouteChecks,
    AccessibilityCheck AccessibilityCheck,
    RiskCheck RiskCheck,
    ReviewRecord Review,
    IReadOnlyList<ArtifactRecord> Artifacts);

internal sealed record ServerBinding(
    string ServerId,
    string GameBuild,
    string SteamBuild,
    string ArtifactId);

internal sealed record ContentBinding(
    Guid VersionId,
    long VersionNumber,
    string ContentHash,
    string ArtifactId);

internal sealed record ZoneAuthority(
    string ZoneId,
    string Authority,
    MapPoint Center,
    double Radius,
    double CenterTolerance,
    double InsideSafetyMargin,
    double OutsideSafetyMargin);

internal sealed record MapPoint(double X, double Y);

internal sealed record SamplingWindow(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string CoordinateSource);

internal sealed record ParticipantPair(
    EvidenceSubject Executor,
    EvidenceSubject Reviewer);

internal sealed record EvidenceSubject(
    string SubjectId,
    string PseudonymDomain,
    string IdentityProvider,
    string Role);

internal sealed record PositionMeasurement(
    DateTimeOffset CapturedAt,
    double X,
    double Y,
    string ArtifactId);

internal sealed record BoundaryMeasurementPair(
    double DirectionDegrees,
    PositionMeasurement Inside,
    PositionMeasurement Outside);

internal sealed record QuoteAttemptBinding(
    string RequestArtifactId,
    string ResponseArtifactId);

internal sealed record RouteCheck(
    string RouteId,
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool Reachable,
    bool Uninterrupted,
    string ArtifactId,
    QuoteAttemptBinding InsideQuote,
    QuoteAttemptBinding OutsideQuote);

internal sealed record AccessibilityCheck(
    DateTimeOffset CheckedAt,
    bool Accessible,
    bool ObstructionFree,
    bool ReturnPathConfirmed,
    string ArtifactId);

internal sealed record RiskCheck(
    DateTimeOffset CheckedAt,
    string RiskLevel,
    string Disposition,
    bool TerrainHazardChecked,
    bool HostileExposureChecked,
    bool RespawnRouteAvailable,
    string ArtifactId);

internal sealed record ReviewRecord(
    DateTimeOffset ReviewedAt,
    string Result,
    string ReviewerKeyId,
    string EvidencePayloadSha256,
    string ArtifactManifestSha256,
    string SignatureAlgorithm,
    string SignatureBase64);

internal sealed record ArtifactRecord(
    string Id,
    string Role,
    string Path,
    string MediaType,
    string Producer,
    string CaptureMode,
    DateTimeOffset CapturedAt,
    long SizeBytes,
    string Sha256);

internal sealed record SignedCaptureEnvelope(
    JsonElement Body,
    CaptureAttestation Attestation);

internal sealed record CaptureAttestation(
    string Algorithm,
    string KeyId,
    DateTimeOffset SignedAt,
    string Nonce,
    string BodySha256,
    string SignatureBase64);

internal sealed record RawServerBuildCapture(
    string RecordType,
    string ServerId,
    string GameBuild,
    string SteamBuild,
    DateTimeOffset CapturedAt,
    string Source);

internal sealed record RawContentZoneCapture(
    string RecordType,
    string ServerId,
    Guid VersionId,
    long VersionNumber,
    string ContentHash,
    string ZoneId,
    MapPoint Center,
    double Radius,
    DateTimeOffset CapturedAt,
    string Source);

internal sealed record RawPositionCapture(
    string RecordType,
    string ServerId,
    string ZoneId,
    DateTimeOffset CapturedAt,
    double X,
    double Y,
    string Source);

internal sealed record RouteTracePoint(
    DateTimeOffset CapturedAt,
    double X,
    double Y);

internal sealed record RawRouteCapture(
    string RecordType,
    string ServerId,
    string ZoneId,
    string RouteId,
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool Reachable,
    bool Uninterrupted,
    IReadOnlyList<RouteTracePoint> Trace,
    string Source);

internal sealed record RawQuoteRequestCapture(
    string RecordType,
    string ServerId,
    string ZoneId,
    string AttemptId,
    DateTimeOffset RequestedAt,
    MapPoint Position,
    string Source);

internal sealed record RawQuoteResponseCapture(
    string RecordType,
    string ServerId,
    string ZoneId,
    string AttemptId,
    DateTimeOffset RespondedAt,
    int HttpStatus,
    string Result,
    string? ErrorCode,
    string Source);

internal sealed record RawAccessibilityCapture(
    string RecordType,
    string ServerId,
    string ZoneId,
    DateTimeOffset CheckedAt,
    bool Accessible,
    bool ObstructionFree,
    bool ReturnPathConfirmed,
    string Source);

internal sealed record RawRiskCapture(
    string RecordType,
    string ServerId,
    string ZoneId,
    DateTimeOffset CheckedAt,
    string RiskLevel,
    string Disposition,
    bool TerrainHazardChecked,
    bool HostileExposureChecked,
    bool RespawnRouteAvailable,
    string Source);

internal sealed record ZoneCalibrationTrustStore(
    string Schema,
    string SchemaVersion,
    IReadOnlyList<TrustedEvidenceKey> CaptureKeys,
    IReadOnlyList<TrustedEvidenceKey> ReviewerKeys);

internal sealed record TrustedEvidenceKey(
    string KeyId,
    string SubjectId,
    string PseudonymDomain,
    string Algorithm,
    string PublicKeySpkiBase64,
    DateTimeOffset ValidFrom,
    DateTimeOffset ExpiresAt,
    bool Revoked);

internal sealed record LoadedTrustStore(
    FileSnapshot Snapshot,
    string Sha256,
    IReadOnlyDictionary<string, TrustedEvidenceKey> CaptureKeys,
    IReadOnlyDictionary<string, TrustedEvidenceKey> ReviewerKeys,
    IReadOnlyDictionary<string, string> PublicKeyFingerprints);

internal sealed record ZoneCalibrationExpectedBinding(
    string ServerId,
    string GameBuild,
    string SteamBuild,
    Guid ContentVersionId,
    long ContentVersionNumber,
    string ContentHash,
    string ZoneId,
    TimeSpan MaximumEvidenceAge);

internal sealed record LoadedSchema(string ResourceName, string Sha256, byte[] Bytes);

internal sealed record FileSnapshot(
    string FullPath,
    string Sha256,
    long SizeBytes,
    byte[] Bytes);

internal sealed record VerifiedArtifact(ArtifactRecord Metadata, FileSnapshot Snapshot);

internal sealed record BoundaryCalculation(
    double DirectionDegrees,
    double InsideDistance,
    double InsideSafetyObserved,
    double OutsideDistance,
    double OutsideSafetyObserved,
    string InsideArtifactSha256,
    string OutsideArtifactSha256);

internal sealed record RouteCalculation(
    string RouteId,
    string Kind,
    int TracePointCount,
    bool TransitionVerified,
    bool InsideQuoteSucceeded,
    bool OutsideQuoteRejected);

internal sealed record CanonicalArtifactHash(
    string Id,
    string Role,
    string Sha256,
    long SizeBytes);

internal sealed record ReviewChallenge(
    string CalibrationId,
    string ReviewerKeyId,
    string EvidencePayloadSha256,
    string ArtifactManifestSha256,
    string StatementBase64);

internal sealed record CanonicalZoneCalibrationReport(
    string Schema,
    string SchemaVersion,
    string SchemaSha256,
    string Canonicalization,
    string Result,
    string SourceEvidenceSha256,
    string EvidenceSchemaSha256,
    string TrustStoreSha256,
    string CalibrationId,
    DateTimeOffset EvidenceExpiresAt,
    string ServerId,
    string GameBuild,
    string SteamBuild,
    Guid ContentVersionId,
    long ContentVersionNumber,
    string ContentHash,
    string ZoneId,
    string ZoneAuthority,
    string DistanceFormula,
    MapPoint Center,
    double Radius,
    double CenterTolerance,
    double CenterDistance,
    double InsideSafetyMargin,
    double OutsideSafetyMargin,
    DateTimeOffset SamplingStartedAt,
    DateTimeOffset SamplingEndedAt,
    string CoordinateSource,
    string ExecutorSubjectId,
    string ReviewerSubjectId,
    IReadOnlyList<string> CaptureKeyIds,
    string ReviewerKeyId,
    string ReviewEvidencePayloadSha256,
    string ReviewArtifactManifestSha256,
    string ReviewSignatureAlgorithm,
    string ReviewSignatureBase64,
    IReadOnlyList<BoundaryCalculation> BoundaryCalculations,
    IReadOnlyList<RouteCalculation> RouteCalculations,
    bool AccessibilityVerified,
    bool ReturnPathConfirmed,
    string RiskLevel,
    string RiskDisposition,
    bool TerrainHazardChecked,
    bool HostileExposureChecked,
    bool RespawnRouteAvailable,
    string ArtifactSetSha256,
    IReadOnlyList<CanonicalArtifactHash> Artifacts,
    DateTimeOffset ReviewedAt);

internal sealed record VerificationResult(
    bool Valid,
    string CalibrationId,
    string ZoneId,
    int BoundaryDirectionCount,
    int ArtifactCount,
    string SourceEvidenceSha256,
    string CanonicalReportSha256,
    string CanonicalReportPath,
    string CanonicalReportHashPath);

internal sealed record ReportVerificationResult(
    bool Valid,
    string CalibrationId,
    string ZoneId,
    string CanonicalReportSha256);

internal sealed class ZoneCalibrationException : Exception
{
    public ZoneCalibrationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

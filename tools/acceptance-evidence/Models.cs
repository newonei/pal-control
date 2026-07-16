using System.Text.Json.Serialization;

namespace PalControl.AcceptanceEvidence;

internal static class EvidenceConstants
{
    public const string ManifestSchemaId =
        "https://github.com/newonei/pal-control/schemas/acceptance-evidence/v1";
    public const string CatalogSchemaId =
        "https://github.com/newonei/pal-control/schemas/acceptance-gate-catalog/v1";
    public const string SchemaVersion = "1.0.0";
    public const string TrustStoreSchemaId =
        "https://github.com/newonei/pal-control/schemas/acceptance-identity-trust-store/v1";
    public const string SignaturePayloadSchemaId =
        "https://github.com/newonei/pal-control/schemas/acceptance-signature-payload/v1";
    public const string SignatureAlgorithm = "ecdsa-p256-sha256-p1363";
}

internal sealed class AcceptanceManifest
{
    [JsonPropertyName("$schema")]
    public required string Schema { get; init; }
    public required string SchemaVersion { get; init; }
    public required string SchemaSha256 { get; init; }
    public required string ManifestId { get; init; }
    public required string GateId { get; init; }
    public required string GateCatalogVersion { get; init; }
    public required string GateCatalogSha256 { get; init; }
    public required string EvidenceMode { get; init; }
    public required EvidenceEnvironment Environment { get; init; }
    public required VersionCombination VersionCombination { get; init; }
    public required RunbookReference Runbook { get; init; }
    public required ExecutionRecord Execution { get; init; }
    public required List<SubjectRecord> Participants { get; init; }
    public required List<EvidenceArtifact> Artifacts { get; init; }
    public required List<EvidenceCheck> Checks { get; init; }
    public required List<EvidenceMetric> Metrics { get; init; }
    public required SensitiveDataScan SensitiveDataScan { get; init; }
    public required ReviewRecord Review { get; init; }
    public required List<RelatedEvidence> RelatedEvidence { get; init; }
    public required ConclusionRecord Conclusion { get; init; }
    public required EvidenceSignatures Signatures { get; init; }
}

internal sealed class EvidenceSignatures
{
    public required string PayloadSchema { get; init; }
    public required string TrustStoreSha256 { get; init; }
    public required EvidenceSignature Executor { get; init; }
    public required EvidenceSignature Reviewer { get; init; }
}

internal sealed class EvidenceSignature
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string SignatureBase64 { get; init; }
}

internal sealed class EvidenceEnvironment
{
    public required string EnvironmentId { get; init; }
    public required string Kind { get; init; }
    public required bool IsSynthetic { get; init; }
    public required string DataClassification { get; init; }
    public required string ServerIdentityHash { get; init; }
    public required string WorldIdentityHash { get; init; }
}

internal sealed class VersionCombination
{
    public required string CombinationId { get; init; }
    public required string PalworldVersion { get; init; }
    public required string SteamBuild { get; init; }
    public required string Ue4ssVersion { get; init; }
    public required string NativeBridgeVersion { get; init; }
    public required string NativeCapability { get; init; }
    public required string PalDefenderVersion { get; init; }
    public required string CaddyVersion { get; init; }
    public required string ControlApiCommit { get; init; }
    public required string DeploymentPackageSha256 { get; init; }
    public required string ConfigurationSha256 { get; init; }
}

internal sealed class RunbookReference
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Commit { get; init; }
}

internal sealed class ExecutionRecord
{
    public required SubjectRecord Executor { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
}

internal sealed class SubjectRecord
{
    public required string SubjectId { get; init; }
    public required string IdentityProvider { get; init; }
    public required string Role { get; init; }
    public required bool ImplementationContributor { get; init; }
}

internal sealed class EvidenceArtifact
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string Role { get; init; }
    public required string MediaType { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required string CaptureMode { get; init; }
    public required string Producer { get; init; }
}

internal sealed class EvidenceCheck
{
    public required string Id { get; init; }
    public required string Result { get; init; }
    public required List<string> ArtifactIds { get; init; }
    public required string Summary { get; init; }
}

internal sealed class EvidenceMetric
{
    public required string Id { get; init; }
    public required decimal Value { get; init; }
    public required string Unit { get; init; }
    public required List<string> ArtifactIds { get; init; }
}

internal sealed class SensitiveDataScan
{
    public required string Scanner { get; init; }
    public required string ScannerVersion { get; init; }
    public required string Command { get; init; }
    public required string Scope { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required string Result { get; init; }
    public required int FindingCount { get; init; }
    public required string ReportArtifactId { get; init; }
    public required List<string> ScannedArtifactIds { get; init; }
}

internal sealed class ReviewRecord
{
    public required SubjectRecord Reviewer { get; init; }
    public required DateTimeOffset ReviewedAt { get; init; }
    public required string Decision { get; init; }
    public required List<string> ArtifactIds { get; init; }
    public required string Summary { get; init; }
}

internal sealed class RelatedEvidence
{
    public required string GateId { get; init; }
    public required string ManifestPath { get; init; }
    public required string Sha256 { get; init; }
}

internal sealed class ConclusionRecord
{
    public required string Result { get; init; }
    public required DateTimeOffset DecidedAt { get; init; }
    public required string Summary { get; init; }
}

internal sealed class IdentityTrustStore
{
    [JsonPropertyName("$schema")]
    public required string Schema { get; init; }
    public required string SchemaVersion { get; init; }
    public required string StoreId { get; init; }
    public required List<TrustedIdentityKey> Keys { get; init; }
}

internal sealed class TrustedIdentityKey
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string PublicKeySpkiBase64 { get; init; }
    public required bool Revoked { get; init; }
    public required SubjectRecord Subject { get; init; }
}

internal sealed class GateCatalog
{
    [JsonPropertyName("$schema")]
    public required string Schema { get; init; }
    public required string CatalogVersion { get; init; }
    public required string ManifestSchemaId { get; init; }
    public required string ManifestSchemaSha256 { get; init; }
    public required List<GatePolicy> Gates { get; init; }
}

internal sealed class GatePolicy
{
    public required string Id { get; init; }
    public required string Priority { get; init; }
    public required string Title { get; init; }
    public required List<string> TodoReferences { get; init; }
    public required List<string> AllowedEnvironmentKinds { get; init; }
    public required string RequiredExecutorRole { get; init; }
    public required bool RequireExecutorNotImplementationContributor { get; init; }
    public required bool RequireIndependentReviewer { get; init; }
    public required bool RequireReviewerNotImplementationContributor { get; init; }
    public required long MinimumDurationSeconds { get; init; }
    public required string? RequiredPalworldVersion { get; init; }
    public required string? RequiredSteamBuild { get; init; }
    public required string? RequiredNativeCapability { get; init; }
    public required List<ParticipantConstraint> ParticipantConstraints { get; init; }
    public required List<ArtifactRoleRequirement> RequiredArtifactRoles { get; init; }
    public required List<string> RequiredChecks { get; init; }
    public required List<MetricConstraint> MetricConstraints { get; init; }
    public required List<string> RequiredRelatedGates { get; init; }
    public required NotApplicablePolicy? NotApplicablePolicy { get; init; }
}

internal sealed class ParticipantConstraint
{
    public required string Role { get; init; }
    public required int Minimum { get; init; }
    public required int? Maximum { get; init; }
    public required bool AllMustNotBeImplementationContributors { get; init; }
}

internal sealed class ArtifactRoleRequirement
{
    public required string Role { get; init; }
    public required int Minimum { get; init; }
}

internal sealed class MetricConstraint
{
    public required string Id { get; init; }
    public required string Operator { get; init; }
    public required decimal Value { get; init; }
    public required string Unit { get; init; }
}

internal sealed class NotApplicablePolicy
{
    public required List<ArtifactRoleRequirement> RequiredArtifactRoles { get; init; }
    public required List<string> RequiredChecks { get; init; }
}

internal sealed record LoadedCatalog(
    GateCatalog Document,
    string Path,
    string Sha256,
    IReadOnlyDictionary<string, GatePolicy> Policies);

internal sealed record LoadedSchema(string Path, string Sha256);

internal sealed record FileSnapshot(string Sha256, long SizeBytes, byte[] Sample);

internal sealed record LoadedTrustStore(
    IdentityTrustStore Document,
    string Path,
    string Sha256,
    IReadOnlyDictionary<string, TrustedIdentityKey> Keys,
    IReadOnlyDictionary<string, TrustedIdentityKey> Subjects,
    IReadOnlyDictionary<string, string> KeyFingerprints);

internal sealed record VerificationSummary(
    bool Valid,
    string ManifestId,
    string GateId,
    string Conclusion,
    int ArtifactCount,
    int RelatedManifestCount,
    string VersionCombinationId,
    string ManifestSha256,
    string IdentitySignatures,
    string EnvelopeSensitiveScan);

internal sealed class EvidenceValidationException : Exception
{
    public EvidenceValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

using System.Security.Cryptography;
using System.Text;

namespace PalControl.AcceptanceEvidence;

internal static class TemplateFactory
{
    public static AcceptanceManifest Create(
        GatePolicy gate,
        LoadedCatalog catalog,
        LoadedSchema schema,
        DateTimeOffset now)
    {
        var started = now.ToUniversalTime();
        var ended = started.AddSeconds(Math.Max(1, gate.MinimumDurationSeconds));
        var artifactRequirements = gate.RequiredArtifactRoles
            .Concat(new[]
            {
                new ArtifactRoleRequirement { Role = "sensitive-scan-report", Minimum = 1 },
                new ArtifactRoleRequirement { Role = "review-record", Minimum = 1 }
            })
            .GroupBy(requirement => requirement.Role, StringComparer.Ordinal)
            .Select(group => new ArtifactRoleRequirement
            {
                Role = group.Key,
                Minimum = group.Max(requirement => requirement.Minimum)
            })
            .ToArray();
        var artifacts = new List<EvidenceArtifact>();
        foreach (var requirement in artifactRequirements)
        {
            for (var index = 1; index <= requirement.Minimum; index++)
            {
                var id = requirement.Minimum == 1
                    ? requirement.Role
                    : $"{requirement.Role}-{index:D2}";
                artifacts.Add(new EvidenceArtifact
                {
                    Id = id,
                    Path = $"evidence/replace-{id}.json",
                    Role = requirement.Role,
                    MediaType = "application/json",
                    Sha256 = "sha256:" + new string('0', 64),
                    SizeBytes = 0,
                    CapturedAt = requirement.Role switch
                    {
                        "sensitive-scan-report" => ended.AddMinutes(2),
                        "review-record" => ended.AddMinutes(3),
                        _ => ended
                    },
                    CaptureMode = "template",
                    Producer = "replace-with-observed-producer-and-version"
                });
            }
        }
        var gateArtifactIds = artifacts
            .Where(artifact => artifact.Role is not "sensitive-scan-report" and not "review-record")
            .Select(artifact => artifact.Id)
            .ToList();
        if (gateArtifactIds.Count == 0)
        {
            gateArtifactIds.Add(artifacts[0].Id);
        }

        var participants = new List<SubjectRecord>();
        foreach (var constraint in gate.ParticipantConstraints)
        {
            for (var index = 1; index <= constraint.Minimum; index++)
            {
                participants.Add(TemplateSubject(
                    $"participant-{constraint.Role}-{index}",
                    constraint.Role,
                    implementationContributor: false));
            }
        }
        var checks = gate.RequiredChecks.Select((id, index) => new EvidenceCheck
        {
            Id = id,
            Result = "pending",
            ArtifactIds = [gateArtifactIds[index % gateArtifactIds.Count]],
            Summary = "Replace with a factual result and exact artifact references."
        }).ToList();
        var metrics = gate.MetricConstraints.Select((constraint, index) => new EvidenceMetric
        {
            Id = constraint.Id,
            Value = constraint.Value,
            Unit = constraint.Unit,
            ArtifactIds = [gateArtifactIds[index % gateArtifactIds.Count]]
        }).ToList();

        var combination = new VersionCombination
        {
            CombinationId = "combo-sha256:" + new string('0', 64),
            PalworldVersion = gate.RequiredPalworldVersion ?? "replace-with-observed-palworld-version",
            SteamBuild = gate.RequiredSteamBuild ?? "replace-with-observed-steam-build",
            Ue4ssVersion = "replace-with-observed-ue4ss-version-or-not-loaded-version",
            NativeBridgeVersion = "replace-with-observed-native-bridge-version",
            NativeCapability = gate.RequiredNativeCapability ?? "experimental",
            PalDefenderVersion = "replace-with-observed-paldefender-version",
            CaddyVersion = "replace-with-observed-caddy-version",
            ControlApiCommit = new string('0', 40),
            DeploymentPackageSha256 = "sha256:" + new string('0', 64),
            ConfigurationSha256 = "sha256:" + new string('0', 64)
        };
        return new AcceptanceManifest
        {
            Schema = EvidenceConstants.ManifestSchemaId,
            SchemaVersion = EvidenceConstants.SchemaVersion,
            SchemaSha256 = schema.Sha256,
            ManifestId = $"ae-template-{gate.Id}"[..Math.Min(96, $"ae-template-{gate.Id}".Length)],
            GateId = gate.Id,
            GateCatalogVersion = catalog.Document.CatalogVersion,
            GateCatalogSha256 = catalog.Sha256,
            EvidenceMode = "template",
            Environment = new EvidenceEnvironment
            {
                EnvironmentId = "replace-environment-id",
                Kind = gate.AllowedEnvironmentKinds[0],
                IsSynthetic = true,
                DataClassification = "redacted",
                ServerIdentityHash = "sha256:" + new string('0', 64),
                WorldIdentityHash = "sha256:" + new string('0', 64)
            },
            VersionCombination = combination,
            Runbook = new RunbookReference
            {
                Id = "replace-runbook-id",
                Version = "replace-with-runbook-version",
                Commit = new string('0', 40)
            },
            Execution = new ExecutionRecord
            {
                Executor = TemplateSubject(
                    "executor",
                    gate.RequiredExecutorRole,
                    implementationContributor: !gate.RequireExecutorNotImplementationContributor),
                StartedAt = started,
                EndedAt = ended
            },
            Participants = participants,
            Artifacts = artifacts,
            Checks = checks,
            Metrics = metrics,
            SensitiveDataScan = new SensitiveDataScan
            {
                Scanner = "replace-with-scanner-name",
                ScannerVersion = "replace-with-scanner-version",
                Command = "Replace with the exact one-line scanner command.",
                Scope = "evidence-artifacts",
                StartedAt = ended.AddMinutes(1),
                EndedAt = ended.AddMinutes(2),
                Result = "pending",
                FindingCount = -1,
                ReportArtifactId = "sensitive-scan-report",
                ScannedArtifactIds = gateArtifactIds
            },
            Review = new ReviewRecord
            {
                Reviewer = TemplateSubject("reviewer", "reviewer", implementationContributor: false),
                ReviewedAt = ended.AddMinutes(3),
                Decision = "pending",
                ArtifactIds = ["review-record"],
                Summary = "Replace with the independent review decision and rationale."
            },
            RelatedEvidence = gate.RequiredRelatedGates.Select(relatedGate => new RelatedEvidence
            {
                GateId = relatedGate,
                ManifestPath = $"replace-{relatedGate}/manifest.json",
                Sha256 = "sha256:" + new string('0', 64)
            }).ToList(),
            Conclusion = new ConclusionRecord
            {
                Result = "pending",
                DecidedAt = ended.AddMinutes(3),
                Summary = "Pending live execution, sensitive-data scan and independent review."
            },
            Signatures = new EvidenceSignatures
            {
                PayloadSchema = EvidenceConstants.SignaturePayloadSchemaId,
                TrustStoreSha256 = "sha256:" + new string('0', 64),
                Executor = new EvidenceSignature
                {
                    KeyId = "replace-executor-key",
                    Algorithm = EvidenceConstants.SignatureAlgorithm,
                    SignatureBase64 = "replace-with-executor-p256-signature"
                },
                Reviewer = new EvidenceSignature
                {
                    KeyId = "replace-reviewer-key",
                    Algorithm = EvidenceConstants.SignatureAlgorithm,
                    SignatureBase64 = "replace-with-reviewer-p256-signature"
                }
            }
        };
    }

    private static SubjectRecord TemplateSubject(
        string seed,
        string role,
        bool implementationContributor)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("template-only:" + seed));
        return new SubjectRecord
        {
            SubjectId = "subj:hmac-sha256:" + Convert.ToHexStringLower(digest),
            IdentityProvider = "replace-identity-provider",
            Role = role,
            ImplementationContributor = implementationContributor
        };
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PalControl.AcceptanceEvidence;

internal static class EvidenceVerifier
{
    private const int MaximumStructuredDocumentBytes = 1024 * 1024;
    private const string TrustedSchemaResource =
        "PalControl.AcceptanceEvidence.acceptance-evidence.schema.v1.json";
    private const string TrustedCatalogResource =
        "PalControl.AcceptanceEvidence.gate-catalog.v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private static readonly Regex IdPattern = new(
        "^[a-z0-9][a-z0-9.-]{2,127}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ManifestIdPattern = new(
        "^ae-[a-z0-9][a-z0-9-]{7,95}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SubjectPattern = new(
        "^subj:hmac-sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(
        "^sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex CombinationPattern = new(
        "^combo-sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex CommitPattern = new(
        "^[a-f0-9]{40}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex PortablePathSegmentPattern = new(
        "^[A-Za-z0-9._-]{1,128}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex PlaceholderTokenPattern = new(
        "(^|[^a-z0-9])(mock|fake|fixture|placeholder|dummy|sample|example|synthetic)([^a-z0-9]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PlaceholderContentPattern = new(
        "(<replace|pal-control acceptance evidence placeholder|synthetic fixture|mock evidence|fake evidence)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SensitiveAssignmentPattern = new(
        "(?i)(?:authorization|api[_-]?key|password|secret|token)\\s*[:=]\\s*[\\\"']?(?:bearer\\s+)?[A-Za-z0-9_./+=:-]{12,}",
        RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        "(?i)\\bbearer\\s+[A-Za-z0-9._~+/=-]{20,}",
        RegexOptions.CultureInvariant);
    private static readonly Regex PrivateKeyPattern = new(
        "-----BEGIN (?:EC |RSA |OPENSSH )?PRIVATE KEY-----",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedPortableNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        },
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> EnvironmentKinds = new(StringComparer.Ordinal)
    {
        "production",
        "controlled-live",
        "fresh-windows-vm",
        "multi-node-production"
    };
    private static readonly HashSet<string> NativeCapabilities = new(StringComparer.Ordinal)
    {
        "unavailable",
        "experimental",
        "stable"
    };

    public static JsonSerializerOptions SerializerOptions => JsonOptions;

    public static LoadedSchema LoadTrustedSchema()
    {
        var snapshot = ReadEmbeddedSnapshot(TrustedSchemaResource);
        return LoadSchemaSnapshot($"embedded:{TrustedSchemaResource}", snapshot);
    }

    public static LoadedCatalog LoadTrustedCatalog(LoadedSchema schema)
    {
        var snapshot = ReadEmbeddedSnapshot(TrustedCatalogResource);
        return LoadCatalogSnapshot(
            $"embedded:{TrustedCatalogResource}",
            snapshot,
            schema);
    }

    public static LoadedTrustStore LoadTrustStore(string path, string pinnedSha256)
    {
        RequireSha256(pinnedSha256, "trustStoreSha256 pin");
        var fullPath = ExistingRegularFile(path, "ACCEPTANCE_TRUST_STORE_NOT_FOUND");
        var snapshot = ReadFileSnapshot(fullPath, captureSample: true);
        if (snapshot.SizeBytes <= 0 || snapshot.SizeBytes > 1024 * 1024 ||
            snapshot.Sha256 != pinnedSha256)
        {
            throw Invalid(
                "ACCEPTANCE_TRUST_STORE_PIN_MISMATCH",
                "The external identity trust store does not match the independently supplied SHA-256 pin.");
        }
        var document = DeserializeStrict<IdentityTrustStore>(
            snapshot.Sample,
            "ACCEPTANCE_TRUST_STORE_INVALID_JSON");
        if (document.Schema != EvidenceConstants.TrustStoreSchemaId ||
            document.SchemaVersion != EvidenceConstants.SchemaVersion)
        {
            throw Invalid(
                "ACCEPTANCE_TRUST_STORE_VERSION_MISMATCH",
                "The identity trust store is not the strict v1 contract.");
        }
        RequireIdentifier(document.StoreId, "trustStore.storeId");
        if (document.Keys.Count < 2)
        {
            throw Invalid(
                "ACCEPTANCE_TRUST_STORE_TOO_SMALL",
                "At least two independently controlled identity keys are required.");
        }
        var keys = new Dictionary<string, TrustedIdentityKey>(StringComparer.Ordinal);
        var subjects = new Dictionary<string, TrustedIdentityKey>(StringComparer.Ordinal);
        var keyFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var publicKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in document.Keys)
        {
            RequireIdentifier(key.KeyId, "trustStore.keys[].keyId");
            if (key.Algorithm != EvidenceConstants.SignatureAlgorithm)
            {
                throw Invalid(
                    "ACCEPTANCE_TRUST_KEY_ALGORITHM_INVALID",
                    $"Trust key '{key.KeyId}' must use {EvidenceConstants.SignatureAlgorithm}.");
            }
            ValidateSubject(key.Subject, $"trustStore.keys[{key.KeyId}].subject");
            var publicKeyFingerprint = ValidateP256PublicKey(key);
            if (!keys.TryAdd(key.KeyId, key) || !subjects.TryAdd(key.Subject.SubjectId, key))
            {
                throw Invalid(
                    "ACCEPTANCE_TRUST_KEY_DUPLICATE",
                    "Trust-store key ids and subject ids must both be unique.");
            }
            if (!publicKeys.Add(publicKeyFingerprint))
            {
                throw Invalid(
                    "ACCEPTANCE_TRUST_PUBLIC_KEY_DUPLICATE",
                    "A canonical ECDSA P-256 public key cannot represent more than one trust-store identity.");
            }
            keyFingerprints.Add(key.KeyId, publicKeyFingerprint);
        }
        if (document.Keys.Count(key => !key.Revoked) < 2)
        {
            throw Invalid(
                "ACCEPTANCE_TRUST_STORE_ACTIVE_KEYS_TOO_SMALL",
                "At least two active identity keys are required.");
        }
        return new LoadedTrustStore(
            document,
            fullPath,
            snapshot.Sha256,
            keys,
            subjects,
            keyFingerprints);
    }

    public static LoadedSchema LoadSchema(string path)
    {
        var fullPath = ExistingRegularFile(path, "ACCEPTANCE_SCHEMA_NOT_FOUND");
        var snapshot = ReadFileSnapshot(fullPath, captureSample: true);
        return LoadSchemaSnapshot(fullPath, snapshot);
    }

    private static LoadedSchema LoadSchemaSnapshot(string source, FileSnapshot snapshot)
    {
        try
        {
            RejectDuplicateProperties(snapshot.Sample);
            using var document = JsonDocument.Parse(snapshot.Sample);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("$id", out var id) ||
                id.GetString() != EvidenceConstants.ManifestSchemaId ||
                !root.TryGetProperty("x-pal-control-schema-version", out var version) ||
                version.GetString() != EvidenceConstants.SchemaVersion ||
                !root.TryGetProperty("additionalProperties", out var additional) ||
                additional.ValueKind != JsonValueKind.False)
            {
                throw Invalid(
                    "ACCEPTANCE_SCHEMA_ID_MISMATCH",
                    "The manifest schema is not the strict pal-control acceptance-evidence v1 schema.");
            }
        }
        catch (JsonException exception)
        {
            throw Invalid(
                "ACCEPTANCE_SCHEMA_INVALID_JSON",
                $"The manifest schema is invalid JSON: {exception.Message}");
        }

        return new LoadedSchema(source, snapshot.Sha256);
    }

    public static LoadedCatalog LoadCatalog(string path, LoadedSchema schema)
    {
        var fullPath = ExistingRegularFile(path, "ACCEPTANCE_CATALOG_NOT_FOUND");
        var snapshot = ReadFileSnapshot(fullPath, captureSample: true);
        return LoadCatalogSnapshot(fullPath, snapshot, schema);
    }

    private static LoadedCatalog LoadCatalogSnapshot(
        string source,
        FileSnapshot snapshot,
        LoadedSchema schema)
    {
        var catalog = DeserializeStrict<GateCatalog>(
            snapshot.Sample,
            "ACCEPTANCE_CATALOG_INVALID_JSON");
        if (catalog.Schema != EvidenceConstants.CatalogSchemaId ||
            catalog.CatalogVersion != EvidenceConstants.SchemaVersion ||
            catalog.ManifestSchemaId != EvidenceConstants.ManifestSchemaId ||
            catalog.ManifestSchemaSha256 != schema.Sha256)
        {
            throw Invalid(
                "ACCEPTANCE_CATALOG_VERSION_MISMATCH",
                "The gate catalog does not bind the loaded v1 manifest schema and its exact SHA-256.");
        }
        RequireSha256(catalog.ManifestSchemaSha256, "catalog.manifestSchemaSha256");
        if (catalog.Gates.Count == 0)
        {
            throw Invalid("ACCEPTANCE_CATALOG_EMPTY", "The gate catalog contains no gates.");
        }

        var policies = new Dictionary<string, GatePolicy>(StringComparer.Ordinal);
        foreach (var gate in catalog.Gates)
        {
            ValidateGatePolicy(gate);
            if (!policies.TryAdd(gate.Id, gate))
            {
                throw Invalid(
                    "ACCEPTANCE_CATALOG_DUPLICATE_GATE",
                    $"Gate '{gate.Id}' occurs more than once in the catalog.");
            }
        }
        foreach (var gate in catalog.Gates)
        {
            foreach (var relatedGate in gate.RequiredRelatedGates)
            {
                if (relatedGate == gate.Id || !policies.ContainsKey(relatedGate))
                {
                    throw Invalid(
                        "ACCEPTANCE_CATALOG_RELATED_GATE_INVALID",
                        $"Gate '{gate.Id}' has invalid related gate '{relatedGate}'.");
                }
            }
        }

        return new LoadedCatalog(catalog, source, snapshot.Sha256, policies);
    }

    public static VerificationSummary Verify(
        string manifestPath,
        LoadedCatalog catalog,
        LoadedSchema schema,
        LoadedTrustStore trustStore,
        DateTimeOffset now)
    {
        using var lease = new VerificationFileLease();
        AcquirePolicyLease(lease, schema.Path, schema.Sha256, "schema");
        AcquirePolicyLease(lease, catalog.Path, catalog.Sha256, "catalog");
        AcquirePolicyLease(lease, trustStore.Path, trustStore.Sha256, "trust store");
        var stack = new HashSet<string>(PathComparer());
        var summary = VerifyManifest(
            ExistingRegularFile(manifestPath, "ACCEPTANCE_MANIFEST_NOT_FOUND"),
            catalog,
            schema,
            trustStore,
            RequireUtc(now, "now"),
            stack,
            lease);
        lease.ValidateAllPathsAndHandles();
        return summary;
    }

    public static string ComputeFileSha256(string path)
    {
        var fullPath = ExistingRegularFile(path, "ACCEPTANCE_HASH_INPUT_NOT_FOUND");
        return ReadFileSnapshot(fullPath, captureSample: false).Sha256;
    }

    public static string ComputeCombinationId(VersionCombination combination)
    {
        RequireConcrete(combination.PalworldVersion, "versionCombination.palworldVersion");
        RequireConcrete(combination.SteamBuild, "versionCombination.steamBuild");
        RequireConcrete(combination.Ue4ssVersion, "versionCombination.ue4ssVersion");
        RequireConcrete(combination.NativeBridgeVersion, "versionCombination.nativeBridgeVersion");
        RequireConcrete(combination.NativeCapability, "versionCombination.nativeCapability");
        RequireConcrete(combination.PalDefenderVersion, "versionCombination.palDefenderVersion");
        RequireConcrete(combination.CaddyVersion, "versionCombination.caddyVersion");
        RequireCommit(combination.ControlApiCommit, "versionCombination.controlApiCommit");
        RequireNonZeroSha256(
            combination.DeploymentPackageSha256,
            "versionCombination.deploymentPackageSha256");
        RequireNonZeroSha256(
            combination.ConfigurationSha256,
            "versionCombination.configurationSha256");
        var canonical = EvidenceCanonicalJson.CreateVersionCombinationPayload(combination);
        var digest = SHA256.HashData(canonical);
        return "combo-sha256:" + Convert.ToHexStringLower(digest);
    }

    public static AcceptanceManifest ReadManifestForCombination(string path)
    {
        var fullPath = ExistingRegularFile(path, "ACCEPTANCE_MANIFEST_NOT_FOUND");
        var snapshot = ReadFileSnapshot(fullPath, captureSample: true);
        ValidateManifestDocumentBytes(snapshot.Sample);
        return DeserializeStrict<AcceptanceManifest>(
            snapshot.Sample,
            "ACCEPTANCE_MANIFEST_INVALID_JSON");
    }

    public static byte[] CreateSignaturePayload(
        string manifestPath,
        string trustStoreSha256,
        string executorKeyId,
        string reviewerKeyId)
    {
        RequireSha256(trustStoreSha256, "trustStoreSha256");
        RequireIdentifier(executorKeyId, "executorKeyId");
        RequireIdentifier(reviewerKeyId, "reviewerKeyId");
        if (executorKeyId == reviewerKeyId)
        {
            throw Invalid(
                "ACCEPTANCE_SIGNATURE_KEYS_NOT_DISTINCT",
                "Executor and reviewer signature key ids must be distinct.");
        }
        var manifest = ReadManifestForCombination(manifestPath);
        return EvidenceCanonicalJson.CreateSignaturePayload(
            manifest,
            trustStoreSha256,
            executorKeyId,
            reviewerKeyId);
    }

    private static VerificationSummary VerifyManifest(
        string manifestPath,
        LoadedCatalog catalog,
        LoadedSchema schema,
        LoadedTrustStore trustStore,
        DateTimeOffset now,
        HashSet<string> stack,
        VerificationFileLease lease)
    {
        if (!stack.Add(manifestPath))
        {
            throw Invalid(
                "ACCEPTANCE_RELATED_EVIDENCE_CYCLE",
                $"Related evidence contains a cycle at '{manifestPath}'.");
        }

        try
        {
            var manifestSnapshot = lease.Acquire(manifestPath, captureSample: true);
            ValidateManifestDocumentBytes(manifestSnapshot.Sample);
            var manifest = DeserializeStrict<AcceptanceManifest>(
                manifestSnapshot.Sample,
                "ACCEPTANCE_MANIFEST_INVALID_JSON");
            if (!catalog.Policies.TryGetValue(manifest.GateId, out var gate))
            {
                throw Invalid(
                    "ACCEPTANCE_GATE_UNKNOWN",
                    $"Gate '{manifest.GateId}' is not present in catalog {catalog.Document.CatalogVersion}.");
            }

            ValidateManifestHeader(manifest, catalog, schema);
            ValidateEnvironment(manifest.Environment, gate);
            ValidateVersionCombination(manifest.VersionCombination, gate);
            ValidateRunbook(manifest.Runbook);
            ValidateExecution(manifest.Execution, gate, now);
            ValidateParticipants(manifest.Participants, gate);
            ValidateTrustedSubjects(manifest, trustStore);

            var root = Path.GetDirectoryName(manifestPath)
                ?? throw Invalid("ACCEPTANCE_MANIFEST_ROOT_INVALID", "The manifest has no parent directory.");
            var artifacts = ValidateArtifacts(manifest, root, now, lease);
            var referencedArtifacts = new HashSet<string>(StringComparer.Ordinal);
            ValidateChecksAndMetrics(manifest, gate, artifacts, referencedArtifacts);
            ValidateSensitiveDataScan(manifest, artifacts, referencedArtifacts, now);
            ValidateReviewAndConclusion(manifest, gate, artifacts, referencedArtifacts, now);
            ValidateArtifactCoverage(manifest, gate, artifacts, referencedArtifacts);
            SoakEvidenceParser.Validate(manifest, root, artifacts, lease);

            var relatedCount = ValidateRelatedEvidence(
                manifest,
                gate,
                root,
                catalog,
                schema,
                trustStore,
                now,
                stack,
                lease);
            ValidateEvidenceSignatures(manifest, trustStore, root, lease);
            return new VerificationSummary(
                true,
                manifest.ManifestId,
                manifest.GateId,
                manifest.Conclusion.Result,
                manifest.Artifacts.Count,
                relatedCount,
                manifest.VersionCombination.CombinationId,
                manifestSnapshot.Sha256,
                "verified",
                "pass");
        }
        finally
        {
            stack.Remove(manifestPath);
        }
    }

    private static void ValidateManifestHeader(
        AcceptanceManifest manifest,
        LoadedCatalog catalog,
        LoadedSchema schema)
    {
        if (manifest.Schema != EvidenceConstants.ManifestSchemaId ||
            manifest.SchemaVersion != EvidenceConstants.SchemaVersion ||
            manifest.SchemaSha256 != schema.Sha256)
        {
            throw Invalid(
                "ACCEPTANCE_MANIFEST_SCHEMA_MISMATCH",
                "The manifest does not bind the exact loaded v1 schema.");
        }
        if (manifest.GateCatalogVersion != catalog.Document.CatalogVersion ||
            manifest.GateCatalogSha256 != catalog.Sha256)
        {
            throw Invalid(
                "ACCEPTANCE_MANIFEST_CATALOG_MISMATCH",
                "The manifest does not bind the exact loaded gate catalog.");
        }
        RequireSha256(manifest.SchemaSha256, "schemaSha256");
        RequireSha256(manifest.GateCatalogSha256, "gateCatalogSha256");
        if (!ManifestIdPattern.IsMatch(manifest.ManifestId))
        {
            throw Invalid(
                "ACCEPTANCE_MANIFEST_ID_INVALID",
                "manifestId must be an opaque lower-case 'ae-' identifier.");
        }
        if (manifest.EvidenceMode != "live")
        {
            throw Invalid(
                "ACCEPTANCE_EVIDENCE_NOT_LIVE",
                "Only evidenceMode 'live' can satisfy an external acceptance gate; templates and synthetic runs fail closed.");
        }
    }

    private static void ValidateEnvironment(EvidenceEnvironment environment, GatePolicy gate)
    {
        RequireIdentifier(environment.EnvironmentId, "environment.environmentId");
        if (!gate.AllowedEnvironmentKinds.Contains(environment.Kind, StringComparer.Ordinal))
        {
            throw Invalid(
                "ACCEPTANCE_ENVIRONMENT_KIND_REJECTED",
                $"Gate '{gate.Id}' does not permit environment kind '{environment.Kind}'.");
        }
        if (environment.IsSynthetic)
        {
            throw Invalid(
                "ACCEPTANCE_SYNTHETIC_ENVIRONMENT_REJECTED",
                "Synthetic, mock, fixture and local-loopback environments cannot satisfy external gates.");
        }
        if (environment.DataClassification != "redacted")
        {
            throw Invalid(
                "ACCEPTANCE_DATA_NOT_REDACTED",
                "environment.dataClassification must be 'redacted'.");
        }
        RequireNonZeroSha256(environment.ServerIdentityHash, "environment.serverIdentityHash");
        RequireNonZeroSha256(environment.WorldIdentityHash, "environment.worldIdentityHash");
    }

    private static void ValidateVersionCombination(VersionCombination combination, GatePolicy gate)
    {
        RequireConcrete(combination.PalworldVersion, "versionCombination.palworldVersion");
        RequireConcrete(combination.SteamBuild, "versionCombination.steamBuild");
        RequireConcrete(combination.Ue4ssVersion, "versionCombination.ue4ssVersion");
        RequireConcrete(combination.NativeBridgeVersion, "versionCombination.nativeBridgeVersion");
        RequireConcrete(combination.PalDefenderVersion, "versionCombination.palDefenderVersion");
        RequireConcrete(combination.CaddyVersion, "versionCombination.caddyVersion");
        if (!NativeCapabilities.Contains(combination.NativeCapability))
        {
            throw Invalid(
                "ACCEPTANCE_NATIVE_CAPABILITY_INVALID",
                "nativeCapability must be unavailable, experimental or stable.");
        }
        if (gate.RequiredPalworldVersion is not null &&
            combination.PalworldVersion != gate.RequiredPalworldVersion)
        {
            throw Invalid(
                "ACCEPTANCE_PALWORLD_VERSION_MISMATCH",
                $"Gate '{gate.Id}' requires Palworld '{gate.RequiredPalworldVersion}'.");
        }
        if (gate.RequiredSteamBuild is not null &&
            combination.SteamBuild != gate.RequiredSteamBuild)
        {
            throw Invalid(
                "ACCEPTANCE_STEAM_BUILD_MISMATCH",
                $"Gate '{gate.Id}' requires Steam build '{gate.RequiredSteamBuild}'.");
        }
        if (gate.RequiredNativeCapability is not null &&
            combination.NativeCapability != gate.RequiredNativeCapability)
        {
            throw Invalid(
                "ACCEPTANCE_NATIVE_CAPABILITY_MISMATCH",
                $"Gate '{gate.Id}' requires native capability '{gate.RequiredNativeCapability}'.");
        }
        RequireCommit(combination.ControlApiCommit, "versionCombination.controlApiCommit");
        RequireNonZeroSha256(
            combination.DeploymentPackageSha256,
            "versionCombination.deploymentPackageSha256");
        RequireNonZeroSha256(
            combination.ConfigurationSha256,
            "versionCombination.configurationSha256");
        if (!CombinationPattern.IsMatch(combination.CombinationId) ||
            IsAllZeroDigest(combination.CombinationId) ||
            combination.CombinationId != ComputeCombinationId(combination))
        {
            throw Invalid(
                "ACCEPTANCE_VERSION_COMBINATION_ID_MISMATCH",
                $"combinationId must equal '{ComputeCombinationId(combination)}'.");
        }
    }

    private static void ValidateRunbook(RunbookReference runbook)
    {
        RequireIdentifier(runbook.Id, "runbook.id");
        RequireConcrete(runbook.Version, "runbook.version");
        RequireCommit(runbook.Commit, "runbook.commit");
    }

    private static void ValidateExecution(
        ExecutionRecord execution,
        GatePolicy gate,
        DateTimeOffset now)
    {
        ValidateSubject(execution.Executor, "execution.executor");
        if (execution.Executor.Role != gate.RequiredExecutorRole)
        {
            throw Invalid(
                "ACCEPTANCE_EXECUTOR_ROLE_MISMATCH",
                $"Gate '{gate.Id}' requires executor role '{gate.RequiredExecutorRole}'.");
        }
        if (gate.RequireExecutorNotImplementationContributor &&
            execution.Executor.ImplementationContributor)
        {
            throw Invalid(
                "ACCEPTANCE_EXECUTOR_NOT_INDEPENDENT",
                $"Gate '{gate.Id}' requires a non-implementation executor.");
        }
        var started = RequireUtc(execution.StartedAt, "execution.startedAt");
        var ended = RequireUtc(execution.EndedAt, "execution.endedAt");
        if (ended <= started || ended > now.AddMinutes(5))
        {
            throw Invalid(
                "ACCEPTANCE_EXECUTION_TIME_INVALID",
                "execution.endedAt must be after startedAt and not be in the future.");
        }
        if ((ended - started).TotalSeconds < gate.MinimumDurationSeconds)
        {
            throw Invalid(
                "ACCEPTANCE_EXECUTION_DURATION_TOO_SHORT",
                $"Gate '{gate.Id}' requires at least {gate.MinimumDurationSeconds} seconds of live execution.");
        }
    }

    private static void ValidateParticipants(
        IReadOnlyList<SubjectRecord> participants,
        GatePolicy gate)
    {
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var participant in participants)
        {
            ValidateSubject(participant, "participants[]");
            if (!subjects.Add(participant.SubjectId))
            {
                throw Invalid(
                    "ACCEPTANCE_PARTICIPANT_DUPLICATE",
                    $"Participant '{participant.SubjectId}' occurs more than once.");
            }
        }
        foreach (var constraint in gate.ParticipantConstraints)
        {
            var matching = participants
                .Where(participant => participant.Role == constraint.Role)
                .ToArray();
            if (matching.Length < constraint.Minimum ||
                (constraint.Maximum is not null && matching.Length > constraint.Maximum.Value))
            {
                throw Invalid(
                    "ACCEPTANCE_PARTICIPANT_COUNT_INVALID",
                    $"Gate '{gate.Id}' requires {constraint.Minimum}..{constraint.Maximum?.ToString() ?? "unbounded"} distinct '{constraint.Role}' participant(s).");
            }
            if (constraint.AllMustNotBeImplementationContributors &&
                matching.Any(participant => participant.ImplementationContributor))
            {
                throw Invalid(
                    "ACCEPTANCE_PARTICIPANT_NOT_INDEPENDENT",
                    $"All '{constraint.Role}' participants must be independent from implementation.");
            }
        }
    }

    private static IReadOnlyDictionary<string, EvidenceArtifact> ValidateArtifacts(
        AcceptanceManifest manifest,
        string root,
        DateTimeOffset now,
        VerificationFileLease lease)
    {
        if (manifest.Artifacts.Count == 0)
        {
            throw Invalid("ACCEPTANCE_ARTIFACTS_EMPTY", "At least one evidence artifact is required.");
        }
        var artifacts = new Dictionary<string, EvidenceArtifact>(StringComparer.Ordinal);
        var paths = new HashSet<string>(PathComparer());
        foreach (var artifact in manifest.Artifacts)
        {
            RequireIdentifier(artifact.Id, "artifacts[].id");
            RequireIdentifier(artifact.Role, $"artifacts[{artifact.Id}].role");
            RequireConcrete(artifact.MediaType, $"artifacts[{artifact.Id}].mediaType");
            RequireConcrete(artifact.Producer, $"artifacts[{artifact.Id}].producer");
            if (PlaceholderTokenPattern.IsMatch(artifact.Producer))
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_PRODUCER_PLACEHOLDER",
                    $"Artifact '{artifact.Id}' has mock/fixture/placeholder provenance.");
            }
            if (artifact.CaptureMode != "live")
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_NOT_LIVE",
                    $"Artifact '{artifact.Id}' is not marked as a live capture.");
            }
            var capturedAt = RequireUtc(artifact.CapturedAt, $"artifacts[{artifact.Id}].capturedAt");
            if (capturedAt > manifest.Review.ReviewedAt || capturedAt > now.AddMinutes(5))
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_TIME_INVALID",
                    $"Artifact '{artifact.Id}' was captured after review or in the future.");
            }
            if (artifact.Role is not ("sensitive-scan-report" or "review-record") &&
                (capturedAt < manifest.Execution.StartedAt ||
                 capturedAt > manifest.Execution.EndedAt))
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_EXECUTION_TIME_INVALID",
                    $"Artifact '{artifact.Id}' was not captured inside the recorded live execution window.");
            }
            if (PlaceholderTokenPattern.IsMatch(artifact.Path))
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_PATH_PLACEHOLDER",
                    $"Artifact '{artifact.Id}' path contains a mock/fixture/placeholder marker.");
            }
            var fullPath = ResolveContainedFile(
                root,
                artifact.Path,
                "ACCEPTANCE_ARTIFACT_PATH_INVALID");
            if (!paths.Add(fullPath))
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_PATH_DUPLICATE",
                    $"Artifact path '{artifact.Path}' is used more than once.");
            }
            var textual = IsTextual(artifact.MediaType);
            var snapshot = lease.Acquire(
                fullPath,
                captureSample: false,
                textualArtifactId: textual ? artifact.Id : null);
            if (snapshot.SizeBytes <= 0 || artifact.SizeBytes <= 0)
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_EMPTY",
                    $"Artifact '{artifact.Id}' is empty; empty evidence cannot satisfy a gate.");
            }
            if (snapshot.SizeBytes != artifact.SizeBytes)
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_SIZE_MISMATCH",
                    $"Artifact '{artifact.Id}' size does not match the manifest.");
            }
            RequireSha256(artifact.Sha256, $"artifacts[{artifact.Id}].sha256");
            if (snapshot.Sha256 != artifact.Sha256)
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_HASH_MISMATCH",
                    $"Artifact '{artifact.Id}' SHA-256 does not match the manifest.");
            }
            if (!artifacts.TryAdd(artifact.Id, artifact))
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_ID_DUPLICATE",
                    $"Artifact id '{artifact.Id}' occurs more than once.");
            }
        }
        return artifacts;
    }

    private static void ValidateChecksAndMetrics(
        AcceptanceManifest manifest,
        GatePolicy gate,
        IReadOnlyDictionary<string, EvidenceArtifact> artifacts,
        HashSet<string> referencedArtifacts)
    {
        var checks = new Dictionary<string, EvidenceCheck>(StringComparer.Ordinal);
        foreach (var check in manifest.Checks)
        {
            RequireIdentifier(check.Id, "checks[].id");
            if (!checks.TryAdd(check.Id, check))
            {
                throw Invalid(
                    "ACCEPTANCE_CHECK_DUPLICATE",
                    $"Check '{check.Id}' occurs more than once.");
            }
            if (check.Result != "pass")
            {
                throw Invalid(
                    "ACCEPTANCE_CHECK_NOT_PASS",
                    $"Check '{check.Id}' is not pass.");
            }
            RequireHumanText(check.Summary, $"checks[{check.Id}].summary");
            RequireArtifactReferences(
                check.ArtifactIds,
                artifacts,
                referencedArtifacts,
                $"checks[{check.Id}].artifactIds");
        }

        var isNotApplicable = manifest.Conclusion.Result == "not-applicable";
        var requiredChecks = isNotApplicable
            ? gate.NotApplicablePolicy?.RequiredChecks
                ?? throw Invalid(
                    "ACCEPTANCE_NOT_APPLICABLE_FORBIDDEN",
                    $"Gate '{gate.Id}' cannot be marked not-applicable.")
            : gate.RequiredChecks;
        foreach (var requiredCheck in requiredChecks)
        {
            if (!checks.ContainsKey(requiredCheck))
            {
                throw Invalid(
                    "ACCEPTANCE_REQUIRED_CHECK_MISSING",
                    $"Gate '{gate.Id}' is missing required check '{requiredCheck}'.");
            }
        }

        var metrics = new Dictionary<string, EvidenceMetric>(StringComparer.Ordinal);
        foreach (var metric in manifest.Metrics)
        {
            RequireIdentifier(metric.Id, "metrics[].id");
            RequireIdentifier(metric.Unit, $"metrics[{metric.Id}].unit");
            if (!metrics.TryAdd(metric.Id, metric))
            {
                throw Invalid(
                    "ACCEPTANCE_METRIC_DUPLICATE",
                    $"Metric '{metric.Id}' occurs more than once.");
            }
            RequireArtifactReferences(
                metric.ArtifactIds,
                artifacts,
                referencedArtifacts,
                $"metrics[{metric.Id}].artifactIds");
        }
        if (!isNotApplicable)
        {
            foreach (var constraint in gate.MetricConstraints)
            {
                if (!metrics.TryGetValue(constraint.Id, out var metric))
                {
                    throw Invalid(
                        "ACCEPTANCE_REQUIRED_METRIC_MISSING",
                        $"Gate '{gate.Id}' is missing metric '{constraint.Id}'.");
                }
                if (metric.Unit != constraint.Unit ||
                    !Compare(metric.Value, constraint.Operator, constraint.Value))
                {
                    throw Invalid(
                        "ACCEPTANCE_METRIC_CONSTRAINT_FAILED",
                        $"Metric '{constraint.Id}' must be {constraint.Operator} {constraint.Value} {constraint.Unit}; observed {metric.Value} {metric.Unit}.");
                }
            }
        }
    }

    private static void ValidateSensitiveDataScan(
        AcceptanceManifest manifest,
        IReadOnlyDictionary<string, EvidenceArtifact> artifacts,
        HashSet<string> referencedArtifacts,
        DateTimeOffset now)
    {
        var scan = manifest.SensitiveDataScan;
        RequireConcrete(scan.Scanner, "sensitiveDataScan.scanner");
        RequireConcrete(scan.ScannerVersion, "sensitiveDataScan.scannerVersion");
        RequireHumanText(scan.Command, "sensitiveDataScan.command");
        if (scan.Command.Contains('\r', StringComparison.Ordinal) ||
            scan.Command.Contains('\n', StringComparison.Ordinal))
        {
            throw Invalid(
                "ACCEPTANCE_SCAN_COMMAND_INVALID",
                "The recorded scanner command must be one line and must not embed shell output.");
        }
        if (scan.Scope != "evidence-artifacts")
        {
            throw Invalid(
                "ACCEPTANCE_SCAN_SCOPE_INVALID",
                "sensitiveDataScan.scope must be 'evidence-artifacts'.");
        }
        var started = RequireUtc(scan.StartedAt, "sensitiveDataScan.startedAt");
        var ended = RequireUtc(scan.EndedAt, "sensitiveDataScan.endedAt");
        if (started < manifest.Execution.EndedAt || ended < started || ended > now.AddMinutes(5))
        {
            throw Invalid(
                "ACCEPTANCE_SCAN_TIME_INVALID",
                "The final sensitive-data scan must start after execution and end before review.");
        }
        if (scan.Result != "pass" || scan.FindingCount != 0)
        {
            throw Invalid(
                "ACCEPTANCE_SENSITIVE_SCAN_FAILED",
                "Sensitive-data scan result must be pass with exactly zero findings.");
        }
        if (!artifacts.TryGetValue(scan.ReportArtifactId, out var report) ||
            report.Role != "sensitive-scan-report")
        {
            throw Invalid(
                "ACCEPTANCE_SCAN_REPORT_INVALID",
                "sensitiveDataScan.reportArtifactId must reference a sensitive-scan-report artifact.");
        }
        if (report.CapturedAt < started || report.CapturedAt > ended)
        {
            throw Invalid(
                "ACCEPTANCE_SCAN_REPORT_TIME_INVALID",
                "The sensitive scan report capture time must fall inside the recorded scan window.");
        }
        referencedArtifacts.Add(report.Id);

        var scanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifactId in scan.ScannedArtifactIds)
        {
            if (!scanned.Add(artifactId) || !artifacts.TryGetValue(artifactId, out var artifact))
            {
                throw Invalid(
                    "ACCEPTANCE_SCAN_ARTIFACT_INVALID",
                    $"Scanned artifact reference '{artifactId}' is missing or duplicated.");
            }
            if (artifact.Role is "sensitive-scan-report" or "review-record" ||
                artifact.CapturedAt > started)
            {
                throw Invalid(
                    "ACCEPTANCE_SCAN_ARTIFACT_INVALID",
                    $"Artifact '{artifactId}' cannot be in the final evidence-artifact scan set.");
            }
            referencedArtifacts.Add(artifactId);
        }
        foreach (var artifact in artifacts.Values.Where(
                     artifact => artifact.Role is not "sensitive-scan-report" and not "review-record"))
        {
            if (!scanned.Contains(artifact.Id))
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_NOT_SCANNED",
                    $"Evidence artifact '{artifact.Id}' is absent from the final sensitive-data scan set.");
            }
        }
    }

    private static void ValidateReviewAndConclusion(
        AcceptanceManifest manifest,
        GatePolicy gate,
        IReadOnlyDictionary<string, EvidenceArtifact> artifacts,
        HashSet<string> referencedArtifacts,
        DateTimeOffset now)
    {
        var review = manifest.Review;
        ValidateSubject(review.Reviewer, "review.reviewer");
        if (review.Reviewer.Role != "reviewer")
        {
            throw Invalid(
                "ACCEPTANCE_REVIEWER_ROLE_INVALID",
                "review.reviewer.role must be 'reviewer'.");
        }
        if (gate.RequireIndependentReviewer &&
            review.Reviewer.SubjectId == manifest.Execution.Executor.SubjectId)
        {
            throw Invalid(
                "ACCEPTANCE_REVIEWER_NOT_DISTINCT",
                $"Gate '{gate.Id}' requires executor and reviewer to be different subjects.");
        }
        if (gate.RequireReviewerNotImplementationContributor &&
            review.Reviewer.ImplementationContributor)
        {
            throw Invalid(
                "ACCEPTANCE_REVIEWER_NOT_INDEPENDENT",
                $"Gate '{gate.Id}' requires a reviewer who did not implement the feature.");
        }
        var reviewedAt = RequireUtc(review.ReviewedAt, "review.reviewedAt");
        if (reviewedAt < manifest.SensitiveDataScan.EndedAt || reviewedAt > now.AddMinutes(5))
        {
            throw Invalid(
                "ACCEPTANCE_REVIEW_TIME_INVALID",
                "Review must occur after the final sensitive-data scan and not in the future.");
        }
        if (review.Decision != "approved")
        {
            throw Invalid(
                "ACCEPTANCE_REVIEW_NOT_APPROVED",
                "The independent review decision must be approved.");
        }
        RequireHumanText(review.Summary, "review.summary");
        RequireArtifactReferences(
            review.ArtifactIds,
            artifacts,
            referencedArtifacts,
            "review.artifactIds");
        if (review.ArtifactIds.Any(id => artifacts[id].Role != "review-record") ||
            review.ArtifactIds.Any(id => artifacts[id].CapturedAt < manifest.SensitiveDataScan.EndedAt))
        {
            throw Invalid(
                "ACCEPTANCE_REVIEW_ARTIFACT_INVALID",
                "Review artifacts must have role 'review-record' and be captured after the final scan.");
        }

        var conclusion = manifest.Conclusion;
        if (conclusion.Result is not "pass" and not "not-applicable")
        {
            throw Invalid(
                "ACCEPTANCE_CONCLUSION_NOT_PASS",
                "A release gate only verifies when conclusion.result is pass or an explicitly permitted not-applicable decision.");
        }
        if (conclusion.Result == "not-applicable" && gate.NotApplicablePolicy is null)
        {
            throw Invalid(
                "ACCEPTANCE_NOT_APPLICABLE_FORBIDDEN",
                $"Gate '{gate.Id}' cannot be marked not-applicable.");
        }
        var decidedAt = RequireUtc(conclusion.DecidedAt, "conclusion.decidedAt");
        if (decidedAt < reviewedAt || decidedAt > now.AddMinutes(5))
        {
            throw Invalid(
                "ACCEPTANCE_CONCLUSION_TIME_INVALID",
                "Conclusion must be decided after review and not in the future.");
        }
        RequireHumanText(conclusion.Summary, "conclusion.summary");
    }

    private static void ValidateArtifactCoverage(
        AcceptanceManifest manifest,
        GatePolicy gate,
        IReadOnlyDictionary<string, EvidenceArtifact> artifacts,
        HashSet<string> referencedArtifacts)
    {
        var requirements = new List<ArtifactRoleRequirement>
        {
            new() { Role = "sensitive-scan-report", Minimum = 1 },
            new() { Role = "review-record", Minimum = 1 }
        };
        if (manifest.Conclusion.Result == "not-applicable")
        {
            requirements.AddRange(gate.NotApplicablePolicy?.RequiredArtifactRoles ?? []);
        }
        else
        {
            requirements.AddRange(gate.RequiredArtifactRoles);
        }
        foreach (var requirement in requirements)
        {
            var count = artifacts.Values.Count(artifact => artifact.Role == requirement.Role);
            if (count < requirement.Minimum)
            {
                throw Invalid(
                    "ACCEPTANCE_REQUIRED_ARTIFACT_ROLE_MISSING",
                    $"Gate '{gate.Id}' requires at least {requirement.Minimum} artifact(s) with role '{requirement.Role}'.");
            }
        }
        foreach (var artifact in artifacts.Values)
        {
            if (!referencedArtifacts.Contains(artifact.Id))
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_UNREFERENCED",
                    $"Artifact '{artifact.Id}' is not used by a check, metric, scan or review.");
            }
        }
    }

    private static int ValidateRelatedEvidence(
        AcceptanceManifest manifest,
        GatePolicy gate,
        string root,
        LoadedCatalog catalog,
        LoadedSchema schema,
        LoadedTrustStore trustStore,
        DateTimeOffset now,
        HashSet<string> stack,
        VerificationFileLease lease)
    {
        var required = new HashSet<string>(gate.RequiredRelatedGates, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(PathComparer());
        var nestedCount = 0;
        foreach (var relation in manifest.RelatedEvidence)
        {
            if (!observed.Add(relation.GateId))
            {
                throw Invalid(
                    "ACCEPTANCE_RELATED_GATE_DUPLICATE",
                    $"Related gate '{relation.GateId}' occurs more than once.");
            }
            if (!required.Contains(relation.GateId))
            {
                throw Invalid(
                    "ACCEPTANCE_RELATED_GATE_UNEXPECTED",
                    $"Gate '{gate.Id}' does not permit related gate '{relation.GateId}'.");
            }
            var relatedPath = ResolveContainedFile(
                root,
                relation.ManifestPath,
                "ACCEPTANCE_RELATED_MANIFEST_PATH_INVALID");
            if (!paths.Add(relatedPath))
            {
                throw Invalid(
                    "ACCEPTANCE_RELATED_MANIFEST_DUPLICATE",
                    $"Related manifest path '{relation.ManifestPath}' occurs more than once.");
            }
            RequireSha256(relation.Sha256, $"relatedEvidence[{relation.GateId}].sha256");
            var snapshot = lease.Acquire(relatedPath, captureSample: true);
            if (snapshot.SizeBytes <= 0 || snapshot.Sha256 != relation.Sha256)
            {
                throw Invalid(
                    "ACCEPTANCE_RELATED_MANIFEST_HASH_MISMATCH",
                    $"Related manifest for gate '{relation.GateId}' is empty or has changed.");
            }
            var relatedManifest = DeserializeStrict<AcceptanceManifest>(
                snapshot.Sample,
                "ACCEPTANCE_RELATED_MANIFEST_INVALID_JSON");
            if (!EvidenceCanonicalJson.CreateVersionCombinationPayload(
                    relatedManifest.VersionCombination).AsSpan().SequenceEqual(
                    EvidenceCanonicalJson.CreateVersionCombinationPayload(
                        manifest.VersionCombination)))
            {
                throw Invalid(
                    "ACCEPTANCE_RELATED_COMBINATION_MISMATCH",
                    $"Related manifest '{relation.ManifestPath}' has different canonical version-combination fields.");
            }
            var summary = VerifyManifest(
                relatedPath,
                catalog,
                schema,
                trustStore,
                now,
                stack,
                lease);
            if (summary.GateId != relation.GateId || summary.Conclusion != "pass")
            {
                throw Invalid(
                    "ACCEPTANCE_RELATED_MANIFEST_INVALID",
                    $"Related manifest '{relation.ManifestPath}' does not provide passing evidence for '{relation.GateId}'.");
            }
            var after = lease.Rehash(relatedPath);
            if (after.SizeBytes != snapshot.SizeBytes || after.Sha256 != relation.Sha256)
            {
                throw Invalid(
                    "ACCEPTANCE_RELATED_MANIFEST_HASH_MISMATCH",
                    $"Related manifest for gate '{relation.GateId}' changed during recursive verification.");
            }
            nestedCount += 1 + summary.RelatedManifestCount;
        }
        if (!required.SetEquals(observed))
        {
            var missing = required.Except(observed, StringComparer.Ordinal).Order().ToArray();
            throw Invalid(
                "ACCEPTANCE_RELATED_GATE_MISSING",
                $"Gate '{gate.Id}' is missing related evidence: {string.Join(", ", missing)}.");
        }
        return nestedCount;
    }

    private static void ValidateSubject(SubjectRecord subject, string field)
    {
        if (!SubjectPattern.IsMatch(subject.SubjectId) || IsAllZeroDigest(subject.SubjectId))
        {
            throw Invalid(
                "ACCEPTANCE_SUBJECT_ID_INVALID",
                $"{field}.subjectId must be a non-zero keyed pseudonym in the form subj:hmac-sha256:<64 lowercase hex>.");
        }
        RequireIdentifier(subject.IdentityProvider, $"{field}.identityProvider");
        RequireIdentifier(subject.Role, $"{field}.role");
    }

    private static void ValidateTrustedSubjects(
        AcceptanceManifest manifest,
        LoadedTrustStore trustStore)
    {
        ValidateTrustedSubject(manifest.Execution.Executor, trustStore, "execution.executor");
        ValidateTrustedSubject(manifest.Review.Reviewer, trustStore, "review.reviewer");
        foreach (var participant in manifest.Participants)
        {
            ValidateTrustedSubject(participant, trustStore, "participants[]");
        }
    }

    private static void ValidateTrustedSubject(
        SubjectRecord subject,
        LoadedTrustStore trustStore,
        string field)
    {
        if (!trustStore.Subjects.TryGetValue(subject.SubjectId, out var trusted) ||
            trusted.Revoked ||
            trusted.Subject.IdentityProvider != subject.IdentityProvider ||
            trusted.Subject.Role != subject.Role ||
            trusted.Subject.ImplementationContributor != subject.ImplementationContributor)
        {
            throw Invalid(
                "ACCEPTANCE_SUBJECT_NOT_TRUSTED",
                $"{field} must exactly match an active subject mapped by the externally pinned identity trust store.");
        }
    }

    private static void ValidateEvidenceSignatures(
        AcceptanceManifest manifest,
        LoadedTrustStore trustStore,
        string evidenceRoot,
        VerificationFileLease lease)
    {
        var signatures = manifest.Signatures;
        if (signatures.PayloadSchema != EvidenceConstants.SignaturePayloadSchemaId ||
            signatures.TrustStoreSha256 != trustStore.Sha256)
        {
            throw Invalid(
                "ACCEPTANCE_SIGNATURE_CONTEXT_MISMATCH",
                "The manifest signatures do not bind the v1 payload schema and exact pinned trust store.");
        }
        if (signatures.Executor.KeyId == signatures.Reviewer.KeyId)
        {
            throw Invalid(
                "ACCEPTANCE_SIGNATURE_KEYS_NOT_DISTINCT",
                "Executor and reviewer must use different ECDSA P-256 keys.");
        }
        var executorKey = ResolveSigningKey(
            signatures.Executor,
            trustStore,
            manifest.Execution.Executor,
            "executor");
        var reviewerKey = ResolveSigningKey(
            signatures.Reviewer,
            trustStore,
            manifest.Review.Reviewer,
            "reviewer");
        if (trustStore.KeyFingerprints[executorKey.KeyId] ==
            trustStore.KeyFingerprints[reviewerKey.KeyId])
        {
            throw Invalid(
                "ACCEPTANCE_SIGNATURE_PUBLIC_KEYS_NOT_DISTINCT",
                "Executor and reviewer signatures must use different canonical P-256 public keys.");
        }
        var payload = EvidenceCanonicalJson.CreateSignaturePayload(
            manifest,
            trustStore.Sha256,
            signatures.Executor.KeyId,
            signatures.Reviewer.KeyId);
        VerifyEvidenceSignature(signatures.Executor, executorKey, payload, "executor");
        VerifyEvidenceSignature(signatures.Reviewer, reviewerKey, payload, "reviewer");
        ValidatePostReviewSensitiveData(manifest, evidenceRoot, payload, lease);
    }

    private static TrustedIdentityKey ResolveSigningKey(
        EvidenceSignature signature,
        LoadedTrustStore trustStore,
        SubjectRecord expectedSubject,
        string phase)
    {
        RequireIdentifier(signature.KeyId, $"signatures.{phase}.keyId");
        if (signature.Algorithm != EvidenceConstants.SignatureAlgorithm ||
            !trustStore.Keys.TryGetValue(signature.KeyId, out var key) ||
            key.Revoked ||
            key.Subject.SubjectId != expectedSubject.SubjectId)
        {
            throw Invalid(
                "ACCEPTANCE_SIGNATURE_KEY_NOT_TRUSTED",
                $"The {phase} signature key is revoked, absent, mismatched to its trusted subject, or uses the wrong algorithm.");
        }
        return key;
    }

    private static void VerifyEvidenceSignature(
        EvidenceSignature signature,
        TrustedIdentityKey trustedKey,
        byte[] payload,
        string phase)
    {
        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.SignatureBase64);
        }
        catch (FormatException)
        {
            throw Invalid(
                "ACCEPTANCE_SIGNATURE_INVALID",
                $"The {phase} signature is not valid base64.");
        }
        if (signatureBytes.Length != 64)
        {
            throw Invalid(
                "ACCEPTANCE_SIGNATURE_INVALID",
                $"The {phase} signature is not a fixed-width P-256 P1363 value.");
        }
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(trustedKey.PublicKeySpkiBase64),
            out var read);
        if (read == 0 || !ecdsa.VerifyData(
                payload,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw Invalid(
                "ACCEPTANCE_SIGNATURE_INVALID",
                $"The {phase} signature does not verify over the complete canonical evidence envelope.");
        }
    }

    private static void ValidatePostReviewSensitiveData(
        AcceptanceManifest manifest,
        string evidenceRoot,
        byte[] canonicalPayload,
        VerificationFileLease lease)
    {
        ScanSensitiveText(
            Encoding.UTF8.GetString(canonicalPayload),
            "canonical manifest envelope");
        foreach (var artifact in manifest.Artifacts.Where(artifact =>
                     artifact.Role is "review-record" or "sensitive-scan-report"))
        {
            if (!IsTextual(artifact.MediaType) || artifact.SizeBytes > 8 * 1024 * 1024)
            {
                throw Invalid(
                    "ACCEPTANCE_POST_REVIEW_SCAN_MEDIA_TYPE_INVALID",
                    $"Post-review artifact '{artifact.Id}' must be a bounded textual document for the verifier's second scan.");
            }
            var path = ResolveContainedFile(
                evidenceRoot,
                artifact.Path,
                "ACCEPTANCE_POST_REVIEW_SCAN_PATH_INVALID");
            byte[] bytes;
            try
            {
                bytes = lease.ReadAllBytes(
                    path,
                    8 * 1024 * 1024,
                    "ACCEPTANCE_POST_REVIEW_SCAN_READ_FAILED");
                if (bytes.LongLength != artifact.SizeBytes ||
                    "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)) != artifact.Sha256)
                {
                    throw Invalid(
                        "ACCEPTANCE_POST_REVIEW_ARTIFACT_CHANGED",
                        $"Post-review artifact '{artifact.Id}' changed during verification.");
                }
            }
            catch (IOException exception)
            {
                throw Invalid(
                    "ACCEPTANCE_POST_REVIEW_SCAN_READ_FAILED",
                    $"Post-review artifact '{artifact.Id}' could not be re-read: {exception.Message}");
            }
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw Invalid(
                    "ACCEPTANCE_POST_REVIEW_SCAN_TEXT_INVALID",
                    $"Post-review artifact '{artifact.Id}' is not valid UTF-8.");
            }
            ScanSensitiveText(text, $"post-review artifact '{artifact.Id}'");
        }
    }

    private static void ScanSensitiveText(string text, string source)
    {
        if (PrivateKeyPattern.IsMatch(text) ||
            BearerPattern.IsMatch(text) ||
            SensitiveAssignmentPattern.IsMatch(text))
        {
            throw Invalid(
                "ACCEPTANCE_ENVELOPE_SENSITIVE_DATA_FOUND",
                $"The verifier's post-review sensitive-data scan found a credential-like value in {source}.");
        }
    }

    private static string ValidateP256PublicKey(TrustedIdentityKey key)
    {
        try
        {
            var bytes = Convert.FromBase64String(key.PublicKeySpkiBase64);
            if (key.PublicKeySpkiBase64 != Convert.ToBase64String(bytes))
            {
                throw new CryptographicException("The SPKI base64 is not canonical.");
            }
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            if (read != bytes.Length || ecdsa.KeySize != 256 ||
                parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                throw new CryptographicException("The key is not NIST P-256.");
            }
            var canonicalSpki = ecdsa.ExportSubjectPublicKeyInfo();
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(canonicalSpki));
        }
        catch (Exception exception) when (
            exception is FormatException or CryptographicException or ArgumentException)
        {
            throw Invalid(
                "ACCEPTANCE_TRUST_KEY_INVALID",
                $"Trust key '{key.KeyId}' is not a valid ECDSA NIST P-256 SPKI public key.");
        }
    }

    private static void ValidateGatePolicy(GatePolicy gate)
    {
        RequireIdentifier(gate.Id, "gates[].id");
        if (gate.Priority is not "P0" and not "P1" and not "P2")
        {
            throw Invalid(
                "ACCEPTANCE_CATALOG_PRIORITY_INVALID",
                $"Gate '{gate.Id}' has invalid priority '{gate.Priority}'.");
        }
        RequireHumanText(gate.Title, $"gates[{gate.Id}].title");
        if (gate.TodoReferences.Count == 0 || gate.TodoReferences.Any(string.IsNullOrWhiteSpace))
        {
            throw Invalid(
                "ACCEPTANCE_CATALOG_TODO_REFERENCE_MISSING",
                $"Gate '{gate.Id}' must point to at least one TODO requirement.");
        }
        if (gate.AllowedEnvironmentKinds.Count == 0 ||
            gate.AllowedEnvironmentKinds.Any(kind => !EnvironmentKinds.Contains(kind)) ||
            gate.AllowedEnvironmentKinds.Distinct(StringComparer.Ordinal).Count() != gate.AllowedEnvironmentKinds.Count)
        {
            throw Invalid(
                "ACCEPTANCE_CATALOG_ENVIRONMENT_INVALID",
                $"Gate '{gate.Id}' has an invalid environment policy.");
        }
        RequireIdentifier(gate.RequiredExecutorRole, $"gates[{gate.Id}].requiredExecutorRole");
        if (gate.MinimumDurationSeconds < 0)
        {
            throw Invalid(
                "ACCEPTANCE_CATALOG_DURATION_INVALID",
                $"Gate '{gate.Id}' has a negative minimum duration.");
        }
        if (gate.RequiredPalworldVersion is not null)
        {
            RequireConcrete(gate.RequiredPalworldVersion, $"gates[{gate.Id}].requiredPalworldVersion");
        }
        if (gate.RequiredSteamBuild is not null)
        {
            RequireConcrete(gate.RequiredSteamBuild, $"gates[{gate.Id}].requiredSteamBuild");
        }
        if (gate.RequiredNativeCapability is not null &&
            !NativeCapabilities.Contains(gate.RequiredNativeCapability))
        {
            throw Invalid(
                "ACCEPTANCE_CATALOG_NATIVE_CAPABILITY_INVALID",
                $"Gate '{gate.Id}' has invalid requiredNativeCapability.");
        }
        RequireUnique(
            gate.ParticipantConstraints.Select(item => item.Role),
            $"gates[{gate.Id}].participantConstraints");
        foreach (var constraint in gate.ParticipantConstraints)
        {
            RequireIdentifier(constraint.Role, $"gates[{gate.Id}].participantConstraints[].role");
            if (constraint.Minimum < 0 ||
                constraint.Maximum is not null && constraint.Maximum < constraint.Minimum)
            {
                throw Invalid(
                    "ACCEPTANCE_CATALOG_PARTICIPANT_CONSTRAINT_INVALID",
                    $"Gate '{gate.Id}' has an invalid participant constraint for '{constraint.Role}'.");
            }
        }
        ValidateArtifactRequirements(gate.RequiredArtifactRoles, gate.Id);
        ValidateRequiredChecks(gate.RequiredChecks, gate.Id);
        RequireUnique(
            gate.MetricConstraints.Select(item => item.Id),
            $"gates[{gate.Id}].metricConstraints");
        foreach (var constraint in gate.MetricConstraints)
        {
            RequireIdentifier(constraint.Id, $"gates[{gate.Id}].metricConstraints[].id");
            RequireIdentifier(constraint.Unit, $"gates[{gate.Id}].metricConstraints[{constraint.Id}].unit");
            if (constraint.Operator is not "eq" and not "gte" and not "lte")
            {
                throw Invalid(
                    "ACCEPTANCE_CATALOG_METRIC_OPERATOR_INVALID",
                    $"Gate '{gate.Id}' metric '{constraint.Id}' has invalid operator.");
            }
        }
        RequireUnique(gate.RequiredRelatedGates, $"gates[{gate.Id}].requiredRelatedGates");
        foreach (var related in gate.RequiredRelatedGates)
        {
            RequireIdentifier(related, $"gates[{gate.Id}].requiredRelatedGates[]");
        }
        if (gate.NotApplicablePolicy is not null)
        {
            ValidateArtifactRequirements(gate.NotApplicablePolicy.RequiredArtifactRoles, gate.Id);
            ValidateRequiredChecks(gate.NotApplicablePolicy.RequiredChecks, gate.Id);
            if (!gate.RequireIndependentReviewer ||
                !gate.RequireReviewerNotImplementationContributor)
            {
                throw Invalid(
                    "ACCEPTANCE_CATALOG_NOT_APPLICABLE_REVIEW_INVALID",
                    $"Gate '{gate.Id}' permits not-applicable without an independent non-implementation review.");
            }
        }
    }

    private static void ValidateArtifactRequirements(
        IReadOnlyList<ArtifactRoleRequirement> requirements,
        string gateId)
    {
        RequireUnique(
            requirements.Select(item => item.Role),
            $"gates[{gateId}].requiredArtifactRoles");
        foreach (var requirement in requirements)
        {
            RequireIdentifier(requirement.Role, $"gates[{gateId}].requiredArtifactRoles[].role");
            if (requirement.Minimum <= 0)
            {
                throw Invalid(
                    "ACCEPTANCE_CATALOG_ARTIFACT_REQUIREMENT_INVALID",
                    $"Gate '{gateId}' artifact role '{requirement.Role}' has a non-positive minimum.");
            }
        }
    }

    private static void ValidateRequiredChecks(IReadOnlyList<string> checks, string gateId)
    {
        RequireUnique(checks, $"gates[{gateId}].requiredChecks");
        foreach (var check in checks)
        {
            RequireIdentifier(check, $"gates[{gateId}].requiredChecks[]");
        }
    }

    private static void RequireArtifactReferences(
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, EvidenceArtifact> artifacts,
        HashSet<string> referenced,
        string field)
    {
        if (ids.Count == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw Invalid(
                "ACCEPTANCE_ARTIFACT_REFERENCE_INVALID",
                $"{field} must contain distinct artifact ids and cannot be empty.");
        }
        foreach (var id in ids)
        {
            if (!artifacts.ContainsKey(id))
            {
                throw Invalid(
                    "ACCEPTANCE_ARTIFACT_REFERENCE_MISSING",
                    $"{field} references missing artifact '{id}'.");
            }
            referenced.Add(id);
        }
    }

    private static bool Compare(decimal observed, string operation, decimal expected) =>
        operation switch
        {
            "eq" => observed == expected,
            "gte" => observed >= expected,
            "lte" => observed <= expected,
            _ => false
        };

    private static T DeserializeStrict<T>(byte[] utf8, string code)
    {
        try
        {
            RejectDuplicateProperties(utf8);
            return JsonSerializer.Deserialize<T>(utf8, JsonOptions)
                ?? throw Invalid(code, "The JSON document is empty.");
        }
        catch (JsonException exception)
        {
            throw Invalid(code, $"Strict JSON deserialization failed: {exception.Message}");
        }
    }

    private static void RejectDuplicateProperties(byte[] utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            RejectDuplicateProperties(document.RootElement, "$");
        }
        catch (JsonException)
        {
            throw;
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid(
                        "ACCEPTANCE_JSON_DUPLICATE_PROPERTY",
                        $"JSON object '{path}' contains duplicate property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

    private static void ValidateManifestDocumentBytes(byte[] utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaximumStructuredDocumentBytes ||
            utf8[0] != (byte)'{' ||
            utf8.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            throw Invalid(
                "ACCEPTANCE_MANIFEST_ENCODING_INVALID",
                "Manifest must be bounded UTF-8 without a byte-order mark.");
        }
        try
        {
            RejectDuplicateProperties(utf8);
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            using var _ = JsonDocument.ParseValue(ref reader);
            var rootEnd = utf8.Length;
            while (rootEnd > 0 && utf8[rootEnd - 1] is
                   (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                rootEnd -= 1;
            }
            var trailing = utf8.AsSpan(rootEnd);
            if (trailing.Length != 0 &&
                !(trailing.Length == 1 && trailing[0] == (byte)'\n'))
            {
                throw Invalid(
                    "ACCEPTANCE_MANIFEST_TRAILING_BYTES_INVALID",
                    "Manifest may end immediately after the JSON object or with one LF byte only.");
            }
            var rawText = new UTF8Encoding(false, true).GetString(utf8);
            ScanSensitiveText(rawText, "raw manifest bytes");
        }
        catch (DecoderFallbackException)
        {
            throw Invalid(
                "ACCEPTANCE_MANIFEST_ENCODING_INVALID",
                "Manifest is not strict UTF-8.");
        }
        catch (JsonException exception)
        {
            throw Invalid(
                "ACCEPTANCE_MANIFEST_INVALID_JSON",
                $"Manifest JSON is invalid: {exception.Message}");
        }
    }

    private static void AcquirePolicyLease(
        VerificationFileLease lease,
        string source,
        string expectedSha256,
        string label)
    {
        if (source.StartsWith("embedded:", StringComparison.Ordinal))
        {
            return;
        }
        var snapshot = lease.Acquire(source, captureSample: true);
        if (snapshot.Sha256 != expectedSha256)
        {
            throw Invalid(
                "ACCEPTANCE_POLICY_CHANGED_DURING_VERIFICATION",
                $"The externally loaded {label} changed during verification.");
        }
    }

    internal static string ExistingRegularFile(string path, string code)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid(code, "A file path is required.");
        }
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw Invalid(code, $"File does not exist: {fullPath}");
        }
        RejectReparseChain(fullPath, code);
        return fullPath;
    }

    internal static string ResolveContainedFile(string root, string relativePath, string code)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relativePath))
        {
            throw Invalid(code, "Evidence paths must be non-empty portable relative paths using '/'.");
        }
        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw Invalid(code, $"Path traversal and empty segments are forbidden: '{relativePath}'.");
        }
        foreach (var segment in segments)
        {
            ValidatePortablePathSegment(segment, relativePath, code);
        }
        var fullRoot = Path.GetFullPath(root);
        RejectReparseChain(fullRoot, code);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(segments)));
        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, PathComparison()) || !File.Exists(fullPath))
        {
            throw Invalid(code, $"Path leaves the evidence root or does not exist: '{relativePath}'.");
        }
        RejectReparseChain(fullPath, code);
        return fullPath;
    }

    private static void ValidatePortablePathSegment(
        string segment,
        string relativePath,
        string code)
    {
        var deviceStem = segment.Split('.', 2)[0];
        if (!PortablePathSegmentPattern.IsMatch(segment) ||
            segment.EndsWith(".", StringComparison.Ordinal) ||
            segment.EndsWith(" ", StringComparison.Ordinal) ||
            ReservedPortableNames.Contains(deviceStem) ||
            segment.Contains(':', StringComparison.Ordinal))
        {
            throw Invalid(
                code,
                $"Evidence path segments must be portable ASCII and cannot be ADS/device names or end in dot/space: '{relativePath}'.");
        }
    }

    private static void RejectReparseChain(string path, string code)
    {
        var fullPath = Path.GetFullPath(path);
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                RejectReparsePoint(current, code);
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, PathComparison()))
            {
                break;
            }
            current = parent;
        }
    }

    private static void RejectReparsePoint(string path, string code)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid(code, $"Symbolic links and reparse points are not accepted as evidence: '{path}'.");
        }
    }

    private static FileSnapshot ReadFileSnapshot(
        string path,
        bool captureSample,
        string? textualArtifactId = null)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return ReadLeasedStreamSnapshot(stream, captureSample, textualArtifactId);
    }

    internal static FileSnapshot ReadLeasedStreamSnapshot(
        FileStream stream,
        bool captureSample,
        string? textualArtifactId = null)
    {
        stream.Position = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sample = captureSample ? new MemoryStream() : null;
        var buffer = new byte[128 * 1024];
        var decoder = textualArtifactId is null
            ? null
            : new UTF8Encoding(false, true).GetDecoder();
        var characters = decoder is null
            ? null
            : new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var textTail = string.Empty;
        long total = 0;
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                hasher.AppendData(buffer, 0, read);
                if (sample is not null)
                {
                    if (total + read > MaximumStructuredDocumentBytes)
                    {
                        throw Invalid(
                            "ACCEPTANCE_STRUCTURED_DOCUMENT_TOO_LARGE",
                            $"Structured JSON input exceeds {MaximumStructuredDocumentBytes} bytes.");
                    }
                    sample.Write(buffer, 0, read);
                }
                if (decoder is not null && characters is not null)
                {
                    var characterCount = decoder.GetChars(
                        buffer,
                        0,
                        read,
                        characters,
                        0,
                        flush: false);
                    textTail = ValidateTextWindow(
                        textualArtifactId!,
                        textTail,
                        characters.AsSpan(0, characterCount));
                }
                total += read;
            }
            if (decoder is not null && characters is not null)
            {
                var characterCount = decoder.GetChars(
                    Array.Empty<byte>(),
                    0,
                    0,
                    characters,
                    0,
                    flush: true);
                _ = ValidateTextWindow(
                    textualArtifactId!,
                    textTail,
                    characters.AsSpan(0, characterCount));
            }
        }
        catch (DecoderFallbackException)
        {
            throw Invalid(
                "ACCEPTANCE_ARTIFACT_TEXT_INVALID",
                $"Text artifact '{textualArtifactId}' is not valid UTF-8.");
        }
        finally
        {
            stream.Position = 0;
        }
        return new FileSnapshot(
            "sha256:" + Convert.ToHexStringLower(hasher.GetHashAndReset()),
            total,
            sample?.ToArray() ?? []);
    }

    private static FileSnapshot ReadEmbeddedSnapshot(string resourceName)
    {
        using var stream = typeof(EvidenceVerifier).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw Invalid(
                "ACCEPTANCE_TRUSTED_POLICY_MISSING",
                "The verifier's embedded trusted policy is missing.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        var bytes = output.ToArray();
        if (bytes.Length == 0 || bytes.Length > 1024 * 1024)
        {
            throw Invalid(
                "ACCEPTANCE_TRUSTED_POLICY_INVALID",
                "The verifier's embedded trusted policy has an invalid size.");
        }
        return new FileSnapshot(
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.LongLength,
            bytes);
    }

    private static string ValidateTextWindow(
        string artifactId,
        string previousTail,
        ReadOnlySpan<char> characters)
    {
        const int retainedCharacters = 256;
        var text = string.Concat(previousTail.AsSpan(), characters);
        if (PlaceholderContentPattern.IsMatch(text))
        {
            throw Invalid(
                "ACCEPTANCE_ARTIFACT_CONTENT_PLACEHOLDER",
                $"Artifact '{artifactId}' contains an explicit mock/fixture/placeholder marker.");
        }
        return text.Length <= retainedCharacters
            ? text
            : text[^retainedCharacters..];
    }

    private static bool IsTextual(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase);

    private static void RequireIdentifier(string value, string field)
    {
        if (!IdPattern.IsMatch(value))
        {
            throw Invalid(
                "ACCEPTANCE_IDENTIFIER_INVALID",
                $"{field} must be a lower-case stable identifier.");
        }
    }

    private static void RequireConcrete(string value, string field)
    {
        if (value is not null && value.Any(char.IsControl))
        {
            throw Invalid(
                "ACCEPTANCE_CONTROL_CHARACTER_REJECTED",
                $"{field} cannot contain control characters.");
        }
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Contains('<', StringComparison.Ordinal) ||
            value.Contains('>', StringComparison.Ordinal) ||
            PlaceholderTokenPattern.IsMatch(value) ||
            value.Equals("todo", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                "ACCEPTANCE_PLACEHOLDER_VALUE_REJECTED",
                $"{field} must contain a concrete observed value, not a placeholder.");
        }
    }

    private static void RequireHumanText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length < 8 || value.Length > 2048 ||
            value.Contains('<', StringComparison.Ordinal) ||
            value.Contains('>', StringComparison.Ordinal) ||
            PlaceholderContentPattern.IsMatch(value))
        {
            throw Invalid(
                "ACCEPTANCE_HUMAN_TEXT_INVALID",
                $"{field} must contain 8..2048 characters of reviewable text.");
        }
    }

    private static void RequireSha256(string value, string field)
    {
        if (!Sha256Pattern.IsMatch(value))
        {
            throw Invalid(
                "ACCEPTANCE_SHA256_INVALID",
                $"{field} must be 'sha256:' plus 64 lowercase hex characters.");
        }
    }

    private static void RequireNonZeroSha256(string value, string field)
    {
        RequireSha256(value, field);
        if (IsAllZeroDigest(value))
        {
            throw Invalid(
                "ACCEPTANCE_SHA256_PLACEHOLDER",
                $"{field} cannot use the all-zero placeholder digest.");
        }
    }

    private static void RequireCommit(string value, string field)
    {
        if (!CommitPattern.IsMatch(value) || value.All(character => character == '0'))
        {
            throw Invalid(
                "ACCEPTANCE_COMMIT_INVALID",
                $"{field} must be a non-zero full 40-character lowercase Git commit.");
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string field)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Invalid(
                "ACCEPTANCE_TIMESTAMP_NOT_UTC",
                $"{field} must use UTC (Z or +00:00).");
        }
        return value;
    }

    private static void RequireUnique(IEnumerable<string> values, string field)
    {
        var array = values.ToArray();
        if (array.Distinct(StringComparer.Ordinal).Count() != array.Length)
        {
            throw Invalid(
                "ACCEPTANCE_CATALOG_DUPLICATE_VALUE",
                $"{field} contains duplicate values.");
        }
    }

    private static bool IsAllZeroDigest(string value)
    {
        var suffix = value[(value.LastIndexOf(':') + 1)..];
        return suffix.Length > 0 && suffix.All(character => character == '0');
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static EvidenceValidationException Invalid(string code, string message) => new(code, message);

}

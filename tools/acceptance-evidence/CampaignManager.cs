using System.Text.Json;

namespace PalControl.AcceptanceEvidence;

internal static class CampaignManager
{
    private const string FinalP0Gate = "p0-11-independent-release-review";

    public static CampaignCreationSummary Create(
        string requestedRoot,
        LoadedCatalog catalog,
        LoadedSchema schema,
        DateTimeOffset now)
    {
        var outputRoot = Path.GetFullPath(requestedRoot);
        var parent = Path.GetDirectoryName(outputRoot)
            ?? throw new ArgumentException("--output must have a parent directory.");
        var leaf = Path.GetFileName(outputRoot);
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
        {
            throw new ArgumentException("--output must name a new campaign directory.");
        }
        EnsureOutsideRepository(outputRoot);
        EvidenceVerifier.RejectReparsePath(
            parent,
            "ACCEPTANCE_CAMPAIGN_REPARSE_PATH");
        if (File.Exists(outputRoot) || Directory.Exists(outputRoot))
        {
            throw new IOException($"Campaign output already exists: {outputRoot}");
        }

        Directory.CreateDirectory(parent);
        EvidenceVerifier.RejectReparsePath(
            parent,
            "ACCEPTANCE_CAMPAIGN_REPARSE_PATH");
        var stagingRoot = Path.Combine(
            parent,
            $".pal-control-campaign-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingRoot);
        EvidenceVerifier.RejectReparsePath(
            stagingRoot,
            "ACCEPTANCE_CAMPAIGN_REPARSE_PATH");
        try
        {
            var gates = new List<CampaignIndexGate>();
            foreach (var gate in catalog.Document.Gates)
            {
                var relativeManifest = ManifestRelativePath(gate.Id);
                var manifestPath = Path.Combine(
                    stagingRoot,
                    relativeManifest.Replace('/', Path.DirectorySeparatorChar));
                var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
                Directory.CreateDirectory(manifestDirectory);
                Directory.CreateDirectory(Path.Combine(manifestDirectory, "evidence"));

                var manifest = TemplateFactory.Create(gate, catalog, schema, now);
                if (gate.Id == FinalP0Gate)
                {
                    manifest.RelatedEvidence.Clear();
                    foreach (var relatedGate in gate.RequiredRelatedGates)
                    {
                        manifest.RelatedEvidence.Add(new RelatedEvidence
                        {
                            GateId = relatedGate,
                            ManifestPath = ManifestRelativePath(relatedGate),
                            Sha256 = "sha256:" + new string('0', 64)
                        });
                    }
                }
                WriteJsonCreateNew(manifestPath, manifest);
                gates.Add(new CampaignIndexGate(
                    gate.Id,
                    gate.Priority,
                    gate.Title,
                    relativeManifest,
                    gate.TodoReferences,
                    gate.MinimumDurationSeconds,
                    gate.AllowedEnvironmentKinds,
                    gate.RequiredRelatedGates));
            }

            var index = new CampaignIndex(
                SchemaVersion: 1,
                CreatedAt: now.ToUniversalTime(),
                CatalogVersion: catalog.Document.CatalogVersion,
                CatalogSha256: catalog.Sha256,
                ManifestSchemaSha256: schema.Sha256,
                GateCount: gates.Count,
                Warning:
                    "Template-only worklist. No gate is complete until live artifacts, external trust pin, two distinct signatures and verify-campaign all pass.",
                Gates: gates);
            WriteJsonCreateNew(Path.Combine(stagingRoot, "campaign-index.json"), index);

            Directory.Move(stagingRoot, outputRoot);
            return new CampaignCreationSummary(
                Created: true,
                Output: outputRoot,
                GateCount: gates.Count,
                P0Count: gates.Count(gate => gate.Priority == "P0"),
                P1Count: gates.Count(gate => gate.Priority == "P1"),
                P2Count: gates.Count(gate => gate.Priority == "P2"),
                Warning: index.Warning);
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
            {
                try
                {
                    EvidenceVerifier.RejectReparsePath(
                        stagingRoot,
                        "ACCEPTANCE_CAMPAIGN_REPARSE_PATH");
                    Directory.Delete(stagingRoot, recursive: true);
                }
                catch
                {
                    // Preserve the original failure and leave an unsafe or
                    // concurrently replaced staging path for manual review.
                }
            }
            throw;
        }
    }

    public static CampaignInspectionSummary Inspect(
        string requestedRoot,
        LoadedCatalog catalog)
    {
        var root = ExistingCampaignRoot(requestedRoot);
        var results = new List<CampaignInspectionGate>();
        foreach (var gate in catalog.Document.Gates)
        {
            var relativePath = ManifestRelativePath(gate.Id);
            var manifestPath = Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(manifestPath))
            {
                results.Add(new CampaignInspectionGate(
                    gate.Id, gate.Priority, relativePath, "missing", false));
                continue;
            }
            try
            {
                var manifest = EvidenceVerifier.ReadManifestForCombination(manifestPath);
                var status = manifest.GateId != gate.Id
                    ? "gate-id-mismatch"
                    : manifest.EvidenceMode != "live" || manifest.Environment.IsSynthetic
                        ? "template"
                        : manifest.Conclusion.Result != "pass" ||
                          manifest.Review.Decision != "approved"
                            ? "live-pending"
                            : "live-awaiting-cryptographic-verification";
                results.Add(new CampaignInspectionGate(
                    gate.Id, gate.Priority, relativePath, status, false));
            }
            catch (Exception exception) when (
                exception is EvidenceValidationException or JsonException or IOException or
                    UnauthorizedAccessException)
            {
                results.Add(new CampaignInspectionGate(
                    gate.Id,
                    gate.Priority,
                    relativePath,
                    "invalid-document",
                    false,
                    exception is EvidenceValidationException validation
                        ? validation.Code
                        : "ACCEPTANCE_CAMPAIGN_DOCUMENT_ERROR"));
            }
        }
        return new CampaignInspectionSummary(
            Root: root,
            GateCount: results.Count,
            VerifiedGateCount: 0,
            Complete: false,
            Warning:
                "Inspection never verifies signatures or evidence. Use verify-campaign with an externally pinned trust store.",
            Gates: results);
    }

    public static CampaignVerificationSummary Verify(
        string requestedRoot,
        LoadedCatalog catalog,
        LoadedSchema schema,
        LoadedTrustStore trustStore,
        DateTimeOffset now)
    {
        var root = ExistingCampaignRoot(requestedRoot);
        var results = new List<CampaignVerificationGate>();
        foreach (var gate in catalog.Document.Gates)
        {
            var relativePath = ManifestRelativePath(gate.Id);
            var manifestPath = Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(manifestPath))
            {
                results.Add(new CampaignVerificationGate(
                    gate.Id,
                    gate.Priority,
                    relativePath,
                    false,
                    null,
                    "ACCEPTANCE_CAMPAIGN_MANIFEST_MISSING"));
                continue;
            }
            try
            {
                var summary = EvidenceVerifier.Verify(
                    manifestPath,
                    catalog,
                    schema,
                    trustStore,
                    now);
                if (summary.GateId != gate.Id)
                {
                    throw new EvidenceValidationException(
                        "ACCEPTANCE_CAMPAIGN_GATE_MISMATCH",
                        $"Expected gate '{gate.Id}', found '{summary.GateId}'.");
                }
                results.Add(new CampaignVerificationGate(
                    gate.Id,
                    gate.Priority,
                    relativePath,
                    true,
                    summary.Conclusion,
                    null));
            }
            catch (Exception exception) when (
                exception is EvidenceValidationException or JsonException or IOException or
                    UnauthorizedAccessException or FormatException)
            {
                results.Add(new CampaignVerificationGate(
                    gate.Id,
                    gate.Priority,
                    relativePath,
                    false,
                    null,
                    exception is EvidenceValidationException validation
                        ? validation.Code
                        : "ACCEPTANCE_CAMPAIGN_VERIFICATION_ERROR"));
            }
        }
        var verified = results.Count(result => result.Valid);
        return new CampaignVerificationSummary(
            Root: root,
            GateCount: results.Count,
            VerifiedGateCount: verified,
            Complete: verified == results.Count,
            Gates: results);
    }

    public static string ManifestRelativePath(string gateId) =>
        gateId == FinalP0Gate
            ? "p0-release-review.json"
            : $"{gateId}/manifest.json";

    private static string ExistingCampaignRoot(string requestedRoot)
    {
        var root = Path.GetFullPath(requestedRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Campaign root does not exist: {root}");
        }
        EvidenceVerifier.RejectReparsePath(
            root,
            "ACCEPTANCE_CAMPAIGN_REPARSE_PATH");
        return root;
    }

    private static void WriteJsonCreateNew<T>(string path, T value)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        JsonSerializer.Serialize(stream, value, EvidenceVerifier.SerializerOptions);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureOutsideRepository(string outputRoot)
    {
        DirectoryInfo? repository = null;
        foreach (var start in new[]
                 {
                     outputRoot,
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var candidate = new DirectoryInfo(start);
                 candidate is not null;
                 candidate = candidate.Parent)
            {
                if (Directory.Exists(Path.Combine(candidate.FullName, ".git")) &&
                    File.Exists(Path.Combine(candidate.FullName, "TODO.md")) &&
                    Directory.Exists(Path.Combine(
                        candidate.FullName,
                        "tools",
                        "acceptance-evidence")))
                {
                    repository = candidate;
                    break;
                }
            }
            if (repository is not null)
            {
                break;
            }
        }
        if (repository is null)
        {
            return;
        }
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = repository.FullName.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (outputRoot.Equals(repository.FullName, comparison) ||
            outputRoot.StartsWith(prefix, comparison))
        {
            throw new ArgumentException(
                "Acceptance campaigns may contain operational evidence and must be created outside the public repository.");
        }
    }
}

internal sealed record CampaignCreationSummary(
    bool Created,
    string Output,
    int GateCount,
    int P0Count,
    int P1Count,
    int P2Count,
    string Warning);

internal sealed record CampaignIndex(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string CatalogVersion,
    string CatalogSha256,
    string ManifestSchemaSha256,
    int GateCount,
    string Warning,
    IReadOnlyList<CampaignIndexGate> Gates);

internal sealed record CampaignIndexGate(
    string Id,
    string Priority,
    string Title,
    string ManifestPath,
    IReadOnlyList<string> TodoReferences,
    long MinimumDurationSeconds,
    IReadOnlyList<string> AllowedEnvironmentKinds,
    IReadOnlyList<string> RequiredRelatedGates);

internal sealed record CampaignInspectionSummary(
    string Root,
    int GateCount,
    int VerifiedGateCount,
    bool Complete,
    string Warning,
    IReadOnlyList<CampaignInspectionGate> Gates);

internal sealed record CampaignInspectionGate(
    string Id,
    string Priority,
    string ManifestPath,
    string Status,
    bool Verified,
    string? ErrorCode = null);

internal sealed record CampaignVerificationSummary(
    string Root,
    int GateCount,
    int VerifiedGateCount,
    bool Complete,
    IReadOnlyList<CampaignVerificationGate> Gates);

internal sealed record CampaignVerificationGate(
    string Id,
    string Priority,
    string ManifestPath,
    bool Valid,
    string? Conclusion,
    string? ErrorCode);

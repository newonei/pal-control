using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PalControl.ControlApi.Infrastructure;

[JsonConverter(typeof(JsonStringEnumConverter<CompatibilityStatus>))]
public enum CompatibilityStatus
{
    Stable,
    Experimental,
    Quarantined
}

public sealed record CompatibilityEvidence(
    string Kind,
    string Source,
    DateTimeOffset ObservedAt,
    string Summary,
    string? ArtifactSha256 = null);

public sealed record CompatibilityCombination(
    string Id,
    string GameVersion,
    string SteamBuild,
    string PalDefenderVersion,
    string Ue4ssCommit,
    string NativeProtocolVersion,
    string NativeModVersion,
    string BridgeAvailability,
    IReadOnlyList<string> Capabilities,
    CompatibilityStatus Status,
    DateTimeOffset VerifiedAt,
    IReadOnlyList<CompatibilityEvidence> Evidence,
    string Notes);

public sealed record CompatibilityMatrixDocument(
    int SchemaVersion,
    string MatrixVersion,
    DateTimeOffset GeneratedAt,
    string CanonicalSha256,
    IReadOnlyList<CompatibilityCombination> Combinations);

public sealed record CompatibilityMatrixSnapshot(
    CompatibilityMatrixDocument Document,
    string CanonicalSha256,
    string SourcePath)
{
    public CompatibilityCombination RequireCombination(string combinationId) =>
        Document.Combinations.SingleOrDefault(combination => string.Equals(
            combination.Id,
            combinationId,
            StringComparison.Ordinal))
        ?? throw new CompatibilityMatrixException(
            "COMPATIBILITY_COMBINATION_UNKNOWN",
            $"Compatibility combination '{combinationId}' is not registered.");
}

public sealed class CompatibilityMatrixException : Exception
{
    public CompatibilityMatrixException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Loads a versioned, self-hashing compatibility matrix. The canonical digest
/// excludes only the CanonicalSha256 property itself; every other byte of
/// semantic JSON participates after object-key ordering and whitespace removal.
/// </summary>
public static partial class CompatibilityMatrixValidator
{
    private const string Unknown = "unknown";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<CompatibilityStatus>(JsonNamingPolicy.CamelCase) }
    };

    public static CompatibilityMatrixSnapshot Load(
        string matrixPath,
        string? expectedCanonicalSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matrixPath);
        var fullPath = Path.GetFullPath(matrixPath);
        if (!File.Exists(fullPath))
        {
            throw new CompatibilityMatrixException(
                "COMPATIBILITY_MATRIX_MISSING",
                $"Compatibility matrix does not exist: {fullPath}");
        }

        string json;
        try
        {
            json = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CompatibilityMatrixException(
                "COMPATIBILITY_MATRIX_UNREADABLE",
                $"Compatibility matrix could not be read: {exception.Message}");
        }

        return Parse(json, fullPath, expectedCanonicalSha256);
    }

    public static CompatibilityMatrixSnapshot Parse(
        string json,
        string sourceName = "memory",
        string? expectedCanonicalSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonObject root;
        CompatibilityMatrixDocument document;
        try
        {
            root = JsonNode.Parse(
                json,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                })?.AsObject()
                ?? throw new JsonException("The matrix root must be an object.");
            document = root.Deserialize<CompatibilityMatrixDocument>(JsonOptions)
                ?? throw new JsonException("The matrix document is empty.");
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new CompatibilityMatrixException(
                "COMPATIBILITY_MATRIX_INVALID_JSON",
                $"Compatibility matrix JSON is invalid: {exception.Message}");
        }

        ValidateDocument(document);
        var computed = ComputeCanonicalSha256(root);
        if (!FixedTimeHexEquals(document.CanonicalSha256, computed))
        {
            throw new CompatibilityMatrixException(
                "COMPATIBILITY_MATRIX_HASH_MISMATCH",
                $"Compatibility matrix canonical SHA-256 mismatch; computed {computed}.");
        }
        if (!string.IsNullOrWhiteSpace(expectedCanonicalSha256) &&
            !FixedTimeHexEquals(expectedCanonicalSha256, computed))
        {
            throw new CompatibilityMatrixException(
                "COMPATIBILITY_MATRIX_PIN_MISMATCH",
                "Compatibility matrix does not match the configured canonical SHA-256 pin.");
        }

        return new CompatibilityMatrixSnapshot(document, computed, sourceName);
    }

    public static string ComputeCanonicalSha256(string json)
    {
        try
        {
            var root = JsonNode.Parse(
                json,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                })?.AsObject()
                ?? throw new JsonException("Compatibility matrix root must be an object.");
            return ComputeCanonicalSha256(root);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new CompatibilityMatrixException(
                "COMPATIBILITY_MATRIX_INVALID_JSON",
                $"Compatibility matrix JSON is invalid: {exception.Message}");
        }
    }

    public static void RequireProductionStable(
        CompatibilityMatrixSnapshot snapshot,
        string combinationId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var combination = snapshot.RequireCombination(combinationId);
        if (combination.Status != CompatibilityStatus.Stable)
        {
            throw new CompatibilityMatrixException(
                "COMPATIBILITY_COMBINATION_NOT_STABLE",
                $"Combination '{combination.Id}' is {ToWireStatus(combination.Status)} and cannot be admitted as production stable.");
        }
        ValidateStableCombination(combination);
    }

    public static string ToWireStatus(CompatibilityStatus status) => status switch
    {
        CompatibilityStatus.Stable => "stable",
        CompatibilityStatus.Experimental => "experimental",
        CompatibilityStatus.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static string ComputeCanonicalSha256(JsonObject root)
    {
        var clone = root.DeepClone().AsObject();
        if (!clone.Remove("canonicalSha256"))
        {
            throw new CompatibilityMatrixException(
                "COMPATIBILITY_MATRIX_HASH_MISSING",
                "Compatibility matrix must contain canonicalSha256.");
        }
        var canonical = Canonicalize(clone).ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static JsonNode Canonicalize(JsonNode node) => node switch
    {
        JsonObject value => new JsonObject(value
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(
                property.Key,
                property.Value is null ? null : Canonicalize(property.Value)))),
        JsonArray value => new JsonArray(value
            .Select(item => item is null ? null : Canonicalize(item))
            .ToArray()),
        _ => node.DeepClone()
    };

    private static void ValidateDocument(CompatibilityMatrixDocument document)
    {
        if (document.SchemaVersion != 1)
        {
            throw Invalid("schemaVersion must be exactly 1.");
        }
        if (document.MatrixVersion is null ||
            !MatrixVersionPattern().IsMatch(document.MatrixVersion))
        {
            throw Invalid("matrixVersion must be an exact semantic version.");
        }
        if (document.CanonicalSha256 is null ||
            !Sha256Pattern().IsMatch(document.CanonicalSha256))
        {
            throw Invalid("canonicalSha256 must be 64 lowercase hexadecimal characters.");
        }
        if (document.GeneratedAt == default || document.GeneratedAt.Offset != TimeSpan.Zero ||
            document.GeneratedAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw Invalid("generatedAt must be a non-default, non-future UTC timestamp.");
        }
        if (document.Combinations is null || document.Combinations.Count == 0 ||
            document.Combinations.Count > 256)
        {
            throw Invalid("combinations must contain 1 to 256 entries.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var tuples = new HashSet<string>(StringComparer.Ordinal);
        string? previousId = null;
        foreach (var combination in document.Combinations)
        {
            if (combination is null)
            {
                throw Invalid("combinations cannot contain null entries.");
            }
            ValidateCombination(combination);
            if (!ids.Add(combination.Id))
            {
                throw Invalid($"Duplicate combination id '{combination.Id}'.");
            }
            if (previousId is not null &&
                string.CompareOrdinal(previousId, combination.Id) >= 0)
            {
                throw Invalid("combinations must be sorted by id with ordinal comparison.");
            }
            previousId = combination.Id;

            var tuple = string.Join('\n',
                combination.GameVersion,
                combination.SteamBuild,
                combination.PalDefenderVersion,
                combination.Ue4ssCommit,
                combination.NativeProtocolVersion,
                combination.NativeModVersion,
                combination.BridgeAvailability);
            if (!tuples.Add(tuple))
            {
                throw Invalid($"Combination '{combination.Id}' duplicates another version tuple.");
            }
            if (combination.Status == CompatibilityStatus.Stable)
            {
                ValidateStableCombination(combination);
            }
        }
    }

    private static void ValidateCombination(CompatibilityCombination combination)
    {
        if (combination.Id is null ||
            !CombinationIdPattern().IsMatch(combination.Id))
        {
            throw Invalid("Each combination id must be a lowercase, versioned safe identifier.");
        }
        if (combination.GameVersion is null ||
            !GameVersionPattern().IsMatch(combination.GameVersion))
        {
            throw Invalid($"Combination '{combination.Id}' has an imprecise gameVersion.");
        }
        if (combination.SteamBuild is null ||
            !SteamBuildPattern().IsMatch(combination.SteamBuild))
        {
            throw Invalid($"Combination '{combination.Id}' has an imprecise steamBuild.");
        }
        if (combination.PalDefenderVersion is null ||
            !ComponentVersionPattern().IsMatch(combination.PalDefenderVersion))
        {
            throw Invalid($"Combination '{combination.Id}' has an imprecise PalDefender version.");
        }
        if (combination.Ue4ssCommit is null ||
            !CommitPattern().IsMatch(combination.Ue4ssCommit))
        {
            throw Invalid($"Combination '{combination.Id}' must pin a full UE4SS commit or use unknown.");
        }
        if (combination.NativeProtocolVersion is null ||
            combination.NativeModVersion is null ||
            !ProtocolPattern().IsMatch(combination.NativeProtocolVersion) ||
            !ComponentVersionPattern().IsMatch(combination.NativeModVersion))
        {
            throw Invalid($"Combination '{combination.Id}' has an imprecise Native protocol or mod version.");
        }
        if (combination.BridgeAvailability is not ("available" or "unavailable" or "unknown"))
        {
            throw Invalid($"Combination '{combination.Id}' has an invalid bridgeAvailability.");
        }
        if (combination.VerifiedAt == default || combination.VerifiedAt.Offset != TimeSpan.Zero ||
            combination.VerifiedAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw Invalid($"Combination '{combination.Id}' verifiedAt must be a valid UTC observation time.");
        }
        if (combination.Capabilities is null || combination.Capabilities.Count == 0 ||
            combination.Capabilities.Count > 64 ||
            combination.Capabilities.Any(capability =>
                capability is null || !CapabilityPattern().IsMatch(capability)) ||
            !IsSortedUnique(combination.Capabilities))
        {
            throw Invalid($"Combination '{combination.Id}' capabilities must be 1 to 64 sorted unique safe names.");
        }
        if (combination.Evidence is null || combination.Evidence.Count == 0 ||
            combination.Evidence.Count > 32)
        {
            throw Invalid($"Combination '{combination.Id}' must have 1 to 32 evidence records.");
        }
        foreach (var evidence in combination.Evidence)
        {
            if (evidence is null || evidence.Kind is null ||
                !EvidenceKindPattern().IsMatch(evidence.Kind) ||
                !IsSafeEvidenceText(evidence.Source, 256) ||
                !IsSafeEvidenceText(evidence.Summary, 512) ||
                evidence.ObservedAt == default || evidence.ObservedAt.Offset != TimeSpan.Zero ||
                evidence.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
                evidence.ArtifactSha256 is not null && !Sha256Pattern().IsMatch(evidence.ArtifactSha256))
            {
                throw Invalid($"Combination '{combination.Id}' contains malformed evidence.");
            }
            if (ContainsSensitiveLabel(evidence.Source) || ContainsSensitiveLabel(evidence.Summary))
            {
                throw Invalid($"Combination '{combination.Id}' evidence appears to contain secret-bearing labels.");
            }
        }
        if (!IsSafeEvidenceText(combination.Notes, 1_024) || ContainsSensitiveLabel(combination.Notes))
        {
            throw Invalid($"Combination '{combination.Id}' notes are missing, oversized, or secret-like.");
        }
    }

    private static void ValidateStableCombination(CompatibilityCombination combination)
    {
        if (new[]
            {
                combination.SteamBuild,
                combination.PalDefenderVersion,
                combination.Ue4ssCommit,
                combination.NativeProtocolVersion,
                combination.NativeModVersion,
                combination.BridgeAvailability
            }.Any(value => string.Equals(value, Unknown, StringComparison.Ordinal)) ||
            combination.PalDefenderVersion.Contains('-', StringComparison.Ordinal) ||
            combination.NativeModVersion.Contains('-', StringComparison.Ordinal) ||
            combination.BridgeAvailability != "available" ||
            combination.Capabilities.Any(capability =>
                capability.Contains("experimental", StringComparison.OrdinalIgnoreCase)))
        {
            throw new CompatibilityMatrixException(
                "COMPATIBILITY_STABLE_REQUIREMENTS_NOT_MET",
                $"Stable combination '{combination.Id}' contains unknown, unavailable, development, or experimental fields.");
        }
    }

    private static bool IsSortedUnique(IReadOnlyList<string> values)
    {
        for (var index = 1; index < values.Count; index++)
        {
            if (string.CompareOrdinal(values[index - 1], values[index]) >= 0)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSafeEvidenceText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool ContainsSensitiveLabel(string value) =>
        SensitiveLabelPattern().IsMatch(value);

    private static bool FixedTimeHexEquals(string? supplied, string expected)
    {
        if (supplied is null || supplied.Length != expected.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(supplied),
            Encoding.ASCII.GetBytes(expected));
    }

    private static CompatibilityMatrixException Invalid(string message) =>
        new("COMPATIBILITY_MATRIX_INVALID", message);

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex MatrixVersionPattern();

    [GeneratedRegex("^[a-z][a-z0-9.-]{2,95}$")]
    private static partial Regex CombinationIdPattern();

    [GeneratedRegex("^v[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex GameVersionPattern();

    [GeneratedRegex("^(unknown|[0-9]{6,12})$")]
    private static partial Regex SteamBuildPattern();

    [GeneratedRegex("^(unknown|[0-9]+\\.[0-9]+(?:\\.[0-9]+)*(?:-[0-9A-Za-z.-]+)?)$")]
    private static partial Regex ComponentVersionPattern();

    [GeneratedRegex("^(unknown|[a-f0-9]{40})$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^(unknown|[0-9]+\\.[0-9]+)$")]
    private static partial Regex ProtocolPattern();

    [GeneratedRegex("^[a-z][a-z0-9.-]{1,95}$")]
    private static partial Regex CapabilityPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex EvidenceKindPattern();

    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("(?i)(password|passwd|secret|api[-_ ]?key|authorization|cookie|token)\\s*[:=]")]
    private static partial Regex SensitiveLabelPattern();
}

public sealed class CompatibilityMatrixStore
{
    public CompatibilityMatrixStore(CompatibilityMatrixSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public CompatibilityMatrixSnapshot Snapshot { get; }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PalControl.ControlApi.Infrastructure;

public sealed record CommandSideStateArchiveFile(
    string RelativePath,
    string Channel,
    string Authority,
    bool Present,
    long Bytes,
    string? Sha256);

public sealed record CommandSideStateArchiveManifest(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    string ArchiveMode,
    string ActiveLogMutationPolicy,
    int RetentionDays,
    int MinimumRetainedArchives,
    IReadOnlyList<CommandSideStateArchiveFile> Files,
    string ContentHash);

/// <summary>
/// Defines the complete registry and immutable archive evidence for the
/// append-only, non-economic JSONL side state. Active logs are never compacted,
/// truncated, or deleted by this service. Their only supported archive is the
/// verified command-state copy inside an economy continuity snapshot; retention
/// is therefore planned for the outer snapshot as one indivisible bundle.
/// </summary>
public sealed class CommandSideStateArchiveService
{
    public const string ManifestFileName = "command-side-state-archive.json";
    public const string ManifestRole = "command-side-state-archive-manifest";
    public const string ArchiveMode = "economy-continuity-snapshot";
    public const string ActiveLogMutationPolicy = "append-only-no-truncate-or-delete";

    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly ChannelDefinition[] Channels =
    [
        new("announcement-events.jsonl", "announcement-state", "non-economic-authoritative"),
        new("command-audit.jsonl", "announcement-delivery", "non-economic-authoritative"),
        new("in-game-notification-events.jsonl", "in-game-notification-state", "non-economic-authoritative"),
        new("in-game-notification-command-audit.jsonl", "in-game-notification-delivery", "non-economic-authoritative"),
        new("save-command-audit.jsonl", "save-command-delivery", "non-economic-authoritative"),
        new("paldefender-command-audit.jsonl", "paldefender-legacy-import", "legacy-migration-evidence"),
        new("extraction-commerce-events.jsonl", "economy-legacy-import", "legacy-migration-evidence")
    ];

    public CommandSideStateArchiveManifest CreateManifest(
        string archivedCommandStateRoot,
        DateTimeOffset capturedAt,
        int retentionDays,
        int minimumRetainedArchives)
    {
        ValidatePolicy(retentionDays, minimumRetainedArchives);
        var root = Path.GetFullPath(archivedCommandStateRoot);
        Directory.CreateDirectory(root);
        RejectUnregisteredJsonLines(root);

        var files = Channels
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select(item => Capture(root, item))
            .ToArray();
        var contentHash = ComputeContentHash(
            capturedAt,
            retentionDays,
            minimumRetainedArchives,
            files);
        var manifest = new CommandSideStateArchiveManifest(
            SchemaVersion,
            capturedAt,
            ArchiveMode,
            ActiveLogMutationPolicy,
            retentionDays,
            minimumRetainedArchives,
            files,
            contentHash);
        WriteManifestDurably(root, manifest);
        Verify(root);
        return manifest;
    }

    public CommandSideStateArchiveManifest Verify(string archivedCommandStateRoot)
    {
        var root = Path.GetFullPath(archivedCommandStateRoot);
        var manifestPath = Path.Combine(root, ManifestFileName);
        CommandSideStateArchiveManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CommandSideStateArchiveManifest>(
                File.ReadAllBytes(manifestPath),
                JsonOptions) ?? throw new InvalidDataException(
                "The command side-state archive manifest is empty.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                "The command side-state archive manifest cannot be read.",
                exception);
        }

        if (manifest.SchemaVersion != SchemaVersion ||
            !string.Equals(manifest.ArchiveMode, ArchiveMode, StringComparison.Ordinal) ||
            !string.Equals(
                manifest.ActiveLogMutationPolicy,
                ActiveLogMutationPolicy,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The command side-state archive policy metadata is unsupported.");
        }
        ValidatePolicy(manifest.RetentionDays, manifest.MinimumRetainedArchives);
        RejectUnregisteredJsonLines(root);

        var byPath = manifest.Files
            .GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (byPath.Any(group => group.Count() != 1) || byPath.Length != Channels.Length)
        {
            throw new InvalidDataException(
                "The command side-state archive manifest does not contain the complete channel registry.");
        }

        foreach (var definition in Channels)
        {
            var stored = byPath.SingleOrDefault(group => string.Equals(
                    group.Key,
                    definition.RelativePath,
                    StringComparison.OrdinalIgnoreCase))
                ?.Single() ?? throw new InvalidDataException(
                    $"The command side-state archive is missing channel '{definition.Channel}'.");
            if (!string.Equals(stored.RelativePath, definition.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(stored.Channel, definition.Channel, StringComparison.Ordinal) ||
                !string.Equals(stored.Authority, definition.Authority, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The command side-state archive channel '{definition.Channel}' was reclassified.");
            }

            var path = Path.Combine(root, definition.RelativePath);
            if (!stored.Present)
            {
                if (stored.Bytes != 0 || stored.Sha256 is not null || File.Exists(path))
                {
                    throw new InvalidDataException(
                        $"The absent command side-state channel '{definition.Channel}' has file evidence.");
                }
                continue;
            }

            ValidateJsonLines(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != stored.Bytes ||
                !string.Equals(HashFile(path), stored.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The command side-state channel '{definition.Channel}' failed SHA-256 verification.");
            }
        }

        var expectedHash = ComputeContentHash(
            manifest.CapturedAt,
            manifest.RetentionDays,
            manifest.MinimumRetainedArchives,
            manifest.Files);
        if (!string.Equals(expectedHash, manifest.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The command side-state archive policy manifest hash is invalid.");
        }
        return manifest;
    }

    public static string ClassifyJsonLines(string fileName)
    {
        var definition = Channels.SingleOrDefault(item => string.Equals(
            item.RelativePath,
            fileName,
            StringComparison.OrdinalIgnoreCase));
        return definition is null
            ? "unregistered-jsonl"
            : $"jsonl-{definition.Channel}";
    }

    private static CommandSideStateArchiveFile Capture(
        string root,
        ChannelDefinition definition)
    {
        var path = Path.Combine(root, definition.RelativePath);
        if (!File.Exists(path))
        {
            return new CommandSideStateArchiveFile(
                definition.RelativePath,
                definition.Channel,
                definition.Authority,
                false,
                0,
                null);
        }
        ValidateJsonLines(path);
        var info = new FileInfo(path);
        return new CommandSideStateArchiveFile(
            definition.RelativePath,
            definition.Channel,
            definition.Authority,
            true,
            info.Length,
            HashFile(path));
    }

    private static void RejectUnregisteredJsonLines(string root)
    {
        var registered = new HashSet<string>(
            Channels.Select(item => item.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains('/', StringComparison.Ordinal) ||
                !registered.Contains(relative))
            {
                throw new InvalidDataException(
                    $"Unregistered command side-state JSONL '{relative}' cannot be archived.");
            }
        }
    }

    private static void ValidateJsonLines(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Command side-state archives cannot read missing files or reparse points.");
        }
        if (info.Length == 0)
        {
            return;
        }
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   1,
                   FileOptions.SequentialScan))
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != (byte)'\n')
            {
                throw new InvalidDataException(
                    "A command side-state JSONL has an incomplete final line.");
            }
        }

        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException(
                    $"Command side-state JSONL line {lineNumber} is empty.");
            }
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"Command side-state JSONL line {lineNumber} is not an object.");
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Command side-state JSONL line {lineNumber} is invalid JSON.",
                    exception);
            }
        }
    }

    private static void ValidatePolicy(int retentionDays, int minimumRetainedArchives)
    {
        if (retentionDays is < 7 or > 3650 || minimumRetainedArchives is < 2 or > 520)
        {
            throw new InvalidDataException(
                "Command side-state archives must inherit a 7-3650 day and 2-520 snapshot retention policy.");
        }
    }

    private static string ComputeContentHash(
        DateTimeOffset capturedAt,
        int retentionDays,
        int minimumRetainedArchives,
        IEnumerable<CommandSideStateArchiveFile> files)
    {
        var builder = new StringBuilder()
            .Append("command-side-state-archive-v1\n")
            .Append(capturedAt.ToString("O")).Append('\n')
            .Append(ArchiveMode).Append('\n')
            .Append(ActiveLogMutationPolicy).Append('\n')
            .Append(retentionDays).Append('\n')
            .Append(minimumRetainedArchives).Append('\n');
        foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(file.RelativePath).Append('\n')
                .Append(file.Channel).Append('\n')
                .Append(file.Authority).Append('\n')
                .Append(file.Present ? "present" : "absent").Append('\n')
                .Append(file.Bytes).Append('\n')
                .Append(file.Sha256 ?? string.Empty).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void WriteManifestDurably(
        string root,
        CommandSideStateArchiveManifest manifest)
    {
        var target = Path.Combine(root, ManifestFileName);
        var temporary = Path.Combine(root, $".{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record ChannelDefinition(
        string RelativePath,
        string Channel,
        string Authority);
}

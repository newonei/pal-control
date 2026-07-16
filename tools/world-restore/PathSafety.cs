namespace PalControl.WorldRestore;

internal static class PathSafety
{
    private static readonly HashSet<string> ReservedWindowsNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9"
        },
        StringComparer.OrdinalIgnoreCase);

    public static string FullPath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{description} is required.");
        }
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"{description} must be an absolute path.");
        }
        return Path.GetFullPath(path);
    }

    public static string EnsureLocalVolume(string path, string description)
    {
        var full = FullPath(path, description);
        if (OperatingSystem.IsWindows())
        {
            if (full.StartsWith("\\\\", StringComparison.Ordinal) ||
                full.StartsWith("\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{description} must not use a UNC path.");
            }
            var root = Path.GetPathRoot(full)
                ?? throw new InvalidDataException($"{description} has no volume root.");
            DriveInfo drive;
            try
            {
                drive = new DriveInfo(root);
                if (!drive.IsReady || drive.DriveType is DriveType.Network or DriveType.NoRootDirectory)
                {
                    throw new InvalidDataException($"{description} must be on a ready local volume.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    $"{description} local-volume identity could not be proven.",
                    exception);
            }
        }
        return full;
    }

    public static void EnsureSameVolume(string first, string second, string description)
    {
        var left = Path.GetPathRoot(EnsureLocalVolume(first, description))
            ?? throw new InvalidDataException($"{description} first path has no volume root.");
        var right = Path.GetPathRoot(EnsureLocalVolume(second, description))
            ?? throw new InvalidDataException($"{description} second path has no volume root.");
        if (!string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description} paths must be on the same local volume.");
        }
    }

    public static void EnsureDirectory(string path, bool create)
    {
        var full = FullPath(path, "directory");
        EnsureAncestorChainHasNoReparsePoint(full);
        if (create)
        {
            Directory.CreateDirectory(full);
            EnsureAncestorChainHasNoReparsePoint(full);
        }
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Directory does not exist: {full}");
        }
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Directory is a reparse point: {full}");
        }
    }

    public static void EnsureRegularFile(string path, string description)
    {
        var full = FullPath(path, description);
        EnsureAncestorChainHasNoReparsePoint(full);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"{description} does not exist.", full);
        }
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException($"{description} is not a regular non-reparse file.");
        }
    }

    public static void EnsureAncestorChainHasNoReparsePoint(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidDataException("Path has no filesystem root.");
        var current = root;
        if (File.Exists(current) || Directory.Exists(current))
        {
            EnsureNotReparse(current);
        }
        var relative = Path.GetRelativePath(root, full);
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }
            EnsureNotReparse(current);
        }
    }

    public static void EnsureNoOverlap(string first, string second, string description)
    {
        var left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        if (PathsEqual(left, right) || IsStrictChild(left, right) || IsStrictChild(right, left))
        {
            throw new InvalidDataException($"{description} paths overlap.");
        }
    }

    public static void EnsureStrictChild(string parent, string child, string description)
    {
        if (!IsStrictChild(parent, child))
        {
            throw new InvalidDataException($"{description} escaped its parent directory.");
        }
    }

    public static bool IsStrictChild(string parent, string child)
    {
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var fullChild = Path.GetFullPath(child);
        return fullChild.Length > fullParent.Length &&
               fullChild.StartsWith(
                   fullParent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    public static bool PathsEqual(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        StringComparison.OrdinalIgnoreCase);

    public static string ValidateManifestRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            value.Contains('\\') ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(value))
        {
            throw new InvalidDataException($"Manifest contains unsafe relative path '{value}'.");
        }
        var segments = value.Split('/');
        if (segments.Length == 0)
        {
            throw new InvalidDataException("Manifest contains an empty relative path.");
        }
        foreach (var segment in segments)
        {
            EnsureSafeLeafName(segment, "manifest path segment");
        }
        return string.Join('/', segments);
    }

    public static void EnsureSafeLeafName(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/') || value.Contains('\\') ||
            value.EndsWith(' ') || value.EndsWith('.'))
        {
            throw new InvalidDataException($"Unsafe {description}: '{value}'.");
        }
        var stem = value.Split('.')[0];
        if (ReservedWindowsNames.Contains(stem))
        {
            throw new InvalidDataException($"Reserved Windows name in {description}: '{value}'.");
        }
    }

    public static IReadOnlyList<FileInventoryEntry> Inventory(
        string root,
        bool requireNonEmpty,
        IReadOnlyCollection<string>? expectedDirectories = null)
    {
        var fullRoot = FullPath(root, "inventory root");
        EnsureDirectory(fullRoot, create: false);
        var files = new List<FileInventoryEntry>();
        var actualDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Reparse point rejected inside inventory: {entry.FullName}");
                }
                EnsureStrictChild(fullRoot, entry.FullName, "inventory entry");
                if (entry is DirectoryInfo directory)
                {
                    var relativeDirectory = NormalizeRelativePath(
                        Path.GetRelativePath(fullRoot, directory.FullName));
                    actualDirectories.Add(relativeDirectory);
                    pending.Push(directory.FullName);
                }
                else if (entry is FileInfo file)
                {
                    files.Add(new FileInventoryEntry(
                        NormalizeRelativePath(Path.GetRelativePath(fullRoot, file.FullName)),
                        file.Length,
                        CanonicalJson.Sha256File(file.FullName)));
                }
                else
                {
                    throw new InvalidDataException("Inventory contains an unsupported filesystem entry.");
                }
            }
        }
        if (requireNonEmpty && files.Count == 0)
        {
            throw new InvalidDataException("Inventory is empty.");
        }
        if (expectedDirectories is not null &&
            !actualDirectories.SetEquals(expectedDirectories))
        {
            throw new InvalidDataException("Inventory contains missing or extra directories.");
        }
        return files.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlySet<string> ExpectedDirectories(
        IEnumerable<string> relativeFilePaths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in relativeFilePaths)
        {
            var segments = file.Split('/');
            for (var count = 1; count < segments.Length; count++)
            {
                result.Add(string.Join('/', segments.Take(count)));
            }
        }
        return result;
    }

    public static IReadOnlyList<string> DirectoryInventory(string root)
    {
        var fullRoot = FullPath(root, "directory inventory root");
        EnsureDirectory(fullRoot, create: false);
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Reparse point rejected inside directory inventory: {entry.FullName}");
                }
                EnsureStrictChild(fullRoot, entry.FullName, "directory inventory entry");
                if (entry is DirectoryInfo directory)
                {
                    directories.Add(NormalizeRelativePath(
                        Path.GetRelativePath(fullRoot, directory.FullName)));
                    pending.Push(directory.FullName);
                }
                else if (entry is not FileInfo)
                {
                    throw new InvalidDataException(
                        "Directory inventory contains an unsupported filesystem entry.");
                }
            }
        }
        return directories.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    public static void AssertInventoriesEqual(
        IReadOnlyList<FileInventoryEntry> expected,
        IReadOnlyList<FileInventoryEntry> actual,
        string description)
    {
        if (expected.Count != actual.Count)
        {
            throw new InvalidDataException($"{description} file count does not match.");
        }
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = actual[index];
            if (!string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal) ||
                left.Length != right.Length ||
                !string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{description} differs at '{left.RelativePath}'.");
            }
        }
    }

    public static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static void EnsureNotReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Reparse point rejected in configured path: {path}");
        }
    }
}

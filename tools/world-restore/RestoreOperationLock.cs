using System.Security.Cryptography;
using System.Text;

namespace PalControl.WorldRestore;

internal sealed class RestoreOperationLock : IDisposable
{
    private readonly FileStream _stream;

    private RestoreOperationLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public static string GetPath(string activeWorldDirectory)
    {
        var active = PathSafety.EnsureLocalVolume(activeWorldDirectory, "active world directory");
        var parent = System.IO.Path.GetDirectoryName(active)
            ?? throw new InvalidDataException("Active world directory has no parent.");
        PathSafety.EnsureDirectory(parent, create: false);
        var identity = System.IO.Path.TrimEndingDirectorySeparator(active).ToUpperInvariant();
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24];
        var lockPath = System.IO.Path.Combine(parent, $".palcontrol-world-restore-{suffix}.lock");
        PathSafety.EnsureStrictChild(parent, lockPath, "restore lock file");
        PathSafety.EnsureSameVolume(active, lockPath, "restore lock");
        return lockPath;
    }

    public static RestoreOperationLock Acquire(string activeWorldDirectory, string purpose)
    {
        var path = GetPath(activeWorldDirectory);
        ValidateLockPath(path, requireExistingFile: false);

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another world-restore process holds the exclusive active-world lock.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                "The exclusive active-world lock could not be acquired; restore is refused.",
                exception);
        }

        try
        {
            var bytes = CanonicalJson.Serialize(new RestoreLockOwner(
                1,
                "pal-control-world-restore-lock",
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                purpose,
                PathSafety.FullPath(activeWorldDirectory, "active world directory")));
            stream.SetLength(0);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return new RestoreOperationLock(path, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static RestoreOperationLock AcquireReadOnly(string activeWorldDirectory)
    {
        var path = GetPath(activeWorldDirectory);
        ValidateLockPath(path, requireExistingFile: true);

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException(
                "The planned restore lock file is missing; read-only status is refused.",
                exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new InvalidOperationException(
                "The planned restore lock path is missing; read-only status is refused.",
                exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another world-restore process holds the exclusive active-world lock.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                "The read-only active-world lock could not be acquired; status is refused.",
                exception);
        }

        try
        {
            ValidateLockPath(path, requireExistingFile: true);
            return new RestoreOperationLock(path, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ValidateLockPath(string path, bool requireExistingFile)
    {
        PathSafety.EnsureAncestorChainHasNoReparsePoint(path);
        if (Directory.Exists(path) ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException("Restore lock path is not a regular local file.");
        }
        if (requireExistingFile && !File.Exists(path))
        {
            throw new InvalidOperationException(
                "The planned restore lock file is missing; read-only status is refused.");
        }
    }

    public void Dispose() => _stream.Dispose();

    private sealed record RestoreLockOwner(
        int SchemaVersion,
        string ReportType,
        int ProcessId,
        DateTimeOffset AcquiredAt,
        string Purpose,
        string ActiveWorldDirectory);
}

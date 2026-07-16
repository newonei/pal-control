namespace PalControl.AcceptanceEvidence;

internal sealed class VerificationFileLease : IDisposable
{
    private const int MaximumLeasedFiles = 1024;
    private readonly Dictionary<string, LeaseEntry> _entries = new(PathComparer());
    private bool _disposed;

    public FileSnapshot Acquire(
        string path,
        bool captureSample,
        string? textualArtifactId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fullPath = EvidenceVerifier.ExistingRegularFile(
            path,
            "ACCEPTANCE_FILE_LEASE_ACQUIRE_FAILED");
        if (_entries.TryGetValue(fullPath, out var existing))
        {
            if ((captureSample && existing.Snapshot.Sample.Length == 0) ||
                (textualArtifactId is not null && !existing.TextValidated))
            {
                var refreshed = EvidenceVerifier.ReadLeasedStreamSnapshot(
                    existing.Stream,
                    captureSample,
                    textualArtifactId);
                EnsureUnchanged(existing, refreshed);
                if (captureSample)
                {
                    existing.Snapshot = refreshed;
                }
                existing.TextValidated |= textualArtifactId is not null;
            }
            return existing.Snapshot;
        }
        if (_entries.Count >= MaximumLeasedFiles)
        {
            throw new EvidenceValidationException(
                "ACCEPTANCE_FILE_LEASE_LIMIT_EXCEEDED",
                $"Verification cannot lease more than {MaximumLeasedFiles} files.");
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            _ = EvidenceVerifier.ExistingRegularFile(
                fullPath,
                "ACCEPTANCE_FILE_LEASE_ACQUIRE_FAILED");
            var snapshot = EvidenceVerifier.ReadLeasedStreamSnapshot(
                stream,
                captureSample,
                textualArtifactId);
            _entries.Add(
                fullPath,
                new LeaseEntry(
                    fullPath,
                    stream,
                    snapshot,
                    textualArtifactId is not null));
            stream = null;
            return snapshot;
        }
        catch (EvidenceValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new EvidenceValidationException(
                "ACCEPTANCE_FILE_LEASE_ACQUIRE_FAILED",
                $"Unable to acquire a deny-write verification lease for '{fullPath}': {exception.Message}");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public byte[] ReadAllBytes(string path, int maximumBytes, string code)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fullPath = Path.GetFullPath(path);
        if (!_entries.TryGetValue(fullPath, out var entry))
        {
            throw new EvidenceValidationException(
                code,
                $"File '{fullPath}' was not acquired by the verification lease.");
        }
        if (entry.Snapshot.SizeBytes < 0 || entry.Snapshot.SizeBytes > maximumBytes)
        {
            throw new EvidenceValidationException(
                code,
                $"Leased file '{fullPath}' exceeds its {maximumBytes}-byte parser limit.");
        }
        var bytes = new byte[checked((int)entry.Snapshot.SizeBytes)];
        entry.Stream.Position = 0;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = entry.Stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EvidenceValidationException(
                    code,
                    $"Leased file '{fullPath}' was truncated during parsing.");
            }
            offset += read;
        }
        if (entry.Stream.ReadByte() != -1)
        {
            throw new EvidenceValidationException(
                code,
                $"Leased file '{fullPath}' grew during parsing.");
        }
        entry.Stream.Position = 0;
        return bytes;
    }

    public FileSnapshot Rehash(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fullPath = Path.GetFullPath(path);
        if (!_entries.TryGetValue(fullPath, out var entry))
        {
            throw new EvidenceValidationException(
                "ACCEPTANCE_FILE_LEASE_MISSING",
                $"File '{fullPath}' was not acquired by the verification lease.");
        }
        var observed = EvidenceVerifier.ReadLeasedStreamSnapshot(
            entry.Stream,
            captureSample: false);
        EnsureUnchanged(entry, observed);
        return observed;
    }

    public void ValidateAllPathsAndHandles()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var entry in _entries.Values.OrderBy(item => item.Path, PathComparer()))
        {
            var handleSnapshot = EvidenceVerifier.ReadLeasedStreamSnapshot(
                entry.Stream,
                captureSample: false);
            EnsureUnchanged(entry, handleSnapshot);

            var path = EvidenceVerifier.ExistingRegularFile(
                entry.Path,
                "ACCEPTANCE_FILE_LEASE_PATH_CHANGED");
            FileSnapshot pathSnapshot;
            try
            {
                using var pathStream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan);
                pathSnapshot = EvidenceVerifier.ReadLeasedStreamSnapshot(
                    pathStream,
                    captureSample: false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new EvidenceValidationException(
                    "ACCEPTANCE_FILE_LEASE_PATH_CHANGED",
                    $"Leased path '{entry.Path}' could not be reopened: {exception.Message}");
            }
            EnsureUnchanged(entry, pathSnapshot);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var entry in _entries.Values.Reverse())
        {
            entry.Stream.Dispose();
        }
        _entries.Clear();
    }

    private static void EnsureUnchanged(LeaseEntry entry, FileSnapshot observed)
    {
        if (observed.SizeBytes != entry.Snapshot.SizeBytes ||
            observed.Sha256 != entry.Snapshot.Sha256)
        {
            throw new EvidenceValidationException(
                "ACCEPTANCE_FILE_CHANGED_DURING_VERIFICATION",
                $"Leased file '{entry.Path}' changed during verification.");
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class LeaseEntry(
        string path,
        FileStream stream,
        FileSnapshot snapshot,
        bool textValidated)
    {
        public string Path { get; } = path;
        public FileStream Stream { get; } = stream;
        public FileSnapshot Snapshot { get; set; } = snapshot;
        public bool TextValidated { get; set; } = textValidated;
    }
}

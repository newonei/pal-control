using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed class SaveManagementOptions
{
    public string BackupRoot { get; init; } = "../../backups/savegames";
    public bool RequireRunningProcess { get; init; } = true;
    public int SnapshotTimeoutSeconds { get; init; } = 45;
    public int StabilitySampleMilliseconds { get; init; } = 750;
    public int StabilityRequiredSamples { get; init; } = 3;
    public long MinimumFreeSpaceBytes { get; init; } = 1L * 1024 * 1024 * 1024;
}

public sealed class SaveManagementException : Exception
{
    public SaveManagementException(string code, string message, bool uncertain = false)
        : base(message)
    {
        Code = code;
        Uncertain = uncertain;
    }

    public string Code { get; }
    public bool Uncertain { get; }
}

public sealed record ResolvedSaveWorld(
    string ServerId,
    string InstallRoot,
    string WorldRoot,
    string WorldGuid,
    string WorldName,
    string GameVersion,
    SaveValidationStatus Validation);

public sealed record NativeSaveSnapshot(
    string BackupId,
    string DirectoryName,
    string RootPath,
    DateTimeOffset CreatedAt,
    string Fingerprint,
    int FileCount,
    long TotalBytes);

public sealed class SaveManagementService
{
    private const string VerificationFileName = "verification.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SaveManagementOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly PalworldRestClient _palworld;
    private readonly ILogger<SaveManagementService> _logger;

    public SaveManagementService(
        IOptions<SaveManagementOptions> options,
        IConfiguration configuration,
        IHostEnvironment environment,
        PalworldRestClient palworld,
        ILogger<SaveManagementService> logger)
    {
        _options = options.Value;
        _configuration = configuration;
        _environment = environment;
        _palworld = palworld;
        _logger = logger;
    }

    public async Task<SaveStatus> GetStatusAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        try
        {
            var world = await ResolveActiveWorldAsync(serverId, cancellationToken);
            var saveFiles = EnumerateRegularFiles(world.WorldRoot, "backup");
            var playerPrefix = "Players" + Path.DirectorySeparatorChar;
            var lastModified = saveFiles.Count == 0
                ? (DateTimeOffset?)null
                : saveFiles.Max(file => new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            var native = ListNativeSnapshots(world);
            var managed = ListManagedBackupsCore(serverId);
            var drive = GetDriveInfo(GetBackupRoot());
            var players = await _palworld.TryGetPlayersAsync(cancellationToken);

            return new SaveStatus(
                serverId,
                true,
                DateTimeOffset.UtcNow,
                world.WorldGuid,
                world.WorldName,
                world.GameVersion,
                players?.Count,
                new SaveFileStatistics(
                    saveFiles.Count,
                    saveFiles.Count(file => file.RelativePath.StartsWith(
                        playerPrefix,
                        StringComparison.OrdinalIgnoreCase)),
                    saveFiles.Sum(file => file.Length),
                    lastModified),
                new SaveDiskStatistics(drive.AvailableFreeSpace, drive.TotalSize),
                new SaveBackupStatistics(
                    native.Count,
                    native.Sum(item => item.TotalBytes),
                    null,
                    native.Count == 0 ? null : native.Max(item => item.CreatedAt)),
                new SaveBackupStatistics(
                    managed.Count,
                    managed.Sum(item => item.TotalBytes),
                    managed.Count(item => string.Equals(
                        item.Integrity,
                        "verified",
                        StringComparison.Ordinal)),
                    managed.Count == 0 ? null : managed.Max(item => item.CreatedAt)),
                world.Validation,
                null);
        }
        catch (SaveManagementException exception)
        {
            _logger.LogWarning("Save status validation failed with {Code}.", exception.Code);
            return EmptyStatus(serverId, exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogError(exception, "Save status inspection failed.");
            return EmptyStatus(
                serverId,
                "SAVE_STATUS_UNAVAILABLE",
                "The save status could not be inspected safely.");
        }
    }

    public async Task<ResolvedSaveWorld> ResolveActiveWorldAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        var configuredServerId = _configuration["Palworld:ServerId"] ?? "local";
        if (!string.Equals(serverId, configuredServerId, StringComparison.Ordinal))
        {
            throw new SaveManagementException(
                "SERVER_NOT_FOUND",
                $"Server '{serverId}' is not configured on this Control API instance.");
        }

        var configuredInstallRoot = _configuration["Palworld:InstallRoot"];
        if (string.IsNullOrWhiteSpace(configuredInstallRoot))
        {
            throw new SaveManagementException(
                "PALWORLD_INSTALL_ROOT_NOT_CONFIGURED",
                "Palworld:InstallRoot is not configured.");
        }

        var installRoot = ResolveConfiguredPath(configuredInstallRoot);
        EnsureExistingAbsoluteAncestorChainHasNoReparsePoints(installRoot);
        var serverExecutable = Path.Combine(installRoot, "PalServer.exe");
        if (!Directory.Exists(installRoot) || !File.Exists(serverExecutable))
        {
            throw new SaveManagementException(
                "PALWORLD_INSTALL_ROOT_INVALID",
                "The configured Palworld installation is not a dedicated-server root.");
        }
        EnsureDirectoryIsNotReparsePoint(installRoot);
        EnsureRegularFileIsNotReparsePoint(serverExecutable);

        var processMatched = !_options.RequireRunningProcess || IsExpectedServerProcessRunning(
            serverExecutable);
        if (!processMatched)
        {
            throw new SaveManagementException(
                "PALWORLD_PROCESS_MISMATCH",
                "No running PalServer.exe matches the configured Palworld installation.");
        }

        var settingsPath = Path.Combine(
            installRoot,
            "Pal",
            "Saved",
            "Config",
            "WindowsServer",
            "GameUserSettings.ini");
        if (!File.Exists(settingsPath))
        {
            throw new SaveManagementException(
                "PALWORLD_SETTINGS_NOT_FOUND",
                "GameUserSettings.ini was not found under the configured installation.");
        }
        EnsurePathHasNoReparsePoints(installRoot, settingsPath);
        var dedicatedServerName = ReadIniValue(settingsPath, "DedicatedServerName");
        if (!IsSafePathSegment(dedicatedServerName))
        {
            throw new SaveManagementException(
                "DEDICATED_SERVER_NAME_INVALID",
                "GameUserSettings.ini does not contain a safe DedicatedServerName.");
        }

        var info = await _palworld.TryGetInfoAsync(cancellationToken);
        if (info is null)
        {
            throw new SaveManagementException(
                "PALWORLD_REST_INFO_UNAVAILABLE",
                "Official REST server information is unavailable.");
        }

        var worldBase = Path.GetFullPath(Path.Combine(
            installRoot,
            "Pal",
            "Saved",
            "SaveGames",
            "0"));
        var worldRoot = Path.GetFullPath(Path.Combine(worldBase, dedicatedServerName));
        EnsureContained(worldBase, worldRoot);
        if (!Directory.Exists(worldRoot))
        {
            throw new SaveManagementException(
                "ACTIVE_WORLD_NOT_FOUND",
                "The configured active world save directory does not exist.");
        }
        EnsurePathHasNoReparsePoints(installRoot, worldRoot);

        var serverNameMatched = string.Equals(
            NormalizeWorldGuid(dedicatedServerName),
            NormalizeWorldGuid(Path.GetFileName(worldRoot)),
            StringComparison.Ordinal);
        var worldGuidMatched = string.Equals(
            NormalizeWorldGuid(dedicatedServerName),
            NormalizeWorldGuid(info.WorldGuid),
            StringComparison.Ordinal);
        if (!serverNameMatched || !worldGuidMatched)
        {
            throw new SaveManagementException(
                "ACTIVE_WORLD_IDENTITY_MISMATCH",
                "DedicatedServerName, the active save directory, and the official REST world GUID do not match.");
        }

        var backupRoot = GetBackupRoot();
        if (PathsEqual(worldRoot, backupRoot) ||
            IsContained(worldRoot, backupRoot) ||
            IsContained(backupRoot, worldRoot))
        {
            throw new SaveManagementException(
                "BACKUP_ROOT_OVERLAPS_ACTIVE_SAVE",
                "The managed backup root must be outside the active world save directory.");
        }

        return new ResolvedSaveWorld(
            serverId,
            installRoot,
            worldRoot,
            dedicatedServerName,
            info.ServerName,
            info.Version,
            new SaveValidationStatus(processMatched, serverNameMatched, worldGuidMatched));
    }

    public IReadOnlySet<string> GetNativeSnapshotIdentities(ResolvedSaveWorld world) =>
        ListNativeSnapshots(world)
            .Select(snapshot => snapshot.DirectoryName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<NativeSaveSnapshot> WaitForNewStableNativeSnapshotAsync(
        ResolvedSaveWorld world,
        IReadOnlySet<string> identitiesBeforeSave,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(_options.SnapshotTimeoutSeconds, 2, 600));
        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(_options.StabilitySampleMilliseconds, 100, 30_000));
        var requiredSamples = Math.Clamp(_options.StabilityRequiredSamples, 2, 20);
        string? candidateIdentity = null;
        string? candidateFingerprint = null;
        var stableSamples = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = ListNativeSnapshots(world)
                .Where(snapshot =>
                    !identitiesBeforeSave.Contains(snapshot.DirectoryName) &&
                    snapshot.FileCount > 0)
                .OrderByDescending(snapshot => snapshot.CreatedAt)
                .FirstOrDefault();
            if (candidate is not null)
            {
                if (string.Equals(candidateIdentity, candidate.DirectoryName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidateFingerprint, candidate.Fingerprint, StringComparison.Ordinal))
                {
                    stableSamples++;
                }
                else
                {
                    candidateIdentity = candidate.DirectoryName;
                    candidateFingerprint = candidate.Fingerprint;
                    stableSamples = 1;
                }

                if (stableSamples >= requiredSamples)
                {
                    return candidate;
                }
            }

            await Task.Delay(interval, cancellationToken);
        }

        throw new SaveManagementException(
            "NEW_NATIVE_SNAPSHOT_NOT_OBSERVED",
            "The game accepted the save request, but no new stable native world snapshot was observed before the timeout.",
            uncertain: true);
    }

    public async Task<SaveBackupSummary> CreateManagedBackupAsync(
        ResolvedSaveWorld world,
        NativeSaveSnapshot snapshot,
        string backupId,
        string label,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(backupId, "N", out _))
        {
            throw new SaveManagementException("INVALID_BACKUP_ID", "The managed backup ID is invalid.");
        }

        EnsurePathHasNoReparsePoints(
            Path.Combine(world.WorldRoot, "backup", "world"),
            snapshot.RootPath);
        var sourceFiles = EnumerateRegularFiles(snapshot.RootPath, "backup");
        if (sourceFiles.Count == 0)
        {
            throw new SaveManagementException(
                "NATIVE_SNAPSHOT_EMPTY",
                "The new native world snapshot contains no regular files.",
                uncertain: true);
        }
        if (!string.Equals(Fingerprint(sourceFiles), snapshot.Fingerprint, StringComparison.Ordinal))
        {
            throw new SaveManagementException(
                "NATIVE_SNAPSHOT_CHANGED",
                "The native world snapshot changed after it was considered stable.",
                uncertain: true);
        }

        var serverRoot = GetServerBackupRoot(world.ServerId, create: true);
        var partialRoot = SafeChild(serverRoot, $".partial-{backupId}");
        var finalRoot = SafeChild(serverRoot, backupId);
        if (Directory.Exists(partialRoot) || Directory.Exists(finalRoot))
        {
            throw new SaveManagementException(
                "BACKUP_ID_COLLISION",
                "The generated managed backup ID already exists.");
        }

        var sourceBytes = sourceFiles.Sum(file => file.Length);
        var backupDrive = GetDriveInfo(serverRoot);
        var requiredFree = checked(sourceBytes + Math.Max(0, _options.MinimumFreeSpaceBytes));
        if (backupDrive.AvailableFreeSpace < requiredFree)
        {
            throw new SaveManagementException(
                "BACKUP_DISK_SPACE_INSUFFICIENT",
                "There is not enough free disk space to create and retain the managed backup.");
        }

        try
        {
            var dataRoot = Path.Combine(partialRoot, "data");
            Directory.CreateDirectory(dataRoot);
            var entries = new List<ManifestFile>(sourceFiles.Count);
            foreach (var source in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(Path.Combine(dataRoot, source.RelativePath));
                EnsureContained(dataRoot, destination);
                var destinationDirectory = Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException("A backup destination has no parent directory.");
                Directory.CreateDirectory(destinationDirectory);
                await CopyFileAsync(source.FullPath, destination, cancellationToken);
                File.SetLastWriteTimeUtc(destination, source.LastWriteTimeUtc);
                var hash = await HashFileAsync(destination, cancellationToken);
                entries.Add(new ManifestFile(
                    NormalizeRelativePath(source.RelativePath),
                    source.Length,
                    new DateTimeOffset(source.LastWriteTimeUtc, TimeSpan.Zero),
                    hash));
            }

            var sourceAfterCopy = EnumerateRegularFiles(snapshot.RootPath, "backup");
            if (!string.Equals(Fingerprint(sourceAfterCopy), snapshot.Fingerprint, StringComparison.Ordinal))
            {
                throw new SaveManagementException(
                    "NATIVE_SNAPSHOT_CHANGED_DURING_COPY",
                    "The native world snapshot changed while it was being copied.",
                    uncertain: true);
            }

            var createdAt = DateTimeOffset.UtcNow;
            var manifest = new ManagedBackupManifest(
                1,
                backupId,
                world.ServerId,
                label,
                world.WorldGuid,
                world.GameVersion,
                createdAt,
                actor,
                reason,
                "verified",
                "stable",
                entries);
            var manifestPath = Path.Combine(partialRoot, "manifest.json");
            await WriteManifestAsync(manifestPath, manifest, cancellationToken);

            var verification = await VerifyManifestFilesAsync(
                dataRoot,
                manifest.Files,
                cancellationToken);
            if (!verification)
            {
                throw new SaveManagementException(
                    "BACKUP_VERIFICATION_FAILED",
                    "The copied managed backup failed verification before publication.");
            }

            var manifestHash = await HashFileAsync(manifestPath, cancellationToken);
            await WriteVerificationAsync(
                partialRoot,
                new BackupVerification(
                    1,
                    backupId,
                    "verified",
                    DateTimeOffset.UtcNow,
                    manifestHash),
                cancellationToken);
            Directory.Move(partialRoot, finalRoot);
            return ToSummary(manifest, manifestHash, "verified");
        }
        catch
        {
            TryDeleteOwnedPartialDirectory(serverRoot, partialRoot, backupId);
            throw;
        }
    }

    public async Task<IReadOnlyList<SaveBackupSummary>> ListBackupsAsync(
        string serverId,
        string? kind,
        CancellationToken cancellationToken)
    {
        var world = await ResolveActiveWorldAsync(serverId, cancellationToken);
        var normalizedKind = string.IsNullOrWhiteSpace(kind)
            ? null
            : kind.Trim().ToLowerInvariant();
        if (normalizedKind is not (null or "native" or "managed"))
        {
            throw new SaveManagementException(
                "INVALID_BACKUP_KIND",
                "Backup kind must be 'native' or 'managed'.");
        }

        var result = new List<SaveBackupSummary>();
        if (normalizedKind is null or "native")
        {
            result.AddRange(ListNativeSnapshots(world).Select(snapshot =>
                new SaveBackupSummary(
                    snapshot.BackupId,
                    "native",
                    null,
                    world.WorldGuid,
                    world.GameVersion,
                    snapshot.CreatedAt,
                    snapshot.FileCount,
                    snapshot.TotalBytes,
                    "unverified",
                    "native",
                    null,
                    null,
                    null)));
        }
        if (normalizedKind is null or "managed")
        {
            result.AddRange(ListManagedBackupsCore(serverId));
        }
        return result.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<SaveBackupSummary?> GetBackupAsync(
        string serverId,
        string backupId,
        CancellationToken cancellationToken)
    {
        var items = await ListBackupsAsync(
            serverId,
            backupId.StartsWith("native-", StringComparison.OrdinalIgnoreCase)
                ? "native"
                : "managed",
            cancellationToken);
        return items.FirstOrDefault(item => string.Equals(
            item.BackupId,
            backupId,
            StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SaveBackupSummary> VerifyManagedBackupAsync(
        string serverId,
        string backupId,
        CancellationToken cancellationToken)
    {
        var configuredServerId = _configuration["Palworld:ServerId"] ?? "local";
        if (!string.Equals(serverId, configuredServerId, StringComparison.Ordinal))
        {
            throw new SaveManagementException(
                "SERVER_NOT_FOUND",
                $"Server '{serverId}' is not configured on this Control API instance.");
        }
        var backupRoot = GetManagedBackupDirectory(serverId, backupId);
        var manifestPath = Path.Combine(backupRoot, "manifest.json");
        EnsureRegularFileIsNotReparsePoint(manifestPath);
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken)
            ?? throw new SaveManagementException(
                "BACKUP_NOT_FOUND",
                $"Managed backup '{backupId}' does not exist.");
        if (!string.Equals(manifest.BackupId, backupId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.ServerId, serverId, StringComparison.Ordinal))
        {
            throw new SaveManagementException(
                "BACKUP_MANIFEST_IDENTITY_MISMATCH",
                "The managed backup manifest identity does not match the requested backup.");
        }

        var manifestHash = await HashFileAsync(manifestPath, cancellationToken);
        var existingVerification = ReadVerification(backupRoot);
        if (!existingVerification.Exists)
        {
            return ToSummary(manifest, manifestHash, "unverified");
        }
        if (existingVerification.Value is not { } anchored ||
            !string.Equals(anchored.BackupId, backupId, StringComparison.OrdinalIgnoreCase))
        {
            return ToSummary(manifest, manifestHash, "failed");
        }
        var anchorHash = anchored.ManifestSha256;
        if (!string.Equals(anchorHash, manifestHash, StringComparison.OrdinalIgnoreCase))
        {
            await WriteVerificationAsync(
                backupRoot,
                anchored with
                {
                    Integrity = "failed",
                    VerifiedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
            return ToSummary(manifest, manifestHash, "failed");
        }
        var verified = await VerifyManifestFilesAsync(
            Path.Combine(backupRoot, "data"),
            manifest.Files,
            cancellationToken);
        var integrity = verified ? "verified" : "failed";
        await WriteVerificationAsync(
            backupRoot,
            new BackupVerification(
                1,
                backupId,
                integrity,
                DateTimeOffset.UtcNow,
                anchorHash),
            cancellationToken);
        return ToSummary(manifest, manifestHash, integrity);
    }

    public void CleanupPartialForCommand(string serverId, string backupId)
    {
        if (!Guid.TryParseExact(backupId, "N", out _))
        {
            return;
        }
        var serverRoot = GetServerBackupRoot(serverId, create: false);
        var partialRoot = SafeChild(serverRoot, $".partial-{backupId}");
        TryDeleteOwnedPartialDirectory(serverRoot, partialRoot, backupId);
    }

    private IReadOnlyList<NativeSaveSnapshot> ListNativeSnapshots(ResolvedSaveWorld world)
    {
        var root = Path.Combine(world.WorldRoot, "backup", "world");
        if (!Directory.Exists(root))
        {
            return [];
        }
        EnsurePathHasNoReparsePoints(world.WorldRoot, root);

        var snapshots = new List<NativeSaveSnapshot>();
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SaveManagementException(
                    "SAVE_REPARSE_POINT_REJECTED",
                    "A reparse point was found inside the native backup tree.");
            }
            if (!TryParseNativeTimestamp(directory.Name, out var createdAt))
            {
                continue;
            }

            var files = EnumerateRegularFiles(directory.FullName, "backup");
            snapshots.Add(new NativeSaveSnapshot(
                NativeBackupId(directory.Name),
                directory.Name,
                directory.FullName,
                createdAt,
                Fingerprint(files),
                files.Count,
                files.Sum(file => file.Length)));
        }
        return snapshots;
    }

    private IReadOnlyList<SaveBackupSummary> ListManagedBackupsCore(string serverId)
    {
        var serverRoot = GetServerBackupRoot(serverId, create: true);
        var results = new List<SaveBackupSummary>();
        foreach (var directory in new DirectoryInfo(serverRoot).EnumerateDirectories())
        {
            if (directory.Name.StartsWith(".partial-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(directory.Name, "N", out _))
            {
                continue;
            }
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SaveManagementException(
                    "SAVE_REPARSE_POINT_REJECTED",
                    "A reparse point was found inside the managed backup tree.");
            }

            try
            {
                var manifestPath = Path.Combine(directory.FullName, "manifest.json");
                EnsureRegularFileIsNotReparsePoint(manifestPath);
                var manifest = ReadManifest(manifestPath);
                if (manifest is null ||
                    !string.Equals(manifest.BackupId, directory.Name, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(manifest.ServerId, serverId, StringComparison.Ordinal))
                {
                    continue;
                }
                var manifestHash = HashFile(manifestPath);
                var integrity = ReadEffectiveIntegrity(
                    directory.FullName,
                    manifest.BackupId,
                    manifestHash);
                results.Add(ToSummary(manifest, manifestHash, integrity));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(
                    exception,
                    "Ignoring an unreadable managed backup manifest for backup {BackupId}.",
                    directory.Name);
            }
        }
        return results;
    }

    private string GetBackupRoot()
    {
        var root = ResolveConfiguredPath(_options.BackupRoot);
        EnsureExistingAbsoluteAncestorChainHasNoReparsePoints(root);
        Directory.CreateDirectory(root);
        EnsureExistingAbsoluteAncestorChainHasNoReparsePoints(root);
        return root;
    }

    private string GetServerBackupRoot(string serverId, bool create)
    {
        if (!IsSafePathSegment(serverId))
        {
            throw new SaveManagementException("INVALID_SERVER_ID", "The server ID is not a safe path segment.");
        }
        var root = GetBackupRoot();
        var serverRoot = SafeChild(root, serverId);
        if (create)
        {
            Directory.CreateDirectory(serverRoot);
        }
        if (Directory.Exists(serverRoot))
        {
            EnsurePathHasNoReparsePoints(root, serverRoot);
        }
        return serverRoot;
    }

    private string GetManagedBackupDirectory(string serverId, string backupId)
    {
        if (!Guid.TryParseExact(backupId, "N", out _))
        {
            throw new SaveManagementException("INVALID_BACKUP_ID", "The managed backup ID is invalid.");
        }
        var serverRoot = GetServerBackupRoot(serverId, create: false);
        var backupRoot = SafeChild(serverRoot, backupId);
        if (!Directory.Exists(backupRoot))
        {
            throw new SaveManagementException(
                "BACKUP_NOT_FOUND",
                $"Managed backup '{backupId}' does not exist.");
        }
        EnsureDirectoryIsNotReparsePoint(backupRoot);
        return backupRoot;
    }

    private string ResolveConfiguredPath(string configuredPath) => Path.GetFullPath(
        Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(_environment.ContentRootPath, configuredPath));

    private bool IsExpectedServerProcessRunning(string expectedExecutable)
    {
        var expected = ResolveProcessPath(expectedExecutable);
        foreach (var process in Process.GetProcessesByName("PalServer"))
        {
            using (process)
            {
                try
                {
                    var actual = process.MainModule?.FileName;
                    if (actual is not null && string.Equals(
                        ResolveProcessPath(actual),
                        expected,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (
                    exception is System.ComponentModel.Win32Exception or
                    InvalidOperationException or
                    IOException or
                    UnauthorizedAccessException)
                {
                    _logger.LogDebug(exception, "Could not inspect one PalServer process path.");
                }
            }
        }
        return false;
    }

    private static string ResolveProcessPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException("A process path has no filesystem root.");
        var current = volumeRoot;
        var relative = Path.GetRelativePath(volumeRoot, fullPath);
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                current = Path.GetFullPath(target.FullName);
            }
        }
        return Path.GetFullPath(current);
    }

    private static string ReadIniValue(string path, string key)
    {
        if (!File.Exists(path))
        {
            throw new SaveManagementException(
                "PALWORLD_SETTINGS_NOT_FOUND",
                "GameUserSettings.ini was not found under the configured installation.");
        }
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0 ||
                !string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return line[(separator + 1)..].Trim().Trim('"');
        }
        return string.Empty;
    }

    private static List<SaveFileEntry> EnumerateRegularFiles(
        string root,
        string excludedDirectoryName)
    {
        var fullRoot = Path.GetFullPath(root);
        EnsureDirectoryIsNotReparsePoint(fullRoot);
        var files = new List<SaveFileEntry>();
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new SaveManagementException(
                        "SAVE_REPARSE_POINT_REJECTED",
                        "A reparse point was found inside a save or backup directory.");
                }
                if (string.Equals(info.Name, excludedDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                EnsureContained(fullRoot, info.FullName);
                pending.Push(info.FullName);
            }
            foreach (var path in Directory.EnumerateFiles(current))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new SaveManagementException(
                        "SAVE_REPARSE_POINT_REJECTED",
                        "A reparse point was found inside a save or backup directory.");
                }
                EnsureContained(fullRoot, info.FullName);
                files.Add(new SaveFileEntry(
                    info.FullName,
                    Path.GetRelativePath(fullRoot, info.FullName),
                    info.Length,
                    info.LastWriteTimeUtc));
            }
        }
        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
    }

    private static async Task<bool> VerifyManifestFilesAsync(
        string dataRoot,
        IReadOnlyList<ManifestFile> expectedFiles,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dataRoot))
        {
            return false;
        }
        IReadOnlyList<SaveFileEntry> actual;
        try
        {
            actual = EnumerateRegularFiles(dataRoot, "backup");
        }
        catch (SaveManagementException)
        {
            return false;
        }
        if (actual.Count != expectedFiles.Count)
        {
            return false;
        }

        var expectedByPath = expectedFiles.ToDictionary(
            file => NormalizeRelativePath(file.RelativePath),
            StringComparer.Ordinal);
        foreach (var file in actual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(file.RelativePath);
            if (!expectedByPath.TryGetValue(relativePath, out var expected) ||
                expected.Length != file.Length)
            {
                return false;
            }
            var hash = await HashFileAsync(file.FullPath, cancellationToken);
            if (!string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destinationStream = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        await destinationStream.FlushAsync(cancellationToken);
        destinationStream.Flush(true);
    }

    private static async Task WriteManifestAsync(
        string path,
        ManagedBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private static async Task WriteVerificationAsync(
        string backupRoot,
        BackupVerification verification,
        CancellationToken cancellationToken)
    {
        EnsureDirectoryIsNotReparsePoint(backupRoot);
        var finalPath = Path.GetFullPath(Path.Combine(backupRoot, VerificationFileName));
        EnsureContained(backupRoot, finalPath);
        if (File.Exists(finalPath))
        {
            EnsureRegularFileIsNotReparsePoint(finalPath);
        }

        var temporaryPath = Path.GetFullPath(Path.Combine(
            backupRoot,
            $".verification-{Guid.NewGuid():N}.tmp"));
        EnsureContained(backupRoot, temporaryPath);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(verification, JsonOptions);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            if (File.Exists(finalPath))
            {
                File.Replace(temporaryPath, finalPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, finalPath);
            }

            using var durable = new FileStream(
                finalPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                1,
                FileOptions.WriteThrough);
            durable.Flush(true);
        }
        finally
        {
            if (File.Exists(temporaryPath) &&
                (File.GetAttributes(temporaryPath) & FileAttributes.ReparsePoint) == 0)
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string ReadEffectiveIntegrity(
        string backupRoot,
        string backupId,
        string manifestSha256)
    {
        var state = ReadVerification(backupRoot);
        if (!state.Exists)
        {
            return "unverified";
        }
        if (state.Value is not { } verification ||
            !string.Equals(verification.BackupId, backupId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                verification.ManifestSha256,
                manifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }
        return verification.Integrity;
    }

    private static VerificationReadState ReadVerification(string backupRoot)
    {
        var path = Path.GetFullPath(Path.Combine(backupRoot, VerificationFileName));
        EnsureContained(backupRoot, path);
        if (!File.Exists(path))
        {
            return new VerificationReadState(false, null);
        }
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return new VerificationReadState(true, null);
            }
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var verification = JsonSerializer.Deserialize<BackupVerification>(stream, JsonOptions);
            return verification is not null &&
                   verification.SchemaVersion == 1 &&
                   verification.Integrity is "verified" or "failed" &&
                   IsSha256(verification.ManifestSha256)
                ? new VerificationReadState(true, verification)
                : new VerificationReadState(true, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new VerificationReadState(true, null);
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static ManagedBackupManifest? ReadManifest(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ManagedBackupManifest>(stream, JsonOptions);
    }

    private static async Task<ManagedBackupManifest?> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<ManagedBackupManifest>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    private static void EnsureRegularFileIsNotReparsePoint(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SaveManagementException(
                "SAVE_REPARSE_POINT_REJECTED",
                "A managed backup metadata file is a reparse point.");
        }
    }

    private static SaveBackupSummary ToSummary(
        ManagedBackupManifest manifest,
        string manifestHash,
        string integrity) => new(
            manifest.BackupId,
            "managed",
            manifest.Label,
            manifest.WorldGuid,
            manifest.GameVersion,
            manifest.CreatedAt,
            manifest.Files.Count,
            manifest.Files.Sum(file => file.Length),
            integrity,
            manifest.Consistency,
            manifest.Actor,
            manifest.Reason,
            manifestHash);

    private static string Fingerprint(IReadOnlyList<SaveFileEntry> files)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            var line = $"{NormalizeRelativePath(file.RelativePath)}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}\n";
            incremental.AppendData(System.Text.Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool TryParseNativeTimestamp(string name, out DateTimeOffset timestamp)
    {
        if (DateTime.TryParseExact(
            name,
            "yyyy.MM.dd-HH.mm.ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var local))
        {
            timestamp = new DateTimeOffset(local);
            return true;
        }
        timestamp = default;
        return false;
    }

    private static string NativeBackupId(string directoryName) =>
        $"native-{directoryName.Replace(".", string.Empty, StringComparison.Ordinal).Replace("-", "T", StringComparison.Ordinal)}";

    private static bool IsSafePathSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string NormalizeWorldGuid(string value) => string.Concat(
        value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SaveManagementException(
                "SAVE_REPARSE_POINT_REJECTED",
                "A configured save or backup directory is a reparse point.");
        }
    }

    private static void EnsurePathHasNoReparsePoints(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        EnsureContained(fullRoot, fullPath);
        EnsureDirectoryIsNotReparsePoint(fullRoot);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var current = fullRoot;
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureDirectoryIsNotReparsePoint(current);
        }
    }

    private static void EnsureExistingAbsoluteAncestorChainHasNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            throw new SaveManagementException(
                "SAVE_PATH_OUTSIDE_ROOT",
                "A configured save or backup path has no filesystem root.");
        }

        var current = volumeRoot;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new SaveManagementException(
                "SAVE_REPARSE_POINT_REJECTED",
                "A configured path ancestor is a reparse point.");
        }
        var relative = Path.GetRelativePath(volumeRoot, fullPath);
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SaveManagementException(
                    "SAVE_REPARSE_POINT_REJECTED",
                    "A configured path ancestor is a reparse point.");
            }
        }
    }

    private static string SafeChild(string parent, string child)
    {
        var path = Path.GetFullPath(Path.Combine(parent, child));
        EnsureContained(parent, path);
        return path;
    }

    private static void EnsureContained(string root, string path)
    {
        if (!IsContained(root, path))
        {
            throw new SaveManagementException(
                "SAVE_PATH_OUTSIDE_ROOT",
                "A save or backup path escaped its configured root.");
        }
    }

    private static bool IsContained(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        return fullPath.Length > fullRoot.Length &&
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        StringComparison.OrdinalIgnoreCase);

    private static DriveInfo GetDriveInfo(string path)
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(driveRoot))
        {
            throw new SaveManagementException(
                "BACKUP_DRIVE_UNAVAILABLE",
                "The managed backup drive could not be resolved.");
        }
        return new DriveInfo(driveRoot);
    }

    private static void TryDeleteOwnedPartialDirectory(
        string serverRoot,
        string partialRoot,
        string backupId)
    {
        if (!Guid.TryParseExact(backupId, "N", out _) ||
            !string.Equals(
                Path.GetFileName(partialRoot),
                $".partial-{backupId}",
                StringComparison.Ordinal) ||
            !IsContained(serverRoot, partialRoot) ||
            !Directory.Exists(partialRoot))
        {
            return;
        }
        try
        {
            if (TreeContainsReparsePoint(partialRoot))
            {
                return;
            }
            Directory.Delete(partialRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A partial directory is never followed or force-deleted when it cannot be inspected safely.
        }
    }

    private static bool TreeContainsReparsePoint(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
            }
        }
        return false;
    }

    private static SaveStatus EmptyStatus(string serverId, string code, string message) => new(
        serverId,
        false,
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        null,
        new SaveFileStatistics(0, 0, 0, null),
        new SaveDiskStatistics(0, 0),
        new SaveBackupStatistics(0, 0, null, null),
        new SaveBackupStatistics(0, 0, 0, null),
        new SaveValidationStatus(false, false, false),
        new ApiError(code, message));

    private sealed record SaveFileEntry(
        string FullPath,
        string RelativePath,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed record ManifestFile(
        string RelativePath,
        long Length,
        DateTimeOffset LastModifiedAt,
        string Sha256);

    private sealed record ManagedBackupManifest(
        int SchemaVersion,
        string BackupId,
        string ServerId,
        string Label,
        string WorldGuid,
        string GameVersion,
        DateTimeOffset CreatedAt,
        string Actor,
        string Reason,
        string Integrity,
        string Consistency,
        IReadOnlyList<ManifestFile> Files);

    private sealed record BackupVerification(
        int SchemaVersion,
        string BackupId,
        string Integrity,
        DateTimeOffset VerifiedAt,
        string ManifestSha256);

    private sealed record VerificationReadState(bool Exists, BackupVerification? Value);
}

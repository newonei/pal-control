using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed class EconomyContinuityOptions
{
    public string BackupRoot { get; init; } = "../../backups/economy";
    public string StagingRoot { get; init; } = "../../backups/economy-staging";
    public int RetentionDays { get; init; } = 60;
    public int MinimumRetainedBackups { get; init; } = 8;
    public long MinimumFreeSpaceBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public int CapacitySafetyPercent { get; init; } = 150;
    public int RpoMinutes { get; init; } = 15;
    public int TargetRtoMinutes { get; init; } = 60;

    public bool IsValid(out string? error)
    {
        if (string.IsNullOrWhiteSpace(BackupRoot) || string.IsNullOrWhiteSpace(StagingRoot) ||
            PathsEqual(BackupRoot, StagingRoot))
        {
            error = "EconomyContinuity backup and staging roots must be distinct non-empty paths.";
            return false;
        }
        if (RetentionDays is < 7 or > 3650 || MinimumRetainedBackups is < 2 or > 520)
        {
            error = "EconomyContinuity retention must keep 2-520 backups for 7-3650 days.";
            return false;
        }
        if (MinimumFreeSpaceBytes is < 16_777_216 or > 1_125_899_906_842_624 ||
            CapacitySafetyPercent is < 100 or > 1000)
        {
            error = "EconomyContinuity free-space and capacity safety settings are invalid.";
            return false;
        }
        if (RpoMinutes is < 1 or > 1440 || TargetRtoMinutes is < 5 or > 1440)
        {
            error = "EconomyContinuity RPO/RTO targets are outside safe documented ranges.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        first.Trim().TrimEnd('/', '\\'),
        second.Trim().TrimEnd('/', '\\'),
        StringComparison.OrdinalIgnoreCase);
}

public sealed record EconomySnapshotFile(
    string RelativePath,
    string Role,
    long Bytes,
    string Sha256);

public sealed record EconomyPendingTransaction(
    string Kind,
    string Id,
    string State,
    DateTimeOffset? UpdatedAt);

public sealed record EconomySnapshotManifest(
    int SchemaVersion,
    string BackupId,
    string ServerId,
    string WorldId,
    DateTimeOffset CreatedAt,
    long LastEconomySequence,
    int SqliteUserVersion,
    int WalLogFrames,
    int WalCheckpointedFrames,
    IReadOnlyList<EconomyPendingTransaction> PendingTransactions,
    IReadOnlyList<EconomySnapshotFile> Files,
    string ContentHash,
    int RpoMinutes,
    int TargetRtoMinutes,
    string? IdempotencyKeyHash);

public sealed record EconomyStagingVerification(
    string BackupId,
    string StagingDirectory,
    DateTimeOffset VerifiedAt,
    bool HashesValid,
    bool SqliteIntegrityValid,
    bool EconomyReplayValid,
    bool WorldIdValid,
    string ExpectedWorldId,
    string ManifestWorldId,
    int PendingTransactionCount,
    string ContentHash,
    bool EconomyForcedClosed,
    bool ActiveSeasonWorldValid,
    bool LedgerProjectionValid,
    int BlockingOrderCount,
    bool SqliteSchemaValid,
    bool ForeignKeysValid,
    bool CommandReplayValid,
    bool CommandIdempotencyValid,
    int PendingCommandCount,
    bool PendingStateMatchesManifest);

public sealed record EconomyCapacityPlan(
    long CurrentAuthoritativeBytes,
    long ExistingBackupBytes,
    int ExistingBackupCount,
    long EstimatedRetainedBytes,
    long RequiredFreeBytes,
    long AvailableFreeBytes,
    bool CapacitySufficient,
    int RetentionDays,
    int MinimumRetainedBackups,
    int SafetyPercent);

public sealed record EconomyRetentionCandidate(
    string BackupId,
    DateTimeOffset CreatedAt,
    long TotalBytes,
    string Reason);

public enum EconomyContinuityFaultPoint
{
    AfterSqliteBackup,
    AfterEconomySideStateCopy,
    AfterCommandSideStateCopy,
    AfterManifestWrite,
    AfterStagingFilesCopied,
    BeforeStagingPublish
}

/// <summary>
/// Creates one recoverable point-in-time bundle for the authoritative economy
/// SQLite database plus registered legacy/command side state that has not yet
/// migrated into SQLite. Non-economic JSONL channels receive a nested immutable
/// archive manifest. SQLite's online backup API includes committed WAL frames;
/// the checkpoint frame counts are retained as evidence in the outer manifest.
/// </summary>
public sealed class EconomyContinuityService
{
    private const int ManifestSchemaVersion = 2;
    private const string ManifestFileName = "manifest.json";
    private const string VerificationFileName = "staging-verification.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly EconomyContinuityOptions _options;
    private readonly string _economyDataDirectory;
    private readonly string _commandDataDirectory;
    private readonly string _backupRoot;
    private readonly string _stagingRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<EconomyContinuityFaultPoint>? _faultInjector;
    private readonly CommandSideStateArchiveService _commandSideStateArchive = new();

    public EconomyContinuityService(
        IOptions<EconomyContinuityOptions> options,
        IOptions<ExtractionPersistenceOptions> extractionPersistence,
        IOptions<CommandPersistenceOptions> commandPersistence,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(
            options.Value,
            ResolvePath(extractionPersistence.Value.DataDirectory, environment.ContentRootPath),
            ResolvePath(commandPersistence.Value.DataDirectory, environment.ContentRootPath),
            environment.ContentRootPath,
            timeProvider)
    {
    }

    public EconomyContinuityService(
        EconomyContinuityOptions options,
        string economyDataDirectory,
        string commandDataDirectory,
        string contentRoot,
        TimeProvider? timeProvider = null,
        Action<EconomyContinuityFaultPoint>? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid(out var error))
        {
            throw new ArgumentException(error, nameof(options));
        }
        _options = options;
        _economyDataDirectory = Path.GetFullPath(economyDataDirectory);
        _commandDataDirectory = Path.GetFullPath(commandDataDirectory);
        _backupRoot = ResolvePath(options.BackupRoot, contentRoot);
        _stagingRoot = ResolvePath(options.StagingRoot, contentRoot);
        if (PathsOverlap(_economyDataDirectory, _backupRoot) ||
            PathsOverlap(_economyDataDirectory, _stagingRoot) ||
            PathsOverlap(_commandDataDirectory, _backupRoot) ||
            PathsOverlap(_commandDataDirectory, _stagingRoot) ||
            PathsOverlap(_backupRoot, _stagingRoot))
        {
            throw new ArgumentException(
                "Economy data, backup, and staging roots must not overlap.",
                nameof(options));
        }
        Directory.CreateDirectory(_backupRoot);
        Directory.CreateDirectory(_stagingRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _faultInjector = faultInjector;
    }

    public int RpoMinutes => _options.RpoMinutes;

    public bool IsWithinRpo(DateTimeOffset createdAt)
    {
        var now = _timeProvider.GetUtcNow();
        return createdAt <= now.AddMinutes(1) &&
            now - createdAt <= TimeSpan.FromMinutes(_options.RpoMinutes);
    }

    public IReadOnlyList<EconomyPendingTransaction> GetCurrentCommandBlockers()
    {
        var verification = ValidateCommandSideState(_commandDataDirectory);
        if (!verification.ReplayValid || !verification.IdempotencyValid)
        {
            throw new InvalidDataException(
                "Current command/outbox state cannot be replayed with stable idempotency mappings.");
        }
        var results = verification.BlockingTransactions.ToList();
        var database = Path.Combine(_economyDataDirectory, "extraction-commerce.db");
        if (File.Exists(database))
        {
            using var connection = OpenReadOnly(database);
            results.AddRange(ReadPalDefenderPendingTransactions(connection));
        }
        return results
            .DistinctBy(item => $"{item.Kind}|{item.Id}|{item.State}", StringComparer.Ordinal)
            .OrderBy(item => item.UpdatedAt)
            .ToArray();
    }

    public async Task<EconomySnapshotManifest> CreateSnapshotAsync(
        string serverId,
        string worldId,
        ExtractionOperationGateState operationGate,
        int activeOperations,
        CancellationToken cancellationToken) => await CreateSnapshotAsync(
            serverId,
            worldId,
            operationGate,
            activeOperations,
            null,
            cancellationToken);

    public async Task<EconomySnapshotManifest> CreateSnapshotAsync(
        string serverId,
        string worldId,
        ExtractionOperationGateState operationGate,
        int activeOperations,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateSafeSegment(serverId, nameof(serverId));
        ValidateWorldId(worldId);
        var idempotencyKeyHash = NormalizeIdempotencyKeyHash(idempotencyKey);
        if (!operationGate.Maintenance || activeOperations != 0)
        {
            throw new InvalidOperationException(
                "A consistent economy snapshot requires maintenance mode and zero active operations.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sourceDatabase = Path.Combine(_economyDataDirectory, "extraction-commerce.db");
            if (!File.Exists(sourceDatabase))
            {
                throw new FileNotFoundException(
                    "The authoritative extraction-commerce.db does not exist.",
                    sourceDatabase);
            }
            var capacity = GetCapacityPlan();
            if (!capacity.CapacitySufficient)
            {
                throw new IOException(
                    "There is not enough free space for the configured economy-backup retention plan.");
            }

            var now = _timeProvider.GetUtcNow();
            var backupId = idempotencyKeyHash is null
                ? $"{now:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"
                : $"idem{idempotencyKeyHash[..15]}-{idempotencyKeyHash.Substring(15, 32)}";
            var serverRoot = SafeChild(_backupRoot, serverId);
            Directory.CreateDirectory(serverRoot);
            var partialRoot = SafeChild(serverRoot, $".partial-{backupId}");
            var finalRoot = SafeChild(serverRoot, backupId);
            if (Directory.Exists(finalRoot))
            {
                var existing = ReadManifest(finalRoot);
                if (idempotencyKeyHash is null ||
                    !string.Equals(existing.IdempotencyKeyHash, idempotencyKeyHash, StringComparison.Ordinal) ||
                    !string.Equals(existing.ServerId, serverId, StringComparison.Ordinal) ||
                    !string.Equals(existing.WorldId, worldId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The generated economy backup identifier already exists.");
                }
                VerifySnapshotDirectory(finalRoot, existing);
                return existing;
            }
            if (Directory.Exists(partialRoot))
            {
                if (idempotencyKeyHash is null)
                {
                    throw new IOException("The generated economy backup identifier already exists.");
                }
                TryDeleteOwnedPartial(serverRoot, partialRoot, backupId);
            }
            Directory.CreateDirectory(partialRoot);
            try
            {
                var economyTarget = SafeChild(partialRoot, "economy-state");
                var commandTarget = SafeChild(partialRoot, "command-state");
                Directory.CreateDirectory(economyTarget);
                Directory.CreateDirectory(commandTarget);

                var sqliteTarget = Path.Combine(economyTarget, "extraction-commerce.db");
                var files = new List<EconomySnapshotFile>();
                CopyStableSideState(
                    _economyDataDirectory,
                    economyTarget,
                    files,
                    partialRoot,
                    excludeMainDatabase: true,
                    rolePrefix: "economy-side-state",
                    excludedSubtree: IsContained(
                        _economyDataDirectory,
                        _commandDataDirectory)
                        ? _commandDataDirectory
                        : null);
                _faultInjector?.Invoke(EconomyContinuityFaultPoint.AfterEconomySideStateCopy);
                var commandExcludedSubtree = IsContained(
                    _commandDataDirectory,
                    _economyDataDirectory)
                    ? _economyDataDirectory
                    : null;
                var commandSourceBefore = CaptureStableSideState(
                    _commandDataDirectory,
                    excludeMainDatabase: PathsEqual(
                        _commandDataDirectory,
                        _economyDataDirectory),
                    excludedSubtree: commandExcludedSubtree);
                var sqliteEvidence = BackupSqlite(sourceDatabase, sqliteTarget);
                files.Add(FileEntry(
                    partialRoot,
                    sqliteTarget,
                    "authoritative-sqlite-online-backup"));
                _faultInjector?.Invoke(EconomyContinuityFaultPoint.AfterSqliteBackup);
                if (Directory.Exists(_commandDataDirectory) &&
                    !PathsEqual(_commandDataDirectory, _economyDataDirectory))
                {
                    CopyStableSideState(
                        _commandDataDirectory,
                        commandTarget,
                        files,
                        partialRoot,
                        excludeMainDatabase: false,
                        rolePrefix: "command-outbox-audit",
                        excludedSubtree: commandExcludedSubtree);
                }
                var archivedCommandRoot = PathsEqual(
                    _commandDataDirectory,
                    _economyDataDirectory)
                        ? economyTarget
                        : commandTarget;
                _commandSideStateArchive.CreateManifest(
                    archivedCommandRoot,
                    now,
                    _options.RetentionDays,
                    _options.MinimumRetainedBackups);
                files.Add(FileEntry(
                    partialRoot,
                    Path.Combine(
                        archivedCommandRoot,
                        CommandSideStateArchiveService.ManifestFileName),
                    CommandSideStateArchiveService.ManifestRole));
                _faultInjector?.Invoke(EconomyContinuityFaultPoint.AfterCommandSideStateCopy);
                var commandSourceAfter = CaptureStableSideState(
                    _commandDataDirectory,
                    excludeMainDatabase: PathsEqual(
                        _commandDataDirectory,
                        _economyDataDirectory),
                    excludedSubtree: commandExcludedSubtree);
                if (!StableSideStateSetsEqual(commandSourceBefore, commandSourceAfter))
                {
                    throw new IOException(
                        "Command/outbox side state changed across the SQLite backup boundary; retry after the queues drain.");
                }

                var commandReplay = ValidateCommandSideState(
                    PathsEqual(_commandDataDirectory, _economyDataDirectory)
                        ? economyTarget
                        : commandTarget);
                if (!commandReplay.ReplayValid || !commandReplay.IdempotencyValid)
                {
                    throw new InvalidDataException(
                        "Command/outbox side state cannot be replayed with stable idempotency mappings.");
                }
                var pendingTransactions = sqliteEvidence.PendingTransactions
                    .Concat(commandReplay.BlockingTransactions)
                    .DistinctBy(
                        item => $"{item.Kind}|{item.Id}|{item.State}",
                        StringComparer.Ordinal)
                    .OrderBy(item => item.UpdatedAt)
                    .ThenBy(item => item.Kind, StringComparer.Ordinal)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray();

                files = files.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToList();
                var contentHash = ComputeContentHash(
                    ManifestSchemaVersion,
                    serverId,
                    worldId,
                    now,
                    sqliteEvidence.LastSequence,
                    sqliteEvidence.UserVersion,
                    sqliteEvidence.WalLogFrames,
                    sqliteEvidence.WalCheckpointedFrames,
                    pendingTransactions,
                    files,
                    _options.RpoMinutes,
                    _options.TargetRtoMinutes,
                    idempotencyKeyHash);
                var manifest = new EconomySnapshotManifest(
                    ManifestSchemaVersion,
                    backupId,
                    serverId,
                    worldId.ToUpperInvariant(),
                    now,
                    sqliteEvidence.LastSequence,
                    sqliteEvidence.UserVersion,
                    sqliteEvidence.WalLogFrames,
                    sqliteEvidence.WalCheckpointedFrames,
                    pendingTransactions,
                    files,
                    contentHash,
                    _options.RpoMinutes,
                    _options.TargetRtoMinutes,
                    idempotencyKeyHash);
                WriteJsonDurably(Path.Combine(partialRoot, ManifestFileName), manifest);
                _faultInjector?.Invoke(EconomyContinuityFaultPoint.AfterManifestWrite);
                VerifySnapshotDirectory(partialRoot, manifest);
                Directory.Move(partialRoot, finalRoot);
                return manifest;
            }
            catch
            {
                TryDeleteOwnedPartial(serverRoot, partialRoot, backupId);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public EconomySnapshotManifest VerifySnapshot(string serverId, string backupId)
    {
        var root = GetBackupDirectory(serverId, backupId);
        var manifest = ReadManifest(root);
        VerifySnapshotDirectory(root, manifest);
        return manifest;
    }

    public async Task<EconomyStagingVerification> RestoreToStagingAsync(
        string serverId,
        string backupId,
        string expectedWorldId,
        CancellationToken cancellationToken)
    {
        ValidateWorldId(expectedWorldId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var backupRoot = GetBackupDirectory(serverId, backupId);
            var manifest = ReadManifest(backupRoot);
            VerifySnapshotDirectory(backupRoot, manifest);
            var serverStagingRoot = SafeChild(_stagingRoot, serverId);
            Directory.CreateDirectory(serverStagingRoot);
            var partial = SafeChild(serverStagingRoot, $".partial-{backupId}");
            var final = SafeChild(serverStagingRoot, backupId);
            if (Directory.Exists(partial))
            {
                TryDeleteOwnedPartial(serverStagingRoot, partial, backupId);
            }
            if (Directory.Exists(final))
            {
                return await RevalidatePublishedStagingAsync(
                    final,
                    manifest,
                    expectedWorldId,
                    cancellationToken);
            }
            Directory.CreateDirectory(partial);
            try
            {
                foreach (var file in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = SafeRelativeFile(backupRoot, file.RelativePath);
                    var destination = SafeRelativeFile(partial, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: false);
                    VerifyFile(destination, file);
                }
                _faultInjector?.Invoke(EconomyContinuityFaultPoint.AfterStagingFilesCopied);

                var stagedEconomy = Path.Combine(partial, "economy-state");
                var stagedDatabase = Path.Combine(stagedEconomy, "extraction-commerce.db");
                var stagedCommands = Directory.Exists(Path.Combine(partial, "command-state"))
                    ? Path.Combine(partial, "command-state")
                    : stagedEconomy;
                var integrityValid = VerifySqliteIntegrity(stagedDatabase);
                var schemaValid = false;
                var foreignKeysValid = false;
                var replayValid = false;
                var activeSeasonWorldValid = false;
                var ledgerProjectionValid = false;
                var blockingOrderCount = 0;
                IReadOnlyList<EconomyPendingTransaction> restoredPending = [];
                if (integrityValid)
                {
                    schemaValid = VerifyRequiredSqliteSchema(
                        stagedDatabase,
                        manifest.SqliteUserVersion);
                    foreignKeysValid = VerifyForeignKeys(stagedDatabase);
                    if (schemaValid && foreignKeysValid)
                    {
                        using var replay = new SqliteExtractionRepository(stagedEconomy);
                        replayValid = replay.IsReady;
                        var activeSeasons = (await replay.ListSeasonsAsync(
                                manifest.ServerId,
                                cancellationToken))
                            .Where(season => season.State == ExtractionSeasonState.Active)
                            .ToArray();
                        activeSeasonWorldValid = activeSeasons.Length == 1 && string.Equals(
                            activeSeasons[0].WorldId,
                            expectedWorldId,
                            StringComparison.OrdinalIgnoreCase);
                        blockingOrderCount = (await replay.ListBlockingOrdersAsync(cancellationToken)).Count;
                        ledgerProjectionValid = VerifyLedgerProjection(stagedDatabase);
                        using var pendingConnection = OpenReadOnly(stagedDatabase);
                        restoredPending = ReadPendingTransactions(pendingConnection);
                    }
                }
                var commandReplay = ValidateCommandSideState(stagedCommands);
                restoredPending = restoredPending
                    .Concat(commandReplay.BlockingTransactions)
                    .DistinctBy(
                        item => $"{item.Kind}|{item.Id}|{item.State}",
                        StringComparer.Ordinal)
                    .ToArray();
                var pendingMatchesManifest = PendingSetsEqual(
                    manifest.PendingTransactions,
                    restoredPending);
                ForceEconomyClosed(stagedEconomy);
                integrityValid = integrityValid && VerifySqliteIntegrity(stagedDatabase);
                schemaValid = schemaValid && VerifyRequiredSqliteSchema(
                    stagedDatabase,
                    manifest.SqliteUserVersion);
                foreignKeysValid = foreignKeysValid && VerifyForeignKeys(stagedDatabase);
                var economyForcedClosed = VerifyEconomyForcedClosed(stagedDatabase);
                var worldValid = string.Equals(
                    manifest.WorldId,
                    expectedWorldId,
                    StringComparison.OrdinalIgnoreCase) && activeSeasonWorldValid;
                var verification = new EconomyStagingVerification(
                    backupId,
                    final,
                    _timeProvider.GetUtcNow(),
                    HashesValid: true,
                    integrityValid,
                    replayValid,
                    worldValid,
                    expectedWorldId.ToUpperInvariant(),
                    manifest.WorldId,
                    manifest.PendingTransactions.Count,
                    manifest.ContentHash,
                    economyForcedClosed,
                    activeSeasonWorldValid,
                    ledgerProjectionValid,
                    blockingOrderCount,
                    schemaValid,
                    foreignKeysValid,
                    commandReplay.ReplayValid,
                    commandReplay.IdempotencyValid,
                    commandReplay.PendingCommandCount,
                    pendingMatchesManifest);
                WriteJsonDurably(Path.Combine(partial, VerificationFileName), verification);
                _faultInjector?.Invoke(EconomyContinuityFaultPoint.BeforeStagingPublish);
                Directory.Move(partial, final);
                return verification with { StagingDirectory = final };
            }
            catch
            {
                TryDeleteOwnedPartial(serverStagingRoot, partial, backupId);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<EconomyStagingVerification> RevalidatePublishedStagingAsync(
        string stagingRoot,
        EconomySnapshotManifest manifest,
        string expectedWorldId,
        CancellationToken cancellationToken)
    {
        var verificationPath = SafeChild(stagingRoot, VerificationFileName);
        if (!File.Exists(verificationPath))
        {
            throw new InvalidDataException(
                "The published staging directory has no durable verification record.");
        }
        EconomyStagingVerification recorded;
        using (var stream = File.OpenRead(verificationPath))
        {
            recorded = JsonSerializer.Deserialize<EconomyStagingVerification>(stream, JsonOptions)
                ?? throw new InvalidDataException("The staging verification record is invalid.");
        }
        if (!string.Equals(recorded.BackupId, manifest.BackupId, StringComparison.Ordinal) ||
            !string.Equals(recorded.ExpectedWorldId, expectedWorldId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(recorded.ManifestWorldId, manifest.WorldId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(recorded.ContentHash, manifest.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The published staging verification belongs to another backup or world.");
        }

        var stagedEconomy = Path.Combine(stagingRoot, "economy-state");
        var stagedDatabase = Path.Combine(stagedEconomy, "extraction-commerce.db");
        var stagedCommands = Directory.Exists(Path.Combine(stagingRoot, "command-state"))
            ? Path.Combine(stagingRoot, "command-state")
            : stagedEconomy;
        var integrityValid = VerifySqliteIntegrity(stagedDatabase);
        var schemaValid = integrityValid && VerifyRequiredSqliteSchema(
            stagedDatabase,
            manifest.SqliteUserVersion);
        var foreignKeysValid = schemaValid && VerifyForeignKeys(stagedDatabase);
        var replayValid = false;
        var activeSeasonWorldValid = false;
        var ledgerProjectionValid = false;
        var blockingOrderCount = 0;
        IReadOnlyList<EconomyPendingTransaction> pending = [];
        if (foreignKeysValid)
        {
            using var replay = new SqliteExtractionRepository(stagedEconomy);
            replayValid = replay.IsReady;
            var activeSeasons = (await replay.ListSeasonsAsync(
                    manifest.ServerId,
                    cancellationToken))
                .Where(season => season.State == ExtractionSeasonState.Active)
                .ToArray();
            activeSeasonWorldValid = activeSeasons.Length == 1 && string.Equals(
                activeSeasons[0].WorldId,
                expectedWorldId,
                StringComparison.OrdinalIgnoreCase);
            blockingOrderCount = (await replay.ListBlockingOrdersAsync(cancellationToken)).Count;
            ledgerProjectionValid = VerifyLedgerProjection(stagedDatabase);
            using var connection = OpenReadOnly(stagedDatabase);
            pending = ReadPendingTransactions(connection);
        }
        var commandReplay = ValidateCommandSideState(stagedCommands);
        pending = pending
            .Concat(commandReplay.BlockingTransactions)
            .DistinctBy(
                item => $"{item.Kind}|{item.Id}|{item.State}",
                StringComparer.Ordinal)
            .ToArray();
        var pendingMatches = PendingSetsEqual(manifest.PendingTransactions, pending);
        var forcedClosed = VerifyEconomyForcedClosed(stagedDatabase);
        if (!integrityValid || !schemaValid || !foreignKeysValid || !replayValid ||
            !activeSeasonWorldValid || !ledgerProjectionValid || blockingOrderCount != 0 ||
            !commandReplay.ReplayValid || !commandReplay.IdempotencyValid ||
            !pendingMatches || !forcedClosed ||
            recorded.PendingCommandCount != commandReplay.PendingCommandCount)
        {
            throw new InvalidDataException(
                "The existing staging restore no longer passes its published verification.");
        }
        return recorded with
        {
            StagingDirectory = stagingRoot,
            VerifiedAt = DateTimeOffset.UtcNow,
            HashesValid = true,
            SqliteIntegrityValid = integrityValid,
            EconomyReplayValid = replayValid,
            WorldIdValid = string.Equals(
                manifest.WorldId,
                expectedWorldId,
                StringComparison.OrdinalIgnoreCase),
            EconomyForcedClosed = forcedClosed,
            ActiveSeasonWorldValid = activeSeasonWorldValid,
            LedgerProjectionValid = ledgerProjectionValid,
            BlockingOrderCount = blockingOrderCount,
            SqliteSchemaValid = schemaValid,
            ForeignKeysValid = foreignKeysValid,
            CommandReplayValid = commandReplay.ReplayValid,
            CommandIdempotencyValid = commandReplay.IdempotencyValid,
            PendingCommandCount = commandReplay.PendingCommandCount,
            PendingStateMatchesManifest = pendingMatches
        };
    }

    public IReadOnlyList<EconomyPendingTransaction> ListPostSnapshotTransactions(
        string serverId,
        string backupId)
    {
        var manifest = VerifySnapshot(serverId, backupId);
        var currentDatabase = Path.Combine(_economyDataDirectory, "extraction-commerce.db");
        using var connection = OpenReadOnly(currentDatabase);
        var results = new List<EconomyPendingTransaction>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT sequence, event_type, occurred_at
                FROM extraction_events
                WHERE sequence > $lastSequence
                ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$lastSequence", manifest.LastEconomySequence);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new EconomyPendingTransaction(
                    "economy_event",
                    reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2))));
            }
        }
        results.AddRange(ReadPendingTransactions(connection));
        var commandReplay = ValidateCommandSideState(_commandDataDirectory);
        results.AddRange(commandReplay.BlockingTransactions);
        results.AddRange(commandReplay.Events.Where(item => item.UpdatedAt > manifest.CreatedAt));
        if (!commandReplay.ReplayValid || !commandReplay.IdempotencyValid)
        {
            results.Add(new EconomyPendingTransaction(
                "command_side_state",
                "replay-validation",
                commandReplay.IdempotencyValid ? "invalid_jsonl" : "idempotency_conflict",
                _timeProvider.GetUtcNow()));
        }
        return results
            .DistinctBy(item => $"{item.Kind}|{item.Id}|{item.State}", StringComparer.Ordinal)
            .OrderBy(item => item.UpdatedAt)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public EconomyCapacityPlan GetCapacityPlan()
    {
        var currentBytes = PathsEqual(_economyDataDirectory, _commandDataDirectory) ||
            IsContained(_economyDataDirectory, _commandDataDirectory)
                ? DirectorySize(_economyDataDirectory)
                : IsContained(_commandDataDirectory, _economyDataDirectory)
                    ? DirectorySize(_commandDataDirectory)
                    : DirectorySize(_economyDataDirectory) + DirectorySize(_commandDataDirectory);
        var backups = EnumerateBackupDirectories();
        var backupBytes = backups.Sum(item => item.Bytes);
        var expectedSingle = Math.Max(currentBytes, backups.Count == 0
            ? 0
            : (long)Math.Ceiling(backups.Average(item => (double)item.Bytes)));
        var estimatedRetained = checked((long)Math.Ceiling(
            expectedSingle * (double)_options.MinimumRetainedBackups *
            _options.CapacitySafetyPercent / 100d));
        var requiredFree = checked(Math.Max(
            _options.MinimumFreeSpaceBytes,
            expectedSingle * _options.CapacitySafetyPercent / 100));
        var drive = new DriveInfo(Path.GetPathRoot(_backupRoot)!);
        return new EconomyCapacityPlan(
            currentBytes,
            backupBytes,
            backups.Count,
            estimatedRetained,
            requiredFree,
            drive.AvailableFreeSpace,
            drive.AvailableFreeSpace >= requiredFree,
            _options.RetentionDays,
            _options.MinimumRetainedBackups,
            _options.CapacitySafetyPercent);
    }

    public IReadOnlyList<EconomyRetentionCandidate> PlanRetention(DateTimeOffset now)
    {
        var all = EnumerateBackupDirectories()
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
        return all
            .Skip(_options.MinimumRetainedBackups)
            .Where(item => item.CreatedAt < now.AddDays(-_options.RetentionDays))
            .Select(item => new EconomyRetentionCandidate(
                item.BackupId,
                item.CreatedAt,
                item.Bytes,
                "older_than_retention_window_and_above_minimum_count"))
            .ToArray();
    }

    private static SqliteBackupEvidence BackupSqlite(string sourcePath, string destinationPath)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        source.Open();
        destination.Open();
        var walLogFrames = 0;
        var walCheckpointedFrames = 0;
        using (var checkpoint = source.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            using var reader = checkpoint.ExecuteReader();
            if (reader.Read())
            {
                walLogFrames = reader.GetInt32(1);
                walCheckpointedFrames = reader.GetInt32(2);
            }
        }
        source.BackupDatabase(destination);
        using var versionCommand = destination.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var userVersion = Convert.ToInt32(versionCommand.ExecuteScalar());
        using var sequenceCommand = destination.CreateCommand();
        sequenceCommand.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM extraction_events;";
        var lastSequence = Convert.ToInt64(sequenceCommand.ExecuteScalar());
        if (!VerifySqliteIntegrity(destinationPath))
        {
            throw new InvalidDataException("The SQLite online backup failed integrity_check.");
        }
        return new SqliteBackupEvidence(
            userVersion,
            lastSequence,
            walLogFrames,
            walCheckpointedFrames,
            ReadPendingTransactions(destination));
    }

    private static IReadOnlyList<EconomyPendingTransaction> ReadPendingTransactions(
        SqliteConnection connection)
    {
        var results = new List<EconomyPendingTransaction>();
        if (TableExists(connection, "extraction_settlement_runs"))
        {
            using var runs = connection.CreateCommand();
            runs.CommandText = """
                SELECT run_id, state, updated_at
                FROM extraction_settlement_runs
                WHERE lower(state) NOT IN ('settled', 'failed', 'expired', 'cancelled')
                ORDER BY updated_at;
                """;
            using var reader = runs.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new EconomyPendingTransaction(
                    "resource_exchange",
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2))));
            }
        }
        if (TableExists(connection, "rollover_operations"))
        {
            using var rollovers = connection.CreateCommand();
            rollovers.CommandText = """
                SELECT operation_id, current_step, updated_at
                FROM rollover_operations
                WHERE current_step <> 'completed'
                ORDER BY updated_at;
                """;
            using var reader = rollovers.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new EconomyPendingTransaction(
                    "rollover",
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2))));
            }
        }
        if (TableExists(connection, "season_settlement_jobs"))
        {
            using var jobs = connection.CreateCommand();
            jobs.CommandText = """
                SELECT job_id, state, updated_at
                FROM season_settlement_jobs
                WHERE state <> 'completed'
                ORDER BY updated_at;
                """;
            using var reader = jobs.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new EconomyPendingTransaction(
                    "season_settlement_job",
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2))));
            }
        }
        if (TableExists(connection, "extraction_delivery_receipts"))
        {
            using var receipts = connection.CreateCommand();
            receipts.CommandText = """
                SELECT delivery_id,
                       CASE
                           WHEN receipt_json IS NULL THEN 'awaiting_receipt'
                           ELSE lower(json_extract(receipt_json, '$.outcome'))
                       END AS state,
                       COALESCE(finalized_at, created_at)
                FROM extraction_delivery_receipts
                WHERE receipt_json IS NULL
                   OR lower(json_extract(receipt_json, '$.outcome')) IN ('partial', 'uncertain')
                ORDER BY COALESCE(finalized_at, created_at);
                """;
            using var reader = receipts.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new EconomyPendingTransaction(
                    "delivery_receipt",
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2))));
            }
        }
        results.AddRange(ReadPalDefenderPendingTransactions(connection));
        if (TableExists(connection, "extraction_events"))
        {
            using var orders = connection.CreateCommand();
            orders.CommandText = """
                WITH latest_orders AS (
                    SELECT json_extract(payload, '$.order.orderId') AS order_id,
                           json_extract(payload, '$.order.state') AS state,
                           json_extract(payload, '$.order.updatedAt') AS updated_at,
                           ROW_NUMBER() OVER (
                               PARTITION BY json_extract(payload, '$.order.orderId')
                               ORDER BY sequence DESC) AS row_number
                    FROM extraction_events
                    WHERE json_type(payload, '$.order') = 'object'
                )
                SELECT order_id, state, updated_at
                FROM latest_orders
                WHERE row_number = 1
                  AND lower(state) IN (
                      'pendingdelivery', 'dispatching',
                      'deliveryfailed', 'deliveryuncertain')
                ORDER BY updated_at;
                """;
            using var reader = orders.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new EconomyPendingTransaction(
                    "shop_order",
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2))));
            }
        }
        return results;
    }

    private static IReadOnlyList<EconomyPendingTransaction>
        ReadPalDefenderPendingTransactions(SqliteConnection connection)
    {
        if (!TableExists(connection, "paldefender_commands"))
        {
            return [];
        }
        var results = new List<EconomyPendingTransaction>();
        using var commands = connection.CreateCommand();
        commands.CommandText = """
            SELECT command_id,
                   CASE
                       WHEN dead_lettered_at IS NOT NULL THEN 'dead_lettered'
                       ELSE state
                   END,
                   updated_at
            FROM paldefender_commands
            WHERE state IN ('accepted', 'dispatched', 'uncertain')
               OR dead_lettered_at IS NOT NULL
            ORDER BY updated_at;
            """;
        using var reader = commands.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EconomyPendingTransaction(
                "command_outbox",
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2))));
        }
        return results;
    }

    private static bool VerifyLedgerProjection(string databasePath)
    {
        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH balance_events AS (
                SELECT event.sequence,
                       json_extract(balance.value, '$.accountId') AS account_id,
                       json_extract(balance.value, '$.currency') AS currency,
                       COALESCE(json_extract(balance.value, '$.seasonId'), '') AS season_id,
                       CAST(json_extract(balance.value, '$.balance') AS INTEGER) AS balance,
                       ROW_NUMBER() OVER (
                           PARTITION BY json_extract(balance.value, '$.accountId'),
                                        json_extract(balance.value, '$.currency'),
                                        COALESCE(json_extract(balance.value, '$.seasonId'), '')
                           ORDER BY event.sequence DESC) AS row_number
                FROM extraction_events AS event,
                     json_each(event.payload, '$.balances') AS balance
                WHERE json_type(event.payload, '$.balances') = 'array'
            ),
            ledger_totals AS (
                SELECT json_extract(entry.value, '$.accountId') AS account_id,
                       json_extract(entry.value, '$.currency') AS currency,
                       COALESCE(json_extract(entry.value, '$.seasonId'), '') AS season_id,
                       SUM(CAST(json_extract(entry.value, '$.delta') AS INTEGER)) AS total
                FROM extraction_events AS event,
                     json_each(event.payload, '$.ledgerEntries') AS entry
                WHERE json_type(event.payload, '$.ledgerEntries') = 'array'
                GROUP BY account_id, currency, season_id
            )
            SELECT COUNT(*)
            FROM balance_events AS balance
            LEFT JOIN ledger_totals AS ledger
              ON ledger.account_id = balance.account_id
             AND ledger.currency = balance.currency
             AND ledger.season_id = balance.season_id
            WHERE balance.row_number = 1
              AND balance.balance <> COALESCE(ledger.total, 0);
            """;
        return Convert.ToInt64(command.ExecuteScalar()) == 0;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1;";
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool VerifySqliteIntegrity(string path)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }
        return rows.Count == 1 && string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase);
    }

    private static bool VerifyRequiredSqliteSchema(string path, int expectedUserVersion)
    {
        using var connection = OpenReadOnly(path);
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(version.ExecuteScalar()) != expectedUserVersion)
            {
                return false;
            }
        }
        var requiredCoreTables = new[]
        {
            "extraction_events",
            "extraction_settlement_runs",
            "extraction_run_credits",
            "player_identity_bindings",
            "player_identity_binding_history"
        };
        if (requiredCoreTables.Any(table => !TableExists(connection, table)))
        {
            return false;
        }
        using (var payloads = connection.CreateCommand())
        {
            payloads.CommandText = "SELECT COUNT(*) FROM extraction_events WHERE NOT json_valid(payload);";
            if (Convert.ToInt64(payloads.ExecuteScalar()) != 0)
            {
                return false;
            }
        }
        if (!TableExists(connection, "economy_schema_migrations"))
        {
            return true;
        }
        var migrationTables = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["admin-audit"] = ["admin_audit_events"],
            ["delivery-evidence"] = ["extraction_delivery_evidence"],
            ["delivery-receipt"] = ["extraction_delivery_receipts"],
            ["economy-gate-state"] = ["economy_gate_state"],
            ["economy-observability"] = ["economy_identity_conflicts"],
            ["paldefender-command-outbox"] =
                ["paldefender_commands", "paldefender_command_events", "paldefender_command_migrations"],
            ["weekly-rollover-state-machine"] = ["rollover_operations", "rollover_steps"],
            ["season-settlement-jobs"] = ["season_settlement_jobs", "season_settlement_items"],
            ["player-notifications"] = ["player_notifications", "player_notification_events"]
        };
        var components = new List<string>();
        using (var migrations = connection.CreateCommand())
        {
            migrations.CommandText = "SELECT component FROM economy_schema_migrations;";
            using var reader = migrations.ExecuteReader();
            while (reader.Read())
            {
                components.Add(reader.GetString(0));
            }
        }
        foreach (var component in components)
        {
            if (migrationTables.TryGetValue(component, out var required) &&
                required.Any(table => !TableExists(connection, table)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool VerifyForeignKeys(string path)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        return !reader.Read();
    }

    private static bool PendingSetsEqual(
        IEnumerable<EconomyPendingTransaction> expected,
        IEnumerable<EconomyPendingTransaction> actual)
    {
        static string Key(EconomyPendingTransaction item) =>
            $"{item.Kind}|{item.Id}|{item.State}|{item.UpdatedAt?.ToUniversalTime():O}";
        return expected.Select(Key).OrderBy(item => item, StringComparer.Ordinal).SequenceEqual(
            actual.Select(Key).OrderBy(item => item, StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void CopyStableSideState(
        string sourceRoot,
        string destinationRoot,
        ICollection<EconomySnapshotFile> files,
        string manifestRoot,
        bool excludeMainDatabase,
        string rolePrefix,
        string? excludedSubtree = null)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ResetOwnedDirectory(destinationRoot);
            var before = CaptureStableSideState(
                sourceRoot,
                excludeMainDatabase,
                excludedSubtree);
            var copied = new List<EconomySnapshotFile>(before.Count);
            var retry = false;
            foreach (var expected in before)
            {
                var source = SafeRelativeFile(sourceRoot, expected.RelativePath);
                var destination = SafeRelativeFile(destinationRoot, expected.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CopyStableFile(source, destination);
                if (new FileInfo(destination).Length != expected.Bytes ||
                    !string.Equals(HashFile(destination), expected.Sha256, StringComparison.Ordinal))
                {
                    retry = true;
                    break;
                }
                copied.Add(FileEntry(
                    manifestRoot,
                    destination,
                    $"{rolePrefix}:{ClassifySideState(Path.GetFileName(source))}"));
            }
            if (retry)
            {
                continue;
            }
            var after = CaptureStableSideState(
                sourceRoot,
                excludeMainDatabase,
                excludedSubtree);
            if (!StableSideStateSetsEqual(before, after))
            {
                continue;
            }
            foreach (var file in copied)
            {
                files.Add(file);
            }
            return;
        }
        ResetOwnedDirectory(destinationRoot);
        throw new IOException(
            $"Continuity side-state directory '{sourceRoot}' did not become stable as one file set.");
    }

    private static IReadOnlyList<StableSideStateFile> CaptureStableSideState(
        string sourceRoot,
        bool excludeMainDatabase,
        string? excludedSubtree)
    {
        var captured = new List<StableSideStateFile>();
        if (!Directory.Exists(sourceRoot))
        {
            return captured;
        }
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (excludedSubtree is not null &&
                (PathsEqual(source, excludedSubtree) || IsContained(excludedSubtree, source)))
            {
                continue;
            }
            var name = Path.GetFileName(source);
            if (ShouldSkipSideState(name, excludeMainDatabase))
            {
                continue;
            }
            var relative = Path.GetRelativePath(sourceRoot, source).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new InvalidDataException("A continuity source escaped its configured root.");
            }
            captured.Add(CaptureStableFile(source, relative));
        }
        return captured;
    }

    private static StableSideStateFile CaptureStableFile(string path, string relativePath)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var before = new FileInfo(path);
            if (!before.Exists)
            {
                break;
            }
            var bytes = before.Length;
            var lastWriteUtcTicks = before.LastWriteTimeUtc.Ticks;
            string hash;
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            var after = new FileInfo(path);
            if (after.Exists && after.Length == bytes &&
                after.LastWriteTimeUtc.Ticks == lastWriteUtcTicks)
            {
                return new StableSideStateFile(relativePath, bytes, lastWriteUtcTicks, hash);
            }
        }
        throw new IOException($"Continuity side-state file '{path}' did not become stable.");
    }

    private static bool StableSideStateSetsEqual(
        IReadOnlyList<StableSideStateFile> first,
        IReadOnlyList<StableSideStateFile> second) =>
        first.Count == second.Count && first.Zip(second).All(pair =>
            string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.Ordinal) &&
            pair.First.Bytes == pair.Second.Bytes &&
            pair.First.LastWriteUtcTicks == pair.Second.LastWriteUtcTicks &&
            string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal));

    private static bool ShouldSkipSideState(string name, bool excludeMainDatabase) =>
        name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(".partial-", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            name,
            CommandSideStateArchiveService.ManifestFileName,
            StringComparison.OrdinalIgnoreCase) ||
        (excludeMainDatabase &&
         string.Equals(name, "extraction-commerce.db", StringComparison.OrdinalIgnoreCase));

    private static void ResetOwnedDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        Directory.CreateDirectory(directory);
    }

    private static void CopyStableFile(string source, string destination)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var before = new FileInfo(source);
            var length = before.Length;
            var lastWrite = before.LastWriteTimeUtc;
            using (var input = new FileStream(
                       source,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(
                       destination,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                var remaining = length;
                var buffer = new byte[64 * 1024];
                while (remaining > 0)
                {
                    var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0)
                    {
                        break;
                    }
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
                output.Flush(flushToDisk: true);
                if (remaining != 0)
                {
                    File.Delete(destination);
                    continue;
                }
            }
            var after = new FileInfo(source);
            if (after.Length == length && after.LastWriteTimeUtc == lastWrite)
            {
                return;
            }
            File.Delete(destination);
        }
        throw new IOException($"Continuity side-state file '{source}' did not become stable.");
    }

    private static string ClassifySideState(string name) =>
        name.Contains("gate", StringComparison.OrdinalIgnoreCase) ? "gate" :
        name.Contains("scheduler", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("rotation", StringComparison.OrdinalIgnoreCase) ? "scheduler" :
        name.Contains("evidence", StringComparison.OrdinalIgnoreCase) ? "delivery-evidence" :
        name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? CommandSideStateArchiveService.ClassifyJsonLines(name) :
        "state";

    private static CommandSideStateVerification ValidateCommandSideState(string root)
    {
        if (!Directory.Exists(root))
        {
            return new CommandSideStateVerification(true, true, 0, [], []);
        }
        var replayValid = true;
        var idempotencyValid = true;
        var eventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idempotency = new Dictionary<string, (string EntityId, string RequestHash)>(
            StringComparer.Ordinal);
        var commands = new Dictionary<string, CommandProjection>(StringComparer.OrdinalIgnoreCase);
        var allEvents = new List<EconomyPendingTransaction>();
        foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                     .OrderBy(item => item, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var isCommandAudit = Path.GetFileName(path)
                .Contains("command", StringComparison.OrdinalIgnoreCase);
            if (!HasCompleteJsonLines(path))
            {
                replayValid = false;
                continue;
            }
            var lineNumber = 0;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        replayValid = false;
                        continue;
                    }
                    using var document = JsonDocument.Parse(line);
                    var item = document.RootElement;
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        replayValid = false;
                        continue;
                    }
                    var eventId = JsonString(item, "eventId");
                    if (eventId is not null &&
                        (!Guid.TryParse(eventId, out _) ||
                         !eventIds.Add($"{relative}|{eventId}")))
                    {
                        replayValid = false;
                    }
                    var commandId = JsonString(item, "commandId");
                    var eventType = JsonString(item, "eventType");
                    var serverId = JsonString(item, "serverId");
                    var state = JsonString(item, "state");
                    var atText = JsonString(item, "at") ?? JsonString(item, "updatedAt");
                    var hasTimestamp = DateTimeOffset.TryParse(atText, out var at);
                    if (isCommandAudit &&
                        (!Guid.TryParse(commandId, out _) || string.IsNullOrWhiteSpace(serverId) ||
                         string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(state) ||
                         !hasTimestamp || eventId is null))
                    {
                        replayValid = false;
                    }
                    if (commandId is not null && state is not null && hasTimestamp)
                    {
                        var commandKey = $"{relative}|{commandId}";
                        if (string.Equals(eventType, "accepted", StringComparison.Ordinal))
                        {
                            if (!string.Equals(state, "accepted", StringComparison.Ordinal))
                            {
                                replayValid = false;
                            }
                            commands.TryAdd(commandKey, new CommandProjection(
                                relative,
                                commandId,
                                state,
                                at,
                                IsRolloverBlockingCommandFile(relative)));
                        }
                        else if (!commands.TryGetValue(commandKey, out var current))
                        {
                            replayValid = false;
                        }
                        else
                        {
                            if (at < current.UpdatedAt || state is not (
                                    "accepted" or "dispatched" or "succeeded" or
                                    "failed" or "uncertain" or "cancelled"))
                            {
                                replayValid = false;
                            }
                            commands[commandKey] = current with
                            {
                                State = state,
                                UpdatedAt = at
                            };
                        }
                        allEvents.Add(new EconomyPendingTransaction(
                            $"command_event:{relative}",
                            $"{commandId}:{lineNumber}",
                            state,
                            at));
                    }

                    var idempotencyKey = JsonString(item, "idempotencyKey");
                    if (string.IsNullOrWhiteSpace(idempotencyKey))
                    {
                        continue;
                    }
                    var requestHash = JsonString(item, "requestHash");
                    var entityId = commandId ??
                        JsonString(item, "announcementId") ??
                        JsonString(item, "notificationId") ??
                        JsonString(item, "deliveryId");
                    if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(entityId) ||
                        requestHash is not { Length: 64 } || !requestHash.All(Uri.IsHexDigit))
                    {
                        idempotencyValid = false;
                        continue;
                    }
                    var scopedKey = $"{relative}|{serverId}|{idempotencyKey}";
                    if (idempotency.TryGetValue(scopedKey, out var existing) &&
                        (!string.Equals(existing.EntityId, entityId, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(existing.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase)))
                    {
                        idempotencyValid = false;
                    }
                    else
                    {
                        idempotency[scopedKey] = (entityId, requestHash);
                    }
                }
            }
            catch (JsonException)
            {
                replayValid = false;
            }
            catch (IOException)
            {
                replayValid = false;
            }
        }

        var pending = commands.Values
            .Where(item => item.State is "accepted" or "dispatched")
            .ToArray();
        var blocking = commands.Values
            .Where(item => item.RolloverBlocking &&
                item.State is "accepted" or "dispatched" or "uncertain")
            .Select(item => new EconomyPendingTransaction(
                $"command_outbox:{item.RelativePath}",
                item.CommandId,
                item.State,
                item.UpdatedAt))
            .ToArray();
        return new CommandSideStateVerification(
            replayValid,
            idempotencyValid,
            pending.Length,
            blocking,
            allEvents);
    }

    private static bool HasCompleteJsonLines(string path)
    {
        var info = new FileInfo(path);
        if (info.Length == 0)
        {
            return true;
        }
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = stream.Length - 1;
        return stream.ReadByte() == (byte)'\n';
    }

    private static string? JsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsRolloverBlockingCommandFile(string relativePath) =>
        relativePath.EndsWith("paldefender-command-audit.jsonl", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith("save-command-audit.jsonl", StringComparison.OrdinalIgnoreCase);

    private static EconomySnapshotFile FileEntry(string root, string path, string role)
    {
        var info = new FileInfo(path);
        return new EconomySnapshotFile(
            Path.GetRelativePath(root, path).Replace('\\', '/'),
            role,
            info.Length,
            HashFile(path));
    }

    private static void VerifySnapshotDirectory(string root, EconomySnapshotManifest manifest)
    {
        if (manifest.SchemaVersion is not (1 or ManifestSchemaVersion) ||
            !string.Equals(
                manifest.ContentHash,
                ComputeContentHash(
                    manifest.SchemaVersion,
                    manifest.ServerId,
                    manifest.WorldId,
                    manifest.CreatedAt,
                    manifest.LastEconomySequence,
                    manifest.SqliteUserVersion,
                    manifest.WalLogFrames,
                    manifest.WalCheckpointedFrames,
                    manifest.PendingTransactions,
                    manifest.Files,
                    manifest.RpoMinutes,
                    manifest.TargetRtoMinutes,
                    manifest.IdempotencyKeyHash),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The economy snapshot manifest hash is invalid.");
        }
        foreach (var file in manifest.Files)
        {
            VerifyFile(SafeRelativeFile(root, file.RelativePath), file);
        }
        var database = SafeRelativeFile(root, "economy-state/extraction-commerce.db");
        if (!VerifySqliteIntegrity(database))
        {
            throw new InvalidDataException("The economy snapshot SQLite database is corrupt.");
        }
        if (!VerifyRequiredSqliteSchema(database, manifest.SqliteUserVersion) ||
            !VerifyForeignKeys(database) || !VerifyLedgerProjection(database))
        {
            throw new InvalidDataException(
                "The economy snapshot failed schema, foreign-key, or ledger projection validation.");
        }
        var archiveManifestFiles = manifest.Files
            .Where(file => string.Equals(
                file.Role,
                CommandSideStateArchiveService.ManifestRole,
                StringComparison.Ordinal))
            .ToArray();
        if (archiveManifestFiles.Length > 1)
        {
            throw new InvalidDataException(
                "The economy snapshot contains multiple command side-state archive manifests.");
        }
        var commandRoot = archiveManifestFiles.Length == 1 &&
            archiveManifestFiles[0].RelativePath.StartsWith(
                "economy-state/",
                StringComparison.Ordinal)
                ? Path.Combine(root, "economy-state")
                : Directory.Exists(Path.Combine(root, "command-state"))
                    ? Path.Combine(root, "command-state")
                    : Path.Combine(root, "economy-state");
        if (archiveManifestFiles.Length == 1)
        {
            var expectedRelativePath = Path.GetRelativePath(
                    root,
                    Path.Combine(
                        commandRoot,
                        CommandSideStateArchiveService.ManifestFileName))
                .Replace('\\', '/');
            if (!string.Equals(
                    archiveManifestFiles[0].RelativePath,
                    expectedRelativePath,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The command side-state archive manifest is outside its frozen command-state root.");
            }
            _ = new CommandSideStateArchiveService().Verify(commandRoot);
        }
        var commandReplay = ValidateCommandSideState(commandRoot);
        if (!commandReplay.ReplayValid || !commandReplay.IdempotencyValid)
        {
            throw new InvalidDataException(
                "The economy snapshot command/outbox state cannot be replayed safely.");
        }
        using var connection = OpenReadOnly(database);
        var pending = ReadPendingTransactions(connection)
            .Concat(commandReplay.BlockingTransactions)
            .DistinctBy(
                item => $"{item.Kind}|{item.Id}|{item.State}",
                StringComparer.Ordinal)
            .ToArray();
        if (!PendingSetsEqual(manifest.PendingTransactions, pending))
        {
            throw new InvalidDataException(
                "The economy snapshot pending-transaction manifest does not match restored state.");
        }
    }

    private static void VerifyFile(string path, EconomySnapshotFile expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expected.Bytes ||
            !string.Equals(HashFile(path), expected.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Economy snapshot file '{expected.RelativePath}' failed SHA-256 verification.");
        }
    }

    private static string ComputeContentHash(
        int schemaVersion,
        string serverId,
        string worldId,
        DateTimeOffset createdAt,
        long lastSequence,
        int sqliteUserVersion,
        int walLogFrames,
        int walCheckpointedFrames,
        IEnumerable<EconomyPendingTransaction> pendingTransactions,
        IEnumerable<EconomySnapshotFile> files,
        int rpoMinutes,
        int targetRtoMinutes,
        string? idempotencyKeyHash)
    {
        if (schemaVersion == 1)
        {
            return ComputeLegacyContentHash(
                serverId,
                worldId,
                createdAt,
                lastSequence,
                files,
                idempotencyKeyHash);
        }
        if (schemaVersion != ManifestSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported economy snapshot schema version '{schemaVersion}'.");
        }
        var builder = new StringBuilder()
            .Append("economy-snapshot-v2\n")
            .Append(serverId).Append('\n')
            .Append(worldId.ToUpperInvariant()).Append('\n')
            .Append(createdAt.ToString("O")).Append('\n')
            .Append(lastSequence).Append('\n')
            .Append(sqliteUserVersion).Append('\n')
            .Append(walLogFrames).Append('\n')
            .Append(walCheckpointedFrames).Append('\n')
            .Append(rpoMinutes).Append('\n')
            .Append(targetRtoMinutes).Append('\n');
        if (idempotencyKeyHash is not null)
        {
            builder.Append("idempotency:").Append(idempotencyKeyHash).Append('\n');
        }
        foreach (var pending in pendingTransactions
                     .OrderBy(item => item.Kind, StringComparer.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal)
                     .ThenBy(item => item.State, StringComparer.Ordinal)
                     .ThenBy(item => item.UpdatedAt))
        {
            builder.Append("pending:")
                .Append(pending.Kind).Append('\n')
                .Append(pending.Id).Append('\n')
                .Append(pending.State).Append('\n')
                .Append(pending.UpdatedAt?.ToUniversalTime().ToString("O") ?? string.Empty)
                .Append('\n');
        }
        foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(file.RelativePath).Append('\n')
                .Append(file.Role).Append('\n')
                .Append(file.Bytes).Append('\n')
                .Append(file.Sha256).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static string ComputeLegacyContentHash(
        string serverId,
        string worldId,
        DateTimeOffset createdAt,
        long lastSequence,
        IEnumerable<EconomySnapshotFile> files,
        string? idempotencyKeyHash)
    {
        var builder = new StringBuilder()
            .Append("economy-snapshot-v1\n")
            .Append(serverId).Append('\n')
            .Append(worldId.ToUpperInvariant()).Append('\n')
            .Append(createdAt.ToString("O")).Append('\n')
            .Append(lastSequence).Append('\n');
        if (idempotencyKeyHash is not null)
        {
            builder.Append("idempotency:").Append(idempotencyKeyHash).Append('\n');
        }
        foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(file.RelativePath).Append('\n')
                .Append(file.Role).Append('\n')
                .Append(file.Bytes).Append('\n')
                .Append(file.Sha256).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void ForceEconomyClosed(string stagedEconomyDirectory)
    {
        var now = DateTimeOffset.UtcNow;
        WriteJsonDurably(
            Path.Combine(stagedEconomyDirectory, "extraction-operation-gate.json"),
            new ExtractionOperationGateState(
                true,
                "恢复快照待 worldId、账本、未决交易与运行依赖复核",
                "system-restore-staging",
                now));
        var circuit = new EconomyCircuitState(
            false,
            "restored_snapshot_revalidation_required",
            "system-restore-staging",
            now);
        WriteJsonDurably(
            Path.Combine(stagedEconomyDirectory, "economy-safety-gate.json"),
            new EconomySafetyGateState(circuit, circuit));
        ForceEconomyClosedInDatabase(stagedEconomyDirectory, now, circuit);
    }

    private static bool VerifyEconomyForcedClosed(string databasePath)
    {
        using var connection = OpenReadOnly(databasePath);
        if (!TableExists(connection, "economy_gate_state"))
        {
            return false;
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE((
                    SELECT json_extract(state_json, '$.maintenance')
                    FROM economy_gate_state WHERE gate_key = 'operation'), 0),
                COALESCE((
                    SELECT json_extract(state_json, '$.purchase.writesEnabled')
                    FROM economy_gate_state WHERE gate_key = 'safety'), 1),
                COALESCE((
                    SELECT json_extract(state_json, '$.resourceExchange.writesEnabled')
                    FROM economy_gate_state WHERE gate_key = 'safety'), 1);
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt64(0) == 1 &&
            reader.GetInt64(1) == 0 && reader.GetInt64(2) == 0;
    }

    private static void ForceEconomyClosedInDatabase(
        string stagedEconomyDirectory,
        DateTimeOffset now,
        EconomyCircuitState circuit)
    {
        var database = Path.Combine(stagedEconomyDirectory, "extraction-commerce.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS economy_schema_migrations (
                component TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                applied_at TEXT NOT NULL,
                PRIMARY KEY (component, version)
            );
            CREATE TABLE IF NOT EXISTS economy_gate_state (
                gate_key TEXT PRIMARY KEY CHECK (gate_key IN ('operation', 'safety')),
                state_json TEXT NOT NULL CHECK (json_valid(state_json)),
                revision INTEGER NOT NULL CHECK (revision > 0),
                updated_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('economy-gate-state', 1, $updatedAt);
            INSERT INTO economy_gate_state (gate_key, state_json, revision, updated_at)
            VALUES ('operation', $operation, 1, $updatedAt)
            ON CONFLICT(gate_key) DO UPDATE SET
                state_json = excluded.state_json,
                revision = economy_gate_state.revision + 1,
                updated_at = excluded.updated_at;
            INSERT INTO economy_gate_state (gate_key, state_json, revision, updated_at)
            VALUES ('safety', $safety, 1, $updatedAt)
            ON CONFLICT(gate_key) DO UPDATE SET
                state_json = excluded.state_json,
                revision = economy_gate_state.revision + 1,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue(
            "$operation",
            JsonSerializer.Serialize(
                new ExtractionOperationGateState(
                    true,
                    "Restored snapshot requires world, ledger, pending-transaction and dependency revalidation",
                    "system-restore-staging",
                    now),
                JsonOptions));
        command.Parameters.AddWithValue(
            "$safety",
            JsonSerializer.Serialize(new EconomySafetyGateState(circuit, circuit), JsonOptions));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private EconomySnapshotManifest ReadManifest(string root)
    {
        var path = Path.Combine(root, ManifestFileName);
        var manifest = JsonSerializer.Deserialize<EconomySnapshotManifest>(
            File.ReadAllBytes(path),
            JsonOptions);
        return manifest ?? throw new InvalidDataException("The economy snapshot manifest is invalid.");
    }

    private string GetBackupDirectory(string serverId, string backupId)
    {
        ValidateSafeSegment(serverId, nameof(serverId));
        ValidateBackupId(backupId);
        var serverRoot = SafeChild(_backupRoot, serverId);
        var backupRoot = SafeChild(serverRoot, backupId);
        if (!Directory.Exists(backupRoot))
        {
            throw new DirectoryNotFoundException($"Economy backup '{backupId}' does not exist.");
        }
        return backupRoot;
    }

    private List<(string BackupId, DateTimeOffset CreatedAt, long Bytes)> EnumerateBackupDirectories()
    {
        var results = new List<(string, DateTimeOffset, long)>();
        foreach (var manifestPath in Directory.EnumerateFiles(
                     _backupRoot,
                     ManifestFileName,
                     SearchOption.AllDirectories))
        {
            try
            {
                var root = Path.GetDirectoryName(manifestPath)!;
                var manifest = ReadManifest(root);
                results.Add((manifest.BackupId, manifest.CreatedAt, DirectorySize(root)));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Corrupt/unreadable backups are deliberately not retention candidates.
            }
        }
        return results;
    }

    private static void WriteJsonDurably<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static long DirectorySize(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .Sum(file => new FileInfo(file).Length)
        : 0;

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ResolvePath(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));

    private static string SafeChild(string root, string child)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, child));
        if (!IsContained(root, candidate))
        {
            throw new InvalidDataException("A continuity path escaped its configured root.");
        }
        return candidate;
    }

    private static string SafeRelativeFile(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Snapshot manifest paths must be relative.");
        }
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(root, candidate))
        {
            throw new InvalidDataException("A snapshot manifest path escaped its root.");
        }
        return candidate;
    }

    private static bool PathsOverlap(string first, string second) =>
        PathsEqual(first, second) || IsContained(first, second) || IsContained(second, first);

    private static bool PathsEqual(string first, string second) => string.Equals(
        NormalizePath(first),
        NormalizePath(second),
        PathComparison());

    private static bool IsContained(string root, string candidate) =>
        NormalizePath(candidate).StartsWith(
            NormalizePath(root) + Path.DirectorySeparatorChar,
            PathComparison());

    private static string NormalizePath(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void ValidateSafeSegment(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value is "." or ".." || value.Length > 64 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Value must be one safe path segment.", name);
        }
    }

    private static void ValidateWorldId(string worldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldId);
        if (worldId.Length != 32 || !worldId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("worldId must be the complete 32-character hexadecimal world GUID.", nameof(worldId));
        }
    }

    private static void ValidateBackupId(string backupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        var parts = backupId.Split('-', 2);
        if (parts.Length != 2 || parts[0].Length != 19 || !Guid.TryParseExact(parts[1], "N", out _))
        {
            throw new ArgumentException("The economy backup identifier is invalid.", nameof(backupId));
        }
    }

    private static string? NormalizeIdempotencyKeyHash(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }
        var value = idempotencyKey.Trim();
        if (value.Length is < 16 or > 128 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Snapshot idempotency keys must contain 16-128 non-control characters.",
                nameof(idempotencyKey));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static void TryDeleteOwnedPartial(string parent, string partial, string backupId)
    {
        if (Directory.Exists(partial) &&
            IsContained(parent, partial) &&
            string.Equals(Path.GetFileName(partial), $".partial-{backupId}", StringComparison.Ordinal))
        {
            Directory.Delete(partial, recursive: true);
        }
    }

    private sealed record SqliteBackupEvidence(
        int UserVersion,
        long LastSequence,
        int WalLogFrames,
        int WalCheckpointedFrames,
        IReadOnlyList<EconomyPendingTransaction> PendingTransactions);

    private sealed record StableSideStateFile(
        string RelativePath,
        long Bytes,
        long LastWriteUtcTicks,
        string Sha256);

    private sealed record CommandProjection(
        string RelativePath,
        string CommandId,
        string State,
        DateTimeOffset UpdatedAt,
        bool RolloverBlocking);

    private sealed record CommandSideStateVerification(
        bool ReplayValid,
        bool IdempotencyValid,
        int PendingCommandCount,
        IReadOnlyList<EconomyPendingTransaction> BlockingTransactions,
        IReadOnlyList<EconomyPendingTransaction> Events);
}

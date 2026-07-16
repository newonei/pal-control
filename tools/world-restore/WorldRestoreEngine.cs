using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PalControl.WorldRestore;

internal sealed partial class WorldRestoreEngine
{
    private const string PlanReportType = "pal-control-world-restore-plan";
    private const string ResultReportType = "pal-control-world-restore-result";
    private const string FailureReportType = "pal-control-world-restore-failure";
    private const string JournalReportType = "pal-control-world-restore-journal";
    private const string ApprovalType = "pal-control-world-restore-approval";
    private const string SignatureAlgorithm = "ecdsa-p256-sha256";
    private const int RestoreSchemaVersion = 3;
    private static readonly TimeSpan MaximumApprovalLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(1);

    private static readonly string[] ManifestProperties =
    [
        "schemaVersion", "backupId", "serverId", "label", "worldGuid", "gameVersion",
        "createdAt", "actor", "reason", "integrity", "consistency", "files"
    ];

    private static readonly string[] ManifestFileProperties =
    [
        "relativePath", "length", "lastModifiedAt", "sha256"
    ];

    private static readonly string[] VerificationProperties =
    [
        "schemaVersion", "backupId", "integrity", "verifiedAt", "manifestSha256"
    ];

    private static readonly string[] TrustStoreProperties = ["schemaVersion", "keys"];

    private static readonly string[] TrustStoreKeyProperties =
        ["subject", "algorithm", "publicKeyPem"];

    private static readonly string[] PlanChecks =
    [
        "activeWorldIdentityMatched", "backupInventoryExact", "backupManifestAnchored",
        "backupPathsSafe", "evidenceSnapshotSameVolume", "exclusiveRestoreLockHeld",
        "localVolumeBound", "originalWorldInventoryFrozen", "palServerStoppedAtPlan",
        "serverIdentityMatched", "stagingInventoryExact", "trustStoreExternallyPinned",
        "worldIdentityMatched"
    ];

    private static readonly string[] PalServerProcessNames =
    [
        "PalServer", "PalServer-Win64-Shipping-Cmd", "PalServer-Win64-Shipping"
    ];

    private const string P256CurveOid = "1.2.840.10045.3.1.7";

    public CommandResult CreatePlan(
        string backupDirectory,
        string activeWorldDirectory,
        string serverId,
        string worldGuid,
        string settingsFile,
        string palServerExecutable,
        string evidenceDirectory,
        string approverTrustStoreSha256)
    {
        ValidateServerId(serverId);
        ValidateExternalTrustStorePin(approverTrustStoreSha256);
        var backupRoot = PathSafety.FullPath(backupDirectory, "backup directory");
        var activeRoot = PathSafety.EnsureLocalVolume(activeWorldDirectory, "active world directory");
        var settingsPath = PathSafety.FullPath(settingsFile, "settings file");
        var executablePath = PathSafety.FullPath(palServerExecutable, "PalServer executable");
        var evidenceRoot = PathSafety.FullPath(evidenceDirectory, "evidence directory");

        PathSafety.EnsureDirectory(activeRoot, create: false);
        PathSafety.EnsureRegularFile(settingsPath, "GameUserSettings.ini");
        PathSafety.EnsureRegularFile(executablePath, "PalServer executable");
        PathSafety.EnsureDirectory(evidenceRoot, create: true);
        PathSafety.EnsureNoOverlap(backupRoot, activeRoot, "backup and active world");
        PathSafety.EnsureNoOverlap(evidenceRoot, activeRoot, "evidence and active world");
        PathSafety.EnsureNoOverlap(evidenceRoot, backupRoot, "evidence and backup");
        PathSafety.EnsureSameVolume(activeRoot, evidenceRoot, "active world and restore evidence");

        using var operationLock = RestoreOperationLock.Acquire(activeRoot, "plan");
        HoldFixtureLockIfRequested(activeRoot);
        var planProcessGate = AssertExpectedPalServerStopped(executablePath);

        var backup = VerifyManagedBackup(backupRoot, serverId, worldGuid);
        ValidateActiveWorldIdentity(
            activeRoot,
            settingsPath,
            executablePath,
            worldGuid,
            backup.Manifest.WorldGuid);
        var originalCapture = CaptureInventory(activeRoot);

        var operationId = Guid.NewGuid().ToString("N");
        var activeParent = Path.GetDirectoryName(activeRoot)
            ?? throw new InvalidDataException("Active world directory has no parent.");
        PathSafety.EnsureDirectory(activeParent, create: false);
        var stage = Path.GetFullPath(Path.Combine(
            activeParent,
            $".palcontrol-world-restore-stage-{operationId}"));
        PathSafety.EnsureStrictChild(activeParent, stage, "restore staging directory");
        PathSafety.EnsureSameVolume(activeRoot, stage, "active world and staging");
        if (Directory.Exists(stage) || File.Exists(stage))
        {
            throw new InvalidOperationException("Unique restore staging path already exists.");
        }

        CopyManagedDataToNewDirectory(backup, stage);
        var stagedInventory = PathSafety.Inventory(
            stage,
            requireNonEmpty: true,
            PathSafety.ExpectedDirectories(backup.Inventory.Select(item => item.RelativePath)));
        PathSafety.AssertInventoriesEqual(
            backup.Inventory,
            stagedInventory,
            "staged managed backup");

        // Re-read the source trust chain after staging so a concurrent edit can
        // never be hidden by a copy that happened to finish first.
        var verifiedAgain = VerifyManagedBackup(backupRoot, serverId, worldGuid);
        if (!string.Equals(
                verifiedAgain.ManifestSha256,
                backup.ManifestSha256,
                StringComparison.Ordinal) ||
            !InventoriesEqual(verifiedAgain.Inventory, backup.Inventory))
        {
            throw new InvalidDataException("Managed backup changed while it was being staged.");
        }
        var originalAfterStaging = CaptureInventory(activeRoot);
        if (originalAfterStaging.Summary != originalCapture.Summary)
        {
            throw new InvalidDataException(
                "Active world changed while the stopped-world restore plan was being created.");
        }
        planProcessGate = AssertExpectedPalServerStopped(executablePath);

        var candidateSummary = SummarizeInventory(backup.Inventory);
        var installationRoot = Path.GetDirectoryName(executablePath)!;
        var checks = PlanChecks.ToDictionary(item => item, _ => true, StringComparer.Ordinal);
        var report = new RestorePlanReport(
            RestoreSchemaVersion,
            PlanReportType,
            operationId,
            DateTimeOffset.UtcNow,
            "plan-only",
            serverId,
            worldGuid,
            backup.Manifest.BackupId,
            backup.ManifestSha256,
            backupRoot,
            activeRoot,
            settingsPath,
            executablePath,
            installationRoot,
            stage,
            operationLock.Path,
            evidenceRoot,
            approverTrustStoreSha256,
            backup.Inventory.Count,
            checked(backup.Inventory.Sum(item => item.Length)),
            originalCapture.Summary,
            candidateSummary,
            planProcessGate,
            checks);
        var reportName = $"world-restore-{operationId}-plan.json";
        var reportHash = CanonicalJson.WriteEvidence(evidenceRoot, reportName, report);
        var reportPath = Path.Combine(evidenceRoot, reportName);
        return new CommandResult(
            "verified",
            "plan-only",
            false,
            reportPath,
            reportHash,
            reportPath,
            reportHash,
            stage,
            activeRoot,
            null,
            null,
            null,
            null);
    }

    public CommandResult Apply(
        string planFile,
        bool execute,
        IReadOnlyList<string> approvalFiles,
        string? trustStoreFile,
        string? externallyPublishedTrustStoreSha256)
    {
        var planPath = PathSafety.FullPath(planFile, "restore plan");
        var plan = CanonicalJson.ReadCanonical<RestorePlanReport>(planPath, 1_048_576);
        var planHash = CanonicalJson.Sha256File(planPath);
        CanonicalJson.VerifyHashSidecar(planPath, planHash);
        ValidatePlan(plan, planPath);
        if (execute)
        {
            if (string.IsNullOrWhiteSpace(externallyPublishedTrustStoreSha256))
            {
                throw new InvalidDataException(
                    "Execution requires the externally published trust-store SHA-256 pin.");
            }
            ValidatePlanTrustStorePin(plan, externallyPublishedTrustStoreSha256);
        }

        using var operationLock = RestoreOperationLock.Acquire(plan.ActiveWorldDirectory, "apply");
        if (!PathSafety.PathsEqual(operationLock.Path, plan.LockFile))
        {
            throw new InvalidDataException("Restore plan is bound to another exclusive lock file.");
        }
        HoldFixtureLockIfRequested(plan.ActiveWorldDirectory);
        var journalPath = GetJournalPath(plan);
        if (File.Exists(journalPath))
        {
            var prior = ReadAndValidateJournal(journalPath, plan, planHash);
            if (!execute || prior.State == "committed")
            {
                ValidateCommittedOrPendingTopology(prior);
                return JournalCommandResult(planPath, planHash, journalPath, prior);
            }
            throw new InvalidOperationException(
                "An unfinished restore journal requires the dedicated recover command and " +
                "two new recovery-purpose approvals; apply will not move directories after a process crash.");
        }

        var backup = VerifyManagedBackup(
            plan.BackupDirectory,
            plan.ServerId,
            plan.WorldGuid);
        if (!string.Equals(backup.Manifest.BackupId, plan.BackupId, StringComparison.Ordinal) ||
            !string.Equals(backup.ManifestSha256, plan.ManifestSha256, StringComparison.Ordinal) ||
            backup.Inventory.Count != plan.FileCount ||
            checked(backup.Inventory.Sum(item => item.Length)) != plan.TotalBytes)
        {
            throw new InvalidDataException("Current managed backup no longer matches the restore plan.");
        }
        ValidateActiveWorldIdentity(
            plan.ActiveWorldDirectory,
            plan.SettingsFile,
            plan.PalServerExecutable,
            plan.WorldGuid,
            backup.Manifest.WorldGuid);
        var activeAtApply = CaptureInventory(plan.ActiveWorldDirectory);
        if (activeAtApply.Summary != plan.OriginalInventory)
        {
            throw new InvalidDataException(
                "Active world no longer matches the complete stopped-world inventory frozen in the plan.");
        }
        var stageInventory = PathSafety.Inventory(
            plan.StagingDirectory,
            requireNonEmpty: true,
            PathSafety.ExpectedDirectories(backup.Inventory.Select(item => item.RelativePath)));
        PathSafety.AssertInventoriesEqual(backup.Inventory, stageInventory, "restore staging directory");

        if (!execute)
        {
            return new CommandResult(
                "verified",
                "plan-only",
                false,
                planPath,
                planHash,
                planPath,
                planHash,
                plan.StagingDirectory,
                plan.ActiveWorldDirectory,
                null,
                null,
                null,
                null);
        }

        if (approvalFiles.Count != 2)
        {
            throw new InvalidDataException("Execution requires exactly two signed approval files.");
        }
        if (string.IsNullOrWhiteSpace(trustStoreFile))
        {
            throw new InvalidDataException("Execution requires an approver trust store.");
        }
        var executionTrustStorePin = externallyPublishedTrustStoreSha256
            ?? throw new InvalidDataException(
                "Execution requires the externally published trust-store SHA-256 pin.");
        var approvalSet = VerifyApprovals(
            approvalFiles,
            trustStoreFile,
            executionTrustStorePin,
            plan,
            planHash,
            DateTimeOffset.UtcNow);
        ValidateApprovalArtifactLocations(approvalSet, plan);
        var processGates = new List<ProcessGateEvidence>
        {
            AssertExpectedPalServerStopped(plan.PalServerExecutable)
        };
        ValidateTestFaultConfiguration(plan);

        var activeParent = Path.GetDirectoryName(plan.ActiveWorldDirectory)
            ?? throw new InvalidDataException("Active world directory has no parent.");
        var rollback = Path.Combine(
            activeParent,
            $".palcontrol-world-rollback-{plan.OperationId}");
        var retired = Path.Combine(
            activeParent,
            $".palcontrol-world-retired-{plan.OperationId}");
        var failedCandidate = Path.Combine(
            activeParent,
            $".palcontrol-world-failed-{plan.OperationId}");
        foreach (var reserved in new[] { rollback, retired, failedCandidate })
        {
            PathSafety.EnsureStrictChild(activeParent, reserved, "restore preservation path");
            if (Directory.Exists(reserved) || File.Exists(reserved))
            {
                throw new InvalidOperationException($"Restore preservation path already exists: {reserved}");
            }
        }

        var originalCapture = CaptureInventory(plan.ActiveWorldDirectory);
        if (originalCapture.Summary != plan.OriginalInventory)
        {
            throw new InvalidDataException(
                "Active world changed after plan approval and before rollback capture.");
        }
        var originalInventory = originalCapture.Files;
        var originalDirectories = originalCapture.Directories;
        var candidateDirectories = PathSafety.ExpectedDirectories(
                backup.Inventory.Select(item => item.RelativePath))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var originalSummary = originalCapture.Summary;
        CopyInventoryToNewDirectory(
            plan.ActiveWorldDirectory,
            rollback,
            originalInventory,
            originalDirectories);
        var rollbackInventory = PathSafety.Inventory(
            rollback,
            requireNonEmpty: true,
            originalDirectories);
        PathSafety.AssertInventoriesEqual(
            originalInventory,
            rollbackInventory,
            "cold rollback copy");
        var sourceAfterRollback = PathSafety.Inventory(
            plan.ActiveWorldDirectory,
            requireNonEmpty: true,
            originalDirectories);
        PathSafety.AssertInventoriesEqual(
            originalInventory,
            sourceAfterRollback,
            "active world after cold rollback copy");
        if (SummarizeInventory(sourceAfterRollback, originalDirectories) != plan.OriginalInventory)
        {
            throw new InvalidDataException("Active world inventory diverged from the approved plan.");
        }

        // All mutable gates are repeated immediately before the first rename.
        processGates.Add(AssertExpectedPalServerStopped(plan.PalServerExecutable));
        ValidateActiveWorldIdentity(
            plan.ActiveWorldDirectory,
            plan.SettingsFile,
            plan.PalServerExecutable,
            plan.WorldGuid,
            backup.Manifest.WorldGuid);
        var backupImmediatelyBeforeSwitch = VerifyManagedBackup(
            plan.BackupDirectory,
            plan.ServerId,
            plan.WorldGuid);
        if (!string.Equals(
                backupImmediatelyBeforeSwitch.ManifestSha256,
                plan.ManifestSha256,
                StringComparison.Ordinal) ||
            !InventoriesEqual(backupImmediatelyBeforeSwitch.Inventory, backup.Inventory))
        {
            throw new InvalidDataException("Managed backup changed before the atomic switch.");
        }
        var stageImmediatelyBeforeSwitch = PathSafety.Inventory(
            plan.StagingDirectory,
            requireNonEmpty: true,
            PathSafety.ExpectedDirectories(backup.Inventory.Select(item => item.RelativePath)));
        PathSafety.AssertInventoriesEqual(
            backup.Inventory,
            stageImmediatelyBeforeSwitch,
            "staging directory before atomic switch");
        approvalSet = VerifyApprovals(
            approvalFiles,
            trustStoreFile,
            executionTrustStorePin,
            plan,
            planHash,
            DateTimeOffset.UtcNow);
        ValidateApprovalArtifactLocations(approvalSet, plan);
        approvalSet = PersistExecutionAuthorizationSnapshot(
            approvalSet,
            plan,
            planHash,
            executionTrustStorePin);
        var authorizationSnapshotDirectory = GetAuthorizationSnapshotDirectory(plan);
        var activeBeforeJournal = CaptureInventory(plan.ActiveWorldDirectory);
        if (activeBeforeJournal.Summary != plan.OriginalInventory)
        {
            throw new InvalidDataException(
                "Active world changed after authorization and before durable journal preparation.");
        }
        var evidenceRoot = Path.GetDirectoryName(planPath)!;
        var journal = new RestoreJournal(
            RestoreSchemaVersion,
            JournalReportType,
            plan.OperationId,
            DateTimeOffset.UtcNow,
            "prepared",
            "pending",
            plan.ServerId,
            plan.WorldGuid,
            plan.BackupId,
            plan.ManifestSha256,
            planHash,
            plan.InstallationRoot,
            plan.ActiveWorldDirectory,
            plan.StagingDirectory,
            rollback,
            retired,
            failedCandidate,
            plan.BackupDirectory,
            evidenceRoot,
            authorizationSnapshotDirectory,
            approvalSet.TrustStoreFile,
            approvalSet.TrustStoreSha256,
            approvalSet.Evidence,
            string.Empty,
            string.Empty,
            string.Empty,
            Array.Empty<ApprovalEvidence>(),
            originalInventory,
            originalDirectories,
            backup.Inventory,
            candidateDirectories,
            originalSummary,
            plan.CandidateInventory,
            processGates.ToArray(),
            string.Empty,
            string.Empty);
        var journalHash = WriteJournal(journalPath, journal);
        PauseFixtureIfRequested("prepared", plan);
        InjectFixtureCrashIfRequested("prepared", plan);

        string? resultName = null;
        string? resultHash = null;
        try
        {
            // This is the last mutable stop gate before retiring the old world.
            processGates.Add(AssertExpectedPalServerStopped(plan.PalServerExecutable));
            AssertDirectoryInventory(
                plan.ActiveWorldDirectory,
                originalInventory,
                originalDirectories,
                "approved original world at final retirement move");
            Directory.Move(plan.ActiveWorldDirectory, retired);
            var retiredAfterMove = PathSafety.Inventory(
                retired,
                requireNonEmpty: true,
                originalDirectories);
            PathSafety.AssertInventoriesEqual(
                originalInventory,
                retiredAfterMove,
                "retired original world");
            journal = journal with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                State = "old-retired",
                ProcessGates = processGates.ToArray()
            };
            journalHash = WriteJournal(journalPath, journal);
            PauseFixtureIfRequested("old-retired", plan);
            InjectFixtureFaultIfRequested("after-active-move", plan);
            InjectFixtureCrashIfRequested("old-retired", plan);

            // A separate final check closes the race before the final Directory.Move.
            processGates.Add(AssertExpectedPalServerStopped(plan.PalServerExecutable));
            var stageAtFinalMove = PathSafety.Inventory(
                plan.StagingDirectory,
                requireNonEmpty: true,
                PathSafety.ExpectedDirectories(backup.Inventory.Select(item => item.RelativePath)));
            PathSafety.AssertInventoriesEqual(
                backup.Inventory,
                stageAtFinalMove,
                "staging directory at final move");
            Directory.Move(plan.StagingDirectory, plan.ActiveWorldDirectory);
            journal = journal with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                State = "candidate-active",
                ProcessGates = processGates.ToArray()
            };
            journalHash = WriteJournal(journalPath, journal);
            PauseFixtureIfRequested("candidate-active", plan);
            InjectFixtureFaultIfRequested("after-stage-move", plan);
            InjectFixtureCrashIfRequested("candidate-active", plan);
            var activeAfterSwitch = PathSafety.Inventory(
                plan.ActiveWorldDirectory,
                requireNonEmpty: true,
                PathSafety.ExpectedDirectories(backup.Inventory.Select(item => item.RelativePath)));
            PathSafety.AssertInventoriesEqual(
                backup.Inventory,
                activeAfterSwitch,
                "active world after switch");

            var rollbackAfterSwitch = PathSafety.Inventory(
                rollback,
                requireNonEmpty: true,
                originalDirectories);
            PathSafety.AssertInventoriesEqual(
                originalInventory,
                rollbackAfterSwitch,
                "cold rollback after switch");
            var retiredAfterSwitch = PathSafety.Inventory(
                retired,
                requireNonEmpty: true,
                originalDirectories);
            PathSafety.AssertInventoriesEqual(
                originalInventory,
                retiredAfterSwitch,
                "retired original world after switch");

            var result = new RestoreResultReport(
                RestoreSchemaVersion,
                ResultReportType,
                plan.OperationId,
                DateTimeOffset.UtcNow,
                "executed",
                plan.ServerId,
                plan.WorldGuid,
                plan.BackupId,
                plan.ManifestSha256,
                planHash,
                plan.ActiveWorldDirectory,
                rollback,
                retired,
                plan.BackupDirectory,
                "committed",
                journalPath,
                journalHash,
                authorizationSnapshotDirectory,
                approvalSet.TrustStoreFile,
                approvalSet.TrustStoreSha256,
                approvalSet.Evidence,
                SummarizeInventory(activeAfterSwitch, candidateDirectories),
                SummarizeInventory(rollbackAfterSwitch, originalDirectories),
                SummarizeInventory(retiredAfterSwitch, originalDirectories),
                processGates.ToArray(),
                new SortedDictionary<string, bool>(StringComparer.Ordinal)
                {
                    ["activeWorldMatchesBackup"] = true,
                    ["backupPreserved"] = Directory.Exists(plan.BackupDirectory),
                    ["coldRollbackCopyVerified"] = true,
                    ["oldWorldRetained"] = Directory.Exists(retired),
                    ["palServerStoppedAtSwitch"] = true,
                    ["twoDistinctApprovalsVerified"] = true
                });
            if (result.Checks.Values.Any(value => !value))
            {
                throw new InvalidOperationException("Post-switch preservation evidence is incomplete.");
            }
            resultName = $"world-restore-{plan.OperationId}-result.json";
            resultHash = CanonicalJson.WriteEvidence(evidenceRoot, resultName, result);
            PauseFixtureIfRequested("result-published", plan);
            journal = journal with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                State = "committed",
                Outcome = "restored",
                ProcessGates = processGates.ToArray(),
                ResultFile = Path.Combine(evidenceRoot, resultName),
                ResultSha256 = resultHash
            };
            WriteJournal(journalPath, journal);
        }
        catch (Exception switchException)
        {
            Exception? recoveryException = null;
            var recovered = false;
            try
            {
                journal = ReadAndValidateJournal(journalPath, plan, planHash);
                journal = RecoverJournalLocked(
                    journalPath,
                    journal,
                    plan,
                    allowInProcessAutomaticRecovery: true);
                processGates = journal.ProcessGates.ToList();
                recovered = journal.State == "committed" && journal.Outcome == "recovered";
            }
            catch (Exception exception)
            {
                recoveryException = exception;
            }
            TryWriteFailureEvidence(
                journalPath,
                journal,
                recovered,
                switchException,
                processGates);
            if (!recovered)
            {
                throw new InvalidOperationException(
                    $"World switch failed and automatic old-world recovery failed: {recoveryException?.Message}",
                    switchException);
            }
            throw new InvalidOperationException(
                "World switch failed; the original active world was automatically restored and verified.",
                switchException);
        }

        if (resultName is null || resultHash is null)
        {
            throw new InvalidOperationException("World restore completed without durable result evidence.");
        }
        return new CommandResult(
            "completed",
            "executed",
            true,
            Path.Combine(evidenceRoot, resultName),
            resultHash,
            planPath,
            planHash,
            null,
            plan.ActiveWorldDirectory,
            rollback,
            retired,
            null,
            null,
            journalPath,
            "committed",
            "restored");
    }

    public CommandResult Status(string planFile)
    {
        var planPath = PathSafety.FullPath(planFile, "restore plan");
        var plan = CanonicalJson.ReadCanonical<RestorePlanReport>(planPath, 1_048_576);
        var planHash = CanonicalJson.Sha256File(planPath);
        CanonicalJson.VerifyHashSidecar(planPath, planHash);
        ValidatePlan(plan, planPath);
        using var operationLock = RestoreOperationLock.AcquireReadOnly(plan.ActiveWorldDirectory);
        if (!PathSafety.PathsEqual(operationLock.Path, plan.LockFile))
        {
            throw new InvalidDataException("Restore plan is bound to another exclusive lock file.");
        }
        var journalPath = GetJournalPath(plan);
        if (!File.Exists(journalPath))
        {
            return new CommandResult(
                "not-started", "journal-status", false, planPath, planHash,
                planPath, planHash, plan.StagingDirectory, plan.ActiveWorldDirectory,
                null, null, null, null, journalPath, null, null);
        }
        var journal = ReadAndValidateJournal(journalPath, plan, planHash);
        ValidateCommittedOrPendingTopology(journal);
        return JournalCommandResult(planPath, planHash, journalPath, journal);
    }

    public CommandResult Recover(
        string planFile,
        string externallyPublishedTrustStoreSha256,
        IReadOnlyList<string> recoveryApprovalFiles)
    {
        var planPath = PathSafety.FullPath(planFile, "restore plan");
        var plan = CanonicalJson.ReadCanonical<RestorePlanReport>(planPath, 1_048_576);
        var planHash = CanonicalJson.Sha256File(planPath);
        CanonicalJson.VerifyHashSidecar(planPath, planHash);
        ValidatePlan(plan, planPath);
        ValidatePlanTrustStorePin(plan, externallyPublishedTrustStoreSha256);
        if (recoveryApprovalFiles.Count != 2)
        {
            throw new InvalidDataException(
                "Manual recovery requires exactly two current recovery-purpose approvals.");
        }
        using var operationLock = RestoreOperationLock.Acquire(plan.ActiveWorldDirectory, "recover");
        if (!PathSafety.PathsEqual(operationLock.Path, plan.LockFile))
        {
            throw new InvalidDataException("Restore plan is bound to another exclusive lock file.");
        }
        var journalPath = GetJournalPath(plan);
        if (!File.Exists(journalPath))
        {
            throw new InvalidOperationException("No durable restore journal exists for this plan.");
        }
        var journal = ReadAndValidateJournal(journalPath, plan, planHash);
        ValidateCommittedOrPendingTopology(journal);
        if (journal.State == "committed")
        {
            throw new InvalidOperationException(
                "Restore journal is already committed; use the read-only status command.");
        }
        var baseJournalHash = CanonicalJson.Sha256File(journalPath);
        var recoveryApprovals = VerifyRecoveryApprovals(
            recoveryApprovalFiles,
            externallyPublishedTrustStoreSha256,
            plan,
            planHash,
            journal,
            baseJournalHash,
            DateTimeOffset.UtcNow);
        ValidateApprovalArtifactLocations(recoveryApprovals, plan);
        recoveryApprovals = PersistRecoveryAuthorizationSnapshot(
            recoveryApprovals,
            plan,
            planHash,
            externallyPublishedTrustStoreSha256,
            journal,
            baseJournalHash);
        if (!string.Equals(
                CanonicalJson.Sha256File(journalPath),
                baseJournalHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Restore journal changed while recovery approvals were being snapshotted.");
        }
        journal = journal with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            RecoveryAuthorizationBaseJournalSha256 = baseJournalHash,
            RecoveryAuthorizationJournalState = journal.State,
            RecoveryAuthorizationJournalOutcome = journal.Outcome,
            RecoveryApprovals = recoveryApprovals.Evidence
        };
        WriteJournal(journalPath, journal);
        PauseFixtureIfRequested("recovery-authorized", plan);
        journal = ReadAndValidateJournal(journalPath, plan, planHash);
        journal = RecoverJournalLocked(
            journalPath,
            journal,
            plan,
            allowInProcessAutomaticRecovery: false);
        return JournalCommandResult(planPath, planHash, journalPath, journal);
    }

    public CommandResult CreateApproval(
        string planFile,
        string subject,
        string reason,
        string privateKeyFile,
        string outputFile,
        string externallyPublishedTrustStoreSha256,
        int validForMinutes)
    {
        ValidateSubject(subject);
        ValidateApprovalReasonAndLifetime(reason, validForMinutes);
        var planPath = PathSafety.FullPath(planFile, "restore plan");
        var plan = CanonicalJson.ReadCanonical<RestorePlanReport>(planPath, 1_048_576);
        var planHash = CanonicalJson.Sha256File(planPath);
        CanonicalJson.VerifyHashSidecar(planPath, planHash);
        ValidatePlan(plan, planPath);
        ValidatePlanTrustStorePin(plan, externallyPublishedTrustStoreSha256);
        var keyPath = PathSafety.FullPath(privateKeyFile, "approver private key");
        PathSafety.EnsureRegularFile(keyPath, "approver private key");
        var outputPath = PathSafety.FullPath(outputFile, "approval output");
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidDataException("Approval output has no parent directory.");
        PathSafety.EnsureDirectory(outputDirectory, create: true);
        if (File.Exists(outputPath) || Directory.Exists(outputPath) || File.Exists(outputPath + ".sha256"))
        {
            throw new InvalidOperationException("Approval output already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var payload = new ApprovalPayload(
            RestoreSchemaVersion,
            ApprovalType,
            Guid.NewGuid().ToString("N"),
            subject,
            reason.Trim(),
            now,
            now.AddMinutes(validForMinutes),
            plan.OperationId,
            plan.ServerId,
            plan.WorldGuid,
            plan.BackupId,
            plan.ManifestSha256,
            planHash,
            plan.ApproverTrustStoreSha256,
            "execute",
            string.Empty,
            string.Empty,
            string.Empty,
            plan.OriginalInventory,
            plan.CandidateInventory);
        byte[] signature;
        using (var key = ECDsa.Create())
        {
            key.ImportFromPem(File.ReadAllText(keyPath, Encoding.UTF8));
            EnsureP256(key, "Approver private key");
            signature = key.SignData(
                CanonicalJson.Serialize(payload),
                HashAlgorithmName.SHA256);
        }
        var approval = new SignedApproval(
            payload.SchemaVersion,
            payload.ApprovalType,
            payload.ApprovalId,
            payload.Subject,
            payload.Reason,
            payload.IssuedAt,
            payload.ExpiresAt,
            payload.OperationId,
            payload.ServerId,
            payload.WorldGuid,
            payload.BackupId,
            payload.ManifestSha256,
            payload.PlanSha256,
            payload.TrustStoreSha256,
            payload.Purpose,
            payload.JournalSha256,
            payload.JournalState,
            payload.JournalOutcome,
            payload.OriginalInventory,
            payload.CandidateInventory,
            SignatureAlgorithm,
            Convert.ToBase64String(signature));
        var hash = CanonicalJson.WriteEvidence(
            outputDirectory,
            Path.GetFileName(outputPath),
            approval);
        return new CommandResult(
            "created",
            "approval",
            false,
            outputPath,
            hash,
            planPath,
            planHash,
            null,
            null,
            null,
            null,
            outputPath,
            subject);
    }

    public CommandResult CreateRecoveryApproval(
        string planFile,
        string subject,
        string reason,
        string privateKeyFile,
        string outputFile,
        string externallyPublishedTrustStoreSha256,
        int validForMinutes)
    {
        ValidateSubject(subject);
        ValidateApprovalReasonAndLifetime(reason, validForMinutes);
        var planPath = PathSafety.FullPath(planFile, "restore plan");
        var plan = CanonicalJson.ReadCanonical<RestorePlanReport>(planPath, 1_048_576);
        var planHash = CanonicalJson.Sha256File(planPath);
        CanonicalJson.VerifyHashSidecar(planPath, planHash);
        ValidatePlan(plan, planPath);
        ValidatePlanTrustStorePin(plan, externallyPublishedTrustStoreSha256);

        using var operationLock = RestoreOperationLock.Acquire(
            plan.ActiveWorldDirectory,
            "approve-recovery");
        if (!PathSafety.PathsEqual(operationLock.Path, plan.LockFile))
        {
            throw new InvalidDataException("Restore plan is bound to another exclusive lock file.");
        }
        var journalPath = GetJournalPath(plan);
        if (!File.Exists(journalPath))
        {
            throw new InvalidOperationException("No durable restore journal exists for recovery approval.");
        }
        var journal = ReadAndValidateJournal(journalPath, plan, planHash);
        ValidateCommittedOrPendingTopology(journal);
        if (journal.State == "committed")
        {
            throw new InvalidOperationException("Committed restore journals do not require recovery approval.");
        }
        var journalHash = CanonicalJson.Sha256File(journalPath);
        if (journal.OriginalInventorySummary != plan.OriginalInventory ||
            journal.CandidateInventorySummary != plan.CandidateInventory)
        {
            throw new InvalidDataException("Recovery journal inventories do not match the approved plan.");
        }

        var keyPath = PathSafety.FullPath(privateKeyFile, "recovery approver private key");
        PathSafety.EnsureRegularFile(keyPath, "recovery approver private key");
        var outputPath = PathSafety.FullPath(outputFile, "recovery approval output");
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidDataException("Recovery approval output has no parent directory.");
        PathSafety.EnsureDirectory(outputDirectory, create: true);
        if (File.Exists(outputPath) || Directory.Exists(outputPath) || File.Exists(outputPath + ".sha256"))
        {
            throw new InvalidOperationException("Recovery approval output already exists.");
        }

        var now = GetRecoveryApprovalIssuanceTime(plan);
        var payload = new ApprovalPayload(
            RestoreSchemaVersion,
            ApprovalType,
            Guid.NewGuid().ToString("N"),
            subject,
            reason.Trim(),
            now,
            now.AddMinutes(validForMinutes),
            plan.OperationId,
            plan.ServerId,
            plan.WorldGuid,
            plan.BackupId,
            plan.ManifestSha256,
            planHash,
            plan.ApproverTrustStoreSha256,
            "recover",
            journalHash,
            journal.State,
            journal.Outcome,
            journal.OriginalInventorySummary,
            journal.CandidateInventorySummary);
        byte[] signature;
        using (var key = ECDsa.Create())
        {
            key.ImportFromPem(File.ReadAllText(keyPath, Encoding.UTF8));
            EnsureP256(key, "Recovery approver private key");
            signature = key.SignData(
                CanonicalJson.Serialize(payload),
                HashAlgorithmName.SHA256);
        }
        var approval = new SignedApproval(
            payload.SchemaVersion,
            payload.ApprovalType,
            payload.ApprovalId,
            payload.Subject,
            payload.Reason,
            payload.IssuedAt,
            payload.ExpiresAt,
            payload.OperationId,
            payload.ServerId,
            payload.WorldGuid,
            payload.BackupId,
            payload.ManifestSha256,
            payload.PlanSha256,
            payload.TrustStoreSha256,
            payload.Purpose,
            payload.JournalSha256,
            payload.JournalState,
            payload.JournalOutcome,
            payload.OriginalInventory,
            payload.CandidateInventory,
            SignatureAlgorithm,
            Convert.ToBase64String(signature));
        var hash = CanonicalJson.WriteEvidence(
            outputDirectory,
            Path.GetFileName(outputPath),
            approval);
        return new CommandResult(
            "created",
            "recovery-approval",
            false,
            outputPath,
            hash,
            planPath,
            planHash,
            null,
            plan.ActiveWorldDirectory,
            null,
            null,
            outputPath,
            subject,
            journalPath,
            journal.State,
            journal.Outcome);
    }

    public CommandResult GenerateKeyPair(
        string privateKeyFile,
        string publicKeyFile)
    {
        var privatePath = PathSafety.FullPath(privateKeyFile, "private key output");
        var publicPath = PathSafety.FullPath(publicKeyFile, "public key output");
        if (PathSafety.PathsEqual(privatePath, publicPath))
        {
            throw new InvalidDataException("Private and public key outputs must differ.");
        }
        foreach (var path in new[] { privatePath, publicPath })
        {
            var parent = Path.GetDirectoryName(path)
                ?? throw new InvalidDataException("Key output path has no parent.");
            PathSafety.EnsureDirectory(parent, create: true);
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new InvalidOperationException($"Key output already exists: {path}");
            }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CanonicalJson.WriteNewDurable(
            privatePath,
            Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));
        CanonicalJson.WriteNewDurable(
            publicPath,
            Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem()));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                privatePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return new CommandResult(
            "created",
            "keygen",
            false,
            publicPath,
            CanonicalJson.Sha256File(publicPath),
            null,
            null,
            null,
            null,
            null,
            null,
            privatePath,
            null);
    }

    private static VerifiedBackup VerifyManagedBackup(
        string backupDirectory,
        string expectedServerId,
        string expectedWorldGuid)
    {
        var root = PathSafety.FullPath(backupDirectory, "managed backup directory");
        PathSafety.EnsureDirectory(root, create: false);
        var rootEntries = new DirectoryInfo(root).EnumerateFileSystemInfos().ToArray();
        foreach (var entry in rootEntries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Managed backup root contains a reparse point.");
            }
        }
        var actualRootShape = rootEntries
            .Select(item => $"{(item is DirectoryInfo ? "D" : "F")}:{item.Name}")
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedRootShape = new[] { "D:data", "F:manifest.json", "F:verification.json" };
        if (!actualRootShape.SequenceEqual(expectedRootShape, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Managed backup root must contain only data, manifest.json, and verification.json.");
        }

        var dataRoot = Path.Combine(root, "data");
        var manifestPath = Path.Combine(root, "manifest.json");
        var verificationPath = Path.Combine(root, "verification.json");
        PathSafety.EnsureDirectory(dataRoot, create: false);
        PathSafety.EnsureRegularFile(manifestPath, "managed backup manifest");
        PathSafety.EnsureRegularFile(verificationPath, "managed backup verification anchor");
        var manifestBytes = ReadBoundedFile(manifestPath, 64L * 1024 * 1024);
        var verificationBytes = ReadBoundedFile(verificationPath, 1_048_576);
        ValidateJsonShape(manifestBytes, ManifestProperties, ManifestFileProperties, "manifest");
        ValidateJsonShape(verificationBytes, VerificationProperties, null, "verification");

        ManagedBackupManifest manifest;
        BackupVerification verification;
        try
        {
            manifest = JsonSerializer.Deserialize<ManagedBackupManifest>(
                           manifestBytes,
                           CanonicalJson.SerializerOptions)
                       ?? throw new InvalidDataException("Managed backup manifest is null.");
            verification = JsonSerializer.Deserialize<BackupVerification>(
                               verificationBytes,
                               CanonicalJson.SerializerOptions)
                           ?? throw new InvalidDataException("Managed backup verification is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Managed backup metadata is invalid JSON.", exception);
        }

        var manifestHash = CanonicalJson.Sha256(manifestBytes);
        if (manifest.SchemaVersion != 1 || verification.SchemaVersion != 1 ||
            !Guid.TryParseExact(manifest.BackupId, "N", out _) ||
            !string.Equals(manifest.BackupId, Path.GetFileName(root), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(verification.BackupId, manifest.BackupId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.ServerId, expectedServerId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Integrity, "verified", StringComparison.Ordinal) ||
            !string.Equals(manifest.Consistency, "stable", StringComparison.Ordinal) ||
            !string.Equals(verification.Integrity, "verified", StringComparison.Ordinal) ||
            !IsSha256(manifestHash) ||
            !string.Equals(verification.ManifestSha256, manifestHash, StringComparison.OrdinalIgnoreCase) ||
            manifest.CreatedAt == default || verification.VerifiedAt == default ||
            manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > 1_000_000 ||
            string.IsNullOrWhiteSpace(manifest.Label) ||
            string.IsNullOrWhiteSpace(manifest.GameVersion) ||
            string.IsNullOrWhiteSpace(manifest.Actor) ||
            string.IsNullOrWhiteSpace(manifest.Reason))
        {
            throw new InvalidDataException("Managed backup manifest or verification anchor is ineligible.");
        }
        if (!WorldGuidsEqual(manifest.WorldGuid, expectedWorldGuid))
        {
            throw new InvalidDataException("Managed backup belongs to another world GUID.");
        }

        var expected = new List<FileInventoryEntry>(manifest.Files.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        foreach (var file in manifest.Files)
        {
            var relative = PathSafety.ValidateManifestRelativePath(file.RelativePath);
            if (!seenPaths.Add(relative) || file.Length < 0 ||
                file.LastModifiedAt == default || !IsSha256(file.Sha256) ||
                !string.Equals(file.Sha256, file.Sha256.ToLowerInvariant(), StringComparison.Ordinal) ||
                (previousPath is not null &&
                 string.Compare(previousPath, relative, StringComparison.Ordinal) >= 0))
            {
                throw new InvalidDataException("Managed backup manifest file inventory is invalid or ambiguous.");
            }
            previousPath = relative;
            expected.Add(new FileInventoryEntry(relative, file.Length, file.Sha256));
        }
        var actual = PathSafety.Inventory(
            dataRoot,
            requireNonEmpty: true,
            PathSafety.ExpectedDirectories(expected.Select(item => item.RelativePath)));
        PathSafety.AssertInventoriesEqual(expected, actual, "managed backup data");
        return new VerifiedBackup(
            root,
            dataRoot,
            manifestPath,
            manifest,
            manifestHash,
            actual);
    }

    private static void ValidateJsonShape(
        byte[] bytes,
        IReadOnlyCollection<string> expectedRootProperties,
        IReadOnlyCollection<string>? expectedFileProperties,
        string description)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            AssertExactProperties(document.RootElement, expectedRootProperties, description);
            if (expectedFileProperties is not null)
            {
                if (!document.RootElement.TryGetProperty("files", out var files) ||
                    files.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Manifest files must be an array.");
                }
                foreach (var file in files.EnumerateArray())
                {
                    AssertExactProperties(file, expectedFileProperties, "manifest file");
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{description} JSON shape is invalid.", exception);
        }
    }

    private static void ValidateTrustStoreJsonShape(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            AssertExactProperties(
                document.RootElement,
                TrustStoreProperties,
                "approver trust store");
            if (!document.RootElement.TryGetProperty("keys", out var keys) ||
                keys.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Approver trust-store keys must be an array.");
            }
            foreach (var key in keys.EnumerateArray())
            {
                AssertExactProperties(key, TrustStoreKeyProperties, "approver trust-store key");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Approver trust-store JSON shape is invalid.", exception);
        }
    }

    private static void AssertExactProperties(
        JsonElement element,
        IReadOnlyCollection<string> expected,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{description} must be a JSON object.");
        }
        var actual = element.EnumerateObject().Select(item => item.Name).ToArray();
        if (actual.Length != expected.Count ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            throw new InvalidDataException($"{description} contains missing, duplicate, or unknown fields.");
        }
    }

    private static void ValidateActiveWorldIdentity(
        string activeWorldDirectory,
        string settingsFile,
        string palServerExecutable,
        string expectedWorldGuid,
        string manifestWorldGuid)
    {
        var active = PathSafety.EnsureLocalVolume(activeWorldDirectory, "active world directory");
        var settings = PathSafety.FullPath(settingsFile, "GameUserSettings.ini");
        var executable = PathSafety.FullPath(palServerExecutable, "PalServer executable");
        PathSafety.EnsureDirectory(active, create: false);
        PathSafety.EnsureRegularFile(settings, "GameUserSettings.ini");
        PathSafety.EnsureRegularFile(executable, "PalServer executable");
        if (!string.Equals(Path.GetFileName(executable), "PalServer.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Configured executable must be named PalServer.exe.");
        }
        var installRoot = Path.GetDirectoryName(executable)!;
        PathSafety.EnsureLocalVolume(installRoot, "PalServer installation root");
        var expectedSettings = Path.Combine(
            installRoot,
            "Pal",
            "Saved",
            "Config",
            "WindowsServer",
            "GameUserSettings.ini");
        if (!PathSafety.PathsEqual(settings, expectedSettings))
        {
            throw new InvalidDataException("GameUserSettings.ini is outside the configured PalServer installation.");
        }
        var dedicatedServerName = ReadDedicatedServerName(settings);
        var expectedActive = Path.Combine(
            installRoot,
            "Pal",
            "Saved",
            "SaveGames",
            "0",
            dedicatedServerName);
        if (!PathSafety.PathsEqual(active, expectedActive) ||
            !string.Equals(Path.GetFileName(active), dedicatedServerName, StringComparison.OrdinalIgnoreCase) ||
            !WorldGuidsEqual(dedicatedServerName, expectedWorldGuid) ||
            !WorldGuidsEqual(dedicatedServerName, manifestWorldGuid))
        {
            throw new InvalidDataException(
                "DedicatedServerName, active directory, requested world GUID, and backup world GUID do not match.");
        }
    }

    private static string ReadDedicatedServerName(string settingsFile)
    {
        var values = new List<string>();
        foreach (var rawLine in File.ReadLines(settingsFile, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0 || !string.Equals(
                    line[..separator].Trim(),
                    "DedicatedServerName",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            values.Add(line[(separator + 1)..].Trim().Trim('"'));
        }
        if (values.Count != 1)
        {
            throw new InvalidDataException("GameUserSettings.ini must contain exactly one DedicatedServerName.");
        }
        PathSafety.EnsureSafeLeafName(values[0], "DedicatedServerName");
        return values[0];
    }

    private static void ValidatePlan(RestorePlanReport plan, string planPath)
    {
        if (plan.SchemaVersion != RestoreSchemaVersion || plan.ReportType != PlanReportType ||
            plan.Mode != "plan-only" || !Guid.TryParseExact(plan.OperationId, "N", out _) ||
            !Guid.TryParseExact(plan.BackupId, "N", out _) ||
            plan.CreatedAt == default || plan.FileCount <= 0 || plan.TotalBytes < 0 ||
            !IsSha256(plan.ManifestSha256) ||
            !IsSha256(plan.ApproverTrustStoreSha256) ||
            plan.OriginalInventory is null ||
            !ValidateInventorySummary(plan.OriginalInventory) ||
            plan.CandidateInventory is null ||
            !ValidateInventorySummary(plan.CandidateInventory) ||
            plan.CandidateInventory.FileCount != plan.FileCount ||
            plan.CandidateInventory.TotalBytes != plan.TotalBytes ||
            plan.PlanProcessGate is null ||
            plan.PlanProcessGate.CheckedAt == default ||
            plan.PlanProcessGate.MatchingProcesses != 0 ||
            plan.PlanProcessGate.InaccessibleProcesses != 0 ||
            plan.PlanProcessGate.ProcessNames is null ||
            !plan.PlanProcessGate.ProcessNames.SequenceEqual(PalServerProcessNames) ||
            plan.Checks is null || plan.Checks.Count != PlanChecks.Length ||
            !plan.Checks.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(PlanChecks) ||
            plan.Checks.Values.Any(value => !value))
        {
            throw new InvalidDataException("Restore plan metadata is invalid.");
        }
        ValidateServerId(plan.ServerId);
        foreach (var path in new[]
                 {
                     plan.BackupDirectory, plan.ActiveWorldDirectory, plan.SettingsFile,
                     plan.PalServerExecutable, plan.InstallationRoot, plan.StagingDirectory,
                     plan.LockFile, plan.EvidenceDirectory
                 })
        {
            if (!string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Restore plan contains a non-canonical absolute path.");
            }
        }
        var activeParent = Path.GetDirectoryName(plan.ActiveWorldDirectory)!;
        PathSafety.EnsureLocalVolume(plan.ActiveWorldDirectory, "planned active world directory");
        PathSafety.EnsureSameVolume(
            plan.ActiveWorldDirectory,
            plan.StagingDirectory,
            "planned active world and staging");
        PathSafety.EnsureSameVolume(
            plan.ActiveWorldDirectory,
            plan.EvidenceDirectory,
            "planned active world and evidence snapshots");
        var expectedInstallationRoot = Path.GetDirectoryName(plan.PalServerExecutable)!;
        if (!PathSafety.PathsEqual(expectedInstallationRoot, plan.InstallationRoot))
        {
            throw new InvalidDataException("Restore plan installation root does not match PalServer.exe.");
        }
        if (!PathSafety.PathsEqual(plan.PlanProcessGate.InstallationRoot, plan.InstallationRoot))
        {
            throw new InvalidDataException("Restore plan process gate is bound to another installation root.");
        }
        var expectedStage = Path.Combine(
            activeParent,
            $".palcontrol-world-restore-stage-{plan.OperationId}");
        if (!PathSafety.PathsEqual(expectedStage, plan.StagingDirectory))
        {
            throw new InvalidDataException("Restore plan staging path is not the operation-owned sibling path.");
        }
        if (!PathSafety.PathsEqual(
                RestoreOperationLock.GetPath(plan.ActiveWorldDirectory),
                plan.LockFile))
        {
            throw new InvalidDataException("Restore plan lock path is not bound to the active world.");
        }
        if (!PathSafety.PathsEqual(Path.GetDirectoryName(planPath)!, plan.EvidenceDirectory))
        {
            throw new InvalidDataException("Restore plan is not located in its bound evidence directory.");
        }
        PathSafety.EnsureNoOverlap(plan.BackupDirectory, plan.ActiveWorldDirectory, "backup and active world");
        PathSafety.EnsureNoOverlap(
            Path.GetDirectoryName(planPath)!,
            plan.ActiveWorldDirectory,
            "plan evidence and active world");
    }

    private static void ValidateExternalTrustStorePin(string pin)
    {
        if (!IsSha256(pin))
        {
            throw new InvalidDataException(
                "The externally published trust-store SHA-256 pin must be exactly 64 lowercase hex characters.");
        }
    }

    private static void ValidatePlanTrustStorePin(RestorePlanReport plan, string externalPin)
    {
        ValidateExternalTrustStorePin(externalPin);
        if (!string.Equals(
                plan.ApproverTrustStoreSha256,
                externalPin,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The externally published trust-store SHA-256 pin does not match the restore plan.");
        }
    }

    private static VerifiedApprovalSet VerifyApprovals(
        IReadOnlyList<string> approvalFiles,
        string trustStoreFile,
        string externallyPublishedTrustStoreSha256,
        RestorePlanReport plan,
        string planHash,
        DateTimeOffset now)
    {
        ValidatePlanTrustStorePin(plan, externallyPublishedTrustStoreSha256);
        var trustPath = PathSafety.FullPath(trustStoreFile, "approver trust store");
        PathSafety.EnsureRegularFile(trustPath, "approver trust store");
        var trustBytes = ReadBoundedFile(trustPath, 1_048_576);
        var actualTrustStoreSha256 = CanonicalJson.Sha256(trustBytes);
        if (!string.Equals(
                actualTrustStoreSha256,
                externallyPublishedTrustStoreSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Approver trust store does not match the externally published SHA-256 pin.");
        }
        ValidateTrustStoreJsonShape(trustBytes);
        ApproverTrustStore trust;
        try
        {
            trust = JsonSerializer.Deserialize<ApproverTrustStore>(
                        trustBytes,
                        CanonicalJson.SerializerOptions)
                    ?? throw new InvalidDataException("Approver trust store is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Approver trust store is invalid JSON.", exception);
        }
        if (trust.SchemaVersion != 1 || trust.Keys is null || trust.Keys.Count < 2 ||
            trust.Keys.Select(item => item.Subject).Distinct(StringComparer.OrdinalIgnoreCase).Count() != trust.Keys.Count)
        {
            throw new InvalidDataException("Approver trust store must contain distinct trusted subjects.");
        }
        var trusted = new Dictionary<string, ApproverTrustKey>(StringComparer.OrdinalIgnoreCase);
        var fingerprintsBySubject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trustedKeyFingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in trust.Keys)
        {
            ValidateSubject(item.Subject);
            if (item.Algorithm != SignatureAlgorithm || string.IsNullOrWhiteSpace(item.PublicKeyPem) ||
                !trusted.TryAdd(item.Subject, item))
            {
                throw new InvalidDataException("Approver trust store contains an invalid key entry.");
            }
            try
            {
                using var publicKey = ECDsa.Create();
                publicKey.ImportFromPem(item.PublicKeyPem);
                EnsureP256(publicKey, "Approver trust-store public key");
                var fingerprint = Convert.ToHexString(SHA256.HashData(
                        publicKey.ExportSubjectPublicKeyInfo()))
                    .ToLowerInvariant();
                if (!trustedKeyFingerprints.Add(fingerprint))
                {
                    throw new InvalidDataException(
                        "Approver trust store must bind each subject to a different ECDSA P-256 key.");
                }
                fingerprintsBySubject.Add(item.Subject, fingerprint);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "Approver trust store contains an invalid ECDSA public key.",
                    exception);
            }
        }

        var approvals = new List<SignedApproval>(2);
        var approvalEvidence = new List<ApprovalEvidence>(2);
        var approvalIds = new HashSet<string>(StringComparer.Ordinal);
        var subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in approvalFiles)
        {
            var path = PathSafety.FullPath(rawPath, "approval file");
            var approval = CanonicalJson.ReadCanonical<SignedApproval>(path, 1_048_576);
            var approvalHash = CanonicalJson.Sha256File(path);
            CanonicalJson.VerifyHashSidecar(path, approvalHash);
            ValidateSubject(approval.Subject);
            if (approval.SchemaVersion != RestoreSchemaVersion || approval.ApprovalType != ApprovalType ||
                approval.Algorithm != SignatureAlgorithm ||
                approval.Purpose != "execute" ||
                !string.IsNullOrEmpty(approval.JournalSha256) ||
                !string.IsNullOrEmpty(approval.JournalState) ||
                !string.IsNullOrEmpty(approval.JournalOutcome) ||
                approval.OriginalInventory is null || approval.CandidateInventory is null ||
                approval.OriginalInventory != plan.OriginalInventory ||
                approval.CandidateInventory != plan.CandidateInventory ||
                !Guid.TryParseExact(approval.ApprovalId, "N", out _) ||
                !approvalIds.Add(approval.ApprovalId) || !subjects.Add(approval.Subject) ||
                !ValidateApprovalTemporalShape(approval, now, requireCurrent: true) ||
                approval.OperationId != plan.OperationId ||
                approval.ServerId != plan.ServerId ||
                !WorldGuidsEqual(approval.WorldGuid, plan.WorldGuid) ||
                approval.BackupId != plan.BackupId ||
                !string.Equals(approval.ManifestSha256, plan.ManifestSha256, StringComparison.Ordinal) ||
                !string.Equals(approval.PlanSha256, planHash, StringComparison.Ordinal) ||
                !string.Equals(
                    approval.TrustStoreSha256,
                    externallyPublishedTrustStoreSha256,
                    StringComparison.Ordinal) ||
                !trusted.TryGetValue(approval.Subject, out var trustKey))
            {
                throw new InvalidDataException(
                    "Approval is expired, duplicated, untrusted, or bound to another restore plan/manifest.");
            }
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(approval.SignatureBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Approval signature is not valid base64.", exception);
            }
            using var key = ECDsa.Create();
            key.ImportFromPem(trustKey.PublicKeyPem);
            EnsureP256(key, "Approval verification public key");
            if (!key.VerifyData(
                    CanonicalJson.Serialize(approval.Payload),
                    signature,
                    HashAlgorithmName.SHA256))
            {
                throw new InvalidDataException("Approval signature verification failed.");
            }
            approvals.Add(approval);
            approvalEvidence.Add(new ApprovalEvidence(
                approval.Subject,
                path,
                approvalHash,
                fingerprintsBySubject[approval.Subject]));
        }
        return new VerifiedApprovalSet(
            approvals,
            approvalEvidence.OrderBy(item => item.Subject, StringComparer.Ordinal).ToArray(),
            trustPath,
            actualTrustStoreSha256);
    }

    private static VerifiedApprovalSet VerifyRecoveryApprovals(
        IReadOnlyList<string> approvalFiles,
        string externallyPublishedTrustStoreSha256,
        RestorePlanReport plan,
        string planHash,
        RestoreJournal journal,
        string journalHash,
        DateTimeOffset now)
    {
        ValidatePlanTrustStorePin(plan, externallyPublishedTrustStoreSha256);
        if (approvalFiles.Count != 2 ||
            journal.TrustStoreSha256 != externallyPublishedTrustStoreSha256 ||
            journal.OriginalInventorySummary != plan.OriginalInventory ||
            journal.CandidateInventorySummary != plan.CandidateInventory)
        {
            throw new InvalidDataException(
                "Recovery requires exactly two approvals and journal/plan trust and inventory bindings.");
        }

        var trustPath = journal.TrustStoreFile;
        PathSafety.EnsureRegularFile(trustPath, "snapshotted recovery trust store");
        var trustBytes = ReadBoundedFile(trustPath, 1_048_576);
        var trustHash = CanonicalJson.Sha256(trustBytes);
        if (trustHash != externallyPublishedTrustStoreSha256)
        {
            throw new InvalidDataException(
                "Snapshotted recovery trust store does not match the external pin.");
        }
        ValidateTrustStoreJsonShape(trustBytes);
        ApproverTrustStore trust;
        try
        {
            trust = JsonSerializer.Deserialize<ApproverTrustStore>(
                        trustBytes,
                        CanonicalJson.SerializerOptions)
                    ?? throw new InvalidDataException("Recovery trust store is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Recovery trust store is invalid JSON.", exception);
        }
        if (trust.SchemaVersion != 1 || trust.Keys is null || trust.Keys.Count < 2)
        {
            throw new InvalidDataException("Recovery trust store metadata is invalid.");
        }
        var trusted = new Dictionary<string, ApproverTrustKey>(StringComparer.OrdinalIgnoreCase);
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var distinctKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trustKey in trust.Keys)
        {
            ValidateSubject(trustKey.Subject);
            if (trustKey.Algorithm != SignatureAlgorithm ||
                string.IsNullOrWhiteSpace(trustKey.PublicKeyPem) ||
                !trusted.TryAdd(trustKey.Subject, trustKey))
            {
                throw new InvalidDataException("Recovery trust store contains an invalid key entry.");
            }
            using var key = ECDsa.Create();
            key.ImportFromPem(trustKey.PublicKeyPem);
            EnsureP256(key, "Recovery trust-store public key");
            var fingerprint = CanonicalJson.Sha256(key.ExportSubjectPublicKeyInfo());
            if (!distinctKeys.Add(fingerprint))
            {
                throw new InvalidDataException(
                    "Recovery trust store maps multiple subjects to one key.");
            }
            fingerprints.Add(trustKey.Subject, fingerprint);
        }

        var approvals = new List<SignedApproval>(2);
        var evidence = new List<ApprovalEvidence>(2);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in approvalFiles)
        {
            var path = PathSafety.FullPath(rawPath, "recovery approval file");
            var approval = CanonicalJson.ReadCanonical<SignedApproval>(path, 1_048_576);
            var approvalHash = CanonicalJson.Sha256File(path);
            CanonicalJson.VerifyHashSidecar(path, approvalHash);
            ValidateSubject(approval.Subject);
            if (approval.SchemaVersion != RestoreSchemaVersion ||
                approval.ApprovalType != ApprovalType || approval.Algorithm != SignatureAlgorithm ||
                approval.Purpose != "recover" || approval.JournalSha256 != journalHash ||
                approval.JournalState != journal.State || approval.JournalOutcome != journal.Outcome ||
                approval.OriginalInventory != journal.OriginalInventorySummary ||
                approval.CandidateInventory != journal.CandidateInventorySummary ||
                !Guid.TryParseExact(approval.ApprovalId, "N", out _) ||
                !ids.Add(approval.ApprovalId) || !subjects.Add(approval.Subject) ||
                !ValidateApprovalTemporalShape(approval, now, requireCurrent: true) ||
                approval.OperationId != plan.OperationId || approval.ServerId != plan.ServerId ||
                !WorldGuidsEqual(approval.WorldGuid, plan.WorldGuid) ||
                approval.BackupId != plan.BackupId ||
                approval.ManifestSha256 != plan.ManifestSha256 ||
                approval.PlanSha256 != planHash ||
                approval.TrustStoreSha256 != externallyPublishedTrustStoreSha256 ||
                !trusted.TryGetValue(approval.Subject, out var trustedKey))
            {
                throw new InvalidDataException(
                    "Recovery approval is expired, duplicated, untrusted, wrong-purpose, or misbound.");
            }
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(approval.SignatureBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Recovery approval signature is invalid base64.", exception);
            }
            using var key = ECDsa.Create();
            key.ImportFromPem(trustedKey.PublicKeyPem);
            EnsureP256(key, "Recovery approval verification public key");
            if (!key.VerifyData(
                    CanonicalJson.Serialize(approval.Payload),
                    signature,
                    HashAlgorithmName.SHA256))
            {
                throw new InvalidDataException("Recovery approval signature verification failed.");
            }
            approvals.Add(approval);
            evidence.Add(new ApprovalEvidence(
                approval.Subject,
                path,
                approvalHash,
                fingerprints[approval.Subject]));
        }
        return new VerifiedApprovalSet(
            approvals,
            evidence.OrderBy(item => item.Subject, StringComparer.Ordinal).ToArray(),
            trustPath,
            trustHash);
    }

    private static void ValidateApprovalArtifactLocations(
        VerifiedApprovalSet approvals,
        RestorePlanReport plan)
    {
        foreach (var artifact in new[] { approvals.TrustStoreFile }
                     .Concat(approvals.Evidence.Select(item => item.ApprovalFile)))
        {
            PathSafety.EnsureNoOverlap(
                artifact,
                plan.ActiveWorldDirectory,
                "approval artifact and active world");
            PathSafety.EnsureNoOverlap(
                artifact,
                plan.StagingDirectory,
                "approval artifact and staging world");
            PathSafety.EnsureNoOverlap(
                artifact,
                plan.BackupDirectory,
                "approval artifact and managed backup");
        }
    }

    private static string GetAuthorizationSnapshotDirectory(RestorePlanReport plan)
    {
        var path = Path.Combine(
            plan.EvidenceDirectory,
            $"world-restore-{plan.OperationId}-authorization");
        PathSafety.EnsureStrictChild(
            plan.EvidenceDirectory,
            path,
            "restore authorization snapshot directory");
        PathSafety.EnsureSameVolume(
            plan.ActiveWorldDirectory,
            path,
            "active world and authorization snapshots");
        return path;
    }

    private static VerifiedApprovalSet PersistExecutionAuthorizationSnapshot(
        VerifiedApprovalSet verified,
        RestorePlanReport plan,
        string planHash,
        string externalPin)
    {
        var directory = GetAuthorizationSnapshotDirectory(plan);
        if (Directory.Exists(directory) || File.Exists(directory))
        {
            throw new InvalidOperationException(
                "Operation authorization snapshot directory already exists.");
        }
        Directory.CreateDirectory(directory);
        PathSafety.EnsureDirectory(directory, create: false);

        var trustDestination = Path.Combine(directory, "execute-trust-store.json");
        WriteSnapshotArtifact(
            verified.TrustStoreFile,
            trustDestination,
            verified.TrustStoreSha256,
            "execution trust store");
        var destinations = new List<string>(2);
        var ordered = verified.Evidence.OrderBy(item => item.Subject, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var destination = Path.Combine(directory, $"execute-approval-{index + 1}.json");
            WriteSnapshotArtifact(
                ordered[index].ApprovalFile,
                destination,
                ordered[index].ApprovalSha256,
                "execution approval");
            destinations.Add(destination);
        }
        return VerifyApprovals(
            destinations,
            trustDestination,
            externalPin,
            plan,
            planHash,
            DateTimeOffset.UtcNow);
    }

    private static VerifiedApprovalSet PersistRecoveryAuthorizationSnapshot(
        VerifiedApprovalSet verified,
        RestorePlanReport plan,
        string planHash,
        string externalPin,
        RestoreJournal journal,
        string baseJournalHash)
    {
        var directory = GetAuthorizationSnapshotDirectory(plan);
        PathSafety.EnsureDirectory(directory, create: false);
        var destinations = new List<string>(2);
        var ordered = verified.Evidence.OrderBy(item => item.Subject, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var destination = Path.Combine(
                directory,
                $"recovery-{baseJournalHash[..16]}-{index + 1}-{ordered[index].ApprovalSha256[..12]}.json");
            PathSafety.EnsureStrictChild(directory, destination, "recovery approval snapshot");
            WriteSnapshotArtifact(
                ordered[index].ApprovalFile,
                destination,
                ordered[index].ApprovalSha256,
                "recovery approval");
            destinations.Add(destination);
        }
        return VerifyRecoveryApprovals(
            destinations,
            externalPin,
            plan,
            planHash,
            journal,
            baseJournalHash,
            DateTimeOffset.UtcNow);
    }

    private static void WriteSnapshotArtifact(
        string source,
        string destination,
        string expectedSha256,
        string description)
    {
        PathSafety.EnsureRegularFile(source, description);
        var bytes = ReadBoundedFile(source, 1_048_576);
        var hash = CanonicalJson.Sha256(bytes);
        if (!string.Equals(hash, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{description} changed before durable snapshot publication.");
        }
        CanonicalJson.WriteNewDurable(destination, bytes);
        var sidecar = destination + ".sha256";
        CanonicalJson.WriteNewDurable(
            sidecar,
            Encoding.UTF8.GetBytes($"{hash}  {Path.GetFileName(destination)}\n"));
        PathSafety.EnsureRegularFile(destination, $"snapshotted {description}");
        CanonicalJson.VerifyHashSidecar(destination, hash);
        if (!string.Equals(CanonicalJson.Sha256File(destination), hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Durable {description} snapshot hash mismatch.");
        }
    }

    private static void CopyManagedDataToNewDirectory(VerifiedBackup backup, string destination)
    {
        CopyInventoryToNewDirectory(backup.DataRoot, destination, backup.Inventory);
        foreach (var file in backup.Manifest.Files)
        {
            var destinationPath = ResolveInventoryPath(destination, file.RelativePath);
            File.SetLastWriteTimeUtc(destinationPath, file.LastModifiedAt.UtcDateTime);
        }
    }

    private static void CopyInventoryToNewDirectory(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<FileInventoryEntry> inventory,
        IReadOnlyCollection<string>? directories = null)
    {
        var source = PathSafety.FullPath(sourceRoot, "copy source");
        var destination = PathSafety.FullPath(destinationRoot, "copy destination");
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("Copy destination has no parent.");
        PathSafety.EnsureDirectory(source, create: false);
        PathSafety.EnsureDirectory(parent, create: false);
        var directoryInventory = directories ?? PathSafety.ExpectedDirectories(
            inventory.Select(item => item.RelativePath));
        var sourceInventory = PathSafety.Inventory(
            source,
            requireNonEmpty: true,
            directoryInventory);
        PathSafety.AssertInventoriesEqual(inventory, sourceInventory, "copy source tree");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new InvalidOperationException("Copy destination already exists.");
        }
        Directory.CreateDirectory(destination);
        PathSafety.EnsureDirectory(destination, create: false);
        foreach (var directory in directoryInventory
                  .OrderBy(item => item.Count(character => character == '/'))
                 .ThenBy(item => item, StringComparer.Ordinal))
        {
            var path = ResolveInventoryPath(destination, directory);
            Directory.CreateDirectory(path);
            PathSafety.EnsureAncestorChainHasNoReparsePoint(path);
        }
        foreach (var file in inventory)
        {
            var sourcePath = ResolveInventoryPath(source, file.RelativePath);
            var destinationPath = ResolveInventoryPath(destination, file.RelativePath);
            PathSafety.EnsureRegularFile(sourcePath, "copy source file");
            using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.WriteThrough);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
        }
        var copied = PathSafety.Inventory(
            destination,
            requireNonEmpty: true,
            directoryInventory);
        PathSafety.AssertInventoriesEqual(inventory, copied, "copied filesystem tree");
    }

    private static string ResolveInventoryPath(string root, string relativePath)
    {
        var validated = PathSafety.ValidateManifestRelativePath(relativePath);
        var path = Path.GetFullPath(Path.Combine(
            root,
            validated.Replace('/', Path.DirectorySeparatorChar)));
        PathSafety.EnsureStrictChild(root, path, "inventory path");
        return path;
    }

    private static ProcessGateEvidence AssertExpectedPalServerStopped(string expectedExecutable)
    {
        var expected = PathSafety.FullPath(expectedExecutable, "PalServer executable");
        PathSafety.EnsureRegularFile(expected, "PalServer executable at process gate");
        var installationRoot = Path.GetDirectoryName(expected)
            ?? throw new InvalidDataException("PalServer executable has no installation root.");
        PathSafety.EnsureLocalVolume(installationRoot, "PalServer installation root");
        var matching = 0;
        var inaccessible = 0;
        foreach (var processName in PalServerProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var actual = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(actual))
                        {
                            inaccessible++;
                            throw new InvalidOperationException(
                                $"A {processName} process path could not be read; stopped state cannot be proven.");
                        }
                        var actualPath = Path.GetFullPath(actual);
                        if (PathSafety.PathsEqual(actualPath, installationRoot) ||
                            PathSafety.IsStrictChild(installationRoot, actualPath))
                        {
                            matching++;
                        }
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        // A process that exits during inspection satisfies this check.
                    }
                    catch (Exception exception) when (
                        exception is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                    {
                        inaccessible++;
                        throw new InvalidOperationException(
                            $"A {processName} process could not be inspected; stopped state cannot be proven.",
                            exception);
                    }
                }
            }
        }
        if (matching != 0 || inaccessible != 0)
        {
            throw new InvalidOperationException(
                "A PalServer, PalServer-Win64-Shipping-Cmd, or PalServer-Win64-Shipping process " +
                "from the configured installation is still running; restore execution is refused.");
        }
        return new ProcessGateEvidence(
            DateTimeOffset.UtcNow,
            installationRoot,
            PalServerProcessNames,
            matching,
            inaccessible);
    }

    private static InventorySummary SummarizeInventory(
        IReadOnlyList<FileInventoryEntry> inventory,
        IReadOnlyCollection<string>? directories = null) =>
        new(
            inventory.Count,
            checked(inventory.Sum(item => item.Length)),
            CanonicalJson.Sha256(CanonicalJson.Serialize(
                new
                {
                    Directories = (directories ?? PathSafety.ExpectedDirectories(
                            inventory.Select(item => item.RelativePath)))
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .ToArray(),
                    Files = inventory.OrderBy(
                            item => item.RelativePath,
                            StringComparer.Ordinal)
                        .ToArray()
                })));

    private static InventoryCapture CaptureInventory(string root)
    {
        var directories = PathSafety.DirectoryInventory(root);
        var files = PathSafety.Inventory(root, requireNonEmpty: true, directories);
        var directoriesAgain = PathSafety.DirectoryInventory(root);
        if (!directories.SequenceEqual(directoriesAgain, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Filesystem directory inventory changed while it was captured.");
        }
        var filesAgain = PathSafety.Inventory(root, requireNonEmpty: true, directoriesAgain);
        PathSafety.AssertInventoriesEqual(files, filesAgain, "stable filesystem inventory capture");
        return new InventoryCapture(
            files,
            directories,
            SummarizeInventory(files, directories));
    }

    private static bool ValidateInventorySummary(InventorySummary summary) =>
        summary.FileCount > 0 && summary.TotalBytes >= 0 && IsSha256(summary.InventorySha256);

    private static string GetJournalPath(RestorePlanReport plan)
    {
        var parent = Path.GetDirectoryName(plan.ActiveWorldDirectory)
            ?? throw new InvalidDataException("Active world directory has no parent.");
        var path = Path.Combine(parent, $".palcontrol-world-restore-{plan.OperationId}-journal.json");
        PathSafety.EnsureStrictChild(parent, path, "restore journal");
        PathSafety.EnsureSameVolume(plan.ActiveWorldDirectory, path, "restore journal");
        return path;
    }

    private static string WriteJournal(string path, RestoreJournal journal)
    {
        if (journal.SchemaVersion != RestoreSchemaVersion || journal.ReportType != JournalReportType ||
            journal.State is not ("prepared" or "old-retired" or "candidate-active" or "committed") ||
            journal.Outcome is not ("pending" or "restored" or "recovered"))
        {
            throw new InvalidDataException("Restore journal state is invalid.");
        }
        return CanonicalJson.WriteCanonicalDurableReplace(path, journal);
    }

    private static RestoreJournal ReadAndValidateJournal(
        string journalPath,
        RestorePlanReport plan,
        string planHash)
    {
        var journal = CanonicalJson.ReadCanonical<RestoreJournal>(journalPath, 32 * 1024 * 1024);
        if (journal.SchemaVersion != RestoreSchemaVersion || journal.ReportType != JournalReportType ||
            journal.OperationId != plan.OperationId || journal.ServerId != plan.ServerId ||
            !WorldGuidsEqual(journal.WorldGuid, plan.WorldGuid) ||
            journal.BackupId != plan.BackupId || journal.ManifestSha256 != plan.ManifestSha256 ||
            journal.PlanSha256 != planHash || journal.UpdatedAt == default ||
            journal.TrustStoreSha256 != plan.ApproverTrustStoreSha256 ||
            journal.State is not ("prepared" or "old-retired" or "candidate-active" or "committed") ||
            journal.Outcome is not ("pending" or "restored" or "recovered") ||
            (journal.State == "committed") != (journal.Outcome != "pending") ||
            journal.OriginalInventory is null || journal.CandidateInventory is null ||
            journal.OriginalDirectories is null || journal.CandidateDirectories is null ||
            journal.OriginalInventory.Count == 0 || journal.CandidateInventory.Count == 0 ||
            !ValidateFileInventoryList(journal.OriginalInventory) ||
            !ValidateFileInventoryList(journal.CandidateInventory) ||
            !ValidateDirectoryList(journal.OriginalDirectories) ||
            !ValidateDirectoryList(journal.CandidateDirectories) ||
            !InventoriesEqualSummary(
                journal.OriginalInventory,
                journal.OriginalDirectories,
                journal.OriginalInventorySummary) ||
            !InventoriesEqualSummary(
                journal.CandidateInventory,
                journal.CandidateDirectories,
                journal.CandidateInventorySummary) ||
            journal.OriginalInventorySummary != plan.OriginalInventory ||
            journal.CandidateInventorySummary != plan.CandidateInventory ||
            journal.RecoveryApprovals is null ||
            (!string.IsNullOrEmpty(journal.RecoveryAuthorizationBaseJournalSha256) &&
             !IsSha256(journal.RecoveryAuthorizationBaseJournalSha256)) ||
            (string.IsNullOrEmpty(journal.RecoveryAuthorizationBaseJournalSha256)
                ? journal.RecoveryApprovals.Count != 0 ||
                  !string.IsNullOrEmpty(journal.RecoveryAuthorizationJournalState) ||
                  !string.IsNullOrEmpty(journal.RecoveryAuthorizationJournalOutcome)
                : journal.RecoveryApprovals.Count != 2 ||
                  journal.RecoveryAuthorizationJournalState is not
                      ("prepared" or "old-retired" or "candidate-active") ||
                  journal.RecoveryAuthorizationJournalOutcome != "pending") ||
            !journal.CandidateDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(
                PathSafety.ExpectedDirectories(
                    journal.CandidateInventory.Select(item => item.RelativePath))))
        {
            throw new InvalidDataException("Restore crash journal is invalid or bound to another plan.");
        }
        var expectedParent = Path.GetDirectoryName(plan.ActiveWorldDirectory)!;
        var expectedRollback = Path.Combine(expectedParent, $".palcontrol-world-rollback-{plan.OperationId}");
        var expectedRetired = Path.Combine(expectedParent, $".palcontrol-world-retired-{plan.OperationId}");
        var expectedFailed = Path.Combine(expectedParent, $".palcontrol-world-failed-{plan.OperationId}");
        var expectedAuthorization = GetAuthorizationSnapshotDirectory(plan);
        var expectedTrustSnapshot = Path.Combine(expectedAuthorization, "execute-trust-store.json");
        if (!PathSafety.PathsEqual(journal.InstallationRoot, plan.InstallationRoot) ||
            !PathSafety.PathsEqual(journal.ActiveWorldDirectory, plan.ActiveWorldDirectory) ||
            !PathSafety.PathsEqual(journal.StagingDirectory, plan.StagingDirectory) ||
            !PathSafety.PathsEqual(journal.RollbackDirectory, expectedRollback) ||
            !PathSafety.PathsEqual(journal.RetiredWorldDirectory, expectedRetired) ||
            !PathSafety.PathsEqual(journal.FailedCandidateDirectory, expectedFailed) ||
            !PathSafety.PathsEqual(journal.BackupDirectory, plan.BackupDirectory) ||
            !PathSafety.PathsEqual(journal.EvidenceDirectory, plan.EvidenceDirectory) ||
            !PathSafety.PathsEqual(journal.AuthorizationSnapshotDirectory, expectedAuthorization) ||
            !PathSafety.PathsEqual(journal.TrustStoreFile, expectedTrustSnapshot))
        {
            throw new InvalidDataException("Restore crash journal paths are not plan-bound.");
        }
        if (!Path.IsPathFullyQualified(journal.EvidenceDirectory))
        {
            throw new InvalidDataException("Restore crash journal evidence path is not absolute.");
        }
        foreach (var path in new[]
                 {
                     journal.ActiveWorldDirectory, journal.StagingDirectory,
                     journal.RollbackDirectory, journal.RetiredWorldDirectory,
                     journal.FailedCandidateDirectory, journal.EvidenceDirectory,
                     journal.AuthorizationSnapshotDirectory, journal.TrustStoreFile
                 })
        {
            if (!string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Restore crash journal contains a non-canonical path.");
            }
            PathSafety.EnsureAncestorChainHasNoReparsePoint(path);
        }
        if (!IsSha256(journal.TrustStoreSha256) ||
            journal.Approvals is null || journal.Approvals.Count != 2 ||
            journal.Approvals.Select(item => item.Subject)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            journal.Approvals.Any(item => !IsSha256(item.ApprovalSha256) ||
                                          !IsSha256(item.KeyFingerprintSha256)))
        {
            throw new InvalidDataException("Restore crash journal approval evidence is invalid.");
        }
        if (journal.Approvals.Any(item =>
                !PathSafety.IsStrictChild(journal.AuthorizationSnapshotDirectory, item.ApprovalFile)) ||
            journal.RecoveryApprovals.Any(item =>
                !PathSafety.IsStrictChild(journal.AuthorizationSnapshotDirectory, item.ApprovalFile)) ||
            journal.RecoveryApprovals.Select(item => item.Subject)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.RecoveryApprovals.Count ||
            journal.RecoveryApprovals.Any(item => !IsSha256(item.ApprovalSha256) ||
                                                  !IsSha256(item.KeyFingerprintSha256)))
        {
            throw new InvalidDataException("Restore journal authorization snapshot paths are invalid.");
        }
        ValidateJournalApprovalArtifacts(journal);
        return journal;
    }

    private static void ValidateJournalApprovalArtifacts(RestoreJournal journal)
    {
        PathSafety.EnsureRegularFile(journal.TrustStoreFile, "journal-bound approver trust store");
        var trustBytes = ReadBoundedFile(journal.TrustStoreFile, 1_048_576);
        if (!string.Equals(
                CanonicalJson.Sha256(trustBytes),
                journal.TrustStoreSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Journal-bound approver trust store changed.");
        }
        ValidateTrustStoreJsonShape(trustBytes);
        ApproverTrustStore trust;
        try
        {
            trust = JsonSerializer.Deserialize<ApproverTrustStore>(
                        trustBytes,
                        CanonicalJson.SerializerOptions)
                    ?? throw new InvalidDataException("Journal-bound trust store is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Journal-bound trust store is invalid JSON.", exception);
        }
        if (trust.SchemaVersion != 1 || trust.Keys is null || trust.Keys.Count < 2)
        {
            throw new InvalidDataException("Journal-bound trust store metadata is invalid.");
        }
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trustedKeys = new Dictionary<string, ApproverTrustKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyEntry in trust.Keys)
        {
            ValidateSubject(keyEntry.Subject);
            if (keyEntry.Algorithm != SignatureAlgorithm ||
                string.IsNullOrWhiteSpace(keyEntry.PublicKeyPem))
            {
                throw new InvalidDataException("Journal-bound trust store contains an invalid key entry.");
            }
            using var key = ECDsa.Create();
            key.ImportFromPem(keyEntry.PublicKeyPem);
            EnsureP256(key, "Journal-bound trust-store public key");
            var fingerprint = Convert.ToHexString(SHA256.HashData(
                    key.ExportSubjectPublicKeyInfo()))
                .ToLowerInvariant();
            if (!fingerprints.TryAdd(keyEntry.Subject, fingerprint))
            {
                throw new InvalidDataException("Journal-bound trust store has duplicate subjects.");
            }
            trustedKeys.Add(keyEntry.Subject, keyEntry);
        }
        var executionApprovalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var approval in journal.Approvals)
        {
            PathSafety.EnsureRegularFile(approval.ApprovalFile, "journal-bound approval file");
            if (!string.Equals(
                    CanonicalJson.Sha256File(approval.ApprovalFile),
                    approval.ApprovalSha256,
                    StringComparison.Ordinal) ||
                !fingerprints.TryGetValue(approval.Subject, out var fingerprint) ||
                !string.Equals(fingerprint, approval.KeyFingerprintSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Journal-bound approval or key fingerprint changed.");
            }
            var signed = CanonicalJson.ReadCanonical<SignedApproval>(
                approval.ApprovalFile,
                1_048_576);
            if (signed.SchemaVersion != RestoreSchemaVersion ||
                signed.ApprovalType != ApprovalType || signed.Algorithm != SignatureAlgorithm ||
                signed.Purpose != "execute" ||
                !string.IsNullOrEmpty(signed.JournalSha256) ||
                !string.IsNullOrEmpty(signed.JournalState) ||
                !string.IsNullOrEmpty(signed.JournalOutcome) ||
                signed.OriginalInventory != journal.OriginalInventorySummary ||
                signed.CandidateInventory != journal.CandidateInventorySummary ||
                !Guid.TryParseExact(signed.ApprovalId, "N", out _) ||
                !executionApprovalIds.Add(signed.ApprovalId) ||
                !ValidateApprovalTemporalShape(signed, DateTimeOffset.UtcNow, requireCurrent: false) ||
                !string.Equals(signed.Subject, approval.Subject, StringComparison.Ordinal) ||
                signed.OperationId != journal.OperationId || signed.ServerId != journal.ServerId ||
                !WorldGuidsEqual(signed.WorldGuid, journal.WorldGuid) ||
                signed.BackupId != journal.BackupId ||
                signed.ManifestSha256 != journal.ManifestSha256 ||
                signed.PlanSha256 != journal.PlanSha256 ||
                signed.TrustStoreSha256 != journal.TrustStoreSha256 ||
                !trustedKeys.TryGetValue(signed.Subject, out var trustedKey))
            {
                throw new InvalidDataException(
                    "Journal-bound approval is not bound to its plan and external trust-store pin.");
            }
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(signed.SignatureBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "Journal-bound approval signature is invalid base64.",
                    exception);
            }
            using var verificationKey = ECDsa.Create();
            verificationKey.ImportFromPem(trustedKey.PublicKeyPem);
            EnsureP256(verificationKey, "Journal-bound approval public key");
            if (!verificationKey.VerifyData(
                    CanonicalJson.Serialize(signed.Payload),
                    signature,
                    HashAlgorithmName.SHA256))
            {
                throw new InvalidDataException(
                    "Journal-bound approval signature verification failed.");
            }
        }
        var recoveryApprovalIds = new HashSet<string>(StringComparer.Ordinal);
        var recoverySubjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var approval in journal.RecoveryApprovals)
        {
            PathSafety.EnsureRegularFile(approval.ApprovalFile, "journal-bound recovery approval file");
            if (!string.Equals(
                    CanonicalJson.Sha256File(approval.ApprovalFile),
                    approval.ApprovalSha256,
                    StringComparison.Ordinal) ||
                !fingerprints.TryGetValue(approval.Subject, out var fingerprint) ||
                !string.Equals(fingerprint, approval.KeyFingerprintSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Journal-bound recovery approval or key fingerprint changed.");
            }
            var signed = CanonicalJson.ReadCanonical<SignedApproval>(
                approval.ApprovalFile,
                1_048_576);
            if (signed.SchemaVersion != RestoreSchemaVersion ||
                signed.ApprovalType != ApprovalType || signed.Algorithm != SignatureAlgorithm ||
                signed.Purpose != "recover" ||
                signed.JournalSha256 != journal.RecoveryAuthorizationBaseJournalSha256 ||
                signed.JournalState != journal.RecoveryAuthorizationJournalState ||
                signed.JournalOutcome != journal.RecoveryAuthorizationJournalOutcome ||
                signed.OriginalInventory != journal.OriginalInventorySummary ||
                signed.CandidateInventory != journal.CandidateInventorySummary ||
                !Guid.TryParseExact(signed.ApprovalId, "N", out _) ||
                !recoveryApprovalIds.Add(signed.ApprovalId) ||
                !recoverySubjects.Add(signed.Subject) ||
                !ValidateApprovalTemporalShape(signed, DateTimeOffset.UtcNow, requireCurrent: false) ||
                !string.Equals(signed.Subject, approval.Subject, StringComparison.Ordinal) ||
                signed.OperationId != journal.OperationId || signed.ServerId != journal.ServerId ||
                !WorldGuidsEqual(signed.WorldGuid, journal.WorldGuid) ||
                signed.BackupId != journal.BackupId ||
                signed.ManifestSha256 != journal.ManifestSha256 ||
                signed.PlanSha256 != journal.PlanSha256 ||
                signed.TrustStoreSha256 != journal.TrustStoreSha256 ||
                !trustedKeys.TryGetValue(signed.Subject, out var trustedKey))
            {
                throw new InvalidDataException(
                    "Journal-bound recovery approval is structurally invalid or misbound.");
            }
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(signed.SignatureBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "Journal-bound recovery approval signature is invalid base64.",
                    exception);
            }
            using var verificationKey = ECDsa.Create();
            verificationKey.ImportFromPem(trustedKey.PublicKeyPem);
            EnsureP256(verificationKey, "Journal-bound recovery approval public key");
            if (!verificationKey.VerifyData(
                    CanonicalJson.Serialize(signed.Payload),
                    signature,
                    HashAlgorithmName.SHA256))
            {
                throw new InvalidDataException(
                    "Journal-bound recovery approval signature verification failed.");
            }
        }
    }

    private static bool InventoriesEqualSummary(
        IReadOnlyList<FileInventoryEntry> inventory,
        IReadOnlyCollection<string> directories,
        InventorySummary summary)
    {
        if (summary is null)
        {
            return false;
        }
        var actual = SummarizeInventory(inventory, directories);
        return actual == summary;
    }

    private static bool ValidateDirectoryList(IReadOnlyList<string> directories)
    {
        if (!directories.SequenceEqual(
                directories.OrderBy(item => item, StringComparer.Ordinal),
                StringComparer.Ordinal) ||
            directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Count)
        {
            return false;
        }
        try
        {
            var set = directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return directories.All(item =>
                string.Equals(
                    PathSafety.ValidateManifestRelativePath(item),
                    item,
                    StringComparison.Ordinal) &&
                PathSafety.ExpectedDirectories([item + "/sentinel"])
                    .All(parent => set.Contains(parent)));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool ValidateFileInventoryList(IReadOnlyList<FileInventoryEntry> inventory)
    {
        if (!inventory.SequenceEqual(
                inventory.OrderBy(item => item.RelativePath, StringComparer.Ordinal)) ||
            inventory.Select(item => item.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != inventory.Count)
        {
            return false;
        }
        try
        {
            return inventory.All(item =>
                item.Length >= 0 && IsSha256(item.Sha256) &&
                string.Equals(
                    PathSafety.ValidateManifestRelativePath(item.RelativePath),
                    item.RelativePath,
                    StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static RestoreTopology InspectTopology(RestoreJournal journal)
    {
        foreach (var path in new[]
                 {
                     journal.ActiveWorldDirectory, journal.StagingDirectory,
                     journal.RollbackDirectory, journal.RetiredWorldDirectory,
                     journal.FailedCandidateDirectory
                 })
        {
            PathSafety.EnsureAncestorChainHasNoReparsePoint(path);
            if (File.Exists(path))
            {
                throw new InvalidDataException($"Restore preservation path became a file: {path}");
            }
        }

        AssertDirectoryInventory(
            journal.RollbackDirectory,
            journal.OriginalInventory,
            journal.OriginalDirectories,
            "journal cold rollback");
        var activeExists = Directory.Exists(journal.ActiveWorldDirectory);
        var stageExists = Directory.Exists(journal.StagingDirectory);
        var retiredExists = Directory.Exists(journal.RetiredWorldDirectory);
        var failedExists = Directory.Exists(journal.FailedCandidateDirectory);
        var activeOriginal = TreeMatches(
            journal.ActiveWorldDirectory,
            journal.OriginalInventory,
            journal.OriginalDirectories);
        var activeCandidate = TreeMatches(
            journal.ActiveWorldDirectory,
            journal.CandidateInventory,
            journal.CandidateDirectories);
        var stageCandidate = TreeMatches(
            journal.StagingDirectory,
            journal.CandidateInventory,
            journal.CandidateDirectories);
        var retiredOriginal = TreeMatches(
            journal.RetiredWorldDirectory,
            journal.OriginalInventory,
            journal.OriginalDirectories);
        var failedCandidate = TreeMatches(
            journal.FailedCandidateDirectory,
            journal.CandidateInventory,
            journal.CandidateDirectories);
        if ((activeExists && !activeOriginal && !activeCandidate) ||
            (stageExists && !stageCandidate) ||
            (retiredExists && !retiredOriginal) ||
            (failedExists && !failedCandidate))
        {
            throw new InvalidDataException(
                "A restore tree does not match its complete file-and-directory journal inventory.");
        }

        if (activeOriginal && !retiredExists &&
            ((stageCandidate && !failedExists) || (failedCandidate && !stageExists)))
        {
            return RestoreTopology.Recovered;
        }
        if (!activeExists && retiredOriginal &&
            ((stageCandidate && !failedExists) || (failedCandidate && !stageExists)))
        {
            return RestoreTopology.OldRetired;
        }
        if (activeCandidate && retiredOriginal && !stageExists && !failedExists)
        {
            return RestoreTopology.CandidateActive;
        }
        throw new InvalidDataException(
            "Restore directories do not match any complete, crash-safe journal topology.");
    }

    private static IReadOnlyList<FileInventoryEntry>? ReadOptionalInventory(
        string path,
        params IReadOnlyCollection<string>[] possibleDirectories)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }
        InvalidDataException? last = null;
        foreach (var directories in possibleDirectories)
        {
            try
            {
                return PathSafety.Inventory(path, requireNonEmpty: true, directories);
            }
            catch (InvalidDataException exception)
            {
                last = exception;
            }
        }
        throw new InvalidDataException(
            "Filesystem tree directory inventory does not match the durable journal.",
            last);
    }

    private static bool TreeMatches(
        string path,
        IReadOnlyList<FileInventoryEntry> expectedFiles,
        IReadOnlyCollection<string> expectedDirectories)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }
        try
        {
            var actual = PathSafety.Inventory(
                path,
                requireNonEmpty: true,
                expectedDirectories);
            PathSafety.AssertInventoriesEqual(expectedFiles, actual, "journal filesystem tree");
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void AssertDirectoryInventory(
        string path,
        IReadOnlyList<FileInventoryEntry> expected,
        IReadOnlyCollection<string> expectedDirectories,
        string description)
    {
        var actual = PathSafety.Inventory(
            path,
            requireNonEmpty: true,
            expectedDirectories);
        PathSafety.AssertInventoriesEqual(expected, actual, description);
    }

    private static void ValidateCommittedOrPendingTopology(RestoreJournal journal)
    {
        var topology = InspectTopology(journal);
        if (journal.State == "committed" &&
            ((journal.Outcome == "restored" && topology != RestoreTopology.CandidateActive) ||
             (journal.Outcome == "recovered" && topology != RestoreTopology.Recovered)))
        {
            throw new InvalidDataException("Committed journal outcome does not match the full filesystem inventory.");
        }
    }

    private static RestoreJournal RecoverJournalLocked(
        string journalPath,
        RestoreJournal journal,
        RestorePlanReport plan,
        bool allowInProcessAutomaticRecovery)
    {
        if (journal.State == "committed")
        {
            ValidateCommittedOrPendingTopology(journal);
            return journal;
        }
        if (!allowInProcessAutomaticRecovery &&
            (!IsSha256(journal.RecoveryAuthorizationBaseJournalSha256) ||
             journal.RecoveryApprovals.Count != 2 ||
             journal.RecoveryAuthorizationJournalState is not
                 ("prepared" or "old-retired" or "candidate-active") ||
             journal.RecoveryAuthorizationJournalOutcome != "pending"))
        {
            throw new InvalidOperationException(
                "Manual recovery lacks a durable two-person recovery authorization snapshot.");
        }

        var gates = journal.ProcessGates.ToList();
        gates.Add(AssertExpectedPalServerStopped(plan.PalServerExecutable));
        var topology = InspectTopology(journal);
        if (topology == RestoreTopology.CandidateActive &&
            TryFinalizePublishedResult(journalPath, journal, plan, gates, out var finalized))
        {
            return finalized;
        }
        if (topology == RestoreTopology.CandidateActive)
        {
            gates.Add(AssertExpectedPalServerStopped(plan.PalServerExecutable));
            AssertDirectoryInventory(
                journal.ActiveWorldDirectory,
                journal.CandidateInventory,
                journal.CandidateDirectories,
                "candidate at recovery move");
            AssertDirectoryInventory(
                journal.RetiredWorldDirectory,
                journal.OriginalInventory,
                journal.OriginalDirectories,
                "retired original at candidate preservation move");
            Directory.Move(journal.ActiveWorldDirectory, journal.FailedCandidateDirectory);
            AssertDirectoryInventory(
                journal.FailedCandidateDirectory,
                journal.CandidateInventory,
                journal.CandidateDirectories,
                "preserved failed candidate");
            journal = journal with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                State = "old-retired",
                ProcessGates = gates.ToArray()
            };
            WriteJournal(journalPath, journal);
            topology = InspectTopology(journal);
        }
        if (topology == RestoreTopology.OldRetired)
        {
            // The final stop check is immediately adjacent to the recovery move.
            gates.Add(AssertExpectedPalServerStopped(plan.PalServerExecutable));
            AssertDirectoryInventory(
                journal.RetiredWorldDirectory,
                journal.OriginalInventory,
                journal.OriginalDirectories,
                "retired original at final recovery move");
            Directory.Move(journal.RetiredWorldDirectory, journal.ActiveWorldDirectory);
            AssertDirectoryInventory(
                journal.ActiveWorldDirectory,
                journal.OriginalInventory,
                journal.OriginalDirectories,
                "recovered original active world");
            topology = InspectTopology(journal);
        }
        if (topology != RestoreTopology.Recovered)
        {
            throw new InvalidDataException("Restore recovery did not reach the verified original-world topology.");
        }

        journal = journal with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            State = "committed",
            Outcome = "recovered",
            ProcessGates = gates.ToArray()
        };
        WriteJournal(journalPath, journal);
        TryWriteFailureEvidence(
            journalPath,
            journal,
            true,
            new IOException("Recovered an incomplete durable restore journal."),
            gates);
        return journal;
    }

    private static bool TryFinalizePublishedResult(
        string journalPath,
        RestoreJournal journal,
        RestorePlanReport plan,
        IReadOnlyList<ProcessGateEvidence> processGates,
        out RestoreJournal finalized)
    {
        var resultPath = Path.Combine(
            journal.EvidenceDirectory,
            $"world-restore-{journal.OperationId}-result.json");
        finalized = journal;
        if (!File.Exists(resultPath))
        {
            return false;
        }
        var result = CanonicalJson.ReadCanonical<RestoreResultReport>(resultPath, 4 * 1024 * 1024);
        var resultHash = CanonicalJson.Sha256File(resultPath);
        CanonicalJson.VerifyHashSidecar(resultPath, resultHash);
        var journalHash = CanonicalJson.Sha256File(journalPath);
        var resultBaseJournalHash = IsSha256(journal.RecoveryAuthorizationBaseJournalSha256)
            ? journal.RecoveryAuthorizationBaseJournalSha256
            : journalHash;
        if (result.SchemaVersion != RestoreSchemaVersion || result.ReportType != ResultReportType ||
            result.OperationId != journal.OperationId || result.Mode != "executed" ||
            result.Phase != "committed" || result.ServerId != plan.ServerId ||
            !WorldGuidsEqual(result.WorldGuid, plan.WorldGuid) ||
            result.BackupId != plan.BackupId || result.ManifestSha256 != plan.ManifestSha256 ||
            result.PlanSha256 != journal.PlanSha256 ||
            !PathSafety.PathsEqual(result.JournalFile, journalPath) ||
            result.JournalSha256BeforeCommit != resultBaseJournalHash ||
            !PathSafety.PathsEqual(
                result.AuthorizationSnapshotDirectory,
                journal.AuthorizationSnapshotDirectory) ||
            result.TrustStoreSha256 != journal.TrustStoreSha256 ||
            result.TrustStoreSha256 != plan.ApproverTrustStoreSha256 ||
            result.Approvals is null ||
            !ApprovalEvidenceEquals(result.Approvals, journal.Approvals) ||
            result.ActiveInventory != journal.CandidateInventorySummary ||
            result.RollbackInventory != journal.OriginalInventorySummary ||
            result.RetiredInventory != journal.OriginalInventorySummary ||
            result.Checks is null || result.Checks.Values.Any(value => !value))
        {
            throw new InvalidDataException(
                "A published result exists but is not the exact durable result for this journal.");
        }
        finalized = journal with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            State = "committed",
            Outcome = "restored",
            ProcessGates = processGates.ToArray(),
            ResultFile = resultPath,
            ResultSha256 = resultHash
        };
        WriteJournal(journalPath, finalized);
        return true;
    }

    private static bool ApprovalEvidenceEquals(
        IReadOnlyList<ApprovalEvidence> left,
        IReadOnlyList<ApprovalEvidence> right) =>
        CanonicalJson.Serialize(left).AsSpan().SequenceEqual(CanonicalJson.Serialize(right));

    private static void TryWriteFailureEvidence(
        string journalPath,
        RestoreJournal journal,
        bool recovered,
        Exception exception,
        IReadOnlyList<ProcessGateEvidence> processGates)
    {
        try
        {
            var failedInventory = ReadOptionalInventory(
                journal.FailedCandidateDirectory,
                journal.CandidateDirectories);
            CanonicalJson.WriteEvidence(
                journal.EvidenceDirectory,
                $"world-restore-{journal.OperationId}-failure.json",
                new RestoreFailureReport(
                    RestoreSchemaVersion,
                    FailureReportType,
                    journal.OperationId,
                    DateTimeOffset.UtcNow,
                    "failed-safe",
                    exception.GetType().Name,
                    recovered,
                    journal.ActiveWorldDirectory,
                    journal.RollbackDirectory,
                    journal.RetiredWorldDirectory,
                    journal.FailedCandidateDirectory,
                    journal.BackupDirectory,
                    recovered ? "committed/recovered" : journal.State,
                    journalPath,
                    journal.AuthorizationSnapshotDirectory,
                    journal.TrustStoreFile,
                    journal.TrustStoreSha256,
                    journal.Approvals,
                    journal.RecoveryAuthorizationBaseJournalSha256,
                    journal.RecoveryAuthorizationJournalState,
                    journal.RecoveryAuthorizationJournalOutcome,
                    journal.RecoveryApprovals,
                    journal.OriginalInventorySummary,
                    journal.OriginalInventorySummary,
                    failedInventory is null
                        ? null
                        : SummarizeInventory(failedInventory, journal.CandidateDirectories),
                    processGates));
        }
        catch
        {
            // Evidence failure must not replace the original switch/recovery result.
        }
    }

    private static CommandResult JournalCommandResult(
        string planPath,
        string planHash,
        string journalPath,
        RestoreJournal journal) =>
        new(
            journal.State == "committed" ? "completed" : "recovery-required",
            "journal-status",
            journal.State == "committed" && journal.Outcome == "restored",
            string.IsNullOrWhiteSpace(journal.ResultFile) ? journalPath : journal.ResultFile,
            string.IsNullOrWhiteSpace(journal.ResultSha256)
                ? CanonicalJson.Sha256File(journalPath)
                : journal.ResultSha256,
            planPath,
            planHash,
            Directory.Exists(journal.StagingDirectory) ? journal.StagingDirectory : null,
            journal.ActiveWorldDirectory,
            journal.RollbackDirectory,
            Directory.Exists(journal.RetiredWorldDirectory) ? journal.RetiredWorldDirectory : null,
            null,
            null,
            journalPath,
            journal.State,
            journal.Outcome);

    private enum RestoreTopology
    {
        Recovered,
        OldRetired,
        CandidateActive
    }

    private static void ValidateTestFaultConfiguration(RestorePlanReport plan)
    {
        var fault = Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_FAULT");
        var crash = Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_CRASH");
        var pause = Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_PAUSE_AT");
        if (string.IsNullOrWhiteSpace(fault) && string.IsNullOrWhiteSpace(crash) &&
            string.IsNullOrWhiteSpace(pause))
        {
            return;
        }
        if (new[] { fault, crash, pause }.Count(value => !string.IsNullOrWhiteSpace(value)) > 1)
        {
            throw new InvalidDataException("Only one fixture fault, crash, or pause injection may be enabled.");
        }
        if (!string.IsNullOrWhiteSpace(fault) &&
            fault is not ("after-active-move" or "after-stage-move"))
        {
            throw new InvalidDataException("Unknown world-restore fixture fault point.");
        }
        if (!string.IsNullOrWhiteSpace(crash) &&
            crash is not ("prepared" or "old-retired" or "candidate-active"))
        {
            throw new InvalidDataException("Unknown world-restore fixture crash point.");
        }
        if (!string.IsNullOrWhiteSpace(pause) &&
            pause is not ("prepared" or "old-retired" or "candidate-active" or "result-published"))
        {
            throw new InvalidDataException("Unknown world-restore fixture pause point.");
        }
        var testRootValue = Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(testRootValue))
        {
            throw new InvalidDataException("Fixture fault injection requires an explicit test root.");
        }
        var testRoot = PathSafety.FullPath(testRootValue, "world-restore fixture root");
        PathSafety.EnsureDirectory(testRoot, create: false);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!PathSafety.IsStrictChild(temp, testRoot) ||
            !new[]
            {
                plan.BackupDirectory,
                plan.ActiveWorldDirectory,
                plan.StagingDirectory,
                plan.SettingsFile,
                plan.PalServerExecutable
            }.All(path => PathSafety.IsStrictChild(testRoot, path)))
        {
            throw new InvalidDataException(
                "Fixture fault injection is restricted to one explicit operating-system temp root.");
        }
    }

    private static void InjectFixtureFaultIfRequested(string point, RestorePlanReport plan)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_FAULT"),
                point,
                StringComparison.Ordinal))
        {
            ValidateTestFaultConfiguration(plan);
            throw new IOException($"Injected fixture fault at {point}.");
        }
    }

    private static void InjectFixtureCrashIfRequested(string point, RestorePlanReport plan)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_CRASH"),
                point,
                StringComparison.Ordinal))
        {
            ValidateTestFaultConfiguration(plan);
            Environment.FailFast($"Injected world-restore fixture crash at durable state {point}.");
        }
    }

    private static void PauseFixtureIfRequested(string point, RestorePlanReport plan)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_PAUSE_AT"),
                point,
                StringComparison.Ordinal))
        {
            ValidateTestFaultConfiguration(plan);
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
    }

    private static void HoldFixtureLockIfRequested(string activeWorldDirectory)
    {
        var raw = Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_HOLD_LOCK_MS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        if (!int.TryParse(raw, out var milliseconds) || milliseconds is < 1 or > 10_000)
        {
            throw new InvalidDataException("Fixture lock hold must be between 1 and 10000 milliseconds.");
        }
        var testRootValue = Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(testRootValue))
        {
            throw new InvalidDataException("Fixture lock hold requires an explicit test root.");
        }
        var testRoot = PathSafety.FullPath(testRootValue, "world-restore fixture root");
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!PathSafety.IsStrictChild(temp, testRoot) ||
            !PathSafety.IsStrictChild(testRoot, activeWorldDirectory))
        {
            throw new InvalidDataException("Fixture lock hold is restricted to one operating-system temp root.");
        }
        Thread.Sleep(milliseconds);
    }

    private static void EnsureP256(ECDsa key, string description)
    {
        ECParameters parameters;
        try
        {
            parameters = key.ExportParameters(includePrivateParameters: false);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"{description} is not an exportable ECDSA key.", exception);
        }
        if (key.KeySize != 256 ||
            !string.Equals(parameters.Curve.Oid.Value, P256CurveOid, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{description} must use the NIST P-256 curve OID {P256CurveOid}.");
        }
    }

    private static byte[] ReadBoundedFile(string path, long maximumBytes)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException("Metadata file has an invalid size.");
        }
        return File.ReadAllBytes(path);
    }

    private static bool InventoriesEqual(
        IReadOnlyList<FileInventoryEntry> left,
        IReadOnlyList<FileInventoryEntry> right)
    {
        try
        {
            PathSafety.AssertInventoriesEqual(left, right, "inventory");
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void ValidateServerId(string serverId)
    {
        if (!SafeIdentityRegex().IsMatch(serverId))
        {
            throw new InvalidDataException("Server ID must be a safe 1 to 64 character identifier.");
        }
    }

    private static void ValidateSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || !SubjectRegex().IsMatch(subject))
        {
            throw new InvalidDataException("Approval subject must be a safe 3 to 128 character identifier.");
        }
    }

    private static void ValidateApprovalReasonAndLifetime(string reason, int validForMinutes)
    {
        if (reason.Trim().Length is < 10 or > 500)
        {
            throw new ArgumentException("Approval reason must contain 10 to 500 characters.");
        }
        if (validForMinutes is < 1 or > 15)
        {
            throw new ArgumentException("Approval lifetime must be between 1 and 15 minutes.");
        }
    }

    private static bool ValidateApprovalTemporalShape(
        SignedApproval approval,
        DateTimeOffset now,
        bool requireCurrent)
    {
        if (approval.Reason is null || approval.Reason != approval.Reason.Trim() ||
            approval.Reason.Length is < 10 or > 500 ||
            approval.IssuedAt == default || approval.ExpiresAt == default ||
            approval.ExpiresAt <= approval.IssuedAt ||
            approval.ExpiresAt - approval.IssuedAt > MaximumApprovalLifetime ||
            approval.IssuedAt > now + MaximumClockSkew)
        {
            return false;
        }
        return !requireCurrent ||
               (approval.IssuedAt >= now - MaximumApprovalLifetime - MaximumClockSkew &&
                approval.ExpiresAt > now);
    }

    private static DateTimeOffset GetRecoveryApprovalIssuanceTime(RestorePlanReport plan)
    {
        var rawAge = Environment.GetEnvironmentVariable(
            "PALCONTROL_WORLD_RESTORE_TEST_RECOVERY_APPROVAL_AGE_MINUTES");
        if (string.IsNullOrWhiteSpace(rawAge))
        {
            return DateTimeOffset.UtcNow;
        }
        if (!int.TryParse(rawAge, out var ageMinutes) || ageMinutes is < 16 or > 60)
        {
            throw new InvalidDataException(
                "Fixture recovery approval age must be between 16 and 60 minutes.");
        }
        var testRootValue = Environment.GetEnvironmentVariable("PALCONTROL_WORLD_RESTORE_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(testRootValue))
        {
            throw new InvalidDataException("Aged fixture approval requires an explicit test root.");
        }
        var testRoot = PathSafety.FullPath(testRootValue, "world-restore fixture root");
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!PathSafety.IsStrictChild(temp, testRoot) ||
            !new[]
            {
                plan.ActiveWorldDirectory,
                plan.StagingDirectory,
                plan.EvidenceDirectory
            }.All(path => PathSafety.IsStrictChild(testRoot, path)))
        {
            throw new InvalidDataException(
                "Aged fixture approval is restricted to one operating-system temp root.");
        }
        return DateTimeOffset.UtcNow.AddMinutes(-ageMinutes);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool WorldGuidsEqual(string first, string second)
    {
        var left = NormalizeWorldGuid(first);
        var right = NormalizeWorldGuid(second);
        return left.Length is >= 16 and <= 64 &&
               string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string NormalizeWorldGuid(string value) => string.Concat(
        value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentityRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@:-]{2,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SubjectRegex();
}

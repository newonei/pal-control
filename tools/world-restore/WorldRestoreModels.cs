using System.Text.Json.Serialization;

namespace PalControl.WorldRestore;

internal sealed record ManagedManifestFile(
    string RelativePath,
    long Length,
    DateTimeOffset LastModifiedAt,
    string Sha256);

internal sealed record ManagedBackupManifest(
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
    IReadOnlyList<ManagedManifestFile> Files);

internal sealed record BackupVerification(
    int SchemaVersion,
    string BackupId,
    string Integrity,
    DateTimeOffset VerifiedAt,
    string ManifestSha256);

internal sealed record RestorePlanReport(
    int SchemaVersion,
    string ReportType,
    string OperationId,
    DateTimeOffset CreatedAt,
    string Mode,
    string ServerId,
    string WorldGuid,
    string BackupId,
    string ManifestSha256,
    string BackupDirectory,
    string ActiveWorldDirectory,
    string SettingsFile,
    string PalServerExecutable,
    string InstallationRoot,
    string StagingDirectory,
    string LockFile,
    string EvidenceDirectory,
    string ApproverTrustStoreSha256,
    int FileCount,
    long TotalBytes,
    InventorySummary OriginalInventory,
    InventorySummary CandidateInventory,
    ProcessGateEvidence PlanProcessGate,
    IReadOnlyDictionary<string, bool> Checks);

internal sealed record InventorySummary(
    int FileCount,
    long TotalBytes,
    string InventorySha256);

internal sealed record ProcessGateEvidence(
    DateTimeOffset CheckedAt,
    string InstallationRoot,
    IReadOnlyList<string> ProcessNames,
    int MatchingProcesses,
    int InaccessibleProcesses);

internal sealed record ApprovalEvidence(
    string Subject,
    string ApprovalFile,
    string ApprovalSha256,
    string KeyFingerprintSha256);

internal sealed record RestoreResultReport(
    int SchemaVersion,
    string ReportType,
    string OperationId,
    DateTimeOffset CompletedAt,
    string Mode,
    string ServerId,
    string WorldGuid,
    string BackupId,
    string ManifestSha256,
    string PlanSha256,
    string ActiveWorldDirectory,
    string RollbackDirectory,
    string RetiredWorldDirectory,
    string BackupDirectory,
    string Phase,
    string JournalFile,
    string JournalSha256BeforeCommit,
    string AuthorizationSnapshotDirectory,
    string TrustStoreFile,
    string TrustStoreSha256,
    IReadOnlyList<ApprovalEvidence> Approvals,
    InventorySummary ActiveInventory,
    InventorySummary RollbackInventory,
    InventorySummary RetiredInventory,
    IReadOnlyList<ProcessGateEvidence> ProcessGates,
    IReadOnlyDictionary<string, bool> Checks);

internal sealed record RestoreFailureReport(
    int SchemaVersion,
    string ReportType,
    string OperationId,
    DateTimeOffset FailedAt,
    string Mode,
    string ErrorType,
    bool OldWorldRecovered,
    string ActiveWorldDirectory,
    string RollbackDirectory,
    string RetiredWorldDirectory,
    string FailedCandidateDirectory,
    string BackupDirectory,
    string Phase,
    string JournalFile,
    string AuthorizationSnapshotDirectory,
    string TrustStoreFile,
    string TrustStoreSha256,
    IReadOnlyList<ApprovalEvidence> Approvals,
    string RecoveryAuthorizationBaseJournalSha256,
    string RecoveryAuthorizationJournalState,
    string RecoveryAuthorizationJournalOutcome,
    IReadOnlyList<ApprovalEvidence> RecoveryApprovals,
    InventorySummary? RollbackInventory,
    InventorySummary? RetiredInventory,
    InventorySummary? FailedCandidateInventory,
    IReadOnlyList<ProcessGateEvidence> ProcessGates);

internal sealed record RestoreJournal(
    int SchemaVersion,
    string ReportType,
    string OperationId,
    DateTimeOffset UpdatedAt,
    string State,
    string Outcome,
    string ServerId,
    string WorldGuid,
    string BackupId,
    string ManifestSha256,
    string PlanSha256,
    string InstallationRoot,
    string ActiveWorldDirectory,
    string StagingDirectory,
    string RollbackDirectory,
    string RetiredWorldDirectory,
    string FailedCandidateDirectory,
    string BackupDirectory,
    string EvidenceDirectory,
    string AuthorizationSnapshotDirectory,
    string TrustStoreFile,
    string TrustStoreSha256,
    IReadOnlyList<ApprovalEvidence> Approvals,
    string RecoveryAuthorizationBaseJournalSha256,
    string RecoveryAuthorizationJournalState,
    string RecoveryAuthorizationJournalOutcome,
    IReadOnlyList<ApprovalEvidence> RecoveryApprovals,
    IReadOnlyList<FileInventoryEntry> OriginalInventory,
    IReadOnlyList<string> OriginalDirectories,
    IReadOnlyList<FileInventoryEntry> CandidateInventory,
    IReadOnlyList<string> CandidateDirectories,
    InventorySummary OriginalInventorySummary,
    InventorySummary CandidateInventorySummary,
    IReadOnlyList<ProcessGateEvidence> ProcessGates,
    string ResultFile,
    string ResultSha256);

internal sealed record VerifiedApprovalSet(
    IReadOnlyList<SignedApproval> Approvals,
    IReadOnlyList<ApprovalEvidence> Evidence,
    string TrustStoreFile,
    string TrustStoreSha256);

internal sealed record ApprovalPayload(
    int SchemaVersion,
    string ApprovalType,
    string ApprovalId,
    string Subject,
    string Reason,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string OperationId,
    string ServerId,
    string WorldGuid,
    string BackupId,
    string ManifestSha256,
    string PlanSha256,
    string TrustStoreSha256,
    string Purpose,
    string JournalSha256,
    string JournalState,
    string JournalOutcome,
    InventorySummary OriginalInventory,
    InventorySummary CandidateInventory);

internal sealed record SignedApproval(
    int SchemaVersion,
    string ApprovalType,
    string ApprovalId,
    string Subject,
    string Reason,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string OperationId,
    string ServerId,
    string WorldGuid,
    string BackupId,
    string ManifestSha256,
    string PlanSha256,
    string TrustStoreSha256,
    string Purpose,
    string JournalSha256,
    string JournalState,
    string JournalOutcome,
    InventorySummary OriginalInventory,
    InventorySummary CandidateInventory,
    string Algorithm,
    string SignatureBase64)
{
    [JsonIgnore]
    public ApprovalPayload Payload => new(
        SchemaVersion,
        ApprovalType,
        ApprovalId,
        Subject,
        Reason,
        IssuedAt,
        ExpiresAt,
        OperationId,
        ServerId,
        WorldGuid,
        BackupId,
        ManifestSha256,
        PlanSha256,
        TrustStoreSha256,
        Purpose,
        JournalSha256,
        JournalState,
        JournalOutcome,
        OriginalInventory,
        CandidateInventory);
}

internal sealed record ApproverTrustStore(
    int SchemaVersion,
    IReadOnlyList<ApproverTrustKey> Keys);

internal sealed record ApproverTrustKey(
    string Subject,
    string Algorithm,
    string PublicKeyPem);

internal sealed record CommandResult(
    string Status,
    string Mode,
    bool Executed,
    string? ReportPath,
    string? ReportSha256,
    string? PlanPath,
    string? PlanSha256,
    string? StagingDirectory,
    string? ActiveWorldDirectory,
    string? RollbackDirectory,
    string? RetiredWorldDirectory,
    string? ApprovalPath,
    string? Subject,
    string? JournalPath = null,
    string? JournalState = null,
    string? JournalOutcome = null);

internal sealed record FileInventoryEntry(string RelativePath, long Length, string Sha256);

internal sealed record InventoryCapture(
    IReadOnlyList<FileInventoryEntry> Files,
    IReadOnlyList<string> Directories,
    InventorySummary Summary);

internal sealed record VerifiedBackup(
    string Root,
    string DataRoot,
    string ManifestPath,
    ManagedBackupManifest Manifest,
    string ManifestSha256,
    IReadOnlyList<FileInventoryEntry> Inventory);

using System.Text;

namespace PalControl.EconomyReconciliation;

public sealed record AuditIssue(
    string Code,
    string Category,
    string? KeyFingerprint,
    string Message);

public sealed record AuditRowHash(
    string Category,
    string KeyFingerprint,
    string CanonicalHash);

public sealed record AuditTableHash(
    string Table,
    long RowCount,
    string CanonicalHash);

public sealed record AuditAccountSummary(
    string AccountFingerprint,
    int WalletScopeCount,
    int LedgerEntryCount,
    int OrderCount,
    int DeliveryCount,
    int RunCount,
    string CanonicalHash);

public sealed record AuditCounts(
    int Accounts,
    int WalletScopes,
    int LedgerEntries,
    int Orders,
    int Deliveries,
    int IdempotencyRecords,
    int SettlementRuns,
    int RunCredits,
    int PhysicalTables,
    long PhysicalRows);

public sealed record AuditBaselineComparison(
    bool Match,
    bool DomainMatch,
    bool PhysicalMatch,
    IReadOnlyList<string> ChangedTables,
    int ChangedRowHashCount);

public sealed record EconomyReconciliationReport(
    int SchemaVersion,
    string CanonicalizationVersion,
    DateTimeOffset GeneratedAtUtc,
    string DatabasePathFingerprint,
    int SqliteUserVersion,
    bool IntegrityOk,
    bool ForeignKeysOk,
    bool DataValid,
    bool Success,
    string DomainCanonicalHash,
    string PhysicalCanonicalHash,
    AuditCounts Counts,
    IReadOnlyList<AuditTableHash> Tables,
    IReadOnlyList<AuditAccountSummary> Accounts,
    IReadOnlyList<AuditRowHash> Rows,
    IReadOnlyList<AuditIssue> Issues,
    AuditBaselineComparison? BaselineComparison);

public static class SafePath
{
    public static string ExistingFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("Required audit input does not exist.", full);
        }
        AssertNoReparsePoint(full);
        return full;
    }

    public static string OutputFile(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
            ?? throw new ArgumentException("The output path has no parent directory.", nameof(path));
        Directory.CreateDirectory(parent);
        AssertNoReparsePoint(parent);
        if (File.Exists(full))
        {
            AssertNoReparsePoint(full);
        }
        return full;
    }

    private static void AssertNoReparsePoint(string path)
    {
        var cursor = File.Exists(path) || Directory.Exists(path)
            ? path
            : Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(cursor))
        {
            var attributes = File.GetAttributes(cursor);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Audit paths cannot traverse a reparse point.");
            }
            var parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            cursor = parent;
        }
    }
}

public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var full = SafePath.OutputFile(path);
        var temporary = $"{full}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        try
        {
            if (File.Exists(full))
            {
                File.Move(temporary, full, overwrite: true);
            }
            else
            {
                File.Move(temporary, full);
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

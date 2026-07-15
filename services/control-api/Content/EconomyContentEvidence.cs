namespace PalControl.ControlApi.Content;

/// <summary>
/// Compares client/server evidence captured from an immutable content version
/// with the version that is current at commit time.
/// </summary>
public static class EconomyContentEvidence
{
    public static bool MatchesCurrent(
        Guid? capturedVersionId,
        string? capturedContentHash,
        EconomyContentVersion current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return capturedVersionId is Guid versionId &&
               versionId != Guid.Empty &&
               versionId == current.VersionId &&
               !string.IsNullOrWhiteSpace(capturedContentHash) &&
               string.Equals(
                   capturedContentHash.Trim(),
                   current.ContentHash,
                   StringComparison.OrdinalIgnoreCase);
    }
}

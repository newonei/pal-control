using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Extraction;

/// <summary>
/// Configuration for the extraction-mode RCON adapter. RCON is deliberately
/// disabled unless it is explicitly enabled and can only target a loopback
/// address.
/// </summary>
public sealed class ExtractionRconOptions
{
    public bool Enabled { get; init; }

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 25575;

    public int TimeoutSeconds { get; init; } = 5;

    public string ApprovedGameVersion { get; init; } = "1.0.0.100427";

    public string ApprovedPalDefenderVersion { get; init; } = "1.8.1.3933";

    /// <summary>
    /// The RCON password. Prefer <see cref="PasswordFile"/> outside local
    /// development. Configuring both sources is rejected as ambiguous.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Path to a UTF-8/ASCII text file containing only the RCON password. One
    /// trailing CR/LF sequence is ignored.
    /// </summary>
    public string? PasswordFile { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<RconOperationOutcome>))]
public enum RconOperationOutcome
{
    Success,
    Failed,
    Uncertain
}

/// <summary>
/// Result of an allow-listed RCON operation. An Uncertain write may already
/// have changed the game state and must be reconciled by a fresh inventory
/// read; callers must never automatically retry it.
/// </summary>
public sealed record RconOperationResult(
    RconOperationOutcome Outcome,
    string? Response,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Success => Outcome == RconOperationOutcome.Success;

    public bool Uncertain => Outcome == RconOperationOutcome.Uncertain;

    public bool RequiresReconciliation => Uncertain;

    // This adapter intentionally has no automatic retry path. In particular,
    // a write that lost its response cannot safely be issued a second time.
    public bool AutomaticRetryAllowed => false;

    public static RconOperationResult Succeeded(string response) =>
        new(RconOperationOutcome.Success, response, null, null);

    public static RconOperationResult Rejected(
        string code,
        string message,
        string? response = null) =>
        new(RconOperationOutcome.Failed, response, code, message);

    public static RconOperationResult OutcomeUncertain(
        string code,
        string message,
        string? response = null) =>
        new(RconOperationOutcome.Uncertain, response, code, message);
}

public sealed record RconItemDeletion(string ItemId, int Quantity);

[JsonConverter(typeof(JsonStringEnumConverter<RconInventoryContainer>))]
public enum RconInventoryContainer
{
    Items,
    KeyItems,
    Armor,
    Weapons,
    Food,
    DropSlot,
    All
}

/// <summary>
/// Narrow contract exposed to the extraction settlement service. There is no
/// API for arbitrary RCON text.
/// </summary>
public interface IExtractionRconAdapter
{
    Task<RconOperationResult> GetCommandsAsync(CancellationToken cancellationToken);

    Task<RconOperationResult> GetVersionAsync(CancellationToken cancellationToken);

    Task<RconOperationResult> SendPrivateInfoMessageAsync(
        string userId,
        string message,
        CancellationToken cancellationToken);

    Task<RconOperationResult> DeleteItemsAsync(
        string userId,
        IReadOnlyCollection<RconItemDeletion> items,
        CancellationToken cancellationToken);

    Task<RconOperationResult> ClearContainersAsync(
        string userId,
        IReadOnlyCollection<RconInventoryContainer> containers,
        CancellationToken cancellationToken);
}

public static class RconCapabilityCatalog
{
    public static bool ContainsExact(string? response, string capability)
    {
        if (string.IsNullOrWhiteSpace(response) || string.IsNullOrWhiteSpace(capability))
        {
            return false;
        }

        // PalDefender versions have emitted semicolon-, comma-, pipe-, newline-,
        // and whitespace-delimited command catalogs. Never use substring matching:
        // every resulting token must still equal the complete approved capability.
        return response
            .Split(
                [';', ',', '|', '\r', '\n', '\t', ' ', '"', '\'', '[', ']', '{', '}', '(', ')'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, capability, StringComparison.OrdinalIgnoreCase));
    }
}

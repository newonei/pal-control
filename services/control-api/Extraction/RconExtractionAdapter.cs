using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Extraction;

/// <summary>
/// Generates only the PalDefender commands needed by extraction settlement.
/// All arguments are token-validated before command construction.
/// </summary>
public sealed partial class ExtractionRconAdapter : IExtractionRconAdapter
{
    private const int MaximumDeleteEntries = 100;
    private const int MaximumItemQuantity = 1_000_000;
    private const int MaximumPrivateMessageBytes = 720;
    private const int MaximumCommandBytes = 4_000;
    private const int MaximumPasswordFileBytes = 4_096;

    private static readonly IReadOnlyDictionary<RconInventoryContainer, string> ContainerTokens =
        new Dictionary<RconInventoryContainer, string>
        {
            [RconInventoryContainer.Items] = "items",
            [RconInventoryContainer.KeyItems] = "keyitems",
            [RconInventoryContainer.Armor] = "armor",
            [RconInventoryContainer.Weapons] = "weapons",
            [RconInventoryContainer.Food] = "food",
            [RconInventoryContainer.DropSlot] = "dropslot",
            [RconInventoryContainer.All] = "all"
        };

    private readonly ExtractionRconOptions _options;

    public ExtractionRconAdapter(IOptions<ExtractionRconOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public Task<RconOperationResult> GetCommandsAsync(CancellationToken cancellationToken) =>
        ExecuteAsync("getrconcmds", isWrite: false, cancellationToken);

    public Task<RconOperationResult> GetVersionAsync(CancellationToken cancellationToken) =>
        ExecuteAsync("version", isWrite: false, cancellationToken);

    public Task<RconOperationResult> SendPrivateInfoMessageAsync(
        string userId,
        string message,
        CancellationToken cancellationToken)
    {
        var userValidation = ValidateUserId(userId);
        if (userValidation is not null)
        {
            return Task.FromResult(userValidation);
        }
        if (string.IsNullOrWhiteSpace(message) ||
            !PrivateMessagePattern().IsMatch(message) ||
            Encoding.UTF8.GetByteCount(message) > MaximumPrivateMessageBytes)
        {
            return Task.FromResult(RconOperationResult.Rejected(
                "rcon_invalid_private_message",
                "The private message must contain 1 to 240 approved printable Unicode characters."));
        }

        return ValidateLengthAndExecute(
            $"send ilog {userId} {message}",
            isWrite: true,
            cancellationToken);
    }

    public Task<RconOperationResult> DeleteItemsAsync(
        string userId,
        IReadOnlyCollection<RconItemDeletion> items,
        CancellationToken cancellationToken)
    {
        var userValidation = ValidateUserId(userId);
        if (userValidation is not null)
        {
            return Task.FromResult(userValidation);
        }

        if (items is null || items.Count == 0)
        {
            return Task.FromResult(RconOperationResult.Rejected(
                "rcon_items_required",
                "At least one item deletion is required."));
        }

        if (items.Count > MaximumDeleteEntries)
        {
            return Task.FromResult(RconOperationResult.Rejected(
                "rcon_too_many_items",
                $"A single deletion may contain at most {MaximumDeleteEntries} item entries."));
        }

        var seenItemIds = new HashSet<string>(StringComparer.Ordinal);
        var itemTokens = new List<string>(items.Count);
        foreach (var item in items)
        {
            if (item is null || !ItemIdPattern().IsMatch(item.ItemId))
            {
                return Task.FromResult(RconOperationResult.Rejected(
                    "rcon_invalid_item_id",
                    "An item ID is invalid."));
            }

            if (!seenItemIds.Add(item.ItemId))
            {
                return Task.FromResult(RconOperationResult.Rejected(
                    "rcon_duplicate_item_id",
                    "Duplicate item IDs are not allowed in one deletion."));
            }

            if (item.Quantity is < 1 or > MaximumItemQuantity)
            {
                return Task.FromResult(RconOperationResult.Rejected(
                    "rcon_invalid_item_quantity",
                    $"Each deletion quantity must be between 1 and {MaximumItemQuantity}."));
            }

            itemTokens.Add($"{item.ItemId}:{item.Quantity}");
        }

        var command = $"delitems {userId} {string.Join(' ', itemTokens)}";
        return ValidateLengthAndExecute(command, isWrite: true, cancellationToken);
    }

    public Task<RconOperationResult> ClearContainersAsync(
        string userId,
        IReadOnlyCollection<RconInventoryContainer> containers,
        CancellationToken cancellationToken)
    {
        var userValidation = ValidateUserId(userId);
        if (userValidation is not null)
        {
            return Task.FromResult(userValidation);
        }

        if (containers is null || containers.Count == 0)
        {
            return Task.FromResult(RconOperationResult.Rejected(
                "rcon_containers_required",
                "At least one inventory container is required."));
        }

        var uniqueContainers = new HashSet<RconInventoryContainer>();
        var tokens = new List<string>(containers.Count);
        foreach (var container in containers)
        {
            if (!ContainerTokens.TryGetValue(container, out var token))
            {
                return Task.FromResult(RconOperationResult.Rejected(
                    "rcon_invalid_container",
                    "An inventory container is invalid."));
            }

            if (!uniqueContainers.Add(container))
            {
                return Task.FromResult(RconOperationResult.Rejected(
                    "rcon_duplicate_container",
                    "Duplicate inventory containers are not allowed."));
            }

            tokens.Add(token);
        }

        if (uniqueContainers.Contains(RconInventoryContainer.All) && uniqueContainers.Count != 1)
        {
            return Task.FromResult(RconOperationResult.Rejected(
                "rcon_all_container_must_be_exclusive",
                "The all container cannot be combined with another container."));
        }

        var command = $"clearinv {userId} {string.Join(' ', tokens)}";
        return ValidateLengthAndExecute(command, isWrite: true, cancellationToken);
    }

    private Task<RconOperationResult> ValidateLengthAndExecute(
        string command,
        bool isWrite,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(command) > MaximumCommandBytes)
        {
            return Task.FromResult(RconOperationResult.Rejected(
                "rcon_command_too_long",
                "The generated allow-listed command exceeds the safe packet size."));
        }

        return ExecuteAsync(command, isWrite, cancellationToken);
    }

    private async Task<RconOperationResult> ExecuteAsync(
        string command,
        bool isWrite,
        CancellationToken cancellationToken)
    {
        var settingsResult = await TryCreateSettingsAsync(cancellationToken);
        if (settingsResult.Error is not null)
        {
            return settingsResult.Error;
        }

        var transport = new SourceRconTransport(settingsResult.Settings!);
        return await transport.ExecuteAsync(command, isWrite, cancellationToken);
    }

    private async Task<RconSettingsResult> TryCreateSettingsAsync(
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return RconSettingsResult.FromError(RconOperationResult.Rejected(
                "rcon_disabled",
                "Extraction RCON is disabled."));
        }

        if (!TryParseLoopback(_options.Host, out var address))
        {
            return RconSettingsResult.FromError(RconOperationResult.Rejected(
                "rcon_host_not_loopback",
                "The RCON host must be an explicit loopback address or localhost."));
        }

        if (_options.Port is < 1 or > 65_535)
        {
            return RconSettingsResult.FromError(RconOperationResult.Rejected(
                "rcon_invalid_port",
                "The RCON port must be between 1 and 65535."));
        }

        if (_options.TimeoutSeconds is < 1 or > 60)
        {
            return RconSettingsResult.FromError(RconOperationResult.Rejected(
                "rcon_invalid_timeout",
                "The RCON timeout must be between 1 and 60 seconds."));
        }

        var hasInlinePassword = !string.IsNullOrEmpty(_options.Password);
        var hasPasswordFile = !string.IsNullOrWhiteSpace(_options.PasswordFile);
        if (hasInlinePassword == hasPasswordFile)
        {
            return RconSettingsResult.FromError(RconOperationResult.Rejected(
                "rcon_password_source_invalid",
                "Configure exactly one RCON password source."));
        }

        string password;
        if (hasPasswordFile)
        {
            try
            {
                var file = new FileInfo(_options.PasswordFile!);
                if (!file.Exists || file.Length is < 1 or > MaximumPasswordFileBytes)
                {
                    return RconSettingsResult.FromError(RconOperationResult.Rejected(
                        "rcon_password_file_invalid",
                        "The RCON password file is unavailable or has an invalid size."));
                }

                password = await File.ReadAllTextAsync(file.FullName, cancellationToken);
                password = password.TrimEnd('\r', '\n');
            }
            catch (OperationCanceledException)
            {
                return RconSettingsResult.FromError(RconOperationResult.Rejected(
                    "rcon_operation_cancelled",
                    "The RCON operation was cancelled before dispatch."));
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    ArgumentException or
                    System.Security.SecurityException)
            {
                return RconSettingsResult.FromError(RconOperationResult.Rejected(
                    "rcon_password_file_unavailable",
                    "The RCON password file could not be read."));
            }
        }
        else
        {
            password = _options.Password!;
        }

        if (!IsSafePassword(password))
        {
            return RconSettingsResult.FromError(RconOperationResult.Rejected(
                "rcon_password_invalid",
                "The RCON password must contain 1 to 256 printable ASCII characters."));
        }

        return RconSettingsResult.FromSettings(new SourceRconSettings(
            address!,
            _options.Port,
            TimeSpan.FromSeconds(_options.TimeoutSeconds),
            password));
    }

    private static RconOperationResult? ValidateUserId(string userId)
    {
        return !string.IsNullOrEmpty(userId) && UserIdPattern().IsMatch(userId)
            ? null
            : RconOperationResult.Rejected(
                "rcon_invalid_user_id",
                "The PalDefender user ID is invalid.");
    }

    private static bool TryParseLoopback(string host, out IPAddress? address)
    {
        address = null;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
            return true;
        }

        return IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
    }

    private static bool IsSafePassword(string password)
    {
        if (password.Length is < 1 or > 256)
        {
            return false;
        }

        return password.All(character => character is >= ' ' and <= '~');
    }

    [GeneratedRegex(
        "^(?:steam|gdk|xbox|xuid|epic)_[A-Za-z0-9]{3,64}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UserIdPattern();

    [GeneratedRegex(
        "^[A-Za-z0-9_][A-Za-z0-9_-]{0,127}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ItemIdPattern();

    [GeneratedRegex(
        "^[\\p{L}\\p{N} _.,:，。！？：、（）【】《》“”‘’\\-]{1,240}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrivateMessagePattern();

    private sealed record RconSettingsResult(
        SourceRconSettings? Settings,
        RconOperationResult? Error)
    {
        public static RconSettingsResult FromSettings(SourceRconSettings settings) =>
            new(settings, null);

        public static RconSettingsResult FromError(RconOperationResult error) =>
            new(null, error);
    }
}

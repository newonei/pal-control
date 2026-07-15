using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Controls fail-fast checks that protect the local trust boundary. Runtime
/// dependency probes deliberately do not belong here: an offline Palworld
/// server, RCON endpoint, PalDefender instance, or Native bridge must not stop
/// the read-only portal from starting.
/// </summary>
public sealed class StartupSecurityValidationOptions
{
    public bool Strict { get; init; }

    public bool AllowNonLoopbackListenerBehindTrustedProxy { get; init; }

    public string[] TrustedProxyAddresses { get; init; } = [];

    public string LogDirectory { get; init; } = "logs";
}

public sealed class StartupSecurityValidator : IValidateOptions<StartupSecurityValidationOptions>
{
    private const long MaximumSecretFileBytes = 64 * 1024;

    private static readonly HashSet<string> BroadPrincipalSids =
    [
        "S-1-1-0",       // Everyone
        "S-1-5-4",       // Interactive
        "S-1-5-7",       // Anonymous
        "S-1-5-11",      // Authenticated Users
        "S-1-5-32-545",  // BUILTIN\Users
        "S-1-5-32-546"   // BUILTIN\Guests
    ];

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public StartupSecurityValidator(
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public ValidateOptionsResult Validate(
        string? name,
        StartupSecurityValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var explicitDevelopmentMode = _configuration.GetValue<bool>(
            "Security:DevelopmentMode");
        var strict = options.Strict ||
            (_environment.IsProduction() && !explicitDevelopmentMode);

        ValidateListener(options, failures);
        ValidatePalworldRest(failures);
        ValidateSaveManagement(failures);
        ValidateConditionalAdapters(strict, failures);
        ValidateFilesystemTopology(options, strict, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<IPAddress> ParseTrustedProxyAddresses(
        StartupSecurityValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var addresses = new List<IPAddress>();
        foreach (var configured in options.TrustedProxyAddresses)
        {
            if (TryParseSafeProxyAddress(configured, out var address))
            {
                addresses.Add(address);
            }
        }
        return addresses;
    }

    private void ValidateListener(
        StartupSecurityValidationOptions options,
        ICollection<string> failures)
    {
        var listeners = new List<string>();
        var urls = _configuration["Urls"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            listeners.AddRange(urls.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        foreach (var endpoint in _configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { Length: > 0 } endpointUrl)
            {
                listeners.Add(endpointUrl);
            }
        }

        var hasNonLoopbackListener = false;
        foreach (var listener in listeners.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseListener(listener, out var isLoopback, out var error))
            {
                failures.Add($"Listener '{listener}' is invalid: {error}");
                continue;
            }
            hasNonLoopbackListener |= !isLoopback;
        }

        if (!hasNonLoopbackListener)
        {
            return;
        }
        if (!options.AllowNonLoopbackListenerBehindTrustedProxy)
        {
            failures.Add(
                "A non-loopback listener requires Security:StartupValidation:" +
                "AllowNonLoopbackListenerBehindTrustedProxy=true and explicit trusted proxy addresses.");
            return;
        }
        if (!_configuration.GetValue<bool>("PlayerPortal:Enabled") ||
            !_configuration.GetValue<bool>("PlayerPortal:CookieSecure"))
        {
            failures.Add(
                "A non-loopback reverse-proxy listener requires an enabled PlayerPortal with CookieSecure=true.");
        }
        if (options.TrustedProxyAddresses.Length == 0)
        {
            failures.Add(
                "A non-loopback reverse-proxy listener requires at least one exact TrustedProxyAddresses entry.");
        }
        foreach (var configured in options.TrustedProxyAddresses)
        {
            if (!TryParseSafeProxyAddress(configured, out _))
            {
                failures.Add(
                    $"Trusted proxy address '{configured}' must be an exact, unicast IP address.");
            }
        }
    }

    private void ValidatePalworldRest(ICollection<string> failures)
    {
        var options = Bind<PalworldRestOptions>("Palworld:OfficialRestApi");
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttp ||
            !baseUri.IsLoopback ||
            !baseUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            failures.Add(
                "Palworld:OfficialRestApi:BaseUrl must be an absolute loopback HTTP URL ending with '/'.");
        }
        if (string.IsNullOrWhiteSpace(options.Username) || options.Username.Length > 128)
        {
            failures.Add(
                "Palworld:OfficialRestApi:Username must contain 1-128 characters.");
        }
        if (options.TimeoutSeconds is < 1 or > 30)
        {
            failures.Add(
                "Palworld:OfficialRestApi:TimeoutSeconds must be between 1 and 30.");
        }
    }

    private void ValidateSaveManagement(ICollection<string> failures)
    {
        var options = Bind<SaveManagementOptions>("SaveManagement");
        if (options.SnapshotTimeoutSeconds is < 1 or > 600)
        {
            failures.Add("SaveManagement:SnapshotTimeoutSeconds must be between 1 and 600.");
        }
        if (options.StabilitySampleMilliseconds is < 50 or > 30_000)
        {
            failures.Add(
                "SaveManagement:StabilitySampleMilliseconds must be between 50 and 30000.");
        }
        if (options.StabilityRequiredSamples is < 1 or > 100)
        {
            failures.Add("SaveManagement:StabilityRequiredSamples must be between 1 and 100.");
        }
        if (options.MinimumFreeSpaceBytes is < 16_777_216 or > 1_125_899_906_842_624)
        {
            failures.Add(
                "SaveManagement:MinimumFreeSpaceBytes must be between 16 MiB and 1 PiB.");
        }
    }

    private void ValidateConditionalAdapters(
        bool strict,
        ICollection<string> failures)
    {
        var rcon = Bind<ExtractionRconOptions>("ExtractionMode:Rcon");
        if (rcon.Enabled)
        {
            if (!rcon.IsValid(out var error))
            {
                failures.Add(error ?? "Enabled extraction RCON configuration is invalid.");
            }
            else if (!string.IsNullOrWhiteSpace(rcon.PasswordFile))
            {
                ValidateSecretFile(
                    "ExtractionMode:Rcon:PasswordFile",
                    rcon.PasswordFile,
                    strict,
                    failures);
            }
        }

        var safety = Bind<EconomySafetyOptions>("ExtractionMode:Safety");
        if (rcon.AllowDevelopmentSettlement)
        {
            foreach (var violation in DevelopmentRconSettlementPolicy.GetViolations(
                         _environment,
                         _configuration,
                         rcon,
                         safety))
            {
                failures.Add(
                    "ExtractionMode:Rcon:AllowDevelopmentSettlement=true is unsafe: " +
                    violation + ".");
            }
        }

        var palDefender = Bind<PalDefenderRestOptions>("Palworld:PalDefenderRestApi");
        if (palDefender.Enabled)
        {
            if (!palDefender.IsValid(out var error))
            {
                failures.Add(error ?? "Enabled PalDefender REST configuration is invalid.");
            }
            var hasInlineToken = !string.IsNullOrWhiteSpace(palDefender.Token);
            var hasTokenFile = !string.IsNullOrWhiteSpace(palDefender.TokenFile);
            if (hasInlineToken == hasTokenFile)
            {
                failures.Add(
                    "An enabled PalDefender REST adapter requires exactly one token source.");
            }
            else if (hasTokenFile)
            {
                ValidateSecretFile(
                    "Palworld:PalDefenderRestApi:TokenFile",
                    palDefender.TokenFile,
                    strict,
                    failures);
            }
        }

        if (safety.RequireNativeForPurchase || safety.RequireNativeForResourceExchange)
        {
            var native = Bind<NativeBridgeOptions>("Palworld:Bridge");
            if (string.IsNullOrWhiteSpace(native.PipeName) ||
                native.PipeName.Length > 128 ||
                native.PipeName.Any(character =>
                    char.IsControl(character) || character is '/' or '\\'))
            {
                failures.Add(
                    "An enabled Native adapter requires a simple PipeName of 1-128 characters.");
            }
            if (native.ConnectTimeoutSeconds is < 1 or > 30 ||
                native.CommandTimeoutSeconds is < 1 or > 120)
            {
                failures.Add(
                    "An enabled Native adapter requires a 1-30 second connect timeout and a 1-120 second command timeout.");
            }
            if (native.MaxFrameBytes is < 1_024 or > 16_777_216)
            {
                failures.Add(
                    "An enabled Native adapter requires MaxFrameBytes between 1 KiB and 16 MiB.");
            }
        }
    }

    private void ValidateFilesystemTopology(
        StartupSecurityValidationOptions validationOptions,
        bool strict,
        ICollection<string> failures)
    {
        var command = Bind<CommandPersistenceOptions>("CommandPersistence");
        var extraction = Bind<ExtractionPersistenceOptions>("ExtractionMode:Persistence");
        var save = Bind<SaveManagementOptions>("SaveManagement");
        var continuity = Bind<EconomyContinuityOptions>("ExtractionMode:Continuity");

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        AddResolvedPath(paths, "command/audit data", command.DataDirectory, failures);
        AddResolvedPath(paths, "extraction/audit data", extraction.DataDirectory, failures);
        AddResolvedPath(paths, "save backup", save.BackupRoot, failures);
        AddResolvedPath(paths, "economy backup", continuity.BackupRoot, failures);
        AddResolvedPath(paths, "economy staging", continuity.StagingRoot, failures);
        AddResolvedPath(paths, "logs", validationOptions.LogDirectory, failures);
        AddResolvedPath(
            paths,
            "Palworld install",
            _configuration["Palworld:InstallRoot"] ?? string.Empty,
            failures);

        string contentRoot;
        try
        {
            contentRoot = Path.GetFullPath(_environment.ContentRootPath);
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            failures.Add("The application content/configuration root is not a valid path.");
            return;
        }

        string webRoot;
        try
        {
            webRoot = string.IsNullOrWhiteSpace(_environment.WebRootPath)
                ? Path.Combine(contentRoot, "wwwroot")
                : Path.GetFullPath(_environment.WebRootPath);
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            failures.Add("The static web root is not a valid path.");
            return;
        }
        var writableLabels = new[]
        {
            "command/audit data",
            "extraction/audit data",
            "save backup",
            "economy backup",
            "economy staging",
            "logs"
        };
        foreach (var label in writableLabels)
        {
            if (!paths.TryGetValue(label, out var path))
            {
                continue;
            }
            if (Overlaps(path, webRoot))
            {
                failures.Add($"The {label} directory must not overlap the static web root.");
            }
            if (paths.TryGetValue("Palworld install", out var installRoot) &&
                Overlaps(path, installRoot))
            {
                failures.Add($"The {label} directory must not overlap the Palworld install/save tree.");
            }
        }

        var isolatedLabels = new[]
        {
            "save backup",
            "economy backup",
            "economy staging"
        };
        for (var first = 0; first < isolatedLabels.Length; first++)
        {
            for (var second = first + 1; second < isolatedLabels.Length; second++)
            {
                if (paths.TryGetValue(isolatedLabels[first], out var firstPath) &&
                    paths.TryGetValue(isolatedLabels[second], out var secondPath) &&
                    Overlaps(firstPath, secondPath))
                {
                    failures.Add(
                        $"The {isolatedLabels[first]} and {isolatedLabels[second]} directories must not overlap.");
                }
            }
        }

        if (paths.TryGetValue("economy staging", out var stagingPath))
        {
            foreach (var activeLabel in new[] { "command/audit data", "extraction/audit data" })
            {
                if (paths.TryGetValue(activeLabel, out var activePath) &&
                    Overlaps(stagingPath, activePath))
                {
                    failures.Add($"The economy staging and {activeLabel} directories must not overlap.");
                }
            }
        }

        var localConfiguration = Path.Combine(contentRoot, "appsettings.Local.json");
        if (IsContained(webRoot, localConfiguration))
        {
            failures.Add("appsettings.Local.json must not be placed under the static web root.");
        }
        foreach (var label in writableLabels)
        {
            if (paths.TryGetValue(label, out var path) && IsContained(path, localConfiguration))
            {
                failures.Add($"appsettings.Local.json must not be stored in the {label} directory.");
            }
        }

        if (!strict || !OperatingSystem.IsWindows())
        {
            return;
        }

        ValidateWindowsDirectoryAcl(
            contentRoot,
            "application configuration directory",
            privateRead: false,
            failures);
        foreach (var label in writableLabels)
        {
            if (paths.TryGetValue(label, out var path))
            {
                ValidateWindowsDirectoryAcl(path, label, privateRead: true, failures);
            }
        }
        ValidateWindowsConfigurationFiles(contentRoot, failures);
    }

    private void AddResolvedPath(
        IDictionary<string, string> paths,
        string label,
        string configured,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            configured.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            failures.Add($"The {label} path must be a valid non-empty path.");
            return;
        }
        try
        {
            paths[label] = Path.GetFullPath(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(_environment.ContentRootPath, configured));
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            failures.Add($"The {label} path could not be normalized.");
        }
    }

    private static void ValidateSecretFile(
        string settingName,
        string? configuredPath,
        bool strict,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathRooted(configuredPath))
        {
            failures.Add($"{settingName} must be an absolute path when its adapter is enabled.");
            return;
        }

        string path;
        try
        {
            path = Path.GetFullPath(configuredPath);
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            failures.Add($"{settingName} could not be normalized.");
            return;
        }
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 1 or > MaximumSecretFileBytes)
            {
                failures.Add($"{settingName} must reference an existing, non-empty secret file up to 64 KiB.");
                return;
            }
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                failures.Add($"{settingName} must not reference a reparse point or symbolic link.");
                return;
            }
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{settingName} must reference a readable secret file.");
            return;
        }

        if (strict && OperatingSystem.IsWindows())
        {
            ValidateWindowsFileAcl(path, settingName, failures);
            ValidateWindowsDirectoryAcl(
                Path.GetDirectoryName(path) ?? Path.GetPathRoot(path) ?? path,
                $"{settingName} parent directory",
                privateRead: false,
                failures);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsDirectoryAcl(
        string path,
        string label,
        bool privateRead,
        ICollection<string> failures)
    {
        var target = new DirectoryInfo(path);
        while (!target.Exists && target.Parent is not null)
        {
            target = target.Parent;
        }
        if (!target.Exists)
        {
            failures.Add($"The {label} directory has no existing ACL-bearing ancestor.");
            return;
        }
        try
        {
            if ((target.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                failures.Add($"The {label} directory must not resolve through a reparse point.");
                return;
            }
            var security = target.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (HasWeakAcl(security, privateRead))
            {
                failures.Add(
                    $"The {label} directory ACL grants broad {(privateRead ? "access" : "write access")} to untrusted local principals.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SystemException)
        {
            failures.Add($"The {label} directory ACL could not be verified.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsConfigurationFiles(
        string contentRoot,
        ICollection<string> failures)
    {
        try
        {
            var candidates = Directory
                .EnumerateFiles(contentRoot, "appsettings*.json", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(
                    contentRoot,
                    "appsettings.Local.json.*.bak",
                    SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                ValidateWindowsFileAcl(candidate, Path.GetFileName(candidate), failures);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add("Application configuration-file ACLs could not be enumerated.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsFileAcl(
        string path,
        string label,
        ICollection<string> failures)
    {
        try
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (HasWeakAcl(security, privateRead: true))
            {
                failures.Add(
                    $"The {label} ACL grants access to broad, untrusted local principals.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SystemException)
        {
            failures.Add($"The {label} ACL could not be verified.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasWeakAcl(FileSystemSecurity security, bool privateRead)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty;
        if (BroadPrincipalSids.Contains(owner))
        {
            return true;
        }
        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                !BroadPrincipalSids.Contains(rule.IdentityReference.Value))
            {
                continue;
            }
            if (privateRead || (rule.FileSystemRights & BroadWriteRights) != 0)
            {
                return true;
            }
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemRights BroadWriteRights =>
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.WriteAttributes |
        FileSystemRights.Delete |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    private T Bind<T>(string section) where T : new() =>
        _configuration.GetSection(section).Get<T>() ?? new T();

    private static bool TryParseListener(
        string configured,
        out bool isLoopback,
        out string error)
    {
        isLoopback = false;
        error = string.Empty;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "only absolute HTTP/HTTPS URLs are supported";
            return false;
        }
        if (uri.Host is "*" or "+" ||
            string.Equals(uri.Host, "0.0.0.0", StringComparison.Ordinal) ||
            string.Equals(uri.Host, "::", StringComparison.Ordinal))
        {
            return true;
        }
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            isLoopback = true;
            return true;
        }
        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            error = "the listener host must be localhost or a literal IP address";
            return false;
        }
        isLoopback = IPAddress.IsLoopback(address);
        return true;
    }

    private static bool TryParseSafeProxyAddress(
        string configured,
        out IPAddress address)
    {
        if (!IPAddress.TryParse(configured, out address!))
        {
            return false;
        }
        if (address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) ||
            address.IsIPv6Multicast)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            (bytes[0] & 0xf0) != 0xe0;
    }

    private static bool Overlaps(string first, string second) =>
        PathsEqual(first, second) || IsContained(first, second) || IsContained(second, first);

    private static bool IsContained(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.Length > fullRoot.Length &&
            fullCandidate.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                PathComparison);
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsPathException(Exception exception) =>
        exception is ArgumentException or NotSupportedException or PathTooLongException;
}

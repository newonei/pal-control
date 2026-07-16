using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public sealed record FederationOptions
{
    public bool Enabled { get; init; }

    public string LocalServerId { get; init; } = "local";

    public string MatrixPath { get; init; } =
        "Compatibility/compatibility-matrix.v1.json";

    public string ExpectedMatrixSha256 { get; init; } = string.Empty;

    public bool AllowExperimentalInDevelopment { get; init; }

    public string IdentityHmacKey { get; init; } = string.Empty;

    public string IdentityHmacKeyFile { get; init; } = string.Empty;

    public string InboundNodeKey { get; init; } = string.Empty;

    public string InboundNodeKeyFile { get; init; } = string.Empty;

    public int RequestTimeoutMilliseconds { get; init; } = 2_000;

    public int MaximumResponseBytes { get; init; } = 32 * 1024;

    public int MaximumRequestBodyBytes { get; init; } = 2 * 1024;

    public int MaximumConcurrentRequests { get; init; } = 8;

    public int InternalRequestsPerMinute { get; init; } = 600;

    public FederationNodeOptions[] Nodes { get; init; } = [];
}

public sealed record FederationNodeOptions
{
    public string ServerId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool Local { get; init; }

    public string BaseUri { get; init; } = string.Empty;

    public string PortalUrl { get; init; } = string.Empty;

    public string ExpectedCombinationId { get; init; } = string.Empty;

    public string NodeKey { get; init; } = string.Empty;

    public string NodeKeyFile { get; init; } = string.Empty;
}

public sealed partial class FederationOptionsValidator : IValidateOptions<FederationOptions>
{
    private readonly IHostEnvironment _environment;

    public FederationOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, FederationOptions options)
    {
        try
        {
            ValidateCore(options, _environment.ContentRootPath, _environment.IsDevelopment());
            return ValidateOptionsResult.Success;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                UnauthorizedAccessException or CompatibilityMatrixException)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }

    public static CompatibilityMatrixSnapshot ValidateCore(
        FederationOptions options,
        string contentRootPath,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        var matrixPath = ResolvePath(contentRootPath, options.MatrixPath);
        var matrix = CompatibilityMatrixValidator.Load(
            matrixPath,
            string.IsNullOrWhiteSpace(options.ExpectedMatrixSha256)
                ? null
                : options.ExpectedMatrixSha256);

        if (!options.Enabled)
        {
            return matrix;
        }

        if (!ServerIdPattern().IsMatch(options.LocalServerId))
        {
            throw new ArgumentException(
                "Federation LocalServerId must be a lowercase safe server identifier.");
        }
        if (options.RequestTimeoutMilliseconds is < 100 or > 10_000 ||
            options.MaximumResponseBytes is < 1_024 or > 256 * 1_024 ||
            options.MaximumRequestBodyBytes is < 256 or > 16 * 1_024 ||
            options.MaximumConcurrentRequests is < 1 or > 64 ||
            options.InternalRequestsPerMinute is < 10 or > 10_000)
        {
            throw new ArgumentException(
                "Federation timeout, body, response, concurrency, or rate limits are outside safe bounds.");
        }
        if (options.Nodes is null || options.Nodes.Length is < 1 or > 16)
        {
            throw new ArgumentException("Federation requires 1 to 16 configured nodes.");
        }
        if (!isDevelopment && !Sha256Pattern().IsMatch(options.ExpectedMatrixSha256))
        {
            throw new ArgumentException(
                "Production federation must pin ExpectedMatrixSha256.");
        }

        var identityKey = FederationSecretResolver.ResolveRequired(
            options.IdentityHmacKey,
            options.IdentityHmacKeyFile,
            contentRootPath,
            "Federation identity HMAC key");
        var inboundKey = FederationSecretResolver.ResolveRequired(
            options.InboundNodeKey,
            options.InboundNodeKeyFile,
            contentRootPath,
            "Federation inbound node key");
        if (FederationSecretResolver.FixedTimeEquals(identityKey, inboundKey))
        {
            throw new ArgumentException(
                "Federation identity HMAC and node authentication keys must be different.");
        }

        var serverIds = new HashSet<string>(StringComparer.Ordinal);
        var baseUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localCount = 0;
        foreach (var node in options.Nodes)
        {
            if (!ServerIdPattern().IsMatch(node.ServerId) || !serverIds.Add(node.ServerId))
            {
                throw new ArgumentException(
                    "Every federation node must have a unique lowercase safe ServerId.");
            }
            if (string.IsNullOrWhiteSpace(node.DisplayName) ||
                node.DisplayName.Length > 80 || node.DisplayName.Any(char.IsControl))
            {
                throw new ArgumentException(
                    $"Federation node '{node.ServerId}' has an invalid DisplayName.");
            }
            var baseUri = RequireSafeUri(node.BaseUri, isDevelopment, portal: false);
            _ = RequireSafeUri(node.PortalUrl, isDevelopment, portal: true);
            if (!baseUris.Add(baseUri.AbsoluteUri))
            {
                throw new ArgumentException("Federation BaseUri entries must be unique.");
            }
            var combination = matrix.RequireCombination(node.ExpectedCombinationId);
            if (!isDevelopment)
            {
                CompatibilityMatrixValidator.RequireProductionStable(
                    matrix,
                    combination.Id);
            }
            else if (node.Local &&
                combination.Status != CompatibilityStatus.Stable &&
                !(options.AllowExperimentalInDevelopment &&
                  combination.Status == CompatibilityStatus.Experimental))
            {
                throw new ArgumentException(
                    $"Local federation node '{node.ServerId}' cannot run the {CompatibilityMatrixValidator.ToWireStatus(combination.Status)} combination in this environment.");
            }

            if (node.Local)
            {
                localCount++;
                if (!string.Equals(node.ServerId, options.LocalServerId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The local federation node ServerId must equal LocalServerId.");
                }
                if (!string.IsNullOrWhiteSpace(node.NodeKey) ||
                    !string.IsNullOrWhiteSpace(node.NodeKeyFile))
                {
                    throw new ArgumentException(
                        "The in-process local federation node must not configure an outbound node key.");
                }
            }
            else
            {
                var nodeKey = FederationSecretResolver.ResolveRequired(
                    node.NodeKey,
                    node.NodeKeyFile,
                    contentRootPath,
                    $"Federation node key for '{node.ServerId}'");
                if (FederationSecretResolver.FixedTimeEquals(identityKey, nodeKey))
                {
                    throw new ArgumentException(
                        $"Federation node '{node.ServerId}' must not reuse the identity HMAC key.");
                }
            }
        }
        if (localCount != 1)
        {
            throw new ArgumentException(
                "Federation must configure exactly one in-process local node.");
        }

        return matrix;
    }

    public static string ResolvePath(string contentRootPath, string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(contentRootPath, configuredPath));
    }

    public static Uri RequireSafeUri(string value, bool isDevelopment, bool portal)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Scheme is not ("https" or "http") ||
            (!portal && uri.AbsolutePath != "/"))
        {
            throw new ArgumentException(
                $"Federation {(portal ? "PortalUrl" : "BaseUri")} must be an absolute HTTP(S) URI without credentials, query, or fragment{(portal ? string.Empty : " and with only the root path")}.");
        }
        if (uri.Scheme != Uri.UriSchemeHttps &&
            (!isDevelopment || !uri.IsLoopback))
        {
            throw new ArgumentException(
                "Federation HTTP is allowed only for loopback Development nodes; production nodes and portals require HTTPS.");
        }
        return uri;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex ServerIdPattern();

    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex Sha256Pattern();
}

public static class FederationSecretResolver
{
    public static byte[] ResolveRequired(
        string inlineValue,
        string filePath,
        string contentRootPath,
        string description)
    {
        var hasInline = !string.IsNullOrWhiteSpace(inlineValue);
        var hasFile = !string.IsNullOrWhiteSpace(filePath);
        if (hasInline == hasFile)
        {
            throw new ArgumentException(
                $"{description} must configure exactly one inline value or file.");
        }
        var value = hasInline
            ? inlineValue.Trim()
            : File.ReadAllText(
                FederationOptionsValidator.ResolvePath(contentRootPath, filePath),
                Encoding.UTF8).Trim();
        if (value.Length is < 32 or > 512 || value.Any(char.IsControl) ||
            value.StartsWith("SET_", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{description} must be 32 to 512 non-placeholder printable characters.");
        }
        return Encoding.UTF8.GetBytes(value);
    }

    public static bool FixedTimeEquals(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (first.Length != second.Length)
        {
            // Perform a comparison anyway so length errors do not take the
            // immediate success path used by valid credentials.
            _ = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                first,
                first);
            return false;
        }
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            first,
            second);
    }
}

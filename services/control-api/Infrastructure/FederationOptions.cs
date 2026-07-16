using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public sealed record FederationOptions
{
    public const int CurrentProtocolVersion = 2;

    public bool Enabled { get; init; }

    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;

    public string LocalServerId { get; init; } = "local";

    public string MatrixPath { get; init; } =
        "Compatibility/compatibility-matrix.v1.json";

    public string ExpectedMatrixSha256 { get; init; } = string.Empty;

    public bool AllowExperimentalInDevelopment { get; init; }

    public FederationIdentityKeyOptions[] IdentityKeys { get; init; } = [];

    public FederationInboundPeerOptions[] InboundPeers { get; init; } = [];

    public int RequestTimeoutMilliseconds { get; init; } = 2_000;

    public int MaximumResponseBytes { get; init; } = 32 * 1024;

    public int MaximumRequestBodyBytes { get; init; } = 2 * 1024;

    public int MaximumConcurrentRequests { get; init; } = 8;

    public int InternalRequestsPerMinute { get; init; } = 600;

    public int MaximumClockSkewSeconds { get; init; } = 120;

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

    public string SigningKeyId { get; init; } = string.Empty;

    public string IdentityKeyId { get; init; } = string.Empty;

    public string NodeKey { get; init; } = string.Empty;

    public string NodeKeyFile { get; init; } = string.Empty;
}

public sealed record FederationIdentityKeyOptions
{
    public string KeyId { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string KeyFile { get; init; } = string.Empty;

    public bool Revoked { get; init; }
}

public sealed record FederationPeerSigningKeyOptions
{
    public string KeyId { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string KeyFile { get; init; } = string.Empty;

    public bool Revoked { get; init; }
}

public sealed record FederationInboundPeerOptions
{
    public string ServerId { get; init; } = string.Empty;

    public bool Revoked { get; init; }

    public FederationPeerSigningKeyOptions[] SigningKeys { get; init; } = [];
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
        if (options.ProtocolVersion != FederationOptions.CurrentProtocolVersion)
        {
            throw new ArgumentException(
                $"Federation ProtocolVersion must be {FederationOptions.CurrentProtocolVersion}.");
        }
        if (options.RequestTimeoutMilliseconds is < 100 or > 10_000 ||
            options.MaximumResponseBytes is < 1_024 or > 256 * 1_024 ||
            options.MaximumRequestBodyBytes is < 256 or > 16 * 1_024 ||
            options.MaximumConcurrentRequests is < 1 or > 64 ||
            options.InternalRequestsPerMinute is < 10 or > 10_000 ||
            options.MaximumClockSkewSeconds is < 30 or > 300)
        {
            throw new ArgumentException(
                "Federation timeout, body, response, concurrency, rate, or clock-skew limits are outside safe bounds.");
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

        if (options.IdentityKeys is null || options.IdentityKeys.Length is < 1 or > 8)
        {
            throw new ArgumentException(
                "Federation requires 1 to 8 versioned identity keys.");
        }
        var identityKeyIds = new HashSet<string>(StringComparer.Ordinal);
        var identityKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var key in options.IdentityKeys)
        {
            if (!IsSafeKeyId(key.KeyId) || !identityKeyIds.Add(key.KeyId))
            {
                throw new ArgumentException(
                    "Every federation identity key must have a unique safe KeyId.");
            }
            if (key.Revoked)
            {
                ValidateRevokedSecretShape(
                    key.Key,
                    key.KeyFile,
                    $"Federation identity key '{key.KeyId}'");
                continue;
            }
            identityKeys[key.KeyId] = FederationSecretResolver.ResolveRequired(
                key.Key,
                key.KeyFile,
                contentRootPath,
                $"Federation identity key '{key.KeyId}'");
        }
        if (identityKeys.Count == 0)
        {
            throw new ArgumentException("Federation requires at least one non-revoked identity key.");
        }

        var serverIds = new HashSet<string>(StringComparer.Ordinal);
        var baseUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outboundSecrets = new List<byte[]>();
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
                    !string.IsNullOrWhiteSpace(node.NodeKeyFile) ||
                    !string.IsNullOrWhiteSpace(node.SigningKeyId) ||
                    !string.IsNullOrWhiteSpace(node.IdentityKeyId))
                {
                    throw new ArgumentException(
                        "The in-process local federation node must not configure outbound signing or identity keys.");
                }
            }
            else
            {
                if (!IsSafeKeyId(node.SigningKeyId) || !IsSafeKeyId(node.IdentityKeyId))
                {
                    throw new ArgumentException(
                        $"Federation node '{node.ServerId}' requires safe SigningKeyId and IdentityKeyId values.");
                }
                if (!identityKeys.ContainsKey(node.IdentityKeyId))
                {
                    throw new ArgumentException(
                        $"Federation node '{node.ServerId}' references a missing or revoked identity key.");
                }
                var nodeKey = FederationSecretResolver.ResolveRequired(
                    node.NodeKey,
                    node.NodeKeyFile,
                    contentRootPath,
                    $"Federation node key for '{node.ServerId}'");
                if (identityKeys.Values.Any(identityKey =>
                        FederationSecretResolver.FixedTimeEquals(identityKey, nodeKey)))
                {
                    throw new ArgumentException(
                        $"Federation node '{node.ServerId}' must not reuse an identity HMAC key for request signing.");
                }
                outboundSecrets.Add(nodeKey);
            }
        }
        if (localCount != 1)
        {
            throw new ArgumentException(
                "Federation must configure exactly one in-process local node.");
        }

        var remoteIds = options.Nodes
            .Where(node => !node.Local)
            .Select(node => node.ServerId)
            .ToHashSet(StringComparer.Ordinal);
        if (options.InboundPeers is null || options.InboundPeers.Length != remoteIds.Count)
        {
            throw new ArgumentException(
                "Federation must configure exactly one inbound peer policy for every remote node.");
        }
        var peerIds = new HashSet<string>(StringComparer.Ordinal);
        var inboundSecrets = new List<byte[]>();
        foreach (var peer in options.InboundPeers)
        {
            if (!ServerIdPattern().IsMatch(peer.ServerId) ||
                !remoteIds.Contains(peer.ServerId) ||
                !peerIds.Add(peer.ServerId))
            {
                throw new ArgumentException(
                    "Every inbound federation peer must map uniquely to one configured remote node.");
            }
            if (peer.SigningKeys is null || peer.SigningKeys.Length > 8 ||
                !peer.Revoked && peer.SigningKeys.Length < 1)
            {
                throw new ArgumentException(
                    $"Federation peer '{peer.ServerId}' requires 1 to 8 signing keys unless the whole peer is revoked.");
            }
            var signingKeyIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in peer.SigningKeys)
            {
                if (!IsSafeKeyId(key.KeyId) || !signingKeyIds.Add(key.KeyId))
                {
                    throw new ArgumentException(
                        $"Federation peer '{peer.ServerId}' has a duplicate or unsafe signing KeyId.");
                }
                if (peer.Revoked || key.Revoked)
                {
                    ValidateRevokedSecretShape(
                        key.Key,
                        key.KeyFile,
                        $"Federation inbound peer '{peer.ServerId}' signing key '{key.KeyId}'");
                    continue;
                }
                var secret = FederationSecretResolver.ResolveRequired(
                    key.Key,
                    key.KeyFile,
                    contentRootPath,
                    $"Federation inbound peer '{peer.ServerId}' signing key '{key.KeyId}'");
                if (identityKeys.Values.Any(identityKey =>
                        FederationSecretResolver.FixedTimeEquals(identityKey, secret)))
                {
                    throw new ArgumentException(
                        $"Federation peer '{peer.ServerId}' must not reuse an identity key for request signing.");
                }
                inboundSecrets.Add(secret);
            }
            if (!peer.Revoked && peer.SigningKeys.All(key => key.Revoked))
            {
                throw new ArgumentException(
                    $"Non-revoked federation peer '{peer.ServerId}' requires a non-revoked signing key.");
            }
        }

        for (var first = 0; first < inboundSecrets.Count; first++)
        {
            for (var second = first + 1; second < inboundSecrets.Count; second++)
            {
                if (FederationSecretResolver.FixedTimeEquals(
                        inboundSecrets[first], inboundSecrets[second]))
                {
                    throw new ArgumentException(
                        "Federation inbound peers must not share signing secrets.");
                }
            }
        }
        for (var first = 0; first < outboundSecrets.Count; first++)
        {
            for (var second = first + 1; second < outboundSecrets.Count; second++)
            {
                if (FederationSecretResolver.FixedTimeEquals(
                        outboundSecrets[first], outboundSecrets[second]))
                {
                    throw new ArgumentException(
                        "Federation outbound peers must not share signing secrets.");
                }
            }
            if (inboundSecrets.Any(inboundSecret =>
                    FederationSecretResolver.FixedTimeEquals(
                        outboundSecrets[first], inboundSecret)))
            {
                throw new ArgumentException(
                    "Federation inbound and outbound trust directions must use different signing secrets.");
            }
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

    [GeneratedRegex("^[a-z][a-z0-9-]{2,47}$")]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex Sha256Pattern();

    public static bool IsSafeKeyId(string? value) =>
        value is not null && KeyIdPattern().IsMatch(value);

    private static void ValidateRevokedSecretShape(
        string inlineValue,
        string filePath,
        string description)
    {
        if (!string.IsNullOrWhiteSpace(inlineValue) &&
            !string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException(
                $"{description} cannot configure both inline and file secrets after revocation.");
        }
    }
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

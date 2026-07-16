using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed record FederationProfileRequest(
    int ProtocolVersion,
    string CallerServerId,
    string TargetServerId,
    string IdentityKeyId,
    string SubjectToken);

public sealed record FederationSeasonProfile(
    string Code,
    string DisplayName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string State);

public sealed record FederationBalanceProfile(
    long MarketCoin,
    long SeasonVoucher);

public sealed record FederationCompatibilityProfile(
    string CombinationId,
    string MatrixVersion,
    string MatrixSha256,
    string Status,
    string GameVersion,
    string SteamBuild,
    string PalDefenderVersion,
    string Ue4ssCommit,
    string NativeProtocolVersion,
    string NativeModVersion,
    string BridgeAvailability,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset VerifiedAt);

public sealed record FederationLocalProfile(
    string ServerId,
    bool AccountExists,
    string? AccountDisplayName,
    FederationSeasonProfile? Season,
    FederationBalanceProfile? Balances,
    bool BalancesAvailable,
    string? UnavailableReason,
    FederationCompatibilityProfile Compatibility,
    DateTimeOffset ObservedAt);

public sealed record FederationNodeSummary(
    string ServerId,
    string DisplayName,
    string PortalUrl,
    bool Local,
    string Availability,
    bool? AccountExists,
    string? AccountDisplayName,
    FederationSeasonProfile? Season,
    FederationBalanceProfile? Balances,
    bool BalancesAvailable,
    FederationCompatibilityProfile? Compatibility,
    bool SwitchAvailable,
    string? ErrorCode,
    DateTimeOffset ObservedAt);

public sealed record FederationAccountOverview(
    string LocalServerId,
    string MatrixVersion,
    string MatrixSha256,
    IReadOnlyList<FederationNodeSummary> Servers,
    DateTimeOffset ObservedAt);

public sealed record FederationNodeHealth(
    string ServerId,
    string Availability,
    FederationCompatibilityProfile? Compatibility,
    string? ErrorCode,
    DateTimeOffset ObservedAt);

public sealed partial class FederationIdentityProtector
{
    private const string Prefix = "fed2_";
    private readonly Dictionary<string, byte[]> _keys;

    public FederationIdentityProtector(
        IOptions<FederationOptions> options,
        IHostEnvironment environment)
    {
        var value = options.Value;
        _keys = value.Enabled
            ? value.IdentityKeys
                .Where(key => !key.Revoked)
                .ToDictionary(
                    key => key.KeyId,
                    key => FederationSecretResolver.ResolveRequired(
                        key.Key,
                        key.KeyFile,
                        environment.ContentRootPath,
                        $"Federation identity key '{key.KeyId}'"),
                    StringComparer.Ordinal)
            : new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["disabled-key"] = RandomNumberGenerator.GetBytes(32)
            };
    }

    internal FederationIdentityProtector(
        string keyId,
        byte[] key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(key);
        if (!FederationOptionsValidator.IsSafeKeyId(keyId) || key.Length < 32)
        {
            throw new ArgumentException("Federation identity key id or value is invalid.", nameof(key));
        }
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [keyId] = key.ToArray()
        };
    }

    public string Protect(
        string identityKeyId,
        string callerServerId,
        string targetServerId,
        string identityProvider,
        string externalUserId)
    {
        if (!_keys.TryGetValue(identityKeyId, out var key))
        {
            throw new FederationException(
                "FEDERATION_IDENTITY_KEY_UNAVAILABLE",
                "The requested federation identity key is unavailable or revoked.",
                StatusCodes.Status503ServiceUnavailable);
        }
        var caller = NormalizeServerId(callerServerId, "caller server");
        var target = NormalizeServerId(targetServerId, "target server");
        var provider = NormalizeIdentityPart(identityProvider, 32, "identity provider")
            .ToLowerInvariant();
        var subject = NormalizeIdentityPart(externalUserId, 160, "external user id")
            .ToLowerInvariant();
        var input = Encoding.UTF8.GetBytes(
            $"pal-control-federation-subject-v2\n{identityKeyId}\n{caller}\n{target}\n{provider}\n{subject}");
        var digest = HMACSHA256.HashData(key, input);
        return Prefix + Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public bool IsValidToken(string? token) =>
        token is not null && SubjectTokenPattern().IsMatch(token);

    public bool FixedTimeTokenEquals(string supplied, string expected)
    {
        if (!IsValidToken(supplied) || !IsValidToken(expected))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(supplied),
            Encoding.ASCII.GetBytes(expected));
    }

    private static string NormalizeIdentityPart(
        string value,
        int maximumLength,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"Federation {description} is invalid.");
        }
        return normalized;
    }

    private static string NormalizeServerId(string value, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 32 ||
            !normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            throw new ArgumentException($"Federation {description} is invalid.");
        }
        return normalized;
    }

    [GeneratedRegex("^fed2_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex SubjectTokenPattern();
}

/// <summary>
/// Resolves a federation token against this node's authoritative extraction
/// repository. It creates no accounts, seeds no wallet and writes no index or
/// balance copy. Every account candidate is checked to avoid an early-exit
/// identity oracle.
/// </summary>
public sealed class FederationLocalProfileService
{
    private readonly ExtractionCommerceService _commerce;
    private readonly FederationIdentityProtector _identity;
    private readonly FederationOptions _options;
    private readonly CompatibilityMatrixSnapshot _matrix;
    private readonly TimeProvider _timeProvider;

    public FederationLocalProfileService(
        ExtractionCommerceService commerce,
        FederationIdentityProtector identity,
        IOptions<FederationOptions> options,
        CompatibilityMatrixStore matrix,
        TimeProvider timeProvider)
    {
        _commerce = commerce;
        _identity = identity;
        _options = options.Value;
        _matrix = matrix.Snapshot;
        _timeProvider = timeProvider;
    }

    internal FederationLocalProfileService(
        ExtractionCommerceService commerce,
        FederationIdentityProtector identity,
        FederationOptions options,
        CompatibilityMatrixSnapshot matrix,
        TimeProvider? timeProvider = null)
    {
        _commerce = commerce;
        _identity = identity;
        _options = options;
        _matrix = matrix;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FederationLocalProfile> GetAsync(
        FederationProfileRequest request,
        string authenticatedCallerServerId,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new FederationException(
                "FEDERATION_DISABLED",
                "Federation is disabled on this node.",
                StatusCodes.Status404NotFound);
        }
        if (request.ProtocolVersion != FederationOptions.CurrentProtocolVersion ||
            !string.Equals(
                request.CallerServerId,
                authenticatedCallerServerId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.TargetServerId,
                _options.LocalServerId,
                StringComparison.Ordinal) ||
            !FederationOptionsValidator.IsSafeKeyId(request.IdentityKeyId) ||
            !_identity.IsValidToken(request.SubjectToken))
        {
            throw new FederationException(
                "FEDERATION_REQUEST_BINDING_INVALID",
                "Federation caller, target, identity key, or subject binding is invalid.",
                StatusCodes.Status400BadRequest);
        }

        var accounts = await _commerce.ListAccountsAsync(cancellationToken);
        ExtractionAccount? matched = null;
        foreach (var account in accounts)
        {
            var candidate = _identity.Protect(
                request.IdentityKeyId,
                request.CallerServerId,
                request.TargetServerId,
                account.IdentityProvider,
                account.ExternalUserId);
            if (_identity.FixedTimeTokenEquals(request.SubjectToken, candidate))
            {
                if (matched is not null)
                {
                    throw new FederationException(
                        "FEDERATION_SUBJECT_AMBIGUOUS",
                        "Federation subject matched more than one local account.",
                        StatusCodes.Status409Conflict);
                }
                matched = account;
            }
        }

        return await ProjectAsync(matched, cancellationToken);
    }

    public async Task<FederationLocalProfile> GetLocalAsync(
        string identityProvider,
        string externalUserId,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new FederationException(
                "FEDERATION_DISABLED",
                "Federation is disabled on this node.",
                StatusCodes.Status404NotFound);
        }
        var matched = await _commerce.FindPlayerAsync(
            identityProvider,
            externalUserId,
            cancellationToken);
        return await ProjectAsync(matched, cancellationToken);
    }

    private async Task<FederationLocalProfile> ProjectAsync(
        ExtractionAccount? matched,
        CancellationToken cancellationToken)
    {
        var localNode = _options.Nodes.Single(node => node.Local);
        var combination = _matrix.RequireCombination(localNode.ExpectedCombinationId);
        var compatibility = ProjectCompatibility(_matrix, combination);

        var season = await _commerce.GetActiveSeasonAsync(
            _options.LocalServerId,
            cancellationToken);
        if (matched is null)
        {
            return new FederationLocalProfile(
                _options.LocalServerId,
                AccountExists: false,
                AccountDisplayName: null,
                ProjectSeason(season),
                Balances: null,
                BalancesAvailable: false,
                UnavailableReason: "account-not-found",
                compatibility,
                _timeProvider.GetUtcNow());
        }
        if (season is null)
        {
            return new FederationLocalProfile(
                _options.LocalServerId,
                AccountExists: true,
                matched.DisplayName,
                Season: null,
                Balances: null,
                BalancesAvailable: false,
                UnavailableReason: "active-season-unavailable",
                compatibility,
                _timeProvider.GetUtcNow());
        }

        var wallet = await _commerce.GetWalletAsync(
            matched.AccountId,
            season.SeasonId,
            cancellationToken);
        return new FederationLocalProfile(
            _options.LocalServerId,
            AccountExists: true,
            matched.DisplayName,
            ProjectSeason(season),
            new FederationBalanceProfile(
                wallet.MarketCoin.Balance,
                wallet.SeasonVoucher.Balance),
            BalancesAvailable: true,
            UnavailableReason: null,
            compatibility,
            _timeProvider.GetUtcNow());
    }

    public FederationNodeHealth GetHealth()
    {
        var localNode = _options.Nodes.Single(node => node.Local);
        var combination = _matrix.RequireCombination(localNode.ExpectedCombinationId);
        return new FederationNodeHealth(
            _options.LocalServerId,
            combination.Status == CompatibilityStatus.Quarantined
                ? "incompatible"
                : "available",
            ProjectCompatibility(_matrix, combination),
            combination.Status == CompatibilityStatus.Quarantined
                ? "COMPATIBILITY_QUARANTINED"
                : null,
            _timeProvider.GetUtcNow());
    }

    public static FederationCompatibilityProfile ProjectCompatibility(
        CompatibilityMatrixSnapshot matrix,
        CompatibilityCombination combination) => new(
            combination.Id,
            matrix.Document.MatrixVersion,
            matrix.CanonicalSha256,
            CompatibilityMatrixValidator.ToWireStatus(combination.Status),
            combination.GameVersion,
            combination.SteamBuild,
            combination.PalDefenderVersion,
            combination.Ue4ssCommit,
            combination.NativeProtocolVersion,
            combination.NativeModVersion,
            combination.BridgeAvailability,
            combination.Capabilities,
            combination.VerifiedAt);

    private static FederationSeasonProfile? ProjectSeason(ExtractionSeason? season) =>
        season is null
            ? null
            : new FederationSeasonProfile(
                season.Code,
                season.DisplayName,
                season.StartsAt,
                season.EndsAt,
                season.State.ToString().ToLowerInvariant());
}

public sealed class FederationException : Exception
{
    public FederationException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }

    public int StatusCode { get; }
}

public sealed record FederationAuthenticatedPeer(
    string CallerServerId,
    string TargetServerId,
    string SigningKeyId,
    string IdentityKeyId);

public static class FederationProtocol
{
    public const string ProtocolHeader = "X-Pal-Control-Federation-Version";
    public const string CallerHeader = "X-Pal-Control-Caller";
    public const string TargetHeader = "X-Pal-Control-Target";
    public const string SigningKeyIdHeader = "X-Pal-Control-Signing-Key-Id";
    public const string IdentityKeyIdHeader = "X-Pal-Control-Identity-Key-Id";
    public const string TimestampHeader = "X-Pal-Control-Timestamp";
    public const string NonceHeader = "X-Pal-Control-Nonce";
    public const string ContentSha256Header = "X-Pal-Control-Content-SHA256";
    public const string SignatureHeader = "X-Pal-Control-Signature";
    public const string ExpectedCombinationHeader =
        "X-Pal-Control-Expected-Combination";
    public const string NoIdentityKey = "-";

    public static string ContentSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Sign(
        ReadOnlySpan<byte> key,
        string method,
        string path,
        string callerServerId,
        string targetServerId,
        string signingKeyId,
        string identityKeyId,
        string expectedCombinationId,
        long timestamp,
        string nonce,
        string contentSha256)
    {
        var canonical = string.Join('\n',
            "pal-control-federation-request-v2",
            method.ToUpperInvariant(),
            path,
            callerServerId,
            targetServerId,
            signingKeyId,
            identityKeyId,
            expectedCombinationId,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            contentSha256);
        return Base64Url(HMACSHA256.HashData(
            key,
            Encoding.UTF8.GetBytes(canonical)));
    }

    public static string NewNonce() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static bool FixedTimeSignatureEquals(string supplied, string expected)
    {
        if (!IsSignature(supplied) || !IsSignature(expected))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(supplied),
            Encoding.ASCII.GetBytes(expected));
    }

    public static bool IsNonce(string? value) =>
        value is not null && value.Length == 22 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public static bool IsContentSha256(string? value) =>
        value is not null && value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSignature(string? value) =>
        value is not null && value.Length == 43 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

public sealed class FederationInternalAuthenticator
{
    private const int MaximumRememberedNonces = 100_000;
    private readonly bool _enabled;
    private readonly string _localServerId;
    private readonly string _expectedCombinationId;
    private readonly int _maximumClockSkewSeconds;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, InboundPeer> _peers;
    private readonly HashSet<string> _identityKeyIds;
    private readonly object _nonceSync = new();
    private readonly Dictionary<string, DateTimeOffset> _seenNonces = new(StringComparer.Ordinal);

    public FederationInternalAuthenticator(
        IOptions<FederationOptions> options,
        IHostEnvironment environment,
        TimeProvider timeProvider)
        : this(options.Value, environment.ContentRootPath, timeProvider)
    {
    }

    internal FederationInternalAuthenticator(
        FederationOptions options,
        string contentRootPath,
        TimeProvider? timeProvider = null)
    {
        _enabled = options.Enabled;
        _localServerId = options.LocalServerId;
        _maximumClockSkewSeconds = options.MaximumClockSkewSeconds;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _expectedCombinationId = options.Enabled
            ? options.Nodes.Single(node => node.Local).ExpectedCombinationId
            : string.Empty;
        _identityKeyIds = options.IdentityKeys
            .Where(key => !key.Revoked)
            .Select(key => key.KeyId)
            .ToHashSet(StringComparer.Ordinal);
        _peers = options.Enabled
            ? options.InboundPeers.ToDictionary(
                peer => peer.ServerId,
                peer => new InboundPeer(
                    peer.Revoked,
                    peer.SigningKeys
                        .Where(key => !peer.Revoked && !key.Revoked)
                        .ToDictionary(
                        key => key.KeyId,
                        key => new InboundSigningKey(
                            FederationSecretResolver.ResolveRequired(
                                key.Key,
                                key.KeyFile,
                                contentRootPath,
                                $"Federation inbound peer '{peer.ServerId}' signing key '{key.KeyId}'")),
                        StringComparer.Ordinal)),
                StringComparer.Ordinal)
            : new Dictionary<string, InboundPeer>(StringComparer.Ordinal);
    }

    public bool Authenticate(
        HttpRequest request,
        ReadOnlySpan<byte> body,
        out FederationAuthenticatedPeer? authenticatedPeer)
    {
        authenticatedPeer = null;
        if (!_enabled || request.QueryString.HasValue ||
            !TrySingleHeader(request, FederationProtocol.ProtocolHeader, out var protocol) ||
            protocol != FederationOptions.CurrentProtocolVersion.ToString(CultureInfo.InvariantCulture) ||
            !TrySingleHeader(request, FederationProtocol.CallerHeader, out var caller) ||
            !TrySingleHeader(request, FederationProtocol.TargetHeader, out var target) ||
            !TrySingleHeader(request, FederationProtocol.SigningKeyIdHeader, out var signingKeyId) ||
            !TrySingleHeader(request, FederationProtocol.IdentityKeyIdHeader, out var identityKeyId) ||
            !TrySingleHeader(request, FederationProtocol.ExpectedCombinationHeader, out var expectedCombination) ||
            !TrySingleHeader(request, FederationProtocol.TimestampHeader, out var timestampText) ||
            !TrySingleHeader(request, FederationProtocol.NonceHeader, out var nonce) ||
            !TrySingleHeader(request, FederationProtocol.ContentSha256Header, out var suppliedContentHash) ||
            !TrySingleHeader(request, FederationProtocol.SignatureHeader, out var suppliedSignature) ||
            !string.Equals(target, _localServerId, StringComparison.Ordinal) ||
            !string.Equals(expectedCombination, _expectedCombinationId, StringComparison.Ordinal) ||
            !FederationOptionsValidator.IsSafeKeyId(signingKeyId) ||
            !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) ||
            !FederationProtocol.IsNonce(nonce) ||
            !FederationProtocol.IsContentSha256(suppliedContentHash) ||
            !_peers.TryGetValue(caller, out var peer) || peer.Revoked ||
            !peer.SigningKeys.TryGetValue(signingKeyId, out var signingKey))
        {
            return false;
        }

        var profileRequest = string.Equals(
            request.Path.Value,
            "/api/v1/internal/federation/profile",
            StringComparison.Ordinal);
        var healthRequest = string.Equals(
            request.Path.Value,
            "/api/v1/internal/federation/health",
            StringComparison.Ordinal);
        if (!profileRequest && !healthRequest ||
            profileRequest && !_identityKeyIds.Contains(identityKeyId) ||
            healthRequest && !string.Equals(identityKeyId, FederationProtocol.NoIdentityKey, StringComparison.Ordinal))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        DateTimeOffset issuedAt;
        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        if (Math.Abs((now - issuedAt).TotalSeconds) > _maximumClockSkewSeconds)
        {
            return false;
        }

        var actualContentHash = FederationProtocol.ContentSha256(body);
        if (!FederationSecretResolver.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualContentHash),
                Encoding.ASCII.GetBytes(suppliedContentHash)))
        {
            return false;
        }
        var expectedSignature = FederationProtocol.Sign(
            signingKey.Secret,
            request.Method,
            request.Path.Value!,
            caller,
            target,
            signingKeyId,
            identityKeyId,
            expectedCombination,
            timestamp,
            nonce,
            suppliedContentHash);
        if (!FederationProtocol.FixedTimeSignatureEquals(
                suppliedSignature,
                expectedSignature))
        {
            return false;
        }

        var replayKey = $"{caller}\n{signingKeyId}\n{nonce}";
        lock (_nonceSync)
        {
            var oldestAllowed = now.AddSeconds(-_maximumClockSkewSeconds);
            if (_seenNonces.Count >= MaximumRememberedNonces)
            {
                foreach (var expired in _seenNonces
                             .Where(pair => pair.Value < oldestAllowed)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _seenNonces.Remove(expired);
                }
            }
            if (_seenNonces.Count >= MaximumRememberedNonces ||
                !_seenNonces.TryAdd(replayKey, now))
            {
                return false;
            }
        }

        authenticatedPeer = new FederationAuthenticatedPeer(
            caller,
            target,
            signingKeyId,
            identityKeyId);
        return true;
    }

    private static bool TrySingleHeader(
        HttpRequest request,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!request.Headers.TryGetValue(name, out var values) ||
            values.Count != 1 || values[0] is not { Length: > 0 and <= 256 } candidate ||
            candidate.Any(char.IsControl))
        {
            return false;
        }
        value = candidate;
        return true;
    }

    private sealed record InboundPeer(
        bool Revoked,
        IReadOnlyDictionary<string, InboundSigningKey> SigningKeys);

    private sealed record InboundSigningKey(byte[] Secret);
}

public sealed class FederationInternalRequestGuard
{
    private const int MaximumBuckets = 4_096;
    private readonly object _sync = new();
    private readonly Dictionary<string, RequestBucket> _buckets = new(StringComparer.Ordinal);
    private readonly FederationOptions _options;
    private readonly TimeProvider _timeProvider;
    private int _active;

    public FederationInternalRequestGuard(
        IOptions<FederationOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    internal FederationInternalRequestGuard(
        FederationOptions options,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryAcquire(
        string remoteAddress,
        out IDisposable? lease,
        out int retryAfterSeconds)
    {
        lease = null;
        retryAfterSeconds = 1;
        if (Interlocked.Increment(ref _active) > _options.MaximumConcurrentRequests)
        {
            Interlocked.Decrement(ref _active);
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var windowStartUnix = now.ToUnixTimeSeconds() / 60 * 60;
        var normalizedAddress = string.IsNullOrWhiteSpace(remoteAddress)
            ? "unavailable"
            : remoteAddress.Trim().ToLowerInvariant();
        lock (_sync)
        {
            if (_buckets.Count >= MaximumBuckets)
            {
                Cleanup(now);
            }
            if (!_buckets.TryGetValue(normalizedAddress, out var bucket) ||
                bucket.WindowStartUnix != windowStartUnix)
            {
                if (_buckets.Count >= MaximumBuckets)
                {
                    Interlocked.Decrement(ref _active);
                    return false;
                }
                bucket = new RequestBucket(windowStartUnix, 0);
                _buckets[normalizedAddress] = bucket;
            }
            if (bucket.Count >= _options.InternalRequestsPerMinute)
            {
                retryAfterSeconds = Math.Max(
                    1,
                    (int)(windowStartUnix + 60 - now.ToUnixTimeSeconds()));
                Interlocked.Decrement(ref _active);
                return false;
            }
            bucket.Count++;
        }

        retryAfterSeconds = 0;
        lease = new GuardLease(this);
        return true;
    }

    private void Cleanup(DateTimeOffset now)
    {
        var oldest = now.ToUnixTimeSeconds() / 60 * 60 - 60;
        foreach (var key in _buckets
                     .Where(pair => pair.Value.WindowStartUnix < oldest)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _buckets.Remove(key);
        }
    }

    private sealed class RequestBucket(long windowStartUnix, int count)
    {
        public long WindowStartUnix { get; } = windowStartUnix;
        public int Count { get; set; } = count;
    }

    private sealed class GuardLease(FederationInternalRequestGuard owner) : IDisposable
    {
        private FederationInternalRequestGuard? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                Interlocked.Decrement(ref owner._active);
            }
        }
    }
}

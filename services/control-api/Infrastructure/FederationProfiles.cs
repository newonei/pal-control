using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed record FederationProfileRequest(string SubjectToken);

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
    private const string Prefix = "fed1_";
    private readonly byte[] _key;

    public FederationIdentityProtector(
        IOptions<FederationOptions> options,
        IHostEnvironment environment)
    {
        var value = options.Value;
        _key = value.Enabled
            ? FederationSecretResolver.ResolveRequired(
                value.IdentityHmacKey,
                value.IdentityHmacKeyFile,
                environment.ContentRootPath,
                "Federation identity HMAC key")
            : RandomNumberGenerator.GetBytes(32);
    }

    internal FederationIdentityProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
        {
            throw new ArgumentException("Federation identity key is too short.", nameof(key));
        }
        _key = key.ToArray();
    }

    public string Protect(string identityProvider, string externalUserId)
    {
        var provider = NormalizeIdentityPart(identityProvider, 32, "identity provider")
            .ToLowerInvariant();
        var subject = NormalizeIdentityPart(externalUserId, 160, "external user id")
            .ToLowerInvariant();
        var input = Encoding.UTF8.GetBytes($"{provider}\n{subject}");
        var digest = HMACSHA256.HashData(_key, input);
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

    [GeneratedRegex("^fed1_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
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
        string subjectToken,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new FederationException(
                "FEDERATION_DISABLED",
                "Federation is disabled on this node.",
                StatusCodes.Status404NotFound);
        }
        if (!_identity.IsValidToken(subjectToken))
        {
            throw new FederationException(
                "FEDERATION_SUBJECT_INVALID",
                "Federation subject token is invalid.",
                StatusCodes.Status400BadRequest);
        }

        var localNode = _options.Nodes.Single(node => node.Local);
        var combination = _matrix.RequireCombination(localNode.ExpectedCombinationId);
        var compatibility = ProjectCompatibility(_matrix, combination);
        var accounts = await _commerce.ListAccountsAsync(cancellationToken);
        ExtractionAccount? matched = null;
        foreach (var account in accounts)
        {
            var candidate = _identity.Protect(
                account.IdentityProvider,
                account.ExternalUserId);
            if (_identity.FixedTimeTokenEquals(subjectToken, candidate))
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

public sealed class FederationInternalAuthenticator
{
    public const string NodeKeyHeader = "X-Pal-Control-Node-Key";
    private readonly byte[] _expectedKey;
    private readonly bool _enabled;

    public FederationInternalAuthenticator(
        IOptions<FederationOptions> options,
        IHostEnvironment environment)
    {
        var value = options.Value;
        _enabled = value.Enabled;
        _expectedKey = value.Enabled
            ? FederationSecretResolver.ResolveRequired(
                value.InboundNodeKey,
                value.InboundNodeKeyFile,
                environment.ContentRootPath,
                "Federation inbound node key")
            : RandomNumberGenerator.GetBytes(32);
    }

    internal FederationInternalAuthenticator(byte[] expectedKey, bool enabled = true)
    {
        _expectedKey = expectedKey.ToArray();
        _enabled = enabled;
    }

    public bool Authenticate(HttpRequest request)
    {
        if (!_enabled ||
            !request.Headers.TryGetValue(NodeKeyHeader, out var values) ||
            values.Count != 1 || values[0] is not { Length: >= 32 and <= 512 } supplied ||
            supplied.Any(char.IsControl))
        {
            return false;
        }
        var actual = Encoding.UTF8.GetBytes(supplied);
        return FederationSecretResolver.FixedTimeEquals(actual, _expectedKey);
    }
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

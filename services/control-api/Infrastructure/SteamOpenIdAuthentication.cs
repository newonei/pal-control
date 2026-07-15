using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public interface ISteamOpenIdProviderClient
{
    Task<bool> CheckAuthenticationAsync(
        IReadOnlyDictionary<string, string> assertion,
        CancellationToken cancellationToken);
}

/// <summary>
/// Minimal OpenID 2.0 direct-verification client. It deliberately has no
/// discovery, delegated identity, association, password, or profile support:
/// Steam's fixed OP is the only production authority.
/// </summary>
public sealed class SteamOpenIdProviderClient(
    HttpClient httpClient,
    IOptions<PlayerPortalOptions> options) : ISteamOpenIdProviderClient
{
    private const string OpenIdNamespace = "http://specs.openid.net/auth/2.0";
    private readonly SteamOpenIdOptions _options = options.Value.OpenId;

    public async Task<bool> CheckAuthenticationAsync(
        IReadOnlyDictionary<string, string> assertion,
        CancellationToken cancellationToken)
    {
        var fields = assertion
            .Where(pair => pair.Key.StartsWith("openid.", StringComparison.Ordinal))
            .Select(pair => new KeyValuePair<string, string>(
                pair.Key,
                pair.Key == "openid.mode" ? "check_authentication" : pair.Value))
            .ToArray();
        if (fields.Length == 0)
        {
            throw new InvalidDataException("The OpenID assertion contained no protocol fields.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            _options.ProviderEndpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                "Steam OpenID check_authentication returned a non-success status.",
                null,
                response.StatusCode);
        }
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > _options.MaximumProviderResponseBytes)
        {
            throw new InvalidDataException("Steam OpenID response exceeded the configured limit.");
        }

        var text = await ReadBoundedTextAsync(
            response.Content,
            _options.MaximumProviderResponseBytes,
            cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator <= 0 || separator > 64 || values.Count >= 16)
            {
                throw new InvalidDataException("Steam OpenID response was malformed.");
            }
            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (!values.TryAdd(key, value))
            {
                throw new InvalidDataException("Steam OpenID response contained duplicate fields.");
            }
        }
        if (!values.TryGetValue("ns", out var ns) ||
            !string.Equals(ns, OpenIdNamespace, StringComparison.Ordinal) ||
            !values.TryGetValue("is_valid", out var isValid))
        {
            throw new InvalidDataException("Steam OpenID response omitted required fields.");
        }
        return string.Equals(isValid, "true", StringComparison.Ordinal);
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maximumBytes, 4096));
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Steam OpenID response exceeded the configured limit.");
            }
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

public sealed partial class SteamOpenIdAuthenticationService
{
    private const string OpenIdNamespace = "http://specs.openid.net/auth/2.0";
    private const string IdentifierSelect =
        "http://specs.openid.net/auth/2.0/identifier_select";
    private const int TokenBytes = 32;
    private const int MaximumStates = 4_096;
    private const int MaximumPendingIdentities = 4_096;
    private const int MaximumNonceFingerprints = 16_384;
    private const int MaximumRateLimitKeys = 8_192;

    private readonly object _sync = new();
    private readonly Dictionary<string, OpenIdState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingSteamIdentity> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _nonceFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FixedWindowBucket> _rateLimits = new(StringComparer.Ordinal);
    private readonly PlayerPortalOptions _portalOptions;
    private readonly SteamOpenIdOptions _options;
    private readonly ISteamOpenIdProviderClient _provider;
    private readonly PlayerIdentitySecurityService _identitySecurity;
    private readonly TimeProvider _timeProvider;

    public SteamOpenIdAuthenticationService(
        IOptions<PlayerPortalOptions> options,
        ISteamOpenIdProviderClient provider,
        PlayerIdentitySecurityService identitySecurity,
        TimeProvider timeProvider)
    {
        _portalOptions = options.Value;
        _options = _portalOptions.OpenId;
        _provider = provider;
        _identitySecurity = identitySecurity;
        _timeProvider = timeProvider;
    }

    public bool Required =>
        _portalOptions.AuthenticationMode == PlayerPortalAuthenticationMode.OpenIdThenGameCode;

    public SteamOpenIdStatus GetStatus(HttpContext context)
    {
        EnsurePortalEnabled();
        var pending = Required && TryGetPendingIdentity(context, out _);
        return new SteamOpenIdStatus(
            Required ? "openIdThenGameCode" : "trustedGameCode",
            Required,
            pending,
            !Required);
    }

    public SteamOpenIdStartResult Start(HttpContext context)
    {
        EnsureRequired();
        var now = _timeProvider.GetUtcNow();
        var clientIp = ClientIp(context);
        try
        {
            Reserve("openid_start", clientIp, _options.StartRequestsPerWindow, now);
            CleanupLocked(now);
            var realm = RequireConfiguredRealm(context);
            var callback = RequireConfiguredReturnUrl(realm);

            var state = CreateOpaqueToken();
            var stateHash = HashSecret(state);
            var expiresAt = now.AddMinutes(_options.StateLifetimeMinutes);
            var returnTo = QueryHelpers.AddQueryString(callback.AbsoluteUri, "state", state);
            lock (_sync)
            {
                CleanupLocked(now);
                if (_states.Count >= MaximumStates)
                {
                    throw CapacityUnavailable();
                }
                _states[stateHash] = new OpenIdState(
                    HashSecret(state),
                    realm.AbsoluteUri,
                    callback.AbsoluteUri,
                    returnTo,
                    expiresAt);
            }
            context.Response.Cookies.Append(
                _options.StateCookieName,
                state,
                TransientCookie(
                    "/api/v1/player/auth/steam",
                    SameSiteMode.Lax,
                    expiresAt));

            var authorizationUrl = QueryHelpers.AddQueryString(
                _options.ProviderEndpoint,
                new Dictionary<string, string?>
                {
                    ["openid.ns"] = OpenIdNamespace,
                    ["openid.mode"] = "checkid_setup",
                    ["openid.return_to"] = returnTo,
                    ["openid.realm"] = realm.AbsoluteUri,
                    ["openid.identity"] = IdentifierSelect,
                    ["openid.claimed_id"] = IdentifierSelect
                });
            Audit(
                PlayerIdentitySecurityEvents.SteamOpenIdStart,
                "succeeded",
                "state_created",
                "normal",
                null,
                context);
            return new SteamOpenIdStartResult(authorizationUrl, expiresAt);
        }
        catch (PlayerPortalException exception)
        {
            Audit(
                PlayerIdentitySecurityEvents.SteamOpenIdStart,
                "denied",
                exception.Code,
                "suspicious",
                null,
                context);
            throw;
        }
    }

    public async Task<SteamOpenIdCallbackResult> CompleteCallbackAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        EnsureRequired();
        string? subject = null;
        var clientIp = ClientIp(context);
        try
        {
            var now = _timeProvider.GetUtcNow();
            Reserve("openid_callback", clientIp, _options.CallbackRequestsPerWindow, now);
            var assertion = ReadAssertion(context.Request);
            var state = RequireField(context.Request.Query, "state", 43);
            var stateRecord = ConsumeState(context, state, now);
            ValidateActualCallback(context, stateRecord.CallbackUrl);
            ValidateAssertion(assertion, stateRecord, state, now, out var steamId, out var nonceHash);
            subject = $"steam_{steamId}";

            bool valid;
            try
            {
                valid = await _provider.CheckAuthenticationAsync(assertion, cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or
                    InvalidDataException or IOException)
            {
                throw new PlayerPortalException(
                    "STEAM_OPENID_PROVIDER_UNAVAILABLE",
                    "Steam 身份验证服务暂时不可用，请稍后重新开始登录。",
                    StatusCodes.Status503ServiceUnavailable);
            }
            if (!valid)
            {
                throw InvalidAssertion();
            }

            string pendingToken;
            DateTimeOffset expiresAt;
            lock (_sync)
            {
                CleanupLocked(now);
                if (_nonceFingerprints.ContainsKey(nonceHash))
                {
                    throw NonceReplay();
                }
                if (_nonceFingerprints.Count >= MaximumNonceFingerprints ||
                    _pending.Count >= MaximumPendingIdentities)
                {
                    throw CapacityUnavailable();
                }
                _nonceFingerprints[nonceHash] = now.AddMinutes(_options.NonceFreshnessMinutes);

                if (context.Request.Cookies.TryGetValue(
                        _options.PendingCookieName,
                        out var previousToken) &&
                    IsOpaqueToken(previousToken))
                {
                    _pending.Remove(HashSecret(previousToken));
                }
                pendingToken = CreateOpaqueToken();
                expiresAt = now.AddMinutes(_options.PendingIdentityLifetimeMinutes);
                _pending[HashSecret(pendingToken)] = new PendingSteamIdentity(
                    subject,
                    expiresAt);
            }

            context.Response.Cookies.Append(
                _options.PendingCookieName,
                pendingToken,
                TransientCookie(
                    "/api/v1/player",
                    SameSiteMode.Strict,
                    expiresAt));
            Audit(
                PlayerIdentitySecurityEvents.SteamOpenIdCallback,
                "succeeded",
                "pending_identity_created",
                "normal",
                subject,
                context);
            return new SteamOpenIdCallbackResult(
                stateRecord.Realm,
                expiresAt);
        }
        catch (PlayerPortalException exception)
        {
            Audit(
                PlayerIdentitySecurityEvents.SteamOpenIdCallback,
                "denied",
                exception.Code,
                exception.StatusCode >= 500 ? "high" : "suspicious",
                subject,
                context);
            throw;
        }
        finally
        {
            context.Response.Cookies.Delete(
                _options.StateCookieName,
                TransientCookie(
                    "/api/v1/player/auth/steam",
                    SameSiteMode.Lax,
                    null));
        }
    }

    public string ResolveCodeRequestUserId(
        HttpContext context,
        string? suppliedUserId,
        string rateLimitScope)
    {
        if (!Required)
        {
            return suppliedUserId ?? string.Empty;
        }
        Reserve(
            rateLimitScope,
            ClientIp(context),
            _options.BindingRequestsPerWindow,
            _timeProvider.GetUtcNow());
        var identity = RequirePendingIdentity(context);
        if (!string.IsNullOrWhiteSpace(suppliedUserId) &&
            !string.Equals(
                suppliedUserId.Trim(),
                identity.UserId,
                StringComparison.OrdinalIgnoreCase))
        {
            Audit(
                PlayerIdentitySecurityEvents.SteamOpenIdBinding,
                "denied",
                "pending_identity_mismatch",
                "high",
                identity.UserId,
                context);
            throw new PlayerPortalException(
                "STEAM_OPENID_IDENTITY_MISMATCH",
                "Steam 身份与验证码接收账号不一致，请重新登录 Steam。",
                StatusCodes.Status403Forbidden);
        }
        return identity.UserId;
    }

    public bool CompletePendingBinding(HttpContext context, string expectedUserId)
    {
        if (!Required)
        {
            return true;
        }
        if (!context.Request.Cookies.TryGetValue(_options.PendingCookieName, out var token) ||
            !IsOpaqueToken(token))
        {
            return false;
        }
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            CleanupLocked(now);
            var key = HashSecret(token);
            if (!_pending.TryGetValue(key, out var identity) ||
                identity.ExpiresAt <= now ||
                !string.Equals(identity.UserId, expectedUserId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            _pending.Remove(key);
        }
        context.Response.Cookies.Delete(
            _options.PendingCookieName,
            TransientCookie("/api/v1/player", SameSiteMode.Strict, null));
        Audit(
            PlayerIdentitySecurityEvents.SteamOpenIdBinding,
            "succeeded",
            "world_playeruid_bound",
            "normal",
            expectedUserId,
            context);
        return true;
    }

    private PendingSteamIdentity RequirePendingIdentity(HttpContext context)
    {
        if (!TryGetPendingIdentity(context, out var identity))
        {
            Audit(
                PlayerIdentitySecurityEvents.SteamOpenIdBinding,
                "denied",
                "pending_identity_missing_or_expired",
                "suspicious",
                null,
                context);
            throw new PlayerPortalException(
                "STEAM_OPENID_REQUIRED",
                "请先通过 Steam 官方页面验证平台身份。",
                StatusCodes.Status401Unauthorized);
        }
        return identity!;
    }

    private bool TryGetPendingIdentity(
        HttpContext context,
        out PendingSteamIdentity? identity)
    {
        identity = null;
        if (!context.Request.Cookies.TryGetValue(_options.PendingCookieName, out var token) ||
            !IsOpaqueToken(token))
        {
            return false;
        }
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            CleanupLocked(now);
            return _pending.TryGetValue(HashSecret(token), out identity) &&
                identity.ExpiresAt > now;
        }
    }

    private OpenIdState ConsumeState(
        HttpContext context,
        string state,
        DateTimeOffset now)
    {
        if (!IsOpaqueToken(state) ||
            !context.Request.Cookies.TryGetValue(_options.StateCookieName, out var cookieState) ||
            !IsOpaqueToken(cookieState) ||
            !FixedTimeEquals(state, cookieState))
        {
            throw LoginCsrf();
        }
        var key = HashSecret(state);
        lock (_sync)
        {
            CleanupLocked(now);
            if (!_states.Remove(key, out var record) ||
                record.ExpiresAt <= now ||
                !string.Equals(record.StateFingerprint, key, StringComparison.Ordinal))
            {
                throw LoginCsrf();
            }
            return record;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadAssertion(HttpRequest request)
    {
        if (request.Query.Count > 32)
        {
            throw InvalidAssertion();
        }
        var assertion = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalLength = 0;
        foreach (var pair in request.Query)
        {
            if (!pair.Key.StartsWith("openid.", StringComparison.Ordinal))
            {
                continue;
            }
            if (pair.Value.Count != 1 || pair.Key.Length > 128 ||
                pair.Value[0] is not { Length: <= 2_048 } value)
            {
                throw InvalidAssertion();
            }
            totalLength += pair.Key.Length + value.Length;
            if (totalLength > 12 * 1024 || !assertion.TryAdd(pair.Key, value))
            {
                throw InvalidAssertion();
            }
        }
        return assertion;
    }

    private void ValidateAssertion(
        IReadOnlyDictionary<string, string> assertion,
        OpenIdState stateRecord,
        string state,
        DateTimeOffset now,
        out string steamId,
        out string nonceHash)
    {
        if (!HasExact(assertion, "openid.ns", OpenIdNamespace) ||
            !HasExact(assertion, "openid.mode", "id_res") ||
            !HasExact(
                assertion,
                "openid.op_endpoint",
                PlayerPortalOptions.NormalizeEndpoint(_options.ProviderEndpoint)) ||
            !assertion.TryGetValue("openid.claimed_id", out var claimedId) ||
            !assertion.TryGetValue("openid.identity", out var identity) ||
            !string.Equals(claimedId, identity, StringComparison.Ordinal) ||
            !assertion.TryGetValue("openid.return_to", out var returnTo) ||
            !string.Equals(returnTo, stateRecord.ReturnTo, StringComparison.Ordinal) ||
            !ReturnToContainsOnlyExpectedState(returnTo, state) ||
            !assertion.TryGetValue("openid.response_nonce", out var nonce) ||
            !assertion.TryGetValue("openid.signed", out var signed) ||
            !assertion.TryGetValue("openid.sig", out var signature) ||
            signature.Length is < 8 or > 1_024 ||
            !assertion.TryGetValue("openid.assoc_handle", out var association) ||
            association.Length is < 1 or > 1_024)
        {
            throw InvalidAssertion();
        }

        var signedFields = signed.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var requiredSigned = new[]
        {
            "op_endpoint", "claimed_id", "identity", "return_to",
            "response_nonce", "assoc_handle"
        };
        if (signedFields.Length != signedFields.Distinct(StringComparer.Ordinal).Count() ||
            requiredSigned.Any(required => !signedFields.Contains(required, StringComparer.Ordinal)))
        {
            throw InvalidAssertion();
        }
        var match = SteamClaimedIdPattern().Match(claimedId);
        if (!match.Success)
        {
            throw InvalidAssertion();
        }
        steamId = match.Groups["steamId"].Value;

        var nonceMatch = ResponseNoncePattern().Match(nonce);
        if (!nonceMatch.Success ||
            !DateTimeOffset.TryParseExact(
                nonce[..20],
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var nonceAt) ||
            nonceAt < now.AddMinutes(-_options.NonceFreshnessMinutes) ||
            nonceAt > now.AddMinutes(1))
        {
            throw InvalidAssertion();
        }
        nonceHash = HashSecret(nonce);
    }

    private static bool ReturnToContainsOnlyExpectedState(string returnTo, string state)
    {
        if (!Uri.TryCreate(returnTo, UriKind.Absolute, out var uri))
        {
            return false;
        }
        var query = QueryHelpers.ParseQuery(uri.Query);
        return query.Count == 1 &&
            query.TryGetValue("state", out var values) &&
            values.Count == 1 &&
            string.Equals(values[0], state, StringComparison.Ordinal);
    }

    private static bool HasExact(
        IReadOnlyDictionary<string, string> values,
        string key,
        string expected) =>
        values.TryGetValue(key, out var actual) &&
        string.Equals(actual, expected, StringComparison.Ordinal);

    private void ValidateActualCallback(HttpContext context, string callbackUrl)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var expected) ||
            !string.Equals(context.Request.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.Equals(context.Request.Host.Host, expected.Host, StringComparison.OrdinalIgnoreCase) ||
            EffectivePort(context.Request) != expected.Port ||
            !string.Equals(context.Request.Path, expected.AbsolutePath, StringComparison.Ordinal))
        {
            throw new PlayerPortalException(
                "STEAM_OPENID_RETURN_TO_MISMATCH",
                "Steam 登录回调地址与服务器允许列表不一致。",
                StatusCodes.Status400BadRequest);
        }
    }

    private static int EffectivePort(HttpRequest request) => request.Host.Port ??
        (string.Equals(request.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ? 443 : 80);

    private static void EnsureRequestOriginMatchesRealm(HttpContext context, Uri realm)
    {
        if (!string.Equals(context.Request.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.Equals(context.Request.Host.Host, realm.Host, StringComparison.OrdinalIgnoreCase) ||
            EffectivePort(context.Request) != realm.Port)
        {
            throw new PlayerPortalException(
                "STEAM_OPENID_REALM_MISMATCH",
                "当前请求地址不在 Steam 登录 realm 允许列表中。",
                StatusCodes.Status400BadRequest);
        }
    }

    private Uri RequireConfiguredRealm(HttpContext context)
    {
        foreach (var value in _options.AllowedRealms)
        {
            if (SteamOpenIdOptions.TryNormalizeRealm(value, out var realm) &&
                string.Equals(context.Request.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
                string.Equals(
                    context.Request.Host.Host,
                    realm.Host,
                    StringComparison.OrdinalIgnoreCase) &&
                EffectivePort(context.Request) == realm.Port)
            {
                return realm;
            }
        }
        throw new PlayerPortalException(
            "STEAM_OPENID_REALM_MISMATCH",
            "当前请求地址不在 Steam 登录 realm 允许列表中。",
            StatusCodes.Status400BadRequest);
    }

    private Uri RequireConfiguredReturnUrl(Uri realm)
    {
        foreach (var value in _options.AllowedReturnUrls)
        {
            if (SteamOpenIdOptions.TryNormalizeReturnUrl(value, out var returnUrl) &&
                SteamOpenIdOptions.SameOrigin(realm, returnUrl))
            {
                return returnUrl;
            }
        }
        throw CapacityUnavailable();
    }

    private void Reserve(
        string scope,
        string clientIp,
        int limit,
        DateTimeOffset now)
    {
        var windowSeconds = _portalOptions.TrafficRateLimitWindowSeconds;
        var unix = now.ToUnixTimeSeconds();
        unix -= unix % windowSeconds;
        var windowStart = DateTimeOffset.FromUnixTimeSeconds(unix);
        var key = $"{scope}\n{clientIp}";
        lock (_sync)
        {
            CleanupLocked(now);
            if (!_rateLimits.TryGetValue(key, out var bucket))
            {
                if (_rateLimits.Count >= MaximumRateLimitKeys)
                {
                    throw RateLimited(windowSeconds);
                }
                bucket = new FixedWindowBucket(windowStart, 0);
                _rateLimits[key] = bucket;
            }
            else if (bucket.WindowStart != windowStart)
            {
                bucket.WindowStart = windowStart;
                bucket.Count = 0;
            }
            if (bucket.Count >= limit)
            {
                var retry = Math.Max(
                    1,
                    (int)Math.Ceiling(
                        (bucket.WindowStart.AddSeconds(windowSeconds) - now).TotalSeconds));
                throw RateLimited(retry);
            }
            bucket.Count++;
        }
    }

    private void CleanupLocked(DateTimeOffset now)
    {
        lock (_sync)
        {
            foreach (var key in _states.Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(pair => pair.Key).ToArray())
            {
                _states.Remove(key);
            }
            foreach (var key in _pending.Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(pair => pair.Key).ToArray())
            {
                _pending.Remove(key);
            }
            foreach (var key in _nonceFingerprints.Where(pair => pair.Value <= now)
                         .Select(pair => pair.Key).ToArray())
            {
                _nonceFingerprints.Remove(key);
            }
            var staleWindow = now.AddSeconds(-_portalOptions.TrafficRateLimitWindowSeconds);
            foreach (var key in _rateLimits.Where(pair => pair.Value.WindowStart <= staleWindow)
                         .Select(pair => pair.Key).ToArray())
            {
                _rateLimits.Remove(key);
            }
        }
    }

    private CookieOptions TransientCookie(
        string path,
        SameSiteMode sameSite,
        DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = sameSite,
        IsEssential = true,
        Path = path,
        Expires = expires,
        MaxAge = expires is null ? null : expires - _timeProvider.GetUtcNow()
    };

    private void Audit(
        string eventType,
        string outcome,
        string reasonCode,
        string riskLevel,
        string? subject,
        HttpContext context) => _identitySecurity.Audit(
        eventType,
        outcome,
        reasonCode,
        riskLevel,
        subject,
        ClientIp(context),
        context.TraceIdentifier);

    private void EnsurePortalEnabled()
    {
        if (!_portalOptions.Enabled)
        {
            throw new PlayerPortalException(
                "PLAYER_PORTAL_DISABLED",
                "玩家商城当前未启用。",
                StatusCodes.Status404NotFound);
        }
    }

    private void EnsureRequired()
    {
        EnsurePortalEnabled();
        if (!Required)
        {
            throw new PlayerPortalException(
                "STEAM_OPENID_NOT_CONFIGURED",
                "当前可信服务器使用游戏内验证码登录。",
                StatusCodes.Status404NotFound);
        }
    }

    private static string RequireField(
        IQueryCollection query,
        string key,
        int maximumLength)
    {
        if (!query.TryGetValue(key, out var values) ||
            values.Count != 1 ||
            values[0] is not { Length: > 0 } value ||
            value.Length > maximumLength)
        {
            throw InvalidAssertion();
        }
        return value;
    }

    private static string ClientIp(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unavailable";
        }
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static string CreateOpaqueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool IsOpaqueToken(string? value) =>
        value is { Length: 43 } && OpaqueTokenPattern().IsMatch(value);

    private static string HashSecret(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private static PlayerPortalException InvalidAssertion() => new(
        "STEAM_OPENID_ASSERTION_INVALID",
        "Steam 身份断言无效，请重新开始登录。",
        StatusCodes.Status401Unauthorized);

    private static PlayerPortalException LoginCsrf() => new(
        "STEAM_OPENID_STATE_INVALID",
        "Steam 登录状态无效或已经过期，请重新开始登录。",
        StatusCodes.Status401Unauthorized);

    private static PlayerPortalException NonceReplay() => new(
        "STEAM_OPENID_NONCE_REPLAYED",
        "Steam 登录响应已被使用，请重新开始登录。",
        StatusCodes.Status401Unauthorized);

    private static PlayerPortalException CapacityUnavailable() => new(
        "STEAM_OPENID_TEMPORARILY_UNAVAILABLE",
        "Steam 登录暂时不可用，请稍后重试。",
        StatusCodes.Status503ServiceUnavailable);

    private static PlayerPortalException RateLimited(int retryAfter) => new(
        "STEAM_OPENID_RATE_LIMITED",
        "Steam 登录请求过于频繁，请稍后重试。",
        StatusCodes.Status429TooManyRequests,
        retryAfter);

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueTokenPattern();

    [GeneratedRegex(
        "^https?://steamcommunity\\.com/openid/id/(?<steamId>7656119[0-9]{10})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SteamClaimedIdPattern();

    [GeneratedRegex(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z[A-Za-z0-9._~-]{8,128}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ResponseNoncePattern();

    private sealed record OpenIdState(
        string StateFingerprint,
        string Realm,
        string CallbackUrl,
        string ReturnTo,
        DateTimeOffset ExpiresAt);

    private sealed class FixedWindowBucket(DateTimeOffset windowStart, int count)
    {
        public DateTimeOffset WindowStart { get; set; } = windowStart;

        public int Count { get; set; } = count;
    }
}

public sealed record SteamOpenIdStartResult(
    string AuthorizationUrl,
    DateTimeOffset ExpiresAt);

public sealed record SteamOpenIdCallbackResult(
    string RedirectUrl,
    DateTimeOffset PendingIdentityExpiresAt);

public sealed record SteamOpenIdStatus(
    string AuthenticationMode,
    bool SteamOpenIdRequired,
    bool PendingPlatformIdentity,
    bool TrustedGameCodeFallback);

public sealed record PendingSteamIdentity(
    string UserId,
    DateTimeOffset ExpiresAt);

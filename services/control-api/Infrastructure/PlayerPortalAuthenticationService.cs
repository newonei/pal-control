using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed partial class PlayerPortalAuthenticationService
{
    private const int ChallengeIdBytes = 32;
    private const int MaximumRateLimitKeys = 4_096;
    private const int MaximumChallenges = 4_096;
    private static readonly TimeSpan ExpirationCleanupInterval = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly Dictionary<string, LoginChallenge> _challenges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _userRequests =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _ipRequests =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastUserRequest =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastIpRequest =
        new(StringComparer.Ordinal);
    private readonly byte[] _challengePepper = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _dummyDigest = RandomNumberGenerator.GetBytes(32);
    private readonly ExtractionModeCoordinator _coordinator;
    private readonly IExtractionRconAdapter _rcon;
    private readonly PlayerPortalOptions _options;
    private readonly ExtractionRconOptions _rconOptions;
    private readonly PlayerIdentitySecurityService _identitySecurity;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _nextExpirationCleanupAt = DateTimeOffset.MinValue;

    public PlayerPortalAuthenticationService(
        ExtractionModeCoordinator coordinator,
        IExtractionRconAdapter rcon,
        IOptions<PlayerPortalOptions> options,
        IOptions<ExtractionRconOptions> rconOptions,
        PlayerIdentitySecurityService identitySecurity,
        TimeProvider timeProvider)
    {
        _coordinator = coordinator;
        _rcon = rcon;
        _options = options.Value;
        _rconOptions = rconOptions.Value;
        _identitySecurity = identitySecurity;
        _timeProvider = timeProvider;
    }

    public bool Enabled => _options.Enabled;

    public bool RequiresSteamOpenId =>
        _options.AuthenticationMode == PlayerPortalAuthenticationMode.OpenIdThenGameCode;

    public async Task<PlayerLoginChallengeResult> RequestCodeAsync(
        string? suppliedUserId,
        string clientIp,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var now = _timeProvider.GetUtcNow();
        var normalizedClientIp = NormalizeClientIp(clientIp);
        string? userId = null;
        try
        {
            ReserveIpRequest(normalizedClientIp, now);
            userId = NormalizeUserId(suppliedUserId);
            ReserveUserRequest(userId, now);
        }
        catch (PlayerPortalException exception)
        {
            _identitySecurity.Audit(
                PlayerIdentitySecurityEvents.CodeRequest,
                "denied",
                exception.Code,
                "suspicious",
                userId,
                normalizedClientIp,
                correlationId);
            throw;
        }

        if (_identitySecurity.IsBanned(userId))
        {
            _identitySecurity.Audit(
                PlayerIdentitySecurityEvents.CodeRequest,
                "denied",
                "subject_banned",
                "high",
                userId,
                normalizedClientIp,
                correlationId);
            throw AccountBanned();
        }

        ExtractionLivePlayer? livePlayer;
        try
        {
            livePlayer = await _coordinator.TryGetPlayerLocationAsync(userId, cancellationToken);
            if (livePlayer is null || !livePlayer.Online)
            {
                throw new ExtractionModeException(
                    "PLAYER_NOT_ONLINE",
                    "The player must be online to receive a portal login code.",
                    StatusCodes.Status409Conflict);
            }
            if (RequiresSteamOpenId &&
                (string.IsNullOrWhiteSpace(livePlayer.PlayerUid) ||
                 !Guid.TryParse(livePlayer.PlayerUid, out var observedPlayerUid) ||
                 observedPlayerUid == Guid.Empty))
            {
                throw new ExtractionModeException(
                    "PLAYER_BINDING_NOT_OBSERVED",
                    "The current online player has no complete PlayerUID.",
                    StatusCodes.Status409Conflict);
            }
        }
        catch (Exception exception) when (
            exception is ExtractionModeException or InvalidOperationException or IOException)
        {
            _identitySecurity.Audit(
                PlayerIdentitySecurityEvents.CodeRequest,
                "failed",
                "player_not_available",
                "normal",
                userId,
                normalizedClientIp,
                correlationId);
            throw new PlayerPortalException(
                "AUTH_CODE_DELIVERY_UNAVAILABLE",
                "验证码无法送达，请确认角色已经在线并完成加载后重试。",
                StatusCodes.Status409Conflict);
        }

        var capability = await ProbePrivateMessagingAsync(cancellationToken);
        if (!capability.Success)
        {
            _identitySecurity.Audit(
                PlayerIdentitySecurityEvents.CodeRequest,
                "failed",
                "message_channel_unavailable",
                "normal",
                userId,
                normalizedClientIp,
                correlationId);
            throw new PlayerPortalException(
                "AUTH_MESSAGE_CHANNEL_UNAVAILABLE",
                "游戏内验证码消息通道暂时不可用，请稍后重试。",
                StatusCodes.Status503ServiceUnavailable);
        }

        var challengeId = CreateOpaqueToken(ChallengeIdBytes);
        var code = RandomNumberGenerator
            .GetInt32(0, 100_000_000)
            .ToString("D8", CultureInfo.InvariantCulture);
        var expiresAt = now.AddMinutes(_options.ChallengeLifetimeMinutes);
        var digest = ComputeCodeDigest(challengeId, code);
        var message = $"幻兽商域登录验证码：{code}。5分钟内有效，请勿向任何人透露。";

        var delivery = await _rcon.SendPrivateInfoMessageAsync(
            userId,
            message,
            cancellationToken);
        if (!delivery.Success)
        {
            // The code is deliberately not stored after a failed or uncertain dispatch.
            _identitySecurity.Audit(
                PlayerIdentitySecurityEvents.CodeRequest,
                "failed",
                "delivery_unavailable",
                "normal",
                userId,
                normalizedClientIp,
                correlationId);
            throw new PlayerPortalException(
                "AUTH_CODE_DELIVERY_UNAVAILABLE",
                "验证码无法送达，请稍后重试。",
                StatusCodes.Status503ServiceUnavailable);
        }

        lock (_sync)
        {
            CleanupExpiredIfDueLocked(now);
            foreach (var oldChallengeId in _challenges
                         .Where(pair => string.Equals(
                             pair.Value.UserId,
                             userId,
                             StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _challenges.Remove(oldChallengeId);
            }
            if (_challenges.Count >= MaximumChallenges)
            {
                var oldest = _challenges.MinBy(pair => pair.Value.ExpiresAt).Key;
                _challenges.Remove(oldest);
            }
            _challenges[challengeId] = new LoginChallenge(
                challengeId,
                userId,
                livePlayer.PlayerUid,
                digest,
                expiresAt,
                Attempts: 0);
        }

        // Never return or log the code or the generated RCON command.
        _identitySecurity.Audit(
            PlayerIdentitySecurityEvents.CodeRequest,
            "succeeded",
            "delivered_in_game",
            "normal",
            userId,
            normalizedClientIp,
            correlationId);
        return new PlayerLoginChallengeResult(
            challengeId,
            expiresAt,
            _options.UserCooldownSeconds);
    }

    public async Task<PlayerPortalSessionCreation> VerifyAsync(
        string? challengeId,
        string? suppliedCode,
        string? clientIp,
        string? correlationId,
        string? expectedOpenIdUserId,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var now = _timeProvider.GetUtcNow();
        var safeChallengeId = challengeId is { Length: 43 } && OpaqueTokenPattern().IsMatch(challengeId)
            ? challengeId
            : string.Empty;
        var safeCode = suppliedCode is { Length: 8 } && EightDigitCodePattern().IsMatch(suppliedCode)
            ? suppliedCode
            : string.Empty;

        LoginChallenge? verifiedChallenge = null;
        LoginChallenge? attemptedChallenge = null;
        lock (_sync)
        {
            CleanupExpiredIfDueLocked(now);
            var found = _challenges.TryGetValue(safeChallengeId, out var challenge) &&
                challenge!.ExpiresAt > now;
            if (!found && challenge is not null)
            {
                _challenges.Remove(safeChallengeId);
            }
            var actualDigest = ComputeCodeDigest(safeChallengeId, safeCode);
            var expectedDigest = found ? challenge!.CodeDigest : _dummyDigest;
            var matches = CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest);
            attemptedChallenge = challenge;

            if (!found || !matches || safeCode.Length != 8)
            {
                if (found)
                {
                    var attempts = challenge!.Attempts + 1;
                    if (attempts >= _options.VerificationMaxAttempts)
                    {
                        _challenges.Remove(safeChallengeId);
                    }
                    else
                    {
                        _challenges[safeChallengeId] = challenge with { Attempts = attempts };
                    }
                }
            }
            else
            {
                _challenges.Remove(safeChallengeId);
                verifiedChallenge = challenge;
            }
        }

        var normalizedClientIp = NormalizeClientIp(clientIp ?? "unavailable");
        if (verifiedChallenge is null)
        {
            _identitySecurity.Audit(
                PlayerIdentitySecurityEvents.CodeVerification,
                "denied",
                "invalid_or_expired_code",
                "suspicious",
                attemptedChallenge?.UserId,
                normalizedClientIp,
                correlationId);
            throw InvalidChallenge();
        }

        string? boundPlayerUid = verifiedChallenge.PlayerUid;
        if (RequiresSteamOpenId)
        {
            if (string.IsNullOrWhiteSpace(expectedOpenIdUserId) ||
                !string.Equals(
                    verifiedChallenge.UserId,
                    expectedOpenIdUserId,
                    StringComparison.OrdinalIgnoreCase))
            {
                _identitySecurity.Audit(
                    PlayerIdentitySecurityEvents.CodeVerification,
                    "denied",
                    "openid_challenge_subject_mismatch",
                    "high",
                    verifiedChallenge.UserId,
                    normalizedClientIp,
                    correlationId);
                throw new PlayerPortalException(
                    "STEAM_OPENID_IDENTITY_MISMATCH",
                    "Steam 身份与游戏内验证码账号不一致，请重新开始登录。",
                    StatusCodes.Status403Forbidden);
            }

            try
            {
                var context = await _coordinator.GetAccountContextAsync(
                    verifiedChallenge.UserId,
                    requireOnline: true,
                    cancellationToken);
                var binding = context.IdentityBinding;
                if (binding is null ||
                    string.IsNullOrWhiteSpace(verifiedChallenge.PlayerUid) ||
                    !string.Equals(
                        binding.PlayerUid,
                        NormalizePlayerUid(verifiedChallenge.PlayerUid),
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        binding.PlatformSubject,
                        verifiedChallenge.UserId,
                        StringComparison.OrdinalIgnoreCase) ||
                    context.Season.WorldId is null ||
                    !string.Equals(
                        binding.WorldId,
                        context.Season.WorldId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new PlayerPortalException(
                        "PLAYER_WORLD_BINDING_FAILED",
                        "无法把 Steam 身份绑定到当前周在线角色，请保持角色在线后重试。",
                        StatusCodes.Status409Conflict);
                }
                boundPlayerUid = binding.PlayerUid;
            }
            catch (PlayerPortalException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ExtractionModeException or InvalidOperationException or IOException)
            {
                _identitySecurity.Audit(
                    PlayerIdentitySecurityEvents.CodeVerification,
                    "denied",
                    "world_playeruid_binding_failed",
                    "high",
                    verifiedChallenge.UserId,
                    normalizedClientIp,
                    correlationId);
                throw new PlayerPortalException(
                    "PLAYER_WORLD_BINDING_FAILED",
                    "无法把 Steam 身份绑定到当前周在线角色，请保持角色在线后重试。",
                    StatusCodes.Status409Conflict);
            }
        }

        var creation = _identitySecurity.CreateSessionIfAllowed(
            verifiedChallenge.UserId,
            boundPlayerUid,
            TimeSpan.FromHours(_options.SessionLifetimeHours));
        if (creation is null)
        {
            _identitySecurity.Audit(
                PlayerIdentitySecurityEvents.CodeVerification,
                "denied",
                "subject_banned",
                "high",
                verifiedChallenge.UserId,
                normalizedClientIp,
                correlationId);
            throw AccountBanned();
        }
        _identitySecurity.Audit(
            PlayerIdentitySecurityEvents.CodeVerification,
            "succeeded",
            "session_created",
            "normal",
            verifiedChallenge.UserId,
            normalizedClientIp,
            correlationId);
        return creation;
    }

    public PlayerPortalSession? Authenticate(HttpContext context)
    {
        EnsureEnabled();
        if (!context.Request.Cookies.TryGetValue(_options.CookieName, out var token) ||
            token.Length != 43 ||
            !OpaqueTokenPattern().IsMatch(token))
        {
            if (context.Request.Cookies.ContainsKey(_options.CookieName))
            {
                _identitySecurity.Audit(
                    PlayerIdentitySecurityEvents.SessionRejected,
                    "denied",
                    "malformed_session_cookie",
                    "suspicious",
                    null,
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.TraceIdentifier);
            }
            return null;
        }

        var session = _identitySecurity.Authenticate(token);
        if (session is null)
        {
            _identitySecurity.Audit(
                PlayerIdentitySecurityEvents.SessionRejected,
                "denied",
                "unknown_expired_or_revoked_session",
                "suspicious",
                null,
                context.Connection.RemoteIpAddress?.ToString(),
                context.TraceIdentifier);
        }
        return session;
    }

    public PlayerPortalSession RequireSession(HttpContext context) =>
        Authenticate(context) ?? throw new PlayerPortalException(
            "PLAYER_SESSION_REQUIRED",
            "请先使用游戏内验证码登录玩家商城。",
            StatusCodes.Status401Unauthorized);

    public void RequireAllowedOrigin(HttpContext context)
    {
        EnsureEnabled();
        var origins = context.Request.Headers.Origin;
        if (origins.Count != 1 ||
            origins[0] is not { Length: > 0 } supplied ||
            supplied.Contains(',') ||
            !PlayerPortalOptions.TryNormalizeOrigin(supplied, out var normalized) ||
            !_options.AllowedOrigins.Any(origin =>
                PlayerPortalOptions.TryNormalizeOrigin(origin, out var allowed) &&
                string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PlayerPortalException(
                "ORIGIN_NOT_ALLOWED",
                "请求来源缺失或不在玩家商城允许列表中。",
                StatusCodes.Status403Forbidden);
        }
    }

    public void RequireCsrf(HttpContext context, PlayerPortalSession session)
    {
        var supplied = context.Request.Headers["X-CSRF-Token"];
        if (supplied.Count != 1 || supplied[0] is not { Length: 43 } token)
        {
            throw InvalidCsrf();
        }
        var expectedBytes = Encoding.ASCII.GetBytes(session.CsrfToken);
        var actualBytes = Encoding.ASCII.GetBytes(token);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw InvalidCsrf();
        }
    }

    public void AppendSessionCookie(HttpContext context, PlayerPortalSessionCreation creation)
    {
        if (context.Request.Cookies.TryGetValue(_options.CookieName, out var previousToken) &&
            previousToken.Length == 43 &&
            OpaqueTokenPattern().IsMatch(previousToken) &&
            !string.Equals(previousToken, creation.SessionToken, StringComparison.Ordinal))
        {
            _identitySecurity.Revoke(previousToken);
        }
        context.Response.Cookies.Append(
            _options.CookieName,
            creation.SessionToken,
            CookieOptions(creation.Session.ExpiresAt));
    }

    public void RevokeCreatedSession(PlayerPortalSessionCreation creation) =>
        _identitySecurity.Revoke(creation.SessionToken);

    public void Logout(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(_options.CookieName, out var token) &&
            token.Length == 43 &&
            OpaqueTokenPattern().IsMatch(token))
        {
            var session = _identitySecurity.Authenticate(token);
            var revoked = _identitySecurity.Revoke(token);
            _identitySecurity.Audit(
                PlayerIdentitySecurityEvents.SessionLogout,
                revoked ? "succeeded" : "ignored",
                revoked ? "session_revoked" : "session_not_found",
                "normal",
                session?.UserId,
                context.Connection.RemoteIpAddress?.ToString(),
                context.TraceIdentifier,
                revoked ? 1 : 0);
        }
        context.Response.Cookies.Delete(_options.CookieName, CookieOptions(null));
    }

    private async Task<RconOperationResult> ProbePrivateMessagingAsync(
        CancellationToken cancellationToken)
    {
        if (!_rconOptions.Enabled)
        {
            return RconOperationResult.Rejected("rcon_disabled", "RCON is disabled.");
        }

        var commands = await _rcon.GetCommandsAsync(cancellationToken);
        if (!commands.Success)
        {
            return commands;
        }
        var hasSend = RconCapabilityCatalog.ContainsExact(commands.Response, "send:3");
        if (!hasSend)
        {
            return RconOperationResult.Rejected(
                "rcon_send_capability_missing",
                "The authenticated RCON command catalog does not expose send:3.");
        }

        var version = await _rcon.GetVersionAsync(cancellationToken);
        if (!version.Success)
        {
            return version;
        }
        try
        {
            var document = JsonNode.Parse(version.Response ?? string.Empty) as JsonObject;
            var gameVersion = document?["game_version"]?.GetValue<string>();
            var palDefenderVersion = document?["paldefender"]?["full"]?.GetValue<string>();
            if (!string.Equals(gameVersion, _rconOptions.ApprovedGameVersion, StringComparison.Ordinal) ||
                !string.Equals(
                    palDefenderVersion,
                    _rconOptions.ApprovedPalDefenderVersion,
                    StringComparison.Ordinal))
            {
                return RconOperationResult.Rejected(
                    "rcon_version_not_approved",
                    "The game or PalDefender version is not approved for player login messaging.");
            }
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            return RconOperationResult.Rejected(
                "rcon_version_response_invalid",
                "The RCON version response was invalid.");
        }
        return RconOperationResult.Succeeded(string.Empty);
    }

    private void ReserveIpRequest(string clientIp, DateTimeOffset now)
    {
        lock (_sync)
        {
            CleanupRateLimitBucketsLocked(now);
            var windowStart = now.AddMinutes(-_options.RateLimitWindowMinutes);
            EnsureBucketCapacity(_ipRequests, clientIp);
            var ipQueue = GetPrunedQueue(_ipRequests, clientIp, windowStart);
            var ipRetry = CooldownRetryAfter(
                _lastIpRequest.GetValueOrDefault(clientIp),
                now,
                _options.IpCooldownSeconds);
            if (ipRetry > 0)
            {
                throw RateLimited(ipRetry);
            }
            if (ipQueue.Count >= _options.IpRequestsPerWindow)
            {
                var retry = Math.Max(
                    1,
                    (int)Math.Ceiling((ipQueue.Peek().AddMinutes(
                        _options.RateLimitWindowMinutes) - now).TotalSeconds));
                throw RateLimited(retry);
            }
            ipQueue.Enqueue(now);
            _lastIpRequest[clientIp] = now;
        }
    }

    private void ReserveUserRequest(string userId, DateTimeOffset now)
    {
        lock (_sync)
        {
            CleanupRateLimitBucketsLocked(now);
            var windowStart = now.AddMinutes(-_options.RateLimitWindowMinutes);
            EnsureBucketCapacity(_userRequests, userId);
            var userQueue = GetPrunedQueue(_userRequests, userId, windowStart);
            var userRetry = CooldownRetryAfter(
                _lastUserRequest.GetValueOrDefault(userId),
                now,
                _options.UserCooldownSeconds);
            if (userRetry > 0)
            {
                throw RateLimited(userRetry);
            }
            if (userQueue.Count >= _options.UserRequestsPerWindow)
            {
                var retry = Math.Max(
                    1,
                    (int)Math.Ceiling((userQueue.Peek().AddMinutes(
                        _options.RateLimitWindowMinutes) - now).TotalSeconds));
                throw RateLimited(retry);
            }
            userQueue.Enqueue(now);
            _lastUserRequest[userId] = now;
        }
    }

    private void CleanupRateLimitBucketsLocked(DateTimeOffset now)
    {
        var windowStart = now.AddMinutes(-_options.RateLimitWindowMinutes);
        CleanupBuckets(_userRequests, _lastUserRequest, windowStart);
        CleanupBuckets(_ipRequests, _lastIpRequest, windowStart);
    }

    private static void CleanupBuckets(
        Dictionary<string, Queue<DateTimeOffset>> buckets,
        Dictionary<string, DateTimeOffset> lastRequests,
        DateTimeOffset windowStart)
    {
        foreach (var key in buckets.Keys.ToArray())
        {
            var queue = buckets[key];
            while (queue.TryPeek(out var timestamp) && timestamp <= windowStart)
            {
                queue.Dequeue();
            }
            if (queue.Count == 0)
            {
                buckets.Remove(key);
                lastRequests.Remove(key);
            }
        }
    }

    private static void EnsureBucketCapacity(
        Dictionary<string, Queue<DateTimeOffset>> buckets,
        string key)
    {
        if (!buckets.ContainsKey(key) && buckets.Count >= MaximumRateLimitKeys)
        {
            throw RateLimited(60);
        }
    }

    private void CleanupExpiredIfDueLocked(DateTimeOffset now)
    {
        if (now < _nextExpirationCleanupAt)
        {
            return;
        }
        foreach (var challengeId in _challenges
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _challenges.Remove(challengeId);
        }
        _nextExpirationCleanupAt = now.Add(ExpirationCleanupInterval);
    }

    private static Queue<DateTimeOffset> GetPrunedQueue(
        Dictionary<string, Queue<DateTimeOffset>> buckets,
        string key,
        DateTimeOffset windowStart)
    {
        if (!buckets.TryGetValue(key, out var queue))
        {
            queue = new Queue<DateTimeOffset>();
            buckets[key] = queue;
        }
        while (queue.TryPeek(out var timestamp) && timestamp <= windowStart)
        {
            queue.Dequeue();
        }
        return queue;
    }

    private static int CooldownRetryAfter(
        DateTimeOffset previous,
        DateTimeOffset now,
        int cooldownSeconds)
    {
        if (previous == default)
        {
            return 0;
        }
        return Math.Max(0, (int)Math.Ceiling(
            (previous.AddSeconds(cooldownSeconds) - now).TotalSeconds));
    }

    private byte[] ComputeCodeDigest(string challengeId, string code)
    {
        var bytes = Encoding.ASCII.GetBytes($"{challengeId}:{code}");
        return HMACSHA256.HashData(_challengePepper, bytes);
    }

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = _options.CookieSecure,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/api/v1/player",
        Expires = expires,
        MaxAge = expires is null ? null : expires - _timeProvider.GetUtcNow()
    };

    private static string CreateOpaqueToken(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string NormalizeUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw InvalidUserId();
        }
        var normalized = userId.Trim().ToLowerInvariant();
        if (!UserIdPattern().IsMatch(normalized))
        {
            throw InvalidUserId();
        }
        return normalized;
    }

    private static string NormalizeClientIp(string clientIp) =>
        string.IsNullOrWhiteSpace(clientIp) ? "unavailable" : clientIp.Trim();

    private static string NormalizePlayerUid(string value) =>
        Guid.Parse(value.Trim()).ToString("N").ToLowerInvariant();

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new PlayerPortalException(
                "PLAYER_PORTAL_DISABLED",
                "玩家商城当前未启用。",
                StatusCodes.Status404NotFound);
        }
    }

    private static PlayerPortalException InvalidUserId() => new(
        "INVALID_PLAYER_USER_ID",
        "请输入受支持的平台 UserId，例如 steam_7656119...。",
        StatusCodes.Status400BadRequest);

    private static PlayerPortalException InvalidChallenge() => new(
        "INVALID_OR_EXPIRED_LOGIN_CODE",
        "验证码无效或已经过期。",
        StatusCodes.Status401Unauthorized);

    private static PlayerPortalException InvalidCsrf() => new(
        "CSRF_TOKEN_INVALID",
        "玩家会话安全令牌缺失或无效，请刷新页面后重试。",
        StatusCodes.Status403Forbidden);

    private static PlayerPortalException RateLimited(int retryAfterSeconds) => new(
        "AUTH_RATE_LIMITED",
        "验证码请求过于频繁，请等待后再试。",
        StatusCodes.Status429TooManyRequests,
        retryAfterSeconds);

    private static PlayerPortalException AccountBanned() => new(
        "PLAYER_ACCOUNT_BANNED",
        "该平台账号已被管理员封禁，无法登录玩家门户。",
        StatusCodes.Status403Forbidden);

    [GeneratedRegex(
        "^(?:steam|gdk|xbox|xuid|epic)_[a-z0-9]{3,64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex UserIdPattern();

    [GeneratedRegex("^[0-9]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex EightDigitCodePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueTokenPattern();

    private sealed record LoginChallenge(
        string ChallengeId,
        string UserId,
        string? PlayerUid,
        byte[] CodeDigest,
        DateTimeOffset ExpiresAt,
        int Attempts);
}

public sealed record PlayerLoginChallengeResult(
    string ChallengeId,
    DateTimeOffset ExpiresAt,
    int RetryAfterSeconds);

public sealed record PlayerPortalSession(
    string UserId,
    string CsrfToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? PlayerUid = null);

public sealed record PlayerPortalSessionCreation(
    string SessionToken,
    PlayerPortalSession Session);

public sealed class PlayerPortalException : Exception
{
    public PlayerPortalException(
        string code,
        string message,
        int statusCode,
        int? retryAfterSeconds = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public string Code { get; }

    public int StatusCode { get; }

    public int? RetryAfterSeconds { get; }
}

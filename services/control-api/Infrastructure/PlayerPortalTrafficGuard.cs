using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Applies the public player-portal traffic boundary after trusted forwarded
/// headers have been processed. The concurrency admission is deliberately
/// non-queuing, and the fixed-window buckets are bounded to prevent a forged or
/// highly distributed address set from growing process memory without limit.
/// </summary>
public sealed class PlayerPortalTrafficGuard
{
    private const int MaximumRateLimitKeys = 8_192;

    private readonly object _sync = new();
    private readonly Dictionary<string, FixedWindowBucket> _buckets = new(StringComparer.Ordinal);
    private readonly PlayerPortalOptions _options;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _nextCleanupAt = DateTimeOffset.MinValue;
    private int _activeRequests;

    public PlayerPortalTrafficGuard(
        IOptions<PlayerPortalOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public bool TryAcquire(
        string scope,
        string clientIp,
        out IDisposable? lease,
        out int retryAfterSeconds,
        out string rejectionCode)
    {
        lease = null;
        retryAfterSeconds = 1;
        rejectionCode = "PLAYER_PORTAL_CONCURRENCY_LIMITED";

        if (Interlocked.Increment(ref _activeRequests) > _options.ConcurrentRequestLimit)
        {
            Interlocked.Decrement(ref _activeRequests);
            return false;
        }

        try
        {
            if (!TryReserveFixedWindow(scope, clientIp, out retryAfterSeconds))
            {
                Interlocked.Decrement(ref _activeRequests);
                rejectionCode = "PLAYER_PORTAL_RATE_LIMITED";
                return false;
            }

            lease = new RequestLease(this);
            return true;
        }
        catch
        {
            Interlocked.Decrement(ref _activeRequests);
            throw;
        }
    }

    private bool TryReserveFixedWindow(
        string scope,
        string clientIp,
        out int retryAfterSeconds)
    {
        var now = _timeProvider.GetUtcNow();
        var windowSeconds = _options.TrafficRateLimitWindowSeconds;
        var windowStartUnix = now.ToUnixTimeSeconds();
        windowStartUnix -= windowStartUnix % windowSeconds;
        var windowStart = DateTimeOffset.FromUnixTimeSeconds(windowStartUnix);
        var limit = string.Equals(scope, "auth", StringComparison.Ordinal)
            ? _options.AuthRequestsPerWindow
            : _options.MeRequestsPerWindow;
        var key = $"{scope}\n{NormalizeClientIp(clientIp)}";

        lock (_sync)
        {
            CleanupBucketsIfDueLocked(now, force: _buckets.Count >= MaximumRateLimitKeys);
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                if (_buckets.Count >= MaximumRateLimitKeys)
                {
                    retryAfterSeconds = windowSeconds;
                    return false;
                }
                bucket = new FixedWindowBucket(windowStart, 0);
                _buckets[key] = bucket;
            }
            else if (bucket.WindowStart != windowStart)
            {
                bucket.WindowStart = windowStart;
                bucket.Count = 0;
            }

            if (bucket.Count >= limit)
            {
                retryAfterSeconds = Math.Max(
                    1,
                    (int)Math.Ceiling(
                        (bucket.WindowStart.AddSeconds(windowSeconds) - now).TotalSeconds));
                return false;
            }

            bucket.Count++;
            retryAfterSeconds = 0;
            return true;
        }
    }

    private void CleanupBucketsIfDueLocked(DateTimeOffset now, bool force)
    {
        if (!force && now < _nextCleanupAt)
        {
            return;
        }

        var staleBefore = now.AddSeconds(-_options.TrafficRateLimitWindowSeconds);
        foreach (var key in _buckets
                     .Where(pair => pair.Value.WindowStart <= staleBefore)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _buckets.Remove(key);
        }
        _nextCleanupAt = now.AddSeconds(_options.TrafficRateLimitWindowSeconds);
    }

    private void Release() => Interlocked.Decrement(ref _activeRequests);

    private static string NormalizeClientIp(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unavailable" : value.Trim();

    private sealed class FixedWindowBucket(DateTimeOffset windowStart, int count)
    {
        public DateTimeOffset WindowStart { get; set; } = windowStart;

        public int Count { get; set; } = count;
    }

    private sealed class RequestLease(PlayerPortalTrafficGuard owner) : IDisposable
    {
        private PlayerPortalTrafficGuard? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

public sealed class PlayerPortalTrafficGuardMiddleware
{
    private const string PlayerPortalPrefix = "/api/v1/player";
    private const string PlayerPortalAuthPrefix = "/api/v1/player/auth";
    private const string PlayerPortalMePrefix = "/api/v1/player/me";

    private readonly RequestDelegate _next;

    public PlayerPortalTrafficGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        PlayerPortalTrafficGuard guard,
        IOptions<PlayerPortalOptions> options)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!IsWithin(path, PlayerPortalPrefix))
        {
            await _next(context);
            return;
        }

        var maximumBodyBytes = Math.Min(options.Value.MaximumRequestBodyBytes, 16 * 1024);
        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = maximumBodyBytes;
        }
        if (context.Request.ContentLength is long contentLength &&
            contentLength > maximumBodyBytes)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "PLAYER_PORTAL_REQUEST_TOO_LARGE",
                "玩家商城请求体不能超过 16 KiB。");
            return;
        }

        var scope = IsWithin(path, PlayerPortalAuthPrefix) ? "auth" : "me";
        var remoteIp = context.Connection.RemoteIpAddress;
        var clientIp = remoteIp is null
            ? "unavailable"
            : (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString();
        if (!guard.TryAcquire(
                scope,
                clientIp,
                out var lease,
                out var retryAfterSeconds,
                out var rejectionCode))
        {
            context.Response.Headers.RetryAfter = Math.Max(1, retryAfterSeconds).ToString();
            var message = rejectionCode == "PLAYER_PORTAL_CONCURRENCY_LIMITED"
                ? "玩家商城当前请求过多，请稍后重试。"
                : "该网络地址的玩家商城请求过于频繁，请稍后重试。";
            await WriteErrorAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                rejectionCode,
                message);
            return;
        }

        using (lease)
        {
            await _next(context);
        }
    }

    private static bool IsWithin(string path, string prefix) =>
        string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) ||
        (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
         path.Length > prefix.Length &&
         path[prefix.Length] == '/');

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new ApiError(code, message));
    }
}

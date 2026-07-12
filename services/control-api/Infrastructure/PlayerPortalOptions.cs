using System.Text.RegularExpressions;

namespace PalControl.ControlApi.Infrastructure;

public sealed partial class PlayerPortalOptions
{
    public bool Enabled { get; init; }

    public string CookieName { get; init; } = "pal_player_session";

    // Keep true behind HTTPS. Local HTTP development may explicitly override it.
    public bool CookieSecure { get; init; } = true;

    public string[] AllowedOrigins { get; init; } = [];

    public int ChallengeLifetimeMinutes { get; init; } = 5;

    public int VerificationMaxAttempts { get; init; } = 5;

    public int SessionLifetimeHours { get; init; } = 12;

    public int UserCooldownSeconds { get; init; } = 60;

    public int IpCooldownSeconds { get; init; } = 2;

    public int RateLimitWindowMinutes { get; init; } = 15;

    public int UserRequestsPerWindow { get; init; } = 5;

    public int IpRequestsPerWindow { get; init; } = 30;

    public int TrafficRateLimitWindowSeconds { get; init; } = 60;

    public int AuthRequestsPerWindow { get; init; } = 30;

    public int MeRequestsPerWindow { get; init; } = 240;

    public int ConcurrentRequestLimit { get; init; } = 64;

    public int MaximumRequestBodyBytes { get; init; } = 16 * 1024;

    public bool IsValid(out string? error)
    {
        if (!CookieNamePattern().IsMatch(CookieName))
        {
            error = "CookieName must contain 1 to 64 safe cookie-name characters.";
            return false;
        }
        if (AllowedOrigins is null || (Enabled && AllowedOrigins.Length == 0))
        {
            error = "An enabled PlayerPortal requires at least one explicit AllowedOrigins entry.";
            return false;
        }
        var uniqueOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in AllowedOrigins)
        {
            if (!TryNormalizeOrigin(origin, out var normalized) ||
                !uniqueOrigins.Add(normalized))
            {
                error = "Each PlayerPortal AllowedOrigins entry must be a unique absolute HTTP(S) origin without credentials, path, query, or fragment.";
                return false;
            }
        }
        if (ChallengeLifetimeMinutes != 5 || VerificationMaxAttempts != 5)
        {
            error = "Player login challenges must expire in 5 minutes and allow at most 5 verification attempts.";
            return false;
        }
        if (SessionLifetimeHours is < 1 or > 24)
        {
            error = "SessionLifetimeHours must be between 1 and 24.";
            return false;
        }
        if (UserCooldownSeconds is < 1 or > 600 || IpCooldownSeconds is < 1 or > 60)
        {
            error = "Player portal request cooldowns are outside their safe ranges.";
            return false;
        }
        if (RateLimitWindowMinutes is < 1 or > 60 ||
            UserRequestsPerWindow is < 1 or > 20 ||
            IpRequestsPerWindow is < 1 or > 200 ||
            IpRequestsPerWindow < UserRequestsPerWindow)
        {
            error = "Player portal request-window limits are outside their safe ranges.";
            return false;
        }
        if (TrafficRateLimitWindowSeconds is < 10 or > 3600 ||
            AuthRequestsPerWindow is < 5 or > 1000 ||
            MeRequestsPerWindow is < 10 or > 10_000 ||
            ConcurrentRequestLimit is < 1 or > 1024 ||
            MaximumRequestBodyBytes is < 1024 or > 16 * 1024)
        {
            error = "Player portal traffic limits are outside their safe ranges.";
            return false;
        }

        error = null;
        return true;
    }

    internal static bool TryNormalizeOrigin(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }
        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CookieNamePattern();
}

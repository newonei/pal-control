using System.Text.RegularExpressions;

namespace PalControl.ControlApi.Infrastructure;

public enum PlayerPortalAuthenticationMode
{
    TrustedGameCode,
    OpenIdThenGameCode
}

public sealed partial class PlayerPortalOptions
{
    public const string OfficialSteamOpenIdEndpoint =
        "https://steamcommunity.com/openid/";

    public bool Enabled { get; init; }

    /// <summary>
    /// TrustedGameCode is intentionally the compatibility default for a local
    /// or trusted-friends deployment. Public Steam deployments must opt in to
    /// OpenIdThenGameCode and production startup validation enforces it.
    /// </summary>
    public PlayerPortalAuthenticationMode AuthenticationMode { get; init; } =
        PlayerPortalAuthenticationMode.TrustedGameCode;

    public bool PublicSteam { get; init; }

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

    public SteamOpenIdOptions OpenId { get; init; } = new();

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
        if (Enabled && PublicSteam &&
            AuthenticationMode != PlayerPortalAuthenticationMode.OpenIdThenGameCode)
        {
            error = "A public Steam player portal requires AuthenticationMode=OpenIdThenGameCode.";
            return false;
        }
        if (Enabled &&
            AuthenticationMode == PlayerPortalAuthenticationMode.OpenIdThenGameCode)
        {
            if (!CookieSecure)
            {
                error = "Steam OpenID login requires Secure cookies.";
                return false;
            }
            if (!OpenId.IsValid(out error))
            {
                return false;
            }
            if (string.Equals(OpenId.StateCookieName, CookieName, StringComparison.Ordinal) ||
                string.Equals(OpenId.PendingCookieName, CookieName, StringComparison.Ordinal))
            {
                error = "Steam OpenID transient cookies must not reuse the player session cookie name.";
                return false;
            }
            if (AllowedOrigins.Any(origin =>
                    !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "Steam OpenID login requires HTTPS PlayerPortal origins.";
                return false;
            }
        }

        error = null;
        return true;
    }

    public bool IsValidForEnvironment(bool isDevelopment, out string? error)
    {
        if (!IsValid(out error) || !Enabled)
        {
            return error is null;
        }
        if (!isDevelopment &&
            AuthenticationMode != PlayerPortalAuthenticationMode.OpenIdThenGameCode)
        {
            error = "A production PlayerPortal requires AuthenticationMode=OpenIdThenGameCode.";
            return false;
        }
        if (AuthenticationMode != PlayerPortalAuthenticationMode.OpenIdThenGameCode)
        {
            error = null;
            return true;
        }

        var providerIsOfficial = string.Equals(
            NormalizeEndpoint(OpenId.ProviderEndpoint),
            OfficialSteamOpenIdEndpoint,
            StringComparison.Ordinal);
        if (!providerIsOfficial &&
            (!isDevelopment || PublicSteam || !OpenId.AllowDevelopmentProvider))
        {
            error = "A non-official Steam OpenID provider is allowed only for a non-public Development portal with AllowDevelopmentProvider=true.";
            return false;
        }
        if ((!isDevelopment || PublicSteam) &&
            (!providerIsOfficial ||
             !Uri.TryCreate(OpenId.ProviderEndpoint, UriKind.Absolute, out var provider) ||
             provider.Scheme != Uri.UriSchemeHttps))
        {
            error = "Production and PublicSteam OpenID must use the official HTTPS provider endpoint.";
            return false;
        }

        error = null;
        return true;
    }

    internal static string NormalizeEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : $"{uri.AbsoluteUri}/";
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

public sealed class SteamOpenIdOptions
{
    public string ProviderEndpoint { get; init; } =
        PlayerPortalOptions.OfficialSteamOpenIdEndpoint;

    public bool AllowDevelopmentProvider { get; init; }

    public string StateCookieName { get; init; } = "pal_player_openid_state";

    public string PendingCookieName { get; init; } = "pal_player_openid_pending";

    public string[] AllowedRealms { get; init; } = [];

    public string[] AllowedReturnUrls { get; init; } = [];

    public int StateLifetimeMinutes { get; init; } = 5;

    public int PendingIdentityLifetimeMinutes { get; init; } = 10;

    public int NonceFreshnessMinutes { get; init; } = 10;

    public int ProviderTimeoutSeconds { get; init; } = 5;

    public int MaximumProviderResponseBytes { get; init; } = 16 * 1024;

    public int StartRequestsPerWindow { get; init; } = 10;

    public int CallbackRequestsPerWindow { get; init; } = 20;

    public int BindingRequestsPerWindow { get; init; } = 10;

    public bool IsValid(out string? error)
    {
        if (!IsSafeCookieName(StateCookieName) ||
            !IsSafeCookieName(PendingCookieName) ||
            string.Equals(StateCookieName, PendingCookieName, StringComparison.Ordinal) ||
            string.Equals(StateCookieName, "pal_player_session", StringComparison.Ordinal))
        {
            error = "Steam OpenID state and pending cookie names must be distinct safe cookie names.";
            return false;
        }
        if (!Uri.TryCreate(ProviderEndpoint, UriKind.Absolute, out var provider) ||
            !string.IsNullOrEmpty(provider.UserInfo) ||
            !string.IsNullOrEmpty(provider.Query) ||
            !string.IsNullOrEmpty(provider.Fragment) ||
            (provider.Scheme != Uri.UriSchemeHttps && provider.Scheme != Uri.UriSchemeHttp))
        {
            error = "Steam OpenID ProviderEndpoint must be an absolute HTTP(S) URL without credentials, query, or fragment.";
            return false;
        }
        if (AllowedRealms is null || AllowedRealms.Length == 0 ||
            AllowedReturnUrls is null || AllowedReturnUrls.Length == 0)
        {
            error = "Steam OpenID requires explicit HTTPS realm and return_to allowlists.";
            return false;
        }

        var realms = new List<Uri>();
        var uniqueRealms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in AllowedRealms)
        {
            if (!TryNormalizeRealm(value, out var realm) ||
                !uniqueRealms.Add(realm.AbsoluteUri))
            {
                error = "Every Steam OpenID realm must be a unique HTTPS origin ending in '/'.";
                return false;
            }
            realms.Add(realm);
        }

        var uniqueReturns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in AllowedReturnUrls)
        {
            if (!TryNormalizeReturnUrl(value, out var returnUrl) ||
                !uniqueReturns.Add(returnUrl.AbsoluteUri) ||
                !realms.Any(realm => SameOrigin(realm, returnUrl)))
            {
                error = "Every Steam OpenID return_to must be a unique HTTPS callback URL under an allowed realm.";
                return false;
            }
        }
        if (StateLifetimeMinutes is < 1 or > 10 ||
            PendingIdentityLifetimeMinutes is < 1 or > 20 ||
            NonceFreshnessMinutes is < 1 or > 30 ||
            ProviderTimeoutSeconds is < 1 or > 10 ||
            MaximumProviderResponseBytes is < 1024 or > 16 * 1024 ||
            StartRequestsPerWindow is < 1 or > 100 ||
            CallbackRequestsPerWindow is < 1 or > 200 ||
            BindingRequestsPerWindow is < 1 or > 100)
        {
            error = "Steam OpenID lifetimes, provider bounds, or independent rate limits are outside their safe ranges.";
            return false;
        }

        error = null;
        return true;
    }

    internal static bool TryNormalizeRealm(string? value, out Uri realm)
    {
        realm = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }
        realm = new Uri(parsed.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return true;
    }

    internal static bool TryNormalizeReturnUrl(string? value, out Uri returnUrl)
    {
        returnUrl = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            !string.Equals(
                parsed.AbsolutePath,
                "/api/v1/player/auth/steam/callback",
                StringComparison.Ordinal))
        {
            return false;
        }
        returnUrl = parsed;
        return true;
    }

    internal static bool SameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.Ordinal) &&
        string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port;

    private static bool IsSafeCookieName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character =>
            character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or '_' or '-' or '.');
}

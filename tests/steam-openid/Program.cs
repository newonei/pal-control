using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Infrastructure;

var failures = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "pal-control-steam-openid", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var now = new DateTimeOffset(2026, 7, 15, 8, 30, 0, TimeSpan.Zero);
var time = new FixedTimeProvider(now);
var options = ValidOptions();
var identity = new PlayerIdentitySecurityService(
    new PlayerIdentitySecurityStore(root),
    new PlayerPortalSessionRegistry(),
    time);

try
{
    Check(new PlayerPortalOptions().AuthenticationMode ==
          PlayerPortalAuthenticationMode.TrustedGameCode,
        "TrustedGameCode was not the compatibility default");
    Check(!new PlayerPortalOptions
    {
        Enabled = true,
        PublicSteam = true,
        AllowedOrigins = ["https://portal.example.test"]
    }.IsValid(out _), "PublicSteam accepted TrustedGameCode");
    Check(!new PlayerPortalOptions
    {
        Enabled = true,
        AuthenticationMode = PlayerPortalAuthenticationMode.TrustedGameCode,
        AllowedOrigins = ["https://portal.example.test"]
    }.IsValidForEnvironment(isDevelopment: false, out _),
        "production accepted TrustedGameCode");
    var developmentFakeProvider = new SteamOpenIdOptions
    {
        ProviderEndpoint = "http://127.0.0.1:5199/openid/",
        AllowDevelopmentProvider = true,
        AllowedRealms = ["https://portal.example.test/"],
        AllowedReturnUrls =
            ["https://portal.example.test/api/v1/player/auth/steam/callback"]
    };
    Check(!new PlayerPortalOptions
    {
        Enabled = true,
        AuthenticationMode = PlayerPortalAuthenticationMode.OpenIdThenGameCode,
        PublicSteam = true,
        CookieSecure = true,
        AllowedOrigins = ["https://portal.example.test"],
        OpenId = developmentFakeProvider
    }.IsValidForEnvironment(isDevelopment: true, out _),
        "PublicSteam accepted a development fake provider");
    Check(new PlayerPortalOptions
    {
        Enabled = true,
        AuthenticationMode = PlayerPortalAuthenticationMode.OpenIdThenGameCode,
        PublicSteam = false,
        CookieSecure = true,
        AllowedOrigins = ["https://portal.example.test"],
        OpenId = developmentFakeProvider
    }.IsValidForEnvironment(isDevelopment: true, out _),
        "non-public Development rejected an explicitly allowed fake provider");

    await VerifyDirectProviderPostAsync(options, failures);

    var provider = new FakeProvider(valid: true);
    var service = CreateService(provider);
    var started = Start(service);
    var callback = CallbackContext(started, StandardAssertion(started));
    var completed = await service.CompleteCallbackAsync(callback, CancellationToken.None);
    Check(completed.RedirectUrl == "https://portal.example.test/",
        "successful callback did not use the fixed realm redirect");
    Check(provider.CallCount == 1, "successful callback did not call check_authentication once");
    var pendingCookie = CookieValue(callback.Response, options.OpenId.PendingCookieName);
    var pendingContext = Context("/api/v1/player/auth/mode", pendingCookieName: options.OpenId.PendingCookieName,
        pendingCookieValue: pendingCookie);
    var status = service.GetStatus(pendingContext);
    Check(status.PendingPlatformIdentity && !status.TrustedGameCodeFallback,
        "OpenID success established no pending-only identity");
    Check(service.ResolveCodeRequestUserId(pendingContext, null, "openid_game_code") ==
          "steam_76561198000000001", "pending identity did not resolve the Steam subject");
    ExpectPortal(
        "STEAM_OPENID_IDENTITY_MISMATCH",
        () => Task.FromResult(service.ResolveCodeRequestUserId(
            pendingContext, "steam_76561198000000002", "openid_binding")));
    Check(!service.CompletePendingBinding(pendingContext, "steam_76561198000000002"),
        "one Steam pending identity completed another account's binding");
    Check(service.CompletePendingBinding(pendingContext, "steam_76561198000000001"),
        "the exact Steam subject could not complete pending binding");

    await ExpectPortalAsync("STEAM_OPENID_STATE_INVALID", () =>
        service.CompleteCallbackAsync(
            CallbackContext(started, StandardAssertion(started)),
            CancellationToken.None));

    var csrfService = CreateService(new FakeProvider(true));
    var csrfStart = Start(csrfService);
    var noCookie = CallbackContext(csrfStart, StandardAssertion(csrfStart), includeStateCookie: false);
    await ExpectPortalAsync("STEAM_OPENID_STATE_INVALID", () =>
        csrfService.CompleteCallbackAsync(noCookie, CancellationToken.None));

    var claimedService = CreateService(new FakeProvider(true));
    var claimedStart = Start(claimedService);
    var forged = StandardAssertion(claimedStart);
    forged["openid.claimed_id"] = "https://attacker.example/openid/id/76561198000000001";
    forged["openid.identity"] = forged["openid.claimed_id"];
    await ExpectPortalAsync("STEAM_OPENID_ASSERTION_INVALID", () =>
        claimedService.CompleteCallbackAsync(
            CallbackContext(claimedStart, forged), CancellationToken.None));

    var returnService = CreateService(new FakeProvider(true));
    var returnStart = Start(returnService);
    var wrongReturn = StandardAssertion(returnStart);
    wrongReturn["openid.return_to"] =
        "https://attacker.example/api/v1/player/auth/steam/callback?state=" + returnStart.State;
    await ExpectPortalAsync("STEAM_OPENID_ASSERTION_INVALID", () =>
        returnService.CompleteCallbackAsync(
            CallbackContext(returnStart, wrongReturn), CancellationToken.None));

    var failedProvider = new FakeProvider(false);
    var failureService = CreateService(failedProvider);
    var failureStart = Start(failureService);
    await ExpectPortalAsync("STEAM_OPENID_ASSERTION_INVALID", () =>
        failureService.CompleteCallbackAsync(
            CallbackContext(failureStart, StandardAssertion(failureStart)),
            CancellationToken.None));
    await ExpectPortalAsync("STEAM_OPENID_STATE_INVALID", () =>
        failureService.CompleteCallbackAsync(
            CallbackContext(failureStart, StandardAssertion(failureStart)),
            CancellationToken.None));

    var nonceService = CreateService(new FakeProvider(true));
    var firstNonceStart = Start(nonceService, "203.0.113.11");
    var secondNonceStart = Start(nonceService, "203.0.113.12");
    var sharedNonce = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "sameNonce123";
    var firstNonceAssertion = StandardAssertion(firstNonceStart, sharedNonce);
    var secondNonceAssertion = StandardAssertion(secondNonceStart, sharedNonce);
    await nonceService.CompleteCallbackAsync(
        CallbackContext(firstNonceStart, firstNonceAssertion, "203.0.113.11"),
        CancellationToken.None);
    await ExpectPortalAsync("STEAM_OPENID_NONCE_REPLAYED", () =>
        nonceService.CompleteCallbackAsync(
            CallbackContext(secondNonceStart, secondNonceAssertion, "203.0.113.12"),
            CancellationToken.None));

    var beforeRestart = CreateService(new FakeProvider(true));
    var restartStart = Start(beforeRestart);
    var afterRestart = CreateService(new FakeProvider(true));
    await ExpectPortalAsync("STEAM_OPENID_STATE_INVALID", () =>
        afterRestart.CompleteCallbackAsync(
            CallbackContext(restartStart, StandardAssertion(restartStart)),
            CancellationToken.None));

    var auditText = string.Join('|', new PlayerIdentitySecurityStore(root).List(100)
        .Select(item => $"{item.EventType}:{item.Outcome}:{item.ReasonCode}:{item.SubjectFingerprint}"));
    Check(!auditText.Contains(sharedNonce, StringComparison.Ordinal) &&
          !auditText.Contains(firstNonceStart.State, StringComparison.Ordinal) &&
          !auditText.Contains("76561198000000001", StringComparison.Ordinal),
        "security audit retained a raw assertion, state, nonce, or SteamID");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

if (failures.Count != 0)
{
    Console.Error.WriteLine("Steam OpenID contract/E2E failed:");
    foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
    return 1;
}
Console.WriteLine("PASS: Steam OpenID state/CSRF, direct verification, strict identity/return_to, nonce replay, pending binding isolation, redacted audit, and restart invalidation.");
return 0;

PlayerPortalOptions ValidOptions() => new()
{
    Enabled = true,
    AuthenticationMode = PlayerPortalAuthenticationMode.OpenIdThenGameCode,
    PublicSteam = true,
    CookieSecure = true,
    AllowedOrigins = ["https://portal.example.test"],
    OpenId = new SteamOpenIdOptions
    {
        AllowedRealms = ["https://portal.example.test/"],
        AllowedReturnUrls =
            ["https://portal.example.test/api/v1/player/auth/steam/callback"]
    }
};

SteamOpenIdAuthenticationService CreateService(ISteamOpenIdProviderClient provider) => new(
    Options.Create(options), provider, identity, time);

StartFixture Start(
    SteamOpenIdAuthenticationService service,
    string ip = "203.0.113.10")
{
    var context = Context("/api/v1/player/auth/steam/start", ip);
    var result = service.Start(context);
    var authorization = new Uri(result.AuthorizationUrl);
    var authorizationQuery = QueryHelpers.ParseQuery(authorization.Query);
    var returnTo = authorizationQuery["openid.return_to"].Single()!;
    var state = QueryHelpers.ParseQuery(new Uri(returnTo).Query)["state"].Single()!;
    return new StartFixture(
        state,
        returnTo,
        CookieValue(context.Response, options.OpenId.StateCookieName));
}

Dictionary<string, string> StandardAssertion(
    StartFixture started,
    string? nonce = null) => new(StringComparer.Ordinal)
{
    ["openid.ns"] = "http://specs.openid.net/auth/2.0",
    ["openid.mode"] = "id_res",
    ["openid.op_endpoint"] = "https://steamcommunity.com/openid/",
    ["openid.claimed_id"] = "https://steamcommunity.com/openid/id/76561198000000001",
    ["openid.identity"] = "https://steamcommunity.com/openid/id/76561198000000001",
    ["openid.return_to"] = started.ReturnTo,
    ["openid.response_nonce"] = nonce ??
        now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + Guid.NewGuid().ToString("N"),
    ["openid.assoc_handle"] = "contract-association",
    ["openid.signed"] =
        "op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
    ["openid.sig"] = "contract-signature"
};

DefaultHttpContext CallbackContext(
    StartFixture fixture,
    IReadOnlyDictionary<string, string> assertion,
    string ip = "203.0.113.10",
    bool includeStateCookie = true)
{
    var context = Context("/api/v1/player/auth/steam/callback", ip);
    var query = assertion.ToDictionary(
        pair => pair.Key,
        pair => (string?)pair.Value,
        StringComparer.Ordinal);
    query["state"] = fixture.State;
    context.Request.QueryString = QueryString.Create(query);
    if (includeStateCookie)
    {
        context.Request.Headers.Cookie =
            $"{options.OpenId.StateCookieName}={fixture.StateCookie}";
    }
    return context;
}

DefaultHttpContext Context(
    string path,
    string ip = "203.0.113.10",
    string? pendingCookieName = null,
    string? pendingCookieValue = null)
{
    var context = new DefaultHttpContext();
    context.Request.Scheme = "https";
    context.Request.Host = new HostString("portal.example.test");
    context.Request.Path = path;
    context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
    context.TraceIdentifier = Guid.NewGuid().ToString("N");
    if (pendingCookieName is not null && pendingCookieValue is not null)
    {
        context.Request.Headers.Cookie = $"{pendingCookieName}={pendingCookieValue}";
    }
    return context;
}

string CookieValue(HttpResponse response, string name)
{
    var prefix = name + "=";
    var header = response.Headers.SetCookie.Single(value =>
        value is not null && value.StartsWith(prefix, StringComparison.Ordinal));
    var safeHeader = header ?? throw new InvalidOperationException("Set-Cookie value was null.");
    return safeHeader[prefix.Length..safeHeader.IndexOf(';')];
}

void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

async Task ExpectPortalAsync(string code, Func<Task> action)
{
    try
    {
        await action();
        failures.Add($"expected {code}, but the operation succeeded");
    }
    catch (PlayerPortalException exception)
    {
        Check(exception.Code == code, $"expected {code}, received {exception.Code}");
    }
}

void ExpectPortal<T>(string code, Func<Task<T>> action) =>
    ExpectPortalAsync(code, async () => { _ = await action(); }).GetAwaiter().GetResult();

static async Task VerifyDirectProviderPostAsync(
    PlayerPortalOptions options,
    List<string> failures)
{
    var handler = new CapturingHandler();
    using var client = new HttpClient(handler);
    var provider = new SteamOpenIdProviderClient(client, Options.Create(options));
    var valid = await provider.CheckAuthenticationAsync(
        new Dictionary<string, string>
        {
            ["openid.ns"] = "http://specs.openid.net/auth/2.0",
            ["openid.mode"] = "id_res"
        },
        CancellationToken.None);
    if (!valid) failures.Add("direct provider validation did not accept is_valid:true");
    if (handler.Body is null ||
        !handler.Body.Contains("openid.mode=check_authentication", StringComparison.Ordinal) ||
        handler.RequestUri != "https://steamcommunity.com/openid/")
    {
        failures.Add("provider client did not POST check_authentication to the fixed Steam OP");
    }
}

sealed record StartFixture(string State, string ReturnTo, string StateCookie);

sealed class FakeProvider(bool valid) : ISteamOpenIdProviderClient
{
    public int CallCount { get; private set; }

    public Task<bool> CheckAuthenticationAsync(
        IReadOnlyDictionary<string, string> assertion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult(valid);
    }
}

sealed class CapturingHandler : HttpMessageHandler
{
    public string? Body { get; private set; }
    public string? RequestUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri?.AbsoluteUri;
        Body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "ns:http://specs.openid.net/auth/2.0\nis_valid:true\n")
        };
    }
}

sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

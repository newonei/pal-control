using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Content;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PalControl.ControlApi";
});
builder.Configuration.AddJsonFile(
    "appsettings.Local.json",
    optional: true,
    reloadOnChange: true);
var externalConfigurationPath = Environment.GetEnvironmentVariable(
    "PAL_CONTROL_CONFIG_PATH");
if (!string.IsNullOrWhiteSpace(externalConfigurationPath))
{
    builder.Configuration.AddJsonFile(
        Path.GetFullPath(externalConfigurationPath),
        optional: false,
        reloadOnChange: true);
}
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.AddProblemDetails();
var startupSecuritySection = builder.Configuration.GetSection(
    "Security:StartupValidation");
var startupSecurity = startupSecuritySection.Get<StartupSecurityValidationOptions>() ?? new();
builder.Services.AddOptions<StartupSecurityValidationOptions>()
    .Bind(startupSecuritySection)
    .ValidateOnStart();
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<StartupSecurityValidationOptions>,
    StartupSecurityValidator>();
builder.Services.AddOptions<CommandPersistenceOptions>()
    .Bind(builder.Configuration.GetSection("CommandPersistence"))
    .Validate(
        static options => options.IsValid(out _),
        "Command persistence directory and PalDefender queue capacity must be valid.")
    .ValidateOnStart();
builder.Services.AddSingleton<AnnouncementStore>();
builder.Services.AddSingleton<AnnouncementCommandQueue>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<AnnouncementCommandQueue>());
builder.Services.AddSingleton<InGameNotificationStore>();
builder.Services.AddSingleton<InGameNotificationCapabilityService>();
builder.Services.AddSingleton<InGameNotificationCommandQueue>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<InGameNotificationCommandQueue>());
builder.Services.AddSingleton<BridgeState>();
builder.Services.Configure<PalworldRestOptions>(
    builder.Configuration.GetSection("Palworld:OfficialRestApi"));
builder.Services.AddOptions<PalDefenderRestOptions>()
    .Bind(builder.Configuration.GetSection("Palworld:PalDefenderRestApi"))
    .Validate(
        static options => options.IsValid(out _),
        "PalDefender REST API must use an absolute loopback HTTP URL, a valid timeout, and a token or token file when enabled.")
    .ValidateOnStart();
builder.Services.Configure<NativeBridgeOptions>(
    builder.Configuration.GetSection("Palworld:Bridge"));
builder.Services.AddSingleton<NativeBridgeState>();
builder.Services.AddSingleton<NativeBridgeClient>();
builder.Services.AddSingleton<INativeBridgeCommandTransport>(services =>
    services.GetRequiredService<NativeBridgeClient>());
builder.Services.AddHostedService(services =>
    services.GetRequiredService<NativeBridgeClient>());
builder.Services.AddHttpClient<PalworldRestClient>((services, client) =>
{
    var options = services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<PalworldRestOptions>>()
        .Value;
    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 30));
});
builder.Services.AddHttpClient<PalDefenderRestClient>((services, client) =>
{
    var options = services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<PalDefenderRestOptions>>()
        .Value;
    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 30));
});
builder.Services.AddSingleton<PalDefenderCommandQueue>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<PalDefenderCommandQueue>());
builder.Services.AddSingleton<PalworldResourceCatalogService>();
builder.Services.AddOptions<ExtractionModeOptions>()
    .Bind(builder.Configuration.GetSection("ExtractionMode"))
    .Validate(
        static options => options.IsValid(out _),
        "Extraction mode cadence, balances, timezone, and poll interval must be valid.")
    .ValidateOnStart();
builder.Services.AddOptions<ExtractionPersistenceOptions>()
    .Bind(builder.Configuration.GetSection("ExtractionMode:Persistence"))
    .Validate(
        static options => options.IsValid(out _),
        "Extraction persistence directory must be valid.")
    .ValidateOnStart();
builder.Services.AddOptions<TeamEconomyOptions>()
    .Bind(builder.Configuration.GetSection("TeamEconomy"))
    .Validate(
        static options => options.IsValid(out _),
        "Enabled team economy requires a protected invitation pepper and valid projection, invitation, leaderboard, and goal limits.")
    .ValidateOnStart();
builder.Services.AddOptions<ExtractionRconOptions>()
    .Bind(builder.Configuration.GetSection("ExtractionMode:Rcon"))
    .Validate(
        static options => options.IsValid(out _),
        "Enabled extraction RCON must be loopback-only and have approved versions and exactly one password source.")
    .ValidateOnStart();
builder.Services.AddOptions<EconomySafetyOptions>()
    .Bind(builder.Configuration.GetSection("ExtractionMode:Safety"))
    .Validate(
        static options => options.IsValid(out _),
        "Economy safety-gate versions, capacities, and optional Native requirements must be valid.")
    .ValidateOnStart();
builder.Services.AddOptions<EconomyContinuityOptions>()
    .Bind(builder.Configuration.GetSection("ExtractionMode:Continuity"))
    .Validate(
        static options => options.IsValid(out _),
        "Economy backup roots, retention, capacity, RPO and RTO must be valid.")
    .ValidateOnStart();
builder.Services.AddOptions<EconomyObservabilityOptions>()
    .Bind(builder.Configuration.GetSection("ExtractionMode:Observability"))
    .Validate(
        static options => options.IsValid(out _),
        "Economy observability thresholds and evaluation cadence must be valid.")
    .ValidateOnStart();
builder.Services.AddSingleton<SqliteExtractionRepository>(services =>
{
    var options = services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ExtractionPersistenceOptions>>()
        .Value;
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    var dataDirectory = Path.GetFullPath(Path.IsPathRooted(options.DataDirectory)
        ? options.DataDirectory
        : Path.Combine(environment.ContentRootPath, options.DataDirectory));
    return new SqliteExtractionRepository(dataDirectory);
});
builder.Services.AddSingleton<IExtractionRepository>(services =>
    services.GetRequiredService<SqliteExtractionRepository>());
builder.Services.AddSingleton<IPlayerIdentityBindingStore>(services =>
    services.GetRequiredService<SqliteExtractionRepository>());
builder.Services.AddSingleton<IExtractionSettlementPersistence>(services =>
    services.GetRequiredService<SqliteExtractionRepository>());
builder.Services.AddSingleton<ExtractionCommerceService>();
builder.Services.AddSingleton<EconomyAnalyticsStore>();
builder.Services.AddSingleton<SqliteEconomyContentStore>();
builder.Services.AddSingleton<IEconomyContentStore>(services =>
    services.GetRequiredService<SqliteEconomyContentStore>());
builder.Services.AddSingleton<EconomyContentRuntimeService>();
builder.Services.AddHostedService<EconomyContentStartupInitializer>();
builder.Services.AddSingleton<SqliteReliableTaskStore>();
builder.Services.AddSingleton<IReliableTaskContentProvider, ReliableTaskContentProvider>();
builder.Services.AddSingleton<ReliableTaskRuntimeService>();
builder.Services.AddSingleton<ExtractionModeCoordinator>();
builder.Services.AddSingleton<ExtractionDeliveryEvidenceStore>();
builder.Services.AddSingleton<ExtractionDeliveryReceiptStore>();
builder.Services.AddSingleton<PalDefenderItemGrantAdapter>();
builder.Services.AddSingleton<ExtractionRunStore>();
builder.Services.AddSingleton<TeamEconomyStore>();
builder.Services.AddHostedService<TeamEconomyProjectionWorker>();
builder.Services.AddSingleton<IExtractionNativeInventoryAdapter, ExtractionNativeInventoryAdapter>();
builder.Services.AddSingleton<ExtractionSettlementService>();
builder.Services.AddSingleton<IExtractionSettlementExecutor>(services =>
    services.GetRequiredService<ExtractionSettlementService>());
builder.Services.AddSingleton<ExtractionSettlementQueue>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<ExtractionSettlementQueue>());
builder.Services.AddSingleton<ExtractionOperationGate>();
builder.Services.AddSingleton<EconomyContinuityService>();
builder.Services.AddSingleton<WeeklyRolloverStateStore>();
builder.Services.AddSingleton<SeasonSettlementJobStore>();
builder.Services.AddSingleton<SeasonSettlementJobService>();
builder.Services.AddSingleton<SeasonLeaderboardStore>();
builder.Services.AddSingleton<SeasonLeaderboardService>();
builder.Services.AddSingleton<PlayerSeasonSettlementService>();
builder.Services.AddOptions<PlayerNotificationOptions>()
    .Bind(builder.Configuration.GetSection("PlayerNotifications"));
builder.Services.AddSingleton<PlayerNotificationStore>();
builder.Services.AddSingleton<IPlayerGameNotificationDispatcher, PlayerGameNotificationDispatcher>();
builder.Services.AddSingleton<PlayerNotificationProjectionService>();
builder.Services.AddHostedService<PlayerNotificationProjectionWorker>();
builder.Services.AddSingleton<IEconomySafetyDependencyProbe, EconomySafetyDependencyProbe>();
builder.Services.AddSingleton<EconomySafetyGate>();
builder.Services.AddSingleton<IExtractionRconAdapter, ExtractionRconAdapter>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<FederationOptions>()
    .Bind(builder.Configuration.GetSection("Federation"))
    .Validate(
        options => !options.Enabled || string.Equals(
            options.LocalServerId,
            builder.Configuration["ExtractionMode:ServerId"],
            StringComparison.Ordinal),
        "Federation LocalServerId must equal the authoritative ExtractionMode ServerId.")
    .ValidateOnStart();
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<FederationOptions>,
    FederationOptionsValidator>();
builder.Services.AddSingleton(services =>
{
    var options = services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<FederationOptions>>()
        .Value;
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    return new CompatibilityMatrixStore(FederationOptionsValidator.ValidateCore(
        options,
        environment.ContentRootPath,
        environment.IsDevelopment()));
});
builder.Services.AddSingleton<FederationIdentityProtector>();
builder.Services.AddSingleton<FederationInternalAuthenticator>();
builder.Services.AddSingleton<FederationInternalRequestGuard>();
builder.Services.AddSingleton<FederationLocalProfileService>();
builder.Services.AddHttpClient("FederationNodes", client =>
{
    // Per-request linked cancellation enforces the configured federation
    // timeout. A global HttpClient timeout would blur caller cancellation.
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.MaxResponseContentBufferSize = 256 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false
}).RedactLoggedHeaders(_ => true);
builder.Services.AddSingleton<FederationNodeClient>();
builder.Services.AddSingleton<IFederationNodeTransport>(services =>
    services.GetRequiredService<FederationNodeClient>());
builder.Services.AddSingleton<FederationAggregationService>();
var adminAuthenticationSection = builder.Configuration.GetSection(
    "Security:AdminAuthentication");
builder.Services.AddOptions<AdminAuthenticationOptions>(
        AdminAuthenticationDefaults.Scheme)
    .Bind(adminAuthenticationSection)
    .Validate(
        static options => options.IsValid(out _),
        "Administrator API-key principals, role assignments, and TOTP secrets must be valid.")
    .Validate(
        options => !options.EnableLoopbackDevelopmentPrincipal ||
            builder.Configuration.GetValue<bool>("Security:DevelopmentMode"),
        "The loopback administrator principal is allowed only in explicit development mode.")
    .ValidateOnStart();
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = AdminAuthenticationDefaults.Scheme;
        options.DefaultChallengeScheme = AdminAuthenticationDefaults.Scheme;
        options.DefaultForbidScheme = AdminAuthenticationDefaults.Scheme;
    })
    .AddScheme<AdminAuthenticationOptions, AdminApiKeyAuthenticationHandler>(
        AdminAuthenticationDefaults.Scheme,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AdminPolicies.ApiAccess, policy => policy
        .AddAuthenticationSchemes(AdminAuthenticationDefaults.Scheme)
        .RequireAuthenticatedUser()
        .AddRequirements(new AdminApiAccessRequirement()));
    AddRolePolicy(options, AdminPolicies.Viewer, AdminRoles.Viewer);
    AddRolePolicy(options, AdminPolicies.Operator, AdminRoles.Operator);
    AddRolePolicy(options, AdminPolicies.EconomyAdmin, AdminRoles.EconomyAdmin);
    AddRolePolicy(options, AdminPolicies.SeasonAdmin, AdminRoles.SeasonAdmin);
    AddRolePolicy(options, AdminPolicies.Owner, AdminRoles.Owner);
    AddHighRiskPolicy(
        options,
        AdminPolicies.EconomyHighRisk,
        AdminRoles.EconomyAdmin);
    AddHighRiskPolicy(
        options,
        AdminPolicies.SeasonHighRisk,
        AdminRoles.SeasonAdmin);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IAuthorizationHandler, AdminApiAccessAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, AdminTotpAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, AdminReasonAuthorizationHandler>();
builder.Services.AddSingleton<AdminAuditStore>();
builder.Services.AddSingleton<AdminOperationKeyStore>();
builder.Services.AddOptions<PlayerPortalOptions>()
    .Bind(builder.Configuration.GetSection("PlayerPortal"))
    .Validate(
        static options => options.IsValid(out _),
        "Player portal cookie, challenge, session, cooldown, and rate-limit settings must be valid.")
    .Validate(
        options => options.IsValidForEnvironment(
            builder.Environment.IsDevelopment(),
            out _),
        "Production/public Steam portals require the official HTTPS OpenID provider; development fakes require explicit opt-in.")
    .Validate(
        options => !options.Enabled ||
            builder.Configuration.GetValue<bool>("ExtractionMode:Enabled"),
        "PlayerPortal cannot be enabled while ExtractionMode is disabled.")
    .Validate(
        options => !options.Enabled ||
            builder.Configuration.GetValue<bool>("ExtractionMode:Rcon:Enabled"),
        "PlayerPortal requires the loopback extraction RCON adapter.")
    .Validate(
        options => !options.Enabled ||
            (!string.IsNullOrWhiteSpace(builder.Configuration[
                "ExtractionMode:Rcon:ApprovedGameVersion"]) &&
             !string.IsNullOrWhiteSpace(builder.Configuration[
                 "ExtractionMode:Rcon:ApprovedPalDefenderVersion"])),
        "PlayerPortal requires non-empty approved game and PalDefender versions.")
    .Validate(
        options => !options.Enabled || options.CookieSecure ||
            builder.Configuration.GetValue<bool>("Security:DevelopmentMode"),
        "An enabled non-development PlayerPortal requires a Secure session cookie.")
    .ValidateOnStart();
builder.Services.AddHttpClient<ISteamOpenIdProviderClient, SteamOpenIdProviderClient>(
    (services, client) =>
    {
        var options = services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<PlayerPortalOptions>>()
            .Value.OpenId;
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.ProviderTimeoutSeconds, 1, 10));
    });
builder.Services.AddSingleton<PlayerPortalAuthenticationService>();
builder.Services.AddSingleton<SteamOpenIdAuthenticationService>();
builder.Services.AddSingleton<PlayerPortalSessionRegistry>();
builder.Services.AddSingleton<PlayerIdentitySecurityStore>();
builder.Services.AddSingleton<PlayerIdentitySecurityService>();
builder.Services.AddSingleton<PlayerPortalTrafficGuard>();
builder.Services.AddHostedService<ExtractionDeliveryWorker>();
builder.Services.AddHostedService<ExtractionSettlementRecoveryWorker>();
builder.Services.AddHostedService<ReliableTaskProjectionRecoveryWorker>();
builder.Services.Configure<SaveManagementOptions>(
    builder.Configuration.GetSection("SaveManagement"));
builder.Services.AddSingleton<SaveManagementService>();
builder.Services.AddSingleton<EconomyObservabilityService>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<EconomyObservabilityService>());
builder.Services.AddSingleton<ServerConfigurationService>();
builder.Services.AddSingleton<SaveCommandQueue>();
builder.Services.AddHostedService(services => services.GetRequiredService<SaveCommandQueue>());
builder.Services.Configure<LiveMapOptions>(builder.Configuration.GetSection("LiveMap"));
builder.Services.AddSingleton<LiveMapService>();
builder.Services.AddHostedService(services => services.GetRequiredService<LiveMapService>());
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var allowedOrigins = builder.Configuration
    .GetSection("Security:AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    if (startupSecurity.AllowNonLoopbackListenerBehindTrustedProxy)
    {
        foreach (var address in StartupSecurityValidator.ParseTrustedProxyAddresses(
                     startupSecurity))
        {
            options.KnownProxies.Add(address);
        }
    }
});

var app = builder.Build();
// Only loopback or an explicitly allow-listed reverse proxy may supply one
// forwarded hop. Public
// proxy routes must expose /api/v1/player only, never the extraction admin,
// PalDefender, save-management, official REST, RCON, or native bridge routes.
app.UseForwardedHeaders();
// All HTTP work carries a validated correlation ID before exception handling,
// security, audit, static-file, or application middleware. The scope omits
// request paths because route values may contain player IDs or one-time codes.
app.UseMiddleware<ControlPlaneCorrelationMiddleware>();
app.UseExceptionHandler();
// Release packages place the built operator console in wwwroot and the
// optional player portal in wwwroot/player. Development builds can omit the
// directory; the API continues to run normally when no static files exist.
app.UseDefaultFiles();
app.UseStaticFiles();
// Every API surface except the purpose-built player portal is operator-only.
// Enforce the documented loopback boundary in code as defense in depth: a
// reverse-proxy routing mistake must not expose wallet adjustments, settlement
// reconciliation, PalDefender or Native operations to the public network.
app.Use(async (context, next) =>
{
    if (IsOperatorOnlyApi(context.Request.Path) && !IsLoopback(context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ApiError(
            "OPERATOR_API_LOOPBACK_REQUIRED",
            "该管理 API 只允许从本机回环地址访问。"));
        return;
    }
    await next(context);
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AdminAuditMiddleware>();
// The player-portal traffic guard must run after trusted X-Forwarded-For has
// replaced the loopback proxy address, but before any public player endpoint.
app.UseMiddleware<PlayerPortalTrafficGuardMiddleware>();

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "ok",
    service = "pal-control-api",
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/health/instance", (
    IOptions<ExtractionPersistenceOptions> persistence,
    IOptions<StartupSecurityValidationOptions> startupSecurity,
    IWebHostEnvironment environment) =>
{
    using var process = Process.GetCurrentProcess();
    var dataDirectory = EconomyOperationsEndpoints.ResolveRuntimePath(
        persistence.Value.DataDirectory,
        environment.ContentRootPath);
    var logDirectory = EconomyOperationsEndpoints.ResolveRuntimePath(
        startupSecurity.Value.LogDirectory,
        environment.ContentRootPath);
    return Results.Ok(new
    {
        schemaVersion = 1,
        service = "pal-control-api",
        processId = Environment.ProcessId,
        processStartedAtUtc = process.StartTime.ToUniversalTime(),
        dataDirectoryFingerprint = EconomyOperationsEndpoints.RuntimePathFingerprint(dataDirectory),
        logDirectoryFingerprint = EconomyOperationsEndpoints.RuntimePathFingerprint(logDirectory)
    });
});

app.MapGet("/health/ready", async (
    BridgeState bridge,
    ExtractionCommerceService commerce,
    ExtractionModeCoordinator extraction,
    ExtractionOperationGate operationGate,
    PalDefenderCommandQueue deliveryCommands,
    CancellationToken cancellationToken) =>
{
    var capabilities = await bridge.GetCapabilitiesAsync("local", cancellationToken);
    if (!commerce.IsReady)
    {
        return Results.Json(new
        {
            status = "unavailable",
            readReady = false,
            officialRestConnected = capabilities.OfficialRestConnected,
            bridgeConnected = capabilities.BridgeConnected,
            economy = new
            {
                enabled = extraction.Enabled,
                gameplayMode = ExtractionModeCoordinator.GameplayMode,
                storeReady = false,
                deliveryQueueReady = deliveryCommands.IsReady,
                maintenance = operationGate.Current.Maintenance
            },
            time = DateTimeOffset.UtcNow
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Ok(new
    {
        status = capabilities.OfficialRestConnected ? "ready" : "degraded",
        readReady = true,
        mode = capabilities.Mode,
        officialRestConnected = capabilities.OfficialRestConnected,
        bridgeConnected = capabilities.BridgeConnected,
        economy = new
        {
            enabled = extraction.Enabled,
            gameplayMode = ExtractionModeCoordinator.GameplayMode,
            storeReady = true,
            deliveryQueueReady = deliveryCommands.IsReady,
            maintenance = operationGate.Current.Maintenance
        },
        time = DateTimeOffset.UtcNow
    });
});

var api = app.MapGroup("/api/v1")
    .RequireAuthorization(AdminPolicies.ApiAccess);
api.MapPalDefenderEndpoints();
api.MapExtractionModeEndpoints();
api.MapEconomyOperationsEndpoints();
api.MapPlayerPortalEndpoints().AllowAnonymous();
api.MapPlayerIdentitySecurityEndpoints();
api.MapEconomyContinuityEndpoints();
api.MapSeasonLeaderboardEndpoints();
api.MapPlayerNotificationEndpoints();
api.MapTeamEconomyEndpoints();
api.MapEconomyObservabilityEndpoints();
api.MapEconomyAnalyticsEndpoints();
api.MapNewPlayerActivityAdminEndpoints();
api.MapEconomyContentEndpoints();
api.MapReliableTaskEndpoints();
api.MapFederationEndpoints();

api.MapGet("/admin/session", (HttpContext context) => Results.Ok(new
{
    subject = AdminIdentity.RequireSubject(context),
    roles = AdminIdentity.Roles(context),
    authenticationMethod = context.User.FindFirst(
        "pal_control_authentication_method")?.Value
}));

api.MapGet("/admin/audit", async (
    int? limit,
    AdminAuditStore audit,
    CancellationToken cancellationToken) => Results.Ok(new
    {
        items = await audit.ListAsync(limit ?? 100, cancellationToken)
    }))
    .RequireAuthorization(AdminPolicies.Owner);

api.MapGet(
    "/servers/{serverId}/game-catalog",
    async (
        string serverId,
        HttpContext context,
        IConfiguration configuration,
        PalworldResourceCatalogService catalogService,
        CancellationToken cancellationToken) =>
    {
        var validation = ValidateServerId(serverId, configuration);
        if (validation is not null)
        {
            return validation;
        }
        try
        {
            var catalog = await catalogService.GetAsync(cancellationToken);
            var etag = $"\"{catalog.Revision}\"";
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "private, max-age=300";
            if (context.Request.Headers.IfNoneMatch.Any(value =>
                    string.Equals(value, etag, StringComparison.Ordinal)))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            return Results.Ok(catalog);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or InvalidDataException)
        {
            return Results.Json(
                new ApiError("GAME_CATALOG_UNAVAILABLE", exception.Message),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

api.MapGet("/servers/{serverId}/configuration", async (
    string serverId,
    IConfiguration configuration,
    ServerConfigurationService service,
    CancellationToken ct) =>
{
    var validation = ValidateServerId(serverId, configuration);
    if (validation is not null) return validation;
    try { return Results.Ok(await service.ReadAsync(ct)); }
    catch (FileNotFoundException ex) { return Results.NotFound(new ApiError("CONFIGURATION_NOT_FOUND", ex.Message)); }
    catch (InvalidDataException ex) { return Results.UnprocessableEntity(new ApiError("CONFIGURATION_INVALID", ex.Message)); }
});

api.MapPut("/servers/{serverId}/configuration", async (
    string serverId,
    ServerConfigurationUpdate request,
    IConfiguration configuration,
    ServerConfigurationService service,
    CancellationToken ct) =>
{
    var validation = ValidateServerId(serverId, configuration);
    if (validation is not null) return validation;
    try { return Results.Ok(await service.UpdateAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = [ex.Message] }); }
    catch (ConfigurationRevisionConflictException ex) { return Results.Conflict(new ApiError("CONFIGURATION_REVISION_CONFLICT", ex.Message)); }
    catch (FileNotFoundException ex) { return Results.NotFound(new ApiError("CONFIGURATION_NOT_FOUND", ex.Message)); }
    catch (InvalidDataException ex) { return Results.UnprocessableEntity(new ApiError("CONFIGURATION_INVALID", ex.Message)); }
});

api.MapGet(
    "/servers/{serverId}/capabilities",
    async (string serverId, BridgeState bridge, CancellationToken cancellationToken) =>
        Results.Ok(await bridge.GetCapabilitiesAsync(serverId, cancellationToken)));

api.MapGet(
    "/servers/{serverId}/info",
    async (string serverId, PalworldRestClient palworld, CancellationToken cancellationToken) =>
    {
        var info = await palworld.TryGetInfoAsync(cancellationToken);
        return info is null
            ? BridgeUnavailable(
                "PALWORLD_REST_UNAVAILABLE",
                $"Official REST is unavailable for server '{serverId}'.")
            : Results.Ok(info);
    });

api.MapGet(
    "/servers/{serverId}/players",
    async (string serverId, PalworldRestClient palworld, CancellationToken cancellationToken) =>
    {
        var players = await palworld.TryGetPlayersAsync(cancellationToken);
        return players is null
            ? BridgeUnavailable(
                "PLAYER_READ_UNAVAILABLE",
                $"Official REST player data is unavailable for server '{serverId}'.")
            : Results.Ok(new { items = players, nextCursor = (string?)null });
    });

api.MapGet(
    "/servers/{serverId}/live-map",
    (
        string serverId,
        HttpContext context,
        IConfiguration configuration,
        LiveMapService liveMap) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }

        var snapshot = liveMap.GetSnapshot();
        var etag = LiveMapEtag(snapshot.StreamId, snapshot.Sequence);
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";
        if (MatchesEtag(context.Request.Headers.IfNoneMatch.ToString(), etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Ok(snapshot);
    });

api.MapGet(
    "/servers/{serverId}/live-map/events",
    async (
        string serverId,
        HttpContext context,
        IConfiguration configuration,
        LiveMapService liveMap) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            await serverValidation.ExecuteAsync(context);
            return;
        }
        await StreamLiveMapAsync(context, liveMap, context.RequestAborted);
    });

api.MapGet(
    "/servers/{serverId}/metrics",
    async (string serverId, PalworldRestClient palworld, CancellationToken cancellationToken) =>
    {
        var metrics = await palworld.TryGetMetricsAsync(cancellationToken);
        return metrics is null
            ? BridgeUnavailable(
                "METRICS_UNAVAILABLE",
                $"Official REST metrics are unavailable for server '{serverId}'.")
             : Results.Ok(metrics);
    });

api.MapGet(
    "/servers/{serverId}/saves/status",
    async (
        string serverId,
        IConfiguration configuration,
        SaveManagementService saves,
        CancellationToken cancellationToken) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        return serverValidation ?? Results.Ok(await saves.GetStatusAsync(serverId, cancellationToken));
    });

api.MapPost(
    "/servers/{serverId}/saves/flush",
    async (
        string serverId,
        SaveOperationInput request,
        HttpRequest httpRequest,
        IConfiguration configuration,
        SaveCommandQueue commands,
        CancellationToken cancellationToken) =>
    {
        var validation = ValidateSaveWriteRequest(
            serverId,
            request.Reason,
            null,
            httpRequest,
            configuration);
        if (validation is not null)
        {
            return validation;
        }
        var result = await commands.EnqueueAsync(
            serverId,
            "flush",
            httpRequest.Headers["Idempotency-Key"].ToString(),
            request.Reason,
            GetActor(httpRequest),
            null,
            null,
            cancellationToken);
        return SaveCommandEnqueueResponse(result, httpRequest);
    });

api.MapGet(
    "/servers/{serverId}/backups",
    async (
        string serverId,
        string? kind,
        IConfiguration configuration,
        SaveManagementService saves,
        CancellationToken cancellationToken) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }
        try
        {
            return Results.Ok(new
            {
                items = await saves.ListBackupsAsync(serverId, kind, cancellationToken)
            });
        }
        catch (SaveManagementException exception)
        {
            return SaveManagementProblem(exception);
        }
    });

api.MapPost(
    "/servers/{serverId}/backups",
    async (
        string serverId,
        CreateBackupInput request,
        HttpRequest httpRequest,
        IConfiguration configuration,
        SaveCommandQueue commands,
        CancellationToken cancellationToken) =>
    {
        var validation = ValidateSaveWriteRequest(
            serverId,
            request.Reason,
            request.Label,
            httpRequest,
            configuration);
        if (validation is not null)
        {
            return validation;
        }
        if (string.IsNullOrWhiteSpace(request.Label))
        {
            return Results.BadRequest(new ApiError(
                "INVALID_BACKUP_LABEL",
                "Backup label must contain 1 to 80 non-control characters."));
        }
        var result = await commands.EnqueueAsync(
            serverId,
            "create-backup",
            httpRequest.Headers["Idempotency-Key"].ToString(),
            request.Reason,
            GetActor(httpRequest),
            request.Label,
            null,
            cancellationToken);
        return SaveCommandEnqueueResponse(result, httpRequest);
    });

api.MapGet(
    "/servers/{serverId}/backups/{backupId}",
    async (
        string serverId,
        string backupId,
        IConfiguration configuration,
        SaveManagementService saves,
        CancellationToken cancellationToken) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }
        try
        {
            var backup = await saves.GetBackupAsync(serverId, backupId, cancellationToken);
            return backup is null
                ? Results.NotFound(new ApiError(
                    "BACKUP_NOT_FOUND",
                    $"Backup '{backupId}' does not exist on server '{serverId}'."))
                : Results.Ok(backup);
        }
        catch (SaveManagementException exception)
        {
            return SaveManagementProblem(exception);
        }
    });

api.MapPost(
    "/servers/{serverId}/backups/{backupId}/verify",
    async (
        string serverId,
        string backupId,
        SaveOperationInput request,
        HttpRequest httpRequest,
        IConfiguration configuration,
        SaveManagementService saves,
        SaveCommandQueue commands,
        CancellationToken cancellationToken) =>
    {
        var validation = ValidateSaveWriteRequest(
            serverId,
            request.Reason,
            null,
            httpRequest,
            configuration);
        if (validation is not null)
        {
            return validation;
        }
        try
        {
            var backup = await saves.GetBackupAsync(serverId, backupId, cancellationToken);
            if (backup is null)
            {
                return Results.NotFound(new ApiError(
                    "BACKUP_NOT_FOUND",
                    $"Backup '{backupId}' does not exist on server '{serverId}'."));
            }
            if (!string.Equals(backup.Kind, "managed", StringComparison.Ordinal))
            {
                return Results.Json(
                    new ApiError(
                        "NATIVE_BACKUP_READ_ONLY",
                        "Native Palworld backups are read-only and do not have a managed SHA-256 manifest."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }
        catch (SaveManagementException exception)
        {
            return SaveManagementProblem(exception);
        }

        var result = await commands.EnqueueAsync(
            serverId,
            "verify-backup",
            httpRequest.Headers["Idempotency-Key"].ToString(),
            request.Reason,
            GetActor(httpRequest),
            null,
            backupId,
            cancellationToken);
        return SaveCommandEnqueueResponse(result, httpRequest);
    });

api.MapGet(
    "/servers/{serverId}/in-game-notifications/capabilities",
    async (
        string serverId,
        IConfiguration configuration,
        InGameNotificationCapabilityService capabilities,
        CancellationToken cancellationToken) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }

        var outcome = await capabilities.ProbeAsync(serverId, cancellationToken);
        if (outcome.Probe is null)
        {
            return BridgeUnavailable(
                outcome.Error?.Code ?? "NATIVE_NOTIFICATION_PRESET_PROBE_FAILED",
                outcome.Error?.Message ?? "Native in-game notification preset probe failed.");
        }
        return Results.Ok(outcome.Probe);
    });

api.MapGet(
    "/servers/{serverId}/in-game-notifications",
    async (
        string serverId,
        IConfiguration configuration,
        InGameNotificationStore notifications,
        CancellationToken cancellationToken) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        return serverValidation ?? Results.Ok(new
        {
            items = await notifications.ListAsync(serverId, cancellationToken)
        });
    });

api.MapPost(
    "/servers/{serverId}/in-game-notifications",
    async (
        string serverId,
        InGameNotificationInput request,
        HttpRequest httpRequest,
        IConfiguration configuration,
        InGameNotificationStore notifications,
        InGameNotificationCapabilityService capabilities,
        CancellationToken cancellationToken) =>
    {
        var idempotencyValidation = ValidateIdempotencyKey(httpRequest);
        if (idempotencyValidation is not null)
        {
            return idempotencyValidation;
        }
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }
        var inputValidation = InGameNotificationContract.ValidateShape(request);
        if (inputValidation is not null)
        {
            return Results.BadRequest(inputValidation);
        }

        var idempotencyKey = httpRequest.Headers["Idempotency-Key"].ToString();
        var existing = await notifications.FindExistingAsync(
            serverId,
            idempotencyKey,
            request,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.IdempotencyConflict)
            {
                return Results.Conflict(new ApiError(
                    "IDEMPOTENCY_KEY_REUSED",
                    "The Idempotency-Key was already used for a different in-game notification."));
            }
            return Results.Ok(existing.Notification);
        }

        var probeOutcome = await capabilities.ProbeAsync(serverId, cancellationToken);
        if (!probeOutcome.Ready || probeOutcome.Probe is null)
        {
            return BridgeUnavailable(
                probeOutcome.Error?.Code ?? "NATIVE_NOTIFICATION_PRESET_UNAVAILABLE",
                probeOutcome.Error?.Message ?? "Native server-native notification presets are unavailable.");
        }
        var capabilityValidation = InGameNotificationContract.ValidateAgainstProbe(
            request,
            probeOutcome.Probe);
        if (capabilityValidation is not null)
        {
            return Results.Json(capabilityValidation, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var result = await notifications.CreateAsync(
            serverId,
            idempotencyKey,
            request,
            GetActor(httpRequest),
            cancellationToken);
        if (result.IdempotencyConflict)
        {
            return Results.Conflict(new ApiError(
                "IDEMPOTENCY_KEY_REUSED",
                "The Idempotency-Key was already used for a different in-game notification."));
        }

        var location = $"/api/v1/servers/{serverId}/in-game-notifications/{result.Notification!.NotificationId}";
        httpRequest.HttpContext.Response.Headers.Location = location;
        return result.Created
            ? Results.Created(location, result.Notification)
            : Results.Ok(result.Notification);
    });

api.MapGet(
    "/servers/{serverId}/in-game-notifications/{notificationId:guid}",
    async (
        string serverId,
        Guid notificationId,
        IConfiguration configuration,
        InGameNotificationStore notifications,
        CancellationToken cancellationToken) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }
        var notification = await notifications.GetAsync(serverId, notificationId, cancellationToken);
        return notification is null
            ? Results.NotFound(new ApiError(
                "IN_GAME_NOTIFICATION_NOT_FOUND",
                $"In-game notification '{notificationId}' does not exist on server '{serverId}'."))
            : Results.Ok(notification);
    });

api.MapPost(
    "/servers/{serverId}/in-game-notifications/{notificationId:guid}/dispatch",
    async (
        string serverId,
        Guid notificationId,
        HttpRequest httpRequest,
        IConfiguration configuration,
        InGameNotificationStore notifications,
        InGameNotificationCommandQueue commands,
        InGameNotificationCapabilityService capabilities,
        CancellationToken cancellationToken) =>
    {
        var idempotencyValidation = ValidateIdempotencyKey(httpRequest);
        if (idempotencyValidation is not null)
        {
            return idempotencyValidation;
        }
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }
        var notification = await notifications.GetAsync(serverId, notificationId, cancellationToken);
        if (notification is null)
        {
            return Results.NotFound(new ApiError(
                "IN_GAME_NOTIFICATION_NOT_FOUND",
                $"In-game notification '{notificationId}' does not exist on server '{serverId}'."));
        }

        var idempotencyKey = httpRequest.Headers["Idempotency-Key"].ToString();
        var existing = await commands.FindExistingAsync(
            serverId,
            notification,
            idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.IdempotencyConflict)
            {
                return Results.Conflict(new ApiError(
                    "IDEMPOTENCY_KEY_REUSED",
                    "The Idempotency-Key was already used for a different in-game notification dispatch."));
            }
            httpRequest.HttpContext.Response.Headers.Location = existing.Command!.StatusUrl;
            return Results.Json(
                existing.Command,
                statusCode: existing.Command.State is "accepted" or "dispatched"
                    ? StatusCodes.Status202Accepted
                    : StatusCodes.Status200OK);
        }
        if (notification.State is "sent" or "uncertain" or "expired" or "cancelled")
        {
            return Results.Conflict(new ApiError(
                "IN_GAME_NOTIFICATION_NOT_DISPATCHABLE",
                $"In-game notification '{notificationId}' is in terminal state '{notification.State}'."));
        }
        if (notification.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            await notifications.SetStateAsync(
                notificationId,
                "expired",
                GetActor(httpRequest),
                cancellationToken);
            return Results.Conflict(new ApiError(
                "NOTIFICATION_EXPIRED",
                "This in-game notification expired before it could be queued."));
        }

        var probeOutcome = await capabilities.ProbeAsync(serverId, cancellationToken);
        if (!probeOutcome.Ready || probeOutcome.Probe is null)
        {
            return BridgeUnavailable(
                probeOutcome.Error?.Code ?? "NATIVE_NOTIFICATION_PRESET_UNAVAILABLE",
                probeOutcome.Error?.Message ?? "Native server-native notification presets are unavailable.");
        }
        var capabilityValidation = InGameNotificationContract.ValidateAgainstProbe(
            InGameNotificationContract.ToInput(notification),
            probeOutcome.Probe);
        if (capabilityValidation is not null)
        {
            return Results.Json(capabilityValidation, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var result = await commands.EnqueueAsync(
            serverId,
            notification,
            idempotencyKey,
            GetActor(httpRequest),
            cancellationToken);
        if (result.IdempotencyConflict)
        {
            return Results.Conflict(new ApiError(
                "IDEMPOTENCY_KEY_REUSED",
                "The Idempotency-Key was already used for a different in-game notification dispatch."));
        }
        if (result.NotificationConflict)
        {
            return Results.Conflict(new ApiError(
                "IN_GAME_NOTIFICATION_ALREADY_QUEUED",
                $"In-game notification '{notificationId}' already has command '{result.Command!.CommandId}' in state '{result.Command.State}'."));
        }
        if (notification.DisplayAt > DateTimeOffset.UtcNow)
        {
            await notifications.SetStateAsync(
                notificationId,
                "scheduled",
                GetActor(httpRequest),
                cancellationToken);
        }

        httpRequest.HttpContext.Response.Headers.Location = result.Command!.StatusUrl;
        httpRequest.HttpContext.Response.Headers.RetryAfter = "1";
        return Results.Json(
            result.Command,
            statusCode: result.Created || result.Command.State is "accepted" or "dispatched"
                ? StatusCodes.Status202Accepted
                : StatusCodes.Status200OK);
    });

api.MapGet(
    "/servers/{serverId}/announcements",
    async (
        string serverId,
        AnnouncementStore announcements,
        CancellationToken cancellationToken) =>
        Results.Ok(await announcements.ListAsync(serverId, cancellationToken)));

api.MapGet(
    "/servers/{serverId}/announcements/client-overlay/probe",
    async (
        string serverId,
        IConfiguration configuration,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }

        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected ||
            !snapshot.Capabilities.Contains("announcements.overlay.probe"))
        {
            return BridgeUnavailable(
                "NATIVE_CLIENT_OVERLAY_PROBE_UNAVAILABLE",
                $"Native client-overlay probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "announcements.overlay.probe",
                new { },
                "read-only client-overlay compatibility probe",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_CLIENT_OVERLAY_PROBE_FAILED",
                    result.Error?.Message ?? "Native client-overlay probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok();
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_CLIENT_OVERLAY_PROBE_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native client-overlay probe timed out."
                    : "Native client-overlay probe connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/announcements/top-banner/probe",
    async (
        string serverId,
        IConfiguration configuration,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }

        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected ||
            !snapshot.Capabilities.Contains("announcements.banner.probe"))
        {
            return BridgeUnavailable(
                "NATIVE_TOP_BANNER_PROBE_UNAVAILABLE",
                $"Native top-banner probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "announcements.banner.probe",
                new { },
                "read-only top-banner compatibility probe",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_TOP_BANNER_PROBE_FAILED",
                    result.Error?.Message ?? "Native top-banner probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok();
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_TOP_BANNER_PROBE_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native top-banner probe timed out."
                    : "Native top-banner probe connection ended.");
        }
    });

api.MapPost(
    "/servers/{serverId}/announcements",
    async (
        string serverId,
        AnnouncementInput request,
        HttpRequest httpRequest,
        AnnouncementStore announcements,
        CancellationToken cancellationToken) =>
    {
        var idempotencyValidation = ValidateIdempotencyKey(httpRequest);
        if (idempotencyValidation is not null)
        {
            return idempotencyValidation;
        }
        var inputValidation = ValidateAnnouncementInput(request);
        if (inputValidation is not null)
        {
            return inputValidation;
        }

        var result = await announcements.CreateAsync(
            serverId,
            httpRequest.Headers["Idempotency-Key"].ToString(),
            request,
            GetActor(httpRequest),
            cancellationToken);
        if (result.IdempotencyConflict)
        {
            return Results.Conflict(new ApiError(
                "IDEMPOTENCY_KEY_REUSED",
                "The Idempotency-Key was already used with a different announcement payload."));
        }

        return result.Created
            ? Results.Created(
                $"/api/v1/servers/{serverId}/announcements/{result.Announcement!.AnnouncementId}",
                result.Announcement)
            : Results.Ok(result.Announcement);
    });

api.MapGet(
    "/servers/{serverId}/announcements/{announcementId:guid}",
    async (
        string serverId,
        Guid announcementId,
        AnnouncementStore announcements,
        CancellationToken cancellationToken) =>
    {
        var announcement = await announcements.GetAsync(
            serverId,
            announcementId,
            cancellationToken);
        return announcement is null
            ? Results.NotFound(new ApiError(
                "ANNOUNCEMENT_NOT_FOUND",
                $"Announcement '{announcementId}' does not exist on server '{serverId}'."))
            : Results.Ok(announcement);
    });

api.MapPost(
    "/servers/{serverId}/announcements/{announcementId:guid}/publish",
    async (
        string serverId,
        Guid announcementId,
        HttpRequest httpRequest,
        AnnouncementStore announcements,
        AnnouncementCommandQueue commands,
        BridgeState bridge,
        IConfiguration configuration,
        CancellationToken cancellationToken) =>
    {
        var idempotencyValidation = ValidateIdempotencyKey(httpRequest);
        if (idempotencyValidation is not null)
        {
            return idempotencyValidation;
        }
        var serverValidation = ValidateServerId(serverId, configuration);
        if (serverValidation is not null)
        {
            return serverValidation;
        }

        var announcement = await announcements.GetAsync(serverId, announcementId, cancellationToken);
        if (announcement is null)
        {
            return Results.NotFound(new ApiError(
                "ANNOUNCEMENT_NOT_FOUND",
                $"Announcement '{announcementId}' does not exist on server '{serverId}'."));
        }

        var existing = await commands.FindExistingAsync(
            serverId,
            announcement,
            httpRequest.Headers["Idempotency-Key"].ToString(),
            cancellationToken);
        if (existing is not null)
        {
            if (existing.IdempotencyConflict)
            {
                return Results.Conflict(new ApiError(
                    "IDEMPOTENCY_KEY_REUSED",
                    "The Idempotency-Key was already used for a different publish command."));
            }
            httpRequest.HttpContext.Response.Headers.Location = existing.Command!.StatusUrl;
            var existingStatus = existing.Command.State is "accepted" or "dispatched"
                ? StatusCodes.Status202Accepted
                : StatusCodes.Status200OK;
            return Results.Json(existing.Command, statusCode: existingStatus);
        }
        if (string.Equals(announcement.State, "published", StringComparison.Ordinal))
        {
            return Results.Conflict(new ApiError(
                "ANNOUNCEMENT_ALREADY_PUBLISHED",
                "This announcement has already been published."));
        }
        if (announcement.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            await announcements.SetStateAsync(
                announcementId,
                "expired",
                GetActor(httpRequest),
                cancellationToken);
            return Results.Conflict(new ApiError(
                "ANNOUNCEMENT_EXPIRED",
                "This announcement expired before it could be published."));
        }

        var capabilities = await bridge.GetCapabilitiesAsync(serverId, cancellationToken);
        var missingChannels = announcement.Channels
            .Where(channel => channel switch
            {
                "chat" => !capabilities.PublishChatAnnouncements,
                "client-overlay" => !capabilities.PublishClientOverlay,
                "top-banner" => !capabilities.PublishTopBanner,
                _ => true
            })
            .ToArray();
        if (missingChannels.Length > 0)
        {
            return BridgeUnavailable(
                "ANNOUNCEMENT_PUBLISH_UNAVAILABLE",
                $"The following announcement channels are unavailable: {string.Join(", ", missingChannels)}.");
        }

        var result = await commands.EnqueueAsync(
            serverId,
            announcement,
            httpRequest.Headers["Idempotency-Key"].ToString(),
            GetActor(httpRequest),
            cancellationToken);
        if (result.IdempotencyConflict)
        {
            return Results.Conflict(new ApiError(
                "IDEMPOTENCY_KEY_REUSED",
                "The Idempotency-Key was already used for a different publish command."));
        }
        if (result.AnnouncementConflict)
        {
            return Results.Conflict(new ApiError(
                "ANNOUNCEMENT_ALREADY_QUEUED",
                $"Announcement '{announcementId}' already has command '{result.Command!.CommandId}' in state '{result.Command.State}'."));
        }

        if (announcement.PublishAt > DateTimeOffset.UtcNow)
        {
            await announcements.SetStateAsync(
                announcementId,
                "scheduled",
                GetActor(httpRequest),
                cancellationToken);
        }

        httpRequest.HttpContext.Response.Headers.Location = result.Command!.StatusUrl;
        httpRequest.HttpContext.Response.Headers.RetryAfter = "1";
        var statusCode = result.Created || result.Command.State is "accepted" or "dispatched"
            ? StatusCodes.Status202Accepted
            : StatusCodes.Status200OK;
        return Results.Json(result.Command, statusCode: statusCode);
    });

api.MapGet(
    "/commands/{commandId:guid}",
    async (
        Guid commandId,
        AnnouncementCommandQueue commands,
        CancellationToken cancellationToken) =>
    {
        var command = await commands.GetStatusAsync(commandId, cancellationToken);
        return command is null
            ? Results.NotFound(new ApiError(
                "COMMAND_NOT_FOUND",
                $"Command '{commandId}' does not exist."))
             : Results.Ok(command);
    });

api.MapGet(
    "/in-game-notification-commands/{commandId:guid}",
    async (
        Guid commandId,
        InGameNotificationCommandQueue commands,
        CancellationToken cancellationToken) =>
    {
        var command = await commands.GetStatusAsync(commandId, cancellationToken);
        return command is null
            ? Results.NotFound(new ApiError(
                "IN_GAME_NOTIFICATION_COMMAND_NOT_FOUND",
                $"In-game notification command '{commandId}' does not exist."))
            : Results.Ok(command);
    });

api.MapGet(
    "/save-commands/{commandId:guid}",
    async (
        Guid commandId,
        SaveCommandQueue commands,
        CancellationToken cancellationToken) =>
    {
        var command = await commands.GetStatusAsync(commandId, cancellationToken);
        return command is null
            ? Results.NotFound(new ApiError(
                "SAVE_COMMAND_NOT_FOUND",
                $"Save command '{commandId}' does not exist."))
            : Results.Ok(command);
    });

api.MapGet(
    "/audit/commands",
    async (
        int? limit,
        AnnouncementCommandQueue commands,
        CancellationToken cancellationToken) =>
        Results.Ok(new
        {
             items = await commands.GetAuditAsync(limit ?? 100, cancellationToken)
         }));

api.MapGet(
    "/audit/in-game-notification-commands",
    async (
        int? limit,
        InGameNotificationCommandQueue commands,
        CancellationToken cancellationToken) =>
        Results.Ok(new
        {
            items = await commands.GetAuditAsync(limit ?? 100, cancellationToken)
        }));

api.MapGet(
    "/audit/save-commands",
    async (
        int? limit,
        SaveCommandQueue commands,
        CancellationToken cancellationToken) =>
        Results.Ok(new
        {
            items = await commands.GetAuditAsync(limit ?? 100, cancellationToken)
        }));

api.MapGet(
    "/servers/{serverId}/bridge/status",
    (string serverId, NativeBridgeState bridge) =>
    {
        var snapshot = bridge.GetSnapshot();
        return Results.Ok(new
        {
            serverId,
            snapshot.Connected,
            snapshot.ProtocolVersion,
            snapshot.GameBuild,
            snapshot.SteamBuild,
            snapshot.ModVersion,
            snapshot.RuntimeExecutableSha256,
            snapshot.RuntimeExecutableSize,
            snapshot.RuntimeNativeDllSha256,
            snapshot.RuntimeNativeDllSize,
            snapshot.RuntimeUe4ssDllSha256,
            snapshot.RuntimeUe4ssDllSize,
            snapshot.RuntimeIdentityVerified,
            snapshot.WriteEnabled,
            snapshot.Capabilities,
            snapshot.Probes,
            snapshot.LastSeenAt,
            snapshot.LastError
        });
    });

api.MapGet(
    "/servers/{serverId}/players/native-probe",
    async (
        string serverId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("players.probe"))
        {
            return BridgeUnavailable(
                "NATIVE_PLAYER_PROBE_UNAVAILABLE",
                $"Native player probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "players.probe",
                new { },
                "read-only native player object probe",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_PLAYER_PROBE_FAILED",
                    result.Error?.Message ?? "Native player probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new { objectCount = 0, objects = Array.Empty<object>() });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_PLAYER_PROBE_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native player probe timed out."
                    : "Native player probe connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/players/native-schema",
    async (
        string serverId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("players.schema"))
        {
            return BridgeUnavailable(
                "NATIVE_PLAYER_SCHEMA_UNAVAILABLE",
                $"Native player schema probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "players.schema",
                new { },
                "read-only PalPlayerState reflection schema",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_PLAYER_SCHEMA_FAILED",
                    result.Error?.Message ?? "Native player schema probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new { propertyCount = 0, properties = Array.Empty<object>() });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_PLAYER_SCHEMA_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native player schema probe timed out."
                    : "Native player schema connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/players/progression/native-schema",
    async (
        string serverId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected ||
            !snapshot.Capabilities.Contains("players.progression.schema"))
        {
            return BridgeUnavailable(
                "NATIVE_PLAYER_PROGRESSION_SCHEMA_UNAVAILABLE",
                $"Native player progression schema probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "players.progression.schema",
                new { },
                "read-only player progression reflection schema",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_PLAYER_PROGRESSION_SCHEMA_FAILED",
                    result.Error?.Message ?? "Native player progression schema probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new { foundTypeCount = 0, types = Array.Empty<object>() });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_PLAYER_PROGRESSION_SCHEMA_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native player progression schema probe timed out."
                    : "Native player progression schema connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/players/progression/native-probe",
    async (
        string serverId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected ||
            !snapshot.Capabilities.Contains("players.progression.probe"))
        {
            return BridgeUnavailable(
                "NATIVE_PLAYER_PROGRESSION_PROBE_UNAVAILABLE",
                $"Native player progression probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "players.progression.probe",
                new { },
                "read-only loaded player progression snapshot",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_PLAYER_PROGRESSION_PROBE_FAILED",
                    result.Error?.Message ?? "Native player progression probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new { mappingReady = false, players = Array.Empty<object>() });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_PLAYER_PROGRESSION_PROBE_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native player progression probe timed out."
                    : "Native player progression probe connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/players/{playerId}/progression",
    async (
        string serverId,
        string playerId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var normalizedPlayerId = NormalizeIdentifier(playerId);
        if (normalizedPlayerId.Length < 8)
        {
            return Results.BadRequest(new ApiError(
                "INVALID_PLAYER_ID",
                "Player ID must contain at least eight hexadecimal characters."));
        }
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected ||
            !snapshot.Capabilities.Contains("players.progression.read"))
        {
            return BridgeUnavailable(
                "PLAYER_PROGRESSION_READ_UNAVAILABLE",
                $"Player progression is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "players.progression.probe",
                new { },
                "read loaded player progression snapshot",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal) ||
                result.Data is not { } data ||
                !data.TryGetProperty("mappingReady", out var mappingReady) ||
                mappingReady.ValueKind != JsonValueKind.True)
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "PLAYER_PROGRESSION_MAPPING_NOT_READY",
                    result.Error?.Message ?? "Native player progression mapping is not ready.");
            }
            foreach (var player in data.GetProperty("players").EnumerateArray())
            {
                var ownerUid = player.GetProperty("playerUId").GetString();
                if (ownerUid is null)
                {
                    continue;
                }
                var normalizedOwner = NormalizeIdentifier(ownerUid);
                if (string.Equals(normalizedOwner, normalizedPlayerId, StringComparison.Ordinal) ||
                    (normalizedPlayerId.Length == 8 &&
                     normalizedOwner.StartsWith(normalizedPlayerId, StringComparison.Ordinal)))
                {
                    return Results.Ok(player);
                }
            }
            return Results.NotFound(new ApiError(
                "PLAYER_PROGRESSION_NOT_LOADED",
                $"No loaded player progression matches '{playerId}'."));
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "PLAYER_PROGRESSION_READ_UNAVAILABLE",
                exception is TimeoutException
                    ? "Player progression read timed out."
                    : "Player progression connection ended.");
        }
    });

api.MapPost(
    "/servers/{serverId}/players/{playerId}/progression/mutations",
    async (
        string serverId,
        string playerId,
        PlayerProgressionMutationRequest request,
        HttpRequest httpRequest,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        if (!long.TryParse(
                request.ExpectedRevision,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expectedRevision) || expectedRevision <= 0)
        {
            return Results.BadRequest(new ApiError(
                "INVALID_EXPECTED_REVISION",
                "expectedRevision must be a positive native revision string."));
        }
        var validation = ValidateMutationRequest(
            request.Reason, expectedRevision, httpRequest);
        if (validation is not null)
        {
            return validation;
        }
        if (request.Patch is null)
        {
            return Results.BadRequest(new ApiError(
                "PLAYER_PROGRESSION_PATCH_REQUIRED",
                "A player progression patch is required."));
        }
        var allocationComplete =
            (request.Patch.AllocateStatusId is not null) ==
            request.Patch.AllocateStatusPoints.HasValue;
        var operationCount =
            (request.Patch.AddExperience.HasValue ? 1 : 0) +
            (request.Patch.TargetLevel.HasValue ? 1 : 0) +
            (request.Patch.GrantStatusPoints.HasValue ? 1 : 0) +
            (request.Patch.GrantTechnologyPoints.HasValue ? 1 : 0) +
            (request.Patch.GrantAncientTechnologyPoints.HasValue ? 1 : 0) +
            (allocationComplete && request.Patch.AllocateStatusId is not null ? 1 : 0);
        if (!allocationComplete || operationCount != 1)
        {
            return Results.BadRequest(new ApiError(
                "SINGLE_PLAYER_PROGRESSION_OPERATION_REQUIRED",
                "Exactly one complete player progression operation is allowed per request."));
        }
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected ||
            !snapshot.Capabilities.Contains("players.progression.write"))
        {
            return BridgeUnavailable(
                "PLAYER_PROGRESSION_WRITE_UNAVAILABLE",
                $"Player progression writes are disabled for '{playerId}'.");
        }

        try
        {
            (long Experience, int Level)? experienceBaseline = null;
            if (!request.DryRun && request.Patch.AddExperience is not null)
            {
                var baselineResult = await bridge.SendCommandAsync(
                    serverId,
                    "players.progression.probe",
                    new { },
                    "Capture the guarded experience mutation baseline.",
                    cancellationToken);
                if (string.Equals(
                        baselineResult.State,
                        "succeeded",
                        StringComparison.Ordinal) &&
                    FindPlayerProgression(
                        baselineResult.Data,
                        playerId) is { } baseline &&
                    string.Equals(
                        baseline.Revision,
                        request.ExpectedRevision,
                        StringComparison.Ordinal))
                {
                    experienceBaseline = (baseline.Experience, baseline.Level);
                }
            }

            var result = await bridge.SendCommandAsync(
                serverId,
                "players.progression.mutate",
                new
                {
                    ownerPlayerId = playerId,
                    addExperience = request.Patch.AddExperience,
                    targetLevel = request.Patch.TargetLevel,
                    grantStatusPoints = request.Patch.GrantStatusPoints,
                    grantTechnologyPoints = request.Patch.GrantTechnologyPoints,
                    grantAncientTechnologyPoints =
                        request.Patch.GrantAncientTechnologyPoints,
                    allocateStatusId = request.Patch.AllocateStatusId,
                    allocateStatusPoints = request.Patch.AllocateStatusPoints,
                    dryRun = request.DryRun
                },
                request.Reason.Trim(),
                cancellationToken,
                expectedRevision,
                idempotencyKey: httpRequest.Headers["Idempotency-Key"].ToString());
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                if (string.Equals(result.State, "uncertain", StringComparison.Ordinal) &&
                    request.Patch.AddExperience is { } addedExperience &&
                    experienceBaseline is { } baseline)
                {
                    var expectedExperience = checked(
                        baseline.Experience + addedExperience);
                    for (var attempt = 0; attempt < 12; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                        var verification = await bridge.SendCommandAsync(
                            serverId,
                            "players.progression.probe",
                            new { },
                            "Verify deferred native experience settlement.",
                            cancellationToken);
                        if (!string.Equals(
                                verification.State,
                                "succeeded",
                                StringComparison.Ordinal) ||
                            FindPlayerProgression(
                                verification.Data,
                                playerId) is not { } current)
                        {
                            continue;
                        }
                        if (current.Experience >= expectedExperience &&
                            current.Level >= baseline.Level)
                        {
                            return Results.Ok(new
                            {
                                dryRun = false,
                                applied = true,
                                operation = "addExperience",
                                nativeFunction = "PalCheatManager.AddPlayerExp",
                                value = addedExperience,
                                readBackVerified = true,
                                delayedReadBack = true,
                                revision = current.Revision,
                                after = new
                                {
                                    level = current.Level,
                                    totalExperience = current.Experience,
                                    unusedStatusPoints = current.UnusedStatusPoints,
                                    technologyPoints = current.TechnologyPoints,
                                    ancientTechnologyPoints =
                                        current.AncientTechnologyPoints
                                }
                            });
                        }
                    }
                }

                var error = new ApiError(
                    result.Error?.Code ?? "PLAYER_PROGRESSION_MUTATION_FAILED",
                    result.Error?.Message ?? "Native player progression mutation failed.");
                if (string.Equals(
                        result.Error?.Code,
                        "PLAYER_PROGRESSION_REVISION_CONFLICT",
                        StringComparison.Ordinal))
                {
                    return Results.Conflict(error);
                }
                return string.Equals(result.State, "uncertain", StringComparison.Ordinal)
                    ? Results.Json(error, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Json(error, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new { request.DryRun, applied = !request.DryRun });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "PLAYER_PROGRESSION_WRITE_UNAVAILABLE",
                exception is TimeoutException
                    ? "Player progression mutation timed out; refresh before retrying."
                    : "Player progression mutation connection ended; refresh before retrying.");
        }
    });

api.MapGet(
    "/servers/{serverId}/pals/native-schema",
    async (
        string serverId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("pals.schema"))
        {
            return BridgeUnavailable(
                "NATIVE_PAL_SCHEMA_UNAVAILABLE",
                $"Native Pal schema probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "pals.schema",
                new { },
                "read-only PalBox reflection schema",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_PAL_SCHEMA_FAILED",
                    result.Error?.Message ?? "Native Pal schema probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new { foundTypeCount = 0, types = Array.Empty<object>() });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_PAL_SCHEMA_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native Pal schema probe timed out."
                    : "Native Pal schema connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/pals/native-probe",
    async (
        string serverId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("pals.probe"))
        {
            return BridgeUnavailable(
                "NATIVE_PAL_PROBE_UNAVAILABLE",
                $"Native Pal probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "pals.probe",
                new { },
                "read-only loaded Pal snapshot",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_PAL_PROBE_FAILED",
                    result.Error?.Message ?? "Native Pal probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(PalPayloadForWeb(data))
                : Results.Ok(new { mappingReady = false, pals = Array.Empty<object>() });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_PAL_PROBE_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native Pal probe timed out."
                    : "Native Pal probe connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/pals/skill-catalog",
    async (
        string serverId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("pals.skills.catalog"))
        {
            return BridgeUnavailable(
                "PAL_SKILL_CATALOG_UNAVAILABLE",
                $"Native Pal skill catalog is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "pals.skills.catalog",
                new { },
                "read-only Pal active and passive skill catalog",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "PAL_SKILL_CATALOG_FAILED",
                    result.Error?.Message ?? "Native Pal skill catalog failed.");
            }
            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new
                {
                    activeSkills = Array.Empty<object>(),
                    passiveSkills = Array.Empty<object>()
                });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "PAL_SKILL_CATALOG_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native Pal skill catalog timed out."
                    : "Native Pal skill catalog connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/inventory/native-schema",
    async (
        string serverId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("inventory.schema"))
        {
            return BridgeUnavailable(
                "NATIVE_INVENTORY_SCHEMA_UNAVAILABLE",
                $"Native inventory schema probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "inventory.schema",
                new { },
                "read-only inventory reflection schema",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_INVENTORY_SCHEMA_FAILED",
                    result.Error?.Message ?? "Native inventory schema probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new { typeCount = 0, types = Array.Empty<object>() });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_INVENTORY_SCHEMA_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native inventory schema probe timed out."
                    : "Native inventory schema connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/inventory/native-probe",
    async (
        string serverId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("inventory.probe"))
        {
            return BridgeUnavailable(
                "NATIVE_INVENTORY_PROBE_UNAVAILABLE",
                $"Native inventory probe is unavailable for server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "inventory.probe",
                new { },
                "read-only player inventory container probe",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "NATIVE_INVENTORY_PROBE_FAILED",
                    result.Error?.Message ?? "Native inventory probe failed.");
            }

            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new { mappingReady = false, inventories = Array.Empty<object>() });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "NATIVE_INVENTORY_PROBE_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native inventory probe timed out."
                    : "Native inventory probe connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/players/{playerId}/inventory",
    async (
        string serverId,
        string playerId,
        HttpResponse response,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var normalizedPlayerId = NormalizeIdentifier(playerId);
        if (normalizedPlayerId.Length < 8)
        {
            return Results.BadRequest(new ApiError(
                "INVALID_PLAYER_ID",
                "playerId must contain at least 8 hexadecimal identifier characters."));
        }

        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("inventory.read"))
        {
            return BridgeUnavailable(
                "INVENTORY_READ_UNAVAILABLE",
                $"Inventory reads are unavailable for player '{playerId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "inventory.probe",
                new { },
                "read-only player inventory snapshot",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal) ||
                result.Data is not { } data ||
                !data.TryGetProperty("mappingReady", out var mappingReady) ||
                !mappingReady.GetBoolean())
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "INVENTORY_READ_FAILED",
                    result.Error?.Message ?? "Native inventory mapping is not ready.");
            }

            JsonElement selectedInventory = default;
            var found = false;
            foreach (var inventory in data.GetProperty("inventories").EnumerateArray())
            {
                var ownerPlayerUId = inventory.GetProperty("ownerPlayerUId").GetString();
                if (ownerPlayerUId is null)
                {
                    continue;
                }

                var normalizedOwner = NormalizeIdentifier(ownerPlayerUId);
                if (string.Equals(normalizedOwner, normalizedPlayerId, StringComparison.Ordinal) ||
                    (normalizedPlayerId.Length == 8 &&
                     normalizedOwner.StartsWith(normalizedPlayerId, StringComparison.Ordinal)))
                {
                    selectedInventory = inventory;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return Results.NotFound(new ApiError(
                    "PLAYER_INVENTORY_NOT_FOUND",
                    $"No loaded inventory matches player '{playerId}'."));
            }

            var containers = new List<InventoryContainerSnapshot>();
            foreach (var container in selectedInventory.GetProperty("containers").EnumerateArray())
            {
                if (!container.GetProperty("resolved").GetBoolean())
                {
                    continue;
                }

                var containerId = container.GetProperty("containerId").GetString();
                var kind = container.GetProperty("kind").GetString();
                if (containerId is null || kind is null)
                {
                    continue;
                }

                var slots = new List<InventorySlotSnapshot>();
                foreach (var slot in container.GetProperty("slots").EnumerateArray())
                {
                    if (slot.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }
                    var itemId = slot.GetProperty("staticItemId").GetString() ?? "None";
                    var quantity = slot.GetProperty("stackCount").GetInt32();
                    if (quantity <= 0 || string.Equals(itemId, "None", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var slotIndex = slot.GetProperty("slotIndex").GetInt32();
                    slots.Add(new InventorySlotSnapshot(
                        SlotId: $"{kind}:{slotIndex}",
                        ItemId: itemId,
                        Quantity: quantity,
                        Durability: null));
                }

                containers.Add(new InventoryContainerSnapshot(
                    ContainerId: containerId,
                    Kind: kind,
                    Slots: slots));
            }

            var ownerUid = selectedInventory.GetProperty("ownerPlayerUId").GetString()!;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(selectedInventory.GetRawText()));
            var revision = BinaryPrimitives.ReadInt64LittleEndian(hash) & long.MaxValue;
            response.Headers.ETag = $"\"{revision}\"";
            return Results.Ok(new InventorySnapshot(
                Revision: revision,
                PlayerSessionId: ownerUid,
                Containers: containers));
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "INVENTORY_READ_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native inventory read timed out."
                    : "Native inventory connection ended.");
        }
    });

api.MapGet(
    "/servers/{serverId}/players/{playerId}/pals",
    async (
        string serverId,
        string playerId,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var normalizedPlayerId = NormalizeIdentifier(playerId);
        if (normalizedPlayerId.Length < 8)
        {
            return Results.BadRequest(new ApiError(
                "INVALID_PLAYER_ID",
                "playerId must contain at least 8 identifier characters."));
        }

        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("pals.read"))
        {
            return BridgeUnavailable(
                "PAL_READ_UNAVAILABLE",
                $"Pal reads are unavailable for player '{playerId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "pals.probe",
                new { },
                "read-only player Pal snapshot",
                cancellationToken);
            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal) ||
                result.Data is not { } data ||
                !data.TryGetProperty("mappingReady", out var mappingReady) ||
                !mappingReady.GetBoolean())
            {
                return BridgeUnavailable(
                    result.Error?.Code ?? "PAL_READ_FAILED",
                    result.Error?.Message ?? "Native Pal mapping is not ready.");
            }

            var items = new List<JsonNode>();
            foreach (var pal in data.GetProperty("pals").EnumerateArray())
            {
                var ownerPlayerUId = pal.GetProperty("ownerPlayerUId").GetString();
                if (ownerPlayerUId is null)
                {
                    continue;
                }
                var normalizedOwner = NormalizeIdentifier(ownerPlayerUId);
                if (string.Equals(normalizedOwner, normalizedPlayerId, StringComparison.Ordinal) ||
                    (normalizedPlayerId.Length == 8 &&
                     normalizedOwner.StartsWith(normalizedPlayerId, StringComparison.Ordinal)))
                {
                    items.Add(PalPayloadForWeb(pal));
                }
            }

            return Results.Ok(new
            {
                items,
                nextCursor = (string?)null,
                observedAt = data.GetProperty("observedAt").GetString()
            });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "PAL_READ_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native Pal read timed out."
                    : "Native Pal connection ended.");
        }
    });

api.MapPost(
    "/servers/{serverId}/players/{playerId}/inventory/transactions",
    async (
        string serverId,
        string playerId,
        InventoryTransactionRequest request,
        HttpRequest httpRequest,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        var validation = ValidateMutationRequest(request.Reason, request.ExpectedRevision, httpRequest);
        if (validation is not null)
        {
            return validation;
        }
        if (request.Operations is null || request.Operations.Count != 1)
        {
            return Results.BadRequest(new ApiError(
                "SINGLE_INVENTORY_OPERATION_REQUIRED",
                "Exactly one inventory quantity operation is supported per transaction."));
        }

        var operation = request.Operations[0];
        if (!string.Equals(operation.Type, "setQuantity", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(operation.ItemId) ||
            string.IsNullOrWhiteSpace(operation.SlotId) ||
            string.IsNullOrWhiteSpace(operation.ContainerId) ||
            operation.ExpectedQuantity is null)
        {
            return Results.BadRequest(new ApiError(
                "INVALID_INVENTORY_OPERATION",
                "A setQuantity operation requires itemId, containerId, slotId and expectedQuantity."));
        }
        if (operation.Quantity is < 1 or > 999999 ||
            operation.ExpectedQuantity is < 1 or > 999999)
        {
            return Results.BadRequest(new ApiError(
                "INVENTORY_QUANTITY_OUT_OF_RANGE",
                "Inventory quantities must be between 1 and 999999."));
        }

        var slotParts = operation.SlotId.Split(':', 2, StringSplitOptions.TrimEntries);
        if (slotParts.Length != 2 ||
            !int.TryParse(slotParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var slotIndex) ||
            slotIndex < 0)
        {
            return Results.BadRequest(new ApiError(
                "INVALID_INVENTORY_SLOT",
                "slotId must use the '<containerKind>:<slotIndex>' format."));
        }
        var writableContainerKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "common", "dropSlot", "food"
        };
        if (!writableContainerKinds.Contains(slotParts[0]))
        {
            return Results.BadRequest(new ApiError(
                "INVENTORY_CONTAINER_READ_ONLY",
                "Only common, dropSlot and food containers support quantity edits."));
        }

        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("inventory.write"))
        {
            return BridgeUnavailable(
                "INVENTORY_WRITE_UNAVAILABLE",
                $"Inventory writes are disabled for player '{playerId}' on server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "inventory.mutate",
                new
                {
                    ownerPlayerId = playerId,
                    containerId = operation.ContainerId,
                    containerKind = slotParts[0],
                    slotIndex,
                    itemId = operation.ItemId,
                    expectedQuantity = operation.ExpectedQuantity.Value,
                    quantity = operation.Quantity,
                    dryRun = request.DryRun
                },
                request.Reason.Trim(),
                cancellationToken,
                request.ExpectedRevision,
                idempotencyKey: httpRequest.Headers["Idempotency-Key"].ToString());

            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                var error = new ApiError(
                    result.Error?.Code ?? "INVENTORY_MUTATION_FAILED",
                    result.Error?.Message ?? "Native inventory mutation failed.");
                return string.Equals(result.Error?.Code, "INVENTORY_SLOT_CONFLICT", StringComparison.Ordinal)
                    ? Results.Conflict(error)
                    : Results.Json(error, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            return result.Data is { } data
                ? Results.Ok(data)
                : Results.Ok(new { request.DryRun, applied = !request.DryRun });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "INVENTORY_WRITE_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native inventory mutation timed out."
                    : "Native inventory mutation connection ended.");
        }
    });

api.MapPost(
    "/servers/{serverId}/players/{playerId}/pals/{instanceId}/mutations",
    async (
        string serverId,
        string playerId,
        string instanceId,
        PalMutationRequest request,
        HttpRequest httpRequest,
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge,
        CancellationToken cancellationToken) =>
    {
        if (!long.TryParse(
                request.ExpectedRevision,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expectedRevision) ||
            expectedRevision < 0)
        {
            return Results.BadRequest(new ApiError(
                "INVALID_REVISION",
                "expectedRevision must be a non-negative integer string."));
        }

        var validation = ValidateMutationRequest(request.Reason, expectedRevision, httpRequest);
        if (validation is not null)
        {
            return validation;
        }
        if (!string.Equals(request.RequireState, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ApiError(
                "PAL_LOADED_STATE_REQUIRED",
                "Pal mutations require requireState='loaded'."));
        }
        if (request.Patch is null ||
            (request.Patch.Nickname is null && request.Patch.Favorite is null &&
             request.Patch.PassiveSkill is null &&
             request.Patch.PassiveSkills is null &&
             request.Patch.ExpectedPassiveSkills is null &&
             request.Patch.EquippedActiveSkills is null))
        {
            return Results.BadRequest(new ApiError(
                "EMPTY_PAL_PATCH",
                "At least one supported Pal field must be supplied."));
        }
        if (request.Patch.Nickname is { } nickname &&
            (nickname.Length > 24 || nickname.Any(char.IsControl)))
        {
            return Results.BadRequest(new ApiError(
                "INVALID_PAL_NICKNAME",
                "Pal nickname must be at most 24 characters and contain no control characters."));
        }
        if (request.Patch.PassiveSkill is { } passiveSkill &&
            (passiveSkill.Index < 0 ||
             string.IsNullOrWhiteSpace(passiveSkill.ExpectedSkillId) ||
             string.IsNullOrWhiteSpace(passiveSkill.SkillId) ||
             passiveSkill.ExpectedSkillId.Length > 96 ||
             passiveSkill.SkillId.Length > 96))
        {
            return Results.BadRequest(new ApiError(
                "INVALID_PASSIVE_SKILL_PATCH",
                "Passive skill replacement requires a valid index, expected skill and new skill."));
        }
        if (request.Patch.EquippedActiveSkills is { } activeSkills &&
            (activeSkills.Count is < 1 or > 3 ||
             activeSkills.Any(string.IsNullOrWhiteSpace) ||
             activeSkills.Any(skill => skill.Length > 96) ||
             activeSkills.Distinct(StringComparer.Ordinal).Count() != activeSkills.Count))
        {
            return Results.BadRequest(new ApiError(
                "INVALID_ACTIVE_SKILL_LOADOUT",
                "Equipped active skills must contain one to three unique valid skill IDs."));
        }
        if ((request.Patch.PassiveSkills is null) !=
            (request.Patch.ExpectedPassiveSkills is null) ||
            request.Patch.PassiveSkills is { } passiveSkills &&
            (passiveSkills.Count is < 1 or > 4 ||
             passiveSkills.Any(string.IsNullOrWhiteSpace) ||
             passiveSkills.Any(skill => skill.Length > 96) ||
             passiveSkills.Distinct(StringComparer.Ordinal).Count() != passiveSkills.Count))
        {
            return Results.BadRequest(new ApiError(
                "INVALID_PASSIVE_SKILL_SET",
                "Passive skill set replacement requires matching expected and desired lists with one to four unique IDs."));
        }

        var snapshot = bridgeState.GetSnapshot();
        if (!snapshot.Connected || !snapshot.Capabilities.Contains("pals.write"))
        {
            return BridgeUnavailable(
                "PAL_WRITE_UNAVAILABLE",
                $"Pal '{instanceId}' writes are disabled for player '{playerId}' on server '{serverId}'.");
        }

        try
        {
            var result = await bridge.SendCommandAsync(
                serverId,
                "pals.mutate",
                new
                {
                    instanceId,
                    ownerPlayerId = playerId,
                    nickname = request.Patch.Nickname,
                    favorite = request.Patch.Favorite,
                    passiveSkillIndex = request.Patch.PassiveSkill?.Index,
                    expectedPassiveSkill = request.Patch.PassiveSkill?.ExpectedSkillId,
                    passiveSkill = request.Patch.PassiveSkill?.SkillId,
                    expectedPassiveSkills = request.Patch.ExpectedPassiveSkills,
                    passiveSkills = request.Patch.PassiveSkills,
                    equippedActiveSkills = request.Patch.EquippedActiveSkills,
                    dryRun = request.DryRun
                },
                request.Reason.Trim(),
                cancellationToken,
                expectedRevision,
                idempotencyKey: httpRequest.Headers["Idempotency-Key"].ToString());

            if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal))
            {
                var error = new ApiError(
                    result.Error?.Code ?? "PAL_MUTATION_FAILED",
                    result.Error?.Message ?? "Native Pal mutation failed.");
                return string.Equals(result.Error?.Code, "REVISION_CONFLICT", StringComparison.Ordinal)
                    ? Results.Conflict(error)
                    : Results.Json(error, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            return result.Data is { } data
                ? Results.Ok(PalPayloadForWeb(data))
                : Results.Ok(new { request.DryRun, applied = !request.DryRun });
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return BridgeUnavailable(
                "PAL_WRITE_UNAVAILABLE",
                exception is TimeoutException
                    ? "Native Pal mutation timed out."
                    : "Native Pal mutation connection ended.");
        }
    });

api.MapGet("/events", async (HttpContext context, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    while (!cancellationToken.IsCancellationRequested)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "heartbeat",
            at = DateTimeOffset.UtcNow
        });
        await context.Response.WriteAsync($"event: heartbeat\ndata: {payload}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
    }
});

app.Run();

static bool IsOperatorOnlyApi(PathString path) =>
    path.StartsWithSegments("/api/v1") &&
    !path.StartsWithSegments("/api/v1/player") &&
    !path.StartsWithSegments("/api/v1/internal/federation");

static bool IsLoopback(IPAddress? address) =>
    address is not null && IPAddress.IsLoopback(address);

static string LiveMapEtag(string streamId, long sequence) =>
    $"\"live-map-{streamId}-{sequence}\"";

static bool MatchesEtag(string ifNoneMatch, string etag) => ifNoneMatch
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Any(candidate =>
    {
        var normalized = candidate.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
            ? candidate[2..].TrimStart()
            : candidate;
        return normalized is "*" || string.Equals(normalized, etag, StringComparison.Ordinal);
    });

static async Task StreamLiveMapAsync(
    HttpContext context,
    LiveMapService liveMap,
    CancellationToken cancellationToken)
{
    LiveMapService.LiveMapSubscription subscription;
    try
    {
        subscription = liveMap.Subscribe();
    }
    catch (InvalidOperationException)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.Response.WriteAsJsonAsync(
            new ApiError(
                "LIVE_MAP_SUBSCRIBER_LIMIT_REACHED",
                "The live-map event stream has reached its subscriber limit."),
            cancellationToken);
        return;
    }

    await using (subscription)
    {
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.ContentType = "text/event-stream";
        await context.Response.StartAsync(cancellationToken);
        await context.Response.WriteAsync("retry: 2000\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var nextSnapshot = subscription.Reader.ReadAsync(cancellationToken).AsTask();
        var nextHeartbeat = Task.Delay(
            TimeSpan.FromMilliseconds(liveMap.HeartbeatIntervalMilliseconds),
            cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(nextSnapshot, nextHeartbeat);
                if (completed == nextSnapshot)
                {
                    var snapshot = await nextSnapshot;
                    var payload = JsonSerializer.Serialize(snapshot, jsonOptions);
                    await context.Response.WriteAsync(
                        $"id: {snapshot.StreamId}:{snapshot.Sequence}\nevent: snapshot\ndata: {payload}\n\n",
                        cancellationToken);
                    nextSnapshot = subscription.Reader.ReadAsync(cancellationToken).AsTask();
                }
                else
                {
                    await nextHeartbeat;
                    var payload = JsonSerializer.Serialize(new
                    {
                        type = "heartbeat",
                        at = DateTimeOffset.UtcNow
                    }, jsonOptions);
                    await context.Response.WriteAsync(
                        $"event: heartbeat\ndata: {payload}\n\n",
                        cancellationToken);
                    nextHeartbeat = Task.Delay(
                        TimeSpan.FromMilliseconds(liveMap.HeartbeatIntervalMilliseconds),
                        cancellationToken);
                }
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected or the host is stopping.
        }
        catch (ChannelClosedException)
        {
            // The sampler is stopping with the host.
        }
    }
}

static IResult? ValidateMutationRequest(string reason, long expectedRevision, HttpRequest request)
{
    if (!request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey) ||
        string.IsNullOrWhiteSpace(idempotencyKey))
    {
        return Results.BadRequest(new ApiError(
            "IDEMPOTENCY_KEY_REQUIRED",
            "Mutation requests require the Idempotency-Key header."));
    }

    if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
    {
        return Results.BadRequest(new ApiError(
            "REASON_REQUIRED",
            "A human-readable reason with at least 3 characters is required."));
    }

    if (expectedRevision < 0)
    {
        return Results.BadRequest(new ApiError(
            "INVALID_REVISION",
            "expectedRevision must be zero or greater."));
    }

    return null;
}

static IResult? ValidateIdempotencyKey(HttpRequest request)
{
    if (!request.Headers.TryGetValue("Idempotency-Key", out var header))
    {
        return Results.BadRequest(new ApiError(
            "IDEMPOTENCY_KEY_REQUIRED",
            "The Idempotency-Key header is required."));
    }

    var key = header.ToString();
    if (key.Length is < 8 or > 128 || key.Any(char.IsControl))
    {
        return Results.BadRequest(new ApiError(
            "INVALID_IDEMPOTENCY_KEY",
            "Idempotency-Key must contain 8 to 128 non-control characters."));
    }
    return null;
}

static IResult? ValidateSaveWriteRequest(
    string serverId,
    string reason,
    string? label,
    HttpRequest request,
    IConfiguration configuration)
{
    var idempotencyValidation = ValidateIdempotencyKey(request);
    if (idempotencyValidation is not null)
    {
        return idempotencyValidation;
    }
    var serverValidation = ValidateServerId(serverId, configuration);
    if (serverValidation is not null)
    {
        return serverValidation;
    }
    if (string.IsNullOrWhiteSpace(reason) ||
        reason.Trim().Length is < 3 or > 500 ||
        reason.Any(char.IsControl))
    {
        return Results.BadRequest(new ApiError(
            "INVALID_SAVE_REASON",
            "A human-readable reason containing 3 to 500 non-control characters is required."));
    }
    if (label is not null &&
        (string.IsNullOrWhiteSpace(label) ||
         label.Trim().Length > 80 ||
         label.Any(char.IsControl)))
    {
        return Results.BadRequest(new ApiError(
            "INVALID_BACKUP_LABEL",
            "Backup label must contain 1 to 80 non-control characters."));
    }
    return null;
}

static IResult SaveCommandEnqueueResponse(
    SaveCommandEnqueueResult result,
    HttpRequest request)
{
    if (result.IdempotencyConflict)
    {
        return Results.Conflict(new ApiError(
            "IDEMPOTENCY_KEY_REUSED",
            "The Idempotency-Key was already used for a different save operation."));
    }
    var command = result.Command
        ?? throw new InvalidOperationException("A save command enqueue result has no command.");
    request.HttpContext.Response.Headers.Location = command.StatusUrl;
    request.HttpContext.Response.Headers.RetryAfter = "1";
    return Results.Json(
        command,
        statusCode: result.Created || command.State is "accepted" or "dispatched"
            ? StatusCodes.Status202Accepted
            : StatusCodes.Status200OK);
}

static IResult SaveManagementProblem(SaveManagementException exception)
{
    var statusCode = exception.Code switch
    {
        "SERVER_NOT_FOUND" or "BACKUP_NOT_FOUND" => StatusCodes.Status404NotFound,
        "INVALID_BACKUP_KIND" or "INVALID_BACKUP_ID" or "INVALID_SERVER_ID" =>
            StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status503ServiceUnavailable
    };
    return Results.Json(
        new ApiError(exception.Code, exception.Message),
        statusCode: statusCode);
}

static IResult? ValidateAnnouncementInput(AnnouncementInput request)
{
    if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 120)
    {
        return Results.BadRequest(new ApiError(
            "INVALID_ANNOUNCEMENT_TITLE",
            "Announcement title is required and must be at most 120 characters."));
    }
    if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Trim().Length > 1000)
    {
        return Results.BadRequest(new ApiError(
            "INVALID_ANNOUNCEMENT_BODY",
            "Announcement body is required and must be at most 1000 characters."));
    }
    if (request.Audience is null ||
        !string.Equals(request.Audience.Type, "global", StringComparison.OrdinalIgnoreCase) ||
        request.Audience.Ids is { Count: > 0 })
    {
        return Results.BadRequest(new ApiError(
            "UNSUPPORTED_ANNOUNCEMENT_AUDIENCE",
            "Announcement delivery currently supports only the global audience without target IDs."));
    }
    var channels = request.Channels?
        .Select(channel => channel?.Trim().ToLowerInvariant())
        .ToArray();
    if (channels is null ||
        channels.Length is < 1 or > 3 ||
        channels.Any(string.IsNullOrEmpty) ||
        channels.Distinct(StringComparer.Ordinal).Count() != channels.Length ||
        channels.Any(channel => channel is not ("chat" or "client-overlay" or "top-banner")))
    {
        return Results.BadRequest(new ApiError(
            "UNSUPPORTED_ANNOUNCEMENT_CHANNEL",
            "Announcement channels must be a unique non-empty subset of chat, client-overlay, and top-banner."));
    }
    if (request.ExpiresAt is { } expiresAt &&
        expiresAt <= (request.PublishAt ?? DateTimeOffset.UtcNow))
    {
        return Results.BadRequest(new ApiError(
            "INVALID_ANNOUNCEMENT_EXPIRY",
            "expiresAt must be later than publishAt."));
    }
    return null;
}

static IResult? ValidateServerId(string serverId, IConfiguration configuration)
{
    var configuredServerId = configuration["Palworld:ServerId"] ?? "local";
    return string.Equals(serverId, configuredServerId, StringComparison.Ordinal)
        ? null
        : Results.NotFound(new ApiError(
            "SERVER_NOT_FOUND",
            $"Server '{serverId}' is not configured on this Control API instance."));
}

static string GetActor(HttpRequest request)
{
    if (request.HttpContext.User.Identity?.Name is { Length: > 0 } name)
    {
        return name;
    }
    return $"local:{request.HttpContext.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback}";
}

static void AddRolePolicy(
    AuthorizationOptions options,
    string policyName,
    string role) => options.AddPolicy(policyName, policy => policy
    .AddAuthenticationSchemes(AdminAuthenticationDefaults.Scheme)
    .RequireAuthenticatedUser()
    .RequireRole(role));

static void AddHighRiskPolicy(
    AuthorizationOptions options,
    string policyName,
    string role) => options.AddPolicy(policyName, policy => policy
    .AddAuthenticationSchemes(AdminAuthenticationDefaults.Scheme)
    .RequireAuthenticatedUser()
    .RequireRole(role)
    .AddRequirements(new AdminTotpRequirement(), new AdminReasonRequirement()));

static IResult BridgeUnavailable(string code, string message) => Results.Json(
    new ApiError(code, message),
    statusCode: StatusCodes.Status503ServiceUnavailable);

static string NormalizeIdentifier(string value) => string.Concat(
    value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

static (
    long Experience,
    int Level,
    string Revision,
    int UnusedStatusPoints,
    int? TechnologyPoints,
    int? AncientTechnologyPoints)? FindPlayerProgression(
        JsonElement? data,
        string playerId)
{
    if (data is not { ValueKind: JsonValueKind.Object } payload ||
        !payload.TryGetProperty("players", out var players) ||
        players.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    var normalizedPlayerId = NormalizeIdentifier(playerId);
    foreach (var player in players.EnumerateArray())
    {
        if (!player.TryGetProperty("playerUId", out var uidElement) ||
            uidElement.GetString() is not { Length: > 0 } playerUId)
        {
            continue;
        }
        var normalizedOwner = NormalizeIdentifier(playerUId);
        if (!string.Equals(
                normalizedOwner,
                normalizedPlayerId,
                StringComparison.Ordinal) &&
            !(normalizedPlayerId.Length == 8 &&
              normalizedOwner.StartsWith(
                  normalizedPlayerId,
                  StringComparison.Ordinal)))
        {
            continue;
        }

        return (
            player.GetProperty("totalExperience").GetInt64(),
            player.GetProperty("level").GetInt32(),
            player.GetProperty("revision").GetString() ?? "0",
            player.GetProperty("unusedStatusPoints").GetInt32(),
            player.TryGetProperty("technologyPoints", out var technology) &&
                technology.ValueKind == JsonValueKind.Number
                ? technology.GetInt32()
                : null,
            player.TryGetProperty(
                    "ancientTechnologyPoints",
                    out var ancientTechnology) &&
                ancientTechnology.ValueKind == JsonValueKind.Number
                ? ancientTechnology.GetInt32()
                : null);
    }
    return null;
}

static JsonNode PalPayloadForWeb(JsonElement payload)
{
    var node = JsonNode.Parse(payload.GetRawText())
        ?? throw new JsonException("Native Pal payload is empty.");
    if (node is JsonObject root)
    {
        if (root["pals"] is JsonArray pals)
        {
            foreach (var pal in pals.OfType<JsonObject>())
            {
                StringifyPalRevision(pal);
            }
        }
        if (root["pal"] is JsonObject resultPal)
        {
            StringifyPalRevision(resultPal);
        }
        if (root.ContainsKey("instanceId"))
        {
            StringifyPalRevision(root);
        }
    }
    return node;
}

static void StringifyPalRevision(JsonObject pal)
{
    if (pal["revision"] is JsonValue value && value.TryGetValue<long>(out var revision))
    {
        pal["revision"] = revision.ToString(CultureInfo.InvariantCulture);
    }
}

public partial class Program;

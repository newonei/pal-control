using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public static class TeamEconomyEndpoints
{
    public static RouteGroupBuilder MapTeamEconomyEndpoints(this RouteGroupBuilder api)
    {
        // The API root is protected by the operator policy. Player routes opt
        // out of that inherited policy and enforce the player session, origin
        // and CSRF boundary explicitly inside each handler.
        var group = api.MapGroup("/player/me/team-economy")
            .AllowAnonymous();

        group.MapGet("", async (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            TeamEconomyStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(httpContext);
            var context = await RequireContextAsync(
                httpContext, authentication, coordinator, cancellationToken);
            await RefreshBestEffortAsync(store, context, timeProvider, cancellationToken);
            return Results.Ok(await store.GetDashboardAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                context.Account.AccountId,
                cancellationToken));
        }));

        group.MapGet("/leaderboards/{metric}", async (
            string metric,
            string? cursor,
            int? limit,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            TeamEconomyStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(httpContext, null, "cursor", "limit");
            if (!Enum.TryParse<TeamEconomyMetric>(metric, ignoreCase: true, out var parsedMetric))
            {
                throw new TeamEconomyException(
                    "TEAM_LEADERBOARD_METRIC_INVALID",
                    "Leaderboard metric must be resourceValue, taskPoints, or deliveredOrders.",
                    StatusCodes.Status400BadRequest);
            }
            var offset = 0;
            if (!string.IsNullOrWhiteSpace(cursor) &&
                (!int.TryParse(cursor, out offset) || offset < 0))
            {
                throw new TeamEconomyException(
                    "TEAM_LEADERBOARD_CURSOR_INVALID",
                    "The leaderboard cursor is invalid.",
                    StatusCodes.Status400BadRequest);
            }
            var context = await RequireContextAsync(
                httpContext, authentication, coordinator, cancellationToken);
            await RefreshBestEffortAsync(store, context, timeProvider, cancellationToken);
            return Results.Ok(await store.GetLeaderboardAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                context.Account.AccountId,
                parsedMetric,
                offset,
                Math.Clamp(limit ?? 50, 1, TeamEconomyLimits.MaximumPageSize),
                cancellationToken));
        }));

        group.MapPost("/teams", async (
            CreateTeamRequest request,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            TeamEconomyStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(httpContext, request);
            var (context, key) = await RequireWriteContextAsync(
                httpContext, authentication, coordinator, cancellationToken);
            return Results.Ok(await store.CreateTeamAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                context.Account.AccountId,
                request.Name ?? string.Empty,
                key,
                cancellationToken));
        }));

        group.MapPost("/invite/rotate", async (
            RotateTeamInviteRequest request,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            TeamEconomyStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(httpContext, request);
            var (context, key) = await RequireWriteContextAsync(
                httpContext, authentication, coordinator, cancellationToken);
            return Results.Ok(await store.RotateInviteAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                context.Account.AccountId,
                request.MaximumUses,
                key,
                cancellationToken));
        }));

        group.MapPost("/join", async (
            JoinTeamRequest request,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            TeamEconomyStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(httpContext, request);
            var (context, key) = await RequireWriteContextAsync(
                httpContext, authentication, coordinator, cancellationToken);
            return Results.Ok(await store.JoinAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                context.Account.AccountId,
                request.Token ?? string.Empty,
                key,
                cancellationToken));
        }));

        group.MapPost("/leave", async (
            EmptyTeamRequest request,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            TeamEconomyStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(httpContext, request);
            var (context, key) = await RequireWriteContextAsync(
                httpContext, authentication, coordinator, cancellationToken);
            return Results.Ok(await store.LeaveAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                context.Account.AccountId,
                key,
                cancellationToken));
        }));

        group.MapPost("/owner/transfer", async (
            TransferTeamOwnerRequest request,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            TeamEconomyStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(httpContext, request);
            var (context, key) = await RequireWriteContextAsync(
                httpContext, authentication, coordinator, cancellationToken);
            return Results.Ok(await store.TransferOwnershipAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                context.Account.AccountId,
                request.MemberHandle ?? string.Empty,
                key,
                cancellationToken));
        }));

        group.MapPost("/dissolve", async (
            DissolveTeamRequest request,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            TeamEconomyStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(httpContext, request);
            var (context, key) = await RequireWriteContextAsync(
                httpContext, authentication, coordinator, cancellationToken);
            return Results.Ok(await store.DissolveAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                context.Account.AccountId,
                request.Confirmation ?? string.Empty,
                key,
                cancellationToken));
        }));

        return api;
    }

    private static async Task RefreshBestEffortAsync(
        TeamEconomyStore store,
        ExtractionAccountContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.ProjectSeasonAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TeamEconomyException exception) when (
            exception.Code == "TEAM_ECONOMY_DISABLED")
        {
            // Disabled is an intentional safe default, not a projection
            // failure. GetDashboardAsync returns the explicit disabled state.
        }
        catch (TeamEconomyException exception)
        {
            await store.RecordProjectionFailureAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                exception.Code,
                CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or SqliteException)
        {
            await store.RecordProjectionFailureAsync(
                context.Season.ServerId,
                context.Season.SeasonId,
                "TEAM_PROJECTION_SOURCE_UNAVAILABLE",
                CancellationToken.None);
        }
    }

    private static async Task<(ExtractionAccountContext Context, string IdempotencyKey)>
        RequireWriteContextAsync(
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            CancellationToken cancellationToken)
    {
        var session = authentication.RequireSession(httpContext);
        authentication.RequireAllowedOrigin(httpContext);
        authentication.RequireCsrf(httpContext, session);
        var context = await coordinator.GetAccountContextAsync(
            session.UserId, requireOnline: false, cancellationToken);
        return (context, RequireIdempotencyKey(httpContext.Request));
    }

    private static async Task<ExtractionAccountContext> RequireContextAsync(
        HttpContext httpContext,
        PlayerPortalAuthenticationService authentication,
        ExtractionModeCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var session = authentication.RequireSession(httpContext);
        return await coordinator.GetAccountContextAsync(
            session.UserId, requireOnline: false, cancellationToken);
    }

    private static string RequireIdempotencyKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) ||
            values.Count != 1 || values[0] is not { Length: >= 8 } key ||
            key.Length > 128 || key.Any(char.IsControl))
        {
            throw new TeamEconomyException(
                "TEAM_IDEMPOTENCY_KEY_REQUIRED",
                "Idempotency-Key must contain 8-128 non-control characters.",
                StatusCodes.Status400BadRequest);
        }
        return key;
    }

    private static void RejectIdentityOverride(
        HttpContext context,
        TeamIdentityOverride? body = null,
        params string[] allowedQuery)
    {
        var allowed = allowedQuery.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var query in context.Request.Query.Keys)
        {
            if (!allowed.Contains(query))
            {
                throw IdentityOverride();
            }
        }
        foreach (var name in new[]
                 {
                     "X-Account-Id", "X-User-Id", "X-Player-Uid", "X-Steam-Id",
                     "X-Season-Id", "X-Server-Id", "X-Team-Id"
                 })
        {
            if (context.Request.Headers.ContainsKey(name))
            {
                throw IdentityOverride();
            }
        }
        if (body is not null &&
            (body.AccountId is not null || body.UserId is not null ||
             body.PlayerUid is not null || body.SteamId is not null ||
             body.SeasonId is not null || body.ServerId is not null ||
             body.TeamId is not null))
        {
            throw IdentityOverride();
        }
    }

    private static TeamEconomyException IdentityOverride() => new(
        "TEAM_IDENTITY_OVERRIDE_FORBIDDEN",
        "Team identity and weekly-world scope are derived only from the authenticated player session.",
        StatusCodes.Status400BadRequest);

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlayerPortalException exception)
        {
            return Results.Json(
                new ApiError(exception.Code, exception.Message),
                statusCode: exception.StatusCode);
        }
        catch (ExtractionModeException exception)
        {
            return Results.Json(
                new ApiError(exception.Code, exception.Message),
                statusCode: exception.StatusCode);
        }
        catch (TeamEconomyException exception)
        {
            return Results.Json(
                new ApiError(exception.Code, exception.Message),
                statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or SqliteException)
        {
            return Results.Json(
                new ApiError(
                    "TEAM_ECONOMY_UNAVAILABLE",
                    "Team collaboration is temporarily unavailable; no economy action was attempted."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    public abstract record TeamIdentityOverride(
        Guid? AccountId = null,
        string? UserId = null,
        string? PlayerUid = null,
        string? SteamId = null,
        Guid? SeasonId = null,
        string? ServerId = null,
        Guid? TeamId = null);

    public sealed record CreateTeamRequest(
        string? Name,
        Guid? AccountId = null,
        string? UserId = null,
        string? PlayerUid = null,
        string? SteamId = null,
        Guid? SeasonId = null,
        string? ServerId = null,
        Guid? TeamId = null)
        : TeamIdentityOverride(AccountId, UserId, PlayerUid, SteamId, SeasonId, ServerId, TeamId);

    public sealed record RotateTeamInviteRequest(
        int? MaximumUses,
        Guid? AccountId = null,
        string? UserId = null,
        string? PlayerUid = null,
        string? SteamId = null,
        Guid? SeasonId = null,
        string? ServerId = null,
        Guid? TeamId = null)
        : TeamIdentityOverride(AccountId, UserId, PlayerUid, SteamId, SeasonId, ServerId, TeamId);

    public sealed record JoinTeamRequest(
        string? Token,
        Guid? AccountId = null,
        string? UserId = null,
        string? PlayerUid = null,
        string? SteamId = null,
        Guid? SeasonId = null,
        string? ServerId = null,
        Guid? TeamId = null)
        : TeamIdentityOverride(AccountId, UserId, PlayerUid, SteamId, SeasonId, ServerId, TeamId);

    public sealed record EmptyTeamRequest(
        Guid? AccountId = null,
        string? UserId = null,
        string? PlayerUid = null,
        string? SteamId = null,
        Guid? SeasonId = null,
        string? ServerId = null,
        Guid? TeamId = null)
        : TeamIdentityOverride(AccountId, UserId, PlayerUid, SteamId, SeasonId, ServerId, TeamId);

    public sealed record TransferTeamOwnerRequest(
        string? MemberHandle,
        Guid? AccountId = null,
        string? UserId = null,
        string? PlayerUid = null,
        string? SteamId = null,
        Guid? SeasonId = null,
        string? ServerId = null,
        Guid? TeamId = null)
        : TeamIdentityOverride(AccountId, UserId, PlayerUid, SteamId, SeasonId, ServerId, TeamId);

    public sealed record DissolveTeamRequest(
        string? Confirmation,
        Guid? AccountId = null,
        string? UserId = null,
        string? PlayerUid = null,
        string? SteamId = null,
        Guid? SeasonId = null,
        string? ServerId = null,
        Guid? TeamId = null)
        : TeamIdentityOverride(AccountId, UserId, PlayerUid, SteamId, SeasonId, ServerId, TeamId);
}

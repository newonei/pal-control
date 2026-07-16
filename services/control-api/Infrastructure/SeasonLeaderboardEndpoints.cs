using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class SeasonLeaderboardEndpoints
{
    public static RouteGroupBuilder MapSeasonLeaderboardEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin/season-leaderboards");

        admin.MapGet("/{seasonId:guid}", async (
            Guid seasonId,
            SeasonLeaderboardService service,
            CancellationToken cancellationToken) => await ExecuteAdminAsync(async () =>
        {
            var record = await service.GetAsync(seasonId, cancellationToken);
            return record is null
                ? Results.NotFound(new ApiError(
                    "SEASON_LEADERBOARD_NOT_FOUND",
                    "The frozen season leaderboard does not exist."))
                : Results.Ok(record);
        }))
        .RequireAuthorization(AdminPolicies.SeasonAdmin);

        admin.MapPost("/{seasonId:guid}/freeze", async (
            Guid seasonId,
            HttpContext context,
            ExtractionOperationGate operationGate,
            SeasonLeaderboardService service,
            CancellationToken cancellationToken) => await ExecuteAdminAsync(async () =>
            Results.Ok(await service.FreezeAsync(
                seasonId,
                AdminIdentity.RequireSubject(context),
                ControlPlaneCorrelationMiddleware.GetCorrelationId(context),
                operationGate.Current,
                operationGate.ActiveOperationCount,
                cancellationToken))))
        .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        admin.MapPut("/{seasonId:guid}/exclusions/{accountId:guid}", async (
            Guid seasonId,
            Guid accountId,
            SeasonLeaderboardExclusionRequest request,
            HttpContext context,
            SeasonLeaderboardService service,
            CancellationToken cancellationToken) => await ExecuteAdminAsync(async () =>
            Results.Ok(await service.SetExclusionAsync(
                seasonId,
                accountId,
                request.Excluded,
                request.Reason,
                AdminIdentity.RequireSubject(context),
                ControlPlaneCorrelationMiddleware.GetCorrelationId(context),
                cancellationToken))))
        .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        admin.MapPost("/{seasonId:guid}/rewards/prepare", async (
            Guid seasonId,
            HttpContext context,
            ExtractionOperationGate operationGate,
            SeasonLeaderboardService service,
            CancellationToken cancellationToken) => await ExecuteAdminAsync(async () =>
            Results.Ok(await service.PrepareRewardsAsync(
                seasonId,
                AdminIdentity.RequireSubject(context),
                ControlPlaneCorrelationMiddleware.GetCorrelationId(context),
                operationGate.Current,
                operationGate.ActiveOperationCount,
                cancellationToken))))
        .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        admin.MapPost("/{seasonId:guid}/rewards/run", async (
            Guid seasonId,
            HttpContext context,
            ExtractionOperationGate operationGate,
            SeasonLeaderboardService service,
            CancellationToken cancellationToken) => await ExecuteAdminAsync(async () =>
            Results.Ok(await service.RunRewardsAsync(
                seasonId,
                AdminIdentity.RequireSubject(context),
                ControlPlaneCorrelationMiddleware.GetCorrelationId(context),
                operationGate.Current,
                operationGate.ActiveOperationCount,
                cancellationToken))))
        .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        admin.MapPost("/{seasonId:guid}/rewards/manual", async (
            Guid seasonId,
            SeasonLeaderboardManualRewardRequest request,
            HttpContext context,
            ExtractionOperationGate operationGate,
            SeasonLeaderboardService service,
            CancellationToken cancellationToken) => await ExecuteAdminAsync(async () =>
            Results.Ok(await service.GrantManualRewardAsync(
                seasonId,
                request.AccountId,
                request.Amount,
                request.ManualKey,
                request.Reason,
                AdminIdentity.RequireSubject(context),
                ControlPlaneCorrelationMiddleware.GetCorrelationId(context),
                operationGate.Current,
                operationGate.ActiveOperationCount,
                cancellationToken))))
        .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        api.MapGet("/player/me/season-leaderboards/latest", async (
            HttpContext context,
            PlayerPortalAuthenticationService authentication,
            IExtractionRepository repository,
            ExtractionModeCoordinator coordinator,
            PlayerSeasonSettlementService service,
            CancellationToken cancellationToken) => await ExecutePlayerAsync(async () =>
        {
            RejectPlayerIdentityOverride(context);
            var account = await RequirePlayerAccountAsync(
                context,
                authentication,
                repository,
                cancellationToken);
            return LatestPlayerResult(await service.GetLatestAsync(
                coordinator.ServerId,
                account.AccountId,
                cancellationToken));
        }))
        .AllowAnonymous();

        api.MapGet("/player/me/season-leaderboards/{seasonId:guid}", async (
            Guid seasonId,
            HttpContext context,
            PlayerPortalAuthenticationService authentication,
            IExtractionRepository repository,
            PlayerSeasonSettlementService service,
            CancellationToken cancellationToken) => await ExecutePlayerAsync(async () =>
        {
            RejectPlayerIdentityOverride(context);
            var account = await RequirePlayerAccountAsync(
                context,
                authentication,
                repository,
                cancellationToken);
            var response = await service.GetAsync(
                seasonId,
                account.AccountId,
                cancellationToken);
            return PlayerSeasonResult(response);
        }))
        .AllowAnonymous();

        return api;
    }

    private static async Task<IResult> ExecuteAdminAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new ApiError(
                "SEASON_LEADERBOARD_REQUEST_INVALID",
                exception.Message));
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new ApiError(
                "SEASON_LEADERBOARD_RESOURCE_NOT_FOUND",
                exception.Message));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException)
        {
            return Results.Conflict(new ApiError(
                "SEASON_LEADERBOARD_PRECONDITION_FAILED",
                exception.Message));
        }
        catch (Exception exception) when (exception is IOException or SqliteException)
        {
            return Results.Json(
                new ApiError(
                    "SEASON_LEADERBOARD_STORAGE_UNAVAILABLE",
                    "Leaderboard storage is temporarily unavailable."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ExecutePlayerAsync(Func<Task<IResult>> action)
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
        catch (InvalidDataException)
        {
            return Results.Json(
                new ApiError(
                    "SEASON_LEADERBOARD_UNAVAILABLE",
                    "The season leaderboard failed integrity validation."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (exception is IOException or SqliteException)
        {
            return Results.Json(
                new ApiError(
                    "SEASON_LEADERBOARD_UNAVAILABLE",
                    "The season leaderboard is temporarily unavailable."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<ExtractionAccount> RequirePlayerAccountAsync(
        HttpContext context,
        PlayerPortalAuthenticationService authentication,
        IExtractionRepository repository,
        CancellationToken cancellationToken)
    {
        var session = authentication.RequireSession(context);
        var separator = session.UserId.IndexOf('_');
        if (separator <= 0)
        {
            throw new PlayerPortalException(
                "PLAYER_SESSION_INVALID",
                "The authenticated player session has an invalid identity.",
                StatusCodes.Status401Unauthorized);
        }
        var provider = session.UserId[..separator];
        return await repository.FindAccountAsync(
            provider,
            session.UserId,
            cancellationToken)
            ?? throw new PlayerPortalException(
                "PLAYER_ACCOUNT_NOT_FOUND",
                "The authenticated player has no economy account.",
                StatusCodes.Status404NotFound);
    }

    internal static IResult LatestPlayerResult(PlayerSeasonSettlementResponse response) =>
        Results.Ok(response);

    internal static IResult PlayerSeasonResult(PlayerSeasonSettlementResponse? response) =>
        response is null
            ? Results.NotFound(new ApiError(
                "SEASON_LEADERBOARD_NOT_FOUND",
                "The frozen season leaderboard does not exist."))
            : Results.Ok(response);

    internal static void RejectPlayerIdentityOverride(HttpContext context)
    {
        foreach (var name in new[] { "accountId", "userId", "steamId", "playerUid" })
        {
            if (context.Request.Query.ContainsKey(name))
            {
                throw new PlayerPortalException(
                    "PLAYER_IDENTITY_OVERRIDE_FORBIDDEN",
                    "Player settlement queries are bound to the authenticated account.",
                    StatusCodes.Status400BadRequest);
            }
        }
    }

    public sealed record SeasonLeaderboardExclusionRequest(bool Excluded, string Reason);

    public sealed record SeasonLeaderboardManualRewardRequest(
        Guid AccountId,
        long Amount,
        string ManualKey,
        string Reason);
}

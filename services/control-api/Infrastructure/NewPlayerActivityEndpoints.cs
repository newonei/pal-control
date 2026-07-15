using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class NewPlayerActivityEndpoints
{
    public static RouteGroupBuilder MapNewPlayerActivityAdminEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/extraction/admin/new-player-activities")
            .RequireAuthorization(AdminPolicies.EconomyAdmin);

        group.MapGet("", async (
            string? activityKey,
            SqliteExtractionRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var activities = await repository.ListNewPlayerActivitiesAsync(
                    activityKey,
                    cancellationToken);
                return Results.Ok(new
                {
                    items = activities.Select(ActivityDto).ToArray()
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapGet("/{activityKey}/versions/{version:int}", async (
            string activityKey,
            int version,
            SqliteExtractionRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var activity = await repository.GetNewPlayerActivityAsync(
                    activityKey,
                    version,
                    cancellationToken);
                return activity is null
                    ? Results.NotFound(new ApiError(
                        "NEW_PLAYER_ACTIVITY_NOT_FOUND",
                        "The requested new-player activity version does not exist."))
                    : Results.Ok(ActivityDto(activity));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapPost("/{activityKey}/versions", async (
            string activityKey,
            NewPlayerActivityDefinition request,
            HttpContext httpContext,
            SqliteExtractionRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var activity = await repository.CreateNewPlayerActivityDraftAsync(
                    activityKey,
                    request,
                    AdminIdentity.RequireSubject(httpContext),
                    cancellationToken);
                AdminAuditEnrichment.SetAfter(httpContext, ActivityDto(activity));
                return Results.Created(
                    $"/api/v1/extraction/admin/new-player-activities/{activity.ActivityKey}/versions/{activity.Version}",
                    ActivityDto(activity));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapPut("/{activityKey}/versions/{version:int}", async (
            string activityKey,
            int version,
            UpdateNewPlayerActivityDraftRequest request,
            HttpContext httpContext,
            SqliteExtractionRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var before = await repository.GetNewPlayerActivityAsync(
                    activityKey,
                    version,
                    cancellationToken);
                AdminAuditEnrichment.SetBefore(
                    httpContext,
                    before is null ? null : ActivityDto(before));
                var activity = await repository.UpdateNewPlayerActivityDraftAsync(
                    activityKey,
                    version,
                    new NewPlayerActivityDefinition(
                        request.Title,
                        request.Description,
                        request.MarketCoin,
                        request.SeasonVoucher),
                    request.ExpectedRevision,
                    cancellationToken);
                AdminAuditEnrichment.SetAfter(httpContext, ActivityDto(activity));
                return Results.Ok(ActivityDto(activity));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapPost("/{activityKey}/versions/{version:int}/publish", async (
            string activityKey,
            int version,
            HttpContext httpContext,
            SqliteExtractionRepository repository,
            ExtractionModeCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var before = await repository.GetNewPlayerActivityAsync(
                    activityKey,
                    version,
                    cancellationToken);
                AdminAuditEnrichment.SetBefore(
                    httpContext,
                    before is null ? null : ActivityDto(before));
                var season = await coordinator.GetActiveSeasonAsync(cancellationToken);
                var activity = await repository.PublishNewPlayerActivityAsync(
                    activityKey,
                    version,
                    season,
                    AdminIdentity.RequireSubject(httpContext),
                    cancellationToken);
                AdminAuditEnrichment.SetAfter(httpContext, ActivityDto(activity));
                return Results.Ok(ActivityDto(activity));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyHighRisk);

        group.MapPost("/{activityKey}/versions/{version:int}/close", async (
            string activityKey,
            int version,
            HttpContext httpContext,
            SqliteExtractionRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var before = await repository.GetNewPlayerActivityAsync(
                    activityKey,
                    version,
                    cancellationToken);
                AdminAuditEnrichment.SetBefore(
                    httpContext,
                    before is null ? null : ActivityDto(before));
                var activity = await repository.CloseNewPlayerActivityAsync(
                    activityKey,
                    version,
                    AdminIdentity.RequireSubject(httpContext),
                    cancellationToken);
                AdminAuditEnrichment.SetAfter(httpContext, ActivityDto(activity));
                return Results.Ok(ActivityDto(activity));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyHighRisk);

        return group;
    }

    internal static object ActivityDto(NewPlayerActivity activity) => new
    {
        activityId = activity.ActivityId,
        activityKey = activity.ActivityKey,
        version = activity.Version,
        state = activity.State.ToString().ToLowerInvariant(),
        title = activity.Title,
        description = activity.Description,
        rewards = new
        {
            merchantCoin = activity.MarketCoin,
            weeklyTicket = activity.SeasonVoucher
        },
        activity.Revision,
        activity.CreatedBy,
        activity.CreatedAt,
        activity.PublishedSeasonId,
        activity.PublishedWorldId,
        activity.PublishedBy,
        activity.PublishedAt,
        activity.ClosedBy,
        activity.ClosedAt
    };

    internal static object AvailabilityDto(NewPlayerActivityAvailability availability) => new
    {
        activity = ActivityDto(availability.Activity),
        claimed = availability.Grant is not null,
        grant = availability.Grant is null ? null : GrantDto(availability.Grant)
    };

    internal static object ClaimDto(NewPlayerActivityClaimResult result) => new
    {
        activity = ActivityDto(result.Activity!),
        grant = GrantDto(result.Grant!),
        balances = new
        {
            merchantCoin = result.Wallet!.MarketCoin.Balance,
            weeklyTicket = result.Wallet.SeasonVoucher.Balance
        },
        result.Created,
        result.IdempotentReplay
    };

    private static object GrantDto(NewPlayerActivityGrant grant) => new
    {
        grant.GrantId,
        grant.ActivityId,
        grant.ActivityKey,
        grant.ActivityVersion,
        grant.SeasonId,
        grant.WorldId,
        rewards = new
        {
            merchantCoin = grant.MarketCoin,
            weeklyTicket = grant.SeasonVoucher
        },
        balancesAfter = new
        {
            merchantCoin = grant.MarketCoinBalanceAfter,
            weeklyTicket = grant.SeasonVoucherBalanceAfter
        },
        grant.ClaimedAt
    };

    private static IResult ToError(Exception exception) => exception switch
    {
        NewPlayerActivityException activityException => Results.Json(
            new ApiError(activityException.Code, activityException.Message),
            statusCode: activityException.StatusCode),
        ArgumentException argumentException => Results.BadRequest(
            new ApiError("INVALID_NEW_PLAYER_ACTIVITY", argumentException.Message)),
        IOException ioException => Results.Json(
            new ApiError("NEW_PLAYER_ACTIVITY_STORE_UNAVAILABLE", ioException.Message),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(
            new ApiError(
                "NEW_PLAYER_ACTIVITY_REQUEST_FAILED",
                "The new-player activity request could not be completed."),
            statusCode: StatusCodes.Status500InternalServerError)
    };

    public sealed record UpdateNewPlayerActivityDraftRequest(
        string Title,
        string Description,
        long MarketCoin,
        long SeasonVoucher,
        long ExpectedRevision);
}

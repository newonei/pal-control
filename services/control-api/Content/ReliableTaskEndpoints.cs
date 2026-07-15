using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ControlApi.Content;

public static class ReliableTaskEndpoints
{
    /// <summary>
    /// Read-only player task surface. Authentication is the existing secure
    /// player-portal session; events themselves are never accepted from HTTP.
    /// Reading also retries any durable pending currency-reward outbox entry.
    /// </summary>
    public static RouteGroupBuilder MapReliableTaskEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/player/me/tasks", async (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            ReliableTaskRuntimeService tasks,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (httpContext.Request.Query.ContainsKey("userId"))
                {
                    return Results.BadRequest(new ApiError(
                        "PLAYER_IDENTITY_OVERRIDE_FORBIDDEN",
                        "Player task identity is derived only from the authenticated portal session."));
                }
                var session = authentication.RequireSession(httpContext);
                var context = await coordinator.GetAccountContextAsync(
                    session.UserId,
                    requireOnline: false,
                    cancellationToken);
                var snapshot = await tasks.GetSnapshotAsync(
                    context.Account.AccountId,
                    context.Season.SeasonId,
                    context.Season.ServerId,
                    cancellationToken);
                return Results.Ok(new
                {
                    snapshot.AccountId,
                    snapshot.SeasonId,
                    snapshot.ServerId,
                    snapshot.RankingPoints,
                    items = snapshot.Tasks.Select(task => new
                    {
                        task.InstanceId,
                        task.Cadence,
                        task.PeriodKey,
                        task.Definition.TaskKey,
                        task.Definition.DisplayName,
                        task.Definition.Description,
                        task.Definition.EventKind,
                        task.Definition.TargetAmount,
                        task.Progress,
                        task.Completed,
                        task.CompletedAt,
                        task.RewardGranted,
                        reward = task.Definition.Reward,
                        task.CurrencyRewardLedgerEntryId,
                        task.RankingRewardEntryId,
                        task.ContentVersionId,
                        task.ContentHash,
                        task.RulesVersion,
                        task.RotationSeed
                    })
                });
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
            catch (ReliableTaskException exception)
            {
                return Results.Json(
                    new ApiError(exception.Code, exception.Message),
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (IOException)
            {
                return Results.Json(
                    new ApiError(
                        "RELIABLE_TASK_STORE_UNAVAILABLE",
                        "Reliable task state is temporarily unavailable."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();

        return api;
    }
}

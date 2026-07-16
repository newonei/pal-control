using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class PlayerNotificationEndpoints
{
    public static RouteGroupBuilder MapPlayerNotificationEndpoints(this RouteGroupBuilder api)
    {
        // The API root is protected by the operator policy. Player routes opt
        // out of that inherited policy and enforce the player session, origin
        // and CSRF boundary explicitly inside each handler.
        var notifications = api.MapGroup("/player/me/notifications")
            .AllowAnonymous();

        notifications.MapGet("", async (
            int? limit,
            HttpContext context,
            PlayerPortalAuthenticationService authentication,
            IExtractionRepository repository,
            PlayerNotificationStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(context);
            var account = await RequirePlayerAccountAsync(
                context,
                authentication,
                repository,
                cancellationToken);
            return Results.Ok(await store.ListForAccountAsync(
                account.AccountId,
                Math.Clamp(limit ?? 50, 1, 100),
                cancellationToken));
        }));

        notifications.MapPost("/{notificationId:guid}/read", async (
            Guid notificationId,
            HttpContext context,
            PlayerPortalAuthenticationService authentication,
            IExtractionRepository repository,
            PlayerNotificationStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(context);
            var session = authentication.RequireSession(context);
            authentication.RequireAllowedOrigin(context);
            authentication.RequireCsrf(context, session);
            var account = await RequirePlayerAccountAsync(
                session,
                repository,
                cancellationToken);
            var result = await store.MarkReadAsync(
                account.AccountId,
                notificationId,
                cancellationToken);
            return result is null
                ? Results.NotFound(new ApiError(
                    "PLAYER_NOTIFICATION_NOT_FOUND",
                    "The notification does not exist."))
                : Results.Ok(result);
        }));

        notifications.MapPost("/read-all", async (
            HttpContext context,
            PlayerPortalAuthenticationService authentication,
            IExtractionRepository repository,
            PlayerNotificationStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
        {
            RejectIdentityOverride(context);
            var session = authentication.RequireSession(context);
            authentication.RequireAllowedOrigin(context);
            authentication.RequireCsrf(context, session);
            var account = await RequirePlayerAccountAsync(
                session,
                repository,
                cancellationToken);
            return Results.Ok(await store.MarkAllReadAsync(
                account.AccountId,
                cancellationToken));
        }));

        return api;
    }

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
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new ApiError(
                "PLAYER_NOTIFICATION_REQUEST_INVALID",
                exception.Message));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or SqliteException)
        {
            return Results.Json(
                new ApiError(
                    "PLAYER_NOTIFICATIONS_UNAVAILABLE",
                    "Player notifications are temporarily unavailable."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<ExtractionAccount> RequirePlayerAccountAsync(
        HttpContext context,
        PlayerPortalAuthenticationService authentication,
        IExtractionRepository repository,
        CancellationToken cancellationToken) => await RequirePlayerAccountAsync(
            authentication.RequireSession(context),
            repository,
            cancellationToken);

    private static async Task<ExtractionAccount> RequirePlayerAccountAsync(
        PlayerPortalSession session,
        IExtractionRepository repository,
        CancellationToken cancellationToken)
    {
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

    internal static void RejectIdentityOverride(HttpContext context)
    {
        foreach (var name in new[]
                 {
                     "accountId", "account_id", "userId", "user_id",
                     "steamId", "steam_id", "playerUid", "player_uid"
                 })
        {
            if (context.Request.Query.ContainsKey(name))
            {
                throw new PlayerPortalException(
                    "PLAYER_IDENTITY_OVERRIDE_FORBIDDEN",
                    "Player notification requests are bound to the authenticated account.",
                    StatusCodes.Status400BadRequest);
            }
        }
    }
}

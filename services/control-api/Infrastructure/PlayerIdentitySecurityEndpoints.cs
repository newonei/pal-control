using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class PlayerIdentitySecurityEndpoints
{
    public static RouteGroupBuilder MapPlayerIdentitySecurityEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/player-identity-security-audit", (
            int? limit,
            PlayerIdentitySecurityStore audit) => Results.Ok(new
            {
                items = audit.List(limit ?? 100)
            }))
            .RequireAuthorization(AdminPolicies.Owner);

        api.MapPost(
            "/admin/player-sessions/revoke",
            async (
                RevokePlayerPortalSessionsRequest request,
                HttpContext context,
                IPlayerIdentityBindingStore bindings,
                PlayerIdentitySecurityService identitySecurity) =>
            {
                var serverId = request.ServerId;
                var playerIdentifier = request.PlayerIdentifier;
                if (string.IsNullOrWhiteSpace(serverId) || serverId.Length > 64 ||
                    string.IsNullOrWhiteSpace(playerIdentifier) ||
                    playerIdentifier.Length is < 3 or > 128 ||
                    serverId.Any(char.IsControl) || playerIdentifier.Any(char.IsControl))
                {
                    return Results.BadRequest(new ApiError(
                        "INVALID_PLAYER_IDENTITY",
                        "serverId and playerIdentifier must identify one configured player binding."));
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var correlationId = CorrelationId(context);
                var subjects = await PlayerIdentityModerationFilter.ResolvePlatformSubjectsAsync(
                    serverId.Trim(),
                    playerIdentifier.Trim(),
                    bindings,
                    identitySecurity,
                    timeout.Token);
                if (subjects.Count == 0)
                {
                    identitySecurity.Audit(
                        PlayerIdentitySecurityEvents.AdministrativeSessionRevocation,
                        "ignored",
                        "no_portal_identity_binding",
                        "normal",
                        null,
                        context.Connection.RemoteIpAddress?.ToString(),
                        correlationId);
                    return Results.NotFound(new ApiError(
                        "PLAYER_IDENTITY_BINDING_NOT_FOUND",
                        "No active platform identity binding or player session matched the identifier."));
                }

                var revokedSessions = 0;
                foreach (var subject in subjects)
                {
                    var revoked = identitySecurity.RevokeAll(subject);
                    revokedSessions += revoked;
                    identitySecurity.Audit(
                        PlayerIdentitySecurityEvents.AdministrativeSessionRevocation,
                        "succeeded",
                        "administrator_requested",
                        "normal",
                        subject,
                        context.Connection.RemoteIpAddress?.ToString(),
                        correlationId,
                        revoked);
                }
                return Results.Ok(new
                {
                    revokedSessions,
                    resolvedSubjects = subjects.Count,
                    correlationId
                });
            })
            .RequireAuthorization(AdminPolicies.Owner);

        return api;
    }

    private static string CorrelationId(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Correlation-ID"];
        return supplied.Count == 1 &&
            Guid.TryParse(supplied[0], out var parsed)
                ? parsed.ToString("D")
                : context.TraceIdentifier;
    }
}

public sealed record RevokePlayerPortalSessionsRequest(
    string? ServerId,
    string? PlayerIdentifier);

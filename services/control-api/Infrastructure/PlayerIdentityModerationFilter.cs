using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static partial class PlayerIdentityModerationFilter
{
    public static RouteGroupBuilder AddPlayerIdentityModerationLink(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (invocationContext, next) =>
        {
            var moderation = GetModerationRequest(invocationContext.HttpContext);
            var result = await next(invocationContext);
            if (moderation is null || !IsSuccessful(result))
            {
                return result;
            }

            var services = invocationContext.HttpContext.RequestServices;
            var bindings = services.GetRequiredService<IPlayerIdentityBindingStore>();
            var identitySecurity = services.GetRequiredService<PlayerIdentitySecurityService>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var httpContext = invocationContext.HttpContext;
            var suppliedCorrelation = httpContext.Request.Headers["X-Correlation-ID"];
            var correlationId = suppliedCorrelation.Count == 1 &&
                Guid.TryParse(suppliedCorrelation[0], out var parsedCorrelation)
                    ? parsedCorrelation.ToString("D")
                    : httpContext.TraceIdentifier;
            var actor = httpContext.User.Identity?.IsAuthenticated == true
                ? AdminIdentity.RequireSubject(httpContext)
                : "unattributed-administrator";
            await ApplyAcceptedModerationAsync(
                moderation.ServerId,
                moderation.PlayerIdentifier,
                moderation.Banned,
                correlationId,
                actor,
                httpContext.Connection.RemoteIpAddress?.ToString(),
                bindings,
                identitySecurity,
                timeout.Token);
            return result;
        });
        return group;
    }

    public static async Task<int> ApplyAcceptedModerationAsync(
        string serverId,
        string playerIdentifier,
        bool banned,
        string correlationId,
        string actor,
        string? sourceIp,
        IPlayerIdentityBindingStore bindings,
        PlayerIdentitySecurityService identitySecurity,
        CancellationToken cancellationToken)
    {
        var subjects = await ResolvePlatformSubjectsAsync(
            serverId,
            playerIdentifier,
            bindings,
            identitySecurity,
            cancellationToken);

        var eventType = banned
            ? PlayerIdentitySecurityEvents.AdministrativeBan
            : PlayerIdentitySecurityEvents.AdministrativeUnban;
        if (subjects.Count == 0)
        {
            identitySecurity.Audit(
                eventType,
                "ignored",
                "no_portal_identity_binding",
                "normal",
                null,
                sourceIp,
                correlationId);
            return 0;
        }

        var actorFingerprint = PlayerIdentitySecurityStore.FingerprintSubject(actor);
        var affectedSessions = 0;
        foreach (var subject in subjects)
        {
            var affected = identitySecurity.ApplyModeration(
                subject,
                banned,
                correlationId,
                actorFingerprint);
            affectedSessions += affected;
            identitySecurity.Audit(
                eventType,
                "succeeded",
                banned ? "sessions_revoked" : "ban_cleared",
                "normal",
                subject,
                sourceIp,
                correlationId,
                affected);
        }
        return affectedSessions;
    }

    public static async Task<IReadOnlyList<string>> ResolvePlatformSubjectsAsync(
        string serverId,
        string playerIdentifier,
        IPlayerIdentityBindingStore bindings,
        PlayerIdentitySecurityService identitySecurity,
        CancellationToken cancellationToken)
    {
        var binding = await bindings.FindActivePlayerIdentityBindingAsync(
            serverId,
            playerIdentifier,
            cancellationToken);
        var subjects = new HashSet<string>(
            identitySecurity.FindSessionSubjects(playerIdentifier),
            StringComparer.OrdinalIgnoreCase);
        if (binding is not null)
        {
            subjects.Add(binding.PlatformSubject);
        }
        if (PlatformSubjectPattern().IsMatch(playerIdentifier))
        {
            subjects.Add(playerIdentifier.ToLowerInvariant());
        }
        return subjects.OrderBy(subject => subject, StringComparer.Ordinal).ToArray();
    }

    private static ModerationRequest? GetModerationRequest(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint endpoint)
        {
            return null;
        }
        var route = endpoint.RoutePattern.RawText ?? string.Empty;
        bool? banned = route.EndsWith(
                "/ban/{playerIdentifier}",
                StringComparison.OrdinalIgnoreCase)
            ? true
            : route.EndsWith(
                "/unban/{userId}",
                StringComparison.OrdinalIgnoreCase)
                ? false
                : null;
        if (banned is null)
        {
            return null;
        }
        var parameterName = banned.Value ? "playerIdentifier" : "userId";
        var serverId = context.Request.RouteValues["serverId"]?.ToString();
        var playerIdentifier = context.Request.RouteValues[parameterName]?.ToString();
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(playerIdentifier))
        {
            return null;
        }
        return new ModerationRequest(
            serverId.Trim(),
            playerIdentifier.Trim(),
            banned.Value);
    }

    private static bool IsSuccessful(object? result)
    {
        if (result is not IStatusCodeHttpResult statusCodeResult)
        {
            return false;
        }
        var statusCode = statusCodeResult.StatusCode ?? StatusCodes.Status200OK;
        return statusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices;
    }

    [GeneratedRegex(
        "^(?:steam|gdk|xbox|xuid|epic)_[a-z0-9]{3,64}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PlatformSubjectPattern();

    private sealed record ModerationRequest(
        string ServerId,
        string PlayerIdentifier,
        bool Banned);
}

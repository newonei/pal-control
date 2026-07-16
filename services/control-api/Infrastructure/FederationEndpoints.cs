using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public static class FederationEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    public static RouteGroupBuilder MapFederationEndpoints(this RouteGroupBuilder api)
    {
        var internalApi = api.MapGroup("/internal/federation")
            .AllowAnonymous();
        internalApi.MapPost("/profile", GetInternalProfileAsync);
        internalApi.MapGet("/health", GetInternalHealth);

        api.MapGet("/player/me/federation", GetPlayerOverviewAsync)
            .AllowAnonymous();

        api.MapGet("/admin/federation/health", GetAdminHealthAsync)
            .RequireAuthorization(AdminPolicies.Viewer);
        api.MapGet("/admin/federation/compatibility-matrix", GetMatrix)
            .RequireAuthorization(AdminPolicies.Viewer);
        return api;
    }

    private static async Task<IResult> GetInternalProfileAsync(
        HttpContext context,
        FederationInternalAuthenticator authentication,
        FederationInternalRequestGuard guard,
        FederationLocalProfileService profiles,
        IOptions<FederationOptions> options,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!options.Value.Enabled)
        {
            return Error("FEDERATION_DISABLED", "Federation is disabled.", 404);
        }
        try
        {
            var rawBody = await ReadProfileRequestBodyAsync(
                context.Request,
                options.Value.MaximumRequestBodyBytes,
                cancellationToken);
            if (!authentication.Authenticate(
                    context.Request,
                    rawBody,
                    out var authenticatedPeer) ||
                authenticatedPeer is null)
            {
                return Error(
                    "FEDERATION_NODE_AUTH_REQUIRED",
                    "A valid, peer-scoped federation request signature is required.",
                    StatusCodes.Status401Unauthorized);
            }
            if (!guard.TryAcquire(
                    $"{authenticatedPeer.CallerServerId}|" +
                    (context.Connection.RemoteIpAddress?.ToString() ?? "unavailable"),
                    out var lease,
                    out var retryAfterSeconds))
            {
                context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
                return Error(
                    "FEDERATION_NODE_RATE_LIMITED",
                    "Federation node request limit reached.",
                    StatusCodes.Status429TooManyRequests);
            }
            using (lease)
            {
                if (context.Request.ContentType is null ||
                    !context.Request.ContentType.StartsWith(
                        "application/json",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Error(
                        "FEDERATION_CONTENT_TYPE_REQUIRED",
                        "Federation profile requests require application/json.",
                        StatusCodes.Status415UnsupportedMediaType);
                }
                var profileRequest = JsonSerializer.Deserialize<FederationProfileRequest>(
                    rawBody,
                    RequestJsonOptions)
                    ?? throw new JsonException("Federation profile request is empty.");
                return Results.Ok(await profiles.GetAsync(
                    profileRequest,
                    authenticatedPeer.CallerServerId,
                    cancellationToken));
            }
        }
        catch (FederationException exception)
        {
            return Error(exception.Code, exception.Message, exception.StatusCode);
        }
        catch (JsonException)
        {
            return Error(
                "FEDERATION_REQUEST_INVALID",
                "Federation profile request JSON is invalid.",
                StatusCodes.Status400BadRequest);
        }
    }

    private static IResult GetInternalHealth(
        HttpContext context,
        FederationInternalAuthenticator authentication,
        FederationInternalRequestGuard guard,
        FederationLocalProfileService profiles,
        IOptions<FederationOptions> options)
    {
        SetNoStore(context);
        if (!options.Value.Enabled)
        {
            return Error("FEDERATION_DISABLED", "Federation is disabled.", 404);
        }
        if (context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding") ||
            !authentication.Authenticate(
                context.Request,
                ReadOnlySpan<byte>.Empty,
                out var authenticatedPeer) ||
            authenticatedPeer is null)
        {
            return Error(
                "FEDERATION_NODE_AUTH_REQUIRED",
                "A valid, peer-scoped federation request signature is required.",
                StatusCodes.Status401Unauthorized);
        }
        if (!guard.TryAcquire(
                $"{authenticatedPeer.CallerServerId}|" +
                (context.Connection.RemoteIpAddress?.ToString() ?? "unavailable"),
                out var lease,
                out var retryAfterSeconds))
        {
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            return Error(
                "FEDERATION_NODE_RATE_LIMITED",
                "Federation node request limit reached.",
                StatusCodes.Status429TooManyRequests);
        }
        using (lease)
        {
            return Results.Ok(profiles.GetHealth());
        }
    }

    private static async Task<IResult> GetPlayerOverviewAsync(
        HttpContext context,
        PlayerPortalAuthenticationService playerAuthentication,
        FederationAggregationService federation,
        IOptions<FederationOptions> options,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        try
        {
            if (!options.Value.Enabled)
            {
                throw new FederationException(
                    "FEDERATION_DISABLED",
                    "Federation is disabled.",
                    StatusCodes.Status404NotFound);
            }
            FederationEndpointSecurity.RejectIdentityOverrides(context.Request);
            var session = playerAuthentication.RequireSession(context);
            var separator = session.UserId.IndexOf('_');
            if (separator is < 1 or > 32)
            {
                throw new FederationException(
                    "FEDERATION_SESSION_IDENTITY_INVALID",
                    "The authenticated session identity cannot be federated.",
                    StatusCodes.Status400BadRequest);
            }
            return Results.Ok(await federation.GetAccountOverviewAsync(
                session.UserId[..separator],
                session.UserId,
                cancellationToken));
        }
        catch (PlayerPortalException exception)
        {
            return Error(exception.Code, exception.Message, exception.StatusCode);
        }
        catch (FederationException exception)
        {
            return Error(exception.Code, exception.Message, exception.StatusCode);
        }
    }

    private static async Task<IResult> GetAdminHealthAsync(
        HttpContext context,
        FederationAggregationService federation,
        CompatibilityMatrixStore matrix,
        IOptions<FederationOptions> options,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var nodes = await federation.GetHealthAsync(cancellationToken);
        return Results.Ok(new
        {
            enabled = options.Value.Enabled,
            localServerId = options.Value.LocalServerId,
            matrixVersion = matrix.Snapshot.Document.MatrixVersion,
            matrixSha256 = matrix.Snapshot.CanonicalSha256,
            nodes
        });
    }

    private static IResult GetMatrix(
        HttpContext context,
        CompatibilityMatrixStore matrix,
        IOptions<FederationOptions> options)
    {
        SetNoStore(context);
        return Results.Ok(new
        {
            matrixVersion = matrix.Snapshot.Document.MatrixVersion,
            matrixSha256 = matrix.Snapshot.CanonicalSha256,
            generatedAt = matrix.Snapshot.Document.GeneratedAt,
            combinations = matrix.Snapshot.Document.Combinations.Select(combination => new
            {
                combination.Id,
                combination.GameVersion,
                combination.SteamBuild,
                combination.PalDefenderVersion,
                combination.Ue4ssCommit,
                combination.NativeProtocolVersion,
                combination.NativeModVersion,
                combination.BridgeAvailability,
                combination.Capabilities,
                status = CompatibilityMatrixValidator.ToWireStatus(combination.Status),
                combination.VerifiedAt,
                evidence = combination.Evidence.Select(item => new
                {
                    item.Kind,
                    item.Source,
                    item.ObservedAt,
                    item.Summary,
                    item.ArtifactSha256
                }),
                combination.Notes,
                configuredServers = options.Value.Nodes
                    .Where(node => string.Equals(
                        node.ExpectedCombinationId,
                        combination.Id,
                        StringComparison.Ordinal))
                    .Select(node => node.ServerId)
            })
        });
    }

    private static async Task<byte[]> ReadProfileRequestBodyAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new FederationException(
                "FEDERATION_REQUEST_OVERSIZED",
                "Federation profile request is too large.",
                StatusCodes.Status413PayloadTooLarge);
        }
        using var output = new MemoryStream(Math.Min(maximumBytes, 2 * 1024));
        var buffer = new byte[1_024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > maximumBytes)
            {
                throw new FederationException(
                    "FEDERATION_REQUEST_OVERSIZED",
                    "Federation profile request is too large.",
                    StatusCodes.Status413PayloadTooLarge);
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }

    private static IResult Error(string code, string message, int statusCode) =>
        Results.Json(new { code, message }, statusCode: statusCode);

}

public static class FederationEndpointSecurity
{
    private static readonly HashSet<string> ForbiddenIdentityHeaders = new(
        [
            "X-Account-Id",
            "X-Federation-Subject",
            "X-Player-Id",
            "X-Player-UserId",
            "X-Steam-Id",
            "X-User-Id"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static void RejectIdentityOverrides(HttpRequest request)
    {
        if (request.Query.Count != 0 ||
            request.ContentLength is > 0 ||
            request.HttpContext.Features
                .Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()
                ?.CanHaveBody == true ||
            request.Headers.ContainsKey("Transfer-Encoding") ||
            request.Headers.Keys.Any(ForbiddenIdentityHeaders.Contains))
        {
            throw new FederationException(
                "FEDERATION_IDENTITY_OVERRIDE_FORBIDDEN",
                "Federated identity is derived only from the authenticated player session.",
                StatusCodes.Status400BadRequest);
        }
    }
}

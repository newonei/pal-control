using System.Security.Cryptography;
using System.Text;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class PlayerPortalEndpoints
{
    public static RouteGroupBuilder MapPlayerPortalEndpoints(this RouteGroupBuilder api)
    {
        var player = api.MapGroup("/player");
        player.AddEndpointFilter(async (invocationContext, next) =>
        {
            invocationContext.HttpContext.Response.Headers.CacheControl = "no-store";
            invocationContext.HttpContext.Response.Headers.Pragma = "no-cache";
            invocationContext.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
            invocationContext.HttpContext.Response.Headers.Vary = "Origin";
            if (HttpMethods.IsPost(invocationContext.HttpContext.Request.Method))
            {
                var authentication = invocationContext.HttpContext.RequestServices
                    .GetRequiredService<PlayerPortalAuthenticationService>();
                if (authentication.Enabled)
                {
                    try
                    {
                        authentication.RequireAllowedOrigin(invocationContext.HttpContext);
                    }
                    catch (Exception exception)
                    {
                        return ToPortalError(
                            invocationContext.HttpContext,
                            exception,
                            anonymous: true);
                    }
                }
            }
            return await next(invocationContext);
        });
        var auth = player.MapGroup("/auth");

        auth.MapPost("/request-code", async (
            RequestPlayerLoginCode request,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var challenge = await authentication.RequestCodeAsync(
                    request.UserId,
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unavailable",
                    cancellationToken);
                return Results.Accepted(value: new
                {
                    challengeId = challenge.ChallengeId,
                    expiresAt = challenge.ExpiresAt,
                    retryAfterSeconds = challenge.RetryAfterSeconds,
                    delivery = "in-game"
                });
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: true);
            }
        });

        auth.MapPost("/verify", (
            VerifyPlayerLoginCode request,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication) =>
        {
            try
            {
                var creation = authentication.Verify(request.ChallengeId, request.Code);
                authentication.AppendSessionCookie(httpContext.Response, creation);
                return Results.Ok(new
                {
                    authenticated = true,
                    userId = creation.Session.UserId,
                    displayName = (string?)null,
                    csrfToken = creation.Session.CsrfToken,
                    expiresAt = creation.Session.ExpiresAt
                });
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: true);
            }
        });

        auth.MapGet("/session", (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication) =>
        {
            try
            {
                var session = authentication.RequireSession(httpContext);
                return Results.Ok(new
                {
                    authenticated = true,
                    userId = session.UserId,
                    displayName = (string?)null,
                    csrfToken = session.CsrfToken,
                    expiresAt = session.ExpiresAt
                });
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        auth.MapPost("/logout", (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication) =>
        {
            try
            {
                var session = authentication.RequireSession(httpContext);
                authentication.RequireCsrf(httpContext, session);
                authentication.Logout(httpContext);
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        var me = player.MapGroup("/me");

        me.MapGet("/overview", async (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            ExtractionSettlementService settlement,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectIdentityOverride(httpContext);
                var session = authentication.RequireSession(httpContext);
                using var operationLease = operationGate.AcquireOperation();
                var context = await coordinator.GetAccountContextAsync(
                    session.UserId,
                    requireOnline: false,
                    cancellationToken);
                var runs = await settlement.ListAsync(
                    context.Account.AccountId,
                    context.Season.SeasonId,
                    1000,
                    cancellationToken);
                return Results.Ok(new
                {
                    userId = context.Account.ExternalUserId,
                    displayName = context.Account.DisplayName,
                    season = ExtractionModeEndpoints.SeasonDto(
                        context.Season,
                        coordinator.GetNextDailyRefresh(DateTimeOffset.UtcNow)),
                    balances = new
                    {
                        merchantCoin = context.Wallet.MarketCoin.Balance,
                        weeklyTicket = context.Wallet.SeasonVoucher.Balance
                    },
                    seasonStats = new
                    {
                        successfulRuns = runs.Count(run =>
                            run.State == ExtractionSettlementState.Settled),
                        failedRuns = runs.Count(run => run.State == ExtractionSettlementState.Failed),
                        uncertainRuns = runs.Count(run =>
                            run.State == ExtractionSettlementState.Uncertain),
                        extractedValue = runs
                            .Where(run => run.State == ExtractionSettlementState.Settled)
                            .Sum(run => run.TotalValue)
                    },
                    online = context.Online,
                    persistence = "sqlite-event-store"
                });
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        me.MapGet("/extraction-zones", async (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionSettlementService settlement,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectIdentityOverride(httpContext);
                var session = authentication.RequireSession(httpContext);
                var snapshot = await settlement.GetPlayerZoneSnapshotAsync(
                    session.UserId,
                    cancellationToken);
                return Results.Ok(new
                {
                    status = snapshot.Status,
                    statusMessage = snapshot.StatusMessage,
                    sampledAt = snapshot.SampledAt,
                    positionSource = "paldefender-map-location",
                    coordinateTransform = new
                    {
                        mapUnits = "paldefender-map-units",
                        worldUnits = "unreal-centimeters",
                        scale = ExtractionCoordinateTransform.Scale,
                        worldXOffset = ExtractionCoordinateTransform.WorldXOffset,
                        worldYOffset = ExtractionCoordinateTransform.WorldYOffset,
                        formula = "worldX = mapY * scale + worldXOffset; worldY = mapX * scale + worldYOffset"
                    },
                    player = new
                    {
                        online = snapshot.Online == true,
                        positionAvailable = snapshot.PlayerMapPosition is not null,
                        mapPosition = snapshot.PlayerMapPosition,
                        worldPosition = snapshot.PlayerWorldPosition,
                        inRange = snapshot.InsideAnyZone == true,
                        insideAnyZone = snapshot.InsideAnyZone,
                        activeZoneId = snapshot.ActiveZoneId,
                        nearestZoneId = snapshot.NearestZoneId
                    },
                    zones = snapshot.Zones
                });
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        me.MapGet("/catalog", async (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectIdentityOverride(httpContext);
                var session = authentication.RequireSession(httpContext);
                using var operationLease = operationGate.AcquireOperation();
                var context = await coordinator.GetAccountContextAsync(
                    session.UserId,
                    requireOnline: false,
                    cancellationToken);
                var products = await coordinator.ListProductsAsync(cancellationToken);
                var orders = await coordinator.ListOrdersAsync(
                    context.Account.AccountId,
                    context.Season.SeasonId,
                    1000,
                    cancellationToken);
                Dictionary<Guid, int> purchasedByProduct = [];
                foreach (var line in orders
                             .Where(order => order.State != ShopOrderState.Refunded)
                             .SelectMany(order => order.Lines))
                {
                    purchasedByProduct[line.ProductId] = checked(
                        purchasedByProduct.GetValueOrDefault(line.ProductId) + line.Quantity);
                }
                var revisionSource = string.Join('|', products.Select(product =>
                    $"{product.ProductId:N}:{product.Revision}:{product.Active}"));
                var revision = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(revisionSource))).ToLowerInvariant();
                return Results.Ok(new
                {
                    revision,
                    items = products.Select(product => ExtractionModeEndpoints.ProductDto(
                        product,
                        purchasedByProduct.GetValueOrDefault(product.ProductId))).ToArray()
                });
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        me.MapGet("/orders", async (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectIdentityOverride(httpContext);
                var session = authentication.RequireSession(httpContext);
                using var operationLease = operationGate.AcquireOperation();
                var context = await coordinator.GetAccountContextAsync(
                    session.UserId,
                    requireOnline: false,
                    cancellationToken);
                var orders = await coordinator.ListOrdersAsync(
                    context.Account.AccountId,
                    context.Season.SeasonId,
                    100,
                    cancellationToken);
                return Results.Ok(new
                {
                    items = orders.Select(ExtractionModeEndpoints.OrderDto).ToArray()
                });
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        me.MapGet("/ledger", async (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectIdentityOverride(httpContext);
                var session = authentication.RequireSession(httpContext);
                using var operationLease = operationGate.AcquireOperation();
                var context = await coordinator.GetAccountContextAsync(
                    session.UserId,
                    requireOnline: false,
                    cancellationToken);
                var entries = await coordinator.ListLedgerAsync(
                    context.Account.AccountId,
                    context.Season.SeasonId,
                    100,
                    cancellationToken);
                return Results.Ok(new
                {
                    items = entries.Select(entry => new
                    {
                        entryId = entry.EntryId,
                        currency = ExtractionModeCoordinator.ToClientCurrency(entry.Currency),
                        amount = entry.Delta,
                        balanceAfter = entry.BalanceAfter,
                        reason = entry.Reason,
                        referenceId = entry.ReferenceId,
                        createdAt = entry.CreatedAt
                    }).ToArray()
                });
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        me.MapGet("/runs", async (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            ExtractionSettlementService settlement,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectIdentityOverride(httpContext);
                var session = authentication.RequireSession(httpContext);
                using var operationLease = operationGate.AcquireOperation();
                var context = await coordinator.GetAccountContextAsync(
                    session.UserId,
                    requireOnline: false,
                    cancellationToken);
                var runs = await settlement.ListAsync(
                    context.Account.AccountId,
                    context.Season.SeasonId,
                    100,
                    cancellationToken);
                var settlementProbe = await settlement.ProbeSettlementAsync(cancellationToken);
                return Results.Ok(new
                {
                    items = runs.Select(ExtractionModeEndpoints.RunDto).ToArray(),
                    settlementEnabled = settlementProbe.Success,
                    reason = settlementProbe.Success
                        ? null
                        : settlementProbe.ErrorMessage ?? "撤离结算当前不可用。"
                });
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        me.MapPost("/orders", async (
            CreatePlayerPortalOrder request,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionModeCoordinator coordinator,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectIdentityOverride(httpContext);
                var session = authentication.RequireSession(httpContext);
                authentication.RequireCsrf(httpContext, session);
                var idempotencyKey = RequireIdempotencyKey(httpContext.Request);
                if (request.Quantity is < 1 or > 99)
                {
                    throw new PlayerPortalException(
                        "INVALID_QUANTITY",
                        "购买数量必须在 1 到 99 之间。",
                        StatusCodes.Status400BadRequest);
                }
                using var operationLease = operationGate.AcquireOperation();
                var context = await coordinator.GetAccountContextAsync(
                    session.UserId,
                    requireOnline: true,
                    cancellationToken);
                var product = await coordinator.FindProductAsync(
                    request.ProductId,
                    cancellationToken);
                var purchase = await coordinator.PurchaseAsync(
                    context,
                    product,
                    request.Quantity,
                    idempotencyKey,
                    cancellationToken);
                if (purchase.ErrorCode is not null || purchase.Order is null)
                {
                    return ExtractionModeEndpoints.PurchaseError(purchase);
                }
                return Results.Ok(ExtractionModeEndpoints.OrderDto(purchase.Order));
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        me.MapPost("/runs/quote", async (
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionSettlementService settlement,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectIdentityOverride(httpContext);
                var session = authentication.RequireSession(httpContext);
                authentication.RequireCsrf(httpContext, session);
                using var operationLease = operationGate.AcquireOperation();
                var run = await settlement.QuoteAsync(session.UserId, cancellationToken);
                return Results.Ok(ExtractionModeEndpoints.QuoteDto(run));
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        me.MapPost("/runs/{runId:guid}/settle", async (
            Guid runId,
            HttpContext httpContext,
            PlayerPortalAuthenticationService authentication,
            ExtractionSettlementService settlement,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectIdentityOverride(httpContext);
                var session = authentication.RequireSession(httpContext);
                authentication.RequireCsrf(httpContext, session);
                var idempotencyKey = RequireIdempotencyKey(httpContext.Request);
                using var operationLease = operationGate.AcquireOperation();
                var run = await settlement.SettleAsync(
                    runId,
                    session.UserId,
                    idempotencyKey,
                    cancellationToken);
                return Results.Ok(ExtractionModeEndpoints.RunDto(run));
            }
            catch (Exception exception)
            {
                return ToPortalError(httpContext, exception, anonymous: false);
            }
        });

        return player;
    }

    private static string RequireIdempotencyKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) ||
            values.Count != 1 ||
            values[0] is not { Length: >= 8 } key ||
            key.Length > 128 ||
            key.Any(char.IsControl))
        {
            throw new PlayerPortalException(
                "IDEMPOTENCY_KEY_REQUIRED",
                "请求必须提供长度为 8 到 128 的有效幂等键。",
                StatusCodes.Status400BadRequest);
        }
        return key;
    }

    private static void RejectIdentityOverride(HttpContext context)
    {
        if (context.Request.Query.ContainsKey("userId"))
        {
            throw new PlayerPortalException(
                "PLAYER_IDENTITY_OVERRIDE_FORBIDDEN",
                "玩家身份只能由已验证的登录会话确定。",
                StatusCodes.Status400BadRequest);
        }
    }

    private static IResult ToPortalError(
        HttpContext context,
        Exception exception,
        bool anonymous)
    {
        if (exception is PlayerPortalException portalException)
        {
            if (portalException.RetryAfterSeconds is int retryAfter)
            {
                context.Response.Headers.RetryAfter = retryAfter.ToString();
            }
            return Results.Json(
                new ApiError(portalException.Code, portalException.Message),
                statusCode: portalException.StatusCode);
        }

        if (!anonymous)
        {
            return exception switch
            {
                ExtractionModeException modeException => Results.Json(
                    new ApiError(modeException.Code, modeException.Message),
                    statusCode: modeException.StatusCode),
                ArgumentException => Results.BadRequest(
                    new ApiError(
                        "INVALID_EXTRACTION_REQUEST",
                        "玩家商城请求参数无效。")),
                KeyNotFoundException => Results.NotFound(
                    new ApiError(
                        "EXTRACTION_RESOURCE_NOT_FOUND",
                        "请求的玩家商城资源不存在。")),
                InvalidOperationException => Results.Json(
                    new ApiError(
                        "EXTRACTION_STATE_CONFLICT",
                        "玩家商城当前状态不允许执行该操作。"),
                    statusCode: StatusCodes.Status409Conflict),
                IOException => Results.Json(
                    new ApiError(
                        "EXTRACTION_STORE_UNAVAILABLE",
                        "玩家商城存储暂时不可用，请稍后重试。"),
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Json(
                    new ApiError(
                        "PLAYER_PORTAL_REQUEST_FAILED",
                        "玩家商城请求处理失败。"),
                    statusCode: StatusCodes.Status500InternalServerError)
            };
        }

        return Results.Json(
            new ApiError(
                "PLAYER_AUTH_REQUEST_FAILED",
                "玩家身份验证请求暂时无法完成。"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    public sealed record RequestPlayerLoginCode(string? UserId);

    public sealed record VerifyPlayerLoginCode(string? ChallengeId, string? Code);

    public sealed record CreatePlayerPortalOrder(Guid ProductId, int Quantity);
}

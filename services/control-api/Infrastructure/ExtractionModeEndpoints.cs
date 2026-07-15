using System.Security.Cryptography;
using System.Text;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class ExtractionModeEndpoints
{
    public static RouteGroupBuilder MapExtractionModeEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/extraction");

        group.MapGet("/capabilities", async (
            ExtractionCommerceService commerce,
            ExtractionOperationGate operationGate,
            EconomySafetyGate safetyGate,
            PalDefenderCommandQueue deliveryCommands,
            ExtractionSettlementQueue settlementQueue,
            CancellationToken cancellationToken) =>
        {
            var gate = operationGate.Current;
            var deliveryQueue = await deliveryCommands.GetEconomyLoadAsync(cancellationToken);
            var backlog = (await commerce.ListBlockingOrdersAsync(cancellationToken)).Count;
            var purchase = await safetyGate.EvaluateAsync(
                EconomyWriteFeature.Purchase,
                context: null,
                deliveryQueue with
                {
                    Backlog = backlog,
                    BacklogCapacity = safetyGate.DeliveryBacklogCapacity
                },
                cancellationToken);
            var resourceExchange = await safetyGate.EvaluateAsync(
                EconomyWriteFeature.ResourceExchange,
                context: null,
                new EconomyQueueSnapshot(
                    settlementQueue.IsAccepting,
                    settlementQueue.AdmittedCount,
                    settlementQueue.Capacity),
                cancellationToken);

            return Results.Ok(new
            {
                gameplayMode = ExtractionModeCoordinator.GameplayMode,
                readReady = commerce.IsReady,
                maintenance = gate.Maintenance,
                writes = new
                {
                    purchase = new
                    {
                        purchase.Enabled,
                        purchase.Blockers,
                        circuit = purchase.Circuit
                    },
                    resourceExchange = new
                    {
                        resourceExchange.Enabled,
                        resourceExchange.Blockers,
                        circuit = resourceExchange.Circuit
                    }
                },
                evaluatedAt = purchase.EvaluatedAt > resourceExchange.EvaluatedAt
                    ? purchase.EvaluatedAt
                    : resourceExchange.EvaluatedAt
            });
        });

        group.MapPut("/admin/safety-gate/{feature}", async (
            string feature,
            EconomyCircuitUpdateRequest request,
            HttpContext httpContext,
            EconomySafetyGate safetyGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var parsedFeature = feature.Trim().ToLowerInvariant() switch
                {
                    "purchase" => EconomyWriteFeature.Purchase,
                    "resource-exchange" => EconomyWriteFeature.ResourceExchange,
                    _ => throw new ExtractionModeException(
                        "INVALID_ECONOMY_WRITE_FEATURE",
                        "feature 只能是 purchase 或 resource-exchange。",
                        StatusCodes.Status400BadRequest)
                };
                if (string.IsNullOrWhiteSpace(request.Reason))
                {
                    return Results.BadRequest(new ApiError(
                        "ECONOMY_CIRCUIT_REASON_REQUIRED",
                        "切换经济熔断器必须填写原因。"));
                }
                AdminAuditEnrichment.SetBefore(httpContext, safetyGate.Current);
                var state = await safetyGate.SetCircuitAsync(
                    parsedFeature,
                    request.WritesEnabled,
                    request.Reason,
                    AdminIdentity.RequireSubject(httpContext),
                    cancellationToken);
                AdminAuditEnrichment.SetAfter(httpContext, state);
                return Results.Ok(state);
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyHighRisk);

        group.MapGet("/overview", async (
            string userId,
            ExtractionModeCoordinator coordinator,
            ExtractionSettlementService settlement,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var operationLease = operationGate.Current.Maintenance
                    ? null
                    : operationGate.AcquireOperation();
                var context = operationLease is null
                    ? await coordinator.GetExistingAccountContextAsync(userId, cancellationToken)
                    : await coordinator.GetAccountContextAsync(
                        userId,
                        requireOnline: false,
                        cancellationToken);
                var statistics = await settlement.GetSeasonStatisticsAsync(
                    context.Account.AccountId,
                    context.Season.SeasonId,
                    cancellationToken);
                return Results.Ok(new
                {
                    gameplayMode = ExtractionModeCoordinator.GameplayMode,
                    userId = context.Account.ExternalUserId,
                    displayName = context.Account.DisplayName,
                    season = SeasonDto(context.Season, coordinator.GetNextDailyRefresh(DateTimeOffset.UtcNow)),
                    balances = new
                    {
                        merchantCoin = context.Wallet.MarketCoin.Balance,
                        weeklyTicket = context.Wallet.SeasonVoucher.Balance
                    },
                    seasonStats = new
                    {
                        settledExchanges = statistics.SettledCount,
                        failedSettlements = statistics.FailedCount,
                        uncertainSettlements = statistics.UncertainCount,
                        exchangedValue = statistics.SettledTotalValue,
                        // Compatibility aliases retained for older clients. These
                        // count settlement outcomes, never raid/action outcomes.
                        successfulRuns = statistics.SettledCount,
                        failedRuns = statistics.FailedCount,
                        uncertainRuns = statistics.UncertainCount,
                        extractedValue = statistics.SettledTotalValue
                    },
                    online = context.Online,
                    persistence = "sqlite-event-store"
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapGet("/catalog", async (
            string? userId,
            ExtractionModeCoordinator coordinator,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var operationLease = operationGate.Current.Maintenance
                    ? null
                    : operationGate.AcquireOperation();
                var products = await coordinator.ListProductsAsync(cancellationToken);
                var content = await coordinator.GetCurrentContentAsync(cancellationToken);
                Dictionary<Guid, int> purchasedByProduct = [];
                Dictionary<Guid, long> globallyPurchasedByProduct = [];
                Guid? seasonId = null;
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    var context = operationLease is null
                        ? await coordinator.GetExistingAccountContextAsync(userId, cancellationToken)
                        : await coordinator.GetAccountContextAsync(
                            userId,
                            requireOnline: false,
                            cancellationToken);
                    var orders = await coordinator.ListOrdersAsync(
                        context.Account.AccountId,
                        context.Season.SeasonId,
                        1000,
                        cancellationToken);
                    foreach (var line in orders
                                 .Where(order => order.State != ShopOrderState.Refunded)
                                 .SelectMany(order => order.Lines))
                    {
                        purchasedByProduct[line.ProductId] = checked(
                            purchasedByProduct.GetValueOrDefault(line.ProductId) + line.Quantity);
                    }
                    seasonId = context.Season.SeasonId;
                }
                if (seasonId is Guid currentSeasonId)
                {
                    foreach (var product in products.Where(product => product.GlobalStock is not null))
                    {
                        globallyPurchasedByProduct[product.ProductId] =
                            await coordinator.GetGlobalPurchasedQuantityAsync(
                                currentSeasonId,
                                product.Sku,
                                cancellationToken);
                    }
                }
                var revisionSource = string.Join('|', products.Select(product =>
                    $"{product.ProductId:N}:{product.Revision}:{product.Active}"));
                var revision = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(revisionSource))).ToLowerInvariant();
                return Results.Ok(new
                {
                    revision,
                    contentVersionId = content?.Version.VersionId,
                    contentHash = content?.Version.ContentHash,
                    businessDate = content?.Version.BusinessDate,
                    rulesVersion = content?.Version.RulesVersion,
                    rotation = content?.Rotation,
                    items = products.Select(product => ProductDto(
                        product,
                        purchasedByProduct.GetValueOrDefault(product.ProductId),
                        globallyPurchasedByProduct.GetValueOrDefault(product.ProductId))).ToArray()
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapGet("/orders", async (
            string userId,
            ExtractionModeCoordinator coordinator,
            ExtractionDeliveryReceiptStore deliveryReceipts,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var operationLease = operationGate.Current.Maintenance
                    ? null
                    : operationGate.AcquireOperation();
                var context = operationLease is null
                    ? await coordinator.GetExistingAccountContextAsync(userId, cancellationToken)
                    : await coordinator.GetAccountContextAsync(
                        userId,
                        requireOnline: false,
                        cancellationToken);
                var orders = await coordinator.ListOrdersAsync(
                    context.Account.AccountId,
                    context.Season.SeasonId,
                    100,
                    cancellationToken);
                return Results.Ok(new
                {
                    items = await OrderDtosAsync(orders, deliveryReceipts, cancellationToken)
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapGet("/ledger", async (
            string userId,
            ExtractionModeCoordinator coordinator,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var operationLease = operationGate.Current.Maintenance
                    ? null
                    : operationGate.AcquireOperation();
                var context = operationLease is null
                    ? await coordinator.GetExistingAccountContextAsync(userId, cancellationToken)
                    : await coordinator.GetAccountContextAsync(
                        userId,
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
                        reason = LedgerReason(entry),
                        referenceId = entry.ReferenceId,
                        createdAt = entry.CreatedAt
                    }).ToArray()
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapGet("/runs", async (
            string userId,
            ExtractionModeCoordinator coordinator,
            ExtractionSettlementService settlement,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var operationLease = operationGate.Current.Maintenance
                    ? null
                    : operationGate.AcquireOperation();
                var context = operationLease is null
                    ? await coordinator.GetExistingAccountContextAsync(userId, cancellationToken)
                    : await coordinator.GetAccountContextAsync(
                        userId,
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
                    items = runs.Select(RunDto).ToArray(),
                    settlementEnabled = settlementProbe.Success,
                    reason = settlementProbe.Success
                        ? null
                        : settlementProbe.ErrorMessage ??
                          "Native 稳定扣物/持久化证据不可用；未证明资源已移除前绝不入账。"
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapPost("/runs/quote", async (
            CreateExtractionQuoteRequest request,
            ExtractionSettlementService settlement,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var operationLease = operationGate.AcquireOperation();
                var run = await settlement.QuoteAsync(request.UserId, cancellationToken);
                return Results.Ok(QuoteDto(run));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapPost("/runs/{runId:guid}/settle", async (
            Guid runId,
            HttpRequest httpRequest,
            SettleExtractionRunRequest request,
            ExtractionSettlementQueue settlementQueue,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var operationLease = operationGate.AcquireOperation();
                if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var keyValues) ||
                    keyValues.Count != 1 ||
                    keyValues[0] is not { Length: >= 8 } idempotencyKey ||
                    idempotencyKey.Length > 128 ||
                    idempotencyKey.Any(char.IsControl))
                {
                    return Results.BadRequest(new ApiError(
                        "IDEMPOTENCY_KEY_REQUIRED",
                        "资源兑换结算必须提供 8 到 128 个字符的 Idempotency-Key。"));
                }
                var run = await settlementQueue.EnqueueAsync(
                    runId,
                    request.UserId,
                    idempotencyKey,
                    cancellationToken);
                return Results.Ok(RunDto(run));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapPost("/orders", async (
            HttpRequest httpRequest,
            CreateExtractionOrderRequest request,
            ExtractionModeCoordinator coordinator,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var operationLease = operationGate.AcquireOperation();
                if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var keyValues) ||
                    keyValues.Count != 1 ||
                    keyValues[0] is not { Length: >= 8 } idempotencyKey ||
                    idempotencyKey.Length > 128 ||
                    idempotencyKey.Any(char.IsControl))
                {
                    return Results.BadRequest(new ApiError(
                        "IDEMPOTENCY_KEY_REQUIRED",
                        "Idempotency-Key 必须是 8 到 128 个非控制字符。"));
                }
                if (request.Quantity is < 1 or > 99)
                {
                    return Results.BadRequest(new ApiError(
                        "INVALID_QUANTITY",
                        "购买数量必须在 1 到 99 之间。"));
                }
                var context = await coordinator.GetAccountContextAsync(
                    request.UserId,
                    requireOnline: true,
                    cancellationToken);
                var product = await coordinator.ResolveProductOfferAsync(
                    request.ProductId,
                    request.ContentVersionId,
                    request.ContentHash,
                    request.Sku,
                    cancellationToken);
                var purchase = await coordinator.PurchaseAsync(
                    context,
                    product,
                    request.Quantity,
                    idempotencyKey,
                    AdminIdentity.RequireSubject(httpRequest.HttpContext),
                    cancellationToken);
                if (purchase.ErrorCode is not null || purchase.Order is null)
                {
                    return PurchaseError(purchase);
                }
                return Results.Ok(OrderDto(purchase.Order));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapPost("/admin/wallet-adjustments", async (
            HttpRequest httpRequest,
            ExtractionWalletAdjustmentRequest request,
            ExtractionModeCoordinator coordinator,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var operationLease = operationGate.AcquireOperation();
                if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var keyValues) ||
                    keyValues.Count != 1 ||
                    keyValues[0] is not { Length: >= 8 } idempotencyKey)
                {
                    return Results.BadRequest(new ApiError(
                        "IDEMPOTENCY_KEY_REQUIRED",
                        "管理员调账必须提供 Idempotency-Key。"));
                }
                if (request.Delta == 0 || string.IsNullOrWhiteSpace(request.Reason))
                {
                    return Results.BadRequest(new ApiError(
                        "INVALID_WALLET_ADJUSTMENT",
                        "delta 不能为 0，并且必须填写原因。"));
                }
                var currency = request.Currency switch
                {
                    "merchantCoin" => ExtractionCurrency.MarketCoin,
                    "weeklyTicket" => ExtractionCurrency.SeasonVoucher,
                    _ => throw new ExtractionModeException(
                        "INVALID_CURRENCY",
                        "currency 只能是 merchantCoin 或 weeklyTicket。",
                        StatusCodes.Status400BadRequest)
                };
                var context = await coordinator.GetAccountContextAsync(
                    request.UserId,
                    requireOnline: false,
                    cancellationToken);
                var beforeBalance = currency == ExtractionCurrency.SeasonVoucher
                    ? context.Wallet.SeasonVoucher
                    : context.Wallet.MarketCoin;
                AdminAuditEnrichment.SetBefore(httpRequest.HttpContext, new
                {
                    accountId = context.Account.AccountId,
                    currency = ExtractionModeCoordinator.ToClientCurrency(currency),
                    balance = beforeBalance.Balance,
                    revision = beforeBalance.Revision
                });
                var adjustment = await coordinator.AdjustWalletAsync(
                    context,
                    currency,
                    request.Delta,
                    request.Reason.Trim(),
                    idempotencyKey,
                    AdminIdentity.RequireSubject(httpRequest.HttpContext),
                    cancellationToken);
                if (adjustment.ErrorCode is not null || adjustment.Balance is null)
                {
                    return Results.Json(
                        new ApiError(
                            adjustment.ErrorCode ?? "WALLET_ADJUSTMENT_FAILED",
                            adjustment.ErrorMessage ?? "钱包调账失败。"),
                        statusCode: StatusCodes.Status409Conflict);
                }
                AdminAuditEnrichment.SetAfter(httpRequest.HttpContext, new
                {
                    accountId = context.Account.AccountId,
                    currency = ExtractionModeCoordinator.ToClientCurrency(currency),
                    balance = adjustment.Balance.Balance,
                    revision = adjustment.Balance.Revision,
                    ledgerEntryId = adjustment.LedgerEntry?.EntryId,
                    created = adjustment.Created
                });
                return Results.Ok(new
                {
                    accountId = context.Account.AccountId,
                    currency = ExtractionModeCoordinator.ToClientCurrency(currency),
                    balance = adjustment.Balance.Balance,
                    ledgerEntry = adjustment.LedgerEntry,
                    created = adjustment.Created
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyHighRisk);

        group.MapPost("/admin/orders/{orderId:guid}/reconcile", async (
            Guid orderId,
            HttpRequest httpRequest,
            ExtractionOrderReconciliationRequest request,
            ExtractionCommerceService commerce,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!operationGate.Current.Maintenance)
                {
                    return Results.Conflict(new ApiError(
                        "RECONCILIATION_MAINTENANCE_REQUIRED",
                        "人工终结不确定订单前必须先进入维护状态。"));
                }
                if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var keyValues) ||
                    keyValues.Count != 1 ||
                    keyValues[0] is not { Length: >= 8 } idempotencyKey ||
                    idempotencyKey.Length > 128 ||
                    idempotencyKey.Any(char.IsControl))
                {
                    return Results.BadRequest(new ApiError(
                        "IDEMPOTENCY_KEY_REQUIRED",
                        "人工订单对账必须提供 8 至 128 字符的 Idempotency-Key。"));
                }
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Resolution);
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Confirmation);
                var resolution = request.Resolution.Trim().ToLowerInvariant();
                var expectedConfirmation = $"ORDER-{orderId:N}-{resolution.ToUpperInvariant()}";
                if (!string.Equals(request.Confirmation, expectedConfirmation, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new ApiError(
                        "RECONCILIATION_CONFIRMATION_INVALID",
                        $"confirmation 必须精确填写 {expectedConfirmation}。"));
                }

                var beforeOrder = await commerce.GetOrderAsync(orderId, cancellationToken);
                AdminAuditEnrichment.SetBefore(
                    httpRequest.HttpContext,
                    beforeOrder is null ? null : OrderDto(beforeOrder));

                ShopOrder? order;
                if (resolution == "delivered")
                {
                    var result = await commerce.MarkUncertainOrderDeliveredAsync(
                        orderId,
                        AdminIdentity.RequireSubject(httpRequest.HttpContext),
                        request.Reason,
                        cancellationToken);
                    if (result.ErrorCode is not null || result.Order is null)
                    {
                        return Results.Conflict(new ApiError(
                            result.ErrorCode ?? "RECONCILIATION_FAILED",
                            result.ErrorMessage ?? "无法终结不确定订单。"));
                    }
                    order = result.Order;
                }
                else if (resolution == "refund")
                {
                    var result = await commerce.RefundUncertainOrderAsync(
                        orderId,
                        idempotencyKey,
                        AdminIdentity.RequireSubject(httpRequest.HttpContext),
                        request.Reason,
                        cancellationToken);
                    if (result.ErrorCode is not null || result.Order is null)
                    {
                        return Results.Conflict(new ApiError(
                            result.ErrorCode ?? "RECONCILIATION_FAILED",
                            result.ErrorMessage ?? "无法退款并终结不确定订单。"));
                    }
                    order = result.Order;
                }
                else
                {
                    return Results.BadRequest(new ApiError(
                        "INVALID_RECONCILIATION_RESOLUTION",
                        "resolution 只能是 delivered 或 refund。"));
                }
                AdminAuditEnrichment.SetAfter(httpRequest.HttpContext, OrderDto(order));
                return Results.Ok(OrderDto(order));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyHighRisk);

        group.MapPost("/admin/runs/{runId:guid}/reconcile", async (
            Guid runId,
            HttpContext httpContext,
            ExtractionRunReconciliationRequest request,
            ExtractionSettlementService settlement,
            ExtractionRunStore runStore,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!operationGate.Current.Maintenance)
                {
                    return Results.Conflict(new ApiError(
                        "RECONCILIATION_MAINTENANCE_REQUIRED",
                        "人工终结不确定资源兑换前必须先进入维护状态。"));
                }
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Resolution);
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Confirmation);
                var resolution = request.Resolution.Trim().ToLowerInvariant();
                var expectedConfirmation = $"RUN-{runId:N}-{resolution.ToUpperInvariant()}";
                if (!string.Equals(request.Confirmation, expectedConfirmation, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new ApiError(
                        "RECONCILIATION_CONFIRMATION_INVALID",
                        $"confirmation 必须精确填写 {expectedConfirmation}。"));
                }
                var beforeRun = await runStore.GetAsync(runId, cancellationToken);
                AdminAuditEnrichment.SetBefore(
                    httpContext,
                    beforeRun is null ? null : RunDto(beforeRun));
                var run = await settlement.ReconcileUncertainAsync(
                    runId,
                    resolution,
                    request.Reason,
                    AdminIdentity.RequireSubject(httpContext),
                    cancellationToken);
                AdminAuditEnrichment.SetAfter(httpContext, RunDto(run));
                return Results.Ok(RunDto(run));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyHighRisk);

        group.MapGet("/admin/settlement/status", GetSettlementStatusAsync);
        // Deprecated compatibility alias. It intentionally returns the same
        // adapter-neutral schema because production settlement is Native; no
        // new caller should infer an RCON transport from this legacy path.
        group.MapGet("/admin/rcon/status", GetSettlementStatusAsync);

        group.MapPost("/admin/rollover/maintenance", async (
            HttpContext httpContext,
            RolloverMaintenanceRequest request,
            ExtractionOperationGate operationGate,
            WeeklyRolloverStateStore rolloverStore,
            Microsoft.Extensions.Options.IOptions<ExtractionModeOptions> extractionOptions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!request.Maintenance && await rolloverStore.FindIncompleteAsync(
                        extractionOptions.Value.ServerId,
                        cancellationToken) is { } incomplete)
                {
                    return Results.Conflict(new ApiError(
                        "ROLLOVER_REOPEN_STEP_REQUIRED",
                        $"Persistent rollover '{incomplete.OperationId:D}' must complete its " +
                        $"'{incomplete.CurrentStep}' step before maintenance can reopen."));
                }
                AdminAuditEnrichment.SetBefore(httpContext, operationGate.Current);
                var state = await operationGate.SetAsync(
                    request.Maintenance,
                    request.Reason,
                    AdminIdentity.RequireSubject(httpContext),
                    cancellationToken);
                AdminAuditEnrichment.SetAfter(httpContext, state);
                return Results.Ok(state);
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.SeasonHighRisk);

        group.MapGet("/admin/rollover/preflight", async (
            ExtractionModeCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await coordinator.GetRolloverPreflightAsync(cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapGet("/admin/rollover/readiness", async (
            ExtractionCommerceService commerce,
            ExtractionRunStore runStore,
            ExtractionOperationGate operationGate,
            CancellationToken cancellationToken) =>
        {
            var blockers = await GetRolloverBlockersAsync(
                commerce,
                runStore,
                operationGate,
                cancellationToken);
            return Results.Ok(new
            {
                maintenance = blockers.GateState,
                readyForWorldSwitch = blockers.ReadyForWorldSwitch,
                activeOperations = blockers.ActiveOperations,
                blockingOrders = blockers.Orders.Select(order => new
                {
                    orderId = order.OrderId,
                    state = order.State.ToString(),
                    updatedAt = order.UpdatedAt
                }).ToArray(),
                blockingRuns = blockers.Runs.Select(run => new
                {
                    runId = run.RunId,
                    state = run.State.ToString(),
                    updatedAt = run.UpdatedAt
                }).ToArray()
            });
        });

        group.MapPost("/admin/rollover/commit", async (
            HttpContext httpContext,
            RolloverCommitRequest request,
            ExtractionModeCoordinator coordinator,
            ExtractionCommerceService commerce,
            ExtractionRunStore runStore,
            ExtractionOperationGate operationGate,
            WeeklyRolloverStateStore rolloverStore,
            SeasonSettlementJobStore seasonJobs,
            Microsoft.Extensions.Options.IOptions<ExtractionModeOptions> extractionOptions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                // The normal coordinator read deliberately rejects an active
                // season whose WorldId is still null. The one exception is the
                // first controlled world binding: initialization has already
                // created exactly one active season, maintenance is closed to
                // players, and no rollover state machine exists yet. Read the
                // repository directly so this bootstrap state is observable;
                // every subsequent commit still requires the durable rollover
                // state machine below.
                var serverId = extractionOptions.Value.ServerId;
                var currentSeason = await commerce.GetActiveSeasonAsync(
                        serverId,
                        cancellationToken)
                    ?? throw new ExtractionModeException(
                        "ROLLOVER_ACTIVE_SEASON_REQUIRED",
                        "A rollover commit requires one active economy season.",
                        StatusCodes.Status409Conflict);
                var initialWorldBinding = currentSeason.WorldId is null;
                if (initialWorldBinding)
                {
                    var seasons = await commerce.ListSeasonsAsync(serverId, cancellationToken);
                    if (seasons.Count != 1 ||
                        seasons[0].SeasonId != currentSeason.SeasonId ||
                        seasons[0].State != ExtractionSeasonState.Active ||
                        seasons[0].WorldId is not null)
                    {
                        return Results.Conflict(new ApiError(
                            "ROLLOVER_INITIAL_BINDING_STATE_INVALID",
                            "The initial world binding requires exactly one active, unbound economy season."));
                    }
                }
                AdminAuditEnrichment.SetBefore(httpContext, new
                {
                    currentSeason.SeasonId,
                    currentSeason.Code,
                    currentSeason.WorldId,
                    currentSeason.Revision
                });
                var blockers = await GetRolloverBlockersAsync(
                    commerce,
                    runStore,
                    operationGate,
                    cancellationToken);
                if (!blockers.GateState.Maintenance)
                {
                    return Results.Conflict(new ApiError(
                        "ROLLOVER_MAINTENANCE_REQUIRED",
                        "提交新世界前必须先关闭交易并进入换档维护。"));
                }
                if (!blockers.ReadyForWorldSwitch)
                {
                    return Results.Conflict(new ApiError(
                        "ROLLOVER_NOT_READY",
                        $"仍有 {blockers.ActiveOperations} 个在途写操作、{blockers.Orders.Count} 个订单和 {blockers.Runs.Count} 个资源兑换记录等待终结。"));
                }
                var persistent = await rolloverStore.FindIncompleteAsync(
                    serverId,
                    cancellationToken);
                if (initialWorldBinding && persistent is not null)
                {
                    return Results.Conflict(new ApiError(
                        "ROLLOVER_INITIAL_BINDING_STATE_INVALID",
                        "The initial world binding cannot bypass an existing persistent rollover operation."));
                }
                if (!initialWorldBinding && persistent is null)
                {
                    return Results.Conflict(new ApiError(
                        "ROLLOVER_PERSISTENT_STATE_REQUIRED",
                        "The initial world binding is already complete; subsequent commits require the persistent rollover workflow."));
                }
                if (persistent is not null &&
                    (persistent.CurrentStep != WeeklyRolloverStep.Commit ||
                     !string.Equals(
                         persistent.TargetWorldId,
                         request.WorldId,
                         StringComparison.OrdinalIgnoreCase)))
                {
                    return Results.Conflict(new ApiError(
                        "ROLLOVER_PERSISTENT_STEP_MISMATCH",
                        $"Persistent rollover '{persistent.OperationId:D}' requires step " +
                        $"'{persistent.CurrentStep}' for target world '{persistent.TargetWorldId}'."));
                }
                if (persistent is not null)
                {
                    var expiry = await seasonJobs.FindExpiryAsync(
                        persistent.FromSeasonId,
                        cancellationToken);
                    if (expiry?.State != SeasonSettlementJobState.Completed ||
                        !string.Equals(
                            expiry.RulesVersion,
                            persistent.RulesVersion,
                            StringComparison.Ordinal))
                    {
                        return Results.Conflict(new ApiError(
                            "ROLLOVER_EXPIRY_JOB_REQUIRED",
                            "The version-matched SeasonVoucher expiry job must complete before the season commit."));
                    }
                }
                var season = await coordinator.CommitRolloverAsync(
                    request.WorldId,
                    cancellationToken);
                AdminAuditEnrichment.SetAfter(httpContext, new
                {
                    season.SeasonId,
                    season.Code,
                    season.WorldId,
                    season.Revision
                });
                return Results.Ok(new
                {
                    seasonId = season.SeasonId,
                    seasonCode = season.Code,
                    worldId = season.WorldId,
                    revision = season.Revision
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.SeasonHighRisk);

        return group;
    }

    private static async Task<IResult> GetSettlementStatusAsync(
        ExtractionSettlementService settlement,
        CancellationToken cancellationToken)
    {
        var result = await settlement.ProbeSettlementAsync(cancellationToken);
        return Results.Ok(new
        {
            adapter = settlement.SettlementAdapter,
            enabled = settlement.SettlementEnabled,
            connected = result.Success,
            outcome = result.Outcome.ToString().ToLowerInvariant(),
            error = result.Success
                ? null
                : new ApiError(
                    result.Outcome == RconOperationOutcome.Uncertain
                        ? "SETTLEMENT_PROBE_UNCERTAIN"
                        : "SETTLEMENT_PROBE_FAILED",
                    result.Outcome == RconOperationOutcome.Uncertain
                        ? "资源结算探针结果不确定；禁止自动重发，未证明资源已移除前绝不入账。"
                        : "资源结算适配器当前不可用；未证明资源已移除前绝不入账。")
        });
    }

    private static async Task<RolloverBlockers> GetRolloverBlockersAsync(
        ExtractionCommerceService commerce,
        ExtractionRunStore runStore,
        ExtractionOperationGate operationGate,
        CancellationToken cancellationToken)
    {
        var orders = await commerce.ListBlockingOrdersAsync(cancellationToken);
        var runs = await runStore.ListRolloverBlockingAsync(cancellationToken);
        var gateState = operationGate.Current;
        var activeOperations = operationGate.ActiveOperationCount;
        return new RolloverBlockers(
            gateState,
            orders,
            runs,
            activeOperations,
            gateState.Maintenance && activeOperations == 0 && orders.Count == 0 && runs.Count == 0);
    }

    internal static object SeasonDto(ExtractionSeason season, DateTimeOffset nextRefreshAt) => new
    {
        seasonId = season.SeasonId,
        name = season.DisplayName,
        state = season.State switch
        {
            ExtractionSeasonState.Scheduled => "scheduled",
            ExtractionSeasonState.Active => "active",
            ExtractionSeasonState.Closed => "closed",
            ExtractionSeasonState.Archived => "closed",
            _ => "scheduled"
        },
        startsAt = season.StartsAt,
        endsAt = season.EndsAt,
        nextShopRefreshAt = nextRefreshAt
    };

    internal static object ProductDto(
        ShopProduct product,
        int purchased,
        long globallyPurchased = 0) => new
    {
        productId = product.ProductId,
        sku = product.Sku,
        name = product.DisplayName,
        description = product.Description,
        category = product.Category,
        tags = product.Tags,
        price = new
        {
            currency = ExtractionModeCoordinator.ToClientCurrency(product.PriceCurrency),
            amount = product.UnitPrice
        },
        deliverySummary = string.Join(" · ", product.ItemGrants.Select(grant =>
            $"{grant.ItemId} × {grant.Quantity}")),
        stockRemaining = product.PurchaseLimitPerSeason is int limit
            ? Math.Max(0, limit - purchased)
            : (int?)null,
        personalLimitRemaining = product.PurchaseLimitPerSeason is int personalLimit
            ? Math.Max(0, personalLimit - purchased)
            : (int?)null,
        serverStockRemaining = product.GlobalStock is long globalStock
            ? Math.Max(0, globalStock - globallyPurchased)
            : (long?)null,
        purchaseLimit = product.PurchaseLimitPerSeason,
        globalStock = product.GlobalStock,
        purchased,
        enabled = product.Active,
        featured = product.FeaturedRank is not null,
        featuredRank = product.FeaturedRank,
        contentVersionId = product.ContentVersionId,
        contentHash = product.ContentHash
    };

    internal static object OrderDto(
        ShopOrder order,
        ExtractionDeliveryReceiptV1? deliveryReceipt = null)
    {
        var line = order.Lines.First();
        var charge = order.Charges.Single();
        var state = OrderOutcome(order.State, deliveryReceipt?.Outcome);
        return new
        {
            orderId = order.OrderId,
            productId = line.ProductId,
            productName = line.DisplayName,
            quantity = line.Quantity,
            currency = ExtractionModeCoordinator.ToClientCurrency(charge.Currency),
            totalAmount = charge.Amount,
            state,
            statusMessage = OrderStatusMessage(order.State, deliveryReceipt?.Outcome),
            createdAt = order.CreatedAt,
            updatedAt = order.UpdatedAt
        };
    }

    internal static async Task<object[]> OrderDtosAsync(
        IReadOnlyList<ShopOrder> orders,
        ExtractionDeliveryReceiptStore deliveryReceipts,
        CancellationToken cancellationToken)
    {
        var items = new object[orders.Count];
        for (var index = 0; index < orders.Count; index++)
        {
            var order = orders[index];
            ExtractionDeliveryReceiptV1? receipt = null;
            if (order.State == ShopOrderState.DeliveryUncertain)
            {
                var registration = await deliveryReceipts.GetAsync(
                    order.DeliveryId,
                    cancellationToken);
                receipt = registration?.Receipt;
            }
            items[index] = OrderDto(order, receipt);
        }
        return items;
    }

    internal static string LedgerReason(WalletLedgerEntry entry) =>
        string.Equals(entry.ReferenceType, "extraction_run", StringComparison.Ordinal)
            ? $"资源兑换结算 {entry.ReferenceId}"
            : entry.Reason;

    internal static object QuoteDto(ExtractionSettlementRun run) => new
    {
        runId = run.RunId,
        state = "quoted",
        zoneName = run.ZoneName,
        items = run.Items.Select(item => new
        {
            itemId = item.ItemId,
            name = item.DisplayName,
            quantity = item.Quantity,
            unitValue = item.UnitValue,
            totalValue = item.TotalValue
        }).ToArray(),
        itemCount = run.ItemCount,
        totalValue = run.TotalValue,
        expiresAt = run.ExpiresAt
    };

    internal static object RunDto(ExtractionSettlementRun run) => new
    {
        runId = run.RunId,
        state = run.State switch
        {
            ExtractionSettlementState.Quoted => "preparing",
            ExtractionSettlementState.Consuming => "deployed",
            ExtractionSettlementState.Removed => "deployed",
            ExtractionSettlementState.Credited => "deployed",
            ExtractionSettlementState.Settled => "extracted",
            ExtractionSettlementState.Uncertain => "uncertain",
            ExtractionSettlementState.Cancelled => "cancelled",
            ExtractionSettlementState.Expired => "cancelled",
            _ => "failed"
        },
        extractedItemCount = run.State == ExtractionSettlementState.Settled ? run.ItemCount : 0,
        extractedValue = run.State == ExtractionSettlementState.Settled ? run.TotalValue : 0,
        rewardCurrency = "weeklyTicket",
        rewardAmount = run.State == ExtractionSettlementState.Settled ? run.TotalValue : 0,
        startedAt = run.QuotedAt,
        endedAt = run.SettledAt ?? (run.State is
            ExtractionSettlementState.Failed or
            ExtractionSettlementState.Uncertain or
            ExtractionSettlementState.Expired or
            ExtractionSettlementState.Cancelled
                ? run.UpdatedAt
                : (DateTimeOffset?)null),
        statusMessage = run.ErrorMessage,
        internalState = run.State.ToString()
    };

    private static string ProductCategory(string sku) => sku switch
    {
        var value when value.Contains("AMMO", StringComparison.OrdinalIgnoreCase) => "弹药",
        var value when value.Contains("SPHERE", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("CAPTURE", StringComparison.OrdinalIgnoreCase) => "捕捉",
        var value when value.Contains("MEDIC", StringComparison.OrdinalIgnoreCase) => "医疗",
        var value when value.Contains("CROSSBOW", StringComparison.OrdinalIgnoreCase) => "武器",
        _ => "补给"
    };

    private static string[] ProductTags(ShopProduct product) =>
    [
        product.PriceCurrency == ExtractionCurrency.MarketCoin ? "永久货币" : "本周货币",
        product.PurchaseLimitPerSeason is null ? "不限购" : $"周限购 {product.PurchaseLimitPerSeason}",
        product.UpdatedBy.StartsWith("daily-rotation:", StringComparison.Ordinal)
            ? "今日轮换价"
            : "基础价"
    ];

    private static string OrderOutcome(
        ShopOrderState state,
        ExtractionDeliveryReceiptOutcome? receiptOutcome) => state switch
    {
        ShopOrderState.PendingDelivery => "accepted",
        ShopOrderState.Dispatching => "delivering",
        ShopOrderState.Delivered => "succeeded",
        ShopOrderState.DeliveryFailed => "failed",
        ShopOrderState.DeliveryUncertain
            when receiptOutcome == ExtractionDeliveryReceiptOutcome.Partial => "partial",
        ShopOrderState.DeliveryUncertain => "uncertain",
        ShopOrderState.Refunded => "refunded",
        _ => "pending"
    };

    private static string? OrderStatusMessage(
        ShopOrderState state,
        ExtractionDeliveryReceiptOutcome? receiptOutcome = null) => state switch
    {
        ShopOrderState.PendingDelivery => "订单已扣款，等待 PalDefender 发货。",
        ShopOrderState.Dispatching => "已提交游戏服，正在等待背包回读确认。",
        ShopOrderState.Delivered => "背包数量变化已确认。",
        ShopOrderState.DeliveryFailed => "游戏服明确拒绝发货，系统将按配置退款。",
        ShopOrderState.DeliveryUncertain
            when receiptOutcome == ExtractionDeliveryReceiptOutcome.Partial =>
                "不可变回执确认仅部分物品到账，已停止自动重发和全额退款，请等待人工核对。",
        ShopOrderState.DeliveryUncertain => "发货结果不确定，已停止自动重试和退款。",
        ShopOrderState.Refunded => "订单已退款。",
        _ => null
    };

    internal static IResult PurchaseError(ShopPurchaseResult result)
    {
        var status = result.ErrorCode switch
        {
            "PRODUCT_NOT_FOUND" => StatusCodes.Status404NotFound,
            "IDEMPOTENCY_CONFLICT" => StatusCodes.Status409Conflict,
            "INSUFFICIENT_FUNDS" => StatusCodes.Status409Conflict,
            "PURCHASE_LIMIT_EXCEEDED" => StatusCodes.Status409Conflict,
            "SEASON_NOT_ACTIVE" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity
        };
        return Results.Json(
            new ApiError(
                result.ErrorCode ?? "PURCHASE_FAILED",
                result.ErrorMessage ?? "商城订单创建失败。"),
            statusCode: status);
    }

    internal static IResult ToError(Exception exception) => exception switch
    {
        ExtractionModeException modeException => Results.Json(
            new ApiError(modeException.Code, modeException.Message),
            statusCode: modeException.StatusCode),
        ArgumentException argumentException => Results.BadRequest(
            new ApiError("INVALID_EXTRACTION_REQUEST", argumentException.Message)),
        KeyNotFoundException keyNotFoundException => Results.NotFound(
            new ApiError("EXTRACTION_RESOURCE_NOT_FOUND", keyNotFoundException.Message)),
        InvalidOperationException invalidOperationException => Results.Json(
            new ApiError("EXTRACTION_STATE_CONFLICT", invalidOperationException.Message),
            statusCode: StatusCodes.Status409Conflict),
        IOException ioException => Results.Json(
            new ApiError("EXTRACTION_STORE_UNAVAILABLE", ioException.Message),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(
            new ApiError("EXTRACTION_REQUEST_FAILED", "资源经济请求处理失败。"),
            statusCode: StatusCodes.Status500InternalServerError)
    };

    public sealed record CreateExtractionOrderRequest(
        string UserId,
        Guid ProductId,
        int Quantity,
        Guid? ContentVersionId = null,
        string? ContentHash = null,
        string? Sku = null);

    public sealed record CreateExtractionQuoteRequest(string UserId);

    public sealed record SettleExtractionRunRequest(string UserId);

    public sealed record RolloverMaintenanceRequest(bool Maintenance, string Reason);

    public sealed record RolloverCommitRequest(string WorldId);

    public sealed record ExtractionWalletAdjustmentRequest(
        string UserId,
        string Currency,
        long Delta,
        string Reason);

    public sealed record ExtractionOrderReconciliationRequest(
        string Resolution,
        string Reason,
        string Confirmation);

    public sealed record ExtractionRunReconciliationRequest(
        string Resolution,
        string Reason,
        string Confirmation);

    public sealed record EconomyCircuitUpdateRequest(
        bool WritesEnabled,
        string Reason);

    private sealed record RolloverBlockers(
        ExtractionOperationGateState GateState,
        IReadOnlyList<ShopOrder> Orders,
        IReadOnlyList<ExtractionSettlementRun> Runs,
        int ActiveOperations,
        bool ReadyForWorldSwitch);
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Content;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Read models used by the economy operations console. They intentionally
/// project global state from the authoritative stores instead of asking the
/// browser to enumerate player identities and stitch together player routes.
/// Raw PlayerUID values, one-time codes, cookies and adapter secrets are never
/// returned by these endpoints.
/// </summary>
public static class EconomyOperationsEndpoints
{
    public static RouteGroupBuilder MapEconomyOperationsEndpoints(this RouteGroupBuilder api)
    {
        var operations = api.MapGroup("/extraction/admin/operations")
            .RequireAuthorization(AdminPolicies.Viewer);

        operations.MapGet("/overview", GetOverviewAsync);
        operations.MapGet("/orders/{orderId:guid}/evidence", GetOrderEvidenceAsync);
        operations.MapGet("/runs/{runId:guid}/evidence", GetRunEvidenceAsync);
        return api;
    }

    private static async Task<IResult> GetOverviewAsync(
        int? limit,
        bool? refresh,
        ExtractionCommerceService commerce,
        ExtractionRunStore runStore,
        ExtractionDeliveryReceiptStore deliveryReceipts,
        PalDefenderCommandQueue commandQueue,
        ExtractionOperationGate operationGate,
        EconomyObservabilityService observability,
        EconomyContentRuntimeService content,
        WeeklyRolloverStateStore rolloverStore,
        SaveManagementService saves,
        AdminAuditStore audit,
        PlayerPortalSessionRegistry playerSessions,
        IOptions<ExtractionModeOptions> options,
        IOptions<ExtractionPersistenceOptions> persistence,
        IOptions<StartupSecurityValidationOptions> startupSecurity,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(limit ?? 100, 1, 250);
        var serverId = options.Value.ServerId;
        var ordersTask = commerce.ListOrdersAsync(null, null, pageSize, cancellationToken);
        var runsTask = runStore.ListAsync(null, null, pageSize, cancellationToken);
        var accountsTask = commerce.ListAccountsAsync(cancellationToken);
        var seasonTask = commerce.GetActiveSeasonAsync(serverId, cancellationToken);
        var rolloverTask = rolloverStore.FindIncompleteAsync(serverId, cancellationToken);
        var auditTask = audit.ListAsync(Math.Min(pageSize, 100), cancellationToken);
        var saveTask = saves.GetStatusAsync(serverId, cancellationToken);
        var metricsTask = refresh == true || observability.Latest is null
            ? observability.CollectAsync(applyAutomaticCircuits: false, cancellationToken)
            : Task.FromResult(observability.Latest);

        EconomyRuntimeContent? currentContent = null;
        try
        {
            currentContent = await content.GetCurrentAsync(cancellationToken);
        }
        catch (ContentStoreException exception) when (
            exception.Code == "CONTENT_NOT_PUBLISHED")
        {
            // A disabled or not-yet-bootstrapped economy still needs an
            // operable read-only console. The missing pointer is explicit in
            // the response rather than turning the whole page into a 500.
        }

        await Task.WhenAll(
            ordersTask,
            runsTask,
            accountsTask,
            seasonTask,
            rolloverTask,
            auditTask,
            saveTask,
            metricsTask);

        var accounts = (await accountsTask).ToDictionary(account => account.AccountId);
        var orders = await ordersTask;
        var orderDtos = new object[orders.Count];
        for (var index = 0; index < orders.Count; index++)
        {
            var order = orders[index];
            var registration = order.State == ShopOrderState.DeliveryUncertain
                ? await deliveryReceipts.GetAsync(order.DeliveryId, cancellationToken)
                : null;
            accounts.TryGetValue(order.AccountId, out var account);
            orderDtos[index] = AdminOrderDto(order, account, registration?.Receipt);
        }

        var gate = operationGate.Current;
        var metrics = await metricsTask;
        var generatedAt = DateTimeOffset.UtcNow;
        var gc = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        var dataDirectory = ResolveRuntimePath(
            persistence.Value.DataDirectory,
            environment.ContentRootPath);
        var logDirectory = ResolveRuntimePath(
            startupSecurity.Value.LogDirectory,
            environment.ContentRootPath);
        return Results.Ok(new
        {
            schemaVersion = 1,
            generatedAt,
            gameplayMode = ExtractionModeCoordinator.GameplayMode,
            runtime = new
            {
                instance = new
                {
                    processId = Environment.ProcessId,
                    processStartedAtUtc = process.StartTime.ToUniversalTime(),
                    dataDirectoryFingerprint = RuntimePathFingerprint(dataDirectory),
                    logDirectoryFingerprint = RuntimePathFingerprint(logDirectory)
                },
                sessions = new
                {
                    active = playerSessions.ActiveCount(generatedAt)
                },
                gc = new
                {
                    heapSizeBytes = gc.HeapSizeBytes,
                    totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
                    gen0Collections = GC.CollectionCount(0),
                    gen1Collections = GC.CollectionCount(1),
                    gen2Collections = GC.CollectionCount(2)
                }
            },
            world = new
            {
                serverId,
                season = (await seasonTask) is { } season ? new
                {
                    season.SeasonId,
                    season.Code,
                    season.DisplayName,
                    state = season.State.ToString(),
                    season.WorldId,
                    season.StartsAt,
                    season.EndsAt,
                    season.Revision
                } : null,
                save = await saveTask
            },
            content = currentContent is null ? null : new
            {
                versionId = currentContent.Version.VersionId,
                currentContent.Version.VersionNumber,
                currentContent.Version.BusinessDate,
                currentContent.Version.RulesVersion,
                currentContent.Version.ContentHash,
                rotationSeed = currentContent.Rotation.Seed,
                hotspotZoneIds = currentContent.HotspotZoneIds
            },
            gate = new
            {
                gate.Maintenance,
                gate.Reason,
                changedBy = gate.Actor,
                changedAt = gate.UpdatedAt,
                activeOperations = operationGate.ActiveOperationCount,
                circuits = metrics.Circuits,
                blockers = new
                {
                    purchase = metrics.DependencyConsistency.PurchaseBlockerCodes,
                    resourceExchange = metrics.DependencyConsistency.ResourceExchangeBlockerCodes
                }
            },
            queues = new
            {
                delivery = metrics.DeliveryQueue,
                settlement = metrics.ResourceSettlementQueue,
                outbox = metrics.Outbox,
                uncertain = metrics.Uncertain
            },
            backups = new
            {
                game = metrics.GameBackup,
                economy = metrics.EconomyBackup
            },
            alerts = metrics.Alerts,
            accounts = accounts.Values
                .OrderBy(account => account.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(account => account.AccountId)
                .Select(account => new
                {
                    account.AccountId,
                    account.DisplayName,
                    platform = account.IdentityProvider,
                    platformSubjectHash = SubjectHash(account.ExternalUserId),
                    account.CreatedAt,
                    account.UpdatedAt
                }).ToArray(),
            orders = orderDtos,
            runs = (await runsTask).Select(run => AdminRunDto(run, accounts.GetValueOrDefault(run.AccountId))).ToArray(),
            rollover = await rolloverTask,
            audit = (await auditTask).Select(AdminAuditDto).ToArray()
        });
    }

    internal static string ResolveRuntimePath(string configuredPath, string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(contentRoot, configuredPath));
    }

    internal static string RuntimePathFingerprint(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"pal-control-runtime-path-v1\n{normalized}")));
    }

    private static async Task<IResult> GetOrderEvidenceAsync(
        Guid orderId,
        ExtractionCommerceService commerce,
        ExtractionDeliveryReceiptStore receipts,
        ExtractionDeliveryEvidenceStore inventoryEvidence,
        CancellationToken cancellationToken)
    {
        var order = await commerce.GetOrderAsync(orderId, cancellationToken);
        if (order is null)
        {
            return Results.NotFound(new ApiError(
                "ORDER_NOT_FOUND",
                "商城订单不存在。"));
        }

        var account = await commerce.GetAccountAsync(order.AccountId, cancellationToken);
        var registration = await receipts.GetAsync(order.DeliveryId, cancellationToken);
        var evidence = await inventoryEvidence.GetAsync(order.DeliveryId, cancellationToken);
        return Results.Ok(new
        {
            schemaVersion = 1,
            generatedAt = DateTimeOffset.UtcNow,
            order = AdminOrderDto(order, account, registration?.Receipt),
            request = registration is null ? null : new
            {
                registration.Request.DeliveryId,
                registration.Request.IdempotencyKey,
                registration.Request.RequestHash,
                registration.Request.ResultId,
                registration.Request.ServerId,
                playerSubjectHash = SubjectHash(registration.Request.PlayerUid),
                registration.Request.WorldId,
                registration.Request.GameVersion,
                registration.Request.AdapterVersion,
                registration.Request.CommandVersion,
                registration.Request.Items,
                registration.Request.CreatedAt
            },
            receipt = registration?.Receipt is { } receipt ? new
            {
                receipt.SchemaVersion,
                receipt.DeliveryId,
                receipt.IdempotencyKey,
                receipt.RequestHash,
                receipt.ResultId,
                receipt.ServerId,
                playerSubjectHash = SubjectHash(receipt.PlayerUid),
                receipt.WorldId,
                receipt.GameVersion,
                receipt.AdapterVersion,
                receipt.CommandVersion,
                receipt.AcknowledgedAt,
                receipt.Items,
                receipt.Outcome,
                receipt.CreatedAt
            } : null,
            inventoryEvidence = evidence
        });
    }

    private static async Task<IResult> GetRunEvidenceAsync(
        Guid runId,
        ExtractionRunStore runs,
        ExtractionCommerceService commerce,
        CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(runId, cancellationToken);
        if (run is null)
        {
            return Results.NotFound(new ApiError(
                "EXTRACTION_RUN_NOT_FOUND",
                "资源兑换记录不存在。"));
        }
        var account = await commerce.GetAccountAsync(run.AccountId, cancellationToken);
        return Results.Ok(new
        {
            schemaVersion = 1,
            generatedAt = DateTimeOffset.UtcNow,
            run = AdminRunDto(run, account),
            quote = new
            {
                run.QuoteSnapshotHash,
                run.Items,
                run.ItemCount,
                run.TotalValue,
                run.ContentVersionId,
                run.ContentHash,
                run.ContentBusinessDate,
                run.ContentRulesVersion,
                run.RotationSeed,
                run.ZoneYieldMultiplierBasisPoints,
                run.Hotspot,
                inventory = run.NativeInventorySnapshot is { } snapshot ? new
                {
                    snapshot.SnapshotVersion,
                    ownerSubjectHash = SubjectHash(snapshot.OwnerPlayerUid),
                    snapshot.ObservedAt,
                    snapshot.SnapshotHash,
                    containers = snapshot.Containers.Select(container => new
                    {
                        container.ContainerKind,
                        slotCount = container.Slots.Count,
                        itemCount = container.Slots.Sum(slot => (long)slot.Quantity)
                    }).ToArray()
                } : null
            },
            settlement = new
            {
                run.SettlementIdempotencyKey,
                run.SettlementRequestHash,
                run.NativeConsumeReceipt,
                run.AttemptCount,
                run.LastHeartbeatAt,
                leaseActive = run.LeaseId is not null && run.LeaseExpiresAt > DateTimeOffset.UtcNow,
                run.LeaseExpiresAt,
                run.ReconciliationActor
            }
        });
    }

    private static object AdminOrderDto(
        ShopOrder order,
        ExtractionAccount? account,
        ExtractionDeliveryReceiptV1? receipt)
    {
        var line = order.Lines.First();
        var charge = order.Charges.Single();
        return new
        {
            order.OrderId,
            order.AccountId,
            order.SeasonId,
            order.ServerId,
            account = account is null ? null : new
            {
                account.AccountId,
                account.DisplayName,
                platform = account.IdentityProvider,
                platformSubjectHash = SubjectHash(account.ExternalUserId)
            },
            order.DeliveryId,
            order.DeliveryAttempt,
            productId = line.ProductId,
            line.Sku,
            productName = line.DisplayName,
            line.Quantity,
            currency = ExtractionModeCoordinator.ToClientCurrency(charge.Currency),
            totalAmount = charge.Amount,
            state = order.State.ToString(),
            outcome = receipt?.Outcome.ToString(),
            receiptResultId = receipt?.ResultId,
            contentVersionId = line.ContentVersionId,
            contentHash = line.ContentHash,
            order.WorldId,
            order.CreatedAt,
            order.UpdatedAt,
            requiresReconciliation = order.State == ShopOrderState.DeliveryUncertain
        };
    }

    private static object AdminRunDto(
        ExtractionSettlementRun run,
        ExtractionAccount? account) => new
    {
        run.RunId,
        run.AccountId,
        run.SeasonId,
        account = account is null ? null : new
        {
            account.AccountId,
            account.DisplayName,
            platform = account.IdentityProvider,
            platformSubjectHash = SubjectHash(account.ExternalUserId)
        },
        run.ZoneId,
        run.ZoneName,
        state = run.State.ToString(),
        run.ItemCount,
        run.TotalValue,
        run.Revision,
        run.AttemptCount,
        run.ErrorCode,
        run.ErrorMessage,
        run.ContentVersionId,
        run.ContentHash,
        run.QuoteSnapshotHash,
        run.SettlementRequestHash,
        run.QuotedAt,
        run.ExpiresAt,
        run.UpdatedAt,
        run.SettledAt,
        requiresReconciliation = run.State == ExtractionSettlementState.Uncertain
    };

    private static object AdminAuditDto(AdminAuditEvent audit) => new
    {
        audit.AuditId,
        audit.CorrelationId,
        audit.Phase,
        audit.Subject,
        audit.Roles,
        audit.Method,
        audit.Path,
        audit.RequestHash,
        audit.Reason,
        audit.BeforeJson,
        audit.AfterJson,
        audit.ServiceVersion,
        audit.ResultStatus,
        audit.OccurredAt
    };

    private static string SubjectHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return $"sha256:{Convert.ToHexString(bytes)[..16].ToLowerInvariant()}";
    }
}

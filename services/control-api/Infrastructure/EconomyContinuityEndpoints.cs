using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class EconomyContinuityEndpoints
{
    public static RouteGroupBuilder MapEconomyContinuityEndpoints(this RouteGroupBuilder api)
    {
        var continuity = api.MapGroup("/admin/economy-continuity");

        continuity.MapGet("/capacity", (EconomyContinuityService service) =>
            Results.Ok(service.GetCapacityPlan()))
            .RequireAuthorization(AdminPolicies.SeasonAdmin);

        continuity.MapGet("/retention-plan", (EconomyContinuityService service) =>
            Results.Ok(new
            {
                deleteAutomatically = false,
                candidates = service.PlanRetention(DateTimeOffset.UtcNow)
            }))
            .RequireAuthorization(AdminPolicies.SeasonAdmin);

        continuity.MapPost("/snapshots", async (
            EconomySnapshotRequest request,
            ExtractionOperationGate operationGate,
            EconomyContinuityService service,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
                Results.Ok(await service.CreateSnapshotAsync(
                    request.ServerId,
                    request.WorldId,
                    operationGate.Current,
                    operationGate.ActiveOperationCount,
                    request.IdempotencyKey,
                    cancellationToken))))
            .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        continuity.MapGet("/snapshots/{serverId}/{backupId}/verify", (
            string serverId,
            string backupId,
            EconomyContinuityService service) => Execute(() =>
                Results.Ok(service.VerifySnapshot(serverId, backupId))))
            .RequireAuthorization(AdminPolicies.SeasonAdmin);

        continuity.MapPost("/snapshots/{serverId}/{backupId}/stage", async (
            string serverId,
            string backupId,
            EconomyStageRequest request,
            EconomyContinuityService service,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
                Results.Ok(await service.RestoreToStagingAsync(
                    serverId,
                    backupId,
                    request.ExpectedWorldId,
                    cancellationToken))))
            .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        continuity.MapGet("/snapshots/{serverId}/{backupId}/post-snapshot", (
            string serverId,
            string backupId,
            EconomyContinuityService service) => Execute(() => Results.Ok(new
            {
                items = service.ListPostSnapshotTransactions(serverId, backupId)
            })))
            .RequireAuthorization(AdminPolicies.SeasonAdmin);

        var rollover = api.MapGroup("/admin/weekly-rollover");
        rollover.MapPost("/operations", async (
            RolloverPrepareRequest request,
            HttpContext context,
            IExtractionRepository repository,
            ExtractionModeCoordinator coordinator,
            WeeklyRolloverStateStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
            {
                var season = await repository.GetSeasonAsync(request.FromSeasonId, cancellationToken)
                    ?? throw new KeyNotFoundException("The source extraction season does not exist.");
                if (season.State != ExtractionSeasonState.Active ||
                    !string.Equals(season.ServerId, request.ServerId, StringComparison.Ordinal) ||
                    !string.Equals(season.WorldId, request.FromWorldId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The rollover source must be the server's active season and exact current world.");
                }
                var preflight = await coordinator.GetRolloverPreflightAsync(cancellationToken);
                if (!preflight.CanStartWorldSwitch ||
                    preflight.CurrentSeasonId != request.FromSeasonId ||
                    !string.Equals(
                        preflight.ActualWorldId,
                        request.FromWorldId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The persistent rollover cannot be prepared: {preflight.Reason}");
                }
                var operation = await store.PrepareAsync(
                    request.ServerId,
                    request.FromSeasonId,
                    request.FromWorldId,
                    request.TargetWorldId,
                    request.RulesVersion,
                    cancellationToken);
                return Results.Ok(ToRolloverResponse(operation));
            }))
            .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        rollover.MapGet("/operations/{operationId:guid}", async (
            Guid operationId,
            WeeklyRolloverStateStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
            {
                var operation = await store.GetAsync(operationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The weekly rollover operation does not exist.");
                return Results.Ok(ToRolloverResponse(operation));
            }))
            .RequireAuthorization(AdminPolicies.SeasonAdmin);

        rollover.MapGet("/operations/active", async (
            string serverId,
            WeeklyRolloverStateStore store,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
            {
                var operation = await store.FindIncompleteAsync(serverId, cancellationToken);
                return operation is null
                    ? Results.NotFound(new ApiError(
                        "ROLLOVER_NOT_ACTIVE",
                        "The server has no incomplete weekly rollover."))
                    : Results.Ok(ToRolloverResponse(operation));
            }))
            .RequireAuthorization(AdminPolicies.SeasonAdmin);

        rollover.MapPost("/operations/{operationId:guid}/steps/{step}", async (
            Guid operationId,
            WeeklyRolloverStep step,
            RolloverStepRequest request,
            HttpContext context,
            WeeklyRolloverStateStore store,
            IExtractionRepository repository,
            ExtractionModeCoordinator coordinator,
            ExtractionRunStore runStore,
            ExtractionOperationGate operationGate,
            EconomySafetyGate safetyGate,
            SaveManagementService saves,
            EconomyContinuityService economyBackups,
            SeasonSettlementJobStore seasonJobs,
            ExtractionSettlementQueue settlementQueue,
            PalDefenderCommandQueue deliveryCommands,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
            {
                var operation = await store.GetAsync(operationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The weekly rollover operation does not exist.");
                var alreadyCompleted = operation.CompletedSteps.Any(item => item.Step == step);
                if (!alreadyCompleted)
                {
                    await ValidateRolloverStepAsync(
                        operation,
                        step,
                        request.Evidence,
                        repository,
                        coordinator,
                        runStore,
                        operationGate,
                        safetyGate,
                        saves,
                        economyBackups,
                        seasonJobs,
                        settlementQueue,
                        deliveryCommands,
                        cancellationToken);
                }
                var actor = AdminIdentity.RequireSubject(context);
                var transition = await store.CompleteStepAsync(
                    operationId,
                    step,
                    request.StepKey,
                    request.Evidence,
                    actor,
                    cancellationToken);
                if (step == WeeklyRolloverStep.Reopen && operationGate.Current.Maintenance)
                {
                    _ = await operationGate.SetAsync(
                        false,
                        $"Weekly rollover {operationId:D} completed with all safety gates revalidated",
                        actor,
                        cancellationToken);
                }
                return Results.Ok(new
                {
                    transition.Applied,
                    transition.IdempotentReplay,
                    transition.StepKey,
                    operation = ToRolloverResponse(transition.Operation)
                });
            }))
            .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        var jobs = api.MapGroup("/admin/season-settlement-jobs");
        jobs.MapPost("/voucher-expiry", async (
            VoucherExpiryPrepareRequest request,
            HttpContext context,
            ExtractionOperationGate operationGate,
            SeasonSettlementJobService service,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
                Results.Ok(await service.PrepareVoucherExpiryAsync(
                    request.SeasonId,
                    request.RulesVersion,
                    AdminIdentity.RequireSubject(context),
                    operationGate.Current,
                    operationGate.ActiveOperationCount,
                    cancellationToken))))
            .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        jobs.MapPost("/rewards", async (
            RewardJobPrepareRequest request,
            HttpContext context,
            ExtractionOperationGate operationGate,
            SeasonSettlementJobService service,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
                Results.Ok(await service.PrepareRewardAsync(
                    request.SourceSeasonId,
                    request.RulesVersion,
                    request.RewardBatchKey,
                    request.Grants,
                    AdminIdentity.RequireSubject(context),
                    operationGate.Current,
                    operationGate.ActiveOperationCount,
                    cancellationToken))))
            .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        jobs.MapGet("/{jobId:guid}", async (
            Guid jobId,
            SeasonSettlementJobService service,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
                service.GetAsync(jobId, cancellationToken) is { } job
                    ? Results.Ok(job)
                    : Results.NotFound(new ApiError(
                        "SEASON_JOB_NOT_FOUND",
                        "The season settlement job does not exist."))))
            .RequireAuthorization(AdminPolicies.SeasonAdmin);

        jobs.MapPost("/{jobId:guid}/run", async (
            Guid jobId,
            ExtractionOperationGate operationGate,
            SeasonSettlementJobService service,
            CancellationToken cancellationToken) => await ExecuteAsync(async () =>
                Results.Ok(await service.RunAsync(
                    jobId,
                    operationGate.Current,
                    operationGate.ActiveOperationCount,
                    cancellationToken))))
            .RequireAuthorization(AdminPolicies.SeasonHighRisk);

        return api;
    }

    private static async Task ValidateRolloverStepAsync(
        WeeklyRolloverOperation operation,
        WeeklyRolloverStep step,
        WeeklyRolloverEvidence evidence,
        IExtractionRepository repository,
        ExtractionModeCoordinator coordinator,
        ExtractionRunStore runStore,
        ExtractionOperationGate operationGate,
        EconomySafetyGate safetyGate,
        SaveManagementService saves,
        EconomyContinuityService economyBackups,
        SeasonSettlementJobStore seasonJobs,
        ExtractionSettlementQueue settlementQueue,
        PalDefenderCommandQueue deliveryCommands,
        CancellationToken cancellationToken)
    {
        if (step is not WeeklyRolloverStep.Preflight &&
            (!operationGate.Current.Maintenance || operationGate.ActiveOperationCount != 0))
        {
            throw new InvalidOperationException(
                "Drain through reopen requires maintenance mode and zero active economy operations.");
        }
        if (step is WeeklyRolloverStep.Preflight or WeeklyRolloverStep.Drain or
            WeeklyRolloverStep.GameBackup or WeeklyRolloverStep.EconomyBackup or
            WeeklyRolloverStep.Stop or WeeklyRolloverStep.NewWorld or
            WeeklyRolloverStep.Probe or WeeklyRolloverStep.Commit or
            WeeklyRolloverStep.Reopen)
        {
            var blockingRuns = await runStore.ListRolloverBlockingAsync(cancellationToken);
            var blockingOrders = await repository.ListBlockingOrdersAsync(cancellationToken);
            if (blockingRuns.Count != 0 || blockingOrders.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Rollover is blocked by {blockingRuns.Count} settlement run(s) and " +
                    $"{blockingOrders.Count} unresolved order(s).");
            }
        }
        if (step is WeeklyRolloverStep.Drain or WeeklyRolloverStep.GameBackup or
            WeeklyRolloverStep.EconomyBackup or WeeklyRolloverStep.Stop or
            WeeklyRolloverStep.NewWorld or WeeklyRolloverStep.Probe or
            WeeklyRolloverStep.Commit or WeeklyRolloverStep.Reopen)
        {
            var deliveryLoad = await deliveryCommands.GetEconomyLoadAsync(cancellationToken);
            var commandBlockers = economyBackups.GetCurrentCommandBlockers();
            if (settlementQueue.AdmittedCount != 0 || deliveryLoad.Pending != 0 ||
                commandBlockers.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Rollover drain is incomplete: {settlementQueue.AdmittedCount} settlement " +
                    $"request(s), {deliveryLoad.Pending} in-memory PalDefender command(s), and " +
                    $"{commandBlockers.Count} durable command/outbox blocker(s) remain.");
            }
        }
        if (step == WeeklyRolloverStep.Preflight)
        {
            var rolloverPreflight = await coordinator.GetRolloverPreflightAsync(cancellationToken);
            var purchase = await safetyGate.EvaluateAsync(
                EconomyWriteFeature.Purchase, null, null, cancellationToken);
            var exchange = await safetyGate.EvaluateAsync(
                EconomyWriteFeature.ResourceExchange, null, null, cancellationToken);
            if (!rolloverPreflight.CanStartWorldSwitch ||
                rolloverPreflight.CurrentSeasonId != operation.FromSeasonId ||
                !string.Equals(
                    rolloverPreflight.ActualWorldId,
                    operation.FromWorldId,
                    StringComparison.OrdinalIgnoreCase) ||
                !purchase.Enabled || !exchange.Enabled ||
                !economyBackups.GetCapacityPlan().CapacitySufficient)
            {
                throw new InvalidOperationException(
                    "Rollover preflight failed economy dependency/version/capacity checks.");
            }
        }
        if (step == WeeklyRolloverStep.Drain &&
            (!operationGate.Current.Maintenance || operationGate.ActiveOperationCount != 0))
        {
            throw new InvalidOperationException("The economy has not drained under maintenance mode.");
        }
        if (step == WeeklyRolloverStep.GameBackup)
        {
            var backup = await saves.VerifyManagedBackupAsync(
                operation.ServerId,
                evidence.EvidenceReference,
                cancellationToken);
            if (!string.Equals(backup.Integrity, "verified", StringComparison.Ordinal) ||
                !string.Equals(backup.WorldGuid, operation.FromWorldId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(backup.ManifestSha256, evidence.EvidenceHash, StringComparison.OrdinalIgnoreCase) ||
                backup.CreatedAt < operation.CreatedAt ||
                !economyBackups.IsWithinRpo(backup.CreatedAt))
            {
                throw new InvalidOperationException(
                    "Game-backup evidence is stale, unverified, from another world, or hash-mismatched.");
            }
        }
        if (step == WeeklyRolloverStep.EconomyBackup)
        {
            var backup = economyBackups.VerifySnapshot(
                operation.ServerId,
                evidence.EvidenceReference);
            var unexpectedPending = backup.PendingTransactions
                .Where(item => !(string.Equals(item.Kind, "rollover", StringComparison.Ordinal) &&
                    string.Equals(item.Id, operation.OperationId.ToString("D"), StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (!string.Equals(backup.WorldId, operation.FromWorldId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(backup.ContentHash, evidence.EvidenceHash, StringComparison.OrdinalIgnoreCase) ||
                backup.CreatedAt < operation.CreatedAt ||
                !economyBackups.IsWithinRpo(backup.CreatedAt) ||
                unexpectedPending.Length != 0)
            {
                throw new InvalidOperationException(
                    "Economy-backup evidence is stale, pending, from another world, or hash-mismatched.");
            }
        }
        if (step == WeeklyRolloverStep.Probe)
        {
            var live = await coordinator.GetRolloverPreflightAsync(cancellationToken);
            if (!string.Equals(
                    live.ActualWorldId,
                    operation.TargetWorldId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Probe evidence does not match the server-side resolved target world.");
            }
        }
        if (step == WeeklyRolloverStep.Commit)
        {
            var expiry = await seasonJobs.FindExpiryAsync(operation.FromSeasonId, cancellationToken);
            if (expiry?.State != SeasonSettlementJobState.Completed ||
                !string.Equals(expiry.RulesVersion, operation.RulesVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Commit requires the version-matched SeasonVoucher expiry job to complete first.");
            }
            var sourceSeason = await repository.GetSeasonAsync(
                operation.FromSeasonId,
                cancellationToken);
            var activeSeasons = (await repository.ListSeasonsAsync(
                    operation.ServerId,
                    cancellationToken))
                .Where(season => season.State == ExtractionSeasonState.Active)
                .ToArray();
            if (sourceSeason is null || sourceSeason.State == ExtractionSeasonState.Active ||
                activeSeasons.Length != 1 ||
                !string.Equals(
                    activeSeasons[0].WorldId,
                    operation.TargetWorldId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Commit requires the source season to be closed and exactly one active season on the probed target world.");
            }
            await coordinator.EnsureSeasonMatchesActiveWorldAsync(
                activeSeasons[0],
                cancellationToken);
        }
        if (step == WeeklyRolloverStep.Reopen)
        {
            var circuits = safetyGate.Current;
            var deliveryLoad = await deliveryCommands.GetEconomyLoadAsync(cancellationToken);
            var purchase = await safetyGate.EvaluateForMaintenanceReopenAsync(
                EconomyWriteFeature.Purchase,
                deliveryLoad,
                cancellationToken);
            var exchange = await safetyGate.EvaluateForMaintenanceReopenAsync(
                EconomyWriteFeature.ResourceExchange,
                new EconomyQueueSnapshot(
                    settlementQueue.IsAccepting,
                    settlementQueue.AdmittedCount,
                    settlementQueue.Capacity),
                cancellationToken);
            if (!operationGate.Current.Maintenance ||
                operationGate.ActiveOperationCount != 0 ||
                !circuits.Purchase.WritesEnabled ||
                !circuits.ResourceExchange.WritesEnabled ||
                !purchase.Enabled || !exchange.Enabled ||
                !evidence.AllGatesPassed)
            {
                throw new InvalidOperationException(
                    "Reopen evidence requires the maintenance gate and both economy circuits to be open.");
            }
        }
    }

    private static object ToRolloverResponse(WeeklyRolloverOperation operation) => new
    {
        operation,
        requiredStepKey = operation.CurrentStep == WeeklyRolloverStep.Completed
            ? null
            : WeeklyRolloverStateStore.StepKey(operation.OperationId, operation.CurrentStep)
    };

    private static IResult Execute(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            return Error(exception);
        }
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
        catch (Exception exception)
        {
            return Error(exception);
        }
    }

    private static IResult Error(Exception exception) => exception switch
    {
        ArgumentException => Results.BadRequest(new ApiError(
            "CONTINUITY_REQUEST_INVALID", exception.Message)),
        KeyNotFoundException or FileNotFoundException or DirectoryNotFoundException => Results.NotFound(
            new ApiError("CONTINUITY_RESOURCE_NOT_FOUND", exception.Message)),
        InvalidOperationException or InvalidDataException or SaveManagementException or SqliteException =>
            Results.Conflict(new ApiError("CONTINUITY_PRECONDITION_FAILED", exception.Message)),
        IOException or UnauthorizedAccessException => Results.Json(
            new ApiError("CONTINUITY_STORAGE_UNAVAILABLE", "Continuity storage is unavailable."),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => throw exception
    };

    public sealed record EconomySnapshotRequest(
        string ServerId,
        string WorldId,
        string? IdempotencyKey = null);
    public sealed record EconomyStageRequest(string ExpectedWorldId);
    public sealed record RolloverPrepareRequest(
        string ServerId,
        Guid FromSeasonId,
        string FromWorldId,
        string TargetWorldId,
        string RulesVersion);
    public sealed record RolloverStepRequest(string StepKey, WeeklyRolloverEvidence Evidence);
    public sealed record VoucherExpiryPrepareRequest(Guid SeasonId, string RulesVersion);
    public sealed record RewardJobPrepareRequest(
        Guid SourceSeasonId,
        string RulesVersion,
        string RewardBatchKey,
        IReadOnlyList<SeasonRewardGrant> Grants);
}

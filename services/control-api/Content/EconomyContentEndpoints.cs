using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ControlApi.Content;

public static class EconomyContentEndpoints
{
    private const string PublishConfirmation = "PUBLISH ECONOMY CONTENT";
    private const string RollbackConfirmation = "ROLLBACK ECONOMY CONTENT";

    public static RouteGroupBuilder MapEconomyContentEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/servers/{serverId}/economy-content")
            .RequireAuthorization(AdminPolicies.Viewer);

        group.MapGet("/current", async (
            string serverId,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                var pointer = await store.GetCurrentAsync(serverId, cancellationToken);
                if (pointer is null)
                {
                    return Results.NotFound(new ApiError(
                        "CONTENT_NOT_PUBLISHED",
                        "No economy content version is currently published."));
                }
                var version = await store.GetVersionAsync(pointer.VersionId, cancellationToken)
                    ?? throw new InvalidDataException(
                        "The current content pointer references a missing version.");
                return Results.Ok(new { pointer, version, rotation = EconomyContentRotation.Create(version) });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapGet("/versions", async (
            string serverId,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                return Results.Ok(new
                {
                    items = await store.ListVersionsAsync(serverId, cancellationToken)
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapGet("/drafts", async (
            string serverId,
            bool? includePublished,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                return Results.Ok(new
                {
                    items = await store.ListDraftsAsync(
                        serverId,
                        includePublished == true,
                        cancellationToken)
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapGet("/drafts/{draftId:guid}", async (
            string serverId,
            Guid draftId,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                var draft = await store.GetDraftAsync(draftId, cancellationToken);
                return draft is null || !string.Equals(
                    draft.ServerId,
                    serverId,
                    StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new ApiError(
                        "CONTENT_DRAFT_NOT_FOUND",
                        "The content draft does not exist."))
                    : Results.Ok(draft);
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapPost("/drafts", async (
            string serverId,
            CreateContentDraftRequest request,
            HttpContext httpContext,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                ArgumentNullException.ThrowIfNull(request.Definition);
                var draft = await store.CreateDraftAsync(
                    serverId,
                    request.Name,
                    request.BasedOnVersionId,
                    request.Definition,
                    AdminIdentity.RequireSubject(httpContext),
                    cancellationToken);
                AdminAuditEnrichment.SetAfter(httpContext, DraftAudit(draft));
                return Results.Created(
                    $"/api/v1/servers/{Uri.EscapeDataString(serverId)}/economy-content/drafts/{draft.DraftId:D}",
                    draft);
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyAdmin);

        group.MapPut("/drafts/{draftId:guid}", async (
            string serverId,
            Guid draftId,
            UpdateContentDraftRequest request,
            HttpContext httpContext,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                ArgumentNullException.ThrowIfNull(request.Definition);
                var revision = RequireRevision(httpContext.Request);
                var before = await store.GetDraftAsync(draftId, cancellationToken);
                AdminAuditEnrichment.SetBefore(
                    httpContext,
                    before is null ? null : DraftAudit(before));
                var draft = await store.UpdateDraftAsync(
                    draftId,
                    revision,
                    request.Definition,
                    AdminIdentity.RequireSubject(httpContext),
                    cancellationToken);
                EnsureDraftServer(draft, serverId);
                AdminAuditEnrichment.SetAfter(httpContext, DraftAudit(draft));
                return Results.Ok(draft);
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyAdmin);

        group.MapGet("/drafts/{draftId:guid}/diff", async (
            string serverId,
            Guid draftId,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                var draft = await store.GetDraftAsync(draftId, cancellationToken)
                    ?? throw new ContentStoreException(
                        "CONTENT_DRAFT_NOT_FOUND",
                        "The content draft does not exist.");
                EnsureDraftServer(draft, serverId);
                return Results.Ok(new
                {
                    draftId,
                    draft.Revision,
                    items = await store.DiffDraftAsync(draftId, cancellationToken)
                });
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        group.MapPost("/drafts/{draftId:guid}/validate", async (
            string serverId,
            Guid draftId,
            HttpContext httpContext,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            EconomyContentRuntimeService runtime,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                var revision = RequireRevision(httpContext.Request);
                var draft = await store.GetDraftAsync(draftId, cancellationToken)
                    ?? throw new ContentStoreException(
                        "CONTENT_DRAFT_NOT_FOUND",
                        "The content draft does not exist.");
                EnsureDraftServer(draft, serverId);
                var context = await runtime.BuildValidationContextAsync(cancellationToken);
                var validation = await store.ValidateDraftAsync(
                    draftId,
                    revision,
                    context,
                    cancellationToken);
                return Results.Ok(validation);
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyAdmin);

        group.MapPost("/drafts/{draftId:guid}/publish", async (
            string serverId,
            Guid draftId,
            PublishContentRequest request,
            HttpContext httpContext,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            EconomyContentRuntimeService runtime,
            ExtractionOperationGate operationGate,
            ExtractionRunStore runStore,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                RequireConfirmation(request.Confirmation, PublishConfirmation);
                RequireReason(request.Reason);
                await RequireMaintenanceAndDrainAsync(operationGate, runStore, cancellationToken);
                var idempotencyKey = RequireIdempotencyKey(httpContext.Request);
                var expectedRevision = RequireRevision(httpContext.Request);
                var businessDate = runtime.GetCurrentBusinessDate();
                if (request.BusinessDate != businessDate)
                {
                    throw new ContentStoreException(
                        "CONTENT_BUSINESS_DATE_MISMATCH",
                        $"Content can only be published for the current business date {businessDate:yyyy-MM-dd}.");
                }
                var before = await store.GetCurrentAsync(serverId, cancellationToken);
                AdminAuditEnrichment.SetBefore(httpContext, before);
                var validationContext = await runtime.BuildValidationContextAsync(cancellationToken);
                var actor = AdminIdentity.RequireSubject(httpContext);
                var prepared = await store.PreparePublishAsync(
                    draftId,
                    expectedRevision,
                    request.BusinessDate,
                    idempotencyKey,
                    actor,
                    validationContext,
                    cancellationToken);
                var activation = await runtime.ActivatePreparedVersionAsync(
                    prepared.Version,
                    prepared.ExpectedCurrentVersionId,
                    "publish",
                    actor,
                    cancellationToken);
                var pointer = await store.GetCurrentAsync(serverId, cancellationToken)
                    ?? throw new InvalidDataException(
                        "The activated content product projection has no current pointer.");
                if (pointer.VersionId != prepared.Version.VersionId)
                {
                    throw new InvalidDataException(
                        "The activated content product projection and current pointer disagree.");
                }
                var result = new ContentPublishResult(
                    prepared.Version,
                    pointer,
                    prepared.VersionCreated,
                    activation.PointerChanged,
                    prepared.Replayed && activation.Replayed);
                AdminAuditEnrichment.SetAfter(httpContext, new
                {
                    result.Pointer,
                    result.VersionCreated,
                    result.PointerChanged,
                    result.Replayed,
                    reason = request.Reason
                });
                return Results.Ok(result);
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyHighRisk);

        group.MapPost("/rollback", async (
            string serverId,
            RollbackContentRequest request,
            HttpContext httpContext,
            IOptions<ExtractionModeOptions> options,
            IEconomyContentStore store,
            EconomyContentRuntimeService runtime,
            ExtractionOperationGate operationGate,
            ExtractionRunStore runStore,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RequireServer(serverId, options.Value);
                RequireConfirmation(request.Confirmation, RollbackConfirmation);
                RequireReason(request.Reason);
                await RequireMaintenanceAndDrainAsync(operationGate, runStore, cancellationToken);
                var idempotencyKey = RequireIdempotencyKey(httpContext.Request);
                var before = await store.GetCurrentAsync(serverId, cancellationToken);
                AdminAuditEnrichment.SetBefore(httpContext, before);
                var actor = AdminIdentity.RequireSubject(httpContext);
                var prepared = await store.PrepareRollbackAsync(
                    serverId,
                    request.TargetVersionId,
                    request.ExpectedCurrentVersionId,
                    idempotencyKey,
                    actor,
                    cancellationToken);
                var activation = await runtime.ActivatePreparedVersionAsync(
                    prepared.Version,
                    prepared.ExpectedCurrentVersionId,
                    "rollback",
                    actor,
                    cancellationToken);
                var pointer = await store.GetCurrentAsync(serverId, cancellationToken)
                    ?? throw new InvalidDataException(
                        "The activated rollback product projection has no current pointer.");
                if (pointer.VersionId != prepared.Version.VersionId)
                {
                    throw new InvalidDataException(
                        "The activated rollback product projection and current pointer disagree.");
                }
                var result = new ContentRollbackResult(
                    pointer,
                    prepared.ExpectedCurrentVersionId,
                    activation.PointerChanged,
                    prepared.Replayed && activation.Replayed);
                AdminAuditEnrichment.SetAfter(httpContext, new
                {
                    result.Pointer,
                    result.PreviousVersionId,
                    result.PointerChanged,
                    result.Replayed,
                    reason = request.Reason
                });
                return Results.Ok(result);
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        }).RequireAuthorization(AdminPolicies.EconomyHighRisk);

        return group;
    }

    private static async Task RequireMaintenanceAndDrainAsync(
        ExtractionOperationGate operationGate,
        ExtractionRunStore runStore,
        CancellationToken cancellationToken)
    {
        if (!operationGate.Current.Maintenance || operationGate.ActiveOperationCount != 0)
        {
            throw new ContentStoreException(
                "CONTENT_MAINTENANCE_REQUIRED",
                "Enter maintenance and drain active player operations before publishing or rolling back content.");
        }
        if ((await runStore.ListRolloverBlockingAsync(cancellationToken)).Count != 0)
        {
            throw new ContentStoreException(
                "CONTENT_SETTLEMENT_DRAIN_REQUIRED",
                "All resource quotes and settlements must reach a terminal state before content activation.");
        }
    }

    private static void RequireServer(string serverId, ExtractionModeOptions options)
    {
        if (!string.Equals(serverId, options.ServerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStoreException(
                "CONTENT_SERVER_NOT_FOUND",
                "The requested economy-content server is not configured in this instance.");
        }
    }

    private static void EnsureDraftServer(EconomyContentDraft draft, string serverId)
    {
        if (!string.Equals(draft.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStoreException(
                "CONTENT_DRAFT_NOT_FOUND",
                "The content draft does not belong to this server.");
        }
    }

    private static long RequireRevision(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("If-Match", out var values) || values.Count != 1)
        {
            throw new ContentStoreException(
                "CONTENT_REVISION_REQUIRED",
                "If-Match must contain the current numeric draft revision.");
        }
        var value = values[0]?.Trim().Trim('"');
        if (!long.TryParse(value, out var revision) || revision < 0)
        {
            throw new ContentStoreException(
                "CONTENT_REVISION_INVALID",
                "If-Match must contain a non-negative numeric draft revision.");
        }
        return revision;
    }

    private static string RequireIdempotencyKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) ||
            values.Count != 1 ||
            values[0] is not { Length: >= 8 and <= 128 } value ||
            value.Any(char.IsControl))
        {
            throw new ContentStoreException(
                "IDEMPOTENCY_KEY_REQUIRED",
                "Idempotency-Key must contain 8 to 128 non-control characters.");
        }
        return value;
    }

    private static void RequireConfirmation(string? actual, string expected)
    {
        if (!string.Equals(actual?.Trim(), expected, StringComparison.Ordinal))
        {
            throw new ContentStoreException(
                "CONTENT_CONFIRMATION_REQUIRED",
                $"Confirmation must exactly equal '{expected}'.");
        }
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 8 or > 512)
        {
            throw new ContentStoreException(
                "CONTENT_REASON_REQUIRED",
                "A reason containing 8 to 512 characters is required.");
        }
    }

    private static object DraftAudit(EconomyContentDraft draft) => new
    {
        draft.DraftId,
        draft.ServerId,
        draft.Name,
        draft.State,
        draft.BasedOnVersionId,
        draft.Revision,
        draft.ContentHash,
        draft.PublishedVersionId,
        productCount = draft.Definition.Products.Count,
        resourceCount = draft.Definition.Resources.Count,
        zoneCount = draft.Definition.ExchangeZones.Count,
        taskCount = draft.Definition.Tasks.Count
    };

    private static IResult ToError(Exception exception) => exception switch
    {
        ContentValidationException validation => Results.UnprocessableEntity(new
        {
            error = new ApiError(validation.Code, validation.Message),
            validation = validation.Validation
        }),
        ContentStoreException content => Results.Json(
            new ApiError(content.Code, content.Message),
            statusCode: content.Code switch
            {
                "CONTENT_DRAFT_NOT_FOUND" or "CONTENT_VERSION_NOT_FOUND" or
                "CONTENT_POINTER_NOT_FOUND" or "CONTENT_SERVER_NOT_FOUND" => StatusCodes.Status404NotFound,
                "CONTENT_VALIDATION_FAILED" => StatusCodes.Status422UnprocessableEntity,
                "CONTENT_MAINTENANCE_REQUIRED" or "CONTENT_SETTLEMENT_DRAIN_REQUIRED" => StatusCodes.Status423Locked,
                "CONTENT_REVISION_REQUIRED" or "CONTENT_REVISION_INVALID" or
                "CONTENT_CONFIRMATION_REQUIRED" or "CONTENT_REASON_REQUIRED" or
                "CONTENT_BUSINESS_DATE_MISMATCH" or "IDEMPOTENCY_KEY_REQUIRED" => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status409Conflict
            }),
        ArgumentException argument => Results.BadRequest(
            new ApiError("INVALID_ECONOMY_CONTENT", argument.Message)),
        InvalidDataException => Results.Json(
            new ApiError(
                "ECONOMY_CONTENT_STORE_INVALID",
                "The economy content store failed an integrity check."),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        IOException => Results.Json(
            new ApiError(
                "ECONOMY_CONTENT_STORE_UNAVAILABLE",
                "The economy content store is unavailable."),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(
            new ApiError(
                "ECONOMY_CONTENT_REQUEST_FAILED",
                "The economy content request could not be completed."),
            statusCode: StatusCodes.Status500InternalServerError)
    };

    public sealed record CreateContentDraftRequest(
        string Name,
        Guid? BasedOnVersionId,
        EconomyContentDefinition Definition);

    public sealed record UpdateContentDraftRequest(EconomyContentDefinition Definition);

    public sealed record PublishContentRequest(
        DateOnly BusinessDate,
        string Reason,
        string Confirmation);

    public sealed record RollbackContentRequest(
        Guid? TargetVersionId,
        Guid ExpectedCurrentVersionId,
        string Reason,
        string Confirmation);
}

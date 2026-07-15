using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed class ExtractionDeliveryWorker : BackgroundService
{
    private readonly ExtractionCommerceService _commerce;
    private readonly ExtractionModeCoordinator _coordinator;
    private readonly PalDefenderItemGrantAdapter _grantAdapter;
    private readonly PalDefenderCommandQueue _commands;
    private readonly ExtractionDeliveryReceiptStore _receipts;
    private readonly EconomySafetyGate _economySafety;
    private readonly ExtractionModeOptions _options;
    private readonly EconomySafetyOptions _safetyOptions;
    private readonly ILogger<ExtractionDeliveryWorker> _logger;

    public ExtractionDeliveryWorker(
        ExtractionCommerceService commerce,
        ExtractionModeCoordinator coordinator,
        PalDefenderItemGrantAdapter grantAdapter,
        PalDefenderCommandQueue commands,
        ExtractionDeliveryReceiptStore receipts,
        EconomySafetyGate economySafety,
        IOptions<ExtractionModeOptions> options,
        IOptions<EconomySafetyOptions> safetyOptions,
        ILogger<ExtractionDeliveryWorker> logger)
    {
        _commerce = commerce;
        _coordinator = coordinator;
        _grantAdapter = grantAdapter;
        _commands = commands;
        _receipts = receipts;
        _economySafety = economySafety;
        _options = options.Value;
        _safetyOptions = safetyOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(allowNewCommands: false, stoppingToken);
                await ReconcileInFlightAsync(stoppingToken);
                await RefundDefinitiveFailuresAsync(stoppingToken);
                var activeSeason = await _commerce.GetActiveSeasonAsync(
                    _options.ServerId,
                    stoppingToken);
                if (activeSeason is not null)
                {
                    await _coordinator.EnsureSeasonMatchesActiveWorldAsync(activeSeason, stoppingToken);
                    await ProcessPendingAsync(allowNewCommands: true, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The extraction delivery worker iteration failed.");
            }

            await Task.Delay(_options.DeliveryPollMilliseconds, stoppingToken);
        }
    }

    private async Task ProcessPendingAsync(
        bool allowNewCommands,
        CancellationToken cancellationToken)
    {
        var deliveries = allowNewCommands
            ? await _commerce.ListPendingDeliveriesAsync(10, cancellationToken)
            : await _commerce.ListAllPendingDeliveriesAsync(cancellationToken);
        foreach (var delivery in deliveries)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(delivery.PlayerUid) ||
                    string.IsNullOrWhiteSpace(delivery.WorldId))
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_TARGET_IDENTITY_MISSING",
                        "The delivery does not contain a verified PlayerUID and world binding; manual reconciliation is required.",
                        cancellationToken);
                    continue;
                }

                var registration = allowNewCommands
                    ? await _receipts.RegisterAsync(
                        delivery,
                        _safetyOptions.ApprovedGameVersion,
                        _safetyOptions.ApprovedPalDefenderVersion,
                        cancellationToken)
                    : await _receipts.GetAsync(delivery.DeliveryId, cancellationToken);
                if (registration is null)
                {
                    continue;
                }
                if (registration.IdempotencyConflict)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_RECEIPT_IDEMPOTENCY_CONFLICT",
                        "The delivery idempotency key was reused with a different immutable request.",
                        cancellationToken);
                    continue;
                }
                if (registration.Receipt is not null)
                {
                    await ApplyReceiptAsync(registration.Receipt, cancellationToken);
                    continue;
                }

                // Recover commands persisted before a previous worker process could
                // transition the commerce delivery to Dispatching.
                var terminal = await _grantAdapter.TryBuildReceiptAsync(
                    registration.Request,
                    finalizeMissingCommands: false,
                    cancellationToken);
                if (!terminal.Pending && terminal.Receipt is not null)
                {
                    var receipt = await _receipts.SaveReceiptAsync(
                        terminal.Receipt,
                        cancellationToken);
                    await ApplyReceiptAsync(receipt, cancellationToken);
                    continue;
                }
                if (!allowNewCommands)
                {
                    continue;
                }

                using var safetyLease = await _economySafety.AcquireAsync(
                    EconomyWriteFeature.Purchase,
                    EconomySafetyContext.ForDelivery(delivery.PlayerIdentifier),
                    await _commands.GetEconomyLoadAsync(cancellationToken),
                    cancellationToken);
                var accepted = await _grantAdapter.EnsureEnqueuedAsync(
                    delivery,
                    registration.Request,
                    $"shop delivery {delivery.DeliveryId:N}",
                    "extraction-delivery-worker",
                    cancellationToken);
                if (accepted.IdempotencyConflict)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_COMMAND_IDEMPOTENCY_CONFLICT",
                        "A per-item delivery command idempotency key resolved to a different request.",
                        cancellationToken);
                    continue;
                }
                if (accepted.CapacityExceeded)
                {
                    // Some item commands may already be durably accepted. Leave the
                    // immutable request pending and resume only its missing lines.
                    continue;
                }
                if (!accepted.AllAccepted || accepted.CommandIds.Count == 0)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_COMMAND_NOT_ACCEPTED",
                        "The item-grant adapter did not durably accept every delivery line.",
                        cancellationToken);
                    continue;
                }
                _ = await _commerce.MarkDeliveryDispatchedAsync(
                    delivery.DeliveryId,
                    accepted.CommandIds[0],
                    cancellationToken);
            }
            catch (ExtractionModeException exception)
            {
                _logger.LogWarning(
                    "Economy safety gate deferred extraction delivery {DeliveryId}: {Code}.",
                    delivery.DeliveryId,
                    exception.Code);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Failed to dispatch extraction delivery {DeliveryId}.",
                    delivery.DeliveryId);
            }
        }
    }

    private async Task ReconcileInFlightAsync(CancellationToken cancellationToken)
    {
        var deliveries = await _commerce.ListInFlightDeliveriesAsync(25, cancellationToken);
        foreach (var delivery in deliveries)
        {
            try
            {
                var registration = await _receipts.GetAsync(
                    delivery.DeliveryId,
                    cancellationToken);
                if (registration is null)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_RECEIPT_REQUEST_MISSING",
                        "The dispatched delivery has no immutable receipt request; automatic redispatch and refund are disabled.",
                        cancellationToken);
                    continue;
                }
                if (registration.Receipt is not null)
                {
                    await ApplyReceiptAsync(registration.Receipt, cancellationToken);
                    continue;
                }

                var terminal = await _grantAdapter.TryBuildReceiptAsync(
                    registration.Request,
                    finalizeMissingCommands: true,
                    cancellationToken);
                if (terminal.Pending || terminal.Receipt is null)
                {
                    continue;
                }
                var receipt = await _receipts.SaveReceiptAsync(
                    terminal.Receipt,
                    cancellationToken);
                await ApplyReceiptAsync(receipt, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Failed to reconcile extraction delivery {DeliveryId}.",
                    delivery.DeliveryId);
            }
        }
    }

    private async Task ApplyReceiptAsync(
        ExtractionDeliveryReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        switch (receipt.Outcome)
        {
            case ExtractionDeliveryReceiptOutcome.Succeeded:
                _ = await _commerce.MarkDeliveryDeliveredAsync(
                    receipt.DeliveryId,
                    cancellationToken);
                break;
            case ExtractionDeliveryReceiptOutcome.Failed:
                _ = await _commerce.MarkDeliveryFailedAsync(
                    receipt.DeliveryId,
                    "PALDEFENDER_DELIVERY_REJECTED_BEFORE_MUTATION",
                    "Every item-grant command failed before a mutation was acknowledged.",
                    cancellationToken);
                break;
            case ExtractionDeliveryReceiptOutcome.Partial:
                _ = await _commerce.MarkDeliveryUncertainAsync(
                    receipt.DeliveryId,
                    "DELIVERY_PARTIAL_GRANTED",
                    "The immutable receipt proves only a partial grant; manual reconciliation is required and no automatic full refund is allowed.",
                    cancellationToken);
                break;
            case ExtractionDeliveryReceiptOutcome.Uncertain:
                _ = await _commerce.MarkDeliveryUncertainAsync(
                    receipt.DeliveryId,
                    "DELIVERY_RECEIPT_UNCERTAIN",
                    "The item-grant response was malformed, missing, or otherwise ambiguous; automatic redispatch and refund are disabled.",
                    cancellationToken);
                break;
            default:
                throw new InvalidDataException("The delivery receipt outcome is unsupported.");
        }
    }

    private async Task RefundDefinitiveFailuresAsync(CancellationToken cancellationToken)
    {
        if (!_options.RefundDefinitiveDeliveryFailures)
        {
            return;
        }

        var orders = await _commerce.ListBlockingOrdersAsync(cancellationToken);
        foreach (var order in orders.Where(order => order.State == ShopOrderState.DeliveryFailed))
        {
            try
            {
                var result = await _commerce.RefundFailedOrderAsync(
                    order.OrderId,
                    $"auto-refund-{order.OrderId:N}",
                    "extraction-delivery-worker",
                    "Every durable item-grant command failed before mutation; restore the original charge.",
                    cancellationToken);
                if (result.ErrorCode is not null)
                {
                    _logger.LogWarning(
                        "Automatic extraction refund for order {OrderId} remains pending: {ErrorCode}: {ErrorMessage}",
                        order.OrderId,
                        result.ErrorCode,
                        result.ErrorMessage);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Automatic extraction refund for order {OrderId} failed and will be retried.",
                    order.OrderId);
            }
        }
    }
}

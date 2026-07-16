using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ControlApi.Content;

/// <summary>
/// Replays durable terminal settlement/order facts that may have committed
/// immediately before a crash in the independent task projection. The task
/// store event id and wallet reward idempotency key make every pass safe.
/// </summary>
public sealed class ReliableTaskProjectionRecoveryWorker : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private readonly ExtractionRunStore _runs;
    private readonly ExtractionCommerceService _commerce;
    private readonly ReliableTaskRuntimeService _tasks;
    private readonly ExtractionModeOptions _options;
    private readonly ILogger<ReliableTaskProjectionRecoveryWorker> _logger;

    public ReliableTaskProjectionRecoveryWorker(
        ExtractionRunStore runs,
        ExtractionCommerceService commerce,
        ReliableTaskRuntimeService tasks,
        IOptions<ExtractionModeOptions> options,
        ILogger<ReliableTaskProjectionRecoveryWorker> logger)
    {
        _runs = runs;
        _commerce = commerce;
        _tasks = tasks;
        _options = options.Value;
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
                await ReplayMissingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                using var scope = ControlPlaneLog.BeginWorker(
                    _logger,
                    nameof(ReliableTaskProjectionRecoveryWorker),
                    "projection.scan.failure");
                _logger.LogSafeError(exception, "Reliable task projection recovery scan failed.");
            }
            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    private async Task ReplayMissingAsync(CancellationToken cancellationToken)
    {
        _ = await _tasks.RecoverPendingCurrencyRewardsAsync(cancellationToken);
        var runs = await _runs.ListAsync(null, null, 1000, cancellationToken);
        foreach (var run in runs.Where(run => run.State == ExtractionSettlementState.Settled))
        {
            using var scope = ControlPlaneLog.BeginWorker(
                _logger,
                nameof(ReliableTaskProjectionRecoveryWorker),
                "projection.settlement",
                run.RunId,
                _options.ServerId);
            if (await _tasks.HasResourceSettlementEventAsync(run.RunId, cancellationToken))
            {
                continue;
            }
            try
            {
                _ = await _tasks.RecordResourceSettlementAsync(run, cancellationToken);
            }
            catch (ReliableTaskException exception)
                when (exception.Code == "TASK_CONTENT_VERSION_NOT_FOUND")
            {
                _logger.LogDebug(
                    "Settlement {RunId} predates retained task content evidence and was not backfilled.",
                    run.RunId);
            }
            catch (Exception exception)
            {
                _logger.LogSafeWarning(
                    exception,
                    "Could not recover reliable task event for settlement {RunId}; it will be retried.",
                    run.RunId);
            }
        }

        var orders = await _commerce.ListOrdersAsync(null, null, 1000, cancellationToken);
        foreach (var order in orders.Where(order => order.State == ShopOrderState.Delivered))
        {
            using var scope = ControlPlaneLog.BeginWorker(
                _logger,
                nameof(ReliableTaskProjectionRecoveryWorker),
                "projection.order",
                order.OrderId,
                _options.ServerId);
            if (await _tasks.HasDeliveredOrderEventAsync(order.OrderId, cancellationToken))
            {
                continue;
            }
            try
            {
                _ = await _tasks.RecordDeliveredOrderAsync(order, cancellationToken);
            }
            catch (ReliableTaskException exception)
                when (exception.Code == "TASK_CONTENT_VERSION_NOT_FOUND")
            {
                _logger.LogDebug(
                    "Order {OrderId} predates retained task content evidence and was not backfilled.",
                    order.OrderId);
            }
            catch (Exception exception)
            {
                _logger.LogSafeWarning(
                    exception,
                    "Could not recover reliable task event for delivered order {OrderId}; it will be retried.",
                    order.OrderId);
            }
        }
    }
}

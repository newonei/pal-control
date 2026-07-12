using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed class ExtractionDeliveryWorker : BackgroundService
{
    private static readonly TimeSpan ReadbackDeadline = TimeSpan.FromSeconds(60);

    private readonly ExtractionCommerceService _commerce;
    private readonly ExtractionModeCoordinator _coordinator;
    private readonly IShopDeliveryCommandDispatcher _dispatcher;
    private readonly PalDefenderCommandQueue _commands;
    private readonly PalDefenderRestClient _palDefender;
    private readonly ExtractionDeliveryEvidenceStore _evidenceStore;
    private readonly ExtractionModeOptions _options;
    private readonly ILogger<ExtractionDeliveryWorker> _logger;

    public ExtractionDeliveryWorker(
        ExtractionCommerceService commerce,
        ExtractionModeCoordinator coordinator,
        IShopDeliveryCommandDispatcher dispatcher,
        PalDefenderCommandQueue commands,
        PalDefenderRestClient palDefender,
        ExtractionDeliveryEvidenceStore evidenceStore,
        IOptions<ExtractionModeOptions> options,
        ILogger<ExtractionDeliveryWorker> logger)
    {
        _commerce = commerce;
        _coordinator = coordinator;
        _dispatcher = dispatcher;
        _commands = commands;
        _palDefender = palDefender;
        _evidenceStore = evidenceStore;
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
                var existingCommand = await _commands.GetStatusByIdempotencyKeyAsync(
                    delivery.ServerId,
                    delivery.IdempotencyKey,
                    cancellationToken);
                if (existingCommand is null && !allowNewCommands)
                {
                    continue;
                }
                var evidence = await _evidenceStore.GetAsync(delivery.DeliveryId, cancellationToken);
                if (existingCommand is null)
                {
                    if (evidence?.CommandId is not null)
                    {
                        await _commerce.MarkDeliveryUncertainAsync(
                            delivery.DeliveryId,
                            "DELIVERY_COMMAND_RECORD_MISSING",
                            "发货证据已关联命令，但持久化命令记录不存在；禁止重新发货。",
                            cancellationToken);
                        continue;
                    }
                    var totals = await ReadInventoryTotalsAsync(
                        delivery.PlayerIdentifier,
                        cancellationToken);
                    if (totals is null)
                    {
                        continue;
                    }
                    var baseline = delivery.Items
                        .Select(item => item.ItemId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            itemId => itemId,
                            itemId => totals.GetValueOrDefault(itemId),
                            StringComparer.OrdinalIgnoreCase);
                    evidence = await _evidenceStore.ReplaceBaselineAsync(
                        delivery.DeliveryId,
                        baseline,
                        cancellationToken);
                }
                else if (evidence is null)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_BASELINE_MISSING",
                        "持久化发货命令存在，但对应的发货前背包基线不存在。",
                        cancellationToken);
                    continue;
                }
                else if (evidence.CommandId is Guid evidenceCommandId &&
                         evidenceCommandId != existingCommand.CommandId)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_EVIDENCE_COMMAND_MISMATCH",
                        "发货证据关联了另一个命令，已停止自动处理。",
                        cancellationToken);
                    continue;
                }

                var accepted = await _dispatcher.EnqueueAsync(
                    delivery,
                    $"商城订单 {delivery.OrderId:N} 第 {delivery.Attempt} 次发货",
                    "extraction-delivery-worker",
                    cancellationToken);
                if (accepted.IdempotencyConflict || !accepted.Accepted || accepted.CommandId is null)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        accepted.ErrorCode ?? "DELIVERY_COMMAND_NOT_ACCEPTED",
                        accepted.ErrorMessage ?? "发货命令未被可靠接收，禁止自动重试。",
                        cancellationToken);
                    continue;
                }
                if (existingCommand is not null &&
                    existingCommand.CommandId != accepted.CommandId.Value)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_IDEMPOTENCY_COMMAND_MISMATCH",
                        "相同发货幂等键解析到了不同命令，已停止自动处理。",
                        cancellationToken);
                    continue;
                }
                _ = await _evidenceStore.AttachCommandAsync(
                    delivery.DeliveryId,
                    accepted.CommandId.Value,
                    cancellationToken);
                _ = await _commerce.MarkDeliveryDispatchedAsync(
                    delivery.DeliveryId,
                    accepted.CommandId.Value,
                    cancellationToken);
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
                if (delivery.CommandId is not Guid commandId)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_COMMAND_ID_MISSING",
                        "发货已进入派发状态，但缺少命令编号。",
                        cancellationToken);
                    continue;
                }

                var command = await _commands.GetStatusAsync(commandId, cancellationToken);
                if (command is null)
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        "DELIVERY_COMMAND_NOT_FOUND",
                        "持久化命令记录不存在，无法证明发货结果。",
                        cancellationToken);
                    continue;
                }
                if (command.State is "accepted" or "dispatched")
                {
                    continue;
                }
                if (command.State == "failed")
                {
                    _ = await _commerce.MarkDeliveryFailedAsync(
                        delivery.DeliveryId,
                        command.Error?.Code ?? "PALDEFENDER_DELIVERY_FAILED",
                        command.Error?.Message ?? "PalDefender 明确拒绝了发货命令。",
                        cancellationToken);
                    continue;
                }
                if (command.State != "succeeded")
                {
                    await _commerce.MarkDeliveryUncertainAsync(
                        delivery.DeliveryId,
                        command.Error?.Code ?? "PALDEFENDER_DELIVERY_UNCERTAIN",
                        command.Error?.Message ?? "PalDefender 发货结果不确定，禁止自动重试和退款。",
                        cancellationToken);
                    continue;
                }

                await VerifySucceededDeliveryAsync(delivery, cancellationToken);
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
                    "PalDefender 明确拒绝发货，自动退回原货币。",
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

    private async Task VerifySucceededDeliveryAsync(
        ShopDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.CommandId is not Guid commandId)
        {
            await _commerce.MarkDeliveryUncertainAsync(
                delivery.DeliveryId,
                "DELIVERY_COMMAND_ID_MISSING",
                "发货成功回读缺少命令编号。",
                cancellationToken);
            return;
        }
        var evidence = await _evidenceStore.GetAsync(delivery.DeliveryId, cancellationToken);
        if (evidence is null)
        {
            await _commerce.MarkDeliveryUncertainAsync(
                delivery.DeliveryId,
                "DELIVERY_BASELINE_MISSING",
                "发货前背包基线不存在，无法证明本次物品增量。",
                cancellationToken);
            return;
        }
        if (evidence.CommandId is null)
        {
            evidence = await _evidenceStore.AttachCommandAsync(
                delivery.DeliveryId,
                commandId,
                cancellationToken);
        }
        else if (evidence.CommandId != commandId)
        {
            await _commerce.MarkDeliveryUncertainAsync(
                delivery.DeliveryId,
                "DELIVERY_EVIDENCE_COMMAND_MISMATCH",
                "发货前基线与当前命令编号不一致。",
                cancellationToken);
            return;
        }
        var order = await _commerce.GetOrderAsync(delivery.OrderId, cancellationToken);
        if (order is null)
        {
            await _commerce.MarkDeliveryUncertainAsync(
                delivery.DeliveryId,
                "DELIVERY_ORDER_MISSING",
                "发货订单不存在，无法完成背包回读。",
                cancellationToken);
            return;
        }

        Dictionary<string, long> expectedDelta = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in order.Lines)
            {
                foreach (var grant in line.ItemGrants)
                {
                    expectedDelta[grant.ItemId] = checked(
                        expectedDelta.GetValueOrDefault(grant.ItemId) +
                        checked((long)grant.Quantity * line.Quantity));
                }
            }
        }
        catch (OverflowException)
        {
            await _commerce.MarkDeliveryUncertainAsync(
                delivery.DeliveryId,
                "DELIVERY_READBACK_OVERFLOW",
                "发货数量超出可验证范围。",
                cancellationToken);
            return;
        }

        if (evidence.VerifiedItemTotals is not null &&
            ProvesExpectedDelta(
                evidence.BaselineItemTotals,
                evidence.VerifiedItemTotals,
                expectedDelta))
        {
            _ = await _commerce.MarkDeliveryDeliveredAsync(delivery.DeliveryId, cancellationToken);
            return;
        }

        var current = await ReadInventoryTotalsAsync(order.PlayerIdentifier, cancellationToken);
        if (current is null)
        {
            if (DateTimeOffset.UtcNow - evidence.CapturedAt >= ReadbackDeadline)
            {
                await _commerce.MarkDeliveryUncertainAsync(
                    delivery.DeliveryId,
                    "DELIVERY_READBACK_UNAVAILABLE",
                    "PalDefender 已接受发货，但 60 秒内无法读取玩家背包。",
                    cancellationToken);
            }
            return;
        }

        var verifiedTotals = expectedDelta.Keys.ToDictionary(
            itemId => itemId,
            itemId => current.GetValueOrDefault(itemId),
            StringComparer.OrdinalIgnoreCase);
        evidence = await _evidenceStore.SaveVerificationAsync(
            delivery.DeliveryId,
            commandId,
            verifiedTotals,
            cancellationToken);
        if (ProvesExpectedDelta(
                evidence.BaselineItemTotals,
                verifiedTotals,
                expectedDelta))
        {
            _ = await _commerce.MarkDeliveryDeliveredAsync(delivery.DeliveryId, cancellationToken);
            return;
        }
        if (DateTimeOffset.UtcNow - evidence.CapturedAt >= ReadbackDeadline)
        {
            await _commerce.MarkDeliveryUncertainAsync(
                delivery.DeliveryId,
                "DELIVERY_READBACK_MISMATCH",
                "PalDefender 返回成功，但背包增量在 60 秒内未被证明；禁止自动重发。",
                cancellationToken);
        }
    }

    private static bool ProvesExpectedDelta(
        IReadOnlyDictionary<string, long> baseline,
        IReadOnlyDictionary<string, long> readback,
        IReadOnlyDictionary<string, long> expectedDelta)
    {
        try
        {
            return expectedDelta.All(expected =>
                readback.GetValueOrDefault(expected.Key) >= checked(
                    baseline.GetValueOrDefault(expected.Key) + expected.Value));
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private async Task<Dictionary<string, long>?> ReadInventoryTotalsAsync(
        string playerIdentifier,
        CancellationToken cancellationToken)
    {
        var response = await _palDefender.GetAsync(
            $"items/{Uri.EscapeDataString(playerIdentifier)}",
            cancellationToken);
        if (!response.IsSuccess || response.Json is null)
        {
            return null;
        }
        Dictionary<string, long> totals = new(StringComparer.OrdinalIgnoreCase);
        CollectItemTotals(response.Json, totals);
        return totals;
    }

    private static void CollectItemTotals(JsonNode node, IDictionary<string, long> totals)
    {
        if (node is JsonObject value)
        {
            var itemId = GetString(value, "ItemID") ?? GetString(value, "ItemId");
            var count = GetInt64(value, "Count");
            if (!string.IsNullOrWhiteSpace(itemId) && count is > 0)
            {
                totals.TryGetValue(itemId, out var current);
                totals[itemId] = checked(current + count.Value);
            }
            foreach (var property in value)
            {
                if (property.Value is not null)
                {
                    CollectItemTotals(property.Value, totals);
                }
            }
            return;
        }
        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    CollectItemTotals(child, totals);
                }
            }
        }
    }

    private static string? GetString(JsonObject value, string name)
    {
        var node = GetProperty(value, name);
        return node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static long? GetInt64(JsonObject value, string name)
    {
        if (GetProperty(value, name) is not JsonValue jsonValue)
        {
            return null;
        }
        if (jsonValue.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }
        if (jsonValue.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }
        return jsonValue.TryGetValue<string>(out var text) &&
               long.TryParse(text, out var parsed)
            ? parsed
            : null;
    }

    private static JsonNode? GetProperty(JsonObject value, string name) =>
        value.FirstOrDefault(property =>
            string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

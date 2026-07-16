using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public static class PlayerNotificationSourceProjector
{
    public static PlayerNotificationSource? FromOrder(
        ShopOrder order,
        ExtractionDeliveryReceiptOutcome? receiptOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        var partial = order.State == ShopOrderState.DeliveryUncertain &&
            receiptOutcome == ExtractionDeliveryReceiptOutcome.Partial;
        var projection = order.State switch
        {
            ShopOrderState.Delivered => (
                Type: "order-delivery",
                State: "delivered",
                Severity: "success",
                Title: "战备物品已送达",
                Message: "订单中的战备物品已由背包回读确认送达。"),
            ShopOrderState.DeliveryFailed => (
                Type: "order-delivery",
                State: "failed",
                Severity: "error",
                Title: "战备发货失败",
                Message: "本次发货已停止，不会自动重复发货；退款结果请以资金账本为准。"),
            ShopOrderState.DeliveryUncertain when partial => (
                Type: "reconciliation",
                State: "partial",
                Severity: "warning",
                Title: "战备仅部分到账，需要核对",
                Message: "系统已停止自动发货与退款。请勿重复购买或重复操作，等待管理员核对。"),
            ShopOrderState.DeliveryUncertain => (
                Type: "reconciliation",
                State: "uncertain",
                Severity: "warning",
                Title: "战备到账结果待核对",
                Message: "结果无法安全确认，系统不会自动重试或退款。请勿重复购买，等待管理员核对。"),
            ShopOrderState.Refunded => (
                Type: "order-delivery",
                State: "refunded",
                Severity: "info",
                Title: "订单款项已退回",
                Message: "该订单已完成退款；最终余额与明细请以资金账本为准。"),
            _ => default
        };
        if (projection == default)
        {
            return null;
        }
        var receiptState = receiptOutcome?.ToString().ToLowerInvariant() ?? "none";
        var eventKind = projection.Type == "reconciliation"
            ? "order-reconciliation"
            : "order";
        return new PlayerNotificationSource(
            order.AccountId,
            order.SeasonId,
            order.PlayerIdentifier,
            projection.Type,
            order.OrderId.ToString("D"),
            $"player-notification-v1:account:{order.AccountId:N}:{eventKind}:{order.OrderId:N}",
            Fingerprint($"order\n{order.OrderId:D}\n{order.State}\n{receiptState}\n{order.UpdatedAt:O}"),
            projection.State,
            projection.Severity,
            projection.Title,
            projection.Message,
            order.UpdatedAt);
    }

    public static PlayerNotificationSource? FromRun(ExtractionSettlementRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var projection = run.State switch
        {
            ExtractionSettlementState.Settled => (
                Type: "resource-settlement",
                State: "settled",
                Severity: "success",
                Title: "资源兑换已结算",
                Message: $"白名单资源已安全结算，{run.TotalValue:N0} 张周券已按账本入账。"),
            ExtractionSettlementState.Failed => (
                Type: "resource-settlement",
                State: "failed",
                Severity: "error",
                Title: "资源兑换失败",
                Message: "本次兑换未完成，系统不会自动重复结算；请查看兑换记录中的安全指引。"),
            ExtractionSettlementState.Uncertain => (
                Type: "reconciliation",
                State: "uncertain",
                Severity: "warning",
                Title: "资源兑换结果待核对",
                Message: "物品扣除或入账结果无法安全确认。请勿再次兑换或重复提交，等待管理员核对。"),
            ExtractionSettlementState.Cancelled => (
                Type: "resource-settlement",
                State: "cancelled",
                Severity: "info",
                Title: "资源兑换已取消",
                Message: "该次资源兑换已取消，没有再次执行结算动作。"),
            ExtractionSettlementState.Expired => (
                Type: "resource-settlement",
                State: "expired",
                Severity: "info",
                Title: "资源兑换报价已过期",
                Message: "旧报价已失效且不会扣除物品；需要时可在兑换点重新扫描。"),
            _ => default
        };
        if (projection == default)
        {
            return null;
        }
        var eventKind = projection.Type == "reconciliation"
            ? "run-reconciliation"
            : "run";
        return new PlayerNotificationSource(
            run.AccountId,
            run.SeasonId,
            run.UserId,
            projection.Type,
            run.RunId.ToString("D"),
            $"player-notification-v1:account:{run.AccountId:N}:{eventKind}:{run.RunId:N}",
            Fingerprint($"run\n{run.RunId:D}\n{run.Revision}\n{run.State}\n{run.UpdatedAt:O}"),
            projection.State,
            projection.Severity,
            projection.Title,
            projection.Message,
            run.SettledAt ?? run.UpdatedAt);
    }

    public static PlayerNotificationSource FromSeason(
        ExtractionAccount account,
        PlayerSeasonSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(settlement);
        var rewardsComplete = settlement.RewardState == "completed" &&
            settlement.PermanentRewards.All(reward =>
                reward.DeliveryState is "paid" or "cancelled");
        var voucherComplete = settlement.VoucherExpiry.JobState == "completed" &&
            settlement.VoucherExpiry.ItemState is "expired" or "not-applicable";
        var state = (rewardsComplete, voucherComplete) switch
        {
            (true, true) => "completed",
            (true, false) => "reward-completed",
            (false, true) => "voucher-expired",
            _ => "frozen"
        };
        var paid = settlement.PermanentRewards
            .Where(reward => reward.DeliveryState == "paid")
            .Sum(reward => reward.MarketCoin);
        var presentation = state switch
        {
            "completed" => (
                Severity: "success",
                Title: "本周结算已完成",
                Message: $"冻结成绩、永久币奖励与周券清零均已核对：永久币入账 {paid:N0}，周券过期 {settlement.VoucherExpiry.ExpiredAmount:N0}。"),
            "reward-completed" => (
                Severity: "success",
                Title: "本周永久币奖励已完成",
                Message: $"冻结名次对应的永久商域币已核对，当前确认入账 {paid:N0}。"),
            "voucher-expired" => (
                Severity: "info",
                Title: "本周周券清零已完成",
                Message: $"本周周券已按规则过期 {settlement.VoucherExpiry.ExpiredAmount:N0} 张；永久币不受影响。"),
            _ => (
                Severity: "info",
                Title: "本周成绩已冻结",
                Message: "本周资源与任务成绩已写入不可变快照，临时数据不会再改变冻结名次。")
        };
        var versionPayload = JsonSerializer.Serialize(new
        {
            state,
            settlement.RewardState,
            settlement.Participation.Participating,
            ResourceRank = settlement.Participation.Resource.Rank,
            TaskRank = settlement.Participation.Task.Rank,
            settlement.VoucherExpiry.JobState,
            settlement.VoucherExpiry.ItemState,
            settlement.VoucherExpiry.ExpiredAmount,
            Rewards = settlement.PermanentRewards.Select(reward => new
            {
                reward.RewardKey,
                reward.DecisionState,
                reward.DeliveryState,
                reward.MarketCoin
            })
        });
        var occurredAt = new[]
        {
            settlement.FrozenAt,
            settlement.VoucherExpiry.CompletedAt ?? settlement.FrozenAt
        }
        .Concat(settlement.PermanentRewards
            .Where(reward => reward.CompletedAt is not null)
            .Select(reward => reward.CompletedAt!.Value))
        .Max();
        return new PlayerNotificationSource(
            account.AccountId,
            settlement.SeasonId,
            account.ExternalUserId,
            "season-end",
            settlement.SeasonId.ToString("D"),
            $"player-notification-v1:account:{account.AccountId:N}:season:{settlement.SeasonId:N}",
            Fingerprint(versionPayload),
            state,
            presentation.Severity,
            presentation.Title,
            presentation.Message,
            occurredAt);
    }

    public static PlayerNotificationSource FromSeasonReconciliation(
        ExtractionAccount account,
        Guid seasonId,
        DateTimeOffset occurredAt) => new(
            account.AccountId,
            seasonId,
            account.ExternalUserId,
            "reconciliation",
            seasonId.ToString("D"),
            $"player-notification-v1:account:{account.AccountId:N}:season-reconciliation:{seasonId:N}",
            Fingerprint($"season-reconciliation\n{account.AccountId:D}\n{seasonId:D}"),
            "reconciliation-required",
            "warning",
            "本周结算需要人工核对",
            "个人结算证据暂时无法完整验证。请勿重复购买或重复结算，等待管理员核对。",
            occurredAt);

    private static string Fingerprint(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class PlayerNotificationProjectionService
{
    private readonly PlayerNotificationStore _store;
    private readonly IPlayerGameNotificationDispatcher _gameDispatcher;
    private readonly bool _gameDeliveryEnabled;

    public PlayerNotificationProjectionService(
        PlayerNotificationStore store,
        IPlayerGameNotificationDispatcher gameDispatcher,
        IOptions<PlayerNotificationOptions> options)
    {
        _store = store;
        _gameDispatcher = gameDispatcher;
        _gameDeliveryEnabled = options.Value.GameDeliveryEnabled;
    }

    public async Task<PlayerNotificationRecord> ProjectAsync(
        PlayerNotificationSource source,
        CancellationToken cancellationToken)
    {
        // Game delivery is an explicit operational opt-in. Projecting the feed
        // while it is disabled records the source version as not-requested.
        // PlayerNotificationStore intentionally leaves an identical source
        // version unchanged, so enabling later cannot sweep old feed rows back
        // into the delivery queue.
        var effectiveSource = source with
        {
            RequestGameDelivery =
                _gameDeliveryEnabled && source.RequestGameDelivery
        };
        var upsert = await _store.UpsertAsync(effectiveSource, cancellationToken);
        var current = upsert.Notification;
        if (!_gameDeliveryEnabled ||
            !effectiveSource.RequestGameDelivery ||
            current.GameState is not ("pending" or "queued"))
        {
            return current;
        }

        var dispatched = await _gameDispatcher.DispatchOrReconcileAsync(
            new PlayerGameNotificationDispatch(
                current.NotificationId,
                current.SourceVersion,
                current.SourceEventKey,
                effectiveSource.TargetPlayerId,
                current.Title,
                current.Message),
            current.GameNotificationId,
            current.GameCommandId,
            cancellationToken);
        await _store.UpdateGameStateAsync(
            current.NotificationId,
            current.SourceVersion,
            dispatched,
            cancellationToken);
        return (await _store.GetAsync(current.NotificationId, cancellationToken)) ?? current;
    }
}

public sealed class PlayerGameNotificationDispatcher : IPlayerGameNotificationDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ExtractionModeCoordinator _coordinator;
    private readonly InGameNotificationCapabilityService _capabilities;
    private readonly InGameNotificationStore _notifications;
    private readonly InGameNotificationCommandQueue _commands;

    public PlayerGameNotificationDispatcher(
        ExtractionModeCoordinator coordinator,
        InGameNotificationCapabilityService capabilities,
        InGameNotificationStore notifications,
        InGameNotificationCommandQueue commands)
    {
        _coordinator = coordinator;
        _capabilities = capabilities;
        _notifications = notifications;
        _commands = commands;
    }

    public async Task<PlayerGameNotificationDispatchResult> DispatchOrReconcileAsync(
        PlayerGameNotificationDispatch request,
        Guid? existingNotificationId,
        Guid? existingCommandId,
        CancellationToken cancellationToken)
    {
        if (existingCommandId is Guid commandId)
        {
            var status = await _commands.GetStatusAsync(commandId, cancellationToken);
            return status is null
                ? new PlayerGameNotificationDispatchResult(
                    "uncertain",
                    existingNotificationId,
                    commandId,
                    "GAME_NOTIFICATION_COMMAND_MISSING")
                : FromCommand(status, existingNotificationId);
        }
        if (!_commands.IsReady)
        {
            return new PlayerGameNotificationDispatchResult(
                "pending",
                existingNotificationId,
                null,
                "GAME_NOTIFICATION_QUEUE_STARTING");
        }

        var probe = await _capabilities.ProbeAsync(_coordinator.ServerId, cancellationToken);
        if (!probe.Ready || probe.Probe is null)
        {
            return new PlayerGameNotificationDispatchResult(
                "blocked",
                null,
                null,
                probe.Error?.Code ?? "GAME_NOTIFICATION_CAPABILITY_UNAVAILABLE");
        }
        if (!probe.Probe.SupportedAudiences.Contains("players", StringComparer.Ordinal))
        {
            return new PlayerGameNotificationDispatchResult(
                "blocked",
                null,
                null,
                "GAME_NOTIFICATION_PLAYERS_AUDIENCE_UNAVAILABLE");
        }
        var preset = probe.Probe.SupportedPresets.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, "player-message", StringComparison.Ordinal));
        if (preset is null)
        {
            return new PlayerGameNotificationDispatchResult(
                "blocked",
                null,
                null,
                "GAME_NOTIFICATION_PLAYER_MESSAGE_PRESET_UNAVAILABLE");
        }

        var input = new InGameNotificationInput
        {
            SchemaVersion = "1",
            Template = new InGameNotificationTemplate
            {
                Preset = preset.Name,
                Parameters = JsonSerializer.SerializeToElement(
                    new { title = request.Title, message = request.Message },
                    JsonOptions)
            },
            Audience = new InGameNotificationAudience
            {
                Type = "players",
                Ids = [request.TargetPlayerId]
            },
            Reason = "player-notification-projection-v1"
        };
        var validation = InGameNotificationContract.ValidateShape(input) ??
            InGameNotificationContract.ValidateAgainstProbe(input, probe.Probe);
        if (validation is not null)
        {
            return new PlayerGameNotificationDispatchResult(
                "blocked",
                null,
                null,
                validation.Code);
        }

        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.SourceEventKey}\n{request.SourceVersion}")))[..32];
        var create = await _notifications.CreateAsync(
            _coordinator.ServerId,
            $"player-notification-{keyHash}",
            input,
            "player-notification-projection",
            cancellationToken);
        if (create.IdempotencyConflict || create.Notification is null)
        {
            return new PlayerGameNotificationDispatchResult(
                "blocked",
                null,
                null,
                "GAME_NOTIFICATION_CREATE_CONFLICT");
        }
        var enqueue = await _commands.EnqueueAsync(
            _coordinator.ServerId,
            create.Notification,
            $"player-notification-dispatch-{keyHash}",
            "player-notification-projection",
            cancellationToken);
        if (enqueue.IdempotencyConflict || enqueue.NotificationConflict || enqueue.Command is null)
        {
            return new PlayerGameNotificationDispatchResult(
                "blocked",
                create.Notification.NotificationId,
                enqueue.Command?.CommandId,
                "GAME_NOTIFICATION_DISPATCH_CONFLICT");
        }
        return FromCommand(enqueue.Command, create.Notification.NotificationId);
    }

    private static PlayerGameNotificationDispatchResult FromCommand(
        CommandStatus command,
        Guid? notificationId)
    {
        var state = command.State switch
        {
            "accepted" or "dispatched" => "queued",
            "succeeded" => "sent",
            "uncertain" => "uncertain",
            "failed" when command.Error?.Code.StartsWith(
                "NATIVE_NOTIFICATION_",
                StringComparison.Ordinal) == true => "blocked",
            "failed" => "failed",
            _ => "queued"
        };
        return new PlayerGameNotificationDispatchResult(
            state,
            notificationId,
            command.CommandId,
            command.Error?.Code);
    }
}

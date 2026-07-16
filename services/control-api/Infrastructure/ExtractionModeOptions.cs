using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed class ExtractionModeOptions
{
    private const long MaximumWebSafeInteger = 9_007_199_254_740_991;
    public bool Enabled { get; init; }
    public string ServerId { get; init; } = "local";
    public string TimeZoneId { get; init; } = "Asia/Shanghai";
    public DayOfWeek WeeklyResetDay { get; init; } = DayOfWeek.Monday;
    public int WeeklyResetHour { get; init; } = 4;
    public int DailyRefreshHour { get; init; } = 4;
    public long InitialMarketCoin { get; init; }
    public long InitialSeasonVoucher { get; init; }
    public string BootstrapPolicyVersion { get; init; } = "legacy-v1";
    public int DeliveryPollMilliseconds { get; init; } = 1_000;
    public bool RefundDefinitiveDeliveryFailures { get; init; } = true;
    public int ExtractionQuoteSeconds { get; init; } = 30;
    public int ExtractionPositionSampleMilliseconds { get; init; } = 2_000;
    public int SettlementLeaseHeartbeatSeconds { get; init; } = 10;
    public int SettlementQueueCapacity { get; init; } = 32;
    public int SettlementWorkerCount { get; init; } = 4;
    public int SettlementQueueOperationTimeoutSeconds { get; init; } = 180;
    // Keep this empty in code. ConfigurationBinder appends configured collection
    // entries to an existing default collection, which used to create a phantom
    // duplicate of the appsettings.json development zone at runtime.
    public IReadOnlyList<ExtractionZoneOptions> ExtractionZones { get; init; } = [];

    public bool IsValid(out string? error)
    {
        if (string.IsNullOrWhiteSpace(ServerId) || ServerId.Length > 64)
        {
            error = "ExtractionMode:ServerId must contain 1 to 64 characters.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(TimeZoneId))
        {
            error = "ExtractionMode:TimeZoneId is required.";
            return false;
        }
        try
        {
            _ = ResolveTimeZone();
        }
        catch (TimeZoneNotFoundException)
        {
            error = $"ExtractionMode:TimeZoneId '{TimeZoneId}' was not found.";
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            error = $"ExtractionMode:TimeZoneId '{TimeZoneId}' is invalid.";
            return false;
        }
        if (WeeklyResetHour is < 0 or > 23 || DailyRefreshHour is < 0 or > 23)
        {
            error = "Extraction reset hours must be between 0 and 23.";
            return false;
        }
        if (InitialMarketCoin is < 0 or > MaximumWebSafeInteger ||
            InitialSeasonVoucher is < 0 or > MaximumWebSafeInteger)
        {
            error = "Initial extraction wallet balances must fit the exact integer range supported by the web console.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(BootstrapPolicyVersion) ||
            BootstrapPolicyVersion.Length > 32 ||
            BootstrapPolicyVersion.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            error = "ExtractionMode:BootstrapPolicyVersion must contain 1 to 32 ASCII letters, digits, '-', '_' or '.'.";
            return false;
        }
        if (Enabled && string.Equals(
                BootstrapPolicyVersion,
                "legacy-v1",
                StringComparison.Ordinal) &&
            (InitialMarketCoin != 1_000 || InitialSeasonVoucher != 300))
        {
            error = "The frozen legacy-v1 bootstrap policy must remain 1000 MarketCoin and 300 SeasonVoucher; use explicit audited adjustments for a policy change.";
            return false;
        }
        if (Enabled && !string.Equals(
                BootstrapPolicyVersion,
                "legacy-v1",
                StringComparison.Ordinal) &&
            (InitialMarketCoin != 0 || InitialSeasonVoucher != 0))
        {
            error = "Non-legacy bootstrap policies must grant zero currency; publish new-player rewards through an explicit audited migration or activity.";
            return false;
        }
        if (DeliveryPollMilliseconds is < 250 or > 30_000)
        {
            error = "ExtractionMode:DeliveryPollMilliseconds must be between 250 and 30000.";
            return false;
        }
        if (ExtractionQuoteSeconds is < 10 or > 300 ||
            ExtractionPositionSampleMilliseconds is < 500 or > 10_000)
        {
            error = "Extraction quote duration or position sampling interval is invalid.";
            return false;
        }
        if (SettlementLeaseHeartbeatSeconds is < 2 or > 30 ||
            SettlementQueueCapacity is < 1 or > 512 ||
            SettlementWorkerCount is < 1 or > 32 ||
            SettlementWorkerCount > SettlementQueueCapacity ||
            SettlementQueueOperationTimeoutSeconds is < 30 or > 600)
        {
            error = "Extraction settlement heartbeat, queue capacity, worker count, or operation timeout is invalid.";
            return false;
        }
        if (ExtractionZones.Count == 0 || ExtractionZones.Any(zone => !zone.IsValid()))
        {
            error = "At least one valid extraction zone is required.";
            return false;
        }
        var duplicateZoneId = ExtractionZones
            .GroupBy(zone => zone.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateZoneId is not null)
        {
            error = $"ExtractionMode:ExtractionZones contains duplicate zone id '{duplicateZoneId}'.";
            return false;
        }
        error = null;
        return true;
    }

    public TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (TimeZoneNotFoundException) when (
            string.Equals(TimeZoneId, "Asia/Shanghai", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }
}

public sealed class ExtractionZoneOptions
{
    public string Id { get; init; } = "dev-extract";
    public string DisplayName { get; init; } = "开发服资源回收点";
    public string RouteHint { get; init; } =
        "打开游戏地图前往坐标 (248, -504)，进入中心半径 100 的区域；到达后回到玩家商城扫描并出售白名单资源。";
    public string RiskHint { get; init; } =
        "公开兑换点可能有野生帕鲁与其他玩家活动；仅携带本次准备出售的资源。";
    public double MapX { get; init; } = 248;
    public double MapY { get; init; } = -504;
    public double Radius { get; init; } = 100;

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Id) &&
        Id.Length <= 64 &&
        !string.IsNullOrWhiteSpace(DisplayName) &&
        DisplayName.Length <= 128 &&
        !string.IsNullOrWhiteSpace(RouteHint) &&
        RouteHint.Length <= 512 &&
        !string.IsNullOrWhiteSpace(RiskHint) &&
        RiskHint.Length <= 512 &&
        double.IsFinite(MapX) &&
        double.IsFinite(MapY) &&
        double.IsFinite(Radius) &&
        Radius is > 0 and <= 10_000;
}

public sealed class PalDefenderShopDeliveryDispatcher : IShopDeliveryCommandDispatcher
{
    private readonly PalDefenderCommandQueue _commands;

    public PalDefenderShopDeliveryDispatcher(PalDefenderCommandQueue commands)
    {
        _commands = commands;
    }

    public async Task<ShopDeliveryCommandAcceptance> EnqueueAsync(
        ShopDeliveryWorkItem delivery,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        var items = new System.Text.Json.Nodes.JsonArray();
        foreach (var item in delivery.Items)
        {
            items.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["ItemID"] = item.ItemId,
                ["Count"] = item.Quantity
            });
        }
        var result = await _commands.EnqueueAsync(
            delivery.ServerId,
            delivery.UpstreamPath,
            new System.Text.Json.Nodes.JsonObject { ["Items"] = items },
            delivery.IdempotencyKey,
            reason,
            actor,
            cancellationToken);
        return new ShopDeliveryCommandAcceptance(
            result.Command?.CommandId,
            result.Command is not null,
            result.IdempotencyConflict,
            result.IdempotencyConflict
                ? "IDEMPOTENCY_CONFLICT"
                : result.CapacityExceeded
                    ? "PALDEFENDER_COMMAND_QUEUE_FULL"
                    : null,
            result.IdempotencyConflict
                ? "The PalDefender delivery idempotency key conflicts with another command."
                : result.CapacityExceeded
                    ? "The PalDefender command queue is at its hard capacity."
                    : null);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed class ExtractionSettlementService
{
    private static readonly IReadOnlyDictionary<string, LootDefinition> LootCatalog =
        new Dictionary<string, LootDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pal_crystal_S"] = new("帕鲁矿碎块", 1),
            ["Leather"] = new("皮革", 2),
            ["Bone"] = new("骨头", 2),
            ["Cloth"] = new("布", 3),
            ["CopperOre"] = new("金属矿石", 2),
            ["CopperIngot"] = new("金属铸块", 5),
            ["Coal"] = new("石炭", 3),
            ["Sulfur"] = new("硫磺", 3),
            ["Quartz"] = new("纯水晶", 8),
            ["PalOil"] = new("优质帕鲁油", 10),
            ["Polymer"] = new("聚合物", 15),
            ["CarbonFiber"] = new("碳纤维", 20),
            ["MachineParts2"] = new("电路板", 25),
            ["PalCrystal_Ex"] = new("古代文明部件", 40),
            ["AncientParts2"] = new("古代文明核心", 100),
            ["MeteorDrop"] = new("陨石碎片", 20),
            ["CrudeOil"] = new("原油", 15),
            ["Diamond"] = new("钻石", 100),
            ["Ruby"] = new("红宝石", 60),
            ["Sapphire"] = new("蓝宝石", 70),
            ["Eemerald"] = new("绿宝石", 80)
        };

    private readonly ExtractionModeCoordinator _coordinator;
    private readonly ExtractionCommerceService _commerce;
    private readonly ExtractionRunStore _runs;
    private readonly PalDefenderRestClient _palDefender;
    private readonly IExtractionRconAdapter _rcon;
    private readonly ExtractionModeOptions _options;
    private readonly ExtractionRconOptions _rconOptions;

    public ExtractionSettlementService(
        ExtractionModeCoordinator coordinator,
        ExtractionCommerceService commerce,
        ExtractionRunStore runs,
        PalDefenderRestClient palDefender,
        IExtractionRconAdapter rcon,
        IOptions<ExtractionModeOptions> options,
        IOptions<ExtractionRconOptions> rconOptions)
    {
        _coordinator = coordinator;
        _commerce = commerce;
        _runs = runs;
        _palDefender = palDefender;
        _rcon = rcon;
        _options = options.Value;
        _rconOptions = rconOptions.Value;
    }

    public bool SettlementEnabled => _rconOptions.Enabled;

    public async Task<RconOperationResult> ProbeSettlementAsync(CancellationToken cancellationToken)
    {
        if (!_rconOptions.Enabled)
        {
            return RconOperationResult.Rejected(
                "rcon_disabled",
                "Extraction RCON is disabled.");
        }

        var commands = await _rcon.GetCommandsAsync(cancellationToken);
        if (!commands.Success)
        {
            return commands;
        }
        var hasDeleteItems = RconCapabilityCatalog.ContainsExact(
            commands.Response,
            "delitems:2");
        if (!hasDeleteItems)
        {
            return RconOperationResult.Rejected(
                "rcon_delitems_capability_missing",
                "The authenticated RCON command catalog does not expose the approved delitems:2 capability.",
                commands.Response);
        }

        var version = await _rcon.GetVersionAsync(cancellationToken);
        if (!version.Success)
        {
            return version;
        }
        try
        {
            var document = JsonNode.Parse(version.Response ?? string.Empty) as JsonObject;
            var gameVersion = document?["game_version"]?.GetValue<string>();
            var palDefenderVersion = document?["paldefender"]?["full"]?.GetValue<string>();
            if (!string.Equals(
                    gameVersion,
                    _rconOptions.ApprovedGameVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    palDefenderVersion,
                    _rconOptions.ApprovedPalDefenderVersion,
                    StringComparison.Ordinal))
            {
                return RconOperationResult.Rejected(
                    "rcon_version_not_approved",
                    $"RCON reported game '{gameVersion ?? "unknown"}' and PalDefender '{palDefenderVersion ?? "unknown"}', which do not match the approved extraction versions.",
                    version.Response);
            }
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            return RconOperationResult.Rejected(
                "rcon_version_response_invalid",
                "The RCON version response was not valid approved JSON.",
                version.Response);
        }
        return RconOperationResult.Succeeded(version.Response ?? string.Empty);
    }

    public async Task<ExtractionSettlementRun> QuoteAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var capability = await ProbeSettlementAsync(cancellationToken);
        if (!capability.Success)
        {
            throw new ExtractionModeException(
                capability.ErrorCode ?? "RCON_UNAVAILABLE",
                capability.ErrorMessage ?? "撤离结算 RCON 当前不可用。",
                StatusCodes.Status503ServiceUnavailable);
        }
        var context = await _coordinator.GetAccountContextAsync(
            userId,
            requireOnline: true,
            cancellationToken);
        await _coordinator.EnsureSeasonMatchesActiveWorldAsync(context.Season, cancellationToken);
        var zone = await RequireStableExtractionZoneAsync(userId, cancellationToken);
        var inventory = await ReadSellableInventoryAsync(userId, cancellationToken);
        var lines = inventory.Totals
            .Where(item => item.Value > 0 && LootCatalog.ContainsKey(item.Key))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                if (item.Value > int.MaxValue)
                {
                    throw new ExtractionModeException(
                        "EXTRACTION_ITEM_COUNT_TOO_LARGE",
                        $"物品 {item.Key} 的数量超出可结算范围。",
                        StatusCodes.Status422UnprocessableEntity);
                }
                var definition = LootCatalog[item.Key];
                return new ExtractionLootLine(
                    item.Key,
                    definition.DisplayName,
                    (int)item.Value,
                    definition.UnitValue,
                    checked(item.Value * definition.UnitValue));
            })
            .ToArray();
        if (lines.Length == 0)
        {
            throw new ExtractionModeException(
                "NO_SELLABLE_EXTRACTION_LOOT",
                "Items、Food 和 DropSlot 中没有可撤离出售的白名单战利品。",
                StatusCodes.Status422UnprocessableEntity);
        }
        return await _runs.CreateQuoteAsync(
            context.Account.AccountId,
            context.Season.SeasonId,
            context.Account.ExternalUserId,
            zone.Id,
            zone.DisplayName,
            lines,
            HashSnapshot(inventory.Totals),
            DateTimeOffset.UtcNow.AddSeconds(_options.ExtractionQuoteSeconds),
            cancellationToken);
    }

    public async Task<ExtractionSettlementRun> SettleAsync(
        Guid runId,
        string userId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var context = await _coordinator.GetAccountContextAsync(
            userId,
            requireOnline: true,
            cancellationToken);
        await _coordinator.EnsureSeasonMatchesActiveWorldAsync(context.Season, cancellationToken);
        var existing = await _runs.GetAsync(runId, cancellationToken)
            ?? throw new ExtractionModeException(
                "EXTRACTION_RUN_NOT_FOUND",
                "撤离报价不存在。",
                StatusCodes.Status404NotFound);
        if (existing.AccountId != context.Account.AccountId ||
            existing.SeasonId != context.Season.SeasonId)
        {
            throw new ExtractionModeException(
                "EXTRACTION_RUN_SCOPE_MISMATCH",
                "撤离报价不属于当前玩家或当前周档。",
                StatusCodes.Status403Forbidden);
        }
        if (existing.SettlementIdempotencyKey is not null)
        {
            if (!string.Equals(
                    existing.SettlementIdempotencyKey,
                    idempotencyKey,
                    StringComparison.Ordinal))
            {
                throw new ExtractionModeException(
                    "IDEMPOTENCY_CONFLICT",
                    "该撤离记录已使用另一个 Idempotency-Key。",
                    StatusCodes.Status409Conflict);
            }
            return existing;
        }
        _ = await RequireStableExtractionZoneAsync(userId, cancellationToken);
        var inventory = await ReadSellableInventoryAsync(userId, cancellationToken);
        if (!string.Equals(
                existing.QuoteSnapshotHash,
                HashSnapshot(inventory.Totals),
                StringComparison.Ordinal))
        {
            throw new ExtractionModeException(
                "EXTRACTION_INVENTORY_CHANGED",
                "报价后可出售战利品发生变化，请重新扫描。",
                StatusCodes.Status409Conflict);
        }

        var preDeleteTotals = existing.Items.ToDictionary(
            line => line.ItemId,
            line => inventory.Totals.GetValueOrDefault(line.ItemId),
            StringComparer.OrdinalIgnoreCase);
        var start = await _runs.StartConsumptionAsync(
            runId,
            context.Account.ExternalUserId,
            idempotencyKey,
            preDeleteTotals,
            cancellationToken);
        if (start.IdempotencyConflict)
        {
            throw new ExtractionModeException(
                "IDEMPOTENCY_CONFLICT",
                "该撤离记录已使用另一个 Idempotency-Key。",
                StatusCodes.Status409Conflict);
        }
        if (!start.Started)
        {
            return start.Run;
        }

        var deletion = await _rcon.DeleteItemsAsync(
            context.Account.ExternalUserId,
            start.Run.Items.Select(line => new RconItemDeletion(line.ItemId, line.Quantity)).ToArray(),
            cancellationToken);
        if (deletion.Outcome == RconOperationOutcome.Failed)
        {
            return await _runs.MarkFailedAsync(
                runId,
                deletion.ErrorCode ?? "RCON_DELETE_FAILED",
                deletion.ErrorMessage ?? "PalDefender 明确拒绝扣除撤离物品。",
                cancellationToken);
        }
        if (deletion.Outcome == RconOperationOutcome.Uncertain)
        {
            return await _runs.MarkUncertainAsync(
                runId,
                deletion.ErrorCode ?? "RCON_DELETE_UNCERTAIN",
                deletion.ErrorMessage ?? "扣物结果不确定，禁止自动重试或入账。",
                cancellationToken);
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var verified = await TryVerifyRemovalAsync(start.Run, cancellationToken);
            if (verified)
            {
                _ = await _runs.MarkRemovedAsync(runId, cancellationToken);
                return await CreditRemovedRunAsync(runId, cancellationToken);
            }
            if (attempt < 19)
            {
                await Task.Delay(250, cancellationToken);
            }
        }
        return await _runs.MarkUncertainAsync(
            runId,
            "RCON_DELETE_READBACK_MISMATCH",
            "RCON 返回成功，但 REST 背包回读未证明物品已按报价移除。",
            cancellationToken);
    }

    public Task<IReadOnlyList<ExtractionSettlementRun>> ListAsync(
        Guid accountId,
        Guid seasonId,
        int limit,
        CancellationToken cancellationToken) =>
        _runs.ListAsync(accountId, seasonId, limit, cancellationToken);

    public Task<IReadOnlyList<ExtractionSettlementRun>> ListRecoverableAsync(
        int limit,
        CancellationToken cancellationToken) =>
        _runs.ListRecoverableAsync(limit, cancellationToken);

    public async Task<ExtractionSettlementRun> ReconcileUncertainAsync(
        Guid runId,
        string resolution,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var normalizedReason = reason.Trim();
        if (normalizedReason.Length is < 3 or > 500 || normalizedReason.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Reconciliation reason must contain 3 to 500 non-control characters.",
                nameof(reason));
        }
        var normalizedResolution = resolution.Trim().ToLowerInvariant();
        var run = await _runs.GetAsync(runId, cancellationToken)
            ?? throw new ExtractionModeException(
                "EXTRACTION_RUN_NOT_FOUND",
                "撤离记录不存在。",
                StatusCodes.Status404NotFound);

        if (normalizedResolution == "failed")
        {
            if (run.State == ExtractionSettlementState.Failed)
            {
                return run;
            }
            if (run.State != ExtractionSettlementState.Uncertain)
            {
                throw new ExtractionModeException(
                    "RECONCILIATION_NOT_ALLOWED",
                    "只有 uncertain 撤离记录可以人工终结。",
                    StatusCodes.Status409Conflict);
            }
            return await _runs.MarkFailedAsync(
                runId,
                "MANUALLY_RECONCILED_FAILED",
                normalizedReason,
                cancellationToken);
        }

        if (normalizedResolution == "settled")
        {
            if (run.State == ExtractionSettlementState.Settled)
            {
                return run;
            }
            if (run.State != ExtractionSettlementState.Uncertain)
            {
                throw new ExtractionModeException(
                    "RECONCILIATION_NOT_ALLOWED",
                    "只有 uncertain 撤离记录可以人工确认扣物并入账。",
                    StatusCodes.Status409Conflict);
            }
            var credit = await _commerce.GrantSeasonVoucherAsync(
                run.AccountId,
                run.SeasonId,
                run.TotalValue,
                "extraction_run",
                run.RunId.ToString("N"),
                $"extraction-credit-{run.RunId:N}",
                "manual-reconciliation",
                normalizedReason,
                cancellationToken);
            if (credit.ErrorCode is not null)
            {
                throw new IOException(
                    $"Extraction wallet credit failed: {credit.ErrorCode}: {credit.ErrorMessage}");
            }
            return await _runs.MarkManuallySettledAsync(
                runId,
                normalizedReason,
                cancellationToken);
        }

        throw new ArgumentException(
            "Resolution must be 'failed' or 'settled'.",
            nameof(resolution));
    }

    public async Task<PlayerExtractionZoneSnapshot> GetPlayerZoneSnapshotAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var player = await _coordinator.TryGetPlayerLocationAsync(userId, cancellationToken);
        var sampledAt = DateTimeOffset.UtcNow;

        var hasLivePosition = player is
        {
            Online: true,
            MapX: double mapX,
            MapY: double mapY
        } && double.IsFinite(mapX) && double.IsFinite(mapY);

        ExtractionMapPoint? playerMapPosition = null;
        ExtractionMapPoint? playerWorldPosition = null;
        if (hasLivePosition)
        {
            playerMapPosition = new ExtractionMapPoint(player!.MapX!.Value, player.MapY!.Value);
            playerWorldPosition = ExtractionCoordinateTransform.ToWorld(playerMapPosition);
        }

        var zoneDistances = _options.ExtractionZones
            .Select(zone =>
            {
                var center = new ExtractionMapPoint(zone.MapX, zone.MapY);
                var worldCenter = ExtractionCoordinateTransform.ToWorld(center);
                double? distanceToCenter = null;
                double? distanceToBoundary = null;
                bool? inside = null;
                if (playerMapPosition is not null)
                {
                    var deltaX = playerMapPosition.X - center.X;
                    var deltaY = playerMapPosition.Y - center.Y;
                    distanceToCenter = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                    distanceToBoundary = Math.Max(0, distanceToCenter.Value - zone.Radius);
                    inside = distanceToCenter <= zone.Radius;
                }
                return new PlayerExtractionZone(
                    zone.Id,
                    zone.DisplayName,
                    zone.RouteHint,
                    center,
                    worldCenter,
                    zone.Radius,
                    zone.Radius * ExtractionCoordinateTransform.Scale,
                    distanceToCenter,
                    distanceToCenter * ExtractionCoordinateTransform.Scale,
                    distanceToBoundary,
                    distanceToBoundary * ExtractionCoordinateTransform.Scale,
                    inside);
            })
            .ToArray();

        var nearestZone = playerMapPosition is null
            ? null
            : zoneDistances.MinBy(zone => zone.DistanceToCenter!.Value);
        var activeZone = zoneDistances.FirstOrDefault(zone => zone.Inside == true);
        var status = player switch
        {
            null => "unavailable",
            { Online: false } => "offline",
            _ when !hasLivePosition => "position-unavailable",
            _ => "live"
        };
        var statusMessage = status switch
        {
            "live" when activeZone is not null => "已进入撤离区域。",
            "live" => "尚未进入撤离区域。",
            "offline" => "玩家当前不在线；静态撤离点信息仍可查看。",
            "position-unavailable" => "玩家在线，但当前位置暂时不可用。",
            _ => "暂时无法从 PalDefender 确认玩家状态；静态撤离点信息仍可查看。"
        };

        return new PlayerExtractionZoneSnapshot(
            status,
            statusMessage,
            sampledAt,
            player?.Online,
            playerMapPosition,
            playerWorldPosition,
            playerMapPosition is null ? null : activeZone is not null,
            activeZone?.Id,
            nearestZone?.Id,
            zoneDistances);
    }

    public async Task<ExtractionSettlementRun> ReconcileAsync(
        ExtractionSettlementRun run,
        CancellationToken cancellationToken)
    {
        if (run.State == ExtractionSettlementState.Removed)
        {
            return await CreditRemovedRunAsync(run.RunId, cancellationToken);
        }
        if (run.State != ExtractionSettlementState.Consuming)
        {
            return run;
        }
        if (await TryVerifyRemovalAsync(run, cancellationToken))
        {
            _ = await _runs.MarkRemovedAsync(run.RunId, cancellationToken);
            return await CreditRemovedRunAsync(run.RunId, cancellationToken);
        }
        if (DateTimeOffset.UtcNow - run.UpdatedAt >= TimeSpan.FromSeconds(60))
        {
            return await _runs.MarkUncertainAsync(
                run.RunId,
                "RCON_DELETE_RECOVERY_UNPROVEN",
                "服务重启后无法证明扣物结果，已停止自动重试和入账。",
                cancellationToken);
        }
        return run;
    }

    private async Task<ExtractionSettlementRun> CreditRemovedRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(runId, cancellationToken)
            ?? throw new KeyNotFoundException($"Extraction run '{runId}' does not exist.");
        var result = await _commerce.GrantSeasonVoucherAsync(
            run.AccountId,
            run.SeasonId,
            run.TotalValue,
            "extraction_run",
            run.RunId.ToString("N"),
            $"extraction-credit-{run.RunId:N}",
            "extraction-settlement",
            $"撤离结算 {run.RunId:N}",
            cancellationToken);
        if (result.ErrorCode is not null)
        {
            throw new IOException(
                $"Extraction wallet credit failed: {result.ErrorCode}: {result.ErrorMessage}");
        }
        return await _runs.MarkSettledAsync(run.RunId, cancellationToken);
    }

    private async Task<bool> TryVerifyRemovalAsync(
        ExtractionSettlementRun run,
        CancellationToken cancellationToken)
    {
        if (run.PreDeleteTotals is null)
        {
            return false;
        }
        var inventory = await ReadSellableInventoryAsync(run.UserId, cancellationToken);
        foreach (var line in run.Items)
        {
            var before = run.PreDeleteTotals.GetValueOrDefault(line.ItemId);
            var expected = before - line.Quantity;
            if (expected < 0 || inventory.Totals.GetValueOrDefault(line.ItemId) != expected)
            {
                return false;
            }
        }
        return true;
    }

    private async Task<ExtractionZoneOptions> RequireStableExtractionZoneAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var first = await _coordinator.GetLivePlayerAsync(userId, cancellationToken);
        var firstZone = FindZone(first) ?? throw new ExtractionModeException(
            "PLAYER_OUTSIDE_EXTRACTION_ZONE",
            "玩家不在配置的撤离区域内。",
            StatusCodes.Status409Conflict);
        await Task.Delay(_options.ExtractionPositionSampleMilliseconds, cancellationToken);
        var second = await _coordinator.GetLivePlayerAsync(userId, cancellationToken);
        var secondZone = FindZone(second);
        if (secondZone is null || !string.Equals(firstZone.Id, secondZone.Id, StringComparison.Ordinal))
        {
            throw new ExtractionModeException(
                "EXTRACTION_ZONE_NOT_STABLE",
                "两次位置采样未持续处于同一撤离区域。",
                StatusCodes.Status409Conflict);
        }
        return firstZone;
    }

    private ExtractionZoneOptions? FindZone(ExtractionLivePlayer player)
    {
        if (player.MapX is not double x || player.MapY is not double y)
        {
            return null;
        }
        return _options.ExtractionZones.FirstOrDefault(zone =>
        {
            var deltaX = x - zone.MapX;
            var deltaY = y - zone.MapY;
            return deltaX * deltaX + deltaY * deltaY <= zone.Radius * zone.Radius;
        });
    }

    private async Task<InventorySnapshot> ReadSellableInventoryAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var response = await _palDefender.GetAsync(
            $"items/{Uri.EscapeDataString(userId)}",
            cancellationToken);
        if (!response.IsSuccess || response.Json is not JsonObject root ||
            GetProperty(root, "Inventory") is not JsonObject inventory)
        {
            throw new ExtractionModeException(
                "INVENTORY_READ_FAILED",
                "无法通过 PalDefender 读取玩家背包。",
                StatusCodes.Status503ServiceUnavailable);
        }
        Dictionary<string, long> totals = new(StringComparer.OrdinalIgnoreCase);
        foreach (var containerName in new[] { "Items", "Food", "DropSlot" })
        {
            if (GetProperty(inventory, containerName) is JsonObject container &&
                GetProperty(container, "Slots") is JsonNode slots)
            {
                CollectItemTotals(slots, totals);
            }
        }
        return new InventorySnapshot(totals);
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

    private static string HashSnapshot(IReadOnlyDictionary<string, long> totals)
    {
        var canonical = string.Join('\n', LootCatalog.Keys
            .OrderBy(itemId => itemId, StringComparer.OrdinalIgnoreCase)
            .Select(itemId => $"{itemId}={totals.GetValueOrDefault(itemId)}"));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static JsonNode? GetProperty(JsonObject value, string name) =>
        value.FirstOrDefault(property =>
            string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

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
        return null;
    }

    private sealed record LootDefinition(string DisplayName, long UnitValue);
    private sealed record InventorySnapshot(IReadOnlyDictionary<string, long> Totals);
}

public sealed record ExtractionMapPoint(double X, double Y);

public sealed record PlayerExtractionZone(
    string Id,
    string Name,
    string RouteHint,
    ExtractionMapPoint MapPosition,
    ExtractionMapPoint WorldPosition,
    double Radius,
    double WorldRadius,
    double? DistanceToCenter,
    double? WorldDistanceToCenter,
    double? DistanceToBoundary,
    double? WorldDistanceToBoundary,
    bool? Inside);

public sealed record PlayerExtractionZoneSnapshot(
    string Status,
    string StatusMessage,
    DateTimeOffset SampledAt,
    bool? Online,
    ExtractionMapPoint? PlayerMapPosition,
    ExtractionMapPoint? PlayerWorldPosition,
    bool? InsideAnyZone,
    string? ActiveZoneId,
    string? NearestZoneId,
    IReadOnlyList<PlayerExtractionZone> Zones);

public static class ExtractionCoordinateTransform
{
    public const double Scale = 459;
    public const double WorldXOffset = -123_888;
    public const double WorldYOffset = 158_000;

    public static ExtractionMapPoint ToWorld(ExtractionMapPoint mapPosition) =>
        new(
            mapPosition.Y * Scale + WorldXOffset,
            mapPosition.X * Scale + WorldYOffset);
}

public sealed class ExtractionSettlementRecoveryWorker : BackgroundService
{
    private readonly ExtractionSettlementService _settlement;
    private readonly ILogger<ExtractionSettlementRecoveryWorker> _logger;

    public ExtractionSettlementRecoveryWorker(
        ExtractionSettlementService settlement,
        ILogger<ExtractionSettlementRecoveryWorker> logger)
    {
        _settlement = settlement;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var runs = await _settlement.ListRecoverableAsync(25, stoppingToken);
                foreach (var run in runs)
                {
                    _ = await _settlement.ReconcileAsync(run, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Extraction settlement recovery failed.");
            }
            await Task.Delay(2_000, stoppingToken);
        }
    }
}

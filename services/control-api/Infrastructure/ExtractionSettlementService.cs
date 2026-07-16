using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed class ExtractionSettlementService : IExtractionSettlementExecutor
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
    private readonly EconomySafetyGate _economySafety;
    private readonly ILogger<ExtractionSettlementService> _logger;
    private readonly IExtractionNativeInventoryAdapter? _nativeInventory;
    private readonly bool _useDevelopmentRconSettlement;
    private readonly EconomyContentRuntimeService? _content;
    private readonly ReliableTaskRuntimeService? _reliableTasks;
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeProvider _timeProvider;

    public ExtractionSettlementService(
        ExtractionModeCoordinator coordinator,
        ExtractionCommerceService commerce,
        ExtractionRunStore runs,
        PalDefenderRestClient palDefender,
        IExtractionRconAdapter rcon,
        IOptions<ExtractionModeOptions> options,
        IOptions<ExtractionRconOptions> rconOptions,
        EconomySafetyGate economySafety,
        ILogger<ExtractionSettlementService> logger,
        IExtractionNativeInventoryAdapter? nativeInventory = null,
        IWebHostEnvironment? environment = null,
        IOptions<EconomySafetyOptions>? safetyOptions = null,
        IConfiguration? configuration = null,
        EconomyContentRuntimeService? content = null,
        ReliableTaskRuntimeService? reliableTasks = null,
        TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator;
        _commerce = commerce;
        _runs = runs;
        _palDefender = palDefender;
        _rcon = rcon;
        _options = options.Value;
        _rconOptions = rconOptions.Value;
        _economySafety = economySafety;
        _logger = logger;
        _nativeInventory = nativeInventory;
        _content = content;
        _reliableTasks = reliableTasks;
        _timeZone = _options.ResolveTimeZone();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _useDevelopmentRconSettlement = DevelopmentRconSettlementPolicy.IsAllowed(
            environment,
            configuration,
            _rconOptions,
            safetyOptions?.Value);
    }

    public bool SettlementEnabled => _useDevelopmentRconSettlement
        ? _rconOptions.Enabled
        : _nativeInventory?.StableSettlementAvailable == true;

    public string SettlementAdapter => _useDevelopmentRconSettlement
        ? "development-rcon"
        : "native";

    internal static void RequireCurrentQuoteContent(
        ExtractionSettlementRun quote,
        EconomyContentVersion currentVersion)
    {
        if (!EconomyContentEvidence.MatchesCurrent(
                quote.ContentVersionId,
                quote.ContentHash,
                currentVersion))
        {
            throw new ExtractionModeException(
                "QUOTE_CONTENT_CHANGED",
                "报价对应的本周经济内容已经切换，请重新扫描。",
                StatusCodes.Status409Conflict);
        }
    }

    internal static void RequireCurrentQuoteDynamicEvidence(
        ExtractionSettlementRun quote,
        EconomyRuntimeContent current)
    {
        var actual = quote.DynamicEconomyEvidence;
        var dynamic = current.DynamicEconomy;
        if (dynamic is null)
        {
            if (actual is not null)
            {
                throw new ExtractionModeException(
                    "QUOTE_EVENT_CHANGED",
                    "报价包含的动态经济事件已不再属于当前内容，请重新扫描。",
                    StatusCodes.Status409Conflict);
            }
            return;
        }
        var zone = dynamic.Zones.FirstOrDefault(candidate => string.Equals(
            candidate.ZoneId,
            quote.ZoneId,
            StringComparison.OrdinalIgnoreCase));
        if (actual is null || zone is null ||
            !string.Equals(actual.PolicyVersion, dynamic.PolicyVersion, StringComparison.Ordinal) ||
            !string.Equals(actual.Seed, dynamic.Seed, StringComparison.Ordinal) ||
            actual.RiskLevel != zone.RiskLevel ||
            actual.OpenWindow != zone.OpenWindow ||
            actual.HotspotWindow != zone.HotspotWindow ||
            !actual.WorldEvents.SequenceEqual(dynamic.WorldEvents))
        {
            throw new ExtractionModeException(
                actual is null ? "QUOTE_EVENT_EVIDENCE_MISSING" : "QUOTE_EVENT_CHANGED",
                "报价缺少或不匹配当前确定性动态区、热点与世界事件证据，请重新扫描。",
                StatusCodes.Status409Conflict);
        }
    }

    internal static DateTimeOffset CalculateDynamicQuoteExpiry(
        DateTimeOffset quotedAt,
        DateTimeOffset configuredExpiry,
        EconomyDynamicZoneEvidence dynamicZone,
        bool hotspotAtQuote,
        IReadOnlyList<EconomyWorldEventEvidence> selectedWorldEvents,
        IReadOnlyList<EconomyWorldEventEvidence> activeWorldEvents)
    {
        var expiresAt = Min(configuredExpiry, dynamicZone.OpenWindow.GraceEndsAt);
        if (dynamicZone.HotspotWindow is { } hotspotWindow)
        {
            expiresAt = hotspotAtQuote
                ? Min(expiresAt, hotspotWindow.GraceEndsAt)
                : quotedAt < hotspotWindow.StartsAt
                    ? Min(expiresAt, hotspotWindow.StartsAt)
                    : expiresAt;
        }

        var activeEventIds = activeWorldEvents.Select(worldEvent => worldEvent.EventId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var worldEvent in selectedWorldEvents)
        {
            if (activeEventIds.Contains(worldEvent.EventId))
            {
                expiresAt = Min(expiresAt, worldEvent.Window.GraceEndsAt);
            }
            else if (quotedAt < worldEvent.Window.StartsAt)
            {
                // Do not let a pre-event quote carry yesterday's/base rate
                // into a newly active event. Quotes captured while active may
                // settle through the event's explicit grace window.
                expiresAt = Min(expiresAt, worldEvent.Window.StartsAt);
            }
        }
        return expiresAt;
    }

    public async Task<RconOperationResult> ProbeSettlementAsync(CancellationToken cancellationToken)
    {
        if (!_useDevelopmentRconSettlement)
        {
            return _nativeInventory?.StableSettlementAvailable == true
                ? RconOperationResult.Succeeded("inventory.probe inventory.consume")
                : RconOperationResult.Rejected(
                    "native_inventory_consume_capability_missing",
                    "Production resource settlement requires stable inventory.probe and inventory.consume capabilities; experimental is rejected.");
        }
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
        var context = await _coordinator.GetAccountContextAsync(
            userId,
            requireOnline: true,
            cancellationToken);
        using var safetyLease = await _economySafety.AcquireAsync(
            EconomyWriteFeature.ResourceExchange,
            EconomySafetyContext.FromAccount(context),
            queue: null,
            cancellationToken);
        await _coordinator.EnsureSeasonMatchesActiveWorldAsync(context.Season, cancellationToken);
        var runtimeContent = _content is null
            ? null
            : await _content.GetCurrentAsync(cancellationToken);
        var quotedAt = _timeProvider.GetUtcNow();
        var zone = await RequireStableExtractionZoneAsync(
            userId,
            runtimeContent,
            expectedZoneId: null,
            includeScheduleGrace: false,
            cancellationToken: cancellationToken);
        ExtractionNativeInventoryQuoteSnapshot? nativeSnapshot = null;
        InventorySnapshot inventory;
        if (_useDevelopmentRconSettlement)
        {
            inventory = await ReadSellableInventoryAsync(userId, cancellationToken);
        }
        else
        {
            var native = RequireNativeInventoryAdapter();
            var playerUid = context.IdentityBinding?.PlayerUid ?? throw new ExtractionModeException(
                "PLAYER_BINDING_REQUIRED",
                "Native 资源报价要求当前周世界的完整 PlayerUID 绑定。",
                StatusCodes.Status409Conflict);
            nativeSnapshot = await native.CaptureQuoteSnapshotAsync(
                _options.ServerId,
                playerUid,
                cancellationToken);
            inventory = new InventorySnapshot(
                ExtractionNativeInventoryCanonicalizer.AggregateTotals(nativeSnapshot));
        }
        var sellableResources = runtimeContent is null
            ? LootCatalog.ToDictionary(
                item => item.Key,
                item => new RuntimeResource(
                    item.Value.DisplayName,
                    item.Value.UnitValue,
                    null,
                    null,
                    null,
                    null),
                StringComparer.OrdinalIgnoreCase)
            : runtimeContent.Resources.Values
                .Where(resource => resource.ExchangeZoneIds.Contains(
                    zone.Id,
                    StringComparer.OrdinalIgnoreCase))
                .ToDictionary(
                    resource => resource.ItemId,
                    resource => new RuntimeResource(
                        resource.DisplayName,
                        resource.UnitValue,
                        resource.Category,
                        resource.IconKey,
                        resource.Rarity,
                        resource.Usage),
                    StringComparer.OrdinalIgnoreCase);
        var lines = inventory.Totals
            .Where(item => item.Value > 0 && sellableResources.ContainsKey(item.Key))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var maximumQuantity = _useDevelopmentRconSettlement
                    ? int.MaxValue
                    : 999_999;
                if (item.Value > maximumQuantity)
                {
                    throw new ExtractionModeException(
                        "EXTRACTION_ITEM_COUNT_TOO_LARGE",
                        $"物品 {item.Key} 的数量超出可结算范围。",
                        StatusCodes.Status422UnprocessableEntity);
                }
                var definition = sellableResources[item.Key];
                var presentation = EconomyContentPresentation.ResolveResource(
                    definition.Category ?? string.Empty,
                    definition.IconKey,
                    definition.Rarity,
                    definition.Usage);
                var effectiveUnitValue = EconomyContentRuntimeService.CalculateEffectiveResourceUnitValue(
                    definition.UnitValue,
                    zone.YieldMultiplierBasisPoints);
                return new ExtractionLootLine(
                    item.Key,
                    definition.DisplayName,
                    (int)item.Value,
                    effectiveUnitValue,
                    checked(item.Value * effectiveUnitValue),
                    presentation.IconKey,
                    presentation.Rarity,
                    presentation.Usage,
                    presentation.PresentationSource);
            })
            .ToArray();
        if (lines.Length == 0)
        {
            throw new ExtractionModeException(
                "NO_SELLABLE_EXTRACTION_LOOT",
                "Items、Food 和 DropSlot 中没有可出售的白名单资源。",
                StatusCodes.Status422UnprocessableEntity);
        }
        if (!_useDevelopmentRconSettlement &&
            lines.Sum(line => (long)line.Quantity) > 16_000_000)
        {
            throw new ExtractionModeException(
                "EXTRACTION_TOTAL_ITEM_COUNT_TOO_LARGE",
                "本次可售资源总数超过 Native 原子扣物的安全上限。",
                StatusCodes.Status422UnprocessableEntity);
        }
        var expiresAt = quotedAt.AddSeconds(_options.ExtractionQuoteSeconds);
        EconomyDynamicQuoteEvidence? dynamicEvidence = null;
        if (zone.DynamicZone is { } dynamicZone && runtimeContent?.DynamicEconomy is { } dynamicEconomy)
        {
            dynamicEvidence = new EconomyDynamicQuoteEvidence(
                dynamicEconomy.PolicyVersion,
                dynamicEconomy.Seed,
                dynamicZone.RiskLevel,
                dynamicZone.OpenWindow,
                dynamicZone.HotspotWindow,
                dynamicEconomy.WorldEvents);
            expiresAt = CalculateDynamicQuoteExpiry(
                quotedAt,
                expiresAt,
                dynamicZone,
                zone.Hotspot,
                dynamicEconomy.WorldEvents,
                zone.ActiveWorldEvents);
        }
        return await _runs.CreateQuoteAsync(
            context.Account.AccountId,
            context.Season.SeasonId,
            context.Account.ExternalUserId,
            zone.Id,
            zone.DisplayName,
            lines,
            nativeSnapshot?.SnapshotHash ?? HashSnapshot(
                inventory.Totals,
                lines.Select(line => line.ItemId)),
            expiresAt,
            cancellationToken,
            nativeSnapshot,
            runtimeContent,
            zone.YieldMultiplierBasisPoints,
            zone.Hotspot,
            dynamicEvidence);
    }

    public async Task<ExtractionQuoteSelectionResult> SelectQuoteAsync(
        Guid sourceRunId,
        long sourceRevision,
        string userId,
        string idempotencyKey,
        IReadOnlyList<ExtractionQuoteSelectionLine> selection,
        CancellationToken cancellationToken)
    {
        var context = await _coordinator.GetAccountContextAsync(
            userId,
            requireOnline: true,
            cancellationToken);
        using var safetyLease = await _economySafety.AcquireAsync(
            EconomyWriteFeature.ResourceExchange,
            EconomySafetyContext.FromAccount(context),
            queue: null,
            cancellationToken);
        await _coordinator.EnsureSeasonMatchesActiveWorldAsync(context.Season, cancellationToken);
        var source = await _runs.GetAsync(sourceRunId, cancellationToken)
            ?? throw new ExtractionModeException(
                "EXTRACTION_RUN_NOT_FOUND",
                "原资源兑换报价不存在。",
                StatusCodes.Status404NotFound);
        if (source.AccountId != context.Account.AccountId ||
            !string.Equals(
                source.UserId,
                context.Account.ExternalUserId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionModeException(
                "EXTRACTION_RUN_OWNER_MISMATCH",
                "原资源兑换报价不属于当前玩家。",
                StatusCodes.Status403Forbidden);
        }
        if (source.SeasonId != context.Season.SeasonId)
        {
            throw new ExtractionModeException(
                "EXTRACTION_RUN_SEASON_MISMATCH",
                "原资源兑换报价不属于当前周世界。",
                StatusCodes.Status409Conflict);
        }

        var runtimeContent = _content is null
            ? null
            : await _content.GetCurrentAsync(cancellationToken);
        if (runtimeContent is not null)
        {
            RequireCurrentQuoteContent(source, runtimeContent.Version);
            RequireCurrentQuoteDynamicEvidence(source, runtimeContent);
        }
        var result = await _runs.CreateSelectedQuoteAsync(
            sourceRunId,
            sourceRevision,
            context.Account.AccountId,
            context.Season.SeasonId,
            context.Account.ExternalUserId,
            idempotencyKey,
            selection,
            runtimeContent?.Version.VersionId,
            runtimeContent?.Version.ContentHash,
            cancellationToken);
        if (result.IdempotencyConflict)
        {
            throw new ExtractionModeException(
                "IDEMPOTENCY_CONFLICT",
                "该 Idempotency-Key 已绑定其他玩家、原报价或资源选择。",
                StatusCodes.Status409Conflict);
        }
        return result;
    }

    public async Task<ExtractionSettlementRun> ExecuteSettlementAsync(
        Guid runId,
        string userId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var context = await _coordinator.GetAccountContextAsync(
            userId,
            requireOnline: true,
            cancellationToken);
        using var safetyLease = await _economySafety.AcquireAsync(
            EconomyWriteFeature.ResourceExchange,
            EconomySafetyContext.FromAccount(context),
            queue: null,
            cancellationToken);
        await _coordinator.EnsureSeasonMatchesActiveWorldAsync(context.Season, cancellationToken);
        var existing = await _runs.GetAsync(runId, cancellationToken)
            ?? throw new ExtractionModeException(
                "EXTRACTION_RUN_NOT_FOUND",
                "资源兑换报价不存在。",
                StatusCodes.Status404NotFound);
        if (existing.AccountId != context.Account.AccountId ||
            existing.SeasonId != context.Season.SeasonId)
        {
            throw new ExtractionModeException(
                "EXTRACTION_RUN_SCOPE_MISMATCH",
                "资源兑换报价不属于当前玩家或当前周档。",
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
                    "该资源兑换记录已使用另一个 Idempotency-Key。",
                    StatusCodes.Status409Conflict);
            }
            return existing;
        }
        var runtimeContent = _content is null
            ? null
            : await _content.GetCurrentAsync(cancellationToken);
        if (runtimeContent is not null)
        {
            RequireCurrentQuoteContent(existing, runtimeContent.Version);
            RequireCurrentQuoteDynamicEvidence(existing, runtimeContent);
        }
        _ = await RequireStableExtractionZoneAsync(
            userId,
            runtimeContent,
            existing.ZoneId,
            includeScheduleGrace: true,
            cancellationToken: cancellationToken);
        if (!_useDevelopmentRconSettlement)
        {
            return await ExecuteNativeSettlementAsync(
                context,
                existing,
                idempotencyKey,
                cancellationToken);
        }
        var inventory = await ReadSellableInventoryAsync(userId, cancellationToken);
        if (!string.Equals(
                existing.QuoteSnapshotHash,
                HashSnapshot(
                    inventory.Totals,
                    existing.Items.Select(line => line.ItemId)),
                StringComparison.Ordinal))
        {
            throw new ExtractionModeException(
                "EXTRACTION_INVENTORY_CHANGED",
                "报价后可出售资源发生变化，请重新扫描。",
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
                "该资源兑换记录已使用另一个 Idempotency-Key。",
                StatusCodes.Status409Conflict);
        }
        if (!start.Started)
        {
            return start.Run;
        }
        var leaseId = RequireLeaseId(start.Run);
        // Once Consuming is durable, the settlement must not be abandoned just
        // because the browser disconnects. Use a bounded service-owned token so
        // RCON outcome classification and the final CAS can still complete.
        using var criticalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(_rconOptions.TimeoutSeconds + 30, 15, 90)));
        var settlementToken = criticalTimeout.Token;
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeatTask = MaintainSettlementLeaseAsync(
            start.Run,
            leaseId,
            "http-settlement",
            criticalTimeout,
            heartbeatStop.Token);
        try
        {
            var deletion = await _rcon.DeleteItemsAsync(
                context.Account.ExternalUserId,
                start.Run.Items.Select(line => new RconItemDeletion(line.ItemId, line.Quantity)).ToArray(),
                settlementToken);
            if (deletion.Outcome == RconOperationOutcome.Failed)
            {
                await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
                var failed = await _runs.TryMarkFailedAsync(
                    runId,
                    start.Run.Revision,
                    leaseId,
                    deletion.ErrorCode ?? "RCON_DELETE_FAILED",
                    deletion.ErrorMessage ?? "PalDefender 明确拒绝扣除待售资源。",
                    settlementToken);
                return failed.Run;
            }
            if (deletion.Outcome == RconOperationOutcome.Uncertain)
            {
                await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
                var uncertain = await _runs.TryMarkUncertainAsync(
                    runId,
                    start.Run.Revision,
                    leaseId,
                    deletion.ErrorCode ?? "RCON_DELETE_UNCERTAIN",
                    deletion.ErrorMessage ?? "扣物结果不确定，禁止自动重试或入账。",
                    settlementToken);
                return uncertain.Run;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                var verified = await TryVerifyRemovalAsync(start.Run, settlementToken);
                if (verified)
                {
                    await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
                    var removed = await _runs.TryMarkRemovedAsync(
                        runId,
                        start.Run.Revision,
                        leaseId,
                        settlementToken);
                    return removed.Applied
                        ? await CreditRemovedRunAsync(removed.Run, null, settlementToken)
                        : removed.Run;
                }
                if (attempt < 19)
                {
                    await Task.Delay(250, settlementToken);
                }
            }
            await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
            var mismatch = await _runs.TryMarkUncertainAsync(
                runId,
                start.Run.Revision,
                leaseId,
                "RCON_DELETE_READBACK_MISMATCH",
                "RCON 返回成功，但 REST 背包回读未证明物品已按报价移除。",
                settlementToken);
            return mismatch.Run;
        }
        catch (OperationCanceledException exception)
        {
            await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
            var current = await TryMarkCriticalSectionUncertainAsync(
                start.Run,
                leaseId,
                "SETTLEMENT_CRITICAL_SECTION_CANCELLED",
                "资源扣除是否完成无法确认，已停止自动入账并转人工核对。",
                exception);
            if (current is not null)
            {
                return current;
            }
            throw;
        }
        catch (Exception exception)
        {
            await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
            _ = await TryMarkCriticalSectionUncertainAsync(
                start.Run,
                leaseId,
                "SETTLEMENT_CRITICAL_SECTION_FAILED",
                "资源扣除收尾异常，已停止自动入账并转人工核对。",
                exception);
            throw;
        }
        finally
        {
            await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
        }
    }

    public Task<IReadOnlyList<ExtractionSettlementRun>> ListAsync(
        Guid accountId,
        Guid seasonId,
        int limit,
        CancellationToken cancellationToken) =>
        _runs.ListAsync(accountId, seasonId, limit, cancellationToken);

    public Task<ExtractionSeasonStatistics> GetSeasonStatisticsAsync(
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken) =>
        _runs.GetSeasonStatisticsAsync(accountId, seasonId, cancellationToken);

    public Task<IReadOnlyList<ExtractionSettlementRun>> ListRecoverableAsync(
        int limit,
        CancellationToken cancellationToken) =>
        _runs.ListRecoverableAsync(limit, cancellationToken);

    public async Task<ExtractionSettlementRun> ReconcileUncertainAsync(
        Guid runId,
        string resolution,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
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
                "资源兑换记录不存在。",
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
                    "只有 uncertain 资源兑换记录可以人工终结。",
                    StatusCodes.Status409Conflict);
            }
            var failed = await _runs.TryMarkManuallyFailedAsync(
                runId,
                run.Revision,
                normalizedReason,
                actor,
                cancellationToken);
            if (!failed.Applied && failed.Run.State != ExtractionSettlementState.Failed)
            {
                throw new ExtractionModeException(
                    "RECONCILIATION_CONFLICT",
                    "资源兑换记录已被另一个结算流程更新，请刷新后重试。",
                    StatusCodes.Status409Conflict);
            }
            return failed.Run;
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
                    "只有 uncertain 资源兑换记录可以人工确认扣物并入账。",
                    StatusCodes.Status409Conflict);
            }
            // Persist the confirmed removal before touching the wallet. This makes
            // a crash/retry resume from Removed and prevents a competing manual
            // failure from winning after a credit has already been created.
            var removed = await _runs.TryBeginManualSettlementAsync(
                runId,
                run.Revision,
                normalizedReason,
                actor,
                cancellationToken);
            if (!removed.Applied)
            {
                if (removed.Run.State == ExtractionSettlementState.Settled)
                {
                    return removed.Run;
                }
                throw new ExtractionModeException(
                    "RECONCILIATION_CONFLICT",
                    "资源兑换记录已被另一个结算流程更新，请刷新后重试。",
                    StatusCodes.Status409Conflict);
            }
            return await CreditRemovedRunAsync(
                removed.Run,
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
        var runtimeContent = _content is null
            ? null
            : await _content.EnsureCurrentForBusinessDateAsync(cancellationToken);
        var player = await _coordinator.TryGetPlayerLocationAsync(userId, cancellationToken);
        var sampledAt = _timeProvider.GetUtcNow();

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

        var runtimeZones = GetRuntimeZones(runtimeContent, sampledAt, includeScheduleGrace: false);
        var zoneDistances = runtimeZones
            .Select(zone =>
            {
                var center = new ExtractionMapPoint(zone.MapX, zone.MapY);
                var worldCenter = ExtractionCoordinateTransform.ToWorld(center);
                double? distanceToCenter = null;
                double? distanceToBoundary = null;
                bool? inside = null;
                if (playerMapPosition is not null)
                {
                    distanceToCenter = ExtractionZoneGeometry.Distance(
                        playerMapPosition.X,
                        playerMapPosition.Y,
                        center.X,
                        center.Y);
                    distanceToBoundary = Math.Max(0, distanceToCenter.Value - zone.Radius);
                    inside = ExtractionZoneGeometry.IsInside(
                        playerMapPosition.X,
                        playerMapPosition.Y,
                        center.X,
                        center.Y,
                        zone.Radius);
                }
                return new PlayerExtractionZone(
                    zone.Id,
                    zone.DisplayName,
                    zone.RouteHint,
                    zone.RiskHint,
                    center,
                    worldCenter,
                    zone.Radius,
                    zone.Radius * ExtractionCoordinateTransform.Scale,
                    distanceToCenter,
                    distanceToCenter * ExtractionCoordinateTransform.Scale,
                    distanceToBoundary,
                    distanceToBoundary * ExtractionCoordinateTransform.Scale,
                    inside,
                    zone.Open,
                    zone.Hotspot,
                    zone.YieldMultiplierBasisPoints,
                    zone.NextOpensAt,
                    zone.OpenWindows,
                    zone.DynamicZone?.RiskLevel,
                    zone.DynamicZone?.SelectedOpen ?? true,
                    zone.DynamicZone?.OpenWindow,
                    zone.DynamicZone?.HotspotWindow,
                    runtimeContent?.DynamicEconomy?.PolicyVersion,
                    runtimeContent?.DynamicEconomy?.Seed,
                    zone.ActiveWorldEvents);
            })
            .ToArray();

        var activeWorldEvents = runtimeZones
            .SelectMany(zone => zone.ActiveWorldEvents)
            .DistinctBy(worldEvent => worldEvent.EventId, StringComparer.Ordinal)
            .OrderBy(worldEvent => worldEvent.EventId, StringComparer.Ordinal)
            .ToArray();

        var nearestZone = playerMapPosition is null
            ? null
            : zoneDistances.MinBy(zone => zone.DistanceToCenter!.Value);
        var activeZone = zoneDistances.FirstOrDefault(zone => zone.Inside == true && zone.Open);
        var status = player switch
        {
            null => "unavailable",
            { Online: false } => "offline",
            _ when !hasLivePosition => "position-unavailable",
            _ => "live"
        };
        var statusMessage = status switch
        {
            "live" when activeZone is not null => "已进入当前开放的资源回收区域。",
            "live" when zoneDistances.Any(zone => zone.Inside == true && !zone.Open) =>
                "已到达资源回收区域，但该区域当前未开放；请查看下次开放时间。",
            "live" => "尚未进入当前开放的资源回收区域。",
            "offline" => "玩家当前不在线；静态资源回收点信息仍可查看。",
            "position-unavailable" => "玩家在线，但当前位置暂时不可用。",
            _ => "暂时无法从 PalDefender 确认玩家状态；静态资源回收点信息仍可查看。"
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
            zoneDistances,
            runtimeZones.All(zone => !zone.Open)
                ? runtimeZones.Select(zone => zone.NextOpensAt)
                    .Where(value => value is not null)
                    .Min()
                : null,
            runtimeContent?.DynamicEconomy?.PolicyVersion,
            runtimeContent?.DynamicEconomy?.Seed,
            activeWorldEvents);
    }

    public async Task<ExtractionSettlementRun> ReconcileAsync(
        ExtractionSettlementRun run,
        CancellationToken cancellationToken)
    {
        var lease = await _runs.TryAcquireRecoveryLeaseAsync(
            run.RunId,
            run.Revision,
            "settlement-recovery",
            cancellationToken);
        if (!lease.Applied)
        {
            return lease.Run;
        }
        var claimed = lease.Run;
        var leaseId = RequireLeaseId(claimed);
        if (claimed.State is
            ExtractionSettlementState.Removed or ExtractionSettlementState.Credited)
        {
            var reconciliationReason = string.Equals(
                claimed.ErrorCode,
                "MANUALLY_RECONCILED_REMOVED",
                StringComparison.Ordinal)
                ? claimed.ErrorMessage
                : null;
            return await CreditRemovedRunAsync(
                claimed,
                reconciliationReason,
                cancellationToken);
        }
        if (claimed.State != ExtractionSettlementState.Consuming)
        {
            return claimed;
        }
        if (claimed.NativeConsumeReceipt is { } nativeReceipt)
        {
            if (nativeReceipt.Disposition == ExtractionNativeConsumeDisposition.Succeeded)
            {
                var removed = await _runs.TryMarkRemovedFromRecordedNativeAsync(
                    claimed.RunId,
                    claimed.Revision,
                    leaseId,
                    cancellationToken);
                return removed.Applied
                    ? await CreditRemovedRunAsync(removed.Run, null, cancellationToken)
                    : removed.Run;
            }
            if (nativeReceipt.Disposition == ExtractionNativeConsumeDisposition.Failed)
            {
                var failed = await _runs.TryMarkFailedAsync(
                    claimed.RunId,
                    claimed.Revision,
                    leaseId,
                    nativeReceipt.ErrorCode ?? "NATIVE_INVENTORY_CONSUME_FAILED",
                    nativeReceipt.ErrorMessage ?? "Native 明确拒绝资源扣除。",
                    cancellationToken);
                return failed.Run;
            }
            var recordedUncertain = await _runs.TryMarkUncertainAsync(
                claimed.RunId,
                claimed.Revision,
                leaseId,
                nativeReceipt.ErrorCode ?? "NATIVE_INVENTORY_CONSUME_UNCERTAIN",
                nativeReceipt.ErrorMessage ?? "Native 扣物结果不确定，禁止自动重试或入账。",
                cancellationToken);
            return recordedUncertain.Run;
        }
        // Consuming does not prove that RCON was dispatched or acknowledged.
        // A lower aggregate inventory total could also come from the player
        // dropping or consuming items, so recovery must never infer a credit
        // from that observation. Only a durable Removed record may auto-credit.
        var stateChangedAt = claimed.StateChangedAt ?? claimed.UpdatedAt;
        if (_timeProvider.GetUtcNow() - stateChangedAt >= TimeSpan.FromSeconds(60))
        {
            var uncertain = await _runs.TryMarkUncertainAsync(
                claimed.RunId,
                claimed.Revision,
                leaseId,
                claimed.NativeInventorySnapshot is null
                    ? "RCON_DELETE_RECOVERY_UNPROVEN"
                    : "NATIVE_INVENTORY_CONSUME_RECOVERY_UNPROVEN",
                "服务中断后没有持久化的扣物回执，已停止自动重试和入账。",
                cancellationToken);
            return uncertain.Run;
        }
        var released = await _runs.TryReleaseLeaseAsync(
            claimed.RunId,
            claimed.Revision,
            leaseId,
            cancellationToken);
        return released.Run;
    }

    private async Task MaintainSettlementLeaseAsync(
        ExtractionSettlementRun run,
        Guid leaseId,
        string leaseOwner,
        CancellationTokenSource criticalTimeout,
        CancellationToken stoppingToken)
    {
        try
        {
            var interval = TimeSpan.FromSeconds(_options.SettlementLeaseHeartbeatSeconds);
            while (true)
            {
                await Task.Delay(interval, stoppingToken);
                using var pulseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var pulseToken = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    pulseTimeout.Token);
                var heartbeat = await _runs.TryHeartbeatLeaseAsync(
                    run.RunId,
                    run.Revision,
                    leaseId,
                    leaseOwner,
                    pulseToken.Token);
                if (!heartbeat.Applied)
                {
                    _logger.LogError(
                        "Resource settlement {RunId} lost lease {LeaseId} during a long operation; cancelling the critical section.",
                        run.RunId,
                        leaseId);
                    criticalTimeout.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown after the external operation completes.
        }
        catch (Exception exception)
        {
            _logger.LogSafeError(
                exception,
                "Resource settlement {RunId} could not persist its lease heartbeat; cancelling the critical section.",
                run.RunId);
            criticalTimeout.Cancel();
        }
    }

    private static async Task StopLeaseHeartbeatAsync(
        CancellationTokenSource heartbeatStop,
        Task heartbeatTask)
    {
        heartbeatStop.Cancel();
        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
            // The heartbeat task observes this token only as a stop signal.
        }
    }

    private async Task<ExtractionSettlementRun?> TryMarkCriticalSectionUncertainAsync(
        ExtractionSettlementRun startedRun,
        Guid leaseId,
        string code,
        string message,
        Exception cause)
    {
        using var finalizationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var result = await _runs.TryMarkUncertainAsync(
                startedRun.RunId,
                startedRun.Revision,
                leaseId,
                code,
                message,
                finalizationTimeout.Token);
            _logger.LogSafeWarning(
                cause,
                "Resource settlement {RunId} left its critical section in state {State}; automatic credit is disabled unless Removed was already durable.",
                startedRun.RunId,
                result.Run.State);
            return result.Run;
        }
        catch (Exception finalizationException)
        {
            _logger.LogSafeError(
                finalizationException,
                "Could not persist the uncertain outcome for resource settlement {RunId}; lease recovery will retry conservatively.",
                startedRun.RunId);
            return null;
        }
    }

    private async Task<ExtractionSettlementRun> CreditRemovedRunAsync(
        ExtractionSettlementRun run,
        string? reconciliationReason,
        CancellationToken cancellationToken)
    {
        if (run.State is not
            (ExtractionSettlementState.Removed or ExtractionSettlementState.Credited))
        {
            return run;
        }
        var leaseId = RequireLeaseId(run);
        var credited = run;
        if (run.State == ExtractionSettlementState.Removed)
        {
            var credit = await _runs.TryCreditRemovedAsync(
                run.RunId,
                run.Revision,
                leaseId,
                cancellationToken);
            if (!credit.Applied)
            {
                return credit.Run;
            }
            credited = credit.Run;
        }
        var settled = await _runs.TryMarkSettledAsync(
            credited.RunId,
            credited.Revision,
            leaseId,
            reconciliationReason,
            cancellationToken);
        if (settled.Run.State == ExtractionSettlementState.Settled && _reliableTasks is not null)
        {
            try
            {
                _ = await _reliableTasks.RecordResourceSettlementAsync(
                    settled.Run,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                // The wallet credit and settlement state are already durable.
                // Never roll them back or report an ambiguous economy outcome
                // because the independent task projection is temporarily down.
                _logger.LogSafeError(
                    exception,
                    "Reliable tasks could not project settled resource exchange {RunId}.",
                    settled.Run.RunId);
            }
        }
        return settled.Run;
    }

    private async Task<ExtractionSettlementRun> ExecuteNativeSettlementAsync(
        ExtractionAccountContext context,
        ExtractionSettlementRun quote,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var native = RequireNativeInventoryAdapter();
        if (!native.StableSettlementAvailable)
        {
            throw new ExtractionModeException(
                "NATIVE_INVENTORY_CONSUME_CAPABILITY_MISSING",
                "Native Bridge 未声明稳定 inventory.consume；experimental 能力不能用于资源入账。",
                StatusCodes.Status503ServiceUnavailable);
        }
        var snapshot = quote.NativeInventorySnapshot ?? throw new ExtractionModeException(
            "NATIVE_QUOTE_SNAPSHOT_MISSING",
            "该报价不包含可用于原子扣物的完整 Native 背包快照，请重新报价。",
            StatusCodes.Status409Conflict);
        if (!string.Equals(
                snapshot.OwnerPlayerUid,
                context.IdentityBinding?.PlayerUid,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                quote.QuoteSnapshotHash,
                ExtractionNativeInventoryCanonicalizer.Hash(snapshot),
                StringComparison.Ordinal))
        {
            throw new ExtractionModeException(
                "NATIVE_QUOTE_SNAPSHOT_INVALID",
                "报价中的 PlayerUID 或完整背包快照哈希不再可信，请重新报价。",
                StatusCodes.Status409Conflict);
        }

        var totals = ExtractionNativeInventoryCanonicalizer.AggregateTotals(snapshot);
        foreach (var line in quote.Items)
        {
            if (totals.GetValueOrDefault(line.ItemId) < line.Quantity)
            {
                throw new ExtractionModeException(
                    "NATIVE_QUOTE_SNAPSHOT_INSUFFICIENT",
                    "报价快照中的资源数量不足，不能构造原子扣物请求。",
                    StatusCodes.Status409Conflict);
            }
        }
        var preDeleteTotals = quote.Items.ToDictionary(
            line => line.ItemId,
            line => totals.GetValueOrDefault(line.ItemId),
            StringComparer.OrdinalIgnoreCase);
        var payload = native.CreateConsumePayload(snapshot, quote.Items);
        var requestHash = native.ComputeRequestHash(quote.RunId, _options.ServerId, payload);
        var start = await _runs.StartConsumptionAsync(
            quote.RunId,
            context.Account.ExternalUserId,
            idempotencyKey,
            preDeleteTotals,
            cancellationToken,
            requestHash);
        if (start.IdempotencyConflict)
        {
            throw new ExtractionModeException(
                "IDEMPOTENCY_CONFLICT",
                "该 Idempotency-Key 已绑定不同的 Native 扣物请求。",
                StatusCodes.Status409Conflict);
        }
        if (!start.Started)
        {
            return start.Run;
        }

        var leaseId = RequireLeaseId(start.Run);
        using var criticalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(_options.SettlementQueueOperationTimeoutSeconds, 30, 600)));
        var settlementToken = criticalTimeout.Token;
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeatTask = MaintainSettlementLeaseAsync(
            start.Run,
            leaseId,
            "http-settlement",
            criticalTimeout,
            heartbeatStop.Token);
        var receiptPersisted = false;
        try
        {
            var outcome = await native.ConsumeAsync(
                _options.ServerId,
                payload,
                requestHash,
                idempotencyKey,
                settlementToken);
            var recorded = await _runs.TryRecordNativeConsumeReceiptAsync(
                start.Run.RunId,
                start.Run.Revision,
                leaseId,
                outcome.Receipt,
                settlementToken);
            await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
            if (!recorded.Applied && recorded.Run.NativeConsumeReceipt is null)
            {
                return recorded.Run;
            }
            receiptPersisted = true;
            using var finalizationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await FinalizeRecordedNativeOutcomeAsync(
                recorded.Run,
                leaseId,
                recorded.Run.NativeConsumeReceipt ?? outcome.Receipt,
                finalizationTimeout.Token);
        }
        catch (Exception exception) when (receiptPersisted)
        {
            await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
            _logger.LogSafeWarning(
                exception,
                "Native consume receipt for extraction run {RunId} is durable; recovery will resume finalization without dispatching another consume command.",
                start.Run.RunId);
            return await _runs.GetAsync(start.Run.RunId, CancellationToken.None) ?? start.Run;
        }
        catch (OperationCanceledException exception)
        {
            await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
            var current = await TryMarkCriticalSectionUncertainAsync(
                start.Run,
                leaseId,
                "NATIVE_INVENTORY_CONSUME_CANCELLED",
                "Native 扣物是否完成无法确认，已停止自动重试和入账。",
                exception);
            if (current is not null)
            {
                return current;
            }
            throw;
        }
        catch (Exception exception)
        {
            await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
            _ = await TryMarkCriticalSectionUncertainAsync(
                start.Run,
                leaseId,
                "NATIVE_INVENTORY_CONSUME_FINALIZATION_FAILED",
                "Native 扣物回执持久化或收尾失败，已转人工对账。",
                exception);
            throw;
        }
        finally
        {
            await StopLeaseHeartbeatAsync(heartbeatStop, heartbeatTask);
        }
    }

    private async Task<ExtractionSettlementRun> CompleteRecordedNativeSuccessAsync(
        ExtractionSettlementRun run,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        var removed = await _runs.TryMarkRemovedAsync(
            run.RunId,
            run.Revision,
            leaseId,
            cancellationToken);
        return removed.Applied
            ? await CreditRemovedRunAsync(removed.Run, null, cancellationToken)
            : removed.Run;
    }

    private async Task<ExtractionSettlementRun> FinalizeRecordedNativeOutcomeAsync(
        ExtractionSettlementRun run,
        Guid leaseId,
        ExtractionNativeConsumeReceipt receipt,
        CancellationToken cancellationToken) =>
        receipt.Disposition switch
        {
            ExtractionNativeConsumeDisposition.Succeeded =>
                await CompleteRecordedNativeSuccessAsync(run, leaseId, cancellationToken),
            ExtractionNativeConsumeDisposition.Failed =>
                (await _runs.TryMarkFailedAsync(
                    run.RunId,
                    run.Revision,
                    leaseId,
                    receipt.ErrorCode ?? "NATIVE_INVENTORY_CONSUME_FAILED",
                    receipt.ErrorMessage ?? "Native 明确拒绝资源扣除。",
                    cancellationToken)).Run,
            _ =>
                (await _runs.TryMarkUncertainAsync(
                    run.RunId,
                    run.Revision,
                    leaseId,
                    receipt.ErrorCode ?? "NATIVE_INVENTORY_CONSUME_UNCERTAIN",
                    receipt.ErrorMessage ?? "Native 扣物证据不确定，禁止自动重试或入账。",
                    cancellationToken)).Run
        };

    private IExtractionNativeInventoryAdapter RequireNativeInventoryAdapter() =>
        _nativeInventory ?? throw new ExtractionModeException(
            "NATIVE_INVENTORY_ADAPTER_NOT_CONFIGURED",
            "正式资源结算要求 Control API Native 背包 adapter。",
            StatusCodes.Status503ServiceUnavailable);

    private static Guid RequireLeaseId(ExtractionSettlementRun run) =>
        run.LeaseId ?? throw new InvalidDataException(
            $"Extraction run '{run.RunId}' in state {run.State} does not own a settlement lease.");

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

    private async Task<RuntimeZone> RequireStableExtractionZoneAsync(
        string userId,
        EconomyRuntimeContent? content,
        string? expectedZoneId,
        bool includeScheduleGrace,
        CancellationToken cancellationToken)
    {
        var candidateZones = GetRuntimeZones(content, _timeProvider.GetUtcNow(), includeScheduleGrace)
            .Where(zone => expectedZoneId is null || string.Equals(
                zone.Id,
                expectedZoneId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var zones = candidateZones.Where(zone => zone.Open).ToArray();
        if (zones.Length == 0)
        {
            var nextOpensAt = candidateZones.Select(zone => zone.NextOpensAt)
                .Where(value => value is not null)
                .Min();
            throw new ExtractionModeException(
                "EXTRACTION_ZONE_CLOSED",
                nextOpensAt is null
                    ? "当前报价对应的资源回收区域未开放，且没有可用的下一开放时间。"
                    : $"当前报价对应的资源回收区域未开放；下一开放时间为 {nextOpensAt:O}。",
                StatusCodes.Status409Conflict,
                nextOpensAt);
        }
        var first = await _coordinator.GetLivePlayerAsync(userId, cancellationToken);
        var firstZone = FindZone(first, zones) ?? throw new ExtractionModeException(
            "PLAYER_OUTSIDE_EXTRACTION_ZONE",
            "玩家不在当前开放的资源回收区域内。",
            StatusCodes.Status409Conflict);
        await Task.Delay(_options.ExtractionPositionSampleMilliseconds, cancellationToken);
        var second = await _coordinator.GetLivePlayerAsync(userId, cancellationToken);
        var secondZone = FindZone(second, zones);
        if (secondZone is null || !string.Equals(firstZone.Id, secondZone.Id, StringComparison.Ordinal))
        {
            throw new ExtractionModeException(
                "EXTRACTION_ZONE_NOT_STABLE",
                "两次位置采样未持续处于同一资源回收区域。",
                StatusCodes.Status409Conflict);
        }
        return firstZone;
    }

    private static RuntimeZone? FindZone(
        ExtractionLivePlayer player,
        IReadOnlyList<RuntimeZone> zones)
    {
        if (player.MapX is not double x || player.MapY is not double y)
        {
            return null;
        }
        return zones.FirstOrDefault(zone =>
        {
            return ExtractionZoneGeometry.IsInside(
                x,
                y,
                zone.MapX,
                zone.MapY,
                zone.Radius);
        });
    }

    private IReadOnlyList<RuntimeZone> GetRuntimeZones(
        EconomyRuntimeContent? content,
        DateTimeOffset now,
        bool includeScheduleGrace)
    {
        if (content is null)
        {
            return _options.ExtractionZones.Select(zone => new RuntimeZone(
                zone.Id,
                zone.DisplayName,
                zone.RouteHint,
                zone.RiskHint,
                zone.MapX,
                zone.MapY,
                zone.Radius,
                10_000,
                true,
                false,
                null,
                [])).ToArray();
        }
        return content.ExchangeZones.Select(zone =>
        {
            var staticOpen = EconomyContentSchedule.IsOpen(
                zone,
                now,
                _timeZone,
                includeScheduleGrace);
            var dynamicZone = content.DynamicEconomy?.Zones.FirstOrDefault(candidate =>
                string.Equals(candidate.ZoneId, zone.ZoneId, StringComparison.OrdinalIgnoreCase));
            var dynamicOpen = content.DynamicEconomy is null ||
                              EconomyDynamicEconomyRuntime.IsZoneOpen(
                                  content.DynamicEconomy,
                                  zone.ZoneId,
                                  now,
                                  includeScheduleGrace);
            var open = staticOpen && dynamicOpen;
            var hotspot = content.DynamicEconomy is null
                ? content.HotspotZoneIds.Contains(zone.ZoneId)
                : EconomyDynamicEconomyRuntime.IsHotspot(
                    content.DynamicEconomy,
                    zone.ZoneId,
                    now,
                    includeScheduleGrace);
            var activeEvents = content.DynamicEconomy is null
                ? []
                : EconomyDynamicEconomyRuntime.ActiveEvents(
                    content.DynamicEconomy,
                    now,
                    includeScheduleGrace);
            var effectiveMultiplier = EconomyContentRuntimeService.CalculateRuntimeZoneYieldMultiplierBasisPoints(
                content.Version,
                zone,
                hotspot,
                activeEvents);
            return new RuntimeZone(
                zone.ZoneId,
                zone.DisplayName,
                zone.RouteHint,
                zone.RiskHint,
                zone.MapX,
                zone.MapY,
                zone.Radius,
                effectiveMultiplier,
                open,
                hotspot,
                open ? null : EconomyDynamicEconomyRuntime.NextZoneOpen(
                    content.Version,
                    zone,
                    now,
                    _timeZone),
                zone.OpenWindows,
                dynamicZone,
                activeEvents);
        }).ToArray();
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

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

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

    private static string HashSnapshot(
        IReadOnlyDictionary<string, long> totals,
        IEnumerable<string> quotedItemIds)
    {
        var canonical = string.Join('\n', quotedItemIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
    private sealed record RuntimeResource(
        string DisplayName,
        long UnitValue,
        string? Category,
        string? IconKey,
        ContentRarity? Rarity,
        string? Usage);
    private sealed record RuntimeZone(
        string Id,
        string DisplayName,
        string RouteHint,
        string RiskHint,
        double MapX,
        double MapY,
        double Radius,
        int YieldMultiplierBasisPoints,
        bool Open,
        bool Hotspot,
        DateTimeOffset? NextOpensAt,
        IReadOnlyList<ContentExchangeWindow> OpenWindows,
        EconomyDynamicZoneEvidence? DynamicZone = null,
        IReadOnlyList<EconomyWorldEventEvidence>? ActiveWorldEventsValue = null)
    {
        public IReadOnlyList<EconomyWorldEventEvidence> ActiveWorldEvents =>
            ActiveWorldEventsValue ?? [];
    }
    private sealed record InventorySnapshot(IReadOnlyDictionary<string, long> Totals);
}

public sealed record ExtractionMapPoint(double X, double Y);

public sealed record PlayerExtractionZone(
    string Id,
    string Name,
    string RouteHint,
    string RiskHint,
    ExtractionMapPoint MapPosition,
    ExtractionMapPoint WorldPosition,
    double Radius,
    double WorldRadius,
    double? DistanceToCenter,
    double? WorldDistanceToCenter,
    double? DistanceToBoundary,
    double? WorldDistanceToBoundary,
    bool? Inside,
    bool Open = true,
    bool Hotspot = false,
    int YieldMultiplierBasisPoints = 10_000,
    DateTimeOffset? NextOpensAt = null,
    IReadOnlyList<ContentExchangeWindow>? OpenWindows = null,
    ContentZoneRiskLevel? RiskLevel = null,
    bool DynamicSelectedOpen = true,
    EconomyEventWindowEvidence? DynamicOpenWindow = null,
    EconomyEventWindowEvidence? HotspotWindow = null,
    string? DynamicPolicyVersion = null,
    string? DynamicSeed = null,
    IReadOnlyList<EconomyWorldEventEvidence>? WorldEvents = null);

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
    IReadOnlyList<PlayerExtractionZone> Zones,
    DateTimeOffset? NextOpensAt = null,
    string? DynamicPolicyVersion = null,
    string? DynamicSeed = null,
    IReadOnlyList<EconomyWorldEventEvidence>? WorldEvents = null);

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
                    using var scope = ControlPlaneLog.BeginWorker(
                        _logger,
                        nameof(ExtractionSettlementRecoveryWorker),
                        "settlement.reconcile",
                        run.RunId);
                    try
                    {
                        _ = await _settlement.ReconcileAsync(run, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogSafeError(
                            exception,
                            "Extraction settlement recovery failed for run {RunId} revision {Revision}.",
                            run.RunId,
                            run.Revision);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                using var scope = ControlPlaneLog.BeginWorker(
                    _logger,
                    nameof(ExtractionSettlementRecoveryWorker),
                    "settlement.scan.failure");
                _logger.LogSafeError(exception, "Extraction settlement recovery failed.");
            }
            await Task.Delay(2_000, stoppingToken);
        }
    }
}

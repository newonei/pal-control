using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed partial class ExtractionModeCoordinator
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ExtractionCommerceService _commerce;
    private readonly PalDefenderRestClient _palDefender;
    private readonly PalworldResourceCatalogService _catalogService;
    private readonly SaveManagementService _saveManagement;
    private readonly ExtractionModeOptions _options;
    private readonly TimeZoneInfo _timeZone;
    private readonly ILogger<ExtractionModeCoordinator> _logger;
    private volatile bool _initialized;

    public ExtractionModeCoordinator(
        ExtractionCommerceService commerce,
        PalDefenderRestClient palDefender,
        PalworldResourceCatalogService catalogService,
        SaveManagementService saveManagement,
        IOptions<ExtractionModeOptions> options,
        ILogger<ExtractionModeCoordinator> logger)
    {
        _commerce = commerce;
        _palDefender = palDefender;
        _catalogService = catalogService;
        _saveManagement = saveManagement;
        _options = options.Value;
        _timeZone = _options.ResolveTimeZone();
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;
    public string ServerId => _options.ServerId;

    public async Task<ExtractionSeason> GetActiveSeasonAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _commerce.GetActiveSeasonAsync(_options.ServerId, cancellationToken)
            ?? throw new InvalidOperationException("The extraction mode has no active season.");
    }

    public async Task<ExtractionAccountContext> GetAccountContextAsync(
        string userId,
        bool requireOnline,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var normalizedUserId = NormalizeUserId(userId);
        var provider = normalizedUserId[..normalizedUserId.IndexOf('_')];
        var existing = await _commerce.FindPlayerAsync(provider, normalizedUserId, cancellationToken);
        var livePlayer = await FindPalDefenderPlayerAsync(normalizedUserId, cancellationToken);
        if (requireOnline && (livePlayer is null || !livePlayer.Online))
        {
            throw new ExtractionModeException(
                "PLAYER_NOT_ONLINE",
                "玩家必须在线，商城才会扣款并发货。",
                StatusCodes.Status409Conflict);
        }
        if (existing is null && livePlayer is null)
        {
            throw new ExtractionModeException(
                "PLAYER_NOT_FOUND",
                "PalDefender 中找不到该平台 UserId，无法创建跨档账户。",
                StatusCodes.Status404NotFound);
        }

        var account = existing;
        if (account is null || (livePlayer is not null &&
            !string.Equals(account.DisplayName, livePlayer.DisplayName, StringComparison.Ordinal)))
        {
            account = await _commerce.RegisterPlayerAsync(
                provider,
                normalizedUserId,
                livePlayer?.DisplayName ?? existing?.DisplayName ?? normalizedUserId,
                cancellationToken);
        }

        var season = await _commerce.GetActiveSeasonAsync(_options.ServerId, cancellationToken)
            ?? throw new InvalidOperationException("The extraction mode has no active season.");
        await SeedWalletAsync(account, season, cancellationToken);
        var wallet = await _commerce.GetWalletAsync(account.AccountId, season.SeasonId, cancellationToken);
        return new ExtractionAccountContext(account, season, wallet, livePlayer?.Online ?? false);
    }

    public async Task<ExtractionLivePlayer> GetLivePlayerAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var player = await FindPalDefenderPlayerAsync(normalizedUserId, cancellationToken);
        if (player is null || !player.Online)
        {
            throw new ExtractionModeException(
                "PLAYER_NOT_ONLINE",
                "玩家必须在线并加载完成才能执行撤离结算。",
                StatusCodes.Status409Conflict);
        }
        if (player.MapX is null || player.MapY is null)
        {
            throw new ExtractionModeException(
                "PLAYER_POSITION_UNAVAILABLE",
                "PalDefender 未返回玩家地图坐标。",
                StatusCodes.Status409Conflict);
        }
        return player;
    }

    public async Task<ExtractionLivePlayer?> TryGetPlayerLocationAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var normalizedUserId = NormalizeUserId(userId);
        return await FindPalDefenderPlayerAsync(normalizedUserId, cancellationToken);
    }

    public async Task EnsurePlayerOnlineForPortalAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var player = await FindPalDefenderPlayerAsync(normalizedUserId, cancellationToken);
        if (player is null || !player.Online)
        {
            throw new ExtractionModeException(
                "PLAYER_NOT_ONLINE",
                "The player must be online to receive a portal login code.",
                StatusCodes.Status409Conflict);
        }
    }

    public Task<IReadOnlyList<ShopProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        _commerce.ListProductsAsync(includeInactive: false, cancellationToken);

    public async Task<ExtractionSeason> CommitRolloverAsync(
        string worldId,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeWorldId(worldId);
        var resolvedWorld = await ResolveActiveWorldAsync(cancellationToken);
        if (!string.Equals(normalized, NormalizeWorldId(resolvedWorld.WorldGuid), StringComparison.Ordinal))
        {
            throw new ExtractionModeException(
                "ROLLOVER_WORLD_MISMATCH",
                "提交的 worldId 与当前 DedicatedServerName、存档目录或官方 REST world GUID 不一致。",
                StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;
        var seasons = await _commerce.ListSeasonsAsync(_options.ServerId, cancellationToken);
        var active = seasons.SingleOrDefault(season => season.State == ExtractionSeasonState.Active);
        var (startsAt, endsAt, code, displayName) = GetCurrentSeasonWindow(now);

        if (active is not null &&
            string.Equals(active.Code, code, StringComparison.OrdinalIgnoreCase))
        {
            if (active.WorldId is null)
            {
                return await _commerce.UpsertSeasonAsync(
                    active.SeasonId,
                    new ExtractionSeasonDefinition(
                        active.ServerId,
                        active.Code,
                        active.DisplayName,
                        normalized,
                        active.StartsAt,
                        active.EndsAt,
                        active.State),
                    active.Revision,
                    cancellationToken);
            }
            if (string.Equals(NormalizeWorldId(active.WorldId), normalized, StringComparison.Ordinal))
            {
                return active;
            }
            throw new ExtractionModeException(
                "ROLLOVER_WINDOW_NOT_ADVANCED",
                "当前周档已经绑定另一个世界；只有进入下一个周窗口后才能提交新世界。",
                StatusCodes.Status409Conflict);
        }

        if (active is not null)
        {
            if (now < active.EndsAt)
            {
                throw new ExtractionModeException(
                    "ROLLOVER_TOO_EARLY",
                    "当前周档尚未结束，禁止提前改绑经济赛季。",
                    StatusCodes.Status409Conflict);
            }
            _ = await _commerce.UpsertSeasonAsync(
                active.SeasonId,
                new ExtractionSeasonDefinition(
                    active.ServerId,
                    active.Code,
                    active.DisplayName,
                    active.WorldId,
                    active.StartsAt,
                    active.EndsAt,
                    ExtractionSeasonState.Closed),
                active.Revision,
                cancellationToken);
        }

        seasons = await _commerce.ListSeasonsAsync(_options.ServerId, cancellationToken);
        var target = seasons.SingleOrDefault(season =>
            string.Equals(season.Code, code, StringComparison.OrdinalIgnoreCase));
        if (target?.WorldId is not null &&
            !string.Equals(NormalizeWorldId(target.WorldId), normalized, StringComparison.Ordinal))
        {
            throw new ExtractionModeException(
                "ROLLOVER_TARGET_CONFLICT",
                "目标周档已经绑定另一个世界，必须人工核对后处理。",
                StatusCodes.Status409Conflict);
        }
        return await _commerce.UpsertSeasonAsync(
            target?.SeasonId,
            new ExtractionSeasonDefinition(
                _options.ServerId,
                code,
                displayName,
                normalized,
                startsAt,
                endsAt,
                ExtractionSeasonState.Active),
            target?.Revision,
            cancellationToken);
    }

    public async Task<ExtractionRolloverPreflight> GetRolloverPreflightAsync(
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var seasons = await _commerce.ListSeasonsAsync(_options.ServerId, cancellationToken);
        var active = seasons.SingleOrDefault(season => season.State == ExtractionSeasonState.Active);
        var resolvedWorld = await ResolveActiveWorldAsync(cancellationToken);
        var actualWorldId = NormalizeWorldId(resolvedWorld.WorldGuid);
        var (targetStartsAt, targetEndsAt, targetCode, _) = GetCurrentSeasonWindow(now);

        string reason;
        var canStartWorldSwitch = false;
        if (active is null)
        {
            reason = "没有活动经济赛季，必须先人工核对。";
        }
        else if (active.WorldId is null)
        {
            reason = "活动经济赛季尚未绑定当前世界，不能启动周换档。";
        }
        else if (!string.Equals(
                     NormalizeWorldId(active.WorldId),
                     actualWorldId,
                     StringComparison.Ordinal))
        {
            reason = "活动经济赛季与当前运行世界不一致。";
        }
        else if (now < active.EndsAt)
        {
            reason = $"当前周档要到 {active.EndsAt:O} 才能切换。";
        }
        else if (string.Equals(active.Code, targetCode, StringComparison.OrdinalIgnoreCase))
        {
            reason = "目标周窗口尚未推进，不能创建新世界。";
        }
        else
        {
            canStartWorldSwitch = true;
            reason = "周档已到期且当前世界身份一致，可以进入受控换档。";
        }

        return new ExtractionRolloverPreflight(
            now,
            active?.SeasonId,
            active?.Code,
            active?.WorldId,
            active?.EndsAt,
            actualWorldId,
            targetCode,
            targetStartsAt,
            targetEndsAt,
            canStartWorldSwitch,
            reason);
    }

    public async Task EnsureSeasonMatchesActiveWorldAsync(
        ExtractionSeason season,
        CancellationToken cancellationToken)
    {
        if (season.WorldId is null)
        {
            throw new ExtractionModeException(
                "SEASON_WORLD_UNBOUND",
                "当前经济赛季尚未绑定 Palworld 世界，商城与撤离保持关闭。",
                StatusCodes.Status423Locked);
        }
        var resolvedWorld = await ResolveActiveWorldAsync(cancellationToken);
        if (!string.Equals(
                NormalizeWorldId(season.WorldId),
                NormalizeWorldId(resolvedWorld.WorldGuid),
                StringComparison.Ordinal))
        {
            throw new ExtractionModeException(
                "SEASON_WORLD_MISMATCH",
                "当前 Palworld 世界与经济赛季不一致，商城与撤离保持关闭。",
                StatusCodes.Status423Locked);
        }
    }

    public Task<IReadOnlyList<ShopOrder>> ListOrdersAsync(
        Guid accountId,
        Guid seasonId,
        int limit,
        CancellationToken cancellationToken) =>
        _commerce.ListOrdersAsync(accountId, seasonId, limit, cancellationToken);

    public Task<IReadOnlyList<WalletLedgerEntry>> ListLedgerAsync(
        Guid accountId,
        Guid seasonId,
        int limit,
        CancellationToken cancellationToken) =>
        _commerce.GetLedgerAsync(accountId, seasonId, limit, cancellationToken);

    public async Task<ShopPurchaseResult> PurchaseAsync(
        ExtractionAccountContext context,
        ShopProduct product,
        int quantity,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await EnsureSeasonMatchesActiveWorldAsync(context.Season, cancellationToken);
        return await _commerce.PurchaseAsync(
            new ShopPurchaseRequest(
                context.Account.AccountId,
                context.Season.SeasonId,
                _options.ServerId,
                context.Account.ExternalUserId,
                [new ShopPurchaseLineInput(product.Sku, quantity)],
                idempotencyKey,
                "local-console",
                $"购买商城商品 {product.Sku}"),
            cancellationToken);
    }

    public async Task<ShopProduct> FindProductAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var products = await _commerce.ListProductsAsync(includeInactive: false, cancellationToken);
        return products.SingleOrDefault(product => product.ProductId == productId)
            ?? throw new ExtractionModeException(
                "PRODUCT_NOT_FOUND",
                "商城商品不存在或已下架。",
                StatusCodes.Status404NotFound);
    }

    public Task<WalletAdjustmentResult> AdjustWalletAsync(
        ExtractionAccountContext context,
        ExtractionCurrency currency,
        long delta,
        string reason,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        _commerce.AdjustWalletAsync(
            new WalletAdjustmentRequest(
                context.Account.AccountId,
                currency == ExtractionCurrency.SeasonVoucher ? context.Season.SeasonId : null,
                currency,
                delta,
                reason,
                "operator_adjustment",
                idempotencyKey,
                "local-console",
                idempotencyKey),
            cancellationToken);

    public DateTimeOffset GetNextDailyRefresh(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, _timeZone);
        var nextLocal = localNow.Date.AddHours(_options.DailyRefreshHour);
        if (localNow.DateTime >= nextLocal)
        {
            nextLocal = nextLocal.AddDays(1);
        }
        return new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextLocal, DateTimeKind.Unspecified), _timeZone),
            TimeSpan.Zero);
    }

    public static string ToClientCurrency(ExtractionCurrency currency) => currency switch
    {
        ExtractionCurrency.MarketCoin => "merchantCoin",
        ExtractionCurrency.SeasonVoucher => "weeklyTicket",
        _ => throw new ArgumentOutOfRangeException(nameof(currency))
    };

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new ExtractionModeException(
                "EXTRACTION_MODE_DISABLED",
                "摸金搜打撤模式尚未启用。",
                StatusCodes.Status503ServiceUnavailable);
        }
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSeasonAsync(cancellationToken);
            if (!_initialized)
            {
                await EnsureProductsAsync(cancellationToken);
            }
            await EnsureDailyRotationAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task EnsureSeasonAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var seasons = await _commerce.ListSeasonsAsync(_options.ServerId, cancellationToken);
        var active = seasons.SingleOrDefault(season => season.State == ExtractionSeasonState.Active);
        if (active is not null)
        {
            if (now < active.StartsAt || now >= active.EndsAt)
            {
                throw new ExtractionModeException(
                    "SEASON_ROLLOVER_REQUIRED",
                    "周档时间窗口已经结束；必须完成受控世界换档并提交新赛季后才能继续交易。",
                    StatusCodes.Status423Locked);
            }
            if (active.WorldId is null)
            {
                throw new ExtractionModeException(
                    "SEASON_WORLD_UNBOUND",
                    "当前经济赛季尚未绑定 Palworld 世界，商城与撤离保持关闭。",
                    StatusCodes.Status423Locked);
            }
            return;
        }

        if (seasons.Count > 0)
        {
            throw new ExtractionModeException(
                "SEASON_ROLLOVER_REQUIRED",
                "没有活动经济赛季；必须通过受控换档提交当前世界。",
                StatusCodes.Status423Locked);
        }

        var (startsAt, endsAt, code, displayName) = GetCurrentSeasonWindow(now);
        _ = await _commerce.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                _options.ServerId,
                code,
                displayName,
                null,
                startsAt,
                endsAt,
                ExtractionSeasonState.Active),
            null,
            cancellationToken);
        throw new ExtractionModeException(
            "SEASON_WORLD_UNBOUND",
            "已创建首个经济赛季；请在维护状态下提交当前 worldId 后再开放商城与撤离。",
            StatusCodes.Status423Locked);
    }

    private async Task<ResolvedSaveWorld> ResolveActiveWorldAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _saveManagement.ResolveActiveWorldAsync(_options.ServerId, cancellationToken);
        }
        catch (SaveManagementException exception)
        {
            throw new ExtractionModeException(
                "ACTIVE_WORLD_VERIFICATION_FAILED",
                $"无法证明当前 Palworld 世界身份：{exception.Code}。",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string NormalizeWorldId(string worldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldId);
        var normalized = worldId.Trim().ToUpperInvariant();
        if (normalized.Length != 32 || normalized.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ExtractionModeException(
                "INVALID_WORLD_ID",
                "worldId 必须是 32 位十六进制 DedicatedServerName。",
                StatusCodes.Status400BadRequest);
        }
        return normalized;
    }

    private async Task EnsureProductsAsync(CancellationToken cancellationToken)
    {
        var existing = await _commerce.ListProductsAsync(includeInactive: true, cancellationToken);
        var existingSkus = existing.Select(product => product.Sku).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = await _catalogService.GetAsync(cancellationToken);
        var knownItemIds = catalog.Items.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var product in SeedProducts())
        {
            if (existingSkus.Contains(product.Sku))
            {
                continue;
            }
            var unknown = product.ItemGrants.FirstOrDefault(grant => !knownItemIds.Contains(grant.ItemId));
            if (unknown is not null)
            {
                _logger.LogError(
                    "Extraction shop product {Sku} references unknown item {ItemId}; it was not seeded.",
                    product.Sku,
                    unknown.ItemId);
                continue;
            }
            await _commerce.UpsertProductAsync(product, null, "system-bootstrap", cancellationToken);
        }
    }

    private async Task EnsureDailyRotationAsync(CancellationToken cancellationToken)
    {
        var businessDate = GetBusinessDate(DateTimeOffset.UtcNow);
        var actor = $"daily-rotation:{businessDate:yyyy-MM-dd}";
        var baseDefinitions = SeedProducts().ToDictionary(
            product => product.Sku,
            StringComparer.OrdinalIgnoreCase);
        var products = await _commerce.ListProductsAsync(includeInactive: true, cancellationToken);
        foreach (var product in products)
        {
            if (!baseDefinitions.TryGetValue(product.Sku, out var baseline))
            {
                continue;
            }
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{businessDate:yyyy-MM-dd}|{product.Sku}"));
            var multiplier = new[] { 90L, 95L, 100L, 105L, 110L }[digest[0] % 5];
            var dailyPrice = Math.Max(1, checked((baseline.UnitPrice * multiplier + 50) / 100));
            if (product.UnitPrice == dailyPrice &&
                string.Equals(product.UpdatedBy, actor, StringComparison.Ordinal))
            {
                continue;
            }
            await _commerce.UpsertProductAsync(
                new ShopProductDefinition(
                    product.Sku,
                    product.DisplayName,
                    product.Description,
                    product.PriceCurrency,
                    dailyPrice,
                    product.ItemGrants,
                    product.PurchaseLimitPerSeason,
                    product.Active,
                    product.AvailableFrom,
                    product.AvailableUntil),
                product.Revision,
                actor,
                cancellationToken);
        }
    }

    private async Task SeedWalletAsync(
        ExtractionAccount account,
        ExtractionSeason season,
        CancellationToken cancellationToken)
    {
        if (_options.InitialMarketCoin > 0)
        {
            var result = await _commerce.GrantMarketCoinAsync(
                account.AccountId,
                _options.InitialMarketCoin,
                "account_bootstrap",
                account.AccountId.ToString("N"),
                $"bootstrap-market-{account.AccountId:N}",
                "system-bootstrap",
                "开发服初始商域币",
                cancellationToken);
            ThrowIfWalletFailed(result);
        }
        if (_options.InitialSeasonVoucher > 0)
        {
            var result = await _commerce.GrantSeasonVoucherAsync(
                account.AccountId,
                season.SeasonId,
                _options.InitialSeasonVoucher,
                "season_bootstrap",
                season.SeasonId.ToString("N"),
                $"bootstrap-voucher-{season.SeasonId:N}-{account.AccountId:N}",
                "system-bootstrap",
                "开发服本周初始战备券",
                cancellationToken);
            ThrowIfWalletFailed(result);
        }
    }

    private async Task<ExtractionLivePlayer?> FindPalDefenderPlayerAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var response = await _palDefender.GetAsync("players", cancellationToken);
        if (!response.IsSuccess || response.Json is not JsonObject root ||
            GetProperty(root, "Players") is not JsonArray players)
        {
            return null;
        }
        foreach (var node in players)
        {
            if (node is not JsonObject player)
            {
                continue;
            }
            var candidate = GetString(player, "UserId");
            if (!string.Equals(candidate, userId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var displayName = GetString(player, "Name") ?? userId;
            var online = string.Equals(
                GetString(player, "Status"),
                "Online",
                StringComparison.OrdinalIgnoreCase);
            var mapLocation = GetProperty(player, "MapLocation") as JsonObject;
            return new ExtractionLivePlayer(
                candidate ?? userId,
                displayName,
                online,
                GetString(player, "PlayerUID"),
                mapLocation is null ? null : GetDouble(mapLocation, "x"),
                mapLocation is null ? null : GetDouble(mapLocation, "y"));
        }
        return null;
    }

    private (DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Code, string DisplayName)
        GetCurrentSeasonWindow(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, _timeZone);
        var daysSinceReset = ((int)localNow.DayOfWeek - (int)_options.WeeklyResetDay + 7) % 7;
        var startLocal = localNow.Date.AddDays(-daysSinceReset).AddHours(_options.WeeklyResetHour);
        if (localNow.DateTime < startLocal)
        {
            startLocal = startLocal.AddDays(-7);
        }
        var endLocal = startLocal.AddDays(7);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified),
            _timeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified),
            _timeZone);
        var week = ISOWeek.GetWeekOfYear(startLocal);
        var year = ISOWeek.GetYear(startLocal);
        return (
            new DateTimeOffset(startUtc, TimeSpan.Zero),
            new DateTimeOffset(endUtc, TimeSpan.Zero),
            $"S{year}-W{week:00}",
            $"第 {week:00} 周行动");
    }

    private DateOnly GetBusinessDate(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, _timeZone);
        var adjusted = localNow.AddHours(-_options.DailyRefreshHour);
        return DateOnly.FromDateTime(adjusted.DateTime);
    }

    private static IReadOnlyList<ShopProductDefinition> SeedProducts() =>
    [
        new(
            "STARTER-CAPTURE",
            "新兵捕捉补给",
            "基础帕鲁球、烤野莓和低级药品，适合新档起步。",
            ExtractionCurrency.MarketCoin,
            120,
            [new("PalSphere", 10), new("Baked_Berries", 20), new("Herbs", 3)],
            3,
            true,
            null,
            null),
        new(
            "FIELD-MEDIC",
            "野战医疗包",
            "医疗用品与应急食物。",
            ExtractionCurrency.SeasonVoucher,
            60,
            [new("Medicines", 3), new("Baked_Berries", 10)],
            5,
            true,
            null,
            null),
        new(
            "MEGA-SPHERE",
            "高级捕捉包",
            "十枚高级帕鲁球。",
            ExtractionCurrency.SeasonVoucher,
            80,
            [new("PalSphere_Mega", 10)],
            5,
            true,
            null,
            null),
        new(
            "COARSE-AMMO",
            "粗制弹药箱",
            "一百发粗制弹药。",
            ExtractionCurrency.SeasonVoucher,
            70,
            [new("RoughBullet", 100)],
            10,
            true,
            null,
            null),
        new(
            "STARTER-CROSSBOW",
            "弩手战备包",
            "一把弩与一百支箭，每周限购一次。",
            ExtractionCurrency.MarketCoin,
            300,
            [new("BowGun", 1), new("Arrow", 100)],
            1,
            true,
            null,
            null)
    ];

    private static string NormalizeUserId(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var normalized = userId.Trim().ToLowerInvariant();
        if (!UserIdPattern().IsMatch(normalized))
        {
            throw new ExtractionModeException(
                "INVALID_USER_ID",
                "必须使用平台 UserId（例如 steam_7656119...），不能使用昵称或本周 PlayerUID。",
                StatusCodes.Status400BadRequest);
        }
        return normalized;
    }

    private static void ThrowIfWalletFailed(WalletAdjustmentResult result)
    {
        if (result.ErrorCode is not null)
        {
            throw new InvalidOperationException(
                $"Wallet bootstrap failed: {result.ErrorCode}: {result.ErrorMessage}");
        }
    }

    private static JsonNode? GetProperty(JsonObject value, string name) =>
        value.FirstOrDefault(property =>
            string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? GetString(JsonObject value, string name) =>
        GetProperty(value, name)?.GetValue<string>();

    private static double? GetDouble(JsonObject value, string name)
    {
        if (GetProperty(value, name) is not JsonValue jsonValue)
        {
            return null;
        }
        if (jsonValue.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue;
        }
        if (jsonValue.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }
        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9]{1,15}_[a-z0-9-]{3,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdPattern();

}

public sealed record ExtractionAccountContext(
    ExtractionAccount Account,
    ExtractionSeason Season,
    ExtractionWalletSnapshot Wallet,
    bool Online);

public sealed record ExtractionLivePlayer(
    string UserId,
    string DisplayName,
    bool Online,
    string? PlayerUid,
    double? MapX,
    double? MapY);

public sealed record ExtractionRolloverPreflight(
    DateTimeOffset CheckedAt,
    Guid? CurrentSeasonId,
    string? CurrentSeasonCode,
    string? CurrentSeasonWorldId,
    DateTimeOffset? CurrentSeasonEndsAt,
    string ActualWorldId,
    string TargetSeasonCode,
    DateTimeOffset TargetSeasonStartsAt,
    DateTimeOffset TargetSeasonEndsAt,
    bool CanStartWorldSwitch,
    string Reason);

public sealed class ExtractionModeException : Exception
{
    public ExtractionModeException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}

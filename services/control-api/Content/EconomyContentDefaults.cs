using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ControlApi.Content;

public static class EconomyContentDefaults
{
    public static EconomyContentDefinition Create(
        ExtractionModeOptions options,
        EconomySafetyOptions safety,
        GameResourceCatalog catalog)
    {
        var names = catalog.Items.ToDictionary(item => item.Id, item => item.Name, StringComparer.OrdinalIgnoreCase);
        var zones = options.ExtractionZones.Select(zone => new ContentExchangeZoneDefinition(
            zone.Id,
            zone.DisplayName,
            zone.RouteHint,
            zone.MapX,
            zone.MapY,
            zone.Radius,
            10_000,
            Enum.GetValues<DayOfWeek>().Select(day =>
                new ContentExchangeWindow(day, TimeOnly.MinValue, TimeOnly.MaxValue, 60)).ToArray(),
            true)).ToArray();
        var zoneIds = zones.Select(zone => zone.ZoneId).ToArray();

        var products = new[]
        {
            Product("STARTER-CAPTURE", "新兵捕捉补给", "基础帕鲁球、烤野莓和低级药品，适合新档起步。",
                "捕捉", ["新手", "推荐"], 1, ExtractionCurrency.MarketCoin, 120,
                [("PalSphere", 10), ("Baked_Berries", 20), ("Herbs", 3)], 3),
            Product("FIELD-MEDIC", "野战医疗包", "医疗用品与应急食物。",
                "医疗", ["本周战备"], null, ExtractionCurrency.SeasonVoucher, 60,
                [("Medicines", 3), ("Baked_Berries", 10)], 5),
            Product("MEGA-SPHERE", "高级捕捉包", "十枚高级帕鲁球。",
                "捕捉", ["本周战备"], 3, ExtractionCurrency.SeasonVoucher, 80,
                [("PalSphere_Mega", 10)], 5),
            Product("COARSE-AMMO", "粗制弹药箱", "一百发粗制弹药。",
                "弹药", ["消耗品"], null, ExtractionCurrency.SeasonVoucher, 70,
                [("RoughBullet", 100)], 10),
            Product("STARTER-CROSSBOW", "弩手战备包", "一把弩与一百支箭，每周限购一次。",
                "武器", ["新手", "推荐"], 2, ExtractionCurrency.MarketCoin, 300,
                [("BowGun", 1), ("Arrow", 100)], 1),
            Product("BUILDER-REPAIR", "建设维修箱", "修理套装与水泥，适合据点维护。",
                "建设", ["据点"], null, ExtractionCurrency.MarketCoin, 180,
                [("RepairKit", 10), ("Cement", 50)], 3),
            Product("FARM-SEEDS", "农场种子箱", "野莓与小麦种子补给。",
                "农业", ["据点", "食物"], null, ExtractionCurrency.SeasonVoucher, 50,
                [("BerrySeeds", 20), ("WheatSeeds", 20)], 5),
            Product("GIGA-SPHERE", "优级捕捉包", "十枚优级帕鲁球，全服库存有限。",
                "捕捉", ["进阶", "全服库存"], 4, ExtractionCurrency.SeasonVoucher, 140,
                [("PalSphere_Giga", 10)], 5, 500),
            Product("HANDGUN-AMMO", "手枪弹药箱", "一百发手枪弹药。",
                "弹药", ["消耗品"], null, ExtractionCurrency.SeasonVoucher, 90,
                [("HandgunBullet", 100)], 10),
            Product("RIFLE-AMMO", "步枪弹药箱", "一百发突击步枪弹药。",
                "弹药", ["进阶", "消耗品"], null, ExtractionCurrency.SeasonVoucher, 180,
                [("AssaultRifleBullet", 100)], 5, 300)
        };

        var resources = new (string Id, string Category, long Value)[]
        {
            ("Wood", "基础材料", 1), ("Stone", "基础材料", 1), ("Fiber", "基础材料", 1),
            ("Pal_crystal_S", "矿物", 1), ("Leather", "生物材料", 2), ("Bone", "生物材料", 2),
            ("Cloth", "加工材料", 3), ("CopperOre", "矿物", 2), ("CopperIngot", "加工材料", 5),
            ("Coal", "矿物", 3), ("Sulfur", "矿物", 3), ("Quartz", "矿物", 8),
            ("PalOil", "生物材料", 10), ("Polymer", "加工材料", 15), ("CarbonFiber", "加工材料", 20),
            ("MachineParts2", "加工材料", 25), ("PalCrystal_Ex", "古代材料", 40),
            ("AncientParts2", "古代材料", 100), ("MeteorDrop", "特殊材料", 20),
            ("CrudeOil", "工业材料", 15), ("Diamond", "贵重品", 100), ("Ruby", "贵重品", 60),
            ("Sapphire", "贵重品", 70), ("Eemerald", "贵重品", 80),
            ("Horn", "生物材料", 2), ("Wool", "生物材料", 2), ("Cloth2", "加工材料", 10),
            ("Charcoal", "加工材料", 2), ("MachineParts", "加工材料", 4),
            ("IronIngot", "加工材料", 8), ("StealIngot", "加工材料", 15),
            ("Processed_Wood", "加工材料", 5), ("HighGrade_Processed_Wood", "加工材料", 10),
            ("Wood_Fine", "高级材料", 4), ("Wood_Ancient", "高级材料", 20),
            ("BeastBone_Ancient", "高级材料", 20), ("AncientParts3", "古代材料", 20),
            ("ManganeseOre", "矿物", 15), ("ManganeseIngot", "加工材料", 35),
            ("PalDarkParts", "特殊材料", 20), ("YakushimaIngot001", "加工材料", 50),
            ("RainbowCrystal", "特殊材料", 30), ("Wood_WorldTree", "高级材料", 40),
            ("NightStone", "矿物", 10), ("WorldTreeOre", "矿物", 30),
            ("WorldTreeIngot", "加工材料", 70), ("PredatorCrystal", "特殊材料", 30),
            ("SkyIslandOre", "矿物", 20), ("SkyislandIngot", "加工材料", 50),
            ("Thermal_Core", "特殊材料", 80), ("AIcore", "特殊材料", 150)
        };
        var activeResources = resources
            .Where(resource => names.ContainsKey(resource.Id))
            .Select(resource => new ContentResourceDefinition(
                resource.Id,
                names[resource.Id],
                resource.Category,
                ["白名单", "方案A"],
                ExtractionCurrency.SeasonVoucher,
                resource.Value,
                zoneIds,
                true))
            .ToArray();

        var tasks = new[]
        {
            Task("daily-exchange", "完成一次资源兑换", ContentTaskCadence.Daily,
                ContentTaskEventKind.ResourceExchangeSettled, 1, null, null, 10, 5),
            Task("daily-leather", "出售 25 个皮革", ContentTaskCadence.Daily,
                ContentTaskEventKind.ResourceItemSettled, 25, "Leather", null, 15, 5),
            Task("daily-spend", "在战备商城消费 100", ContentTaskCadence.Daily,
                ContentTaskEventKind.CurrencySpent, 100, null, ExtractionCurrency.SeasonVoucher, 15, 5),
            Task("weekly-value", "本周兑换价值达到 2000", ContentTaskCadence.Weekly,
                ContentTaskEventKind.ResourceValueSettled, 2_000, null, null, 80, 30),
            Task("weekly-orders", "本周完成 3 个商城订单", ContentTaskCadence.Weekly,
                ContentTaskEventKind.ShopOrderDelivered, 3, null, null, 60, 20),
            Task("weekly-coal", "本周出售 100 个石炭", ContentTaskCadence.Weekly,
                ContentTaskEventKind.ResourceItemSettled, 100, "Coal", null, 60, 20)
        };

        var activeTasks = tasks
            .Where(task => task.TargetItemId is null || names.ContainsKey(task.TargetItemId))
            .ToArray();
        var dailyTaskPool = activeTasks
            .Where(task => task.Cadence == ContentTaskCadence.Daily)
            .Select(task => task.TaskKey)
            .ToArray();
        var weeklyTaskPool = activeTasks
            .Where(task => task.Cadence == ContentTaskCadence.Weekly)
            .Select(task => task.TaskKey)
            .ToArray();

        return new EconomyContentDefinition(
            1,
            options.ServerId,
            "方案 A 周世界资源经济",
            new EconomyContentDependencies(
                EconomyContentRuntimeService.SupportedRulesVersion,
                catalog.Revision,
                safety.ApprovedGameVersion,
                safety.ApprovedPalDefenderVersion),
            options.TimeZoneId,
            options.DailyRefreshHour,
            products.Where(product => product.ItemGrants.All(grant => names.ContainsKey(grant.ItemId))).ToArray(),
            activeResources,
            zones,
            activeTasks,
            new ContentRotationPolicy(
                EconomyContentRuntimeService.SupportedRulesVersion,
                1,
                "scheme-a-rotation-v1",
                dailyTaskPool,
                dailyTaskPool.Length,
                weeklyTaskPool,
                weeklyTaskPool.Length,
                zoneIds,
                Math.Min(1, zoneIds.Length)));
    }

    private static ContentProductDefinition Product(
        string sku,
        string name,
        string description,
        string category,
        IReadOnlyList<string> tags,
        int? featuredRank,
        ExtractionCurrency currency,
        long price,
        IReadOnlyList<(string ItemId, int Quantity)> grants,
        int? personalLimit,
        long? globalStock = null) => new(
        sku,
        name,
        description,
        category,
        tags,
        featuredRank,
        currency,
        price,
        grants.Select(grant => new ContentItemGrant(grant.ItemId, grant.Quantity)).ToArray(),
        personalLimit,
        globalStock,
        true,
        null,
        null);

    private static ContentTaskDefinition Task(
        string key,
        string name,
        ContentTaskCadence cadence,
        ContentTaskEventKind kind,
        long target,
        string? itemId,
        ExtractionCurrency? targetCurrency,
        long reward,
        int points) => new(
        key,
        name,
        $"仅按服务端可复核经济事件累计：{name}。",
        cadence,
        kind,
        target,
        itemId,
        targetCurrency,
        [],
        new ContentTaskReward(ExtractionCurrency.MarketCoin, reward, points),
        true);
}

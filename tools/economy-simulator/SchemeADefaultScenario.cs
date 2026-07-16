using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;

namespace PalControl.EconomySimulator;

public static class SchemeADefaultScenario
{
    public static Guid ContentVersionId { get; } = Guid.Parse("7a2e9f22-6d67-4fb3-bdf7-d6e55a2a2026");

    public static EconomyContentDefinition Create()
    {
        var zones = new[]
        {
            new ContentExchangeZoneDefinition(
                "harbor", "Harbor Exchange", "South harbor", 100, 200, 250, 10_000,
                [new ContentExchangeWindow(DayOfWeek.Monday, TimeOnly.MinValue, TimeOnly.MaxValue, 60)], true),
            new ContentExchangeZoneDefinition(
                "ridge", "Ridge Exchange", "North ridge", 500, 600, 180, 12_500,
                [new ContentExchangeWindow(DayOfWeek.Monday, TimeOnly.MinValue, TimeOnly.MaxValue, 60)], true)
        };
        var products = new[]
        {
            Product("FIELD-MEDIC", ExtractionCurrency.SeasonVoucher, 60, "Medicines", 3, 5),
            Product("MEGA-SPHERE", ExtractionCurrency.SeasonVoucher, 80, "PalSphere_Mega", 10, 5),
            Product("COARSE-AMMO", ExtractionCurrency.SeasonVoucher, 70, "RoughBullet", 100, 10),
            Product("GIGA-SPHERE", ExtractionCurrency.SeasonVoucher, 140, "PalSphere_Giga", 10, 5, 500),
            Product("RIFLE-AMMO", ExtractionCurrency.SeasonVoucher, 180, "AssaultRifleBullet", 100, 5, 300),
            Product("STARTER-CAPTURE", ExtractionCurrency.MarketCoin, 120, "PalSphere", 10, 3),
            Product("STARTER-CROSSBOW", ExtractionCurrency.MarketCoin, 300, "BowGun", 1, 1)
        };
        var resources = new[]
        {
            Resource("Wood", 1),
            Resource("Stone", 1),
            Resource("Leather", 2),
            Resource("CopperIngot", 5),
            Resource("Quartz", 8),
            Resource("Polymer", 15),
            Resource("CrudeOil", 15),
            Resource("Diamond", 100)
        };
        var tasks = new[]
        {
            Task("daily-exchange", ContentTaskCadence.Daily, 10),
            Task("daily-spend", ContentTaskCadence.Daily, 15),
            Task("daily-resource", ContentTaskCadence.Daily, 15),
            Task("weekly-value", ContentTaskCadence.Weekly, 80),
            Task("weekly-orders", ContentTaskCadence.Weekly, 60),
            Task("weekly-resource", ContentTaskCadence.Weekly, 60)
        };
        var rotation = new ContentRotationPolicy(
            "scheme-a-v1", 1, "scheme-a-simulation-v1",
            tasks.Where(task => task.Cadence == ContentTaskCadence.Daily).Select(task => task.TaskKey).ToArray(), 3,
            tasks.Where(task => task.Cadence == ContentTaskCadence.Weekly).Select(task => task.TaskKey).ToArray(), 3,
            ["harbor", "ridge"], 1);
        var definition = new EconomyContentDefinition(
            1,
            "scheme-a-simulation",
            "Scheme A reproducible balance scenario",
            new EconomyContentDependencies("scheme-a-v1", "simulated-catalog-v1", "simulated-game", "simulated-paldefender"),
            "Asia/Shanghai",
            4,
            products,
            resources,
            zones,
            tasks,
            rotation);
        return definition with
        {
            BalancePolicy = EconomyContentDefaults.CreateBalancePolicy(
                definition.Dependencies.ResourceCatalogRevision,
                products,
                resources,
                zones,
                rotation),
            DynamicEconomyPolicy = EconomyContentDefaults.CreateDynamicEconomyPolicy(
                zones.Select(zone => zone.ZoneId).ToArray())
        };
    }

    private static ContentProductDefinition Product(
        string sku,
        ExtractionCurrency currency,
        long price,
        string itemId,
        int quantity,
        int limit,
        long? globalStock = null) => new(
        sku, sku, $"Simulation offer {sku}", "simulation", ["scheme-a"], null,
        currency, price, [new ContentItemGrant(itemId, quantity)], limit, globalStock, true, null, null);

    private static ContentResourceDefinition Resource(string itemId, long value) => new(
        itemId, itemId, "simulation", ["scheme-a"], ExtractionCurrency.SeasonVoucher,
        value, ["harbor", "ridge"], true);

    private static ContentTaskDefinition Task(string key, ContentTaskCadence cadence, long reward) => new(
        key, key, $"Simulation task {key}", cadence, ContentTaskEventKind.ResourceExchangeSettled,
        1, null, null, [], new ContentTaskReward(ExtractionCurrency.MarketCoin, reward, 5), true);
}

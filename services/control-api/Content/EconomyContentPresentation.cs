namespace PalControl.ControlApi.Content;

public sealed record ResolvedContentPresentation(
    string IconKey,
    ContentRarity Rarity,
    string Usage,
    string PresentationSource);

/// <summary>
/// Keeps presentation data local and finite. Icon keys are identifiers for
/// code-native SVGs in trusted clients; they are never treated as URLs/paths.
/// </summary>
public static class EconomyContentPresentation
{
    public const int MaximumIconKeyLength = 32;
    public const int MaximumUsageLength = 160;
    public const string ContentSource = "content";
    public const string LegacyFallbackSource = "legacy-fallback";

    public static readonly IReadOnlySet<string> AllowedIconKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "supply", "capture", "medical", "ammo", "weapon", "building", "farming",
            "material-basic", "mineral", "biological", "processed", "ancient",
            "industrial", "valuable", "special", "advanced"
        };

    public static bool IsSafeIconKey(string? value) =>
        value is { Length: > 0 and <= MaximumIconKeyLength } &&
        AllowedIconKeys.Contains(value) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    public static bool IsSafeUsage(string? value)
    {
        if (value is not { Length: > 0 and <= MaximumUsageLength } ||
            string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            value.IndexOfAny(['<', '>', '\\']) >= 0)
        {
            return false;
        }
        var lowered = value.ToLowerInvariant();
        return !lowered.Contains("://", StringComparison.Ordinal) &&
               !lowered.Contains("javascript:", StringComparison.Ordinal) &&
               !lowered.Contains("data:", StringComparison.Ordinal) &&
               !lowered.Contains("file:", StringComparison.Ordinal) &&
               !lowered.Contains("../", StringComparison.Ordinal) &&
               !lowered.Contains("..\\", StringComparison.Ordinal);
    }

    public static ResolvedContentPresentation ResolveProduct(
        string category,
        string? iconKey,
        ContentRarity? rarity,
        string? usage) => Resolve(
            iconKey,
            rarity,
            usage,
            ProductIcon(category),
            ContentRarity.Uncommon,
            "用于本周世界的日常战备与补给。请在购买前核对发放清单。");

    public static ResolvedContentPresentation ResolveResource(
        string category,
        string? iconKey,
        ContentRarity? rarity,
        string? usage) => Resolve(
            iconKey,
            rarity,
            usage,
            ResourceIcon(category),
            ContentRarity.Common,
            ResourceUsage(category));

    public static ResolvedContentPresentation DefaultProduct(
        string sku,
        string category,
        string description)
    {
        var rarity = sku switch
        {
            "GIGA-SPHERE" or "RIFLE-AMMO" => ContentRarity.Epic,
            "MEGA-SPHERE" or "STARTER-CROSSBOW" or "HANDGUN-AMMO" => ContentRarity.Rare,
            "BUILDER-REPAIR" or "FIELD-MEDIC" => ContentRarity.Uncommon,
            _ => ContentRarity.Common
        };
        return new ResolvedContentPresentation(
            ProductIcon(category),
            rarity,
            description,
            ContentSource);
    }

    public static ResolvedContentPresentation DefaultResource(string category, long unitValue)
    {
        var rarity = unitValue switch
        {
            >= 100 => ContentRarity.Legendary,
            >= 50 => ContentRarity.Epic,
            >= 15 => ContentRarity.Rare,
            >= 3 => ContentRarity.Uncommon,
            _ => ContentRarity.Common
        };
        return new ResolvedContentPresentation(
            ResourceIcon(category),
            rarity,
            ResourceUsage(category),
            ContentSource);
    }

    private static ResolvedContentPresentation Resolve(
        string? iconKey,
        ContentRarity? rarity,
        string? usage,
        string fallbackIcon,
        ContentRarity fallbackRarity,
        string fallbackUsage) =>
        IsSafeIconKey(iconKey) && rarity is not null && Enum.IsDefined(rarity.Value) && IsSafeUsage(usage)
            ? new ResolvedContentPresentation(iconKey!, rarity.Value, usage!, ContentSource)
            : new ResolvedContentPresentation(
                fallbackIcon,
                fallbackRarity,
                fallbackUsage,
                LegacyFallbackSource);

    private static string ProductIcon(string category) => category switch
    {
        "捕捉" => "capture",
        "医疗" => "medical",
        "弹药" => "ammo",
        "武器" => "weapon",
        "建设" => "building",
        "农业" => "farming",
        _ => "supply"
    };

    private static string ResourceIcon(string category) => category switch
    {
        "基础材料" => "material-basic",
        "矿物" => "mineral",
        "生物材料" => "biological",
        "加工材料" => "processed",
        "古代材料" => "ancient",
        "工业材料" => "industrial",
        "贵重品" => "valuable",
        "特殊材料" => "special",
        "高级材料" => "advanced",
        _ => "material-basic"
    };

    private static string ResourceUsage(string category) => category switch
    {
        "基础材料" => "用于据点建设、修理与常规制作，也是周世界早期的基础储备。",
        "矿物" => "用于冶炼、科技制造与中后期设施建设，出售前建议保留生产需求。",
        "生物材料" => "来自生物相关产出，常用于装备、药品或其他加工配方。",
        "加工材料" => "由基础资源进一步加工，常用于装备、设施与高级部件制造。",
        "古代材料" => "用于古代科技和稀有制造，获取成本较高，出售前请谨慎核对。",
        "工业材料" => "用于自动化、能源或工业生产链，是中后期据点的重要储备。",
        "贵重品" => "主要用于高价值交易与周经济兑换，通常不属于基础生产消耗。",
        "特殊材料" => "用于特殊科技、事件或高阶制造，来源有限，建议按计划保留。",
        "高级材料" => "用于高阶建筑、装备或终局制造，出售前请核对本周建设计划。",
        _ => "本周世界白名单资源；可在开放兑换点出售，具体用途以游戏当前版本为准。"
    };
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Content;

[JsonConverter(typeof(JsonStringEnumConverter<ContentDraftState>))]
public enum ContentDraftState
{
    Draft,
    Published,
    Abandoned
}

[JsonConverter(typeof(JsonStringEnumConverter<ContentTaskCadence>))]
public enum ContentTaskCadence
{
    Daily,
    Weekly
}

/// <summary>
/// Only server-observed economy facts are available to the first task system.
/// Client-reported kills, gathering, deaths, PvP and hotspot entry are
/// deliberately absent until an authoritative adapter is implemented.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContentTaskEventKind>))]
public enum ContentTaskEventKind
{
    ResourceExchangeSettled,
    ResourceItemSettled,
    ResourceValueSettled,
    ShopOrderDelivered,
    CurrencySpent
}

public sealed record EconomyContentDependencies(
    string RulesVersion,
    string ResourceCatalogRevision,
    string GameVersion,
    string PalDefenderVersion);

public sealed record ContentItemGrant(string ItemId, int Quantity);

[JsonConverter(typeof(JsonStringEnumConverter<ContentRarity>))]
public enum ContentRarity
{
    Common,
    Uncommon,
    Rare,
    Epic,
    Legendary
}

public sealed record ContentProductDefinition(
    string Sku,
    string DisplayName,
    string Description,
    string Category,
    IReadOnlyList<string> Tags,
    int? FeaturedRank,
    ExtractionCurrency PriceCurrency,
    long UnitPrice,
    IReadOnlyList<ContentItemGrant> ItemGrants,
    int? PurchaseLimitPerSeason,
    long? GlobalStock,
    bool Active,
    DateTimeOffset? AvailableFrom,
    DateTimeOffset? AvailableUntil,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IconKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ContentRarity? Rarity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Usage = null);

/// <summary>
/// Presence in this collection is the sell allow-list. Items outside it are
/// not assigned an implicit fallback value.
/// </summary>
public sealed record ContentResourceDefinition(
    string ItemId,
    string DisplayName,
    string Category,
    IReadOnlyList<string> Tags,
    ExtractionCurrency Currency,
    long UnitValue,
    IReadOnlyList<string> ExchangeZoneIds,
    bool Active,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IconKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ContentRarity? Rarity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Usage = null);

public sealed record ContentExchangeWindow(
    DayOfWeek DayOfWeek,
    TimeOnly OpensAt,
    TimeOnly ClosesAt,
    int GraceSeconds);

public sealed record ContentExchangeZoneDefinition(
    string ZoneId,
    string DisplayName,
    string RouteHint,
    double MapX,
    double MapY,
    double Radius,
    int YieldMultiplierBasisPoints,
    IReadOnlyList<ContentExchangeWindow> OpenWindows,
    bool Active,
    string RiskHint = "");

public sealed record ContentTaskReward(
    ExtractionCurrency Currency,
    long Amount,
    int RankingPoints);

public sealed record ContentTaskDefinition(
    string TaskKey,
    string DisplayName,
    string Description,
    ContentTaskCadence Cadence,
    ContentTaskEventKind EventKind,
    long TargetAmount,
    string? TargetItemId,
    ExtractionCurrency? TargetCurrency,
    IReadOnlyList<string> ExchangeZoneIds,
    ContentTaskReward Reward,
    bool Active);

/// <summary>
/// Defines how a deterministic daily/weekly rotation is derived. A published
/// version supplies the business date and content hash; clients never supply
/// the seed or selected task/zone set.
/// </summary>
public sealed record ContentRotationPolicy(
    string RulesVersion,
    int AlgorithmVersion,
    string SeedNamespace,
    IReadOnlyList<string> DailyTaskPool,
    int DailyTaskCount,
    IReadOnlyList<string> WeeklyTaskPool,
    int WeeklyTaskCount,
    IReadOnlyList<string> HotspotZonePool,
    int DailyHotspotCount,
    int HotspotYieldMultiplierBasisPoints = 12_000);

public sealed record EconomyContentDefinition(
    int SchemaVersion,
    string ServerId,
    string DisplayName,
    EconomyContentDependencies Dependencies,
    string TimeZoneId,
    int DailyRefreshHour,
    IReadOnlyList<ContentProductDefinition> Products,
    IReadOnlyList<ContentResourceDefinition> Resources,
    IReadOnlyList<ContentExchangeZoneDefinition> ExchangeZones,
    IReadOnlyList<ContentTaskDefinition> Tasks,
    ContentRotationPolicy Rotation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ContentEconomyBalancePolicy? BalancePolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ContentDynamicEconomyPolicy? DynamicEconomyPolicy = null);

public sealed record EconomyContentValidationContext(
    IReadOnlySet<string> KnownItemIds,
    IReadOnlySet<string> SupportedRulesVersions,
    string? ExpectedResourceCatalogRevision = null,
    string? ExpectedGameVersion = null,
    string? ExpectedPalDefenderVersion = null);

public sealed record ContentValidationIssue(
    string Code,
    string Path,
    string Message);

public sealed record ContentValidationResult(
    bool Valid,
    string ContentHash,
    IReadOnlyList<ContentValidationIssue> Errors,
    IReadOnlyList<ContentValidationIssue> Warnings,
    DateTimeOffset ValidatedAt);

public sealed record EconomyContentDraft(
    Guid DraftId,
    string ServerId,
    string Name,
    ContentDraftState State,
    Guid? BasedOnVersionId,
    long Revision,
    string ContentHash,
    EconomyContentDefinition Definition,
    ContentValidationResult? LastValidation,
    Guid? PublishedVersionId,
    string CreatedBy,
    string UpdatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EconomyContentVersion(
    Guid VersionId,
    string ServerId,
    long VersionNumber,
    DateOnly BusinessDate,
    string RulesVersion,
    string ContentHash,
    EconomyContentDefinition Definition,
    Guid SourceDraftId,
    string PublishedBy,
    DateTimeOffset PublishedAt);

public sealed record EconomyContentPointer(
    string ServerId,
    Guid VersionId,
    long VersionNumber,
    DateOnly BusinessDate,
    string RulesVersion,
    string ContentHash,
    DateTimeOffset UpdatedAt);

public sealed record ContentPublishResult(
    EconomyContentVersion Version,
    EconomyContentPointer Pointer,
    bool VersionCreated,
    bool PointerChanged,
    bool Replayed);

/// <summary>
/// An immutable content version that is durable but not yet player-visible.
/// The current pointer is changed only when the complete commerce projection
/// is committed in the extraction repository transaction.
/// </summary>
public sealed record PreparedContentPublish(
    EconomyContentVersion Version,
    Guid? ExpectedCurrentVersionId,
    bool VersionCreated,
    bool Replayed);

public sealed record ContentRollbackResult(
    EconomyContentPointer Pointer,
    Guid PreviousVersionId,
    bool PointerChanged,
    bool Replayed);

/// <summary>
/// A validated rollback target that is not visible until its complete product
/// projection and the pointer compare-and-swap commit together.
/// </summary>
public sealed record PreparedContentRollback(
    EconomyContentVersion Version,
    Guid ExpectedCurrentVersionId,
    bool Replayed);

[JsonConverter(typeof(JsonStringEnumConverter<ContentDiffKind>))]
public enum ContentDiffKind
{
    Added,
    Removed,
    Changed
}

public sealed record ContentDiffEntry(
    string Path,
    ContentDiffKind Kind,
    string? Before,
    string? After);

public sealed record EconomyContentRotationIdentity(
    string ServerId,
    DateOnly BusinessDate,
    string RulesVersion,
    int AlgorithmVersion,
    string Seed,
    string ContentHash,
    Guid CurrentContentVersionId);

public static class EconomyContentRotation
{
    public static EconomyContentRotationIdentity Create(EconomyContentVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var source = string.Join('|',
            version.ServerId,
            version.BusinessDate.ToString("yyyy-MM-dd", null),
            version.Definition.Rotation.RulesVersion,
            version.Definition.Rotation.AlgorithmVersion,
            version.Definition.Rotation.SeedNamespace,
            version.ContentHash);
        var seed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();
        return new EconomyContentRotationIdentity(
            version.ServerId,
            version.BusinessDate,
            version.RulesVersion,
            version.Definition.Rotation.AlgorithmVersion,
            seed,
            version.ContentHash,
            version.VersionId);
    }
}

public class ContentStoreException : Exception
{
    public ContentStoreException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class ContentValidationException : ContentStoreException
{
    public ContentValidationException(ContentValidationResult validation)
        : base("CONTENT_VALIDATION_FAILED", "The content draft failed validation and was not published.")
    {
        Validation = validation;
    }

    public ContentValidationResult Validation { get; }
}

public enum ContentPublishFaultPoint
{
    AfterVersionInserted,
    AfterPointerUpdated
}

public interface IContentPublishFaultInjector
{
    void ThrowIfRequested(ContentPublishFaultPoint point);
}

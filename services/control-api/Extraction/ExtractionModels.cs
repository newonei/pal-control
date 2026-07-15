using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Extraction;

[JsonConverter(typeof(JsonStringEnumConverter<ExtractionCurrency>))]
public enum ExtractionCurrency
{
    MarketCoin,
    SeasonVoucher
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtractionSeasonState>))]
public enum ExtractionSeasonState
{
    Draft,
    Scheduled,
    Active,
    Closed,
    Archived
}

[JsonConverter(typeof(JsonStringEnumConverter<ShopOrderState>))]
public enum ShopOrderState
{
    PendingDelivery,
    Dispatching,
    Delivered,
    DeliveryFailed,
    DeliveryUncertain,
    Refunded
}

[JsonConverter(typeof(JsonStringEnumConverter<ShopDeliveryState>))]
public enum ShopDeliveryState
{
    Pending,
    Dispatching,
    Delivered,
    Failed,
    Uncertain
}

public sealed record ExtractionSeason(
    Guid SeasonId,
    string ServerId,
    string Code,
    string DisplayName,
    string? WorldId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    ExtractionSeasonState State,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ExtractionSeasonDefinition(
    string ServerId,
    string Code,
    string DisplayName,
    string? WorldId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    ExtractionSeasonState State);

public sealed record ExtractionAccount(
    Guid AccountId,
    string IdentityProvider,
    string ExternalUserId,
    string DisplayName,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A server-observed mapping between a cross-world platform subject and the
/// player's complete identity in one weekly world. Display names are
/// intentionally not part of the authorization identity.
/// </summary>
public sealed record PlayerIdentityBinding(
    Guid BindingId,
    string PlatformSubject,
    Guid SeasonId,
    string WorldId,
    string PlayerUid,
    Guid AccountId,
    DateTimeOffset FirstBoundAt,
    DateTimeOffset LastVerifiedAt);

public sealed record PlayerIdentityBindingRequest(
    string PlatformSubject,
    Guid SeasonId,
    string WorldId,
    string PlayerUid,
    Guid AccountId);

public sealed record PlayerIdentityBindingResult(
    PlayerIdentityBinding? Binding,
    bool Created,
    bool Verified,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record PlayerIdentityBindingHistoryEntry(
    Guid HistoryId,
    Guid BindingId,
    string PlatformSubject,
    Guid SeasonId,
    string WorldId,
    string PlayerUid,
    Guid AccountId,
    string Action,
    DateTimeOffset OccurredAt);

public sealed record WalletBalance(
    Guid AccountId,
    ExtractionCurrency Currency,
    Guid? SeasonId,
    long Balance,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed record ExtractionWalletSnapshot(
    Guid AccountId,
    Guid SeasonId,
    WalletBalance MarketCoin,
    WalletBalance SeasonVoucher);

public sealed record WalletLedgerEntry(
    Guid EntryId,
    Guid AccountId,
    ExtractionCurrency Currency,
    Guid? SeasonId,
    long Delta,
    long BalanceAfter,
    string Reason,
    string ReferenceType,
    string ReferenceId,
    string Actor,
    DateTimeOffset CreatedAt);

public sealed record WalletAdjustmentRequest(
    Guid AccountId,
    Guid? SeasonId,
    ExtractionCurrency Currency,
    long Delta,
    string Reason,
    string ReferenceType,
    string ReferenceId,
    string Actor,
    string IdempotencyKey);

public sealed record WalletAdjustmentResult(
    WalletBalance? Balance,
    WalletLedgerEntry? LedgerEntry,
    bool Created,
    bool IdempotencyConflict,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record ShopItemGrant(string ItemId, int Quantity);

public sealed record ShopProduct(
    Guid ProductId,
    string Sku,
    string DisplayName,
    string Description,
    ExtractionCurrency PriceCurrency,
    long UnitPrice,
    IReadOnlyList<ShopItemGrant> ItemGrants,
    int? PurchaseLimitPerSeason,
    bool Active,
    DateTimeOffset? AvailableFrom,
    DateTimeOffset? AvailableUntil,
    long Revision,
    string UpdatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ShopProductDefinition(
    string Sku,
    string DisplayName,
    string Description,
    ExtractionCurrency PriceCurrency,
    long UnitPrice,
    IReadOnlyList<ShopItemGrant> ItemGrants,
    int? PurchaseLimitPerSeason,
    bool Active,
    DateTimeOffset? AvailableFrom,
    DateTimeOffset? AvailableUntil);

public sealed record ShopPurchaseLineInput(string Sku, int Quantity);

public sealed record ShopPurchaseRequest(
    Guid AccountId,
    Guid SeasonId,
    string ServerId,
    string PlayerIdentifier,
    IReadOnlyList<ShopPurchaseLineInput> Lines,
    string IdempotencyKey,
    string Actor,
    string Reason,
    string? PlayerUid = null,
    string? WorldId = null);

public sealed record ShopOrderCharge(ExtractionCurrency Currency, long Amount);

public sealed record ShopOrderLine(
    Guid OrderLineId,
    Guid ProductId,
    string Sku,
    string DisplayName,
    int Quantity,
    ExtractionCurrency PriceCurrency,
    long UnitPrice,
    long LineTotal,
    IReadOnlyList<ShopItemGrant> ItemGrants);

public sealed record ShopOrder(
    Guid OrderId,
    Guid AccountId,
    Guid SeasonId,
    string ServerId,
    string PlayerIdentifier,
    IReadOnlyList<ShopOrderLine> Lines,
    IReadOnlyList<ShopOrderCharge> Charges,
    ShopOrderState State,
    Guid DeliveryId,
    int DeliveryAttempt,
    string PurchaseIdempotencyKey,
    string Actor,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PlayerUid = null,
    string? WorldId = null);

public sealed record ShopDelivery(
    Guid DeliveryId,
    Guid OrderId,
    int Attempt,
    ShopDeliveryState State,
    string IdempotencyKey,
    Guid? CommandId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? CompletedAt);

public sealed record ShopDeliveryWorkItem(
    Guid DeliveryId,
    Guid OrderId,
    string ServerId,
    string PlayerIdentifier,
    string UpstreamPath,
    string IdempotencyKey,
    IReadOnlyList<ShopItemGrant> Items,
    int Attempt,
    string? PlayerUid = null,
    string? WorldId = null);

public sealed record ShopPurchaseResult(
    ShopOrder? Order,
    ShopDeliveryWorkItem? Delivery,
    bool Created,
    bool IdempotencyConflict,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record ShopDeliveryUpdateResult(
    ShopOrder? Order,
    ShopDelivery? Delivery,
    bool Updated,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record ShopRefundResult(
    ShopOrder? Order,
    IReadOnlyList<WalletLedgerEntry> LedgerEntries,
    bool Created,
    bool IdempotencyConflict,
    string? ErrorCode,
    string? ErrorMessage);

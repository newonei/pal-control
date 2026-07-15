namespace PalControl.ControlApi.Extraction;

public interface IPlayerIdentityBindingStore
{
    Task<PlayerIdentityBinding?> FindActivePlayerIdentityBindingAsync(
        string serverId,
        string playerIdentifier,
        CancellationToken cancellationToken);
}

public interface IExtractionRepository : IPlayerIdentityBindingStore
{
    bool IsReady { get; }

    /// <summary>
    /// Performs a rolled-back write against the authoritative store. A cached
    /// startup-ready flag is not sufficient for a money-moving admission
    /// decision because disk permissions, capacity, or SQLite locking can
    /// drift while the read projection remains available.
    /// </summary>
    Task ProbeWriteAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ExtractionSeason>> ListSeasonsAsync(
        string? serverId,
        CancellationToken cancellationToken);

    Task<ExtractionSeason?> GetSeasonAsync(
        Guid seasonId,
        CancellationToken cancellationToken);

    Task<ExtractionSeason> UpsertSeasonAsync(
        Guid? seasonId,
        ExtractionSeasonDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    Task<ExtractionAccount> GetOrCreateAccountAsync(
        string identityProvider,
        string externalUserId,
        string displayName,
        CancellationToken cancellationToken);

    Task<ExtractionAccount?> GetAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken);

    Task<ExtractionAccount?> FindAccountAsync(
        string identityProvider,
        string externalUserId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExtractionAccount>> ListAccountsAsync(
        CancellationToken cancellationToken);

    Task<PlayerIdentityBindingResult> BindOrVerifyPlayerIdentityAsync(
        PlayerIdentityBindingRequest request,
        CancellationToken cancellationToken);

    Task<PlayerIdentityBinding?> GetPlayerIdentityBindingAsync(
        Guid accountId,
        Guid seasonId,
        string worldId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PlayerIdentityBindingHistoryEntry>> ListPlayerIdentityBindingHistoryAsync(
        Guid? accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken);

    Task<ExtractionWalletSnapshot> GetWalletAsync(
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken);

    Task<WalletAdjustmentResult> AdjustWalletAsync(
        WalletAdjustmentRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WalletLedgerEntry>> GetLedgerAsync(
        Guid accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken);

    Task<WalletLedgerEntry?> FindLedgerEntryByReferenceAsync(
        Guid accountId,
        ExtractionCurrency currency,
        Guid? seasonId,
        string referenceType,
        string referenceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShopProduct>> ListProductsAsync(
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<ShopProduct?> GetProductAsync(
        string sku,
        CancellationToken cancellationToken);

    Task<ShopProduct> UpsertProductAsync(
        ShopProductDefinition definition,
        long? expectedRevision,
        string actor,
        CancellationToken cancellationToken);

    Task<ContentProductProjectionActivationResult> ActivateContentProductProjectionAsync(
        ContentProductProjectionActivation activation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the quantity currently occupying a product's server-wide stock
    /// for one weekly season. Refunded orders do not occupy stock.
    /// </summary>
    Task<long> GetGlobalPurchasedQuantityAsync(
        Guid seasonId,
        string sku,
        CancellationToken cancellationToken);

    Task<ShopPurchaseResult> PurchaseAsync(
        ShopPurchaseRequest request,
        CancellationToken cancellationToken);

    Task<ShopOrder?> GetOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShopOrder>> ListOrdersAsync(
        Guid? accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShopOrder>> ListBlockingOrdersAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShopDeliveryWorkItem>> ListPendingDeliveriesAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShopDeliveryWorkItem>> ListAllPendingDeliveriesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShopDelivery>> ListInFlightDeliveriesAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<ShopDeliveryUpdateResult> MarkDeliveryDispatchedAsync(
        Guid deliveryId,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<ShopDeliveryUpdateResult> MarkDeliveryOutcomeAsync(
        Guid deliveryId,
        ShopDeliveryState state,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken);

    Task<ShopDeliveryUpdateResult> PrepareDeliveryRetryAsync(
        Guid orderId,
        string actor,
        string reason,
        CancellationToken cancellationToken);

    Task<ShopRefundResult> RefundFailedOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken);

    Task<ShopRefundResult> RefundUncertainOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken);

    Task<ShopDeliveryUpdateResult> MarkUncertainOrderDeliveredAsync(
        Guid orderId,
        string actor,
        string reason,
        CancellationToken cancellationToken);
}

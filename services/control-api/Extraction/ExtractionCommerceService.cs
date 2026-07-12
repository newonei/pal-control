namespace PalControl.ControlApi.Extraction;

public sealed class ExtractionPersistenceOptions
{
    public string DataDirectory { get; init; } = "data/extraction";
}

public sealed record ShopDeliveryCommandAcceptance(
    Guid? CommandId,
    bool Accepted,
    bool IdempotencyConflict,
    string? ErrorCode,
    string? ErrorMessage);

public interface IShopDeliveryCommandDispatcher
{
    Task<ShopDeliveryCommandAcceptance> EnqueueAsync(
        ShopDeliveryWorkItem delivery,
        string reason,
        string actor,
        CancellationToken cancellationToken);
}

public sealed class ExtractionCommerceService
{
    private readonly IExtractionRepository _repository;

    public ExtractionCommerceService(IExtractionRepository repository)
    {
        _repository = repository;
    }

    public bool IsReady => _repository.IsReady;

    public async Task<ExtractionSeason?> GetActiveSeasonAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var seasons = await _repository.ListSeasonsAsync(serverId, cancellationToken);
        return seasons.SingleOrDefault(season => season.State == ExtractionSeasonState.Active);
    }

    public Task<IReadOnlyList<ExtractionSeason>> ListSeasonsAsync(
        string? serverId,
        CancellationToken cancellationToken) =>
        _repository.ListSeasonsAsync(serverId, cancellationToken);

    public Task<ExtractionSeason> UpsertSeasonAsync(
        Guid? seasonId,
        ExtractionSeasonDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        _repository.UpsertSeasonAsync(seasonId, definition, expectedRevision, cancellationToken);

    public Task<ExtractionAccount> RegisterPlayerAsync(
        string identityProvider,
        string externalUserId,
        string displayName,
        CancellationToken cancellationToken) =>
        _repository.GetOrCreateAccountAsync(
            identityProvider,
            externalUserId,
            displayName,
            cancellationToken);

    public Task<ExtractionAccount?> FindPlayerAsync(
        string identityProvider,
        string externalUserId,
        CancellationToken cancellationToken) =>
        _repository.FindAccountAsync(identityProvider, externalUserId, cancellationToken);

    public Task<ExtractionWalletSnapshot> GetWalletAsync(
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken) =>
        _repository.GetWalletAsync(accountId, seasonId, cancellationToken);

    public Task<WalletAdjustmentResult> AdjustWalletAsync(
        WalletAdjustmentRequest request,
        CancellationToken cancellationToken) =>
        _repository.AdjustWalletAsync(request, cancellationToken);

    public Task<WalletAdjustmentResult> GrantMarketCoinAsync(
        Guid accountId,
        long amount,
        string referenceType,
        string referenceId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        _repository.AdjustWalletAsync(
            new WalletAdjustmentRequest(
                accountId,
                null,
                ExtractionCurrency.MarketCoin,
                amount,
                reason,
                referenceType,
                referenceId,
                actor,
                idempotencyKey),
            cancellationToken);

    public Task<WalletAdjustmentResult> GrantSeasonVoucherAsync(
        Guid accountId,
        Guid seasonId,
        long amount,
        string referenceType,
        string referenceId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        _repository.AdjustWalletAsync(
            new WalletAdjustmentRequest(
                accountId,
                seasonId,
                ExtractionCurrency.SeasonVoucher,
                amount,
                reason,
                referenceType,
                referenceId,
                actor,
                idempotencyKey),
            cancellationToken);

    public Task<IReadOnlyList<WalletLedgerEntry>> GetLedgerAsync(
        Guid accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken) =>
        _repository.GetLedgerAsync(accountId, seasonId, limit, cancellationToken);

    public Task<IReadOnlyList<ShopProduct>> ListProductsAsync(
        bool includeInactive,
        CancellationToken cancellationToken) =>
        _repository.ListProductsAsync(includeInactive, cancellationToken);

    public Task<ShopProduct> UpsertProductAsync(
        ShopProductDefinition definition,
        long? expectedRevision,
        string actor,
        CancellationToken cancellationToken) =>
        _repository.UpsertProductAsync(definition, expectedRevision, actor, cancellationToken);

    public Task<ShopPurchaseResult> PurchaseAsync(
        ShopPurchaseRequest request,
        CancellationToken cancellationToken) =>
        _repository.PurchaseAsync(request, cancellationToken);

    public Task<ShopOrder?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        _repository.GetOrderAsync(orderId, cancellationToken);

    public Task<IReadOnlyList<ShopOrder>> ListOrdersAsync(
        Guid? accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken) =>
        _repository.ListOrdersAsync(accountId, seasonId, limit, cancellationToken);

    public Task<IReadOnlyList<ShopOrder>> ListBlockingOrdersAsync(
        CancellationToken cancellationToken) =>
        _repository.ListBlockingOrdersAsync(cancellationToken);

    public Task<IReadOnlyList<ShopDeliveryWorkItem>> ListPendingDeliveriesAsync(
        int limit,
        CancellationToken cancellationToken) =>
        _repository.ListPendingDeliveriesAsync(limit, cancellationToken);

    public Task<IReadOnlyList<ShopDeliveryWorkItem>> ListAllPendingDeliveriesAsync(
        CancellationToken cancellationToken) =>
        _repository.ListAllPendingDeliveriesAsync(cancellationToken);

    public Task<IReadOnlyList<ShopDelivery>> ListInFlightDeliveriesAsync(
        int limit,
        CancellationToken cancellationToken) =>
        _repository.ListInFlightDeliveriesAsync(limit, cancellationToken);

    public Task<ShopDeliveryUpdateResult> MarkDeliveryDispatchedAsync(
        Guid deliveryId,
        Guid commandId,
        CancellationToken cancellationToken) =>
        _repository.MarkDeliveryDispatchedAsync(deliveryId, commandId, cancellationToken);

    public Task<ShopDeliveryUpdateResult> MarkDeliveryDeliveredAsync(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        _repository.MarkDeliveryOutcomeAsync(
            deliveryId,
            ShopDeliveryState.Delivered,
            null,
            null,
            cancellationToken);

    public Task<ShopDeliveryUpdateResult> MarkDeliveryFailedAsync(
        Guid deliveryId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken) =>
        _repository.MarkDeliveryOutcomeAsync(
            deliveryId,
            ShopDeliveryState.Failed,
            errorCode,
            errorMessage,
            cancellationToken);

    public Task<ShopDeliveryUpdateResult> MarkDeliveryUncertainAsync(
        Guid deliveryId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken) =>
        _repository.MarkDeliveryOutcomeAsync(
            deliveryId,
            ShopDeliveryState.Uncertain,
            errorCode,
            errorMessage,
            cancellationToken);

    public Task<ShopDeliveryUpdateResult> PrepareDeliveryRetryAsync(
        Guid orderId,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        _repository.PrepareDeliveryRetryAsync(orderId, actor, reason, cancellationToken);

    public Task<ShopRefundResult> RefundFailedOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        _repository.RefundFailedOrderAsync(
            orderId,
            idempotencyKey,
            actor,
            reason,
            cancellationToken);

    public Task<ShopRefundResult> RefundUncertainOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        _repository.RefundUncertainOrderAsync(
            orderId,
            idempotencyKey,
            actor,
            reason,
            cancellationToken);

    public Task<ShopDeliveryUpdateResult> MarkUncertainOrderDeliveredAsync(
        Guid orderId,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        _repository.MarkUncertainOrderDeliveredAsync(
            orderId,
            actor,
            reason,
            cancellationToken);
}

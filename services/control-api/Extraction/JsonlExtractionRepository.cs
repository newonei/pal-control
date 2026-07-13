using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PalControl.ControlApi.Extraction;

public sealed class SqliteExtractionRepository : IExtractionRepository, IDisposable, IAsyncDisposable
{
    private const int StoreSchemaVersion = 1;
    private const long MaximumWebSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ExtractionSeason> _seasons = [];
    private readonly Dictionary<Guid, ExtractionAccount> _accounts = [];
    private readonly Dictionary<string, Guid> _accountIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WalletBalance> _balances = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, WalletLedgerEntry> _ledger = [];
    private readonly Dictionary<string, ShopProduct> _products = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ShopOrder> _orders = [];
    private readonly Dictionary<Guid, ShopDelivery> _deliveries = [];
    private readonly Dictionary<string, StoredIdempotency> _idempotency = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _eventIds = [];
    private readonly TimeProvider _timeProvider;
    private readonly string _databasePath;
    private readonly string _legacyEventPath;
    private readonly string _authoritativeMarkerPath;
    private readonly string _connectionString;
    private readonly FileStream _instanceLock;
    private volatile bool _isReady;
    private bool _disposed;

    public SqliteExtractionRepository(string dataDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullDataDirectory);
        _databasePath = Path.Combine(fullDataDirectory, "extraction-commerce.db");
        _legacyEventPath = Path.Combine(fullDataDirectory, "extraction-commerce-events.jsonl");
        _authoritativeMarkerPath = Path.Combine(
            fullDataDirectory,
            "extraction-commerce.sqlite-authoritative.json");
        if (File.Exists(_authoritativeMarkerPath) && !File.Exists(_databasePath))
        {
            throw new InvalidDataException(
                "The authoritative extraction SQLite database is missing; refusing to recreate it from legacy JSONL.");
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _instanceLock = new FileStream(
            Path.Combine(fullDataDirectory, "extraction-commerce.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);

        InitializeDatabase();
        MigrateLegacyEventsIfNeeded();
        LoadEvents();
        if (File.Exists(_authoritativeMarkerPath) && _eventIds.Count == 0)
        {
            throw new InvalidDataException(
                "The authoritative extraction SQLite database contains no events; refusing legacy rollback.");
        }
        if (_eventIds.Count > 0)
        {
            EnsureAuthoritativeMarker();
        }
        EnsureWritable();
        _isReady = true;
    }

    public bool IsReady => _isReady && !_disposed;

    public async Task<IReadOnlyList<ExtractionSeason>> ListSeasonsAsync(
        string? serverId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _seasons.Values
                .Where(season => string.IsNullOrWhiteSpace(serverId) ||
                    string.Equals(season.ServerId, serverId.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(season => season.StartsAt)
                .Select(CloneSeason)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionSeason?> GetSeasonAsync(
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _seasons.TryGetValue(seasonId, out var season) ? CloneSeason(season) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionSeason> UpsertSeasonAsync(
        Guid? seasonId,
        ExtractionSeasonDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var serverId = NormalizeRequired(definition.ServerId, 64, nameof(definition.ServerId));
        var code = NormalizeRequired(definition.Code, 64, nameof(definition.Code));
        var displayName = NormalizeRequired(definition.DisplayName, 128, nameof(definition.DisplayName));
        var worldId = NormalizeOptional(definition.WorldId, 128, nameof(definition.WorldId));
        if (definition.EndsAt <= definition.StartsAt)
        {
            throw new ArgumentException("Season EndsAt must be later than StartsAt.", nameof(definition));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var id = seasonId ?? Guid.NewGuid();
            _seasons.TryGetValue(id, out var existing);
            VerifyRevision(existing?.Revision, expectedRevision, "season");
            if (_seasons.Values.Any(season =>
                    season.SeasonId != id &&
                    string.Equals(season.ServerId, serverId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(season.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Season code '{code}' already exists for server '{serverId}'.");
            }
            if (definition.State == ExtractionSeasonState.Active &&
                _seasons.Values.Any(season =>
                    season.SeasonId != id &&
                    season.State == ExtractionSeasonState.Active &&
                    string.Equals(season.ServerId, serverId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Server '{serverId}' already has an active extraction season.");
            }

            var now = UtcNow();
            var season = new ExtractionSeason(
                id,
                serverId,
                code,
                displayName,
                worldId,
                definition.StartsAt.ToUniversalTime(),
                definition.EndsAt.ToUniversalTime(),
                definition.State,
                checked((existing?.Revision ?? 0) + 1),
                existing?.CreatedAt ?? now,
                now);
            await AppendAndApplyAsync(NewEvent("season.upserted", now, season: season), cancellationToken);
            return CloneSeason(season);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionAccount> GetOrCreateAccountAsync(
        string identityProvider,
        string externalUserId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var provider = NormalizeRequired(identityProvider, 32, nameof(identityProvider)).ToLowerInvariant();
        var userId = NormalizeRequired(externalUserId, 128, nameof(externalUserId));
        var normalizedDisplayName = NormalizeRequired(displayName, 128, nameof(displayName));
        var identityKey = AccountIdentityKey(provider, userId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var now = UtcNow();
            if (_accountIdentities.TryGetValue(identityKey, out var accountId))
            {
                var existing = _accounts[accountId];
                if (string.Equals(existing.DisplayName, normalizedDisplayName, StringComparison.Ordinal))
                {
                    return CloneAccount(existing);
                }

                var updated = existing with
                {
                    DisplayName = normalizedDisplayName,
                    Revision = checked(existing.Revision + 1),
                    UpdatedAt = now
                };
                await AppendAndApplyAsync(NewEvent("account.updated", now, account: updated), cancellationToken);
                return CloneAccount(updated);
            }

            var account = new ExtractionAccount(
                Guid.NewGuid(),
                provider,
                userId,
                normalizedDisplayName,
                1,
                now,
                now);
            await AppendAndApplyAsync(NewEvent("account.created", now, account: account), cancellationToken);
            return CloneAccount(account);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionAccount?> GetAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _accounts.TryGetValue(accountId, out var account) ? CloneAccount(account) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionAccount?> FindAccountAsync(
        string identityProvider,
        string externalUserId,
        CancellationToken cancellationToken)
    {
        var provider = NormalizeRequired(identityProvider, 32, nameof(identityProvider)).ToLowerInvariant();
        var userId = NormalizeRequired(externalUserId, 128, nameof(externalUserId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _accountIdentities.TryGetValue(AccountIdentityKey(provider, userId), out var accountId)
                ? CloneAccount(_accounts[accountId])
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionWalletSnapshot> GetWalletAsync(
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireAccount(accountId);
            RequireSeason(seasonId);
            return CreateWalletSnapshot(accountId, seasonId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WalletAdjustmentResult> AdjustWalletAsync(
        WalletAdjustmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizeWalletAdjustment(request);
        var requestHash = HashWalletAdjustment(normalized);
        var scope = IdempotencyScope("wallet", normalized.AccountId, normalized.IdempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (_idempotency.TryGetValue(scope, out var replay))
            {
                if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return new WalletAdjustmentResult(
                        null, null, false, true, "IDEMPOTENCY_CONFLICT",
                        "The idempotency key was already used for a different wallet adjustment.");
                }
                var existingEntry = _ledger[replay.ResourceId];
                return new WalletAdjustmentResult(
                    CloneBalance(GetBalance(existingEntry.AccountId, existingEntry.Currency, existingEntry.SeasonId)),
                    existingEntry,
                    false,
                    false,
                    null,
                    null);
            }

            if (!_accounts.ContainsKey(normalized.AccountId))
            {
                return WalletFailure("ACCOUNT_NOT_FOUND", "The extraction account does not exist.");
            }
            if (normalized.Currency == ExtractionCurrency.SeasonVoucher &&
                (normalized.SeasonId is null || !_seasons.ContainsKey(normalized.SeasonId.Value)))
            {
                return WalletFailure("SEASON_NOT_FOUND", "Season vouchers require an existing season.");
            }

            var current = GetBalance(normalized.AccountId, normalized.Currency, normalized.SeasonId);
            long nextBalance;
            try
            {
                nextBalance = checked(current.Balance + normalized.Delta);
            }
            catch (OverflowException)
            {
                return WalletFailure("BALANCE_OVERFLOW", "The wallet adjustment exceeds the supported balance range.");
            }
            if (nextBalance < 0)
            {
                return WalletFailure("INSUFFICIENT_FUNDS", "The wallet balance cannot become negative.");
            }
            if (nextBalance > MaximumWebSafeInteger)
            {
                return WalletFailure(
                    "BALANCE_OUT_OF_WEB_RANGE",
                    "The wallet balance cannot exceed the exact integer range supported by the web console.");
            }

            var now = UtcNow();
            var balance = current with
            {
                Balance = nextBalance,
                Revision = checked(current.Revision + 1),
                UpdatedAt = now
            };
            var ledger = new WalletLedgerEntry(
                Guid.NewGuid(),
                normalized.AccountId,
                normalized.Currency,
                normalized.SeasonId,
                normalized.Delta,
                nextBalance,
                normalized.Reason,
                normalized.ReferenceType,
                normalized.ReferenceId,
                normalized.Actor,
                now);
            var idempotency = new StoredIdempotency(
                scope,
                requestHash,
                "wallet-ledger",
                ledger.EntryId,
                now);
            await AppendAndApplyAsync(
                NewEvent(
                    "wallet.adjusted",
                    now,
                    balances: [balance],
                    ledgerEntries: [ledger],
                    idempotency: idempotency),
                cancellationToken);
            return new WalletAdjustmentResult(CloneBalance(balance), ledger, true, false, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WalletLedgerEntry>> GetLedgerAsync(
        Guid accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireAccount(accountId);
            return _ledger.Values
                .Where(entry => entry.AccountId == accountId &&
                    (seasonId is null || entry.SeasonId is null || entry.SeasonId == seasonId))
                .OrderByDescending(entry => entry.CreatedAt)
                .ThenByDescending(entry => entry.EntryId)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WalletLedgerEntry?> FindLedgerEntryByReferenceAsync(
        Guid accountId,
        ExtractionCurrency currency,
        Guid? seasonId,
        string referenceType,
        string referenceId,
        CancellationToken cancellationToken)
    {
        var normalizedReferenceType = NormalizeRequired(
            referenceType,
            64,
            nameof(referenceType));
        var normalizedReferenceId = NormalizeRequired(
            referenceId,
            128,
            nameof(referenceId));
        var scopedSeasonId = ScopeSeasonId(currency, seasonId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireAccount(accountId);
            var matches = _ledger.Values
                .Where(entry => entry.AccountId == accountId &&
                    entry.Currency == currency &&
                    entry.SeasonId == scopedSeasonId &&
                    string.Equals(
                        entry.ReferenceType,
                        normalizedReferenceType,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        entry.ReferenceId,
                        normalizedReferenceId,
                        StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidDataException(
                    $"More than one wallet ledger entry exists for reference '{normalizedReferenceType}:{normalizedReferenceId}'.");
            }
            return matches.SingleOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopProduct>> ListProductsAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _products.Values
                .Where(product => includeInactive || product.Active)
                .OrderBy(product => product.Sku, StringComparer.OrdinalIgnoreCase)
                .Select(CloneProduct)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopProduct?> GetProductAsync(
        string sku,
        CancellationToken cancellationToken)
    {
        var normalizedSku = NormalizeSku(sku);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _products.TryGetValue(normalizedSku, out var product) ? CloneProduct(product) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopProduct> UpsertProductAsync(
        ShopProductDefinition definition,
        long? expectedRevision,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = NormalizeProductDefinition(definition);
        var normalizedActor = NormalizeRequired(actor, 128, nameof(actor));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            _products.TryGetValue(normalized.Sku, out var existing);
            VerifyRevision(existing?.Revision, expectedRevision, "product");
            var now = UtcNow();
            var product = new ShopProduct(
                existing?.ProductId ?? Guid.NewGuid(),
                normalized.Sku,
                normalized.DisplayName,
                normalized.Description,
                normalized.PriceCurrency,
                normalized.UnitPrice,
                normalized.ItemGrants.ToArray(),
                normalized.PurchaseLimitPerSeason,
                normalized.Active,
                normalized.AvailableFrom?.ToUniversalTime(),
                normalized.AvailableUntil?.ToUniversalTime(),
                checked((existing?.Revision ?? 0) + 1),
                normalizedActor,
                existing?.CreatedAt ?? now,
                now);
            await AppendAndApplyAsync(NewEvent("product.upserted", now, product: product), cancellationToken);
            return CloneProduct(product);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopPurchaseResult> PurchaseAsync(
        ShopPurchaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizePurchaseRequest(request);
        var requestHash = HashPurchase(normalized);
        var scope = IdempotencyScope("purchase", normalized.AccountId, normalized.IdempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (_idempotency.TryGetValue(scope, out var replay))
            {
                if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return PurchaseFailure(
                        "IDEMPOTENCY_CONFLICT",
                        "The idempotency key was already used for a different purchase.",
                        idempotencyConflict: true);
                }

                var replayedOrder = _orders[replay.ResourceId];
                var pendingDelivery = _deliveries.TryGetValue(replayedOrder.DeliveryId, out var replayedDelivery) &&
                    replayedDelivery.State == ShopDeliveryState.Pending
                        ? CreateDeliveryWorkItem(replayedOrder)
                        : null;
                return new ShopPurchaseResult(
                    CloneOrder(replayedOrder),
                    pendingDelivery,
                    false,
                    false,
                    null,
                    null);
            }

            if (!_accounts.ContainsKey(normalized.AccountId))
            {
                return PurchaseFailure("ACCOUNT_NOT_FOUND", "The extraction account does not exist.");
            }
            if (!_seasons.TryGetValue(normalized.SeasonId, out var season))
            {
                return PurchaseFailure("SEASON_NOT_FOUND", "The extraction season does not exist.");
            }
            var now = UtcNow();
            if (season.State != ExtractionSeasonState.Active || now < season.StartsAt || now >= season.EndsAt)
            {
                return PurchaseFailure("SEASON_NOT_ACTIVE", "Purchases require an active season within its time window.");
            }
            if (!string.Equals(season.ServerId, normalized.ServerId, StringComparison.OrdinalIgnoreCase))
            {
                return PurchaseFailure("SERVER_SEASON_MISMATCH", "The season does not belong to the target server.");
            }

            var products = new List<(ShopProduct Product, int Quantity)>();
            foreach (var requestedLine in normalized.Lines)
            {
                if (!_products.TryGetValue(requestedLine.Sku, out var product))
                {
                    return PurchaseFailure("PRODUCT_NOT_FOUND", $"Shop product '{requestedLine.Sku}' does not exist.");
                }
                if (!product.Active ||
                    (product.AvailableFrom is not null && now < product.AvailableFrom.Value) ||
                    (product.AvailableUntil is not null && now >= product.AvailableUntil.Value))
                {
                    return PurchaseFailure("PRODUCT_NOT_AVAILABLE", $"Shop product '{product.Sku}' is not available.");
                }

                if (product.PurchaseLimitPerSeason is int limit)
                {
                    var alreadyPurchased = _orders.Values
                        .Where(order => order.AccountId == normalized.AccountId &&
                            order.SeasonId == normalized.SeasonId &&
                            order.State != ShopOrderState.Refunded)
                        .SelectMany(order => order.Lines)
                        .Where(line => string.Equals(line.Sku, product.Sku, StringComparison.OrdinalIgnoreCase))
                        .Sum(line => (long)line.Quantity);
                    if (alreadyPurchased + requestedLine.Quantity > limit)
                    {
                        return PurchaseFailure(
                            "PURCHASE_LIMIT_EXCEEDED",
                            $"Shop product '{product.Sku}' exceeds its per-season purchase limit of {limit}.");
                    }
                }
                products.Add((product, requestedLine.Quantity));
            }

            Guid orderId = Guid.NewGuid();
            List<ShopOrderLine> orderLines = [];
            Dictionary<ExtractionCurrency, long> chargeTotals = [];
            try
            {
                foreach (var entry in products)
                {
                    var lineTotal = checked(entry.Product.UnitPrice * entry.Quantity);
                    if (lineTotal > 0)
                    {
                        chargeTotals[entry.Product.PriceCurrency] = checked(
                            chargeTotals.GetValueOrDefault(entry.Product.PriceCurrency) + lineTotal);
                    }
                    orderLines.Add(new ShopOrderLine(
                        Guid.NewGuid(),
                        entry.Product.ProductId,
                        entry.Product.Sku,
                        entry.Product.DisplayName,
                        entry.Quantity,
                        entry.Product.PriceCurrency,
                        entry.Product.UnitPrice,
                        lineTotal,
                        entry.Product.ItemGrants.ToArray()));
                }
            }
            catch (OverflowException)
            {
                return PurchaseFailure("ORDER_TOTAL_OVERFLOW", "The requested order exceeds the supported amount range.");
            }

            List<WalletBalance> updatedBalances = [];
            List<WalletLedgerEntry> ledgerEntries = [];
            foreach (var charge in chargeTotals.OrderBy(item => item.Key))
            {
                var balanceSeasonId = ScopeSeasonId(charge.Key, normalized.SeasonId);
                var current = GetBalance(normalized.AccountId, charge.Key, balanceSeasonId);
                if (current.Balance < charge.Value)
                {
                    return PurchaseFailure(
                        "INSUFFICIENT_FUNDS",
                        $"The {charge.Key} balance is insufficient for this purchase.");
                }

                var nextBalance = current with
                {
                    Balance = checked(current.Balance - charge.Value),
                    Revision = checked(current.Revision + 1),
                    UpdatedAt = now
                };
                updatedBalances.Add(nextBalance);
                ledgerEntries.Add(new WalletLedgerEntry(
                    Guid.NewGuid(),
                    normalized.AccountId,
                    charge.Key,
                    balanceSeasonId,
                    -charge.Value,
                    nextBalance.Balance,
                    normalized.Reason,
                    "shop_order",
                    orderId.ToString("N"),
                    normalized.Actor,
                    now));
            }

            var deliveryId = Guid.NewGuid();
            var delivery = new ShopDelivery(
                deliveryId,
                orderId,
                1,
                ShopDeliveryState.Pending,
                DeliveryIdempotencyKey(orderId, 1),
                null,
                null,
                null,
                now,
                null,
                null);
            var order = new ShopOrder(
                orderId,
                normalized.AccountId,
                normalized.SeasonId,
                normalized.ServerId,
                normalized.PlayerIdentifier,
                orderLines.ToArray(),
                chargeTotals
                    .OrderBy(item => item.Key)
                    .Select(item => new ShopOrderCharge(item.Key, item.Value))
                    .ToArray(),
                ShopOrderState.PendingDelivery,
                deliveryId,
                1,
                normalized.IdempotencyKey,
                normalized.Actor,
                normalized.Reason,
                now,
                now);
            ShopDeliveryWorkItem workItem;
            try
            {
                workItem = CreateDeliveryWorkItem(order);
            }
            catch (OverflowException)
            {
                return PurchaseFailure(
                    "DELIVERY_QUANTITY_OVERFLOW",
                    "The requested item delivery exceeds the supported quantity range.");
            }

            var idempotency = new StoredIdempotency(
                scope,
                requestHash,
                "shop-order",
                orderId,
                now);
            await AppendAndApplyAsync(
                NewEvent(
                    "purchase.committed",
                    now,
                    balances: updatedBalances,
                    ledgerEntries: ledgerEntries,
                    order: order,
                    delivery: delivery,
                    idempotency: idempotency),
                cancellationToken);
            return new ShopPurchaseResult(CloneOrder(order), workItem, true, false, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopOrder?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _orders.TryGetValue(orderId, out var order) ? CloneOrder(order) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopOrder>> ListOrdersAsync(
        Guid? accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _orders.Values
                .Where(order => (accountId is null || order.AccountId == accountId) &&
                    (seasonId is null || order.SeasonId == seasonId))
                .OrderByDescending(order => order.CreatedAt)
                .ThenByDescending(order => order.OrderId)
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(CloneOrder)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopOrder>> ListBlockingOrdersAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _orders.Values
                .Where(order => order.State is
                    ShopOrderState.PendingDelivery or
                    ShopOrderState.Dispatching or
                    ShopOrderState.DeliveryFailed or
                    ShopOrderState.DeliveryUncertain)
                .OrderBy(order => order.CreatedAt)
                .ThenBy(order => order.OrderId)
                .Select(CloneOrder)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopDeliveryWorkItem>> ListPendingDeliveriesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _deliveries.Values
                .Where(delivery => delivery.State == ShopDeliveryState.Pending)
                .OrderBy(delivery => delivery.CreatedAt)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(delivery => CreateDeliveryWorkItem(_orders[delivery.OrderId]))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopDeliveryWorkItem>> ListAllPendingDeliveriesAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _deliveries.Values
                .Where(delivery => delivery.State == ShopDeliveryState.Pending)
                .OrderBy(delivery => delivery.CreatedAt)
                .Select(delivery => CreateDeliveryWorkItem(_orders[delivery.OrderId]))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopDelivery>> ListInFlightDeliveriesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _deliveries.Values
                .Where(delivery =>
                    delivery.State == ShopDeliveryState.Dispatching &&
                    delivery.CommandId is not null)
                .OrderBy(delivery => delivery.DispatchedAt)
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(delivery => delivery with { })
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopDeliveryUpdateResult> MarkDeliveryDispatchedAsync(
        Guid deliveryId,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("Command id cannot be empty.", nameof(commandId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_deliveries.TryGetValue(deliveryId, out var delivery))
            {
                return DeliveryFailure("DELIVERY_NOT_FOUND", "The shop delivery does not exist.");
            }
            var order = _orders[delivery.OrderId];
            if (delivery.State == ShopDeliveryState.Dispatching && delivery.CommandId == commandId)
            {
                return new ShopDeliveryUpdateResult(CloneOrder(order), delivery, false, null, null);
            }
            if (delivery.State != ShopDeliveryState.Pending)
            {
                return DeliveryFailure(
                    "INVALID_DELIVERY_STATE",
                    $"Delivery '{deliveryId}' cannot be dispatched from state '{delivery.State}'.",
                    order,
                    delivery);
            }

            var now = UtcNow();
            var updatedDelivery = delivery with
            {
                State = ShopDeliveryState.Dispatching,
                CommandId = commandId,
                DispatchedAt = now
            };
            var updatedOrder = order with { State = ShopOrderState.Dispatching, UpdatedAt = now };
            await AppendAndApplyAsync(
                NewEvent(
                    "delivery.dispatched",
                    now,
                    order: updatedOrder,
                    delivery: updatedDelivery),
                cancellationToken);
            return new ShopDeliveryUpdateResult(
                CloneOrder(updatedOrder),
                updatedDelivery,
                true,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopDeliveryUpdateResult> MarkDeliveryOutcomeAsync(
        Guid deliveryId,
        ShopDeliveryState state,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (state is not (ShopDeliveryState.Delivered or ShopDeliveryState.Failed or ShopDeliveryState.Uncertain))
        {
            throw new ArgumentException(
                "A delivery outcome must be Delivered, Failed, or Uncertain.",
                nameof(state));
        }
        var normalizedErrorCode = NormalizeOptional(errorCode, 64, nameof(errorCode));
        var normalizedErrorMessage = NormalizeOptional(errorMessage, 1024, nameof(errorMessage));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_deliveries.TryGetValue(deliveryId, out var delivery))
            {
                return DeliveryFailure("DELIVERY_NOT_FOUND", "The shop delivery does not exist.");
            }
            var order = _orders[delivery.OrderId];
            if (delivery.State == state)
            {
                return new ShopDeliveryUpdateResult(CloneOrder(order), delivery, false, null, null);
            }
            if (delivery.State is ShopDeliveryState.Delivered or ShopDeliveryState.Failed or ShopDeliveryState.Uncertain)
            {
                return DeliveryFailure(
                    "DELIVERY_ALREADY_TERMINAL",
                    $"Delivery '{deliveryId}' already has terminal state '{delivery.State}'.",
                    order,
                    delivery);
            }

            var now = UtcNow();
            var updatedDelivery = delivery with
            {
                State = state,
                ErrorCode = state == ShopDeliveryState.Delivered ? null : normalizedErrorCode,
                ErrorMessage = state == ShopDeliveryState.Delivered ? null : normalizedErrorMessage,
                CompletedAt = now
            };
            var orderState = state switch
            {
                ShopDeliveryState.Delivered => ShopOrderState.Delivered,
                ShopDeliveryState.Failed => ShopOrderState.DeliveryFailed,
                ShopDeliveryState.Uncertain => ShopOrderState.DeliveryUncertain,
                _ => throw new InvalidOperationException("Unsupported terminal delivery state.")
            };
            var updatedOrder = order with { State = orderState, UpdatedAt = now };
            await AppendAndApplyAsync(
                NewEvent(
                    $"delivery.{state.ToString().ToLowerInvariant()}",
                    now,
                    order: updatedOrder,
                    delivery: updatedDelivery),
                cancellationToken);
            return new ShopDeliveryUpdateResult(
                CloneOrder(updatedOrder),
                updatedDelivery,
                true,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopDeliveryUpdateResult> PrepareDeliveryRetryAsync(
        Guid orderId,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        _ = NormalizeRequired(actor, 128, nameof(actor));
        _ = NormalizeRequired(reason, 512, nameof(reason));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return DeliveryFailure("ORDER_NOT_FOUND", "The shop order does not exist.");
            }
            _deliveries.TryGetValue(order.DeliveryId, out var currentDelivery);
            if (order.State != ShopOrderState.DeliveryFailed ||
                currentDelivery is null ||
                currentDelivery.State != ShopDeliveryState.Failed)
            {
                return DeliveryFailure(
                    "RETRY_NOT_ALLOWED",
                    "Only a definitively failed delivery can be retried. Uncertain deliveries require reconciliation.",
                    order,
                    currentDelivery);
            }

            var attempt = checked(order.DeliveryAttempt + 1);
            var now = UtcNow();
            var delivery = new ShopDelivery(
                Guid.NewGuid(),
                order.OrderId,
                attempt,
                ShopDeliveryState.Pending,
                DeliveryIdempotencyKey(order.OrderId, attempt),
                null,
                null,
                null,
                now,
                null,
                null);
            var updatedOrder = order with
            {
                State = ShopOrderState.PendingDelivery,
                DeliveryId = delivery.DeliveryId,
                DeliveryAttempt = attempt,
                UpdatedAt = now
            };
            await AppendAndApplyAsync(
                NewEvent("delivery.retry-prepared", now, order: updatedOrder, delivery: delivery),
                cancellationToken);
            return new ShopDeliveryUpdateResult(
                CloneOrder(updatedOrder),
                delivery,
                true,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopDeliveryUpdateResult> MarkUncertainOrderDeliveredAsync(
        Guid orderId,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        _ = NormalizeRequired(actor, 128, nameof(actor));
        var normalizedReason = NormalizeRequired(reason, 512, nameof(reason));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return DeliveryFailure("ORDER_NOT_FOUND", "The shop order does not exist.");
            }
            if (!_deliveries.TryGetValue(order.DeliveryId, out var delivery))
            {
                return DeliveryFailure("DELIVERY_NOT_FOUND", "The shop delivery does not exist.", order);
            }
            if (order.State == ShopOrderState.Delivered && delivery.State == ShopDeliveryState.Delivered)
            {
                return new ShopDeliveryUpdateResult(CloneOrder(order), delivery, false, null, null);
            }
            if (order.State != ShopOrderState.DeliveryUncertain ||
                delivery.State != ShopDeliveryState.Uncertain)
            {
                return DeliveryFailure(
                    "RECONCILIATION_NOT_ALLOWED",
                    "Only an uncertain delivery can be manually confirmed as delivered.",
                    order,
                    delivery);
            }

            var now = UtcNow();
            var updatedDelivery = delivery with
            {
                State = ShopDeliveryState.Delivered,
                ErrorCode = "MANUALLY_RECONCILED_DELIVERED",
                ErrorMessage = normalizedReason,
                CompletedAt = delivery.CompletedAt ?? now
            };
            var updatedOrder = order with { State = ShopOrderState.Delivered, UpdatedAt = now };
            await AppendAndApplyAsync(
                NewEvent(
                    "delivery.manually-reconciled-delivered",
                    now,
                    order: updatedOrder,
                    delivery: updatedDelivery),
                cancellationToken);
            return new ShopDeliveryUpdateResult(
                CloneOrder(updatedOrder),
                updatedDelivery,
                true,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ShopRefundResult> RefundFailedOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        RefundOrderAsync(
            orderId,
            idempotencyKey,
            actor,
            reason,
            allowUncertain: false,
            cancellationToken);

    public Task<ShopRefundResult> RefundUncertainOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        RefundOrderAsync(
            orderId,
            idempotencyKey,
            actor,
            reason,
            allowUncertain: true,
            cancellationToken);

    private async Task<ShopRefundResult> RefundOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        bool allowUncertain,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedActor = NormalizeRequired(actor, 128, nameof(actor));
        var normalizedReason = NormalizeRequired(reason, 512, nameof(reason));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return RefundFailure("ORDER_NOT_FOUND", "The shop order does not exist.");
            }
            var scope = IdempotencyScope("refund", order.AccountId, normalizedKey);
            var requestHash = HashRefund(orderId, normalizedReason);
            if (_idempotency.TryGetValue(scope, out var replay))
            {
                if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return new ShopRefundResult(
                        null,
                        [],
                        false,
                        true,
                        "IDEMPOTENCY_CONFLICT",
                        "The idempotency key was already used for a different refund.");
                }
                var replayedOrder = _orders[replay.ResourceId];
                var replayedEntries = _ledger.Values
                    .Where(entry => entry.ReferenceType == "shop_refund" &&
                        string.Equals(entry.ReferenceId, orderId.ToString("N"), StringComparison.Ordinal))
                    .OrderBy(entry => entry.Currency)
                    .ToArray();
                return new ShopRefundResult(CloneOrder(replayedOrder), replayedEntries, false, false, null, null);
            }
            if (order.State != ShopOrderState.DeliveryFailed &&
                !(allowUncertain && order.State == ShopOrderState.DeliveryUncertain))
            {
                return RefundFailure(
                    "REFUND_NOT_ALLOWED",
                    allowUncertain
                        ? "Only an uncertain order can be manually refunded by this reconciliation path."
                        : "Only a definitively failed order can be automatically refunded. Delivered or uncertain orders require manual reconciliation.",
                    order);
            }

            var now = UtcNow();
            List<WalletBalance> updatedBalances = [];
            List<WalletLedgerEntry> ledgerEntries = [];
            try
            {
                foreach (var charge in order.Charges)
                {
                    var seasonId = ScopeSeasonId(charge.Currency, order.SeasonId);
                    var current = GetBalance(order.AccountId, charge.Currency, seasonId);
                    var refundedBalance = checked(current.Balance + charge.Amount);
                    if (refundedBalance > MaximumWebSafeInteger)
                    {
                        return RefundFailure(
                            "BALANCE_OUT_OF_WEB_RANGE",
                            "The refund would exceed the exact integer range supported by the web console.",
                            order);
                    }
                    var next = current with
                    {
                        Balance = refundedBalance,
                        Revision = checked(current.Revision + 1),
                        UpdatedAt = now
                    };
                    updatedBalances.Add(next);
                    ledgerEntries.Add(new WalletLedgerEntry(
                        Guid.NewGuid(),
                        order.AccountId,
                        charge.Currency,
                        seasonId,
                        charge.Amount,
                        next.Balance,
                        normalizedReason,
                        "shop_refund",
                        order.OrderId.ToString("N"),
                        normalizedActor,
                        now));
                }
            }
            catch (OverflowException)
            {
                return RefundFailure("BALANCE_OVERFLOW", "The refund exceeds the supported balance range.", order);
            }

            var updatedOrder = order with { State = ShopOrderState.Refunded, UpdatedAt = now };
            var idempotency = new StoredIdempotency(
                scope,
                requestHash,
                "shop-order",
                order.OrderId,
                now);
            await AppendAndApplyAsync(
                NewEvent(
                    "order.refunded",
                    now,
                    balances: updatedBalances,
                    ledgerEntries: ledgerEntries,
                    order: updatedOrder,
                    idempotency: idempotency),
                cancellationToken);
            return new ShopRefundResult(
                CloneOrder(updatedOrder),
                ledgerEntries.ToArray(),
                true,
                false,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _isReady = false;
        _instanceLock.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private ExtractionWalletSnapshot CreateWalletSnapshot(Guid accountId, Guid seasonId) => new(
        accountId,
        seasonId,
        CloneBalance(GetBalance(accountId, ExtractionCurrency.MarketCoin, null)),
        CloneBalance(GetBalance(accountId, ExtractionCurrency.SeasonVoucher, seasonId)));

    private WalletBalance GetBalance(
        Guid accountId,
        ExtractionCurrency currency,
        Guid? seasonId)
    {
        var scopedSeasonId = ScopeSeasonId(currency, seasonId);
        return _balances.TryGetValue(BalanceKey(accountId, currency, scopedSeasonId), out var balance)
            ? balance
            : new WalletBalance(accountId, currency, scopedSeasonId, 0, 0, UtcNow());
    }

    private ShopDeliveryWorkItem CreateDeliveryWorkItem(ShopOrder order)
    {
        Dictionary<string, int> items = new(StringComparer.Ordinal);
        foreach (var line in order.Lines)
        {
            foreach (var grant in line.ItemGrants)
            {
                var quantity = checked(grant.Quantity * line.Quantity);
                items[grant.ItemId] = checked(items.GetValueOrDefault(grant.ItemId) + quantity);
            }
        }
        var delivery = _deliveries.TryGetValue(order.DeliveryId, out var storedDelivery)
            ? storedDelivery
            : new ShopDelivery(
                order.DeliveryId,
                order.OrderId,
                order.DeliveryAttempt,
                ShopDeliveryState.Pending,
                DeliveryIdempotencyKey(order.OrderId, order.DeliveryAttempt),
                null,
                null,
                null,
                order.CreatedAt,
                null,
                null);
        return new ShopDeliveryWorkItem(
            delivery.DeliveryId,
            order.OrderId,
            order.ServerId,
            order.PlayerIdentifier,
            $"give/items/{Uri.EscapeDataString(order.PlayerIdentifier)}",
            delivery.IdempotencyKey,
            items.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new ShopItemGrant(item.Key, item.Value))
                .ToArray(),
            delivery.Attempt);
    }

    private static WalletAdjustmentRequest NormalizeWalletAdjustment(WalletAdjustmentRequest request)
    {
        if (request.AccountId == Guid.Empty)
        {
            throw new ArgumentException("Account id cannot be empty.", nameof(request));
        }
        if (request.Delta == 0)
        {
            throw new ArgumentException("Wallet adjustment delta cannot be zero.", nameof(request));
        }
        if (request.Delta is < -MaximumWebSafeInteger or > MaximumWebSafeInteger)
        {
            throw new ArgumentException(
                "Wallet adjustment delta must fit the exact integer range supported by the web console.",
                nameof(request));
        }
        if (request.Currency == ExtractionCurrency.MarketCoin && request.SeasonId is not null)
        {
            throw new ArgumentException("MarketCoin is permanent and must not have a season id.", nameof(request));
        }
        if (request.Currency == ExtractionCurrency.SeasonVoucher && request.SeasonId is null)
        {
            throw new ArgumentException("SeasonVoucher requires a season id.", nameof(request));
        }
        return request with
        {
            Reason = NormalizeRequired(request.Reason, 512, nameof(request.Reason)),
            ReferenceType = NormalizeRequired(request.ReferenceType, 64, nameof(request.ReferenceType)),
            ReferenceId = NormalizeRequired(request.ReferenceId, 128, nameof(request.ReferenceId)),
            Actor = NormalizeRequired(request.Actor, 128, nameof(request.Actor)),
            IdempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey)
        };
    }

    private static ShopProductDefinition NormalizeProductDefinition(ShopProductDefinition definition)
    {
        if (definition.UnitPrice is < 0 or > MaximumWebSafeInteger)
        {
            throw new ArgumentException(
                "Product unit price must be within the exact integer range supported by the web console.",
                nameof(definition));
        }
        if (definition.PurchaseLimitPerSeason is <= 0)
        {
            throw new ArgumentException("Purchase limit must be positive when supplied.", nameof(definition));
        }
        if (definition.AvailableFrom is not null &&
            definition.AvailableUntil is not null &&
            definition.AvailableUntil <= definition.AvailableFrom)
        {
            throw new ArgumentException("AvailableUntil must be later than AvailableFrom.", nameof(definition));
        }
        if (definition.ItemGrants is null || definition.ItemGrants.Count == 0)
        {
            throw new ArgumentException("A product must grant at least one item.", nameof(definition));
        }
        if (definition.ItemGrants.Count > 100)
        {
            throw new ArgumentException("A product cannot contain more than 100 item grants.", nameof(definition));
        }

        Dictionary<string, int> mergedGrants = new(StringComparer.Ordinal);
        foreach (var grant in definition.ItemGrants)
        {
            if (grant is null)
            {
                throw new ArgumentException("Product item grants cannot contain null values.", nameof(definition));
            }
            var itemId = NormalizeRequired(grant.ItemId, 128, nameof(grant.ItemId));
            if (grant.Quantity <= 0)
            {
                throw new ArgumentException("Product item grant quantity must be positive.", nameof(definition));
            }
            mergedGrants[itemId] = checked(mergedGrants.GetValueOrDefault(itemId) + grant.Quantity);
        }
        return definition with
        {
            Sku = NormalizeSku(definition.Sku),
            DisplayName = NormalizeRequired(definition.DisplayName, 128, nameof(definition.DisplayName)),
            Description = NormalizeRequired(definition.Description, 1024, nameof(definition.Description)),
            ItemGrants = mergedGrants
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new ShopItemGrant(item.Key, item.Value))
                .ToArray()
        };
    }

    private static ShopPurchaseRequest NormalizePurchaseRequest(ShopPurchaseRequest request)
    {
        if (request.AccountId == Guid.Empty || request.SeasonId == Guid.Empty)
        {
            throw new ArgumentException("Account id and season id cannot be empty.", nameof(request));
        }
        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new ArgumentException("A purchase must contain at least one line.", nameof(request));
        }
        if (request.Lines.Count > 50)
        {
            throw new ArgumentException("A purchase cannot contain more than 50 lines.", nameof(request));
        }

        Dictionary<string, int> mergedLines = new(StringComparer.OrdinalIgnoreCase);
        foreach (var line in request.Lines)
        {
            if (line is null)
            {
                throw new ArgumentException("Purchase lines cannot contain null values.", nameof(request));
            }
            var sku = NormalizeSku(line.Sku);
            if (line.Quantity is <= 0 or > 1000)
            {
                throw new ArgumentException("Purchase line quantity must be between 1 and 1000.", nameof(request));
            }
            mergedLines[sku] = checked(mergedLines.GetValueOrDefault(sku) + line.Quantity);
            if (mergedLines[sku] > 1000)
            {
                throw new ArgumentException("Merged purchase quantity cannot exceed 1000 per SKU.", nameof(request));
            }
        }
        return request with
        {
            ServerId = NormalizeRequired(request.ServerId, 64, nameof(request.ServerId)),
            PlayerIdentifier = NormalizeRequired(request.PlayerIdentifier, 128, nameof(request.PlayerIdentifier)),
            Lines = mergedLines
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => new ShopPurchaseLineInput(item.Key, item.Value))
                .ToArray(),
            IdempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey),
            Actor = NormalizeRequired(request.Actor, 128, nameof(request.Actor)),
            Reason = NormalizeRequired(request.Reason, 512, nameof(request.Reason))
        };
    }

    private static string HashWalletAdjustment(WalletAdjustmentRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("operation", "wallet.adjust");
        writer.WriteString("accountId", request.AccountId);
        writer.WriteString("currency", request.Currency.ToString());
        if (request.SeasonId is Guid seasonId)
        {
            writer.WriteString("seasonId", seasonId);
        }
        else
        {
            writer.WriteNull("seasonId");
        }
        writer.WriteNumber("delta", request.Delta);
        writer.WriteString("reason", request.Reason);
        writer.WriteString("referenceType", request.ReferenceType);
        writer.WriteString("referenceId", request.ReferenceId);
        writer.WriteEndObject();
        writer.Flush();
        return Sha256(buffer.WrittenSpan);
    }

    private static string HashPurchase(ShopPurchaseRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("operation", "shop.purchase");
        writer.WriteString("accountId", request.AccountId);
        writer.WriteString("seasonId", request.SeasonId);
        writer.WriteString("serverId", request.ServerId);
        writer.WriteString("playerIdentifier", request.PlayerIdentifier);
        writer.WriteStartArray("lines");
        foreach (var line in request.Lines)
        {
            writer.WriteStartObject();
            writer.WriteString("sku", line.Sku);
            writer.WriteNumber("quantity", line.Quantity);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Sha256(buffer.WrittenSpan);
    }

    private static string HashRefund(Guid orderId, string reason)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("operation", "shop.refund");
        writer.WriteString("orderId", orderId);
        writer.WriteString("reason", reason);
        writer.WriteEndObject();
        writer.Flush();
        return Sha256(buffer.WrittenSpan);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private async Task AppendAndApplyAsync(StoreEvent storeEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(storeEvent, JsonOptions);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
            await using var command = CreateInsertCommand(connection, transaction, storeEvent, payload);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            EnsureAuthoritativeMarker();
        }
        catch
        {
            _isReady = false;
            throw;
        }
        ApplyEvent(storeEvent);
    }

    private void LoadEvents()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence, payload FROM extraction_events ORDER BY sequence;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sequence = reader.GetInt64(0);
            var storeEvent = DeserializeStoreEvent(reader.GetString(1), $"SQLite sequence {sequence}");
            ApplyEvent(storeEvent);
        }
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS extraction_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                event_type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private void MigrateLegacyEventsIfNeeded()
    {
        if (!File.Exists(_legacyEventPath))
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM extraction_events;";
            var eventCount = Convert.ToInt64(countCommand.ExecuteScalar());
            if (eventCount > 0)
            {
                return;
            }
            if (File.Exists(_authoritativeMarkerPath))
            {
                throw new InvalidDataException(
                    "The authoritative extraction SQLite database is empty; refusing to restore stale legacy JSONL.");
            }
        }

        RepairPartialLegacyFinalLine();
        var events = new List<(StoreEvent Event, string Payload)>();
        var eventIds = new HashSet<Guid>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(_legacyEventPath, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException($"Legacy extraction event line {lineNumber} is empty.");
            }
            var storeEvent = DeserializeStoreEvent(line, $"legacy JSONL line {lineNumber}");
            if (!eventIds.Add(storeEvent.EventId))
            {
                throw new InvalidDataException($"Duplicate legacy extraction event id '{storeEvent.EventId}'.");
            }
            events.Add((storeEvent, line));
        }
        if (events.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var entry in events)
        {
            using var command = CreateInsertCommand(connection, transaction, entry.Event, entry.Payload);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void EnsureAuthoritativeMarker()
    {
        if (File.Exists(_authoritativeMarkerPath))
        {
            return;
        }
        var tempPath = $"{_authoritativeMarkerPath}.{Guid.NewGuid():N}.tmp";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            authoritativeStore = Path.GetFileName(_databasePath),
            legacyStore = Path.GetFileName(_legacyEventPath),
            createdAt = DateTimeOffset.UtcNow
        }, JsonOptions);
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _authoritativeMarkerPath);
        }
        catch (IOException) when (File.Exists(_authoritativeMarkerPath))
        {
            // Another safe startup/write may have won the atomic marker creation race.
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static SqliteCommand CreateInsertCommand(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        StoreEvent storeEvent,
        string payload)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO extraction_events (event_id, event_type, occurred_at, payload)
            VALUES ($eventId, $eventType, $occurredAt, $payload);
            """;
        command.Parameters.AddWithValue("$eventId", storeEvent.EventId.ToString("D"));
        command.Parameters.AddWithValue("$eventType", storeEvent.EventType);
        command.Parameters.AddWithValue("$occurredAt", storeEvent.At.ToString("O"));
        command.Parameters.AddWithValue("$payload", payload);
        return command;
    }

    private static StoreEvent DeserializeStoreEvent(string payload, string location)
    {
        StoreEvent storeEvent;
        try
        {
            storeEvent = JsonSerializer.Deserialize<StoreEvent>(payload, JsonOptions)
                ?? throw new JsonException("The extraction event is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Extraction event at {location} is invalid JSON.", exception);
        }
        if (storeEvent.SchemaVersion != StoreSchemaVersion)
        {
            throw new InvalidDataException(
                $"Extraction event at {location} uses unsupported schema version {storeEvent.SchemaVersion}.");
        }
        return storeEvent;
    }

    private void ApplyEvent(StoreEvent storeEvent)
    {
        if (!_eventIds.Add(storeEvent.EventId))
        {
            throw new InvalidDataException($"Duplicate extraction event id '{storeEvent.EventId}'.");
        }
        if (storeEvent.Season is not null)
        {
            _seasons[storeEvent.Season.SeasonId] = CloneSeason(storeEvent.Season);
        }
        if (storeEvent.Account is not null)
        {
            var account = CloneAccount(storeEvent.Account);
            _accounts[account.AccountId] = account;
            _accountIdentities[AccountIdentityKey(account.IdentityProvider, account.ExternalUserId)] = account.AccountId;
        }
        if (storeEvent.Balances is not null)
        {
            foreach (var balance in storeEvent.Balances)
            {
                var clone = CloneBalance(balance);
                _balances[BalanceKey(clone.AccountId, clone.Currency, clone.SeasonId)] = clone;
            }
        }
        if (storeEvent.LedgerEntries is not null)
        {
            foreach (var entry in storeEvent.LedgerEntries)
            {
                _ledger[entry.EntryId] = entry;
            }
        }
        if (storeEvent.Product is not null)
        {
            var product = CloneProduct(storeEvent.Product);
            _products[product.Sku] = product;
        }
        if (storeEvent.Order is not null)
        {
            var order = CloneOrder(storeEvent.Order);
            _orders[order.OrderId] = order;
        }
        if (storeEvent.Delivery is not null)
        {
            _deliveries[storeEvent.Delivery.DeliveryId] = storeEvent.Delivery;
        }
        if (storeEvent.Idempotency is not null)
        {
            if (_idempotency.TryGetValue(storeEvent.Idempotency.Scope, out var existing) &&
                (!string.Equals(existing.RequestHash, storeEvent.Idempotency.RequestHash, StringComparison.Ordinal) ||
                 existing.ResourceId != storeEvent.Idempotency.ResourceId))
            {
                throw new InvalidDataException(
                    $"Extraction idempotency scope '{storeEvent.Idempotency.Scope}' has conflicting events.");
            }
            _idempotency[storeEvent.Idempotency.Scope] = storeEvent.Idempotency;
        }
    }

    private void RepairPartialLegacyFinalLine()
    {
        if (!File.Exists(_legacyEventPath))
        {
            return;
        }
        var bytes = File.ReadAllBytes(_legacyEventPath);
        if (bytes.Length == 0 || bytes[^1] == (byte)'\n')
        {
            return;
        }
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        using var stream = new FileStream(
            _legacyEventPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        stream.SetLength(lastNewline < 0 ? 0 : lastNewline + 1);
        stream.Flush(flushToDisk: true);
    }

    private void EnsureWritable()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        transaction.Rollback();
    }

    private static StoreEvent NewEvent(
        string eventType,
        DateTimeOffset at,
        ExtractionSeason? season = null,
        ExtractionAccount? account = null,
        IEnumerable<WalletBalance>? balances = null,
        IEnumerable<WalletLedgerEntry>? ledgerEntries = null,
        ShopProduct? product = null,
        ShopOrder? order = null,
        ShopDelivery? delivery = null,
        StoredIdempotency? idempotency = null) => new()
        {
            SchemaVersion = StoreSchemaVersion,
            EventId = Guid.NewGuid(),
            EventType = eventType,
            At = at,
            Season = season,
            Account = account,
            Balances = balances?.ToArray(),
            LedgerEntries = ledgerEntries?.ToArray(),
            Product = product,
            Order = order,
            Delivery = delivery,
            Idempotency = idempotency
        };

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isReady)
        {
            throw new IOException("The extraction commerce event store is not ready.");
        }
    }

    private void RequireAccount(Guid accountId)
    {
        if (!_accounts.ContainsKey(accountId))
        {
            throw new KeyNotFoundException($"Extraction account '{accountId}' does not exist.");
        }
    }

    private void RequireSeason(Guid seasonId)
    {
        if (!_seasons.ContainsKey(seasonId))
        {
            throw new KeyNotFoundException($"Extraction season '{seasonId}' does not exist.");
        }
    }

    private static Guid? ScopeSeasonId(ExtractionCurrency currency, Guid? seasonId) =>
        currency == ExtractionCurrency.MarketCoin
            ? null
            : seasonId ?? throw new ArgumentException("SeasonVoucher requires a season id.", nameof(seasonId));

    private static string AccountIdentityKey(string provider, string userId) => $"{provider}\n{userId}";

    private static string BalanceKey(
        Guid accountId,
        ExtractionCurrency currency,
        Guid? seasonId) => $"{accountId:N}\n{currency}\n{seasonId?.ToString("N") ?? "permanent"}";

    private static string IdempotencyScope(string operation, Guid accountId, string key) =>
        $"{operation}\n{accountId:N}\n{key}";

    private static string DeliveryIdempotencyKey(Guid orderId, int attempt) =>
        $"shop-delivery:{orderId:N}:{attempt}";

    private static string NormalizeSku(string value)
    {
        var sku = NormalizeRequired(value, 64, nameof(value)).ToUpperInvariant();
        if (sku.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "SKU may contain only ASCII letters, digits, '-', '_' and '.'.",
                nameof(value));
        }
        return sku;
    }

    private static string NormalizeIdempotencyKey(string value)
    {
        var key = NormalizeRequired(value, 128, nameof(value));
        if (key.Length < 8 || key.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Idempotency key must contain 8 to 128 non-control characters.",
                nameof(value));
        }
        return key;
    }

    private static string NormalizeRequired(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} must contain at most {maxLength} non-control characters.",
                parameterName);
        }
        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeRequired(value, maxLength, parameterName);

    private static void VerifyRevision(long? actualRevision, long? expectedRevision, string resourceName)
    {
        if (expectedRevision is null)
        {
            return;
        }
        var actual = actualRevision ?? 0;
        if (actual != expectedRevision.Value)
        {
            throw new InvalidOperationException(
                $"The {resourceName} revision changed (expected {expectedRevision}, actual {actual}).");
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

    private static ExtractionSeason CloneSeason(ExtractionSeason season) => season with { };

    private static ExtractionAccount CloneAccount(ExtractionAccount account) => account with { };

    private static WalletBalance CloneBalance(WalletBalance balance) => balance with { };

    private static ShopProduct CloneProduct(ShopProduct product) => product with
    {
        ItemGrants = product.ItemGrants.ToArray()
    };

    private static ShopOrder CloneOrder(ShopOrder order) => order with
    {
        Lines = order.Lines.Select(line => line with { ItemGrants = line.ItemGrants.ToArray() }).ToArray(),
        Charges = order.Charges.ToArray()
    };

    private static WalletAdjustmentResult WalletFailure(string code, string message) =>
        new(null, null, false, false, code, message);

    private static ShopPurchaseResult PurchaseFailure(
        string code,
        string message,
        bool idempotencyConflict = false) =>
        new(null, null, false, idempotencyConflict, code, message);

    private static ShopDeliveryUpdateResult DeliveryFailure(
        string code,
        string message,
        ShopOrder? order = null,
        ShopDelivery? delivery = null) =>
        new(order is null ? null : CloneOrder(order), delivery, false, code, message);

    private static ShopRefundResult RefundFailure(
        string code,
        string message,
        ShopOrder? order = null) =>
        new(order is null ? null : CloneOrder(order), [], false, false, code, message);

    private sealed record StoredIdempotency(
        string Scope,
        string RequestHash,
        string ResourceType,
        Guid ResourceId,
        DateTimeOffset CreatedAt);

    private sealed class StoreEvent
    {
        public required int SchemaVersion { get; init; }
        public required Guid EventId { get; init; }
        public required string EventType { get; init; }
        public required DateTimeOffset At { get; init; }
        public ExtractionSeason? Season { get; init; }
        public ExtractionAccount? Account { get; init; }
        public IReadOnlyList<WalletBalance>? Balances { get; init; }
        public IReadOnlyList<WalletLedgerEntry>? LedgerEntries { get; init; }
        public ShopProduct? Product { get; init; }
        public ShopOrder? Order { get; init; }
        public ShopDelivery? Delivery { get; init; }
        public StoredIdempotency? Idempotency { get; init; }
    }
}

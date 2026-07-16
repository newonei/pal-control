using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Persists only server-observed funnel denominators and recomputes analytics
/// from the authoritative SQLite event store. There is intentionally no route
/// that accepts analytics facts from a browser.
/// </summary>
public sealed class EconomyAnalyticsStore
{
    private const int SchemaVersion = 1;
    private const int MinimumCohortSize = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _businessTimeZone;

    public EconomyAnalyticsStore(
        IOptions<ExtractionPersistenceOptions> persistence,
        IOptions<ExtractionModeOptions> extraction,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(
            ResolveDataDirectory(persistence.Value.DataDirectory, environment.ContentRootPath),
            extraction.Value.ResolveTimeZone(),
            timeProvider)
    {
    }

    public EconomyAnalyticsStore(
        string dataDirectory,
        TimeZoneInfo? businessTimeZone = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _businessTimeZone = businessTimeZone ?? TimeZoneInfo.Utc;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Initialize();
    }

    public Task RecordPortalSessionAsync(
        Guid accountId,
        string serverId,
        Guid seasonId,
        Guid? contentVersionId,
        DateOnly businessDate,
        CancellationToken cancellationToken) =>
        RecordFactAsync(
            EconomyAnalyticsFactType.PortalSession,
            accountId,
            serverId,
            seasonId,
            contentVersionId,
            businessDate,
            cancellationToken);

    public Task RecordCatalogViewAsync(
        Guid accountId,
        string serverId,
        Guid seasonId,
        Guid? contentVersionId,
        DateOnly businessDate,
        CancellationToken cancellationToken) =>
        RecordFactAsync(
            EconomyAnalyticsFactType.CatalogView,
            accountId,
            serverId,
            seasonId,
            contentVersionId,
            businessDate,
            cancellationToken);

    public async Task<EconomyAnalyticsReport> QueryAsync(
        EconomyAnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(query);
        await using var connection = Open(readOnly: true);
        await ExecuteAsync(
            connection,
            "PRAGMA query_only=ON; PRAGMA busy_timeout=5000; BEGIN DEFERRED;",
            cancellationToken);
        try
        {
            await EnsureHealthyAsync(connection, cancellationToken);
            var state = await LoadStateAsync(connection, cancellationToken);
            return BuildReport(query, state);
        }
        finally
        {
            await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None);
        }
    }

    private async Task RecordFactAsync(
        EconomyAnalyticsFactType type,
        Guid accountId,
        string serverId,
        Guid seasonId,
        Guid? contentVersionId,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty || seasonId == Guid.Empty ||
            string.IsNullOrWhiteSpace(serverId) || serverId.Trim().Length > 64 ||
            contentVersionId == Guid.Empty)
        {
            throw new ArgumentException("The authoritative analytics fact identity is invalid.");
        }
        var normalizedServer = serverId.Trim().ToLowerInvariant();
        var typeValue = FactType(type);
        var keyMaterial = string.Join(
            '\n',
            SchemaVersion.ToString(CultureInfo.InvariantCulture),
            typeValue,
            normalizedServer,
            accountId.ToString("N"),
            seasonId.ToString("N"),
            contentVersionId?.ToString("N") ?? "none",
            businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var eventKey = Sha256($"pal-control-economy-analytics-key-v1\n{keyMaterial}");
        var now = _timeProvider.GetUtcNow();
        await using var connection = Open(readOnly: false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO economy_analytics_events (
                event_id, event_key, event_type, account_id, server_id,
                season_id, content_version_id, business_date, occurred_at, source)
            VALUES (
                $eventId, $eventKey, $eventType, $accountId, $serverId,
                $seasonId, $contentVersionId, $businessDate, $occurredAt, 'server-observed')
            ON CONFLICT(event_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$eventKey", eventKey);
        command.Parameters.AddWithValue("$eventType", typeValue);
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", normalizedServer);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue(
            "$contentVersionId",
            contentVersionId is Guid versionId ? versionId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue(
            "$businessDate",
            businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$occurredAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private EconomyAnalyticsReport BuildReport(EconomyAnalyticsQuery query, AnalyticsState state)
    {
        var serverSeasons = state.Seasons.Values
            .Where(season => string.Equals(season.ServerId, query.ServerId, StringComparison.OrdinalIgnoreCase))
            .Select(season => season.SeasonId)
            .ToHashSet();
        if (query.SeasonId is Guid requestedSeason && !serverSeasons.Contains(requestedSeason))
        {
            throw new EconomyAnalyticsException(
                "ANALYTICS_SEASON_NOT_FOUND",
                "The requested season does not belong to the selected server.",
                StatusCodes.Status404NotFound);
        }
        if (query.ContentVersionId is Guid requestedVersion &&
            (!state.ContentVersions.TryGetValue(requestedVersion, out var version) ||
             !string.Equals(version.ServerId, query.ServerId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new EconomyAnalyticsException(
                "ANALYTICS_CONTENT_VERSION_NOT_FOUND",
                "The requested content version does not belong to the selected server.",
                StatusCodes.Status404NotFound);
        }

        bool SeasonMatches(Guid seasonId) =>
            serverSeasons.Contains(seasonId) &&
            (query.SeasonId is null || query.SeasonId == seasonId);
        bool VersionMatches(Guid? versionId) =>
            query.ContentVersionId is null || query.ContentVersionId == versionId;
        bool DateMatches(DateTimeOffset at, DateOnly? businessDate) =>
            DateFor(query.DateBasis, at, businessDate) is var date &&
            date >= query.From && date <= query.To;
        bool VisibleByEnd(DateTimeOffset at, DateOnly? businessDate) =>
            DateFor(query.DateBasis, at, businessDate) <= query.To;

        var relevantAnalytics = state.AnalyticsEvents.Where(fact =>
                string.Equals(fact.ServerId, query.ServerId, StringComparison.OrdinalIgnoreCase) &&
                SeasonMatches(fact.SeasonId) &&
                VersionMatches(fact.ContentVersionId))
            .ToArray();
        var analyticsInWindow = relevantAnalytics.Where(fact =>
                DateMatches(fact.OccurredAt, fact.BusinessDate))
            .ToArray();

        var orderFacts = state.Orders.Values
            .Where(order => string.Equals(order.Value.ServerId, query.ServerId, StringComparison.OrdinalIgnoreCase) &&
                SeasonMatches(order.Value.SeasonId))
            .Select(order => new DatedOrder(
                order.Value,
                ResolveOrderVersion(order.Value),
                ResolveOrderBusinessDate(order.Value, state)))
            .Where(order => VersionMatches(order.ContentVersionId))
            .ToArray();
        var ordersInWindow = orderFacts.Where(order =>
                DateMatches(order.Value.CreatedAt, order.BusinessDate))
            .ToArray();
        var deliveredOrders = ordersInWindow.Where(order =>
                order.Value.State == ShopOrderState.Delivered &&
                VisibleByEnd(order.Value.UpdatedAt, order.BusinessDate))
            .ToArray();

        var runFacts = state.Runs.Values
            .Where(run => SeasonMatches(run.Value.SeasonId))
            .Select(run => new DatedRun(
                run.Value,
                ResolveRunBusinessDate(run.Value, state)))
            .Where(run => VersionMatches(run.Value.ContentVersionId))
            .ToArray();
        var runsInWindow = runFacts.Where(run =>
                DateMatches(run.Value.QuotedAt, run.BusinessDate))
            .ToArray();
        var settledRuns = runsInWindow.Where(run =>
                run.Value.State == ExtractionSettlementState.Settled &&
                run.Value.SettledAt is DateTimeOffset settledAt &&
                VisibleByEnd(settledAt, run.BusinessDate))
            .ToArray();
        var uncertainRuns = runsInWindow.Where(run =>
                run.Value.State == ExtractionSettlementState.Uncertain)
            .ToArray();

        var bindings = state.Bindings.Where(binding =>
                SeasonMatches(binding.SeasonId) &&
                query.ContentVersionId is null)
            .ToArray();
        var bindingsInWindow = bindings.Where(binding =>
                DateMatches(binding.LastVerifiedAt, null))
            .ToArray();

        var accountPopulation = new HashSet<Guid>();
        foreach (var fact in relevantAnalytics.Where(fact => VisibleByEnd(fact.OccurredAt, fact.BusinessDate)))
        {
            accountPopulation.Add(fact.AccountId);
        }
        foreach (var binding in bindings.Where(binding => VisibleByEnd(binding.FirstBoundAt, null)))
        {
            accountPopulation.Add(binding.AccountId);
        }
        foreach (var order in orderFacts.Where(order => VisibleByEnd(order.Value.CreatedAt, order.BusinessDate)))
        {
            accountPopulation.Add(order.Value.AccountId);
        }
        foreach (var run in runFacts.Where(run => VisibleByEnd(run.Value.QuotedAt, run.BusinessDate)))
        {
            accountPopulation.Add(run.Value.AccountId);
        }
        accountPopulation.RemoveWhere(accountId => !state.Accounts.ContainsKey(accountId));

        var loggedAccounts = analyticsInWindow
            .Where(fact => fact.Type == EconomyAnalyticsFactType.PortalSession)
            .Select(fact => fact.AccountId)
            .Concat(bindingsInWindow.Select(binding => binding.AccountId))
            .ToHashSet();
        var catalogAccounts = analyticsInWindow
            .Where(fact => fact.Type == EconomyAnalyticsFactType.CatalogView)
            .Select(fact => fact.AccountId)
            .ToHashSet();
        var orderAccounts = ordersInWindow.Select(order => order.Value.AccountId).ToHashSet();
        var deliveredAccounts = deliveredOrders.Select(order => order.Value.AccountId).ToHashSet();
        var quoteAccounts = runsInWindow.Select(run => run.Value.AccountId).ToHashSet();
        var settledAccounts = settledRuns.Select(run => run.Value.AccountId).ToHashSet();

        var funnel = new[]
        {
            Funnel("accounts", "账户", accountPopulation, accountPopulation.Count, false,
                "account + server-linked SQLite facts"),
            Funnel("authenticated", "已绑定或登录", loggedAccounts, loggedAccounts.Count, false,
                "player_identity_bindings + server-observed portal_session"),
            Funnel("catalog-viewed", "目录访问", catalogAccounts,
                analyticsInWindow.LongCount(fact => fact.Type == EconomyAnalyticsFactType.CatalogView), false,
                "unique server-observed catalog_view"),
            Funnel("order-created", "创建订单", orderAccounts, ordersInWindow.LongLength, false,
                "immutable shop order"),
            Funnel("order-delivered", "已送达", deliveredAccounts, deliveredOrders.LongLength, true,
                "final delivered order; refunded/failed excluded"),
            Funnel("resource-quoted", "资源报价", quoteAccounts, runsInWindow.LongLength, false,
                "persisted settlement run quote"),
            Funnel("resource-settled", "资源已结算", settledAccounts, settledRuns.LongLength, true,
                "final settled run; uncertain/failed excluded")
        };

        var alerts = new List<EconomyAnalyticsAlert>();
        var productMetrics = BuildProducts(
            query,
            state,
            analyticsInWindow,
            deliveredOrders,
            alerts);
        var zoneMetrics = BuildZones(runsInWindow);
        var exchangeCohort = quoteAccounts.Union(settledAccounts).ToHashSet();
        var exchangeSuppressed = IsSuppressed(exchangeCohort.Count);
        var exchange = new EconomyAnalyticsExchangeSummary(
            Count(quoteAccounts.Count),
            Count(settledAccounts.Count),
            Protected(runsInWindow.LongLength, exchangeCohort.Count),
            Protected(settledRuns.LongLength, exchangeCohort.Count),
            Protected(uncertainRuns.LongLength, exchangeCohort.Count),
            Protected(SumChecked(settledRuns.Select(run => run.Value.TotalValue)), exchangeCohort.Count),
            Rate(settledAccounts.Count, quoteAccounts.Count, settledAccounts.IsSubsetOf(quoteAccounts)));

        var ledgerProjection = BuildCurrencyHealth(
            query,
            state,
            accountPopulation,
            orderFacts,
            runFacts,
            DateMatches,
            VisibleByEnd);
        alerts.AddRange(ledgerProjection.Alerts);

        var uncertainOrderAccounts = ordersInWindow
            .Where(order => order.Value.State == ShopOrderState.DeliveryUncertain)
            .Select(order => order.Value.AccountId)
            .ToHashSet();
        var uncertainDeliveryIds = state.Deliveries.Values
            .Where(delivery => delivery.Value.State == ShopDeliveryState.Uncertain)
            .Select(delivery => delivery.Value.DeliveryId)
            .ToHashSet();
        var uncertainDeliveryOrders = ordersInWindow
            .Where(order => uncertainDeliveryIds.Contains(order.Value.DeliveryId))
            .ToArray();
        var uncertainDeliveries = uncertainDeliveryOrders.LongLength;
        var uncertainAccounts = uncertainOrderAccounts
            .Union(uncertainDeliveryOrders.Select(order => order.Value.AccountId))
            .Union(uncertainRuns.Select(run => run.Value.AccountId))
            .ToHashSet();
        var uncertainSuppressed = IsSuppressed(uncertainAccounts.Count);
        var uncertain = new EconomyAnalyticsUncertainHealth(
            Protected(uncertainOrderAccounts.Count, uncertainAccounts.Count),
            Protected(uncertainDeliveries, uncertainAccounts.Count),
            Protected(uncertainRuns.LongLength, uncertainAccounts.Count),
            uncertainSuppressed);
        if (uncertainOrderAccounts.Count > 0 || uncertainDeliveries > 0 || uncertainRuns.Length > 0)
        {
            alerts.Add(new EconomyAnalyticsAlert(
                "ECONOMY_UNCERTAIN_PRESENT",
                "critical",
                "存在单列的 uncertain 订单、发货或资源结算；不得计入成功并需人工对账。"));
        }
        if (exchangeSuppressed)
        {
            alerts.Add(new EconomyAnalyticsAlert(
                "SMALL_SAMPLE_SUPPRESSED",
                "info",
                $"少于 {MinimumCohortSize} 个账户的指标已隐藏。"));
        }

        var currentDate = query.DateBasis == EconomyAnalyticsDateBasis.Utc
            ? DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime)
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                _timeProvider.GetUtcNow(),
                _businessTimeZone).DateTime);
        var stableThrough = currentDate.AddDays(-1);
        var sourceComplete = !alerts.Any(alert => alert.Code is
            "CATALOG_DENOMINATOR_INCOMPLETE" or
            "LEGACY_BUSINESS_DATE_FALLBACK");
        var orderedProducts = productMetrics.OrderBy(metric => metric.Sku, StringComparer.Ordinal).ToArray();
        var orderedZones = zoneMetrics.OrderBy(metric => metric.ZoneId, StringComparer.Ordinal).ToArray();
        var productsPage = orderedProducts.Skip(query.Offset).Take(query.Limit).ToArray();
        var zonesPage = orderedZones.Skip(query.Offset).Take(query.Limit).ToArray();
        var hasNext = query.Offset + query.Limit < Math.Max(orderedProducts.Length, orderedZones.Length);
        var nextCursor = hasNext
            ? (query.Offset + query.Limit).ToString(CultureInfo.InvariantCulture)
            : null;
        var sourceHash = ComputeSourceHash(query, state.SourceFacts);
        return new EconomyAnalyticsReport(
            SchemaVersion,
            query.ServerId,
            query.SeasonId,
            query.ContentVersionId,
            new EconomyAnalyticsWindow(
                query.From,
                query.To,
                query.DateBasis == EconomyAnalyticsDateBasis.Utc ? "utc" : "business",
                query.To <= stableThrough,
                stableThrough,
                _businessTimeZone.Id),
            new EconomyAnalyticsPrivacy(
                MinimumCohortSize,
                "non-zero cohorts smaller than the minimum expose neither counts nor amounts",
                ContainsPlayerIdentifiers: false),
            new EconomyAnalyticsSource(
                "sqlite-authoritative-recomputation",
                SchemaVersion,
                state.AsOf,
                sourceHash,
                state.Tables.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                state.RowsRead,
                sourceComplete),
            funnel,
            productsPage,
            exchange,
            zonesPage,
            ledgerProjection.Currencies,
            uncertain,
            alerts.DistinctBy(alert => alert.Code).OrderBy(alert => alert.Code, StringComparer.Ordinal).ToArray(),
            new EconomyAnalyticsPage(
                query.Limit,
                query.Offset,
                orderedProducts.Length,
                orderedZones.Length,
                nextCursor));

        Guid? ResolveOrderVersion(ShopOrder order)
        {
            var versions = order.Lines.Select(line => line.ContentVersionId).Distinct().ToArray();
            if (versions.Length > 1)
            {
                throw InvalidSource("ANALYTICS_ORDER_VERSION_CONFLICT", "One order contains conflicting content versions.");
            }
            return versions.SingleOrDefault();
        }
    }

    private IReadOnlyList<EconomyAnalyticsProductMetric> BuildProducts(
        EconomyAnalyticsQuery query,
        AnalyticsState state,
        IReadOnlyCollection<AnalyticsEventRow> analyticsInWindow,
        IReadOnlyCollection<DatedOrder> deliveredOrders,
        List<EconomyAnalyticsAlert> alerts)
    {
        var views = analyticsInWindow
            .Where(fact => fact.Type == EconomyAnalyticsFactType.CatalogView)
            .ToArray();
        var skus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in views.Select(view => view.ContentVersionId).OfType<Guid>())
        {
            if (state.ProductsByVersion.TryGetValue(version, out var versionProducts))
            {
                skus.UnionWith(versionProducts.Keys);
            }
        }
        foreach (var line in deliveredOrders.SelectMany(order => order.Value.Lines))
        {
            skus.Add(line.Sku);
        }

        var results = new List<EconomyAnalyticsProductMetric>();
        foreach (var sku in skus.OrderBy(value => value, StringComparer.Ordinal))
        {
            var skuVersions = state.ProductsByVersion
                .Where(pair => pair.Value.ContainsKey(sku))
                .Select(pair => pair.Key)
                .ToHashSet();
            if (query.ContentVersionId is Guid selectedVersion)
            {
                skuVersions.IntersectWith([selectedVersion]);
            }
            var viewers = views
                .Where(view => view.ContentVersionId is Guid versionId && skuVersions.Contains(versionId))
                .Select(view => view.AccountId)
                .ToHashSet();
            var matchingLines = deliveredOrders.SelectMany(order => order.Value.Lines
                    .Where(line => string.Equals(line.Sku, sku, StringComparison.OrdinalIgnoreCase))
                    .Select(line => (order.Value.AccountId, line.Quantity)))
                .ToArray();
            var buyers = matchingLines.Select(line => line.AccountId).ToHashSet();
            var denominatorComplete = buyers.IsSubsetOf(viewers);
            if (!denominatorComplete)
            {
                alerts.Add(new EconomyAnalyticsAlert(
                    "CATALOG_DENOMINATOR_INCOMPLETE",
                    "warning",
                    "存在已送达购买但没有同版本唯一目录访问事实，购买率保持不可用。"));
            }
            var cohort = viewers.Union(buyers).ToHashSet();
            results.Add(new EconomyAnalyticsProductMetric(
                sku,
                Count(viewers.Count),
                Count(buyers.Count),
                Protected(SumChecked(matchingLines.Select(line => (long)line.Quantity)), cohort.Count),
                Rate(buyers.Count, viewers.Count, denominatorComplete)));
        }
        return results;
    }

    private static IReadOnlyList<EconomyAnalyticsZoneMetric> BuildZones(
        IReadOnlyCollection<DatedRun> runs)
    {
        var results = new List<EconomyAnalyticsZoneMetric>();
        foreach (var group in runs.GroupBy(run => run.Value.ZoneId, StringComparer.OrdinalIgnoreCase))
        {
            var accounts = group.Select(run => run.Value.AccountId).ToHashSet();
            var settled = group.Where(run => run.Value.State == ExtractionSettlementState.Settled).ToArray();
            var uncertain = group.LongCount(run => run.Value.State == ExtractionSettlementState.Uncertain);
            var suppressed = IsSuppressed(accounts.Count);
            results.Add(new EconomyAnalyticsZoneMetric(
                group.Key,
                Count(accounts.Count),
                Protected(group.LongCount(), accounts.Count),
                Protected(settled.LongLength, accounts.Count),
                Protected(uncertain, accounts.Count),
                Protected(SumChecked(settled.Select(run => run.Value.TotalValue)), accounts.Count),
                suppressed));
        }
        return results;
    }

    private static CurrencyProjection BuildCurrencyHealth(
        EconomyAnalyticsQuery query,
        AnalyticsState state,
        IReadOnlySet<Guid> accountPopulation,
        IReadOnlyCollection<DatedOrder> orders,
        IReadOnlyCollection<DatedRun> runs,
        Func<DateTimeOffset, DateOnly?, bool> dateMatches,
        Func<DateTimeOffset, DateOnly?, bool> visibleByEnd)
    {
        var orderReferences = orders.ToDictionary(
            order => order.Value.OrderId.ToString("N"),
            StringComparer.OrdinalIgnoreCase);
        var runReferences = runs.ToDictionary(
            run => run.Value.RunId.ToString("N"),
            StringComparer.OrdinalIgnoreCase);
        var alerts = new List<EconomyAnalyticsAlert>();
        var running = new Dictionary<string, long>(StringComparer.Ordinal);
        var endBalances = new Dictionary<string, (Guid AccountId, ExtractionCurrency Currency, long Balance)>();
        foreach (var ledger in state.Ledger.OrderBy(value => value.Sequence).ThenBy(value => value.Index))
        {
            var entry = ledger.Value;
            var key = WalletKey(entry.AccountId, entry.Currency, entry.SeasonId);
            long next;
            try
            {
                next = checked(running.GetValueOrDefault(key) + entry.Delta);
            }
            catch (OverflowException)
            {
                throw InvalidSource("ANALYTICS_LEDGER_OVERFLOW", "A wallet stream overflows Int64.");
            }
            running[key] = next;
            if (next != entry.BalanceAfter)
            {
                alerts.Add(new EconomyAnalyticsAlert(
                    "LEDGER_PROJECTION_MISMATCH",
                    "critical",
                    "账本 BalanceAfter 与按事件顺序重算结果不一致。"));
            }
            if (next < 0)
            {
                alerts.Add(new EconomyAnalyticsAlert(
                    "NEGATIVE_WALLET_BALANCE",
                    "critical",
                    "存在负数钱包余额，经济指标不得视为健康。"));
            }
            var association = LedgerAssociation(entry, orderReferences, runReferences);
            var businessDate = association.BusinessDate;
            if (visibleByEnd(entry.CreatedAt, businessDate) && accountPopulation.Contains(entry.AccountId) &&
                (query.SeasonId is null || entry.Currency == ExtractionCurrency.MarketCoin || entry.SeasonId == query.SeasonId))
            {
                endBalances[key] = (entry.AccountId, entry.Currency, next);
            }
        }

        var flows = state.Ledger.Where(ledger => accountPopulation.Contains(ledger.Value.AccountId))
            .Select(ledger => (ledger.Value, Association: LedgerAssociation(
                ledger.Value,
                orderReferences,
                runReferences)))
            .Where(item => dateMatches(item.Value.CreatedAt, item.Association.BusinessDate))
            .Where(item => query.SeasonId is null ||
                item.Value.SeasonId == query.SeasonId ||
                item.Association.SeasonId == query.SeasonId)
            .Where(item => query.ContentVersionId is null ||
                item.Association.ContentVersionId == query.ContentVersionId)
            .ToArray();
        var currencies = new List<EconomyAnalyticsCurrencyHealth>();
        foreach (var currency in Enum.GetValues<ExtractionCurrency>())
        {
            var currencyFlows = flows.Where(item => item.Value.Currency == currency).ToArray();
            var balances = endBalances.Values
                .Where(item => item.Currency == currency && accountPopulation.Contains(item.AccountId))
                .Select(item => item.Balance)
                .OrderBy(value => value)
                .ToArray();
            var accounts = currencyFlows.Select(item => item.Value.AccountId)
                .Concat(endBalances.Values.Where(item => item.Currency == currency).Select(item => item.AccountId))
                .ToHashSet();
            var suppressed = IsSuppressed(accounts.Count);
            var inflow = SumChecked(currencyFlows.Where(item => item.Value.Delta > 0).Select(item => item.Value.Delta));
            var outflow = SumChecked(currencyFlows.Where(item => item.Value.Delta < 0).Select(item => checked(-item.Value.Delta)));
            currencies.Add(new EconomyAnalyticsCurrencyHealth(
                CurrencyName(currency),
                Count(accounts.Count),
                Protected(inflow, accounts.Count),
                Protected(outflow, accounts.Count),
                Protected(checked(inflow - outflow), accounts.Count),
                Protected(Percentile(balances, 50), accounts.Count),
                Protected(Percentile(balances, 95), accounts.Count),
                Protected(balances.Length == 0 ? 0 : balances[0], accounts.Count),
                Protected(balances.Length == 0 ? 0 : balances[^1], accounts.Count),
                suppressed));
        }
        return new CurrencyProjection(currencies, alerts);
    }

    private static LedgerLink LedgerAssociation(
        WalletLedgerEntry ledger,
        IReadOnlyDictionary<string, DatedOrder> orders,
        IReadOnlyDictionary<string, DatedRun> runs)
    {
        if (ledger.ReferenceType is "shop_order" or "shop_refund" &&
            orders.TryGetValue(ledger.ReferenceId, out var order))
        {
            return new LedgerLink(order.Value.SeasonId, order.ContentVersionId, order.BusinessDate);
        }
        if (string.Equals(ledger.ReferenceType, "extraction_run", StringComparison.Ordinal) &&
            runs.TryGetValue(ledger.ReferenceId, out var run))
        {
            return new LedgerLink(run.Value.SeasonId, run.Value.ContentVersionId, run.BusinessDate);
        }
        return new LedgerLink(ledger.SeasonId, null, null);
    }

    private DateOnly ResolveOrderBusinessDate(ShopOrder order, AnalyticsState state)
    {
        var versionId = order.Lines.Select(line => line.ContentVersionId).Distinct().SingleOrDefault();
        if (versionId is Guid value)
        {
            if (!state.ContentVersions.TryGetValue(value, out var content))
            {
                throw InvalidSource(
                    "ANALYTICS_CONTENT_VERSION_MISSING",
                    "A versioned order references a missing immutable content version.");
            }
            return content.BusinessDate;
        }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(order.CreatedAt, _businessTimeZone).DateTime);
    }

    private DateOnly ResolveRunBusinessDate(ExtractionSettlementRun run, AnalyticsState state)
    {
        if (run.ContentBusinessDate is DateOnly date)
        {
            return date;
        }
        if (run.ContentVersionId is Guid versionId)
        {
            if (!state.ContentVersions.TryGetValue(versionId, out var content))
            {
                throw InvalidSource(
                    "ANALYTICS_CONTENT_VERSION_MISSING",
                    "A versioned settlement references a missing immutable content version.");
            }
            return content.BusinessDate;
        }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(run.QuotedAt, _businessTimeZone).DateTime);
    }

    private DateOnly DateFor(
        EconomyAnalyticsDateBasis basis,
        DateTimeOffset at,
        DateOnly? businessDate) =>
        basis == EconomyAnalyticsDateBasis.Utc
            ? DateOnly.FromDateTime(at.UtcDateTime)
            : businessDate ?? DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(at, _businessTimeZone).DateTime);

    private async Task<AnalyticsState> LoadStateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var state = new AnalyticsState();
        await LoadContentVersionsAsync(connection, state, cancellationToken);
        await LoadExtractionEventsAsync(connection, state, cancellationToken);
        await LoadRunsAsync(connection, state, cancellationToken);
        await LoadBindingsAsync(connection, state, cancellationToken);
        await LoadAnalyticsEventsAsync(connection, state, cancellationToken);
        return state;
    }

    private static async Task LoadContentVersionsAsync(
        SqliteConnection connection,
        AnalyticsState state,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "content_versions", cancellationToken))
        {
            return;
        }
        state.Tables.Add("content_versions");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version_id, server_id, version_number, business_date,
                   rules_version, content_hash, published_at
            FROM content_versions ORDER BY version_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var versionId = RequiredGuid(reader.GetString(0), "content version");
            var businessDate = RequiredDate(reader.GetString(3), "content business date");
            var publishedAt = RequiredTimestamp(reader.GetString(6), "content publication");
            var row = new ContentVersionFact(
                versionId,
                RequiredBounded(reader.GetString(1), 64, "content server"),
                reader.GetInt64(2),
                businessDate,
                RequiredBounded(reader.GetString(4), 128, "content rules"),
                RequiredSha256(reader.GetString(5), "content hash"),
                publishedAt);
            if (!state.ContentVersions.TryAdd(versionId, row))
            {
                throw InvalidSource("ANALYTICS_DUPLICATE_CONTENT_VERSION", "A content version id is duplicated.");
            }
            state.AddSource("content", string.Join('|',
                versionId.ToString("N"), row.ServerId.ToLowerInvariant(), row.VersionNumber,
                row.BusinessDate, row.RulesVersion, row.ContentHash, row.PublishedAt.ToString("O")),
                publishedAt);
        }
    }

    private static async Task LoadExtractionEventsAsync(
        SqliteConnection connection,
        AnalyticsState state,
        CancellationToken cancellationToken)
    {
        state.Tables.Add("extraction_events");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, event_id, event_type, occurred_at, payload
            FROM extraction_events ORDER BY sequence;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sequence = reader.GetInt64(0);
            var sqlEventId = RequiredGuid(reader.GetString(1), "event id");
            var sqlEventType = RequiredBounded(reader.GetString(2), 128, "event type");
            var sqlOccurredAt = RequiredTimestamp(reader.GetString(3), "event timestamp");
            var payload = reader.GetString(4);
            ExtractionEventEnvelope envelope;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload);
                envelope = JsonSerializer.Deserialize<ExtractionEventEnvelope>(payload, JsonOptions)
                    ?? throw new InvalidDataException();
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                throw InvalidSource("ANALYTICS_EVENT_PAYLOAD_INVALID", "An extraction event payload is invalid.");
            }
            using (document)
            {
                if (envelope.SchemaVersion != 1 || envelope.EventId != sqlEventId ||
                    !string.Equals(envelope.EventType, sqlEventType, StringComparison.Ordinal) ||
                    envelope.At != sqlOccurredAt)
                {
                    throw InvalidSource(
                        "ANALYTICS_EVENT_ENVELOPE_MISMATCH",
                        "An extraction event envelope differs from its immutable SQL columns.");
                }
                state.AddSource(
                    "event",
                    $"{sequence}|{CanonicalJson(document.RootElement)}",
                    sqlOccurredAt);
            }
            if (envelope.Season is { } season)
            {
                state.Seasons[season.SeasonId] = season;
            }
            if (envelope.Account is { } account)
            {
                state.Accounts[account.AccountId] = account;
            }
            if (envelope.Product is { } product && product.ContentVersionId is Guid contentVersionId)
            {
                if (!state.ProductsByVersion.TryGetValue(contentVersionId, out var products))
                {
                    products = new Dictionary<string, ShopProduct>(StringComparer.OrdinalIgnoreCase);
                    state.ProductsByVersion[contentVersionId] = products;
                }
                products[product.Sku] = product;
            }
            if (envelope.Order is { } order)
            {
                state.Orders[order.OrderId] = new Sequenced<ShopOrder>(order, sequence, 0);
            }
            if (envelope.Delivery is { } delivery)
            {
                state.Deliveries[delivery.DeliveryId] = new Sequenced<ShopDelivery>(delivery, sequence, 0);
            }
            if (envelope.LedgerEntries is { } ledgerEntries)
            {
                for (var index = 0; index < ledgerEntries.Count; index++)
                {
                    var ledger = ledgerEntries[index];
                    if (!state.LedgerIds.Add(ledger.EntryId))
                    {
                        throw InvalidSource(
                            "ANALYTICS_LEDGER_DUPLICATE",
                            "A wallet ledger entry appears more than once.");
                    }
                    state.Ledger.Add(new Sequenced<WalletLedgerEntry>(ledger, sequence, index));
                }
            }
        }
    }

    private static async Task LoadRunsAsync(
        SqliteConnection connection,
        AnalyticsState state,
        CancellationToken cancellationToken)
    {
        state.Tables.Add("extraction_settlement_runs");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, account_id, season_id, state, revision, updated_at, payload
            FROM extraction_settlement_runs ORDER BY run_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var runId = RequiredGuid(reader.GetString(0), "run id");
            ExtractionSettlementRun run;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(reader.GetString(6));
                run = JsonSerializer.Deserialize<ExtractionSettlementRun>(
                    document.RootElement.GetRawText(), JsonOptions)
                    ?? throw new InvalidDataException();
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                throw InvalidSource("ANALYTICS_RUN_PAYLOAD_INVALID", "A settlement run payload is invalid.");
            }
            using (document)
            {
                var updatedAt = RequiredTimestamp(reader.GetString(5), "run update");
                if (run.RunId != runId ||
                    run.AccountId != RequiredGuid(reader.GetString(1), "run account") ||
                    run.SeasonId != RequiredGuid(reader.GetString(2), "run season") ||
                    !string.Equals(run.State.ToString(), reader.GetString(3), StringComparison.Ordinal) ||
                    run.Revision != reader.GetInt64(4) || run.UpdatedAt != updatedAt)
                {
                    throw InvalidSource(
                        "ANALYTICS_RUN_ENVELOPE_MISMATCH",
                        "A settlement run payload differs from its immutable SQL columns.");
                }
                state.AddSource("run", $"{runId:N}|{CanonicalJson(document.RootElement)}", updatedAt);
            }
            if (!state.Runs.TryAdd(runId, new Sequenced<ExtractionSettlementRun>(run, 0, 0)))
            {
                throw InvalidSource("ANALYTICS_RUN_DUPLICATE", "A settlement run id is duplicated.");
            }
        }
    }

    private static async Task LoadBindingsAsync(
        SqliteConnection connection,
        AnalyticsState state,
        CancellationToken cancellationToken)
    {
        state.Tables.Add("player_identity_bindings");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT binding_id, season_id, account_id, first_bound_at, last_verified_at
            FROM player_identity_bindings ORDER BY binding_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var binding = new BindingFact(
                RequiredGuid(reader.GetString(0), "binding id"),
                RequiredGuid(reader.GetString(1), "binding season"),
                RequiredGuid(reader.GetString(2), "binding account"),
                RequiredTimestamp(reader.GetString(3), "binding creation"),
                RequiredTimestamp(reader.GetString(4), "binding verification"));
            if (binding.LastVerifiedAt < binding.FirstBoundAt)
            {
                throw InvalidSource("ANALYTICS_BINDING_TIME_INVALID", "A binding verification predates creation.");
            }
            state.Bindings.Add(binding);
            state.AddSource("binding", string.Join('|',
                binding.BindingId.ToString("N"), binding.SeasonId.ToString("N"),
                binding.AccountId.ToString("N"), binding.FirstBoundAt.ToString("O"),
                binding.LastVerifiedAt.ToString("O")), binding.LastVerifiedAt);
        }
    }

    private static async Task LoadAnalyticsEventsAsync(
        SqliteConnection connection,
        AnalyticsState state,
        CancellationToken cancellationToken)
    {
        state.Tables.Add("economy_analytics_events");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, event_key, event_type, account_id, server_id,
                   season_id, content_version_id, business_date, occurred_at, source
            FROM economy_analytics_events ORDER BY event_key;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventId = RequiredGuid(reader.GetString(0), "analytics event id");
            var eventKey = RequiredSha256(reader.GetString(1), "analytics event key");
            var eventType = ParseFactType(reader.GetString(2));
            var accountId = RequiredGuid(reader.GetString(3), "analytics account");
            var serverId = RequiredBounded(reader.GetString(4), 64, "analytics server");
            var seasonId = RequiredGuid(reader.GetString(5), "analytics season");
            Guid? contentVersionId = reader.IsDBNull(6)
                ? null
                : RequiredGuid(reader.GetString(6), "analytics content version");
            var businessDate = RequiredDate(reader.GetString(7), "analytics business date");
            var occurredAt = RequiredTimestamp(reader.GetString(8), "analytics timestamp");
            if (!string.Equals(reader.GetString(9), "server-observed", StringComparison.Ordinal))
            {
                throw InvalidSource(
                    "ANALYTICS_UNTRUSTED_SOURCE",
                    "An analytics event was not produced by the server-observed source.");
            }
            var fact = new AnalyticsEventRow(
                eventId,
                eventKey,
                eventType,
                accountId,
                serverId,
                seasonId,
                contentVersionId,
                businessDate,
                occurredAt);
            state.AnalyticsEvents.Add(fact);
            state.AddSource("analytics", string.Join('|',
                eventId.ToString("N"), eventKey, FactType(eventType), accountId.ToString("N"),
                serverId.ToLowerInvariant(), seasonId.ToString("N"),
                contentVersionId?.ToString("N") ?? "none", businessDate, occurredAt.ToString("O")),
                occurredAt);
        }
    }

    private void Initialize()
    {
        using var connection = Open(readOnly: false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS economy_schema_migrations (
                component TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                applied_at TEXT NOT NULL,
                PRIMARY KEY (component, version)
            );
            CREATE TABLE IF NOT EXISTS economy_analytics_events (
                event_id TEXT PRIMARY KEY,
                event_key TEXT NOT NULL UNIQUE CHECK (length(event_key) = 64),
                event_type TEXT NOT NULL CHECK (event_type IN ('portal_session', 'catalog_view')),
                account_id TEXT NOT NULL,
                server_id TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                content_version_id TEXT NULL,
                business_date TEXT NOT NULL CHECK (length(business_date) = 10),
                occurred_at TEXT NOT NULL,
                source TEXT NOT NULL CHECK (source = 'server-observed')
            );
            CREATE INDEX IF NOT EXISTS ix_economy_analytics_slice
                ON economy_analytics_events (
                    server_id, business_date, season_id, content_version_id, event_type);
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('economy-analytics', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
    }

    private async Task EnsureHealthyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            await using var reader = await integrity.ExecuteReaderAsync(cancellationToken);
            var rows = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(reader.GetString(0));
            }
            if (rows.Count != 1 || !string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidSource("ANALYTICS_SQLITE_INTEGRITY_FAILED", "SQLite integrity_check failed.");
            }
        }
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw InvalidSource("ANALYTICS_SQLITE_FOREIGN_KEY_FAILED", "SQLite foreign_key_check failed.");
            }
        }
        foreach (var table in new[]
                 {
                     "extraction_events",
                     "extraction_settlement_runs",
                     "player_identity_bindings",
                     "economy_analytics_events"
                 })
        {
            if (!await TableExistsAsync(connection, table, cancellationToken))
            {
                throw InvalidSource("ANALYTICS_REQUIRED_TABLE_MISSING", "A required analytics source table is missing.");
            }
        }
    }

    private SqliteConnection Open(bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString)
        {
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = readOnly ? SqliteCacheMode.Private : SqliteCacheMode.Shared,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void ValidateQuery(EconomyAnalyticsQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.ServerId) || query.ServerId.Trim().Length > 64)
        {
            throw new EconomyAnalyticsException(
                "ANALYTICS_SERVER_INVALID",
                "serverId must contain 1 to 64 characters.",
                StatusCodes.Status400BadRequest);
        }
        if (query.From > query.To || query.To.DayNumber - query.From.DayNumber > 92)
        {
            throw new EconomyAnalyticsException(
                "ANALYTICS_WINDOW_INVALID",
                "The analytics window must be ordered and no longer than 93 days.",
                StatusCodes.Status400BadRequest);
        }
        if (query.Limit is < 1 or > 100 || query.Offset < 0 || query.Offset > 100_000)
        {
            throw new EconomyAnalyticsException(
                "ANALYTICS_PAGE_INVALID",
                "limit must be 1 to 100 and cursor must be a bounded non-negative offset.",
                StatusCodes.Status400BadRequest);
        }
        if (query.SeasonId == Guid.Empty || query.ContentVersionId == Guid.Empty)
        {
            throw new EconomyAnalyticsException(
                "ANALYTICS_FILTER_INVALID",
                "Season and content version filters cannot be empty identifiers.",
                StatusCodes.Status400BadRequest);
        }
    }

    private static EconomyAnalyticsFunnelStage Funnel(
        string key,
        string label,
        IReadOnlySet<Guid> accounts,
        long facts,
        bool successOnly,
        string source) =>
        new(
            key,
            label,
            Count(accounts.Count),
            Protected(facts, accounts.Count),
            successOnly,
            source);

    private static EconomyAnalyticsCount Count(long value) =>
        new(IsSuppressed(value) ? null : value, IsSuppressed(value));

    private static long? Protected(long value, long cohortSize) =>
        IsSuppressed(cohortSize) && value != 0 ? null : value;

    private static bool IsSuppressed(long value) => value is > 0 and < MinimumCohortSize;

    private static EconomyAnalyticsRate Rate(
        long numerator,
        long denominator,
        bool denominatorComplete)
    {
        var cohort = Math.Max(numerator, denominator);
        var suppressed = IsSuppressed(cohort);
        int? basisPoints = null;
        if (!suppressed && denominatorComplete && denominator > 0)
        {
            basisPoints = checked((int)Math.Min(10_000, numerator * 10_000L / denominator));
        }
        return new EconomyAnalyticsRate(
            suppressed ? null : numerator,
            suppressed ? null : denominator,
            basisPoints,
            suppressed,
            denominatorComplete);
    }

    private static long SumChecked(IEnumerable<long> values)
    {
        long sum = 0;
        try
        {
            foreach (var value in values)
            {
                sum = checked(sum + value);
            }
            return sum;
        }
        catch (OverflowException)
        {
            throw InvalidSource("ANALYTICS_SUM_OVERFLOW", "An analytics aggregate overflows Int64.");
        }
    }

    private static long Percentile(IReadOnlyList<long> sorted, int percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }
        var rank = (int)Math.Ceiling(percentile / 100d * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static string CurrencyName(ExtractionCurrency currency) => currency switch
    {
        ExtractionCurrency.MarketCoin => "merchantCoin",
        ExtractionCurrency.SeasonVoucher => "weeklyTicket",
        _ => throw InvalidSource("ANALYTICS_CURRENCY_INVALID", "A wallet currency is unsupported.")
    };

    private static string WalletKey(Guid accountId, ExtractionCurrency currency, Guid? seasonId) =>
        $"{accountId:N}|{currency}|{seasonId?.ToString("N") ?? "permanent"}";

    private static string FactType(EconomyAnalyticsFactType type) => type switch
    {
        EconomyAnalyticsFactType.PortalSession => "portal_session",
        EconomyAnalyticsFactType.CatalogView => "catalog_view",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static EconomyAnalyticsFactType ParseFactType(string value) => value switch
    {
        "portal_session" => EconomyAnalyticsFactType.PortalSession,
        "catalog_view" => EconomyAnalyticsFactType.CatalogView,
        _ => throw InvalidSource("ANALYTICS_EVENT_TYPE_INVALID", "An analytics event type is unsupported.")
    };

    private static string ComputeSourceHash(
        EconomyAnalyticsQuery query,
        IReadOnlyCollection<string> facts)
    {
        var queryIdentity = string.Join('|',
            query.ServerId.ToLowerInvariant(), query.From, query.To, query.DateBasis,
            query.SeasonId?.ToString("N") ?? "all", query.ContentVersionId?.ToString("N") ?? "all");
        return Sha256(
            "pal-control-economy-analytics-recomputation-v1\n" + queryIdentity + "\n" +
            string.Join('\n', facts.OrderBy(value => value, StringComparer.Ordinal)));
    }

    private static string CanonicalJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteCanonical(writer, element);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                {
                    throw InvalidSource("ANALYTICS_JSON_DUPLICATE_PROPERTY", "Canonical JSON contains duplicate properties.");
                }
                foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw InvalidSource("ANALYTICS_JSON_TOKEN_INVALID", "Canonical JSON contains an unsupported token.");
        }
    }

    private static Guid RequiredGuid(string value, string field) =>
        Guid.TryParse(value, out var result) && result != Guid.Empty
            ? result
            : throw InvalidSource("ANALYTICS_IDENTIFIER_INVALID", $"A {field} value is invalid.");

    private static DateTimeOffset RequiredTimestamp(string value, string field) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result
            : throw InvalidSource("ANALYTICS_TIMESTAMP_INVALID", $"A {field} value is invalid.");

    private static DateOnly RequiredDate(string value, string field) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : throw InvalidSource("ANALYTICS_DATE_INVALID", $"A {field} value is invalid.");

    private static string RequiredBounded(string value, int maximum, string field) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximum
            ? value.Trim()
            : throw InvalidSource("ANALYTICS_TEXT_INVALID", $"A {field} value is invalid.");

    private static string RequiredSha256(string value, string field) =>
        value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : throw InvalidSource("ANALYTICS_HASH_INVALID", $"A {field} value is invalid.");

    private static EconomyAnalyticsException InvalidSource(string code, string message) =>
        new(code, message, StatusCodes.Status409Conflict);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM sqlite_master
            WHERE type = 'table' AND name = $table LIMIT 1;
            """;
        command.Parameters.AddWithValue("$table", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));

    private sealed class AnalyticsState
    {
        public Dictionary<Guid, ExtractionSeason> Seasons { get; } = [];
        public Dictionary<Guid, ExtractionAccount> Accounts { get; } = [];
        public Dictionary<Guid, Sequenced<ShopOrder>> Orders { get; } = [];
        public Dictionary<Guid, Sequenced<ShopDelivery>> Deliveries { get; } = [];
        public List<Sequenced<WalletLedgerEntry>> Ledger { get; } = [];
        public HashSet<Guid> LedgerIds { get; } = [];
        public Dictionary<Guid, Sequenced<ExtractionSettlementRun>> Runs { get; } = [];
        public List<BindingFact> Bindings { get; } = [];
        public List<AnalyticsEventRow> AnalyticsEvents { get; } = [];
        public Dictionary<Guid, ContentVersionFact> ContentVersions { get; } = [];
        public Dictionary<Guid, Dictionary<string, ShopProduct>> ProductsByVersion { get; } = [];
        public HashSet<string> Tables { get; } = [];
        public List<string> SourceFacts { get; } = [];
        public long RowsRead { get; private set; }
        public DateTimeOffset AsOf { get; private set; } = DateTimeOffset.UnixEpoch;

        public void AddSource(string kind, string value, DateTimeOffset at)
        {
            SourceFacts.Add($"{kind}|{value}");
            RowsRead++;
            if (at > AsOf)
            {
                AsOf = at;
            }
        }
    }

    private sealed class ExtractionEventEnvelope
    {
        public int SchemaVersion { get; init; }
        public Guid EventId { get; init; }
        public string EventType { get; init; } = string.Empty;
        public DateTimeOffset At { get; init; }
        public ExtractionSeason? Season { get; init; }
        public ExtractionAccount? Account { get; init; }
        public IReadOnlyList<WalletLedgerEntry>? LedgerEntries { get; init; }
        public ShopProduct? Product { get; init; }
        public ShopOrder? Order { get; init; }
        public ShopDelivery? Delivery { get; init; }
    }

    private sealed record Sequenced<T>(T Value, long Sequence, int Index);
    private sealed record ContentVersionFact(
        Guid VersionId,
        string ServerId,
        long VersionNumber,
        DateOnly BusinessDate,
        string RulesVersion,
        string ContentHash,
        DateTimeOffset PublishedAt);
    private sealed record BindingFact(
        Guid BindingId,
        Guid SeasonId,
        Guid AccountId,
        DateTimeOffset FirstBoundAt,
        DateTimeOffset LastVerifiedAt);
    private sealed record AnalyticsEventRow(
        Guid EventId,
        string EventKey,
        EconomyAnalyticsFactType Type,
        Guid AccountId,
        string ServerId,
        Guid SeasonId,
        Guid? ContentVersionId,
        DateOnly BusinessDate,
        DateTimeOffset OccurredAt);
    private sealed record DatedOrder(ShopOrder Value, Guid? ContentVersionId, DateOnly BusinessDate);
    private sealed record DatedRun(ExtractionSettlementRun Value, DateOnly BusinessDate);
    private sealed record LedgerLink(Guid? SeasonId, Guid? ContentVersionId, DateOnly? BusinessDate);
    private sealed record CurrencyProjection(
        IReadOnlyList<EconomyAnalyticsCurrencyHealth> Currencies,
        IReadOnlyList<EconomyAnalyticsAlert> Alerts);
}

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var root = Path.Combine(Path.GetTempPath(), $"pal-control-economy-analytics-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
var database = Path.Combine(root, "extraction-commerce.db");
try
{
    Guid firstAccountId;
    const string serverId = "analytics-server";
    var seasonId = Guid.NewGuid();
    var contentVersionId = Guid.NewGuid();
    var contentHash = new string('c', 64);
    await using (var repository = new SqliteExtractionRepository(root, clock))
    {
        var analytics = new EconomyAnalyticsStore(root, TimeZoneInfo.Utc, clock);
        var season = await repository.UpsertSeasonAsync(
            seasonId,
            new ExtractionSeasonDefinition(
                serverId,
                "analytics-week",
                "Analytics Week",
                "0123456789abcdef0123456789abcdef",
                clock.GetUtcNow().AddDays(-1),
                clock.GetUtcNow().AddDays(6),
                ExtractionSeasonState.Active),
            null,
            CancellationToken.None);
        CreateContentVersion(database, contentVersionId, serverId, contentHash, clock.GetUtcNow());
        _ = await repository.UpsertProductAsync(
            new ShopProductDefinition(
                "ANALYTICS-KIT",
                "Analytics Kit",
                "Deterministic fixture product",
                ExtractionCurrency.MarketCoin,
                100,
                [new ShopItemGrant("Leather", 1)],
                null,
                true,
                null,
                null,
                ContentVersionId: contentVersionId,
                ContentHash: contentHash),
            null,
            "fixture",
            CancellationToken.None);

        var accounts = new List<ExtractionAccount>();
        for (var index = 0; index < 120; index++)
        {
            var externalId = $"76561198{index:D9}";
            var account = await repository.GetOrCreateAccountAsync(
                "steam",
                externalId,
                $"Analytics Player {index:D3}",
                CancellationToken.None);
            accounts.Add(account);
            _ = await repository.AdjustWalletAsync(
                new WalletAdjustmentRequest(
                    account.AccountId,
                    null,
                    ExtractionCurrency.MarketCoin,
                    1_000,
                    "fixture funding",
                    "fixture",
                    $"fund-market-{index:D3}",
                    "fixture",
                    $"analytics-market-{index:D3}"),
                CancellationToken.None);
            _ = await repository.AdjustWalletAsync(
                new WalletAdjustmentRequest(
                    account.AccountId,
                    season.SeasonId,
                    ExtractionCurrency.SeasonVoucher,
                    50,
                    "fixture weekly funding",
                    "fixture",
                    $"fund-weekly-{index:D3}",
                    "fixture",
                    $"analytics-weekly-{index:D3}"),
                CancellationToken.None);
            await analytics.RecordPortalSessionAsync(
                account.AccountId,
                serverId,
                season.SeasonId,
                contentVersionId,
                new DateOnly(2026, 7, 15),
                CancellationToken.None);
            await analytics.RecordCatalogViewAsync(
                account.AccountId,
                serverId,
                season.SeasonId,
                contentVersionId,
                new DateOnly(2026, 7, 15),
                CancellationToken.None);
            // A refresh/retry must not increase the denominator.
            await analytics.RecordCatalogViewAsync(
                account.AccountId,
                serverId,
                season.SeasonId,
                contentVersionId,
                new DateOnly(2026, 7, 15),
                CancellationToken.None);
        }
        firstAccountId = accounts[0].AccountId;

        for (var index = 0; index < 80; index++)
        {
            var purchase = await repository.PurchaseAsync(
                new ShopPurchaseRequest(
                    accounts[index].AccountId,
                    season.SeasonId,
                    serverId,
                    $"fixture-player-{index:D3}",
                    [new ShopPurchaseLineInput("ANALYTICS-KIT", 1)],
                    $"analytics-purchase-{index:D3}",
                    "fixture",
                    "analytics fixture",
                    ExpectedContentVersionId: contentVersionId,
                    ExpectedContentHash: contentHash),
                CancellationToken.None);
            Assert(purchase.Created && purchase.Order is not null && purchase.Delivery is not null,
                $"Purchase fixture {index} was not created.");
            var delivery = purchase.Delivery!;
            var dispatch = await repository.MarkDeliveryDispatchedAsync(
                delivery.DeliveryId,
                Guid.NewGuid(),
                CancellationToken.None);
            Assert(dispatch.Updated, $"Delivery fixture {index} was not dispatched.");
            if (index < 60)
            {
                var delivered = await repository.MarkDeliveryOutcomeAsync(
                    delivery.DeliveryId,
                    ShopDeliveryState.Delivered,
                    null,
                    null,
                    CancellationToken.None);
                Assert(delivered.Updated, $"Delivery fixture {index} was not delivered.");
            }
            else if (index < 70)
            {
                var failed = await repository.MarkDeliveryOutcomeAsync(
                    delivery.DeliveryId,
                    ShopDeliveryState.Failed,
                    "FIXTURE_FAILED",
                    "fixture failed before mutation",
                    CancellationToken.None);
                Assert(failed.Updated, $"Delivery fixture {index} was not failed.");
                var refund = await repository.RefundFailedOrderAsync(
                    purchase.Order!.OrderId,
                    $"analytics-refund-{index:D3}",
                    "fixture",
                    "verified failure",
                    CancellationToken.None);
                Assert(refund.Created && refund.Order?.State == ShopOrderState.Refunded,
                    $"Order fixture {index} was not refunded.");
            }
            else
            {
                var uncertain = await repository.MarkDeliveryOutcomeAsync(
                    delivery.DeliveryId,
                    ShopDeliveryState.Uncertain,
                    "FIXTURE_UNCERTAIN",
                    "fixture requires reconciliation",
                    CancellationToken.None);
                Assert(uncertain.Updated, $"Delivery fixture {index} was not uncertain.");
            }
        }

        var runs = new List<ExtractionSettlementRun>();
        for (var index = 0; index < 100; index++)
        {
            var state = index < 70
                ? ExtractionSettlementState.Settled
                : index < 80
                    ? ExtractionSettlementState.Uncertain
                    : ExtractionSettlementState.Failed;
            var now = clock.GetUtcNow();
            runs.Add(new ExtractionSettlementRun(
                Guid.NewGuid(),
                accounts[index].AccountId,
                season.SeasonId,
                $"fixture-player-{index:D3}",
                $"zone-{index % 3:D2}",
                $"Zone {index % 3:D2}",
                state,
                [new ExtractionLootLine("Stone", "Stone", 5, 10, 50)],
                5,
                50,
                new string('a', 64),
                null,
                $"analytics-settle-{index:D3}",
                state == ExtractionSettlementState.Uncertain ? "FIXTURE_UNCERTAIN" : null,
                null,
                now,
                now.AddSeconds(30),
                now,
                state == ExtractionSettlementState.Settled ? now : null)
            {
                Revision = 2,
                StateChangedAt = now,
                ContentVersionId = contentVersionId,
                ContentHash = contentHash,
                ContentBusinessDate = new DateOnly(2026, 7, 15),
                ContentRulesVersion = "analytics-v1",
                ZoneYieldMultiplierBasisPoints = 10_000
            });
        }
        await repository.PersistSettlementRunWritesAsync(
            runs.Select(ExtractionSettlementRunWrite.Insert).ToArray(),
            CancellationToken.None);

        clock.SetUtcNow(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var query = new EconomyAnalyticsQuery(
            serverId,
            new DateOnly(2026, 7, 15),
            new DateOnly(2026, 7, 15),
            EconomyAnalyticsDateBasis.Business,
            null,
            null,
            1,
            0);
        var report = await analytics.QueryAsync(query);
        Assert(report.Window.Stable, "The completed business-day window was not stable.");
        Assert(report.Source.RecomputationHash.Length == 64 && report.Source.Complete,
            "The authoritative source hash/completeness evidence is missing.");
        Assert(Stage(report, "accounts") == 120 &&
               Stage(report, "authenticated") == 120 &&
               Stage(report, "catalog-viewed") == 120 &&
               Stage(report, "order-created") == 80 &&
               Stage(report, "order-delivered") == 60 &&
               Stage(report, "resource-quoted") == 100 &&
               Stage(report, "resource-settled") == 70,
            "The deterministic funnel does not exclude failed/refunded/uncertain outcomes.");
        Assert(report.Products.Count == 1 &&
               report.Products[0].PurchaseRate.BasisPoints == 5_000 &&
               report.Products[0].DeliveredQuantity == 60,
            "The product delivered-purchase rate is incorrect.");
        Assert(report.ResourceExchange.ConversionRate.BasisPoints == 7_000 &&
               report.ResourceExchange.UncertainRuns == 10,
            "The resource conversion/uncertain split is incorrect.");
        Assert(report.Zones.Count == 1 && report.Page.TotalZones == 3 && report.Page.NextCursor == "1",
            "The deterministic dimension pagination contract is incorrect.");
        Assert(report.Uncertain.Orders == 10 &&
               report.Uncertain.Deliveries == 10 &&
               report.Uncertain.ResourceSettlements == 10,
            "Uncertain outcomes were not kept in their own health bucket.");
        Assert(report.Currencies.Single(item => item.Currency == "merchantCoin").Accounts.Value == 120 &&
               report.Currencies.Single(item => item.Currency == "weeklyTicket").Inflow == 6_000,
            "Dual-currency flow and balance health is incomplete.");
        var filtered = await analytics.QueryAsync(query with
        {
            SeasonId = season.SeasonId,
            ContentVersionId = contentVersionId,
            Limit = 100
        });
        Assert(Stage(filtered, "order-delivered") == 60 && Stage(filtered, "resource-settled") == 70,
            "Season/content-version slicing changed authoritative success facts.");
        Assert(ScalarLong(database, "SELECT COUNT(*) FROM economy_analytics_events;") == 240,
            "Duplicate refreshes changed a unique daily analytics denominator.");

        var secondPage = await analytics.QueryAsync(query with { Offset = 1 });
        Assert(secondPage.Page.Offset == 1 && secondPage.Zones[0].ZoneId == "zone-01" &&
               secondPage.Source.RecomputationHash == report.Source.RecomputationHash,
            "Pagination changed the recomputable source identity.");

        var restarted = new EconomyAnalyticsStore(root, TimeZoneInfo.Utc, clock);
        var restartedReport = await restarted.QueryAsync(query);
        Assert(restartedReport.Source.RecomputationHash == report.Source.RecomputationHash &&
               Stage(restartedReport, "order-delivered") == 60,
            "Analytics facts or recomputation changed after service restart.");

        var json = JsonSerializer.Serialize(report);
        Assert(!json.Contains(firstAccountId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
               !json.Contains("76561198", StringComparison.Ordinal) &&
               !json.Contains("Analytics Player", StringComparison.Ordinal),
            "The aggregate report leaked an account/platform/display identity.");

        clock.SetUtcNow(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        for (var index = 0; index < 3; index++)
        {
            await analytics.RecordCatalogViewAsync(
                accounts[index].AccountId,
                serverId,
                season.SeasonId,
                contentVersionId,
                new DateOnly(2026, 7, 16),
                CancellationToken.None);
        }
        var small = await analytics.QueryAsync(query with
        {
            From = new DateOnly(2026, 7, 16),
            To = new DateOnly(2026, 7, 16),
            Limit = 100
        });
        var smallCatalog = small.Funnel.Single(stage => stage.Key == "catalog-viewed");
        Assert(!small.Window.Stable && smallCatalog.Accounts is { Suppressed: true, Value: null },
            "Current-window stability or small-cohort suppression is incorrect.");

        await AssertThrowsAsync(
            () => analytics.QueryAsync(query with { To = query.From.AddDays(93) }),
            "ANALYTICS_WINDOW_INVALID");
        await AssertThrowsAsync(
            () => analytics.QueryAsync(query with { Offset = -1 }),
            "ANALYTICS_PAGE_INVALID");
    }

    var corruptRoot = Path.Combine(root, "corrupt");
    Directory.CreateDirectory(corruptRoot);
    BackupDatabase(database, Path.Combine(corruptRoot, "extraction-commerce.db"));
    Execute(Path.Combine(corruptRoot, "extraction-commerce.db"),
        "UPDATE extraction_events SET payload = '{' WHERE sequence = 1;");
    var corrupt = new EconomyAnalyticsStore(corruptRoot, TimeZoneInfo.Utc, clock);
    await AssertThrowsAsync(
        () => corrupt.QueryAsync(new EconomyAnalyticsQuery(
            serverId,
            new DateOnly(2026, 7, 15),
            new DateOnly(2026, 7, 15),
            EconomyAnalyticsDateBasis.Business,
            seasonId,
            contentVersionId,
            50,
            0)),
        "ANALYTICS_EVENT_PAYLOAD_INVALID");

    Console.WriteLine(
        "PASS: 120-account authoritative analytics funnel, rates, zone/currency health, unique denominators, pagination, restart, privacy, suppression and fail-closed corruption.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static long? Stage(EconomyAnalyticsReport report, string key) =>
    report.Funnel.Single(stage => stage.Key == key).Accounts.Value;

static void CreateContentVersion(
    string database,
    Guid versionId,
    string serverId,
    string contentHash,
    DateTimeOffset publishedAt)
{
    using var connection = Open(database);
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS content_versions (
            version_id TEXT PRIMARY KEY,
            server_id TEXT NOT NULL,
            version_number INTEGER NOT NULL,
            business_date TEXT NOT NULL,
            rules_version TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            document_json TEXT NOT NULL,
            source_draft_id TEXT NOT NULL,
            published_by TEXT NOT NULL,
            published_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS content_current (
            server_id TEXT PRIMARY KEY,
            version_id TEXT NOT NULL,
            updated_at TEXT NOT NULL);
        INSERT INTO content_versions (
            version_id, server_id, version_number, business_date, rules_version,
            content_hash, document_json, source_draft_id, published_by, published_at)
        VALUES (
            $versionId, $serverId, 1, '2026-07-15', 'analytics-v1',
            $contentHash, '{}', $draftId, 'fixture', $publishedAt);
        INSERT INTO content_current (server_id, version_id, updated_at)
        VALUES ($serverId, $versionId, $publishedAt);
        """;
    command.Parameters.AddWithValue("$versionId", versionId.ToString("D"));
    command.Parameters.AddWithValue("$serverId", serverId);
    command.Parameters.AddWithValue("$contentHash", contentHash);
    command.Parameters.AddWithValue("$draftId", Guid.NewGuid().ToString("D"));
    command.Parameters.AddWithValue("$publishedAt", publishedAt.ToString("O"));
    command.ExecuteNonQuery();
}

static void BackupDatabase(string source, string target)
{
    using var sourceConnection = Open(source);
    using var targetConnection = Open(target);
    sourceConnection.BackupDatabase(targetConnection);
}

static long ScalarLong(string database, string sql)
{
    using var connection = Open(database);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
}

static void Execute(string database, string sql)
{
    using var connection = Open(database);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static SqliteConnection Open(string database)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = database,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString());
    connection.Open();
    return connection;
}

static async Task AssertThrowsAsync(Func<Task> action, string code)
{
    try
    {
        await action();
    }
    catch (EconomyAnalyticsException exception) when (exception.Code == code)
    {
        return;
    }
    throw new InvalidOperationException($"Expected stable analytics failure {code}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
}

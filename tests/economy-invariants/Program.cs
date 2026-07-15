using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var cancellationToken = CancellationToken.None;

await VerifyDailyPriceRotationAsync(cancellationToken);
VerifyZoneGeometryAndValidation();
VerifyWhitelistSnapshotHash();
await VerifyWalletConservationAndIdempotencyAsync(cancellationToken);
await VerifyConcurrentPurchaseLimitsAndReplaysAsync(cancellationToken);
await VerifyDomainAndDatabaseUniquenessAsync(cancellationToken);
await VerifyLegacyJsonlMigrationAsync(cancellationToken);

Console.WriteLine(
    "PASS: price, zone, hash, wallet conservation, 100-way concurrency, limits, replay, migration, and uniqueness checks.");

static async Task VerifyDailyPriceRotationAsync(CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        using var repository = new SqliteExtractionRepository(directory);
        var commerce = new ExtractionCommerceService(repository);
        var seedMethod = RequireMethod(
            typeof(ExtractionModeCoordinator),
            "SeedProducts",
            BindingFlags.Static | BindingFlags.NonPublic);
        var definitions = seedMethod.Invoke(null, null) as IReadOnlyList<ShopProductDefinition>
            ?? throw new InvalidOperationException("Could not read the production shop seed definitions.");
        var baseline = definitions.First();
        await repository.UpsertProductAsync(
            baseline with { UnitPrice = checked(baseline.UnitPrice + 777) },
            null,
            "test-pre-rotation",
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var options = new ExtractionModeOptions
        {
            Enabled = false,
            TimeZoneId = "UTC",
            // Put the next business-date boundary about 12 hours away so this
            // test cannot become flaky at midnight.
            DailyRefreshHour = (now.Hour + 12) % 24,
            BootstrapPolicyVersion = "test-zero-grant",
            InitialMarketCoin = 0,
            InitialSeasonVoucher = 0
        };
        var coordinator = new ExtractionModeCoordinator(
            commerce,
            null!,
            null!,
            null!,
            null!,
            null!,
            Options.Create(options),
            NullLogger<ExtractionModeCoordinator>.Instance);
        var rotationMethod = RequireMethod(
            typeof(ExtractionModeCoordinator),
            "EnsureDailyRotationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        await InvokePrivateTaskAsync(rotationMethod, coordinator, cancellationToken);
        var rotated = await repository.GetProductAsync(baseline.Sku, cancellationToken)
            ?? throw new InvalidOperationException("Daily rotation removed the seeded product.");
        const string actorPrefix = "daily-rotation:";
        Assert(rotated.UpdatedBy.StartsWith(actorPrefix, StringComparison.Ordinal),
            "Daily price rotation did not record its business-date actor.");
        var businessDate = DateOnly.ParseExact(
            rotated.UpdatedBy[actorPrefix.Length..],
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{businessDate:yyyy-MM-dd}|{baseline.Sku}"));
        var multiplier = new[] { 90L, 95L, 100L, 105L, 110L }[digest[0] % 5];
        var expected = Math.Max(1, checked((baseline.UnitPrice * multiplier + 50) / 100));
        Assert(rotated.UnitPrice == expected,
            $"Daily price {rotated.UnitPrice} did not match deterministic expected value {expected}.");
        Assert(rotated.UnitPrice >= Math.Max(1, baseline.UnitPrice * 90 / 100) &&
               rotated.UnitPrice <= checked((baseline.UnitPrice * 110 + 99) / 100),
            "Daily price escaped the documented 90%-110% band.");

        var revision = rotated.Revision;
        await InvokePrivateTaskAsync(rotationMethod, coordinator, cancellationToken);
        var replay = await repository.GetProductAsync(baseline.Sku, cancellationToken)
            ?? throw new InvalidOperationException("Daily rotation replay removed the product.");
        Assert(replay.Revision == revision && replay.UnitPrice == rotated.UnitPrice,
            "Replaying the same daily rotation created another product revision.");
    });
}

static void VerifyZoneGeometryAndValidation()
{
    var zone = new ExtractionZoneOptions
    {
        Id = "zone-a",
        DisplayName = "Zone A",
        RouteHint = "test route",
        MapX = 10,
        MapY = 20,
        Radius = 5
    };
    Assert(zone.IsValid(), "A finite positive extraction zone was rejected.");
    Assert(!WithRadius(zone, 0).IsValid(), "A zero-radius extraction zone was accepted.");
    Assert(!WithRadius(zone, 10_001).IsValid(), "An oversized extraction zone was accepted.");
    Assert(!WithMapX(zone, double.NaN).IsValid(), "A NaN extraction-zone coordinate was accepted.");
    Assert(!WithMapY(zone, double.PositiveInfinity).IsValid(),
        "An infinite extraction-zone coordinate was accepted.");

    var options = new ExtractionModeOptions { ExtractionZones = [zone] };
    var service = new ExtractionSettlementService(
        null!,
        null!,
        null!,
        null!,
        null!,
        Options.Create(options),
        Options.Create(new ExtractionRconOptions()),
        null!,
        NullLogger<ExtractionSettlementService>.Instance);
    var findZone = RequireMethod(
        typeof(ExtractionSettlementService),
        "FindZone",
        BindingFlags.Instance | BindingFlags.NonPublic);

    var boundary = new ExtractionLivePlayer("steam_boundary", "Boundary", true, null, 13, 24);
    var outside = boundary with { MapX = 13.0001 };
    var missing = boundary with { MapX = null };
    Assert(ReferenceEquals(findZone.Invoke(service, [boundary]), zone),
        "A point exactly on the circular zone boundary was rejected.");
    Assert(findZone.Invoke(service, [outside]) is null,
        "A point outside the circular zone boundary was accepted.");
    Assert(findZone.Invoke(service, [missing]) is null,
        "A player without a complete position matched an extraction zone.");

    var world = ExtractionCoordinateTransform.ToWorld(new ExtractionMapPoint(10, 20));
    Assert(world.X == 20 * ExtractionCoordinateTransform.Scale +
            ExtractionCoordinateTransform.WorldXOffset &&
           world.Y == 10 * ExtractionCoordinateTransform.Scale +
            ExtractionCoordinateTransform.WorldYOffset,
        "The public map-to-world coordinate transform changed unexpectedly.");
}

static void VerifyWhitelistSnapshotHash()
{
    var hashMethod = RequireMethod(
        typeof(ExtractionSettlementService),
        "HashSnapshot",
        BindingFlags.Static | BindingFlags.NonPublic);
    var firstTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["Leather"] = 5,
        ["Bone"] = 3
    };
    var reorderedTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["bone"] = 3,
        ["leather"] = 5
    };
    var withUnknownItem = new Dictionary<string, long>(reorderedTotals, StringComparer.OrdinalIgnoreCase)
    {
        ["NotWhitelisted"] = 999_999
    };
    var changedWhitelistItem = new Dictionary<string, long>(firstTotals, StringComparer.OrdinalIgnoreCase)
    {
        ["Leather"] = 6
    };

    var first = InvokeHash(hashMethod, firstTotals);
    var reordered = InvokeHash(hashMethod, reorderedTotals);
    var unknown = InvokeHash(hashMethod, withUnknownItem);
    var changed = InvokeHash(hashMethod, changedWhitelistItem);
    Assert(first.Length == 64 && first.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'),
        "Snapshot hash is not lowercase SHA-256 hexadecimal.");
    Assert(first == reordered,
        "Snapshot hash changed with dictionary order or item-id casing.");
    Assert(first == unknown,
        "A non-whitelisted inventory item changed the settlement snapshot hash.");
    Assert(first != changed,
        "Changing a whitelisted inventory quantity did not change the snapshot hash.");
}

static async Task VerifyWalletConservationAndIdempotencyAsync(
    CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        using var repository = new SqliteExtractionRepository(directory);
        var (season, account) = await CreateSeasonAndAccountAsync(
            repository,
            "wallet",
            cancellationToken);
        var seed = await repository.AdjustWalletAsync(
            WalletRequest(account.AccountId, season.SeasonId, 100, "wallet-seed-0001", "seed"),
            cancellationToken);
        Assert(seed.Created, "Wallet concurrency seed was not created.");

        var debits = await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            repository.AdjustWalletAsync(
                WalletRequest(
                    account.AccountId,
                    season.SeasonId,
                    -1,
                    $"wallet-debit-{index:000}",
                    $"debit-{index:000}"),
                cancellationToken)));
        Assert(debits.All(result => result.Created && result.ErrorCode is null),
            "One of 100 distinct concurrent one-coin debits failed.");

        var wallet = await repository.GetWalletAsync(
            account.AccountId,
            season.SeasonId,
            cancellationToken);
        var ledger = await repository.GetLedgerAsync(
            account.AccountId,
            season.SeasonId,
            1_000,
            cancellationToken);
        Assert(wallet.MarketCoin.Balance == 0, "100 concurrent debits did not end at zero.");
        Assert(ledger.Count == 101 && ledger.Sum(entry => entry.Delta) == wallet.MarketCoin.Balance,
            "Wallet balance and ledger delta sum are not conserved.");
        Assert(ledger.Select(entry => entry.EntryId).Distinct().Count() == ledger.Count,
            "Concurrent wallet writes produced duplicate ledger entry ids.");
        Assert(ledger.Count(entry => entry.Delta == -1) == 100,
            "Concurrent wallet debits produced an unexpected ledger shape.");

        var replayAccount = await repository.GetOrCreateAccountAsync(
            "steam",
            "steam_wallet_replay",
            "Wallet Replay",
            cancellationToken);
        const string replayKey = "wallet-replay-0001";
        var replays = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            repository.AdjustWalletAsync(
                WalletRequest(replayAccount.AccountId, season.SeasonId, 7, replayKey, "replay"),
                cancellationToken)));
        Assert(replays.Count(result => result.Created) == 1 &&
               replays.All(result => !result.IdempotencyConflict && result.ErrorCode is null),
            "100 exact same-key wallet requests were not collapsed to one write.");
        var replayWallet = await repository.GetWalletAsync(
            replayAccount.AccountId,
            season.SeasonId,
            cancellationToken);
        var replayLedger = await repository.GetLedgerAsync(
            replayAccount.AccountId,
            season.SeasonId,
            100,
            cancellationToken);
        Assert(replayWallet.MarketCoin.Balance == 7 && replayLedger.Count == 1,
            "Same-key wallet replay changed the balance more than once.");

        var conflict = await repository.AdjustWalletAsync(
            WalletRequest(replayAccount.AccountId, season.SeasonId, 8, replayKey, "changed"),
            cancellationToken);
        Assert(conflict.IdempotencyConflict && conflict.ErrorCode == "IDEMPOTENCY_CONFLICT",
            "Same wallet key with a changed payload was not rejected.");

        var isolatedAccount = await repository.GetOrCreateAccountAsync(
            "steam",
            "steam_wallet_isolated",
            "Wallet Isolated",
            cancellationToken);
        var isolated = await repository.AdjustWalletAsync(
            WalletRequest(isolatedAccount.AccountId, season.SeasonId, 9, replayKey, "isolated"),
            cancellationToken);
        Assert(isolated.Created,
            "An idempotency key used by another account leaked across account scope.");
    });
}

static async Task VerifyConcurrentPurchaseLimitsAndReplaysAsync(
    CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        using var repository = new SqliteExtractionRepository(directory);
        var (season, account) = await CreateSeasonAndAccountAsync(
            repository,
            "purchase",
            cancellationToken);
        var seed = await repository.AdjustWalletAsync(
            WalletRequest(account.AccountId, season.SeasonId, 1_000, "purchase-seed-0001", "seed"),
            cancellationToken);
        Assert(seed.Created, "Purchase concurrency seed was not created.");

        await repository.UpsertProductAsync(
            Product("LIMIT-ITEM", 10, 10),
            null,
            "test",
            cancellationToken);
        var purchases = await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            repository.PurchaseAsync(
                PurchaseRequest(
                    account.AccountId,
                    season.SeasonId,
                    "LIMIT-ITEM",
                    1,
                    $"purchase-limit-{index:000}"),
                cancellationToken)));
        Assert(purchases.Count(result => result.Created) == 10,
            "100 concurrent limited purchases did not create exactly 10 orders.");
        Assert(purchases.Count(result => result.ErrorCode == "PURCHASE_LIMIT_EXCEEDED") == 90,
            "Concurrent purchase-limit failures were not stable.");

        var afterLimit = await repository.GetWalletAsync(
            account.AccountId,
            season.SeasonId,
            cancellationToken);
        var limitOrders = await repository.ListOrdersAsync(
            account.AccountId,
            season.SeasonId,
            1_000,
            cancellationToken);
        var limitDeliveries = await repository.ListAllPendingDeliveriesAsync(cancellationToken);
        var limitLedger = await repository.GetLedgerAsync(
            account.AccountId,
            season.SeasonId,
            1_000,
            cancellationToken);
        Assert(afterLimit.MarketCoin.Balance == 900 &&
               limitOrders.Count == 10 &&
               limitDeliveries.Count == 10,
            "Concurrent limited purchases did not conserve orders, deliveries, and wallet balance.");
        Assert(limitLedger.Sum(entry => entry.Delta) == afterLimit.MarketCoin.Balance,
            "Purchase debits and the wallet balance are not conserved.");

        await repository.UpsertProductAsync(
            Product("REPLAY-ITEM", 5, null),
            null,
            "test",
            cancellationToken);
        const string replayKey = "purchase-replay-0001";
        var replayRequest = PurchaseRequest(
            account.AccountId,
            season.SeasonId,
            "REPLAY-ITEM",
            1,
            replayKey);
        var replays = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            repository.PurchaseAsync(replayRequest, cancellationToken)));
        var replayOrderIds = replays.Select(result => result.Order?.OrderId).Distinct().ToArray();
        Assert(replays.Count(result => result.Created) == 1 &&
               replayOrderIds.Length == 1 &&
               replayOrderIds[0] is not null &&
               replays.All(result => !result.IdempotencyConflict && result.ErrorCode is null),
            "100 exact same-key purchases did not resolve to one order.");
        var afterReplay = await repository.GetWalletAsync(
            account.AccountId,
            season.SeasonId,
            cancellationToken);
        Assert(afterReplay.MarketCoin.Balance == 895,
            "Same-key purchase replay debited the wallet more than once.");

        var conflict = await repository.PurchaseAsync(
            PurchaseRequest(
                account.AccountId,
                season.SeasonId,
                "REPLAY-ITEM",
                2,
                replayKey),
            cancellationToken);
        Assert(conflict.IdempotencyConflict && conflict.ErrorCode == "IDEMPOTENCY_CONFLICT",
            "Same purchase key with a changed quantity was not rejected.");

        var isolatedAccount = await repository.GetOrCreateAccountAsync(
            "steam",
            "steam_purchase_isolated",
            "Purchase Isolated",
            cancellationToken);
        var isolatedSeed = await repository.AdjustWalletAsync(
            WalletRequest(
                isolatedAccount.AccountId,
                season.SeasonId,
                20,
                "purchase-seed-0001",
                "seed"),
            cancellationToken);
        var isolatedPurchase = await repository.PurchaseAsync(
            PurchaseRequest(
                isolatedAccount.AccountId,
                season.SeasonId,
                "REPLAY-ITEM",
                1,
                replayKey),
            cancellationToken);
        Assert(isolatedSeed.Created && isolatedPurchase.Created &&
               isolatedPurchase.Order?.OrderId != replayOrderIds[0],
            "Purchase idempotency leaked across account scope.");
    });
}

static async Task VerifyDomainAndDatabaseUniquenessAsync(
    CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async directory =>
    {
        string databasePath;
        using (var repository = new SqliteExtractionRepository(directory))
        {
            var (season, account) = await CreateSeasonAndAccountAsync(
                repository,
                "unique",
                cancellationToken);
            await AssertThrowsAsync<InvalidOperationException>(() =>
                repository.UpsertSeasonAsync(
                    null,
                    new ExtractionSeasonDefinition(
                        "local",
                        season.Code,
                        "Duplicate Code",
                        "world-duplicate-code",
                        season.StartsAt,
                        season.EndsAt,
                        ExtractionSeasonState.Draft),
                    null,
                    cancellationToken),
                "Duplicate per-server season code was accepted.");
            await AssertThrowsAsync<InvalidOperationException>(() =>
                repository.UpsertSeasonAsync(
                    null,
                    new ExtractionSeasonDefinition(
                        "local",
                        "S-UNIQUE-OTHER",
                        "Second Active",
                        "world-second-active",
                        season.StartsAt,
                        season.EndsAt,
                        ExtractionSeasonState.Active),
                    null,
                    cancellationToken),
                "A second active season for one server was accepted.");

            var sameIdentity = await repository.GetOrCreateAccountAsync(
                "STEAM",
                account.ExternalUserId.ToUpperInvariant(),
                "Updated Display Name",
                cancellationToken);
            Assert(sameIdentity.AccountId == account.AccountId && sameIdentity.Revision == 2,
                "Case-insensitive platform identity created a duplicate account.");

            var firstProduct = await repository.UpsertProductAsync(
                Product("UNIQUE-SKU", 3, null),
                null,
                "test",
                cancellationToken);
            var updatedProduct = await repository.UpsertProductAsync(
                Product("unique-sku", 4, null),
                firstProduct.Revision,
                "test-update",
                cancellationToken);
            Assert(updatedProduct.ProductId == firstProduct.ProductId &&
                   updatedProduct.Revision == firstProduct.Revision + 1,
                "Case-insensitive SKU upsert created a duplicate product.");
            databasePath = Path.Combine(directory, "extraction-commerce.db");
        }

        using var connection = OpenDatabase(databasePath);
        string eventId;
        string eventType;
        string occurredAt;
        string payload;
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT event_id, event_type, occurred_at, payload
                FROM extraction_events
                ORDER BY sequence
                LIMIT 1;
                """;
            using var reader = select.ExecuteReader();
            Assert(reader.Read(), "The uniqueness fixture created no SQLite event.");
            eventId = reader.GetString(0);
            eventType = reader.GetString(1);
            occurredAt = reader.GetString(2);
            payload = reader.GetString(3);
        }
        var duplicateRejected = false;
        try
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO extraction_events (event_id, event_type, occurred_at, payload)
                VALUES ($eventId, $eventType, $occurredAt, $payload);
                """;
            insert.Parameters.AddWithValue("$eventId", eventId);
            insert.Parameters.AddWithValue("$eventType", eventType);
            insert.Parameters.AddWithValue("$occurredAt", occurredAt);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            duplicateRejected = true;
        }
        Assert(duplicateRejected, "SQLite accepted a duplicate extraction event id.");
    });
}

static async Task VerifyLegacyJsonlMigrationAsync(CancellationToken cancellationToken)
{
    await WithStoreDirectoryAsync(async root =>
    {
        var sourceDirectory = Path.Combine(root, "source");
        var targetDirectory = Path.Combine(root, "target");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetDirectory);

        ShopPurchaseRequest purchaseRequest;
        Guid expectedOrderId;
        Guid expectedAccountId;
        Guid expectedSeasonId;
        using (var source = new SqliteExtractionRepository(sourceDirectory))
        {
            var (season, account) = await CreateSeasonAndAccountAsync(
                source,
                "migration",
                cancellationToken);
            expectedAccountId = account.AccountId;
            expectedSeasonId = season.SeasonId;
            var seeded = await source.AdjustWalletAsync(
                WalletRequest(account.AccountId, season.SeasonId, 50, "migration-seed-0001", "seed"),
                cancellationToken);
            Assert(seeded.Created, "Migration wallet fixture was not created.");
            await source.UpsertProductAsync(
                Product("MIGRATION-ITEM", 7, null),
                null,
                "test",
                cancellationToken);
            purchaseRequest = PurchaseRequest(
                account.AccountId,
                season.SeasonId,
                "MIGRATION-ITEM",
                2,
                "migration-order-0001");
            var purchase = await source.PurchaseAsync(purchaseRequest, cancellationToken);
            Assert(purchase.Created && purchase.Order is not null,
                "Migration purchase fixture was not created.");
            expectedOrderId = purchase.Order?.OrderId
                ?? throw new InvalidOperationException("Migration purchase has no order projection.");
        }

        var sourceDatabase = Path.Combine(sourceDirectory, "extraction-commerce.db");
        var payloads = ReadEventPayloads(sourceDatabase);
        Assert(payloads.Count >= 5, "Migration fixture did not contain the expected event history.");
        var legacyPath = Path.Combine(targetDirectory, "extraction-commerce-events.jsonl");
        File.WriteAllLines(legacyPath, payloads, new UTF8Encoding(false));
        var legacyBytes = File.ReadAllBytes(legacyPath);

        using (var migrated = new SqliteExtractionRepository(targetDirectory))
        {
            var seasons = await migrated.ListSeasonsAsync("local", cancellationToken);
            var account = await migrated.GetAccountAsync(expectedAccountId, cancellationToken);
            var wallet = await migrated.GetWalletAsync(
                expectedAccountId,
                expectedSeasonId,
                cancellationToken);
            var order = await migrated.GetOrderAsync(expectedOrderId, cancellationToken);
            var product = await migrated.GetProductAsync("migration-item", cancellationToken);
            Assert(seasons.Single().SeasonId == expectedSeasonId &&
                   account?.AccountId == expectedAccountId &&
                   wallet.MarketCoin.Balance == 36 &&
                   order?.OrderId == expectedOrderId &&
                   product?.Sku == "MIGRATION-ITEM",
                "Legacy JSONL migration did not rebuild the full economy projection.");

            var replay = await migrated.PurchaseAsync(purchaseRequest, cancellationToken);
            Assert(!replay.Created && !replay.IdempotencyConflict &&
                   replay.Order?.OrderId == expectedOrderId,
                "Migrated purchase idempotency did not survive the JSONL-to-SQLite transition.");
        }

        var targetDatabase = Path.Combine(targetDirectory, "extraction-commerce.db");
        Assert(ReadEventPayloads(targetDatabase).Count == payloads.Count,
            "JSONL-to-SQLite migration changed the event count.");
        Assert(File.Exists(Path.Combine(
                   targetDirectory,
                   "extraction-commerce.sqlite-authoritative.json")),
            "Migration did not create the SQLite authoritative marker.");
        Assert(File.ReadAllBytes(legacyPath).SequenceEqual(legacyBytes),
            "Migration modified the legacy JSONL evidence instead of retaining it read-only.");
    });
}

static async Task<(ExtractionSeason Season, ExtractionAccount Account)>
    CreateSeasonAndAccountAsync(
        SqliteExtractionRepository repository,
        string suffix,
        CancellationToken cancellationToken)
{
    var now = DateTimeOffset.UtcNow;
    var season = await repository.UpsertSeasonAsync(
        null,
        new ExtractionSeasonDefinition(
            "local",
            $"S-{suffix.ToUpperInvariant()}",
            $"{suffix} test season",
            $"world-{suffix}",
            now.AddDays(-1),
            now.AddDays(1),
            ExtractionSeasonState.Active),
        null,
        cancellationToken);
    var account = await repository.GetOrCreateAccountAsync(
        "steam",
        $"steam_{suffix}_user",
        $"{suffix} test account",
        cancellationToken);
    return (season, account);
}

static WalletAdjustmentRequest WalletRequest(
    Guid accountId,
    Guid seasonId,
    long delta,
    string idempotencyKey,
    string referenceId) =>
    new(
        accountId,
        null,
        ExtractionCurrency.MarketCoin,
        delta,
        "economy invariant test",
        "test",
        referenceId,
        "economy-invariants",
        idempotencyKey);

static ShopProductDefinition Product(string sku, long unitPrice, int? limit) =>
    new(
        sku,
        sku,
        "Economy invariant product",
        ExtractionCurrency.MarketCoin,
        unitPrice,
        [new ShopItemGrant("Leather", 1)],
        limit,
        true,
        null,
        null);

static ShopPurchaseRequest PurchaseRequest(
    Guid accountId,
    Guid seasonId,
    string sku,
    int quantity,
    string idempotencyKey) =>
    new(
        accountId,
        seasonId,
        "local",
        $"player-{accountId:N}",
        [new ShopPurchaseLineInput(sku, quantity)],
        idempotencyKey,
        "economy-invariants",
        "economy invariant purchase");

static IReadOnlyList<string> ReadEventPayloads(string databasePath)
{
    using var connection = OpenDatabase(databasePath);
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT payload FROM extraction_events ORDER BY sequence;";
    using var reader = command.ExecuteReader();
    var payloads = new List<string>();
    while (reader.Read())
    {
        payloads.Add(reader.GetString(0));
    }
    return payloads;
}

static SqliteConnection OpenDatabase(string databasePath)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());
    connection.Open();
    return connection;
}

static MethodInfo RequireMethod(Type type, string name, BindingFlags flags) =>
    type.GetMethod(name, flags)
    ?? throw new InvalidOperationException($"Could not find production method {type.Name}.{name}.");

static async Task InvokePrivateTaskAsync(
    MethodInfo method,
    object instance,
    CancellationToken cancellationToken)
{
    var task = method.Invoke(instance, [cancellationToken]) as Task
        ?? throw new InvalidOperationException($"{method.Name} did not return a Task.");
    await task;
}

static string InvokeHash(MethodInfo method, IReadOnlyDictionary<string, long> totals) =>
    method.Invoke(null, [totals]) as string
    ?? throw new InvalidOperationException("Production snapshot hashing returned null.");

static async Task AssertThrowsAsync<TException>(
    Func<Task> action,
    string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static async Task WithStoreDirectoryAsync(Func<string, Task> action)
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"pal-control-economy-invariants-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        await action(directory);
    }
    finally
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(50);
            }
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static ExtractionZoneOptions WithRadius(ExtractionZoneOptions zone, double radius) =>
    new()
    {
        Id = zone.Id,
        DisplayName = zone.DisplayName,
        RouteHint = zone.RouteHint,
        MapX = zone.MapX,
        MapY = zone.MapY,
        Radius = radius
    };

static ExtractionZoneOptions WithMapX(ExtractionZoneOptions zone, double mapX) =>
    new()
    {
        Id = zone.Id,
        DisplayName = zone.DisplayName,
        RouteHint = zone.RouteHint,
        MapX = mapX,
        MapY = zone.MapY,
        Radius = zone.Radius
    };

static ExtractionZoneOptions WithMapY(ExtractionZoneOptions zone, double mapY) =>
    new()
    {
        Id = zone.Id,
        DisplayName = zone.DisplayName,
        RouteHint = zone.RouteHint,
        MapX = zone.MapX,
        MapY = mapY,
        Radius = zone.Radius
    };

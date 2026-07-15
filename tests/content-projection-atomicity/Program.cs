using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;

var directory = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-content-projection-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    await VerifyAtomicProjectionLifecycleAsync(directory, CancellationToken.None);
    Console.WriteLine(
        "PASS: publish/rollback product projection is atomic across injected Nth-product failures, restart retry, 20 successive business-date activations, catalog visibility, stale offers, event replay, global stock, and order product identity.");
    return 0;
}
finally
{
    for (var attempt = 0; attempt < 20 && Directory.Exists(directory); attempt++)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException) when (attempt < 19)
        {
            await Task.Delay(50);
        }
        catch (UnauthorizedAccessException) when (attempt < 19)
        {
            await Task.Delay(50);
        }
    }
}

static async Task VerifyAtomicProjectionLifecycleAsync(
    string directory,
    CancellationToken cancellationToken)
{
    var context = ValidationContext();
    var date = new DateOnly(2026, 7, 15);
    var fault = new NthProductFaultInjector();
    Guid firstOrderId;
    Guid coreProductId;
    Guid seasonId;
    Guid accountId;
    PreparedContentPublish first;
    PreparedContentPublish second;
    PreparedContentPublish current;

    await using var store = new SqliteEconomyContentStore(directory);
    using (var repository = new SqliteExtractionRepository(
               directory,
               contentProjectionFaultInjector: fault))
    {
        first = await PreparePublishAsync(
            store,
            BuildDefinition(1, ["CORE", "AMMO", "BUILD", "FOOD"]),
            null,
            date,
            "prepare-first-version",
            context,
            cancellationToken);

        fault.Arm(3);
        await AssertThrowsAsync<InjectedProjectionException>(() => repository
            .ActivateContentProductProjectionAsync(
                PublishProjection(first, "publish", "atomic-test:first"),
                cancellationToken));
        Assert(await store.GetCurrentAsync("local", cancellationToken) is null,
            "A failed bootstrap projection exposed a content pointer.");
        Assert((await repository.ListProductsAsync(false, cancellationToken)).Count == 0,
            "A failed bootstrap projection exposed a partial catalog.");
    }

    using (var repository = new SqliteExtractionRepository(
               directory,
               contentProjectionFaultInjector: fault))
    {
        var activated = await repository.ActivateContentProductProjectionAsync(
            PublishProjection(first, "publish", "atomic-test:first-retry"),
            cancellationToken);
        Assert(activated.PointerChanged && !activated.Replayed,
            "Restart retry did not activate the prepared bootstrap version.");
        await AssertCurrentCatalogAsync(store, repository, first.Version, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var season = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local",
                "atomic-week",
                "Atomic Projection Week",
                "00112233445566778899AABBCCDDEEFF",
                now.AddHours(-1),
                now.AddDays(7),
                ExtractionSeasonState.Active),
            null,
            cancellationToken);
        var account = await repository.GetOrCreateAccountAsync(
            "test",
            "atomic-player",
            "Atomic Player",
            cancellationToken);
        seasonId = season.SeasonId;
        accountId = account.AccountId;
        _ = await repository.AdjustWalletAsync(
            new WalletAdjustmentRequest(
                accountId,
                null,
                ExtractionCurrency.MarketCoin,
                100_000,
                "Atomic projection test funding",
                "test-funding",
                "atomic-player",
                "atomic-test",
                "atomic-funding-0001"),
            cancellationToken);

        var core = (await repository.ListProductsAsync(false, cancellationToken))
            .Single(product => product.Sku == "CORE");
        coreProductId = core.ProductId;
        var firstOrder = await PurchaseAsync(
            repository,
            accountId,
            seasonId,
            core,
            "atomic-order-first",
            cancellationToken);
        Assert(firstOrder.Created && firstOrder.Order is not null,
            "The first current offer did not create an order.");
        var createdFirstOrder = firstOrder.Order
            ?? throw new InvalidOperationException("The created first order is missing.");
        firstOrderId = createdFirstOrder.OrderId;
        Assert(createdFirstOrder.Lines.Single().ProductId == coreProductId &&
               createdFirstOrder.Lines.Single().ContentVersionId == first.Version.VersionId,
            "The first order did not freeze the current product/version identity.");

        second = await PreparePublishAsync(
            store,
            BuildDefinition(2, ["CORE", "AMMO", "TOOLS", "MEDICAL"]),
            first.Version.VersionId,
            date,
            "prepare-second-version",
            context,
            cancellationToken);
        fault.Arm(2);
        await AssertThrowsAsync<InjectedProjectionException>(() => repository
            .ActivateContentProductProjectionAsync(
                PublishProjection(second, "publish", "atomic-test:second"),
                cancellationToken));
        await AssertCurrentCatalogAsync(store, repository, first.Version, cancellationToken);

        var stillCurrent = (await repository.ListProductsAsync(false, cancellationToken))
            .Single(product => product.Sku == "CORE");
        var afterFailedPublish = await PurchaseAsync(
            repository,
            accountId,
            seasonId,
            stillCurrent,
            "atomic-order-after-failed-publish",
            cancellationToken);
        Assert(afterFailedPublish.Created &&
               afterFailedPublish.Order?.Lines.Single().ContentVersionId == first.Version.VersionId,
            "A failed publish made order offer evidence disagree with the old visible catalog.");
    }

    using (var repository = new SqliteExtractionRepository(
               directory,
               contentProjectionFaultInjector: fault))
    {
        _ = await repository.ActivateContentProductProjectionAsync(
            PublishProjection(second, "publish", "atomic-test:second-retry"),
            cancellationToken);
        await AssertCurrentCatalogAsync(store, repository, second.Version, cancellationToken);
        var secondCore = (await repository.ListProductsAsync(false, cancellationToken))
            .Single(product => product.Sku == "CORE");
        Assert(secondCore.ProductId == coreProductId,
            "Content activation replaced the stable product identity referenced by existing orders.");

        var stale = await PurchaseAsync(
            repository,
            accountId,
            seasonId,
            secondCore with
            {
                ContentVersionId = first.Version.VersionId,
                ContentHash = first.Version.ContentHash
            },
            "atomic-order-stale-v1",
            cancellationToken);
        Assert(!stale.Created && stale.ErrorCode == "OFFER_NOT_AVAILABLE",
            "An order using the pre-activation offer was not rejected.");
        var currentOrder = await PurchaseAsync(
            repository,
            accountId,
            seasonId,
            secondCore,
            "atomic-order-current-v2",
            cancellationToken);
        Assert(currentOrder.Created &&
               currentOrder.Order?.Lines.Single().ContentVersionId == second.Version.VersionId,
            "An order using the new complete catalog did not freeze the new offer identity.");

        current = second;
        for (var publication = 0; publication < 20; publication++)
        {
            var generation = publication + 3;
            var rotatingSku = publication % 2 == 0 ? "ROTATING-A" : "ROTATING-B";
            var next = await PreparePublishAsync(
                store,
                BuildDefinition(generation, ["CORE", "AMMO", rotatingSku, $"WEEK-{publication:00}"]),
                current.Version.VersionId,
                date.AddDays(publication + 1),
                $"prepare-rotation-{publication:00}",
                context,
                cancellationToken);
            _ = await repository.ActivateContentProductProjectionAsync(
                PublishProjection(next, "publish", $"atomic-test:rotation-{publication:00}"),
                cancellationToken);
            await AssertCurrentCatalogAsync(store, repository, next.Version, cancellationToken);
            current = next;
        }
        Assert(await repository.GetGlobalPurchasedQuantityAsync(
                   seasonId,
                   "CORE",
                   cancellationToken) == 3,
            "Repeated content activation changed server-wide purchased stock accounting.");

        var rollback = await store.PrepareRollbackAsync(
            "local",
            first.Version.VersionId,
            current.Version.VersionId,
            "prepare-atomic-rollback",
            "atomic-test",
            cancellationToken);
        fault.Arm(2);
        await AssertThrowsAsync<InjectedProjectionException>(() => repository
            .ActivateContentProductProjectionAsync(
                RollbackProjection(rollback, "rollback", "atomic-test:rollback"),
                cancellationToken));
        await AssertCurrentCatalogAsync(store, repository, current.Version, cancellationToken);
    }

    using (var repository = new SqliteExtractionRepository(directory))
    {
        var rollback = await store.PrepareRollbackAsync(
            "local",
            first.Version.VersionId,
            current.Version.VersionId,
            "prepare-atomic-rollback",
            "atomic-test",
            cancellationToken);
        var activated = await repository.ActivateContentProductProjectionAsync(
            RollbackProjection(rollback, "rollback", "atomic-test:rollback-retry"),
            cancellationToken);
        Assert(rollback.Replayed && activated.PointerChanged,
            "Restart retry did not resume the prepared rollback.");
        await AssertCurrentCatalogAsync(store, repository, first.Version, cancellationToken);
        var firstCoreAgain = (await repository.ListProductsAsync(false, cancellationToken))
            .Single(product => product.Sku == "CORE");
        Assert(firstCoreAgain.ProductId == coreProductId,
            "Rollback broke the stable product identity referenced by orders.");

        var staleCurrent = await PurchaseAsync(
            repository,
            accountId,
            seasonId,
            firstCoreAgain with
            {
                ContentVersionId = current.Version.VersionId,
                ContentHash = current.Version.ContentHash
            },
            "atomic-order-stale-rollback",
            cancellationToken);
        Assert(!staleCurrent.Created && staleCurrent.ErrorCode == "OFFER_NOT_AVAILABLE",
            "Rollback did not invalidate the replaced offer.");
        var rollbackOrder = await PurchaseAsync(
            repository,
            accountId,
            seasonId,
            firstCoreAgain,
            "atomic-order-after-rollback",
            cancellationToken);
        Assert(rollbackOrder.Created &&
               rollbackOrder.Order?.Lines.Single().ContentVersionId == first.Version.VersionId,
            "Rollback catalog and committed order offer evidence disagree.");
        Assert(await repository.GetGlobalPurchasedQuantityAsync(
                   seasonId,
                   "CORE",
                   cancellationToken) == 4,
            "Rollback lost or reset server-wide stock occupancy.");

        var replay = await repository.ActivateContentProductProjectionAsync(
            RollbackProjection(rollback, "rollback", "atomic-test:rollback-replay"),
            cancellationToken);
        Assert(replay.Replayed && !replay.PointerChanged,
            "Idempotent rollback activation was not recognized as a replay.");
    }

    await using (var restartedStore = new SqliteEconomyContentStore(directory))
    using (var restartedRepository = new SqliteExtractionRepository(directory))
    {
        await AssertCurrentCatalogAsync(
            restartedStore,
            restartedRepository,
            first.Version,
            cancellationToken);
        var replayedOrder = await restartedRepository.GetOrderAsync(
            firstOrderId,
            cancellationToken);
        Assert(replayedOrder?.Lines.Single().ProductId == coreProductId &&
               replayedOrder.Lines.Single().ContentVersionId == first.Version.VersionId,
            "Event replay lost the order's product/content foreign identity.");
        Assert(await restartedRepository.GetGlobalPurchasedQuantityAsync(
                   seasonId,
                   "CORE",
                   cancellationToken) == 4,
            "Event replay changed server-wide stock occupancy.");
    }
}

static async Task<PreparedContentPublish> PreparePublishAsync(
    SqliteEconomyContentStore store,
    EconomyContentDefinition definition,
    Guid? basedOnVersionId,
    DateOnly businessDate,
    string key,
    EconomyContentValidationContext context,
    CancellationToken cancellationToken)
{
    var draft = await store.CreateDraftAsync(
        "local",
        key,
        basedOnVersionId,
        definition,
        "atomic-test",
        cancellationToken);
    return await store.PreparePublishAsync(
        draft.DraftId,
        draft.Revision,
        businessDate,
        key,
        "atomic-test",
        context,
        cancellationToken);
}

static ContentProductProjectionActivation PublishProjection(
    PreparedContentPublish prepared,
    string action,
    string actor) => ProjectionForVersion(
        prepared.Version,
        prepared.ExpectedCurrentVersionId,
        action,
        actor);

static ContentProductProjectionActivation RollbackProjection(
    PreparedContentRollback prepared,
    string action,
    string actor) => ProjectionForVersion(
        prepared.Version,
        prepared.ExpectedCurrentVersionId,
        action,
        actor);

static ContentProductProjectionActivation ProjectionForVersion(
    EconomyContentVersion version,
    Guid? expectedCurrentVersionId,
    string action,
    string actor) => new(
        version.ServerId,
        version.VersionId,
        version.VersionNumber,
        version.BusinessDate,
        version.RulesVersion,
        version.ContentHash,
        expectedCurrentVersionId,
        action,
        actor,
        EconomyContentProductProjection.Create(version));

static async Task AssertCurrentCatalogAsync(
    SqliteEconomyContentStore store,
    SqliteExtractionRepository repository,
    EconomyContentVersion expected,
    CancellationToken cancellationToken)
{
    var pointer = await store.GetCurrentAsync("local", cancellationToken);
    Assert(pointer?.VersionId == expected.VersionId &&
           pointer.ContentHash == expected.ContentHash,
        "The current pointer does not identify the expected complete content version.");
    var expectedSkus = expected.Definition.Products
        .Where(product => product.Active)
        .Select(product => product.Sku)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var catalog = await repository.ListProductsAsync(false, cancellationToken);
    Assert(catalog.Select(product => product.Sku)
               .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
               .SequenceEqual(expectedSkus, StringComparer.OrdinalIgnoreCase),
        "The player catalog is not the complete expected SKU set.");
    Assert(catalog.All(product =>
            product.ContentVersionId == expected.VersionId &&
            product.ContentHash == expected.ContentHash),
        "The player catalog contains mixed content-version evidence.");
}

static Task<ShopPurchaseResult> PurchaseAsync(
    SqliteExtractionRepository repository,
    Guid accountId,
    Guid seasonId,
    ShopProduct offer,
    string idempotencyKey,
    CancellationToken cancellationToken) => repository.PurchaseAsync(
        new ShopPurchaseRequest(
            accountId,
            seasonId,
            "local",
            "atomic-player",
            [new ShopPurchaseLineInput(offer.Sku, 1)],
            idempotencyKey,
            "atomic-test",
            "Atomic product projection order",
            ExpectedContentVersionId: offer.ContentVersionId,
            ExpectedContentHash: offer.ContentHash),
        cancellationToken);

static EconomyContentDefinition BuildDefinition(
    int generation,
    IReadOnlyList<string> skus)
{
    var windows = Enum.GetValues<DayOfWeek>()
        .Select(day => new ContentExchangeWindow(
            day,
            new TimeOnly(4, 0),
            new TimeOnly(3, 59),
            60))
        .ToArray();
    var products = skus.Select((sku, index) => new ContentProductDefinition(
        sku,
        $"{sku} generation {generation}",
        $"Atomic projection product {sku} generation {generation}.",
        index == 0 ? "Core" : "Rotating",
        ["atomic", $"generation-{generation}"],
        index + 1,
        ExtractionCurrency.MarketCoin,
        100 + generation * 10 + index,
        [new ContentItemGrant(index % 2 == 0 ? "Stone" : "Wood", index + 1)],
        100,
        index == 0 ? 100L : null,
        true,
        null,
        null)).ToArray();
    return new EconomyContentDefinition(
        1,
        "local",
        $"Atomic content generation {generation}",
        new EconomyContentDependencies(
            "weekly-economy-v1",
            "catalog-atomic-v1",
            "1.0.0-test",
            "1.8.1-test"),
        "UTC",
        4,
        products,
        [
            new ContentResourceDefinition(
                "Stone", "Stone", "RawMaterial", ["basic"],
                ExtractionCurrency.SeasonVoucher, 2, ["atomic-zone"], true),
            new ContentResourceDefinition(
                "Wood", "Wood", "RawMaterial", ["basic"],
                ExtractionCurrency.SeasonVoucher, 1, ["atomic-zone"], true)
        ],
        [
            new ContentExchangeZoneDefinition(
                "atomic-zone", "Atomic Zone", "Atomic test zone.",
                0, 0, 100, 10_000, windows, true)
        ],
        [
            TaskDefinition("daily-exchange", ContentTaskCadence.Daily, ContentTaskEventKind.ResourceExchangeSettled, 1),
            TaskDefinition("daily-stone", ContentTaskCadence.Daily, ContentTaskEventKind.ResourceItemSettled, 10, "Stone"),
            TaskDefinition("daily-spend", ContentTaskCadence.Daily, ContentTaskEventKind.CurrencySpent, 10,
                currency: ExtractionCurrency.MarketCoin),
            TaskDefinition("weekly-value", ContentTaskCadence.Weekly, ContentTaskEventKind.ResourceValueSettled, 100),
            TaskDefinition("weekly-orders", ContentTaskCadence.Weekly, ContentTaskEventKind.ShopOrderDelivered, 1),
            TaskDefinition("weekly-wood", ContentTaskCadence.Weekly, ContentTaskEventKind.ResourceItemSettled, 20, "Wood")
        ],
        new ContentRotationPolicy(
            "weekly-economy-v1",
            1,
            "atomic-rotation",
            ["daily-exchange", "daily-stone", "daily-spend"],
            2,
            ["weekly-value", "weekly-orders", "weekly-wood"],
            2,
            ["atomic-zone"],
            1));
}

static ContentTaskDefinition TaskDefinition(
    string key,
    ContentTaskCadence cadence,
    ContentTaskEventKind eventKind,
    long target,
    string? itemId = null,
    ExtractionCurrency? currency = null) => new(
        key,
        key,
        $"Complete {key}.",
        cadence,
        eventKind,
        target,
        itemId,
        currency,
        [],
        new ContentTaskReward(ExtractionCurrency.MarketCoin, 10, 5),
        true);

static EconomyContentValidationContext ValidationContext() => new(
    new HashSet<string>(["Stone", "Wood"], StringComparer.OrdinalIgnoreCase),
    new HashSet<string>(["weekly-economy-v1"], StringComparer.Ordinal),
    "catalog-atomic-v1",
    "1.0.0-test",
    "1.8.1-test");

static async Task AssertThrowsAsync<TException>(Func<Task> action)
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
    throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class InjectedProjectionException : Exception
{
    public InjectedProjectionException(int productNumber, string sku)
        : base($"Injected product projection failure after #{productNumber} ({sku}).")
    {
    }
}

sealed class NthProductFaultInjector : IContentProductProjectionFaultInjector
{
    private int? _armedProductNumber;

    public void Arm(int productNumber) => _armedProductNumber = productNumber;

    public void ThrowAfterProjectedProduct(int productNumber, string sku)
    {
        if (_armedProductNumber != productNumber)
        {
            return;
        }
        _armedProductNumber = null;
        throw new InjectedProjectionException(productNumber, sku);
    }
}

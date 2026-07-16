using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var directory = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-permanent-currency-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    var (definition, context) = CreateSchemeADefault();
    VerifyPublishedContractAndBounds(definition, context);
    VerifyContractFailuresAreExplicit(definition, context);
    await VerifyReliableTaskRewardReplayAsync(
        Path.Combine(directory, "task-replay"),
        CancellationToken.None);
    await VerifyShopDebitLimitAndIdempotencyAsync(
        Path.Combine(directory, "shop-sink"),
        CancellationToken.None);
    Console.WriteLine(
        "PASS: Scheme A MarketCoin contract proves bounded 480/week task inflow and 1200/week sink outflow; source replay, shop debit, idempotency, and personal limits remain unique.");
    return 0;
}
finally
{
    for (var attempt = 0; attempt < 10 && Directory.Exists(directory); attempt++)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException) when (attempt < 9)
        {
            await Task.Delay(50);
        }
        catch (UnauthorizedAccessException) when (attempt < 9)
        {
            await Task.Delay(50);
        }
    }
}

static void VerifyPublishedContractAndBounds(
    EconomyContentDefinition definition,
    EconomyContentValidationContext context)
{
    var validation = new EconomyContentDefinitionValidator().Validate(definition, context);
    Assert(validation.Valid,
        "The built-in modern Scheme A content failed the permanent-currency publication contract: " +
        string.Join(" | ", validation.Errors.Select(error => $"{error.Code}: {error.Message}")));

    var analysis = EconomyPermanentCurrencyAnalyzer.Analyze(definition, context.KnownItemIds);
    Assert(analysis.Complete &&
           analysis.MinimumMarketCoinInflowPerPlayerPerWeek == 480 &&
           analysis.MaximumMarketCoinInflowPerPlayerPerWeek == 480,
        "The six selected reliable tasks no longer prove the exact bounded 480 MarketCoin weekly inflow.");
    Assert(analysis.MaximumMarketCoinOutflowPerPlayerPerWeek == 1_200,
        "The built-in personal purchase limits no longer prove the 1200 MarketCoin weekly outflow cap.");
    Assert(analysis.Sources.Count == 6 &&
           analysis.Sources.Select(source => source.TaskKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
               .SetEquals([
                   "daily-exchange", "daily-leather", "daily-spend",
                   "weekly-value", "weekly-orders", "weekly-coal"
               ]),
        "The bounded inflow diagnosis does not list all six concrete reliable-task sources.");
    Assert(analysis.Sinks.Count == 3 &&
           analysis.Sinks.Select(sink => sink.Sku).ToHashSet(StringComparer.OrdinalIgnoreCase)
               .SetEquals(["STARTER-CAPTURE", "STARTER-CROSSBOW", "BUILDER-REPAIR"]) &&
           analysis.Sinks.Select(sink => sink.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3,
        "The bounded outflow diagnosis does not list the three distinct-use MarketCoin SKUs.");

    var reducedDailySelection = definition with
    {
        Rotation = definition.Rotation with { DailyTaskCount = 2 }
    };
    var reducedAnalysis = EconomyPermanentCurrencyAnalyzer.Analyze(
        reducedDailySelection,
        context.KnownItemIds);
    Assert(reducedAnalysis.MinimumMarketCoinInflowPerPlayerPerWeek == 375 &&
           reducedAnalysis.MaximumMarketCoinInflowPerPlayerPerWeek == 410,
        "Changing the daily task selection count did not recompute the 375..410 weekly inflow bounds.");

    var reducedSinkLimit = definition with
    {
        Products = definition.Products.Select(product =>
            string.Equals(product.Sku, "STARTER-CAPTURE", StringComparison.OrdinalIgnoreCase)
                ? product with { PurchaseLimitPerSeason = 2 }
                : product).ToArray()
    };
    var reducedSinkAnalysis = EconomyPermanentCurrencyAnalyzer.Analyze(
        reducedSinkLimit,
        context.KnownItemIds);
    Assert(reducedSinkAnalysis.MaximumMarketCoinOutflowPerPlayerPerWeek == 1_080,
        "Changing a personal limit did not recompute the weekly MarketCoin outflow cap.");
}

static void VerifyContractFailuresAreExplicit(
    EconomyContentDefinition definition,
    EconomyContentValidationContext context)
{
    var validator = new EconomyContentDefinitionValidator();
    var noScheduledSource = definition with
    {
        Rotation = definition.Rotation with
        {
            DailyTaskPool = [],
            DailyTaskCount = 0,
            WeeklyTaskPool = [],
            WeeklyTaskCount = 0
        }
    };
    var noSource = validator.Validate(noScheduledSource, context);
    Assert(!noSource.Valid &&
           noSource.Errors.Any(error =>
               error.Code == "BOUNDED_MARKET_COIN_GAMEPLAY_SOURCE_REQUIRED" &&
               error.Message.Contains("Scheduled sources: none", StringComparison.Ordinal)) &&
           noSource.Warnings.Count(warning =>
               warning.Code == "UNSCHEDULED_MARKET_COIN_REWARD_NOT_COUNTED") == 6,
        "Unscheduled/unbounded MarketCoin grants were incorrectly counted as cadence-bounded gameplay inflow.");

    var mixedRewards = definition.Tasks.Select((task, index) => task with
    {
        Reward = task.Reward with
        {
            Currency = index == 0
                ? ExtractionCurrency.MarketCoin
                : ExtractionCurrency.SeasonVoucher
        }
    }).ToArray();
    var rotationCanSelectZero = definition with
    {
        Tasks = mixedRewards,
        Rotation = definition.Rotation with
        {
            DailyTaskCount = 1,
            WeeklyTaskCount = 0
        }
    };
    var unstable = validator.Validate(rotationCanSelectZero, context);
    Assert(!unstable.Valid && unstable.Errors.Any(error =>
            error.Code == "MARKET_COIN_SOURCE_NOT_GUARANTEED" &&
            error.Message.Contains("daily-exchange", StringComparison.Ordinal)),
        "A rotation capable of selecting zero weekly MarketCoin inflow was not blocked with its concrete task source.");

    var oneSink = definition with
    {
        Products = definition.Products.Select(product =>
            product.PriceCurrency == ExtractionCurrency.MarketCoin &&
            !string.Equals(product.Sku, "STARTER-CAPTURE", StringComparison.OrdinalIgnoreCase)
                ? product with { Active = false }
                : product).ToArray()
    };
    var oneSinkValidation = validator.Validate(oneSink, context);
    Assert(!oneSinkValidation.Valid && oneSinkValidation.Errors.Any(error =>
            error.Code == "MARKET_COIN_SINKS_REQUIRED" &&
            error.Message.Contains("STARTER-CAPTURE", StringComparison.Ordinal)),
        "A modern policy with fewer than two eligible MarketCoin sinks was not blocked with its concrete SKU.");

    var unlimitedSink = definition with
    {
        Products = definition.Products.Select(product =>
            string.Equals(product.Sku, "BUILDER-REPAIR", StringComparison.OrdinalIgnoreCase)
                ? product with { PurchaseLimitPerSeason = null }
                : product).ToArray()
    };
    var unlimited = validator.Validate(unlimitedSink, context);
    Assert(!unlimited.Valid && unlimited.Errors.Any(error =>
            error.Code == "UNBOUNDED_MARKET_COIN_SINK" &&
            error.Message.Contains("BUILDER-REPAIR", StringComparison.Ordinal)),
        "An active MarketCoin SKU without a personal weekly limit did not fail closed.");

    var legacy = validator.Validate(definition with { BalancePolicy = null }, context);
    Assert(legacy.Warnings.Any(warning => warning.Code == "PERMANENT_CURRENCY_CONTRACT_NOT_ENFORCED"),
        "Legacy content did not explicitly disclose that the modern permanent-currency contract was skipped.");
}

static async Task VerifyReliableTaskRewardReplayAsync(
    string directory,
    CancellationToken cancellationToken)
{
    Directory.CreateDirectory(directory);
    var now = DateTimeOffset.Parse("2026-07-16T08:00:00Z");
    var task = new ContentTaskDefinition(
        "daily-market-source",
        "Daily source",
        "Authoritative settled exchange",
        ContentTaskCadence.Daily,
        ContentTaskEventKind.ResourceExchangeSettled,
        1,
        null,
        null,
        [],
        new ContentTaskReward(ExtractionCurrency.MarketCoin, 10, 0),
        true);
    var definition = new EconomyContentDefinition(
        1,
        "local",
        "Reliable MarketCoin source",
        new EconomyContentDependencies("weekly-economy-v1", "catalog", "game", "plugin"),
        "UTC",
        0,
        [],
        [],
        [],
        [task],
        new ContentRotationPolicy(
            "weekly-economy-v1", 1, "permanent-currency-test",
            [task.TaskKey], 1, [], 0, [], 0));
    var version = new EconomyContentVersion(
        Guid.NewGuid(),
        definition.ServerId,
        1,
        DateOnly.FromDateTime(now.UtcDateTime),
        definition.Dependencies.RulesVersion,
        new string('a', 64),
        definition,
        Guid.NewGuid(),
        "test",
        now.AddHours(-1));
    var runtime = new EconomyRuntimeContent(
        version,
        EconomyContentRotation.Create(version),
        new Dictionary<string, ContentProductDefinition>(),
        new Dictionary<string, ContentResourceDefinition>(),
        [],
        new HashSet<string>());

    using var repository = new SqliteExtractionRepository(directory);
    using var taskStore = new SqliteReliableTaskStore(directory);
    var season = await CreateSeasonAsync(repository, now, cancellationToken);
    var account = await repository.GetOrCreateAccountAsync(
        "steam", "steam_permanent_source", "Permanent source", cancellationToken);
    var service = new ReliableTaskRuntimeService(
        taskStore,
        new ExtractionCommerceService(repository),
        new FixedContentProvider(runtime));
    var initial = await service.GetSnapshotAsync(
        account.AccountId, season.SeasonId, "local", cancellationToken);
    Assert(initial.Tasks.Count == 1, "The cadence-bounded daily source did not create exactly one task instance.");

    var runId = Guid.NewGuid();
    var settled = new ExtractionSettlementRun(
        runId,
        account.AccountId,
        season.SeasonId,
        "steam_permanent_source",
        "exchange-a",
        "Exchange A",
        ExtractionSettlementState.Settled,
        [new ExtractionLootLine("Leather", "Leather", 1, 2, 2)],
        1,
        2,
        new string('b', 64),
        null,
        $"settlement:{runId:N}",
        null,
        null,
        now.AddMinutes(-1),
        now.AddMinutes(1),
        now,
        now)
    {
        ContentVersionId = version.VersionId,
        ContentHash = version.ContentHash,
        ContentBusinessDate = version.BusinessDate,
        ContentRulesVersion = version.RulesVersion,
        RotationSeed = runtime.Rotation.Seed
    };
    for (var replay = 0; replay < 20; replay++)
    {
        var result = await service.RecordResourceSettlementAsync(settled, cancellationToken);
        Assert(replay == 0 ? result.Applied : result.Replayed,
            "The authoritative source event did not retain 20x replay semantics.");
    }

    var wallet = await repository.GetWalletAsync(
        account.AccountId, season.SeasonId, cancellationToken);
    var ledger = await repository.GetLedgerAsync(
        account.AccountId, season.SeasonId, 100, cancellationToken);
    var snapshot = await service.GetSnapshotAsync(
        account.AccountId, season.SeasonId, "local", cancellationToken);
    Assert(wallet.MarketCoin.Balance == 10 &&
           ledger.Count(entry => entry.ReferenceType == "reliable-task-reward") == 1 &&
           snapshot.Tasks.Single().RewardGranted,
        "Twenty event deliveries created more than one MarketCoin reward for one daily task instance.");
}

static async Task VerifyShopDebitLimitAndIdempotencyAsync(
    string directory,
    CancellationToken cancellationToken)
{
    Directory.CreateDirectory(directory);
    using var repository = new SqliteExtractionRepository(directory);
    var now = DateTimeOffset.UtcNow;
    var season = await CreateSeasonAsync(repository, now, cancellationToken);
    var account = await repository.GetOrCreateAccountAsync(
        "steam", "steam_permanent_sink", "Permanent sink", cancellationToken);
    var seed = await repository.AdjustWalletAsync(
        new WalletAdjustmentRequest(
            account.AccountId,
            null,
            ExtractionCurrency.MarketCoin,
            1_000,
            "permanent currency sink fixture",
            "test",
            "seed",
            "permanent-currency-contract",
            "permanent-sink-seed"),
        cancellationToken);
    Assert(seed.Created, "The MarketCoin sink fixture was not funded.");
    await repository.UpsertProductAsync(
        new ShopProductDefinition(
            "BOUND-SINK",
            "Bound sink",
            "Meaningful recurring sink",
            ExtractionCurrency.MarketCoin,
            120,
            [new ShopItemGrant("PalSphere", 10)],
            2,
            true,
            null,
            null),
        null,
        "test",
        cancellationToken);

    ShopPurchaseRequest Request(string key, int quantity = 1) => new(
        account.AccountId,
        season.SeasonId,
        "local",
        "steam_permanent_sink",
        [new ShopPurchaseLineInput("BOUND-SINK", quantity)],
        key,
        "permanent-currency-contract",
        "bounded sink purchase");

    var replayRequest = Request("bounded-sink-purchase-1");
    var replays = await Task.WhenAll(Enumerable.Range(0, 20)
        .Select(_ => repository.PurchaseAsync(replayRequest, cancellationToken)));
    Assert(replays.Count(result => result.Created) == 1 &&
           replays.Select(result => result.Order?.OrderId).Distinct().Count() == 1,
        "Twenty same-key sink purchases did not collapse to one durable debit/order.");
    var second = await repository.PurchaseAsync(Request("bounded-sink-purchase-2"), cancellationToken);
    var overLimit = await repository.PurchaseAsync(Request("bounded-sink-purchase-3"), cancellationToken);
    var conflict = await repository.PurchaseAsync(Request("bounded-sink-purchase-1", 2), cancellationToken);
    var wallet = await repository.GetWalletAsync(
        account.AccountId, season.SeasonId, cancellationToken);
    Assert(second.Created &&
           overLimit.ErrorCode == "PURCHASE_LIMIT_EXCEEDED" &&
           conflict.IdempotencyConflict && conflict.ErrorCode == "IDEMPOTENCY_CONFLICT" &&
           wallet.MarketCoin.Balance == 760,
        "MarketCoin sink debit, idempotency conflict, or personal-limit enforcement changed unexpectedly.");
}

static async Task<ExtractionSeason> CreateSeasonAsync(
    SqliteExtractionRepository repository,
    DateTimeOffset now,
    CancellationToken cancellationToken) =>
    await repository.UpsertSeasonAsync(
        null,
        new ExtractionSeasonDefinition(
            "local",
            $"WEEK-{Guid.NewGuid():N}",
            "Permanent currency contract week",
            Guid.NewGuid().ToString("N"),
            now.AddDays(-1),
            now.AddDays(6),
            ExtractionSeasonState.Active),
        null,
        cancellationToken);

static (EconomyContentDefinition Definition, EconomyContentValidationContext Context)
    CreateSchemeADefault()
{
    var itemIds = new[]
    {
        "PalSphere", "Baked_Berries", "Herbs", "Medicines", "PalSphere_Mega",
        "RoughBullet", "BowGun", "Arrow", "RepairKit", "Cement", "BerrySeeds",
        "WheatSeeds", "PalSphere_Giga", "HandgunBullet", "AssaultRifleBullet",
        "Wood", "Stone", "Fiber", "Pal_crystal_S", "Leather", "Bone", "Cloth",
        "CopperOre", "CopperIngot", "Coal", "Sulfur", "Quartz", "PalOil", "Polymer",
        "CarbonFiber", "MachineParts2", "PalCrystal_Ex", "AncientParts2", "MeteorDrop",
        "CrudeOil", "Diamond", "Ruby", "Sapphire", "Eemerald", "Horn", "Wool",
        "Cloth2", "Charcoal", "MachineParts", "IronIngot", "StealIngot",
        "Processed_Wood", "HighGrade_Processed_Wood", "Wood_Fine", "Wood_Ancient",
        "BeastBone_Ancient", "AncientParts3", "ManganeseOre", "ManganeseIngot",
        "PalDarkParts", "YakushimaIngot001", "RainbowCrystal", "Wood_WorldTree",
        "NightStone", "WorldTreeOre", "WorldTreeIngot", "PredatorCrystal",
        "SkyIslandOre", "SkyislandIngot", "Thermal_Core", "AIcore"
    };
    var catalog = new GameResourceCatalog(
        "1",
        "catalog-permanent-currency-v1",
        DateTimeOffset.Parse("2026-07-16T00:00:00Z"),
        new GameCatalogSource("test", "test", "about:blank", "about:blank", "about:blank"),
        new GameCatalogCoverage("test", "test", "test", "test", "test"),
        itemIds.Select(id => new GameCatalogEntry(id, id, "test")).ToArray(),
        [], [], [], []);
    var options = new ExtractionModeOptions
    {
        Enabled = true,
        ServerId = "local",
        BootstrapPolicyVersion = "activity-v1",
        InitialMarketCoin = 0,
        InitialSeasonVoucher = 0,
        ExtractionZones =
        [
            new ExtractionZoneOptions(),
            new ExtractionZoneOptions
            {
                Id = "dev-extract-candidate-2",
                DisplayName = "Candidate zone 2",
                RouteHint = "Contract route",
                RiskHint = "Contract risk",
                MapX = 348,
                MapY = -504,
                Radius = 100
            }
        ]
    };
    var safety = new EconomySafetyOptions
    {
        ApprovedGameVersion = "1.0.0-test",
        ApprovedPalDefenderVersion = "1.8.1-test"
    };
    var definition = EconomyContentDefaults.Create(options, safety, catalog);
    var context = new EconomyContentValidationContext(
        itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>([EconomyContentRuntimeService.SupportedRulesVersion], StringComparer.Ordinal),
        catalog.Revision,
        safety.ApprovedGameVersion,
        safety.ApprovedPalDefenderVersion);
    return (definition, context);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class FixedContentProvider : IReliableTaskContentProvider
{
    private readonly EconomyRuntimeContent _content;

    public FixedContentProvider(EconomyRuntimeContent content)
    {
        _content = content;
    }

    public Task<EconomyRuntimeContent> GetCurrentAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_content);

    public Task<EconomyRuntimeContent> GetForEventAsync(
        DateTimeOffset occurredAt,
        Guid? contentVersionId,
        string? contentHash,
        CancellationToken cancellationToken) =>
        Task.FromResult(_content);
}

using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var directory = Path.Combine(Path.GetTempPath(), $"pal-control-content-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    VerifySchemeADefaults();
    await VerifyContentLifecycleAsync(directory, CancellationToken.None);
    Console.WriteLine(
        "PASS: content validation, semantic diff, canonical hash, 20x publish replay, atomic publish faults, immutable versions, current-pointer rollback, stale-offer rejection, and restart persistence.");
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

static void VerifySchemeADefaults()
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
        "catalog-default-test-v1",
        DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
        new GameCatalogSource("test", "test", "about:blank", "about:blank", "about:blank"),
        new GameCatalogCoverage("test", "test", "test", "test", "test"),
        itemIds.Select(id => new GameCatalogEntry(id, id, "test")).ToArray(),
        [],
        [],
        [],
        []);
    var options = new ExtractionModeOptions
    {
        Enabled = true,
        ServerId = "local",
        BootstrapPolicyVersion = "legacy-v1",
        InitialMarketCoin = 1_000,
        InitialSeasonVoucher = 300,
        ExtractionZones = [new ExtractionZoneOptions()]
    };
    var safety = new EconomySafetyOptions
    {
        ApprovedGameVersion = "1.0.0-test",
        ApprovedPalDefenderVersion = "1.8.1-test"
    };
    var definition = EconomyContentDefaults.Create(options, safety, catalog);
    var validation = new EconomyContentDefinitionValidator().Validate(
        definition,
        new EconomyContentValidationContext(
            itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([EconomyContentRuntimeService.SupportedRulesVersion], StringComparer.Ordinal),
            catalog.Revision,
            safety.ApprovedGameVersion,
            safety.ApprovedPalDefenderVersion));
    Assert(validation.Valid, "The built-in Scheme A content failed its own strict validation.");
    Assert(definition.Products.Count == 10 && definition.Resources.Count >= 50 &&
           definition.Tasks.Count == 6 && definition.ExchangeZones.Count >= 1,
        "The built-in Scheme A content does not contain its launch products, resources, tasks, and zone.");
    Assert(definition.Products.All(product =>
            !string.IsNullOrWhiteSpace(product.Category) && product.Tags.Count > 0),
        "A built-in product is missing explicit category or tags.");
    Assert(definition.Resources.All(resource => resource.ExchangeZoneIds.Count > 0),
        "A built-in sellable resource is not assigned to an exchange zone.");

    var version = new EconomyContentVersion(
        Guid.NewGuid(), "local", 1, new DateOnly(2026, 7, 15),
        EconomyContentRuntimeService.SupportedRulesVersion,
        validation.ContentHash, definition, Guid.NewGuid(), "test", DateTimeOffset.UtcNow);
    Assert(EconomyContentEvidence.MatchesCurrent(
               version.VersionId, version.ContentHash, version),
        "Matching resource-quote content evidence was rejected.");
    Assert(!EconomyContentEvidence.MatchesCurrent(
               Guid.NewGuid(), version.ContentHash, version) &&
           !EconomyContentEvidence.MatchesCurrent(
               version.VersionId, new string('0', 64), version) &&
           !EconomyContentEvidence.MatchesCurrent(null, null, version),
        "Stale or missing resource-quote content evidence was accepted.");
    var product = definition.Products[0];
    var first = EconomyContentRuntimeService.CalculateEffectiveUnitPrice(version, product);
    var second = EconomyContentRuntimeService.CalculateEffectiveUnitPrice(version, product);
    Assert(first == second && first >= product.UnitPrice * 90 / 100 &&
           first <= (product.UnitPrice * 110 + 99) / 100,
        "Daily effective price is not deterministic or escaped the 90%-110% range.");

    var zone = definition.ExchangeZones[0];
    var zoneTimeZone = options.ResolveTimeZone();
    Assert(EconomyContentSchedule.IsOpen(
            zone,
            DateTimeOffset.Parse("2026-07-15T08:00:00Z"),
            zoneTimeZone),
        "The default exchange zone is unexpectedly closed during its all-day schedule.");
}

static async Task VerifyContentLifecycleAsync(string directory, CancellationToken cancellationToken)
{
    var fault = new OneShotContentFaultInjector();
    var validationContext = ValidationContext();
    var firstDefinition = ValidDefinition();
    var businessDate = new DateOnly(2026, 7, 15);
    Guid firstVersionId;
    Guid secondVersionId;
    string firstHash;

    await using (var store = new SqliteEconomyContentStore(directory, faultInjector: fault))
    {
        await VerifyValidationFailuresAsync(store, firstDefinition, validationContext, businessDate, cancellationToken);

        var firstDraft = await store.CreateDraftAsync(
            "local",
            "launch-content",
            null,
            firstDefinition,
            "content-admin:test",
            cancellationToken);
        var validation = await store.ValidateDraftAsync(
            firstDraft.DraftId,
            firstDraft.Revision,
            validationContext,
            cancellationToken);
        Assert(validation.Valid && validation.Errors.Count == 0, "The valid content document did not pass validation.");
        firstHash = validation.ContentHash;

        var reordered = firstDefinition with
        {
            Products = firstDefinition.Products.Reverse().ToArray(),
            Resources = firstDefinition.Resources.Reverse().ToArray(),
            ExchangeZones = firstDefinition.ExchangeZones.Reverse().ToArray(),
            Tasks = firstDefinition.Tasks.Reverse().ToArray(),
            Rotation = firstDefinition.Rotation with
            {
                DailyTaskPool = firstDefinition.Rotation.DailyTaskPool.Reverse().ToArray(),
                WeeklyTaskPool = firstDefinition.Rotation.WeeklyTaskPool.Reverse().ToArray(),
                HotspotZonePool = firstDefinition.Rotation.HotspotZonePool.Reverse().ToArray()
            }
        };
        Assert(
            EconomyContentCanonicalizer.Hash(reordered) == firstHash,
            "Canonical content hash changed when unordered definition collections were reordered.");

        var firstPublish = await store.PublishAsync(
            firstDraft.DraftId,
            firstDraft.Revision,
            businessDate,
            "launch-publish-00",
            "content-admin:test",
            validationContext,
            cancellationToken);
        Assert(firstPublish.VersionCreated && firstPublish.PointerChanged && !firstPublish.Replayed,
            "The first complete version was not atomically published.");
        firstVersionId = firstPublish.Version.VersionId;

        for (var replay = 1; replay < 20; replay++)
        {
            var result = await store.PublishAsync(
                firstDraft.DraftId,
                firstDraft.Revision,
                businessDate,
                $"launch-publish-{replay:00}",
                "content-admin:test",
                validationContext,
                cancellationToken);
            Assert(result.Version.VersionId == firstVersionId && result.Replayed && !result.VersionCreated,
                "Repeated publication did not replay the one immutable version.");
        }
        Assert((await store.ListVersionsAsync("local", cancellationToken)).Count == 1,
            "Twenty repeated publications created more than one version.");

        var duplicateDraft = await store.CreateDraftAsync(
            "local",
            "same-content-second-draft",
            firstVersionId,
            reordered,
            "content-admin:test",
            cancellationToken);
        var duplicatePublish = await store.PublishAsync(
            duplicateDraft.DraftId,
            duplicateDraft.Revision,
            businessDate,
            "duplicate-content-draft",
            "content-admin:test",
            validationContext,
            cancellationToken);
        Assert(!duplicatePublish.VersionCreated && duplicatePublish.Version.VersionId == firstVersionId,
            "Same-day canonical content from a second draft was not de-duplicated.");

        var secondDefinition = firstDefinition with
        {
            DisplayName = "Weekly economy content v2",
            Products = firstDefinition.Products.Select(product =>
                product.Sku == "STARTER-CAPTURE" ? product with { UnitPrice = 135 } : product).ToArray()
        };
        var secondDraft = await store.CreateDraftAsync(
            "local",
            "price-balance-v2",
            firstVersionId,
            secondDefinition,
            "content-admin:test",
            cancellationToken);
        var diff = await store.DiffDraftAsync(secondDraft.DraftId, cancellationToken);
        Assert(diff.Any(entry => entry.Path.EndsWith("/unitPrice", StringComparison.Ordinal) &&
                                 entry.Kind == ContentDiffKind.Changed &&
                                 entry.Before == "120" && entry.After == "135"),
            "Draft diff did not identify the product price change.");

        fault.Arm(ContentPublishFaultPoint.AfterVersionInserted);
        await AssertThrowsAsync<InvalidOperationException>(() => store.PublishAsync(
            secondDraft.DraftId,
            secondDraft.Revision,
            businessDate,
            "publish-v2-fault-version",
            "content-admin:test",
            validationContext,
            cancellationToken));
        Assert((await store.ListVersionsAsync("local", cancellationToken)).Count == 1,
            "A failed publication leaked a partial immutable version.");
        Assert((await store.GetCurrentAsync("local", cancellationToken))?.VersionId == firstVersionId,
            "A failure before pointer update changed the current version.");

        fault.Arm(ContentPublishFaultPoint.AfterPointerUpdated);
        await AssertThrowsAsync<InvalidOperationException>(() => store.PublishAsync(
            secondDraft.DraftId,
            secondDraft.Revision,
            businessDate,
            "publish-v2-fault-pointer",
            "content-admin:test",
            validationContext,
            cancellationToken));
        Assert((await store.ListVersionsAsync("local", cancellationToken)).Count == 1,
            "A pointer-update failure leaked a partial immutable version.");
        Assert((await store.GetCurrentAsync("local", cancellationToken))?.VersionId == firstVersionId,
            "A failed transaction exposed the new current pointer.");

        var secondPublish = await store.PublishAsync(
            secondDraft.DraftId,
            secondDraft.Revision,
            businessDate,
            "publish-v2-success",
            "content-admin:test",
            validationContext,
            cancellationToken);
        secondVersionId = secondPublish.Version.VersionId;
        Assert(secondVersionId != firstVersionId && secondPublish.Version.VersionNumber == 2,
            "The changed document did not create immutable version 2.");

        var staleDraft = await store.CreateDraftAsync(
            "local",
            "stale-base-v1",
            firstVersionId,
            firstDefinition with { DisplayName = "stale content" },
            "content-admin:test",
            cancellationToken);
        await AssertContentErrorAsync(
            "CONTENT_POINTER_CONFLICT",
            () => store.PublishAsync(
                staleDraft.DraftId,
                staleDraft.Revision,
                businessDate,
                "stale-base-publish",
                "content-admin:test",
                validationContext,
                cancellationToken));
        Assert((await store.GetCurrentAsync("local", cancellationToken))?.VersionId == secondVersionId,
            "Publishing a stale-base draft changed the current pointer.");

        var rotationA = EconomyContentRotation.Create(secondPublish.Version);
        var rotationB = EconomyContentRotation.Create(secondPublish.Version);
        Assert(rotationA == rotationB && rotationA.CurrentContentVersionId == secondVersionId &&
               rotationA.BusinessDate == businessDate && rotationA.ContentHash == secondPublish.Version.ContentHash,
            "Rotation identity is not deterministic or is missing current-version evidence.");

        await AssertContentErrorAsync("OFFER_NOT_AVAILABLE", () => store.ResolveCurrentProductAsync(
            "local", firstVersionId, firstHash, "STARTER-CAPTURE", cancellationToken));
        var currentProduct = await store.ResolveCurrentProductAsync(
            "local",
            secondVersionId,
            secondPublish.Version.ContentHash,
            "STARTER-CAPTURE",
            cancellationToken);
        Assert(currentProduct.UnitPrice == 135 && currentProduct.Category == "Starter" &&
               currentProduct.Tags.Contains("recommended", StringComparer.Ordinal),
            "Current-version product metadata was not preserved.");

        fault.Arm(ContentPublishFaultPoint.AfterPointerUpdated);
        await AssertThrowsAsync<InvalidOperationException>(() => store.RollbackAsync(
            "local",
            firstVersionId,
            secondVersionId,
            "rollback-fault",
            "content-admin:test",
            cancellationToken));
        Assert((await store.GetCurrentAsync("local", cancellationToken))?.VersionId == secondVersionId,
            "A failed rollback exposed a partial pointer update.");

        var rollback = await store.RollbackAsync(
            "local",
            null,
            secondVersionId,
            "rollback-to-previous",
            "content-admin:test",
            cancellationToken);
        Assert(rollback.Pointer.VersionId == firstVersionId && rollback.PointerChanged && !rollback.Replayed,
            "Rollback did not restore the previous complete content version.");
        for (var replay = 0; replay < 20; replay++)
        {
            var replayed = await store.RollbackAsync(
                "local",
                null,
                secondVersionId,
                "rollback-to-previous",
                "content-admin:test",
                cancellationToken);
            Assert(replayed.Replayed && replayed.Pointer.VersionId == firstVersionId,
                "Rollback idempotency replay changed the current pointer.");
        }
        await AssertContentErrorAsync("OFFER_NOT_AVAILABLE", () => store.ResolveCurrentProductAsync(
            "local",
            secondVersionId,
            secondPublish.Version.ContentHash,
            "STARTER-CAPTURE",
            cancellationToken));
        _ = await store.ResolveCurrentProductAsync(
            "local", firstVersionId, firstHash, "STARTER-CAPTURE", cancellationToken);
    }

    await using (var restarted = new SqliteEconomyContentStore(directory))
    {
        var current = await restarted.GetCurrentAsync("local", cancellationToken);
        var versions = await restarted.ListVersionsAsync("local", cancellationToken);
        Assert(current?.VersionId == firstVersionId && versions.Count == 2 &&
               versions.Single(version => version.VersionId == secondVersionId).Definition.Products
                   .Single(product => product.Sku == "STARTER-CAPTURE").UnitPrice == 135,
            "Published content versions or the rollback pointer did not survive restart.");
    }
}

static async Task VerifyValidationFailuresAsync(
    SqliteEconomyContentStore store,
    EconomyContentDefinition valid,
    EconomyContentValidationContext context,
    DateOnly businessDate,
    CancellationToken cancellationToken)
{
    var duplicate = valid.Products[0] with
    {
        UnitPrice = -1,
        ItemGrants = [new ContentItemGrant("UnknownItem", 1)]
    };
    var invalid = valid with
    {
        Dependencies = new EconomyContentDependencies("", "", "", ""),
        Products = [duplicate, duplicate],
        Resources =
        [
            valid.Resources[0] with
            {
                ItemId = "UnknownResource",
                UnitValue = -10,
                ExchangeZoneIds = ["retired-zone"]
            }
        ],
        ExchangeZones =
        [
            valid.ExchangeZones[0] with { ZoneId = "retired-zone", Radius = -1, Active = false }
        ],
        Rotation = valid.Rotation with { RulesVersion = "", HotspotZonePool = ["retired-zone"] }
    };
    var draft = await store.CreateDraftAsync(
        "local", "invalid-draft", null, invalid, "content-admin:test", cancellationToken);
    var validation = await store.ValidateDraftAsync(draft.DraftId, draft.Revision, context, cancellationToken);
    var codes = validation.Errors.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);
    foreach (var expected in new[]
             {
                 "DUPLICATE_SKU",
                 "UNKNOWN_ITEM",
                 "NEGATIVE_OR_INVALID_PRICE",
                 "NEGATIVE_OR_INVALID_RESOURCE_VALUE",
                 "INVALID_OR_INACTIVE_ZONE",
                 "INVALID_ZONE_GEOMETRY",
                 "RULES_VERSION_REQUIRED",
                 "RESOURCE_CATALOG_REVISION_REQUIRED",
                 "GAME_VERSION_REQUIRED",
                 "PALDEFENDER_VERSION_REQUIRED"
             })
    {
        Assert(codes.Contains(expected), $"Validation did not report required error code {expected}.");
    }
    await AssertThrowsAsync<ContentValidationException>(() => store.PublishAsync(
        draft.DraftId,
        draft.Revision,
        businessDate,
        "invalid-publish",
        "content-admin:test",
        context,
        cancellationToken));
    Assert(await store.GetCurrentAsync("local", cancellationToken) is null,
        "An invalid draft changed the current pointer.");
}

static EconomyContentDefinition ValidDefinition()
{
    var allWeek = Enum.GetValues<DayOfWeek>()
        .Select(day => new ContentExchangeWindow(day, new TimeOnly(4, 0), new TimeOnly(3, 59), 60))
        .ToArray();
    var zones = new[]
    {
        new ContentExchangeZoneDefinition(
            "coastal-depot", "Coastal Depot", "Travel to the coastal marker.",
            248, -504, 100, 10_000, allWeek, true),
        new ContentExchangeZoneDefinition(
            "volcano-depot", "Volcano Depot", "Travel to the volcano marker.",
            -420, 610, 120, 12_000, allWeek, true)
    };
    var tasks = new[]
    {
        TaskDefinition("daily-exchange", ContentTaskCadence.Daily, ContentTaskEventKind.ResourceExchangeSettled, 1),
        TaskDefinition("daily-stone", ContentTaskCadence.Daily, ContentTaskEventKind.ResourceItemSettled, 100, "Stone"),
        TaskDefinition("daily-spend", ContentTaskCadence.Daily, ContentTaskEventKind.CurrencySpent, 100,
            currency: ExtractionCurrency.MarketCoin),
        TaskDefinition("weekly-value", ContentTaskCadence.Weekly, ContentTaskEventKind.ResourceValueSettled, 2_000),
        TaskDefinition("weekly-orders", ContentTaskCadence.Weekly, ContentTaskEventKind.ShopOrderDelivered, 3),
        TaskDefinition("weekly-wood", ContentTaskCadence.Weekly, ContentTaskEventKind.ResourceItemSettled, 500, "Wood")
    };
    return new EconomyContentDefinition(
        1,
        "local",
        "Weekly economy content v1",
        new EconomyContentDependencies("weekly-economy-v1", "catalog-test-v1", "1.0.0-test", "1.8.1-test"),
        "Asia/Shanghai",
        4,
        [
            new ContentProductDefinition(
                "STARTER-CAPTURE", "Starter capture supplies", "A starter bundle.",
                "Starter", ["recommended", "capture"], 1,
                ExtractionCurrency.MarketCoin, 120,
                [new("PalSphere", 10), new("Baked_Berries", 20), new("Herbs", 3)],
                3, null, true, null, null),
            new ContentProductDefinition(
                "ARROW-CRATE", "Arrow crate", "A weekly arrow supply.",
                "Ammunition", ["ranged"], null,
                ExtractionCurrency.SeasonVoucher, 70,
                [new("Arrow", 100)],
                10, 500, true, null, null)
        ],
        [
            new ContentResourceDefinition(
                "Stone", "Stone", "RawMaterial", ["basic"], ExtractionCurrency.SeasonVoucher,
                2, ["coastal-depot", "volcano-depot"], true),
            new ContentResourceDefinition(
                "Wood", "Wood", "RawMaterial", ["basic"], ExtractionCurrency.SeasonVoucher,
                1, ["coastal-depot"], true)
        ],
        zones,
        tasks,
        new ContentRotationPolicy(
            "weekly-economy-v1",
            1,
            "scheme-a-rotation-v1",
            ["daily-exchange", "daily-stone", "daily-spend"],
            2,
            ["weekly-value", "weekly-orders", "weekly-wood"],
            2,
            ["coastal-depot", "volcano-depot"],
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
        $"Complete {key} using server-observed economy evidence.",
        cadence,
        eventKind,
        target,
        itemId,
        currency,
        [],
        new ContentTaskReward(ExtractionCurrency.MarketCoin, 10, 5),
        true);

static EconomyContentValidationContext ValidationContext() => new(
    new HashSet<string>(
        ["PalSphere", "Baked_Berries", "Herbs", "Arrow", "Stone", "Wood"],
        StringComparer.OrdinalIgnoreCase),
    new HashSet<string>(["weekly-economy-v1"], StringComparer.Ordinal),
    "catalog-test-v1",
    "1.0.0-test",
    "1.8.1-test");

static async Task AssertContentErrorAsync(string code, Func<Task> action)
{
    try
    {
        await action();
    }
    catch (ContentStoreException exception) when (exception.Code == code)
    {
        return;
    }
    throw new InvalidOperationException($"Expected content error {code}.");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
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

sealed class OneShotContentFaultInjector : IContentPublishFaultInjector
{
    private ContentPublishFaultPoint? _armed;

    public void Arm(ContentPublishFaultPoint point) => _armed = point;

    public void ThrowIfRequested(ContentPublishFaultPoint point)
    {
        if (_armed != point)
        {
            return;
        }
        _armed = null;
        throw new InvalidOperationException($"Injected content publication fault at {point}.");
    }
}

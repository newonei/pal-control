using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var directory = Path.Combine(Path.GetTempPath(), $"pal-control-content-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    VerifySchemeADefaults();
    VerifyPresentationValidation();
    VerifyRotationAndScheduleSemantics();
    await VerifyContentLifecycleAsync(directory, CancellationToken.None);
    Console.WriteLine(
        "PASS: content validation, legacy hash compatibility, deterministic 20x dynamic-zone/event replay, adjacent-day rotation, risk levels, timed-hotspot and discount/yield authority, close/grace boundaries, next-open projection, semantic diff, atomic publish faults, immutable versions, current-pointer rollback, stale-offer rejection, and restart persistence.");
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
        ExtractionZones =
        [
            new ExtractionZoneOptions(),
            new ExtractionZoneOptions
            {
                Id = "dev-extract-candidate-2",
                DisplayName = "Development candidate zone 2",
                RouteHint = "Local deterministic rotation test route; production coordinates remain unverified.",
                RiskHint = "Candidate route and risk remain subject to real-server acceptance.",
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
    var validationContext = new EconomyContentValidationContext(
            itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([EconomyContentRuntimeService.SupportedRulesVersion], StringComparer.Ordinal),
            catalog.Revision,
            safety.ApprovedGameVersion,
            safety.ApprovedPalDefenderVersion);
    var validator = new EconomyContentDefinitionValidator();
    var validation = validator.Validate(definition, validationContext);
    Assert(validation.Valid, "The built-in Scheme A content failed its own strict validation.");
    Assert(definition.Products.Count == 10 && definition.Resources.Count(resource => resource.Active) == 51 &&
           definition.Tasks.Count == 6 && definition.ExchangeZones.Count >= 2 &&
           definition.Rotation.HotspotZonePool.Count >= 2,
        "The built-in Scheme A content does not contain its 10 launch products, 51 active resources, tasks, and two-zone hotspot pool.");
    Assert(definition.Products.All(product =>
            !string.IsNullOrWhiteSpace(product.Category) && product.Tags.Count > 0 &&
            EconomyContentPresentation.IsSafeIconKey(product.IconKey) &&
            product.Rarity is not null && Enum.IsDefined(product.Rarity.Value) &&
            EconomyContentPresentation.IsSafeUsage(product.Usage)),
        "A built-in product is missing safe, finite presentation metadata.");
    Assert(definition.Resources.All(resource => resource.ExchangeZoneIds.Count > 0),
        "A built-in sellable resource is not assigned to an exchange zone.");
    Assert(definition.Resources.Where(resource => resource.Active).All(resource =>
            EconomyContentPresentation.IsSafeIconKey(resource.IconKey) &&
            resource.Rarity is not null && Enum.IsDefined(resource.Rarity.Value) &&
            EconomyContentPresentation.IsSafeUsage(resource.Usage)),
        "A built-in active resource is missing safe, finite presentation metadata.");
    Assert(definition.ExchangeZones.All(zone => !string.IsNullOrWhiteSpace(zone.RouteHint) &&
                                                !string.IsNullOrWhiteSpace(zone.RiskHint) &&
                                                zone.OpenWindows.Count > 0),
        "A built-in exchange zone is missing route, risk, or opening-window guidance.");
    var balancePolicy = definition.BalancePolicy;
    Assert(balancePolicy is not null &&
           balancePolicy.CurrencyShadowRates.Count == Enum.GetValues<ExtractionCurrency>().Length &&
           balancePolicy.ResourceShadowCosts.Count == definition.Resources.Count(resource => resource.Active) &&
           balancePolicy.Transformations.Count > 0 &&
           balancePolicy.Attestation is
           {
               ReachableGraphComplete: true,
               EvidenceKind: EconomyArbitrageGraphAnalyzer.OperationalShadowGraphEvidenceKind
           },
        "The built-in Scheme A content is missing its complete attested economic shadow policy.");
    Assert(balancePolicy!.Transformations.All(transformation =>
            transformation.EvidenceNote.Contains("不是 Palworld 实际制作配方", StringComparison.Ordinal)),
        "A built-in shadow transformation is not explicitly distinguished from a real game recipe.");
    Assert(!validation.Warnings.Any(warning => warning.Code == "BALANCE_POLICY_NOT_CONFIGURED" ||
                                               warning.Code == "INDIRECT_ARBITRAGE_NOT_EVALUATED" ||
                                               warning.Code == "DYNAMIC_ECONOMY_POLICY_NOT_CONFIGURED"),
        "The built-in attested policy fell back to legacy direct-only analysis.");

    var lineLimitItemIds = Enumerable.Range(0, ExtractionRunStore.MaximumSelectionLines + 1)
        .Select(index => $"SelectionLimitItem{index:000}")
        .ToArray();
    var lineLimitResources = lineLimitItemIds.Select(itemId =>
        definition.Resources[0] with
        {
            ItemId = itemId,
            DisplayName = itemId,
            ExchangeZoneIds = [definition.ExchangeZones[0].ZoneId]
        }).ToArray();
    var lineLimitValidation = validator.Validate(
        definition with
        {
            Resources = lineLimitResources,
            BalancePolicy = null
        },
        validationContext with
        {
            KnownItemIds = itemIds.Concat(lineLimitItemIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
        });
    Assert(lineLimitValidation.Errors.Any(error =>
            error.Code == "ZONE_RESOURCE_SELECTION_LIMIT_EXCEEDED"),
        "A zone exposing more ItemIDs than one atomic selective settlement accepted was publishable.");

    var dynamicPolicy = definition.DynamicEconomyPolicy
        ?? throw new InvalidOperationException("The built-in content is missing its dynamic policy.");
    Assert(dynamicPolicy is
           {
               ZonePool.Count: >= 2,
               DailyOpenZoneCount: 1,
               TimedHotspotCount: 1,
               TimedHotspotDurationMinutes: 180,
               DailyWorldEventCount: 1
           } &&
           dynamicPolicy.ZonePool.Select(rule => rule.RiskLevel).Distinct().Count() >= 2 &&
           dynamicPolicy.WorldEvents.Any(worldEvent =>
               worldEvent.Kind == ContentWorldEventKind.ResourceSurge &&
               worldEvent.ZoneYieldMultiplierBasisPoints > 10_000) &&
           dynamicPolicy.WorldEvents.Any(worldEvent =>
               worldEvent.Kind == ContentWorldEventKind.SupplyRelief &&
               worldEvent.ProductPriceMultiplierBasisPoints < 10_000),
        "The built-in content is missing two risk-rated candidates, one timed hotspot, or two purely economic event variants.");

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
    Assert(first == second && first >= product.UnitPrice * 81 / 100 &&
           first <= (product.UnitPrice * 110 + 99) / 100,
        "Daily/event effective price is not deterministic or escaped the modeled 81%-110% range.");

    var dynamicEvidence = EconomyDynamicEconomyRuntime.Create(version, options.ResolveTimeZone())
        ?? throw new InvalidOperationException("Built-in dynamic economy evidence was not created.");
    var serializedEvidence = System.Text.Json.JsonSerializer.Serialize(
        dynamicEvidence,
        EconomyContentJson.Options);
    for (var replay = 0; replay < 20; replay++)
    {
        Assert(System.Text.Json.JsonSerializer.Serialize(
                   EconomyDynamicEconomyRuntime.Create(version, options.ResolveTimeZone()),
                   EconomyContentJson.Options) == serializedEvidence &&
               EconomyContentRuntimeService.CalculateEffectiveUnitPrice(version, product) == first,
            "Same-date dynamic zone/event/price evidence changed during a 20x replay.");
    }
    Assert(dynamicEvidence.Zones.Count(zoneEvidence => zoneEvidence.SelectedOpen) == 1 &&
           dynamicEvidence.Zones.Count(zoneEvidence => zoneEvidence.SelectedHotspot) == 1 &&
           dynamicEvidence.WorldEvents.Count == 1 &&
           dynamicEvidence.WorldEvents.All(worldEvent =>
               worldEvent.EventId.Length == 32 && worldEvent.Seed.Length == 64),
        "Daily dynamic selection did not freeze one open zone, one timed hotspot, and one identified economic event.");
    var selectedWorldEvent = dynamicEvidence.WorldEvents.Single();
    Assert(EconomyDynamicEconomyRuntime.ActiveEvents(
               dynamicEvidence,
               selectedWorldEvent.Window.StartsAt.AddTicks(-1)).Count == 0 &&
           EconomyDynamicEconomyRuntime.ActiveEvents(
               dynamicEvidence,
               selectedWorldEvent.Window.StartsAt).Single() == selectedWorldEvent &&
           EconomyDynamicEconomyRuntime.ActiveEvents(
               dynamicEvidence,
               selectedWorldEvent.Window.EndsAt).Count == 0 &&
           EconomyDynamicEconomyRuntime.ActiveEvents(
               dynamicEvidence,
               selectedWorldEvent.Window.EndsAt,
               includeGrace: true).Single() == selectedWorldEvent &&
           EconomyDynamicEconomyRuntime.ActiveEvents(
               dynamicEvidence,
               selectedWorldEvent.Window.GraceEndsAt,
               includeGrace: true).Count == 0,
        "World-event visibility did not honor before/during/after and grace-window boundaries.");

    var nextVersion = version with
    {
        VersionId = Guid.NewGuid(),
        VersionNumber = 2,
        BusinessDate = version.BusinessDate.AddDays(1)
    };
    var nextEvidence = EconomyDynamicEconomyRuntime.Create(nextVersion, options.ResolveTimeZone())
        ?? throw new InvalidOperationException("Next-day dynamic economy evidence was not created.");
    Assert(!dynamicEvidence.Zones.Where(zoneEvidence => zoneEvidence.SelectedOpen)
               .Select(zoneEvidence => zoneEvidence.ZoneId)
               .SequenceEqual(nextEvidence.Zones.Where(zoneEvidence => zoneEvidence.SelectedOpen)
                   .Select(zoneEvidence => zoneEvidence.ZoneId), StringComparer.OrdinalIgnoreCase) &&
           dynamicEvidence.WorldEvents.Single().EventKey != nextEvidence.WorldEvents.Single().EventKey,
        "Adjacent dates did not rotate both the two-zone pool and the two-event pool.");
    var closedZone = definition.ExchangeZones.Single(zoneDefinition =>
        !dynamicEvidence.Zones.Single(zoneEvidence =>
            string.Equals(zoneEvidence.ZoneId, zoneDefinition.ZoneId, StringComparison.OrdinalIgnoreCase)).SelectedOpen);
    Assert(EconomyDynamicEconomyRuntime.NextZoneOpen(
               version,
               closedZone,
               dynamicEvidence.Zones[0].OpenWindow.StartsAt,
               options.ResolveTimeZone()) is not null,
        "A dynamically closed zone did not publish an authoritative nextOpensAt.");

    var invalidPolicy = dynamicPolicy with
    {
        ZonePool =
        [
            dynamicPolicy.ZonePool[0] with { RiskLevel = (ContentZoneRiskLevel)999 }
        ],
        TimedHotspotStartsAtMinute = 1_400,
        WorldEvents = [dynamicPolicy.WorldEvents[0]]
    };
    var invalidDynamicValidation = validator.Validate(
        definition with { DynamicEconomyPolicy = invalidPolicy },
        validationContext);
    Assert(!invalidDynamicValidation.Valid &&
           invalidDynamicValidation.Errors.Any(error => error.Code == "INVALID_DYNAMIC_ZONE_POOL") &&
           invalidDynamicValidation.Errors.Any(error => error.Code == "INVALID_DYNAMIC_ZONE_RISK") &&
           invalidDynamicValidation.Errors.Any(error => error.Code == "HOTSPOT_OUTSIDE_DYNAMIC_OPEN_WINDOW") &&
           invalidDynamicValidation.Errors.Any(error => error.Code == "WORLD_EVENT_VARIETY_REQUIRED"),
        "Malformed dynamic zones, risk, hotspot window, or event variety did not fail closed.");

    var zone = definition.ExchangeZones[0];
    var zoneTimeZone = options.ResolveTimeZone();
    Assert(EconomyContentSchedule.IsOpen(
            zone,
            DateTimeOffset.Parse("2026-07-15T08:00:00Z"),
            zoneTimeZone),
        "The default exchange zone is unexpectedly closed during its all-day schedule.");
}

static void VerifyPresentationValidation()
{
    var validator = new EconomyContentDefinitionValidator();
    var legacy = ValidDefinition();
    var complete = legacy with
    {
        Products =
        [
            legacy.Products[0] with
            {
                IconKey = "capture",
                Rarity = ContentRarity.Rare,
                Usage = "用于每周捕捉补给。"
            },
            legacy.Products[1]
        ],
        Resources =
        [
            legacy.Resources[0] with
            {
                IconKey = "mineral",
                Rarity = ContentRarity.Common,
                Usage = "用于据点建设与常规制作。"
            },
            legacy.Resources[1]
        ]
    };
    Assert(validator.Validate(complete, ValidationContext()).Valid,
        "Complete safe presentation metadata was rejected.");

    var invalidProducts = new[]
    {
        complete.Products[0] with { IconKey = "" },
        complete.Products[0] with { IconKey = "unknown-icon" },
        complete.Products[0] with { IconKey = "https://evil.example/icon.svg" },
        complete.Products[0] with { IconKey = "../capture" },
        complete.Products[0] with { IconKey = "<script>" },
        complete.Products[0] with { Rarity = (ContentRarity)999 },
        complete.Products[0] with { Usage = "" },
        complete.Products[0] with { Usage = "<img src=x onerror=alert(1)>" },
        complete.Products[0] with { Usage = "javascript:alert(1)" },
        complete.Products[0] with { Usage = "data:text/html,evil" },
        complete.Products[0] with { Usage = "../secrets" },
        complete.Products[0] with { Usage = "unsafe\u0001text" },
        complete.Products[0] with { Rarity = null, Usage = null }
    };
    foreach (var product in invalidProducts)
    {
        var result = validator.Validate(complete with
        {
            Products = [product, complete.Products[1]]
        }, ValidationContext());
        Assert(!result.Valid && result.Errors.Any(issue =>
                issue.Code is "PRESENTATION_FIELDS_INCOMPLETE" or
                    "INVALID_PRESENTATION_ICON" or
                    "INVALID_PRESENTATION_RARITY" or
                    "INVALID_PRESENTATION_USAGE"),
            $"Unsafe product presentation was accepted: {product.IconKey}/{product.Rarity}/{product.Usage}");
    }

    var invalidResource = complete.Resources[0] with { Usage = "file:C:/secrets" };
    var resourceResult = validator.Validate(complete with
    {
        Resources = [invalidResource, complete.Resources[1]]
    }, ValidationContext());
    Assert(!resourceResult.Valid && resourceResult.Errors.Any(issue =>
            issue.Code == "INVALID_PRESENTATION_USAGE"),
        "Unsafe resource presentation was accepted.");
}

static void VerifyRotationAndScheduleSemantics()
{
    var definition = ValidDefinition();
    var legacyJson = EconomyContentCanonicalizer.Serialize(definition);
    Assert(!legacyJson.Contains("dynamicEconomyPolicy", StringComparison.Ordinal) &&
           !legacyJson.Contains("\"iconKey\"", StringComparison.Ordinal) &&
           !legacyJson.Contains("\"rarity\"", StringComparison.Ordinal) &&
           !legacyJson.Contains("\"usage\"", StringComparison.Ordinal) &&
           EconomyContentCanonicalizer.Hash(
               System.Text.Json.JsonSerializer.Deserialize<EconomyContentDefinition>(
                   legacyJson,
                   EconomyContentJson.Options)!) == EconomyContentCanonicalizer.Hash(definition),
        "Omitting trailing optional presentation fields changed legacy canonical JSON/hash compatibility.");
    var hash = EconomyContentCanonicalizer.Hash(definition);
    Assert(hash == "c28cfbe0fb13380b4020de587bb529a62443e54bda23b203ea2d108fd5b89d7e",
        $"Legacy presentation fixture hash changed: {hash}");
    var monday = new EconomyContentVersion(
        Guid.NewGuid(), "local", 1, new DateOnly(2026, 7, 13),
        definition.Dependencies.RulesVersion, hash, definition, Guid.NewGuid(), "test",
        DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
    var tuesday = monday with
    {
        VersionId = Guid.NewGuid(),
        VersionNumber = 2,
        BusinessDate = monday.BusinessDate.AddDays(1)
    };
    var mondayHotspots = EconomyContentRuntimeService.SelectDailyHotspots(monday);
    var mondayReplay = EconomyContentRuntimeService.SelectDailyHotspots(monday);
    var tuesdayHotspots = EconomyContentRuntimeService.SelectDailyHotspots(tuesday);
    Assert(mondayHotspots.SetEquals(mondayReplay) && mondayHotspots.Count == 1,
        "Replaying one business date did not reproduce its single deterministic hotspot.");
    Assert(!mondayHotspots.SetEquals(tuesdayHotspots),
        "A two-zone/one-hotspot pool did not move on the adjacent business date.");

    var hotspotZone = definition.ExchangeZones.Single(zone => mondayHotspots.Contains(zone.ZoneId));
    var effectiveMultiplier = EconomyContentRuntimeService.CalculateZoneYieldMultiplierBasisPoints(
        definition.Rotation,
        hotspotZone,
        hotspot: true);
    Assert(effectiveMultiplier > hotspotZone.YieldMultiplierBasisPoints,
        "The selected hotspot did not increase the authoritative server-side yield multiplier.");
    Assert(EconomyContentRuntimeService.CalculateEffectiveResourceUnitValue(9, effectiveMultiplier) ==
           (long)Math.Ceiling(9m * effectiveMultiplier / 10_000m),
        "Hotspot resource pricing does not match settlement's rounded-up unit-value rule.");

    var utc = TimeZoneInfo.Utc;
    var boundaryZone = new ContentExchangeZoneDefinition(
        "boundary", "Boundary", "Test close boundary", 0, 0, 10, 10_000,
        [new ContentExchangeWindow(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(12, 0), 60)],
        true,
        "Test-only risk hint");
    var atClose = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
    Assert(!EconomyContentSchedule.IsOpen(boundaryZone, atClose, utc, includeGrace: false),
        "A new quote was accepted at the closing boundary.");
    Assert(EconomyContentSchedule.IsOpen(boundaryZone, atClose, utc, includeGrace: true) &&
           EconomyContentSchedule.IsOpen(
               boundaryZone,
               DateTimeOffset.Parse("2026-07-13T12:01:00Z"),
               utc,
               includeGrace: true),
        "An existing quote could not settle through the configured close grace boundary.");
    Assert(!EconomyContentSchedule.IsOpen(
            boundaryZone,
            DateTimeOffset.Parse("2026-07-13T12:01:00.001Z"),
            utc,
            includeGrace: true),
        "A quote could settle after the configured close grace boundary.");

    var laterZone = boundaryZone with
    {
        ZoneId = "later",
        OpenWindows =
        [
            new ContentExchangeWindow(
                DayOfWeek.Tuesday,
                new TimeOnly(11, 0),
                new TimeOnly(12, 0),
                60)
        ]
    };
    var earlierZone = laterZone with
    {
        ZoneId = "earlier",
        OpenWindows =
        [
            new ContentExchangeWindow(
                DayOfWeek.Tuesday,
                new TimeOnly(9, 0),
                new TimeOnly(10, 0),
                60)
        ]
    };
    Assert(EconomyContentSchedule.NextOpen(
               [laterZone, earlierZone],
               DateTimeOffset.Parse("2026-07-13T12:30:00Z"),
               utc) == DateTimeOffset.Parse("2026-07-14T09:00:00Z"),
        "An all-closed zone set did not project its earliest next opening.");
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

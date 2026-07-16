using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var cancellationToken = CancellationToken.None;
await VerifyRejectedSelectionsHaveNoSideEffectsAsync(cancellationToken);
await VerifyAtomicDerivationAndRestartIdempotencyAsync(cancellationToken);
await VerifySelectionVsSettlementConcurrencyAsync(cancellationToken);
await VerifyAtomicPersistenceFailureAsync(cancellationToken);
await VerifyStaleRowBatchRollsBackAsync(cancellationToken);
await VerifyNativePayloadContainsOnlySelectedItemsAsync(cancellationToken);
Console.WriteLine(
    "PASS: selective sale validation, row-level atomic source cancellation/child creation, exact restart idempotency, selection-vs-settlement fencing, persistence/CAS rollback, development hashes, and Native selected-only payload checks.");

static async Task VerifyRejectedSelectionsHaveNoSideEffectsAsync(
    CancellationToken cancellationToken)
{
    await WithStoreAsync(async fixture =>
    {
        var accountId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var content = RuntimeContent();
        var source = await CreateQuoteAsync(
            fixture.Store,
            accountId,
            seasonId,
            "steam_selection_validation",
            content,
            cancellationToken);
        var baseline = await SnapshotAsync(fixture.Store, cancellationToken);

        var cases = new (string Name, Guid AccountId, Guid SeasonId, string UserId,
            long Revision, ExtractionQuoteSelectionLine[] Items, Guid? ContentId, string? ContentHash,
            string ErrorCode)[]
        {
            ("empty", accountId, seasonId, source.UserId, source.Revision, [],
                content.Version.VersionId, content.Version.ContentHash, "EXTRACTION_SELECTION_EMPTY"),
            ("duplicate-case", accountId, seasonId, source.UserId, source.Revision,
                [new("Leather", 1), new("leather", 1)], content.Version.VersionId,
                content.Version.ContentHash, "EXTRACTION_SELECTION_DUPLICATE_ITEM"),
            ("unknown", accountId, seasonId, source.UserId, source.Revision,
                [new("UnknownItem", 1)], content.Version.VersionId,
                content.Version.ContentHash, "EXTRACTION_SELECTION_ITEM_UNKNOWN"),
            ("zero", accountId, seasonId, source.UserId, source.Revision,
                [new("Leather", 0)], content.Version.VersionId,
                content.Version.ContentHash, "EXTRACTION_SELECTION_QUANTITY_INVALID"),
            ("negative", accountId, seasonId, source.UserId, source.Revision,
                [new("Leather", -1)], content.Version.VersionId,
                content.Version.ContentHash, "EXTRACTION_SELECTION_QUANTITY_INVALID"),
            ("over-quote", accountId, seasonId, source.UserId, source.Revision,
                [new("Leather", 6)], content.Version.VersionId,
                content.Version.ContentHash, "EXTRACTION_SELECTION_OVER_QUANTITY"),
            ("over-safe-line", accountId, seasonId, source.UserId, source.Revision,
                [new("Leather", ExtractionRunStore.MaximumSelectedLineQuantity + 1)],
                content.Version.VersionId, content.Version.ContentHash,
                "EXTRACTION_SELECTION_QUANTITY_TOO_LARGE"),
            ("over-safe-total", accountId, seasonId, source.UserId, source.Revision,
                Enumerable.Range(0, 17)
                    .Select(index => new ExtractionQuoteSelectionLine(
                        $"SafeItem{index}",
                        ExtractionRunStore.MaximumSelectedLineQuantity))
                    .ToArray(),
                content.Version.VersionId, content.Version.ContentHash,
                "EXTRACTION_SELECTION_TOTAL_TOO_LARGE"),
            ("too-many-lines", accountId, seasonId, source.UserId, source.Revision,
                Enumerable.Range(0, ExtractionRunStore.MaximumSelectionLines + 1)
                    .Select(index => new ExtractionQuoteSelectionLine($"SafeItem{index}", 1))
                    .ToArray(),
                content.Version.VersionId, content.Version.ContentHash,
                "EXTRACTION_SELECTION_TOO_LARGE"),
            ("wrong-owner", Guid.NewGuid(), seasonId, "steam_attacker", source.Revision,
                [new("Leather", 1)], content.Version.VersionId,
                content.Version.ContentHash, "EXTRACTION_RUN_OWNER_MISMATCH"),
            ("wrong-season", accountId, Guid.NewGuid(), source.UserId, source.Revision,
                [new("Leather", 1)], content.Version.VersionId,
                content.Version.ContentHash, "EXTRACTION_RUN_SEASON_MISMATCH"),
            ("stale-revision", accountId, seasonId, source.UserId, source.Revision + 1,
                [new("Leather", 1)], content.Version.VersionId,
                content.Version.ContentHash, "EXTRACTION_QUOTE_REVISION_CHANGED"),
            ("old-pointer", accountId, seasonId, source.UserId, source.Revision,
                [new("Leather", 1)], Guid.NewGuid(), new string('f', 64),
                "QUOTE_CONTENT_CHANGED")
        };

        foreach (var test in cases)
        {
            var error = await CaptureAsync(() => fixture.Store.CreateSelectedQuoteAsync(
                source.RunId,
                test.Revision,
                test.AccountId,
                test.SeasonId,
                test.UserId,
                $"selection-reject-{test.Name}-0001",
                test.Items,
                test.ContentId,
                test.ContentHash,
                cancellationToken));
            Assert(error?.Code == test.ErrorCode,
                $"{test.Name} returned '{error?.Code ?? "success"}', expected '{test.ErrorCode}'.");
            Assert(await SnapshotAsync(fixture.Store, cancellationToken) == baseline,
                $"Rejected {test.Name} selection mutated a run, revision, or evidence field.");
        }

        var expired = await CreateQuoteAsync(
            fixture.Store,
            Guid.NewGuid(),
            seasonId,
            "steam_expired_selection",
            content,
            cancellationToken,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));
        var expiredBaseline = await SnapshotAsync(fixture.Store, cancellationToken);
        var expiredError = await CaptureAsync(() => fixture.Store.CreateSelectedQuoteAsync(
            expired.RunId,
            expired.Revision,
            expired.AccountId,
            expired.SeasonId,
            expired.UserId,
            "selection-expired-0001",
            [new("Leather", 1)],
            content.Version.VersionId,
            content.Version.ContentHash,
            cancellationToken));
        Assert(expiredError?.Code == "EXTRACTION_QUOTE_EXPIRED",
            "An expired source quote was not rejected with the stable expiry code.");
        Assert(await SnapshotAsync(fixture.Store, cancellationToken) == expiredBaseline,
            "Expired selection rejection mutated the source to Expired or created a child.");
    });
}

static async Task VerifyAtomicDerivationAndRestartIdempotencyAsync(
    CancellationToken cancellationToken)
{
    var directory = NewDirectory();
    var content = RuntimeContent();
    Guid sourceRunId;
    Guid childRunId;
    Guid accountId;
    Guid seasonId;
    try
    {
        using (var fixture = CreateStore(directory))
        {
            accountId = Guid.NewGuid();
            seasonId = Guid.NewGuid();
            var source = await CreateQuoteAsync(
                fixture.Store,
                accountId,
                seasonId,
                "steam_selection_restart",
                content,
                cancellationToken);
            sourceRunId = source.RunId;
            var untouchedHistory = await CreateQuoteAsync(
                fixture.Store,
                Guid.NewGuid(),
                seasonId,
                "steam_selection_untouched_history",
                content,
                cancellationToken);
            InstallSettlementRunWriteAudit(directory);
            var key = "selection-response-loss-restart-0001";
            var created = await fixture.Store.CreateSelectedQuoteAsync(
                source.RunId,
                source.Revision,
                accountId,
                seasonId,
                source.UserId,
                key,
                [new("bone", 2)],
                content.Version.VersionId,
                content.Version.ContentHash,
                cancellationToken);
            Assert(created.Created && !created.IdempotentReplay && !created.IdempotencyConflict,
                "The first exact selection was not recorded as a creation.");
            childRunId = created.Run.RunId;
            var writes = ReadSettlementRunWriteAudit(directory);
            Assert(writes.Count == 2 &&
                   writes[0] == ("UPDATE", sourceRunId.ToString("D")) &&
                   writes[1] == ("INSERT", childRunId.ToString("D")) &&
                   writes.All(write => !string.Equals(
                       write.RunId,
                       untouchedHistory.RunId.ToString("D"),
                       StringComparison.Ordinal)),
                $"Selective derivation wrote unexpected SQLite rows: {string.Join(", ", writes)}.");
            Assert(created.Run.Items is
                   [{
                       ItemId: "Bone", Quantity: 2, TotalValue: 6,
                       IconKey: "ancient", Rarity: ContentRarity.Rare,
                       Usage: "用于古代科技与稀有制造。", PresentationSource: "content"
                   }] &&
                   created.Run.ItemCount == 2 && created.Run.TotalValue == 6,
                "The child quote did not preserve canonical ItemID, selected quantity/value, or frozen presentation metadata.");
            Assert(created.Run.QuotedAt == source.QuotedAt &&
                   created.Run.ExpiresAt == source.ExpiresAt &&
                   created.Run.ContentVersionId == source.ContentVersionId &&
                   created.Run.ContentHash == source.ContentHash &&
                   created.Run.ContentBusinessDate == source.ContentBusinessDate &&
                   created.Run.ContentRulesVersion == source.ContentRulesVersion &&
                   created.Run.RotationSeed == source.RotationSeed &&
                   created.Run.ZoneId == source.ZoneId &&
                   created.Run.ZoneYieldMultiplierBasisPoints == source.ZoneYieldMultiplierBasisPoints &&
                   created.Run.Hotspot == source.Hotspot &&
                   JsonSerializer.Serialize(created.Run.DynamicEconomyEvidence) ==
                       JsonSerializer.Serialize(source.DynamicEconomyEvidence),
                "The selected child lost frozen quote/content/dynamic evidence.");
            Assert(created.Run.SourceQuoteRunId == source.RunId &&
                   created.Run.SourceQuoteRevision == source.Revision &&
                   created.Run.SelectionIdempotencyKey == key &&
                   IsSha256(created.Run.SelectionRequestHash),
                "The child did not retain durable selection lineage/idempotency evidence.");
            var expectedDevelopmentHash = Sha256("Bone=4");
            Assert(created.Run.QuoteSnapshotHash == expectedDevelopmentHash,
                "Development child hash was not based on the selected item's original quoted quantity.");
            var cancelled = await fixture.Store.GetAsync(source.RunId, cancellationToken);
            Assert(cancelled?.State == ExtractionSettlementState.Cancelled &&
                   cancelled.SelectedChildRunId == childRunId &&
                   cancelled.Revision == source.Revision + 1,
                "Source cancellation and child creation were not visible as one committed state.");

            var replays = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
                fixture.Store.CreateSelectedQuoteAsync(
                    source.RunId,
                    source.Revision,
                    accountId,
                    seasonId,
                    source.UserId,
                    key,
                    [new("BONE", 2)],
                    content.Version.VersionId,
                    content.Version.ContentHash,
                    cancellationToken)));
            Assert(replays.All(result =>
                    result.IdempotentReplay && !result.Created &&
                    result.Run.RunId == childRunId),
                "Twenty same-key canonical replays did not return the original child.");

            var conflict = await fixture.Store.CreateSelectedQuoteAsync(
                source.RunId,
                source.Revision,
                accountId,
                seasonId,
                source.UserId,
                key,
                [new("Bone", 1)],
                content.Version.VersionId,
                content.Version.ContentHash,
                cancellationToken);
            Assert(conflict.IdempotencyConflict && !conflict.Created,
                "The same key with a different selection was not a side-effect-free conflict.");

            var scopeConflictBaseline = await SnapshotAsync(fixture.Store, cancellationToken);
            var accountConflict = await fixture.Store.CreateSelectedQuoteAsync(
                source.RunId,
                source.Revision,
                Guid.NewGuid(),
                seasonId,
                "steam_other_account",
                key,
                [new("Bone", 2)],
                content.Version.VersionId,
                content.Version.ContentHash,
                cancellationToken);
            var sourceConflict = await fixture.Store.CreateSelectedQuoteAsync(
                Guid.NewGuid(),
                source.Revision,
                accountId,
                seasonId,
                source.UserId,
                key,
                [new("Bone", 2)],
                content.Version.VersionId,
                content.Version.ContentHash,
                cancellationToken);
            Assert(accountConflict.IdempotencyConflict && sourceConflict.IdempotencyConflict,
                "The same selection key was not bound to its original account and source quote.");
            Assert(await SnapshotAsync(fixture.Store, cancellationToken) == scopeConflictBaseline,
                "A cross-account or cross-source selection-key conflict changed durable state.");

            var sourceStart = await CaptureAsync(() => fixture.Store.StartConsumptionAsync(
                source.RunId,
                source.UserId,
                "source-must-never-settle-0001",
                new Dictionary<string, long> { ["Leather"] = 5, ["Bone"] = 4 },
                cancellationToken));
            Assert(sourceStart?.Code == "EXTRACTION_QUOTE_NOT_SETTLEABLE",
                "The cancelled source quote remained settleable after child derivation.");
            var childStarts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
                fixture.Store.StartConsumptionAsync(
                    childRunId,
                    source.UserId,
                    "selected-child-settlement-0001",
                    new Dictionary<string, long> { ["Bone"] = 4 },
                    cancellationToken)));
            Assert(childStarts.Count(result => result.Started) == 1 &&
                   childStarts.All(result => !result.IdempotencyConflict),
                "Same-key child settlement did not enter consumption exactly once.");
        }

        using (var reopened = CreateStore(directory))
        {
            var replay = await reopened.Store.CreateSelectedQuoteAsync(
                sourceRunId,
                1,
                accountId,
                seasonId,
                "steam_selection_restart",
                "selection-response-loss-restart-0001",
                [new("Bone", 2)],
                content.Version.VersionId,
                content.Version.ContentHash,
                cancellationToken);
            Assert(replay.IdempotentReplay && replay.Run.RunId == childRunId,
                "A response-loss replay after SQLite/process restart did not recover the same child id.");
            var runs = await reopened.Store.ListAsync(accountId, seasonId, 100, cancellationToken);
            Assert(runs.Count(run => run.SourceQuoteRunId == sourceRunId) == 1,
                "Restart replay created a duplicate selected child.");
        }
    }
    finally
    {
        Cleanup(directory);
    }
}

static async Task VerifySelectionVsSettlementConcurrencyAsync(
    CancellationToken cancellationToken)
{
    await WithStoreAsync(async fixture =>
    {
        var content = RuntimeContent();
        var source = await CreateQuoteAsync(
            fixture.Store,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "steam_selection_race",
            content,
            cancellationToken);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = Enumerable.Range(0, 100).Select(async index =>
        {
            await start.Task;
            if (index % 2 == 0)
            {
                try
                {
                    var result = await fixture.Store.CreateSelectedQuoteAsync(
                        source.RunId,
                        source.Revision,
                        source.AccountId,
                        source.SeasonId,
                        source.UserId,
                        "selection-race-shared-key-0001",
                        [new("Leather", 3)],
                        content.Version.VersionId,
                        content.Version.ContentHash,
                        cancellationToken);
                    return result.Created ? "selection-created" : "selection-replayed";
                }
                catch (ExtractionModeException exception)
                {
                    return $"selection-rejected:{exception.Code}";
                }
            }
            try
            {
                var result = await fixture.Store.StartConsumptionAsync(
                    source.RunId,
                    source.UserId,
                    "settlement-race-shared-key-0001",
                    new Dictionary<string, long> { ["Leather"] = 5, ["Bone"] = 4 },
                    cancellationToken);
                return result.Started ? "settlement-started" : "settlement-replayed";
            }
            catch (ExtractionModeException exception)
            {
                return $"settlement-rejected:{exception.Code}";
            }
        }).ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(operations);
        var current = await fixture.Store.GetAsync(source.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Race source disappeared.");
        var children = (await fixture.Store.ListAsync(
            source.AccountId,
            source.SeasonId,
            100,
            cancellationToken)).Where(run => run.SourceQuoteRunId == source.RunId).ToArray();
        var selectionWon = current.State == ExtractionSettlementState.Cancelled;
        var settlementWon = current.State == ExtractionSettlementState.Consuming;
        Assert(selectionWon ^ settlementWon,
            $"Race did not end with exactly one winner (state {current.State}).");
        Assert(selectionWon
                ? children.Length == 1 && outcomes.Count(value => value == "selection-created") == 1 &&
                  outcomes.All(value => !value.Equals("settlement-started", StringComparison.Ordinal))
                : children.Length == 0 && outcomes.Count(value => value == "settlement-started") == 1 &&
                  outcomes.All(value => !value.Equals("selection-created", StringComparison.Ordinal)),
            "One hundred concurrent selection/settlement calls crossed the source CAS fence.");
    });
}

static async Task VerifyAtomicPersistenceFailureAsync(CancellationToken cancellationToken)
{
    var directory = NewDirectory();
    try
    {
        using var repository = new SqliteExtractionRepository(directory);
        var persistence = new FaultingPersistence(repository);
        using var store = CreateRunStore(directory, persistence);
        var content = RuntimeContent();
        var source = await CreateQuoteAsync(
            store,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "steam_selection_fault",
            content,
            cancellationToken);
        persistence.FailNextPersist();
        await AssertThrowsAsync<IOException>(() => store.CreateSelectedQuoteAsync(
            source.RunId,
            source.Revision,
            source.AccountId,
            source.SeasonId,
            source.UserId,
            "selection-persist-fault-0001",
            [new("Leather", 1)],
            content.Version.VersionId,
            content.Version.ContentHash,
            cancellationToken));
        var current = await store.GetAsync(source.RunId, cancellationToken);
        var runs = await store.ListAsync(source.AccountId, source.SeasonId, 100, cancellationToken);
        Assert(current?.State == ExtractionSettlementState.Quoted &&
               current.Revision == source.Revision && runs.Count == 1,
            "A failed row-write batch exposed a cancelled source or child in memory.");
    }
    finally
    {
        Cleanup(directory);
    }
}

static async Task VerifyStaleRowBatchRollsBackAsync(CancellationToken cancellationToken)
{
    var directory = NewDirectory();
    var childRunId = Guid.NewGuid();
    Guid sourceRunId;
    try
    {
        using (var repository = new SqliteExtractionRepository(directory))
        using (var store = CreateRunStore(directory, repository))
        {
            var source = await CreateQuoteAsync(
                store,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "steam_selection_stale_cas",
                RuntimeContent(),
                cancellationToken);
            sourceRunId = source.RunId;
            var staleExpected = source with { Revision = 0 };
            var cancelled = source with
            {
                State = ExtractionSettlementState.Cancelled,
                Revision = checked(source.Revision + 1),
                StateChangedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorCode = "TEST_STALE_CAS",
                ErrorMessage = "must roll back"
            };
            var child = source with
            {
                RunId = childRunId,
                QuotedAt = source.QuotedAt.AddSeconds(-1),
                UpdatedAt = source.UpdatedAt,
                Revision = 1
            };

            await AssertThrowsAsync<InvalidOperationException>(() =>
                repository.PersistSettlementRunWritesAsync(
                    [
                        ExtractionSettlementRunWrite.Insert(child),
                        ExtractionSettlementRunWrite.Update(staleExpected, cancelled)
                    ],
                    cancellationToken));
        }

        using var reopened = CreateStore(directory);
        var sourceAfterRestart = await reopened.Store.GetAsync(sourceRunId, cancellationToken);
        var childAfterRestart = await reopened.Store.GetAsync(childRunId, cancellationToken);
        Assert(sourceAfterRestart is { State: ExtractionSettlementState.Quoted, Revision: 1 } &&
               childAfterRestart is null,
            "A stale row CAS did not roll back the earlier insert in the same SQLite transaction.");
    }
    finally
    {
        Cleanup(directory);
    }
}

static async Task VerifyNativePayloadContainsOnlySelectedItemsAsync(
    CancellationToken cancellationToken)
{
    await WithStoreAsync(async fixture =>
    {
        var content = RuntimeContent();
        var nativeSnapshot = NativeSnapshot(Guid.NewGuid().ToString("N"));
        var source = await fixture.Store.CreateQuoteAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "steam_native_selection",
            "zone-a",
            "A 区",
            Lines(),
            nativeSnapshot.SnapshotHash,
            DateTimeOffset.UtcNow.AddMinutes(2),
            cancellationToken,
            nativeSnapshot,
            content,
            11_500,
            true,
            DynamicEvidence());
        var selected = await fixture.Store.CreateSelectedQuoteAsync(
            source.RunId,
            source.Revision,
            source.AccountId,
            source.SeasonId,
            source.UserId,
            "native-selection-payload-0001",
            [new("Bone", 2)],
            content.Version.VersionId,
            content.Version.ContentHash,
            cancellationToken);
        var childSnapshot = selected.Run.NativeInventorySnapshot
            ?? throw new InvalidOperationException("Native selected child lost its snapshot.");
        Assert(childSnapshot.SnapshotHash == nativeSnapshot.SnapshotHash &&
               selected.Run.QuoteSnapshotHash == nativeSnapshot.SnapshotHash &&
               ExtractionNativeInventoryCanonicalizer.Hash(childSnapshot) ==
                   nativeSnapshot.SnapshotHash,
            "Native child did not retain the complete canonical quote snapshot/hash.");
        var adapter = new ExtractionNativeInventoryAdapter(
            StableNativeState(),
            new NoopNativeTransport());
        var payload = adapter.CreateConsumePayload(
            childSnapshot,
            selected.Run.Items);
        Assert(payload.Items is [{ ItemId: "Bone", Quantity: 2 }] &&
               payload.Items.All(item => item.ItemId != "Leather"),
            $"Native consume authorization list contained unselected items or the wrong quantity: {JsonSerializer.Serialize(payload.Items)}");
        Assert(payload.ExpectedContainers.SelectMany(container => container.Slots)
                .Any(slot => slot.ItemId == "Leather" && slot.Quantity == 5),
            "Native optimistic-lock evidence no longer contains the complete frozen inventory snapshot.");
    });
}

static async Task<ExtractionSettlementRun> CreateQuoteAsync(
    ExtractionRunStore store,
    Guid accountId,
    Guid seasonId,
    string userId,
    EconomyRuntimeContent content,
    CancellationToken cancellationToken,
    DateTimeOffset? expiresAt = null) =>
    await store.CreateQuoteAsync(
        accountId,
        seasonId,
        userId,
        "zone-a",
        "A 区",
        Lines(),
        Sha256("Bone=4\nLeather=5"),
        expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(2),
        cancellationToken,
        runtimeContent: content,
        zoneYieldMultiplierBasisPoints: 11_500,
        hotspot: true,
        dynamicEconomyEvidence: DynamicEvidence());

static ExtractionLootLine[] Lines() =>
[
    new("Leather", "皮革", 5, 2, 10,
        "biological", ContentRarity.Uncommon, "用于装备与加工配方。", "content"),
    new("Bone", "骨头", 4, 3, 12,
        "ancient", ContentRarity.Rare, "用于古代科技与稀有制造。", "content")
];

static EconomyRuntimeContent RuntimeContent()
{
    var now = DateTimeOffset.UtcNow;
    var versionId = Guid.NewGuid();
    var hash = new string('a', 64);
    var seed = new string('b', 64);
    return new EconomyRuntimeContent(
        new EconomyContentVersion(
            versionId,
            "local",
            1,
            DateOnly.FromDateTime(now.UtcDateTime),
            EconomyContentRuntimeService.SupportedRulesVersion,
            hash,
            null!,
            Guid.NewGuid(),
            "test",
            now),
        new EconomyContentRotationIdentity(
            "local",
            DateOnly.FromDateTime(now.UtcDateTime),
            EconomyContentRuntimeService.SupportedRulesVersion,
            1,
            seed,
            hash,
            versionId),
        new Dictionary<string, ContentProductDefinition>(),
        new Dictionary<string, ContentResourceDefinition>(),
        [],
        new HashSet<string>(),
        null);
}

static EconomyDynamicQuoteEvidence DynamicEvidence()
{
    var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
    var open = new EconomyEventWindowEvidence(now, now.AddHours(8), now.AddHours(8).AddMinutes(1));
    return new EconomyDynamicQuoteEvidence(
        "dynamic-v1",
        new string('c', 64),
        ContentZoneRiskLevel.Elevated,
        open,
        new EconomyEventWindowEvidence(now.AddHours(1), now.AddHours(2), now.AddHours(2).AddMinutes(1)),
        [new EconomyWorldEventEvidence(
            new string('d', 32),
            "resource-surge",
            "资源潮汐",
            ContentWorldEventKind.ResourceSurge,
            new string('e', 64),
            open,
            11_500,
            10_000)]);
}

static ExtractionNativeInventoryQuoteSnapshot NativeSnapshot(string playerUid)
{
    var empty = Guid.Empty.ToString("N");
    var containers = new[]
    {
        new ExtractionNativeInventoryContainerSnapshot(
            "common",
            Guid.NewGuid().ToString("N"),
            [
                new ExtractionNativeInventorySlotSnapshot(0, "Leather", 5, empty, empty, false, 0, 0),
                new ExtractionNativeInventorySlotSnapshot(1, "Bone", 4, empty, empty, false, 0, 0)
            ]),
        new ExtractionNativeInventoryContainerSnapshot("dropSlot", Guid.NewGuid().ToString("N"), []),
        new ExtractionNativeInventoryContainerSnapshot("food", Guid.NewGuid().ToString("N"), [])
    };
    var snapshot = new ExtractionNativeInventoryQuoteSnapshot(
        1,
        playerUid,
        DateTimeOffset.UtcNow,
        containers,
        string.Empty);
    return snapshot with { SnapshotHash = ExtractionNativeInventoryCanonicalizer.Hash(snapshot) };
}

static NativeBridgeState StableNativeState()
{
    var state = new NativeBridgeState();
    state.OnHello(new NativeBridgeHello(
        "1.0",
        "1.0.0.100427",
        "test",
        ["inventory.probe", "inventory.consume"],
        new Dictionary<string, bool>()));
    return state;
}

static async Task<string> SnapshotAsync(
    ExtractionRunStore store,
    CancellationToken cancellationToken)
{
    var runs = await store.ListAsync(null, null, 1_000, cancellationToken);
    return JsonSerializer.Serialize(runs.OrderBy(run => run.RunId));
}

static async Task<ExtractionModeException?> CaptureAsync(Func<Task> operation)
{
    try
    {
        await operation();
        return null;
    }
    catch (ExtractionModeException exception)
    {
        return exception;
    }
}

static async Task AssertThrowsAsync<TException>(Func<Task> operation)
    where TException : Exception
{
    try
    {
        await operation();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static string Sha256(string value) => Convert.ToHexStringLower(
    SHA256.HashData(Encoding.UTF8.GetBytes(value)));

static bool IsSha256(string? value) => value is { Length: 64 } &&
    value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

static void InstallSettlementRunWriteAudit(string directory)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "extraction-commerce.db"),
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE test_settlement_run_write_audit (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            operation TEXT NOT NULL,
            run_id TEXT NOT NULL
        );
        CREATE TRIGGER test_settlement_run_insert
        AFTER INSERT ON extraction_settlement_runs
        BEGIN
            INSERT INTO test_settlement_run_write_audit (operation, run_id)
            VALUES ('INSERT', NEW.run_id);
        END;
        CREATE TRIGGER test_settlement_run_update
        AFTER UPDATE ON extraction_settlement_runs
        BEGIN
            INSERT INTO test_settlement_run_write_audit (operation, run_id)
            VALUES ('UPDATE', NEW.run_id);
        END;
        CREATE TRIGGER test_settlement_run_delete
        AFTER DELETE ON extraction_settlement_runs
        BEGIN
            INSERT INTO test_settlement_run_write_audit (operation, run_id)
            VALUES ('DELETE', OLD.run_id);
        END;
        """;
    command.ExecuteNonQuery();
}

static IReadOnlyList<(string Operation, string RunId)> ReadSettlementRunWriteAudit(
    string directory)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "extraction-commerce.db"),
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT operation, run_id
        FROM test_settlement_run_write_audit
        ORDER BY sequence;
        """;
    using var reader = command.ExecuteReader();
    var writes = new List<(string Operation, string RunId)>();
    while (reader.Read())
    {
        writes.Add((reader.GetString(0), reader.GetString(1)));
    }
    return writes;
}

static async Task WithStoreAsync(Func<StoreFixture, Task> test)
{
    var directory = NewDirectory();
    try
    {
        using var fixture = CreateStore(directory);
        await test(fixture);
    }
    finally
    {
        Cleanup(directory);
    }
}

static string NewDirectory()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"pal-control-selective-sale-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    return directory;
}

static void Cleanup(string directory)
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }
}

static StoreFixture CreateStore(string directory)
{
    var repository = new SqliteExtractionRepository(directory);
    return new StoreFixture(repository, CreateRunStore(directory, repository));
}

static ExtractionRunStore CreateRunStore(
    string directory,
    IExtractionSettlementPersistence persistence) => new(
        Options.Create(new ExtractionPersistenceOptions { DataDirectory = directory }),
        new TestWebHostEnvironment(directory),
        persistence);

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class FaultingPersistence(IExtractionSettlementPersistence inner)
    : IExtractionSettlementPersistence
{
    private int _failNext;

    public void FailNextPersist() => Interlocked.Exchange(ref _failNext, 1);

    public IReadOnlyList<ExtractionSettlementRun> LoadAndMigrateSettlementRuns(
        string legacyJsonPath) => inner.LoadAndMigrateSettlementRuns(legacyJsonPath);

    public Task PersistSettlementRunWritesAsync(
        IReadOnlyCollection<ExtractionSettlementRunWrite> writes,
        CancellationToken cancellationToken) =>
        Interlocked.Exchange(ref _failNext, 0) == 1
            ? Task.FromException(new IOException("injected selection row-write failure"))
            : inner.PersistSettlementRunWritesAsync(writes, cancellationToken);

    public Task<ExtractionRunCreditCommit> CreditRemovedRunAsync(
        ExtractionSettlementRun expectedRun,
        CancellationToken cancellationToken) =>
        inner.CreditRemovedRunAsync(expectedRun, cancellationToken);
}

internal sealed class NoopNativeTransport : INativeBridgeCommandTransport
{
    public Task<NativeBridgeResult> SendCommandAsync(
        string serverId,
        string operation,
        object payload,
        string reason,
        CancellationToken cancellationToken,
        long expectedRevision = 0,
        string? idempotencyKey = null) =>
        throw new InvalidOperationException("The payload-only test must not dispatch Native Bridge.");
}

internal sealed class StoreFixture(
    SqliteExtractionRepository repository,
    ExtractionRunStore store) : IDisposable
{
    public ExtractionRunStore Store { get; } = store;

    public void Dispose()
    {
        Store.Dispose();
        repository.Dispose();
    }
}

internal sealed class TestWebHostEnvironment(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "PalControl.SelectiveResourceSale.Harness";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

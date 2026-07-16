using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Content;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var cancellationToken = CancellationToken.None;
await VerifyStableCapabilityAndFullSnapshotAsync(cancellationToken);
await VerifyContentSwitchFailsBeforeNativeDispatchAsync(cancellationToken);
await VerifyDynamicQuoteEvidenceAndWindowSemanticsAsync(cancellationToken);
await VerifyConsumeEvidenceClassificationAsync(cancellationToken);
await VerifyDurableIdempotencyAndReceiptAsync(cancellationToken);
await VerifyPersistenceFaultBoundariesAsync(cancellationToken);
VerifyProductionNeverSelectsRcon();
Console.WriteLine("PASS: Native quote snapshot, content/event-switch pre-dispatch rejection, dynamic event/hotspot quote grace, durable dynamic evidence, exact consume evidence, deterministic persistence faults, restart no-redispatch, and durable idempotency checks.");

static async Task VerifyContentSwitchFailsBeforeNativeDispatchAsync(
    CancellationToken cancellationToken)
{
    var playerUid = Guid.NewGuid().ToString("N");
    var snapshot = Snapshot(playerUid);
    var transport = new FakeNativeBridgeTransport();
    var adapter = new ExtractionNativeInventoryAdapter(StableState(), transport);
    var line = new ExtractionLootLine("Leather", "皮革", 5, 2, 10);
    var contentA = Guid.NewGuid();
    var contentB = Guid.NewGuid();
    var hashA = new string('a', 64);
    var hashB = new string('b', 64);
    var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
    var quote = new ExtractionSettlementRun(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "steam_native-content-switch",
        "zone-1",
        "回收点",
        ExtractionSettlementState.Quoted,
        [line],
        5,
        10,
        snapshot.SnapshotHash,
        null,
        null,
        null,
        null,
        now,
        now.AddMinutes(2),
        now,
        null)
    {
        ContentVersionId = contentA,
        ContentHash = hashA,
        NativeInventorySnapshot = snapshot
    };
    var currentVersion = new EconomyContentVersion(
        contentB,
        "local",
        2,
        new DateOnly(2026, 7, 16),
        EconomyContentRuntimeService.SupportedRulesVersion,
        hashB,
        null!,
        Guid.NewGuid(),
        "test",
        now);

    ExtractionModeException? rejected = null;
    try
    {
        ExtractionSettlementService.RequireCurrentQuoteContent(quote, currentVersion);
        var payload = adapter.CreateConsumePayload(snapshot, quote.Items);
        var requestHash = adapter.ComputeRequestHash(quote.RunId, "local", payload);
        transport.Results.Enqueue(Result("succeeded", SuccessJson(actualConsumed: 5)));
        _ = await adapter.ConsumeAsync(
            "local",
            payload,
            requestHash,
            "content-switch-native-key",
            cancellationToken);
    }
    catch (ExtractionModeException exception)
    {
        rejected = exception;
    }

    Assert(rejected?.Code == "QUOTE_CONTENT_CHANGED" && rejected.StatusCode == 409,
        "A content-A quote did not fail closed after the current pointer moved to content B.");
    Assert(transport.Calls.Count == 0,
        "Native inventory.consume was dispatched for a stale content quote.");
    Assert(quote.State == ExtractionSettlementState.Quoted &&
           quote.SettlementIdempotencyKey is null &&
           quote.SettlementRequestHash is null &&
           quote.NativeConsumeReceipt is null &&
           quote.AttemptCount == 0,
        "The pre-dispatch content guard mutated the quoted run.");
}

static async Task VerifyDynamicQuoteEvidenceAndWindowSemanticsAsync(
    CancellationToken cancellationToken)
{
    var start = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
    var openWindow = new EconomyEventWindowEvidence(start, start.AddHours(1), start.AddHours(1).AddMinutes(1));
    var hotspotWindow = new EconomyEventWindowEvidence(
        start.AddMinutes(10),
        start.AddMinutes(20),
        start.AddMinutes(21));
    var worldEvent = new EconomyWorldEventEvidence(
        new string('e', 32),
        "resource-surge",
        "Resource surge",
        ContentWorldEventKind.ResourceSurge,
        new string('f', 64),
        new EconomyEventWindowEvidence(
            start.AddMinutes(30),
            start.AddMinutes(40),
            start.AddMinutes(42)),
        11_500,
        10_000);
    var dynamicZone = new EconomyDynamicZoneEvidence(
        "zone-1",
        ContentZoneRiskLevel.Elevated,
        true,
        openWindow,
        true,
        hotspotWindow);

    Assert(ExtractionSettlementService.CalculateDynamicQuoteExpiry(
               start.AddMinutes(5),
               start.AddHours(2),
               dynamicZone,
               hotspotAtQuote: false,
               [worldEvent],
               []) == hotspotWindow.StartsAt,
        "A pre-hotspot quote crossed the hotspot activation boundary with its old multiplier.");
    Assert(ExtractionSettlementService.CalculateDynamicQuoteExpiry(
               start.AddMinutes(15),
               start.AddHours(2),
               dynamicZone,
               hotspotAtQuote: true,
               [worldEvent],
               []) == hotspotWindow.GraceEndsAt,
        "An active-hotspot quote did not retain exactly its configured grace window.");
    Assert(ExtractionSettlementService.CalculateDynamicQuoteExpiry(
               start.AddMinutes(35),
               start.AddHours(2),
               dynamicZone,
               hotspotAtQuote: false,
               [worldEvent],
               [worldEvent]) == worldEvent.Window.GraceEndsAt,
        "An active economic-event quote did not retain exactly its configured grace window.");

    var evidence = new EconomyDynamicQuoteEvidence(
        "dynamic-v1",
        new string('a', 64),
        dynamicZone.RiskLevel,
        dynamicZone.OpenWindow,
        dynamicZone.HotspotWindow,
        [worldEvent]);
    var directory = Path.Combine(Path.GetTempPath(), $"pal-control-dynamic-quote-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    Guid runId;
    try
    {
        using (var fixture = CreateStore(directory))
        {
            var quote = await fixture.Store.CreateQuoteAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "steam_dynamic-player",
                "zone-1",
                "Dynamic zone",
                [new ExtractionLootLine("Leather", "Leather", 5, 3, 15)],
                new string('b', 64),
                DateTimeOffset.UtcNow.AddMinutes(2),
                cancellationToken,
                dynamicEconomyEvidence: evidence,
                zoneYieldMultiplierBasisPoints: 15_000,
                hotspot: true);
            runId = quote.RunId;
            var currentDynamic = new EconomyDynamicEconomyEvidence(
                evidence.PolicyVersion,
                evidence.Seed,
                new DateOnly(2026, 7, 16),
                [dynamicZone],
                [worldEvent]);
            var versionId = Guid.NewGuid();
            var currentRuntime = new EconomyRuntimeContent(
                new EconomyContentVersion(
                    versionId,
                    "local",
                    1,
                    currentDynamic.BusinessDate,
                    EconomyContentRuntimeService.SupportedRulesVersion,
                    new string('c', 64),
                    null!,
                    Guid.NewGuid(),
                    "test",
                    start),
                new EconomyContentRotationIdentity(
                    "local",
                    currentDynamic.BusinessDate,
                    EconomyContentRuntimeService.SupportedRulesVersion,
                    1,
                    evidence.Seed,
                    new string('c', 64),
                    versionId),
                new Dictionary<string, ContentProductDefinition>(),
                new Dictionary<string, ContentResourceDefinition>(),
                [],
                new HashSet<string>(),
                currentDynamic);
            ExtractionSettlementService.RequireCurrentQuoteDynamicEvidence(quote, currentRuntime);
            var changedRuntime = currentRuntime with
            {
                DynamicEconomy = currentDynamic with
                {
                    WorldEvents = [worldEvent with { EventId = new string('9', 32) }]
                }
            };
            var changed = await CaptureExtractionErrorAsync(() => Task.Run(() =>
                ExtractionSettlementService.RequireCurrentQuoteDynamicEvidence(quote, changedRuntime)));
            Assert(changed?.Code == "QUOTE_EVENT_CHANGED" && changed.StatusCode == 409,
                "Changed deterministic event evidence was accepted for an existing quote.");
            var returnedEvents = (EconomyWorldEventEvidence[])quote.DynamicEconomyEvidence!.WorldEvents;
            returnedEvents[0] = returnedEvents[0] with { EventId = new string('0', 32) };
            var isolated = await fixture.Store.GetAsync(runId, cancellationToken);
            Assert(isolated?.DynamicEconomyEvidence?.WorldEvents.Single().EventId == worldEvent.EventId,
                "Mutating returned quote evidence changed the authoritative in-memory run.");
        }

        using (var reopened = CreateStore(directory))
        {
            var loaded = await reopened.Store.GetAsync(runId, cancellationToken);
            Assert(loaded?.DynamicEconomyEvidence is not null &&
                   loaded.DynamicEconomyEvidence.WorldEvents.Single() == worldEvent &&
                   loaded.DynamicEconomyEvidence.HotspotWindow == hotspotWindow &&
                   loaded.ZoneYieldMultiplierBasisPoints == 15_000 && loaded.Hotspot,
                "Restart lost frozen event id/seed/window/multiplier/hotspot evidence.");
        }
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static async Task VerifyStableCapabilityAndFullSnapshotAsync(CancellationToken cancellationToken)
{
    var playerUid = Guid.NewGuid();
    var transport = new FakeNativeBridgeTransport();
    transport.Results.Enqueue(Result("succeeded", ProbeJson(playerUid)));
    var state = StableState();
    var adapter = new ExtractionNativeInventoryAdapter(state, transport);

    var snapshot = await adapter.CaptureQuoteSnapshotAsync(
        "local",
        playerUid.ToString("D"),
        cancellationToken);
    Assert(snapshot.SnapshotVersion == 1, "The quote snapshot version was not pinned to 1.");
    Assert(snapshot.OwnerPlayerUid == playerUid.ToString("N").ToLowerInvariant(),
        "The complete PlayerUID was not normalized and retained.");
    Assert(snapshot.Containers.Select(container => container.ContainerKind)
            .SequenceEqual(["common", "dropSlot", "food"]),
        "The exact common/dropSlot/food snapshot was not retained in canonical order.");
    Assert(snapshot.Containers.Sum(container => container.Slots.Count) == 4,
        "The full slot arrays were not retained.");
    var leather = snapshot.Containers[0].Slots[0];
    Assert(leather.ItemId == "Leather" && leather.Quantity == 5 &&
           leather.DynamicCreatedWorldId == Guid.Empty.ToString("N") &&
           leather.DynamicLocalIdInCreatedWorld == Guid.Empty.ToString("N") &&
           !leather.HasDynamicItemData && leather.CorruptionProgressBits == 0,
        "Exact static/dynamic/corruption slot metadata was not parsed.");
    Assert(snapshot.SnapshotHash == ExtractionNativeInventoryCanonicalizer.Hash(snapshot),
        "The persisted full snapshot hash is not deterministic.");
    Assert(ExtractionNativeInventoryCanonicalizer.AggregateTotals(snapshot)["Leather"] == 5,
        "The sellable total was not derived from the Native full snapshot.");

    var payload = adapter.CreateConsumePayload(
        snapshot,
        [new ExtractionLootLine("Leather", "皮革", 5, 2, 10)]);
    var payloadJson = JsonSerializer.Serialize(payload);
    Assert(payloadJson.Contains("\"snapshotVersion\":1", StringComparison.Ordinal) &&
           payloadJson.Contains("\"corruptionProgressBits\":0", StringComparison.Ordinal) &&
           payloadJson.Contains("\"dynamicCreatedWorldId\"", StringComparison.Ordinal),
        "The consume payload omitted versioned exact slot metadata.");

    var experimentalState = new NativeBridgeState();
    experimentalState.OnHello(new NativeBridgeHello(
        "1.0",
        "1.0.0.100427",
        "0.3.0-dev",
        ["inventory.probe", "inventory.consume.experimental"],
        new Dictionary<string, bool>()));
    var experimental = new ExtractionNativeInventoryAdapter(
        experimentalState,
        new FakeNativeBridgeTransport());
    Assert(!experimental.StableSettlementAvailable,
        "inventory.consume.experimental was incorrectly accepted as stable.");
    var rejected = await CaptureExtractionErrorAsync(() => experimental.CaptureQuoteSnapshotAsync(
        "local", playerUid.ToString("N"), cancellationToken));
    Assert(rejected?.Code == "NATIVE_INVENTORY_CONSUME_CAPABILITY_MISSING",
        "An experimental-only bridge did not fail quote capture closed.");

    var corruptMetadataTransport = new FakeNativeBridgeTransport();
    corruptMetadataTransport.Results.Enqueue(Result(
        "succeeded",
        ProbeJson(playerUid).Replace(
            "\"corruptionProgressBits\":0",
            "\"corruptionProgressBits\":1",
            StringComparison.Ordinal)));
    var corruptMetadataAdapter = new ExtractionNativeInventoryAdapter(
        StableState(),
        corruptMetadataTransport);
    var corruptMetadata = await CaptureExtractionErrorAsync(() =>
        corruptMetadataAdapter.CaptureQuoteSnapshotAsync(
            "local", playerUid.ToString("N"), cancellationToken));
    Assert(corruptMetadata?.Code == "NATIVE_INVENTORY_PROBE_INVALID",
        "A human-readable corruption value that disagreed with its exact bits was accepted.");
}

static async Task VerifyConsumeEvidenceClassificationAsync(CancellationToken cancellationToken)
{
    var playerUid = Guid.NewGuid();
    var transport = new FakeNativeBridgeTransport();
    transport.Results.Enqueue(Result("succeeded", ProbeJson(playerUid)));
    var adapter = new ExtractionNativeInventoryAdapter(StableState(), transport);
    var snapshot = await adapter.CaptureQuoteSnapshotAsync(
        "local", playerUid.ToString("N"), cancellationToken);
    var payload = adapter.CreateConsumePayload(
        snapshot,
        [new ExtractionLootLine("Leather", "皮革", 5, 2, 10)]);
    var requestHash = adapter.ComputeRequestHash(Guid.NewGuid(), "local", payload);

    transport.Results.Enqueue(Result("succeeded", SuccessJson(actualConsumed: 5)));
    var succeeded = await adapter.ConsumeAsync(
        "local", payload, requestHash, "native-idempotency-0001", cancellationToken);
    Assert(succeeded.Receipt.Disposition == ExtractionNativeConsumeDisposition.Succeeded &&
           succeeded.Receipt.PersistenceVerified &&
           succeeded.Receipt.Items.Single().ActualConsumed == 5,
        "Exact persisted per-line evidence was not classified as succeeded.");
    Assert(transport.Calls.Last().Operation == "inventory.consume" &&
           transport.Calls.Last().IdempotencyKey == "native-idempotency-0001",
        "The fake bridge did not receive inventory.consume with the caller's durable key.");

    transport.Results.Enqueue(Result("succeeded", SuccessJson(actualConsumed: 4)));
    var mismatch = await adapter.ConsumeAsync(
        "local", payload, requestHash, "native-idempotency-0002", cancellationToken);
    Assert(mismatch.Receipt.Disposition == ExtractionNativeConsumeDisposition.Uncertain &&
           mismatch.Receipt.ErrorCode == "NATIVE_INVENTORY_CONSUME_EVIDENCE_INVALID",
        "A per-line actualConsumed mismatch was allowed to credit.");

    transport.Results.Enqueue(Result(
        "failed",
        """{"applied":false,"snapshotMatched":true,"persistenceVerified":false,"rollback":{"attempted":true,"verified":true}}""",
        "INVENTORY_SNAPSHOT_CONFLICT"));
    var failed = await adapter.ConsumeAsync(
        "local", payload, requestHash, "native-idempotency-0003", cancellationToken);
    Assert(failed.Receipt.Disposition == ExtractionNativeConsumeDisposition.Failed,
        "A definitive pre-credit failure with verified rollback was not classified as failed.");

    transport.Results.Enqueue(Result(
        "failed",
        """{"applied":false,"rollback":{"attempted":true,"verified":false}}""",
        "INVENTORY_CONSUME_VERIFY_FAILED"));
    var unsafeRollback = await adapter.ConsumeAsync(
        "local", payload, requestHash, "native-idempotency-0004", cancellationToken);
    Assert(unsafeRollback.Receipt.Disposition == ExtractionNativeConsumeDisposition.Uncertain,
        "An unverified rollback was incorrectly classified as a definitive failure.");

    transport.Exception = new IOException("fake bridge disconnected after dispatch");
    var disconnected = await adapter.ConsumeAsync(
        "local", payload, requestHash, "native-idempotency-0005", cancellationToken);
    Assert(disconnected.Receipt.Disposition == ExtractionNativeConsumeDisposition.Uncertain,
        "A transport break was incorrectly treated as safe to retry.");
}

static async Task VerifyDurableIdempotencyAndReceiptAsync(CancellationToken cancellationToken)
{
    var directory = Path.Combine(Path.GetTempPath(), $"pal-control-native-settlement-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var playerUid = Guid.NewGuid().ToString("N");
        var snapshot = Snapshot(playerUid);
        var requestHash = new string('a', 64);
        var receipt = Receipt(requestHash);
        Guid runId;
        Guid accountId = Guid.NewGuid();
        Guid seasonId = Guid.NewGuid();
        using (var fixture = CreateStore(directory))
        {
            var quote = await fixture.Store.CreateQuoteAsync(
                accountId,
                seasonId,
                "steam_native-player",
                "zone-1",
                "回收点",
                [new ExtractionLootLine("Leather", "皮革", 5, 2, 10)],
                snapshot.SnapshotHash,
                DateTimeOffset.UtcNow.AddMinutes(2),
                cancellationToken,
                snapshot);
            runId = quote.RunId;
            var start = await fixture.Store.StartConsumptionAsync(
                quote.RunId,
                quote.UserId,
                "durable-native-key",
                new Dictionary<string, long> { ["Leather"] = 5 },
                cancellationToken,
                requestHash);
            Assert(start.Started && start.Run.SettlementRequestHash == requestHash,
                "The Native request hash was not persisted before bridge dispatch.");
            var prematureRemoval = await fixture.Store.TryMarkRemovedAsync(
                quote.RunId,
                start.Run.Revision,
                start.Run.LeaseId!.Value,
                cancellationToken);
            Assert(!prematureRemoval.Applied &&
                   prematureRemoval.Run.State == ExtractionSettlementState.Consuming,
                "A Native run reached Removed without a persisted succeeded receipt.");
            var recorded = await fixture.Store.TryRecordNativeConsumeReceiptAsync(
                quote.RunId,
                start.Run.Revision,
                start.Run.LeaseId!.Value,
                receipt,
                cancellationToken);
            Assert(recorded.Applied && recorded.Run.NativeConsumeReceipt?.ResponseHash == receipt.ResponseHash,
                "The Native consume receipt was not durably attached to Consuming.");
        }

        using (var reopened = CreateStore(directory))
        {
            var loaded = await reopened.Store.GetAsync(runId, cancellationToken);
            Assert(loaded?.NativeInventorySnapshot?.SnapshotHash == snapshot.SnapshotHash &&
                   loaded.NativeConsumeReceipt?.Disposition == ExtractionNativeConsumeDisposition.Succeeded,
                "Restart lost the full quote snapshot or Native result.");
            var replay = await reopened.Store.StartConsumptionAsync(
                runId,
                "steam_native-player",
                "durable-native-key",
                new Dictionary<string, long> { ["Leather"] = 5 },
                cancellationToken,
                requestHash);
            Assert(!replay.Started && !replay.IdempotencyConflict &&
                   replay.Run.NativeConsumeReceipt?.ResponseHash == receipt.ResponseHash,
                "A same-key replay after restart did not reuse the persisted result.");
            var changedPayload = await reopened.Store.StartConsumptionAsync(
                runId,
                "steam_native-player",
                "durable-native-key",
                new Dictionary<string, long> { ["Leather"] = 5 },
                cancellationToken,
                new string('b', 64));
            Assert(changedPayload.IdempotencyConflict,
                "The same key with a different request hash was not rejected.");

            var secondSnapshot = Snapshot(Guid.NewGuid().ToString("N"));
            var secondQuote = await reopened.Store.CreateQuoteAsync(
                Guid.NewGuid(),
                seasonId,
                "steam_other-player",
                "zone-1",
                "回收点",
                [new ExtractionLootLine("Leather", "皮革", 5, 2, 10)],
                secondSnapshot.SnapshotHash,
                DateTimeOffset.UtcNow.AddMinutes(2),
                cancellationToken,
                secondSnapshot);
            var crossRun = await reopened.Store.StartConsumptionAsync(
                secondQuote.RunId,
                secondQuote.UserId,
                "durable-native-key",
                new Dictionary<string, long> { ["Leather"] = 5 },
                cancellationToken,
                new string('c', 64));
            Assert(crossRun.IdempotencyConflict,
                "A durable Native idempotency key was reused by another run.");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task VerifyPersistenceFaultBoundariesAsync(CancellationToken cancellationToken)
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"pal-control-native-settlement-faults-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var playerUid = Guid.NewGuid().ToString("N");
        var snapshot = Snapshot(playerUid);
        var transport = new FakeNativeBridgeTransport();
        var adapter = new ExtractionNativeInventoryAdapter(StableState(), transport);
        Guid runId;
        string requestHash;
        ExtractionNativeConsumeReceipt receipt;

        using (var repository = new SqliteExtractionRepository(directory))
        {
            var persistence = new FaultInjectingSettlementPersistence(repository);
            using var store = CreateRunStore(directory, persistence);
            var quote = await store.CreateQuoteAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "steam_native-fault-player",
                "zone-fault",
                "故障注入回收点",
                [new ExtractionLootLine("Leather", "皮革", 5, 2, 10)],
                snapshot.SnapshotHash,
                DateTimeOffset.UtcNow.AddMinutes(2),
                cancellationToken,
                snapshot);
            runId = quote.RunId;
            var payload = adapter.CreateConsumePayload(snapshot, quote.Items);
            requestHash = adapter.ComputeRequestHash(runId, "local", payload);

            persistence.FailNextPersist(
                "injected failure while persisting Consuming before Native dispatch");
            await AssertThrowsAsync<IOException>(async () =>
            {
                var start = await store.StartConsumptionAsync(
                    runId,
                    quote.UserId,
                    "native-fault-key",
                    new Dictionary<string, long> { ["Leather"] = 5 },
                    cancellationToken,
                    requestHash);
                if (start.Started)
                {
                    _ = await adapter.ConsumeAsync(
                        "local",
                        payload,
                        requestHash,
                        "native-fault-key",
                        cancellationToken);
                }
            }, "A pre-dispatch persistence failure was not surfaced.");
            Assert(transport.Calls.Count == 0,
                "Native was called even though Consuming/requestHash persistence failed.");
            var afterStartFailure = await store.GetAsync(runId, cancellationToken);
            Assert(afterStartFailure?.State == ExtractionSettlementState.Quoted &&
                   afterStartFailure.SettlementIdempotencyKey is null,
                "A failed pre-dispatch persistence attempt leaked an in-memory Consuming state.");

            var durableStart = await store.StartConsumptionAsync(
                runId,
                quote.UserId,
                "native-fault-key",
                new Dictionary<string, long> { ["Leather"] = 5 },
                cancellationToken,
                requestHash);
            Assert(durableStart.Started,
                "The run could not start after the injected persistence failure was removed.");
            transport.Results.Enqueue(Result("succeeded", SuccessJson(actualConsumed: 5)));
            var consumed = await adapter.ConsumeAsync(
                "local",
                payload,
                requestHash,
                "native-fault-key",
                cancellationToken);
            receipt = consumed.Receipt;
            Assert(transport.Calls.Count == 1 &&
                   receipt.Disposition == ExtractionNativeConsumeDisposition.Succeeded,
                "The deterministic Native consume did not execute exactly once.");

            persistence.FailNextPersist(
                "injected failure while persisting the Native success receipt");
            await AssertThrowsAsync<IOException>(
                () => store.TryRecordNativeConsumeReceiptAsync(
                    runId,
                    durableStart.Run.Revision,
                    durableStart.Run.LeaseId!.Value,
                    receipt,
                    cancellationToken),
                "A Native receipt persistence failure was not surfaced.");
            var afterReceiptFailure = await store.GetAsync(runId, cancellationToken);
            Assert(afterReceiptFailure?.State == ExtractionSettlementState.Consuming &&
                   afterReceiptFailure.NativeConsumeReceipt is null,
                "A failed receipt commit leaked non-durable Native success evidence.");
        }

        using (var reopened = CreateStore(directory))
        {
            var loaded = await reopened.Store.GetAsync(runId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "Restart lost the fault-injected Native settlement run.");
            Assert(loaded.State == ExtractionSettlementState.Consuming &&
                   loaded.SettlementRequestHash == requestHash &&
                   loaded.NativeConsumeReceipt is null,
                "Restart did not preserve the safe Consuming/no-receipt boundary.");
            var replay = await reopened.Store.StartConsumptionAsync(
                runId,
                loaded.UserId,
                "native-fault-key",
                new Dictionary<string, long> { ["Leather"] = 5 },
                cancellationToken,
                requestHash);
            Assert(!replay.Started && !replay.IdempotencyConflict &&
                   transport.Calls.Count == 1,
                "Restart replay redispatched a Native consume without a durable receipt.");

            var uncertain = await reopened.Store.TryMarkUncertainAsync(
                runId,
                loaded.Revision,
                loaded.LeaseId!.Value,
                "NATIVE_RECEIPT_PERSISTENCE_UNCERTAIN",
                "Native succeeded but its receipt commit was fault-injected.",
                cancellationToken);
            Assert(uncertain.Applied &&
                   uncertain.Run.State == ExtractionSettlementState.Uncertain,
                "A lost Native receipt was not durably quarantined as Uncertain.");
            var forbiddenTerminalAdvance = await reopened.Store.TryMarkRemovedAsync(
                runId,
                uncertain.Run.Revision,
                loaded.LeaseId.Value,
                cancellationToken);
            Assert(!forbiddenTerminalAdvance.Applied &&
                   forbiddenTerminalAdvance.Run.State == ExtractionSettlementState.Uncertain,
                "An Uncertain run advanced to Removed without recovered success evidence.");
        }

        using (var finalRestart = CreateStore(directory))
        {
            var terminal = await finalRestart.Store.GetAsync(runId, cancellationToken);
            Assert(terminal?.State == ExtractionSettlementState.Uncertain &&
                   transport.Calls.Count == 1,
                "Restart lost the Uncertain quarantine or duplicated Native consume.");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void VerifyProductionNeverSelectsRcon()
{
    var experimentalState = new NativeBridgeState();
    experimentalState.OnHello(new NativeBridgeHello(
        "1.0",
        "1.0.0.100427",
        "0.3.0-dev",
        ["inventory.probe", "inventory.consume.experimental"],
        new Dictionary<string, bool>()));
    var adapter = new ExtractionNativeInventoryAdapter(
        experimentalState,
        new FakeNativeBridgeTransport());
    var rcon = Options.Create(new ExtractionRconOptions
    {
        Enabled = true,
        AllowDevelopmentSettlement = true
    });
    var mode = Options.Create(new ExtractionModeOptions());
    var safety = Options.Create(new EconomySafetyOptions());
    var isolatedDevelopment = SettlementConfiguration(
        explicitDevelopmentMode: true,
        publicSteam: false);

    var production = new ExtractionSettlementService(
        null!, null!, null!, null!, null!, mode, rcon, null!,
        NullLogger<ExtractionSettlementService>.Instance,
        adapter,
        new TestWebHostEnvironment(Path.GetTempPath()) { EnvironmentName = "Production" },
        safety,
        isolatedDevelopment);
    Assert(!production.SettlementEnabled,
        "Production selected enabled RCON when stable inventory.consume was absent.");

    var development = new ExtractionSettlementService(
        null!, null!, null!, null!, null!, mode, rcon, null!,
        NullLogger<ExtractionSettlementService>.Instance,
        adapter,
        new TestWebHostEnvironment(Path.GetTempPath()) { EnvironmentName = "Development" },
        safety,
        isolatedDevelopment);
    Assert(development.SettlementEnabled,
        "The explicitly isolated Development/RCON diagnostic path was not retained.");
    Assert(development.SettlementAdapter == "development-rcon",
        "The isolated diagnostic path did not report the development-rcon adapter.");

    var publicSteam = new ExtractionSettlementService(
        null!, null!, null!, null!, null!, mode, rcon, null!,
        NullLogger<ExtractionSettlementService>.Instance,
        adapter,
        new TestWebHostEnvironment(Path.GetTempPath()) { EnvironmentName = "Development" },
        safety,
        SettlementConfiguration(explicitDevelopmentMode: true, publicSteam: true));
    Assert(!publicSteam.SettlementEnabled,
        "A public Steam portal selected the RCON settlement diagnostic.");

    var implicitDevelopment = new ExtractionSettlementService(
        null!, null!, null!, null!, null!, mode, rcon, null!,
        NullLogger<ExtractionSettlementService>.Instance,
        adapter,
        new TestWebHostEnvironment(Path.GetTempPath()) { EnvironmentName = "Development" },
        safety,
        SettlementConfiguration(explicitDevelopmentMode: false, publicSteam: false));
    Assert(!implicitDevelopment.SettlementEnabled,
        "ASPNETCORE_ENVIRONMENT alone enabled the RCON settlement diagnostic.");

    var switchDisabled = new ExtractionSettlementService(
        null!, null!, null!, null!, null!, mode,
        Options.Create(new ExtractionRconOptions { Enabled = true }), null!,
        NullLogger<ExtractionSettlementService>.Instance,
        adapter,
        new TestWebHostEnvironment(Path.GetTempPath()) { EnvironmentName = "Development" },
        safety,
        isolatedDevelopment);
    Assert(!switchDisabled.SettlementEnabled,
        "RCON settlement was selected without AllowDevelopmentSettlement=true.");

    var rconDisabled = new ExtractionSettlementService(
        null!, null!, null!, null!, null!, mode,
        Options.Create(new ExtractionRconOptions { AllowDevelopmentSettlement = true }), null!,
        NullLogger<ExtractionSettlementService>.Instance,
        adapter,
        new TestWebHostEnvironment(Path.GetTempPath()) { EnvironmentName = "Development" },
        safety,
        isolatedDevelopment);
    Assert(!rconDisabled.SettlementEnabled,
        "RCON settlement was selected while the RCON adapter was disabled.");

    var nativeRequired = new ExtractionSettlementService(
        null!, null!, null!, null!, null!, mode, rcon, null!,
        NullLogger<ExtractionSettlementService>.Instance,
        adapter,
        new TestWebHostEnvironment(Path.GetTempPath()) { EnvironmentName = "Development" },
        Options.Create(new EconomySafetyOptions { RequireNativeForResourceExchange = true }),
        isolatedDevelopment);
    Assert(!nativeRequired.SettlementEnabled,
        "RCON settlement bypassed RequireNativeForResourceExchange=true.");
}

static IConfiguration SettlementConfiguration(
    bool explicitDevelopmentMode,
    bool publicSteam) =>
    new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:DevelopmentMode"] = explicitDevelopmentMode.ToString(),
            ["PlayerPortal:PublicSteam"] = publicSteam.ToString()
        })
        .Build();

static NativeBridgeState StableState()
{
    var state = new NativeBridgeState();
    state.OnHello(new NativeBridgeHello(
        "1.0",
        "1.0.0.100427",
        "0.4.0",
        ["inventory.probe", "inventory.consume"],
        new Dictionary<string, bool>()));
    return state;
}

static ExtractionNativeInventoryQuoteSnapshot Snapshot(string playerUid)
{
    var empty = Guid.Empty.ToString("N");
    var containers = new[]
    {
        new ExtractionNativeInventoryContainerSnapshot(
            "common",
            Guid.NewGuid().ToString("N"),
            [new ExtractionNativeInventorySlotSnapshot(0, "Leather", 5, empty, empty, false, 0, 0)]),
        new ExtractionNativeInventoryContainerSnapshot(
            "dropSlot", Guid.NewGuid().ToString("N"), []),
        new ExtractionNativeInventoryContainerSnapshot(
            "food", Guid.NewGuid().ToString("N"), [])
    };
    var value = new ExtractionNativeInventoryQuoteSnapshot(
        1, playerUid, DateTimeOffset.UtcNow, containers, string.Empty);
    return value with { SnapshotHash = ExtractionNativeInventoryCanonicalizer.Hash(value) };
}

static ExtractionNativeConsumeReceipt Receipt(string requestHash) => new(
    ExtractionNativeConsumeDisposition.Succeeded,
    requestHash,
    new string('d', 64),
    "succeeded",
    9,
    true,
    true,
    true,
    true,
    [new ExtractionNativeConsumeItemEvidence("Leather", 5, 5, 0, 5)],
    null,
    null,
    DateTimeOffset.UtcNow);

static NativeBridgeResult Result(string state, string dataJson, string? errorCode = null)
{
    using var document = JsonDocument.Parse(dataJson);
    return new NativeBridgeResult(
        Guid.NewGuid(),
        state,
        10,
        document.RootElement.Clone(),
        errorCode is null ? null : new NativeBridgeError(errorCode, "fake bridge result"));
}

static string ProbeJson(Guid playerUid) => $$"""
{
  "observedAt":"2026-07-15T00:00:00Z",
  "mappingReady":true,
  "truncated":false,
  "inventories":[{
    "ownerPlayerUId":"{{playerUid:D}}",
    "containers":[
      {"kind":"common","containerId":"{{Guid.NewGuid():D}}","resolved":true,"slotCount":2,"truncated":false,"slots":[
        {"slotIndex":0,"staticItemId":"Leather","stackCount":5,"dynamicCreatedWorldId":"{{Guid.Empty:D}}","dynamicLocalIdInCreatedWorld":"{{Guid.Empty:D}}","hasDynamicItemData":false,"corruptionProgress":0.0,"corruptionProgressBits":0},
        {"slotIndex":1,"staticItemId":"None","stackCount":0,"dynamicCreatedWorldId":"{{Guid.Empty:D}}","dynamicLocalIdInCreatedWorld":"{{Guid.Empty:D}}","hasDynamicItemData":false,"corruptionProgress":0.0,"corruptionProgressBits":0}
      ]},
      {"kind":"dropSlot","containerId":"{{Guid.NewGuid():D}}","resolved":true,"slotCount":1,"truncated":false,"slots":[
        {"slotIndex":0,"staticItemId":"None","stackCount":0,"dynamicCreatedWorldId":"{{Guid.Empty:D}}","dynamicLocalIdInCreatedWorld":"{{Guid.Empty:D}}","hasDynamicItemData":false,"corruptionProgress":0.0,"corruptionProgressBits":0}
      ]},
      {"kind":"food","containerId":"{{Guid.NewGuid():D}}","resolved":true,"slotCount":1,"truncated":false,"slots":[
        {"slotIndex":0,"staticItemId":"None","stackCount":0,"dynamicCreatedWorldId":"{{Guid.Empty:D}}","dynamicLocalIdInCreatedWorld":"{{Guid.Empty:D}}","hasDynamicItemData":false,"corruptionProgress":0.0,"corruptionProgressBits":0}
      ]}
    ]
  }]
}
""";

static string SuccessJson(int actualConsumed) => $$"""
{
  "applied":true,
  "snapshotMatched":true,
  "persistenceVerified":true,
  "items":[{"itemId":"Leather","requestedQuantity":5,"beforeQuantity":5,"afterQuantity":0,"actualConsumed":{{actualConsumed}}}],
  "settlement":{"liveAggregateVerified":true}
}
""";

static async Task<ExtractionModeException?> CaptureExtractionErrorAsync(Func<Task> action)
{
    try
    {
        await action();
        return null;
    }
    catch (ExtractionModeException exception)
    {
        return exception;
    }
}

static StoreFixture CreateStore(string directory)
{
    var repository = new SqliteExtractionRepository(directory);
    var store = CreateRunStore(directory, repository);
    return new StoreFixture(repository, store);
}

static ExtractionRunStore CreateRunStore(
    string directory,
    IExtractionSettlementPersistence persistence) => new(
        Options.Create(new ExtractionPersistenceOptions { DataDirectory = directory }),
        new TestWebHostEnvironment(directory),
        persistence);

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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class FakeNativeBridgeTransport : INativeBridgeCommandTransport
{
    public Queue<NativeBridgeResult> Results { get; } = new();
    public List<FakeNativeCall> Calls { get; } = [];
    public Exception? Exception { get; set; }

    public Task<NativeBridgeResult> SendCommandAsync(
        string serverId,
        string operation,
        object payload,
        string reason,
        CancellationToken cancellationToken,
        long expectedRevision = 0,
        string? idempotencyKey = null)
    {
        Calls.Add(new FakeNativeCall(serverId, operation, payload, idempotencyKey));
        if (Exception is not null)
        {
            return Task.FromException<NativeBridgeResult>(Exception);
        }
        return Task.FromResult(Results.Dequeue());
    }
}

internal sealed record FakeNativeCall(
    string ServerId,
    string Operation,
    object Payload,
    string? IdempotencyKey);

internal sealed class FaultInjectingSettlementPersistence(
    IExtractionSettlementPersistence inner) : IExtractionSettlementPersistence
{
    private string? _nextPersistFailure;

    public void FailNextPersist(string message) =>
        _nextPersistFailure = message;

    public IReadOnlyList<ExtractionSettlementRun> LoadAndMigrateSettlementRuns(
        string legacyJsonPath) =>
        inner.LoadAndMigrateSettlementRuns(legacyJsonPath);

    public Task PersistSettlementRunWritesAsync(
        IReadOnlyCollection<ExtractionSettlementRunWrite> writes,
        CancellationToken cancellationToken)
    {
        var failure = Interlocked.Exchange(ref _nextPersistFailure, null);
        return failure is null
            ? inner.PersistSettlementRunWritesAsync(writes, cancellationToken)
            : Task.FromException(new IOException(failure));
    }

    public Task<ExtractionRunCreditCommit> CreditRemovedRunAsync(
        ExtractionSettlementRun expectedRun,
        CancellationToken cancellationToken) =>
        inner.CreditRemovedRunAsync(expectedRun, cancellationToken);
}

internal sealed class StoreFixture(
    SqliteExtractionRepository repository,
    ExtractionRunStore store) : IDisposable
{
    public SqliteExtractionRepository Repository { get; } = repository;
    public ExtractionRunStore Store { get; } = store;

    public void Dispose()
    {
        Store.Dispose();
        Repository.Dispose();
    }
}

internal sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "PalControl.NativeSettlementHarness";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRootPath;
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = contentRootPath;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

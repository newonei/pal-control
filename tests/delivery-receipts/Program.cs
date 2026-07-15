using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

const string GameVersion = "1.0.0.100427";
const string AdapterVersion = "1.8.1.3933";

var root = Path.Combine(Path.GetTempPath(), $"pal-control-delivery-receipts-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    await VerifyReceiptStoreAsync(Path.Combine(root, "receipt-store"));
    await VerifyOrderOutcomeProjectionAsync(Path.Combine(root, "order-outcome-projection"));
    VerifyGrantedItemsParser();
    await VerifyGrantAdapterAsync(Path.Combine(root, "grant-adapter"));
    await VerifyQueueHardCapacityAsync(Path.Combine(root, "queue-capacity"));
    await VerifyAcceptedPersistenceFailureNeverDispatchesAsync(
        Path.Combine(root, "accepted-persistence-fault"));
    await VerifySqliteRestartAndLeaseRecoveryAsync(Path.Combine(root, "sqlite-restart"));
    await VerifyDispatchedCrashNeverResendsAsync(Path.Combine(root, "dispatched-crash"));
    await VerifyAfterDurableDispatchFaultAsync(
        Path.Combine(root, "after-durable-dispatch-fault"));
    await VerifyTerminalAckPersistenceFaultAsync(
        Path.Combine(root, "terminal-ack-persistence-fault"));
    await VerifyReceiptPersistenceFaultRecoveryAsync(
        Path.Combine(root, "receipt-persistence-fault"));
    await VerifyDeadLetterAndObservabilityAsync(Path.Combine(root, "dead-letter"));
    await VerifyLegacyJsonlMigrationAsync(Path.Combine(root, "legacy-migration"));
    VerifyLegacyMigrationFailsClosed(Path.Combine(root, "legacy-conflict"));

    Console.WriteLine(
        "PASS: immutable receipts plus SQLite outbox concurrency, deterministic persistence/dispatch/ACK faults, restart recovery, no-resend, dead-letter, observability, and fail-closed JSONL migration.");
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch
    {
        // Windows may briefly retain SQLite or event-log handles after process teardown.
    }
}

static async Task VerifyOrderOutcomeProjectionAsync(string directory)
{
    var store = new ExtractionDeliveryReceiptStore(directory);
    var delivery = CreateDelivery(
        "order-outcome-player",
        [new ShopItemGrant("Partial_Item", 5)]);
    var registration = await store.RegisterAsync(
        delivery,
        GameVersion,
        AdapterVersion,
        CancellationToken.None);
    var acknowledgedAt = registration.Request.CreatedAt.AddSeconds(1);
    var partialReceipt = new ExtractionDeliveryReceiptV1(
        ExtractionDeliveryReceiptContract.SchemaVersion,
        registration.Request.DeliveryId,
        registration.Request.IdempotencyKey,
        registration.Request.RequestHash,
        registration.Request.ResultId,
        registration.Request.ServerId,
        registration.Request.PlayerUid,
        registration.Request.WorldId,
        registration.Request.GameVersion,
        registration.Request.AdapterVersion,
        registration.Request.CommandVersion,
        acknowledgedAt,
        [new ExtractionDeliveryReceiptItem(
            "Partial_Item",
            5,
            2,
            Guid.NewGuid(),
            ExtractionDeliveryReceiptItemResult.Partial,
            acknowledgedAt)],
        ExtractionDeliveryReceiptOutcome.Partial,
        acknowledgedAt);
    await store.SaveReceiptAsync(partialReceipt, CancellationToken.None);

    var now = DateTimeOffset.UtcNow;
    var order = new ShopOrder(
        delivery.OrderId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        delivery.ServerId,
        delivery.PlayerIdentifier,
        [new ShopOrderLine(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PARTIAL-ITEM",
            "Partial item",
            1,
            ExtractionCurrency.MarketCoin,
            10,
            10,
            delivery.Items)],
        [new ShopOrderCharge(ExtractionCurrency.MarketCoin, 10)],
        ShopOrderState.DeliveryUncertain,
        delivery.DeliveryId,
        delivery.Attempt,
        "purchase-outcome-projection-0001",
        "delivery-receipts-harness",
        "order outcome protocol",
        now,
        now,
        delivery.PlayerUid,
        delivery.WorldId);

    var projected = await ExtractionModeEndpoints.OrderDtosAsync(
        [order],
        store,
        CancellationToken.None);
    var partial = JsonSerializer.SerializeToNode(projected.Single())!.AsObject();
    Assert(partial["state"]?.GetValue<string>() == "partial" &&
           partial["statusMessage"]?.GetValue<string>().Contains("部分物品到账", StringComparison.Ordinal) == true,
        "A DeliveryUncertain order with a persisted Partial receipt was not projected as partial.");

    var uncertain = JsonSerializer.SerializeToNode(
        ExtractionModeEndpoints.OrderDto(order))!.AsObject();
    var refunded = JsonSerializer.SerializeToNode(
        ExtractionModeEndpoints.OrderDto(
            order with { State = ShopOrderState.Refunded },
            partialReceipt))!.AsObject();
    var accepted = JsonSerializer.SerializeToNode(
        ExtractionModeEndpoints.OrderDto(
            order with { State = ShopOrderState.PendingDelivery }))!.AsObject();
    Assert(uncertain["state"]?.GetValue<string>() == "uncertain",
        "An uncertain order without a receipt did not remain uncertain.");
    Assert(refunded["state"]?.GetValue<string>() == "refunded",
        "A refunded order was confused with a cancelled order.");
    Assert(accepted["state"]?.GetValue<string>() == "accepted",
        "A newly created order without a receipt did not preserve the accepted DTO contract.");
}

static async Task VerifyReceiptStoreAsync(string directory)
{
    var store = new ExtractionDeliveryReceiptStore(directory);
    var delivery = CreateDelivery(
        "receipt-store-player",
        [new ShopItemGrant("Wood", 5), new ShopItemGrant("Stone", 3)]);

    var first = await store.RegisterAsync(
        delivery,
        GameVersion,
        AdapterVersion,
        CancellationToken.None);
    Assert(first.Created && !first.IdempotencyConflict && first.Receipt is null,
        "The first receipt request was not registered as a new immutable request.");
    Assert(first.Request.ResultId != Guid.Empty,
        "Receipt registration did not allocate a stable result id.");

    var replay = await store.RegisterAsync(
        delivery,
        GameVersion,
        AdapterVersion,
        CancellationToken.None);
    Assert(!replay.Created && !replay.IdempotencyConflict,
        "An exact receipt-request replay was not accepted.");
    Assert(replay.Request.ResultId == first.Request.ResultId &&
           replay.Request.RequestHash == first.Request.RequestHash,
        "An exact replay changed ResultId or request hash.");

    var changedQuantity = delivery with
    {
        Items = [new ShopItemGrant("Wood", 6), new ShopItemGrant("Stone", 3)]
    };
    var conflictingPayload = await store.RegisterAsync(
        changedQuantity,
        GameVersion,
        AdapterVersion,
        CancellationToken.None);
    Assert(!conflictingPayload.Created && conflictingPayload.IdempotencyConflict,
        "The same idempotency key with a different item quantity did not conflict.");
    Assert(conflictingPayload.Request.ResultId == first.Request.ResultId,
        "A conflicting replay did not return the original stable result id.");

    var changedIdentity = delivery with { PlayerUid = Guid.NewGuid().ToString("N") };
    var conflictingIdentity = await store.RegisterAsync(
        changedIdentity,
        GameVersion,
        AdapterVersion,
        CancellationToken.None);
    Assert(!conflictingIdentity.Created && conflictingIdentity.IdempotencyConflict,
        "The same idempotency key with a different PlayerUID did not conflict.");

    var acknowledgedAt = DateTimeOffset.UtcNow;
    var finalReceipt = CreateSuccessfulReceipt(first.Request, acknowledgedAt);
    var saved = await store.SaveReceiptAsync(finalReceipt, CancellationToken.None);
    var exactFinalReplay = await store.SaveReceiptAsync(finalReceipt, CancellationToken.None);
    Assert(ReceiptsEqual(saved, exactFinalReplay) &&
           saved.ResultId == first.Request.ResultId,
        "An exact final-receipt replay did not return the original receipt.");

    await AssertThrowsAsync<InvalidOperationException>(
        () => store.SaveReceiptAsync(
            finalReceipt with { AcknowledgedAt = acknowledgedAt.AddMilliseconds(1) },
            CancellationToken.None),
        "A finalized receipt was replaced by different evidence.");

    var reopened = new ExtractionDeliveryReceiptStore(directory);
    var afterRestart = await reopened.GetAsync(delivery.DeliveryId, CancellationToken.None);
    Assert(afterRestart?.Receipt is not null &&
           ReceiptsEqual(afterRestart.Receipt, finalReceipt) &&
           afterRestart.Request.ResultId == first.Request.ResultId,
        "The immutable final receipt or stable result id was lost after store reconstruction.");
    var replayAfterRestart = await reopened.RegisterAsync(
        delivery,
        GameVersion,
        AdapterVersion,
        CancellationToken.None);
    Assert(!replayAfterRestart.Created && !replayAfterRestart.IdempotencyConflict &&
           replayAfterRestart.Request.ResultId == first.Request.ResultId &&
           replayAfterRestart.Receipt is not null &&
           ReceiptsEqual(replayAfterRestart.Receipt, finalReceipt),
        "Exact request replay after store reconstruction did not return the persisted ResultId and receipt.");
    var finalReplayAfterRestart = await reopened.SaveReceiptAsync(
        finalReceipt,
        CancellationToken.None);
    Assert(ReceiptsEqual(finalReplayAfterRestart, finalReceipt),
        "Exact final-receipt replay after store reconstruction was not stable.");
}

static void VerifyGrantedItemsParser()
{
    Assert(PalDefenderItemGrantAdapter.TryReadGrantedItems(
               JsonNode.Parse("{\"Granted\":{\"Items\":7}}"),
               out var exact) && exact == 7,
        "A normal Granted.Items acknowledgement was not parsed.");
    Assert(PalDefenderItemGrantAdapter.TryReadGrantedItems(
               JsonNode.Parse("{\"gRaNtEd\":{\"iTeMs\":2}}"),
               out var caseInsensitive) && caseInsensitive == 2,
        "Granted.Items parsing is unexpectedly case-sensitive.");
    Assert(!PalDefenderItemGrantAdapter.TryReadGrantedItems(
               JsonNode.Parse("{\"Granted\":{\"Items\":\"unknown\"}}"),
               out _),
        "A malformed Granted.Items value was accepted.");
    Assert(!PalDefenderItemGrantAdapter.TryReadGrantedItems(
               JsonNode.Parse("{\"Granted\":{\"Items\":-1}}"),
               out _),
        "A negative Granted.Items value was accepted.");
}

static async Task VerifyGrantAdapterAsync(string directory)
{
    var handler = new RecordingGrantHandler();
    await using var fixture = QueueFixture.Create(directory, capacity: 32, handler);
    await fixture.StartAsync();
    var store = new ExtractionDeliveryReceiptStore(Path.Combine(directory, "receipts"));
    var adapter = new PalDefenderItemGrantAdapter(fixture.Queue, TimeProvider.System);

    var succeeded = await DispatchAndBuildAsync(
        store,
        adapter,
        fixture.Queue,
        CreateDelivery(
            "player-exact-receipt",
            [new ShopItemGrant("Z_Exact", 4), new ShopItemGrant("A_Exact", 2)]));
    Assert(succeeded.Receipt.Outcome == ExtractionDeliveryReceiptOutcome.Succeeded &&
           succeeded.Receipt.Items.All(item =>
               item.Result == ExtractionDeliveryReceiptItemResult.Succeeded &&
               item.Granted == item.Requested),
        "Exact per-item acknowledgements did not produce a successful receipt.");

    var partial = await DispatchAndBuildAsync(
        store,
        adapter,
        fixture.Queue,
        CreateDelivery(
            "player-partial-receipt",
            [new ShopItemGrant("Exact_Line", 3), new ShopItemGrant("Partial_Line", 5)]));
    Assert(partial.Receipt.Outcome == ExtractionDeliveryReceiptOutcome.Partial,
        "A mixed exact/partial grant did not produce a Partial receipt.");
    var partialLine = partial.Receipt.Items.Single(item => item.ItemId == "Partial_Line");
    Assert(partialLine.Result == ExtractionDeliveryReceiptItemResult.Partial &&
           partialLine.Granted == 4,
        "A lower Granted.Items count was not preserved as per-item partial evidence.");

    var malformed = await DispatchAndBuildAsync(
        store,
        adapter,
        fixture.Queue,
        CreateDelivery(
            "player-malformed-receipt",
            [new ShopItemGrant("Malformed_Line", 7)]));
    Assert(malformed.Receipt.Outcome == ExtractionDeliveryReceiptOutcome.Uncertain &&
           malformed.Receipt.Items.Single().Result ==
               ExtractionDeliveryReceiptItemResult.InvalidReceipt &&
           malformed.Receipt.Items.Single().Granted is null,
        "A malformed acknowledgement was not classified as uncertain evidence.");

    var missingDelivery = CreateDelivery(
        "player-missing-command",
        [new ShopItemGrant("Missing_Command", 2)]);
    var missingRegistration = await store.RegisterAsync(
        missingDelivery,
        GameVersion,
        AdapterVersion,
        CancellationToken.None);
    var missing = await adapter.TryBuildReceiptAsync(
        missingRegistration.Request,
        finalizeMissingCommands: true,
        CancellationToken.None);
    Assert(!missing.Pending && missing.Receipt is not null &&
           missing.Receipt.Outcome == ExtractionDeliveryReceiptOutcome.Uncertain &&
           missing.Receipt.AcknowledgedAt is null &&
           missing.Receipt.Items.Single().Result ==
               ExtractionDeliveryReceiptItemResult.CommandRecordMissing,
        "A missing durable command invented an ACK or escaped uncertain classification.");
    _ = await store.SaveReceiptAsync(missing.Receipt!, CancellationToken.None);

    Assert(handler.Requests.Count == 5,
        "The adapter did not issue exactly one upstream request per requested ItemID.");
    foreach (var request in handler.Requests)
    {
        var items = request.Body["Items"] as JsonArray;
        Assert(items?.Count == 1 && items[0] is JsonObject,
            "An upstream give/items command contained more than one ItemID.");
    }
    Assert(handler.Requests
               .Select(request => request.ItemId)
               .Order(StringComparer.Ordinal)
               .SequenceEqual(
                   new[]
                   {
                       "A_Exact", "Exact_Line", "Malformed_Line", "Partial_Line", "Z_Exact"
                   }.Order(StringComparer.Ordinal)),
        "The one-command-per-item adapter lost or duplicated an ItemID.");

    foreach (var result in new[] { succeeded, partial, malformed })
    {
        await AssertCommandEvidenceAsync(fixture.Queue, result);
    }
}

static async Task<CompletedDispatch> DispatchAndBuildAsync(
    ExtractionDeliveryReceiptStore store,
    PalDefenderItemGrantAdapter adapter,
    PalDefenderCommandQueue queue,
    ShopDeliveryWorkItem delivery)
{
    var registration = await store.RegisterAsync(
        delivery,
        GameVersion,
        AdapterVersion,
        CancellationToken.None);
    Assert(registration.Created && !registration.IdempotencyConflict,
        "The adapter test could not register its receipt request.");

    var dispatch = await adapter.EnsureEnqueuedAsync(
        delivery,
        registration.Request,
        "delivery receipt harness",
        "extraction-delivery-worker",
        CancellationToken.None);
    Assert(dispatch.AllAccepted && !dispatch.IdempotencyConflict &&
           !dispatch.CapacityExceeded &&
           dispatch.CommandIds.Count == registration.Request.Items.Count,
        "The item-grant adapter did not accept one command per request line.");

    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (DateTimeOffset.UtcNow < deadline)
    {
        var candidate = await adapter.TryBuildReceiptAsync(
            registration.Request,
            finalizeMissingCommands: false,
            CancellationToken.None);
        if (!candidate.Pending && candidate.Receipt is not null)
        {
            return new CompletedDispatch(delivery, dispatch.CommandIds, candidate.Receipt);
        }
        await Task.Delay(20);
    }
    throw new TimeoutException("The PalDefender command queue did not produce a terminal receipt.");
}

static async Task AssertCommandEvidenceAsync(
    PalDefenderCommandQueue queue,
    CompletedDispatch completed)
{
    var snapshots = new List<PalDefenderCommandSnapshot>();
    foreach (var commandId in completed.CommandIds)
    {
        var snapshot = await queue.GetSnapshotAsync(commandId, CancellationToken.None);
        Assert(snapshot is not null && snapshot.CompletedAt is not null,
            "A receipt line was finalized without a command completion timestamp.");
        snapshots.Add(snapshot!);

        Assert(snapshot!.UpstreamPath == completed.Delivery.UpstreamPath,
            "The internal command snapshot lost the actual execution path.");
        var status = await queue.GetStatusAsync(commandId, CancellationToken.None);
        var statusJson = JsonSerializer.Serialize(status);
        Assert(statusJson.Contains("give/items/[redacted]", StringComparison.Ordinal) &&
               !statusJson.Contains(
                   completed.Delivery.PlayerIdentifier,
                   StringComparison.OrdinalIgnoreCase),
            "The external command status exposed the full player identifier.");
    }

    var maxCompletedAt = snapshots.Max(snapshot => snapshot.CompletedAt!.Value);
    Assert(completed.Receipt.AcknowledgedAt == maxCompletedAt,
        "Receipt ACK time was not derived from the latest durable command CompletedAt.");
    Assert(completed.Receipt.Items.All(item => item.CompletedAt is not null),
        "A terminal receipt line omitted its command CompletedAt evidence.");

    var commandIds = completed.CommandIds.ToHashSet();
    var audit = (await queue.GetAuditAsync(1_000, CancellationToken.None))
        .Where(item => commandIds.Contains(item.CommandId))
        .ToArray();
    Assert(audit.Length >= completed.CommandIds.Count * 3,
        "The command audit omitted accepted, dispatched, or terminal evidence.");
    Assert(audit.All(item => item.UpstreamPath == "give/items/[redacted]") &&
           audit.All(item => !item.UpstreamPath.Contains(
               completed.Delivery.PlayerIdentifier,
               StringComparison.OrdinalIgnoreCase)),
        "The external command audit exposed the full player identifier.");
}

static async Task VerifyQueueHardCapacityAsync(string directory)
{
    const int concurrency = 100;
    const int capacity = 11;
    var handler = new RecordingGrantHandler();
    await using var fixture = QueueFixture.Create(directory, capacity, handler);

    var attempts = Enumerable.Range(0, concurrency)
        .Select(index => fixture.Queue.EnqueueAsync(
            "local",
            $"give/items/capacity-player-{index:D3}",
            CapacityBody(index),
            $"capacity-key-{index:D3}",
            "capacity harness",
            "capacity-harness",
            CancellationToken.None))
        .ToArray();
    var results = await Task.WhenAll(attempts);

    Assert(results.Count(result => result.Created) == capacity,
        "Concurrent enqueue accepted a count different from the hard capacity.");
    Assert(results.Count(result => result.CapacityExceeded) == concurrency - capacity,
        "Concurrent enqueue did not reject every request beyond hard capacity.");
    Assert(results.All(result => !result.IdempotencyConflict),
        "Unique concurrent requests unexpectedly collided on idempotency.");

    var acceptedIndex = Array.FindIndex(results, result => result.Created);
    Assert(acceptedIndex >= 0 && results[acceptedIndex].Command is not null,
        "The capacity test did not retain an accepted command for replay.");
    var original = results[acceptedIndex].Command!;
    var replay = await fixture.Queue.EnqueueAsync(
        "local",
        $"give/items/capacity-player-{acceptedIndex:D3}",
        CapacityBody(acceptedIndex),
        $"capacity-key-{acceptedIndex:D3}",
        "capacity harness",
        "capacity-harness",
        CancellationToken.None);
    Assert(!replay.Created && !replay.CapacityExceeded &&
           !replay.IdempotencyConflict && replay.Command?.CommandId == original.CommandId,
        "A full queue did not replay an existing exact idempotency key.");

    var conflict = await fixture.Queue.EnqueueAsync(
        "local",
        $"give/items/capacity-player-{acceptedIndex:D3}",
        CapacityBody(acceptedIndex + 1_000),
        $"capacity-key-{acceptedIndex:D3}",
        "capacity harness",
        "capacity-harness",
        CancellationToken.None);
    Assert(!conflict.Created && conflict.IdempotencyConflict &&
           !conflict.CapacityExceeded && conflict.Command is null,
        "A full queue did not prioritize same-key/different-payload conflict detection.");

    var load = await fixture.Queue.GetEconomyLoadAsync(CancellationToken.None);
    Assert(load.Pending == capacity && load.Capacity == capacity,
        "The reported queue load diverged from the enforced hard capacity.");
    Assert(handler.Requests.Count == 0,
        "An unstarted capacity-test queue unexpectedly dispatched a command.");

    var audit = await fixture.Queue.GetAuditAsync(1_000, CancellationToken.None);
    Assert(audit.Count == capacity &&
           audit.All(item => item.UpstreamPath == "give/items/[redacted]"),
        "Capacity-test audit count or player-path redaction is incorrect.");
}

static async Task VerifyAcceptedPersistenceFailureNeverDispatchesAsync(string root)
{
    var handler = new RecordingGrantHandler();
    await using (var faulted = QueueFixture.Create(root, 8, handler))
    {
        ExecuteSql(
            root,
            """
            CREATE TRIGGER fail_command_accept
            BEFORE INSERT ON paldefender_commands
            BEGIN
                SELECT RAISE(ABORT, 'injected accepted persistence failure');
            END;
            """);

        await AssertThrowsAsync<SqliteException>(
            () => faulted.Queue.EnqueueAsync(
                "local",
                "give/items/accepted-persistence-player",
                CapacityBody(710),
                "accepted-persistence-key-0001",
                "accepted persistence fault harness",
                "fault-harness",
                CancellationToken.None),
            "A failed durable accepted insert was not surfaced.");
        Assert(handler.Requests.Count == 0 &&
               QueryScalar(root, "SELECT COUNT(*) FROM paldefender_commands;") == 0 &&
               QueryScalar(root, "SELECT COUNT(*) FROM paldefender_command_events;") == 0,
            "A failed accepted transaction leaked a command/event or reached PalDefender.");
    }

    ExecuteSql(root, "DROP TRIGGER fail_command_accept;");
    await using (var restarted = QueueFixture.Create(root, 8, handler))
    {
        var accepted = await restarted.Queue.EnqueueAsync(
            "local",
            "give/items/accepted-persistence-player",
            CapacityBody(710),
            "accepted-persistence-key-0001",
            "accepted persistence fault harness",
            "fault-harness",
            CancellationToken.None);
        Assert(accepted.Created && accepted.Command is not null &&
               QueryScalar(root, "SELECT COUNT(*) FROM paldefender_commands;") == 1 &&
               QueryScalar(root, "SELECT COUNT(*) FROM paldefender_command_events;") == 1 &&
               handler.Requests.Count == 0,
            "The queue did not recover cleanly after the accepted persistence fault was removed.");
    }
}

static async Task VerifyAfterDurableDispatchFaultAsync(string root)
{
    var handler = new RecordingGrantHandler();
    Guid commandId;
    await using (var faulted = QueueFixture.Create(
        root,
        8,
        handler,
        dependencyProbe: null,
        faultInjector: new ThrowingQueueFaultInjector(
            PalDefenderCommandFaultPoint.AfterDurableDispatch)))
    {
        var accepted = await faulted.Queue.EnqueueAsync(
            "local",
            "give/items/post-dispatch-persistence-player",
            CapacityBody(711),
            "post-dispatch-persistence-key-0001",
            "post-dispatch persistence fault harness",
            "fault-harness",
            CancellationToken.None);
        commandId = accepted.Command?.CommandId ??
            throw new InvalidOperationException("The post-dispatch fault command was not accepted.");
        await faulted.StartAsync();
        var uncertain = await WaitForStateAsync(
            faulted.Queue,
            commandId,
            "uncertain",
            TimeSpan.FromSeconds(5));
        Assert(uncertain.Error?.Code == "COMMAND_OUTCOME_UNCERTAIN" &&
               handler.Requests.Count == 0,
            "A fault after durable dispatch reached PalDefender or escaped Uncertain.");
        var audit = await faulted.Queue.GetAuditAsync(100, CancellationToken.None);
        Assert(audit.Any(item => item.CommandId == commandId &&
                                 item.EventType == "dispatched") &&
               audit.Any(item => item.CommandId == commandId &&
                                 item.EventType == "persistence-interrupted-uncertain"),
            "The post-dispatch fault omitted durable dispatch/uncertain evidence.");
    }

    await using (var restarted = QueueFixture.Create(root, 8, handler))
    {
        await restarted.StartAsync();
        await Task.Delay(150);
        var status = await restarted.Queue.GetStatusAsync(commandId, CancellationToken.None);
        Assert(status?.State == "uncertain" && handler.Requests.Count == 0,
            "Restart resent a command interrupted after durable dispatch.");
    }
}

static async Task VerifyTerminalAckPersistenceFaultAsync(string root)
{
    var handler = new RecordingGrantHandler();
    Guid commandId;
    await using (var faulted = QueueFixture.Create(
        root,
        8,
        handler,
        dependencyProbe: null,
        faultInjector: new ThrowingQueueFaultInjector(
            PalDefenderCommandFaultPoint.BeforeTerminalPersistence)))
    {
        var accepted = await faulted.Queue.EnqueueAsync(
            "local",
            "give/items/terminal-persistence-player",
            CapacityBody(712),
            "terminal-persistence-key-0001",
            "terminal ACK persistence fault harness",
            "fault-harness",
            CancellationToken.None);
        commandId = accepted.Command?.CommandId ??
            throw new InvalidOperationException("The terminal-persistence command was not accepted.");
        await faulted.StartAsync();
        var uncertain = await WaitForStateAsync(
            faulted.Queue,
            commandId,
            "uncertain",
            TimeSpan.FromSeconds(5));
        Assert(uncertain.Error?.Code == "COMMAND_OUTCOME_UNCERTAIN" &&
               handler.Requests.Count == 1,
            "A success ACK persistence fault did not quarantine exactly one upstream call.");
        var audit = await faulted.Queue.GetAuditAsync(100, CancellationToken.None);
        Assert(audit.Any(item => item.CommandId == commandId &&
                                 item.EventType == "dispatched") &&
               !audit.Any(item => item.CommandId == commandId &&
                                  item.EventType == "succeeded") &&
               audit.Any(item => item.CommandId == commandId &&
                                 item.EventType == "persistence-interrupted-uncertain"),
            "A failed terminal ACK commit invented success or omitted uncertain evidence.");
    }

    await using (var restarted = QueueFixture.Create(root, 8, handler))
    {
        await restarted.StartAsync();
        await Task.Delay(150);
        var status = await restarted.Queue.GetStatusAsync(commandId, CancellationToken.None);
        Assert(status?.State == "uncertain" && handler.Requests.Count == 1,
            "Restart resent a delivery whose upstream ACK could not be persisted.");
    }
}

static async Task VerifyReceiptPersistenceFaultRecoveryAsync(string root)
{
    var handler = new RecordingGrantHandler();
    var receiptDirectory = Path.Combine(root, "extraction");
    var receiptStore = new ExtractionDeliveryReceiptStore(receiptDirectory);
    var delivery = CreateDelivery(
        "receipt-persistence-player",
        [new ShopItemGrant("Receipt_Fault_Item", 3)]);
    var registration = await receiptStore.RegisterAsync(
        delivery,
        GameVersion,
        AdapterVersion,
        CancellationToken.None);
    Assert(registration.Created && registration.Receipt is null,
        "The receipt-persistence request was not committed before dispatch.");

    IReadOnlyList<Guid> commandIds;
    ExtractionDeliveryReceiptV1 firstReceipt;
    await using (var fixture = QueueFixture.Create(root, 8, handler))
    {
        await fixture.StartAsync();
        var adapter = new PalDefenderItemGrantAdapter(fixture.Queue, TimeProvider.System);
        var dispatch = await adapter.EnsureEnqueuedAsync(
            delivery,
            registration.Request,
            "receipt persistence fault harness",
            "fault-harness",
            CancellationToken.None);
        Assert(dispatch.AllAccepted && dispatch.CommandIds.Count == 1,
            "The receipt-persistence delivery was not durably enqueued.");
        commandIds = dispatch.CommandIds;
        firstReceipt = await WaitForReceiptAsync(adapter, registration.Request);
        Assert(firstReceipt.Outcome == ExtractionDeliveryReceiptOutcome.Succeeded &&
               handler.Requests.Count == 1,
            "The receipt-persistence upstream command did not produce exact ACK evidence.");

        ExecuteSql(
            root,
            $"""
            CREATE TRIGGER fail_delivery_receipt_commit
            BEFORE UPDATE OF receipt_json ON extraction_delivery_receipts
            WHEN OLD.delivery_id = '{delivery.DeliveryId:D}'
            BEGIN
                SELECT RAISE(ABORT, 'injected delivery receipt persistence failure');
            END;
            """);
        await AssertThrowsAsync<SqliteException>(
            () => receiptStore.SaveReceiptAsync(firstReceipt, CancellationToken.None),
            "The delivery receipt persistence failure was not surfaced.");
        var afterFailure = await receiptStore.GetAsync(
            delivery.DeliveryId,
            CancellationToken.None);
        Assert(afterFailure is not null && afterFailure.Receipt is null,
            "A failed receipt transaction leaked non-durable terminal evidence.");
    }

    await using (var restarted = QueueFixture.Create(root, 8, handler))
    {
        await restarted.StartAsync();
        await Task.Delay(150);
        var durableCommand = await restarted.Queue.GetStatusAsync(
            commandIds.Single(),
            CancellationToken.None);
        Assert(durableCommand?.State == "succeeded" && handler.Requests.Count == 1,
            "Restart resent a command after only the receipt commit failed.");

        var adapter = new PalDefenderItemGrantAdapter(restarted.Queue, TimeProvider.System);
        var rebuilt = await WaitForReceiptAsync(adapter, registration.Request);
        Assert(ReceiptsEqual(firstReceipt with { CreatedAt = rebuilt.CreatedAt }, rebuilt),
            "Durable command snapshots did not rebuild the same ACK evidence.");
        ExecuteSql(root, "DROP TRIGGER fail_delivery_receipt_commit;");
        _ = await receiptStore.SaveReceiptAsync(rebuilt, CancellationToken.None);
        firstReceipt = rebuilt;
    }

    var reopened = new ExtractionDeliveryReceiptStore(receiptDirectory);
    var persisted = await reopened.GetAsync(delivery.DeliveryId, CancellationToken.None);
    Assert(persisted?.Receipt is not null &&
           ReceiptsEqual(persisted.Receipt, firstReceipt) &&
           handler.Requests.Count == 1,
        "Recovered receipt evidence was not immutable across restart.");
    await AssertThrowsAsync<InvalidOperationException>(
        () => reopened.SaveReceiptAsync(
            firstReceipt with { CreatedAt = firstReceipt.CreatedAt.AddMilliseconds(1) },
            CancellationToken.None),
        "A recovered terminal receipt was later replaced.");
}

static async Task<ExtractionDeliveryReceiptV1> WaitForReceiptAsync(
    PalDefenderItemGrantAdapter adapter,
    ExtractionDeliveryReceiptRequest request)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (DateTimeOffset.UtcNow < deadline)
    {
        var candidate = await adapter.TryBuildReceiptAsync(
            request,
            finalizeMissingCommands: false,
            CancellationToken.None);
        if (!candidate.Pending && candidate.Receipt is not null)
        {
            return candidate.Receipt;
        }
        await Task.Delay(20);
    }
    throw new TimeoutException("The durable command evidence did not produce a receipt.");
}

static async Task VerifySqliteRestartAndLeaseRecoveryAsync(string root)
{
    var handler = new RecordingGrantHandler();
    Guid commandId;
    var body = CapacityBody(701);
    await using (var first = QueueFixture.Create(root, 8, handler))
    {
        var accepted = await first.Queue.EnqueueAsync(
            "local",
            "give/items/restart-player",
            body,
            "restart-safe-key-0001",
            "restart lease harness",
            "restart-harness",
            CancellationToken.None);
        Assert(accepted.Created && accepted.Command is not null,
            "The restart harness could not durably accept a command.");
        commandId = accepted.Command!.CommandId;
        ExecuteSql(
            root,
            """
            UPDATE paldefender_commands
            SET lease_owner = 'crashed-worker',
                lease_until = $leaseUntil,
                attempt_count = 1,
                updated_at = $updatedAt
            WHERE command_id = $commandId AND state = 'accepted';
            """,
            ("$leaseUntil", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")),
            ("$updatedAt", DateTimeOffset.UtcNow.ToString("O")),
            ("$commandId", commandId.ToString("D")));
    }

    await using (var restarted = QueueFixture.Create(root, 8, handler))
    {
        await restarted.StartAsync();
        var status = await WaitForStateAsync(
            restarted.Queue,
            commandId,
            "succeeded",
            TimeSpan.FromSeconds(10));
        Assert(status.State == "succeeded" && handler.Requests.Count == 1,
            "An accepted leased command was not recovered and dispatched exactly once.");
        var replay = await restarted.Queue.EnqueueAsync(
            "local",
            "give/items/restart-player",
            body,
            "restart-safe-key-0001",
            "restart lease harness",
            "restart-harness",
            CancellationToken.None);
        Assert(!replay.Created && !replay.IdempotencyConflict &&
               replay.Command?.CommandId == commandId,
            "SQLite idempotency did not replay the same command after restart.");
    }

    await using (var secondRestart = QueueFixture.Create(root, 8, handler))
    {
        await secondRestart.StartAsync();
        await Task.Delay(150);
        var status = await secondRestart.Queue.GetStatusAsync(commandId, CancellationToken.None);
        Assert(status?.State == "succeeded" && handler.Requests.Count == 1,
            "A terminal SQLite command was resent after a second restart.");
    }
    AssertThrows<SqliteException>(
        () =>
        {
            ExecuteSql(
                root,
                "UPDATE paldefender_commands SET state = 'accepted' WHERE command_id = $commandId;",
                ("$commandId", commandId.ToString("D")));
            return new object();
        },
        "A terminal SQLite command could be reopened.");
    AssertThrows<SqliteException>(
        () =>
        {
            ExecuteSql(
                root,
                "UPDATE paldefender_command_events SET reason = 'tampered';");
            return new object();
        },
        "An immutable PalDefender command event could be updated.");
}

static async Task VerifyDispatchedCrashNeverResendsAsync(string root)
{
    var handler = new RecordingGrantHandler();
    Guid commandId;
    PalDefenderCommandSnapshot snapshot;
    var body = CapacityBody(702);
    const string path = "give/items/dispatched-crash-player";
    const string key = "dispatched-crash-key-0001";
    const string reason = "dispatched crash harness";
    const string actor = "restart-harness";
    await using (var first = QueueFixture.Create(root, 8, handler))
    {
        var accepted = await first.Queue.EnqueueAsync(
            "local", path, body, key, reason, actor, CancellationToken.None);
        commandId = accepted.Command?.CommandId ??
            throw new InvalidOperationException("The dispatched-crash command was not accepted.");
        snapshot = await first.Queue.GetSnapshotAsync(commandId, CancellationToken.None) ??
            throw new InvalidOperationException("The dispatched-crash snapshot is missing.");
        var dispatchedAt = DateTimeOffset.UtcNow;
        ExecuteSql(
            root,
            """
            UPDATE paldefender_commands
            SET state = 'dispatched', dispatched_at = $at, updated_at = $at,
                lease_owner = NULL, lease_until = NULL, attempt_count = 1
            WHERE command_id = $commandId AND state = 'accepted';
            INSERT INTO paldefender_command_events (
                event_id, command_id, event_type, state, at, server_id,
                upstream_path, idempotency_key, request_hash, reason, actor)
            VALUES ($eventId, $commandId, 'dispatched', 'dispatched', $at,
                    'local', $path, $key, $hash, $reason, $actor);
            """,
            ("$at", dispatchedAt.ToString("O")),
            ("$commandId", commandId.ToString("D")),
            ("$eventId", Guid.NewGuid().ToString("D")),
            ("$path", path),
            ("$key", key),
            ("$hash", snapshot.RequestHash),
            ("$reason", reason),
            ("$actor", actor));
    }

    await using (var restarted = QueueFixture.Create(root, 8, handler))
    {
        await restarted.StartAsync();
        var status = await WaitForStateAsync(
            restarted.Queue,
            commandId,
            "uncertain",
            TimeSpan.FromSeconds(5));
        Assert(status.Error?.Code == "COMMAND_OUTCOME_UNCERTAIN" &&
               handler.Requests.Count == 0,
            "A crash-recovered dispatched command was resent or not classified uncertain.");
        var audit = await restarted.Queue.GetAuditAsync(100, CancellationToken.None);
        Assert(audit.Any(item => item.CommandId == commandId &&
                                 item.EventType == "recovered-uncertain"),
            "Crash recovery did not append immutable recovered-uncertain evidence.");
    }
}

static async Task VerifyDeadLetterAndObservabilityAsync(string root)
{
    var handler = new RecordingGrantHandler();
    Guid commandId;
    await using (var seed = QueueFixture.Create(root, 8, handler))
    {
        var accepted = await seed.Queue.EnqueueAsync(
            "local",
            "give/items/dead-letter-player",
            CapacityBody(703),
            "dead-letter-key-0001",
            "dead letter harness",
            "extraction-delivery-worker",
            CancellationToken.None);
        commandId = accepted.Command?.CommandId ??
            throw new InvalidOperationException("The dead-letter command was not accepted.");
    }
    ExecuteSql(
        root,
        """
        UPDATE paldefender_commands SET failure_count = 4
        WHERE command_id = $commandId;
        """,
        ("$commandId", commandId.ToString("D")));
    await using var fixture = QueueFixture.Create(
        root,
        8,
        handler,
        dependencyProbe: null,
        faultInjector: new ThrowingQueueFaultInjector(
            PalDefenderCommandFaultPoint.BeforeDurableDispatch));
    await fixture.StartAsync();
    var status = await WaitForStateAsync(
        fixture.Queue,
        commandId,
        "failed",
        TimeSpan.FromSeconds(5));
    Assert(status.Error?.Code == "COMMAND_DEAD_LETTERED" && handler.Requests.Count == 0,
        "A repeated pre-dispatch failure was not dead-lettered without an upstream write.");
    var observability = await fixture.Queue.GetObservabilityAsync(
        DateTimeOffset.UtcNow,
        CancellationToken.None);
    Assert(observability.States["deadLettered"] == 1 &&
           observability.States["leased"] == 0 && observability.Pending == 0,
        "Outbox observability omitted dead-letter or lease state.");
}

static async Task VerifyLegacyJsonlMigrationAsync(string root)
{
    var commandDirectory = Path.Combine(root, "commands");
    Directory.CreateDirectory(commandDirectory);
    var path = Path.Combine(commandDirectory, "paldefender-command-audit.jsonl");
    var commandId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow.AddMinutes(-1);
    const string requestHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    var events = new[]
    {
        LegacyEvent(commandId, "accepted", "accepted", now, requestHash, CapacityBody(704)),
        LegacyEvent(commandId, "dispatched", "dispatched", now.AddSeconds(1), requestHash, null),
        LegacyEvent(commandId, "succeeded", "succeeded", now.AddSeconds(2), requestHash, null, 200)
    };
    File.WriteAllText(
        path,
        string.Join(
            Environment.NewLine,
            events.Select(item => JsonSerializer.Serialize(item))) +
        Environment.NewLine,
        Encoding.UTF8);

    var handler = new RecordingGrantHandler();
    await using (var migrated = QueueFixture.Create(root, 8, handler))
    {
        var status = await migrated.Queue.GetStatusAsync(commandId, CancellationToken.None);
        Assert(status?.State == "succeeded",
            "Legacy JSONL projection was not imported into SQLite.");
        var audit = await migrated.Queue.GetAuditAsync(100, CancellationToken.None);
        Assert(audit.Count(item => item.CommandId == commandId) == 3,
            "Legacy immutable events were not imported exactly once.");
    }
    Assert(!File.Exists(path) && File.Exists(path + ".migrated-to-sqlite-v1"),
        "The migrated JSONL was not retained under a non-authoritative archive name.");
    Assert(QueryScalar(root, "SELECT COUNT(*) FROM paldefender_commands;") == 1 &&
           QueryScalar(root, "SELECT COUNT(*) FROM paldefender_command_events;") == 3,
        "SQLite migration lost or duplicated legacy commands/events.");

    await using (var replay = QueueFixture.Create(root, 8, handler))
    {
        var status = await replay.Queue.GetStatusAsync(commandId, CancellationToken.None);
        Assert(status?.State == "succeeded",
            "The completed migration marker did not survive restart.");
    }

    File.WriteAllText(path, "{}" + Environment.NewLine, Encoding.UTF8);
    AssertThrows<InvalidDataException>(
        () => QueueFixture.Create(root, 8, handler),
        "A post-migration legacy JSONL mutation did not fail closed.");
}

static void VerifyLegacyMigrationFailsClosed(string root)
{
    var commandDirectory = Path.Combine(root, "commands");
    Directory.CreateDirectory(commandDirectory);
    var firstId = Guid.NewGuid();
    var secondId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var events = new[]
    {
        LegacyEvent(firstId, "accepted", "accepted", now,
            new string('b', 64), CapacityBody(705)),
        LegacyEvent(secondId, "accepted", "accepted", now.AddSeconds(1),
            new string('c', 64), CapacityBody(706))
    };
    File.WriteAllText(
        Path.Combine(commandDirectory, "paldefender-command-audit.jsonl"),
        string.Join(
            Environment.NewLine,
            events.Select(item => JsonSerializer.Serialize(item))) +
        Environment.NewLine,
        Encoding.UTF8);
    var handler = new RecordingGrantHandler();
    AssertThrows<InvalidDataException>(
        () => QueueFixture.Create(root, 8, handler),
        "Conflicting legacy server/idempotency mappings did not fail closed.");
    Assert(QueryScalar(root, "SELECT COUNT(*) FROM paldefender_commands;") == 0,
        "A failed legacy migration left partially imported commands.");
}

static LegacyQueueEvent LegacyEvent(
    Guid commandId,
    string eventType,
    string state,
    DateTimeOffset at,
    string requestHash,
    JsonNode? body,
    int? httpStatus = null) => new(
    Guid.NewGuid(),
    commandId,
    eventType,
    state,
    at,
    "local",
    "give/items/legacy-player",
    "legacy-idempotency-key-0001",
    requestHash,
    "legacy migration harness",
    "legacy-harness",
    body,
    httpStatus,
    httpStatus is null ? null : new JsonObject { ["ok"] = true },
    null,
    null,
    null);

static async Task<CommandStatus> WaitForStateAsync(
    PalDefenderCommandQueue queue,
    Guid commandId,
    string expectedState,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow.Add(timeout);
    while (DateTimeOffset.UtcNow < deadline)
    {
        var status = await queue.GetStatusAsync(commandId, CancellationToken.None);
        if (status?.State == expectedState)
        {
            return status;
        }
        await Task.Delay(20);
    }
    throw new TimeoutException(
        $"Command '{commandId}' did not reach state '{expectedState}'.");
}

static void ExecuteSql(string root, string sql, params (string Name, object Value)[] values)
{
    using var connection = OpenOutboxDatabase(root);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    foreach (var value in values)
    {
        command.Parameters.AddWithValue(value.Name, value.Value);
    }
    command.ExecuteNonQuery();
}

static long QueryScalar(string root, string sql)
{
    using var connection = OpenOutboxDatabase(root);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}

static SqliteConnection OpenOutboxDatabase(string root)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(root, "extraction", "extraction-commerce.db"),
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());
    connection.Open();
    return connection;
}

static void AssertThrows<TException>(Func<object> action, string message)
    where TException : Exception
{
    try
    {
        _ = action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static ShopDeliveryWorkItem CreateDelivery(
    string playerIdentifier,
    IReadOnlyList<ShopItemGrant> items)
{
    var deliveryId = Guid.NewGuid();
    return new ShopDeliveryWorkItem(
        deliveryId,
        Guid.NewGuid(),
        "local",
        playerIdentifier,
        $"give/items/{Uri.EscapeDataString(playerIdentifier)}",
        $"delivery-receipt-{deliveryId:N}",
        items,
        Attempt: 0,
        PlayerUid: Guid.NewGuid().ToString("N"),
        WorldId: Guid.NewGuid().ToString("N").ToUpperInvariant());
}

static ExtractionDeliveryReceiptV1 CreateSuccessfulReceipt(
    ExtractionDeliveryReceiptRequest request,
    DateTimeOffset acknowledgedAt) => new(
    ExtractionDeliveryReceiptContract.SchemaVersion,
    request.DeliveryId,
    request.IdempotencyKey,
    request.RequestHash,
    request.ResultId,
    request.ServerId,
    request.PlayerUid,
    request.WorldId,
    request.GameVersion,
    request.AdapterVersion,
    request.CommandVersion,
    acknowledgedAt,
    request.Items.Select(item => new ExtractionDeliveryReceiptItem(
        item.ItemId,
        item.Quantity,
        item.Quantity,
        Guid.NewGuid(),
        ExtractionDeliveryReceiptItemResult.Succeeded,
        acknowledgedAt)).ToArray(),
    ExtractionDeliveryReceiptOutcome.Succeeded,
    acknowledgedAt);

static JsonObject CapacityBody(int index) => new()
{
    ["Items"] = new JsonArray
    {
        new JsonObject
        {
            ["ItemID"] = $"Capacity_Item_{index:D4}",
            ["Count"] = 1
        }
    }
};

static bool ReceiptsEqual(
    ExtractionDeliveryReceiptV1 left,
    ExtractionDeliveryReceiptV1 right) =>
    left.SchemaVersion == right.SchemaVersion &&
    left.DeliveryId == right.DeliveryId &&
    left.IdempotencyKey == right.IdempotencyKey &&
    left.RequestHash == right.RequestHash &&
    left.ResultId == right.ResultId &&
    left.ServerId == right.ServerId &&
    left.PlayerUid == right.PlayerUid &&
    left.WorldId == right.WorldId &&
    left.GameVersion == right.GameVersion &&
    left.AdapterVersion == right.AdapterVersion &&
    left.CommandVersion == right.CommandVersion &&
    left.AcknowledgedAt == right.AcknowledgedAt &&
    left.Outcome == right.Outcome &&
    left.CreatedAt == right.CreatedAt &&
    left.Items.SequenceEqual(right.Items);

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

sealed record CompletedDispatch(
    ShopDeliveryWorkItem Delivery,
    IReadOnlyList<Guid> CommandIds,
    ExtractionDeliveryReceiptV1 Receipt);

sealed record LegacyQueueEvent(
    Guid EventId,
    Guid CommandId,
    string EventType,
    string State,
    DateTimeOffset At,
    string ServerId,
    string UpstreamPath,
    string IdempotencyKey,
    string RequestHash,
    string Reason,
    string Actor,
    JsonNode? Body,
    int? HttpStatus,
    JsonNode? ResponseJson,
    string? ResponseText,
    string? ErrorCode,
    string? ErrorMessage);

sealed record RecordedGrantRequest(
    string RequestUri,
    JsonObject Body,
    string ItemId,
    int Requested);

sealed class RecordingGrantHandler : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<RecordedGrantRequest> _requests = [];

    public IReadOnlyList<RecordedGrantRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests
                    .Select(request => request with { Body = (JsonObject)request.Body.DeepClone() })
                    .ToArray();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var raw = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var body = JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidDataException("The adapter sent an invalid JSON body.");
        var items = body["Items"] as JsonArray;
        if (items?.Count != 1 || items[0] is not JsonObject item)
        {
            throw new InvalidDataException("The adapter sent a non-attributable multi-item command.");
        }
        var itemId = item["ItemID"]?.GetValue<string>()
            ?? throw new InvalidDataException("The adapter command omitted ItemID.");
        var requested = item["Count"]?.GetValue<int>()
            ?? throw new InvalidDataException("The adapter command omitted Count.");
        lock (_sync)
        {
            _requests.Add(new RecordedGrantRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                (JsonObject)body.DeepClone(),
                itemId,
                requested));
        }

        JsonNode granted = itemId.StartsWith("Malformed_", StringComparison.Ordinal)
            ? JsonValue.Create("unknown")!
            : JsonValue.Create(itemId.StartsWith("Partial_", StringComparison.Ordinal)
                ? requested - 1
                : requested)!;
        var responseBody = new JsonObject
        {
            ["Granted"] = new JsonObject { ["Items"] = granted }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                responseBody.ToJsonString(),
                Encoding.UTF8,
                "application/json")
        };
    }
}

sealed class QueueFixture : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TestWebHostEnvironment _environment;
    private bool _started;

    private QueueFixture(
        PalDefenderCommandQueue queue,
        HttpClient httpClient,
        TestWebHostEnvironment environment)
    {
        Queue = queue;
        _httpClient = httpClient;
        _environment = environment;
    }

    public PalDefenderCommandQueue Queue { get; }

    public static QueueFixture Create(
        string root,
        int capacity,
        RecordingGrantHandler handler,
        IEconomySafetyDependencyProbe? dependencyProbe = null,
        IPalDefenderCommandFaultInjector? faultInjector = null)
    {
        Directory.CreateDirectory(root);
        var environment = new TestWebHostEnvironment(root);
        var extractionOptions = Options.Create(new ExtractionPersistenceOptions
        {
            DataDirectory = Path.Combine(root, "extraction")
        });
        var operationGate = new ExtractionOperationGate(extractionOptions, environment);
        var economyGate = new EconomySafetyGate(
            dependencyProbe ?? new EmptyDependencyProbe(),
            operationGate,
            Options.Create(new ExtractionModeOptions
            {
                Enabled = true,
                InitialMarketCoin = 1_000,
                InitialSeasonVoucher = 300
            }),
            Options.Create(new EconomySafetyOptions
            {
                PalDefenderGrantReceiptSemanticsVerified = true
            }),
            extractionOptions,
            environment);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:17993/v1/pdapi/")
        };
        var restClient = new PalDefenderRestClient(
            httpClient,
            Options.Create(new PalDefenderRestOptions
            {
                Enabled = true,
                Token = "delivery-receipt-harness-token"
            }),
            NullLogger<PalDefenderRestClient>.Instance);
        var queue = new PalDefenderCommandQueue(
            Options.Create(new CommandPersistenceOptions
            {
                DataDirectory = Path.Combine(root, "commands"),
                PalDefenderQueueCapacity = capacity
            }),
            extractionOptions,
            environment,
            restClient,
            economyGate,
            NullLogger<PalDefenderCommandQueue>.Instance,
            faultInjector);
        return new QueueFixture(queue, httpClient, environment);
    }

    public async Task StartAsync()
    {
        await Queue.StartAsync(CancellationToken.None);
        _started = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Queue.StopAsync(timeout.Token);
        }
        Queue.Dispose();
        _httpClient.Dispose();
        _environment.Dispose();
    }
}

sealed class EmptyDependencyProbe : IEconomySafetyDependencyProbe
{
    public Task<IReadOnlyList<ApiError>> ProbeAsync(
        EconomyWriteFeature feature,
        EconomySafetyContext? context,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApiError>>([]);
}

sealed class ThrowingDependencyProbe : IEconomySafetyDependencyProbe
{
    public Task<IReadOnlyList<ApiError>> ProbeAsync(
        EconomyWriteFeature feature,
        EconomySafetyContext? context,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Injected pre-dispatch dependency failure.");
}

sealed class ThrowingQueueFaultInjector : IPalDefenderCommandFaultInjector
{
    private readonly PalDefenderCommandFaultPoint _point;

    public ThrowingQueueFaultInjector(PalDefenderCommandFaultPoint point)
    {
        _point = point;
    }

    public void Inject(PalDefenderCommandFaultPoint point, Guid commandId)
    {
        if (point == _point)
        {
            throw new InjectedQueueFaultException(point, commandId);
        }
    }
}

sealed class InjectedQueueFaultException : Exception
{
    public InjectedQueueFaultException(PalDefenderCommandFaultPoint point, Guid commandId)
        : base($"Injected queue fault {point} for {commandId}.")
    {
    }
}

sealed class TestWebHostEnvironment : IWebHostEnvironment, IDisposable
{
    private readonly PhysicalFileProvider _contentRootProvider;

    public TestWebHostEnvironment(string root)
    {
        ApplicationName = "PalControl.DeliveryReceiptsHarness";
        EnvironmentName = "Testing";
        WebRootPath = root;
        WebRootFileProvider = new NullFileProvider();
        ContentRootPath = root;
        _contentRootProvider = new PhysicalFileProvider(root);
        ContentRootFileProvider = _contentRootProvider;
    }

    public string ApplicationName { get; set; }
    public IFileProvider WebRootFileProvider { get; set; }
    public string WebRootPath { get; set; }
    public string EnvironmentName { get; set; }
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }

    public void Dispose() => _contentRootProvider.Dispose();
}

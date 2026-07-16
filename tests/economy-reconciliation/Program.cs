using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;
using PalControl.EconomyReconciliation;

var root = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-economy-reconciliation-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var sourceDirectory = Path.Combine(root, "source");
    Directory.CreateDirectory(sourceDirectory);
    var sourceDatabase = Path.Combine(sourceDirectory, "extraction-commerce.db");
    var fixture = await CreateEconomyFixtureAsync(sourceDirectory);
    CreateDeliverySideEffectFixture(sourceDatabase, fixture);

    var baseline = EconomyReconciliationAuditor.Audit(sourceDatabase);
    Assert(baseline.Success && baseline.DataValid,
        $"The valid fixture failed reconciliation: {string.Join(',', baseline.Issues.Select(issue => issue.Code))}");
    Assert(baseline.Counts.Accounts == 1 &&
           baseline.Counts.WalletScopes == 2 &&
           baseline.Counts.Orders == 1 &&
           baseline.Counts.Deliveries == 1 &&
           baseline.Counts.SettlementRuns == 1 &&
           baseline.Counts.RunCredits == 1,
        "The valid fixture was not projected across all required economy entities.");
    Assert(baseline.Accounts.Count == 1 &&
           baseline.Accounts[0].LedgerEntryCount == 3 &&
           baseline.Accounts[0].CanonicalHash.Length == 64,
        "Per-account recomputation evidence is incomplete.");
    Assert(baseline.Rows.Count > baseline.Counts.PhysicalRows &&
           baseline.Rows.All(row => row.KeyFingerprint.Length == 64 && row.CanonicalHash.Length == 64),
        "Row-level canonical hashes are missing or expose raw row keys.");

    ReformatFirstEventJson(sourceDatabase);
    var reformatted = EconomyReconciliationAuditor.Audit(sourceDatabase, baseline);
    Assert(reformatted.Success && reformatted.BaselineComparison is { Match: true },
        "Canonical hashes changed after JSON whitespace/property-order-only formatting.");

    var balanceTamper = Backup(sourceDatabase, Path.Combine(root, "balance-tamper.db"));
    Execute(balanceTamper, """
        UPDATE extraction_events
        SET payload = json_set(
            payload,
            '$.balances[0].balance',
            json_extract(payload, '$.balances[0].balance') + 1)
        WHERE sequence = (
            SELECT MAX(sequence) FROM extraction_events
            WHERE json_type(payload, '$.balances') = 'array');
        """);
    AssertInvalid(
        EconomyReconciliationAuditor.Audit(balanceTamper, baseline),
        "WALLET_LEDGER_PROJECTION_MISMATCH");

    var idempotencyTamper = Backup(sourceDatabase, Path.Combine(root, "idempotency-tamper.db"));
    Execute(idempotencyTamper, """
        UPDATE extraction_events
        SET payload = json_set(payload, '$.idempotency.requestHash', 'bad')
        WHERE sequence = (
            SELECT MAX(sequence) FROM extraction_events
            WHERE json_type(payload, '$.idempotency') = 'object');
        """);
    AssertInvalid(
        EconomyReconciliationAuditor.Audit(idempotencyTamper, baseline),
        "IDEMPOTENCY_RECORD_INVALID");

    var creditTamper = Backup(sourceDatabase, Path.Combine(root, "credit-tamper.db"));
    Execute(creditTamper, "UPDATE extraction_run_credits SET amount = amount + 1;");
    AssertInvalid(
        EconomyReconciliationAuditor.Audit(creditTamper, baseline),
        "RUN_CREDIT_LEDGER_MISMATCH");

    var deliveryTamper = Backup(sourceDatabase, Path.Combine(root, "delivery-tamper.db"));
    Execute(deliveryTamper, $"""
        UPDATE extraction_events
        SET payload = json_set(payload, '$.delivery.orderId', '{Guid.NewGuid():D}')
        WHERE sequence = (
            SELECT MAX(sequence) FROM extraction_events
            WHERE json_type(payload, '$.delivery') = 'object');
        """);
    AssertInvalid(
        EconomyReconciliationAuditor.Audit(deliveryTamper, baseline),
        "ORDER_CURRENT_DELIVERY_MISMATCH");

    var receiptTamper = Backup(sourceDatabase, Path.Combine(root, "receipt-tamper.db"));
    Execute(receiptTamper, "UPDATE extraction_delivery_receipts SET idempotency_key = 'wrong-key';");
    AssertInvalid(
        EconomyReconciliationAuditor.Audit(receiptTamper, baseline),
        "DELIVERY_RECEIPT_LINK_MISMATCH");

    var commandTamper = Backup(sourceDatabase, Path.Combine(root, "command-tamper.db"));
    Execute(commandTamper, "UPDATE paldefender_commands SET idempotency_key = 'wrong-command-key';");
    AssertInvalid(
        EconomyReconciliationAuditor.Audit(commandTamper, baseline),
        "DELIVERY_COMMAND_LINK_MISMATCH");

    var rowTamper = Backup(sourceDatabase, Path.Combine(root, "physical-row-tamper.db"));
    Execute(rowTamper, "UPDATE extraction_events SET occurred_at = '2000-01-01T00:00:00Z' WHERE sequence = 1;");
    var rowMismatch = EconomyReconciliationAuditor.Audit(rowTamper, baseline);
    Assert(!rowMismatch.Success &&
           rowMismatch.BaselineComparison is { PhysicalMatch: false, ChangedRowHashCount: > 0 } &&
           rowMismatch.Issues.Any(issue => issue.Code == "EVENT_ENVELOPE_COLUMN_MISMATCH"),
        "Physical row hash or immutable event-column drift was not detected.");

    var baselinePath = Path.Combine(root, "baseline.json");
    AtomicFile.WriteAllText(
        baselinePath,
        JsonSerializer.Serialize(baseline, EconomyReconciliationAuditor.JsonOptions));
    var roundTrip = JsonSerializer.Deserialize<EconomyReconciliationReport>(
        await File.ReadAllTextAsync(baselinePath),
        EconomyReconciliationAuditor.JsonOptions);
    Assert(roundTrip is not null &&
           EconomyReconciliationAuditor.Audit(sourceDatabase, roundTrip).Success,
        "A serialized baseline could not be used for a strict post-migration comparison.");

    Console.WriteLine(
        "PASS: read-only SQLite account/ledger recomputation, order/run/delivery/idempotency reconciliation, privacy-safe canonical row hashes, strict baseline compare and tamper faults.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static async Task<FixtureSideEffect> CreateEconomyFixtureAsync(string directory)
{
    await using var repository = new SqliteExtractionRepository(directory);
    var now = DateTimeOffset.UtcNow;
    var season = await repository.UpsertSeasonAsync(
        null,
        new ExtractionSeasonDefinition(
            "reconciliation-server",
            "week-reconciliation",
            "Reconciliation Week",
            "0123456789abcdef0123456789abcdef",
            now.AddDays(-1),
            now.AddDays(6),
            ExtractionSeasonState.Active),
        null,
        CancellationToken.None);
    var account = await repository.GetOrCreateAccountAsync(
        "steam",
        "76561198000000000",
        "Reconciliation Player",
        CancellationToken.None);
    var funded = await repository.AdjustWalletAsync(
        new WalletAdjustmentRequest(
            account.AccountId,
            null,
            ExtractionCurrency.MarketCoin,
            1_000,
            "Reconciliation fixture funding",
            "fixture",
            "funding",
            "test",
            "reconciliation-funding-0001"),
        CancellationToken.None);
    Assert(funded.Created, "Fixture funding was not created.");
    _ = await repository.UpsertProductAsync(
        new ShopProductDefinition(
            "RECONCILIATION-KIT",
            "Reconciliation Kit",
            "Fixture product",
            ExtractionCurrency.MarketCoin,
            125,
            [new ShopItemGrant("Leather", 2)],
            5,
            true,
            null,
            null),
        null,
        "test",
        CancellationToken.None);
    var purchase = await repository.PurchaseAsync(
        new ShopPurchaseRequest(
            account.AccountId,
            season.SeasonId,
            season.ServerId,
            "fixture-player",
            [new ShopPurchaseLineInput("RECONCILIATION-KIT", 2)],
            "reconciliation-purchase-0001",
            "test",
            "Reconciliation fixture order"),
        CancellationToken.None);
    Assert(purchase.Created && purchase.Order is not null && purchase.Delivery is not null,
        "Fixture purchase was not created.");
    var deliveryWorkItem = purchase.Delivery
        ?? throw new InvalidOperationException("Fixture purchase did not return a delivery work item.");
    var commandId = Guid.NewGuid();
    var dispatched = await repository.MarkDeliveryDispatchedAsync(
        deliveryWorkItem.DeliveryId,
        commandId,
        CancellationToken.None);
    Assert(dispatched.Updated, "Fixture delivery was not marked dispatched.");
    var delivered = await repository.MarkDeliveryOutcomeAsync(
        deliveryWorkItem.DeliveryId,
        ShopDeliveryState.Delivered,
        null,
        null,
        CancellationToken.None);
    Assert(delivered.Updated, "Fixture delivery was not marked delivered.");

    var run = new ExtractionSettlementRun(
        Guid.NewGuid(),
        account.AccountId,
        season.SeasonId,
        "fixture-player",
        "zone-reconciliation",
        "Reconciliation Zone",
        ExtractionSettlementState.Removed,
        [new ExtractionLootLine("Stone", "Stone", 5, 10, 50)],
        5,
        50,
        new string('a', 64),
        new Dictionary<string, long> { ["Stone"] = 5 },
        "reconciliation-settlement-0001",
        null,
        null,
        now,
        now.AddMinutes(1),
        now,
        null)
    {
        Revision = 2,
        StateChangedAt = now,
        LeaseId = Guid.NewGuid(),
        LeaseOwner = "test",
        LeaseExpiresAt = now.AddMinutes(2),
        AttemptCount = 1,
        LastHeartbeatAt = now
    };
    await repository.PersistSettlementRunWritesAsync(
        [ExtractionSettlementRunWrite.Insert(run)],
        CancellationToken.None);
    var credited = await repository.CreditRemovedRunAsync(run, CancellationToken.None);
    Assert(credited.CreditCreated && credited.Run.State == ExtractionSettlementState.Credited,
        "Fixture run credit was not committed atomically.");
    return new FixtureSideEffect(
        deliveryWorkItem.DeliveryId,
        deliveryWorkItem.IdempotencyKey,
        commandId,
        season.ServerId);
}

static void CreateDeliverySideEffectFixture(string database, FixtureSideEffect fixture)
{
    var now = DateTimeOffset.UtcNow;
    var requestHash = new string('b', 64);
    var commandKey = PalDefenderItemGrantAdapter.LineIdempotencyKey(fixture.DeliveryId, 0);
    var receipt = new ExtractionDeliveryReceiptV1(
        ExtractionDeliveryReceiptContract.SchemaVersion,
        fixture.DeliveryId,
        fixture.IdempotencyKey,
        requestHash,
        Guid.NewGuid(),
        fixture.ServerId,
        "0123456789abcdef0123456789abcdef",
        "fedcba9876543210fedcba9876543210",
        "fixture-game",
        "fixture-adapter",
        ExtractionDeliveryReceiptContract.CommandVersion,
        now,
        [new ExtractionDeliveryReceiptItem(
            "Leather",
            4,
            4,
            fixture.CommandId,
            ExtractionDeliveryReceiptItemResult.Succeeded,
            now)],
        ExtractionDeliveryReceiptOutcome.Succeeded,
        now);
    var receiptJson = JsonSerializer.Serialize(
        receipt,
        EconomyReconciliationAuditor.JsonOptions);
    using var connection = Open(database, readOnly: false);
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE paldefender_commands (
            command_id TEXT PRIMARY KEY,
            server_id TEXT NOT NULL,
            idempotency_key TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL);
        CREATE TABLE extraction_delivery_receipts (
            delivery_id TEXT PRIMARY KEY,
            idempotency_key TEXT NOT NULL UNIQUE,
            request_hash TEXT NOT NULL,
            server_id TEXT NOT NULL,
            receipt_json TEXT NULL);
        CREATE TABLE extraction_delivery_evidence (
            delivery_id TEXT PRIMARY KEY,
            command_id TEXT NULL);
        INSERT INTO paldefender_commands (command_id, server_id, idempotency_key, state)
        VALUES ($commandId, $serverId, $commandKey, 'succeeded');
        INSERT INTO extraction_delivery_receipts (
            delivery_id, idempotency_key, request_hash, server_id, receipt_json)
        VALUES ($deliveryId, $idempotencyKey, $requestHash, $serverId, $receiptJson);
        INSERT INTO extraction_delivery_evidence (delivery_id, command_id)
        VALUES ($deliveryId, $commandId);
        """;
    command.Parameters.AddWithValue("$commandId", fixture.CommandId.ToString("D"));
    command.Parameters.AddWithValue("$serverId", fixture.ServerId);
    command.Parameters.AddWithValue("$idempotencyKey", fixture.IdempotencyKey);
    command.Parameters.AddWithValue("$commandKey", commandKey);
    command.Parameters.AddWithValue("$requestHash", requestHash);
    command.Parameters.AddWithValue("$receiptJson", receiptJson);
    command.Parameters.AddWithValue("$deliveryId", fixture.DeliveryId.ToString("D"));
    command.ExecuteNonQuery();
}

static void ReformatFirstEventJson(string database)
{
    using var connection = Open(database, readOnly: false);
    using var select = connection.CreateCommand();
    select.CommandText = "SELECT sequence, payload FROM extraction_events ORDER BY sequence LIMIT 1;";
    using var reader = select.ExecuteReader();
    Assert(reader.Read(), "Fixture has no extraction event to reformat.");
    var sequence = reader.GetInt64(0);
    using var document = JsonDocument.Parse(reader.GetString(1));
    var reversed = document.RootElement.EnumerateObject().Reverse()
        .ToDictionary(property => property.Name, property => property.Value.Clone());
    var reformatted = JsonSerializer.Serialize(reversed, new JsonSerializerOptions { WriteIndented = true });
    reader.Close();
    using var update = connection.CreateCommand();
    update.CommandText = "UPDATE extraction_events SET payload = $payload WHERE sequence = $sequence;";
    update.Parameters.AddWithValue("$payload", reformatted);
    update.Parameters.AddWithValue("$sequence", sequence);
    Assert(update.ExecuteNonQuery() == 1, "Fixture JSON reformat did not update one event.");
}

static string Backup(string source, string destination)
{
    using var input = Open(source, readOnly: true);
    using var output = Open(destination, readOnly: false);
    input.BackupDatabase(output);
    return destination;
}

static void Execute(string database, string sql)
{
    using var connection = Open(database, readOnly: false);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static SqliteConnection Open(string path, bool readOnly)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString());
    connection.Open();
    return connection;
}

static void AssertInvalid(EconomyReconciliationReport report, string code)
{
    Assert(!report.Success && !report.DataValid,
        $"Tampered database unexpectedly passed ({code}).");
    Assert(report.Issues.Any(issue => issue.Code == code),
        $"Tampered database did not emit stable issue code {code}: {string.Join(',', report.Issues.Select(issue => issue.Code))}");
    Assert(report.BaselineComparison is { Match: false, ChangedRowHashCount: > 0 },
        $"Tampered database did not differ from its canonical baseline ({code}).");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record FixtureSideEffect(
    Guid DeliveryId,
    string IdempotencyKey,
    Guid CommandId,
    string ServerId);

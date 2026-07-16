using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.EconomyReconciliation;

public static class EconomyReconciliationAuditor
{
    private const long MaximumWebSafeInteger = 9_007_199_254_740_991;
    private static readonly string[] RequiredCoreTables =
    [
        "extraction_events",
        "extraction_settlement_runs",
        "extraction_run_credits"
    ];

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static EconomyReconciliationReport Audit(
        string databasePath,
        EconomyReconciliationReport? baseline = null)
    {
        var database = SafePath.ExistingFile(databasePath);
        SQLitePCL.Batteries_V2.Init();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA query_only=ON; PRAGMA busy_timeout=5000; BEGIN DEFERRED;");
        try
        {
            var issues = new List<AuditIssue>();
            var integrityOk = ReadIntegrity(connection);
            var foreignKeysOk = ReadForeignKeys(connection);
            var userVersion = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (!integrityOk)
            {
                AddIssue(issues, "SQLITE_INTEGRITY_FAILED", "database", null,
                    "SQLite integrity_check did not return exactly one ok row.");
            }
            if (!foreignKeysOk)
            {
                AddIssue(issues, "SQLITE_FOREIGN_KEY_FAILED", "database", null,
                    "SQLite foreign_key_check returned at least one violation.");
            }
            if (userVersion != 1)
            {
                AddIssue(issues, "SQLITE_USER_VERSION_UNSUPPORTED", "database", null,
                    "The current reconciliation contract supports SQLite user_version 1 only.");
            }

            var physical = AuditPhysicalTables(connection, issues);
            foreach (var required in RequiredCoreTables.Where(required =>
                         physical.All(table => !string.Equals(
                             table.Table,
                             required,
                             StringComparison.Ordinal))))
            {
                AddIssue(issues, "REQUIRED_TABLE_MISSING", "database", required,
                    "A required authoritative economy table is missing.");
            }

            var state = LoadLogicalState(connection, issues);
            ValidateLogicalState(state, physical, issues);
            var logicalRows = BuildLogicalRows(state)
                .Concat(physical.Where(table => IsDomainSideEffectTable(table.Table))
                    .SelectMany(table => table.Rows))
                .OrderBy(row => row.Category, StringComparer.Ordinal)
                .ThenBy(row => row.KeyFingerprint, StringComparer.Ordinal)
                .ToArray();
            var physicalRows = physical.SelectMany(table => table.Rows).ToArray();
            var allRows = logicalRows.Concat(physicalRows)
                .OrderBy(row => row.Category, StringComparer.Ordinal)
                .ThenBy(row => row.KeyFingerprint, StringComparer.Ordinal)
                .ThenBy(row => row.CanonicalHash, StringComparer.Ordinal)
                .ToArray();
            var domainHash = CanonicalHash.Aggregate("domain", logicalRows);
            var tableReports = physical.Select(table => new AuditTableHash(
                    table.Table,
                    table.Rows.Count,
                    table.CanonicalHash))
                .OrderBy(table => table.Table, StringComparer.Ordinal)
                .ToArray();
            var physicalHash = CanonicalHash.Domain(
                "physical-database",
                string.Join('\n', tableReports.Select(table =>
                    $"{table.Table}\n{table.RowCount}\n{table.CanonicalHash}")));
            var accountReports = BuildAccountSummaries(state);
            var counts = new AuditCounts(
                state.Accounts.Count,
                state.Balances.Count,
                state.Ledger.Count,
                state.Orders.Count,
                state.Deliveries.Count,
                state.Idempotency.Count,
                state.Runs.Count,
                state.RunCredits.Count,
                physical.Count,
                physical.Sum(table => (long)table.Rows.Count));

            var dataValid = issues.Count == 0;
            var comparison = baseline is null
                ? null
                : CompareBaseline(baseline, domainHash, physicalHash, tableReports, allRows);
            if (comparison is { Match: false })
            {
                AddIssue(issues, "BASELINE_RECONCILIATION_MISMATCH", "baseline", null,
                    "The post-migration canonical hashes do not match the approved baseline.");
            }
            return new EconomyReconciliationReport(
                1,
                CanonicalHash.Version,
                DateTimeOffset.UtcNow,
                CanonicalHash.Key("database-path", database.ToUpperInvariant()),
                userVersion,
                integrityOk,
                foreignKeysOk,
                dataValid,
                dataValid && (comparison?.Match ?? true),
                domainHash,
                physicalHash,
                counts,
                tableReports,
                accountReports,
                allRows,
                issues.OrderBy(issue => issue.Code, StringComparer.Ordinal)
                    .ThenBy(issue => issue.Category, StringComparer.Ordinal)
                    .ThenBy(issue => issue.KeyFingerprint, StringComparer.Ordinal)
                    .ToArray(),
                comparison);
        }
        finally
        {
            Execute(connection, "ROLLBACK;");
        }
    }

    private static List<PhysicalTable> AuditPhysicalTables(
        SqliteConnection connection,
        List<AuditIssue> issues)
    {
        var names = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }
        var tables = new List<PhysicalTable>();
        foreach (var name in names)
        {
            try
            {
                tables.Add(AuditPhysicalTable(connection, name));
            }
            catch (Exception exception) when (exception is SqliteException or InvalidDataException)
            {
                AddIssue(issues, "TABLE_CANONICALIZATION_FAILED", $"physical:{name}", name,
                    "A physical table could not be canonicalized safely.");
            }
        }
        return tables;
    }

    private static PhysicalTable AuditPhysicalTable(SqliteConnection connection, string table)
    {
        var columns = ReadColumns(connection, table);
        if (columns.Count == 0)
        {
            throw new InvalidDataException("A physical table has no columns.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(column => Quote(column.Name)))} FROM {Quote(table)};";
        using var reader = command.ExecuteReader();
        var pending = new List<(string Key, string Row)>();
        while (reader.Read())
        {
            var row = CanonicalSqlRow(reader, columns);
            var primaryKeyColumns = columns.Where(column => column.PrimaryKeyOrder > 0)
                .OrderBy(column => column.PrimaryKeyOrder)
                .ToArray();
            var key = primaryKeyColumns.Length == 0
                ? row
                : CanonicalSqlRow(reader, primaryKeyColumns);
            pending.Add((key, row));
        }
        var rows = new List<AuditRowHash>(pending.Count);
        var duplicateOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in pending.OrderBy(item => item.Key, StringComparer.Ordinal)
                     .ThenBy(item => item.Row, StringComparer.Ordinal))
        {
            var ordinal = duplicateOrdinals.GetValueOrDefault(item.Key);
            duplicateOrdinals[item.Key] = ordinal + 1;
            var keyed = $"{item.Key}\noccurrence:{ordinal}";
            rows.Add(new AuditRowHash(
                $"physical:{table}",
                CanonicalHash.Key($"physical:{table}", keyed),
                CanonicalHash.Domain($"physical:{table}", item.Row)));
        }
        return new PhysicalTable(
            table,
            rows,
            CanonicalHash.Aggregate($"physical:{table}", rows));
    }

    private static List<ColumnInfo> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<ColumnInfo>();
        while (reader.Read())
        {
            columns.Add(new ColumnInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(5)));
        }
        return columns;
    }

    private static string CanonicalSqlRow(
        SqliteDataReader reader,
        IReadOnlyCollection<ColumnInfo> columns)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var column in columns.OrderBy(column => column.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(column.Name);
            WriteSqlValue(writer, reader.GetValue(column.Ordinal));
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSqlValue(Utf8JsonWriter writer, object value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case DBNull:
                writer.WriteString("type", "null");
                writer.WriteNull("value");
                break;
            case long integer:
                writer.WriteString("type", "integer");
                writer.WriteNumber("value", integer);
                break;
            case int integer:
                writer.WriteString("type", "integer");
                writer.WriteNumber("value", integer);
                break;
            case double real:
                writer.WriteString("type", "real");
                writer.WriteString("value", real.ToString("R", CultureInfo.InvariantCulture));
                break;
            case byte[] bytes:
                writer.WriteString("type", "blob");
                writer.WriteBase64String("value", bytes);
                break;
            case string text when TryCanonicalJson(text, out var canonicalJson):
                writer.WriteString("type", "json");
                writer.WritePropertyName("value");
                writer.WriteRawValue(canonicalJson, skipInputValidation: false);
                break;
            case string text:
                writer.WriteString("type", "text");
                writer.WriteString("value", text);
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported SQLite value type '{value.GetType().Name}'.");
        }
        writer.WriteEndObject();
    }

    private static bool TryCanonicalJson(string value, out string canonical)
    {
        canonical = string.Empty;
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            canonical = CanonicalHash.Json(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static LogicalState LoadLogicalState(
        SqliteConnection connection,
        List<AuditIssue> issues)
    {
        var state = new LogicalState();
        if (!TableExists(connection, "extraction_events"))
        {
            return state;
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT sequence, event_id, event_type, occurred_at, payload
                FROM extraction_events
                ORDER BY sequence;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sequence = reader.GetInt64(0);
                var sqlEventId = reader.GetString(1);
                var sqlEventType = reader.GetString(2);
                var sqlOccurredAt = reader.GetString(3);
                try
                {
                    using var document = JsonDocument.Parse(reader.GetString(4));
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        ReadRequiredInt(root, "schemaVersion") != 1)
                    {
                        throw new InvalidDataException("Unsupported extraction event envelope.");
                    }
                    var eventId = ReadRequiredGuid(root, "eventId");
                    var eventType = ReadRequiredString(root, "eventType");
                    var occurredAt = ReadRequiredTimestamp(root, "at");
                    if (!Guid.TryParse(sqlEventId, out var storedEventId) ||
                        storedEventId != eventId ||
                        !string.Equals(sqlEventType, eventType, StringComparison.Ordinal) ||
                        !DateTimeOffset.TryParse(
                            sqlOccurredAt,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var storedOccurredAt) ||
                        storedOccurredAt != occurredAt)
                    {
                        AddIssue(issues, "EVENT_ENVELOPE_COLUMN_MISMATCH", "event", eventId.ToString("N"),
                            "An extraction event envelope does not match its immutable SQL columns.");
                    }
                    ApplyEventPayload(state, root, sequence, issues);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException)
                {
                    AddIssue(issues, "EVENT_PAYLOAD_INVALID", "event", sqlEventId,
                        "An extraction event payload cannot be parsed under schema version 1.");
                }
            }
        }
        LoadRuns(connection, state, issues);
        LoadRunCredits(connection, state, issues);
        LoadDeliveryLinks(connection, state, issues);
        return state;
    }

    private static void ApplyEventPayload(
        LogicalState state,
        JsonElement root,
        long sequence,
        List<AuditIssue> issues)
    {
        if (TryReadObject<ExtractionSeason>(root, "season", out var season, out var seasonJson))
        {
            if (season.SeasonId == Guid.Empty)
            {
                AddIssue(issues, "SEASON_ID_INVALID", "season", null,
                    "A projected season has an empty id.");
            }
            else
            {
                state.Seasons[season.SeasonId] = new LogicalValue<ExtractionSeason>(season, seasonJson, sequence, 0);
            }
        }
        if (TryReadObject<ExtractionAccount>(root, "account", out var account, out var accountJson))
        {
            if (account.AccountId == Guid.Empty)
            {
                AddIssue(issues, "ACCOUNT_ID_INVALID", "account", null,
                    "A projected account has an empty id.");
            }
            else
            {
                state.Accounts[account.AccountId] = new LogicalValue<ExtractionAccount>(account, accountJson, sequence, 0);
            }
        }
        if (TryReadArray<WalletBalance>(root, "balances", out var balances))
        {
            for (var index = 0; index < balances.Count; index++)
            {
                var item = balances[index];
                state.Balances[WalletKey(item.Value.AccountId, item.Value.Currency, item.Value.SeasonId)] =
                    new LogicalValue<WalletBalance>(item.Value, item.Element, sequence, index);
            }
        }
        if (TryReadArray<WalletLedgerEntry>(root, "ledgerEntries", out var ledgerEntries))
        {
            for (var index = 0; index < ledgerEntries.Count; index++)
            {
                var item = ledgerEntries[index];
                if (state.Ledger.ContainsKey(item.Value.EntryId))
                {
                    AddIssue(issues, "LEDGER_ENTRY_DUPLICATE", "ledger", item.Value.EntryId.ToString("N"),
                        "A wallet ledger entry id occurs in more than one event.");
                    continue;
                }
                state.Ledger[item.Value.EntryId] =
                    new LogicalValue<WalletLedgerEntry>(item.Value, item.Element, sequence, index);
            }
        }
        if (TryReadObject<ShopProduct>(root, "product", out var product, out var productJson))
        {
            state.Products[product.Sku] = new LogicalValue<ShopProduct>(product, productJson, sequence, 0);
        }
        if (TryReadObject<ShopOrder>(root, "order", out var order, out var orderJson))
        {
            state.Orders[order.OrderId] = new LogicalValue<ShopOrder>(order, orderJson, sequence, 0);
        }
        if (TryReadObject<ShopDelivery>(root, "delivery", out var delivery, out var deliveryJson))
        {
            state.Deliveries[delivery.DeliveryId] =
                new LogicalValue<ShopDelivery>(delivery, deliveryJson, sequence, 0);
        }
        if (root.TryGetProperty("idempotency", out var idempotencyJson) &&
            idempotencyJson.ValueKind != JsonValueKind.Null)
        {
            var idempotency = JsonSerializer.Deserialize<StoredIdempotency>(
                idempotencyJson.GetRawText(), JsonOptions)
                ?? throw new InvalidDataException("The idempotency projection is empty.");
            if (state.Idempotency.TryGetValue(idempotency.Scope, out var existing) &&
                (!string.Equals(existing.Value.RequestHash, idempotency.RequestHash, StringComparison.Ordinal) ||
                 existing.Value.ResourceId != idempotency.ResourceId ||
                 !string.Equals(existing.Value.ResourceType, idempotency.ResourceType, StringComparison.Ordinal)))
            {
                AddIssue(issues, "IDEMPOTENCY_SCOPE_CONFLICT", "idempotency", idempotency.Scope,
                    "One idempotency scope maps to conflicting request or resource evidence.");
            }
            else
            {
                state.Idempotency[idempotency.Scope] =
                    new LogicalValue<StoredIdempotency>(idempotency, idempotencyJson.Clone(), sequence, 0);
            }
        }
    }

    private static void LoadRuns(
        SqliteConnection connection,
        LogicalState state,
        List<AuditIssue> issues)
    {
        if (!TableExists(connection, "extraction_settlement_runs"))
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, account_id, season_id, user_id, state, revision, updated_at, payload
            FROM extraction_settlement_runs
            ORDER BY run_id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sqlRunId = reader.GetString(0);
            try
            {
                using var document = JsonDocument.Parse(reader.GetString(7));
                var element = document.RootElement.Clone();
                var run = JsonSerializer.Deserialize<ExtractionSettlementRun>(element.GetRawText(), JsonOptions)
                    ?? throw new InvalidDataException("The settlement run is empty.");
                if (!Guid.TryParse(sqlRunId, out var runId) ||
                    runId != run.RunId ||
                    !Guid.TryParse(reader.GetString(1), out var accountId) || accountId != run.AccountId ||
                    !Guid.TryParse(reader.GetString(2), out var seasonId) || seasonId != run.SeasonId ||
                    !string.Equals(reader.GetString(3), run.UserId, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(4), run.State.ToString(), StringComparison.Ordinal) ||
                    reader.GetInt64(5) != run.Revision ||
                    !DateTimeOffset.TryParse(reader.GetString(6), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var updatedAt) || updatedAt != run.UpdatedAt)
                {
                    AddIssue(issues, "RUN_COLUMN_PAYLOAD_MISMATCH", "run", sqlRunId,
                        "A settlement run payload does not match its CAS/search columns.");
                }
                state.Runs[run.RunId] = new LogicalValue<ExtractionSettlementRun>(run, element, 0, 0);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                AddIssue(issues, "RUN_PAYLOAD_INVALID", "run", sqlRunId,
                    "A settlement run payload cannot be parsed.");
            }
        }
    }

    private static void LoadRunCredits(
        SqliteConnection connection,
        LogicalState state,
        List<AuditIssue> issues)
    {
        if (!TableExists(connection, "extraction_run_credits"))
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, account_id, season_id, ledger_entry_id, amount, created_at
            FROM extraction_run_credits
            ORDER BY run_id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rawRunId = reader.GetString(0);
            if (!Guid.TryParse(rawRunId, out var runId) ||
                !Guid.TryParse(reader.GetString(1), out var accountId) ||
                !Guid.TryParse(reader.GetString(2), out var seasonId) ||
                !Guid.TryParse(reader.GetString(3), out var ledgerEntryId) ||
                !DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var createdAt))
            {
                AddIssue(issues, "RUN_CREDIT_ROW_INVALID", "run-credit", rawRunId,
                    "A run-credit row contains an invalid identifier or timestamp.");
                continue;
            }
            var credit = new RunCredit(
                runId,
                accountId,
                seasonId,
                ledgerEntryId,
                reader.GetInt64(4),
                createdAt);
            state.RunCredits[runId] = credit;
        }
    }

    private static void LoadDeliveryLinks(
        SqliteConnection connection,
        LogicalState state,
        List<AuditIssue> issues)
    {
        if (TableExists(connection, "paldefender_commands"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT command_id, server_id, idempotency_key, state
                FROM paldefender_commands;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var commandId))
                {
                    AddIssue(issues, "DELIVERY_COMMAND_ID_INVALID", "paldefender-command", reader.GetString(0),
                        "A PalDefender command id is invalid.");
                    continue;
                }
                state.Commands[commandId] = new CommandLink(
                    commandId,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3));
            }
        }
        if (TableExists(connection, "extraction_delivery_receipts"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT delivery_id, idempotency_key, request_hash, server_id, receipt_json
                FROM extraction_delivery_receipts;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rawDeliveryId = reader.GetString(0);
                if (Guid.TryParse(rawDeliveryId, out var deliveryId))
                {
                    var idempotencyKey = reader.GetString(1);
                    var requestHash = reader.GetString(2);
                    var serverId = reader.GetString(3);
                    ExtractionDeliveryReceiptV1? receipt = null;
                    if (!reader.IsDBNull(4))
                    {
                        try
                        {
                            receipt = JsonSerializer.Deserialize<ExtractionDeliveryReceiptV1>(
                                reader.GetString(4),
                                JsonOptions) ?? throw new InvalidDataException("The delivery receipt is empty.");
                        }
                        catch (Exception exception) when (exception is JsonException or InvalidDataException)
                        {
                            AddIssue(issues, "DELIVERY_RECEIPT_PAYLOAD_INVALID", "delivery-receipt", rawDeliveryId,
                                "A finalized delivery receipt cannot be parsed under schema version 1.");
                        }
                    }
                    if (!IsSha256(requestHash) || string.IsNullOrWhiteSpace(serverId))
                    {
                        AddIssue(issues, "DELIVERY_RECEIPT_ENVELOPE_INVALID", "delivery-receipt", rawDeliveryId,
                            "A delivery receipt request envelope has an invalid hash or server id.");
                    }
                    if (receipt is not null &&
                        (receipt.SchemaVersion != ExtractionDeliveryReceiptContract.SchemaVersion ||
                         receipt.DeliveryId != deliveryId ||
                         !string.Equals(receipt.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) ||
                         !string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal) ||
                         !string.Equals(receipt.ServerId, serverId, StringComparison.OrdinalIgnoreCase)))
                    {
                        AddIssue(issues, "DELIVERY_RECEIPT_COLUMN_PAYLOAD_MISMATCH", "delivery-receipt", rawDeliveryId,
                            "A finalized receipt payload differs from its immutable SQL request envelope.");
                    }
                    state.Receipts[deliveryId] = new ReceiptLink(
                        deliveryId,
                        idempotencyKey,
                        requestHash,
                        serverId,
                        receipt);
                }
                else
                {
                    AddIssue(issues, "DELIVERY_RECEIPT_ID_INVALID", "delivery-receipt", rawDeliveryId,
                        "A delivery receipt id is invalid.");
                }
            }
        }
        if (TableExists(connection, "extraction_delivery_evidence"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT delivery_id, command_id FROM extraction_delivery_evidence;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var deliveryId))
                {
                    state.EvidenceCommands[deliveryId] = reader.IsDBNull(1)
                        ? null
                        : Guid.TryParse(reader.GetString(1), out var commandId)
                            ? commandId
                            : Guid.Empty;
                }
                else
                {
                    AddIssue(issues, "DELIVERY_EVIDENCE_ID_INVALID", "delivery-evidence", reader.GetString(0),
                        "A delivery evidence id is invalid.");
                }
            }
        }
    }

    private static void ValidateLogicalState(
        LogicalState state,
        IReadOnlyCollection<PhysicalTable> physical,
        List<AuditIssue> issues)
    {
        ValidateAccounts(state, issues);
        ValidateWallets(state, issues);
        ValidateOrdersAndDeliveries(state, issues);
        ValidateIdempotency(state, issues);
        ValidateRuns(state, issues);
        ValidateSideEffectLinks(state, physical, issues);
    }

    private static void ValidateAccounts(LogicalState state, List<AuditIssue> issues)
    {
        var identities = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in state.Accounts.Values.Select(value => value.Value))
        {
            if (account.AccountId == Guid.Empty ||
                string.IsNullOrWhiteSpace(account.IdentityProvider) ||
                string.IsNullOrWhiteSpace(account.ExternalUserId) ||
                account.Revision <= 0)
            {
                AddIssue(issues, "ACCOUNT_PROJECTION_INVALID", "account", account.AccountId.ToString("N"),
                    "An account projection has an invalid identity or revision.");
            }
            var identity = $"{account.IdentityProvider}\n{account.ExternalUserId}";
            if (identities.TryGetValue(identity, out var existing) && existing != account.AccountId)
            {
                AddIssue(issues, "ACCOUNT_IDENTITY_NOT_UNIQUE", "account", identity,
                    "One normalized platform identity maps to multiple accounts.");
            }
            else
            {
                identities[identity] = account.AccountId;
            }
        }
    }

    private static void ValidateWallets(LogicalState state, List<AuditIssue> issues)
    {
        var running = new Dictionary<string, long>(StringComparer.Ordinal);
        var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var ledgerValue in state.Ledger.Values
                     .OrderBy(value => value.Sequence)
                     .ThenBy(value => value.Index))
        {
            var ledger = ledgerValue.Value;
            var key = WalletKey(ledger.AccountId, ledger.Currency, ledger.SeasonId);
            if (!state.Accounts.ContainsKey(ledger.AccountId))
            {
                AddIssue(issues, "LEDGER_ACCOUNT_MISSING", "ledger", ledger.EntryId.ToString("N"),
                    "A wallet ledger entry references a missing account.");
            }
            ValidateCurrencyScope(
                ledger.Currency,
                ledger.SeasonId,
                "LEDGER_CURRENCY_SCOPE_INVALID",
                "ledger",
                ledger.EntryId.ToString("N"),
                issues);
            if (ledger.SeasonId is Guid seasonId && !state.Seasons.ContainsKey(seasonId))
            {
                AddIssue(issues, "LEDGER_SEASON_MISSING", "ledger", ledger.EntryId.ToString("N"),
                    "A seasonal wallet ledger entry references a missing season.");
            }
            long next;
            try
            {
                next = checked(running.GetValueOrDefault(key) + ledger.Delta);
            }
            catch (OverflowException)
            {
                AddIssue(issues, "LEDGER_SUM_OVERFLOW", "ledger", ledger.EntryId.ToString("N"),
                    "A wallet stream overflows Int64 while being recomputed.");
                continue;
            }
            running[key] = next;
            revisions[key] = revisions.GetValueOrDefault(key) + 1;
            if (next < 0 || next > MaximumWebSafeInteger || ledger.BalanceAfter != next)
            {
                AddIssue(issues, "LEDGER_BALANCE_AFTER_MISMATCH", "ledger", ledger.EntryId.ToString("N"),
                    "A ledger BalanceAfter differs from the sequence-ordered delta projection.");
            }
        }

        foreach (var balanceValue in state.Balances.Values)
        {
            var balance = balanceValue.Value;
            var key = WalletKey(balance.AccountId, balance.Currency, balance.SeasonId);
            if (!state.Accounts.ContainsKey(balance.AccountId))
            {
                AddIssue(issues, "BALANCE_ACCOUNT_MISSING", "wallet", key,
                    "A wallet projection references a missing account.");
            }
            ValidateCurrencyScope(
                balance.Currency,
                balance.SeasonId,
                "BALANCE_CURRENCY_SCOPE_INVALID",
                "wallet",
                key,
                issues);
            if (balance.SeasonId is Guid seasonId && !state.Seasons.ContainsKey(seasonId))
            {
                AddIssue(issues, "BALANCE_SEASON_MISSING", "wallet", key,
                    "A seasonal wallet projection references a missing season.");
            }
            if (balance.Balance != running.GetValueOrDefault(key) ||
                balance.Revision != revisions.GetValueOrDefault(key) ||
                balance.Balance < 0 ||
                balance.Balance > MaximumWebSafeInteger)
            {
                AddIssue(issues, "WALLET_LEDGER_PROJECTION_MISMATCH", "wallet", key,
                    "A wallet balance/revision differs from the complete ledger projection.");
            }
        }
        foreach (var key in running.Keys.Where(key => !state.Balances.ContainsKey(key)))
        {
            AddIssue(issues, "WALLET_PROJECTION_MISSING", "wallet", key,
                "A non-empty wallet ledger stream has no final wallet projection.");
        }
    }

    private static void ValidateOrdersAndDeliveries(LogicalState state, List<AuditIssue> issues)
    {
        foreach (var order in state.Orders.Values.Select(value => value.Value))
        {
            var orderKey = order.OrderId.ToString("N");
            if (!state.Accounts.ContainsKey(order.AccountId))
            {
                AddIssue(issues, "ORDER_ACCOUNT_MISSING", "order", orderKey,
                    "An order references a missing account.");
            }
            if (!state.Seasons.ContainsKey(order.SeasonId))
            {
                AddIssue(issues, "ORDER_SEASON_MISSING", "order", orderKey,
                    "An order references a missing season.");
            }
            var expectedCharges = new Dictionary<ExtractionCurrency, long>();
            try
            {
                foreach (var line in order.Lines ?? [])
                {
                    if (line.Quantity <= 0 || line.UnitPrice < 0 ||
                        line.LineTotal != checked(line.UnitPrice * line.Quantity))
                    {
                        AddIssue(issues, "ORDER_LINE_TOTAL_INVALID", "order", orderKey,
                            "An immutable order line has invalid quantity, price, or total evidence.");
                    }
                    if (line.LineTotal > 0)
                    {
                        expectedCharges[line.PriceCurrency] = checked(
                            expectedCharges.GetValueOrDefault(line.PriceCurrency) + line.LineTotal);
                    }
                }
            }
            catch (OverflowException)
            {
                AddIssue(issues, "ORDER_TOTAL_OVERFLOW", "order", orderKey,
                    "An immutable order total overflows Int64.");
            }
            var storedCharges = new Dictionary<ExtractionCurrency, long>();
            try
            {
                foreach (var charge in order.Charges ?? [])
                {
                    if (charge.Amount <= 0 || storedCharges.ContainsKey(charge.Currency))
                    {
                        AddIssue(issues, "ORDER_CHARGE_ROW_INVALID", "order", orderKey,
                            "Stored order charges must be positive and unique per currency.");
                    }
                    storedCharges[charge.Currency] = checked(
                        storedCharges.GetValueOrDefault(charge.Currency) + charge.Amount);
                }
            }
            catch (OverflowException)
            {
                AddIssue(issues, "ORDER_TOTAL_OVERFLOW", "order", orderKey,
                    "Stored order charges overflow Int64.");
            }
            if (!DictionaryEqual(expectedCharges, storedCharges))
            {
                AddIssue(issues, "ORDER_CHARGE_TOTAL_MISMATCH", "order", orderKey,
                    "Stored order charges differ from immutable line totals.");
            }
            foreach (var charge in storedCharges)
            {
                Guid? seasonId = charge.Key == ExtractionCurrency.MarketCoin ? null : order.SeasonId;
                var purchaseEntries = state.Ledger.Values.Select(value => value.Value)
                    .Where(entry => entry.AccountId == order.AccountId &&
                        entry.Currency == charge.Key &&
                        entry.SeasonId == seasonId &&
                        string.Equals(entry.ReferenceType, "shop_order", StringComparison.Ordinal) &&
                        string.Equals(entry.ReferenceId, orderKey, StringComparison.Ordinal));
                if (!TryCheckedLedgerSum(purchaseEntries, out var purchaseDelta))
                {
                    AddIssue(issues, "ORDER_LEDGER_SUM_OVERFLOW", "order", orderKey,
                        "Order charge ledger entries overflow Int64.");
                }
                else if (purchaseDelta != -charge.Value)
                {
                    AddIssue(issues, "ORDER_LEDGER_CHARGE_MISMATCH", "order", orderKey,
                        "Order charges do not have one conserved negative ledger projection.");
                }
                var refundEntries = state.Ledger.Values.Select(value => value.Value)
                    .Where(entry => entry.AccountId == order.AccountId &&
                        entry.Currency == charge.Key &&
                        entry.SeasonId == seasonId &&
                        string.Equals(entry.ReferenceType, "shop_refund", StringComparison.Ordinal) &&
                        string.Equals(entry.ReferenceId, orderKey, StringComparison.Ordinal));
                var expectedRefund = order.State == ShopOrderState.Refunded ? charge.Value : 0;
                if (!TryCheckedLedgerSum(refundEntries, out var refundDelta))
                {
                    AddIssue(issues, "ORDER_LEDGER_SUM_OVERFLOW", "order", orderKey,
                        "Order refund ledger entries overflow Int64.");
                }
                else if (refundDelta != expectedRefund)
                {
                    AddIssue(issues, "ORDER_LEDGER_REFUND_MISMATCH", "order", orderKey,
                        "Order refund state differs from its conserved positive ledger projection.");
                }
            }
            if (!state.Deliveries.TryGetValue(order.DeliveryId, out var currentDelivery) ||
                currentDelivery.Value.OrderId != order.OrderId ||
                currentDelivery.Value.Attempt != order.DeliveryAttempt)
            {
                AddIssue(issues, "ORDER_CURRENT_DELIVERY_MISMATCH", "order", orderKey,
                    "An order does not reference its current delivery attempt.");
                continue;
            }
            if (!OrderDeliveryStatesAgree(order.State, currentDelivery.Value.State))
            {
                AddIssue(issues, "ORDER_DELIVERY_STATE_MISMATCH", "order", orderKey,
                    "Current order and delivery terminal/progress states disagree.");
            }
        }

        foreach (var delivery in state.Deliveries.Values.Select(value => value.Value))
        {
            var deliveryKey = delivery.DeliveryId.ToString("N");
            if (!state.Orders.TryGetValue(delivery.OrderId, out var order) ||
                delivery.Attempt <= 0 ||
                delivery.Attempt > order.Value.DeliveryAttempt)
            {
                AddIssue(issues, "DELIVERY_ORDER_MISMATCH", "delivery", deliveryKey,
                    "A delivery does not map to a valid order attempt.");
            }
            var expectedKey = $"shop-delivery:{delivery.OrderId:N}:{delivery.Attempt}";
            if (!string.Equals(delivery.IdempotencyKey, expectedKey, StringComparison.Ordinal))
            {
                AddIssue(issues, "DELIVERY_IDEMPOTENCY_KEY_MISMATCH", "delivery", deliveryKey,
                    "A delivery idempotency key does not match its immutable order attempt.");
            }
            var terminal = delivery.State is
                ShopDeliveryState.Delivered or ShopDeliveryState.Failed or ShopDeliveryState.Uncertain;
            if (terminal != (delivery.CompletedAt is not null) ||
                (delivery.State == ShopDeliveryState.Dispatching && delivery.DispatchedAt is null))
            {
                AddIssue(issues, "DELIVERY_TIMESTAMP_STATE_MISMATCH", "delivery", deliveryKey,
                    "A delivery state does not agree with its dispatch/completion timestamps.");
            }
        }
    }

    private static bool TryCheckedLedgerSum(
        IEnumerable<WalletLedgerEntry> entries,
        out long total)
    {
        total = 0;
        try
        {
            foreach (var entry in entries)
            {
                total = checked(total + entry.Delta);
            }
            return true;
        }
        catch (OverflowException)
        {
            total = 0;
            return false;
        }
    }

    private static void ValidateIdempotency(LogicalState state, List<AuditIssue> issues)
    {
        foreach (var stored in state.Idempotency.Values.Select(value => value.Value))
        {
            var scopeParts = stored.Scope.Split('\n');
            if (scopeParts.Length != 3 ||
                !Guid.TryParseExact(scopeParts.ElementAtOrDefault(1), "N", out var accountId) ||
                !IsSha256(stored.RequestHash))
            {
                AddIssue(issues, "IDEMPOTENCY_RECORD_INVALID", "idempotency", stored.Scope,
                    "An idempotency record has an invalid scope or request hash.");
                continue;
            }
            if (!state.Accounts.ContainsKey(accountId))
            {
                AddIssue(issues, "IDEMPOTENCY_ACCOUNT_MISSING", "idempotency", stored.Scope,
                    "An idempotency scope references a missing account.");
            }
            switch (stored.ResourceType)
            {
                case "wallet-ledger" when state.Ledger.TryGetValue(stored.ResourceId, out var ledger):
                    if (scopeParts[0] != "wallet" || ledger.Value.AccountId != accountId)
                    {
                        AddIssue(issues, "IDEMPOTENCY_LEDGER_MISMATCH", "idempotency", stored.Scope,
                            "A wallet idempotency record maps to the wrong operation or account.");
                    }
                    break;
                case "shop-order" when state.Orders.TryGetValue(stored.ResourceId, out var order):
                    if (scopeParts[0] is not ("purchase" or "refund") ||
                        order.Value.AccountId != accountId ||
                        (scopeParts[0] == "purchase" &&
                         !string.Equals(order.Value.PurchaseIdempotencyKey, scopeParts[2], StringComparison.Ordinal)))
                    {
                        AddIssue(issues, "IDEMPOTENCY_ORDER_MISMATCH", "idempotency", stored.Scope,
                            "A shop idempotency record maps to the wrong operation, account, or purchase key.");
                    }
                    break;
                default:
                    AddIssue(issues, "IDEMPOTENCY_RESOURCE_MISSING", "idempotency", stored.Scope,
                        "An idempotency record references a missing or unsupported durable resource.");
                    break;
            }
        }
        foreach (var order in state.Orders.Values.Select(value => value.Value))
        {
            var expectedScope = $"purchase\n{order.AccountId:N}\n{order.PurchaseIdempotencyKey}";
            if (!state.Idempotency.TryGetValue(expectedScope, out var idempotency) ||
                idempotency.Value.ResourceId != order.OrderId)
            {
                AddIssue(issues, "ORDER_PURCHASE_IDEMPOTENCY_MISSING", "order", order.OrderId.ToString("N"),
                    "An order lacks its immutable purchase idempotency mapping.");
            }
        }
    }

    private static void ValidateRuns(LogicalState state, List<AuditIssue> issues)
    {
        foreach (var runValue in state.Runs.Values)
        {
            var run = runValue.Value;
            var runKey = run.RunId.ToString("N");
            if (!state.Accounts.ContainsKey(run.AccountId))
            {
                AddIssue(issues, "RUN_ACCOUNT_MISSING", "run", runKey,
                    "A settlement run references a missing account.");
            }
            if (!state.Seasons.ContainsKey(run.SeasonId))
            {
                AddIssue(issues, "RUN_SEASON_MISSING", "run", runKey,
                    "A settlement run references a missing season.");
            }
            long itemTotal;
            int itemCount;
            try
            {
                itemTotal = run.Items.Sum(item => checked(item.UnitValue * item.Quantity));
                itemCount = run.Items.Sum(item => item.Quantity);
            }
            catch (OverflowException)
            {
                AddIssue(issues, "RUN_TOTAL_OVERFLOW", "run", runKey,
                    "A settlement run item projection overflows its supported range.");
                itemTotal = long.MinValue;
                itemCount = int.MinValue;
            }
            if (run.Items.Any(item => item.Quantity <= 0 || item.UnitValue <= 0 ||
                    item.TotalValue != item.UnitValue * item.Quantity) ||
                itemTotal != run.TotalValue ||
                itemCount != run.ItemCount ||
                run.TotalValue <= 0 ||
                run.Revision < 0)
            {
                AddIssue(issues, "RUN_VALUE_PROJECTION_MISMATCH", "run", runKey,
                    "A settlement run total/count differs from its immutable item rows.");
            }
            var requiresCredit = run.State is ExtractionSettlementState.Credited or ExtractionSettlementState.Settled;
            if (requiresCredit != state.RunCredits.ContainsKey(run.RunId))
            {
                AddIssue(issues, "RUN_CREDIT_STATE_MISMATCH", "run", runKey,
                    "A credited/settled run and its unique credit row disagree.");
            }
        }

        foreach (var credit in state.RunCredits.Values)
        {
            var runKey = credit.RunId.ToString("N");
            if (!state.Runs.TryGetValue(credit.RunId, out var run) ||
                run.Value.AccountId != credit.AccountId ||
                run.Value.SeasonId != credit.SeasonId ||
                run.Value.TotalValue != credit.Amount ||
                !state.Ledger.TryGetValue(credit.LedgerEntryId, out var ledger) ||
                ledger.Value.AccountId != credit.AccountId ||
                ledger.Value.SeasonId != credit.SeasonId ||
                ledger.Value.Currency != ExtractionCurrency.SeasonVoucher ||
                ledger.Value.Delta != credit.Amount ||
                !string.Equals(ledger.Value.ReferenceType, "extraction_run", StringComparison.Ordinal) ||
                !string.Equals(ledger.Value.ReferenceId, runKey, StringComparison.Ordinal))
            {
                AddIssue(issues, "RUN_CREDIT_LEDGER_MISMATCH", "run-credit", runKey,
                    "A unique run-credit row does not match its run and wallet ledger entry.");
            }
        }
        foreach (var ledger in state.Ledger.Values.Select(value => value.Value).Where(entry =>
                     string.Equals(entry.ReferenceType, "extraction_run", StringComparison.Ordinal)))
        {
            if (!Guid.TryParseExact(ledger.ReferenceId, "N", out var runId) ||
                !state.RunCredits.TryGetValue(runId, out var credit) ||
                credit.LedgerEntryId != ledger.EntryId)
            {
                AddIssue(issues, "LEDGER_RUN_CREDIT_MISSING", "ledger", ledger.EntryId.ToString("N"),
                    "An extraction-run ledger entry lacks its unique run-credit index.");
            }
        }
    }

    private static void ValidateSideEffectLinks(
        LogicalState state,
        IReadOnlyCollection<PhysicalTable> physical,
        List<AuditIssue> issues)
    {
        var commandTablePresent = physical.Any(table =>
            string.Equals(table.Table, "paldefender_commands", StringComparison.Ordinal));
        var receiptTablePresent = physical.Any(table =>
            string.Equals(table.Table, "extraction_delivery_receipts", StringComparison.Ordinal));
        foreach (var delivery in state.Deliveries.Values.Select(value => value.Value))
        {
            var deliveryKey = delivery.DeliveryId.ToString("N");
            if (delivery.CommandId is Guid commandId && commandId != Guid.Empty)
            {
                var expectedCommandKey = PalDefenderItemGrantAdapter.LineIdempotencyKey(
                    delivery.DeliveryId,
                    0);
                if (!state.Commands.TryGetValue(commandId, out var command) ||
                    !string.Equals(command.IdempotencyKey, expectedCommandKey, StringComparison.Ordinal))
                {
                    AddIssue(issues, "DELIVERY_COMMAND_LINK_MISMATCH", "delivery", deliveryKey,
                        "A delivery command id does not map to its first durable per-item outbox command.");
                }
                else if (state.Orders.TryGetValue(delivery.OrderId, out var order) &&
                    !string.Equals(command.ServerId, order.Value.ServerId, StringComparison.OrdinalIgnoreCase))
                {
                    AddIssue(issues, "DELIVERY_COMMAND_SERVER_MISMATCH", "delivery", deliveryKey,
                        "A delivery outbox command targets a different server than its order.");
                }
            }
            else if (commandTablePresent && delivery.State == ShopDeliveryState.Dispatching)
            {
                AddIssue(issues, "DELIVERY_DISPATCH_COMMAND_MISSING", "delivery", deliveryKey,
                    "A dispatching delivery lacks its durable command id.");
            }
            if (state.Receipts.TryGetValue(delivery.DeliveryId, out var receipt))
            {
                if (!string.Equals(receipt.IdempotencyKey, delivery.IdempotencyKey, StringComparison.Ordinal) ||
                    state.Orders.TryGetValue(delivery.OrderId, out var receiptOrder) &&
                    !string.Equals(receipt.ServerId, receiptOrder.Value.ServerId, StringComparison.OrdinalIgnoreCase))
                {
                    AddIssue(issues, "DELIVERY_RECEIPT_LINK_MISMATCH", "delivery", deliveryKey,
                        "A structured delivery receipt uses a different delivery key or server.");
                }
                if (receipt.Receipt is { } finalized)
                {
                    if (!ReceiptOutcomeMatchesDelivery(finalized.Outcome, delivery.State) ||
                        !ReceiptOutcomeMatchesItems(finalized) ||
                        !state.Orders.TryGetValue(delivery.OrderId, out var order) ||
                        !ReceiptItemsMatchOrder(finalized.Items, order.Value))
                    {
                        AddIssue(issues, "DELIVERY_RECEIPT_RESULT_MISMATCH", "delivery", deliveryKey,
                            "A finalized receipt result does not match its order grants or delivery state.");
                    }
                    if (finalized.Items is null)
                    {
                        AddIssue(issues, "DELIVERY_RECEIPT_ITEM_COMMAND_MISMATCH", "delivery", deliveryKey,
                            "A finalized receipt has no attributable per-item command evidence.");
                    }
                    else
                    {
                        for (var index = 0; index < finalized.Items.Count; index++)
                        {
                            var item = finalized.Items[index];
                            var expectedItemKey = PalDefenderItemGrantAdapter.LineIdempotencyKey(
                                delivery.DeliveryId,
                                index);
                            if (item.CommandId is Guid itemCommandId && itemCommandId != Guid.Empty)
                            {
                                if (!state.Commands.TryGetValue(itemCommandId, out var itemCommand) ||
                                    !string.Equals(itemCommand.IdempotencyKey, expectedItemKey, StringComparison.Ordinal) ||
                                    !string.Equals(itemCommand.ServerId, finalized.ServerId, StringComparison.OrdinalIgnoreCase) ||
                                    !ReceiptItemCommandStateMatches(item.Result, itemCommand.State))
                                {
                                    AddIssue(issues, "DELIVERY_RECEIPT_ITEM_COMMAND_MISMATCH", "delivery", deliveryKey,
                                        "A receipt item does not map to its durable per-item outbox command.");
                                }
                            }
                            else if (item.Result != ExtractionDeliveryReceiptItemResult.CommandRecordMissing)
                            {
                                AddIssue(issues, "DELIVERY_RECEIPT_ITEM_COMMAND_MISMATCH", "delivery", deliveryKey,
                                    "A receipt item lacks its required durable command reference.");
                            }
                        }
                    }
                }
                else if (delivery.State == ShopDeliveryState.Delivered)
                {
                    AddIssue(issues, "DELIVERY_FINAL_RECEIPT_MISSING", "delivery", deliveryKey,
                        "A delivered order has no finalized immutable receipt.");
                }
            }
            else if (receiptTablePresent && delivery.State == ShopDeliveryState.Delivered)
            {
                AddIssue(issues, "DELIVERY_FINAL_RECEIPT_MISSING", "delivery", deliveryKey,
                    "A delivered order has no immutable receipt registration.");
            }
            if (state.EvidenceCommands.TryGetValue(delivery.DeliveryId, out var evidenceCommand) &&
                evidenceCommand is Guid evidenceCommandId &&
                evidenceCommandId != delivery.CommandId)
            {
                AddIssue(issues, "DELIVERY_EVIDENCE_COMMAND_MISMATCH", "delivery", deliveryKey,
                    "Delivery inventory evidence maps to a different durable command.");
            }
        }
        foreach (var deliveryId in state.Receipts.Keys.Where(id => !state.Deliveries.ContainsKey(id)))
        {
            AddIssue(issues, "DELIVERY_RECEIPT_ORPHAN", "delivery-receipt", deliveryId.ToString("N"),
                "A structured delivery receipt references no shop delivery.");
        }
        foreach (var deliveryId in state.EvidenceCommands.Keys.Where(id => !state.Deliveries.ContainsKey(id)))
        {
            AddIssue(issues, "DELIVERY_EVIDENCE_ORPHAN", "delivery-evidence", deliveryId.ToString("N"),
                "Delivery inventory evidence references no shop delivery.");
        }
    }

    private static bool ReceiptOutcomeMatchesDelivery(
        ExtractionDeliveryReceiptOutcome outcome,
        ShopDeliveryState state) =>
        (outcome, state) switch
        {
            (ExtractionDeliveryReceiptOutcome.Succeeded, ShopDeliveryState.Delivered) => true,
            (ExtractionDeliveryReceiptOutcome.Failed, ShopDeliveryState.Failed) => true,
            (ExtractionDeliveryReceiptOutcome.Partial, ShopDeliveryState.Uncertain) => true,
            (ExtractionDeliveryReceiptOutcome.Uncertain, ShopDeliveryState.Uncertain) => true,
            _ => false
        };

    private static bool ReceiptOutcomeMatchesItems(ExtractionDeliveryReceiptV1 receipt)
    {
        if (receipt.Items is null || receipt.Items.Count == 0 || receipt.Items.Count > 100)
        {
            return false;
        }
        var expected = receipt.Items.Any(item => item.Result is
                ExtractionDeliveryReceiptItemResult.Uncertain or
                ExtractionDeliveryReceiptItemResult.InvalidReceipt or
                ExtractionDeliveryReceiptItemResult.CommandRecordMissing)
            ? ExtractionDeliveryReceiptOutcome.Uncertain
            : receipt.Items.All(item => item.Result == ExtractionDeliveryReceiptItemResult.Succeeded)
                ? ExtractionDeliveryReceiptOutcome.Succeeded
                : receipt.Items.All(item => item.Result == ExtractionDeliveryReceiptItemResult.FailedBeforeMutation)
                    ? ExtractionDeliveryReceiptOutcome.Failed
                    : ExtractionDeliveryReceiptOutcome.Partial;
        return expected == receipt.Outcome && receipt.Items.All(item =>
            !string.IsNullOrWhiteSpace(item.ItemId) &&
            item.Requested > 0 &&
            item.Granted is null or >= 0 &&
            item.Result switch
            {
                ExtractionDeliveryReceiptItemResult.Succeeded => item.Granted == item.Requested,
                ExtractionDeliveryReceiptItemResult.FailedBeforeMutation => item.Granted == 0,
                ExtractionDeliveryReceiptItemResult.Partial => item.Granted is >= 0 && item.Granted < item.Requested,
                ExtractionDeliveryReceiptItemResult.Uncertain or
                ExtractionDeliveryReceiptItemResult.CommandRecordMissing => item.Granted is null,
                ExtractionDeliveryReceiptItemResult.InvalidReceipt => true,
                _ => false
            });
    }

    private static bool ReceiptItemCommandStateMatches(
        ExtractionDeliveryReceiptItemResult result,
        string commandState) =>
        result switch
        {
            ExtractionDeliveryReceiptItemResult.Succeeded or
            ExtractionDeliveryReceiptItemResult.Partial or
            ExtractionDeliveryReceiptItemResult.InvalidReceipt =>
                string.Equals(commandState, "succeeded", StringComparison.Ordinal),
            ExtractionDeliveryReceiptItemResult.FailedBeforeMutation =>
                string.Equals(commandState, "failed", StringComparison.Ordinal),
            ExtractionDeliveryReceiptItemResult.Uncertain =>
                string.Equals(commandState, "uncertain", StringComparison.Ordinal),
            ExtractionDeliveryReceiptItemResult.CommandRecordMissing => false,
            _ => false
        };

    private static bool ReceiptItemsMatchOrder(
        IReadOnlyList<ExtractionDeliveryReceiptItem>? items,
        ShopOrder order)
    {
        if (items is null || order.Lines is null)
        {
            return false;
        }
        try
        {
            var expected = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in order.Lines)
            {
                if (line.ItemGrants is null || line.Quantity <= 0)
                {
                    return false;
                }
                foreach (var grant in line.ItemGrants)
                {
                    var quantity = checked(grant.Quantity * line.Quantity);
                    expected[grant.ItemId] = checked(expected.GetValueOrDefault(grant.ItemId) + quantity);
                }
            }
            return items.Count == expected.Count &&
                items.Select((item, index) => (item, index)).All(pair =>
                    pair.item.Requested > 0 &&
                    string.Equals(pair.item.ItemId, expected.ElementAt(pair.index).Key, StringComparison.Ordinal) &&
                    pair.item.Requested == expected.ElementAt(pair.index).Value);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static IReadOnlyList<AuditRowHash> BuildLogicalRows(LogicalState state)
    {
        var rows = new List<AuditRowHash>();
        AddRows(rows, "season", state.Seasons.Values,
            value => value.Value.SeasonId.ToString("N"));
        AddRows(rows, "account", state.Accounts.Values,
            value => value.Value.AccountId.ToString("N"));
        AddRows(rows, "wallet", state.Balances.Values,
            value => WalletKey(value.Value.AccountId, value.Value.Currency, value.Value.SeasonId));
        AddRows(rows, "ledger", state.Ledger.Values,
            value => value.Value.EntryId.ToString("N"));
        AddRows(rows, "product", state.Products.Values,
            value => value.Value.Sku.ToUpperInvariant());
        AddRows(rows, "order", state.Orders.Values,
            value => value.Value.OrderId.ToString("N"));
        AddRows(rows, "delivery", state.Deliveries.Values,
            value => value.Value.DeliveryId.ToString("N"));
        AddRows(rows, "idempotency", state.Idempotency.Values,
            value => value.Value.Scope);
        AddRows(rows, "run", state.Runs.Values,
            value => value.Value.RunId.ToString("N"));
        foreach (var credit in state.RunCredits.Values)
        {
            var element = CanonicalHash.ToElement(credit, JsonOptions);
            rows.Add(CreateRow("run-credit", credit.RunId.ToString("N"), element));
        }
        return rows.OrderBy(row => row.Category, StringComparer.Ordinal)
            .ThenBy(row => row.KeyFingerprint, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddRows<T>(
        List<AuditRowHash> rows,
        string category,
        IEnumerable<LogicalValue<T>> values,
        Func<LogicalValue<T>, string> key)
    {
        foreach (var value in values)
        {
            rows.Add(CreateRow(category, key(value), value.Element));
        }
    }

    private static AuditRowHash CreateRow(string category, string key, JsonElement element)
    {
        var canonical = CanonicalHash.Json(element);
        return new AuditRowHash(
            $"logical:{category}",
            CanonicalHash.Key($"logical:{category}", key),
            CanonicalHash.Domain($"logical:{category}", canonical));
    }

    private static IReadOnlyList<AuditAccountSummary> BuildAccountSummaries(LogicalState state)
    {
        var results = new List<AuditAccountSummary>();
        foreach (var account in state.Accounts.Values.Select(value => value.Value)
                     .OrderBy(account => account.AccountId))
        {
            var associated = new List<AuditRowHash>();
            associated.Add(CreateRow(
                "account",
                account.AccountId.ToString("N"),
                state.Accounts[account.AccountId].Element));
            foreach (var balance in state.Balances.Values.Where(value =>
                         value.Value.AccountId == account.AccountId))
            {
                associated.Add(CreateRow(
                    "wallet",
                    WalletKey(balance.Value.AccountId, balance.Value.Currency, balance.Value.SeasonId),
                    balance.Element));
            }
            foreach (var ledger in state.Ledger.Values.Where(value =>
                         value.Value.AccountId == account.AccountId))
            {
                associated.Add(CreateRow("ledger", ledger.Value.EntryId.ToString("N"), ledger.Element));
            }
            foreach (var order in state.Orders.Values.Where(value =>
                         value.Value.AccountId == account.AccountId))
            {
                associated.Add(CreateRow("order", order.Value.OrderId.ToString("N"), order.Element));
                foreach (var delivery in state.Deliveries.Values.Where(value =>
                             value.Value.OrderId == order.Value.OrderId))
                {
                    associated.Add(CreateRow(
                        "delivery",
                        delivery.Value.DeliveryId.ToString("N"),
                        delivery.Element));
                }
            }
            foreach (var run in state.Runs.Values.Where(value =>
                         value.Value.AccountId == account.AccountId))
            {
                associated.Add(CreateRow("run", run.Value.RunId.ToString("N"), run.Element));
                if (state.RunCredits.TryGetValue(run.Value.RunId, out var credit))
                {
                    associated.Add(CreateRow(
                        "run-credit",
                        credit.RunId.ToString("N"),
                        CanonicalHash.ToElement(credit, JsonOptions)));
                }
            }
            var orders = state.Orders.Values.Count(value => value.Value.AccountId == account.AccountId);
            var orderIds = state.Orders.Values.Where(value => value.Value.AccountId == account.AccountId)
                .Select(value => value.Value.OrderId)
                .ToHashSet();
            results.Add(new AuditAccountSummary(
                CanonicalHash.Key("account", account.AccountId.ToString("N")),
                state.Balances.Values.Count(value => value.Value.AccountId == account.AccountId),
                state.Ledger.Values.Count(value => value.Value.AccountId == account.AccountId),
                orders,
                state.Deliveries.Values.Count(value => orderIds.Contains(value.Value.OrderId)),
                state.Runs.Values.Count(value => value.Value.AccountId == account.AccountId),
                CanonicalHash.Aggregate($"account:{account.AccountId:N}", associated)));
        }
        return results.OrderBy(result => result.AccountFingerprint, StringComparer.Ordinal).ToArray();
    }

    private static AuditBaselineComparison CompareBaseline(
        EconomyReconciliationReport baseline,
        string domainHash,
        string physicalHash,
        IReadOnlyCollection<AuditTableHash> tables,
        IReadOnlyCollection<AuditRowHash> rows)
    {
        var compatible = baseline.SchemaVersion == 1 &&
            string.Equals(
                baseline.CanonicalizationVersion,
                CanonicalHash.Version,
                StringComparison.Ordinal);
        var domainMatch = compatible && baseline.DataValid &&
            string.Equals(baseline.DomainCanonicalHash, domainHash, StringComparison.Ordinal);
        var physicalMatch = compatible && baseline.DataValid &&
            string.Equals(baseline.PhysicalCanonicalHash, physicalHash, StringComparison.Ordinal);
        var currentTables = tables.ToDictionary(table => table.Table, StringComparer.Ordinal);
        var baselineTables = baseline.Tables.ToDictionary(table => table.Table, StringComparer.Ordinal);
        var changedTables = currentTables.Keys.Union(baselineTables.Keys, StringComparer.Ordinal)
            .Where(table => !currentTables.TryGetValue(table, out var current) ||
                !baselineTables.TryGetValue(table, out var previous) ||
                current.RowCount != previous.RowCount ||
                !string.Equals(current.CanonicalHash, previous.CanonicalHash, StringComparison.Ordinal))
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToArray();
        var currentRows = rows.Select(RowIdentity).ToHashSet(StringComparer.Ordinal);
        var baselineRows = baseline.Rows.Select(RowIdentity).ToHashSet(StringComparer.Ordinal);
        var changedRows = currentRows.Except(baselineRows, StringComparer.Ordinal).Count() +
            baselineRows.Except(currentRows, StringComparer.Ordinal).Count();
        return new AuditBaselineComparison(
            compatible && domainMatch && physicalMatch && changedTables.Length == 0 && changedRows == 0,
            domainMatch,
            physicalMatch,
            changedTables,
            changedRows);
    }

    private static string RowIdentity(AuditRowHash row) =>
        $"{row.Category}\n{row.KeyFingerprint}\n{row.CanonicalHash}";

    private static bool IsDomainSideEffectTable(string table) =>
        table is
            "paldefender_commands" or
            "paldefender_command_events" or
            "extraction_delivery_receipts" or
            "extraction_delivery_evidence" or
            "season_settlement_jobs" or
            "season_settlement_items" ||
        table.Contains("idempotency", StringComparison.OrdinalIgnoreCase);

    private static bool OrderDeliveryStatesAgree(
        ShopOrderState order,
        ShopDeliveryState delivery) =>
        (order, delivery) switch
        {
            (ShopOrderState.PendingDelivery, ShopDeliveryState.Pending) => true,
            (ShopOrderState.Dispatching, ShopDeliveryState.Dispatching) => true,
            (ShopOrderState.Delivered, ShopDeliveryState.Delivered) => true,
            (ShopOrderState.DeliveryFailed, ShopDeliveryState.Failed) => true,
            (ShopOrderState.DeliveryUncertain, ShopDeliveryState.Uncertain) => true,
            (ShopOrderState.Refunded, ShopDeliveryState.Failed or ShopDeliveryState.Uncertain) => true,
            _ => false
        };

    private static void ValidateCurrencyScope(
        ExtractionCurrency currency,
        Guid? seasonId,
        string code,
        string category,
        string key,
        List<AuditIssue> issues)
    {
        if ((currency == ExtractionCurrency.MarketCoin && seasonId is not null) ||
            (currency == ExtractionCurrency.SeasonVoucher && seasonId is null))
        {
            AddIssue(issues, code, category, key,
                "A permanent/seasonal currency uses the wrong season scope.");
        }
    }

    private static bool DictionaryEqual<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> left,
        IReadOnlyDictionary<TKey, TValue> right)
        where TKey : notnull
        where TValue : IEquatable<TValue> =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value));

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string WalletKey(
        Guid accountId,
        ExtractionCurrency currency,
        Guid? seasonId) =>
        $"{accountId:N}\n{currency}\n{seasonId?.ToString("N") ?? "permanent"}";

    private static bool TryReadObject<T>(
        JsonElement root,
        string property,
        out T value,
        out JsonElement element)
        where T : class
    {
        value = null!;
        element = default;
        if (!root.TryGetProperty(property, out var candidate) ||
            candidate.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        if (candidate.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Event property '{property}' must be an object.");
        }
        value = JsonSerializer.Deserialize<T>(candidate.GetRawText(), JsonOptions)
            ?? throw new InvalidDataException($"Event property '{property}' is empty.");
        element = candidate.Clone();
        return true;
    }

    private static bool TryReadArray<T>(
        JsonElement root,
        string property,
        out List<(T Value, JsonElement Element)> values)
    {
        values = [];
        if (!root.TryGetProperty(property, out var candidate) ||
            candidate.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        if (candidate.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Event property '{property}' must be an array.");
        }
        foreach (var element in candidate.EnumerateArray())
        {
            var value = JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
            if (value is null)
            {
                throw new InvalidDataException($"Event property '{property}' contains an empty row.");
            }
            values.Add((value, element.Clone()));
        }
        return true;
    }

    private static int ReadRequiredInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException($"Required integer property '{property}' is invalid.");

    private static string ReadRequiredString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Required string property '{property}' is invalid.");

    private static Guid ReadRequiredGuid(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetGuid(out var result) && result != Guid.Empty
            ? result
            : throw new InvalidDataException($"Required GUID property '{property}' is invalid.");

    private static DateTimeOffset ReadRequiredTimestamp(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetDateTimeOffset(out var result)
            ? result
            : throw new InvalidDataException($"Required timestamp property '{property}' is invalid.");

    private static bool ReadIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }
        return rows.Count == 1 && string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadForeignKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        return !reader.Read();
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM sqlite_master
            WHERE type = 'table' AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static object Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()
            ?? throw new InvalidDataException("A required SQLite scalar query returned null.");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void AddIssue(
        List<AuditIssue> issues,
        string code,
        string category,
        string? key,
        string message) =>
        issues.Add(new AuditIssue(
            code,
            category,
            key is null ? null : CanonicalHash.Key($"issue:{category}", key),
            message));

    private sealed class LogicalState
    {
        public Dictionary<Guid, LogicalValue<ExtractionSeason>> Seasons { get; } = [];
        public Dictionary<Guid, LogicalValue<ExtractionAccount>> Accounts { get; } = [];
        public Dictionary<string, LogicalValue<WalletBalance>> Balances { get; } =
            new(StringComparer.Ordinal);
        public Dictionary<Guid, LogicalValue<WalletLedgerEntry>> Ledger { get; } = [];
        public Dictionary<string, LogicalValue<ShopProduct>> Products { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, LogicalValue<ShopOrder>> Orders { get; } = [];
        public Dictionary<Guid, LogicalValue<ShopDelivery>> Deliveries { get; } = [];
        public Dictionary<string, LogicalValue<StoredIdempotency>> Idempotency { get; } =
            new(StringComparer.Ordinal);
        public Dictionary<Guid, LogicalValue<ExtractionSettlementRun>> Runs { get; } = [];
        public Dictionary<Guid, RunCredit> RunCredits { get; } = [];
        public Dictionary<Guid, CommandLink> Commands { get; } = [];
        public Dictionary<Guid, ReceiptLink> Receipts { get; } = [];
        public Dictionary<Guid, Guid?> EvidenceCommands { get; } = [];
    }

    private sealed record LogicalValue<T>(T Value, JsonElement Element, long Sequence, int Index);

    private sealed record StoredIdempotency(
        string Scope,
        string RequestHash,
        string ResourceType,
        Guid ResourceId,
        DateTimeOffset CreatedAt);

    private sealed record RunCredit(
        Guid RunId,
        Guid AccountId,
        Guid SeasonId,
        Guid LedgerEntryId,
        long Amount,
        DateTimeOffset CreatedAt);

    private sealed record CommandLink(
        Guid CommandId,
        string ServerId,
        string IdempotencyKey,
        string State);

    private sealed record ReceiptLink(
        Guid DeliveryId,
        string IdempotencyKey,
        string RequestHash,
        string ServerId,
        ExtractionDeliveryReceiptV1? Receipt);

    private sealed record ColumnInfo(int Ordinal, string Name, int PrimaryKeyOrder);

    private sealed record PhysicalTable(
        string Table,
        IReadOnlyList<AuditRowHash> Rows,
        string CanonicalHash);
}

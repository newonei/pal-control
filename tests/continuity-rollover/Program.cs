using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var cancellationToken = CancellationToken.None;
await VerifyRolloverCrashResumeAsync(cancellationToken);
await VerifyRolloverSchemaMigrationAsync(cancellationToken);
await VerifyObservabilityMigrationSchemaGuardAsync(cancellationToken);
await VerifySeasonJobsAsync(cancellationToken);
await VerifyDeliveryEvidenceMigrationAsync(cancellationToken);
await VerifyContinuitySnapshotAsync(cancellationToken);
await VerifySharedDataRootArchiveAsync(cancellationToken);
await VerifyCommandSideStateFailureClosedAsync(cancellationToken);
Console.WriteLine(
    "PASS: rollover transactional fault recovery, unique expiry/reward ledger, consistent snapshot, queue/idempotency replay, staging restore, reconciliation and corruption checks.");

static async Task VerifyRolloverCrashResumeAsync(CancellationToken cancellationToken)
{
    await WithDirectoryAsync(async directory =>
    {
        const string serverId = "local";
        const string fromWorld = "11111111111111111111111111111111";
        const string targetWorld = "22222222222222222222222222222222";
        var seasonId = Guid.NewGuid();
        var store = new WeeklyRolloverStateStore(directory);
        var operation = await store.PrepareAsync(
            serverId, seasonId, fromWorld, targetWorld, "rules-v1", cancellationToken);
        var same = await store.PrepareAsync(
            serverId, seasonId, fromWorld, targetWorld, "rules-v1", cancellationToken);
        Assert(operation.OperationId == same.OperationId,
            "deterministic rollover preparation returned a different operation id");
        Assert((await store.FindIncompleteAsync(serverId, cancellationToken))?.OperationId ==
               operation.OperationId,
            "restart lookup did not find the server's incomplete rollover");

        await AssertThrowsAsync<InvalidOperationException>(() => store.CompleteStepAsync(
            operation.OperationId,
            WeeklyRolloverStep.Drain,
            WeeklyRolloverStateStore.StepKey(operation.OperationId, WeeklyRolloverStep.Drain),
            Evidence(WeeklyRolloverStep.Drain, targetWorld),
            "test",
            cancellationToken), "rollover allowed a phase skip");

        var executable = new[]
        {
            WeeklyRolloverStep.Preflight,
            WeeklyRolloverStep.Drain,
            WeeklyRolloverStep.GameBackup,
            WeeklyRolloverStep.EconomyBackup,
            WeeklyRolloverStep.Stop,
            WeeklyRolloverStep.NewWorld,
            WeeklyRolloverStep.Probe,
            WeeklyRolloverStep.Commit,
            WeeklyRolloverStep.Reopen
        };
        foreach (var step in executable)
        {
            // A new store instance represents process death/restart before every phase.
            store = new WeeklyRolloverStateStore(directory);
            var before = await store.GetAsync(operation.OperationId, cancellationToken);
            Assert(before?.CurrentStep == step, $"restart did not resume at {step}");
            var evidence = Evidence(step, targetWorld);
            var key = WeeklyRolloverStateStore.StepKey(operation.OperationId, step);
            foreach (var faultPoint in Enum.GetValues<WeeklyRolloverFaultPoint>())
            {
                var injected = new WeeklyRolloverStateStore(
                    directory,
                    TimeProvider.System,
                    (point, faultStep) =>
                    {
                        if (point == faultPoint && faultStep == step)
                        {
                            throw new InjectedFaultException($"rollover:{step}:{point}");
                        }
                    });
                await AssertThrowsAsync<InjectedFaultException>(() => injected.CompleteStepAsync(
                    operation.OperationId,
                    step,
                    key,
                    evidence,
                    "test-fault",
                    cancellationToken), $"{step} did not surface injected fault at {faultPoint}");
                var afterFault = await new WeeklyRolloverStateStore(directory).GetAsync(
                    operation.OperationId,
                    cancellationToken);
                Assert(afterFault?.CurrentStep == step &&
                       afterFault.CompletedSteps.All(item => item.Step != step),
                    $"{step} left partial durable state after {faultPoint}");
            }
            if (step == WeeklyRolloverStep.Probe)
            {
                var wrong = evidence with { ObservedWorldId = "33333333333333333333333333333333" };
                await AssertThrowsAsync<InvalidOperationException>(() => store.CompleteStepAsync(
                    operation.OperationId, step, key, wrong, "test", cancellationToken),
                    "probe accepted evidence from the wrong world");
            }
            var transition = await store.CompleteStepAsync(
                operation.OperationId, step, key, evidence, "test", cancellationToken);
            Assert(transition.Applied && !transition.IdempotentReplay,
                $"{step} was not applied exactly once");

            store = new WeeklyRolloverStateStore(directory);
            var replay = await store.CompleteStepAsync(
                operation.OperationId, step, key, evidence, "test", cancellationToken);
            Assert(!replay.Applied && replay.IdempotentReplay,
                $"{step} did not replay idempotently after restart");
            await AssertThrowsAsync<InvalidOperationException>(() => store.CompleteStepAsync(
                operation.OperationId,
                step,
                key,
                evidence with { EvidenceHash = Hash($"conflicting-{step}") },
                "test",
                cancellationToken), $"{step} accepted conflicting replay evidence");
            await AssertThrowsAsync<InvalidOperationException>(() => store.CompleteStepAsync(
                operation.OperationId,
                step,
                key,
                evidence with { ObservedWorldId = "33333333333333333333333333333333" },
                "test",
                cancellationToken),
                $"{step} accepted the same external hash with a conflicting evidence envelope");
        }
        var completed = await store.GetAsync(operation.OperationId, cancellationToken);
        Assert(completed?.CurrentStep == WeeklyRolloverStep.Completed,
            "rollover did not reach the terminal projection");
        Assert(completed?.NewSeasonCommitted == true,
            "commit did not persist the irreversible new-season marker");
        Assert(completed?.CompletedSteps.Count == executable.Length,
            "rollover completion evidence count is incorrect");
        Assert(completed?.CompletedSteps.All(item => item.EvidencePayloadHash is { Length: 64 }) == true,
            "rollover did not persist a canonical full-evidence payload hash for every step");

        var second = await store.PrepareAsync(
            serverId,
            Guid.NewGuid(),
            targetWorld,
            "44444444444444444444444444444444",
            "rules-v2",
            cancellationToken);
        Assert(second.OperationId != operation.OperationId,
            "a completed rollover prevented the next weekly operation");
    });
}

static async Task VerifyRolloverSchemaMigrationAsync(CancellationToken cancellationToken)
{
    await WithDirectoryAsync(async directory =>
    {
        var database = Path.Combine(directory, "extraction-commerce.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = database,
                         Mode = SqliteOpenMode.ReadWriteCreate,
                         Pooling = false
                     }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE economy_schema_migrations (
                    component TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    applied_at TEXT NOT NULL,
                    PRIMARY KEY (component, version));
                CREATE TABLE rollover_operations (
                    operation_id TEXT PRIMARY KEY,
                    server_id TEXT NOT NULL,
                    from_season_id TEXT NOT NULL,
                    from_world_id TEXT NOT NULL,
                    target_world_id TEXT NOT NULL,
                    rules_version TEXT NOT NULL,
                    current_step TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    new_season_committed INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
                CREATE TABLE rollover_steps (
                    operation_id TEXT NOT NULL,
                    step TEXT NOT NULL,
                    step_key TEXT NOT NULL UNIQUE,
                    evidence_type TEXT NOT NULL,
                    evidence_reference TEXT NOT NULL,
                    evidence_hash TEXT NOT NULL,
                    actor TEXT NOT NULL,
                    completed_at TEXT NOT NULL,
                    PRIMARY KEY (operation_id, step));
                INSERT INTO economy_schema_migrations(component, version, applied_at)
                VALUES ('weekly-rollover-state-machine', 1, $appliedAt);
                """;
            command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _ = new WeeklyRolloverStateStore(directory);
        await using var migrated = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await migrated.OpenAsync(cancellationToken);
        await using (var columns = migrated.CreateCommand())
        {
            columns.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('rollover_steps')
                WHERE name = 'evidence_payload_hash';
                """;
            Assert(Convert.ToInt64(await columns.ExecuteScalarAsync(cancellationToken)) == 1,
                "N-1 rollover schema did not add evidence_payload_hash");
        }
        await using (var marker = migrated.CreateCommand())
        {
            marker.CommandText = """
                SELECT COUNT(*) FROM economy_schema_migrations
                WHERE component = 'weekly-rollover-state-machine' AND version = 2;
                """;
            Assert(Convert.ToInt64(await marker.ExecuteScalarAsync(cancellationToken)) == 1,
                "N-1 rollover schema migration marker v2 is missing");
        }
    });
}

static async Task VerifyObservabilityMigrationSchemaGuardAsync(
    CancellationToken cancellationToken)
{
    await WithDirectoryAsync(async directory =>
    {
        using var repository = new SqliteExtractionRepository(directory);
        var database = Path.Combine(directory, "extraction-commerce.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS economy_schema_migrations (
                    component TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    applied_at TEXT NOT NULL,
                    PRIMARY KEY (component, version));
                DROP TABLE IF EXISTS economy_identity_conflicts;
                INSERT OR REPLACE INTO economy_schema_migrations(component, version, applied_at)
                VALUES ('economy-observability', 1, $appliedAt);
                """;
            command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var verify = typeof(EconomyContinuityService).GetMethod(
            "VerifyRequiredSqliteSchema",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Continuity schema verifier was not found.");
        var userVersion = 0;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            userVersion = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken));
        }
        Assert(!(bool)(verify.Invoke(null, [database, userVersion]) ?? true),
            "continuity staging accepted economy-observability migration without its table");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE economy_identity_conflicts (
                    conflict_id TEXT PRIMARY KEY,
                    observed_at TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        Assert((bool)(verify.Invoke(null, [database, userVersion]) ?? false),
            "continuity staging rejected economy-observability migration with its table present");
    });
}

static async Task VerifySeasonJobsAsync(CancellationToken cancellationToken)
{
    await WithDirectoryAsync(async directory =>
    {
        const string world = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        using var repository = new SqliteExtractionRepository(directory);
        var now = DateTimeOffset.UtcNow;
        var season = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local", "week-1", "Week 1", world,
                now.AddDays(-1), now.AddDays(6), ExtractionSeasonState.Active),
            null,
            cancellationToken);
        var first = await repository.GetOrCreateAccountAsync(
            "steam", "steam-1", "First", cancellationToken);
        var second = await repository.GetOrCreateAccountAsync(
            "steam", "steam-2", "Second", cancellationToken);
        await GrantAsync(repository, first.AccountId, season.SeasonId, 300, "seed-1", cancellationToken);
        await GrantAsync(repository, second.AccountId, season.SeasonId, 125, "seed-2", cancellationToken);

        var closed = new ExtractionOperationGateState(
            true, "weekly rollover", "test", DateTimeOffset.UtcNow);
        var store = new SeasonSettlementJobStore(directory);
        var service = new SeasonSettlementJobService(repository, store, TimeProvider.System);
        var expiry = await service.PrepareVoucherExpiryAsync(
            season.SeasonId, "rules-v1", "test", closed, 0, cancellationToken);
        Assert(expiry.Items.Count == 2 && expiry.Items.Sum(item => item.Delta) == -425,
            "expiry did not freeze every positive SeasonVoucher balance");

        // Inject death after the authoritative ledger write but before the job item update.
        var crashItem = expiry.Items[0];
        var crashWrite = await repository.AdjustWalletAsync(
            new WalletAdjustmentRequest(
                crashItem.AccountId,
                crashItem.TargetSeasonId,
                crashItem.Currency,
                crashItem.Delta,
                crashItem.Reason,
                crashItem.ReferenceType,
                crashItem.ReferenceId,
                $"season-job:{expiry.JobId:N}",
                crashItem.IdempotencyKey),
            cancellationToken);
        Assert(crashWrite.Created, "crash-window ledger setup was not created");

        for (var replay = 0; replay < 20; replay++)
        {
            store = new SeasonSettlementJobStore(directory);
            service = new SeasonSettlementJobService(repository, store, TimeProvider.System);
            var completed = await service.RunAsync(expiry.JobId, closed, 0, cancellationToken);
            Assert(completed.State == SeasonSettlementJobState.Completed,
                "expiry did not remain completed across repeated restarts");
        }
        Assert((await repository.GetWalletAsync(first.AccountId, season.SeasonId, cancellationToken))
                .SeasonVoucher.Balance == 0,
            "first SeasonVoucher balance did not expire to zero");
        Assert((await repository.GetWalletAsync(second.AccountId, season.SeasonId, cancellationToken))
                .SeasonVoucher.Balance == 0,
            "second SeasonVoucher balance did not expire to zero");
        foreach (var item in expiry.Items)
        {
            var matching = (await repository.GetLedgerAsync(
                    item.AccountId, season.SeasonId, 1000, cancellationToken))
                .Count(entry => entry.ReferenceType == item.ReferenceType &&
                    entry.ReferenceId == item.ReferenceId);
            Assert(matching == 1, "expiry replay created duplicate ledger entries");
        }
        await AssertThrowsAsync<InvalidOperationException>(() => service.PrepareVoucherExpiryAsync(
            season.SeasonId, "rules-drift", "test", closed, 0, cancellationToken),
            "expiry accepted a rules-version drift after freezing");

        var reward = await service.PrepareRewardAsync(
            season.SeasonId,
            "rules-v1",
            "weekly-top-two",
            [
                new SeasonRewardGrant(first.AccountId, ExtractionCurrency.MarketCoin, null, 50, "rank-1"),
                new SeasonRewardGrant(second.AccountId, ExtractionCurrency.MarketCoin, null, 25, "rank-2")
            ],
            "test",
            closed,
            0,
            cancellationToken);
        for (var replay = 0; replay < 20; replay++)
        {
            store = new SeasonSettlementJobStore(directory);
            service = new SeasonSettlementJobService(repository, store, TimeProvider.System);
            var completed = await service.RunAsync(reward.JobId, closed, 0, cancellationToken);
            Assert(completed.State == SeasonSettlementJobState.Completed,
                "reward did not remain completed across repeated restarts");
        }
        Assert((await repository.GetWalletAsync(first.AccountId, season.SeasonId, cancellationToken))
                .MarketCoin.Balance == 50,
            "first weekly reward was duplicated or omitted");
        Assert((await repository.GetWalletAsync(second.AccountId, season.SeasonId, cancellationToken))
                .MarketCoin.Balance == 25,
            "second weekly reward was duplicated or omitted");
        await AssertThrowsAsync<InvalidOperationException>(() => service.PrepareRewardAsync(
            season.SeasonId,
            "rules-v1",
            "weekly-top-two",
            [new SeasonRewardGrant(first.AccountId, ExtractionCurrency.MarketCoin, null, 999, "rank-1")],
            "test",
            closed,
            0,
            cancellationToken), "reward accepted conflicting content under a deterministic job key");
    });
}

static async Task VerifyContinuitySnapshotAsync(CancellationToken cancellationToken)
{
    await WithDirectoryAsync(async root =>
    {
        var commands = Path.Combine(root, "data");
        var economy = Path.Combine(commands, "extraction");
        var backups = Path.Combine(root, "backups");
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(economy);
        Directory.CreateDirectory(commands);
        var commandId = Guid.NewGuid();
        var commandHash = Hash("continuity-command");
        await WriteCommandAuditAsync(
            Path.Combine(commands, "paldefender-command-audit.jsonl"),
            [
                CommandEvent(commandId, "accepted", "snapshot-command-key", commandHash),
                CommandEvent(commandId, "succeeded", "snapshot-command-key", commandHash)
            ],
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(commands, "announcement-events.jsonl"),
            JsonSerializer.Serialize(new
            {
                eventId = Guid.NewGuid(),
                eventType = "created",
                at = DateTimeOffset.UtcNow,
                serverId = "local",
                announcementId = Guid.NewGuid(),
                idempotencyKey = "announcement-snapshot-key",
                requestHash = Hash("announcement-snapshot"),
                state = "draft"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n",
            cancellationToken);
        foreach (var sideStateName in new[]
                 {
                     "command-audit.jsonl",
                     "in-game-notification-command-audit.jsonl",
                     "save-command-audit.jsonl"
                 })
        {
            var sideStateCommandId = Guid.NewGuid();
            var sideStateHash = Hash(sideStateName);
            await WriteCommandAuditAsync(
                Path.Combine(commands, sideStateName),
                [
                    CommandEvent(sideStateCommandId, "accepted", sideStateName, sideStateHash),
                    CommandEvent(sideStateCommandId, "succeeded", sideStateName, sideStateHash)
                ],
                cancellationToken);
        }
        await File.WriteAllTextAsync(
            Path.Combine(commands, "in-game-notification-events.jsonl"),
            JsonSerializer.Serialize(new
            {
                eventId = Guid.NewGuid(),
                eventType = "created",
                at = DateTimeOffset.UtcNow,
                serverId = "local",
                notificationId = Guid.NewGuid(),
                state = "draft"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n",
            cancellationToken);
        const string world = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        using var repository = new SqliteExtractionRepository(economy);
        var now = DateTimeOffset.UtcNow;
        var season = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local", "week-snapshot", "Snapshot Week", world,
                now.AddDays(-1), now.AddDays(6), ExtractionSeasonState.Active),
            null,
            cancellationToken);
        var account = await repository.GetOrCreateAccountAsync(
            "steam", "snapshot-user", "Snapshot User", cancellationToken);
        await GrantAsync(repository, account.AccountId, season.SeasonId, 20, "snapshot-seed", cancellationToken);
        var continuity = new EconomyContinuityService(
            new EconomyContinuityOptions
            {
                BackupRoot = backups,
                StagingRoot = staging,
                RetentionDays = 60,
                MinimumRetainedBackups = 8,
                MinimumFreeSpaceBytes = 16_777_216,
                CapacitySafetyPercent = 100,
                RpoMinutes = 15,
                TargetRtoMinutes = 60
            },
            economy,
            commands,
            root,
            TimeProvider.System);
        var closed = new ExtractionOperationGateState(
            true, "snapshot", "test", DateTimeOffset.UtcNow);
        Assert(continuity.GetCapacityPlan().CurrentAuthoritativeBytes == DirectoryBytes(commands),
            "capacity planning double-counted nested command and economy data roots");
        Assert(continuity.IsWithinRpo(DateTimeOffset.UtcNow.AddMinutes(-14)) &&
               !continuity.IsWithinRpo(DateTimeOffset.UtcNow.AddMinutes(-16)) &&
               !continuity.IsWithinRpo(DateTimeOffset.UtcNow.AddMinutes(2)),
            "backup freshness did not enforce the configured RPO window and future-clock bound");
        var raced = false;
        var racingContinuity = new EconomyContinuityService(
            new EconomyContinuityOptions
            {
                BackupRoot = backups,
                StagingRoot = staging,
                RetentionDays = 60,
                MinimumRetainedBackups = 8,
                MinimumFreeSpaceBytes = 16_777_216,
                CapacitySafetyPercent = 100,
                RpoMinutes = 15,
                TargetRtoMinutes = 60
            },
            economy,
            commands,
            root,
            TimeProvider.System,
            point =>
            {
                if (point == EconomyContinuityFaultPoint.AfterSqliteBackup && !raced)
                {
                    raced = true;
                    File.AppendAllText(
                        Path.Combine(commands, "paldefender-command-audit.jsonl"),
                        CommandEvent(
                            commandId,
                            "succeeded",
                            "snapshot-command-key",
                            commandHash) + "\n");
                }
            });
        await AssertThrowsAsync<IOException>(() => racingContinuity.CreateSnapshotAsync(
            "local",
            world,
            closed,
            0,
            "cross-store-race-snapshot",
            cancellationToken), "snapshot accepted command state that changed across the SQLite backup boundary");
        Assert(!Directory.EnumerateDirectories(backups, ".partial-*", SearchOption.AllDirectories).Any(),
            "cross-store consistency failure left a partial snapshot");
        foreach (var faultPoint in new[]
                 {
                     EconomyContinuityFaultPoint.AfterEconomySideStateCopy,
                     EconomyContinuityFaultPoint.AfterSqliteBackup,
                     EconomyContinuityFaultPoint.AfterCommandSideStateCopy,
                     EconomyContinuityFaultPoint.AfterManifestWrite
                 })
        {
            var faultKey = $"fault-injection-{faultPoint}-snapshot";
            var faulted = new EconomyContinuityService(
                new EconomyContinuityOptions
                {
                    BackupRoot = backups,
                    StagingRoot = staging,
                    RetentionDays = 60,
                    MinimumRetainedBackups = 8,
                    MinimumFreeSpaceBytes = 16_777_216,
                    CapacitySafetyPercent = 100,
                    RpoMinutes = 15,
                    TargetRtoMinutes = 60
                },
                economy,
                commands,
                root,
                TimeProvider.System,
                point =>
                {
                    if (point == faultPoint)
                    {
                        throw new InjectedFaultException($"continuity:{point}");
                    }
                });
            await AssertThrowsAsync<InjectedFaultException>(() => faulted.CreateSnapshotAsync(
                "local",
                world,
                closed,
                0,
                faultKey,
                cancellationToken), $"snapshot did not surface injected fault at {faultPoint}");
            Assert(!Directory.Exists(backups) ||
                   !Directory.EnumerateDirectories(backups, ".partial-*", SearchOption.AllDirectories).Any(),
                $"snapshot left a partial directory after {faultPoint}");
            var recovered = await continuity.CreateSnapshotAsync(
                "local", world, closed, 0, faultKey, cancellationToken);
            Assert(continuity.VerifySnapshot("local", recovered.BackupId).ContentHash ==
                   recovered.ContentHash,
                $"snapshot did not recover with the same idempotency key after {faultPoint}");
        }
        var activeSideStateBeforeArchive = Directory
            .EnumerateFiles(commands, "*.jsonl", SearchOption.TopDirectoryOnly)
            .ToDictionary(
                path => Path.GetFileName(path)!,
                path => FileHash(path),
                StringComparer.OrdinalIgnoreCase);
        var manifest = await continuity.CreateSnapshotAsync(
            "local", world, closed, 0,
            "weekly-rollover-step-economy-backup-0001",
            cancellationToken);
        var idempotentReplay = await continuity.CreateSnapshotAsync(
            "local", world, closed, 0,
            "weekly-rollover-step-economy-backup-0001",
            cancellationToken);
        Assert(idempotentReplay.BackupId == manifest.BackupId &&
               idempotentReplay.ContentHash == manifest.ContentHash,
            "snapshot idempotency key created a duplicate backup");
        Assert(manifest.SqliteUserVersion == 1,
            "snapshot did not capture the versioned SQLite schema");
        Assert(manifest.Files.Any(file => file.Role.Contains("jsonl-", StringComparison.Ordinal)),
            "snapshot omitted registered command/outbox side state");
        Assert(manifest.Files.Any(file => string.Equals(
                file.RelativePath,
                "command-state/announcement-events.jsonl",
                StringComparison.Ordinal)),
            "snapshot omitted announcement side state");
        Assert(manifest.Files.Count(file =>
                file.RelativePath.EndsWith("extraction-commerce.db", StringComparison.OrdinalIgnoreCase)) == 1,
            "nested command/economy roots duplicated the authoritative SQLite database as side state");
        var archiveManifestEntry = manifest.Files.Single(file => string.Equals(
            file.Role,
            CommandSideStateArchiveService.ManifestRole,
            StringComparison.Ordinal));
        var archivedCommandRoot = Path.GetDirectoryName(Path.Combine(
            backups,
            "local",
            manifest.BackupId,
            archiveManifestEntry.RelativePath))!;
        var sideStateArchive = new CommandSideStateArchiveService().Verify(archivedCommandRoot);
        Assert(sideStateArchive.RetentionDays == 60 &&
               sideStateArchive.MinimumRetainedArchives == 8 &&
               sideStateArchive.ArchiveMode == CommandSideStateArchiveService.ArchiveMode &&
               sideStateArchive.ActiveLogMutationPolicy ==
                   CommandSideStateArchiveService.ActiveLogMutationPolicy,
            "command side-state archive did not freeze the inherited retention and no-mutation policy");
        var requiredActiveChannels = new[]
        {
            "announcement-state",
            "announcement-delivery",
            "in-game-notification-state",
            "in-game-notification-delivery",
            "save-command-delivery"
        };
        Assert(requiredActiveChannels.All(channel => sideStateArchive.Files.Any(file =>
                file.Channel == channel &&
                file.Authority == "non-economic-authoritative" &&
                file.Present && file.Bytes > 0 && file.Sha256?.Length == 64)),
            "command side-state archive omitted a registered non-economic authoritative channel");
        foreach (var archived in sideStateArchive.Files.Where(file => file.Present))
        {
            Assert(activeSideStateBeforeArchive.TryGetValue(
                       archived.RelativePath,
                       out var activeHashBeforeArchive) &&
                   activeHashBeforeArchive == FileHash(Path.Combine(commands, archived.RelativePath)),
                $"archiving mutated the active side-state log for {archived.Channel}");
            Assert(FileHash(Path.Combine(commands, archived.RelativePath)) ==
                   FileHash(Path.Combine(archivedCommandRoot, archived.RelativePath)),
                $"archiving changed active side-state bytes for {archived.Channel}");
        }
        Assert(manifest.PendingTransactions.All(item => item.Kind != "command_outbox"),
            "terminal command state was incorrectly classified as pending");
        Assert(continuity.VerifySnapshot("local", manifest.BackupId).ContentHash == manifest.ContentHash,
            "fresh snapshot verification changed the content hash");
        var archivedAnnouncementPath = Path.Combine(
            archivedCommandRoot,
            "announcement-events.jsonl");
        var archivedAnnouncementBytes = await File.ReadAllBytesAsync(
            archivedAnnouncementPath,
            cancellationToken);
        await File.AppendAllTextAsync(
            archivedAnnouncementPath,
            "{\"eventId\":\"00000000-0000-0000-0000-000000000000\"}\n",
            cancellationToken);
        AssertThrows<InvalidDataException>(
            () => continuity.VerifySnapshot("local", manifest.BackupId),
            "tampered command side-state archive passed outer and inner SHA-256 verification");
        await File.WriteAllBytesAsync(
            archivedAnnouncementPath,
            archivedAnnouncementBytes,
            cancellationToken);
        var manifestPath = Path.Combine(backups, "local", manifest.BackupId, "manifest.json");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var manifestJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        var legacyManifest = manifest with
        {
            SchemaVersion = 1,
            ContentHash = LegacySnapshotHash(manifest)
        };
        await File.WriteAllBytesAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(legacyManifest, manifestJsonOptions),
            cancellationToken);
        Assert(continuity.VerifySnapshot("local", manifest.BackupId).SchemaVersion == 1,
            "N-1 snapshot manifest compatibility was not preserved");
        var tamperedManifest = manifest with { RpoMinutes = manifest.RpoMinutes + 1 };
        await File.WriteAllBytesAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(tamperedManifest, manifestJsonOptions),
            cancellationToken);
        AssertThrows<InvalidDataException>(
            () => continuity.VerifySnapshot("local", manifest.BackupId),
            "v2 manifest accepted tampered recovery policy metadata");
        await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);

        foreach (var faultPoint in new[]
                 {
                     EconomyContinuityFaultPoint.AfterStagingFilesCopied,
                     EconomyContinuityFaultPoint.BeforeStagingPublish
                 })
        {
            var faulted = new EconomyContinuityService(
                new EconomyContinuityOptions
                {
                    BackupRoot = backups,
                    StagingRoot = staging,
                    RetentionDays = 60,
                    MinimumRetainedBackups = 8,
                    MinimumFreeSpaceBytes = 16_777_216,
                    CapacitySafetyPercent = 100,
                    RpoMinutes = 15,
                    TargetRtoMinutes = 60
                },
                economy,
                commands,
                root,
                TimeProvider.System,
                point =>
                {
                    if (point == faultPoint)
                    {
                        throw new InjectedFaultException($"restore:{point}");
                    }
                });
            await AssertThrowsAsync<InjectedFaultException>(() => faulted.RestoreToStagingAsync(
                "local", manifest.BackupId, world, cancellationToken),
                $"staging restore did not surface injected fault at {faultPoint}");
            Assert(!Directory.Exists(staging) ||
                   !Directory.EnumerateDirectories(staging, ".partial-*", SearchOption.AllDirectories).Any(),
                $"staging restore left a partial directory after {faultPoint}");
        }
        var restored = await continuity.RestoreToStagingAsync(
            "local", manifest.BackupId, world, cancellationToken);
        Assert(restored.HashesValid && restored.SqliteIntegrityValid && restored.EconomyReplayValid,
            "staging restore did not pass hash/integrity/replay checks");
        Assert(restored.WorldIdValid && restored.ActiveSeasonWorldValid && restored.LedgerProjectionValid,
            "staging restore did not validate world/active-season/ledger consistency");
        Assert(restored.SqliteSchemaValid && restored.ForeignKeysValid &&
               restored.CommandReplayValid && restored.CommandIdempotencyValid &&
               restored.PendingCommandCount == 0 && restored.PendingStateMatchesManifest,
            "staging restore did not validate schema, queue replay, idempotency, and pending-state parity");
        Assert(restored.EconomyForcedClosed && File.Exists(Path.Combine(
                restored.StagingDirectory, "economy-state", "extraction-operation-gate.json")),
            "restored economy was not forced closed for revalidation");
        using (var staged = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = Path.Combine(
                       restored.StagingDirectory, "economy-state", "extraction-commerce.db"),
                   Mode = SqliteOpenMode.ReadOnly,
                   Pooling = false
               }.ToString()))
        {
            staged.Open();
            using var gateState = staged.CreateCommand();
            gateState.CommandText = """
                SELECT json_extract(state_json, '$.maintenance')
                FROM economy_gate_state WHERE gate_key = 'operation';
                """;
            Assert(Convert.ToInt64(gateState.ExecuteScalar()) == 1,
                "restored authoritative SQLite operation gate was not forced closed");
            using var circuits = staged.CreateCommand();
            circuits.CommandText = """
                SELECT json_extract(state_json, '$.purchase.writesEnabled'),
                       json_extract(state_json, '$.resourceExchange.writesEnabled')
                FROM economy_gate_state WHERE gate_key = 'safety';
                """;
            using var circuitReader = circuits.ExecuteReader();
            Assert(circuitReader.Read() && circuitReader.GetInt64(0) == 0 &&
                   circuitReader.GetInt64(1) == 0,
                "restored authoritative SQLite safety circuits were not forced closed");
        }
        var replayedStage = await continuity.RestoreToStagingAsync(
            "local", manifest.BackupId, world, cancellationToken);
        Assert(replayedStage.BackupId == restored.BackupId &&
               replayedStage.ContentHash == restored.ContentHash &&
               replayedStage.EconomyForcedClosed &&
               replayedStage.PendingStateMatchesManifest,
            "same-backup staging replay was not revalidated idempotently");

        await GrantAsync(repository, account.AccountId, season.SeasonId, 1, "after-snapshot", cancellationToken);
        var reconciliation = continuity.ListPostSnapshotTransactions("local", manifest.BackupId);
        Assert(reconciliation.Any(item => item.Kind == "economy_event"),
            "post-snapshot transaction reconciliation omitted newer economy events");
        Assert(continuity.GetCapacityPlan().CapacitySufficient,
            "test continuity capacity plan was unexpectedly insufficient");
        Assert(continuity.PlanRetention(DateTimeOffset.UtcNow).Count == 0,
            "retention planner proposed deleting a fresh protected backup");

        var retentionBackups = Path.Combine(root, "retention-backups");
        var retentionContinuity = new EconomyContinuityService(
            new EconomyContinuityOptions
            {
                BackupRoot = retentionBackups,
                StagingRoot = Path.Combine(root, "retention-staging"),
                RetentionDays = 7,
                MinimumRetainedBackups = 2,
                MinimumFreeSpaceBytes = 16_777_216,
                CapacitySafetyPercent = 100,
                RpoMinutes = 15,
                TargetRtoMinutes = 60
            },
            economy,
            commands,
            root,
            new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(-8)));
        for (var index = 0; index < 3; index++)
        {
            _ = await retentionContinuity.CreateSnapshotAsync(
                "local",
                world,
                closed,
                0,
                $"side-state-retention-{index}",
                cancellationToken);
        }
        var retentionCandidates = retentionContinuity.PlanRetention(DateTimeOffset.UtcNow);
        Assert(retentionCandidates.Count == 1,
            "side-state archive retention did not preserve the newest minimum bundle count");
        var retainedCandidate = retentionContinuity.VerifySnapshot(
            "local",
            retentionCandidates.Single().BackupId);
        Assert(retainedCandidate.Files.Any(file => string.Equals(
                   file.Role,
                   CommandSideStateArchiveService.ManifestRole,
                   StringComparison.Ordinal)) &&
               Directory.Exists(Path.Combine(
                   retentionBackups,
                   "local",
                   retentionCandidates.Single().BackupId)),
            "retention planning separated a JSONL archive from its outer snapshot or deleted it automatically");

        var database = Path.Combine(
            backups, "local", manifest.BackupId, "economy-state", "extraction-commerce.db");
        await using (var stream = new FileStream(database, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = Math.Min(128, stream.Length - 1);
            stream.WriteByte(0xCC);
            await stream.FlushAsync(cancellationToken);
        }
        AssertThrows<InvalidDataException>(
            () => continuity.VerifySnapshot("local", manifest.BackupId),
            "corrupt snapshot passed SHA-256/integrity verification");
    });
}

static async Task VerifySharedDataRootArchiveAsync(CancellationToken cancellationToken)
{
    await WithDirectoryAsync(async root =>
    {
        var data = Path.Combine(root, "data");
        var backups = Path.Combine(root, "backups");
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(data);
        const string world = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
        using var repository = new SqliteExtractionRepository(data);
        var now = DateTimeOffset.UtcNow;
        _ = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local",
                "week-shared-root",
                "Shared Root Week",
                world,
                now.AddDays(-1),
                now.AddDays(6),
                ExtractionSeasonState.Active),
            null,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(data, "announcement-events.jsonl"),
            JsonSerializer.Serialize(new
            {
                eventId = Guid.NewGuid(),
                eventType = "created",
                at = now,
                serverId = "local",
                announcementId = Guid.NewGuid(),
                state = "draft"
            }) + "\n",
            cancellationToken);
        var continuity = new EconomyContinuityService(
            new EconomyContinuityOptions
            {
                BackupRoot = backups,
                StagingRoot = staging,
                RetentionDays = 60,
                MinimumRetainedBackups = 8,
                MinimumFreeSpaceBytes = 16_777_216,
                CapacitySafetyPercent = 100,
                RpoMinutes = 15,
                TargetRtoMinutes = 60
            },
            data,
            data,
            root,
            TimeProvider.System);
        var closed = new ExtractionOperationGateState(
            true,
            "shared data root archive",
            "test",
            now);
        var manifest = await continuity.CreateSnapshotAsync(
            "local",
            world,
            closed,
            0,
            "shared-data-root-snapshot",
            cancellationToken);
        var archiveEntry = manifest.Files.Single(file => string.Equals(
            file.Role,
            CommandSideStateArchiveService.ManifestRole,
            StringComparison.Ordinal));
        Assert(archiveEntry.RelativePath ==
               $"economy-state/{CommandSideStateArchiveService.ManifestFileName}",
            "shared command/economy root did not place the JSONL archive manifest beside economy state");
        Assert(continuity.VerifySnapshot("local", manifest.BackupId).ContentHash ==
               manifest.ContentHash,
            "shared command/economy root snapshot did not verify");
        var restored = await continuity.RestoreToStagingAsync(
            "local",
            manifest.BackupId,
            world,
            cancellationToken);
        Assert(restored.CommandReplayValid && restored.CommandIdempotencyValid &&
               File.Exists(Path.Combine(
                   restored.StagingDirectory,
                   "economy-state",
                   CommandSideStateArchiveService.ManifestFileName)),
            "shared command/economy root did not restore and verify its JSONL archive");
    });
}

static async Task VerifyCommandSideStateFailureClosedAsync(CancellationToken cancellationToken)
{
    await WithDirectoryAsync(async root =>
    {
        var economy = Path.Combine(root, "economy");
        var commands = Path.Combine(root, "commands");
        var backups = Path.Combine(root, "backups");
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(economy);
        Directory.CreateDirectory(commands);
        const string world = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        using var repository = new SqliteExtractionRepository(economy);
        var now = DateTimeOffset.UtcNow;
        _ = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local", "week-command-replay", "Command Replay Week", world,
                now.AddDays(-1), now.AddDays(6), ExtractionSeasonState.Active),
            null,
            cancellationToken);
        var commandPath = Path.Combine(commands, "paldefender-command-audit.jsonl");
        var firstCommand = Guid.NewGuid();
        var firstHash = Hash("pending-command");
        await WriteCommandAuditAsync(
            commandPath,
            [CommandEvent(firstCommand, "accepted", "stable-command-key", firstHash)],
            cancellationToken);
        var continuity = new EconomyContinuityService(
            new EconomyContinuityOptions
            {
                BackupRoot = backups,
                StagingRoot = staging,
                RetentionDays = 60,
                MinimumRetainedBackups = 8,
                MinimumFreeSpaceBytes = 16_777_216,
                CapacitySafetyPercent = 100,
                RpoMinutes = 15,
                TargetRtoMinutes = 60
            },
            economy,
            commands,
            root,
            TimeProvider.System);
        var closed = new ExtractionOperationGateState(
            true, "command replay snapshot", "test", DateTimeOffset.UtcNow);
        var manifest = await continuity.CreateSnapshotAsync(
            "local",
            world,
            closed,
            0,
            "pending-command-replay-snapshot",
            cancellationToken);
        Assert(manifest.PendingTransactions.Any(item =>
                item.Kind.StartsWith("command_outbox:", StringComparison.Ordinal) &&
                item.Id == firstCommand.ToString("D") && item.State == "accepted"),
            "snapshot did not classify an accepted PalDefender outbox command as blocking");
        var restored = await continuity.RestoreToStagingAsync(
            "local", manifest.BackupId, world, cancellationToken);
        Assert(restored.CommandReplayValid && restored.CommandIdempotencyValid &&
               restored.PendingCommandCount == 1 && restored.PendingStateMatchesManifest,
            "staging restore did not preserve pending queue and idempotency state");
        var backedUpCommand = Path.Combine(
            backups,
            "local",
            manifest.BackupId,
            "command-state",
            "paldefender-command-audit.jsonl");
        var stagedCommand = Path.Combine(
            restored.StagingDirectory,
            "command-state",
            "paldefender-command-audit.jsonl");
        Assert(FileHash(backedUpCommand) == FileHash(stagedCommand),
            "staging restore changed the durable command queue bytes");

        var unregistered = Path.Combine(commands, "unregistered-side-state.jsonl");
        await File.WriteAllTextAsync(
            unregistered,
            JsonSerializer.Serialize(new { eventId = Guid.NewGuid() }) + "\n",
            cancellationToken);
        await AssertThrowsAsync<InvalidDataException>(() => continuity.CreateSnapshotAsync(
            "local",
            world,
            closed,
            0,
            "unregistered-side-state-snapshot",
            cancellationToken), "snapshot silently archived an unregistered JSONL channel");
        File.Delete(unregistered);
        Assert(!Directory.EnumerateDirectories(backups, ".partial-*", SearchOption.AllDirectories).Any(),
            "unregistered JSONL failure left a partial snapshot");

        var nestedSideStateDirectory = Path.Combine(commands, "nested");
        Directory.CreateDirectory(nestedSideStateDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(nestedSideStateDirectory, "announcement-events.jsonl"),
            JsonSerializer.Serialize(new { eventId = Guid.NewGuid() }) + "\n",
            cancellationToken);
        await AssertThrowsAsync<InvalidDataException>(() => continuity.CreateSnapshotAsync(
            "local",
            world,
            closed,
            0,
            "nested-side-state-snapshot",
            cancellationToken), "snapshot treated a nested JSONL as a registered root channel");
        Directory.Delete(nestedSideStateDirectory, recursive: true);
        Assert(!Directory.EnumerateDirectories(backups, ".partial-*", SearchOption.AllDirectories).Any(),
            "nested JSONL failure left a partial snapshot");

        var conflictingCommand = Guid.NewGuid();
        await WriteCommandAuditAsync(
            commandPath,
            [
                CommandEvent(firstCommand, "accepted", "stable-command-key", firstHash),
                CommandEvent(
                    conflictingCommand,
                    "accepted",
                    "stable-command-key",
                    Hash("conflicting-command"))
            ],
            cancellationToken);
        await AssertThrowsAsync<InvalidDataException>(() => continuity.CreateSnapshotAsync(
            "local",
            world,
            closed,
            0,
            "conflicting-command-snapshot",
            cancellationToken), "snapshot accepted a conflicting command idempotency mapping");
        Assert(!Directory.EnumerateDirectories(backups, ".partial-*", SearchOption.AllDirectories).Any(),
            "failed idempotency validation left a partial snapshot");

        await File.AppendAllTextAsync(commandPath, "{\"eventId\":", cancellationToken);
        await AssertThrowsAsync<InvalidDataException>(() => continuity.CreateSnapshotAsync(
            "local",
            world,
            closed,
            0,
            "partial-command-log-snapshot",
            cancellationToken), "snapshot accepted a partial command audit tail");
        Assert(!Directory.EnumerateDirectories(backups, ".partial-*", SearchOption.AllDirectories).Any(),
            "partial command audit failure left a partial snapshot");
    });
}

static async Task VerifyDeliveryEvidenceMigrationAsync(CancellationToken cancellationToken)
{
    await WithDirectoryAsync(async directory =>
    {
        var deliveryId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var legacy = new ExtractionDeliveryEvidence(
            deliveryId,
            capturedAt,
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["Leather"] = 10,
                ["Wood"] = 20
            });
        await File.WriteAllTextAsync(
            Path.Combine(directory, "delivery-inventory-evidence.json"),
            JsonSerializer.Serialize(new[] { legacy }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            cancellationToken);
        using var repository = new SqliteExtractionRepository(directory);
        var store = new ExtractionDeliveryEvidenceStore(directory);
        var imported = await store.GetAsync(deliveryId, cancellationToken);
        Assert(imported is not null && imported.BaselineItemTotals["Leather"] == 10,
            "legacy delivery evidence was not imported into SQLite");

        // Once the migration marker commits, later legacy-file edits are ignored.
        await File.WriteAllTextAsync(
            Path.Combine(directory, "delivery-inventory-evidence.json"),
            "[]",
            cancellationToken);
        store = new ExtractionDeliveryEvidenceStore(directory);
        Assert((await store.GetAsync(deliveryId, cancellationToken)) is not null,
            "legacy JSON replaced SQLite authority after migration");

        var commandId = Guid.NewGuid();
        _ = await store.AttachCommandAsync(deliveryId, commandId, cancellationToken);
        _ = await store.AttachCommandAsync(deliveryId, commandId, cancellationToken);
        await AssertThrowsAsync<InvalidOperationException>(() => store.AttachCommandAsync(
            deliveryId, Guid.NewGuid(), cancellationToken),
            "delivery evidence accepted a conflicting command");
        var verified = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["Leather"] = 12,
            ["Wood"] = 20
        };
        _ = await store.SaveVerificationAsync(
            deliveryId, commandId, verified, cancellationToken);
        _ = await store.SaveVerificationAsync(
            deliveryId, commandId, verified, cancellationToken);
        var refreshed = await store.SaveVerificationAsync(
            deliveryId,
            commandId,
            new Dictionary<string, long> { ["Leather"] = 999 },
            cancellationToken);
        Assert(refreshed.VerifiedItemTotals is not null &&
               refreshed.VerifiedItemTotals.GetValueOrDefault("Leather") == 999,
            "a later delivery verification sample did not replace the insufficient readback");
        await AssertThrowsAsync<InvalidOperationException>(() => store.ReplaceBaselineAsync(
            deliveryId,
            new Dictionary<string, long> { ["Leather"] = 1 },
            cancellationToken), "attached delivery baseline was replaceable");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM extraction_delivery_evidence;
            SELECT COUNT(*) FROM economy_schema_migrations
            WHERE component = 'delivery-evidence-legacy-json' AND version = 1;
            """;
        using var reader = command.ExecuteReader();
        Assert(reader.Read() && reader.GetInt64(0) == 1,
            "SQLite delivery evidence row count is incorrect");
        Assert(reader.NextResult() && reader.Read() && reader.GetInt64(0) == 1,
            "delivery evidence migration marker is missing");
    });
}

static WeeklyRolloverEvidence Evidence(WeeklyRolloverStep step, string targetWorld) => new(
    Verified: true,
    EvidenceType: "test-evidence",
    EvidenceReference: $"evidence-{step}",
    EvidenceHash: Hash($"evidence-{step}"),
    ObservedWorldId: step is WeeklyRolloverStep.Probe or WeeklyRolloverStep.Commit
        ? targetWorld
        : null,
    BlockingTransactions: 0,
    AllGatesPassed: step == WeeklyRolloverStep.Reopen,
    BlockerCodes: []);

static string CommandEvent(
    Guid commandId,
    string state,
    string idempotencyKey,
    string requestHash) => JsonSerializer.Serialize(
    new
    {
        eventId = Guid.NewGuid(),
        commandId,
        eventType = state,
        state,
        at = DateTimeOffset.UtcNow,
        serverId = "local",
        upstreamPath = "/api/give-item",
        idempotencyKey,
        requestHash,
        reason = "continuity test",
        actor = "test",
        body = new { itemId = "Leather", count = 1 }
    },
    new JsonSerializerOptions(JsonSerializerDefaults.Web));

static async Task WriteCommandAuditAsync(
    string path,
    IEnumerable<string> events,
    CancellationToken cancellationToken) => await File.WriteAllTextAsync(
        path,
        string.Join('\n', events) + "\n",
        cancellationToken);

static string FileHash(string path) => Convert.ToHexString(
    SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static string LegacySnapshotHash(EconomySnapshotManifest manifest)
{
    var builder = new StringBuilder()
        .Append("economy-snapshot-v1\n")
        .Append(manifest.ServerId).Append('\n')
        .Append(manifest.WorldId.ToUpperInvariant()).Append('\n')
        .Append(manifest.CreatedAt.ToString("O")).Append('\n')
        .Append(manifest.LastEconomySequence).Append('\n');
    if (manifest.IdempotencyKeyHash is not null)
    {
        builder.Append("idempotency:").Append(manifest.IdempotencyKeyHash).Append('\n');
    }
    foreach (var file in manifest.Files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
    {
        builder.Append(file.RelativePath).Append('\n')
            .Append(file.Role).Append('\n')
            .Append(file.Bytes).Append('\n')
            .Append(file.Sha256).Append('\n');
    }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
        .ToLowerInvariant();
}

static long DirectoryBytes(string path) => Directory.EnumerateFiles(
        path,
        "*",
        SearchOption.AllDirectories)
    .Sum(file => new FileInfo(file).Length);

static async Task GrantAsync(
    IExtractionRepository repository,
    Guid accountId,
    Guid seasonId,
    long amount,
    string reference,
    CancellationToken cancellationToken)
{
    var result = await repository.AdjustWalletAsync(
        new WalletAdjustmentRequest(
            accountId,
            seasonId,
            ExtractionCurrency.SeasonVoucher,
            amount,
            "test grant",
            "test",
            reference,
            "test",
            $"test-wallet-{reference}"),
        cancellationToken);
    Assert(result.ErrorCode is null, $"test wallet grant failed: {result.ErrorCode}");
}

static string Hash(string value) => Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static async Task WithDirectoryAsync(Func<string, Task> action)
{
    var directory = Path.Combine(
        Path.GetTempPath(), "pal-control-continuity-rollover", Guid.NewGuid().ToString("N"));
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
                await Task.Delay(25);
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

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
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

sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}

sealed class InjectedFaultException : Exception
{
    public InjectedFaultException(string message) : base(message)
    {
    }
}

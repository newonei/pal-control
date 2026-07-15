using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

[JsonConverter(typeof(JsonStringEnumConverter<WeeklyRolloverStep>))]
public enum WeeklyRolloverStep
{
    Preflight,
    Drain,
    GameBackup,
    EconomyBackup,
    Stop,
    NewWorld,
    Probe,
    Commit,
    Reopen,
    Completed
}

public sealed record WeeklyRolloverOperation(
    Guid OperationId,
    string ServerId,
    Guid FromSeasonId,
    string FromWorldId,
    string TargetWorldId,
    string RulesVersion,
    WeeklyRolloverStep CurrentStep,
    long Revision,
    bool NewSeasonCommitted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WeeklyRolloverStepRecord> CompletedSteps);

public sealed record WeeklyRolloverStepRecord(
    WeeklyRolloverStep Step,
    string StepKey,
    string EvidenceType,
    string EvidenceReference,
    string EvidenceHash,
    string? EvidencePayloadHash,
    string Actor,
    DateTimeOffset CompletedAt);

public sealed record WeeklyRolloverEvidence(
    bool Verified,
    string EvidenceType,
    string EvidenceReference,
    string EvidenceHash,
    string? ObservedWorldId = null,
    int BlockingTransactions = 0,
    bool AllGatesPassed = false,
    IReadOnlyList<string>? BlockerCodes = null);

public sealed record WeeklyRolloverTransition(
    WeeklyRolloverOperation Operation,
    bool Applied,
    bool IdempotentReplay,
    string StepKey);

public enum WeeklyRolloverFaultPoint
{
    AfterStepEvidencePersisted,
    AfterOperationAdvanced
}

public sealed class WeeklyRolloverStateStore
{
    private static readonly WeeklyRolloverStep[] OrderedSteps =
    [
        WeeklyRolloverStep.Preflight,
        WeeklyRolloverStep.Drain,
        WeeklyRolloverStep.GameBackup,
        WeeklyRolloverStep.EconomyBackup,
        WeeklyRolloverStep.Stop,
        WeeklyRolloverStep.NewWorld,
        WeeklyRolloverStep.Probe,
        WeeklyRolloverStep.Commit,
        WeeklyRolloverStep.Reopen,
        WeeklyRolloverStep.Completed
    ];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly Action<WeeklyRolloverFaultPoint, WeeklyRolloverStep>? _faultInjector;

    public WeeklyRolloverStateStore(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
        : this(ResolveDataDirectory(options.Value.DataDirectory, environment.ContentRootPath), timeProvider)
    {
    }

    public WeeklyRolloverStateStore(
        string dataDirectory,
        TimeProvider? timeProvider = null,
        Action<WeeklyRolloverFaultPoint, WeeklyRolloverStep>? faultInjector = null)
    {
        var fullPath = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fullPath, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _faultInjector = faultInjector;
        Initialize();
    }

    public async Task<WeeklyRolloverOperation> PrepareAsync(
        string serverId,
        Guid fromSeasonId,
        string fromWorldId,
        string targetWorldId,
        string rulesVersion,
        CancellationToken cancellationToken)
    {
        ValidateInputs(serverId, fromSeasonId, fromWorldId, targetWorldId, rulesVersion);
        var operationId = DeterministicOperationId(
            serverId,
            fromSeasonId,
            fromWorldId,
            targetWorldId,
            rulesVersion);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO rollover_operations (
                    operation_id, server_id, from_season_id, from_world_id,
                    target_world_id, rules_version, current_step, revision,
                    new_season_committed, created_at, updated_at)
                VALUES (
                    $operationId, $serverId, $fromSeasonId, $fromWorldId,
                    $targetWorldId, $rulesVersion, 'preflight', 0,
                    0, $createdAt, $updatedAt);
                """;
            command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            command.Parameters.AddWithValue("$serverId", serverId.Trim());
            command.Parameters.AddWithValue("$fromSeasonId", fromSeasonId.ToString("D"));
            command.Parameters.AddWithValue("$fromWorldId", fromWorldId.ToUpperInvariant());
            command.Parameters.AddWithValue("$targetWorldId", targetWorldId.ToUpperInvariant());
            command.Parameters.AddWithValue("$rulesVersion", rulesVersion.Trim());
            command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            var operation = await LoadAsync(connection, operationId, cancellationToken)
                ?? throw new InvalidOperationException("The prepared rollover could not be reloaded.");
            if (!PayloadMatches(operation, serverId, fromSeasonId, fromWorldId, targetWorldId, rulesVersion))
            {
                throw new InvalidOperationException(
                    "The deterministic rollover id is already bound to a different payload.");
            }
            return operation;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WeeklyRolloverOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            return await LoadAsync(connection, operationId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WeeklyRolloverOperation?> FindIncompleteAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (serverId.Trim().Length > 64)
        {
            throw new ArgumentException("Server id is too long.", nameof(serverId));
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT operation_id
                FROM rollover_operations
                WHERE server_id = $serverId AND current_step <> 'completed'
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$serverId", serverId.Trim());
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string operationId
                ? await LoadAsync(connection, Guid.Parse(operationId), cancellationToken)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WeeklyRolloverTransition> CompleteStepAsync(
        Guid operationId,
        WeeklyRolloverStep requestedStep,
        string suppliedStepKey,
        WeeklyRolloverEvidence evidence,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedStepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ValidateEvidence(requestedStep, evidence);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = connection.BeginTransaction();
            var operation = await LoadAsync(connection, operationId, cancellationToken, transaction)
                ?? throw new KeyNotFoundException($"Rollover '{operationId}' does not exist.");
            var expectedKey = StepKey(operationId, requestedStep);
            var evidencePayloadHash = ComputeEvidencePayloadHash(evidence);
            if (!FixedEquals(expectedKey, suppliedStepKey))
            {
                throw new InvalidOperationException("The deterministic rollover step key is invalid.");
            }

            var existing = operation.CompletedSteps.FirstOrDefault(item => item.Step == requestedStep);
            if (existing is not null)
            {
                if (!string.Equals(existing.StepKey, expectedKey, StringComparison.Ordinal) ||
                    !string.Equals(existing.EvidenceType, evidence.EvidenceType, StringComparison.Ordinal) ||
                    !string.Equals(existing.EvidenceReference, evidence.EvidenceReference, StringComparison.Ordinal) ||
                    !string.Equals(existing.EvidenceHash, evidence.EvidenceHash, StringComparison.OrdinalIgnoreCase) ||
                    (existing.EvidencePayloadHash is { Length: > 0 } &&
                     !FixedEquals(existing.EvidencePayloadHash, evidencePayloadHash)))
                {
                    throw new InvalidOperationException(
                        "The completed rollover step was replayed with conflicting evidence.");
                }
                await transaction.RollbackAsync(cancellationToken);
                return new WeeklyRolloverTransition(operation, false, true, expectedKey);
            }
            if (operation.CurrentStep != requestedStep)
            {
                throw new InvalidOperationException(
                    $"Rollover step '{requestedStep}' cannot run while '{operation.CurrentStep}' is required.");
            }
            if (requestedStep is WeeklyRolloverStep.Probe or WeeklyRolloverStep.Commit &&
                !string.Equals(
                    operation.TargetWorldId,
                    evidence.ObservedWorldId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Probe/commit evidence belongs to a world other than this rollover target.");
            }
            if (requestedStep == WeeklyRolloverStep.Completed)
            {
                throw new InvalidOperationException("Completed is a terminal projection, not an executable step.");
            }

            var now = _timeProvider.GetUtcNow();
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO rollover_steps (
                        operation_id, step, step_key, evidence_type,
                        evidence_reference, evidence_hash, evidence_payload_hash,
                        actor, completed_at)
                    VALUES (
                        $operationId, $step, $stepKey, $evidenceType,
                        $evidenceReference, $evidenceHash, $evidencePayloadHash,
                        $actor, $completedAt);
                    """;
                insert.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
                insert.Parameters.AddWithValue("$step", ToStorage(requestedStep));
                insert.Parameters.AddWithValue("$stepKey", expectedKey);
                insert.Parameters.AddWithValue("$evidenceType", evidence.EvidenceType.Trim());
                insert.Parameters.AddWithValue("$evidenceReference", evidence.EvidenceReference.Trim());
                insert.Parameters.AddWithValue("$evidenceHash", evidence.EvidenceHash.ToLowerInvariant());
                insert.Parameters.AddWithValue("$evidencePayloadHash", evidencePayloadHash);
                insert.Parameters.AddWithValue("$actor", actor.Trim());
                insert.Parameters.AddWithValue("$completedAt", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            _faultInjector?.Invoke(
                WeeklyRolloverFaultPoint.AfterStepEvidencePersisted,
                requestedStep);

            var nextStep = Next(requestedStep);
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE rollover_operations
                    SET current_step = $nextStep,
                        revision = revision + 1,
                        new_season_committed = CASE
                            WHEN $completedStep = 'commit' THEN 1
                            ELSE new_season_committed END,
                        updated_at = $updatedAt
                    WHERE operation_id = $operationId
                      AND current_step = $expectedStep
                      AND revision = $expectedRevision;
                    """;
                update.Parameters.AddWithValue("$nextStep", ToStorage(nextStep));
                update.Parameters.AddWithValue("$completedStep", ToStorage(requestedStep));
                update.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
                update.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
                update.Parameters.AddWithValue("$expectedStep", ToStorage(requestedStep));
                update.Parameters.AddWithValue("$expectedRevision", operation.Revision);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException("The rollover state changed concurrently.");
                }
            }
            _faultInjector?.Invoke(
                WeeklyRolloverFaultPoint.AfterOperationAdvanced,
                requestedStep);
            await transaction.CommitAsync(cancellationToken);
            var updated = await LoadAsync(connection, operationId, cancellationToken)
                ?? throw new InvalidOperationException("The advanced rollover could not be reloaded.");
            return new WeeklyRolloverTransition(updated, true, false, expectedKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string StepKey(Guid operationId, WeeklyRolloverStep step)
    {
        var payload = $"weekly-rollover-step-v1|{operationId:D}|{ToStorage(step)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    private static string ComputeEvidencePayloadHash(WeeklyRolloverEvidence evidence)
    {
        var blockerCodes = evidence.BlockerCodes ?? [];
        var payload = new StringBuilder()
            .Append("weekly-rollover-evidence-v1\n")
            .Append(evidence.Verified ? '1' : '0').Append('\n')
            .Append(evidence.EvidenceType.Trim()).Append('\n')
            .Append(evidence.EvidenceReference.Trim()).Append('\n')
            .Append(evidence.EvidenceHash.ToLowerInvariant()).Append('\n')
            .Append(evidence.ObservedWorldId?.Trim().ToUpperInvariant() ?? string.Empty).Append('\n')
            .Append(evidence.BlockingTransactions).Append('\n')
            .Append(evidence.AllGatesPassed ? '1' : '0').Append('\n');
        foreach (var blocker in blockerCodes
                     .Select(item => item.Trim())
                     .OrderBy(item => item, StringComparer.Ordinal))
        {
            payload.Append(blocker).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())))
            .ToLowerInvariant();
    }

    public static Guid DeterministicOperationId(
        string serverId,
        Guid fromSeasonId,
        string fromWorldId,
        string targetWorldId,
        string rulesVersion)
    {
        var payload = string.Join('|',
            "weekly-rollover-v1",
            serverId.Trim().ToLowerInvariant(),
            fromSeasonId.ToString("D"),
            fromWorldId.ToUpperInvariant(),
            targetWorldId.ToUpperInvariant(),
            rulesVersion.Trim());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS economy_schema_migrations (
                component TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                applied_at TEXT NOT NULL,
                PRIMARY KEY (component, version)
            );
            CREATE TABLE IF NOT EXISTS rollover_operations (
                operation_id TEXT PRIMARY KEY,
                server_id TEXT NOT NULL,
                from_season_id TEXT NOT NULL,
                from_world_id TEXT NOT NULL CHECK (length(from_world_id) = 32),
                target_world_id TEXT NOT NULL CHECK (length(target_world_id) = 32),
                rules_version TEXT NOT NULL,
                current_step TEXT NOT NULL CHECK (current_step IN (
                    'preflight', 'drain', 'game_backup', 'economy_backup',
                    'stop', 'new_world', 'probe', 'commit', 'reopen', 'completed')),
                revision INTEGER NOT NULL CHECK (revision >= 0),
                new_season_committed INTEGER NOT NULL CHECK (new_season_committed IN (0, 1)),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_rollover_one_incomplete_per_server
                ON rollover_operations (server_id)
                WHERE current_step <> 'completed';
            CREATE TABLE IF NOT EXISTS rollover_steps (
                operation_id TEXT NOT NULL,
                step TEXT NOT NULL,
                step_key TEXT NOT NULL UNIQUE CHECK (length(step_key) = 64),
                evidence_type TEXT NOT NULL,
                evidence_reference TEXT NOT NULL,
                evidence_hash TEXT NOT NULL CHECK (length(evidence_hash) = 64),
                evidence_payload_hash TEXT NOT NULL CHECK (length(evidence_payload_hash) = 64),
                actor TEXT NOT NULL,
                completed_at TEXT NOT NULL,
                PRIMARY KEY (operation_id, step),
                FOREIGN KEY (operation_id) REFERENCES rollover_operations(operation_id)
            );
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('weekly-rollover-state-machine', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
        EnsureEvidencePayloadHashColumn();
    }

    private void EnsureEvidencePayloadHashColumn()
    {
        using var connection = Open();
        var exists = false;
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(rollover_steps);";
            using var reader = columns.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(
                        reader.GetString(1),
                        "evidence_payload_hash",
                        StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (!exists)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText =
                "ALTER TABLE rollover_steps ADD COLUMN evidence_payload_hash TEXT NULL;";
            alter.ExecuteNonQuery();
        }
        using var migration = connection.CreateCommand();
        migration.CommandText = """
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('weekly-rollover-state-machine', 2, $appliedAt);
            """;
        migration.Parameters.AddWithValue("$appliedAt", _timeProvider.GetUtcNow().ToString("O"));
        migration.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static async Task<WeeklyRolloverOperation?> LoadAsync(
        SqliteConnection connection,
        Guid operationId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT server_id, from_season_id, from_world_id, target_world_id,
                   rules_version, current_step, revision, new_season_committed,
                   created_at, updated_at
            FROM rollover_operations
            WHERE operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var serverId = reader.GetString(0);
        var fromSeasonId = Guid.Parse(reader.GetString(1));
        var fromWorldId = reader.GetString(2);
        var targetWorldId = reader.GetString(3);
        var rulesVersion = reader.GetString(4);
        var currentStep = FromStorage(reader.GetString(5));
        var revision = reader.GetInt64(6);
        var committed = reader.GetInt32(7) == 1;
        var createdAt = DateTimeOffset.Parse(reader.GetString(8));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(9));
        await reader.DisposeAsync();

        var steps = new List<WeeklyRolloverStepRecord>();
        await using var stepCommand = connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            SELECT step, step_key, evidence_type, evidence_reference,
                   evidence_hash, evidence_payload_hash, actor, completed_at
            FROM rollover_steps
            WHERE operation_id = $operationId
            ORDER BY rowid;
            """;
        stepCommand.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        await using var stepReader = await stepCommand.ExecuteReaderAsync(cancellationToken);
        while (await stepReader.ReadAsync(cancellationToken))
        {
            steps.Add(new WeeklyRolloverStepRecord(
                FromStorage(stepReader.GetString(0)),
                stepReader.GetString(1),
                stepReader.GetString(2),
                stepReader.GetString(3),
                stepReader.GetString(4),
                stepReader.IsDBNull(5) ? null : stepReader.GetString(5),
                stepReader.GetString(6),
                DateTimeOffset.Parse(stepReader.GetString(7))));
        }
        return new WeeklyRolloverOperation(
            operationId,
            serverId,
            fromSeasonId,
            fromWorldId,
            targetWorldId,
            rulesVersion,
            currentStep,
            revision,
            committed,
            createdAt,
            updatedAt,
            steps);
    }

    private static void ValidateEvidence(WeeklyRolloverStep step, WeeklyRolloverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.Verified || string.IsNullOrWhiteSpace(evidence.EvidenceType) ||
            string.IsNullOrWhiteSpace(evidence.EvidenceReference) ||
            evidence.EvidenceType.Length > 64 || evidence.EvidenceReference.Length > 256 ||
            evidence.EvidenceType.Any(char.IsControl) ||
            evidence.EvidenceReference.Any(char.IsControl) ||
            evidence.EvidenceHash.Length != 64 || !evidence.EvidenceHash.All(Uri.IsHexDigit) ||
            evidence.ObservedWorldId is { Length: > 64 } ||
            evidence.ObservedWorldId?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("Each rollover step requires bounded, verified SHA-256 evidence.");
        }
        if (evidence.BlockerCodes is { Count: > 0 } || evidence.BlockingTransactions != 0)
        {
            throw new InvalidOperationException("A rollover step cannot complete while blockers remain.");
        }
        if (step is WeeklyRolloverStep.Probe or WeeklyRolloverStep.Commit)
        {
            ValidateWorldId(evidence.ObservedWorldId);
        }
        if (step == WeeklyRolloverStep.Reopen && !evidence.AllGatesPassed)
        {
            throw new InvalidOperationException("Reopen requires every economy safety gate to pass again.");
        }
    }

    private static void ValidateInputs(
        string serverId,
        Guid fromSeasonId,
        string fromWorldId,
        string targetWorldId,
        string rulesVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesVersion);
        if (serverId.Length > 64 || rulesVersion.Length is < 1 or > 128 || fromSeasonId == Guid.Empty)
        {
            throw new ArgumentException("The rollover identity or rules version is invalid.");
        }
        ValidateWorldId(fromWorldId);
        ValidateWorldId(targetWorldId);
        if (string.Equals(fromWorldId, targetWorldId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A rollover target world must differ from the current world.");
        }
    }

    private static void ValidateWorldId(string? worldId)
    {
        if (worldId is not { Length: 32 } || !worldId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Rollover world IDs must be complete 32-character hexadecimal values.");
        }
    }

    private static bool PayloadMatches(
        WeeklyRolloverOperation operation,
        string serverId,
        Guid fromSeasonId,
        string fromWorldId,
        string targetWorldId,
        string rulesVersion) =>
        string.Equals(operation.ServerId, serverId.Trim(), StringComparison.Ordinal) &&
        operation.FromSeasonId == fromSeasonId &&
        string.Equals(operation.FromWorldId, fromWorldId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(operation.TargetWorldId, targetWorldId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(operation.RulesVersion, rulesVersion.Trim(), StringComparison.Ordinal);

    private static WeeklyRolloverStep Next(WeeklyRolloverStep step)
    {
        var index = Array.IndexOf(OrderedSteps, step);
        if (index < 0 || index + 1 >= OrderedSteps.Length)
        {
            throw new InvalidOperationException("The rollover step has no valid successor.");
        }
        return OrderedSteps[index + 1];
    }

    private static string ToStorage(WeeklyRolloverStep step) => step switch
    {
        WeeklyRolloverStep.Preflight => "preflight",
        WeeklyRolloverStep.Drain => "drain",
        WeeklyRolloverStep.GameBackup => "game_backup",
        WeeklyRolloverStep.EconomyBackup => "economy_backup",
        WeeklyRolloverStep.Stop => "stop",
        WeeklyRolloverStep.NewWorld => "new_world",
        WeeklyRolloverStep.Probe => "probe",
        WeeklyRolloverStep.Commit => "commit",
        WeeklyRolloverStep.Reopen => "reopen",
        WeeklyRolloverStep.Completed => "completed",
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };

    private static WeeklyRolloverStep FromStorage(string value) => value switch
    {
        "preflight" => WeeklyRolloverStep.Preflight,
        "drain" => WeeklyRolloverStep.Drain,
        "game_backup" => WeeklyRolloverStep.GameBackup,
        "economy_backup" => WeeklyRolloverStep.EconomyBackup,
        "stop" => WeeklyRolloverStep.Stop,
        "new_world" => WeeklyRolloverStep.NewWorld,
        "probe" => WeeklyRolloverStep.Probe,
        "commit" => WeeklyRolloverStep.Commit,
        "reopen" => WeeklyRolloverStep.Reopen,
        "completed" => WeeklyRolloverStep.Completed,
        _ => throw new InvalidDataException($"Unknown rollover step '{value}'.")
    };

    private static bool FixedEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var suppliedBytes = Encoding.ASCII.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));
}

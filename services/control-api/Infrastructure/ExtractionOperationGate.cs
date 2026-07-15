using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed record ExtractionOperationGateState(
    bool Maintenance,
    string Reason,
    string Actor,
    DateTimeOffset UpdatedAt);

public sealed class ExtractionOperationGate
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly string _legacyPath;
    private readonly string _connectionString;
    private ExtractionOperationGateState _state;
    private int _activeOperations;
    private TaskCompletionSource<bool> _drained = CompletedDrain();

    public ExtractionOperationGate(
        IOptions<ExtractionPersistenceOptions> options,
        IWebHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var directory = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(directory);
        _legacyPath = Path.Combine(directory, "extraction-operation-gate.json");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        Initialize();
        ImportLegacyOnce();
        var persisted = Load();
        _state = persisted ?? new ExtractionOperationGateState(
            false,
            "Normal operation",
            "system-bootstrap",
            DateTimeOffset.UtcNow);
        if (persisted is null)
        {
            Persist(_state);
        }
    }

    public ExtractionOperationGateState Current
    {
        get
        {
            lock (_sync)
            {
                return _state with { };
            }
        }
    }

    public int ActiveOperationCount
    {
        get
        {
            lock (_sync)
            {
                return _activeOperations;
            }
        }
    }

    public IDisposable AcquireOperation()
    {
        lock (_sync)
        {
            ThrowIfMaintenance(_state);
            if (_activeOperations == 0)
            {
                _drained = NewDrain();
            }
            _activeOperations = checked(_activeOperations + 1);
            return new OperationLease(this);
        }
    }

    public void ThrowIfClosed()
    {
        lock (_sync)
        {
            ThrowIfMaintenance(_state);
        }
    }

    public async Task<ExtractionOperationGateState> SetAsync(
        bool maintenance,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (reason.Trim().Length is < 3 or > 500 || reason.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Maintenance reason must contain 3 to 500 non-control characters.",
                nameof(reason));
        }
        if (actor.Trim().Length > 256 || actor.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Maintenance actor must contain at most 256 non-control characters.",
                nameof(actor));
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ExtractionOperationGateState previous;
            lock (_sync)
            {
                previous = _state;
            }
            var updated = new ExtractionOperationGateState(
                maintenance,
                reason.Trim(),
                actor.Trim(),
                DateTimeOffset.UtcNow);

            if (maintenance)
            {
                lock (_sync)
                {
                    _state = updated;
                }
                try
                {
                    await PersistAsync(updated, cancellationToken);
                }
                catch
                {
                    lock (_sync)
                    {
                        _state = previous;
                    }
                    throw;
                }

                Task drainTask;
                lock (_sync)
                {
                    drainTask = _activeOperations == 0
                        ? Task.CompletedTask
                        : _drained.Task;
                }
                await drainTask.WaitAsync(cancellationToken);
            }
            else
            {
                // Reopening is published only after durable persistence succeeds.
                await PersistAsync(updated, cancellationToken);
                lock (_sync)
                {
                    _state = updated;
                }
            }
            return updated with { };
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReleaseOperation()
    {
        TaskCompletionSource<bool>? completed = null;
        lock (_sync)
        {
            if (_activeOperations <= 0)
            {
                throw new InvalidOperationException("Extraction operation lease count underflowed.");
            }
            _activeOperations--;
            if (_activeOperations == 0)
            {
                completed = _drained;
            }
        }
        completed?.TrySetResult(true);
    }

    private static void ThrowIfMaintenance(ExtractionOperationGateState state)
    {
        if (state.Maintenance)
        {
            throw new ExtractionModeException(
                "EXTRACTION_MAINTENANCE",
                $"The resource economy is in rollover maintenance: {state.Reason}",
                StatusCodes.Status423Locked);
        }
    }

    private static TaskCompletionSource<bool> NewDrain() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CompletedDrain()
    {
        var source = NewDrain();
        source.SetResult(true);
        return source;
    }

    private sealed class OperationLease : IDisposable
    {
        private ExtractionOperationGate? _owner;

        public OperationLease(ExtractionOperationGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
        }
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS economy_schema_migrations (
                component TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                applied_at TEXT NOT NULL,
                PRIMARY KEY (component, version)
            );
            CREATE TABLE IF NOT EXISTS economy_gate_state (
                gate_key TEXT PRIMARY KEY CHECK (gate_key IN ('operation', 'safety')),
                state_json TEXT NOT NULL CHECK (json_valid(state_json)),
                revision INTEGER NOT NULL CHECK (revision > 0),
                updated_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO economy_schema_migrations (component, version, applied_at)
            VALUES ('economy-gate-state', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void ImportLegacyOnce()
    {
        using var connection = Open();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = """
                SELECT 1 FROM economy_schema_migrations
                WHERE component = 'operation-gate-legacy-json' AND version = 1;
                """;
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }
        ExtractionOperationGateState? legacy = null;
        if (File.Exists(_legacyPath))
        {
            legacy = JsonSerializer.Deserialize<ExtractionOperationGateState>(
                File.ReadAllBytes(_legacyPath), JsonOptions)
                ?? throw new InvalidDataException("The legacy extraction operation gate is invalid.");
            ValidateState(legacy);
        }
        using var transaction = connection.BeginTransaction();
        if (legacy is not null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO economy_gate_state (
                    gate_key, state_json, revision, updated_at)
                VALUES ('operation', $state, 1, $updatedAt);
                """;
            insert.Parameters.AddWithValue("$state", JsonSerializer.Serialize(legacy, JsonOptions));
            insert.Parameters.AddWithValue("$updatedAt", legacy.UpdatedAt.ToString("O"));
            insert.ExecuteNonQuery();
        }
        using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = """
                INSERT INTO economy_schema_migrations (component, version, applied_at)
                VALUES ('operation-gate-legacy-json', 1, $appliedAt);
                """;
            marker.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            marker.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private ExtractionOperationGateState? Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state_json FROM economy_gate_state WHERE gate_key = 'operation';
            """;
        return command.ExecuteScalar() is string json ? DeserializeState(json) : null;
    }

    private async Task PersistAsync(
        ExtractionOperationGateState state,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = CreateUpsert(connection, state);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void Persist(ExtractionOperationGateState state)
    {
        using var connection = Open();
        using var command = CreateUpsert(connection, state);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand CreateUpsert(
        SqliteConnection connection,
        ExtractionOperationGateState state)
    {
        ValidateState(state);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO economy_gate_state (gate_key, state_json, revision, updated_at)
            VALUES ('operation', $state, 1, $updatedAt)
            ON CONFLICT(gate_key) DO UPDATE SET
                state_json = excluded.state_json,
                revision = economy_gate_state.revision + 1,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state, JsonOptions));
        command.Parameters.AddWithValue("$updatedAt", state.UpdatedAt.ToString("O"));
        return command;
    }

    private static ExtractionOperationGateState DeserializeState(string json)
    {
        var state = JsonSerializer.Deserialize<ExtractionOperationGateState>(json, JsonOptions)
            ?? throw new InvalidDataException("The extraction operation gate is invalid.");
        ValidateState(state);
        return state;
    }

    private static void ValidateState(ExtractionOperationGateState state)
    {
        if (string.IsNullOrWhiteSpace(state.Reason) || state.Reason.Length > 500 ||
            state.Reason.Any(char.IsControl) || string.IsNullOrWhiteSpace(state.Actor) ||
            state.Actor.Length > 256 || state.Actor.Any(char.IsControl))
        {
            throw new InvalidDataException("The extraction operation gate state is invalid.");
        }
    }
}

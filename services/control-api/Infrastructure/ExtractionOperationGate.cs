using System.Text.Json;
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
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly string _path;
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
        _path = Path.Combine(directory, "extraction-operation-gate.json");
        _state = Load() ?? new ExtractionOperationGateState(
            false,
            "正常运营",
            "system-bootstrap",
            DateTimeOffset.UtcNow);
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
        if (reason.Trim().Length is < 3 or > 500 || reason.Any(char.IsControl))
        {
            throw new ArgumentException("Maintenance reason must contain 3 to 500 non-control characters.", nameof(reason));
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
                actor,
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
                $"摸金经济已进入换档维护：{state.Reason}",
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

    private ExtractionOperationGateState? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        var bytes = File.ReadAllBytes(_path);
        return JsonSerializer.Deserialize<ExtractionOperationGateState>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The extraction operation gate is invalid.");
    }

    private async Task PersistAsync(
        ExtractionOperationGateState state,
        CancellationToken cancellationToken)
    {
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}

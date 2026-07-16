using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public interface IExtractionSettlementExecutor
{
    Task<ExtractionSettlementRun> ExecuteSettlementAsync(
        Guid runId,
        string userId,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed class ExtractionSettlementQueue : BackgroundService
{
    private readonly Channel<SettlementWorkItem> _channel;
    private readonly ConcurrentDictionary<string, SettlementWorkItem> _activePlayers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _admission;
    private readonly IExtractionSettlementExecutor _executor;
    private readonly ExtractionOperationGate _operationGate;
    private readonly EconomySafetyGate? _economySafety;
    private readonly ILogger<ExtractionSettlementQueue> _logger;
    private readonly int _workerCount;
    private readonly TimeSpan _operationTimeout;
    private int _accepting = 1;
    private int _admittedCount;

    public ExtractionSettlementQueue(
        IExtractionSettlementExecutor executor,
        ExtractionOperationGate operationGate,
        IOptions<ExtractionModeOptions> options,
        ILogger<ExtractionSettlementQueue> logger,
        EconomySafetyGate? economySafety = null)
    {
        _executor = executor;
        _operationGate = operationGate;
        _economySafety = economySafety;
        _logger = logger;
        var value = options.Value;
        Capacity = value.SettlementQueueCapacity;
        _workerCount = value.SettlementWorkerCount;
        _operationTimeout = TimeSpan.FromSeconds(
            value.SettlementQueueOperationTimeoutSeconds);
        _admission = new SemaphoreSlim(Capacity, Capacity);
        _channel = Channel.CreateBounded<SettlementWorkItem>(new BoundedChannelOptions(Capacity)
        {
            SingleWriter = false,
            SingleReader = _workerCount == 1,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public int Capacity { get; }

    public int AdmittedCount => Volatile.Read(ref _admittedCount);

    public bool IsAccepting => Volatile.Read(ref _accepting) != 0;

    public async Task<ExtractionSettlementRun> EnqueueAsync(
        Guid runId,
        string userId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var playerKey = userId.Trim();

        if (_economySafety is not null)
        {
            await _economySafety.RequireAsync(
                EconomyWriteFeature.ResourceExchange,
                EconomySafetyContext.ForDelivery(playerKey),
                new EconomyQueueSnapshot(
                    Volatile.Read(ref _accepting) != 0,
                    AdmittedCount,
                    Capacity),
                cancellationToken);
        }

        while (true)
        {
            if (_activePlayers.TryGetValue(playerKey, out var existing))
            {
                if (existing.RunId == runId && string.Equals(
                        existing.IdempotencyKey,
                        idempotencyKey,
                        StringComparison.Ordinal))
                {
                    return await existing.Completion.Task.WaitAsync(cancellationToken);
                }
                throw new ExtractionModeException(
                    "PLAYER_SETTLEMENT_IN_PROGRESS",
                    "该玩家已有资源兑换正在排队或执行，请等待完成后再试。",
                    StatusCodes.Status409Conflict);
            }
            if (Volatile.Read(ref _accepting) == 0 || !_admission.Wait(0))
            {
                throw QueueFull();
            }

            var work = new SettlementWorkItem(
                runId,
                playerKey,
                idempotencyKey,
                new TaskCompletionSource<ExtractionSettlementRun>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            if (!_activePlayers.TryAdd(playerKey, work))
            {
                _admission.Release();
                continue;
            }
            Interlocked.Increment(ref _admittedCount);
            if (!_channel.Writer.TryWrite(work))
            {
                CompleteWork(
                    work,
                    exception: QueueFull());
            }
            return await work.Completion.Task.WaitAsync(cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _workerCount)
            .Select(workerId => RunWorkerAsync(workerId, stoppingToken))
            .ToArray();
        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            Interlocked.Exchange(ref _accepting, 0);
            var stopped = new IOException("Resource settlement queue stopped before the request was executed.");
            while (_channel.Reader.TryRead(out var pending))
            {
                CompleteWork(pending, exception: stopped);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _accepting, 0);
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                using var scope = ControlPlaneLog.BeginWorker(
                    _logger,
                    nameof(ExtractionSettlementQueue),
                    "settlement.execute",
                    work.RunId,
                    subjectFingerprint: PlayerIdentitySecurityStore.FingerprintSubject(work.UserId));
                try
                {
                    using var operationLease = _operationGate.AcquireOperation();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(_operationTimeout);
                    var result = await _executor.ExecuteSettlementAsync(
                        work.RunId,
                        work.UserId,
                        work.IdempotencyKey,
                        timeout.Token);
                    CompleteWork(work, result: result);
                }
                catch (Exception exception)
                {
                    _logger.LogSafeWarning(
                        exception,
                        "Settlement queue worker {WorkerId} failed run {RunId} for player fingerprint {PlayerFingerprint}.",
                        workerId,
                        work.RunId,
                        PlayerIdentitySecurityStore.FingerprintSubject(work.UserId));
                    CompleteWork(work, exception: exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown; the outer finally fails requests that were not read.
        }
    }

    private void CompleteWork(
        SettlementWorkItem work,
        ExtractionSettlementRun? result = null,
        Exception? exception = null)
    {
        if (!_activePlayers.TryRemove(
                new KeyValuePair<string, SettlementWorkItem>(work.UserId, work)))
        {
            return;
        }
        Interlocked.Decrement(ref _admittedCount);
        _admission.Release();
        if (exception is not null)
        {
            work.Completion.TrySetException(exception);
        }
        else if (result is not null)
        {
            work.Completion.TrySetResult(result);
        }
        else
        {
            work.Completion.TrySetException(
                new InvalidOperationException("Settlement work completed without a result."));
        }
    }

    private static ExtractionModeException QueueFull() => new(
        "SETTLEMENT_QUEUE_FULL",
        "资源兑换队列已满，服务器正在处理其他玩家的请求，请稍后重试。",
        StatusCodes.Status429TooManyRequests);

    private sealed record SettlementWorkItem(
        Guid RunId,
        string UserId,
        string IdempotencyKey,
        TaskCompletionSource<ExtractionSettlementRun> Completion);
}

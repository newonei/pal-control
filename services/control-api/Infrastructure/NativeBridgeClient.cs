using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public sealed class NativeBridgeOptions
{
    public string PipeName { get; init; } = "pal-control.local.v1";
    public int ConnectTimeoutSeconds { get; init; } = 3;
    public int CommandTimeoutSeconds { get; init; } = 5;
    public int MaxFrameBytes { get; init; } = 1_048_576;
}

public sealed record NativeBridgeSnapshot(
    bool Connected,
    string? ProtocolVersion,
    string? GameBuild,
    string? ModVersion,
    IReadOnlySet<string> Capabilities,
    IReadOnlyDictionary<string, bool> Probes,
    DateTimeOffset? LastSeenAt,
    string? LastError);

public sealed class NativeBridgeState
{
    private readonly object _gate = new();
    private NativeBridgeSnapshot _snapshot = new(
        Connected: false,
        ProtocolVersion: null,
        GameBuild: null,
        ModVersion: null,
        Capabilities: new HashSet<string>(StringComparer.Ordinal),
        Probes: new Dictionary<string, bool>(StringComparer.Ordinal),
        LastSeenAt: null,
        LastError: "PAL_MOD_BRIDGE_NOT_CONNECTED");

    public NativeBridgeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public void OnHello(NativeBridgeHello hello)
    {
        lock (_gate)
        {
            _snapshot = new NativeBridgeSnapshot(
                Connected: true,
                ProtocolVersion: hello.ProtocolVersion,
                GameBuild: hello.GameBuild,
                ModVersion: hello.ModVersion,
                Capabilities: new HashSet<string>(hello.Capabilities, StringComparer.Ordinal),
                Probes: new Dictionary<string, bool>(hello.Probes, StringComparer.Ordinal),
                LastSeenAt: DateTimeOffset.UtcNow,
                LastError: null);
        }
    }

    public void Touch()
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Connected = true,
                LastSeenAt = DateTimeOffset.UtcNow,
                LastError = null
            };
        }
    }

    public void Disconnect(string error)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Connected = false,
                LastError = error
            };
        }
    }
}

public interface INativeBridgeCommandTransport
{
    Task<NativeBridgeResult> SendCommandAsync(
        string serverId,
        string operation,
        object payload,
        string reason,
        CancellationToken cancellationToken,
        long expectedRevision = 0,
        string? idempotencyKey = null);
}

public sealed class NativeBridgeClient : BackgroundService, INativeBridgeCommandTransport
{
    private readonly NativeBridgeOptions _options;
    private readonly NativeBridgeState _state;
    private readonly ILogger<NativeBridgeClient> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<NativeBridgeResult>> _pending = new();
    private NamedPipeClientStream? _activePipe;

    public NativeBridgeClient(
        IOptions<NativeBridgeOptions> options,
        NativeBridgeState state,
        ILogger<NativeBridgeClient> logger)
    {
        _options = options.Value;
        _state = state;
        _logger = logger;
    }

    public async Task<NativeBridgeResult> SendCommandAsync(
        string serverId,
        string operation,
        object payload,
        string reason,
        CancellationToken cancellationToken,
        long expectedRevision = 0,
        string? idempotencyKey = null)
    {
        var pipe = Volatile.Read(ref _activePipe);
        if (pipe is null || !pipe.IsConnected)
        {
            throw new IOException("Native Mod bridge is not connected.");
        }

        var commandId = Guid.NewGuid();
        using var scope = ControlPlaneLog.BeginAdapter(
            _logger,
            nameof(NativeBridgeClient),
            operation,
            commandId,
            serverId);
        var payloadJson = JsonSerializer.Serialize(payload);
        var requestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var envelope = new
        {
            protocolVersion = "1.0",
            messageType = "command",
            messageId = Guid.NewGuid(),
            sentAt = DateTimeOffset.UtcNow,
            commandId,
            idempotencyKey = idempotencyKey ?? $"native-command-{commandId:N}",
            requestHash,
            serverId,
            actorId = "control-api",
            operation,
            deadline = DateTimeOffset.UtcNow.AddSeconds(
                Math.Clamp(_options.CommandTimeoutSeconds, 1, 30)),
            expectedRevision,
            reason,
            payload
        };

        var completion = new TaskCompletionSource<NativeBridgeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(commandId, completion))
        {
            throw new InvalidOperationException("Unable to register Native Mod command.");
        }

        try
        {
            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(envelope), cancellationToken);
            return await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(Math.Clamp(_options.CommandTimeoutSeconds, 1, 30)),
                cancellationToken);
        }
        finally
        {
            _pending.TryRemove(commandId, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = ControlPlaneLog.BeginWorker(
                _logger,
                nameof(NativeBridgeClient),
                "native-bridge.connection");
            try
            {
                await RunConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or UnauthorizedAccessException)
            {
                _state.Disconnect(exception.GetType().Name);
                _logger.LogSafeDebug(exception, "Native bridge connection ended.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken stoppingToken)
    {
        await using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _options.PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Clamp(_options.ConnectTimeoutSeconds, 1, 30)));

        try
        {
            await pipe.ConnectAsync(connectTimeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out connecting to the Native Mod pipe.");
        }

        _logger.LogInformation(
            "Connected to Native Mod pipe {PipeFingerprint}.",
            ControlPlaneLog.Fingerprint(_options.PipeName));

        Volatile.Write(ref _activePipe, pipe);
        try
        {
            while (!stoppingToken.IsCancellationRequested && pipe.IsConnected)
            {
                var payload = await ReadFrameAsync(pipe, stoppingToken);
                ProcessMessage(payload);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _activePipe, null, pipe);
            FailPending(new IOException("Native Mod bridge connection ended."));
        }
    }

    private async Task WriteFrameAsync(
        NamedPipeClientStream pipe,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length == 0 ||
            payload.Length > Math.Clamp(_options.MaxFrameBytes, 1024, 16_777_216))
        {
            throw new IOException($"Invalid Native Bridge outbound frame length: {payload.Length}.");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var frame = new byte[sizeof(uint) + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
            payload.CopyTo(frame, sizeof(uint));
            await pipe.WriteAsync(frame, CancellationToken.None);
            await pipe.FlushAsync(CancellationToken.None);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);

        if (length == 0 || length > Math.Clamp(_options.MaxFrameBytes, 1024, 16_777_216))
        {
            throw new IOException($"Invalid Native Bridge frame length: {length}.");
        }

        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private void ProcessMessage(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("messageType", out var messageType))
        {
            throw new IOException("Native Bridge messageType is missing.");
        }

        switch (messageType.GetString())
        {
            case "hello":
                var hello = document.RootElement.Deserialize<NativeBridgeHello>();
                if (hello is null || hello.ProtocolVersion != "1.0")
                {
                    throw new IOException("Native Bridge protocol is incompatible.");
                }
                _state.OnHello(hello);
                break;
            case "heartbeat":
                _state.Touch();
                break;
            case "result":
                var result = document.RootElement.Deserialize<NativeBridgeResult>();
                if (result is null)
                {
                    throw new IOException("Native Bridge result is invalid.");
                }
                _state.Touch();
                if (_pending.TryRemove(result.CommandId, out var completion))
                {
                    completion.TrySetResult(result);
                }
                break;
            default:
                _logger.LogDebug(
                    "Ignoring unsupported Native Bridge message type {MessageTypeFingerprint}.",
                    ControlPlaneLog.Fingerprint(messageType.GetString()));
                break;
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }
}

public sealed record NativeBridgeHello(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("gameBuild")] string GameBuild,
    [property: JsonPropertyName("modVersion")] string ModVersion,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("probes")] IReadOnlyDictionary<string, bool> Probes);

public sealed record NativeBridgeResult(
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("observedRevision")] long ObservedRevision,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("error")] NativeBridgeError? Error);

public sealed record NativeBridgeError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

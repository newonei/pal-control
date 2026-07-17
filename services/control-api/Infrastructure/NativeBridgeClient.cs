using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
    string? SteamBuild,
    string? ModVersion,
    string? RuntimeExecutableSha256,
    long? RuntimeExecutableSize,
    string? RuntimeExecutablePath,
    string? RuntimeProcessSid,
    string? RuntimeNativeDllSha256,
    long? RuntimeNativeDllSize,
    string? RuntimeUe4ssDllSha256,
    long? RuntimeUe4ssDllSize,
    bool RuntimeIdentityVerified,
    bool WriteEnabled,
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
        SteamBuild: null,
        ModVersion: null,
        RuntimeExecutableSha256: null,
        RuntimeExecutableSize: null,
        RuntimeExecutablePath: null,
        RuntimeProcessSid: null,
        RuntimeNativeDllSha256: null,
        RuntimeNativeDllSize: null,
        RuntimeUe4ssDllSha256: null,
        RuntimeUe4ssDllSize: null,
        RuntimeIdentityVerified: false,
        WriteEnabled: false,
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

    public void OnHello(NativeBridgeHello hello) =>
        OnHello(hello, runtimeExecutablePath: null, runtimeProcessSid: null);

    public void OnHello(
        NativeBridgeHello hello,
        string? runtimeExecutablePath,
        string? runtimeProcessSid)
    {
        NativeBridgeProtocol.ValidateHello(hello);
        lock (_gate)
        {
            _snapshot = new NativeBridgeSnapshot(
                Connected: true,
                ProtocolVersion: hello.ProtocolVersion,
                GameBuild: hello.GameBuild,
                SteamBuild: hello.SteamBuild,
                ModVersion: hello.ModVersion,
                RuntimeExecutableSha256: hello.RuntimeExecutableSha256,
                RuntimeExecutableSize: hello.RuntimeExecutableSize,
                RuntimeExecutablePath: runtimeExecutablePath,
                RuntimeProcessSid: runtimeProcessSid,
                RuntimeNativeDllSha256: hello.RuntimeNativeDllSha256,
                RuntimeNativeDllSize: hello.RuntimeNativeDllSize,
                RuntimeUe4ssDllSha256: hello.RuntimeUe4ssDllSha256,
                RuntimeUe4ssDllSize: hello.RuntimeUe4ssDllSize,
                RuntimeIdentityVerified: hello.RuntimeIdentityVerified,
                WriteEnabled: hello.WriteEnabled,
                Capabilities: new HashSet<string>(hello.Capabilities, StringComparer.Ordinal),
                Probes: new Dictionary<string, bool>(hello.Probes, StringComparer.Ordinal),
                LastSeenAt: DateTimeOffset.UtcNow,
                LastError: null);
        }
    }

    public bool Touch()
    {
        lock (_gate)
        {
            if (!_snapshot.Connected || !_snapshot.RuntimeIdentityVerified)
            {
                return false;
            }
            _snapshot = _snapshot with
            {
                LastSeenAt = DateTimeOffset.UtcNow,
                LastError = null
            };
            return true;
        }
    }

    public void Disconnect(string error)
    {
        lock (_gate)
        {
            _snapshot = new NativeBridgeSnapshot(
                Connected: false,
                ProtocolVersion: null,
                GameBuild: null,
                SteamBuild: null,
                ModVersion: null,
                RuntimeExecutableSha256: null,
                RuntimeExecutableSize: null,
                RuntimeExecutablePath: null,
                RuntimeProcessSid: null,
                RuntimeNativeDllSha256: null,
                RuntimeNativeDllSize: null,
                RuntimeUe4ssDllSha256: null,
                RuntimeUe4ssDllSize: null,
                RuntimeIdentityVerified: false,
                WriteEnabled: false,
                Capabilities: new HashSet<string>(StringComparer.Ordinal),
                Probes: new Dictionary<string, bool>(StringComparer.Ordinal),
                LastSeenAt: null,
                LastError: error);
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
    private readonly EconomySafetyOptions _safetyOptions;
    private readonly NativeBridgeState _state;
    private readonly ILogger<NativeBridgeClient> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<NativeBridgeResult>> _pending = new();
    private NamedPipeClientStream? _activePipe;

    public NativeBridgeClient(
        IOptions<NativeBridgeOptions> options,
        IOptions<EconomySafetyOptions> safetyOptions,
        NativeBridgeState state,
        ILogger<NativeBridgeClient> logger)
    {
        _options = options.Value;
        _safetyOptions = safetyOptions.Value;
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
        var snapshot = _state.GetSnapshot();
        if (!snapshot.Connected || !snapshot.RuntimeIdentityVerified ||
            snapshot.ProtocolVersion != NativeBridgeProtocol.CurrentVersion)
        {
            throw new IOException("Native Mod bridge has no verified runtime identity.");
        }
        if (NativeBridgeProtocol.IsWriteOperation(operation) && !snapshot.WriteEnabled)
        {
            throw new InvalidOperationException(
                "Native Mod write capabilities are quarantined for this runtime-bound build.");
        }
        if (NativeBridgeProtocol.IsWriteOperation(operation) &&
            !NativeBridgeProtocol.MatchesApprovedWriteIdentity(snapshot, _safetyOptions))
        {
            throw new InvalidOperationException(
                "Native Mod runtime identity changed or is not approved for writes.");
        }

        var commandId = Guid.NewGuid();
        using var scope = ControlPlaneLog.BeginAdapter(
            _logger,
            nameof(NativeBridgeClient),
            operation,
            commandId,
            serverId);
        var timeout = TimeSpan.FromSeconds(
            Math.Clamp(_options.CommandTimeoutSeconds, 1, 30));
        using var commandDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandDeadline.CancelAfter(timeout);
        var sentAt = DateTimeOffset.UtcNow;
        var deadline = sentAt.Add(timeout);
        // Serialize the caller-owned object exactly once. The hash and the envelope
        // both consume this immutable UTF-8 fragment, so a mutable/custom getter
        // cannot produce a different payload during a second serialization pass.
        var payloadUtf8 = JsonSerializer.SerializeToUtf8Bytes(payload);
        var requestHash = Convert.ToHexStringLower(
            SHA256.HashData(payloadUtf8));
        var envelopeUtf8 = SerializeCommandEnvelope(
            messageId: Guid.NewGuid(),
            sentAt,
            commandId,
            idempotencyKey ?? $"native-command-{commandId:N}",
            requestHash,
            serverId,
            operation,
            deadline,
            expectedRevision,
            reason,
            payloadUtf8);

        var completion = new TaskCompletionSource<NativeBridgeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(commandId, completion))
        {
            throw new InvalidOperationException("Unable to register Native Mod command.");
        }

        try
        {
            try
            {
                await WriteFrameAsync(
                    pipe,
                    envelopeUtf8,
                    commandDeadline.Token);
                return await completion.Task.WaitAsync(commandDeadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Native Mod bridge command exceeded its absolute deadline.");
            }
        }
        finally
        {
            _pending.TryRemove(commandId, out _);
        }
    }

    private static byte[] SerializeCommandEnvelope(
        Guid messageId,
        DateTimeOffset sentAt,
        Guid commandId,
        string idempotencyKey,
        string requestHash,
        string serverId,
        string operation,
        DateTimeOffset deadline,
        long expectedRevision,
        string reason,
        ReadOnlySpan<byte> payloadUtf8)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("protocolVersion", NativeBridgeProtocol.CurrentVersion);
        writer.WriteString("messageType", "command");
        writer.WriteString("messageId", messageId);
        writer.WriteString("sentAt", sentAt);
        writer.WriteString("commandId", commandId);
        writer.WriteString("idempotencyKey", idempotencyKey);
        writer.WriteString("requestHash", requestHash);
        writer.WriteString("serverId", serverId);
        writer.WriteString("actorId", "control-api");
        writer.WriteString("operation", operation);
        writer.WriteString("deadline", deadline);
        writer.WriteNumber("expectedRevision", expectedRevision);
        writer.WriteString("reason", reason);
        writer.WritePropertyName("payload");
        writer.WriteRawValue(payloadUtf8, skipInputValidation: false);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
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
                exception is IOException or TimeoutException or UnauthorizedAccessException or
                    InvalidOperationException or JsonException)
            {
                var reasonCode = ClassifyConnectionFailure(exception);
                _state.Disconnect(reasonCode);
                _logger.LogSafeDebug(
                    exception,
                    "Native bridge connection ended with {ReasonCode}.",
                    reasonCode);
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

    private static string ClassifyConnectionFailure(Exception exception)
    {
        var sawIoException = false;
        var sawIdentityInspectionFailure = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return "PAL_MOD_BRIDGE_TIMEOUT";
            }
            if (current is UnauthorizedAccessException)
            {
                return "PAL_MOD_BRIDGE_ACCESS_DENIED";
            }
            if (current is JsonException)
            {
                return "PAL_MOD_BRIDGE_JSON_INVALID";
            }
            if (current.Message is
                "Native Bridge pipe server executable inspection failed.")
            {
                sawIdentityInspectionFailure = true;
                continue;
            }
            if (current.Message ==
                "Native Bridge pipe server process identity is unavailable.")
            {
                return "PAL_MOD_BRIDGE_PROCESS_ID_UNAVAILABLE";
            }
            if (current.Message is
                "Native Bridge server process cannot be opened for limited identity inspection." or
                "Native Bridge server executable path is unavailable.")
            {
                return "PAL_MOD_BRIDGE_PROCESS_PATH_UNAVAILABLE";
            }
            if (current.Message is
                "Native Bridge server process token cannot be opened for identity inspection." or
                "Native Bridge server process token user is unavailable." or
                "Native Bridge server process SID cannot be converted.")
            {
                return "PAL_MOD_BRIDGE_PROCESS_SID_UNAVAILABLE";
            }
            if (current.Message ==
                "Native Bridge server process SID is not canonical.")
            {
                return "PAL_MOD_BRIDGE_PROCESS_SID_INVALID";
            }
            if (current.Message ==
                "Native Bridge server executable canonical path is unavailable.")
            {
                return "PAL_MOD_BRIDGE_PROCESS_CANONICAL_PATH_INVALID";
            }
            if (current.Message ==
                "Native Bridge process module changed during inspection.")
            {
                return "PAL_MOD_BRIDGE_PROCESS_FILE_UNSTABLE";
            }
            if (current.Message ==
                "Native Bridge hello does not match the independently inspected pipe-server executable.")
            {
                return "PAL_MOD_BRIDGE_PROCESS_HELLO_MISMATCH";
            }
            if (current.Message ==
                "Native Bridge hello does not match the configured approved runtime identity.")
            {
                return "PAL_MOD_BRIDGE_APPROVED_IDENTITY_MISMATCH";
            }
            if (current.Message ==
                "Native Bridge hello failed runtime identity or capability validation.")
            {
                return "PAL_MOD_BRIDGE_HELLO_INVALID";
            }
            if (current is IOException)
            {
                sawIoException = true;
            }
        }
        if (sawIdentityInspectionFailure)
        {
            return "PAL_MOD_BRIDGE_PROCESS_IDENTITY_INVALID";
        }
        return sawIoException
            ? "PAL_MOD_BRIDGE_PROTOCOL_INVALID"
            : "PAL_MOD_BRIDGE_STATE_INVALID";
    }

    private async Task RunConnectionAsync(CancellationToken stoppingToken)
    {
        using var handshakeDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        handshakeDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _options.PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            handshakeDeadline.Token);
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

        NativeBridgeServerProcessIdentity serverIdentity;
        try
        {
            serverIdentity = await ReadServerProcessIdentityAsync(
                pipe,
                handshakeDeadline.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Native Bridge process identity exceeded the absolute handshake deadline.");
        }
        _state.Disconnect("PAL_MOD_BRIDGE_AWAITING_HELLO");
        Volatile.Write(ref _activePipe, pipe);
        var helloReceived = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested && pipe.IsConnected)
            {
                byte[] payload;
                try
                {
                    payload = await ReadFrameAsync(
                        pipe,
                        helloReceived ? stoppingToken : handshakeDeadline.Token);
                }
                catch (OperationCanceledException) when (
                    !stoppingToken.IsCancellationRequested &&
                    handshakeDeadline.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Native Bridge hello exceeded the absolute handshake deadline.");
                }
                ProcessMessage(payload, ref helloReceived, serverIdentity);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _activePipe, null, pipe);
            FailPending(new IOException("Native Mod bridge connection ended."));
            _state.Disconnect("PAL_MOD_BRIDGE_CONNECTION_ENDED");
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
            await pipe.WriteAsync(frame, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var frameDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        frameDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var lengthBytes = new byte[sizeof(uint)];
            await stream.ReadExactlyAsync(lengthBytes, frameDeadline.Token);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);

            if (length == 0 ||
                length > Math.Clamp(_options.MaxFrameBytes, 1024, 16_777_216))
            {
                throw new IOException($"Invalid Native Bridge frame length: {length}.");
            }

            var payload = new byte[(int)length];
            await stream.ReadExactlyAsync(payload, frameDeadline.Token);
            return payload;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Native Bridge frame exceeded its 30-second absolute deadline.");
        }
    }

    private void ProcessMessage(
        byte[] payload,
        ref bool helloReceived,
        NativeBridgeServerProcessIdentity serverIdentity)
    {
        try
        {
            ProcessMessageCore(payload, ref helloReceived, serverIdentity);
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or ArgumentException or
                NullReferenceException or NotSupportedException)
        {
            throw new IOException("Native Bridge sent a malformed protocol frame.", exception);
        }
    }

    private void ProcessMessageCore(
        byte[] payload,
        ref bool helloReceived,
        NativeBridgeServerProcessIdentity serverIdentity)
    {
        using var document = JsonDocument.Parse(payload);
        var messageType = NativeBridgeProtocol.ValidateBaseEnvelope(document.RootElement);
        if (!helloReceived && messageType != "hello")
        {
            throw new IOException("Native Bridge first frame must be a valid hello.");
        }

        switch (messageType)
        {
            case "hello":
                if (helloReceived)
                {
                    throw new IOException("Native Bridge sent more than one hello on a connection.");
                }
                var hello = document.RootElement.Deserialize<NativeBridgeHello>();
                if (hello is null)
                {
                    throw new IOException("Native Bridge hello is invalid.");
                }
                if (!serverIdentity.Matches(hello))
                {
                    throw new IOException(
                        "Native Bridge hello does not match the independently inspected pipe-server executable.");
                }
                if (NativeBridgeProtocol.HasApprovedRuntimeIdentity(_safetyOptions) &&
                    (!NativeBridgeProtocol.MatchesApprovedRuntimeIdentity(
                         hello,
                         _safetyOptions) ||
                     !serverIdentity.MatchesApprovedLocalIdentity(_safetyOptions)))
                {
                    throw new IOException(
                        "Native Bridge hello does not match the configured approved runtime identity.");
                }
                _state.OnHello(
                    hello,
                    serverIdentity.Executable.CanonicalPath,
                    serverIdentity.ProcessSid);
                helloReceived = true;
                break;
            case "heartbeat":
                if (!helloReceived || !_state.Touch())
                {
                    throw new IOException("Native Bridge heartbeat arrived before a valid hello.");
                }
                break;
            case "result":
                if (!helloReceived)
                {
                    throw new IOException("Native Bridge result arrived before a valid hello.");
                }
                var result = document.RootElement.Deserialize<NativeBridgeResult>();
                if (result is null)
                {
                    throw new IOException("Native Bridge result is invalid.");
                }
                NativeBridgeProtocol.ValidateResult(result);
                if (!_state.Touch())
                {
                    throw new IOException("Native Bridge result has no active verified hello.");
                }
                if (_pending.TryRemove(result.CommandId, out var completion))
                {
                    completion.TrySetResult(result);
                }
                break;
            default:
                throw new IOException("Native Bridge messageType is not supported.");
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

    private static async Task<NativeBridgeServerProcessIdentity>
        ReadServerProcessIdentityAsync(
            NamedPipeClientStream pipe,
            CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() ||
            !GetNamedPipeServerProcessId(
                pipe.SafePipeHandle.DangerousGetHandle(),
                out var processId) ||
            processId == 0)
        {
            throw new IOException(
                "Native Bridge pipe server process identity is unavailable.");
        }

        try
        {
            const uint ProcessQueryLimitedInformation = 0x1000;
            var process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                processId);
            if (process == IntPtr.Zero)
            {
                throw new IOException(
                    "Native Bridge server process cannot be opened for limited identity inspection.");
            }
            try
            {
                var path = new StringBuilder(32_768);
                var pathLength = path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref pathLength) ||
                    pathLength <= 0)
                {
                    throw new IOException(
                        "Native Bridge server executable path is unavailable.");
                }
                var processSid = ReadProcessUserSid(process);
                return new NativeBridgeServerProcessIdentity(
                    processId,
                    processSid,
                    await ReadFileIdentityAsync(
                        path.ToString(),
                        cancellationToken));
            }
            finally
            {
                CloseHandle(process);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                UnauthorizedAccessException or IOException or OverflowException)
        {
            throw new IOException(
                "Native Bridge pipe server executable inspection failed.",
                exception);
        }
    }

    private static async Task<NativeBridgeFileIdentity> ReadFileIdentityAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1_048_576,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var canonicalPath = GetCanonicalExecutablePath(
            stream.SafeFileHandle.DangerousGetHandle());
        var size = stream.Length;
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1_048_576];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            sha256.AppendData(buffer.AsSpan(0, read));
        }
        var digest = Convert.ToHexStringLower(sha256.GetHashAndReset());
        if (size <= 0 || stream.Length != size)
        {
            throw new IOException("Native Bridge process module changed during inspection.");
        }
        return new NativeBridgeFileIdentity(canonicalPath, digest, size);
    }

    private static string GetCanonicalExecutablePath(IntPtr fileHandle)
    {
        var path = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(
            fileHandle,
            path,
            (uint)path.Capacity,
            fileNameNormalized: 0);
        if (length >= path.Capacity)
        {
            if (length is 0 or > 32_767)
            {
                throw new IOException(
                    "Native Bridge server executable canonical path is unavailable.");
            }
            path = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(
                fileHandle,
                path,
                (uint)path.Capacity,
                fileNameNormalized: 0);
        }
        if (length == 0 || length >= path.Capacity ||
            !NativeBridgeProtocol.TryNormalizeWindowsExecutablePath(
                path.ToString(),
                out var canonicalPath))
        {
            throw new IOException(
                "Native Bridge server executable canonical path is unavailable.");
        }
        return canonicalPath;
    }

    private static string ReadProcessUserSid(IntPtr process)
    {
        const uint TokenQuery = 0x0008;
        const int TokenUserInformationClass = 1;
        if (!OpenProcessToken(process, TokenQuery, out var token) || token == IntPtr.Zero)
        {
            throw new IOException(
                "Native Bridge server process token cannot be opened for identity inspection.");
        }
        try
        {
            _ = GetTokenInformation(
                token,
                TokenUserInformationClass,
                IntPtr.Zero,
                0,
                out var requiredLength);
            if (requiredLength <= 0 || requiredLength > 65_536)
            {
                throw new IOException(
                    "Native Bridge server process token user is unavailable.");
            }

            var tokenInformation = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(
                        token,
                        TokenUserInformationClass,
                        tokenInformation,
                        requiredLength,
                        out _) ||
                    Marshal.PtrToStructure<TokenUser>(tokenInformation).User.Sid == IntPtr.Zero)
                {
                    throw new IOException(
                        "Native Bridge server process token user is unavailable.");
                }
                var tokenUser = Marshal.PtrToStructure<TokenUser>(tokenInformation);
                if (!ConvertSidToStringSid(tokenUser.User.Sid, out var sidPointer) ||
                    sidPointer == IntPtr.Zero)
                {
                    throw new IOException(
                        "Native Bridge server process SID cannot be converted.");
                }
                try
                {
                    var sid = Marshal.PtrToStringUni(sidPointer);
                    if (!NativeBridgeProtocol.IsValidWindowsSid(sid))
                    {
                        throw new IOException(
                            "Native Bridge server process SID is invalid.");
                    }
                    return sid!;
                }
                finally
                {
                    _ = LocalFree(sidPointer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tokenInformation);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        IntPtr pipe,
        out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executablePath,
        ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        IntPtr file,
        StringBuilder filePath,
        uint filePathLength,
        uint fileNameNormalized);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr process,
        uint desiredAccess,
        out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(
        IntPtr sid,
        out IntPtr stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }
}

internal sealed record NativeBridgeFileIdentity(
    string CanonicalPath,
    string Sha256,
    long Size);

internal sealed record NativeBridgeServerProcessIdentity(
    uint ProcessId,
    string ProcessSid,
    NativeBridgeFileIdentity Executable)
{
    public bool Matches(NativeBridgeHello hello) =>
        string.Equals(Executable.Sha256, hello.RuntimeExecutableSha256,
            StringComparison.Ordinal) &&
        Executable.Size == hello.RuntimeExecutableSize;

    public bool MatchesApprovedLocalIdentity(EconomySafetyOptions approved) =>
        NativeBridgeProtocol.PathsEqual(
            Executable.CanonicalPath,
            approved.ApprovedPalServerExecutablePath) &&
        string.Equals(
            ProcessSid,
            approved.ApprovedPalServerProcessSid,
            StringComparison.Ordinal);
}

public sealed record NativeBridgeHello(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("gameBuild")] string GameBuild,
    [property: JsonPropertyName("modVersion")] string ModVersion,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("probes")] IReadOnlyDictionary<string, bool> Probes,
    [property: JsonPropertyName("steamBuild")] string SteamBuild,
    [property: JsonPropertyName("runtimeExecutableSha256")] string RuntimeExecutableSha256,
    [property: JsonPropertyName("runtimeExecutableSize")] long RuntimeExecutableSize,
    [property: JsonPropertyName("runtimeIdentityVerified")] bool RuntimeIdentityVerified,
    [property: JsonPropertyName("writeEnabled")] bool WriteEnabled,
    [property: JsonPropertyName("runtimeNativeDllSha256")] string RuntimeNativeDllSha256 = "",
    [property: JsonPropertyName("runtimeNativeDllSize")] long RuntimeNativeDllSize = 0,
    [property: JsonPropertyName("runtimeUe4ssDllSha256")] string RuntimeUe4ssDllSha256 = "",
    [property: JsonPropertyName("runtimeUe4ssDllSize")] long RuntimeUe4ssDllSize = 0);

public static class NativeBridgeProtocol
{
    public const string CurrentVersion = "1.1";
    public static readonly TimeSpan MaximumInboundMessageAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumInboundClockLead = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> WriteOperations = new(StringComparer.Ordinal)
    {
        "players.progression.mutate",
        "inventory.mutate",
        "inventory.consume",
        "pals.mutate",
        "announcements.overlay.send",
        "announcements.banner.send",
        "ui.notifications.send"
    };

    private static readonly HashSet<string> WriteCapabilities = new(StringComparer.Ordinal)
    {
        "players.progression.mutate",
        "players.progression.write",
        "inventory.mutate",
        "inventory.write",
        "inventory.consume",
        "inventory.consume.experimental",
        "pals.mutate",
        "pals.write",
        "announcements.overlay.write",
        "announcements.banner.write",
        "ui.notifications.write"
    };

    public static bool IsWriteOperation(string operation) =>
        WriteOperations.Contains(operation);

    public static string ValidateBaseEnvelope(
        JsonElement root,
        DateTimeOffset? now = null)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("protocolVersion", out var protocolVersion) ||
            protocolVersion.ValueKind != JsonValueKind.String ||
            protocolVersion.GetString() != CurrentVersion)
        {
            throw new IOException("Native Bridge protocol is incompatible.");
        }

        if (!root.TryGetProperty("messageType", out var messageTypeElement) ||
            messageTypeElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(messageTypeElement.GetString()))
        {
            throw new IOException("Native Bridge messageType is missing or invalid.");
        }

        if (!root.TryGetProperty("messageId", out var messageIdElement) ||
            messageIdElement.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(messageIdElement.GetString(), "D", out var messageId) ||
            messageId == Guid.Empty)
        {
            throw new IOException("Native Bridge messageId must be a non-empty canonical UUID.");
        }

        if (!root.TryGetProperty("sentAt", out var sentAtElement) ||
            sentAtElement.ValueKind != JsonValueKind.String ||
            sentAtElement.GetString() is not { } sentAtText ||
            !HasExplicitRfc3339Offset(sentAtText) ||
            !sentAtElement.TryGetDateTimeOffset(out var sentAt))
        {
            throw new IOException(
                "Native Bridge sentAt must be an RFC 3339 timestamp with an explicit offset.");
        }

        var observedAt = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var sentAtUtc = sentAt.ToUniversalTime();
        if (observedAt - sentAtUtc > MaximumInboundMessageAge ||
            sentAtUtc - observedAt > MaximumInboundClockLead)
        {
            throw new IOException("Native Bridge sentAt is outside the allowed freshness window.");
        }

        return messageTypeElement.GetString()!;
    }

    private static bool HasExplicitRfc3339Offset(string value)
    {
        if (value.Length < 20 || value[4] != '-' || value[7] != '-' ||
            value[10] is not ('T' or 't') || value[13] != ':' || value[16] != ':')
        {
            return false;
        }

        var offsetStart = value[^1] is 'Z' or 'z' ? value.Length - 1 : value.Length - 6;
        if (offsetStart < 19 ||
            (offsetStart == value.Length - 6 &&
             (value[offsetStart] is not ('+' or '-') || value[offsetStart + 3] != ':' ||
              !value[(offsetStart + 1)..(offsetStart + 3)].All(char.IsAsciiDigit) ||
              !value[(offsetStart + 4)..].All(char.IsAsciiDigit))))
        {
            return false;
        }

        if (offsetStart > 19 &&
            (value[19] != '.' || offsetStart == 20 ||
             !value[20..offsetStart].All(char.IsAsciiDigit)))
        {
            return false;
        }

        for (var index = 0; index < 19; index++)
        {
            if (index is 4 or 7 or 10 or 13 or 16)
            {
                continue;
            }
            if (!char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }
        return true;
    }

    public static bool MatchesApprovedWriteIdentity(
        NativeBridgeSnapshot snapshot,
        EconomySafetyOptions approved) =>
        snapshot.Connected && snapshot.RuntimeIdentityVerified && snapshot.WriteEnabled &&
        HasApprovedRuntimeIdentity(approved) &&
        MatchesApprovedRuntimeIdentity(snapshot, approved);

    public static bool HasApprovedRuntimeIdentity(EconomySafetyOptions approved) =>
        !string.IsNullOrWhiteSpace(approved.ApprovedNativeProtocolVersion) &&
        !string.IsNullOrWhiteSpace(approved.ApprovedNativeGameBuild) &&
        !string.IsNullOrWhiteSpace(approved.ApprovedNativeSteamBuild) &&
        !string.IsNullOrWhiteSpace(approved.ApprovedNativeModVersion) &&
        approved.ApprovedNativeExecutableSha256 is { Length: 64 } &&
        approved.ApprovedNativeExecutableSize > 0 &&
        TryNormalizeWindowsExecutablePath(
            approved.ApprovedPalServerExecutablePath,
            out _) &&
        IsValidWindowsSid(approved.ApprovedPalServerProcessSid) &&
        approved.ApprovedNativeDllSha256 is { Length: 64 } &&
        approved.ApprovedNativeDllSize > 0 &&
        approved.ApprovedUe4ssDllSha256 is { Length: 64 } &&
        approved.ApprovedUe4ssDllSize > 0;

    public static bool MatchesApprovedRuntimeIdentity(
        NativeBridgeSnapshot snapshot,
        EconomySafetyOptions approved) =>
        string.Equals(snapshot.ProtocolVersion, approved.ApprovedNativeProtocolVersion,
            StringComparison.Ordinal) &&
        string.Equals(snapshot.GameBuild, approved.ApprovedNativeGameBuild,
            StringComparison.Ordinal) &&
        string.Equals(snapshot.SteamBuild, approved.ApprovedNativeSteamBuild,
            StringComparison.Ordinal) &&
        string.Equals(snapshot.ModVersion, approved.ApprovedNativeModVersion,
            StringComparison.Ordinal) &&
        string.Equals(snapshot.RuntimeExecutableSha256,
            approved.ApprovedNativeExecutableSha256, StringComparison.Ordinal) &&
        snapshot.RuntimeExecutableSize == approved.ApprovedNativeExecutableSize &&
        PathsEqual(
            snapshot.RuntimeExecutablePath,
            approved.ApprovedPalServerExecutablePath) &&
        string.Equals(
            snapshot.RuntimeProcessSid,
            approved.ApprovedPalServerProcessSid,
            StringComparison.Ordinal) &&
        string.Equals(snapshot.RuntimeNativeDllSha256,
            approved.ApprovedNativeDllSha256, StringComparison.Ordinal) &&
        snapshot.RuntimeNativeDllSize == approved.ApprovedNativeDllSize &&
        string.Equals(snapshot.RuntimeUe4ssDllSha256,
            approved.ApprovedUe4ssDllSha256, StringComparison.Ordinal) &&
        snapshot.RuntimeUe4ssDllSize == approved.ApprovedUe4ssDllSize;

    public static bool MatchesApprovedRuntimeIdentity(
        NativeBridgeHello hello,
        EconomySafetyOptions approved) =>
        string.Equals(hello.ProtocolVersion, approved.ApprovedNativeProtocolVersion,
            StringComparison.Ordinal) &&
        string.Equals(hello.GameBuild, approved.ApprovedNativeGameBuild,
            StringComparison.Ordinal) &&
        string.Equals(hello.SteamBuild, approved.ApprovedNativeSteamBuild,
            StringComparison.Ordinal) &&
        string.Equals(hello.ModVersion, approved.ApprovedNativeModVersion,
            StringComparison.Ordinal) &&
        string.Equals(hello.RuntimeExecutableSha256,
            approved.ApprovedNativeExecutableSha256, StringComparison.Ordinal) &&
        hello.RuntimeExecutableSize == approved.ApprovedNativeExecutableSize &&
        string.Equals(hello.RuntimeNativeDllSha256,
            approved.ApprovedNativeDllSha256, StringComparison.Ordinal) &&
        hello.RuntimeNativeDllSize == approved.ApprovedNativeDllSize &&
        string.Equals(hello.RuntimeUe4ssDllSha256,
            approved.ApprovedUe4ssDllSha256, StringComparison.Ordinal) &&
        hello.RuntimeUe4ssDllSize == approved.ApprovedUe4ssDllSize;

    public static bool PathsEqual(string? left, string? right) =>
        TryNormalizeWindowsExecutablePath(left, out var normalizedLeft) &&
        TryNormalizeWindowsExecutablePath(right, out var normalizedRight) &&
        string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);

    public static bool TryNormalizeWindowsExecutablePath(
        string? path,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            path.Any(char.IsControl))
        {
            return false;
        }

        var candidate = path.Replace('/', '\\');
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPathPrefix = @"\\?\";
        const string nativePathPrefix = @"\??\";
        if (candidate.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = @"\\" + candidate[extendedUncPrefix.Length..];
        }
        else if (candidate.StartsWith(extendedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[extendedPathPrefix.Length..];
        }
        else if (candidate.StartsWith(nativePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[nativePathPrefix.Length..];
        }

        // GetFinalPathNameByHandle normally returns a reviewed DOS path with
        // the Win32 extended prefix (\\?\). Strip only the exact supported
        // prefixes before rejecting wildcard/device metacharacters in the
        // remaining path.
        if (candidate.Any(character =>
                character is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return false;
        }

        var driveAbsolute = candidate.Length >= 4 &&
            char.IsAsciiLetter(candidate[0]) &&
            candidate[1] == ':' &&
            candidate[2] == '\\';
        var uncAbsolute = candidate.StartsWith(@"\\", StringComparison.Ordinal) &&
            !candidate.StartsWith(@"\\.\", StringComparison.Ordinal);
        if (!driveAbsolute && !uncAbsolute)
        {
            return false;
        }

        var segmentText = driveAbsolute ? candidate[3..] : candidate[2..];
        var segments = segmentText.Split('\\', StringSplitOptions.None);
        var minimumSegments = driveAbsolute ? 1 : 3;
        if (segments.Length < minimumSegments ||
            segments.Any(segment =>
                segment.Length == 0 || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Contains(':')) ||
            !segments[^1].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (driveAbsolute)
        {
            candidate = char.ToUpperInvariant(candidate[0]) + candidate[1..];
        }
        normalized = candidate;
        return true;
    }

    public static bool IsValidWindowsSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || sid.Length > 184 ||
            !string.Equals(sid, sid.Trim(), StringComparison.Ordinal) ||
            !sid.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = sid.Split('-', StringSplitOptions.None);
        if (parts.Length is < 4 or > 18 || parts[0] != "S" || parts[1] != "1" ||
            !TryParseCanonicalUnsigned(parts[2], 281_474_976_710_655, out _))
        {
            return false;
        }
        return parts.Skip(3).All(part =>
            TryParseCanonicalUnsigned(part, uint.MaxValue, out _));
    }

    private static bool TryParseCanonicalUnsigned(
        string value,
        ulong maximum,
        out ulong parsed)
    {
        parsed = 0;
        return value.Length > 0 &&
            (value.Length == 1 || value[0] != '0') &&
            value.All(character => character is >= '0' and <= '9') &&
            ulong.TryParse(value, out parsed) &&
            parsed <= maximum;
    }

    public static void ValidateHello(NativeBridgeHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        var gameVersion = !string.IsNullOrWhiteSpace(hello.GameBuild) &&
            hello.GameBuild.StartsWith('v')
            ? hello.GameBuild[1..]
            : string.Empty;
        if (hello.ProtocolVersion != CurrentVersion ||
            gameVersion.Count(character => character == '.') != 3 ||
            !Version.TryParse(gameVersion, out _) ||
            string.IsNullOrWhiteSpace(hello.SteamBuild) ||
            hello.SteamBuild.Length > 32 ||
            hello.SteamBuild.Any(character => character is < '0' or > '9') ||
            string.IsNullOrWhiteSpace(hello.ModVersion) || hello.ModVersion.Length > 64 ||
            hello.ModVersion.Any(char.IsControl) ||
            hello.RuntimeExecutableSha256 is null ||
            hello.RuntimeExecutableSha256.Length != 64 ||
            hello.RuntimeExecutableSha256.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')) ||
            hello.RuntimeExecutableSize <= 0 ||
            hello.RuntimeNativeDllSha256 is null ||
            hello.RuntimeNativeDllSha256.Length != 64 ||
            hello.RuntimeNativeDllSha256.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')) ||
            hello.RuntimeNativeDllSize <= 0 ||
            hello.RuntimeUe4ssDllSha256 is null ||
            hello.RuntimeUe4ssDllSha256.Length != 64 ||
            hello.RuntimeUe4ssDllSha256.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')) ||
            hello.RuntimeUe4ssDllSize <= 0 ||
            !hello.RuntimeIdentityVerified ||
            hello.Capabilities is null || hello.Capabilities.Count is < 1 or > 128 ||
            hello.Capabilities.Any(capability =>
                string.IsNullOrWhiteSpace(capability) || capability.Length > 128 ||
                capability.Any(char.IsControl)) ||
            hello.Capabilities.Distinct(StringComparer.Ordinal).Count() !=
                hello.Capabilities.Count ||
            hello.Probes is null || hello.Probes.Count is < 1 or > 128 ||
            hello.Probes.Keys.Any(probe =>
                string.IsNullOrWhiteSpace(probe) || probe.Length > 128 ||
                probe.Any(char.IsControl)) ||
            !hello.Probes.TryGetValue("runtime.executable.sha256", out var hashProbe) ||
            !hashProbe ||
            !hello.Probes.TryGetValue("runtime.native_dll.sha256", out var nativeDllProbe) ||
            !nativeDllProbe ||
            !hello.Probes.TryGetValue("runtime.ue4ss_dll.sha256", out var ue4ssDllProbe) ||
            !ue4ssDllProbe ||
            !hello.Probes.TryGetValue("runtime.write_enabled", out var writeProbe) ||
            writeProbe != hello.WriteEnabled ||
            (!hello.WriteEnabled && hello.Capabilities.Any(WriteCapabilities.Contains)))
        {
            throw new IOException(
                "Native Bridge hello failed runtime identity or capability validation.");
        }
    }

    public static void ValidateResult(NativeBridgeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var supportedState = result.State is "succeeded" or "failed" or "uncertain";
        var dataIsNullOrObject = result.Data is null ||
            result.Data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Object;
        if (result.CommandId == Guid.Empty || !supportedState ||
            result.ObservedRevision < 0 || !dataIsNullOrObject)
        {
            throw new IOException("Native Bridge result failed structural validation.");
        }

        if (result.State == "succeeded")
        {
            if (result.Error is not null || result.Data is null ||
                result.Data.Value.ValueKind != JsonValueKind.Object)
            {
                throw new IOException(
                    "Native Bridge succeeded result requires object data and no error.");
            }
            return;
        }

        if (result.Error is null ||
            !IsBoundedProtocolText(result.Error.Code, 1, 128) ||
            !IsBoundedProtocolText(result.Error.Message, 1, 1_024))
        {
            throw new IOException(
                "Native Bridge failed/uncertain result requires a bounded error.");
        }
    }

    private static bool IsBoundedProtocolText(
        string? value,
        int minimumLength,
        int maximumLength) =>
        value is not null && value.Length >= minimumLength &&
        value.Length <= maximumLength &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);
}

public sealed record NativeBridgeResult(
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("observedRevision")] long ObservedRevision,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("error")] NativeBridgeError? Error);

public sealed record NativeBridgeError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

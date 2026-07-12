using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

var pipeName = args.FirstOrDefault() ?? "pal-control.local.v1";
var notificationStatePath = args.Skip(1).FirstOrDefault();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await using var pipe = new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    maxNumberOfServerInstances: 1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);
using var writeGate = new SemaphoreSlim(1, 1);

Console.WriteLine($"Waiting for Control API on pipe {pipeName}...");
await pipe.WaitForConnectionAsync(shutdown.Token);

await WriteFrameAsync(pipe, writeGate, new
{
    protocolVersion = "1.0",
    messageType = "hello",
    messageId = Guid.NewGuid(),
    sentAt = DateTimeOffset.UtcNow,
    gameBuild = "v1.0.0.100427-smoke",
    modVersion = "0.2.0-smoke",
    capabilities = new[]
    {
        "bridge.hello",
        "announcements.overlay.probe",
        "announcements.overlay.write",
        "announcements.banner.probe",
        "announcements.banner.write",
        "ui.notifications.probe",
        "ui.notifications.write"
    },
    probes = new Dictionary<string, bool>
    {
        ["ue4ss.unreal_init"] = true,
        ["pal.adapter.loaded"] = true,
        ["announcements.overlay"] = true,
        ["announcements.banner"] = true,
        ["ui.notifications"] = true
    }
}, shutdown.Token);

using var connectionEnded = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
var heartbeatTask = SendHeartbeatsAsync(
    pipe,
    writeGate,
    connectionEnded.Token);

try
{
    while (!shutdown.IsCancellationRequested && pipe.IsConnected)
    {
        var frame = await ReadFrameAsync(pipe, shutdown.Token);
        using var document = JsonDocument.Parse(frame);
        var root = document.RootElement;
        if (!root.TryGetProperty("messageType", out var messageType) ||
            !string.Equals(messageType.GetString(), "command", StringComparison.Ordinal))
        {
            continue;
        }

        var commandId = root.GetProperty("commandId").GetGuid();
        var operation = root.GetProperty("operation").GetString();
        var payload = root.GetProperty("payload");
        var message = payload.TryGetProperty("message", out var messageProperty)
            ? messageProperty.GetString()
            : null;
        var isOverlayProbe = string.Equals(
            operation,
            "announcements.overlay.probe",
            StringComparison.Ordinal);
        var isBannerProbe = string.Equals(
            operation,
            "announcements.banner.probe",
            StringComparison.Ordinal);
        var isOverlaySend = string.Equals(
                operation,
                "announcements.overlay.send",
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(message);
        var isBannerSend = string.Equals(
                operation,
                "announcements.banner.send",
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(message);
        var isNotificationProbe = string.Equals(
            operation,
            "ui.notifications.probe",
            StringComparison.Ordinal);
        var notificationPreset = default(JsonElement);
        var notificationParameters = default(JsonElement);
        var isNotificationSend = string.Equals(
                operation,
                "ui.notifications.send",
                StringComparison.Ordinal) &&
            payload.TryGetProperty("deliveryId", out _) &&
            payload.TryGetProperty("schemaVersion", out var notificationSchema) &&
            string.Equals(notificationSchema.GetString(), "1", StringComparison.Ordinal) &&
            payload.TryGetProperty("preset", out notificationPreset) &&
            payload.TryGetProperty("parameters", out notificationParameters) &&
            notificationParameters.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("audience", out var notificationAudience) &&
            notificationAudience.TryGetProperty("type", out var notificationAudienceType) &&
            string.Equals(notificationAudienceType.GetString(), "global", StringComparison.Ordinal);
        var succeeded = isOverlayProbe || isBannerProbe || isOverlaySend || isBannerSend ||
            isNotificationProbe || isNotificationSend;
        object? resultData = isOverlayProbe
            ? new
            {
                ready = true,
                dispatched = false,
                attemptedRecipients = 2,
                propertiesSize = 16,
                functionFlags = 150720,
                function = "/Script/Pal.PalGameStateInGame:BroadcastServerNotice"
            }
            : isBannerProbe
                ? new
                {
                    ready = true,
                    dispatched = false,
                    attemptedRecipients = 2,
                    function = "smoke:top-banner"
                }
            : isNotificationProbe
                ? new
                {
                    ready = true,
                    dispatched = false,
                    mode = "server-native-presets",
                    schemaVersions = new[] { "1" },
                    supportedAudiences = new[] { "global" },
                    supportedPresets = new object[]
                    {
                        new
                        {
                            name = "boss-defeat-reward",
                            displayName = "头目讨伐奖励",
                            description = "调用游戏原生 Client RPC 显示头目讨伐奖励通知。",
                            function = "/Script/Pal.PalNetworkPlayerComponent:ShowBossDefeatRewardUI_ToClient",
                            functionFlags = 84020416L,
                            propertiesSize = 20,
                            positionPolicy = new
                            {
                                mode = "game-defined",
                                configurable = false,
                                note = "位置由游戏原生客户端 UI 固定。"
                            },
                            durationPolicy = new
                            {
                                mode = "game-defined",
                                configurable = false,
                                note = "显示时长由游戏原生客户端 UI 固定；delaySeconds 仅为触发延迟，不是显示时长。"
                            },
                            parameters = new object[]
                            {
                                new
                                {
                                    name = "technologyPoint",
                                    type = "integer",
                                    description = "仅用于客户端通知显示，不会发放或写入真实科技点。",
                                    required = false,
                                    minimum = 0,
                                    maximum = 9999,
                                    @default = 1
                                },
                                new
                                {
                                    name = "delaySeconds",
                                    type = "integer",
                                    description = "原生 RPC 的触发延迟，不是显示时长。",
                                    required = false,
                                    minimum = 0,
                                    maximum = 60,
                                    @default = 0
                                }
                            }
                        },
                        NativeExpPreset(
                            "boss-bonus-exp",
                            "头目额外经验显示",
                            "/Script/Pal.PalNetworkPlayerComponent:ShowDefeatBossBonusExpReward_ToClient"),
                        NativeExpPreset(
                            "expedition-bonus-exp",
                            "远征额外经验显示",
                            "/Script/Pal.PalNetworkPlayerComponent:ShowExpeditionBonusExpReward_ToClient"),
                        new
                        {
                            name = "unlock-hard-mode",
                            displayName = "困难模式解锁",
                            description = "调用游戏原生 Client RPC 显示困难模式解锁通知。",
                            function = "/Script/Pal.PalNetworkPlayerComponent:ShowUnlockHardModeUI_ToClient",
                            functionFlags = 84020416L,
                            propertiesSize = 0,
                            positionPolicy = new
                            {
                                mode = "game-defined",
                                configurable = false,
                                note = "位置由游戏原生客户端 UI 固定。"
                            },
                            durationPolicy = new
                            {
                                mode = "game-defined",
                                configurable = false,
                                note = "显示时长由游戏原生客户端 UI 固定。"
                            },
                            parameters = Array.Empty<object>()
                        }
                    }
                }
            : isNotificationSend
                ? new
                {
                    dispatched = true,
                    deliveryId = payload.GetProperty("deliveryId").ToString(),
                    preset = notificationPreset.GetString(),
                    attemptedRecipients = 2,
                    deliveredRecipients = (int?)null,
                    deliveryAcknowledged = false,
                    transport = "reliable-client-rpc"
                }
            : isOverlaySend || isBannerSend
                ? new
                {
                    attemptedRecipients = 2,
                    deliveredRecipients = (int?)null,
                    deliveryAcknowledged = false,
                    targetCount = 2,
                    gameStateCount = 1
                }
                : null;

        if (isNotificationSend && notificationStatePath is { Length: > 0 })
        {
            await File.AppendAllTextAsync(
                notificationStatePath,
                payload.GetProperty("deliveryId").ToString() + Environment.NewLine,
                shutdown.Token);
        }
        if (isNotificationSend &&
            notificationParameters.TryGetProperty("technologyPoint", out var technologyPoint) &&
            technologyPoint.TryGetInt32(out var technologyPointValue) &&
            technologyPointValue == 9999)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), shutdown.Token);
        }

        await WriteFrameAsync(pipe, writeGate, new
        {
            protocolVersion = "1.0",
            messageType = "result",
            messageId = Guid.NewGuid(),
            sentAt = DateTimeOffset.UtcNow,
            commandId,
            state = succeeded ? "succeeded" : "failed",
            observedRevision = 0,
            data = resultData,
            error = succeeded
                ? null
                : new
                {
                    code = "UNSUPPORTED_SMOKE_COMMAND",
                    message = "The fake Native Bridge accepts only announcement or server-native notification probe/send operations."
                }
        }, shutdown.Token);

        Console.WriteLine($"{operation}: {(succeeded ? "succeeded" : "failed")}");
    }
}
catch (EndOfStreamException)
{
}
catch (IOException) when (!shutdown.IsCancellationRequested)
{
}
finally
{
    connectionEnded.Cancel();
    try
    {
        await heartbeatTask;
    }
    catch (OperationCanceledException)
    {
    }
}

static object NativeExpPreset(string name, string displayName, string function) => new
{
    name,
    displayName,
    description = "实验性：改变客户端 UI 显示累计值，但不发放真实经验，也不写入服务器状态。",
    function,
    functionFlags = 16911552L,
    propertiesSize = 4,
    positionPolicy = new
    {
        mode = "game-defined",
        configurable = false,
        note = "位置由游戏原生客户端 UI 固定。"
    },
    durationPolicy = new
    {
        mode = "game-defined",
        configurable = false,
        note = "显示时长由游戏原生客户端 UI 固定。"
    },
    parameters = new object[]
    {
        new
        {
            name = "rewardExp",
            type = "integer",
            description = "仅改变客户端通知 UI 显示累计值。",
            required = true,
            minimum = 0,
            maximum = 10_000_000
        }
    }
};

static async Task SendHeartbeatsAsync(
    Stream stream,
    SemaphoreSlim writeGate,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        await WriteFrameAsync(stream, writeGate, new
        {
            protocolVersion = "1.0",
            messageType = "heartbeat",
            messageId = Guid.NewGuid(),
            sentAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }
}

static async Task<byte[]> ReadFrameAsync(
    Stream stream,
    CancellationToken cancellationToken)
{
    var header = new byte[sizeof(uint)];
    await stream.ReadExactlyAsync(header, cancellationToken);
    var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (length == 0 || length > 1_048_576)
    {
        throw new IOException($"Invalid frame length: {length}.");
    }

    var payload = new byte[(int)length];
    await stream.ReadExactlyAsync(payload, cancellationToken);
    return payload;
}

static async Task WriteFrameAsync(
    Stream stream,
    SemaphoreSlim writeGate,
    object message,
    CancellationToken cancellationToken)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(message);
    var frame = new byte[sizeof(uint) + payload.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
    payload.CopyTo(frame, sizeof(uint));

    await writeGate.WaitAsync(cancellationToken);
    try
    {
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
    finally
    {
        writeGate.Release();
    }
}

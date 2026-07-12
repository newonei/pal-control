using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PalControl.ControlApi.Extraction;

internal sealed record SourceRconSettings(
    IPAddress Address,
    int Port,
    TimeSpan Timeout,
    string Password);

/// <summary>
/// Minimal Source RCON transport. It is intentionally internal so consumers
/// cannot bypass <see cref="IExtractionRconAdapter"/>'s command allow-list.
/// </summary>
internal sealed class SourceRconTransport
{
    private const int ServerDataResponseValue = 0;
    private const int ServerDataExecCommand = 2;
    private const int ServerDataAuthResponse = 2;
    private const int ServerDataAuth = 3;
    private const int MaximumPacketBytes = 1_048_576;
    private const int MaximumIgnoredPackets = 32;
    // Palworld's RCON implementation echoes only the low 16 bits of request ids.
    private static int _requestSequence = (Environment.TickCount & int.MaxValue) % ushort.MaxValue;

    private readonly SourceRconSettings _settings;

    public SourceRconTransport(SourceRconSettings settings)
    {
        _settings = settings;
    }

    public async Task<RconOperationResult> ExecuteAsync(
        string command,
        bool isWrite,
        CancellationToken cancellationToken)
    {
        var commandWriteStarted = false;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_settings.Timeout);
        var operationToken = timeoutSource.Token;

        try
        {
            using var client = new TcpClient(_settings.Address.AddressFamily)
            {
                NoDelay = true
            };

            await client.ConnectAsync(_settings.Address, _settings.Port, operationToken);
            using var stream = client.GetStream();

            var authId = NextRequestId();
            await WritePacketAsync(stream, authId, ServerDataAuth, _settings.Password, operationToken);
            var authenticated = await ReadAuthenticationAsync(stream, authId, operationToken);
            if (!authenticated)
            {
                return RconOperationResult.Rejected(
                    "rcon_authentication_failed",
                    "RCON authentication was rejected.");
            }

            var commandId = NextRequestId();
            commandWriteStarted = true;
            await WritePacketAsync(
                stream,
                commandId,
                ServerDataExecCommand,
                command,
                operationToken);

            var response = await ReadCommandResponseAsync(stream, commandId, operationToken);
            if (LooksLikeCommandFailure(response))
            {
                return RconOperationResult.Rejected(
                    "rcon_command_rejected",
                    "PalDefender reported that the allow-listed command failed.",
                    response);
            }

            return RconOperationResult.Succeeded(response);
        }
        catch (OperationCanceledException) when (commandWriteStarted && isWrite)
        {
            var code = cancellationToken.IsCancellationRequested
                ? "rcon_write_cancelled_uncertain"
                : "rcon_write_timeout_uncertain";
            return RconOperationResult.OutcomeUncertain(
                code,
                "The RCON write may have been applied; refresh inventory state and do not retry automatically.");
        }
        catch (Exception exception) when (
            commandWriteStarted &&
            isWrite &&
            IsTransportOrProtocolFailure(exception))
        {
            return RconOperationResult.OutcomeUncertain(
                "rcon_write_transport_uncertain",
                "The RCON write may have been applied; refresh inventory state and do not retry automatically.");
        }
        catch (OperationCanceledException)
        {
            var code = cancellationToken.IsCancellationRequested
                ? "rcon_operation_cancelled"
                : "rcon_timeout";
            var message = cancellationToken.IsCancellationRequested
                ? "The RCON operation was cancelled before a write was dispatched."
                : "The RCON operation timed out before a write was dispatched.";
            return RconOperationResult.Rejected(code, message);
        }
        catch (RconAuthenticationException)
        {
            return RconOperationResult.Rejected(
                "rcon_authentication_failed",
                "RCON authentication was rejected.");
        }
        catch (SocketException)
        {
            return RconOperationResult.Rejected(
                "rcon_connection_failed",
                "The loopback RCON service is unavailable.");
        }
        catch (IOException)
        {
            return RconOperationResult.Rejected(
                "rcon_transport_failed",
                "The RCON connection closed before a write was dispatched.");
        }
        catch (RconProtocolException exception)
        {
            return RconOperationResult.Rejected(
                "rcon_protocol_failed",
                $"The RCON service returned an invalid Source RCON packet ({exception.Message}).");
        }
    }

    private static async Task<bool> ReadAuthenticationAsync(
        NetworkStream stream,
        int requestId,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < MaximumIgnoredPackets; index++)
        {
            var packet = await ReadPacketAsync(stream, cancellationToken);
            if (packet.Type == ServerDataAuthResponse)
            {
                if (packet.RequestId == -1)
                {
                    return false;
                }

                if (packet.RequestId == requestId)
                {
                    return true;
                }
            }

            // Source servers may send an empty SERVERDATA_RESPONSE_VALUE before
            // the actual authentication response.
            if (packet.Type != ServerDataResponseValue || packet.RequestId != requestId)
            {
                throw new RconProtocolException("unexpected authentication packet");
            }
        }

        throw new RconProtocolException("authentication response limit exceeded");
    }

    private static async Task<string> ReadCommandResponseAsync(
        NetworkStream stream,
        int requestId,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < MaximumIgnoredPackets; index++)
        {
            var packet = await ReadPacketAsync(stream, cancellationToken);
            // PalDefender uses request id 0 for successful JSON command responses,
            // while base-game RCON errors echo the caller's request id.
            if ((packet.RequestId == requestId || packet.RequestId == 0) &&
                packet.Type == ServerDataResponseValue)
            {
                return packet.Body;
            }

            if (packet.RequestId == -1)
            {
                throw new RconAuthenticationException();
            }
        }

        throw new RconProtocolException("command response limit exceeded");
    }

    private static async Task WritePacketAsync(
        NetworkStream stream,
        int requestId,
        int type,
        string body,
        CancellationToken cancellationToken)
    {
        // Palworld/PalDefender accepts UTF-8 command bodies. ASCII credentials
        // and commands remain byte-for-byte identical, while private player
        // notifications can safely contain localized text.
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var packetLength = bodyBytes.Length + 10;
        var packet = new byte[packetLength + sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), packetLength);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), type);
        bodyBytes.CopyTo(packet.AsSpan(12));
        // The array's final two bytes are the Source RCON body and packet NUL
        // terminators and remain zero-initialized.
        await stream.WriteAsync(packet, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<SourceRconPacket> ReadPacketAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken);
        var packetLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (packetLength < 10 || packetLength > MaximumPacketBytes - sizeof(int))
        {
            throw new RconProtocolException("invalid packet length");
        }

        var payload = new byte[packetLength];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        if (payload[^1] != 0 || payload[^2] != 0)
        {
            throw new RconProtocolException("missing packet terminators");
        }

        var bodyLength = packetLength - 10;
        if (payload.AsSpan(8, bodyLength).IndexOf((byte)0) >= 0)
        {
            throw new RconProtocolException("embedded body terminator");
        }

        return new SourceRconPacket(
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4)),
            Encoding.UTF8.GetString(payload, 8, bodyLength));
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken);
            if (read == 0)
            {
                throw new IOException("The RCON connection closed unexpectedly.");
            }

            offset += read;
        }
    }

    private static int NextRequestId()
    {
        var value = Interlocked.Increment(ref _requestSequence) & int.MaxValue;
        var normalized = value % ushort.MaxValue;
        return normalized == 0 ? 1 : normalized;
    }

    private static bool LooksLikeCommandFailure(string response)
    {
        var value = response.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        return value.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("not enough argument", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("invalid argument", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("not online", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("no player", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("couldn't", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("error", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("usage:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransportOrProtocolFailure(Exception exception) =>
        exception is SocketException or IOException or RconProtocolException or RconAuthenticationException;

    private sealed record SourceRconPacket(int RequestId, int Type, string Body);

    private sealed class RconProtocolException(string message) : Exception(message);

    private sealed class RconAuthenticationException : Exception;
}

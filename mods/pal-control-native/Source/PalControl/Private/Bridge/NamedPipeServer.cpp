#include "Bridge/NamedPipeServer.hpp"

#include <sddl.h>

#include <glaze/glaze.hpp>

#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <mutex>
#include <stdexcept>
#include <string_view>
#include <utility>

namespace PalControl::Bridge::Detail
{
    struct CommandWire
    {
        std::string protocolVersion;
        std::string messageType;
        std::string commandId;
        std::string idempotencyKey;
        std::string requestHash;
        std::string serverId;
        std::string actorId;
        std::string operation;
        std::string deadline;
        std::uint64_t expectedRevision{};
        std::string reason;
        glz::raw_json payload;
    };
}

namespace
{
    constexpr auto PipePath = LR"(\\.\pipe\pal-control.local.v1)";
    constexpr std::uint32_t MaxFrameBytes = 1'048'576;
    constexpr std::size_t MaxQueuedCommands = 128;
    constexpr std::size_t MaxQueuedResults = 128;

    std::string EscapeJson(std::string_view value)
    {
        std::string escaped;
        escaped.reserve(value.size());
        for (const char character : value)
        {
            switch (character)
            {
                case '\\': escaped += "\\\\"; break;
                case '"': escaped += "\\\""; break;
                case '\n': escaped += "\\n"; break;
                case '\r': escaped += "\\r"; break;
                case '\t': escaped += "\\t"; break;
                default: escaped += character; break;
            }
        }
        return escaped;
    }

    std::string UtcNow()
    {
        SYSTEMTIME time{};
        GetSystemTime(&time);
        std::array<char, 32> buffer{};
        std::snprintf(
            buffer.data(),
            buffer.size(),
            "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
            time.wYear,
            time.wMonth,
            time.wDay,
            time.wHour,
            time.wMinute,
            time.wSecond,
            time.wMilliseconds);
        return buffer.data();
    }

    bool WriteAll(HANDLE pipe, const void* data, std::uint32_t size)
    {
        const auto* cursor = static_cast<const std::byte*>(data);
        std::uint32_t remaining = size;
        while (remaining > 0)
        {
            DWORD written = 0;
            if (!WriteFile(pipe, cursor, remaining, &written, nullptr) || written == 0)
            {
                return false;
            }
            cursor += written;
            remaining -= written;
        }
        return true;
    }

    bool WriteFrame(HANDLE pipe, std::string_view payload)
    {
        if (payload.empty() || payload.size() > MaxFrameBytes)
        {
            return false;
        }

        const auto length = static_cast<std::uint32_t>(payload.size());
        return WriteAll(pipe, &length, sizeof(length)) &&
               WriteAll(pipe, payload.data(), length);
    }

    bool ReadAll(HANDLE pipe, void* data, std::uint32_t size)
    {
        auto* cursor = static_cast<std::byte*>(data);
        std::uint32_t remaining = size;
        while (remaining > 0)
        {
            DWORD read = 0;
            if (!ReadFile(pipe, cursor, remaining, &read, nullptr) || read == 0)
            {
                return false;
            }
            cursor += read;
            remaining -= read;
        }
        return true;
    }

    enum class ReadFrameState
    {
        NoData,
        Frame,
        Disconnected,
        Invalid
    };

    ReadFrameState PollFrame(HANDLE pipe, std::string& payload)
    {
        DWORD available = 0;
        if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
        {
            return ReadFrameState::Disconnected;
        }
        if (available < sizeof(std::uint32_t))
        {
            return ReadFrameState::NoData;
        }

        std::uint32_t length = 0;
        if (!ReadAll(pipe, &length, sizeof(length)))
        {
            return ReadFrameState::Disconnected;
        }
        if (length == 0 || length > MaxFrameBytes)
        {
            return ReadFrameState::Invalid;
        }

        payload.resize(length);
        return ReadAll(pipe, payload.data(), length)
            ? ReadFrameState::Frame
            : ReadFrameState::Disconnected;
    }

    bool ParseCommand(
        std::string_view payload,
        PalControl::Contracts::CommandEnvelope& command)
    {
        PalControl::Bridge::Detail::CommandWire wire{};
        const auto error = glz::read<glz::opts{
            .error_on_unknown_keys = false,
            .error_on_missing_keys = true}>(wire, payload);
        if (error ||
            wire.protocolVersion != PalControl::Contracts::ProtocolVersion ||
            wire.messageType != "command" ||
            wire.commandId.empty() ||
            wire.operation.empty())
        {
            return false;
        }

        command = PalControl::Contracts::CommandEnvelope{
            .CommandId = std::move(wire.commandId),
            .IdempotencyKey = std::move(wire.idempotencyKey),
            .RequestHash = std::move(wire.requestHash),
            .ServerId = std::move(wire.serverId),
            .ActorId = std::move(wire.actorId),
            .Operation = std::move(wire.operation),
            .Deadline = std::move(wire.deadline),
            .ExpectedRevision = wire.expectedRevision,
            .Reason = std::move(wire.reason),
            .PayloadJson = std::move(wire.payload.str)
        };
        return true;
    }

    std::string BuildHello(std::string_view gameBuild, std::string_view modVersion)
    {
        return std::string{"{"} +
            "\"protocolVersion\":\"1.0\"," +
            "\"messageType\":\"hello\"," +
            "\"messageId\":\"00000000-0000-4000-8000-000000000001\"," +
            "\"sentAt\":\"" + UtcNow() + "\"," +
            "\"gameBuild\":\"" + EscapeJson(gameBuild) + "\"," +
            "\"modVersion\":\"" + EscapeJson(modVersion) + "\"," +
            "\"capabilities\":[\"bridge.hello\",\"players.probe\",\"players.schema\",\"players.progression.schema\",\"players.progression.probe\",\"players.progression.read\",\"players.progression.mutate\",\"players.progression.write\",\"inventory.schema\",\"inventory.probe\",\"inventory.read\",\"inventory.mutate\",\"inventory.write\",\"inventory.consume.experimental\",\"pals.schema\",\"pals.probe\",\"pals.read\",\"pals.skills.catalog\",\"pals.mutate\",\"pals.write\",\"announcements.overlay.probe\",\"announcements.overlay.write\",\"announcements.banner.probe\",\"announcements.banner.write\",\"ui.notifications.probe\",\"ui.notifications.write\"]," +
            "\"probes\":{" +
                "\"ue4ss.unreal_init\":true," +
                "\"engine.tick.registered\":true," +
                "\"pal.adapter.loaded\":true" +
            "}}";
    }

    std::string BuildHeartbeat()
    {
        return std::string{"{"} +
            "\"protocolVersion\":\"1.0\"," +
            "\"messageType\":\"heartbeat\"," +
            "\"messageId\":\"00000000-0000-4000-8000-000000000002\"," +
            "\"sentAt\":\"" + UtcNow() + "\"}";
    }

    std::string BuildResult(const PalControl::Contracts::CommandResult& result)
    {
        const char* state = "failed";
        if (result.State == PalControl::Contracts::CommandState::Succeeded)
        {
            state = "succeeded";
        }
        else if (result.State == PalControl::Contracts::CommandState::Uncertain)
        {
            state = "uncertain";
        }

        const auto data = result.DataJson.empty() ? "null" : result.DataJson;
        const auto error = result.ErrorCode.empty()
            ? std::string{"null"}
            : std::string{"{\"code\":\""} + EscapeJson(result.ErrorCode) +
                "\",\"message\":\"" + EscapeJson(result.ErrorMessage) + "\"}";

        return std::string{"{"} +
            "\"protocolVersion\":\"1.0\"," +
            "\"messageType\":\"result\"," +
            "\"messageId\":\"" + EscapeJson(result.CommandId) + "\"," +
            "\"sentAt\":\"" + UtcNow() + "\"," +
            "\"commandId\":\"" + EscapeJson(result.CommandId) + "\"," +
            "\"state\":\"" + state + "\"," +
            "\"observedRevision\":" + std::to_string(result.ObservedRevision) + "," +
            "\"data\":" + data + "," +
            "\"error\":" + error + "}";
    }

    SECURITY_ATTRIBUTES CreatePipeSecurity(PSECURITY_DESCRIPTOR& descriptor)
    {
        // Local SYSTEM, administrators, and the object owner only.
        constexpr auto SecurityDescriptor =
            L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                SecurityDescriptor,
                SDDL_REVISION_1,
                &descriptor,
                nullptr))
        {
            throw std::runtime_error("Unable to create Named Pipe security descriptor.");
        }

        SECURITY_ATTRIBUTES attributes{};
        attributes.nLength = sizeof(attributes);
        attributes.lpSecurityDescriptor = descriptor;
        attributes.bInheritHandle = FALSE;
        return attributes;
    }
}

namespace PalControl::Bridge
{
    NamedPipeServer::~NamedPipeServer()
    {
        Stop();
    }

    void NamedPipeServer::Start(std::string gameBuild, std::string modVersion)
    {
        if (worker_.joinable())
        {
            return;
        }

        worker_ = std::jthread(
            [this, gameBuild = std::move(gameBuild), modVersion = std::move(modVersion)](
                std::stop_token stopToken)
            {
                Run(stopToken, gameBuild, modVersion);
            });
    }

    void NamedPipeServer::Stop()
    {
        if (!worker_.joinable())
        {
            return;
        }

        worker_.request_stop();
        const auto wakeClient = CreateFileW(
            PipePath,
            GENERIC_READ,
            0,
            nullptr,
            OPEN_EXISTING,
            0,
            nullptr);
        if (wakeClient != INVALID_HANDLE_VALUE)
        {
            CloseHandle(wakeClient);
        }
        worker_.join();
        running_.store(false);
    }

    bool NamedPipeServer::IsRunning() const noexcept
    {
        return running_.load();
    }

    bool NamedPipeServer::TryDequeueCommand(Contracts::CommandEnvelope& command)
    {
        std::scoped_lock lock(commandMutex_);
        if (commands_.empty())
        {
            return false;
        }
        command = std::move(commands_.front());
        commands_.pop_front();
        return true;
    }

    void NamedPipeServer::EnqueueResult(Contracts::CommandResult result)
    {
        std::scoped_lock lock(resultMutex_);
        if (results_.size() >= MaxQueuedResults)
        {
            results_.pop_front();
        }
        results_.emplace_back(std::move(result));
    }

    void NamedPipeServer::Run(
        std::stop_token stopToken,
        std::string gameBuild,
        std::string modVersion)
    {
        running_.store(true);

        while (!stopToken.stop_requested())
        {
            PSECURITY_DESCRIPTOR descriptor = nullptr;
            SECURITY_ATTRIBUTES security{};
            try
            {
                security = CreatePipeSecurity(descriptor);
            }
            catch (...)
            {
                running_.store(false);
                return;
            }

            const auto pipe = CreateNamedPipeW(
                PipePath,
                PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1,
                MaxFrameBytes,
                MaxFrameBytes,
                0,
                &security);
            LocalFree(descriptor);

            if (pipe == INVALID_HANDLE_VALUE)
            {
                std::this_thread::sleep_for(std::chrono::seconds(1));
                continue;
            }

            activePipe_.store(pipe);
            const bool connected = ConnectNamedPipe(pipe, nullptr) != FALSE ||
                GetLastError() == ERROR_PIPE_CONNECTED;

            if (connected && !stopToken.stop_requested())
            {
                ServeClient(pipe, stopToken, gameBuild, modVersion);
            }

            if (activePipe_.exchange(INVALID_HANDLE_VALUE) == pipe)
            {
                FlushFileBuffers(pipe);
                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
            }
        }

        running_.store(false);
    }

    void NamedPipeServer::ServeClient(
        HANDLE pipe,
        std::stop_token stopToken,
        const std::string& gameBuild,
        const std::string& modVersion)
    {
        if (!WriteFrame(pipe, BuildHello(gameBuild, modVersion)))
        {
            return;
        }

        auto nextHeartbeat = std::chrono::steady_clock::now() + std::chrono::seconds(10);
        while (!stopToken.stop_requested())
        {
            std::string payload;
            switch (PollFrame(pipe, payload))
            {
                case ReadFrameState::Frame:
                {
                    Contracts::CommandEnvelope command{};
                    if (!ParseCommand(payload, command))
                    {
                        return;
                    }

                    std::scoped_lock lock(commandMutex_);
                    if (commands_.size() >= MaxQueuedCommands)
                    {
                        EnqueueResult(Contracts::CommandResult{
                            .CommandId = command.CommandId,
                            .State = Contracts::CommandState::Failed,
                            .ErrorCode = "COMMAND_QUEUE_FULL",
                            .ErrorMessage = "The game-thread command queue is full."
                        });
                    }
                    else
                    {
                        commands_.emplace_back(std::move(command));
                    }
                    break;
                }
                case ReadFrameState::NoData:
                    break;
                case ReadFrameState::Disconnected:
                case ReadFrameState::Invalid:
                    return;
            }

            for (;;)
            {
                Contracts::CommandResult result{};
                {
                    std::scoped_lock lock(resultMutex_);
                    if (results_.empty())
                    {
                        break;
                    }
                    result = std::move(results_.front());
                    results_.pop_front();
                }
                if (!WriteFrame(pipe, BuildResult(result)))
                {
                    return;
                }
            }

            const auto now = std::chrono::steady_clock::now();
            if (now >= nextHeartbeat)
            {
                if (!WriteFrame(pipe, BuildHeartbeat()))
                {
                    return;
                }
                nextHeartbeat = now + std::chrono::seconds(10);
            }

            std::this_thread::sleep_for(std::chrono::milliseconds(25));
        }
    }
}

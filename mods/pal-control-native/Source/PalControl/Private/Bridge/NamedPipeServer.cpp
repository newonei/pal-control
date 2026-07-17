#include "Bridge/NamedPipeServer.hpp"
#include "Runtime/ExecutableIdentity.hpp"

#include <sddl.h>

#include <glaze/glaze.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string_view>
#include <utility>

#ifndef PALCONTROL_PIPE_NAME
#define PALCONTROL_PIPE_NAME "pal-control.local.v1"
#endif
#ifndef PALCONTROL_CONTROL_API_SERVICE_SID
#define PALCONTROL_CONTROL_API_SERVICE_SID "S-1-5-80-993063732-716721481-3728868849-3499021384-1810321418"
#endif
#define PALCONTROL_WIDEN_INNER(value) L##value
#define PALCONTROL_WIDEN(value) PALCONTROL_WIDEN_INNER(value)

namespace PalControl::Bridge::Detail
{
    struct CommandWire
    {
        std::string protocolVersion;
        std::string messageType;
        std::string messageId;
        std::string sentAt;
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
    constexpr auto PipePath = L"\\\\.\\pipe\\" PALCONTROL_WIDEN(PALCONTROL_PIPE_NAME);
    constexpr std::uint32_t MaxFrameBytes = 1'048'576;
    constexpr std::size_t MaxQueuedCommands = 128;
    constexpr std::size_t MaxQueuedResults = 128;
    constexpr std::uint64_t MaxCommandDeadlineTicks = 30ULL * 10'000'000ULL;

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

    std::optional<std::uint64_t> ParseDeadlineUtcFileTimeTicks(
        std::string_view value)
    {
        const auto parseDigits = [&](std::size_t offset, std::size_t length)
            -> std::optional<unsigned int>
        {
            if (offset + length > value.size())
            {
                return std::nullopt;
            }
            unsigned int result = 0;
            for (std::size_t index = 0; index < length; ++index)
            {
                const auto character = static_cast<unsigned char>(
                    value[offset + index]);
                if (!std::isdigit(character))
                {
                    return std::nullopt;
                }
                result = result * 10 + (character - '0');
            }
            return result;
        };
        if (value.size() < 20 || value[4] != '-' || value[7] != '-' ||
            value[10] != 'T' || value[13] != ':' || value[16] != ':')
        {
            return std::nullopt;
        }
        const auto year = parseDigits(0, 4);
        const auto month = parseDigits(5, 2);
        const auto day = parseDigits(8, 2);
        const auto hour = parseDigits(11, 2);
        const auto minute = parseDigits(14, 2);
        const auto second = parseDigits(17, 2);
        if (!year || !month || !day || !hour || !minute || !second)
        {
            return std::nullopt;
        }

        std::size_t zoneOffset = 19;
        unsigned int milliseconds = 0;
        if (zoneOffset < value.size() && value[zoneOffset] == '.')
        {
            ++zoneOffset;
            std::size_t fractionalDigits = 0;
            while (zoneOffset < value.size() &&
                   std::isdigit(static_cast<unsigned char>(value[zoneOffset])))
            {
                if (fractionalDigits < 3)
                {
                    milliseconds = milliseconds * 10 +
                        static_cast<unsigned int>(value[zoneOffset] - '0');
                }
                ++fractionalDigits;
                ++zoneOffset;
            }
            if (fractionalDigits == 0)
            {
                return std::nullopt;
            }
            while (fractionalDigits++ < 3)
            {
                milliseconds *= 10;
            }
        }

        int offsetMinutes = 0;
        if (zoneOffset < value.size() && value[zoneOffset] == 'Z')
        {
            if (++zoneOffset != value.size())
            {
                return std::nullopt;
            }
        }
        else if (zoneOffset + 6 == value.size() &&
                 (value[zoneOffset] == '+' || value[zoneOffset] == '-') &&
                 value[zoneOffset + 3] == ':')
        {
            const auto offsetHours = parseDigits(zoneOffset + 1, 2);
            const auto offsetMinutePart = parseDigits(zoneOffset + 4, 2);
            if (!offsetHours || !offsetMinutePart || *offsetHours > 14 ||
                *offsetMinutePart > 59 ||
                (*offsetHours == 14 && *offsetMinutePart != 0))
            {
                return std::nullopt;
            }
            offsetMinutes = static_cast<int>(*offsetHours * 60 +
                *offsetMinutePart);
            if (value[zoneOffset] == '-')
            {
                offsetMinutes = -offsetMinutes;
            }
        }
        else
        {
            return std::nullopt;
        }

        SYSTEMTIME deadlineSystemTime{};
        deadlineSystemTime.wYear = static_cast<WORD>(*year);
        deadlineSystemTime.wMonth = static_cast<WORD>(*month);
        deadlineSystemTime.wDay = static_cast<WORD>(*day);
        deadlineSystemTime.wHour = static_cast<WORD>(*hour);
        deadlineSystemTime.wMinute = static_cast<WORD>(*minute);
        deadlineSystemTime.wSecond = static_cast<WORD>(*second);
        deadlineSystemTime.wMilliseconds = static_cast<WORD>(milliseconds);
        FILETIME deadlineFileTime{};
        if (!SystemTimeToFileTime(&deadlineSystemTime, &deadlineFileTime))
        {
            return std::nullopt;
        }
        ULARGE_INTEGER deadlineTicks{};
        deadlineTicks.LowPart = deadlineFileTime.dwLowDateTime;
        deadlineTicks.HighPart = deadlineFileTime.dwHighDateTime;
        constexpr std::int64_t TicksPerMinute = 60LL * 10'000'000LL;
        const auto utcTicks = static_cast<std::int64_t>(deadlineTicks.QuadPart) -
            static_cast<std::int64_t>(offsetMinutes) * TicksPerMinute;
        return utcTicks > 0
            ? std::optional<std::uint64_t>{static_cast<std::uint64_t>(utcTicks)}
            : std::nullopt;
    }

    std::uint64_t UtcFileTimeTicksNow()
    {
        FILETIME nowFileTime{};
        GetSystemTimeAsFileTime(&nowFileTime);
        ULARGE_INTEGER nowTicks{};
        nowTicks.LowPart = nowFileTime.dwLowDateTime;
        nowTicks.HighPart = nowFileTime.dwHighDateTime;
        return nowTicks.QuadPart;
    }

    bool HasControlCharacter(std::string_view value)
    {
        for (const auto character : value)
        {
            if (std::iscntrl(static_cast<unsigned char>(character)) != 0)
            {
                return true;
            }
        }
        return false;
    }

    bool IsCanonicalUuid(std::string_view value)
    {
        if (value.size() != 36 || value[8] != '-' || value[13] != '-' ||
            value[18] != '-' || value[23] != '-')
        {
            return false;
        }
        for (std::size_t index = 0; index < value.size(); ++index)
        {
            if (index == 8 || index == 13 || index == 18 || index == 23)
            {
                continue;
            }
            const auto character = static_cast<unsigned char>(value[index]);
            if (!std::isxdigit(character))
            {
                return false;
            }
        }
        return true;
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
        std::uint32_t length = 0;
        DWORD headerBytes = 0;
        if (!PeekNamedPipe(
                pipe,
                &length,
                sizeof(length),
                &headerBytes,
                &available,
                nullptr))
        {
            return ReadFrameState::Disconnected;
        }
        if (available < sizeof(length) || headerBytes < sizeof(length))
        {
            return ReadFrameState::NoData;
        }
        if (length == 0 || length > MaxFrameBytes)
        {
            return ReadFrameState::Invalid;
        }
        if (available < sizeof(length) + length)
        {
            // Do not consume the header until the complete bounded frame is
            // buffered. A client that sends only a header cannot strand the
            // server worker in a synchronous body read.
            return ReadFrameState::NoData;
        }
        if (!ReadAll(pipe, &length, sizeof(length)))
        {
            return ReadFrameState::Disconnected;
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
        const auto sentAtTicks = ParseDeadlineUtcFileTimeTicks(wire.sentAt);
        const auto deadlineTicks = ParseDeadlineUtcFileTimeTicks(wire.deadline);
        const auto nowTicks = UtcFileTimeTicksNow();
        const auto payloadHash =
            PalControl::Runtime::ComputeSha256Hex(wire.payload.str);
        const bool requestHashValid = wire.requestHash.size() == 64 &&
            std::all_of(
                wire.requestHash.begin(),
                wire.requestHash.end(),
                [](unsigned char character)
                {
                    return (character >= '0' && character <= '9') ||
                        (character >= 'a' && character <= 'f');
                });
        if (error ||
            wire.protocolVersion != PalControl::Contracts::ProtocolVersion ||
            wire.messageType != "command" ||
            !IsCanonicalUuid(wire.messageId) ||
            wire.sentAt.size() > 64 || !sentAtTicks ||
            !IsCanonicalUuid(wire.commandId) ||
            wire.idempotencyKey.size() < 8 || wire.idempotencyKey.size() > 128 ||
            HasControlCharacter(wire.idempotencyKey) ||
            !requestHashValid || !payloadHash || *payloadHash != wire.requestHash ||
            wire.serverId.empty() || wire.serverId.size() > 128 ||
            HasControlCharacter(wire.serverId) ||
            wire.actorId.empty() || wire.actorId.size() > 128 ||
            HasControlCharacter(wire.actorId) ||
            wire.operation.empty() || wire.operation.size() > 128 ||
            HasControlCharacter(wire.operation) ||
            wire.deadline.size() > 64 || !deadlineTicks ||
            *sentAtTicks > *deadlineTicks ||
            *deadlineTicks > nowTicks + MaxCommandDeadlineTicks ||
            wire.reason.empty() || wire.reason.size() > 512 ||
            HasControlCharacter(wire.reason) ||
            wire.payload.str.empty() || wire.payload.str.size() > 262'144)
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
            .DeadlineUtcFileTimeTicks = *deadlineTicks,
            .RequestHashVerified = true,
            .ExpectedRevision = wire.expectedRevision,
            .Reason = std::move(wire.reason),
            .PayloadJson = std::move(wire.payload.str)
        };
        return true;
    }

    std::string BuildHello(const PalControl::Bridge::BridgeIdentity& identity)
    {
        auto capabilities = std::string{
            "\"bridge.hello\",\"players.probe\",\"players.schema\","
            "\"players.progression.schema\",\"players.progression.probe\","
            "\"players.progression.read\",\"inventory.schema\",\"inventory.probe\","
            "\"inventory.read\",\"pals.schema\",\"pals.probe\",\"pals.read\","
            "\"pals.skills.catalog\",\"announcements.overlay.probe\","
            "\"announcements.banner.probe\",\"ui.notifications.probe\""};
        if (identity.WriteEnabled)
        {
            capabilities +=
                ",\"players.progression.mutate\",\"players.progression.write\","
                "\"inventory.mutate\",\"inventory.write\","
                "\"inventory.consume.experimental\",\"pals.mutate\",\"pals.write\","
                "\"announcements.overlay.write\",\"announcements.banner.write\","
                "\"ui.notifications.write\"";
        }
        return std::string{"{"} +
            "\"protocolVersion\":\"1.1\"," +
            "\"messageType\":\"hello\"," +
            "\"messageId\":\"00000000-0000-4000-8000-000000000001\"," +
            "\"sentAt\":\"" + UtcNow() + "\"," +
            "\"gameBuild\":\"" + EscapeJson(identity.GameBuild) + "\"," +
            "\"steamBuild\":\"" + EscapeJson(identity.SteamBuild) + "\"," +
            "\"modVersion\":\"" + EscapeJson(identity.ModVersion) + "\"," +
            "\"runtimeExecutableSha256\":\"" +
                EscapeJson(identity.RuntimeExecutableSha256) + "\"," +
            "\"runtimeExecutableSize\":" +
                std::to_string(identity.RuntimeExecutableSize) + "," +
            "\"runtimeNativeDllSha256\":\"" +
                EscapeJson(identity.RuntimeNativeDllSha256) + "\"," +
            "\"runtimeNativeDllSize\":" +
                std::to_string(identity.RuntimeNativeDllSize) + "," +
            "\"runtimeUe4ssDllSha256\":\"" +
                EscapeJson(identity.RuntimeUe4ssDllSha256) + "\"," +
            "\"runtimeUe4ssDllSize\":" +
                std::to_string(identity.RuntimeUe4ssDllSize) + "," +
            "\"runtimeIdentityVerified\":true," +
            "\"writeEnabled\":" +
                std::string{identity.WriteEnabled ? "true" : "false"} + "," +
            "\"capabilities\":[" + capabilities + "]," +
            "\"probes\":{" +
                "\"ue4ss.unreal_init\":true," +
                "\"engine.tick.registered\":true," +
                "\"pal.adapter.loaded\":true," +
                "\"runtime.executable.sha256\":true," +
                "\"runtime.native_dll.sha256\":true," +
                "\"runtime.ue4ss_dll.sha256\":true," +
                "\"runtime.write_enabled\":" +
                    std::string{identity.WriteEnabled ? "true" : "false"} +
            "}}";
    }

    std::string BuildHeartbeat()
    {
        return std::string{"{"} +
            "\"protocolVersion\":\"1.1\"," +
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
            "\"protocolVersion\":\"1.1\"," +
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
        // Local SYSTEM, administrators, the object owner, and the exact
        // low-privilege Control API virtual-service SID only.
        const auto securityDescriptor = std::wstring{
            L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)(A;;GA;;;"} +
            PALCONTROL_WIDEN(PALCONTROL_CONTROL_API_SERVICE_SID) + L")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                securityDescriptor.c_str(),
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

    void NamedPipeServer::Start(BridgeIdentity identity)
    {
        if (worker_.joinable())
        {
            return;
        }

        worker_ = std::jthread(
            [this, identity = std::move(identity)](
                std::stop_token stopToken)
            {
                Run(stopToken, identity);
            });
    }

    void NamedPipeServer::Stop()
    {
        if (!worker_.joinable())
        {
            return;
        }

        worker_.request_stop();
        {
            // Serialize cancellation with worker-side exchange/CloseHandle so
            // a recycled HANDLE value can never target an unrelated object.
            std::scoped_lock lock(pipeHandleMutex_);
            const auto activePipe = activePipe_.load();
            if (activePipe != INVALID_HANDLE_VALUE)
            {
                CancelIoEx(activePipe, nullptr);
                DisconnectNamedPipe(activePipe);
            }
        }
        // Cover the publish-to-ConnectNamedPipe window: a client may connect
        // before the server issues ConnectNamedPipe, which then returns
        // ERROR_PIPE_CONNECTED instead of blocking during shutdown.
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

    bool NamedPipeServer::TryExecuteNextCommand(
        const std::function<Contracts::CommandResult(
            const Contracts::CommandEnvelope&)>& executor)
    {
        QueuedCommand queued{};
        {
            std::scoped_lock lock(commandMutex_);
            if (commands_.empty())
            {
                return false;
            }
            queued = std::move(commands_.front());
            commands_.pop_front();
        }

        Contracts::CommandResult result{};
        {
            // Holding the session lease through adapter execution prevents a
            // command that was still queued at disconnect from starting on
            // the game thread. An operation already executing at disconnect
            // completes without a response and is reconciled as uncertain.
            std::scoped_lock lock(sessionMutex_);
            if (activeSessionGeneration_ != queued.SessionGeneration)
            {
                return true;
            }
            result = executor(queued.Command);
        }

        EnqueueResult(std::move(result), queued.SessionGeneration);
        return true;
    }

    std::uint64_t NamedPipeServer::BeginSession()
    {
        std::scoped_lock sessionLock(sessionMutex_);
        std::scoped_lock commandLock(commandMutex_);
        std::scoped_lock resultLock(resultMutex_);
        commands_.clear();
        results_.clear();
        if (++nextSessionGeneration_ == 0)
        {
            ++nextSessionGeneration_;
        }
        activeSessionGeneration_ = nextSessionGeneration_;
        return activeSessionGeneration_;
    }

    void NamedPipeServer::EndSession(std::uint64_t sessionGeneration)
    {
        std::scoped_lock sessionLock(sessionMutex_);
        if (activeSessionGeneration_ != sessionGeneration)
        {
            return;
        }
        activeSessionGeneration_ = 0;
        std::scoped_lock commandLock(commandMutex_);
        std::scoped_lock resultLock(resultMutex_);
        std::erase_if(commands_, [sessionGeneration](const QueuedCommand& queued)
        {
            return queued.SessionGeneration == sessionGeneration;
        });
        std::erase_if(results_, [sessionGeneration](const QueuedResult& queued)
        {
            return queued.SessionGeneration == sessionGeneration;
        });
    }

    void NamedPipeServer::EnqueueResult(
        Contracts::CommandResult result,
        std::uint64_t sessionGeneration)
    {
        std::scoped_lock sessionLock(sessionMutex_);
        if (activeSessionGeneration_ != sessionGeneration)
        {
            return;
        }
        std::scoped_lock resultLock(resultMutex_);
        if (results_.size() >= MaxQueuedResults)
        {
            results_.pop_front();
        }
        results_.emplace_back(QueuedResult{
            .Result = std::move(result),
            .SessionGeneration = sessionGeneration
        });
    }

    void NamedPipeServer::Run(
        std::stop_token stopToken,
        BridgeIdentity identity)
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

            {
                std::scoped_lock lock(pipeHandleMutex_);
                if (stopToken.stop_requested())
                {
                    CloseHandle(pipe);
                    break;
                }
                activePipe_.store(pipe);
            }
            const bool connected = ConnectNamedPipe(pipe, nullptr) != FALSE ||
                GetLastError() == ERROR_PIPE_CONNECTED;

            if (connected && !stopToken.stop_requested())
            {
                const auto sessionGeneration = BeginSession();
                ServeClient(pipe, stopToken, identity, sessionGeneration);
                EndSession(sessionGeneration);
            }

            {
                std::scoped_lock lock(pipeHandleMutex_);
                if (activePipe_.exchange(INVALID_HANDLE_VALUE) == pipe)
                {
                    DisconnectNamedPipe(pipe);
                    CloseHandle(pipe);
                }
            }
        }

        running_.store(false);
    }

    void NamedPipeServer::ServeClient(
        HANDLE pipe,
        std::stop_token stopToken,
        const BridgeIdentity& identity,
        std::uint64_t sessionGeneration)
    {
        if (!WriteFrame(pipe, BuildHello(identity)))
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
                    if (UtcFileTimeTicksNow() >=
                        command.DeadlineUtcFileTimeTicks)
                    {
                        EnqueueResult(Contracts::CommandResult{
                            .CommandId = command.CommandId,
                            .State = Contracts::CommandState::Failed,
                            .ErrorCode = "COMMAND_DEADLINE_EXPIRED",
                            .ErrorMessage =
                                "The command deadline expired before Native queue admission."
                        }, sessionGeneration);
                    }
                    else
                    {
                        bool queueFull = false;
                        {
                            std::scoped_lock sessionLock(sessionMutex_);
                            if (activeSessionGeneration_ != sessionGeneration)
                            {
                                return;
                            }
                            std::scoped_lock commandLock(commandMutex_);
                            queueFull = commands_.size() >= MaxQueuedCommands;
                            if (!queueFull)
                            {
                                commands_.emplace_back(QueuedCommand{
                                    .Command = std::move(command),
                                    .SessionGeneration = sessionGeneration
                                });
                            }
                        }
                        if (queueFull)
                        {
                            EnqueueResult(Contracts::CommandResult{
                                .CommandId = command.CommandId,
                                .State = Contracts::CommandState::Failed,
                                .ErrorCode = "COMMAND_QUEUE_FULL",
                                .ErrorMessage =
                                    "The game-thread command queue is full."
                            }, sessionGeneration);
                        }
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
                QueuedResult queued{};
                {
                    std::scoped_lock lock(resultMutex_);
                    if (results_.empty())
                    {
                        break;
                    }
                    queued = std::move(results_.front());
                    results_.pop_front();
                }
                if (queued.SessionGeneration != sessionGeneration)
                {
                    continue;
                }
                if (!WriteFrame(pipe, BuildResult(queued.Result)))
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

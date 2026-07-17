#pragma once

#include "Contracts/BridgeMessage.hpp"

#include <Windows.h>

#include <atomic>
#include <deque>
#include <functional>
#include <mutex>
#include <stop_token>
#include <string>
#include <thread>

namespace PalControl::Bridge
{
    struct BridgeIdentity
    {
        std::string GameBuild;
        std::string SteamBuild;
        std::string ModVersion;
        std::string RuntimeExecutableSha256;
        std::uint64_t RuntimeExecutableSize = 0;
        std::string RuntimeNativeDllSha256;
        std::uint64_t RuntimeNativeDllSize = 0;
        std::string RuntimeUe4ssDllSha256;
        std::uint64_t RuntimeUe4ssDllSize = 0;
        bool WriteEnabled = false;
    };

    class NamedPipeServer final
    {
    public:
        NamedPipeServer() = default;
        ~NamedPipeServer();

        NamedPipeServer(const NamedPipeServer&) = delete;
        NamedPipeServer& operator=(const NamedPipeServer&) = delete;

        void Start(BridgeIdentity identity);
        void Stop();

        [[nodiscard]] bool IsRunning() const noexcept;
        [[nodiscard]] bool TryExecuteNextCommand(
            const std::function<Contracts::CommandResult(
                const Contracts::CommandEnvelope&)>& executor);

    private:
        struct QueuedCommand
        {
            Contracts::CommandEnvelope Command;
            std::uint64_t SessionGeneration = 0;
        };

        struct QueuedResult
        {
            Contracts::CommandResult Result;
            std::uint64_t SessionGeneration = 0;
        };

        void Run(std::stop_token stopToken, BridgeIdentity identity);
        void ServeClient(
            HANDLE pipe,
            std::stop_token stopToken,
            const BridgeIdentity& identity,
            std::uint64_t sessionGeneration);
        [[nodiscard]] std::uint64_t BeginSession();
        void EndSession(std::uint64_t sessionGeneration);
        void EnqueueResult(
            Contracts::CommandResult result,
            std::uint64_t sessionGeneration);

        std::jthread worker_;
        std::atomic<HANDLE> activePipe_{INVALID_HANDLE_VALUE};
        std::mutex pipeHandleMutex_;
        std::atomic_bool running_{false};
        std::mutex sessionMutex_;
        std::uint64_t nextSessionGeneration_ = 0;
        std::uint64_t activeSessionGeneration_ = 0;
        std::mutex commandMutex_;
        std::deque<QueuedCommand> commands_;
        std::mutex resultMutex_;
        std::deque<QueuedResult> results_;
    };
}

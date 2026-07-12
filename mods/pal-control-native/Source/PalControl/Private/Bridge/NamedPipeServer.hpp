#pragma once

#include "Contracts/BridgeMessage.hpp"

#include <Windows.h>

#include <atomic>
#include <deque>
#include <mutex>
#include <stop_token>
#include <string>
#include <thread>

namespace PalControl::Bridge
{
    class NamedPipeServer final
    {
    public:
        NamedPipeServer() = default;
        ~NamedPipeServer();

        NamedPipeServer(const NamedPipeServer&) = delete;
        NamedPipeServer& operator=(const NamedPipeServer&) = delete;

        void Start(std::string gameBuild, std::string modVersion);
        void Stop();

        [[nodiscard]] bool IsRunning() const noexcept;
        [[nodiscard]] bool TryDequeueCommand(Contracts::CommandEnvelope& command);
        void EnqueueResult(Contracts::CommandResult result);

    private:
        void Run(std::stop_token stopToken, std::string gameBuild, std::string modVersion);
        void ServeClient(
            HANDLE pipe,
            std::stop_token stopToken,
            const std::string& gameBuild,
            const std::string& modVersion);

        std::jthread worker_;
        std::atomic<HANDLE> activePipe_{INVALID_HANDLE_VALUE};
        std::atomic_bool running_{false};
        std::mutex commandMutex_;
        std::deque<Contracts::CommandEnvelope> commands_;
        std::mutex resultMutex_;
        std::deque<Contracts::CommandResult> results_;
    };
}

#pragma once

#include "../Contracts/BridgeMessage.hpp"

#include <string>
#include <vector>

namespace PalControl::Game
{
    struct ProbeResult
    {
        std::string GameBuild;
        bool Supported = false;
        std::vector<std::string> MissingSymbols;
    };

    class IGameAdapter
    {
    public:
        virtual ~IGameAdapter() = default;

        // All methods on this interface are game-thread only.
        virtual ProbeResult Probe() = 0;
        virtual Contracts::CommandResult ReadPlayers(
            const Contracts::CommandEnvelope& command) = 0;
        virtual Contracts::CommandResult ReadInventory(
            const Contracts::CommandEnvelope& command) = 0;
        virtual Contracts::CommandResult ReadPals(
            const Contracts::CommandEnvelope& command) = 0;
        virtual Contracts::CommandResult MutateInventory(
            const Contracts::CommandEnvelope& command) = 0;
        virtual Contracts::CommandResult MutatePal(
            const Contracts::CommandEnvelope& command) = 0;
    };
}

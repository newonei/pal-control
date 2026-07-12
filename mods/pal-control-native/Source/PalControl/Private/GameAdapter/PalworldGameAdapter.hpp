#pragma once

#include "Contracts/BridgeMessage.hpp"

#include <deque>
#include <string>
#include <unordered_map>

namespace PalControl::Game
{
    class PalworldGameAdapter final
    {
    public:
        // Game-thread only. The caller is the UEngine::Tick command pump.
        [[nodiscard]] Contracts::CommandResult Execute(
            const Contracts::CommandEnvelope& command) const;

    private:
        struct CachedConsumeResult
        {
            std::string RequestHash;
            std::string PayloadJson;
            Contracts::CommandResult Result;
        };

        mutable std::deque<std::string> consumeCacheOrder_;
        mutable std::unordered_map<std::string, CachedConsumeResult>
            consumeCache_;

        [[nodiscard]] Contracts::CommandResult ProbePlayers(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ReadPlayerSchema(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ReadPlayerProgressionSchema(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ProbePlayerProgression(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult MutatePlayerProgression(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ReadInventorySchema(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ProbeInventory(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult MutateInventory(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ConsumeInventory(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ProbePals(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ReadPalSkillCatalog(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult MutatePal(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ReadPalSchema(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult SendOverlayAnnouncement(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ProbeOverlayAnnouncement(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult SendTopBannerAnnouncement(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ProbeTopBannerAnnouncement(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult ProbeInGameNotifications(
            const Contracts::CommandEnvelope& command) const;
        [[nodiscard]] Contracts::CommandResult SendInGameNotification(
            const Contracts::CommandEnvelope& command) const;
    };
}

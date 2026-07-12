#pragma once

#include <Unreal/UFunctionStructs.hpp>

#include <chrono>
#include <string>
#include <unordered_map>

namespace RC::Unreal
{
    class FFloatProperty;
    class FNameProperty;
    class FObjectProperty;
    class FStrProperty;
    class FStructProperty;
    class UClass;
    class UFunction;
    class UObject;
    struct FGuid;
}

namespace PalControl::Game
{
    // Read-only, game-thread chat query. It listens to the server's native
    // BroadcastChatMessage function and replies only to the controller whose
    // PalPlayerState owns SenderPlayerUId.
    class ExtractionChatCommand final
    {
    public:
        ExtractionChatCommand() = default;
        ~ExtractionChatCommand();

        ExtractionChatCommand(const ExtractionChatCommand&) = delete;
        ExtractionChatCommand& operator=(const ExtractionChatCommand&) = delete;

        [[nodiscard]] bool Install();
        void Uninstall() noexcept;
        [[nodiscard]] bool IsInstalled() const noexcept;

    private:
        void OnBroadcastChat(
            RC::Unreal::UnrealScriptFunctionCallableContext& context);
        [[nodiscard]] bool IsRateLimited(const std::string& senderKey);
        [[nodiscard]] RC::Unreal::UObject* ResolveUniqueSenderController(
            RC::Unreal::UObject* gameState,
            const RC::Unreal::FGuid& senderPlayerUid) const;
        [[nodiscard]] bool SendReply(RC::Unreal::UObject* controller) const;

        RC::Unreal::UFunction* chatFunction_{};
        RC::Unreal::FStructProperty* chatMessageParameter_{};
        RC::Unreal::FStrProperty* chatTextProperty_{};
        RC::Unreal::FStructProperty* senderPlayerUidProperty_{};
        RC::Unreal::UClass* gameStateClass_{};
        RC::Unreal::UClass* playerControllerClass_{};
        RC::Unreal::UClass* playerStateClass_{};
        RC::Unreal::FObjectProperty* controllerPlayerStateProperty_{};
        RC::Unreal::FStructProperty* playerStateUidProperty_{};
        RC::Unreal::UFunction* clientMessageFunction_{};
        RC::Unreal::FStrProperty* clientMessageTextProperty_{};
        RC::Unreal::FNameProperty* clientMessageTypeProperty_{};
        RC::Unreal::FFloatProperty* clientMessageLifetimeProperty_{};
        RC::Unreal::CallbackId hookId_{-1};
        std::unordered_map<std::string, std::chrono::steady_clock::time_point>
            lastReplies_;
    };
}

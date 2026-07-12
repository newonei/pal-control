#pragma once

#include <Unreal/UFunctionStructs.hpp>

#include <random>
#include <chrono>
#include <string>
#include <unordered_map>

namespace RC::Unreal
{
    class FNameProperty;
    class FBoolProperty;
    class FObjectProperty;
    class FStructProperty;
    class UClass;
    class UFunction;
    class UObject;
}

namespace PalControl::Game
{
    // Before the authoritative RequestRespawn RPC executes, register a
    // different transform selected from the game's own live respawn anchors.
    class RandomSafeRespawn final
    {
    public:
        RandomSafeRespawn();
        ~RandomSafeRespawn();

        RandomSafeRespawn(const RandomSafeRespawn&) = delete;
        RandomSafeRespawn& operator=(const RandomSafeRespawn&) = delete;

        [[nodiscard]] bool Install();
        void Uninstall() noexcept;
        [[nodiscard]] bool IsInstalled() const noexcept;

    private:
        struct PreviousPoint
        {
            std::string Id;
            double X{};
            double Y{};
            double Z{};
        };

        void OnRequestRespawn(
            RC::Unreal::UnrealScriptFunctionCallableContext& context);
        [[nodiscard]] RC::Unreal::UObject* ResolvePlayerNetworkComponent(
            RC::Unreal::UObject* playerState) const;

        RC::Unreal::UFunction* requestRespawnFunction_{};
        RC::Unreal::UFunction* registerRespawnPointFunction_{};
        RC::Unreal::UFunction* getLocationFunction_{};
        RC::Unreal::UFunction* getRotationFunction_{};
        RC::Unreal::UFunction* isPlayerCompletelyDeadFunction_{};
        RC::Unreal::FStructProperty* registerPlayerUidParameter_{};
        RC::Unreal::FStructProperty* registerLocationParameter_{};
        RC::Unreal::FStructProperty* registerRotationParameter_{};
        RC::Unreal::FStructProperty* getLocationReturn_{};
        RC::Unreal::FStructProperty* getRotationReturn_{};
        RC::Unreal::FStructProperty* playerUidProperty_{};
        RC::Unreal::FBoolProperty* isPlayerCompletelyDeadReturn_{};
        RC::Unreal::FNameProperty* respawnPointIdProperty_{};
        RC::Unreal::FObjectProperty* controllerPlayerStateProperty_{};
        RC::Unreal::FObjectProperty* controllerTransmitterProperty_{};
        RC::Unreal::FObjectProperty* transmitterPlayerProperty_{};
        RC::Unreal::FObjectProperty* transmitterOwnerProperty_{};
        RC::Unreal::UClass* playerStateClass_{};
        RC::Unreal::UClass* playerControllerClass_{};
        RC::Unreal::UClass* transmitterClass_{};
        RC::Unreal::UClass* networkPlayerComponentClass_{};
        RC::Unreal::UClass* respawnLocationClass_{};
        RC::Unreal::CallbackId hookId_{-1};
        std::unordered_map<std::string, PreviousPoint> previousPointByPlayer_;
        std::unordered_map<std::string, std::chrono::steady_clock::time_point>
            lastAttemptByPlayer_;
        std::mt19937_64 random_;
    };
}

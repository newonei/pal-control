#include "RandomSafeRespawn.hpp"

#include <DynamicOutput/Output.hpp>
#include <Helpers/String.hpp>
#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/Quat.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <Unreal/UnrealCoreStructs.hpp>
#include <Unreal/UnrealFlags.hpp>
#include <Unreal/World.hpp>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cstddef>
#include <stdexcept>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace
{
    using namespace RC;
    using namespace RC::Unreal;

    constexpr std::size_t MaxRememberedPlayers = 256;
    constexpr std::size_t MaxRespawnCandidates = 128;
    constexpr double MaxAbsWorldXY = 2'000'000.0;
    constexpr double MaxAbsWorldZ = 1'000'000.0;
    constexpr double MinimumAnchorSeparation = 5'000.0;
    constexpr double MinimumAnchorSeparationSquared =
        MinimumAnchorSeparation * MinimumAnchorSeparation;
    constexpr auto AttemptCooldown = std::chrono::seconds(2);

    struct RespawnCandidate
    {
        std::string Id;
        FVector Location;
        FQuat Rotation;
    };

    template <typename TProperty>
    TProperty* FindTypedProperty(UStruct* owner, const File::CharType* name)
    {
        if (!owner)
        {
            return nullptr;
        }
        auto* property = owner->FindProperty(FName(name));
        return property && property->IsA<TProperty>()
            ? static_cast<TProperty*>(property)
            : nullptr;
    }

    bool IsLiveObject(UObject* object, UClass* expectedClass, UWorld* world)
    {
        return object && expectedClass && world && object->IsA(expectedClass) &&
            object->GetWorld() == world &&
            !object->HasAnyFlags(static_cast<EObjectFlags>(
                RF_ClassDefaultObject |
                RF_ArchetypeObject |
                RF_BeginDestroyed |
                RF_FinishDestroyed)) &&
            !object->HasAnyInternalFlags(
                EInternalObjectFlags::Unreachable |
                EInternalObjectFlags::PendingKill |
                EInternalObjectFlags::PendingConstruction);
    }

    bool IsAuthoritativeObject(UObject* object, UClass* expectedClass)
    {
        auto* world = object ? object->GetWorld() : nullptr;
        if (!IsLiveObject(object, expectedClass, world))
        {
            return false;
        }
        auto* authorityProperty = FindTypedProperty<FObjectProperty>(
            world->GetClassPrivate(),
            STR("AuthorityGameMode"));
        auto* authorityGameMode = authorityProperty
            ? *authorityProperty->ContainerPtrToValuePtr<UObject*>(world)
            : nullptr;
        return authorityGameMode &&
            !authorityGameMode->HasAnyFlags(static_cast<EObjectFlags>(
                RF_ClassDefaultObject |
                RF_ArchetypeObject |
                RF_BeginDestroyed |
                RF_FinishDestroyed));
    }

    bool IsStructProperty(FStructProperty* property, const File::CharType* name)
    {
        auto* structure = property ? property->GetStruct().Get() : nullptr;
        return structure && structure->GetName() == name;
    }

    std::string GuidKey(const FGuid& value)
    {
        char buffer[33]{};
        std::snprintf(
            buffer,
            sizeof(buffer),
            "%08x%08x%08x%08x",
            value.A,
            value.B,
            value.C,
            value.D);
        return buffer;
    }

    bool BindNoParameterServerRpc(UFunction* function, std::string_view fullName)
    {
        constexpr auto RequiredFlags = static_cast<EFunctionFlags>(
            FUNC_Native | FUNC_Net | FUNC_NetReliable | FUNC_NetServer);
        constexpr auto RejectedFlags = static_cast<EFunctionFlags>(
            FUNC_Static | FUNC_NetClient | FUNC_NetMulticast);
        if (!function || function->GetReturnProperty() ||
            to_string(function->GetFullName()) != fullName ||
            static_cast<std::uint32_t>(function->GetFunctionFlags()) != 0x04220CC0U ||
            !function->HasAllFunctionFlags(RequiredFlags) ||
            function->HasAnyFunctionFlags(RejectedFlags))
        {
            return false;
        }
        for (FProperty* property : TFieldRange<FProperty>(
                 function,
                 EFieldIterationFlags::IncludeAll))
        {
            if (property->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm))
            {
                return false;
            }
        }
        return function->GetPropertiesSize() == 0;
    }

    bool BindRegisterRespawnPoint(
        UFunction* function,
        FStructProperty*& uid,
        FStructProperty*& location,
        FStructProperty*& rotation)
    {
        constexpr auto RequiredFlags = static_cast<EFunctionFlags>(
            FUNC_Native | FUNC_Net | FUNC_NetReliable | FUNC_NetServer);
        constexpr auto RejectedFlags = static_cast<EFunctionFlags>(
            FUNC_Static | FUNC_NetClient | FUNC_NetMulticast);
        if (!function || function->GetReturnProperty() ||
            to_string(function->GetFullName()) !=
                "Function /Script/Pal.PalNetworkPlayerComponent:RegisterRespawnPoint_ToServer" ||
            !function->HasAllFunctionFlags(RequiredFlags) ||
            function->HasAnyFunctionFlags(RejectedFlags))
        {
            return false;
        }
        std::size_t count = 0;
        for (FProperty* property : TFieldRange<FProperty>(
                 function,
                 EFieldIterationFlags::IncludeAll))
        {
            if (!property->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm))
            {
                continue;
            }
            ++count;
            if (!property->IsA<FStructProperty>() ||
                property->HasAnyPropertyFlags(static_cast<EPropertyFlags>(
                    CPF_OutParm | CPF_ReturnParm)))
            {
                return false;
            }
            auto* structure = static_cast<FStructProperty*>(property);
            auto* structureType = structure->GetStruct().Get();
            if (!structureType) return false;
            const auto name = structureType->GetName();
            const auto propertyName = property->GetName();
            if (name == STR("Guid") &&
                (propertyName == STR("PlayerUId") || propertyName == STR("PlayerUID")) &&
                property->GetOffset_Internal() == 0x00) uid = structure;
            else if (name == STR("Vector") && propertyName == STR("Location") &&
                property->GetOffset_Internal() == 0x10) location = structure;
            else if (name == STR("Quat") && propertyName == STR("Rotation") &&
                property->GetOffset_Internal() == 0x30) rotation = structure;
            else return false;
        }
        return count == 3 && uid && location && rotation &&
            static_cast<std::uint32_t>(function->GetFunctionFlags()) == 0x00A20CC0U &&
            uid->GetSize() == sizeof(FGuid) &&
            location->GetSize() == FVector::StaticSize() &&
            rotation->GetSize() == FQuat::StaticSize() &&
            function->GetPropertiesSize() == 80;
    }

    bool BindPureStructReturn(
        UFunction* function,
        std::string_view fullName,
        const File::CharType* structName,
        FStructProperty*& returnProperty)
    {
        if (!function || to_string(function->GetFullName()) != fullName)
        {
            return false;
        }
        std::size_t count = 0;
        for (FProperty* property : TFieldRange<FProperty>(
                 function,
                 EFieldIterationFlags::IncludeAll))
        {
            if (!property->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm))
            {
                continue;
            }
            ++count;
            if (property->HasAnyPropertyFlags(EPropertyFlags::CPF_ReturnParm) &&
                property->IsA<FStructProperty>() &&
                property->GetName() == STR("ReturnValue") &&
                property->GetOffset_Internal() == 0)
            {
                returnProperty = static_cast<FStructProperty*>(property);
            }
        }
        const auto expectedSize =
            std::basic_string_view<File::CharType>{structName} == STR("Vector")
            ? FVector::StaticSize()
            : FQuat::StaticSize();
        const auto expectedFlags =
            std::basic_string_view<File::CharType>{structName} == STR("Vector")
            ? 0x54820400U
            : 0x54820400U;
        return count == 1 && returnProperty &&
            function->GetReturnProperty() == returnProperty &&
            static_cast<std::uint32_t>(function->GetFunctionFlags()) == expectedFlags &&
            IsStructProperty(returnProperty, structName) &&
            returnProperty->GetSize() == expectedSize &&
            function->GetPropertiesSize() == expectedSize;
    }

    bool BindPureBoolReturn(
        UFunction* function,
        std::string_view fullName,
        FBoolProperty*& returnProperty)
    {
        if (!function || to_string(function->GetFullName()) != fullName ||
            static_cast<std::uint32_t>(function->GetFunctionFlags()) != 0x54020401U ||
            function->GetPropertiesSize() != sizeof(bool))
        {
            return false;
        }
        std::size_t count = 0;
        for (FProperty* property : TFieldRange<FProperty>(
                 function,
                 EFieldIterationFlags::IncludeAll))
        {
            if (!property->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm)) continue;
            ++count;
            if (property->HasAnyPropertyFlags(EPropertyFlags::CPF_ReturnParm) &&
                property->IsA<FBoolProperty>() &&
                property->GetName() == STR("ReturnValue") &&
                property->GetOffset_Internal() == 0)
            {
                returnProperty = static_cast<FBoolProperty*>(property);
            }
        }
        return count == 1 && returnProperty &&
            function->GetReturnProperty() == returnProperty &&
            returnProperty->GetSize() == sizeof(bool);
    }

    bool IsSafeTransform(const FVector& location, const FQuat& rotation)
    {
        const auto x = location.X();
        const auto y = location.Y();
        const auto z = location.Z();
        const auto qx = rotation.GetX();
        const auto qy = rotation.GetY();
        const auto qz = rotation.GetZ();
        const auto qw = rotation.GetW();
        const auto norm = qx * qx + qy * qy + qz * qz + qw * qw;
        return std::isfinite(x) && std::isfinite(y) && std::isfinite(z) &&
            (std::abs(x) + std::abs(y) + std::abs(z)) > 1.0 &&
            std::abs(x) <= MaxAbsWorldXY && std::abs(y) <= MaxAbsWorldXY &&
            std::abs(z) <= MaxAbsWorldZ &&
            std::isfinite(norm) && norm >= 0.5 && norm <= 1.5;
    }

    double DistanceSquared(const FVector& left, const FVector& right)
    {
        const auto x = left.X() - right.X();
        const auto y = left.Y() - right.Y();
        const auto z = left.Z() - right.Z();
        return x * x + y * y + z * z;
    }
}

namespace PalControl::Game
{
    RandomSafeRespawn::RandomSafeRespawn()
        : random_(
            static_cast<std::mt19937_64::result_type>(
                std::chrono::high_resolution_clock::now()
                    .time_since_epoch()
                    .count()) ^
            static_cast<std::mt19937_64::result_type>(std::random_device{}()))
    {
    }

    RandomSafeRespawn::~RandomSafeRespawn()
    {
        Uninstall();
    }

    bool RandomSafeRespawn::Install()
    {
        using namespace RC;
        using namespace RC::Unreal;
        if (IsInstalled()) return true;

        playerStateClass_ = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalPlayerState"));
        playerControllerClass_ = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalPlayerController"));
        transmitterClass_ = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalNetworkTransmitter"));
        networkPlayerComponentClass_ = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalNetworkPlayerComponent"));
        respawnLocationClass_ = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalLocationPoint_Respawn"));
        requestRespawnFunction_ = UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr, nullptr, STR("/Script/Pal.PalPlayerState:RequestRespawn"));
        registerRespawnPointFunction_ = UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr, nullptr, STR("/Script/Pal.PalNetworkPlayerComponent:RegisterRespawnPoint_ToServer"));
        getLocationFunction_ = UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr, nullptr, STR("/Script/Pal.PalLocationPoint:GetLocation"));
        getRotationFunction_ = UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr, nullptr, STR("/Script/Pal.PalLocationPoint:GetRotation"));
        isPlayerCompletelyDeadFunction_ = UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr, nullptr, STR("/Script/Pal.PalPlayerState:IsPlayerCompletelyDead"));

        playerUidProperty_ = FindTypedProperty<FStructProperty>(
            playerStateClass_, STR("PlayerUId"));
        respawnPointIdProperty_ = FindTypedProperty<FNameProperty>(
            respawnLocationClass_, STR("RespawnPointID"));
        controllerPlayerStateProperty_ = FindTypedProperty<FObjectProperty>(
            playerControllerClass_, STR("PlayerState"));
        controllerTransmitterProperty_ = FindTypedProperty<FObjectProperty>(
            playerControllerClass_, STR("Transmitter"));
        transmitterPlayerProperty_ = FindTypedProperty<FObjectProperty>(
            transmitterClass_, STR("Player"));
        transmitterOwnerProperty_ = FindTypedProperty<FObjectProperty>(
            transmitterClass_, STR("Owner"));

        const bool ready = playerStateClass_ && playerControllerClass_ &&
            transmitterClass_ && networkPlayerComponentClass_ &&
            respawnLocationClass_ &&
            IsStructProperty(playerUidProperty_, STR("Guid")) &&
            respawnPointIdProperty_ && controllerPlayerStateProperty_ &&
            controllerTransmitterProperty_ && transmitterPlayerProperty_ &&
            transmitterOwnerProperty_ &&
            controllerTransmitterProperty_->GetPropertyClass().Get() ==
                transmitterClass_ &&
            transmitterPlayerProperty_->GetPropertyClass().Get() ==
                networkPlayerComponentClass_ &&
            BindNoParameterServerRpc(
                requestRespawnFunction_,
                "Function /Script/Pal.PalPlayerState:RequestRespawn") &&
            BindRegisterRespawnPoint(
                registerRespawnPointFunction_,
                registerPlayerUidParameter_,
                registerLocationParameter_,
                registerRotationParameter_) &&
            BindPureStructReturn(
                getLocationFunction_,
                "Function /Script/Pal.PalLocationPoint:GetLocation",
                STR("Vector"),
                getLocationReturn_) &&
            BindPureStructReturn(
                getRotationFunction_,
                "Function /Script/Pal.PalLocationPoint:GetRotation",
                STR("Quat"),
                getRotationReturn_) &&
            BindPureBoolReturn(
                isPlayerCompletelyDeadFunction_,
                "Function /Script/Pal.PalPlayerState:IsPlayerCompletelyDead",
                isPlayerCompletelyDeadReturn_) &&
            respawnPointIdProperty_->GetSize() == sizeof(FName) &&
            respawnPointIdProperty_->GetOffset_Internal() == 0x70;
        if (!ready)
        {
            Output::send<LogLevel::Error>(
                STR("[PalControlNative] Random safe respawn disabled: current Palworld respawn schema did not match the guarded v1.0 binding.\n"));
            return false;
        }

        try
        {
            hookId_ = requestRespawnFunction_->RegisterPreHook(
                [](UnrealScriptFunctionCallableContext& context, void* customData)
                {
                    static_cast<RandomSafeRespawn*>(customData)
                        ->OnRequestRespawn(context);
                },
                this);
            if (hookId_ < 0)
            {
                throw std::runtime_error("RequestRespawn hook returned an invalid callback id.");
            }
        }
        catch (...)
        {
            hookId_ = -1;
            Output::send<LogLevel::Error>(
                STR("[PalControlNative] Random safe respawn disabled: RequestRespawn hook registration failed.\n"));
            return false;
        }

        Output::send<LogLevel::Verbose>(
            STR("[PalControlNative] Random safe respawn enabled: authoritative RequestRespawn will register a different native safe anchor.\n"));
        return true;
    }

    void RandomSafeRespawn::Uninstall() noexcept
    {
        if (requestRespawnFunction_ && hookId_ >= 0)
        {
            try { requestRespawnFunction_->UnregisterHook(hookId_); }
            catch (...) { /* The game is tearing down. */ }
        }
        hookId_ = -1;
        previousPointByPlayer_.clear();
        lastAttemptByPlayer_.clear();
    }

    bool RandomSafeRespawn::IsInstalled() const noexcept
    {
        return requestRespawnFunction_ && hookId_ >= 0;
    }

    RC::Unreal::UObject* RandomSafeRespawn::ResolvePlayerNetworkComponent(
        RC::Unreal::UObject* playerState) const
    {
        using namespace RC::Unreal;
        auto* world = playerState ? playerState->GetWorld() : nullptr;
        if (!world) return nullptr;
        std::vector<UObject*> controllers;
        UObjectGlobals::FindAllOf(STR("PalPlayerController"), controllers);
        std::vector<UObject*> transmitters;
        UObjectGlobals::FindAllOf(STR("PalNetworkTransmitter"), transmitters);
        std::vector<UObject*> components;
        UObjectGlobals::FindAllOf(STR("PalNetworkPlayerComponent"), components);
        UObject* match = nullptr;
        for (auto* controller : controllers)
        {
            if (!IsLiveObject(controller, playerControllerClass_, world)) continue;
            auto* state = *controllerPlayerStateProperty_
                ->ContainerPtrToValuePtr<UObject*>(controller);
            if (state != playerState) continue;
            auto* transmitter = *controllerTransmitterProperty_
                ->ContainerPtrToValuePtr<UObject*>(controller);
            if (!IsLiveObject(transmitter, transmitterClass_, world)) return nullptr;
            auto* owner = *transmitterOwnerProperty_
                ->ContainerPtrToValuePtr<UObject*>(transmitter);
            const auto ownedTransmitterCount = std::ranges::count_if(
                transmitters,
                [&](UObject* candidate)
                {
                    if (!IsLiveObject(candidate, transmitterClass_, world)) return false;
                    return *transmitterOwnerProperty_
                        ->ContainerPtrToValuePtr<UObject*>(candidate) == controller;
                });
            if (owner != controller || ownedTransmitterCount != 1) return nullptr;
            auto* component = *transmitterPlayerProperty_
                ->ContainerPtrToValuePtr<UObject*>(transmitter);
            const auto ownedComponentCount = std::ranges::count_if(
                components,
                [&](UObject* candidate)
                {
                    return IsLiveObject(candidate, networkPlayerComponentClass_, world) &&
                        candidate->GetOuterPrivate() == transmitter;
                });
            if (!IsLiveObject(component, networkPlayerComponentClass_, world) ||
                component->GetOuterPrivate() != transmitter ||
                ownedComponentCount != 1 || match)
            {
                return nullptr;
            }
            match = component;
        }
        return match;
    }

    void RandomSafeRespawn::OnRequestRespawn(
        RC::Unreal::UnrealScriptFunctionCallableContext& context)
    {
        using namespace RC;
        using namespace RC::Unreal;
        auto* playerState = context.Context;
        if (!IsAuthoritativeObject(playerState, playerStateClass_)) return;
        auto* uid = playerUidProperty_->ContainerPtrToValuePtr<FGuid>(playerState);
        if (!uid || !uid->is_valid()) return;

        const auto stateStorageCount = std::max<std::size_t>(
            (isPlayerCompletelyDeadFunction_->GetPropertiesSize() +
             sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t),
            1);
        std::vector<std::max_align_t> stateStorage(stateStorageCount);
        auto* stateParameters = stateStorage.data();
        isPlayerCompletelyDeadFunction_->InitializeStruct(stateParameters);
        playerState->ProcessEvent(
            isPlayerCompletelyDeadFunction_,
            stateParameters);
        const auto isCompletelyDead = isPlayerCompletelyDeadReturn_
            ->GetPropertyValueInContainer(stateParameters);
        isPlayerCompletelyDeadFunction_->DestroyStruct(stateParameters);
        if (!isCompletelyDead) return;

        const auto playerKey = GuidKey(*uid);
        const auto now = std::chrono::steady_clock::now();
        if (const auto attempt = lastAttemptByPlayer_.find(playerKey);
            attempt != lastAttemptByPlayer_.end() &&
            now - attempt->second < AttemptCooldown)
        {
            return;
        }
        if (lastAttemptByPlayer_.size() >= MaxRememberedPlayers &&
            !lastAttemptByPlayer_.contains(playerKey))
        {
            lastAttemptByPlayer_.clear();
        }
        lastAttemptByPlayer_.insert_or_assign(playerKey, now);

        auto* networkComponent = ResolvePlayerNetworkComponent(playerState);
        if (!networkComponent) return;

        auto* world = playerState->GetWorld();
        std::vector<UObject*> objects;
        UObjectGlobals::FindAllOf(STR("PalLocationPoint_Respawn"), objects);
        std::vector<RespawnCandidate> candidates;
        candidates.reserve(std::min(objects.size(), MaxRespawnCandidates));
        std::unordered_set<std::string> seen;
        for (auto* object : objects)
        {
            if (candidates.size() >= MaxRespawnCandidates) break;
            if (!IsLiveObject(object, respawnLocationClass_, world)) continue;
            auto* id = respawnPointIdProperty_->ContainerPtrToValuePtr<FName>(object);
            if (!id || id->IsNone()) continue;
            auto idText = to_string(id->ToString());
            if (idText.empty() || !seen.insert(idText).second) continue;

            const auto locationStorageCount = std::max<std::size_t>(
                (getLocationFunction_->GetPropertiesSize() + sizeof(std::max_align_t) - 1) /
                    sizeof(std::max_align_t), 1);
            const auto rotationStorageCount = std::max<std::size_t>(
                (getRotationFunction_->GetPropertiesSize() + sizeof(std::max_align_t) - 1) /
                    sizeof(std::max_align_t), 1);
            std::vector<std::max_align_t> locationStorage(locationStorageCount);
            std::vector<std::max_align_t> rotationStorage(rotationStorageCount);
            auto* locationParameters = locationStorage.data();
            auto* rotationParameters = rotationStorage.data();
            getLocationFunction_->InitializeStruct(locationParameters);
            getRotationFunction_->InitializeStruct(rotationParameters);
            object->ProcessEvent(getLocationFunction_, locationParameters);
            object->ProcessEvent(getRotationFunction_, rotationParameters);
            const auto location = *getLocationReturn_
                ->ContainerPtrToValuePtr<FVector>(locationParameters);
            const auto rotation = *getRotationReturn_
                ->ContainerPtrToValuePtr<FQuat>(rotationParameters);
            getLocationFunction_->DestroyStruct(locationParameters);
            getRotationFunction_->DestroyStruct(rotationParameters);
            if (IsSafeTransform(location, rotation))
            {
                const auto duplicate = std::ranges::any_of(
                    candidates,
                    [&](const RespawnCandidate& candidate)
                    {
                        return DistanceSquared(candidate.Location, location) <
                            MinimumAnchorSeparationSquared;
                    });
                if (!duplicate)
                {
                    candidates.push_back({std::move(idText), location, rotation});
                }
            }
        }
        if (candidates.size() < 2) return;

        const auto previous = previousPointByPlayer_.find(playerKey);
        std::vector<std::size_t> eligible;
        for (std::size_t index = 0; index < candidates.size(); ++index)
        {
            if (previous == previousPointByPlayer_.end() ||
                (candidates[index].Id != previous->second.Id &&
                 DistanceSquared(
                     candidates[index].Location,
                     FVector(
                         previous->second.X,
                         previous->second.Y,
                         previous->second.Z)) >= MinimumAnchorSeparationSquared))
            {
                eligible.push_back(index);
            }
        }
        if (eligible.empty()) return;
        std::uniform_int_distribution<std::size_t> distribution(0, eligible.size() - 1);
        const auto& selected = candidates[eligible[distribution(random_)]];

        const auto registerStorageCount = std::max<std::size_t>(
            (registerRespawnPointFunction_->GetPropertiesSize() +
             sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t),
            1);
        std::vector<std::max_align_t> registerStorage(registerStorageCount);
        auto* parameters = registerStorage.data();
        registerRespawnPointFunction_->InitializeStruct(parameters);
        *registerPlayerUidParameter_->ContainerPtrToValuePtr<FGuid>(parameters) = *uid;
        *registerLocationParameter_->ContainerPtrToValuePtr<FVector>(parameters) = selected.Location;
        *registerRotationParameter_->ContainerPtrToValuePtr<FQuat>(parameters) = selected.Rotation;
        networkComponent->ProcessEvent(registerRespawnPointFunction_, parameters);
        registerRespawnPointFunction_->DestroyStruct(parameters);

        if (previousPointByPlayer_.size() >= MaxRememberedPlayers &&
            previous == previousPointByPlayer_.end())
        {
            previousPointByPlayer_.clear();
        }
        previousPointByPlayer_.insert_or_assign(
            playerKey,
            PreviousPoint{
                selected.Id,
                selected.Location.X(),
                selected.Location.Y(),
                selected.Location.Z()});
        Output::send<LogLevel::Verbose>(
            STR("[PalControlNative] Submitted random safe respawn anchor '{}' from {} validated candidates.\n"),
            ensure_str(selected.Id),
            candidates.size());
    }
}

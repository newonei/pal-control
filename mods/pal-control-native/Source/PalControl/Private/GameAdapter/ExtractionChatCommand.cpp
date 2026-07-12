#include "ExtractionChatCommand.hpp"

#include <DynamicOutput/Output.hpp>
#include <Helpers/String.hpp>
#include <Unreal/Core/Containers/FString.hpp>
#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/FStrProperty.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <Unreal/UnrealCoreStructs.hpp>
#include <Unreal/UnrealFlags.hpp>
#include <Unreal/World.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdio>
#include <string_view>
#include <vector>

namespace
{
    using namespace RC;
    using namespace RC::Unreal;

    // Palworld reserves '/' chat input for administrator commands before a
    // normal chat broadcast is emitted. Public commands therefore use '!'
    // (with a bare-text compatibility alias) so non-admin players can reach
    // this server-side hook without gaining administrator privileges.
    constexpr std::string_view ChineseExtractionCommand{
        "\xE6\x92\xA4\xE7\xA6\xBB"};
    constexpr std::string_view ChineseBangExtractionCommand{
        "!\xE6\x92\xA4\xE7\xA6\xBB"};
    constexpr std::array<std::string_view, 2> EnglishExtractionCommands{
        "!extract",
        "extract"};
    constexpr auto ReplyCooldown = std::chrono::seconds(2);
    constexpr auto ReplyEntryLifetime = std::chrono::minutes(1);
    constexpr std::size_t MaxReplyEntries = 128;

    constexpr wchar_t ReplySummary[] =
        L"[\u64A4\u79BB\u70B9] \u5F00\u53D1\u670D\u64A4\u79BB\u70B9 | X 248, Y -504 | \u534A\u5F84 100";
    constexpr wchar_t ReplyRoute[] =
        L"\u8DEF\u7EBF\uFF1A\u524D\u5F80\u5730\u56FE\u5750\u6807 X 248\u3001Y -504\uFF1B\u8FDB\u5165\u64A4\u79BB\u5708\uFF0C\u7F51\u9875\u63D0\u793A\u201C\u5DF2\u8FDB\u5165\u64A4\u79BB\u533A\u57DF\u201D\u540E\u518D\u7ED3\u7B97\u3002";

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

    bool IsExtractionQuery(std::string_view raw)
    {
        while (!raw.empty() && std::isspace(
                   static_cast<unsigned char>(raw.front())))
        {
            raw.remove_prefix(1);
        }
        while (!raw.empty() && std::isspace(
                   static_cast<unsigned char>(raw.back())))
        {
            raw.remove_suffix(1);
        }
        if (raw == ChineseExtractionCommand ||
            raw == ChineseBangExtractionCommand)
        {
            return true;
        }
        for (const auto command : EnglishExtractionCommands)
        {
            if (raw.size() != command.size())
            {
                continue;
            }
            bool matches = true;
            for (std::size_t index = 0; index < raw.size(); ++index)
            {
                if (std::tolower(static_cast<unsigned char>(raw[index])) !=
                    command[index])
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
            {
                return true;
            }
        }
        return false;
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

    bool IsAuthoritativeGameState(UObject* object, UClass* gameStateClass)
    {
        auto* world = object ? object->GetWorld() : nullptr;
        if (!IsLiveObject(object, gameStateClass, world) ||
            world->HasAnyFlags(static_cast<EObjectFlags>(
                RF_ClassDefaultObject |
                RF_ArchetypeObject |
                RF_BeginDestroyed |
                RF_FinishDestroyed)) ||
            world->HasAnyInternalFlags(
                EInternalObjectFlags::Unreachable |
                EInternalObjectFlags::PendingKill |
                EInternalObjectFlags::PendingConstruction))
        {
            return false;
        }
        auto* authorityGameModeProperty = FindTypedProperty<FObjectProperty>(
            world->GetClassPrivate(),
            STR("AuthorityGameMode"));
        auto* authorityGameMode = authorityGameModeProperty
            ? *authorityGameModeProperty
                  ->ContainerPtrToValuePtr<UObject*>(world)
            : nullptr;
        return authorityGameMode &&
            !authorityGameMode->HasAnyFlags(static_cast<EObjectFlags>(
                RF_ClassDefaultObject |
                RF_ArchetypeObject |
                RF_BeginDestroyed |
                RF_FinishDestroyed)) &&
            !authorityGameMode->HasAnyInternalFlags(
                EInternalObjectFlags::Unreachable |
                EInternalObjectFlags::PendingKill |
                EInternalObjectFlags::PendingConstruction);
    }

    bool EqualGuid(const FGuid& left, const FGuid& right)
    {
        return left.A == right.A && left.B == right.B &&
            left.C == right.C && left.D == right.D;
    }

    std::string GuidKey(const FGuid& value)
    {
        std::array<char, 33> buffer{};
        std::snprintf(
            buffer.data(),
            buffer.size(),
            "%08x%08x%08x%08x",
            value.A,
            value.B,
            value.C,
            value.D);
        return buffer.data();
    }

    bool ResolveChatBinding(
        UFunction*& function,
        FStructProperty*& messageParameter,
        FStrProperty*& textProperty,
        FStructProperty*& senderUidProperty,
        UClass*& gameStateClass)
    {
        gameStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalGameStateInGame"));
        function = UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalGameStateInGame:BroadcastChatMessage"));
        if (!gameStateClass || !function || function->GetReturnProperty() ||
            to_string(function->GetFullName()) !=
                "Function /Script/Pal.PalGameStateInGame:BroadcastChatMessage" ||
            !function->HasAnyFunctionFlags(EFunctionFlags::FUNC_Native))
        {
            return false;
        }

        std::size_t parameterCount = 0;
        for (FProperty* parameter : TFieldRange<FProperty>(
                 function,
                 EFieldIterationFlags::IncludeAll))
        {
            if (!parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm))
            {
                continue;
            }
            ++parameterCount;
            if (parameter->GetName() == STR("ChatMessage") &&
                parameter->IsA<FStructProperty>() &&
                !parameter->HasAnyPropertyFlags(static_cast<EPropertyFlags>(
                    CPF_OutParm | CPF_ReturnParm)))
            {
                messageParameter = static_cast<FStructProperty*>(parameter);
            }
        }
        auto* chatStruct = messageParameter
            ? messageParameter->GetStruct().Get()
            : nullptr;
        if (parameterCount != 1 || !chatStruct ||
            chatStruct->GetName() != STR("PalChatMessage"))
        {
            return false;
        }
        textProperty = FindTypedProperty<FStrProperty>(chatStruct, STR("Message"));
        senderUidProperty = FindTypedProperty<FStructProperty>(
            chatStruct,
            STR("SenderPlayerUId"));
        return textProperty && senderUidProperty &&
            senderUidProperty->GetStruct() &&
            senderUidProperty->GetStruct()->GetName() == STR("Guid");
    }

    bool ResolveIdentityBinding(
        UClass*& controllerClass,
        UClass*& playerStateClass,
        FObjectProperty*& controllerPlayerState,
        FStructProperty*& playerStateUid)
    {
        controllerClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalPlayerController"));
        playerStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalPlayerState"));
        controllerPlayerState = FindTypedProperty<FObjectProperty>(
            controllerClass,
            STR("PlayerState"));
        playerStateUid = FindTypedProperty<FStructProperty>(
            playerStateClass,
            STR("PlayerUId"));
        return controllerClass && playerStateClass && controllerPlayerState &&
            playerStateUid && playerStateUid->GetStruct() &&
            playerStateUid->GetStruct()->GetName() == STR("Guid");
    }

    bool ResolveClientMessageBinding(
        UFunction*& function,
        FStrProperty*& textProperty,
        FNameProperty*& typeProperty,
        FFloatProperty*& lifetimeProperty)
    {
        function = UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr,
            nullptr,
            STR("/Script/Engine.PlayerController:ClientMessage"));
        constexpr auto RequiredFlags = static_cast<EFunctionFlags>(
            FUNC_Public |
            FUNC_Native |
            FUNC_Event |
            FUNC_Net |
            FUNC_NetReliable |
            FUNC_NetClient);
        constexpr auto RejectedFlags = static_cast<EFunctionFlags>(
            FUNC_Static | FUNC_NetServer | FUNC_NetMulticast);
        if (!function || function->GetPropertiesSize() != 32 ||
            function->GetReturnProperty() ||
            to_string(function->GetFullName()) !=
                "Function /Script/Engine.PlayerController:ClientMessage" ||
            !function->HasAllFunctionFlags(RequiredFlags) ||
            function->HasAnyFunctionFlags(RejectedFlags))
        {
            return false;
        }

        std::size_t parameterCount = 0;
        for (FProperty* parameter : TFieldRange<FProperty>(
                 function,
                 EFieldIterationFlags::IncludeAll))
        {
            if (!parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm))
            {
                continue;
            }
            ++parameterCount;
            if (parameter->GetName() == STR("S") &&
                parameter->IsA<FStrProperty>())
            {
                textProperty = static_cast<FStrProperty*>(parameter);
            }
            else if (parameter->GetName() == STR("Type") &&
                     parameter->IsA<FNameProperty>())
            {
                typeProperty = static_cast<FNameProperty*>(parameter);
            }
            else if (parameter->GetName() == STR("MsgLifeTime") &&
                     parameter->IsA<FFloatProperty>())
            {
                lifetimeProperty = static_cast<FFloatProperty*>(parameter);
            }
        }
        return parameterCount == 3 && textProperty && typeProperty &&
            lifetimeProperty;
    }
}

namespace PalControl::Game
{
    ExtractionChatCommand::~ExtractionChatCommand()
    {
        Uninstall();
    }

    bool ExtractionChatCommand::Install()
    {
        using namespace RC;
        using namespace RC::Unreal;
        if (IsInstalled())
        {
            return true;
        }
        if (!ResolveChatBinding(
                chatFunction_,
                chatMessageParameter_,
                chatTextProperty_,
                senderPlayerUidProperty_,
                gameStateClass_) ||
            !ResolveIdentityBinding(
                playerControllerClass_,
                playerStateClass_,
                controllerPlayerStateProperty_,
                playerStateUidProperty_) ||
            !ResolveClientMessageBinding(
                clientMessageFunction_,
                clientMessageTextProperty_,
                clientMessageTypeProperty_,
                clientMessageLifetimeProperty_))
        {
            Output::send<LogLevel::Error>(
                STR("[PalControlNative] Extraction chat command disabled: native chat, identity, or ClientMessage schema mismatch.\n"));
            return false;
        }

        try
        {
            hookId_ = chatFunction_->RegisterPostHook(
                [](UnrealScriptFunctionCallableContext& context, void* customData)
                {
                    static_cast<ExtractionChatCommand*>(customData)
                        ->OnBroadcastChat(context);
                },
                this);
        }
        catch (...)
        {
            hookId_ = -1;
            Output::send<LogLevel::Error>(
                STR("[PalControlNative] Extraction chat command disabled: BroadcastChatMessage hook registration failed.\n"));
            return false;
        }

        Output::send<LogLevel::Verbose>(
            STR("[PalControlNative] Read-only public extraction chat aliases registered (!extract and non-slash Chinese equivalents).\n"));
        return true;
    }

    void ExtractionChatCommand::Uninstall() noexcept
    {
        if (chatFunction_ && hookId_ >= 0)
        {
            try
            {
                chatFunction_->UnregisterHook(hookId_);
            }
            catch (...)
            {
                // The game is already tearing down. Never throw from mod unload.
            }
        }
        hookId_ = -1;
        lastReplies_.clear();
    }

    bool ExtractionChatCommand::IsInstalled() const noexcept
    {
        return chatFunction_ && hookId_ >= 0;
    }

    void ExtractionChatCommand::OnBroadcastChat(
        RC::Unreal::UnrealScriptFunctionCallableContext& context)
    {
        using namespace RC;
        using namespace RC::Unreal;
        auto* gameState = context.Context;
        auto* locals = context.TheStack.Locals();
        if (!locals || !IsAuthoritativeGameState(gameState, gameStateClass_))
        {
            return;
        }
        auto* chatMessage = chatMessageParameter_
            ->ContainerPtrToValuePtr<uint8>(locals);
        if (!chatMessage)
        {
            return;
        }
        auto* text = chatTextProperty_
            ->ContainerPtrToValuePtr<FString>(chatMessage);
        auto* senderUid = senderPlayerUidProperty_
            ->ContainerPtrToValuePtr<FGuid>(chatMessage);
        if (!text || !senderUid || !senderUid->is_valid())
        {
            return;
        }
        const auto message = to_string(**text);
        if (!IsExtractionQuery(message))
        {
            return;
        }

        const auto senderKey = GuidKey(*senderUid);
        if (IsRateLimited(senderKey))
        {
            return;
        }
        auto* controller = ResolveUniqueSenderController(gameState, *senderUid);
        if (!controller)
        {
            return;
        }
        (void)SendReply(controller);
    }

    bool ExtractionChatCommand::IsRateLimited(const std::string& senderKey)
    {
        const auto now = std::chrono::steady_clock::now();
        if (auto found = lastReplies_.find(senderKey);
            found != lastReplies_.end() && now - found->second < ReplyCooldown)
        {
            return true;
        }
        if (lastReplies_.size() >= MaxReplyEntries)
        {
            std::erase_if(lastReplies_, [now](const auto& entry)
            {
                return now - entry.second >= ReplyEntryLifetime;
            });
            if (lastReplies_.size() >= MaxReplyEntries)
            {
                lastReplies_.clear();
            }
        }
        lastReplies_.insert_or_assign(senderKey, now);
        return false;
    }

    RC::Unreal::UObject* ExtractionChatCommand::ResolveUniqueSenderController(
        RC::Unreal::UObject* gameState,
        const RC::Unreal::FGuid& senderPlayerUid) const
    {
        using namespace RC::Unreal;
        auto* world = gameState ? gameState->GetWorld() : nullptr;
        if (!world)
        {
            return nullptr;
        }

        std::vector<UObject*> controllers;
        UObjectGlobals::FindAllOf(STR("PalPlayerController"), controllers);
        UObject* match = nullptr;
        for (auto* controller : controllers)
        {
            if (!IsLiveObject(controller, playerControllerClass_, world))
            {
                continue;
            }
            auto* playerState = *controllerPlayerStateProperty_
                ->ContainerPtrToValuePtr<UObject*>(controller);
            if (!IsLiveObject(playerState, playerStateClass_, world))
            {
                continue;
            }
            auto* uid = playerStateUidProperty_
                ->ContainerPtrToValuePtr<FGuid>(playerState);
            if (!uid || !EqualGuid(*uid, senderPlayerUid))
            {
                continue;
            }
            if (match)
            {
                return nullptr;
            }
            match = controller;
        }
        return match;
    }

    bool ExtractionChatCommand::SendReply(RC::Unreal::UObject* controller) const
    {
        using namespace RC::Unreal;
        if (!controller || !clientMessageFunction_)
        {
            return false;
        }
        const auto sendLine = [&](const wchar_t* line)
        {
            std::vector<uint8> parameters(
                std::max<std::size_t>(
                    clientMessageFunction_->GetPropertiesSize(),
                    1),
                0);
            clientMessageFunction_->InitializeStruct(parameters.data());
            *clientMessageTextProperty_
                 ->ContainerPtrToValuePtr<FString>(parameters.data()) =
                FString(line);
            *clientMessageTypeProperty_
                 ->ContainerPtrToValuePtr<FName>(parameters.data()) =
                FName(STR("Event"));
            *clientMessageLifetimeProperty_
                 ->ContainerPtrToValuePtr<float>(parameters.data()) = 15.0F;
            controller->ProcessEvent(clientMessageFunction_, parameters.data());
            clientMessageFunction_->DestroyStruct(parameters.data());
        };
        sendLine(ReplySummary);
        sendLine(ReplyRoute);
        return true;
    }
}

#include "Bridge/NamedPipeServer.hpp"
#include "GameAdapter/ExtractionChatCommand.hpp"
#include "GameAdapter/PalworldGameAdapter.hpp"
#include "Runtime/ExecutableIdentity.hpp"
#if PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN
#include "GameAdapter/RandomSafeRespawn.hpp"
#endif

#include <DynamicOutput/Output.hpp>
#include <Helpers/String.hpp>
#include <Mod/CppUserModBase.hpp>
#include <Unreal/Hooks/Hooks.hpp>

#include <memory>
#include <string>

#ifndef PALCONTROL_TARGET_GAME_BUILD
#define PALCONTROL_TARGET_GAME_BUILD "unknown"
#endif
#ifndef PALCONTROL_TARGET_STEAM_BUILD
#define PALCONTROL_TARGET_STEAM_BUILD "unknown"
#endif
#ifndef PALCONTROL_TARGET_EXECUTABLE_SHA256
#define PALCONTROL_TARGET_EXECUTABLE_SHA256 ""
#endif
#ifndef PALCONTROL_TARGET_EXECUTABLE_SIZE
#define PALCONTROL_TARGET_EXECUTABLE_SIZE 0
#endif
#ifndef PALCONTROL_NATIVE_MOD_VERSION
#define PALCONTROL_NATIVE_MOD_VERSION "0.0.0-unconfigured"
#endif
#ifndef PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256
#define PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256 ""
#endif
#ifndef PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE
#define PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE 0
#endif
#ifndef PALCONTROL_ENABLE_WRITE_CAPABILITIES
#define PALCONTROL_ENABLE_WRITE_CAPABILITIES 0
#endif

namespace PalControl
{
    using namespace RC;

    class PalControlNativeMod final : public CppUserModBase
    {
    public:
        PalControlNativeMod()
        {
            ModVersion = to_wstring(PALCONTROL_NATIVE_MOD_VERSION);
            ModName = STR("PalControlNative");
            ModAuthors = STR("Pal Control");
            ModDescription = STR("Local Named Pipe bridge for Palworld server control.");
            Output::send<LogLevel::Verbose>(STR("[PalControlNative] Loaded with guarded inventory and Pal mutations.\n"));
        }

        ~PalControlNativeMod() override
        {
            if (engineTickHook_ != Unreal::Hook::ERROR_ID)
            {
                Unreal::Hook::UnregisterCallback(engineTickHook_);
                engineTickHook_ = Unreal::Hook::ERROR_ID;
            }
            extractionChatCommand_.Uninstall();
#if PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN
            randomSafeRespawn_.Uninstall();
#endif
            bridge_.Stop();
        }

        auto on_unreal_init() -> void override
        {
            const auto executable = Runtime::ReadCurrentExecutableIdentity();
            const auto nativeModule = Runtime::ReadCurrentModuleIdentity();
            const auto ue4ssModule = Runtime::ReadLoadedModuleIdentity(L"UE4SS.dll");
            if (!executable ||
                executable->Sha256 != PALCONTROL_TARGET_EXECUTABLE_SHA256 ||
                executable->Size != PALCONTROL_TARGET_EXECUTABLE_SIZE ||
                !nativeModule || !ue4ssModule ||
                ue4ssModule->Sha256 != PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256 ||
                ue4ssModule->Size != PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE)
            {
                Output::send<LogLevel::Error>(
                    STR("[PalControlNative] Runtime executable/UE4SS/module identity did not match the reviewed target; no hook or bridge was registered.\n"));
                return;
            }

            engineTickHook_ = Unreal::Hook::RegisterEngineTickPreCallback(
                [this](auto&, Unreal::UEngine*, float, bool)
                {
                    for (int processed = 0; processed < 4; ++processed)
                    {
                        if (!bridge_.TryExecuteNextCommand(
                                [this](const Contracts::CommandEnvelope& command)
                                {
                                    return adapter_.Execute(command);
                                }))
                        {
                            break;
                        }
                    }
                },
                {false, true, STR("PalControlNative"), STR("ReadOnlyCommandPump")});

            if (engineTickHook_ == Unreal::Hook::ERROR_ID)
            {
                Output::send<LogLevel::Error>(
                    STR("[PalControlNative] Engine Tick hook registration failed; bridge not started.\n"));
                return;
            }

#if PALCONTROL_ENABLE_WRITE_CAPABILITIES
            (void)extractionChatCommand_.Install();
#if PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN
            (void)randomSafeRespawn_.Install();
#endif
#endif
            bridge_.Start(Bridge::BridgeIdentity{
                .GameBuild = PALCONTROL_TARGET_GAME_BUILD,
                .SteamBuild = PALCONTROL_TARGET_STEAM_BUILD,
                .ModVersion = PALCONTROL_NATIVE_MOD_VERSION,
                .RuntimeExecutableSha256 = executable->Sha256,
                .RuntimeExecutableSize = executable->Size,
                .RuntimeNativeDllSha256 = nativeModule->Sha256,
                .RuntimeNativeDllSize = nativeModule->Size,
                .RuntimeUe4ssDllSha256 = ue4ssModule->Sha256,
                .RuntimeUe4ssDllSize = ue4ssModule->Size,
                .WriteEnabled = PALCONTROL_ENABLE_WRITE_CAPABILITIES != 0
            });
            Output::send<LogLevel::Verbose>(
                STR("[PalControlNative] Runtime-bound read-only bridge ready; probes run on Engine Tick.\n"));
        }

    private:
        Bridge::NamedPipeServer bridge_;
        Game::PalworldGameAdapter adapter_;
        Game::ExtractionChatCommand extractionChatCommand_;
#if PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN
        Game::RandomSafeRespawn randomSafeRespawn_;
#endif
        Unreal::Hook::GlobalCallbackId engineTickHook_{Unreal::Hook::ERROR_ID};
    };
}

#define MOD_EXPORT __declspec(dllexport)

extern "C"
{
    MOD_EXPORT RC::CppUserModBase* start_mod()
    {
        return new PalControl::PalControlNativeMod();
    }

    MOD_EXPORT void uninstall_mod(RC::CppUserModBase* mod)
    {
        delete mod;
    }
}

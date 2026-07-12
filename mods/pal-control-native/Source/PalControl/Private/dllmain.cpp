#include "Bridge/NamedPipeServer.hpp"
#include "GameAdapter/ExtractionChatCommand.hpp"
#include "GameAdapter/PalworldGameAdapter.hpp"
#if PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN
#include "GameAdapter/RandomSafeRespawn.hpp"
#endif

#include <DynamicOutput/Output.hpp>
#include <Mod/CppUserModBase.hpp>
#include <Unreal/Hooks/Hooks.hpp>

#include <memory>
#include <string>

#ifndef PALCONTROL_TARGET_GAME_BUILD
#define PALCONTROL_TARGET_GAME_BUILD "unknown"
#endif

namespace PalControl
{
    using namespace RC;

    class PalControlNativeMod final : public CppUserModBase
    {
    public:
        PalControlNativeMod()
        {
            ModVersion = STR("0.3.0-dev.35");
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
            engineTickHook_ = Unreal::Hook::RegisterEngineTickPreCallback(
                [this](auto&, Unreal::UEngine*, float, bool)
                {
                    Contracts::CommandEnvelope command{};
                    for (int processed = 0;
                         processed < 4 && bridge_.TryDequeueCommand(command);
                         ++processed)
                    {
                        bridge_.EnqueueResult(adapter_.Execute(command));
                    }
                },
                {false, true, STR("PalControlNative"), STR("ReadOnlyCommandPump")});

            if (engineTickHook_ == Unreal::Hook::ERROR_ID)
            {
                Output::send<LogLevel::Error>(
                    STR("[PalControlNative] Engine Tick hook registration failed; bridge not started.\n"));
                return;
            }

            (void)extractionChatCommand_.Install();
#if PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN
            (void)randomSafeRespawn_.Install();
#endif
            bridge_.Start(PALCONTROL_TARGET_GAME_BUILD, "0.3.0-dev.35");
            Output::send<LogLevel::Verbose>(
                STR("[PalControlNative] Duplex bridge ready; read-only player probe runs on Engine Tick.\n"));
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

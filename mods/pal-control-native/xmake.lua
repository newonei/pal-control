local project_name = "PalControlNative"

target(project_name)
    add_rules("ue4ss.mod")
    add_defines(
        "UNICODE",
        "_UNICODE",
        "NOMINMAX",
        "WIN32_LEAN_AND_MEAN",
        "PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN=1",
        'PALCONTROL_TARGET_GAME_BUILD="v1.0.0.100427"'
    )
    add_includedirs(
        "Source/PalControl/Public",
        "Source/PalControl/Private"
    )
    add_files(
        "Source/PalControl/Private/dllmain.cpp",
        "Source/PalControl/Private/Bridge/NamedPipeServer.cpp",
        "Source/PalControl/Private/GameAdapter/ExtractionChatCommand.cpp",
        "Source/PalControl/Private/GameAdapter/RandomSafeRespawn.cpp",
        "Source/PalControl/Private/GameAdapter/PalworldGameAdapter.cpp"
    )
    add_syslinks("Advapi32")

local project_name = "PalControlNative"

target(project_name)
    add_rules("ue4ss.mod")
    add_cxxflags("/Brepro")
    add_ldflags("/Brepro", "/PDBALTPATH:PalControlNative.pdb")
    add_defines(
        "UNICODE",
        "_UNICODE",
        "NOMINMAX",
        "WIN32_LEAN_AND_MEAN",
        "PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN=0",
        "PALCONTROL_ENABLE_WRITE_CAPABILITIES=0",
        'PALCONTROL_TARGET_GAME_BUILD="v1.0.1.100619"',
        'PALCONTROL_TARGET_STEAM_BUILD="24181105"',
        'PALCONTROL_TARGET_EXECUTABLE_SHA256="c812def687b7c13be91d22c2ca6ed48389b6260e4d72a93dddfea274be76419e"',
        "PALCONTROL_TARGET_EXECUTABLE_SIZE=152378880",
        'PALCONTROL_NATIVE_MOD_VERSION="0.3.0-dev.37-ro"',
        'PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256="afc83a57a30c3d44bc0d432222f24857b0c3788738b8a1975dfeef746edab798"',
        "PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE=16494592",
        'PALCONTROL_PIPE_NAME="pal-control.local.v1"',
        'PALCONTROL_CONTROL_API_SERVICE_SID="S-1-5-80-993063732-716721481-3728868849-3499021384-1810321418"'
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
        "Source/PalControl/Private/GameAdapter/PalworldGameAdapter.cpp",
        "Source/PalControl/Private/Runtime/ExecutableIdentity.cpp"
    )
    add_syslinks("Advapi32", "Bcrypt")

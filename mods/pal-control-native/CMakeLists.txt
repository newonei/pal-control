cmake_minimum_required(VERSION 3.22)

set(TARGET PalControlNative)
project(${TARGET} LANGUAGES CXX)

option(
    PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN
    "Enable the experimental random safe-respawn hook"
    ON)

add_library(${TARGET} SHARED
    "Source/PalControl/Private/dllmain.cpp"
    "Source/PalControl/Private/Bridge/NamedPipeServer.cpp"
    "Source/PalControl/Private/GameAdapter/ExtractionChatCommand.cpp"
    "Source/PalControl/Private/GameAdapter/RandomSafeRespawn.cpp"
    "Source/PalControl/Private/GameAdapter/PalworldGameAdapter.cpp"
)

target_compile_features(${TARGET} PRIVATE cxx_std_23)
target_compile_definitions(${TARGET} PRIVATE
    UNICODE
    _UNICODE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    PALCONTROL_TARGET_GAME_BUILD="v1.0.0.100427"
    PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN=$<BOOL:${PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN}>
)

target_include_directories(${TARGET} PRIVATE
    "Source/PalControl/Public"
    "Source/PalControl/Private"
)

# The official UE4SS C++ template defines the UE4SS target in its parent build.
target_link_libraries(${TARGET} PRIVATE UE4SS Advapi32)

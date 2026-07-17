cmake_minimum_required(VERSION 3.22)

set(TARGET PalControlNative)
project(${TARGET} LANGUAGES CXX)

option(
    PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN
    "Enable the experimental random safe-respawn hook"
    OFF)
option(
    PALCONTROL_ENABLE_WRITE_CAPABILITIES
    "Advertise and execute Native mutation operations"
    OFF)
set(
    PALCONTROL_TARGET_GAME_BUILD
    "unknown"
    CACHE STRING
    "Exact reviewed Palworld game version")
set(
    PALCONTROL_TARGET_STEAM_BUILD
    "unknown"
    CACHE STRING
    "Exact reviewed Steam build id")
set(
    PALCONTROL_TARGET_EXECUTABLE_SHA256
    ""
    CACHE STRING
    "Lowercase SHA-256 of the exact reviewed PalServer executable")
set(
    PALCONTROL_TARGET_EXECUTABLE_SIZE
    "0"
    CACHE STRING
    "Byte length of the exact reviewed PalServer executable")
set(
    PALCONTROL_NATIVE_MOD_VERSION
    "0.0.0-unconfigured"
    CACHE STRING
    "Version advertised by this Native build")
set(
    PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256
    ""
    CACHE STRING
    "Lowercase SHA-256 of the exact reviewed UE4SS runtime DLL")
set(
    PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE
    "0"
    CACHE STRING
    "Byte length of the exact reviewed UE4SS runtime DLL")
set(
    PALCONTROL_PIPE_NAME
    "pal-control.local.v1"
    CACHE STRING
    "Local Native Bridge pipe name")
set(
    PALCONTROL_CONTROL_API_SERVICE_SID
    "S-1-5-80-993063732-716721481-3728868849-3499021384-1810321418"
    CACHE STRING
    "Windows service SID allowed to connect to the Native Bridge")

add_library(${TARGET} SHARED
    "Source/PalControl/Private/dllmain.cpp"
    "Source/PalControl/Private/Bridge/NamedPipeServer.cpp"
    "Source/PalControl/Private/GameAdapter/ExtractionChatCommand.cpp"
    "Source/PalControl/Private/GameAdapter/RandomSafeRespawn.cpp"
    "Source/PalControl/Private/GameAdapter/PalworldGameAdapter.cpp"
    "Source/PalControl/Private/Runtime/ExecutableIdentity.cpp"
)

target_compile_features(${TARGET} PRIVATE cxx_std_23)
if(MSVC)
    # Remove wall-clock/link-path entropy from the DLL. The PDB remains an
    # external build artifact, while the PE embeds one stable relative name.
    target_compile_options(${TARGET} PRIVATE /Brepro)
    target_link_options(${TARGET} PRIVATE /Brepro /PDBALTPATH:PalControlNative.pdb)
endif()
target_compile_definitions(${TARGET} PRIVATE
    UNICODE
    _UNICODE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    PALCONTROL_TARGET_GAME_BUILD="${PALCONTROL_TARGET_GAME_BUILD}"
    PALCONTROL_TARGET_STEAM_BUILD="${PALCONTROL_TARGET_STEAM_BUILD}"
    PALCONTROL_TARGET_EXECUTABLE_SHA256="${PALCONTROL_TARGET_EXECUTABLE_SHA256}"
    PALCONTROL_TARGET_EXECUTABLE_SIZE=${PALCONTROL_TARGET_EXECUTABLE_SIZE}
    PALCONTROL_NATIVE_MOD_VERSION="${PALCONTROL_NATIVE_MOD_VERSION}"
    PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256="${PALCONTROL_TARGET_UE4SS_RUNTIME_SHA256}"
    PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE=${PALCONTROL_TARGET_UE4SS_RUNTIME_SIZE}
    PALCONTROL_PIPE_NAME="${PALCONTROL_PIPE_NAME}"
    PALCONTROL_CONTROL_API_SERVICE_SID="${PALCONTROL_CONTROL_API_SERVICE_SID}"
    PALCONTROL_ENABLE_WRITE_CAPABILITIES=$<BOOL:${PALCONTROL_ENABLE_WRITE_CAPABILITIES}>
    PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN=$<BOOL:${PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN}>
)

target_include_directories(${TARGET} PRIVATE
    "Source/PalControl/Public"
    "Source/PalControl/Private"
)

# The official UE4SS C++ template defines the UE4SS target in its parent build.
target_link_libraries(${TARGET} PRIVATE UE4SS Advapi32 Bcrypt)

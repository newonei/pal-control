#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace PalControl::Runtime
{
    struct ExecutableIdentity
    {
        std::string Sha256;
        std::uint64_t Size = 0;
    };

    // Hash the executable that hosts this DLL. The caller uses the result to
    // bind a Native build to one reviewed PalServer binary before registering
    // any Unreal hook or opening the local bridge.
    [[nodiscard]] std::optional<ExecutableIdentity>
        ReadCurrentExecutableIdentity();

    // Report the exact on-disk modules loaded into the verified host. These
    // values are compared outside the DLL because a binary cannot embed its
    // own final SHA-256 without changing that digest.
    [[nodiscard]] std::optional<ExecutableIdentity>
        ReadCurrentModuleIdentity();
    [[nodiscard]] std::optional<ExecutableIdentity>
        ReadLoadedModuleIdentity(const wchar_t* moduleName);

    // Compute a lowercase SHA-256 for an already bounded protocol payload.
    // Native command parsing uses this to bind requestHash to the exact raw
    // JSON that will reach the game-thread adapter.
    [[nodiscard]] std::optional<std::string>
        ComputeSha256Hex(std::string_view value);
}

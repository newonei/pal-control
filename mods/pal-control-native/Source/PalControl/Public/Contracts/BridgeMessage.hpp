#pragma once

#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

namespace PalControl::Contracts
{
    inline constexpr const char* ProtocolVersion = "1.1";

    struct Hello
    {
        std::string GameBuild;
        std::string SteamBuild;
        std::string ModVersion;
        std::string RuntimeExecutableSha256;
        std::uint64_t RuntimeExecutableSize = 0;
        std::string RuntimeNativeDllSha256;
        std::uint64_t RuntimeNativeDllSize = 0;
        std::string RuntimeUe4ssDllSha256;
        std::uint64_t RuntimeUe4ssDllSize = 0;
        bool RuntimeIdentityVerified = false;
        bool WriteEnabled = false;
        std::vector<std::string> Capabilities;
        std::unordered_map<std::string, bool> Probes;
    };

    struct CommandEnvelope
    {
        std::string CommandId;
        std::string IdempotencyKey;
        std::string RequestHash;
        std::string ServerId;
        std::string ActorId;
        std::string Operation;
        std::string Deadline;
        std::uint64_t DeadlineUtcFileTimeTicks = 0;
        bool RequestHashVerified = false;
        std::uint64_t ExpectedRevision = 0;
        std::string Reason;
        std::string PayloadJson;
    };

    enum class CommandState
    {
        Succeeded,
        Failed,
        Uncertain
    };

    struct CommandResult
    {
        std::string CommandId;
        CommandState State = CommandState::Failed;
        std::uint64_t ObservedRevision = 0;
        std::string DataJson;
        std::string ErrorCode;
        std::string ErrorMessage;
    };
}

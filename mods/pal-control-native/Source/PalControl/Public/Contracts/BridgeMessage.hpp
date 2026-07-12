#pragma once

#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

namespace PalControl::Contracts
{
    inline constexpr const char* ProtocolVersion = "1.0";

    struct Hello
    {
        std::string GameBuild;
        std::string ModVersion;
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

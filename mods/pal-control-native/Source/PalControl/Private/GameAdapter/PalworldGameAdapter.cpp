#include "GameAdapter/PalworldGameAdapter.hpp"

#include <Windows.h>

#include <Helpers/String.hpp>
#include <Unreal/Core/Containers/FString.hpp>
#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/FStrProperty.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/Engine/UDataTable.hpp>
#include <Unreal/FText.hpp>
#include <Unreal/Property/FEnumProperty.hpp>
#include <Unreal/Property/FTextProperty.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <Unreal/UnrealCoreStructs.hpp>
#include <Unreal/UnrealFlags.hpp>
#include <Unreal/World.hpp>

#include <glaze/glaze.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstddef>
#include <cmath>
#include <cstdio>
#include <limits>
#include <optional>
#include <ranges>
#include <set>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace PalControl::Game::Detail
{
    struct InventoryMutationPayload
    {
        std::string ownerPlayerId;
        std::string containerId;
        std::string containerKind;
        std::string itemId;
        std::int32_t slotIndex = -1;
        std::int32_t expectedQuantity = -1;
        std::int32_t quantity = -1;
        bool dryRun = true;
    };

    struct InventoryConsumeItemPayload
    {
        std::string itemId;
        std::int32_t quantity = 0;
    };

    struct InventoryExpectedSlotPayload
    {
        std::int32_t slotIndex = -1;
        std::string itemId;
        std::int32_t quantity = -1;
    };

    struct InventoryExpectedContainerPayload
    {
        std::string containerKind;
        std::string containerId;
        std::vector<InventoryExpectedSlotPayload> slots;
    };

    struct InventoryConsumePayload
    {
        std::string ownerPlayerId;
        std::vector<InventoryConsumeItemPayload> items;
        std::vector<InventoryExpectedContainerPayload> expectedContainers;
    };

    struct PalMutationPayload
    {
        std::string instanceId;
        std::string ownerPlayerId;
        std::optional<std::string> nickname;
        std::optional<bool> favorite;
        std::optional<std::int32_t> passiveSkillIndex;
        std::optional<std::string> expectedPassiveSkill;
        std::optional<std::string> passiveSkill;
        std::optional<std::vector<std::string>> expectedPassiveSkills;
        std::optional<std::vector<std::string>> passiveSkills;
        std::optional<std::vector<std::string>> equippedActiveSkills;
        bool dryRun = true;
    };

    struct PlayerProgressionMutationPayload
    {
        std::string ownerPlayerId;
        std::optional<std::int32_t> addExperience;
        std::optional<std::int32_t> targetLevel;
        std::optional<std::int32_t> grantStatusPoints;
        std::optional<std::int32_t> grantTechnologyPoints;
        std::optional<std::int32_t> grantAncientTechnologyPoints;
        std::optional<std::string> allocateStatusId;
        std::optional<std::int32_t> allocateStatusPoints;
        bool dryRun = true;
    };

    struct OverlayAnnouncementPayload
    {
        std::string title;
        std::string body;
        std::string message;
        std::string audience;
        std::optional<double> lifetimeSeconds;
    };

    struct InGameNotificationAudiencePayload
    {
        std::string type;
        std::optional<std::vector<std::string>> ids;
    };

    struct InGameNotificationPayload
    {
        std::string deliveryId;
        std::string schemaVersion;
        std::string preset;
        glz::raw_json parameters;
        InGameNotificationAudiencePayload audience;
    };

    struct BossDefeatNotificationParameters
    {
        std::optional<std::int32_t> technologyPoint;
        std::optional<std::int32_t> delaySeconds;
    };

    struct ExpNotificationParameters
    {
        std::optional<std::int32_t> rewardExp;
    };
}

namespace
{
    constexpr std::size_t MaxReturnedObjects = 128;
    constexpr std::size_t MaxReturnedProperties = 512;
    constexpr std::size_t MaxReturnedFunctions = 256;
    constexpr std::size_t MaxPropertiesPerInventoryType = 256;
    constexpr std::size_t MaxInventoryObjects = 64;
    constexpr std::size_t MaxSlotsPerContainer = 256;
    constexpr std::size_t MaxPropertiesPerPalType = 384;
    constexpr std::size_t MaxPalObjectsPerType = 32;
    constexpr std::size_t MaxReturnedPals = 512;
    constexpr std::size_t MaxReturnedPassiveSkills = 32;
    constexpr std::size_t MaxReturnedActiveSkills = 64;
    constexpr std::size_t MaxReturnedSkillCatalogEntries = 2048;

    std::string EscapeJson(std::string_view value)
    {
        std::string escaped;
        escaped.reserve(value.size());
        for (const char character : value)
        {
            switch (character)
            {
                case '\\': escaped += "\\\\"; break;
                case '"': escaped += "\\\""; break;
                case '\n': escaped += "\\n"; break;
                case '\r': escaped += "\\r"; break;
                case '\t': escaped += "\\t"; break;
                default: escaped += character; break;
            }
        }
        return escaped;
    }

    std::string UtcNow()
    {
        SYSTEMTIME time{};
        GetSystemTime(&time);
        std::array<char, 32> buffer{};
        std::snprintf(
            buffer.data(),
            buffer.size(),
            "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
            time.wYear,
            time.wMonth,
            time.wDay,
            time.wHour,
            time.wMinute,
            time.wSecond,
            time.wMilliseconds);
        return buffer.data();
    }

    std::optional<bool> IsDeadlineExpired(std::string_view value)
    {
        const auto parseDigits = [&](std::size_t offset, std::size_t length)
            -> std::optional<unsigned int>
        {
            if (offset + length > value.size())
            {
                return std::nullopt;
            }
            unsigned int result = 0;
            for (std::size_t index = 0; index < length; ++index)
            {
                const auto character = static_cast<unsigned char>(
                    value[offset + index]);
                if (!std::isdigit(character))
                {
                    return std::nullopt;
                }
                result = result * 10 + (character - '0');
            }
            return result;
        };
        if (value.size() < 20 || value[4] != '-' || value[7] != '-' ||
            value[10] != 'T' || value[13] != ':' || value[16] != ':')
        {
            return std::nullopt;
        }
        const auto year = parseDigits(0, 4);
        const auto month = parseDigits(5, 2);
        const auto day = parseDigits(8, 2);
        const auto hour = parseDigits(11, 2);
        const auto minute = parseDigits(14, 2);
        const auto second = parseDigits(17, 2);
        if (!year || !month || !day || !hour || !minute || !second)
        {
            return std::nullopt;
        }

        std::size_t zoneOffset = 19;
        unsigned int milliseconds = 0;
        if (zoneOffset < value.size() && value[zoneOffset] == '.')
        {
            ++zoneOffset;
            std::size_t fractionalDigits = 0;
            while (zoneOffset < value.size() &&
                   std::isdigit(static_cast<unsigned char>(value[zoneOffset])))
            {
                if (fractionalDigits < 3)
                {
                    milliseconds = milliseconds * 10 +
                        static_cast<unsigned int>(value[zoneOffset] - '0');
                }
                ++fractionalDigits;
                ++zoneOffset;
            }
            if (fractionalDigits == 0)
            {
                return std::nullopt;
            }
            while (fractionalDigits++ < 3)
            {
                milliseconds *= 10;
            }
        }

        int offsetMinutes = 0;
        if (zoneOffset < value.size() && value[zoneOffset] == 'Z')
        {
            if (++zoneOffset != value.size())
            {
                return std::nullopt;
            }
        }
        else if (zoneOffset + 6 == value.size() &&
                 (value[zoneOffset] == '+' || value[zoneOffset] == '-') &&
                 value[zoneOffset + 3] == ':')
        {
            const auto offsetHours = parseDigits(zoneOffset + 1, 2);
            const auto offsetMinutePart = parseDigits(zoneOffset + 4, 2);
            if (!offsetHours || !offsetMinutePart || *offsetHours > 14 ||
                *offsetMinutePart > 59 ||
                (*offsetHours == 14 && *offsetMinutePart != 0))
            {
                return std::nullopt;
            }
            offsetMinutes = static_cast<int>(*offsetHours * 60 +
                *offsetMinutePart);
            if (value[zoneOffset] == '-')
            {
                offsetMinutes = -offsetMinutes;
            }
        }
        else
        {
            return std::nullopt;
        }

        SYSTEMTIME deadlineSystemTime{};
        deadlineSystemTime.wYear = static_cast<WORD>(*year);
        deadlineSystemTime.wMonth = static_cast<WORD>(*month);
        deadlineSystemTime.wDay = static_cast<WORD>(*day);
        deadlineSystemTime.wHour = static_cast<WORD>(*hour);
        deadlineSystemTime.wMinute = static_cast<WORD>(*minute);
        deadlineSystemTime.wSecond = static_cast<WORD>(*second);
        deadlineSystemTime.wMilliseconds = static_cast<WORD>(milliseconds);
        FILETIME deadlineFileTime{};
        if (!SystemTimeToFileTime(&deadlineSystemTime, &deadlineFileTime))
        {
            return std::nullopt;
        }

        ULARGE_INTEGER deadlineTicks{};
        deadlineTicks.LowPart = deadlineFileTime.dwLowDateTime;
        deadlineTicks.HighPart = deadlineFileTime.dwHighDateTime;
        constexpr std::int64_t TicksPerMinute = 60LL * 10'000'000LL;
        const auto utcTicks = static_cast<std::int64_t>(deadlineTicks.QuadPart) -
            static_cast<std::int64_t>(offsetMinutes) * TicksPerMinute;
        if (utcTicks < 0)
        {
            return std::nullopt;
        }

        FILETIME nowFileTime{};
        GetSystemTimeAsFileTime(&nowFileTime);
        ULARGE_INTEGER nowTicks{};
        nowTicks.LowPart = nowFileTime.dwLowDateTime;
        nowTicks.HighPart = nowFileTime.dwHighDateTime;
        return nowTicks.QuadPart >= static_cast<std::uint64_t>(utcTicks);
    }

    PalControl::Contracts::CommandResult Failure(
        const PalControl::Contracts::CommandEnvelope& command,
        std::string code,
        std::string message)
    {
        return PalControl::Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = PalControl::Contracts::CommandState::Failed,
            .ErrorCode = std::move(code),
            .ErrorMessage = std::move(message)
        };
    }

    bool IsIdentityCandidate(std::string_view name)
    {
        std::string lowered{name};
        std::transform(lowered.begin(), lowered.end(), lowered.begin(), [](unsigned char value) {
            return static_cast<char>(std::tolower(value));
        });
        constexpr std::array<std::string_view, 10> Tokens{
            "uid", "userid", "playerid", "account", "platform", "steam",
            "epic", "nickname", "playername", "level"};
        return std::ranges::any_of(Tokens, [&](std::string_view token) {
            return lowered.find(token) != std::string::npos;
        });
    }

    bool IsPlayerProgressionFunction(std::string_view name)
    {
        std::string lowered{name};
        std::transform(lowered.begin(), lowered.end(), lowered.begin(), [](unsigned char value) {
            return static_cast<char>(std::tolower(value));
        });
        constexpr std::array<std::string_view, 16> Tokens{
            "playerexp", "experience", "level", "statuspoint", "statusrank",
            "unusedstatus", "technology", "ancienttechnology", "bosstechnology",
            "maxhp", "maxsp", "stamina", "inventoryweight", "workspeed",
            "craftspeed", "capturelevel"};
        return std::ranges::any_of(Tokens, [&](std::string_view token) {
            return lowered.find(token) != std::string::npos;
        }) && lowered.find("delegate__delegatesignature") == std::string::npos;
    }

    bool IsInventoryFunction(std::string_view name)
    {
        std::string lowered{name};
        std::transform(lowered.begin(), lowered.end(), lowered.begin(), [](unsigned char value) {
            return static_cast<char>(std::tolower(value));
        });
        constexpr std::array<std::string_view, 4> Tokens{
            "inventory", "container", "slot", "item"};
        return std::ranges::any_of(Tokens, [&](std::string_view token) {
            return lowered.find(token) != std::string::npos;
        });
    }

    bool IsPalFunction(std::string_view name)
    {
        std::string lowered{name};
        std::transform(lowered.begin(), lowered.end(), lowered.begin(), [](unsigned char value) {
            return static_cast<char>(std::tolower(value));
        });
        constexpr std::array<std::string_view, 13> Tokens{
            "individual", "character", "container", "slot", "nickname", "rank",
            "level", "exp", "passive", "talent", "save", "waza", "skill"};
        return std::ranges::any_of(Tokens, [&](std::string_view token) {
            return lowered.find(token) != std::string::npos;
        });
    }

    bool IsPalMutationFunction(std::string_view name)
    {
        std::string lowered{name};
        std::transform(lowered.begin(), lowered.end(), lowered.begin(), [](unsigned char value) {
            return static_cast<char>(std::tolower(value));
        });
        const bool fieldMatch = lowered.find("talent") != std::string::npos ||
            lowered.find("passive") != std::string::npos ||
            lowered.find("waza") != std::string::npos ||
            lowered.find("skill") != std::string::npos ||
            lowered.find("rank") != std::string::npos ||
            lowered.find("level") != std::string::npos;
        const bool mutationMatch = lowered.find("set") != std::string::npos ||
            lowered.find("add") != std::string::npos ||
            lowered.find("remove") != std::string::npos ||
            lowered.find("update") != std::string::npos ||
            lowered.find("change") != std::string::npos ||
            lowered.find("upgrade") != std::string::npos;
        return fieldMatch && mutationMatch &&
            lowered.find("delegate__delegatesignature") == std::string::npos;
    }

    struct IdentityProperties
    {
        RC::Unreal::FStructProperty* PlayerUId{};
        RC::Unreal::FStrProperty* AccountName{};
        RC::Unreal::FIntProperty* PlayerId{};
        RC::Unreal::FStrProperty* PlayerName{};

        [[nodiscard]] bool IsReady() const
        {
            return PlayerUId && AccountName && PlayerId && PlayerName;
        }
    };

    IdentityProperties ResolveIdentityProperties(RC::Unreal::UClass* playerStateClass)
    {
        using namespace RC::Unreal;
        IdentityProperties result{};
        if (!playerStateClass)
        {
            return result;
        }

        if (auto* property = playerStateClass->FindProperty(FName(STR("PlayerUId")));
            property && property->IsA<FStructProperty>())
        {
            result.PlayerUId = static_cast<FStructProperty*>(property);
        }
        if (auto* property = playerStateClass->FindProperty(FName(STR("AccountName")));
            property && property->IsA<FStrProperty>())
        {
            result.AccountName = static_cast<FStrProperty*>(property);
        }
        if (auto* property = playerStateClass->FindProperty(FName(STR("PlayerId")));
            property && property->IsA<FIntProperty>())
        {
            result.PlayerId = static_cast<FIntProperty*>(property);
        }
        if (auto* property = playerStateClass->FindProperty(FName(STR("PlayerNamePrivate")));
            property && property->IsA<FStrProperty>())
        {
            result.PlayerName = static_cast<FStrProperty*>(property);
        }
        return result;
    }

    std::optional<std::string> ReadString(
        RC::Unreal::FStrProperty* property,
        RC::Unreal::UObject* object)
    {
        if (!property || !object)
        {
            return std::nullopt;
        }
        auto value = RC::to_string(**property->ContainerPtrToValuePtr<RC::Unreal::FString>(object));
        if (value.size() > 512)
        {
            return std::nullopt;
        }
        return value;
    }

    std::optional<std::int32_t> ReadInt(
        RC::Unreal::FIntProperty* property,
        RC::Unreal::UObject* object)
    {
        if (!property || !object)
        {
            return std::nullopt;
        }
        return *property->ContainerPtrToValuePtr<std::int32_t>(object);
    }

    std::optional<std::string> ReadPlayerUId(
        RC::Unreal::FStructProperty* property,
        RC::Unreal::UObject* object)
    {
        if (!property || !object)
        {
            return std::nullopt;
        }

        if (!property->GetStruct() || property->GetStruct()->GetName() != STR("Guid"))
        {
            return std::nullopt;
        }

        const auto* value = property->ContainerPtrToValuePtr<RC::Unreal::FGuid>(object);
        if (!value || !value->is_valid())
        {
            return std::nullopt;
        }

        std::array<char, 37> buffer{};
        std::snprintf(
            buffer.data(),
            buffer.size(),
            "%08x-%04x-%04x-%04x-%04x%08x",
            value->A,
            value->B >> 16,
            value->B & 0xffff,
            value->C >> 16,
            value->C & 0xffff,
            value->D);
        return buffer.data();
    }

    std::string JsonString(const std::optional<std::string>& value)
    {
        return value ? "\"" + EscapeJson(*value) + "\"" : "null";
    }

    std::string PropertyDetailType(RC::Unreal::FProperty* property)
    {
        using namespace RC;
        using namespace RC::Unreal;
        if (property && property->IsA<FStructProperty>())
        {
            auto* structProperty = static_cast<FStructProperty*>(property);
            if (structProperty->GetStruct())
            {
                return to_string(structProperty->GetStruct()->GetName());
            }
        }
        if (property && property->IsA<FArrayProperty>())
        {
            auto* arrayProperty = static_cast<FArrayProperty*>(property);
            if (arrayProperty->GetInner())
            {
                const auto innerType = to_string(arrayProperty->GetInner()->GetClass().GetName());
                const auto innerDetail = PropertyDetailType(arrayProperty->GetInner());
                return innerDetail.empty() ? innerType : innerType + "<" + innerDetail + ">";
            }
        }
        if (property && property->IsA<FEnumProperty>())
        {
            auto* enumProperty = static_cast<FEnumProperty*>(property);
            if (enumProperty->GetEnum())
            {
                return to_string(enumProperty->GetEnum()->GetName());
            }
        }
        if (property && property->IsA<FObjectProperty>())
        {
            auto* objectProperty = static_cast<FObjectProperty*>(property);
            if (objectProperty->GetPropertyClass())
            {
                return to_string(objectProperty->GetPropertyClass()->GetName());
            }
        }
        return {};
    }

    template <typename PropertyType>
    PropertyType* FindTypedProperty(
        RC::Unreal::UStruct* owner,
        const RC::File::CharType* name)
    {
        if (!owner)
        {
            return nullptr;
        }
        auto* property = owner->FindProperty(RC::Unreal::FName(name));
        return property && property->IsA<PropertyType>()
            ? static_cast<PropertyType*>(property)
            : nullptr;
    }

    std::string ShortEnumName(std::string value)
    {
        if (const auto separator = value.rfind("::");
            separator != std::string::npos)
        {
            value.erase(0, separator + 2);
        }
        return value;
    }

    std::string CanonicalPlayerStatusId(std::string_view nativeName)
    {
        constexpr std::array<std::pair<std::string_view, std::string_view>, 6> Names{{
            {"最大HP", "StatusName_AddMaxHP"},
            {"最大SP", "StatusName_AddMaxSP"},
            {"攻撃力", "StatusName_AddPower"},
            {"所持重量", "StatusName_AddMaxInventoryWeight"},
            {"作業速度", "StatusName_AddWorkSpeed"},
            {"捕獲率", "StatusName_AddCaptureLevel"}
        }};
        const auto iterator = std::ranges::find_if(Names, [&](const auto& entry) {
            return entry.first == nativeName || entry.second == nativeName;
        });
        return iterator == Names.end()
            ? std::string{nativeName}
            : std::string{iterator->second};
    }

    std::string NativePlayerStatusName(std::string_view canonicalId)
    {
        constexpr std::array<std::pair<std::string_view, std::string_view>, 6> Names{{
            {"StatusName_AddMaxHP", "最大HP"},
            {"StatusName_AddMaxSP", "最大SP"},
            {"StatusName_AddPower", "攻撃力"},
            {"StatusName_AddMaxInventoryWeight", "所持重量"},
            {"StatusName_AddWorkSpeed", "作業速度"},
            {"StatusName_AddCaptureLevel", "捕獲率"}
        }};
        const auto iterator = std::ranges::find_if(Names, [&](const auto& entry) {
            return entry.first == canonicalId || entry.second == canonicalId;
        });
        return iterator == Names.end()
            ? std::string{canonicalId}
            : std::string{iterator->second};
    }

    struct EnumArraySnapshot
    {
        std::vector<std::int64_t> Values;
        std::vector<std::string> Names;
        std::string Json{"[]"};
        bool Ready = false;
    };

    EnumArraySnapshot ReadEnumArray(
        RC::Unreal::FArrayProperty* property,
        void* container,
        std::size_t maxEntries)
    {
        using namespace RC;
        using namespace RC::Unreal;
        EnumArraySnapshot result{};
        if (!property || !container || !property->GetInner() ||
            !property->GetInner()->IsA<FEnumProperty>())
        {
            return result;
        }
        auto* enumProperty = static_cast<FEnumProperty*>(property->GetInner());
        auto* underlying = enumProperty->GetUnderlyingProperty();
        auto* enumType = enumProperty->GetEnum().Get();
        auto* arrayMemory = property->ContainerPtrToValuePtr<void>(container);
        if (!underlying || !enumType || !arrayMemory)
        {
            return result;
        }

        FScriptArrayHelper helper(property, arrayMemory);
        const auto count = std::min<std::size_t>(
            static_cast<std::size_t>(std::max(helper.Num(), 0)),
            maxEntries);
        std::string json{"["};
        for (std::size_t index = 0; index < count; ++index)
        {
            const auto value = underlying->GetSignedIntPropertyValue(
                helper.GetRawPtr(static_cast<int32>(index)));
            const auto name = ShortEnumName(to_string(
                enumType->GetNameByValue(value).ToString()));
            if (index > 0)
            {
                json += ',';
            }
            json += std::string{"{"} +
                "\"id\":\"" + EscapeJson(name) + "\"," +
                "\"value\":" + std::to_string(value) + "}";
            result.Values.push_back(value);
            result.Names.push_back(name);
        }
        json += ']';
        result.Json = std::move(json);
        result.Ready = true;
        return result;
    }

    std::string JoinStringsForRevision(const std::vector<std::string>& values)
    {
        std::string result;
        for (const auto& value : values)
        {
            result += value;
            result.push_back(',');
        }
        return result;
    }

    std::set<std::string> ReadPassiveSkillCatalogIds(
        std::set<std::string>* sources = nullptr)
    {
        using namespace RC;
        using namespace RC::Unreal;
        std::vector<UObject*> dataTableObjects;
        UObjectGlobals::FindAllOf(STR("DataTable"), dataTableObjects);
        std::set<std::string> result;
        for (auto* object : dataTableObjects)
        {
            auto* table = Cast<UDataTable>(object);
            if (!table || !table->GetRowStruct())
            {
                continue;
            }
            const auto fullName = to_string(table->GetFullName());
            if (fullName.find(
                    "/Game/Pal/DataTable/PassiveSkill/DT_PassiveSkill_Main") ==
                std::string::npos)
            {
                continue;
            }
            if (sources)
            {
                sources->insert(fullName);
            }
            const auto rowNames = table->GetRowNames();
            const auto rowCount = std::min<std::size_t>(
                static_cast<std::size_t>(std::max(rowNames.Num(), 0)),
                MaxReturnedSkillCatalogEntries);
            for (std::size_t index = 0; index < rowCount; ++index)
            {
                const auto id = to_string(
                    rowNames[static_cast<int32>(index)].ToString());
                if (!id.empty() && id != "None")
                {
                    result.insert(id);
                }
            }
        }
        return result;
    }

    struct LocalizedSkillTexts
    {
        std::unordered_map<std::string, std::string> Names;
        std::unordered_map<std::string, std::string> Descriptions;
    };

    bool IsUsableLocalizedText(std::string_view value)
    {
        return !value.empty() && value != "zh-Hans Text" && value != "None";
    }

    LocalizedSkillTexts ReadLocalizedSkillTexts()
    {
        using namespace RC;
        using namespace RC::Unreal;
        LocalizedSkillTexts result{};
        std::vector<UObject*> dataTableObjects;
        UObjectGlobals::FindAllOf(STR("DataTable"), dataTableObjects);
        for (auto* object : dataTableObjects)
        {
            auto* table = Cast<UDataTable>(object);
            if (!table || !table->GetRowStruct())
            {
                continue;
            }
            const auto fullName = to_string(table->GetFullName());
            const bool isNameTable = fullName.find(
                "/Game/Pal/DataTable/Text/DT_SkillNameText") != std::string::npos;
            const bool isDescriptionTable = fullName.find(
                "/Game/Pal/DataTable/Text/DT_SkillDescText") != std::string::npos;
            if (!isNameTable && !isDescriptionTable)
            {
                continue;
            }
            auto* textProperty = FindTypedProperty<FTextProperty>(
                table->GetRowStruct(), STR("TextData"));
            if (!textProperty)
            {
                continue;
            }
            auto& destination = isNameTable ? result.Names : result.Descriptions;
            for (auto& pair : table->GetRowMap())
            {
                const auto key = to_string(pair.Key.ToString());
                const auto* text = textProperty->ContainerPtrToValuePtr<FText>(pair.Value);
                const auto value = text ? to_string(text->ToString()) : std::string{};
                if (IsUsableLocalizedText(value))
                {
                    destination.insert_or_assign(key, value);
                }
            }
        }
        return result;
    }

    std::string FindLocalizedText(
        const std::unordered_map<std::string, std::string>& values,
        std::string_view key)
    {
        const auto found = values.find(std::string{key});
        return found == values.end() ? std::string{} : found->second;
    }

    std::string ReadNameValue(
        RC::Unreal::FNameProperty* property,
        void* row)
    {
        if (!property || !row)
        {
            return {};
        }
        const auto* value = property->ContainerPtrToValuePtr<RC::Unreal::FName>(row);
        return value ? RC::to_string(value->ToString()) : std::string{};
    }

    std::string ReadEnumValue(
        RC::Unreal::FEnumProperty* property,
        void* row)
    {
        using namespace RC;
        using namespace RC::Unreal;
        if (!property || !row || !property->GetUnderlyingProperty() || !property->GetEnum())
        {
            return {};
        }
        const auto value = property->GetUnderlyingProperty()->GetSignedIntPropertyValue(
            property->ContainerPtrToValuePtr<void>(row));
        return ShortEnumName(to_string(property->GetEnum()->GetNameByValue(value).ToString()));
    }

    struct PassiveSkillCatalogSnapshot
    {
        std::string Json{"[]"};
        std::size_t Count{};
        std::size_t LocalizedCount{};
        std::size_t ObtainableCount{};
    };

    PassiveSkillCatalogSnapshot ReadPassiveSkillCatalog(
        const LocalizedSkillTexts& localizedTexts)
    {
        using namespace RC;
        using namespace RC::Unreal;
        PassiveSkillCatalogSnapshot result{};
        std::vector<UObject*> dataTableObjects;
        UObjectGlobals::FindAllOf(STR("DataTable"), dataTableObjects);
        UDataTable* passiveTable = nullptr;
        for (auto* object : dataTableObjects)
        {
            auto* candidate = Cast<UDataTable>(object);
            if (!candidate || !candidate->GetRowStruct())
            {
                continue;
            }
            const auto fullName = to_string(candidate->GetFullName());
            if (fullName.find(
                    "/Game/Pal/DataTable/PassiveSkill/DT_PassiveSkill_Main") ==
                std::string::npos)
            {
                continue;
            }
            passiveTable = candidate;
            if (fullName.starts_with("CompositeDataTable"))
            {
                break;
            }
        }
        if (!passiveTable)
        {
            return result;
        }

        auto* rowStruct = passiveTable->GetRowStruct().Get();
        auto* rankProperty = FindTypedProperty<FIntProperty>(rowStruct, STR("Rank"));
        auto* lotteryWeightProperty = FindTypedProperty<FIntProperty>(
            rowStruct, STR("LotteryWeight"));
        auto* nameOverrideProperty = FindTypedProperty<FNameProperty>(
            rowStruct, STR("OverrideNameTextID"));
        auto* descriptionOverrideProperty = FindTypedProperty<FNameProperty>(
            rowStruct, STR("OverrideDescMsgID"));
        auto* summaryOverrideProperty = FindTypedProperty<FNameProperty>(
            rowStruct, STR("OverrideSummaryTextId"));
        auto* categoryProperty = FindTypedProperty<FEnumProperty>(
            rowStruct, STR("Category"));
        constexpr std::array<const RC::File::CharType*, 4> EffectTypeNames{
            STR("EffectType1"), STR("EffectType2"), STR("EffectType3"), STR("EffectType4")};
        constexpr std::array<const RC::File::CharType*, 4> EffectValueNames{
            STR("EffectValue1"), STR("EffectValue2"), STR("EffectValue3"), STR("EffectValue4")};
        constexpr std::array<const RC::File::CharType*, 4> TargetTypeNames{
            STR("TargetType1"), STR("TargetType2"), STR("TargetType3"), STR("TargetType4")};
        constexpr std::array<const RC::File::CharType*, 4> PalAvailabilityNames{
            STR("AddPal"), STR("AddRarePal"), STR("AddWorldTreePal"), STR("AddMutationPal")};

        std::string json{"["};
        for (auto& pair : passiveTable->GetRowMap())
        {
            if (result.Count >= MaxReturnedSkillCatalogEntries)
            {
                break;
            }
            const auto id = to_string(pair.Key.ToString());
            if (id.empty() || id == "None")
            {
                continue;
            }
            const auto defaultTextKey = "PASSIVE_" + id;
            auto nameKey = ReadNameValue(nameOverrideProperty, pair.Value);
            if (nameKey.empty() || nameKey == "None")
            {
                nameKey = defaultTextKey;
            }
            auto descriptionKey = ReadNameValue(descriptionOverrideProperty, pair.Value);
            if (descriptionKey.empty() || descriptionKey == "None")
            {
                descriptionKey = ReadNameValue(summaryOverrideProperty, pair.Value);
            }
            if (descriptionKey.empty() || descriptionKey == "None")
            {
                descriptionKey = defaultTextKey;
            }
            const auto localizedName = FindLocalizedText(localizedTexts.Names, nameKey);
            const auto description = FindLocalizedText(
                localizedTexts.Descriptions, descriptionKey);
            const bool localized = IsUsableLocalizedText(localizedName);
            const auto rank = rankProperty
                ? *rankProperty->ContainerPtrToValuePtr<int32>(pair.Value)
                : 0;
            const auto lotteryWeight = lotteryWeightProperty
                ? *lotteryWeightProperty->ContainerPtrToValuePtr<int32>(pair.Value)
                : 0;
            bool obtainable = lotteryWeight > 0;
            for (const auto* availabilityName : PalAvailabilityNames)
            {
                if (auto* property = FindTypedProperty<FBoolProperty>(
                        rowStruct, availabilityName);
                    property && property->GetPropertyValueInContainer(pair.Value))
                {
                    obtainable = true;
                }
            }
            const auto category = ReadEnumValue(categoryProperty, pair.Value);
            std::string effectsJson{"["};
            std::size_t effectCount = 0;
            for (std::size_t effectIndex = 0; effectIndex < EffectTypeNames.size(); ++effectIndex)
            {
                auto* typeProperty = FindTypedProperty<FEnumProperty>(
                    rowStruct, EffectTypeNames[effectIndex]);
                auto* valueProperty = FindTypedProperty<FFloatProperty>(
                    rowStruct, EffectValueNames[effectIndex]);
                auto* targetProperty = FindTypedProperty<FEnumProperty>(
                    rowStruct, TargetTypeNames[effectIndex]);
                const auto effectType = ReadEnumValue(typeProperty, pair.Value);
                const auto effectValue = valueProperty
                    ? *valueProperty->ContainerPtrToValuePtr<float>(pair.Value)
                    : 0.0F;
                std::string loweredEffectType{effectType};
                std::transform(
                    loweredEffectType.begin(),
                    loweredEffectType.end(),
                    loweredEffectType.begin(),
                    [](unsigned char value) {
                        return static_cast<char>(std::tolower(value));
                    });
                if (loweredEffectType.empty() || loweredEffectType == "none" ||
                    loweredEffectType == "no" || loweredEffectType == "max")
                {
                    continue;
                }
                if (effectCount++ > 0)
                {
                    effectsJson += ',';
                }
                effectsJson += std::string{"{"} +
                    "\"type\":\"" + EscapeJson(effectType) + "\"," +
                    "\"value\":" + std::to_string(effectValue) + "," +
                    "\"target\":\"" + EscapeJson(ReadEnumValue(targetProperty, pair.Value)) + "\"}";
            }
            effectsJson += ']';
            if (result.Count++ > 0)
            {
                json += ',';
            }
            result.LocalizedCount += localized ? 1 : 0;
            result.ObtainableCount += obtainable ? 1 : 0;
            const auto polarity = rank > 0 ? "positive" : rank < 0 ? "negative" : "neutral";
            const bool internal = !localized ||
                category.find("NotDisplayable") != std::string::npos;
            json += std::string{"{"} +
                "\"id\":\"" + EscapeJson(id) + "\"," +
                "\"name\":\"" + EscapeJson(localized ? localizedName : id) + "\"," +
                "\"description\":\"" + EscapeJson(description) + "\"," +
                "\"rank\":" + std::to_string(rank) + "," +
                "\"category\":\"" + EscapeJson(category) + "\"," +
                "\"polarity\":\"" + polarity + "\"," +
                "\"localized\":" + (localized ? "true" : "false") + "," +
                "\"obtainable\":" + (obtainable ? "true" : "false") + "," +
                "\"internal\":" + (internal ? "true" : "false") + "," +
                "\"effects\":" + effectsJson + "}";
        }
        json += ']';
        result.Json = std::move(json);
        return result;
    }

    std::optional<std::int64_t> ResolveActiveSkillValue(
        RC::Unreal::FArrayProperty* activeSkillProperty,
        std::string_view requestedId)
    {
        using namespace RC;
        using namespace RC::Unreal;
        if (!activeSkillProperty || !activeSkillProperty->GetInner() ||
            !activeSkillProperty->GetInner()->IsA<FEnumProperty>())
        {
            return std::nullopt;
        }
        auto* enumProperty = static_cast<FEnumProperty*>(
            activeSkillProperty->GetInner());
        auto* enumType = enumProperty->GetEnum().Get();
        if (!enumType)
        {
            return std::nullopt;
        }
        std::vector<std::pair<FName, int64>> names;
        enumType->GetEnumNamesAsVector(names);
        for (const auto& [name, value] : names)
        {
            if (ShortEnumName(to_string(name.ToString())) == requestedId)
            {
                return value;
            }
        }
        return std::nullopt;
    }

    RC::Unreal::UFunction* ResolveFunction(
        RC::Unreal::UObject* target,
        const RC::File::CharType* functionName,
        const RC::File::CharType* functionPath);

    bool InvokeNamePairFunction(
        RC::Unreal::UObject* target,
        const RC::File::CharType* functionName,
        const RC::File::CharType* functionPath,
        const RC::File::CharType* firstParameterName,
        std::string_view firstValue,
        const RC::File::CharType* secondParameterName,
        std::string_view secondValue)
    {
        using namespace RC;
        using namespace RC::Unreal;
        auto* function = ResolveFunction(target, functionName, functionPath);
        auto* firstParameter = FindTypedProperty<FNameProperty>(
            function,
            firstParameterName);
        auto* secondParameter = FindTypedProperty<FNameProperty>(
            function,
            secondParameterName);
        if (!function || !firstParameter || !secondParameter)
        {
            return false;
        }
        std::vector<uint8> parameters(
            std::max<std::size_t>(function->GetPropertiesSize(), 1),
            0);
        function->InitializeStruct(parameters.data());
        const auto firstWide = to_wstring(std::string{firstValue});
        const auto secondWide = to_wstring(std::string{secondValue});
        *firstParameter->ContainerPtrToValuePtr<FName>(parameters.data()) =
            FName(firstWide.c_str());
        *secondParameter->ContainerPtrToValuePtr<FName>(parameters.data()) =
            FName(secondWide.c_str());
        target->ProcessEvent(function, parameters.data());
        function->DestroyStruct(parameters.data());
        return true;
    }

    bool InvokeNameFunction(
        RC::Unreal::UObject* target,
        const RC::File::CharType* functionName,
        const RC::File::CharType* functionPath,
        const RC::File::CharType* parameterName,
        std::string_view value)
    {
        using namespace RC;
        using namespace RC::Unreal;
        auto* function = ResolveFunction(target, functionName, functionPath);
        auto* parameter = FindTypedProperty<FNameProperty>(function, parameterName);
        if (!function || !parameter)
        {
            return false;
        }
        std::vector<uint8> parameters(
            std::max<std::size_t>(function->GetPropertiesSize(), 1),
            0);
        function->InitializeStruct(parameters.data());
        const auto wideValue = to_wstring(std::string{value});
        *parameter->ContainerPtrToValuePtr<FName>(parameters.data()) =
            FName(wideValue.c_str());
        target->ProcessEvent(function, parameters.data());
        function->DestroyStruct(parameters.data());
        return true;
    }

    bool WriteNameArray(
        RC::Unreal::FArrayProperty* property,
        void* container,
        const std::vector<std::string>& values)
    {
        using namespace RC;
        using namespace RC::Unreal;
        if (!property || !container || !property->GetInner() ||
            !property->GetInner()->IsA<FNameProperty>())
        {
            return false;
        }
        auto* array = property->ContainerPtrToValuePtr<TArray<FName>>(container);
        if (!array || static_cast<std::size_t>(std::max(array->Num(), 0)) !=
                values.size())
        {
            return false;
        }
        for (std::size_t index = 0; index < values.size(); ++index)
        {
            const auto wideValue = to_wstring(values[index]);
            (*array)[static_cast<int32>(index)] = FName(wideValue.c_str());
        }
        return true;
    }

    bool InvokeActiveSkillFunction(
        RC::Unreal::UObject* target,
        const RC::File::CharType* functionName,
        const RC::File::CharType* functionPath,
        std::int64_t value)
    {
        using namespace RC::Unreal;
        auto* function = ResolveFunction(target, functionName, functionPath);
        auto* parameter = FindTypedProperty<FEnumProperty>(
            function,
            STR("WazaID"));
        auto* underlying = parameter ? parameter->GetUnderlyingProperty() : nullptr;
        if (!function || !parameter || !underlying)
        {
            return false;
        }
        std::vector<uint8> parameters(
            std::max<std::size_t>(function->GetPropertiesSize(), 1),
            0);
        function->InitializeStruct(parameters.data());
        underlying->SetIntPropertyValue(
            parameter->ContainerPtrToValuePtr<void>(parameters.data()),
            value);
        target->ProcessEvent(function, parameters.data());
        function->DestroyStruct(parameters.data());
        return true;
    }

    bool InvokeNoParameterFunction(
        RC::Unreal::UObject* target,
        const RC::File::CharType* functionName,
        const RC::File::CharType* functionPath)
    {
        auto* function = ResolveFunction(target, functionName, functionPath);
        if (!target || !function || function->GetPropertiesSize() != 0)
        {
            return false;
        }
        target->ProcessEvent(function, nullptr);
        return true;
    }

    std::optional<RC::Unreal::FGuid> ReadNestedGuid(
        RC::Unreal::FStructProperty* property,
        void* container)
    {
        using namespace RC::Unreal;
        if (!property || !container || !property->GetStruct())
        {
            return std::nullopt;
        }

        auto* valueMemory = property->ContainerPtrToValuePtr<uint8>(container);
        if (property->GetStruct()->GetName() == STR("Guid"))
        {
            const auto value = *reinterpret_cast<FGuid*>(valueMemory);
            return value.is_valid() ? std::optional<FGuid>{value} : std::nullopt;
        }
        if (property->GetStruct()->GetName() == STR("PalContainerId"))
        {
            auto* idProperty = FindTypedProperty<FStructProperty>(
                property->GetStruct(),
                STR("ID"));
            if (!idProperty || !idProperty->GetStruct() ||
                idProperty->GetStruct()->GetName() != STR("Guid"))
            {
                return std::nullopt;
            }
            const auto* value = idProperty->ContainerPtrToValuePtr<FGuid>(valueMemory);
            return value && value->is_valid()
                ? std::optional<FGuid>{*value}
                : std::nullopt;
        }
        return std::nullopt;
    }

    std::optional<RC::Unreal::FGuid> ReadTopLevelGuid(
        RC::Unreal::FStructProperty* property,
        RC::Unreal::UObject* object)
    {
        using namespace RC::Unreal;
        if (!property || !object || !property->GetStruct())
        {
            return std::nullopt;
        }
        if (property->GetStruct()->GetName() == STR("Guid"))
        {
            const auto* value = property->ContainerPtrToValuePtr<FGuid>(object);
            return value && value->is_valid()
                ? std::optional<FGuid>{*value}
                : std::nullopt;
        }

        auto* valueMemory = property->ContainerPtrToValuePtr<uint8>(object);
        if (property->GetStruct()->GetName() == STR("PalContainerId"))
        {
            auto* idProperty = FindTypedProperty<FStructProperty>(
                property->GetStruct(),
                STR("ID"));
            if (!idProperty || !idProperty->GetStruct() ||
                idProperty->GetStruct()->GetName() != STR("Guid"))
            {
                return std::nullopt;
            }
            const auto* value = idProperty->ContainerPtrToValuePtr<FGuid>(valueMemory);
            return value && value->is_valid()
                ? std::optional<FGuid>{*value}
                : std::nullopt;
        }
        return std::nullopt;
    }

    std::string GuidToString(const RC::Unreal::FGuid& value)
    {
        std::array<char, 37> buffer{};
        std::snprintf(
            buffer.data(),
            buffer.size(),
            "%08x-%04x-%04x-%04x-%04x%08x",
            value.A,
            value.B >> 16,
            value.B & 0xffff,
            value.C >> 16,
            value.C & 0xffff,
            value.D);
        return buffer.data();
    }

    std::string NormalizeIdentifier(std::string_view value)
    {
        std::string normalized;
        normalized.reserve(value.size());
        for (const auto character : value)
        {
            if (std::isalnum(static_cast<unsigned char>(character)))
            {
                normalized.push_back(static_cast<char>(
                    std::tolower(static_cast<unsigned char>(character))));
            }
        }
        return normalized;
    }

    std::uint64_t StableRevision(std::string_view value)
    {
        std::uint64_t hash = 1469598103934665603ULL;
        for (const auto character : value)
        {
            hash ^= static_cast<unsigned char>(character);
            hash *= 1099511628211ULL;
        }
        return hash & 0x7fffffffffffffffULL;
    }

    RC::Unreal::UFunction* ResolveFunction(
        RC::Unreal::UObject* target,
        const RC::File::CharType* functionName,
        const RC::File::CharType* functionPath)
    {
        using namespace RC::Unreal;
        auto* function = target
            ? target->GetFunctionByName(FName(functionName))
            : nullptr;
        return function ? function : UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr,
            nullptr,
            functionPath);
    }

    bool IsAuthoritativeLiveGameState(
        RC::Unreal::UObject* object,
        RC::Unreal::UClass* gameStateClass)
    {
        using namespace RC::Unreal;

        if (!object || !gameStateClass || !object->IsA(gameStateClass) ||
            object->HasAnyFlags(static_cast<EObjectFlags>(
                RF_ClassDefaultObject |
                RF_ArchetypeObject |
                RF_BeginDestroyed |
                RF_FinishDestroyed)) ||
            object->HasAnyInternalFlags(
                EInternalObjectFlags::Unreachable |
                EInternalObjectFlags::PendingKill |
                EInternalObjectFlags::PendingConstruction))
        {
            return false;
        }

        auto* world = object->GetWorld();
        if (!world || world->HasAnyFlags(static_cast<EObjectFlags>(
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
            ? *authorityGameModeProperty->ContainerPtrToValuePtr<UObject*>(world)
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

    RC::Unreal::FStrProperty* ValidateServerNoticeFunction(
        RC::Unreal::UFunction* function)
    {
        using namespace RC;
        using namespace RC::Unreal;

        if (!function || function->GetPropertiesSize() != sizeof(FString) ||
            function->GetReturnProperty() ||
            to_string(function->GetFullName()) !=
                "Function /Script/Pal.PalGameStateInGame:BroadcastServerNotice")
        {
            return nullptr;
        }

        constexpr auto RequiredFlags = static_cast<EFunctionFlags>(
            FUNC_Public |
            FUNC_Native |
            FUNC_Event |
            FUNC_Net |
            FUNC_NetReliable |
            FUNC_NetMulticast);
        constexpr auto RejectedFlags = static_cast<EFunctionFlags>(
            FUNC_NetServer | FUNC_NetClient);
        if (!function->HasAllFunctionFlags(RequiredFlags) ||
            function->HasAnyFunctionFlags(RejectedFlags))
        {
            return nullptr;
        }

        FStrProperty* noticeMessageProperty = nullptr;
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
            if (parameter->GetName() == STR("NoticeMessage") &&
                parameter->IsA<FStrProperty>() &&
                !parameter->HasAnyPropertyFlags(static_cast<EPropertyFlags>(
                    CPF_OutParm | CPF_ReturnParm)))
            {
                noticeMessageProperty = static_cast<FStrProperty*>(parameter);
            }
        }
        return parameterCount == 1 ? noticeMessageProperty : nullptr;
    }

    bool ValidateClientMessageFunction(
        RC::Unreal::UFunction* function,
        RC::Unreal::FStrProperty*& messageProperty,
        RC::Unreal::FNameProperty*& typeProperty,
        RC::Unreal::FFloatProperty*& lifetimeProperty)
    {
        using namespace RC;
        using namespace RC::Unreal;

        messageProperty = nullptr;
        typeProperty = nullptr;
        lifetimeProperty = nullptr;
        if (!function || function->GetPropertiesSize() != 32 ||
            function->GetReturnProperty() ||
            to_string(function->GetFullName()) !=
                "Function /Script/Engine.PlayerController:ClientMessage")
        {
            return false;
        }

        constexpr auto RequiredFlags = static_cast<EFunctionFlags>(
            FUNC_Public |
            FUNC_Native |
            FUNC_Event |
            FUNC_Net |
            FUNC_NetReliable |
            FUNC_NetClient);
        constexpr auto RejectedFlags = static_cast<EFunctionFlags>(
            FUNC_Static | FUNC_NetServer | FUNC_NetMulticast);
        if (!function->HasAllFunctionFlags(RequiredFlags) ||
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
                parameter->IsA<FStrProperty>() &&
                !parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_ReturnParm))
            {
                messageProperty = static_cast<FStrProperty*>(parameter);
            }
            else if (parameter->GetName() == STR("Type") &&
                parameter->IsA<FNameProperty>() &&
                !parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_ReturnParm))
            {
                typeProperty = static_cast<FNameProperty*>(parameter);
            }
            else if (parameter->GetName() == STR("MsgLifeTime") &&
                parameter->IsA<FFloatProperty>() &&
                !parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_ReturnParm))
            {
                lifetimeProperty = static_cast<FFloatProperty*>(parameter);
            }
        }
        return parameterCount == 3 && messageProperty && typeProperty &&
            lifetimeProperty;
    }

    bool ValidateReliableClientNotificationRpc(
        RC::Unreal::UFunction* function,
        std::string_view expectedFullName,
        std::int32_t expectedPropertiesSize)
    {
        using namespace RC;
        using namespace RC::Unreal;

        if (!function ||
            function->GetPropertiesSize() != expectedPropertiesSize ||
            function->GetReturnProperty() ||
            to_string(function->GetFullName()) != expectedFullName)
        {
            return false;
        }

        constexpr auto RequiredFlags = static_cast<EFunctionFlags>(
            FUNC_Public |
            FUNC_Native |
            FUNC_Event |
            FUNC_Net |
            FUNC_NetReliable |
            FUNC_NetClient);
        constexpr auto RejectedFlags = static_cast<EFunctionFlags>(
            FUNC_Static | FUNC_NetServer | FUNC_NetMulticast);
        return function->HasAllFunctionFlags(RequiredFlags) &&
            !function->HasAnyFunctionFlags(RejectedFlags);
    }

    bool IsInputParameter(RC::Unreal::FProperty* property)
    {
        using namespace RC::Unreal;
        return property &&
            property->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm) &&
            !property->HasAnyPropertyFlags(static_cast<EPropertyFlags>(
                CPF_OutParm | CPF_ReturnParm));
    }

    std::size_t CountParameters(RC::Unreal::UFunction* function)
    {
        using namespace RC::Unreal;
        if (!function)
        {
            return 0;
        }
        std::size_t count = 0;
        for (FProperty* property : TFieldRange<FProperty>(
                 function,
                 EFieldIterationFlags::IncludeAll))
        {
            if (property && property->HasAnyPropertyFlags(
                    EPropertyFlags::CPF_Parm))
            {
                ++count;
            }
        }
        return count;
    }

    struct BossDefeatNotificationBinding
    {
        RC::Unreal::UFunction* Function{};
        RC::Unreal::FStructProperty* DisplayData{};
        RC::Unreal::FIntProperty* TechnologyPoint{};
        RC::Unreal::FNameProperty* DefeatCharacterId{};
        RC::Unreal::FBoolProperty* AfterTeleport{};
        RC::Unreal::FIntProperty* DelayTime{};
    };

    bool BindBossDefeatNotification(
        RC::Unreal::UFunction* function,
        BossDefeatNotificationBinding& binding)
    {
        using namespace RC;
        using namespace RC::Unreal;

        binding = {};
        if (!ValidateReliableClientNotificationRpc(
                function,
                "Function /Script/Pal.PalNetworkPlayerComponent:ShowBossDefeatRewardUI_ToClient",
                0x14) ||
            CountParameters(function) != 3)
        {
            return false;
        }

        auto* displayData = FindTypedProperty<FStructProperty>(
            function,
            STR("BossDefeatDisplayData"));
        auto* afterTeleport = FindTypedProperty<FBoolProperty>(
            function,
            STR("AfterTeleport"));
        auto* delayTime = FindTypedProperty<FIntProperty>(
            function,
            STR("DelayTime"));
        auto* displayStruct = displayData
            ? displayData->GetStruct().Get()
            : nullptr;
        auto* technologyPoint = FindTypedProperty<FIntProperty>(
            displayStruct,
            STR("TechnologyPoint"));
        auto* defeatCharacterId = FindTypedProperty<FNameProperty>(
            displayStruct,
            STR("DefeatCharacterID"));

        if (!displayData || !afterTeleport || !delayTime || !displayStruct ||
            !technologyPoint || !defeatCharacterId ||
            !IsInputParameter(displayData) ||
            !IsInputParameter(afterTeleport) ||
            !IsInputParameter(delayTime) ||
            displayData->GetOffset_Internal() != 0x00 ||
            displayData->GetSize() != 0x0C ||
            displayStruct->GetName() != STR("PalUIBossDefeatRewardDisplayData") ||
            displayStruct->GetPropertiesSize() != 0x0C ||
            technologyPoint->GetOffset_Internal() != 0x00 ||
            technologyPoint->GetSize() != sizeof(std::int32_t) ||
            defeatCharacterId->GetOffset_Internal() != 0x04 ||
            defeatCharacterId->GetSize() != sizeof(FName) ||
            afterTeleport->GetOffset_Internal() != 0x0C ||
            afterTeleport->GetSize() != sizeof(bool) ||
            delayTime->GetOffset_Internal() != 0x10 ||
            delayTime->GetSize() != sizeof(std::int32_t))
        {
            return false;
        }

        std::size_t displayFieldCount = 0;
        for (FProperty* property : TFieldRange<FProperty>(
                 displayStruct,
                 EFieldIterationFlags::IncludeAll))
        {
            if (property)
            {
                ++displayFieldCount;
            }
        }
        if (displayFieldCount != 2)
        {
            return false;
        }

        binding = BossDefeatNotificationBinding{
            .Function = function,
            .DisplayData = displayData,
            .TechnologyPoint = technologyPoint,
            .DefeatCharacterId = defeatCharacterId,
            .AfterTeleport = afterTeleport,
            .DelayTime = delayTime
        };
        return true;
    }

    struct ExpNotificationBinding
    {
        RC::Unreal::UFunction* Function{};
        RC::Unreal::FIntProperty* RewardExp{};
    };

    bool BindExpNotification(
        RC::Unreal::UFunction* function,
        std::string_view expectedFullName,
        ExpNotificationBinding& binding)
    {
        using namespace RC::Unreal;

        binding = {};
        if (!ValidateReliableClientNotificationRpc(
                function,
                expectedFullName,
                sizeof(std::int32_t)) ||
            CountParameters(function) != 1)
        {
            return false;
        }
        auto* rewardExp = FindTypedProperty<FIntProperty>(
            function,
            STR("RewardExp"));
        if (!rewardExp || !IsInputParameter(rewardExp) ||
            rewardExp->GetOffset_Internal() != 0 ||
            rewardExp->GetSize() != sizeof(std::int32_t))
        {
            return false;
        }
        binding = ExpNotificationBinding{
            .Function = function,
            .RewardExp = rewardExp
        };
        return true;
    }

    bool ValidateNoParameterNotification(
        RC::Unreal::UFunction* function,
        std::string_view expectedFullName)
    {
        return ValidateReliableClientNotificationRpc(
                function,
                expectedFullName,
                0) &&
            CountParameters(function) == 0;
    }

    std::vector<RC::Unreal::UObject*> FindLivePalPlayerControllers(
        RC::Unreal::UWorld* world,
        RC::Unreal::UClass* playerControllerClass)
    {
        using namespace RC::Unreal;

        std::vector<UObject*> liveControllers;
        if (!world || !playerControllerClass)
        {
            return liveControllers;
        }
        std::vector<UObject*> controllers;
        UObjectGlobals::FindAllOf(STR("PalPlayerController"), controllers);
        liveControllers.reserve(controllers.size());
        for (auto* controller : controllers)
        {
            if (controller && controller->IsA(playerControllerClass) &&
                controller->GetWorld() == world &&
                !controller->HasAnyFlags(static_cast<EObjectFlags>(
                    RF_ClassDefaultObject |
                    RF_ArchetypeObject |
                    RF_BeginDestroyed |
                    RF_FinishDestroyed)) &&
                !controller->HasAnyInternalFlags(
                    EInternalObjectFlags::Unreachable |
                    EInternalObjectFlags::PendingKill |
                    EInternalObjectFlags::PendingConstruction))
            {
                liveControllers.push_back(controller);
            }
        }
        return liveControllers;
    }

    std::size_t CountLivePalPlayerControllers(RC::Unreal::UWorld* world)
    {
        using namespace RC::Unreal;

        if (!world)
        {
            return 0;
        }
        std::vector<UObject*> controllers;
        UObjectGlobals::FindAllOf(STR("PalPlayerController"), controllers);
        return static_cast<std::size_t>(std::ranges::count_if(
            controllers,
            [world](UObject* controller)
            {
                return controller && controller->GetWorld() == world &&
                    !controller->HasAnyFlags(static_cast<EObjectFlags>(
                        RF_ClassDefaultObject |
                        RF_ArchetypeObject |
                        RF_BeginDestroyed |
                        RF_FinishDestroyed)) &&
                    !controller->HasAnyInternalFlags(
                        EInternalObjectFlags::Unreachable |
                        EInternalObjectFlags::PendingKill |
                    EInternalObjectFlags::PendingConstruction);
            }));
    }

    struct NotificationTargets
    {
        RC::Unreal::UWorld* World{};
        RC::Unreal::UClass* ControllerClass{};
        RC::Unreal::UClass* TransmitterClass{};
        RC::Unreal::UClass* ComponentClass{};
        std::vector<RC::Unreal::UObject*> Controllers;
        std::vector<RC::Unreal::UObject*> Transmitters;
        std::vector<RC::Unreal::UObject*> Components;
        std::string MappingJson{"[]"};
        std::string ErrorCode;
        std::string ErrorMessage;

        [[nodiscard]] bool IsReady() const
        {
            return World && ControllerClass && TransmitterClass &&
                ComponentClass && ErrorCode.empty() &&
                Controllers.size() == Transmitters.size() &&
                Controllers.size() == Components.size();
        }
    };

    bool IsLiveNotificationObject(
        RC::Unreal::UObject* object,
        RC::Unreal::UClass* expectedClass,
        RC::Unreal::UWorld* world)
    {
        using namespace RC::Unreal;
        return object && expectedClass && world &&
            object->IsA(expectedClass) && object->GetWorld() == world &&
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

    NotificationTargets ResolveNotificationTargets()
    {
        using namespace RC::Unreal;

        NotificationTargets result{};
        auto* gameStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalGameStateInGame"));
        result.ControllerClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalPlayerController"));
        result.TransmitterClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalNetworkTransmitter"));
        result.ComponentClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalNetworkPlayerComponent"));
        if (!gameStateClass || !result.ControllerClass ||
            !result.TransmitterClass || !result.ComponentClass)
        {
            result.ErrorCode = "NATIVE_NOTIFICATION_TARGET_CLASS_UNAVAILABLE";
            result.ErrorMessage =
                "The Palworld GameState, PalPlayerController, PalNetworkTransmitter, or PalNetworkPlayerComponent class is unavailable.";
            return result;
        }

        auto* transmitterProperty = FindTypedProperty<FObjectProperty>(
            result.ControllerClass,
            STR("Transmitter"));
        auto* playerComponentProperty = FindTypedProperty<FObjectProperty>(
            result.TransmitterClass,
            STR("Player"));
        auto* actorOwnerProperty = FindTypedProperty<FObjectProperty>(
            result.TransmitterClass,
            STR("Owner"));
        if (!transmitterProperty || !playerComponentProperty ||
            !actorOwnerProperty ||
            transmitterProperty->GetPropertyClass().Get() !=
                result.TransmitterClass ||
            playerComponentProperty->GetPropertyClass().Get() !=
                result.ComponentClass)
        {
            result.ErrorCode = "NATIVE_NOTIFICATION_OWNERSHIP_SCHEMA_MISMATCH";
            result.ErrorMessage =
                "Expected PalPlayerController.Transmitter, PalNetworkTransmitter.Player, and Actor.Owner object properties with exact Palworld classes were not found.";
            return result;
        }

        std::vector<UObject*> gameStateObjects;
        UObjectGlobals::FindAllOf(STR("PalGameStateInGame"), gameStateObjects);
        std::vector<UObject*> authoritativeGameStates;
        authoritativeGameStates.reserve(gameStateObjects.size());
        for (auto* object : gameStateObjects)
        {
            if (IsAuthoritativeLiveGameState(object, gameStateClass))
            {
                authoritativeGameStates.push_back(object);
            }
        }
        if (authoritativeGameStates.size() != 1)
        {
            result.ErrorCode = authoritativeGameStates.empty()
                ? "NATIVE_NOTIFICATION_GAME_STATE_UNAVAILABLE"
                : "NATIVE_NOTIFICATION_GAME_STATE_AMBIGUOUS";
            result.ErrorMessage = authoritativeGameStates.empty()
                ? "No authoritative live PalGameStateInGame object is available."
                : "More than one authoritative live PalGameStateInGame object was found; notification dispatch was refused.";
            return result;
        }

        result.World = authoritativeGameStates.front()->GetWorld();
        result.Controllers = FindLivePalPlayerControllers(
            result.World,
            result.ControllerClass);

        std::vector<UObject*> allTransmitters;
        UObjectGlobals::FindAllOf(STR("PalNetworkTransmitter"), allTransmitters);
        std::vector<UObject*> allComponents;
        UObjectGlobals::FindAllOf(
            STR("PalNetworkPlayerComponent"),
            allComponents);
        std::set<UObject*> uniqueControllers;
        std::set<UObject*> uniqueTransmitters;
        std::set<UObject*> uniqueComponents;
        std::string mappingsJson{"["};

        for (std::size_t index = 0; index < result.Controllers.size(); ++index)
        {
            auto* controller = result.Controllers[index];
            if (!uniqueControllers.insert(controller).second)
            {
                result.ErrorCode = "NATIVE_NOTIFICATION_CONTROLLER_DUPLICATE";
                result.ErrorMessage =
                    "The live PalPlayerController enumeration contained a duplicate object; notification targeting was refused.";
                return result;
            }

            auto* transmitter = controller
                ? *transmitterProperty->ContainerPtrToValuePtr<UObject*>(controller)
                : nullptr;
            if (!IsLiveNotificationObject(
                    transmitter,
                    result.TransmitterClass,
                    result.World))
            {
                result.ErrorCode = "NATIVE_NOTIFICATION_TRANSMITTER_UNAVAILABLE";
                result.ErrorMessage = std::string{
                    "PalPlayerController.Transmitter was null, stale, wrong-class, or in another world for controller "} +
                    (controller
                        ? RC::to_string(controller->GetFullName())
                        : std::string{"null"}) + ".";
                return result;
            }

            auto* networkOwner =
                *actorOwnerProperty->ContainerPtrToValuePtr<UObject*>(transmitter);
            std::size_t ownedTransmitterCount = 0;
            for (auto* candidate : allTransmitters)
            {
                if (!IsLiveNotificationObject(
                        candidate,
                        result.TransmitterClass,
                        result.World))
                {
                    continue;
                }
                auto* candidateOwner =
                    *actorOwnerProperty->ContainerPtrToValuePtr<UObject*>(candidate);
                if (candidateOwner == controller)
                {
                    ++ownedTransmitterCount;
                }
            }
            if (networkOwner != controller || ownedTransmitterCount != 1 ||
                !uniqueTransmitters.insert(transmitter).second)
            {
                result.ErrorCode =
                    "NATIVE_NOTIFICATION_TRANSMITTER_OWNERSHIP_AMBIGUOUS";
                result.ErrorMessage = std::string{
                    "Each controller must own exactly one unique live PalNetworkTransmitter through Actor.Owner; controller="} +
                    RC::to_string(controller->GetFullName()) +
                    ",transmitter=" + RC::to_string(transmitter->GetFullName()) +
                    ",ownedCount=" + std::to_string(ownedTransmitterCount) + ".";
                return result;
            }

            auto* component =
                *playerComponentProperty->ContainerPtrToValuePtr<UObject*>(
                    transmitter);
            if (!IsLiveNotificationObject(
                    component,
                    result.ComponentClass,
                    result.World))
            {
                result.ErrorCode = "NATIVE_NOTIFICATION_COMPONENT_UNAVAILABLE";
                result.ErrorMessage = std::string{
                    "PalNetworkTransmitter.Player was null, stale, wrong-class, or in another world for transmitter "} +
                    RC::to_string(transmitter->GetFullName()) + ".";
                return result;
            }

            std::size_t ownedComponentCount = 0;
            UObject* soleOwnedComponent = nullptr;
            for (auto* candidate : allComponents)
            {
                if (IsLiveNotificationObject(
                        candidate,
                        result.ComponentClass,
                        result.World) &&
                    candidate->GetOuterPrivate() == transmitter)
                {
                    ++ownedComponentCount;
                    soleOwnedComponent = candidate;
                }
            }
            if (component->GetOuterPrivate() != transmitter ||
                ownedComponentCount != 1 || soleOwnedComponent != component ||
                !uniqueComponents.insert(component).second)
            {
                result.ErrorCode =
                    "NATIVE_NOTIFICATION_COMPONENT_OWNERSHIP_AMBIGUOUS";
                result.ErrorMessage = std::string{
                    "Each player transmitter must own exactly one unique live PalNetworkPlayerComponent through the direct UObject Outer chain; transmitter="} +
                    RC::to_string(transmitter->GetFullName()) +
                    ",component=" + RC::to_string(component->GetFullName()) +
                    ",ownedCount=" + std::to_string(ownedComponentCount) + ".";
                return result;
            }

            result.Transmitters.push_back(transmitter);
            result.Components.push_back(component);
            if (index > 0)
            {
                mappingsJson += ',';
            }
            mappingsJson += std::string{"{"} +
                "\"controller\":\"" +
                    EscapeJson(RC::to_string(controller->GetFullName())) +
                    "\"," +
                "\"transmitter\":\"" +
                    EscapeJson(RC::to_string(transmitter->GetFullName())) +
                    "\"," +
                "\"networkOwner\":\"" +
                    EscapeJson(RC::to_string(networkOwner->GetFullName())) +
                    "\"," +
                "\"component\":\"" +
                    EscapeJson(RC::to_string(component->GetFullName())) +
                    "\"," +
                "\"componentOuter\":\"" +
                    EscapeJson(RC::to_string(
                        component->GetOuterPrivate()->GetFullName())) +
                    "\"}";
        }
        mappingsJson += ']';
        result.MappingJson = std::move(mappingsJson);
        return result;
    }

    bool ComponentsCanInvokeCanonicalFunction(
        const std::vector<RC::Unreal::UObject*>& components,
        RC::Unreal::UClass* componentClass,
        const RC::File::CharType* functionName,
        RC::Unreal::UFunction* expectedFunction)
    {
        using namespace RC::Unreal;
        if (!componentClass || !expectedFunction)
        {
            return false;
        }
        return std::ranges::all_of(
            components,
            [componentClass, functionName](UObject* component)
            {
                // ProcessEvent may invoke a UFunction declared by a base class on
                // any live instance of that class. Blueprint-derived components
                // can resolve an override/thunk with a different UFunction address,
                // so pointer identity with the canonical base function is not a
                // valid compatibility requirement. The canonical function itself
                // is still validated fail-closed before this target check.
                return component && component->IsA(componentClass) &&
                    component->GetFunctionByName(FName(functionName));
            });
    }

    std::string DescribeNotificationFunction(RC::Unreal::UFunction* function)
    {
        using namespace RC;
        if (!function)
        {
            return "{fullName=unresolved,flags=0,propertiesSize=-1,paramCount=-1}";
        }
        std::array<char, 16> flagsHex{};
        std::snprintf(
            flagsHex.data(),
            flagsHex.size(),
            "0x%08X",
            static_cast<unsigned int>(function->GetFunctionFlags()));
        return std::string{"{fullName="} +
            to_string(function->GetFullName()) +
            ",flags=" + flagsHex.data() +
            ",propertiesSize=" +
                std::to_string(function->GetPropertiesSize()) +
            ",paramCount=" + std::to_string(CountParameters(function)) + "}";
    }

    std::string DescribeComponentFunctionResolutions(
        const std::vector<RC::Unreal::UObject*>& components,
        const RC::File::CharType* functionName)
    {
        using namespace RC;
        using namespace RC::Unreal;

        std::string result{"["};
        const auto count = std::min<std::size_t>(components.size(), 8);
        for (std::size_t index = 0; index < count; ++index)
        {
            if (index > 0)
            {
                result += ',';
            }
            auto* component = components[index];
            auto* resolved = component
                ? component->GetFunctionByName(FName(functionName))
                : nullptr;
            result += std::string{"{component="} +
                (component
                    ? to_string(component->GetFullName())
                    : std::string{"null"}) +
                ",resolved=" + DescribeNotificationFunction(resolved) + "}";
        }
        if (components.size() > count)
        {
            result += ",{truncated=" +
                std::to_string(components.size() - count) + "}";
        }
        result += ']';
        return result;
    }

    bool IsGuidD(std::string_view value)
    {
        if (value.size() != 36)
        {
            return false;
        }
        for (std::size_t index = 0; index < value.size(); ++index)
        {
            if (index == 8 || index == 13 || index == 18 || index == 23)
            {
                if (value[index] != '-')
                {
                    return false;
                }
                continue;
            }
            if (!std::isxdigit(static_cast<unsigned char>(value[index])))
            {
                return false;
            }
        }
        return true;
    }

    bool InvokeObjectParameterFunction(
        RC::Unreal::UObject* target,
        const RC::File::CharType* functionName,
        const RC::File::CharType* functionPath,
        const RC::File::CharType* parameterName,
        RC::Unreal::UObject* value)
    {
        using namespace RC::Unreal;
        if (!target || !value)
        {
            return false;
        }
        auto* function = ResolveFunction(target, functionName, functionPath);
        auto* parameter = FindTypedProperty<FObjectProperty>(
            function,
            parameterName);
        if (!function || !parameter)
        {
            return false;
        }

        std::vector<uint8> parameters(
            std::max<std::size_t>(function->GetPropertiesSize(), 1),
            0);
        function->InitializeStruct(parameters.data());
        *parameter->ContainerPtrToValuePtr<UObject*>(parameters.data()) = value;
        target->ProcessEvent(function, parameters.data());
        function->DestroyStruct(parameters.data());
        return true;
    }

    std::optional<std::int32_t> ReadNativeItemStackCount(
        RC::Unreal::UObject* container,
        std::string_view itemId)
    {
        using namespace RC;
        using namespace RC::Unreal;
        if (!container)
        {
            return std::nullopt;
        }
        auto* function = container->GetFunctionByName(
            FName(STR("GetItemStackCount")));
        auto* itemParameter = FindTypedProperty<FNameProperty>(
            function,
            STR("StaticItemId"));
        auto* returnParameter = FindTypedProperty<FIntProperty>(
            function,
            STR("ReturnValue"));
        if (!function || !itemParameter || !returnParameter)
        {
            return std::nullopt;
        }

        std::vector<uint8> parameters(
            std::max<std::size_t>(function->GetPropertiesSize(), 1),
            0);
        function->InitializeStruct(parameters.data());
        const auto wideItemId = to_wstring(std::string{itemId});
        *itemParameter->ContainerPtrToValuePtr<FName>(parameters.data()) =
            FName(wideItemId.c_str());
        container->ProcessEvent(function, parameters.data());
        const auto result = *returnParameter
            ->ContainerPtrToValuePtr<int32>(parameters.data());
        function->DestroyStruct(parameters.data());
        return result;
    }

    struct InventoryProperties
    {
        struct ContainerField
        {
            const char* Kind;
            RC::Unreal::FStructProperty* Property;
        };

        RC::Unreal::UClass* InventoryClass{};
        RC::Unreal::UClass* ContainerClass{};
        RC::Unreal::UClass* SlotClass{};
        RC::Unreal::FStructProperty* OwnerPlayerUId{};
        RC::Unreal::FStructProperty* InventoryInfo{};
        std::array<ContainerField, 6> ContainerFields{};
        RC::Unreal::FStructProperty* ContainerId{};
        RC::Unreal::FArrayProperty* SlotArray{};
        RC::Unreal::FIntProperty* SlotIndex{};
        RC::Unreal::FStructProperty* ItemId{};
        RC::Unreal::FNameProperty* StaticItemId{};
        RC::Unreal::FIntProperty* StackCount{};

        [[nodiscard]] bool IsReady() const
        {
            return InventoryClass && ContainerClass && SlotClass &&
                OwnerPlayerUId && InventoryInfo && ContainerId && SlotArray &&
                SlotIndex && ItemId && StaticItemId && StackCount &&
                std::ranges::all_of(ContainerFields, [](const ContainerField& field) {
                    return field.Property != nullptr;
                });
        }
    };

    InventoryProperties ResolveInventoryProperties()
    {
        using namespace RC::Unreal;
        using RC::Unreal::UObjectGlobals::StaticFindObject;
        InventoryProperties result{};
        result.InventoryClass = StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalPlayerInventoryData"));
        result.ContainerClass = StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalItemContainer"));
        result.SlotClass = StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalItemSlot"));
        result.OwnerPlayerUId = FindTypedProperty<FStructProperty>(
            result.InventoryClass, STR("OwnerPlayerUId"));
        result.InventoryInfo = FindTypedProperty<FStructProperty>(
            result.InventoryClass, STR("MyInventoryInfo"));

        auto* infoStruct = result.InventoryInfo
            ? static_cast<UStruct*>(result.InventoryInfo->GetStruct())
            : nullptr;
        result.ContainerFields = {{
            {"common", FindTypedProperty<FStructProperty>(infoStruct, STR("CommonContainerId"))},
            {"dropSlot", FindTypedProperty<FStructProperty>(infoStruct, STR("DropSlotContainerId"))},
            {"essential", FindTypedProperty<FStructProperty>(infoStruct, STR("EssentialContainerId"))},
            {"weaponLoadout", FindTypedProperty<FStructProperty>(infoStruct, STR("WeaponLoadOutContainerId"))},
            {"armor", FindTypedProperty<FStructProperty>(infoStruct, STR("PlayerEquipArmorContainerId"))},
            {"food", FindTypedProperty<FStructProperty>(infoStruct, STR("FoodEquipContainerId"))}
        }};

        result.ContainerId = FindTypedProperty<FStructProperty>(
            result.ContainerClass, STR("ID"));
        result.SlotArray = FindTypedProperty<FArrayProperty>(
            result.ContainerClass, STR("ItemSlotArray"));
        result.SlotIndex = FindTypedProperty<FIntProperty>(
            result.SlotClass, STR("SlotIndex"));
        result.ItemId = FindTypedProperty<FStructProperty>(
            result.SlotClass, STR("ItemId"));
        result.StackCount = FindTypedProperty<FIntProperty>(
            result.SlotClass, STR("StackCount"));
        auto* itemIdStruct = result.ItemId
            ? static_cast<UStruct*>(result.ItemId->GetStruct())
            : nullptr;
        result.StaticItemId = FindTypedProperty<FNameProperty>(
            itemIdStruct, STR("StaticId"));
        return result;
    }

    struct PalProperties
    {
        RC::Unreal::UClass* ParameterClass{};
        RC::Unreal::FStructProperty* IndividualId{};
        RC::Unreal::FStructProperty* InstanceId{};
        RC::Unreal::FStructProperty* SaveParameter{};
        RC::Unreal::FStructProperty* SaveParameterMirror{};
        RC::Unreal::FNameProperty* CharacterId{};
        RC::Unreal::FByteProperty* Level{};
        RC::Unreal::FByteProperty* Rank{};
        RC::Unreal::FInt64Property* Exp{};
        RC::Unreal::FStrProperty* NickName{};
        RC::Unreal::FStrProperty* FilteredNickName{};
        RC::Unreal::FBoolProperty* IsRarePal{};
        RC::Unreal::FBoolProperty* IsFavoritePal{};
        RC::Unreal::FByteProperty* TalentHp{};
        RC::Unreal::FByteProperty* TalentMelee{};
        RC::Unreal::FByteProperty* TalentShot{};
        RC::Unreal::FByteProperty* TalentDefense{};
        RC::Unreal::FArrayProperty* EquipWaza{};
        RC::Unreal::FArrayProperty* MasteredWaza{};
        RC::Unreal::FArrayProperty* PassiveSkillList{};
        RC::Unreal::FStructProperty* OwnerPlayerUId{};
        RC::Unreal::FStructProperty* SlotId{};
        RC::Unreal::FStructProperty* SlotContainerId{};
        RC::Unreal::FIntProperty* SlotIndex{};

        [[nodiscard]] bool IsReady() const
        {
            return ParameterClass && IndividualId && InstanceId && SaveParameter &&
                CharacterId && Level && Rank && Exp && NickName && IsFavoritePal &&
                TalentHp && TalentMelee && TalentShot && TalentDefense &&
                EquipWaza && MasteredWaza && PassiveSkillList &&
                OwnerPlayerUId && SlotId &&
                SlotContainerId && SlotIndex;
        }
    };

    PalProperties ResolvePalProperties()
    {
        using namespace RC::Unreal;
        using RC::Unreal::UObjectGlobals::StaticFindObject;

        PalProperties result{};
        result.ParameterClass = StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalIndividualCharacterParameter"));
        result.IndividualId = FindTypedProperty<FStructProperty>(
            result.ParameterClass,
            STR("IndividualId"));
        auto* individualIdStruct = result.IndividualId
            ? static_cast<UStruct*>(result.IndividualId->GetStruct())
            : nullptr;
        result.InstanceId = FindTypedProperty<FStructProperty>(
            individualIdStruct,
            STR("InstanceId"));
        if (!result.InstanceId)
        {
            result.InstanceId = FindTypedProperty<FStructProperty>(
                individualIdStruct,
                STR("InstanceID"));
        }
        result.SaveParameter = FindTypedProperty<FStructProperty>(
            result.ParameterClass,
            STR("SaveParameter"));
        result.SaveParameterMirror = FindTypedProperty<FStructProperty>(
            result.ParameterClass,
            STR("SaveParameterMirror"));
        auto* saveStruct = result.SaveParameter
            ? static_cast<UStruct*>(result.SaveParameter->GetStruct())
            : nullptr;
        result.CharacterId = FindTypedProperty<FNameProperty>(saveStruct, STR("CharacterID"));
        result.Level = FindTypedProperty<FByteProperty>(saveStruct, STR("Level"));
        result.Rank = FindTypedProperty<FByteProperty>(saveStruct, STR("Rank"));
        result.Exp = FindTypedProperty<FInt64Property>(saveStruct, STR("Exp"));
        result.NickName = FindTypedProperty<FStrProperty>(saveStruct, STR("NickName"));
        result.FilteredNickName = FindTypedProperty<FStrProperty>(saveStruct, STR("FilteredNickName"));
        result.IsRarePal = FindTypedProperty<FBoolProperty>(saveStruct, STR("IsRarePal"));
        result.IsFavoritePal = FindTypedProperty<FBoolProperty>(saveStruct, STR("IsFavoritePal"));
        result.TalentHp = FindTypedProperty<FByteProperty>(saveStruct, STR("Talent_HP"));
        result.TalentMelee = FindTypedProperty<FByteProperty>(saveStruct, STR("Talent_Melee"));
        result.TalentShot = FindTypedProperty<FByteProperty>(saveStruct, STR("Talent_Shot"));
        result.TalentDefense = FindTypedProperty<FByteProperty>(saveStruct, STR("Talent_Defense"));
        result.EquipWaza = FindTypedProperty<FArrayProperty>(saveStruct, STR("EquipWaza"));
        result.MasteredWaza = FindTypedProperty<FArrayProperty>(saveStruct, STR("MasteredWaza"));
        result.PassiveSkillList = FindTypedProperty<FArrayProperty>(saveStruct, STR("PassiveSkillList"));
        result.OwnerPlayerUId = FindTypedProperty<FStructProperty>(saveStruct, STR("OwnerPlayerUId"));
        result.SlotId = FindTypedProperty<FStructProperty>(saveStruct, STR("SlotId"));

        auto* slotStruct = result.SlotId
            ? static_cast<UStruct*>(result.SlotId->GetStruct())
            : nullptr;
        result.SlotContainerId = FindTypedProperty<FStructProperty>(
            slotStruct,
            STR("ContainerId"));
        result.SlotIndex = FindTypedProperty<FIntProperty>(slotStruct, STR("SlotIndex"));
        return result;
    }

    struct PalSnapshot
    {
        std::string Json;
        std::string InstanceId;
        std::string OwnerPlayerUId;
        std::vector<std::string> PassiveSkills;
        std::vector<std::string> EquippedActiveSkills;
        std::vector<std::string> MasteredActiveSkills;
        std::uint64_t Revision{};
    };

    std::optional<PalSnapshot> ReadPalSnapshot(
        const PalProperties& mapping,
        RC::Unreal::UObject* object)
    {
        using namespace RC;
        using namespace RC::Unreal;

        if (!mapping.IsReady() || !object || !object->IsA(mapping.ParameterClass))
        {
            return std::nullopt;
        }

        auto* individualIdMemory = mapping.IndividualId
            ->ContainerPtrToValuePtr<uint8>(object);
        const auto instanceId = ReadNestedGuid(mapping.InstanceId, individualIdMemory);
        auto* saveMemory = mapping.SaveParameter
            ->ContainerPtrToValuePtr<uint8>(object);
        const auto ownerPlayerUId = ReadNestedGuid(mapping.OwnerPlayerUId, saveMemory);
        if (!instanceId || !ownerPlayerUId || !saveMemory)
        {
            return std::nullopt;
        }

        const auto instanceIdText = GuidToString(*instanceId);
        const auto ownerPlayerUIdText = GuidToString(*ownerPlayerUId);
        const auto* characterId = mapping.CharacterId
            ->ContainerPtrToValuePtr<FName>(saveMemory);
        const auto characterIdText = characterId
            ? to_string(characterId->ToString())
            : std::string{"Unknown"};
        const auto* nickNameValue = mapping.NickName
            ->ContainerPtrToValuePtr<FString>(saveMemory);
        const auto nickName = nickNameValue
            ? to_string(**nickNameValue)
            : std::string{};
        const auto level = *mapping.Level->ContainerPtrToValuePtr<uint8>(saveMemory);
        const auto rank = *mapping.Rank->ContainerPtrToValuePtr<uint8>(saveMemory);
        const auto exp = *mapping.Exp->ContainerPtrToValuePtr<int64>(saveMemory);
        const auto favorite = mapping.IsFavoritePal->GetPropertyValueInContainer(saveMemory);
        const auto rare = mapping.IsRarePal
            ? mapping.IsRarePal->GetPropertyValueInContainer(saveMemory)
            : false;
        const auto talentHp = *mapping.TalentHp->ContainerPtrToValuePtr<uint8>(saveMemory);
        const auto talentMelee = *mapping.TalentMelee->ContainerPtrToValuePtr<uint8>(saveMemory);
        const auto talentShot = *mapping.TalentShot->ContainerPtrToValuePtr<uint8>(saveMemory);
        const auto talentDefense = *mapping.TalentDefense->ContainerPtrToValuePtr<uint8>(saveMemory);

        auto* slotIdMemory = mapping.SlotId->ContainerPtrToValuePtr<uint8>(saveMemory);
        const auto containerId = ReadNestedGuid(mapping.SlotContainerId, slotIdMemory);
        const auto slotIndex = slotIdMemory
            ? *mapping.SlotIndex->ContainerPtrToValuePtr<int32>(slotIdMemory)
            : -1;

        std::string passiveSkillsJson{"["};
        std::vector<std::string> passiveSkillNames;
        const auto* passiveSkills = mapping.PassiveSkillList
            ->ContainerPtrToValuePtr<TArray<FName>>(saveMemory);
        const auto passiveSkillCount = passiveSkills
            ? static_cast<std::size_t>(std::max(passiveSkills->Num(), 0))
            : 0;
        const auto returnedPassiveSkillCount = std::min(
            passiveSkillCount,
            MaxReturnedPassiveSkills);
        for (std::size_t index = 0; index < returnedPassiveSkillCount; ++index)
        {
            if (index > 0)
            {
                passiveSkillsJson += ',';
            }
            const auto passiveSkillName = to_string(
                (*passiveSkills)[static_cast<int32>(index)].ToString());
            passiveSkillsJson += "\"" + EscapeJson(passiveSkillName) + "\"";
            passiveSkillNames.push_back(passiveSkillName);
        }
        passiveSkillsJson += ']';

        const auto equippedActiveSkills = ReadEnumArray(
            mapping.EquipWaza,
            saveMemory,
            MaxReturnedActiveSkills);
        const auto masteredActiveSkills = ReadEnumArray(
            mapping.MasteredWaza,
            saveMemory,
            MaxReturnedActiveSkills);

        const auto canonical = instanceIdText + "|" + ownerPlayerUIdText + "|" +
            characterIdText + "|" + nickName + "|" + std::to_string(level) + "|" +
            std::to_string(rank) + "|" + std::to_string(exp) + "|" +
            (favorite ? "1" : "0") + "|" + std::to_string(talentHp) + "|" +
            std::to_string(talentMelee) + "|" + std::to_string(talentShot) + "|" +
            std::to_string(talentDefense) + "|" +
            JoinStringsForRevision(passiveSkillNames) + "|" +
            JoinStringsForRevision(equippedActiveSkills.Names) + "|" +
            JoinStringsForRevision(masteredActiveSkills.Names);
        const auto revision = StableRevision(canonical);

        const auto json = std::string{"{"} +
            "\"instanceId\":\"" + EscapeJson(instanceIdText) + "\"," +
            "\"ownerPlayerUId\":\"" + EscapeJson(ownerPlayerUIdText) + "\"," +
            "\"characterId\":\"" + EscapeJson(characterIdText) + "\"," +
            "\"nickname\":\"" + EscapeJson(nickName) + "\"," +
            "\"level\":" + std::to_string(level) + "," +
            "\"rank\":" + std::to_string(rank) + "," +
            "\"exp\":" + std::to_string(exp) + "," +
            "\"rare\":" + (rare ? "true" : "false") + "," +
            "\"favorite\":" + (favorite ? "true" : "false") + "," +
            "\"talents\":{" +
                "\"hp\":" + std::to_string(talentHp) + "," +
                "\"melee\":" + std::to_string(talentMelee) + "," +
                "\"shot\":" + std::to_string(talentShot) + "," +
                "\"defense\":" + std::to_string(talentDefense) + "}," +
            "\"passiveSkills\":" + passiveSkillsJson + "," +
            "\"activeSkills\":{" +
                "\"equipped\":" + equippedActiveSkills.Json + "," +
                "\"mastered\":" + masteredActiveSkills.Json + "}," +
            "\"location\":{" +
                "\"containerId\":" + (containerId
                    ? "\"" + EscapeJson(GuidToString(*containerId)) + "\""
                    : std::string{"null"}) + "," +
                "\"slotIndex\":" + std::to_string(slotIndex) + "}," +
            "\"revision\":" + std::to_string(revision) + "}";

        return PalSnapshot{
            .Json = json,
            .InstanceId = instanceIdText,
            .OwnerPlayerUId = ownerPlayerUIdText,
            .PassiveSkills = passiveSkillNames,
            .EquippedActiveSkills = equippedActiveSkills.Names,
            .MasteredActiveSkills = masteredActiveSkills.Names,
            .Revision = revision
        };
    }

    struct PlayerProgressionProperties
    {
        RC::Unreal::UClass* ParameterClass{};
        RC::Unreal::FStructProperty* IndividualId{};
        RC::Unreal::FStructProperty* InstanceId{};
        RC::Unreal::FStructProperty* PlayerStateIndividualHandleId{};
        RC::Unreal::FStructProperty* SaveParameter{};
        RC::Unreal::FBoolProperty* IsPlayer{};
        RC::Unreal::FStructProperty* OwnerPlayerUId{};
        RC::Unreal::FByteProperty* Level{};
        RC::Unreal::FInt64Property* Exp{};
        RC::Unreal::FUInt16Property* UnusedStatusPoint{};
        RC::Unreal::FArrayProperty* GotStatusPointList{};
        RC::Unreal::FNameProperty* StatusName{};
        RC::Unreal::FIntProperty* StatusPoint{};
        RC::Unreal::UClass* TechnologyClass{};
        RC::Unreal::FStructProperty* TechnologyOwnerPlayerUId{};
        RC::Unreal::FIntProperty* TechnologyPoint{};
        RC::Unreal::FIntProperty* BossTechnologyPoint{};

        [[nodiscard]] bool IsReady() const
        {
            return ParameterClass && IndividualId && InstanceId &&
                PlayerStateIndividualHandleId && SaveParameter &&
                IsPlayer && OwnerPlayerUId && Level && Exp && UnusedStatusPoint &&
                GotStatusPointList && StatusName && StatusPoint && TechnologyClass &&
                TechnologyOwnerPlayerUId && TechnologyPoint && BossTechnologyPoint;
        }
    };

    PlayerProgressionProperties ResolvePlayerProgressionProperties()
    {
        using namespace RC::Unreal;
        using RC::Unreal::UObjectGlobals::StaticFindObject;
        PlayerProgressionProperties result{};
        result.ParameterClass = StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalIndividualCharacterParameter"));
        result.IndividualId = FindTypedProperty<FStructProperty>(
            result.ParameterClass, STR("IndividualId"));
        auto* individualIdStruct = result.IndividualId
            ? static_cast<UStruct*>(result.IndividualId->GetStruct())
            : nullptr;
        result.InstanceId = FindTypedProperty<FStructProperty>(
            individualIdStruct, STR("InstanceId"));
        if (!result.InstanceId)
        {
            result.InstanceId = FindTypedProperty<FStructProperty>(
                individualIdStruct, STR("InstanceID"));
        }
        auto* playerStateClass = StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalPlayerState"));
        result.PlayerStateIndividualHandleId = FindTypedProperty<FStructProperty>(
            playerStateClass, STR("IndividualHandleId"));
        result.SaveParameter = FindTypedProperty<FStructProperty>(
            result.ParameterClass, STR("SaveParameter"));
        auto* saveStruct = result.SaveParameter
            ? static_cast<UStruct*>(result.SaveParameter->GetStruct())
            : nullptr;
        result.IsPlayer = FindTypedProperty<FBoolProperty>(saveStruct, STR("IsPlayer"));
        result.OwnerPlayerUId = FindTypedProperty<FStructProperty>(
            saveStruct, STR("OwnerPlayerUId"));
        result.Level = FindTypedProperty<FByteProperty>(saveStruct, STR("Level"));
        result.Exp = FindTypedProperty<FInt64Property>(saveStruct, STR("Exp"));
        result.UnusedStatusPoint = FindTypedProperty<FUInt16Property>(
            saveStruct, STR("UnusedStatusPoint"));
        result.GotStatusPointList = FindTypedProperty<FArrayProperty>(
            saveStruct, STR("GotStatusPointList"));
        auto* statusStruct = result.GotStatusPointList &&
                result.GotStatusPointList->GetInner() &&
                result.GotStatusPointList->GetInner()->IsA<FStructProperty>()
            ? static_cast<FStructProperty*>(result.GotStatusPointList->GetInner())
                ->GetStruct().Get()
            : nullptr;
        result.StatusName = FindTypedProperty<FNameProperty>(
            statusStruct, STR("StatusName"));
        result.StatusPoint = FindTypedProperty<FIntProperty>(
            statusStruct, STR("StatusPoint"));

        result.TechnologyClass = StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalTechnologyData"));
        result.TechnologyOwnerPlayerUId = FindTypedProperty<FStructProperty>(
            result.TechnologyClass, STR("OwnerPlayerUId"));
        result.TechnologyPoint = FindTypedProperty<FIntProperty>(
            result.TechnologyClass, STR("TechnologyPoint"));
        result.BossTechnologyPoint = FindTypedProperty<FIntProperty>(
            result.TechnologyClass, STR("bossTechnologyPoint"));
        return result;
    }

    std::optional<std::int64_t> CalculateExperienceToNextLevel(
        std::int64_t totalExperience)
    {
        using namespace RC;
        using namespace RC::Unreal;
        if (totalExperience < 0 ||
            totalExperience > std::numeric_limits<std::int32_t>::max())
        {
            return std::nullopt;
        }
        auto* function = UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalExpDatabase:CalcNeedLevelUpExp"));
        auto* target = UObjectGlobals::StaticFindObject<UObject*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.Default__PalExpDatabase"));
        auto* totalExpProperty = FindTypedProperty<FIntProperty>(
            function, STR("TotalEXP"));
        auto* isPlayerProperty = FindTypedProperty<FBoolProperty>(
            function, STR("IsPlayer"));
        auto* returnProperty = FindTypedProperty<FInt64Property>(
            function, STR("ReturnValue"));
        if (!function || !target || !totalExpProperty || !isPlayerProperty ||
            !returnProperty || function->GetPropertiesSize() != 16)
        {
            return std::nullopt;
        }
        std::vector<uint8> parameters(function->GetPropertiesSize(), 0);
        function->InitializeStruct(parameters.data());
        *totalExpProperty->ContainerPtrToValuePtr<int32>(parameters.data()) =
            static_cast<int32>(totalExperience);
        isPlayerProperty->SetPropertyValueInContainer(parameters.data(), true);
        target->ProcessEvent(function, parameters.data());
        const auto result = *returnProperty
            ->ContainerPtrToValuePtr<int64>(parameters.data());
        function->DestroyStruct(parameters.data());
        return result >= 0 ? std::optional<std::int64_t>{result} : std::nullopt;
    }

    std::optional<std::int32_t> CalculateLevelFromExperience(
        std::int64_t totalExperience)
    {
        using namespace RC;
        using namespace RC::Unreal;
        if (totalExperience < 0 ||
            totalExperience > std::numeric_limits<std::int32_t>::max())
        {
            return std::nullopt;
        }
        auto* function = UObjectGlobals::StaticFindObject<UFunction*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalExpDatabase:CalcLevelFromTotalExp"));
        auto* target = UObjectGlobals::StaticFindObject<UObject*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.Default__PalExpDatabase"));
        auto* totalExpProperty = FindTypedProperty<FIntProperty>(
            function, STR("TotalEXP"));
        auto* isPlayerProperty = FindTypedProperty<FBoolProperty>(
            function, STR("IsPlayer"));
        auto* returnProperty = FindTypedProperty<FIntProperty>(
            function, STR("ReturnValue"));
        if (!function || !target || !totalExpProperty || !isPlayerProperty ||
            !returnProperty || function->GetPropertiesSize() != 12)
        {
            return std::nullopt;
        }
        std::vector<uint8> parameters(function->GetPropertiesSize(), 0);
        function->InitializeStruct(parameters.data());
        *totalExpProperty->ContainerPtrToValuePtr<int32>(parameters.data()) =
            static_cast<int32>(totalExperience);
        isPlayerProperty->SetPropertyValueInContainer(parameters.data(), true);
        target->ProcessEvent(function, parameters.data());
        const auto result = *returnProperty
            ->ContainerPtrToValuePtr<int32>(parameters.data());
        function->DestroyStruct(parameters.data());
        return result > 0 ? std::optional<std::int32_t>{result} : std::nullopt;
    }

    std::optional<std::int64_t> CalculateMinimumExperienceForLevel(
        std::int32_t targetLevel)
    {
        if (targetLevel < 1 || targetLevel > 100)
        {
            return std::nullopt;
        }
        std::int64_t low = 0;
        std::int64_t high = std::numeric_limits<std::int32_t>::max();
        const auto maximumLevel = CalculateLevelFromExperience(high);
        if (!maximumLevel || *maximumLevel < targetLevel)
        {
            return std::nullopt;
        }
        while (low < high)
        {
            const auto middle = low + (high - low) / 2;
            const auto level = CalculateLevelFromExperience(middle);
            if (!level)
            {
                return std::nullopt;
            }
            if (*level >= targetLevel)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        return low;
    }

    std::int32_t ReadAllocatedStatusPoint(
        const PlayerProgressionProperties& mapping,
        void* saveMemory,
        std::string_view requestedId)
    {
        using namespace RC;
        using namespace RC::Unreal;
        auto* arrayMemory = mapping.GotStatusPointList
            ->ContainerPtrToValuePtr<void>(saveMemory);
        FScriptArrayHelper helper(mapping.GotStatusPointList, arrayMemory);
        for (int32 index = 0; index < helper.Num(); ++index)
        {
            auto* entry = helper.GetRawPtr(index);
            const auto* nameValue = mapping.StatusName
                ->ContainerPtrToValuePtr<FName>(entry);
            if (nameValue && CanonicalPlayerStatusId(
                    to_string(nameValue->ToString())) == requestedId)
            {
                return *mapping.StatusPoint
                    ->ContainerPtrToValuePtr<int32>(entry);
            }
        }
        return 0;
    }

    std::uint64_t PlayerProgressionRevision(
        const PlayerProgressionProperties& mapping,
        RC::Unreal::UObject* parameter,
        RC::Unreal::UObject* technology,
        std::string_view resolvedPlayerUid)
    {
        using namespace RC;
        using namespace RC::Unreal;
        auto* saveMemory = mapping.SaveParameter
            ->ContainerPtrToValuePtr<uint8>(parameter);
        const auto savedPlayerUid = ReadNestedGuid(
            mapping.OwnerPlayerUId, saveMemory);
        const auto playerUid = resolvedPlayerUid.empty()
            ? (savedPlayerUid ? GuidToString(*savedPlayerUid) : std::string{"none"})
            : std::string{resolvedPlayerUid};
        auto* individualIdMemory = mapping.IndividualId
            ->ContainerPtrToValuePtr<uint8>(parameter);
        const auto instanceId = ReadNestedGuid(
            mapping.InstanceId, individualIdMemory);
        const auto level = *mapping.Level->ContainerPtrToValuePtr<uint8>(saveMemory);
        const auto experience = *mapping.Exp
            ->ContainerPtrToValuePtr<int64>(saveMemory);
        const auto unused = *mapping.UnusedStatusPoint
            ->ContainerPtrToValuePtr<uint16>(saveMemory);
        std::string canonical = playerUid + "|" +
            (instanceId ? GuidToString(*instanceId) : std::string{"none"}) + "|" +
            std::to_string(level) + "|" +
            std::to_string(experience) + "|" + std::to_string(unused) + "|";
        auto* arrayMemory = mapping.GotStatusPointList
            ->ContainerPtrToValuePtr<void>(saveMemory);
        FScriptArrayHelper helper(mapping.GotStatusPointList, arrayMemory);
        for (int32 index = 0; index < helper.Num(); ++index)
        {
            auto* entry = helper.GetRawPtr(index);
            const auto* nameValue = mapping.StatusName
                ->ContainerPtrToValuePtr<FName>(entry);
            canonical += (nameValue ? to_string(nameValue->ToString()) : "None") +
                std::string{":"} + std::to_string(*mapping.StatusPoint
                    ->ContainerPtrToValuePtr<int32>(entry)) + ",";
        }
        if (technology)
        {
            canonical += "|" + std::to_string(*mapping.TechnologyPoint
                ->ContainerPtrToValuePtr<int32>(technology));
            canonical += "|" + std::to_string(*mapping.BossTechnologyPoint
                ->ContainerPtrToValuePtr<int32>(technology));
        }
        else
        {
            canonical += "|none|none";
        }
        return StableRevision(canonical);
    }

    bool InvokeSingleIntFunction(
        RC::Unreal::UObject* target,
        const RC::File::CharType* functionName,
        const RC::File::CharType* functionPath,
        std::string_view expectedFullName,
        const RC::File::CharType* parameterName,
        std::int32_t value)
    {
        using namespace RC;
        using namespace RC::Unreal;
        auto* function = ResolveFunction(target, functionName, functionPath);
        auto* parameter = FindTypedProperty<FIntProperty>(function, parameterName);
        if (!target || !function || !parameter || function->GetReturnProperty() ||
            function->GetPropertiesSize() != sizeof(std::int32_t) ||
            CountParameters(function) != 1 ||
            to_string(function->GetFullName()) != expectedFullName)
        {
            return false;
        }
        std::vector<uint8> parameters(function->GetPropertiesSize(), 0);
        function->InitializeStruct(parameters.data());
        *parameter->ContainerPtrToValuePtr<int32>(parameters.data()) = value;
        target->ProcessEvent(function, parameters.data());
        function->DestroyStruct(parameters.data());
        return true;
    }

    bool InvokePlayerStatusAllocation(
        RC::Unreal::UObject* controller,
        std::string_view statusId,
        std::int32_t targetRank)
    {
        using namespace RC;
        using namespace RC::Unreal;
        auto* function = ResolveFunction(
            controller,
            STR("Debug_SetStatusPoint_ToServer"),
            STR("/Script/Pal.PalPlayerController:Debug_SetStatusPoint_ToServer"));
        auto* nameProperty = FindTypedProperty<FNameProperty>(
            function, STR("StatusPointName"));
        auto* pointProperty = FindTypedProperty<FIntProperty>(
            function, STR("StatusLevel"));
        if (!controller || !function || !nameProperty || !pointProperty ||
            function->GetReturnProperty() || function->GetPropertiesSize() != 12 ||
            CountParameters(function) != 2 ||
            to_string(function->GetFullName()) !=
                "Function /Script/Pal.PalPlayerController:Debug_SetStatusPoint_ToServer")
        {
            return false;
        }
        std::vector<uint8> parameters(function->GetPropertiesSize(), 0);
        function->InitializeStruct(parameters.data());
        const auto wideName = to_wstring(NativePlayerStatusName(statusId));
        *nameProperty->ContainerPtrToValuePtr<FName>(parameters.data()) =
            FName(wideName.c_str());
        *pointProperty->ContainerPtrToValuePtr<int32>(parameters.data()) =
            targetRank;
        controller->ProcessEvent(function, parameters.data());
        function->DestroyStruct(parameters.data());
        return true;
    }

}

namespace PalControl::Game
{
    Contracts::CommandResult PalworldGameAdapter::Execute(
        const Contracts::CommandEnvelope& command) const
    {
        if (command.Operation == "players.probe")
        {
            return ProbePlayers(command);
        }
        if (command.Operation == "players.schema")
        {
            return ReadPlayerSchema(command);
        }
        if (command.Operation == "players.progression.schema")
        {
            return ReadPlayerProgressionSchema(command);
        }
        if (command.Operation == "players.progression.probe")
        {
            return ProbePlayerProgression(command);
        }
        if (command.Operation == "players.progression.mutate")
        {
            return MutatePlayerProgression(command);
        }
        if (command.Operation == "inventory.schema")
        {
            return ReadInventorySchema(command);
        }
        if (command.Operation == "inventory.probe")
        {
            return ProbeInventory(command);
        }
        if (command.Operation == "inventory.mutate")
        {
            return MutateInventory(command);
        }
        if (command.Operation == "inventory.consume")
        {
            const bool idempotencyKeyValid =
                command.IdempotencyKey.size() >= 8 &&
                command.IdempotencyKey.size() <= 128 &&
                std::ranges::none_of(command.IdempotencyKey, [](unsigned char character)
                {
                    return std::iscntrl(character) != 0;
                });
            const bool requestHashValid = command.RequestHash.size() == 64 &&
                std::ranges::all_of(command.RequestHash, [](unsigned char character)
                {
                    return (character >= '0' && character <= '9') ||
                        (character >= 'a' && character <= 'f');
                });
            if (!idempotencyKeyValid || !requestHashValid ||
                command.ServerId.empty() || command.PayloadJson.size() > 262'144)
            {
                return Failure(
                    command,
                    "INVALID_INVENTORY_CONSUME_ENVELOPE",
                    "Atomic consume requires a scoped idempotency key, lowercase SHA-256 request hash, server id, and a payload no larger than 256 KiB.");
            }

            const auto cacheKey = command.ServerId + "\n" + command.Operation +
                "\n" + command.IdempotencyKey;
            if (const auto cached = consumeCache_.find(cacheKey);
                cached != consumeCache_.end())
            {
                if (cached->second.RequestHash != command.RequestHash ||
                    cached->second.PayloadJson != command.PayloadJson)
                {
                    return Failure(
                        command,
                        "INVENTORY_CONSUME_IDEMPOTENCY_CONFLICT",
                        "The idempotency key was already used with a different consume payload.");
                }
                auto replay = cached->second.Result;
                replay.CommandId = command.CommandId;
                return replay;
            }

            const auto expired = IsDeadlineExpired(command.Deadline);
            if (!expired)
            {
                return Failure(
                    command,
                    "INVALID_COMMAND_DEADLINE",
                    "The consume deadline must be an RFC 3339 timestamp with an explicit UTC offset.");
            }
            if (*expired)
            {
                return Failure(
                    command,
                    "COMMAND_DEADLINE_EXPIRED",
                    "The atomic consume deadline expired before game-thread execution.");
            }

            auto result = ConsumeInventory(command);
            constexpr std::size_t ConsumeCacheLimit = 256;
            if (consumeCache_.size() >= ConsumeCacheLimit &&
                !consumeCacheOrder_.empty())
            {
                consumeCache_.erase(consumeCacheOrder_.front());
                consumeCacheOrder_.pop_front();
            }
            consumeCacheOrder_.push_back(cacheKey);
            consumeCache_.emplace(cacheKey, CachedConsumeResult{
                .RequestHash = command.RequestHash,
                .PayloadJson = command.PayloadJson,
                .Result = result
            });
            return result;
        }
        if (command.Operation == "pals.schema")
        {
            return ReadPalSchema(command);
        }
        if (command.Operation == "pals.probe")
        {
            return ProbePals(command);
        }
        if (command.Operation == "pals.skills.catalog")
        {
            return ReadPalSkillCatalog(command);
        }
        if (command.Operation == "pals.mutate")
        {
            return MutatePal(command);
        }
        if (command.Operation == "announcements.overlay.send")
        {
            return SendOverlayAnnouncement(command);
        }
        if (command.Operation == "announcements.overlay.probe")
        {
            return ProbeOverlayAnnouncement(command);
        }
        if (command.Operation == "announcements.banner.probe")
        {
            return ProbeTopBannerAnnouncement(command);
        }
        if (command.Operation == "announcements.banner.send")
        {
            return SendTopBannerAnnouncement(command);
        }
        if (command.Operation == "ui.notifications.probe")
        {
            return ProbeInGameNotifications(command);
        }
        if (command.Operation == "ui.notifications.send")
        {
            return SendInGameNotification(command);
        }

        return Failure(
            command,
            "OPERATION_NOT_SUPPORTED",
            "This Native Mod operation is not enabled in read-only safe mode.");
    }

    Contracts::CommandResult PalworldGameAdapter::ProbeInGameNotifications(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        const auto targets = ResolveNotificationTargets();
        if (!targets.IsReady())
        {
            return Failure(
                command,
                targets.ErrorCode,
                targets.ErrorMessage);
        }

        auto* bossFunction = ResolveFunction(
            nullptr,
            STR("ShowBossDefeatRewardUI_ToClient"),
            STR("/Script/Pal.PalNetworkPlayerComponent:ShowBossDefeatRewardUI_ToClient"));
        auto* bossExpFunction = ResolveFunction(
            nullptr,
            STR("ShowDefeatBossBonusExpReward_ToClient"),
            STR("/Script/Pal.PalNetworkPlayerComponent:ShowDefeatBossBonusExpReward_ToClient"));
        auto* expeditionExpFunction = ResolveFunction(
            nullptr,
            STR("ShowExpeditionBonusExpReward_ToClient"),
            STR("/Script/Pal.PalNetworkPlayerComponent:ShowExpeditionBonusExpReward_ToClient"));
        auto* unlockHardModeFunction = ResolveFunction(
            nullptr,
            STR("ShowUnlockHardModeUI_ToClient"),
            STR("/Script/Pal.PalNetworkPlayerComponent:ShowUnlockHardModeUI_ToClient"));

        BossDefeatNotificationBinding bossBinding{};
        ExpNotificationBinding bossExpBinding{};
        ExpNotificationBinding expeditionExpBinding{};
        const bool bossReady = BindBossDefeatNotification(
                bossFunction,
                bossBinding) &&
            ComponentsCanInvokeCanonicalFunction(
                targets.Components,
                targets.ComponentClass,
                STR("ShowBossDefeatRewardUI_ToClient"),
                bossFunction);
        const bool bossExpReady = BindExpNotification(
                bossExpFunction,
                "Function /Script/Pal.PalNetworkPlayerComponent:ShowDefeatBossBonusExpReward_ToClient",
                bossExpBinding) &&
            ComponentsCanInvokeCanonicalFunction(
                targets.Components,
                targets.ComponentClass,
                STR("ShowDefeatBossBonusExpReward_ToClient"),
                bossExpFunction);
        const bool expeditionExpReady = BindExpNotification(
                expeditionExpFunction,
                "Function /Script/Pal.PalNetworkPlayerComponent:ShowExpeditionBonusExpReward_ToClient",
                expeditionExpBinding) &&
            ComponentsCanInvokeCanonicalFunction(
                targets.Components,
                targets.ComponentClass,
                STR("ShowExpeditionBonusExpReward_ToClient"),
                expeditionExpFunction);
        const bool unlockHardModeReady = ValidateNoParameterNotification(
                unlockHardModeFunction,
                "Function /Script/Pal.PalNetworkPlayerComponent:ShowUnlockHardModeUI_ToClient") &&
            ComponentsCanInvokeCanonicalFunction(
                targets.Components,
                targets.ComponentClass,
                STR("ShowUnlockHardModeUI_ToClient"),
                unlockHardModeFunction);

        if (!bossReady && !bossExpReady && !expeditionExpReady &&
            !unlockHardModeReady)
        {
            return Failure(
                command,
                "NATIVE_NOTIFICATION_PRESETS_UNAVAILABLE",
                "No display-only reliable Client RPC matched the fail-closed notification preset signatures for this game build. "
                "boss=" + DescribeNotificationFunction(bossFunction) +
                ",components=" + DescribeComponentFunctionResolutions(
                    targets.Components,
                    STR("ShowBossDefeatRewardUI_ToClient")) +
                "; bossExp=" + DescribeNotificationFunction(bossExpFunction) +
                ",components=" + DescribeComponentFunctionResolutions(
                    targets.Components,
                    STR("ShowDefeatBossBonusExpReward_ToClient")) +
                "; expeditionExp=" +
                    DescribeNotificationFunction(expeditionExpFunction) +
                ",components=" + DescribeComponentFunctionResolutions(
                    targets.Components,
                    STR("ShowExpeditionBonusExpReward_ToClient")) +
                "; unlockHardMode=" +
                    DescribeNotificationFunction(unlockHardModeFunction) +
                ",components=" + DescribeComponentFunctionResolutions(
                    targets.Components,
                    STR("ShowUnlockHardModeUI_ToClient")) + ".");
        }

        const auto positionPolicy = std::string{
            "\"positionPolicy\":{"
                "\"mode\":\"game-defined\","
                "\"configurable\":false,"
                "\"note\":\"Position is fixed by the stock Palworld client layout for this preset.\"},"};
        const auto fixedDurationPolicy = std::string{
            "\"durationPolicy\":{"
                "\"mode\":\"game-defined\","
                "\"configurable\":false,"
                "\"note\":\"Visible duration is fixed by the stock Palworld client and cannot be overridden by the server.\"}"};
        const auto delayedDurationPolicy = std::string{
            "\"durationPolicy\":{"
                "\"mode\":\"game-defined\","
                "\"configurable\":false,"
                "\"note\":\"Visible duration is fixed by the stock client; delaySeconds schedules the trigger and is not display duration.\"}"};

        std::string presetsJson{"["};
        std::size_t presetCount = 0;
        const auto appendPreset = [&](std::string presetJson)
        {
            if (presetCount++ > 0)
            {
                presetsJson += ',';
            }
            presetsJson += std::move(presetJson);
        };

        if (bossReady)
        {
            appendPreset(std::string{"{"} +
                "\"name\":\"boss-defeat-reward\"," +
                "\"displayName\":\"\\u5934\\u76ee\\u8ba8\\u4f10\\u5956\\u52b1\"," +
                "\"description\":\"Native boss-defeat reward panel. Display only: no boss record, reward grant, achievement, or save mutation.\"," +
                "\"function\":\"/Script/Pal.PalNetworkPlayerComponent:ShowBossDefeatRewardUI_ToClient\"," +
                "\"functionFlags\":" +
                    std::to_string(bossFunction->GetFunctionFlags()) + "," +
                "\"propertiesSize\":" +
                    std::to_string(bossFunction->GetPropertiesSize()) + "," +
                positionPolicy + delayedDurationPolicy + "," +
                "\"parameters\":[" +
                    "{\"name\":\"technologyPoint\",\"type\":\"integer\",\"required\":false,\"minimum\":0,\"maximum\":9999,\"default\":1,\"description\":\"Technology-point number rendered by the panel only; it is not granted to the player.\"}," +
                    "{\"name\":\"delaySeconds\",\"type\":\"integer\",\"required\":false,\"minimum\":0,\"maximum\":60,\"default\":0,\"description\":\"Trigger delay in seconds; this does not control visible duration.\"}]" +
                "}");
        }
        if (bossExpReady)
        {
            appendPreset(std::string{"{"} +
                "\"name\":\"boss-bonus-exp\"," +
                "\"displayName\":\"\\u5934\\u76ee\\u8ba8\\u4f10\\u7ecf\\u9a8c\\u5c55\\u793a\"," +
                "\"description\":\"Experimental native boss bonus-EXP display simulation. It changes only the client's displayed accumulated value; it grants no real EXP and writes no server state.\"," +
                "\"function\":\"/Script/Pal.PalNetworkPlayerComponent:ShowDefeatBossBonusExpReward_ToClient\"," +
                "\"functionFlags\":" +
                    std::to_string(bossExpFunction->GetFunctionFlags()) + "," +
                "\"propertiesSize\":" +
                    std::to_string(bossExpFunction->GetPropertiesSize()) + "," +
                positionPolicy + fixedDurationPolicy + "," +
                "\"parameters\":[{\"name\":\"rewardExp\",\"type\":\"integer\",\"required\":true,\"minimum\":0,\"maximum\":10000000,\"description\":\"EXP number rendered by the client-only notification simulation.\"}]" +
                "}");
        }
        if (expeditionExpReady)
        {
            appendPreset(std::string{"{"} +
                "\"name\":\"expedition-bonus-exp\"," +
                "\"displayName\":\"\\u8fdc\\u5f81\\u7ecf\\u9a8c\\u5c55\\u793a\"," +
                "\"description\":\"Experimental native expedition bonus-EXP display simulation. It changes only the client's displayed accumulated value; it grants no real EXP and writes no server state.\"," +
                "\"function\":\"/Script/Pal.PalNetworkPlayerComponent:ShowExpeditionBonusExpReward_ToClient\"," +
                "\"functionFlags\":" +
                    std::to_string(expeditionExpFunction->GetFunctionFlags()) + "," +
                "\"propertiesSize\":" +
                    std::to_string(expeditionExpFunction->GetPropertiesSize()) + "," +
                positionPolicy + fixedDurationPolicy + "," +
                "\"parameters\":[{\"name\":\"rewardExp\",\"type\":\"integer\",\"required\":true,\"minimum\":0,\"maximum\":10000000,\"description\":\"EXP number rendered by the client-only notification simulation.\"}]" +
                "}");
        }
        if (unlockHardModeReady)
        {
            appendPreset(std::string{"{"} +
                "\"name\":\"unlock-hard-mode\"," +
                "\"displayName\":\"\\u89e3\\u9501\\u56f0\\u96be\\u6a21\\u5f0f\\u63d0\\u793a\"," +
                "\"description\":\"Native hard-mode-unlocked presentation. Display only: it does not unlock any mode, achievement, or save flag.\"," +
                "\"function\":\"/Script/Pal.PalNetworkPlayerComponent:ShowUnlockHardModeUI_ToClient\"," +
                "\"functionFlags\":" +
                    std::to_string(unlockHardModeFunction->GetFunctionFlags()) + "," +
                "\"propertiesSize\":" +
                    std::to_string(unlockHardModeFunction->GetPropertiesSize()) + "," +
                positionPolicy + fixedDurationPolicy + "," +
                "\"parameters\":[]" +
                "}");
        }
        presetsJson += ']';

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = std::string{"{"} +
                "\"ready\":true," +
                "\"dispatched\":false," +
                "\"mode\":\"server-native-presets\"," +
                "\"schemaVersions\":[\"1\"]," +
                "\"supportedAudiences\":[\"global\"]," +
                "\"attemptedRecipients\":" +
                    std::to_string(targets.Components.size()) + "," +
                "\"transport\":\"reliable-client-rpc\"," +
                "\"targetType\":\"PalNetworkPlayerComponent\"," +
                "\"componentMappings\":" + targets.MappingJson + "," +
                "\"supportedPresets\":" + presetsJson + "}"
        };
    }

    Contracts::CommandResult PalworldGameAdapter::SendInGameNotification(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        Detail::InGameNotificationPayload payload{};
        const auto parseError = glz::read<glz::opts{
            .error_on_unknown_keys = true,
            .error_on_missing_keys = true}>(payload, command.PayloadJson);
        if (parseError || !IsGuidD(payload.deliveryId) ||
            payload.schemaVersion != "1" ||
            payload.audience.type != "global" ||
            (payload.audience.ids && !payload.audience.ids->empty()) ||
            payload.parameters.str.empty())
        {
            return Failure(
                command,
                "INVALID_NATIVE_NOTIFICATION_PAYLOAD",
                "Notification deliveryId, schemaVersion '1', global audience with no IDs, preset, and parameters object are required; unknown fields are refused.");
        }

        const auto targets = ResolveNotificationTargets();
        if (!targets.IsReady())
        {
            return Failure(
                command,
                targets.ErrorCode,
                targets.ErrorMessage);
        }

        const auto success = [&](UFunction* function)
        {
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = Contracts::CommandState::Succeeded,
                .ObservedRevision = 0,
                .DataJson = std::string{"{"} +
                    "\"dispatched\":true," +
                    "\"deliveryId\":\"" + EscapeJson(payload.deliveryId) + "\"," +
                    "\"preset\":\"" + EscapeJson(payload.preset) + "\"," +
                    "\"attemptedRecipients\":" +
                        std::to_string(targets.Components.size()) + "," +
                    "\"deliveredRecipients\":null," +
                    "\"deliveryAcknowledged\":false," +
                    "\"targetCount\":" +
                        std::to_string(targets.Components.size()) + "," +
                    "\"transport\":\"reliable-client-rpc\"," +
                    "\"function\":\"" +
                        EscapeJson(function
                            ? to_string(function->GetFullName()).substr(9)
                            : std::string{"unresolved"}) + "\"}"
            };
        };

        if (payload.preset == "boss-defeat-reward")
        {
            Detail::BossDefeatNotificationParameters parameters{};
            const auto parameterError = glz::read<glz::opts{
                .error_on_unknown_keys = true,
                .error_on_missing_keys = false}>(
                    parameters,
                    payload.parameters.str);
            const auto technologyPoint = parameters.technologyPoint.value_or(1);
            const auto delaySeconds = parameters.delaySeconds.value_or(0);
            if (parameterError ||
                technologyPoint < 0 || technologyPoint > 9999 ||
                delaySeconds < 0 || delaySeconds > 60)
            {
                return Failure(
                    command,
                    "INVALID_BOSS_DEFEAT_NOTIFICATION_PARAMETERS",
                    "boss-defeat-reward accepts only technologyPoint 0..9999 and delaySeconds 0..60; the native character ID is fixed to the verified-safe None value.");
            }

            auto* function = ResolveFunction(
                nullptr,
                STR("ShowBossDefeatRewardUI_ToClient"),
                STR("/Script/Pal.PalNetworkPlayerComponent:ShowBossDefeatRewardUI_ToClient"));
            BossDefeatNotificationBinding binding{};
            if (!BindBossDefeatNotification(function, binding) ||
                !ComponentsCanInvokeCanonicalFunction(
                    targets.Components,
                    targets.ComponentClass,
                    STR("ShowBossDefeatRewardUI_ToClient"),
                    function))
            {
                return Failure(
                    command,
                    "BOSS_DEFEAT_NOTIFICATION_SIGNATURE_MISMATCH",
                    "The boss-defeat display RPC is not the expected reliable Client function for this game build; dispatch was refused before any player was contacted. canonical=" +
                        DescribeNotificationFunction(function) +
                        ",components=" + DescribeComponentFunctionResolutions(
                            targets.Components,
                            STR("ShowBossDefeatRewardUI_ToClient")) + ".");
            }

            std::vector<std::vector<uint8>> parameterFrames;
            parameterFrames.reserve(targets.Components.size());
            for (std::size_t index = 0;
                 index < targets.Components.size();
                 ++index)
            {
                parameterFrames.emplace_back(
                    std::max<std::size_t>(function->GetPropertiesSize(), 1),
                    0);
                auto& frame = parameterFrames.back();
                function->InitializeStruct(frame.data());
                auto* displayData = binding.DisplayData
                    ->ContainerPtrToValuePtr<uint8>(frame.data());
                *binding.TechnologyPoint
                    ->ContainerPtrToValuePtr<int32>(displayData) =
                        technologyPoint;
                *binding.DefeatCharacterId
                    ->ContainerPtrToValuePtr<FName>(displayData) =
                        FName(STR("None"));
                // AfterTeleport=true depends on an active client teleport flow and is
                // deliberately not part of the public server-only API.
                binding.AfterTeleport->SetPropertyValueInContainer(
                    frame.data(),
                    false);
                *binding.DelayTime->ContainerPtrToValuePtr<int32>(frame.data()) =
                    delaySeconds;
            }

            for (std::size_t index = 0;
                 index < targets.Components.size();
                 ++index)
            {
                targets.Components[index]->ProcessEvent(
                    function,
                    parameterFrames[index].data());
                function->DestroyStruct(parameterFrames[index].data());
            }
            return success(function);
        }

        if (payload.preset == "boss-bonus-exp" ||
            payload.preset == "expedition-bonus-exp")
        {
            Detail::ExpNotificationParameters parameters{};
            const auto parameterError = glz::read<glz::opts{
                .error_on_unknown_keys = true,
                .error_on_missing_keys = false}>(
                    parameters,
                    payload.parameters.str);
            if (parameterError || !parameters.rewardExp ||
                *parameters.rewardExp < 0 ||
                *parameters.rewardExp > 10'000'000)
            {
                return Failure(
                    command,
                    "INVALID_EXP_NOTIFICATION_PARAMETERS",
                    "EXP notification presets require exactly rewardExp between 0 and 10000000.");
            }

            const bool isBoss = payload.preset == "boss-bonus-exp";
            const auto* functionName = isBoss
                ? STR("ShowDefeatBossBonusExpReward_ToClient")
                : STR("ShowExpeditionBonusExpReward_ToClient");
            const auto* functionPath = isBoss
                ? STR("/Script/Pal.PalNetworkPlayerComponent:ShowDefeatBossBonusExpReward_ToClient")
                : STR("/Script/Pal.PalNetworkPlayerComponent:ShowExpeditionBonusExpReward_ToClient");
            const auto expectedFullName = isBoss
                ? "Function /Script/Pal.PalNetworkPlayerComponent:ShowDefeatBossBonusExpReward_ToClient"
                : "Function /Script/Pal.PalNetworkPlayerComponent:ShowExpeditionBonusExpReward_ToClient";
            auto* function = ResolveFunction(nullptr, functionName, functionPath);
            ExpNotificationBinding binding{};
            if (!BindExpNotification(function, expectedFullName, binding) ||
                !ComponentsCanInvokeCanonicalFunction(
                    targets.Components,
                    targets.ComponentClass,
                    functionName,
                    function))
            {
                return Failure(
                    command,
                    "EXP_NOTIFICATION_SIGNATURE_MISMATCH",
                    "The EXP display RPC is not the expected reliable Client function for this game build; dispatch was refused before any player was contacted. canonical=" +
                        DescribeNotificationFunction(function) +
                        ",components=" + DescribeComponentFunctionResolutions(
                            targets.Components,
                            functionName) + ".");
            }

            std::vector<std::vector<uint8>> parameterFrames;
            parameterFrames.reserve(targets.Components.size());
            for (std::size_t index = 0;
                 index < targets.Components.size();
                 ++index)
            {
                parameterFrames.emplace_back(
                    std::max<std::size_t>(function->GetPropertiesSize(), 1),
                    0);
                auto& frame = parameterFrames.back();
                function->InitializeStruct(frame.data());
                *binding.RewardExp->ContainerPtrToValuePtr<int32>(frame.data()) =
                    *parameters.rewardExp;
            }
            for (std::size_t index = 0;
                 index < targets.Components.size();
                 ++index)
            {
                targets.Components[index]->ProcessEvent(
                    function,
                    parameterFrames[index].data());
                function->DestroyStruct(parameterFrames[index].data());
            }
            return success(function);
        }

        if (payload.preset == "unlock-hard-mode")
        {
            std::unordered_map<std::string, glz::raw_json> parameters;
            const auto parameterError = glz::read<glz::opts{
                .error_on_unknown_keys = true,
                .error_on_missing_keys = false}>(
                    parameters,
                    payload.parameters.str);
            if (parameterError || !parameters.empty())
            {
                return Failure(
                    command,
                    "INVALID_UNLOCK_HARD_MODE_NOTIFICATION_PARAMETERS",
                    "unlock-hard-mode accepts an empty parameters object only.");
            }

            auto* function = ResolveFunction(
                nullptr,
                STR("ShowUnlockHardModeUI_ToClient"),
                STR("/Script/Pal.PalNetworkPlayerComponent:ShowUnlockHardModeUI_ToClient"));
            if (!ValidateNoParameterNotification(
                    function,
                    "Function /Script/Pal.PalNetworkPlayerComponent:ShowUnlockHardModeUI_ToClient") ||
                !ComponentsCanInvokeCanonicalFunction(
                    targets.Components,
                    targets.ComponentClass,
                    STR("ShowUnlockHardModeUI_ToClient"),
                    function))
            {
                return Failure(
                    command,
                    "UNLOCK_HARD_MODE_NOTIFICATION_SIGNATURE_MISMATCH",
                    "The hard-mode display RPC is not the expected reliable Client function for this game build; dispatch was refused before any player was contacted. canonical=" +
                        DescribeNotificationFunction(function) +
                        ",components=" + DescribeComponentFunctionResolutions(
                            targets.Components,
                            STR("ShowUnlockHardModeUI_ToClient")) + ".");
            }

            // The zero-parameter signature was validated on every live player component
            // before the first display-only RPC is invoked.
            for (auto* component : targets.Components)
            {
                component->ProcessEvent(function, nullptr);
            }
            return success(function);
        }

        return Failure(
            command,
            "NATIVE_NOTIFICATION_PRESET_NOT_SUPPORTED",
            "Only a preset advertised by ui.notifications.probe may be dispatched.");
    }

    Contracts::CommandResult PalworldGameAdapter::SendTopBannerAnnouncement(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        Detail::OverlayAnnouncementPayload payload{};
        const auto parseError = glz::read<glz::opts{
            .error_on_unknown_keys = false,
            .error_on_missing_keys = false}>(payload, command.PayloadJson);
        if (parseError || payload.message.empty() ||
            payload.message.size() > 4096 ||
            payload.message.find('\0') != std::string::npos)
        {
            return Failure(
                command,
                "INVALID_TOP_BANNER_ANNOUNCEMENT_PAYLOAD",
                "Top-banner announcement message must contain between 1 and 4096 UTF-8 bytes.");
        }

        auto* gameStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalGameStateInGame"));
        if (!gameStateClass)
        {
            return Failure(
                command,
                "TOP_BANNER_GAME_STATE_CLASS_UNAVAILABLE",
                "PalGameStateInGame is unavailable for this game build.");
        }

        std::vector<UObject*> gameStateObjects;
        UObjectGlobals::FindAllOf(STR("PalGameStateInGame"), gameStateObjects);
        std::vector<UObject*> authoritativeGameStates;
        authoritativeGameStates.reserve(gameStateObjects.size());
        for (auto* object : gameStateObjects)
        {
            if (IsAuthoritativeLiveGameState(object, gameStateClass))
            {
                authoritativeGameStates.push_back(object);
            }
        }
        if (authoritativeGameStates.empty())
        {
            return Failure(
                command,
                "TOP_BANNER_GAME_STATE_UNAVAILABLE",
                "No authoritative live PalGameStateInGame object is available.");
        }
        if (authoritativeGameStates.size() != 1)
        {
            return Failure(
                command,
                "TOP_BANNER_GAME_STATE_AMBIGUOUS",
                "More than one authoritative live PalGameStateInGame object was found; dispatch was refused.");
        }

        auto* gameState = authoritativeGameStates.front();
        const auto attemptedRecipients = CountLivePalPlayerControllers(
            gameState->GetWorld());
        auto* function = ResolveFunction(
            gameState,
            STR("BroadcastServerNotice"),
            STR("/Script/Pal.PalGameStateInGame:BroadcastServerNotice"));
        auto* messageProperty = ValidateServerNoticeFunction(function);
        if (!messageProperty)
        {
            return Failure(
                command,
                "TOP_BANNER_BROADCAST_SIGNATURE_MISMATCH",
                "BroadcastServerNotice is not the expected reliable NetMulticast FString function.");
        }

        std::vector<uint8> parameters(
            std::max<std::size_t>(function->GetPropertiesSize(), 1),
            0);
        function->InitializeStruct(parameters.data());
        const auto wideMessage = to_wstring(payload.message);
        *messageProperty->ContainerPtrToValuePtr<FString>(parameters.data()) =
            FString(wideMessage.c_str());
        gameState->ProcessEvent(function, parameters.data());
        function->DestroyStruct(parameters.data());

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = std::string{"{"} +
                "\"dispatched\":true," +
                "\"attemptedRecipients\":" +
                    std::to_string(attemptedRecipients) + "," +
                "\"deliveredRecipients\":null," +
                "\"deliveryAcknowledged\":false," +
                "\"targetCount\":1," +
                "\"transport\":\"reliable-net-multicast\"," +
                "\"function\":\"/Script/Pal.PalGameStateInGame:BroadcastServerNotice\"}"
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ProbeTopBannerAnnouncement(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        auto* gameStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalGameStateInGame"));
        if (!gameStateClass)
        {
            return Failure(
                command,
                "TOP_BANNER_GAME_STATE_CLASS_UNAVAILABLE",
                "PalGameStateInGame is unavailable for this game build.");
        }

        std::vector<UObject*> gameStateObjects;
        UObjectGlobals::FindAllOf(STR("PalGameStateInGame"), gameStateObjects);
        std::vector<UObject*> authoritativeGameStates;
        authoritativeGameStates.reserve(gameStateObjects.size());
        for (auto* object : gameStateObjects)
        {
            if (IsAuthoritativeLiveGameState(object, gameStateClass))
            {
                authoritativeGameStates.push_back(object);
            }
        }
        if (authoritativeGameStates.size() != 1)
        {
            return Failure(
                command,
                authoritativeGameStates.empty()
                    ? "TOP_BANNER_GAME_STATE_UNAVAILABLE"
                    : "TOP_BANNER_GAME_STATE_AMBIGUOUS",
                authoritativeGameStates.empty()
                    ? "No authoritative live PalGameStateInGame object is available."
                    : "More than one authoritative live PalGameStateInGame object was found; the probe was refused.");
        }

        auto* gameState = authoritativeGameStates.front();
        auto* function = ResolveFunction(
            gameState,
            STR("BroadcastServerNotice"),
            STR("/Script/Pal.PalGameStateInGame:BroadcastServerNotice"));
        if (!ValidateServerNoticeFunction(function))
        {
            const auto observedName = function
                ? to_string(function->GetFullName())
                : std::string{"unresolved"};
            return Failure(
                command,
                "TOP_BANNER_BROADCAST_SIGNATURE_MISMATCH",
                "BroadcastServerNotice signature probe failed; observed function: " +
                    observedName + ".");
        }

        const auto attemptedRecipients = CountLivePalPlayerControllers(
            gameState->GetWorld());
        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = std::string{"{"} +
                "\"ready\":true," +
                "\"dispatched\":false," +
                "\"attemptedRecipients\":" +
                    std::to_string(attemptedRecipients) + "," +
                "\"propertiesSize\":" +
                    std::to_string(function->GetPropertiesSize()) + "," +
                "\"functionFlags\":" +
                    std::to_string(function->GetFunctionFlags()) + "," +
                "\"function\":\"/Script/Pal.PalGameStateInGame:BroadcastServerNotice\"}"
        };
    }

    Contracts::CommandResult PalworldGameAdapter::SendOverlayAnnouncement(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        Detail::OverlayAnnouncementPayload payload{};
        const auto parseError = glz::read<glz::opts{
            .error_on_unknown_keys = false,
            .error_on_missing_keys = false}>(payload, command.PayloadJson);
        const auto lifetimeSeconds = payload.lifetimeSeconds.value_or(8.0);
        if (parseError || payload.message.empty() ||
            payload.message.size() > 4096 ||
            payload.message.find('\0') != std::string::npos ||
            !std::isfinite(lifetimeSeconds) ||
            lifetimeSeconds < 0.5 || lifetimeSeconds > 60.0)
        {
            return Failure(
                command,
                "INVALID_OVERLAY_ANNOUNCEMENT_PAYLOAD",
                "Overlay message must contain 1 to 4096 UTF-8 bytes and use a lifetime between 0.5 and 60 seconds.");
        }

        auto* gameStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalGameStateInGame"));
        auto* playerControllerClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalPlayerController"));
        if (!gameStateClass || !playerControllerClass)
        {
            return Failure(
                command,
                "OVERLAY_TARGET_CLASS_UNAVAILABLE",
                "The Palworld GameState or player-controller class is unavailable.");
        }

        std::vector<UObject*> gameStateObjects;
        UObjectGlobals::FindAllOf(STR("PalGameStateInGame"), gameStateObjects);
        std::vector<UObject*> authoritativeGameStates;
        for (auto* object : gameStateObjects)
        {
            if (IsAuthoritativeLiveGameState(object, gameStateClass))
            {
                authoritativeGameStates.push_back(object);
            }
        }
        if (authoritativeGameStates.size() != 1)
        {
            return Failure(
                command,
                authoritativeGameStates.empty()
                    ? "OVERLAY_GAME_STATE_UNAVAILABLE"
                    : "OVERLAY_GAME_STATE_AMBIGUOUS",
                authoritativeGameStates.empty()
                    ? "No authoritative live PalGameStateInGame object is available."
                    : "More than one authoritative live PalGameStateInGame object was found; dispatch was refused.");
        }

        auto* function = ResolveFunction(
            nullptr,
            STR("ClientMessage"),
            STR("/Script/Engine.PlayerController:ClientMessage"));
        FStrProperty* messageProperty = nullptr;
        FNameProperty* typeProperty = nullptr;
        FFloatProperty* lifetimeProperty = nullptr;
        if (!ValidateClientMessageFunction(
                function,
                messageProperty,
                typeProperty,
                lifetimeProperty))
        {
            return Failure(
                command,
                "OVERLAY_CLIENT_MESSAGE_SIGNATURE_MISMATCH",
                "PlayerController.ClientMessage is not the expected reliable Client FString/FName/float RPC.");
        }

        const auto controllers = FindLivePalPlayerControllers(
            authoritativeGameStates.front()->GetWorld(),
            playerControllerClass);
        const auto wideMessage = to_wstring(payload.message);
        for (auto* controller : controllers)
        {
            std::vector<uint8> parameters(
                std::max<std::size_t>(function->GetPropertiesSize(), 1),
                0);
            function->InitializeStruct(parameters.data());
            *messageProperty->ContainerPtrToValuePtr<FString>(parameters.data()) =
                FString(wideMessage.c_str());
            *typeProperty->ContainerPtrToValuePtr<FName>(parameters.data()) =
                FName(STR("Event"));
            *lifetimeProperty->ContainerPtrToValuePtr<float>(parameters.data()) =
                static_cast<float>(lifetimeSeconds);
            controller->ProcessEvent(function, parameters.data());
            function->DestroyStruct(parameters.data());
        }

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = std::string{"{"} +
                "\"dispatched\":true," +
                "\"attemptedRecipients\":" +
                    std::to_string(controllers.size()) + "," +
                "\"deliveredRecipients\":null," +
                "\"deliveryAcknowledged\":false," +
                "\"targetCount\":" + std::to_string(controllers.size()) + "," +
                "\"transport\":\"reliable-client-rpc\"," +
                "\"function\":\"/Script/Engine.PlayerController:ClientMessage\"}"
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ProbeOverlayAnnouncement(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        auto* gameStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalGameStateInGame"));
        auto* playerControllerClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalPlayerController"));
        if (!gameStateClass || !playerControllerClass)
        {
            return Failure(
                command,
                "OVERLAY_TARGET_CLASS_UNAVAILABLE",
                "The Palworld GameState or player-controller class is unavailable.");
        }

        std::vector<UObject*> gameStateObjects;
        UObjectGlobals::FindAllOf(STR("PalGameStateInGame"), gameStateObjects);
        std::vector<UObject*> authoritativeGameStates;
        for (auto* object : gameStateObjects)
        {
            if (IsAuthoritativeLiveGameState(object, gameStateClass))
            {
                authoritativeGameStates.push_back(object);
            }
        }
        if (authoritativeGameStates.size() != 1)
        {
            return Failure(
                command,
                authoritativeGameStates.empty()
                    ? "OVERLAY_GAME_STATE_UNAVAILABLE"
                    : "OVERLAY_GAME_STATE_AMBIGUOUS",
                authoritativeGameStates.empty()
                    ? "No authoritative live PalGameStateInGame object is available."
                    : "More than one authoritative live PalGameStateInGame object was found; the probe was refused.");
        }

        auto* function = ResolveFunction(
            nullptr,
            STR("ClientMessage"),
            STR("/Script/Engine.PlayerController:ClientMessage"));
        FStrProperty* messageProperty = nullptr;
        FNameProperty* typeProperty = nullptr;
        FFloatProperty* lifetimeProperty = nullptr;
        if (!ValidateClientMessageFunction(
                function,
                messageProperty,
                typeProperty,
                lifetimeProperty))
        {
            return Failure(
                command,
                "OVERLAY_CLIENT_MESSAGE_SIGNATURE_MISMATCH",
                "PlayerController.ClientMessage signature probe failed.");
        }

        const auto controllers = FindLivePalPlayerControllers(
            authoritativeGameStates.front()->GetWorld(),
            playerControllerClass);
        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = std::string{"{"} +
                "\"ready\":true," +
                "\"dispatched\":false," +
                "\"attemptedRecipients\":" +
                    std::to_string(controllers.size()) + "," +
                "\"propertiesSize\":" +
                    std::to_string(function->GetPropertiesSize()) + "," +
                "\"functionFlags\":" +
                    std::to_string(function->GetFunctionFlags()) + "," +
                "\"transport\":\"reliable-client-rpc\"," +
                "\"function\":\"/Script/Engine.PlayerController:ClientMessage\"}"
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ProbePlayers(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        auto* playerStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalPlayerState"));

        std::vector<UObject*> playerStates;
        UObjectGlobals::FindAllOf(STR("PalPlayerState"), playerStates);
        const auto identityProperties = ResolveIdentityProperties(playerStateClass);
        const auto uidStructName = identityProperties.PlayerUId &&
                identityProperties.PlayerUId->GetStruct()
            ? to_string(identityProperties.PlayerUId->GetStruct()->GetName())
            : std::string{"unknown"};

        std::string objectsJson{"["};
        const auto returnedCount = std::min(playerStates.size(), MaxReturnedObjects);
        for (std::size_t index = 0; index < returnedCount; ++index)
        {
            auto* object = playerStates[index];
            if (index > 0)
            {
                objectsJson += ',';
            }

            const auto className = object->GetClassPrivate()
                ? to_string(object->GetClassPrivate()->GetName())
                : std::string{"unknown"};
            const auto accountName = ReadString(identityProperties.AccountName, object);
            const auto playerName = ReadString(identityProperties.PlayerName, object);
            const auto playerId = ReadInt(identityProperties.PlayerId, object);
            const auto playerUId = ReadPlayerUId(identityProperties.PlayerUId, object);
            objectsJson += std::string{"{"} +
                "\"ephemeralObjectId\":" + std::to_string(object->GetInternalIndex()) + "," +
                "\"objectName\":\"" + EscapeJson(to_string(object->GetName())) + "\"," +
                "\"fullName\":\"" + EscapeJson(to_string(object->GetFullName())) + "\"," +
                "\"className\":\"" + EscapeJson(className) + "\"," +
                "\"identity\":{" +
                    "\"playerUId\":" + JsonString(playerUId) + "," +
                    "\"accountName\":" + JsonString(accountName) + "," +
                    "\"displayName\":" + JsonString(playerName) + "," +
                    "\"playerId\":" + (playerId ? std::to_string(*playerId) : "null") + "," +
                    "\"level\":null," +
                    "\"levelSource\":\"official-rest\"}}";
        }
        objectsJson += ']';

        const bool classFound = playerStateClass != nullptr;
        const auto data = std::string{"{"} +
            "\"observedAt\":\"" + UtcNow() + "\"," +
            "\"executionThread\":\"unreal-engine-tick\"," +
            "\"targetClass\":\"/Script/Pal.PalPlayerState\"," +
            "\"classFound\":" + (classFound ? "true" : "false") + "," +
            "\"identityMapping\":{" +
                "\"ready\":" + (identityProperties.IsReady() ? "true" : "false") + "," +
                "\"playerUId\":\"PlayerUId\"," +
                "\"playerUIdType\":\"" + EscapeJson(uidStructName) + "\"," +
                "\"accountName\":\"AccountName\"," +
                "\"displayName\":\"PlayerNamePrivate\"," +
                "\"playerId\":\"PlayerId\"," +
                "\"levelSource\":\"official-rest\"}," +
            "\"objectCount\":" + std::to_string(playerStates.size()) + "," +
            "\"truncated\":" + (playerStates.size() > returnedCount ? "true" : "false") + "," +
            "\"objects\":" + objectsJson + "}";

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = data
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ProbePlayerProgression(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        const auto mapping = ResolvePlayerProgressionProperties();
        if (!mapping.IsReady())
        {
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = Contracts::CommandState::Succeeded,
                .ObservedRevision = 0,
                .DataJson = std::string{"{"} +
                    "\"observedAt\":\"" + UtcNow() + "\"," +
                    "\"executionThread\":\"unreal-engine-tick\"," +
                    "\"mappingReady\":false," +
                    "\"parameterObjectCount\":0," +
                    "\"playerCount\":0," +
                    "\"players\":[]}"
            };
        }

        auto* playerStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalPlayerState"));
        const auto identity = ResolveIdentityProperties(playerStateClass);
        std::vector<UObject*> playerStateObjects;
        UObjectGlobals::FindAllOf(STR("PalPlayerState"), playerStateObjects);
        std::unordered_map<std::string, UObject*> playerStatesByUid;
        std::unordered_map<std::string, UObject*> playerStatesByInstanceId;
        std::unordered_map<std::string, std::string> playerUidsByInstanceId;
        for (auto* playerState : playerStateObjects)
        {
            const auto playerUid = ReadPlayerUId(identity.PlayerUId, playerState);
            if (playerUid)
            {
                playerStatesByUid.emplace(NormalizeIdentifier(*playerUid), playerState);
                auto* handleIdMemory = mapping.PlayerStateIndividualHandleId
                    ->ContainerPtrToValuePtr<uint8>(playerState);
                const auto instanceId = ReadNestedGuid(
                    mapping.InstanceId, handleIdMemory);
                if (instanceId)
                {
                    const auto normalizedInstanceId = NormalizeIdentifier(
                        GuidToString(*instanceId));
                    playerStatesByInstanceId.emplace(
                        normalizedInstanceId, playerState);
                    playerUidsByInstanceId.emplace(
                        normalizedInstanceId, *playerUid);
                }
            }
        }

        std::vector<UObject*> technologyObjects;
        UObjectGlobals::FindAllOf(STR("PalTechnologyData"), technologyObjects);
        std::unordered_map<std::string, UObject*> technologyByUid;
        for (auto* technology : technologyObjects)
        {
            const auto playerUid = ReadTopLevelGuid(
                mapping.TechnologyOwnerPlayerUId, technology);
            if (playerUid)
            {
                technologyByUid.emplace(
                    NormalizeIdentifier(GuidToString(*playerUid)), technology);
            }
        }

        std::vector<UObject*> parameterObjects;
        UObjectGlobals::FindAllOf(
            STR("PalIndividualCharacterParameter"), parameterObjects);
        std::string playersJson{"["};
        std::size_t playerCount = 0;
        std::uint64_t maxRevision = 0;
        for (auto* parameter : parameterObjects)
        {
            if (!parameter || !parameter->IsA(mapping.ParameterClass))
            {
                continue;
            }
            auto* saveMemory = mapping.SaveParameter
                ->ContainerPtrToValuePtr<uint8>(parameter);
            if (!saveMemory)
            {
                continue;
            }
            auto* individualIdMemory = mapping.IndividualId
                ->ContainerPtrToValuePtr<uint8>(parameter);
            const auto instanceId = ReadNestedGuid(
                mapping.InstanceId, individualIdMemory);
            const auto normalizedInstanceId = instanceId
                ? NormalizeIdentifier(GuidToString(*instanceId))
                : std::string{};
            auto linkedUid = playerUidsByInstanceId.find(normalizedInstanceId);
            const bool linkedToOnlinePlayer =
                linkedUid != playerUidsByInstanceId.end();
            if (!linkedToOnlinePlayer &&
                !mapping.IsPlayer->GetPropertyValueInContainer(saveMemory))
            {
                continue;
            }
            const auto savedOwnerUid = ReadNestedGuid(
                mapping.OwnerPlayerUId, saveMemory);
            if (!linkedToOnlinePlayer && !savedOwnerUid)
            {
                continue;
            }
            const auto playerUidText = linkedToOnlinePlayer
                ? linkedUid->second
                : GuidToString(*savedOwnerUid);
            const auto normalizedUid = NormalizeIdentifier(playerUidText);
            const auto level = *mapping.Level
                ->ContainerPtrToValuePtr<uint8>(saveMemory);
            const auto experience = *mapping.Exp
                ->ContainerPtrToValuePtr<int64>(saveMemory);
            const auto unusedStatusPoints = *mapping.UnusedStatusPoint
                ->ContainerPtrToValuePtr<uint16>(saveMemory);

            std::string statusPointsJson{"["};
            std::string statusCanonical;
            auto* statusArrayMemory = mapping.GotStatusPointList
                ->ContainerPtrToValuePtr<void>(saveMemory);
            FScriptArrayHelper statusHelper(
                mapping.GotStatusPointList, statusArrayMemory);
            const auto statusPointCount = std::min<std::size_t>(
                static_cast<std::size_t>(std::max(statusHelper.Num(), 0)), 64);
            for (std::size_t index = 0; index < statusPointCount; ++index)
            {
                auto* entry = statusHelper.GetRawPtr(static_cast<int32>(index));
                const auto* nameValue = mapping.StatusName
                    ->ContainerPtrToValuePtr<FName>(entry);
                const auto name = nameValue
                    ? to_string(nameValue->ToString())
                    : std::string{"None"};
                const auto point = *mapping.StatusPoint
                    ->ContainerPtrToValuePtr<int32>(entry);
                if (index > 0)
                {
                    statusPointsJson += ',';
                }
                statusPointsJson += std::string{"{"} +
                    "\"id\":\"" + EscapeJson(CanonicalPlayerStatusId(name)) + "\"," +
                    "\"nativeName\":\"" + EscapeJson(name) + "\"," +
                    "\"rank\":" + std::to_string(point) + "}";
                statusCanonical += name + ":" + std::to_string(point) + ",";
            }
            statusPointsJson += ']';

            std::optional<std::int32_t> technologyPoints;
            std::optional<std::int32_t> ancientTechnologyPoints;
            if (const auto iterator = technologyByUid.find(normalizedUid);
                iterator != technologyByUid.end())
            {
                technologyPoints = *mapping.TechnologyPoint
                    ->ContainerPtrToValuePtr<int32>(iterator->second);
                ancientTechnologyPoints = *mapping.BossTechnologyPoint
                    ->ContainerPtrToValuePtr<int32>(iterator->second);
            }

            std::optional<std::string> displayName;
            std::optional<std::string> accountName;
            std::optional<std::int32_t> playerId;
            bool online = false;
            if (const auto linkedState = playerStatesByInstanceId.find(
                    normalizedInstanceId);
                linkedState != playerStatesByInstanceId.end())
            {
                online = true;
                displayName = ReadString(identity.PlayerName, linkedState->second);
                accountName = ReadString(identity.AccountName, linkedState->second);
                playerId = ReadInt(identity.PlayerId, linkedState->second);
            }
            else if (const auto iterator = playerStatesByUid.find(normalizedUid);
                iterator != playerStatesByUid.end())
            {
                online = true;
                displayName = ReadString(identity.PlayerName, iterator->second);
                accountName = ReadString(identity.AccountName, iterator->second);
                playerId = ReadInt(identity.PlayerId, iterator->second);
            }
            const std::optional<std::int64_t> experienceToNext{};
            const auto canonical = playerUidText + "|" +
                (instanceId ? GuidToString(*instanceId) : std::string{"none"}) + "|" +
                std::to_string(level) + "|" + std::to_string(experience) + "|" +
                std::to_string(unusedStatusPoints) + "|" + statusCanonical + "|" +
                (technologyPoints ? std::to_string(*technologyPoints) : "none") + "|" +
                (ancientTechnologyPoints
                    ? std::to_string(*ancientTechnologyPoints)
                    : "none");
            const auto revision = StableRevision(canonical);
            maxRevision = std::max(maxRevision, revision);

            if (playerCount > 0)
            {
                playersJson += ',';
            }
            playersJson += std::string{"{"} +
                "\"playerUId\":\"" + EscapeJson(playerUidText) + "\"," +
                "\"instanceId\":" + (instanceId
                    ? "\"" + EscapeJson(GuidToString(*instanceId)) + "\""
                    : std::string{"null"}) + "," +
                "\"displayName\":" + JsonString(displayName) + "," +
                "\"accountName\":" + JsonString(accountName) + "," +
                "\"playerId\":" + (playerId
                    ? std::to_string(*playerId)
                    : std::string{"null"}) + "," +
                "\"online\":" + (online ? "true" : "false") + "," +
                "\"loaded\":true," +
                "\"level\":" + std::to_string(level) + "," +
                "\"totalExperience\":" + std::to_string(experience) + "," +
                "\"experienceToNextLevel\":" + (experienceToNext
                    ? std::to_string(*experienceToNext)
                    : std::string{"null"}) + "," +
                "\"unusedStatusPoints\":" + std::to_string(unusedStatusPoints) + "," +
                "\"statusPoints\":" + statusPointsJson + "," +
                "\"technologyPoints\":" + (technologyPoints
                    ? std::to_string(*technologyPoints)
                    : std::string{"null"}) + "," +
                "\"ancientTechnologyPoints\":" + (ancientTechnologyPoints
                    ? std::to_string(*ancientTechnologyPoints)
                    : std::string{"null"}) + "," +
                "\"revision\":\"" + std::to_string(revision) + "\"}";
            ++playerCount;
        }
        playersJson += ']';

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = maxRevision,
            .DataJson = std::string{"{"} +
                "\"observedAt\":\"" + UtcNow() + "\"," +
                "\"executionThread\":\"unreal-engine-tick\"," +
                "\"mappingReady\":true," +
                "\"parameterObjectCount\":" + std::to_string(parameterObjects.size()) + "," +
                "\"technologyObjectCount\":" + std::to_string(technologyObjects.size()) + "," +
                "\"playerCount\":" + std::to_string(playerCount) + "," +
                "\"players\":" + playersJson + "}"
        };
    }

    Contracts::CommandResult PalworldGameAdapter::MutatePlayerProgression(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        Detail::PlayerProgressionMutationPayload payload{};
        const auto parseError = glz::read<glz::opts{
            .error_on_unknown_keys = true,
            .error_on_missing_keys = false}>(payload, command.PayloadJson);
        const bool allocationComplete = payload.allocateStatusId.has_value() ==
            payload.allocateStatusPoints.has_value();
        const auto operationCount =
            static_cast<int>(payload.addExperience.has_value()) +
            static_cast<int>(payload.targetLevel.has_value()) +
            static_cast<int>(payload.grantStatusPoints.has_value()) +
            static_cast<int>(payload.grantTechnologyPoints.has_value()) +
            static_cast<int>(payload.grantAncientTechnologyPoints.has_value()) +
            static_cast<int>(payload.allocateStatusId.has_value() && allocationComplete);
        if (parseError || payload.ownerPlayerId.empty() || !allocationComplete ||
            operationCount != 1)
        {
            return Failure(
                command,
                "INVALID_PLAYER_PROGRESSION_MUTATION",
                "Exactly one complete player progression operation is required.");
        }
        const auto positiveAndBounded = [](const std::optional<std::int32_t>& value,
                                            std::int32_t maximum)
        {
            return !value || (*value > 0 && *value <= maximum);
        };
        if (!positiveAndBounded(payload.addExperience, 10'000'000) ||
            !positiveAndBounded(payload.grantStatusPoints, 1'000) ||
            !positiveAndBounded(payload.grantTechnologyPoints, 10'000) ||
            !positiveAndBounded(payload.grantAncientTechnologyPoints, 10'000) ||
            !positiveAndBounded(payload.allocateStatusPoints, 100) ||
            (payload.targetLevel && (*payload.targetLevel < 2 || *payload.targetLevel > 100)))
        {
            return Failure(
                command,
                "PLAYER_PROGRESSION_VALUE_OUT_OF_RANGE",
                "Player progression values exceed the guarded per-operation limits.");
        }
        constexpr std::array<std::string_view, 6> WritableStatusIds{
            "StatusName_AddMaxHP",
            "StatusName_AddMaxSP",
            "StatusName_AddPower",
            "StatusName_AddMaxInventoryWeight",
            "StatusName_AddWorkSpeed",
            "StatusName_AddCaptureLevel"};
        if (payload.allocateStatusId &&
            std::ranges::find(WritableStatusIds, *payload.allocateStatusId) ==
                WritableStatusIds.end())
        {
            return Failure(
                command,
                "PLAYER_STATUS_ID_NOT_SUPPORTED",
                "The requested player status ID is not in the validated allowlist.");
        }

        const auto mapping = ResolvePlayerProgressionProperties();
        if (!mapping.IsReady())
        {
            return Failure(
                command,
                "PLAYER_PROGRESSION_MAPPING_NOT_READY",
                "Player progression reflection mapping is not ready for this game build.");
        }
        const auto normalizedOwnerId = NormalizeIdentifier(payload.ownerPlayerId);
        auto* playerStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalPlayerState"));
        const auto identity = ResolveIdentityProperties(playerStateClass);
        std::vector<UObject*> playerStates;
        UObjectGlobals::FindAllOf(STR("PalPlayerState"), playerStates);
        UObject* selectedPlayerState = nullptr;
        std::optional<FGuid> selectedPlayerInstanceId;
        std::string selectedUid;
        for (auto* playerState : playerStates)
        {
            const auto ownerUid = ReadPlayerUId(identity.PlayerUId, playerState);
            if (!ownerUid)
            {
                continue;
            }
            const auto normalizedUid = NormalizeIdentifier(*ownerUid);
            if (normalizedUid != normalizedOwnerId &&
                !(normalizedOwnerId.size() == 8 &&
                  normalizedUid.starts_with(normalizedOwnerId)))
            {
                continue;
            }
            auto* handleIdMemory = mapping.PlayerStateIndividualHandleId
                ->ContainerPtrToValuePtr<uint8>(playerState);
            const auto instanceId = ReadNestedGuid(
                mapping.InstanceId, handleIdMemory);
            if (!instanceId)
            {
                continue;
            }
            selectedPlayerState = playerState;
            selectedPlayerInstanceId = instanceId;
            selectedUid = *ownerUid;
            break;
        }
        if (!selectedPlayerState || !selectedPlayerInstanceId)
        {
            return Failure(
                command,
                "PLAYER_MUST_BE_ONLINE",
                "Player progression writes require an online player state with a valid individual handle ID.");
        }
        std::vector<UObject*> parameterObjects;
        UObjectGlobals::FindAllOf(
            STR("PalIndividualCharacterParameter"), parameterObjects);
        UObject* selectedParameter = nullptr;
        for (auto* parameter : parameterObjects)
        {
            if (!parameter || !parameter->IsA(mapping.ParameterClass))
            {
                continue;
            }
            auto* saveMemory = mapping.SaveParameter
                ->ContainerPtrToValuePtr<uint8>(parameter);
            if (!saveMemory)
            {
                continue;
            }
            auto* individualIdMemory = mapping.IndividualId
                ->ContainerPtrToValuePtr<uint8>(parameter);
            const auto instanceId = ReadNestedGuid(
                mapping.InstanceId, individualIdMemory);
            if (instanceId && NormalizeIdentifier(GuidToString(*instanceId)) ==
                    NormalizeIdentifier(GuidToString(*selectedPlayerInstanceId)))
            {
                if (selectedParameter)
                {
                    return Failure(
                        command,
                        "PLAYER_PROGRESSION_OWNER_AMBIGUOUS",
                        "More than one loaded player progression object matched the owner ID.");
                }
                selectedParameter = parameter;
            }
        }
        if (!selectedParameter)
        {
            return Failure(
                command,
                "PLAYER_PROGRESSION_NOT_LOADED",
                "The requested player progression object is not loaded.");
        }

        UObject* selectedTechnology = nullptr;
        std::vector<UObject*> technologyObjects;
        UObjectGlobals::FindAllOf(STR("PalTechnologyData"), technologyObjects);
        for (auto* technology : technologyObjects)
        {
            const auto ownerUid = ReadTopLevelGuid(
                mapping.TechnologyOwnerPlayerUId, technology);
            if (ownerUid && NormalizeIdentifier(GuidToString(*ownerUid)) ==
                    NormalizeIdentifier(selectedUid))
            {
                selectedTechnology = technology;
                break;
            }
        }

        auto* playerControllerClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr, nullptr, STR("/Script/Pal.PalPlayerController"));
        if (!playerControllerClass)
        {
            return Failure(
                command,
                "PLAYER_MUST_BE_ONLINE",
                "Player progression writes require an online authoritative player controller.");
        }
        auto* playerStateProperty = FindTypedProperty<FObjectProperty>(
            playerControllerClass, STR("PlayerState"));
        auto* cheatManagerProperty = FindTypedProperty<FObjectProperty>(
            playerControllerClass, STR("CheatManager"));
        UObject* selectedController = nullptr;
        const auto controllers = FindLivePalPlayerControllers(
            selectedPlayerState->GetWorld(), playerControllerClass);
        for (auto* controller : controllers)
        {
            auto* state = playerStateProperty
                ? *playerStateProperty->ContainerPtrToValuePtr<UObject*>(controller)
                : nullptr;
            if (state == selectedPlayerState)
            {
                selectedController = controller;
                break;
            }
        }
        if (!selectedController)
        {
            return Failure(
                command,
                "PLAYER_CONTROLLER_NOT_RESOLVED",
                "The authoritative player controller could not be matched to the requested UID.");
        }
        auto* cheatManager = cheatManagerProperty
            ? *cheatManagerProperty->ContainerPtrToValuePtr<UObject*>(selectedController)
            : nullptr;
        if (!cheatManager && cheatManagerProperty)
        {
            auto* cheatManagerClass = UObjectGlobals::StaticFindObject<UClass*>(
                nullptr, nullptr, STR("/Script/Pal.PalCheatManager"));
            if (cheatManagerClass)
            {
                FStaticConstructObjectParameters construction{
                    cheatManagerClass, selectedController};
                auto* createdCheatManager =
                    UObjectGlobals::StaticConstructObject(construction);
                if (createdCheatManager &&
                    createdCheatManager->IsA(cheatManagerClass))
                {
                    *cheatManagerProperty
                        ->ContainerPtrToValuePtr<UObject*>(selectedController) =
                        createdCheatManager;
                    cheatManager = createdCheatManager;
                }
            }
        }

        auto* saveMemory = mapping.SaveParameter
            ->ContainerPtrToValuePtr<uint8>(selectedParameter);
        const auto beforeLevel = static_cast<std::int32_t>(
            *mapping.Level->ContainerPtrToValuePtr<uint8>(saveMemory));
        const auto beforeExperience = *mapping.Exp
            ->ContainerPtrToValuePtr<int64>(saveMemory);
        const auto beforeUnused = static_cast<std::int32_t>(
            *mapping.UnusedStatusPoint->ContainerPtrToValuePtr<uint16>(saveMemory));
        const auto beforeTechnology = selectedTechnology
            ? *mapping.TechnologyPoint
                ->ContainerPtrToValuePtr<int32>(selectedTechnology)
            : 0;
        const auto beforeAncientTechnology = selectedTechnology
            ? *mapping.BossTechnologyPoint
                ->ContainerPtrToValuePtr<int32>(selectedTechnology)
            : 0;
        const auto beforeStatusRank = payload.allocateStatusId
            ? ReadAllocatedStatusPoint(mapping, saveMemory, *payload.allocateStatusId)
            : 0;
        const auto beforeRevision = PlayerProgressionRevision(
            mapping, selectedParameter, selectedTechnology, selectedUid);
        if (command.ExpectedRevision == 0 ||
            command.ExpectedRevision != beforeRevision)
        {
            return Failure(
                command,
                "PLAYER_PROGRESSION_REVISION_CONFLICT",
                "Player progression changed after it was read; refresh before retrying.");
        }

        std::string operation;
        std::string nativeFunction;
        std::int32_t operationValue = 0;
        std::int64_t requestedExperience = beforeExperience;
        if (payload.addExperience)
        {
            operation = "addExperience";
            nativeFunction = "PalCheatManager.AddPlayerExp";
            operationValue = *payload.addExperience;
            requestedExperience += operationValue;
        }
        else if (payload.targetLevel)
        {
            return Failure(
                command,
                "TARGET_LEVEL_CALCULATOR_NOT_VERIFIED",
                "The PalExpDatabase static calculator is disabled because this UE4SS build cannot invoke its class default object safely. Use guarded addExperience instead.");
        }
        else if (payload.grantStatusPoints)
        {
            operation = "grantStatusPoints";
            nativeFunction = "PalCheatManager.AddExStatusPoint";
            operationValue = *payload.grantStatusPoints;
        }
        else if (payload.grantTechnologyPoints)
        {
            operation = "grantTechnologyPoints";
            nativeFunction = "PalCheatManager.AddTechnologyPoints";
            operationValue = *payload.grantTechnologyPoints;
        }
        else if (payload.grantAncientTechnologyPoints)
        {
            operation = "grantAncientTechnologyPoints";
            nativeFunction = "PalCheatManager.AddBossTechnologyPoints";
            operationValue = *payload.grantAncientTechnologyPoints;
        }
        else
        {
            operation = "allocateStatusPoints";
            nativeFunction = "PalPlayerController.Debug_SetStatusPoint_ToServer";
            operationValue = *payload.allocateStatusPoints;
            if (operationValue > beforeUnused)
            {
                return Failure(
                    command,
                    "INSUFFICIENT_UNUSED_STATUS_POINTS",
                    "The player does not have enough unused status points.");
            }
        }
        const bool requiresCheatManager = payload.addExperience ||
            payload.grantStatusPoints ||
            payload.grantTechnologyPoints ||
            payload.grantAncientTechnologyPoints;
        if (requiresCheatManager && !cheatManager)
        {
            return Failure(
                command,
                "PLAYER_CHEAT_MANAGER_NOT_AVAILABLE",
                "The player's native PalCheatManager is not available for this operation.");
        }
        if ((payload.grantTechnologyPoints ||
             payload.grantAncientTechnologyPoints) && !selectedTechnology)
        {
            return Failure(
                command,
                "PLAYER_TECHNOLOGY_DATA_NOT_LOADED",
                "The player's technology data is not loaded.");
        }

        if (payload.dryRun)
        {
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = Contracts::CommandState::Succeeded,
                .ObservedRevision = beforeRevision,
                .DataJson = std::string{"{"} +
                    "\"dryRun\":true," +
                    "\"applied\":false," +
                    "\"operation\":\"" + EscapeJson(operation) + "\"," +
                    "\"nativeFunction\":\"" + EscapeJson(nativeFunction) + "\"," +
                    "\"value\":" + std::to_string(operationValue) + "," +
                    "\"before\":{" +
                        "\"level\":" + std::to_string(beforeLevel) + "," +
                        "\"totalExperience\":" + std::to_string(beforeExperience) + "," +
                        "\"unusedStatusPoints\":" + std::to_string(beforeUnused) + "," +
                        "\"technologyPoints\":" + std::to_string(beforeTechnology) + "," +
                        "\"ancientTechnologyPoints\":" + std::to_string(beforeAncientTechnology) +
                    "}," +
                    "\"preview\":{" +
                        "\"targetExperience\":" + std::to_string(requestedExperience) + "," +
                        "\"targetStatusRank\":" + std::to_string(
                            beforeStatusRank + (payload.allocateStatusPoints
                                ? *payload.allocateStatusPoints : 0)) +
                    "}}"
            };
        }

        bool invoked = false;
        if (payload.addExperience)
        {
            invoked = InvokeSingleIntFunction(
                cheatManager,
                STR("AddPlayerExp"),
                STR("/Script/Pal.PalCheatManager:AddPlayerExp"),
                "Function /Script/Pal.PalCheatManager:AddPlayerExp",
                STR("addExp"),
                operationValue);
        }
        else if (payload.grantStatusPoints)
        {
            invoked = InvokeSingleIntFunction(
                cheatManager,
                STR("AddExStatusPoint"),
                STR("/Script/Pal.PalCheatManager:AddExStatusPoint"),
                "Function /Script/Pal.PalCheatManager:AddExStatusPoint",
                STR("Point"),
                operationValue);
        }
        else if (payload.grantTechnologyPoints)
        {
            invoked = InvokeSingleIntFunction(
                cheatManager,
                STR("AddTechnologyPoints"),
                STR("/Script/Pal.PalCheatManager:AddTechnologyPoints"),
                "Function /Script/Pal.PalCheatManager:AddTechnologyPoints",
                STR("AddPoints"),
                operationValue);
        }
        else if (payload.grantAncientTechnologyPoints)
        {
            invoked = InvokeSingleIntFunction(
                cheatManager,
                STR("AddBossTechnologyPoints"),
                STR("/Script/Pal.PalCheatManager:AddBossTechnologyPoints"),
                "Function /Script/Pal.PalCheatManager:AddBossTechnologyPoints",
                STR("AddPoints"),
                operationValue);
        }
        else
        {
            invoked = InvokePlayerStatusAllocation(
                selectedController,
                *payload.allocateStatusId,
                beforeStatusRank + operationValue);
        }
        if (!invoked)
        {
            return Failure(
                command,
                "PLAYER_NATIVE_FUNCTION_SIGNATURE_MISMATCH",
                "The selected native player function did not match its validated signature.");
        }

        const auto afterLevel = static_cast<std::int32_t>(
            *mapping.Level->ContainerPtrToValuePtr<uint8>(saveMemory));
        const auto afterExperience = *mapping.Exp
            ->ContainerPtrToValuePtr<int64>(saveMemory);
        const auto afterUnused = static_cast<std::int32_t>(
            *mapping.UnusedStatusPoint->ContainerPtrToValuePtr<uint16>(saveMemory));
        const auto afterTechnology = selectedTechnology
            ? *mapping.TechnologyPoint
                ->ContainerPtrToValuePtr<int32>(selectedTechnology)
            : beforeTechnology;
        const auto afterAncientTechnology = selectedTechnology
            ? *mapping.BossTechnologyPoint
                ->ContainerPtrToValuePtr<int32>(selectedTechnology)
            : beforeAncientTechnology;
        const auto afterStatusRank = payload.allocateStatusId
            ? ReadAllocatedStatusPoint(mapping, saveMemory, *payload.allocateStatusId)
            : beforeStatusRank;
        bool verified = false;
        if (payload.addExperience || payload.targetLevel)
        {
            verified = afterExperience == requestedExperience &&
                afterLevel >= beforeLevel;
        }
        else if (payload.grantStatusPoints)
        {
            verified = afterUnused == beforeUnused + operationValue;
        }
        else if (payload.grantTechnologyPoints)
        {
            verified = afterTechnology == beforeTechnology + operationValue;
        }
        else if (payload.grantAncientTechnologyPoints)
        {
            verified = afterAncientTechnology ==
                beforeAncientTechnology + operationValue;
        }
        else
        {
            verified = afterStatusRank == beforeStatusRank + operationValue &&
                afterUnused == beforeUnused - operationValue;
        }
        const auto afterRevision = PlayerProgressionRevision(
            mapping, selectedParameter, selectedTechnology, selectedUid);
        if (!verified)
        {
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = Contracts::CommandState::Uncertain,
                .ObservedRevision = afterRevision,
                .ErrorCode = "PLAYER_NATIVE_SETTLEMENT_VERIFY_FAILED",
                .ErrorMessage = "The native player function was dispatched, but its expected progression settlement could not be proven. Refresh before any retry."
            };
        }
        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = afterRevision,
            .DataJson = std::string{"{"} +
                "\"dryRun\":false," +
                "\"applied\":true," +
                "\"operation\":\"" + EscapeJson(operation) + "\"," +
                "\"nativeFunction\":\"" + EscapeJson(nativeFunction) + "\"," +
                "\"readBackVerified\":true," +
                "\"beforeRevision\":\"" + std::to_string(beforeRevision) + "\"," +
                "\"revision\":\"" + std::to_string(afterRevision) + "\"," +
                "\"after\":{" +
                    "\"level\":" + std::to_string(afterLevel) + "," +
                    "\"totalExperience\":" + std::to_string(afterExperience) + "," +
                    "\"unusedStatusPoints\":" + std::to_string(afterUnused) + "," +
                    "\"technologyPoints\":" + std::to_string(afterTechnology) + "," +
                    "\"ancientTechnologyPoints\":" + std::to_string(afterAncientTechnology) + "," +
                    "\"statusRank\":" + std::to_string(afterStatusRank) + "}}"
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ReadPlayerSchema(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        auto* playerStateClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalPlayerState"));
        if (!playerStateClass)
        {
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = Contracts::CommandState::Succeeded,
                .DataJson = std::string{"{"} +
                    "\"observedAt\":\"" + UtcNow() + "\"," +
                    "\"executionThread\":\"unreal-engine-tick\"," +
                    "\"targetClass\":\"/Script/Pal.PalPlayerState\"," +
                    "\"classFound\":false," +
                    "\"propertyCount\":0," +
                    "\"properties\":[]," +
                    "\"identityCandidates\":[]," +
                    "\"candidateFunctions\":[]," +
                    "\"inheritance\":[]}"
            };
        }

        std::string inheritanceJson{"["};
        bool firstInheritance = true;
        for (UStruct* current = playerStateClass; current; current = current->GetSuperStruct())
        {
            if (!firstInheritance)
            {
                inheritanceJson += ',';
            }
            firstInheritance = false;
            inheritanceJson += "\"" + EscapeJson(to_string(current->GetName())) + "\"";
        }
        inheritanceJson += ']';

        std::string propertiesJson{"["};
        std::string candidatesJson{"["};
        std::size_t propertyCount = 0;
        std::size_t returnedProperties = 0;
        std::size_t candidateCount = 0;
        for (FProperty* property : TFieldRange<FProperty>(
                 playerStateClass,
                 EFieldIterationFlags::Default))
        {
            ++propertyCount;
            const auto propertyName = to_string(property->GetName());
            const auto typeName = to_string(property->GetClass().GetName());
            const auto detailType = PropertyDetailType(property);
            auto* owner = Cast<UClass>(property->GetOutermostOwner());
            const auto ownerName = owner ? to_string(owner->GetName()) : std::string{"unknown"};
            const bool replicated = property->HasAnyPropertyFlags(CPF_Net);

            if (returnedProperties < MaxReturnedProperties)
            {
                if (returnedProperties > 0)
                {
                    propertiesJson += ',';
                }
                propertiesJson += std::string{"{"} +
                    "\"name\":\"" + EscapeJson(propertyName) + "\"," +
                    "\"type\":\"" + EscapeJson(typeName) + "\"," +
                    "\"detailType\":" + (detailType.empty()
                        ? std::string{"null"}
                        : "\"" + EscapeJson(detailType) + "\"") + "," +
                    "\"owner\":\"" + EscapeJson(ownerName) + "\"," +
                    "\"replicated\":" + (replicated ? "true" : "false") + "}";
                ++returnedProperties;
            }

            if (IsIdentityCandidate(propertyName))
            {
                if (candidateCount > 0)
                {
                    candidatesJson += ',';
                }
                candidatesJson += std::string{"{"} +
                    "\"name\":\"" + EscapeJson(propertyName) + "\"," +
                    "\"type\":\"" + EscapeJson(typeName) + "\"," +
                    "\"detailType\":" + (detailType.empty()
                        ? std::string{"null"}
                        : "\"" + EscapeJson(detailType) + "\"") + "," +
                    "\"owner\":\"" + EscapeJson(ownerName) + "\"," +
                    "\"replicated\":" + (replicated ? "true" : "false") + "}";
                ++candidateCount;
            }
        }
        propertiesJson += ']';
        candidatesJson += ']';

        std::string functionsJson{"["};
        std::size_t functionCount = 0;
        for (UFunction* function : TFieldRange<UFunction>(
                 playerStateClass,
                 EFieldIterationFlags::IncludeAll))
        {
            const auto functionName = to_string(function->GetName());
            if (!IsIdentityCandidate(functionName) || functionCount >= MaxReturnedFunctions)
            {
                continue;
            }
            if (functionCount > 0)
            {
                functionsJson += ',';
            }
            functionsJson += "\"" + EscapeJson(functionName) + "\"";
            ++functionCount;
        }
        functionsJson += ']';

        const auto data = std::string{"{"} +
            "\"observedAt\":\"" + UtcNow() + "\"," +
            "\"executionThread\":\"unreal-engine-tick\"," +
            "\"targetClass\":\"/Script/Pal.PalPlayerState\"," +
            "\"classFound\":true," +
            "\"propertyCount\":" + std::to_string(propertyCount) + "," +
            "\"truncated\":" + (propertyCount > returnedProperties ? "true" : "false") + "," +
            "\"properties\":" + propertiesJson + "," +
            "\"identityCandidates\":" + candidatesJson + "," +
            "\"candidateFunctions\":" + functionsJson + "," +
            "\"inheritance\":" + inheritanceJson + "}";

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = data
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ReadPlayerProgressionSchema(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        struct Candidate
        {
            const RC::File::CharType* Path;
            const RC::File::CharType* ShortName;
        };

        constexpr std::array<Candidate, 9> Candidates{{
            {STR("/Script/Pal.PalIndividualCharacterSaveParameter"), STR("PalIndividualCharacterSaveParameter")},
            {STR("/Script/Pal.PalIndividualCharacterParameter"), STR("PalIndividualCharacterParameter")},
            {STR("/Script/Pal.PalIndividualCharacterHandle"), STR("PalIndividualCharacterHandle")},
            {STR("/Script/Pal.PalPlayerState"), STR("PalPlayerState")},
            {STR("/Script/Pal.PalPlayerController"), STR("PalPlayerController")},
            {STR("/Script/Pal.PalTechnologyData"), STR("PalTechnologyData")},
            {STR("/Script/Pal.PalPlayerRecordData"), STR("PalPlayerRecordData")},
            {STR("/Script/Pal.PalGotStatusPoint"), STR("PalGotStatusPoint")},
            {STR("/Script/Pal.PalStatusAndRank"), STR("PalStatusAndRank")}
        }};

        const auto serializeParameters = [](UFunction* function)
        {
            std::string json{"["};
            std::size_t count = 0;
            for (FProperty* parameter : TFieldRange<FProperty>(
                     function,
                     EFieldIterationFlags::IncludeAll))
            {
                if (!parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm))
                {
                    continue;
                }
                if (count > 0)
                {
                    json += ',';
                }
                const auto detail = PropertyDetailType(parameter);
                json += std::string{"{"} +
                    "\"name\":\"" + EscapeJson(to_string(parameter->GetName())) + "\"," +
                    "\"propertyClass\":\"" + EscapeJson(to_string(parameter->GetClass().GetName())) + "\"," +
                    "\"detailType\":" + (detail.empty()
                        ? std::string{"null"}
                        : "\"" + EscapeJson(detail) + "\"") + "," +
                    "\"out\":" + (parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_OutParm)
                        ? "true" : "false") + "," +
                    "\"return\":" + (parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_ReturnParm)
                        ? "true" : "false") + "}";
                ++count;
            }
            json += ']';
            return std::pair{std::move(json), count};
        };

        std::string typesJson{"["};
        std::string missingTypesJson{"["};
        std::size_t foundTypeCount = 0;
        std::size_t missingTypeCount = 0;
        for (const auto& candidate : Candidates)
        {
            auto* type = UObjectGlobals::StaticFindObject<UStruct*>(
                nullptr,
                nullptr,
                candidate.Path);
            if (!type)
            {
                if (missingTypeCount > 0)
                {
                    missingTypesJson += ',';
                }
                missingTypesJson += "\"" + EscapeJson(to_string(candidate.Path)) + "\"";
                ++missingTypeCount;
                continue;
            }
            if (foundTypeCount > 0)
            {
                typesJson += ',';
            }

            std::string propertiesJson{"["};
            std::size_t propertyCount = 0;
            for (FProperty* property : TFieldRange<FProperty>(
                     type,
                     EFieldIterationFlags::IncludeAll))
            {
                if (propertyCount >= MaxPropertiesPerPalType)
                {
                    break;
                }
                if (propertyCount > 0)
                {
                    propertiesJson += ',';
                }
                const auto detail = PropertyDetailType(property);
                auto* owner = Cast<UStruct>(property->GetOutermostOwner());
                propertiesJson += std::string{"{"} +
                    "\"name\":\"" + EscapeJson(to_string(property->GetName())) + "\"," +
                    "\"propertyClass\":\"" + EscapeJson(to_string(property->GetClass().GetName())) + "\"," +
                    "\"detailType\":" + (detail.empty()
                        ? std::string{"null"}
                        : "\"" + EscapeJson(detail) + "\"") + "," +
                    "\"owner\":" + (owner
                        ? "\"" + EscapeJson(to_string(owner->GetName())) + "\""
                        : std::string{"null"}) + "}";
                ++propertyCount;
            }
            propertiesJson += ']';

            std::string functionsJson{"["};
            std::size_t functionCount = 0;
            if (Cast<UClass>(type))
            {
                for (UFunction* function : TFieldRange<UFunction>(
                         type,
                         EFieldIterationFlags::IncludeAll))
                {
                    const auto functionName = to_string(function->GetName());
                    if (!IsPlayerProgressionFunction(functionName) ||
                        functionCount >= MaxReturnedFunctions)
                    {
                        continue;
                    }
                    if (functionCount > 0)
                    {
                        functionsJson += ',';
                    }
                    const auto [parametersJson, parameterCount] =
                        serializeParameters(function);
                    functionsJson += std::string{"{"} +
                        "\"name\":\"" + EscapeJson(functionName) + "\"," +
                        "\"fullName\":\"" + EscapeJson(to_string(function->GetFullName())) + "\"," +
                        "\"functionFlags\":" + std::to_string(function->GetFunctionFlags()) + "," +
                        "\"parameterCount\":" + std::to_string(parameterCount) + "," +
                        "\"parameterSize\":" + std::to_string(function->GetPropertiesSize()) + "," +
                        "\"parameters\":" + parametersJson + "}";
                    ++functionCount;
                }
            }
            functionsJson += ']';

            std::vector<UObject*> objects;
            UObjectGlobals::FindAllOf(candidate.ShortName, objects);
            typesJson += std::string{"{"} +
                "\"path\":\"" + EscapeJson(to_string(candidate.Path)) + "\"," +
                "\"name\":\"" + EscapeJson(to_string(type->GetName())) + "\"," +
                "\"kind\":\"" + (Cast<UClass>(type) ? "class" : "struct") + "\"," +
                "\"propertyCount\":" + std::to_string(propertyCount) + "," +
                "\"properties\":" + propertiesJson + "," +
                "\"functionCount\":" + std::to_string(functionCount) + "," +
                "\"functions\":" + functionsJson + "," +
                "\"objectCount\":" + std::to_string(objects.size()) + "}";
            ++foundTypeCount;
        }
        typesJson += ']';
        missingTypesJson += ']';

        std::vector<UObject*> functionObjects;
        UObjectGlobals::FindAllOf(STR("Function"), functionObjects);
        std::string globalFunctionsJson{"["};
        std::size_t globalFunctionCount = 0;
        for (auto* object : functionObjects)
        {
            auto* function = Cast<UFunction>(object);
            if (!function || globalFunctionCount >= MaxReturnedFunctions)
            {
                continue;
            }
            const auto name = to_string(function->GetName());
            const auto fullName = to_string(function->GetFullName());
            if (!IsPlayerProgressionFunction(name) ||
                fullName.find("/Script/Pal.") == std::string::npos)
            {
                continue;
            }
            if (globalFunctionCount > 0)
            {
                globalFunctionsJson += ',';
            }
            const auto [parametersJson, parameterCount] =
                serializeParameters(function);
            globalFunctionsJson += std::string{"{"} +
                "\"name\":\"" + EscapeJson(name) + "\"," +
                "\"fullName\":\"" + EscapeJson(fullName) + "\"," +
                "\"functionFlags\":" + std::to_string(function->GetFunctionFlags()) + "," +
                "\"parameterCount\":" + std::to_string(parameterCount) + "," +
                "\"parameterSize\":" + std::to_string(function->GetPropertiesSize()) + "," +
                "\"parameters\":" + parametersJson + "}";
            ++globalFunctionCount;
        }
        globalFunctionsJson += ']';

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = std::string{"{"} +
                "\"observedAt\":\"" + UtcNow() + "\"," +
                "\"executionThread\":\"unreal-engine-tick\"," +
                "\"candidateTypeCount\":" + std::to_string(Candidates.size()) + "," +
                "\"foundTypeCount\":" + std::to_string(foundTypeCount) + "," +
                "\"missingTypes\":" + missingTypesJson + "," +
                "\"globalFunctionCount\":" + std::to_string(globalFunctionCount) + "," +
                "\"globalFunctions\":" + globalFunctionsJson + "," +
                "\"types\":" + typesJson + "}"
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ReadInventorySchema(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        constexpr std::array<const File::CharType*, 13> CandidatePaths{
            STR("/Script/Pal.PalPlayerInventoryData"),
            STR("/Script/Pal.PalPlayerDataInventoryInfo"),
            STR("/Script/Pal.PalItemContainer"),
            STR("/Script/Pal.PalItemSlot"),
            STR("/Script/Pal.PalContainerId"),
            STR("/Script/Pal.PalItemId"),
            STR("/Script/Pal.PalItemSlotId"),
            STR("/Script/Pal.PalItemSlotIdAndNum"),
            STR("/Script/Pal.PalItemContainerInfo"),
            STR("/Script/Pal.PalItemContainerSaveData"),
            STR("/Script/Pal.PalItemSlotSaveData"),
            STR("/Script/Pal.PalItemContainerManager"),
            STR("/Script/Pal.PalItemContainerMultiHelper")
        };

        std::string typesJson{"["};
        std::string missingJson{"["};
        std::size_t typeCount = 0;
        std::size_t missingCount = 0;
        for (const auto* path : CandidatePaths)
        {
            auto* object = UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, path);
            auto* type = object ? Cast<UStruct>(object) : nullptr;
            if (!type)
            {
                if (missingCount > 0)
                {
                    missingJson += ',';
                }
                missingJson += "\"" + EscapeJson(to_string(path)) + "\"";
                ++missingCount;
                continue;
            }

            if (typeCount > 0)
            {
                typesJson += ',';
            }

            const auto kind = Cast<UClass>(type) ? "class" :
                (Cast<UScriptStruct>(type) ? "struct" : "other");
            std::string propertiesJson{"["};
            std::size_t propertyCount = 0;
            std::size_t returnedProperties = 0;
            for (FProperty* property : TFieldRange<FProperty>(
                     type,
                     EFieldIterationFlags::Default))
            {
                ++propertyCount;
                if (returnedProperties >= MaxPropertiesPerInventoryType)
                {
                    continue;
                }
                if (returnedProperties > 0)
                {
                    propertiesJson += ',';
                }

                const auto detailType = PropertyDetailType(property);
                auto* owner = Cast<UClass>(property->GetOutermostOwner());
                if (!owner)
                {
                    owner = Cast<UClass>(type);
                }
                propertiesJson += std::string{"{"} +
                    "\"name\":\"" + EscapeJson(to_string(property->GetName())) + "\"," +
                    "\"type\":\"" + EscapeJson(to_string(property->GetClass().GetName())) + "\"," +
                    "\"detailType\":" + (detailType.empty()
                        ? std::string{"null"}
                        : "\"" + EscapeJson(detailType) + "\"") + "," +
                    "\"owner\":\"" + EscapeJson(owner
                        ? to_string(owner->GetName())
                        : to_string(type->GetName())) + "\"," +
                    "\"replicated\":" +
                        (property->HasAnyPropertyFlags(CPF_Net) ? "true" : "false") + "}";
                ++returnedProperties;
            }
            propertiesJson += ']';

            std::string functionsJson{"["};
            std::string functionSchemasJson{"["};
            std::size_t functionCount = 0;
            if (Cast<UClass>(type))
            {
                for (UFunction* function : TFieldRange<UFunction>(
                         type,
                         EFieldIterationFlags::IncludeAll))
                {
                    const auto functionName = to_string(function->GetName());
                    if (!IsInventoryFunction(functionName) ||
                        functionCount >= MaxReturnedFunctions)
                    {
                        continue;
                    }
                    if (functionCount > 0)
                    {
                        functionsJson += ',';
                        functionSchemasJson += ',';
                    }
                    functionsJson += "\"" + EscapeJson(functionName) + "\"";

                    std::string parametersJson{"["};
                    std::size_t parameterCount = 0;
                    for (FProperty* parameter : TFieldRange<FProperty>(
                             function,
                             EFieldIterationFlags::IncludeAll))
                    {
                        if (!parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm))
                        {
                            continue;
                        }
                        if (parameterCount > 0)
                        {
                            parametersJson += ',';
                        }
                        const auto parameterDetail = PropertyDetailType(parameter);
                        parametersJson += std::string{"{"} +
                            "\"name\":\"" + EscapeJson(to_string(parameter->GetName())) + "\"," +
                            "\"propertyClass\":\"" + EscapeJson(to_string(parameter->GetClass().GetName())) + "\"," +
                            "\"detailType\":" + (parameterDetail.empty()
                                ? std::string{"null"}
                                : "\"" + EscapeJson(parameterDetail) + "\"") + "," +
                            "\"out\":" + (parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_OutParm)
                                ? "true" : "false") + "," +
                            "\"return\":" + (parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_ReturnParm)
                                ? "true" : "false") + "}";
                        ++parameterCount;
                    }
                    parametersJson += ']';
                    functionSchemasJson += std::string{"{"} +
                        "\"name\":\"" + EscapeJson(functionName) + "\"," +
                        "\"parameterCount\":" + std::to_string(parameterCount) + "," +
                        "\"parameterSize\":" + std::to_string(function->GetPropertiesSize()) + "," +
                        "\"parameters\":" + parametersJson + "}";
                    ++functionCount;
                }
            }
            functionsJson += ']';
            functionSchemasJson += ']';

            typesJson += std::string{"{"} +
                "\"name\":\"" + EscapeJson(to_string(type->GetName())) + "\"," +
                "\"fullName\":\"" + EscapeJson(to_string(type->GetFullName())) + "\"," +
                "\"kind\":\"" + kind + "\"," +
                "\"propertyCount\":" + std::to_string(propertyCount) + "," +
                "\"truncated\":" +
                    (propertyCount > returnedProperties ? "true" : "false") + "," +
                "\"properties\":" + propertiesJson + "," +
                "\"candidateFunctions\":" + functionsJson + "," +
                "\"functionSchemas\":" + functionSchemasJson + "}";
            ++typeCount;
        }
        typesJson += ']';
        missingJson += ']';

        const auto data = std::string{"{"} +
            "\"observedAt\":\"" + UtcNow() + "\"," +
            "\"executionThread\":\"unreal-engine-tick\"," +
            "\"typeCount\":" + std::to_string(typeCount) + "," +
            "\"missingTypes\":" + missingJson + "," +
            "\"types\":" + typesJson + "}";

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = data
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ProbeInventory(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        const auto mapping = ResolveInventoryProperties();
        std::vector<UObject*> inventoryObjects;
        std::vector<UObject*> containerObjects;
        UObjectGlobals::FindAllOf(STR("PalPlayerInventoryData"), inventoryObjects);
        UObjectGlobals::FindAllOf(STR("PalItemContainer"), containerObjects);

        std::unordered_map<std::string, UObject*> containersById;
        std::unordered_map<std::string, std::uint64_t> containerScores;
        if (mapping.IsReady())
        {
            for (auto* container : containerObjects)
            {
                if (const auto id = ReadTopLevelGuid(mapping.ContainerId, container))
                {
                    std::uint64_t score = 0;
                    const auto* slots = mapping.SlotArray
                        ->ContainerPtrToValuePtr<TArray<UObject*>>(container);
                    if (slots)
                    {
                        score = static_cast<std::uint64_t>(std::max(slots->Num(), 0));
                        const auto inspectedSlots = std::min(
                            static_cast<std::size_t>(std::max(slots->Num(), 0)),
                            MaxSlotsPerContainer);
                        for (std::size_t index = 0; index < inspectedSlots; ++index)
                        {
                            auto* slot = (*slots)[static_cast<int32>(index)];
                            if (!slot || !slot->IsA(mapping.SlotClass))
                            {
                                continue;
                            }
                            const auto stackCount = *mapping.StackCount
                                ->ContainerPtrToValuePtr<int32>(slot);
                            auto* itemIdMemory = mapping.ItemId
                                ->ContainerPtrToValuePtr<uint8>(slot);
                            const auto* staticItemId = mapping.StaticItemId
                                ->ContainerPtrToValuePtr<FName>(itemIdMemory);
                            if (stackCount > 0 && staticItemId &&
                                staticItemId->ToString() != STR("None"))
                            {
                                score += 1'000'000ULL +
                                    static_cast<std::uint64_t>(stackCount);
                            }
                        }
                    }

                    const auto idText = GuidToString(*id);
                    auto existingScore = containerScores.find(idText);
                    if (existingScore == containerScores.end() ||
                        score > existingScore->second)
                    {
                        containersById[idText] = container;
                        containerScores[idText] = score;
                    }
                }
            }
        }

        std::string inventoriesJson{"["};
        const auto returnedInventories = std::min(
            inventoryObjects.size(),
            MaxInventoryObjects);
        for (std::size_t inventoryIndex = 0;
             inventoryIndex < returnedInventories;
             ++inventoryIndex)
        {
            auto* inventory = inventoryObjects[inventoryIndex];
            if (inventoryIndex > 0)
            {
                inventoriesJson += ',';
            }

            const auto ownerPlayerUId = ReadTopLevelGuid(
                mapping.OwnerPlayerUId,
                inventory);
            auto* inventoryInfoMemory = mapping.InventoryInfo
                ? mapping.InventoryInfo->ContainerPtrToValuePtr<uint8>(inventory)
                : nullptr;

            std::string containersJson{"["};
            for (std::size_t containerIndex = 0;
                 containerIndex < mapping.ContainerFields.size();
                 ++containerIndex)
            {
                const auto& containerField = mapping.ContainerFields[containerIndex];
                if (containerIndex > 0)
                {
                    containersJson += ',';
                }

                const auto containerId = ReadNestedGuid(
                    containerField.Property,
                    inventoryInfoMemory);
                const auto containerIdText = containerId
                    ? GuidToString(*containerId)
                    : std::string{};
                auto foundContainer = containersById.find(containerIdText);
                auto* container = foundContainer == containersById.end()
                    ? nullptr
                    : foundContainer->second;

                std::string slotsJson{"["};
                std::size_t slotCount = 0;
                std::size_t returnedSlots = 0;
                if (container && mapping.SlotArray)
                {
                    const auto* slots = mapping.SlotArray
                        ->ContainerPtrToValuePtr<TArray<UObject*>>(container);
                    if (slots)
                    {
                        slotCount = static_cast<std::size_t>(std::max(slots->Num(), 0));
                        returnedSlots = std::min(slotCount, MaxSlotsPerContainer);
                        for (std::size_t slotArrayIndex = 0;
                             slotArrayIndex < returnedSlots;
                             ++slotArrayIndex)
                        {
                            auto* slot = (*slots)[static_cast<int32>(slotArrayIndex)];
                            if (slotArrayIndex > 0)
                            {
                                slotsJson += ',';
                            }
                            if (!slot || !slot->IsA(mapping.SlotClass))
                            {
                                slotsJson += "null";
                                continue;
                            }

                            const auto slotIndex = *mapping.SlotIndex
                                ->ContainerPtrToValuePtr<int32>(slot);
                            const auto stackCount = *mapping.StackCount
                                ->ContainerPtrToValuePtr<int32>(slot);
                            auto* itemIdMemory = mapping.ItemId
                                ->ContainerPtrToValuePtr<uint8>(slot);
                            const auto* staticItemId = mapping.StaticItemId
                                ->ContainerPtrToValuePtr<FName>(itemIdMemory);
                            const auto staticItemIdText = staticItemId
                                ? to_string(staticItemId->ToString())
                                : std::string{"None"};

                            slotsJson += std::string{"{"} +
                                "\"slotIndex\":" + std::to_string(slotIndex) + "," +
                                "\"staticItemId\":\"" +
                                    EscapeJson(staticItemIdText) + "\"," +
                                "\"stackCount\":" + std::to_string(stackCount) + "}";
                        }
                    }
                }
                slotsJson += ']';

                containersJson += std::string{"{"} +
                    "\"kind\":\"" + containerField.Kind + "\"," +
                    "\"containerId\":" + (containerId
                        ? "\"" + EscapeJson(containerIdText) + "\""
                        : std::string{"null"}) + "," +
                    "\"resolved\":" + (container ? "true" : "false") + "," +
                    "\"slotCount\":" + std::to_string(slotCount) + "," +
                    "\"truncated\":" +
                        (slotCount > returnedSlots ? "true" : "false") + "," +
                    "\"slots\":" + slotsJson + "}";
            }
            containersJson += ']';

            inventoriesJson += std::string{"{"} +
                "\"ownerPlayerUId\":" + (ownerPlayerUId
                    ? "\"" + GuidToString(*ownerPlayerUId) + "\""
                    : std::string{"null"}) + "," +
                "\"objectName\":\"" + EscapeJson(to_string(inventory->GetName())) + "\"," +
                "\"containers\":" + containersJson + "}";
        }
        inventoriesJson += ']';

        const auto data = std::string{"{"} +
            "\"observedAt\":\"" + UtcNow() + "\"," +
            "\"executionThread\":\"unreal-engine-tick\"," +
            "\"mappingReady\":" + (mapping.IsReady() ? "true" : "false") + "," +
            "\"inventoryObjectCount\":" + std::to_string(inventoryObjects.size()) + "," +
            "\"containerObjectCount\":" + std::to_string(containerObjects.size()) + "," +
            "\"truncated\":" +
                (inventoryObjects.size() > returnedInventories ? "true" : "false") + "," +
            "\"inventories\":" + inventoriesJson + "}";

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = data
        };
    }

    Contracts::CommandResult PalworldGameAdapter::MutateInventory(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        Detail::InventoryMutationPayload payload{};
        const auto parseError = glz::read<glz::opts{
            .error_on_unknown_keys = true,
            .error_on_missing_keys = false}>(payload, command.PayloadJson);
        if (parseError || payload.ownerPlayerId.empty() ||
            payload.containerId.empty() || payload.containerKind.empty() ||
            payload.itemId.empty() || payload.slotIndex < 0 ||
            payload.expectedQuantity < 1 || payload.quantity < 1)
        {
            return Failure(
                command,
                "INVALID_INVENTORY_MUTATION_PAYLOAD",
                "Inventory mutation payload is invalid or incomplete.");
        }
        if (payload.quantity > 999999)
        {
            return Failure(
                command,
                "INVENTORY_QUANTITY_OUT_OF_RANGE",
                "Inventory quantity must be between 1 and 999999.");
        }
        constexpr std::array<std::string_view, 3> WritableKinds{
            "common", "dropSlot", "food"};
        if (std::ranges::find(WritableKinds, payload.containerKind) ==
            WritableKinds.end())
        {
            return Failure(
                command,
                "INVENTORY_CONTAINER_READ_ONLY",
                "Only common, dropSlot and food containers support quantity edits.");
        }

        const auto mapping = ResolveInventoryProperties();
        if (!mapping.IsReady())
        {
            return Failure(
                command,
                "INVENTORY_MAPPING_NOT_READY",
                "Inventory reflection mapping is not ready for this game build.");
        }

        const auto normalizedOwnerId = NormalizeIdentifier(payload.ownerPlayerId);
        const auto normalizedContainerId = NormalizeIdentifier(payload.containerId);
        std::vector<UObject*> inventoryObjects;
        UObjectGlobals::FindAllOf(STR("PalPlayerInventoryData"), inventoryObjects);

        UObject* selectedInventory = nullptr;
        const InventoryProperties::ContainerField* selectedContainerField = nullptr;
        for (auto* inventory : inventoryObjects)
        {
            const auto ownerId = ReadTopLevelGuid(mapping.OwnerPlayerUId, inventory);
            if (!ownerId)
            {
                continue;
            }
            const auto normalizedOwner = NormalizeIdentifier(GuidToString(*ownerId));
            const bool ownerMatches = normalizedOwner == normalizedOwnerId ||
                (normalizedOwnerId.size() == 8 &&
                 normalizedOwner.starts_with(normalizedOwnerId));
            if (!ownerMatches)
            {
                continue;
            }

            auto* inventoryInfoMemory = mapping.InventoryInfo
                ->ContainerPtrToValuePtr<uint8>(inventory);
            for (const auto& field : mapping.ContainerFields)
            {
                if (payload.containerKind != field.Kind)
                {
                    continue;
                }
                const auto fieldContainerId = ReadNestedGuid(
                    field.Property,
                    inventoryInfoMemory);
                if (!fieldContainerId || NormalizeIdentifier(
                        GuidToString(*fieldContainerId)) != normalizedContainerId)
                {
                    return Failure(
                        command,
                        "INVENTORY_CONTAINER_OWNER_MISMATCH",
                        "The selected container does not belong to the requested player inventory.");
                }
                selectedInventory = inventory;
                selectedContainerField = &field;
                break;
            }
            break;
        }
        if (!selectedInventory || !selectedContainerField)
        {
            return Failure(
                command,
                "PLAYER_INVENTORY_NOT_LOADED",
                "The requested player inventory is not loaded in the server process.");
        }

        std::vector<UObject*> containerObjects;
        UObjectGlobals::FindAllOf(STR("PalItemContainer"), containerObjects);
        UObject* selectedContainer = nullptr;
        std::uint64_t selectedScore = 0;
        for (auto* container : containerObjects)
        {
            const auto containerId = ReadTopLevelGuid(mapping.ContainerId, container);
            if (!containerId || NormalizeIdentifier(GuidToString(*containerId)) !=
                    normalizedContainerId)
            {
                continue;
            }
            const auto* slots = mapping.SlotArray
                ->ContainerPtrToValuePtr<TArray<UObject*>>(container);
            std::uint64_t score = slots
                ? static_cast<std::uint64_t>(std::max(slots->Num(), 0))
                : 0;
            if (slots)
            {
                const auto inspectedSlots = std::min(
                    static_cast<std::size_t>(std::max(slots->Num(), 0)),
                    MaxSlotsPerContainer);
                for (std::size_t index = 0; index < inspectedSlots; ++index)
                {
                    auto* slot = (*slots)[static_cast<int32>(index)];
                    if (!slot || !slot->IsA(mapping.SlotClass))
                    {
                        continue;
                    }
                    const auto stackCount = *mapping.StackCount
                        ->ContainerPtrToValuePtr<int32>(slot);
                    if (stackCount > 0)
                    {
                        score += 1'000'000ULL +
                            static_cast<std::uint64_t>(stackCount);
                    }
                }
            }
            if (!selectedContainer || score > selectedScore)
            {
                selectedContainer = container;
                selectedScore = score;
            }
        }
        if (!selectedContainer)
        {
            return Failure(
                command,
                "INVENTORY_CONTAINER_NOT_LOADED",
                "The requested inventory container is not loaded in the server process.");
        }

        UObject* selectedSlot = nullptr;
        const auto* slots = mapping.SlotArray
            ->ContainerPtrToValuePtr<TArray<UObject*>>(selectedContainer);
        if (slots)
        {
            const auto inspectedSlots = std::min(
                static_cast<std::size_t>(std::max(slots->Num(), 0)),
                MaxSlotsPerContainer);
            for (std::size_t index = 0; index < inspectedSlots; ++index)
            {
                auto* slot = (*slots)[static_cast<int32>(index)];
                if (slot && slot->IsA(mapping.SlotClass) &&
                    *mapping.SlotIndex->ContainerPtrToValuePtr<int32>(slot) ==
                        payload.slotIndex)
                {
                    selectedSlot = slot;
                    break;
                }
            }
        }
        if (!selectedSlot)
        {
            return Failure(
                command,
                "INVENTORY_SLOT_NOT_LOADED",
                "The requested inventory slot is not loaded in the selected container.");
        }

        auto* itemIdMemory = mapping.ItemId
            ->ContainerPtrToValuePtr<uint8>(selectedSlot);
        const auto* staticItemId = mapping.StaticItemId
            ->ContainerPtrToValuePtr<FName>(itemIdMemory);
        const auto currentItemId = staticItemId
            ? to_string(staticItemId->ToString())
            : std::string{"None"};
        const auto currentQuantity = *mapping.StackCount
            ->ContainerPtrToValuePtr<int32>(selectedSlot);
        if (currentItemId != payload.itemId ||
            currentQuantity != payload.expectedQuantity)
        {
            return Failure(
                command,
                "INVENTORY_SLOT_CONFLICT",
                "The item or quantity changed after it was read; refresh before retrying.");
        }
        if (currentQuantity == payload.quantity)
        {
            return Failure(
                command,
                "EMPTY_INVENTORY_PATCH",
                "The requested quantity is already applied.");
        }

        auto* containerUpdate = ResolveFunction(
            selectedContainer,
            STR("OnUpdateSlotContent"),
            STR("/Script/Pal.PalItemContainer:OnUpdateSlotContent"));
        auto* inventoryUpdate = ResolveFunction(
            selectedInventory,
            STR("OnUpdateInventoryContainer"),
            STR("/Script/Pal.PalPlayerInventoryData:OnUpdateInventoryContainer"));
        const auto readDirectAggregate = [&]()
        {
            std::int64_t total = 0;
            const auto* aggregateSlots = mapping.SlotArray
                ->ContainerPtrToValuePtr<TArray<UObject*>>(selectedContainer);
            if (!aggregateSlots)
            {
                return total;
            }
            const auto inspectedSlots = std::min(
                static_cast<std::size_t>(std::max(aggregateSlots->Num(), 0)),
                MaxSlotsPerContainer);
            for (std::size_t index = 0; index < inspectedSlots; ++index)
            {
                auto* aggregateSlot = (*aggregateSlots)[static_cast<int32>(index)];
                if (!aggregateSlot || !aggregateSlot->IsA(mapping.SlotClass))
                {
                    continue;
                }
                auto* aggregateItemIdMemory = mapping.ItemId
                    ->ContainerPtrToValuePtr<uint8>(aggregateSlot);
                const auto* aggregateItemId = mapping.StaticItemId
                    ->ContainerPtrToValuePtr<FName>(aggregateItemIdMemory);
                if (aggregateItemId &&
                    to_string(aggregateItemId->ToString()) == payload.itemId)
                {
                    total += *mapping.StackCount
                        ->ContainerPtrToValuePtr<int32>(aggregateSlot);
                }
            }
            return total;
        };
        const auto nativeAggregateBefore = ReadNativeItemStackCount(
            selectedContainer,
            payload.itemId);
        const auto aggregateBefore = nativeAggregateBefore
            ? static_cast<std::int64_t>(*nativeAggregateBefore)
            : readDirectAggregate();
        if (!containerUpdate || !inventoryUpdate)
        {
            return Failure(
                command,
                "INVENTORY_NATIVE_SETTLEMENT_UNAVAILABLE",
                "The native inventory settlement functions are unavailable for this game build.");
        }

        const auto revisionFor = [&](std::int32_t quantity)
        {
            return StableRevision(payload.containerId + "|" +
                std::to_string(payload.slotIndex) + "|" + payload.itemId + "|" +
                std::to_string(quantity));
        };
        if (payload.dryRun)
        {
            const auto data = std::string{"{"} +
                "\"dryRun\":true," +
                "\"applied\":false," +
                "\"settlement\":{" +
                    "\"functions\":[\"PalItemContainer.OnUpdateSlotContent\"," +
                    "\"PalPlayerInventoryData.OnUpdateInventoryContainer\"]," +
                    "\"planned\":true," +
                    "\"aggregateVerified\":false}," +
                "\"slot\":{" +
                    "\"containerId\":\"" + EscapeJson(payload.containerId) + "\"," +
                    "\"containerKind\":\"" + EscapeJson(payload.containerKind) + "\"," +
                    "\"slotIndex\":" + std::to_string(payload.slotIndex) + "," +
                    "\"itemId\":\"" + EscapeJson(payload.itemId) + "\"," +
                    "\"quantity\":" + std::to_string(currentQuantity) + "," +
                    "\"requestedQuantity\":" + std::to_string(payload.quantity) + "}}";
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = Contracts::CommandState::Succeeded,
                .ObservedRevision = revisionFor(currentQuantity),
                .DataJson = data
            };
        }

        *mapping.StackCount->ContainerPtrToValuePtr<int32>(selectedSlot) =
            payload.quantity;
        const bool containerNotified = InvokeObjectParameterFunction(
            selectedContainer,
            STR("OnUpdateSlotContent"),
            STR("/Script/Pal.PalItemContainer:OnUpdateSlotContent"),
            STR("Slot"),
            selectedSlot);
        const bool inventoryNotified = InvokeObjectParameterFunction(
            selectedInventory,
            STR("OnUpdateInventoryContainer"),
            STR("/Script/Pal.PalPlayerInventoryData:OnUpdateInventoryContainer"),
            STR("Container"),
            selectedContainer);
        const auto nativeAggregateAfter = ReadNativeItemStackCount(
            selectedContainer,
            payload.itemId);
        const auto aggregateAfter = nativeAggregateAfter
            ? static_cast<std::int64_t>(*nativeAggregateAfter)
            : readDirectAggregate();
        const auto expectedAggregate = aggregateBefore - currentQuantity +
            payload.quantity;
        const bool verified = containerNotified && inventoryNotified &&
            aggregateAfter == expectedAggregate &&
            *mapping.StackCount->ContainerPtrToValuePtr<int32>(selectedSlot) ==
                payload.quantity;
        if (!verified)
        {
            *mapping.StackCount->ContainerPtrToValuePtr<int32>(selectedSlot) =
                currentQuantity;
            InvokeObjectParameterFunction(
                selectedContainer,
                STR("OnUpdateSlotContent"),
                STR("/Script/Pal.PalItemContainer:OnUpdateSlotContent"),
                STR("Slot"),
                selectedSlot);
            InvokeObjectParameterFunction(
                selectedInventory,
                STR("OnUpdateInventoryContainer"),
                STR("/Script/Pal.PalPlayerInventoryData:OnUpdateInventoryContainer"),
                STR("Container"),
                selectedContainer);
            const auto nativeRollbackAggregate = ReadNativeItemStackCount(
                selectedContainer,
                payload.itemId);
            const auto rollbackAggregate = nativeRollbackAggregate
                ? static_cast<std::int64_t>(*nativeRollbackAggregate)
                : readDirectAggregate();
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = rollbackAggregate == aggregateBefore
                    ? Contracts::CommandState::Failed
                    : Contracts::CommandState::Uncertain,
                .ObservedRevision = revisionFor(currentQuantity),
                .ErrorCode = "INVENTORY_NATIVE_SETTLEMENT_VERIFY_FAILED",
                .ErrorMessage = "Native inventory settlement verification failed and the quantity was rolled back."
            };
        }

        const auto data = std::string{"{"} +
            "\"dryRun\":false," +
            "\"applied\":true," +
            "\"settlement\":{" +
                "\"functions\":[\"PalItemContainer.OnUpdateSlotContent\"," +
                "\"PalPlayerInventoryData.OnUpdateInventoryContainer\"]," +
                "\"planned\":false," +
                "\"aggregateVerified\":true}," +
            "\"slot\":{" +
                "\"containerId\":\"" + EscapeJson(payload.containerId) + "\"," +
                "\"containerKind\":\"" + EscapeJson(payload.containerKind) + "\"," +
                "\"slotIndex\":" + std::to_string(payload.slotIndex) + "," +
                "\"itemId\":\"" + EscapeJson(payload.itemId) + "\"," +
                "\"quantity\":" + std::to_string(payload.quantity) + "," +
                "\"previousQuantity\":" + std::to_string(currentQuantity) + "}}";
        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = revisionFor(payload.quantity),
            .DataJson = data
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ConsumeInventory(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        Detail::InventoryConsumePayload payload{};
        const auto parseError = glz::read<glz::opts{
            .error_on_unknown_keys = true,
            .error_on_missing_keys = true}>(payload, command.PayloadJson);
        const auto isFullIdentifier = [](std::string_view value)
        {
            if (IsGuidD(value))
            {
                return true;
            }
            return value.size() == 32 && std::ranges::all_of(value, [](unsigned char character)
            {
                return std::isxdigit(character) != 0;
            });
        };
        const auto isSafeItemId = [](std::string_view value)
        {
            return !value.empty() && value.size() <= 128 && value != "None" &&
                std::ranges::all_of(value, [](unsigned char character)
                {
                    return std::isalnum(character) != 0 || character == '_' ||
                        character == '-';
                });
        };
        if (parseError || !isFullIdentifier(payload.ownerPlayerId) ||
            payload.items.empty() || payload.items.size() > 64 ||
            payload.expectedContainers.size() != 3)
        {
            return Failure(
                command,
                "INVALID_INVENTORY_CONSUME_PAYLOAD",
                "Inventory consume requires one full owner id, 1-64 item lines, and the complete common/dropSlot/food snapshots.");
        }

        std::set<std::string> requestedItemIds;
        std::int64_t requestedTotal = 0;
        for (const auto& item : payload.items)
        {
            if (!isSafeItemId(item.itemId) || item.quantity < 1 ||
                item.quantity > 999999 || !requestedItemIds.insert(item.itemId).second)
            {
                return Failure(
                    command,
                    "INVALID_INVENTORY_CONSUME_ITEM",
                    "Consume item ids must be unique safe identifiers with quantities between 1 and 999999.");
            }
            requestedTotal += item.quantity;
        }
        if (requestedTotal > 16'000'000)
        {
            return Failure(
                command,
                "INVENTORY_CONSUME_TOTAL_OUT_OF_RANGE",
                "The requested aggregate consume quantity is too large.");
        }

        constexpr std::array<std::string_view, 3> RequiredKinds{
            "common", "dropSlot", "food"};
        std::set<std::string> expectedKinds;
        std::set<std::string> expectedContainerIds;
        for (const auto& expected : payload.expectedContainers)
        {
            if (std::ranges::find(RequiredKinds, expected.containerKind) ==
                    RequiredKinds.end() ||
                !expectedKinds.insert(expected.containerKind).second ||
                !isFullIdentifier(expected.containerId) ||
                !expectedContainerIds.insert(
                    NormalizeIdentifier(expected.containerId)).second ||
                expected.slots.size() > MaxSlotsPerContainer)
            {
                return Failure(
                    command,
                    "INVALID_INVENTORY_EXPECTED_CONTAINER",
                    "Expected containers must uniquely describe common, dropSlot, and food using full container ids.");
            }

            std::set<std::int32_t> expectedSlotIds;
            for (const auto& slot : expected.slots)
            {
                const bool emptySlot = slot.itemId == "None" && slot.quantity == 0;
                const bool occupiedSlot = isSafeItemId(slot.itemId) &&
                    slot.quantity >= 1 && slot.quantity <= 999999;
                if (slot.slotIndex < 0 ||
                    !expectedSlotIds.insert(slot.slotIndex).second ||
                    (!emptySlot && !occupiedSlot))
                {
                    return Failure(
                        command,
                        "INVALID_INVENTORY_EXPECTED_SLOT",
                        "Expected slots must be unique and contain either None/0 or a safe item id with a positive quantity.");
                }
            }
        }
        if (expectedKinds.size() != RequiredKinds.size())
        {
            return Failure(
                command,
                "INVENTORY_EXPECTED_CONTAINER_SET_INCOMPLETE",
                "The complete common, dropSlot, and food container set is required.");
        }

        const auto mapping = ResolveInventoryProperties();
        if (!mapping.IsReady())
        {
            return Failure(
                command,
                "INVENTORY_MAPPING_NOT_READY",
                "Inventory reflection mapping is not ready for this game build.");
        }

        const auto findExpectedContainer = [&](std::string_view kind)
            -> const Detail::InventoryExpectedContainerPayload*
        {
            const auto iterator = std::ranges::find_if(
                payload.expectedContainers,
                [&](const auto& expected)
                {
                    return expected.containerKind == kind;
                });
            return iterator == payload.expectedContainers.end()
                ? nullptr
                : &*iterator;
        };

        const auto normalizedOwnerId = NormalizeIdentifier(payload.ownerPlayerId);
        std::vector<UObject*> inventoryObjects;
        UObjectGlobals::FindAllOf(STR("PalPlayerInventoryData"), inventoryObjects);
        UObject* selectedInventory = nullptr;
        bool ownerInventoryLoaded = false;
        for (auto* inventory : inventoryObjects)
        {
            const auto ownerId = ReadTopLevelGuid(mapping.OwnerPlayerUId, inventory);
            if (!ownerId || NormalizeIdentifier(GuidToString(*ownerId)) !=
                    normalizedOwnerId)
            {
                continue;
            }
            ownerInventoryLoaded = true;
            auto* inventoryInfoMemory = mapping.InventoryInfo
                ->ContainerPtrToValuePtr<uint8>(inventory);
            bool containerIdsMatch = true;
            for (const auto& field : mapping.ContainerFields)
            {
                if (std::ranges::find(RequiredKinds, std::string_view{field.Kind}) ==
                    RequiredKinds.end())
                {
                    continue;
                }
                const auto* expected = findExpectedContainer(field.Kind);
                const auto actualId = ReadNestedGuid(field.Property, inventoryInfoMemory);
                if (!expected || !actualId || NormalizeIdentifier(
                        GuidToString(*actualId)) != NormalizeIdentifier(
                        expected->containerId))
                {
                    containerIdsMatch = false;
                    break;
                }
            }
            if (containerIdsMatch)
            {
                selectedInventory = inventory;
                break;
            }
        }
        if (!selectedInventory)
        {
            return Failure(
                command,
                ownerInventoryLoaded
                    ? "INVENTORY_CONTAINER_SNAPSHOT_CONFLICT"
                    : "PLAYER_INVENTORY_NOT_LOADED",
                ownerInventoryLoaded
                    ? "The expected container ids no longer match the player's live inventory; refresh the quote."
                    : "The requested player's inventory is not loaded in the server process.");
        }

        auto* inventoryUpdate = ResolveFunction(
            selectedInventory,
            STR("OnUpdateInventoryContainer"),
            STR("/Script/Pal.PalPlayerInventoryData:OnUpdateInventoryContainer"));
        const auto isExactObjectCallback = [](
            UFunction* function,
            const File::CharType* parameterName,
            UClass* expectedParameterClass,
            std::string_view expectedFullName)
        {
            auto* parameter = FindTypedProperty<FObjectProperty>(
                function,
                parameterName);
            return function && parameter && expectedParameterClass &&
                !function->GetReturnProperty() &&
                function->GetPropertiesSize() == sizeof(UObject*) &&
                CountParameters(function) == 1 &&
                parameter->GetPropertyClass().Get() == expectedParameterClass &&
                to_string(function->GetFullName()) == expectedFullName;
        };
        if (!isExactObjectCallback(
                inventoryUpdate,
                STR("Container"),
                mapping.ContainerClass,
                "Function /Script/Pal.PalPlayerInventoryData:OnUpdateInventoryContainer"))
        {
            return Failure(
                command,
                "INVENTORY_NATIVE_SETTLEMENT_UNAVAILABLE",
                "The native inventory settlement functions are unavailable for this game build.");
        }

        auto* containerManagerClass = UObjectGlobals::StaticFindObject<UClass*>(
            nullptr,
            nullptr,
            STR("/Script/Pal.PalItemContainerManager"));
        if (!containerManagerClass)
        {
            return Failure(
                command,
                "INVENTORY_CONTAINER_MANAGER_UNAVAILABLE",
                "The authoritative item container manager is unavailable for this game build.");
        }
        const auto resolveManagedContainer = [&](
            std::string_view kind,
            std::string_view expectedContainerId) -> UObject*
        {
            const auto field = std::ranges::find_if(
                mapping.ContainerFields,
                [&](const auto& candidate)
                {
                    return candidate.Kind == kind;
                });
            if (field == mapping.ContainerFields.end())
            {
                return nullptr;
            }
            auto* inventoryInfoMemory = mapping.InventoryInfo
                ->ContainerPtrToValuePtr<uint8>(selectedInventory);
            auto* sourceContainerId = field->Property
                ->ContainerPtrToValuePtr<void>(inventoryInfoMemory);
            if (!sourceContainerId)
            {
                return nullptr;
            }

            std::vector<UObject*> managerObjects;
            UObjectGlobals::FindAllOf(
                STR("PalItemContainerManager"),
                managerObjects);
            std::vector<UObject*> resolvedContainers;
            for (auto* manager : managerObjects)
            {
                if (!manager || !manager->IsA(containerManagerClass) ||
                    manager->HasAnyFlags(static_cast<EObjectFlags>(
                        RF_ClassDefaultObject |
                        RF_ArchetypeObject |
                        RF_BeginDestroyed |
                        RF_FinishDestroyed)))
                {
                    continue;
                }
                auto* function = ResolveFunction(
                    manager,
                    STR("TryGetContainer"),
                    STR("/Script/Pal.PalItemContainerManager:TryGetContainer"));
                auto* idParameter = FindTypedProperty<FStructProperty>(
                    function,
                    STR("ContainerId"));
                auto* containerParameter = FindTypedProperty<FObjectProperty>(
                    function,
                    STR("Container"));
                auto* returnParameter = FindTypedProperty<FBoolProperty>(
                    function,
                    STR("ReturnValue"));
                if (!function || !idParameter || !containerParameter ||
                    !returnParameter || !function->GetReturnProperty() ||
                    function->GetReturnProperty() != returnParameter ||
                    CountParameters(function) != 3 ||
                    to_string(function->GetFullName()) !=
                        "Function /Script/Pal.PalItemContainerManager:TryGetContainer" ||
                    idParameter->GetStruct().Get() != field->Property->GetStruct().Get() ||
                    containerParameter->GetPropertyClass().Get() !=
                        mapping.ContainerClass)
                {
                    continue;
                }

                const auto storageCount = std::max<std::size_t>(
                    (function->GetPropertiesSize() + sizeof(std::max_align_t) - 1) /
                        sizeof(std::max_align_t),
                    1);
                std::vector<std::max_align_t> storage(storageCount);
                auto* parameters = storage.data();
                function->InitializeStruct(parameters);
                idParameter->CopyCompleteValue(
                    idParameter->ContainerPtrToValuePtr<void>(parameters),
                    sourceContainerId);
                manager->ProcessEvent(function, parameters);
                const auto succeeded = returnParameter->GetPropertyValue(
                    returnParameter->ContainerPtrToValuePtr<void>(parameters));
                auto* resolved = *containerParameter
                    ->ContainerPtrToValuePtr<UObject*>(parameters);
                function->DestroyStruct(parameters);
                if (!succeeded || !resolved ||
                    !resolved->IsA(mapping.ContainerClass))
                {
                    continue;
                }
                const auto resolvedId = ReadTopLevelGuid(
                    mapping.ContainerId,
                    resolved);
                if (!resolvedId || NormalizeIdentifier(GuidToString(*resolvedId)) !=
                        NormalizeIdentifier(expectedContainerId))
                {
                    continue;
                }
                if (std::ranges::find(resolvedContainers, resolved) ==
                    resolvedContainers.end())
                {
                    resolvedContainers.push_back(resolved);
                }
            }
            return resolvedContainers.size() == 1
                ? resolvedContainers.front()
                : nullptr;
        };

        struct LiveSlot
        {
            UObject* Object{};
            std::int32_t SlotIndex{};
            std::string ItemId;
            std::int32_t Quantity{};
        };
        struct LiveContainer
        {
            const Detail::InventoryExpectedContainerPayload* Expected{};
            UObject* Object{};
            std::vector<LiveSlot> Slots;
            std::uint64_t BeforeRevision{};
        };

        std::vector<LiveContainer> liveContainers;
        liveContainers.reserve(RequiredKinds.size());
        for (const auto kind : RequiredKinds)
        {
            const auto* expected = findExpectedContainer(kind);
            auto* selectedContainer = resolveManagedContainer(
                kind,
                expected->containerId);
            if (!selectedContainer)
            {
                return Failure(
                    command,
                    "INVENTORY_AUTHORITATIVE_CONTAINER_UNAVAILABLE",
                    "The item container manager did not resolve exactly one authoritative expected container.");
            }
            auto* slotUpdate = ResolveFunction(
                selectedContainer,
                STR("OnUpdateSlotContent"),
                STR("/Script/Pal.PalItemContainer:OnUpdateSlotContent"));
            if (!isExactObjectCallback(
                    slotUpdate,
                    STR("Slot"),
                    mapping.SlotClass,
                    "Function /Script/Pal.PalItemContainer:OnUpdateSlotContent"))
            {
                return Failure(
                    command,
                    "INVENTORY_NATIVE_SETTLEMENT_UNAVAILABLE",
                    "The native inventory settlement functions are unavailable for an expected container.");
            }

            const auto* slots = mapping.SlotArray
                ->ContainerPtrToValuePtr<TArray<UObject*>>(selectedContainer);
            if (!slots || slots->Num() < 0 ||
                static_cast<std::size_t>(slots->Num()) > MaxSlotsPerContainer ||
                static_cast<std::size_t>(slots->Num()) != expected->slots.size())
            {
                return Failure(
                    command,
                    "INVENTORY_SNAPSHOT_SLOT_COUNT_CONFLICT",
                    "The live slot count differs from the expected complete snapshot.");
            }

            std::unordered_map<std::int32_t,
                const Detail::InventoryExpectedSlotPayload*> expectedBySlot;
            for (const auto& expectedSlot : expected->slots)
            {
                expectedBySlot.emplace(expectedSlot.slotIndex, &expectedSlot);
            }

            LiveContainer live{
                .Expected = expected,
                .Object = selectedContainer
            };
            live.Slots.reserve(expected->slots.size());
            std::set<std::int32_t> liveSlotIds;
            for (int32 index = 0; index < slots->Num(); ++index)
            {
                auto* slot = (*slots)[index];
                if (!slot || !slot->IsA(mapping.SlotClass))
                {
                    return Failure(
                        command,
                        "INVENTORY_SNAPSHOT_UNREADABLE_SLOT",
                        "A live slot cannot be safely represented by the expected snapshot contract.");
                }
                const auto slotIndex = *mapping.SlotIndex
                    ->ContainerPtrToValuePtr<int32>(slot);
                auto* itemIdMemory = mapping.ItemId
                    ->ContainerPtrToValuePtr<uint8>(slot);
                const auto* staticItemId = mapping.StaticItemId
                    ->ContainerPtrToValuePtr<FName>(itemIdMemory);
                const auto itemId = staticItemId
                    ? to_string(staticItemId->ToString())
                    : std::string{"None"};
                const auto quantity = *mapping.StackCount
                    ->ContainerPtrToValuePtr<int32>(slot);
                const auto expectedSlot = expectedBySlot.find(slotIndex);
                if (!liveSlotIds.insert(slotIndex).second ||
                    expectedSlot == expectedBySlot.end() ||
                    expectedSlot->second->itemId != itemId ||
                    expectedSlot->second->quantity != quantity)
                {
                    return Failure(
                        command,
                        "INVENTORY_SNAPSHOT_CONFLICT",
                        "The inventory changed after it was quoted; refresh before retrying.");
                }
                live.Slots.push_back(LiveSlot{
                    .Object = slot,
                    .SlotIndex = slotIndex,
                    .ItemId = itemId,
                    .Quantity = quantity
                });
            }
            std::sort(live.Slots.begin(), live.Slots.end(), [](const auto& left, const auto& right)
            {
                return left.SlotIndex < right.SlotIndex;
            });
            liveContainers.push_back(std::move(live));
        }

        const auto containerRevision = [&](const LiveContainer& container)
        {
            std::string canonical = container.Expected->containerKind + "|" +
                container.Expected->containerId;
            struct RevisionSlot
            {
                std::int32_t SlotIndex{};
                std::string ItemId;
                std::int32_t Quantity{};
            };
            std::vector<RevisionSlot> observed;
            auto* currentContainer = resolveManagedContainer(
                container.Expected->containerKind,
                container.Expected->containerId);
            if (!currentContainer)
            {
                return std::uint64_t{0};
            }
            const auto* slots = mapping.SlotArray
                ->ContainerPtrToValuePtr<TArray<UObject*>>(currentContainer);
            if (!slots || slots->Num() < 0 ||
                static_cast<std::size_t>(slots->Num()) > MaxSlotsPerContainer)
            {
                return std::uint64_t{0};
            }
            observed.reserve(static_cast<std::size_t>(slots->Num()));
            for (int32 index = 0; index < slots->Num(); ++index)
            {
                auto* slot = (*slots)[index];
                if (!slot || !slot->IsA(mapping.SlotClass))
                {
                    return std::uint64_t{0};
                }
                auto* itemIdMemory = mapping.ItemId
                    ->ContainerPtrToValuePtr<uint8>(slot);
                const auto* staticItemId = mapping.StaticItemId
                    ->ContainerPtrToValuePtr<FName>(itemIdMemory);
                observed.push_back(RevisionSlot{
                    .SlotIndex = *mapping.SlotIndex
                        ->ContainerPtrToValuePtr<int32>(slot),
                    .ItemId = staticItemId
                        ? to_string(staticItemId->ToString())
                        : std::string{"None"},
                    .Quantity = *mapping.StackCount
                        ->ContainerPtrToValuePtr<int32>(slot)
                });
            }
            std::sort(observed.begin(), observed.end(), [](const auto& left, const auto& right)
            {
                return left.SlotIndex < right.SlotIndex;
            });
            for (const auto& slot : observed)
            {
                canonical += "|" + std::to_string(slot.SlotIndex) + ":" +
                    slot.ItemId + ":" + std::to_string(slot.Quantity);
            }
            return StableRevision(canonical);
        };
        for (auto& container : liveContainers)
        {
            container.BeforeRevision = containerRevision(container);
        }
        const auto aggregateRevision = [&]()
        {
            std::string canonical;
            for (const auto& container : liveContainers)
            {
                canonical += "|" + std::to_string(containerRevision(container));
            }
            return StableRevision(canonical);
        };
        const auto beforeRevision = aggregateRevision();

        const auto readDirectTotal = [&](std::string_view itemId)
        {
            std::int64_t total = 0;
            for (const auto& container : liveContainers)
            {
                auto* currentContainer = resolveManagedContainer(
                    container.Expected->containerKind,
                    container.Expected->containerId);
                if (!currentContainer)
                {
                    return std::numeric_limits<std::int64_t>::min();
                }
                const auto* slots = mapping.SlotArray
                    ->ContainerPtrToValuePtr<TArray<UObject*>>(currentContainer);
                if (!slots || slots->Num() < 0 ||
                    static_cast<std::size_t>(slots->Num()) > MaxSlotsPerContainer)
                {
                    return std::numeric_limits<std::int64_t>::min();
                }
                for (int32 index = 0; index < slots->Num(); ++index)
                {
                    auto* slot = (*slots)[index];
                    if (!slot || !slot->IsA(mapping.SlotClass))
                    {
                        return std::numeric_limits<std::int64_t>::min();
                    }
                    auto* itemIdMemory = mapping.ItemId
                        ->ContainerPtrToValuePtr<uint8>(slot);
                    const auto* staticItemId = mapping.StaticItemId
                        ->ContainerPtrToValuePtr<FName>(itemIdMemory);
                    if (staticItemId && to_string(staticItemId->ToString()) == itemId)
                    {
                        total += *mapping.StackCount
                            ->ContainerPtrToValuePtr<int32>(slot);
                    }
                }
            }
            return total;
        };
        const auto readNativeTotal = [&](std::string_view itemId)
            -> std::optional<std::int64_t>
        {
            std::int64_t total = 0;
            for (const auto& container : liveContainers)
            {
                auto* currentContainer = resolveManagedContainer(
                    container.Expected->containerKind,
                    container.Expected->containerId);
                const auto count = ReadNativeItemStackCount(
                    currentContainer,
                    itemId);
                if (!count)
                {
                    return std::nullopt;
                }
                total += *count;
            }
            return total;
        };

        struct ItemEvidence
        {
            const Detail::InventoryConsumeItemPayload* Requested{};
            std::int64_t Before{};
            std::int64_t After{};
            std::int64_t Actual{};
        };
        std::vector<ItemEvidence> itemEvidence;
        itemEvidence.reserve(payload.items.size());
        for (const auto& item : payload.items)
        {
            const auto directBefore = readDirectTotal(item.itemId);
            const auto nativeBefore = readNativeTotal(item.itemId);
            if (!nativeBefore || *nativeBefore != directBefore)
            {
                return Failure(
                    command,
                    "INVENTORY_NATIVE_COUNT_CONFLICT",
                    "The native and reflected inventory totals disagree; no items were changed.");
            }
            if (directBefore < item.quantity)
            {
                return Failure(
                    command,
                    "INVENTORY_CONSUME_INSUFFICIENT_ITEMS",
                    "The complete live snapshot does not contain the requested item quantity.");
            }
            itemEvidence.push_back(ItemEvidence{
                .Requested = &item,
                .Before = directBefore,
                .After = directBefore,
                .Actual = 0
            });
        }

        struct PlannedChange
        {
            std::size_t ContainerIndex{};
            std::size_t SlotVectorIndex{};
            std::int32_t Before{};
            std::int32_t After{};
        };
        std::vector<PlannedChange> changes;
        for (const auto& item : payload.items)
        {
            std::int64_t remaining = item.quantity;
            for (std::size_t containerIndex = 0;
                 containerIndex < liveContainers.size() && remaining > 0;
                 ++containerIndex)
            {
                const auto& container = liveContainers[containerIndex];
                for (std::size_t slotIndex = 0;
                     slotIndex < container.Slots.size() && remaining > 0;
                     ++slotIndex)
                {
                    const auto& slot = container.Slots[slotIndex];
                    if (slot.ItemId != item.itemId || slot.Quantity <= 1)
                    {
                        continue;
                    }
                    const auto removable = static_cast<std::int64_t>(slot.Quantity - 1);
                    const auto take = static_cast<std::int32_t>(std::min(remaining, removable));
                    if (take > 0)
                    {
                        changes.push_back(PlannedChange{
                            .ContainerIndex = containerIndex,
                            .SlotVectorIndex = slotIndex,
                            .Before = slot.Quantity,
                            .After = slot.Quantity - take
                        });
                        remaining -= take;
                    }
                }
            }
            if (remaining > 0)
            {
                return Failure(
                    command,
                    "INVENTORY_SLOT_CLEAR_UNSUPPORTED",
                    "This safe build cannot consume the last item in a slot because a verified native slot-clear function is unavailable; no items were changed.");
            }
        }
        if (changes.size() > 128)
        {
            return Failure(
                command,
                "INVENTORY_CONSUME_PLAN_TOO_LARGE",
                "The consume plan would update more than 128 slots in one game tick; split it into a new server-authored quote.");
        }

        const auto currentContainerFor = [&](std::size_t containerIndex)
            -> UObject*
        {
            const auto& container = liveContainers[containerIndex];
            return resolveManagedContainer(
                container.Expected->containerKind,
                container.Expected->containerId);
        };
        const auto findCurrentSlot = [&](UObject* container, std::int32_t slotIndex)
            -> UObject*
        {
            if (!container || !container->IsA(mapping.ContainerClass))
            {
                return nullptr;
            }
            const auto* slots = mapping.SlotArray
                ->ContainerPtrToValuePtr<TArray<UObject*>>(container);
            if (!slots || slots->Num() < 0 ||
                static_cast<std::size_t>(slots->Num()) > MaxSlotsPerContainer)
            {
                return nullptr;
            }
            for (int32 index = 0; index < slots->Num(); ++index)
            {
                auto* slot = (*slots)[index];
                if (slot && slot->IsA(mapping.SlotClass) &&
                    *mapping.SlotIndex->ContainerPtrToValuePtr<int32>(slot) ==
                        slotIndex)
                {
                    return slot;
                }
            }
            return nullptr;
        };
        const auto inventoryAssociationMatches = [&]()
        {
            const auto ownerId = ReadTopLevelGuid(
                mapping.OwnerPlayerUId,
                selectedInventory);
            if (!ownerId || NormalizeIdentifier(GuidToString(*ownerId)) !=
                    normalizedOwnerId)
            {
                return false;
            }
            auto* inventoryInfoMemory = mapping.InventoryInfo
                ->ContainerPtrToValuePtr<uint8>(selectedInventory);
            for (const auto kind : RequiredKinds)
            {
                const auto field = std::ranges::find_if(
                    mapping.ContainerFields,
                    [&](const auto& candidate)
                    {
                        return candidate.Kind == kind;
                    });
                const auto* expected = findExpectedContainer(kind);
                const auto actualId = field == mapping.ContainerFields.end()
                    ? std::optional<FGuid>{}
                    : ReadNestedGuid(field->Property, inventoryInfoMemory);
                if (!expected || !actualId || NormalizeIdentifier(
                        GuidToString(*actualId)) != NormalizeIdentifier(
                        expected->containerId))
                {
                    return false;
                }
            }
            return true;
        };
        const auto completeStateMatches = [&](bool expectApplied)
        {
            if (!inventoryAssociationMatches())
            {
                return false;
            }
            for (std::size_t containerIndex = 0;
                 containerIndex < liveContainers.size();
                 ++containerIndex)
            {
                const auto& original = liveContainers[containerIndex];
                auto* currentContainer = currentContainerFor(containerIndex);
                if (!currentContainer)
                {
                    return false;
                }
                const auto currentId = ReadTopLevelGuid(
                    mapping.ContainerId,
                    currentContainer);
                const auto* currentSlots = mapping.SlotArray
                    ->ContainerPtrToValuePtr<TArray<UObject*>>(currentContainer);
                if (!currentId || NormalizeIdentifier(GuidToString(*currentId)) !=
                        NormalizeIdentifier(original.Expected->containerId) ||
                    !currentSlots || currentSlots->Num() < 0 ||
                    static_cast<std::size_t>(currentSlots->Num()) !=
                        original.Slots.size())
                {
                    return false;
                }
                std::set<std::int32_t> observedSlotIds;
                for (int32 arrayIndex = 0;
                     arrayIndex < currentSlots->Num();
                     ++arrayIndex)
                {
                    auto* currentSlot = (*currentSlots)[arrayIndex];
                    if (!currentSlot || !currentSlot->IsA(mapping.SlotClass))
                    {
                        return false;
                    }
                    const auto slotIndex = *mapping.SlotIndex
                        ->ContainerPtrToValuePtr<int32>(currentSlot);
                    if (!observedSlotIds.insert(slotIndex).second)
                    {
                        return false;
                    }
                    const auto originalSlot = std::ranges::find_if(
                        original.Slots,
                        [&](const auto& slot)
                        {
                            return slot.SlotIndex == slotIndex;
                        });
                    if (originalSlot == original.Slots.end())
                    {
                        return false;
                    }
                    const auto slotVectorIndex = static_cast<std::size_t>(
                        std::distance(original.Slots.begin(), originalSlot));
                    const auto planned = std::ranges::find_if(
                        changes,
                        [&](const auto& change)
                        {
                            return change.ContainerIndex == containerIndex &&
                                change.SlotVectorIndex == slotVectorIndex;
                        });
                    const auto expectedQuantity = expectApplied &&
                        planned != changes.end()
                        ? planned->After
                        : originalSlot->Quantity;
                    auto* itemIdMemory = mapping.ItemId
                        ->ContainerPtrToValuePtr<uint8>(currentSlot);
                    const auto* staticItemId = mapping.StaticItemId
                        ->ContainerPtrToValuePtr<FName>(itemIdMemory);
                    if (!staticItemId ||
                        to_string(staticItemId->ToString()) !=
                            originalSlot->ItemId ||
                        *mapping.StackCount->ContainerPtrToValuePtr<int32>(
                            currentSlot) != expectedQuantity)
                    {
                        return false;
                    }
                }
            }
            return true;
        };

        std::set<std::size_t> changedContainers;
        for (const auto& change : changes)
        {
            auto& container = liveContainers[change.ContainerIndex];
            auto& slot = container.Slots[change.SlotVectorIndex];
            *mapping.StackCount->ContainerPtrToValuePtr<int32>(slot.Object) =
                change.After;
            changedContainers.insert(change.ContainerIndex);
        }

        bool callbacksSucceeded = true;
        for (const auto& change : changes)
        {
            const auto& originalSlot = liveContainers[change.ContainerIndex]
                .Slots[change.SlotVectorIndex];
            auto* currentContainer = currentContainerFor(change.ContainerIndex);
            auto* currentSlot = findCurrentSlot(
                currentContainer,
                originalSlot.SlotIndex);
            auto* itemIdMemory = currentSlot
                ? mapping.ItemId->ContainerPtrToValuePtr<uint8>(currentSlot)
                : nullptr;
            const auto* staticItemId = itemIdMemory
                ? mapping.StaticItemId->ContainerPtrToValuePtr<FName>(itemIdMemory)
                : nullptr;
            const bool currentSlotMatches = currentSlot && staticItemId &&
                to_string(staticItemId->ToString()) == originalSlot.ItemId &&
                *mapping.StackCount->ContainerPtrToValuePtr<int32>(currentSlot) ==
                    change.After;
            callbacksSucceeded = currentSlotMatches &&
                InvokeObjectParameterFunction(
                    currentContainer,
                    STR("OnUpdateSlotContent"),
                    STR("/Script/Pal.PalItemContainer:OnUpdateSlotContent"),
                    STR("Slot"),
                    currentSlot) && callbacksSucceeded;
        }
        for (const auto containerIndex : changedContainers)
        {
            auto* currentContainer = currentContainerFor(containerIndex);
            callbacksSucceeded = currentContainer && InvokeObjectParameterFunction(
                    selectedInventory,
                    STR("OnUpdateInventoryContainer"),
                    STR("/Script/Pal.PalPlayerInventoryData:OnUpdateInventoryContainer"),
                    STR("Container"),
                    currentContainer) && callbacksSucceeded;
        }

        const bool quantitiesVerified = completeStateMatches(true);
        bool aggregatesVerified = true;
        for (auto& evidence : itemEvidence)
        {
            const auto directAfter = readDirectTotal(evidence.Requested->itemId);
            const auto nativeAfter = readNativeTotal(evidence.Requested->itemId);
            if (!nativeAfter ||
                directAfter == std::numeric_limits<std::int64_t>::min())
            {
                evidence.After = directAfter;
                evidence.Actual = 0;
                aggregatesVerified = false;
                continue;
            }
            evidence.After = *nativeAfter;
            evidence.Actual = evidence.Before - *nativeAfter;
            aggregatesVerified = aggregatesVerified &&
                *nativeAfter == directAfter &&
                evidence.Actual == evidence.Requested->quantity;
        }

        const bool verified = callbacksSucceeded && quantitiesVerified &&
            aggregatesVerified;
        if (!verified)
        {
            bool rollbackWriteSafe = true;
            for (const auto& change : changes)
            {
                const auto& originalSlot = liveContainers[change.ContainerIndex]
                    .Slots[change.SlotVectorIndex];
                auto* currentContainer = currentContainerFor(change.ContainerIndex);
                auto* currentSlot = findCurrentSlot(
                    currentContainer,
                    originalSlot.SlotIndex);
                auto* itemIdMemory = currentSlot
                    ? mapping.ItemId->ContainerPtrToValuePtr<uint8>(currentSlot)
                    : nullptr;
                const auto* staticItemId = itemIdMemory
                    ? mapping.StaticItemId->ContainerPtrToValuePtr<FName>(itemIdMemory)
                    : nullptr;
                if (!currentSlot || !staticItemId ||
                    to_string(staticItemId->ToString()) != originalSlot.ItemId)
                {
                    rollbackWriteSafe = false;
                    continue;
                }
                auto* currentQuantity = mapping.StackCount
                    ->ContainerPtrToValuePtr<int32>(currentSlot);
                if (*currentQuantity == change.After)
                {
                    *currentQuantity = change.Before;
                }
                else if (*currentQuantity != change.Before)
                {
                    rollbackWriteSafe = false;
                }
            }
            bool rollbackCallbacksSucceeded = true;
            for (const auto& change : changes)
            {
                const auto& originalSlot = liveContainers[change.ContainerIndex]
                    .Slots[change.SlotVectorIndex];
                auto* currentContainer = currentContainerFor(change.ContainerIndex);
                auto* currentSlot = findCurrentSlot(
                    currentContainer,
                    originalSlot.SlotIndex);
                const bool quantityRestored = currentSlot &&
                    *mapping.StackCount->ContainerPtrToValuePtr<int32>(currentSlot) ==
                        change.Before;
                rollbackCallbacksSucceeded = quantityRestored &&
                    InvokeObjectParameterFunction(
                        currentContainer,
                        STR("OnUpdateSlotContent"),
                        STR("/Script/Pal.PalItemContainer:OnUpdateSlotContent"),
                        STR("Slot"),
                        currentSlot) && rollbackCallbacksSucceeded;
            }
            for (const auto containerIndex : changedContainers)
            {
                auto* currentContainer = currentContainerFor(containerIndex);
                rollbackCallbacksSucceeded = currentContainer &&
                    InvokeObjectParameterFunction(
                        selectedInventory,
                        STR("OnUpdateInventoryContainer"),
                        STR("/Script/Pal.PalPlayerInventoryData:OnUpdateInventoryContainer"),
                        STR("Container"),
                        currentContainer) &&
                    rollbackCallbacksSucceeded;
            }

            bool rollbackVerified = rollbackWriteSafe &&
                rollbackCallbacksSucceeded && completeStateMatches(false);
            for (const auto& evidence : itemEvidence)
            {
                const auto directRollback = readDirectTotal(evidence.Requested->itemId);
                const auto nativeRollback = readNativeTotal(evidence.Requested->itemId);
                rollbackVerified = rollbackVerified && nativeRollback &&
                    directRollback == evidence.Before &&
                    *nativeRollback == evidence.Before;
            }

            std::string evidenceJson{"["};
            for (std::size_t index = 0; index < itemEvidence.size(); ++index)
            {
                if (index > 0)
                {
                    evidenceJson += ',';
                }
                const auto& evidence = itemEvidence[index];
                const auto rollbackTotal = readDirectTotal(evidence.Requested->itemId);
                evidenceJson += std::string{"{"} +
                    "\"itemId\":\"" + EscapeJson(evidence.Requested->itemId) + "\"," +
                    "\"requestedQuantity\":" + std::to_string(evidence.Requested->quantity) + "," +
                    "\"beforeQuantity\":" + std::to_string(evidence.Before) + "," +
                    "\"afterQuantity\":" + std::to_string(evidence.After) + "," +
                    "\"actualConsumed\":" + std::to_string(evidence.Actual) + "," +
                    "\"rollbackQuantity\":" + std::to_string(rollbackTotal) + "}";
            }
            evidenceJson += ']';
            const auto data = std::string{"{"} +
                "\"executionThread\":\"unreal-engine-tick\"," +
                "\"applied\":false," +
                "\"snapshotMatched\":true," +
                "\"slotClearSupported\":false," +
                "\"persistenceVerified\":false," +
                "\"beforeRevision\":" + std::to_string(beforeRevision) + "," +
                "\"observedRevision\":" + std::to_string(aggregateRevision()) + "," +
                "\"items\":" + evidenceJson + "," +
                "\"rollback\":{\"attempted\":true,\"verified\":" +
                    (rollbackVerified ? "true" : "false") + "}}";
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = rollbackVerified
                    ? Contracts::CommandState::Failed
                    : Contracts::CommandState::Uncertain,
                .ObservedRevision = aggregateRevision(),
                .DataJson = data,
                .ErrorCode = "INVENTORY_CONSUME_VERIFY_FAILED",
                .ErrorMessage = rollbackVerified
                    ? "Atomic consume verification failed; all reflected and native totals were restored."
                    : "Atomic consume verification failed and rollback could not be proven; do not retry automatically."
            };
        }

        std::string itemsJson{"["};
        for (std::size_t index = 0; index < itemEvidence.size(); ++index)
        {
            if (index > 0)
            {
                itemsJson += ',';
            }
            const auto& evidence = itemEvidence[index];
            itemsJson += std::string{"{"} +
                "\"itemId\":\"" + EscapeJson(evidence.Requested->itemId) + "\"," +
                "\"requestedQuantity\":" + std::to_string(evidence.Requested->quantity) + "," +
                "\"beforeQuantity\":" + std::to_string(evidence.Before) + "," +
                "\"afterQuantity\":" + std::to_string(evidence.After) + "," +
                "\"actualConsumed\":" + std::to_string(evidence.Actual) + "}";
        }
        itemsJson += ']';

        std::string containersJson{"["};
        for (std::size_t containerIndex = 0;
             containerIndex < liveContainers.size();
             ++containerIndex)
        {
            if (containerIndex > 0)
            {
                containersJson += ',';
            }
            const auto& container = liveContainers[containerIndex];
            std::string changesJson{"["};
            std::size_t returnedChanges = 0;
            for (const auto& change : changes)
            {
                if (change.ContainerIndex != containerIndex)
                {
                    continue;
                }
                if (returnedChanges++ > 0)
                {
                    changesJson += ',';
                }
                const auto& slot = container.Slots[change.SlotVectorIndex];
                changesJson += std::string{"{"} +
                    "\"slotIndex\":" + std::to_string(slot.SlotIndex) + "," +
                    "\"itemId\":\"" + EscapeJson(slot.ItemId) + "\"," +
                    "\"beforeQuantity\":" + std::to_string(change.Before) + "," +
                    "\"afterQuantity\":" + std::to_string(change.After) + "}";
            }
            changesJson += ']';
            containersJson += std::string{"{"} +
                "\"containerKind\":\"" + EscapeJson(container.Expected->containerKind) + "\"," +
                "\"containerId\":\"" + EscapeJson(container.Expected->containerId) + "\"," +
                "\"beforeRevision\":" + std::to_string(container.BeforeRevision) + "," +
                "\"afterRevision\":" + std::to_string(containerRevision(container)) + "," +
                "\"changes\":" + changesJson + "}";
        }
        containersJson += ']';
        const auto afterRevision = aggregateRevision();
        const auto data = std::string{"{"} +
            "\"executionThread\":\"unreal-engine-tick\"," +
            "\"applied\":true," +
            "\"snapshotMatched\":true," +
            "\"slotClearSupported\":false," +
            "\"persistenceVerified\":false," +
            "\"beforeRevision\":" + std::to_string(beforeRevision) + "," +
            "\"observedRevision\":" + std::to_string(afterRevision) + "," +
            "\"items\":" + itemsJson + "," +
            "\"containers\":" + containersJson + "," +
            "\"settlement\":{\"atomicWithinCommand\":true," +
                "\"liveAggregateVerified\":true," +
                "\"authorityResolvedBy\":\"PalItemContainerManager.TryGetContainer\"," +
                "\"functions\":[\"PalItemContainer.OnUpdateSlotContent\"," +
                "\"PalPlayerInventoryData.OnUpdateInventoryContainer\"]}}";
        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = afterRevision,
            .DataJson = data
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ProbePals(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        const auto mapping = ResolvePalProperties();
        std::vector<UObject*> parameterObjects;
        UObjectGlobals::FindAllOf(
            STR("PalIndividualCharacterParameter"),
            parameterObjects);

        std::string palsJson{"["};
        std::size_t returnedCount = 0;
        const auto candidateCount = std::min(
            parameterObjects.size(),
            MaxReturnedPals);
        for (std::size_t index = 0; index < candidateCount; ++index)
        {
            const auto pal = ReadPalSnapshot(mapping, parameterObjects[index]);
            if (!pal)
            {
                continue;
            }
            if (returnedCount > 0)
            {
                palsJson += ',';
            }
            palsJson += pal->Json;
            ++returnedCount;
        }
        palsJson += ']';

        const auto data = std::string{"{"} +
            "\"observedAt\":\"" + UtcNow() + "\"," +
            "\"executionThread\":\"unreal-engine-tick\"," +
            "\"mappingReady\":" + (mapping.IsReady() ? "true" : "false") + "," +
            "\"parameterObjectCount\":" + std::to_string(parameterObjects.size()) + "," +
            "\"palCount\":" + std::to_string(returnedCount) + "," +
            "\"truncated\":" +
                (parameterObjects.size() > candidateCount ? "true" : "false") + "," +
            "\"pals\":" + palsJson + "}";

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = data
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ReadPalSkillCatalog(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        const auto mapping = ResolvePalProperties();
        if (!mapping.IsReady() || !mapping.EquipWaza->GetInner() ||
            !mapping.EquipWaza->GetInner()->IsA<FEnumProperty>())
        {
            return Failure(
                command,
                "PAL_SKILL_CATALOG_MAPPING_NOT_READY",
                "Pal skill catalog reflection mapping is not ready.");
        }

        auto* activeEnumProperty = static_cast<FEnumProperty*>(
            mapping.EquipWaza->GetInner());
        auto* activeEnum = activeEnumProperty->GetEnum().Get();
        if (!activeEnum)
        {
            return Failure(
                command,
                "PAL_ACTIVE_SKILL_ENUM_NOT_READY",
                "The active skill enum is not loaded.");
        }

        const auto localizedTexts = ReadLocalizedSkillTexts();
        std::vector<std::pair<FName, int64>> activeNames;
        activeEnum->GetEnumNamesAsVector(activeNames);
        std::string activeJson{"["};
        std::size_t activeCount = 0;
        for (const auto& [enumName, enumValue] : activeNames)
        {
            if (activeCount >= MaxReturnedSkillCatalogEntries)
            {
                break;
            }
            const auto id = ShortEnumName(to_string(enumName.ToString()));
            std::string lowered{id};
            std::transform(lowered.begin(), lowered.end(), lowered.begin(),
                [](unsigned char value) {
                    return static_cast<char>(std::tolower(value));
                });
            if (id.empty() || lowered == "none" || lowered.ends_with("_max") ||
                lowered == "max")
            {
                continue;
            }
            if (activeCount > 0)
            {
                activeJson += ',';
            }
            const auto textKey = "ACTION_SKILL_" + id;
            const auto localizedName = FindLocalizedText(localizedTexts.Names, textKey);
            const auto description = FindLocalizedText(localizedTexts.Descriptions, textKey);
            activeJson += std::string{"{"} +
                "\"id\":\"" + EscapeJson(id) + "\"," +
                "\"value\":" + std::to_string(enumValue) + "," +
                "\"name\":\"" + EscapeJson(IsUsableLocalizedText(localizedName)
                    ? localizedName : id) + "\"," +
                "\"description\":\"" + EscapeJson(description) + "\"," +
                "\"localized\":" + (IsUsableLocalizedText(localizedName)
                    ? "true" : "false") + "}";
            ++activeCount;
        }
        activeJson += ']';

        std::set<std::string> passiveSources;
        ReadPassiveSkillCatalogIds(&passiveSources);
        const auto passiveCatalog = ReadPassiveSkillCatalog(localizedTexts);

        std::string sourcesJson{"["};
        std::size_t sourceIndex = 0;
        for (const auto& source : passiveSources)
        {
            if (sourceIndex++ > 0)
            {
                sourcesJson += ',';
            }
            sourcesJson += "\"" + EscapeJson(source) + "\"";
        }
        sourcesJson += ']';

        const auto data = std::string{"{"} +
            "\"observedAt\":\"" + UtcNow() + "\"," +
            "\"executionThread\":\"unreal-engine-tick\"," +
            "\"locale\":\"zh-Hans\"," +
            "\"activeEnum\":\"" + EscapeJson(to_string(activeEnum->GetName())) + "\"," +
            "\"activeSkillCount\":" + std::to_string(activeCount) + "," +
            "\"activeSkills\":" + activeJson + "," +
            "\"passiveSkillCount\":" + std::to_string(passiveCatalog.Count) + "," +
            "\"localizedPassiveSkillCount\":" +
                std::to_string(passiveCatalog.LocalizedCount) + "," +
            "\"obtainablePassiveSkillCount\":" +
                std::to_string(passiveCatalog.ObtainableCount) + "," +
            "\"passiveSkills\":" + passiveCatalog.Json + "," +
            "\"passiveSources\":" + sourcesJson + "," +
            "\"catalogRevision\":\"" + std::to_string(StableRevision(
                std::to_string(activeCount) + "|" +
                std::to_string(passiveCatalog.Count) + "|zh-Hans")) + "\"}";
        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = data
        };
    }

    Contracts::CommandResult PalworldGameAdapter::MutatePal(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        Detail::PalMutationPayload payload{};
        const auto parseError = glz::read<glz::opts{
            .error_on_unknown_keys = true,
            .error_on_missing_keys = false}>(payload, command.PayloadJson);
        if (parseError || payload.instanceId.empty() || payload.ownerPlayerId.empty())
        {
            return Failure(
                command,
                "INVALID_PAL_MUTATION_PAYLOAD",
                "Pal mutation payload is invalid or incomplete.");
        }
        const bool anyPassiveField = payload.passiveSkillIndex ||
            payload.expectedPassiveSkill || payload.passiveSkill;
        const bool passiveMutation = payload.passiveSkillIndex &&
            payload.expectedPassiveSkill && payload.passiveSkill;
        const bool anyPassiveSetField = payload.expectedPassiveSkills ||
            payload.passiveSkills;
        const bool passiveSetMutation = payload.expectedPassiveSkills &&
            payload.passiveSkills;
        const bool activeMutation = payload.equippedActiveSkills.has_value();
        if (anyPassiveField && !passiveMutation)
        {
            return Failure(
                command,
                "INCOMPLETE_PASSIVE_SKILL_MUTATION",
                "Passive skill replacement requires index, expected skill and new skill.");
        }
        if (anyPassiveSetField && !passiveSetMutation)
        {
            return Failure(
                command,
                "INCOMPLETE_PASSIVE_SKILL_SET_MUTATION",
                "Passive skill set replacement requires expected and desired lists.");
        }
        if (passiveMutation && passiveSetMutation)
        {
            return Failure(
                command,
                "AMBIGUOUS_PASSIVE_SKILL_MUTATION",
                "Use either a single passive replacement or a full passive set replacement.");
        }
        if (!payload.nickname && !payload.favorite && !passiveMutation &&
            !passiveSetMutation && !activeMutation)
        {
            return Failure(
                command,
                "EMPTY_PAL_PATCH",
                "At least one supported Pal field must be supplied.");
        }
        if (payload.nickname && payload.nickname->size() > 96)
        {
            return Failure(
                command,
                "PAL_NICKNAME_TOO_LONG",
                "Pal nickname exceeds the UTF-8 safety limit.");
        }
        if (payload.passiveSkill &&
            (payload.passiveSkill->empty() || payload.passiveSkill->size() > 96))
        {
            return Failure(
                command,
                "INVALID_PASSIVE_SKILL_ID",
                "Passive skill ID is empty or exceeds the safety limit.");
        }
        if (payload.equippedActiveSkills &&
            (payload.equippedActiveSkills->empty() ||
             payload.equippedActiveSkills->size() > 3))
        {
            return Failure(
                command,
                "INVALID_ACTIVE_SKILL_LOADOUT",
                "Equipped active skills must contain between one and three entries.");
        }
        if (payload.passiveSkills &&
            (payload.passiveSkills->empty() || payload.passiveSkills->size() > 4))
        {
            return Failure(
                command,
                "INVALID_PASSIVE_SKILL_SET",
                "Passive skill set must contain between one and four entries.");
        }

        const auto mapping = ResolvePalProperties();
        if (!mapping.IsReady())
        {
            return Failure(
                command,
                "PAL_MAPPING_NOT_READY",
                "Pal reflection mapping is not ready for this game build.");
        }

        const auto normalizedInstanceId = NormalizeIdentifier(payload.instanceId);
        const auto normalizedPlayerId = NormalizeIdentifier(payload.ownerPlayerId);
        std::vector<UObject*> parameterObjects;
        UObjectGlobals::FindAllOf(
            STR("PalIndividualCharacterParameter"),
            parameterObjects);

        UObject* selectedObject = nullptr;
        PalSnapshot currentPal{};
        for (auto* object : parameterObjects)
        {
            const auto pal = ReadPalSnapshot(mapping, object);
            if (!pal || NormalizeIdentifier(pal->InstanceId) != normalizedInstanceId)
            {
                continue;
            }
            const auto normalizedOwner = NormalizeIdentifier(pal->OwnerPlayerUId);
            const auto ownerMatches = normalizedOwner == normalizedPlayerId ||
                (normalizedPlayerId.size() == 8 &&
                 normalizedOwner.starts_with(normalizedPlayerId));
            if (!ownerMatches)
            {
                return Failure(
                    command,
                    "PAL_OWNER_MISMATCH",
                    "The selected Pal does not belong to the requested player.");
            }
            selectedObject = object;
            currentPal = *pal;
            break;
        }

        if (!selectedObject)
        {
            return Failure(
                command,
                "PAL_NOT_LOADED",
                "The selected Pal is not loaded in the current server process.");
        }
        if (command.ExpectedRevision != currentPal.Revision)
        {
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = Contracts::CommandState::Failed,
                .ObservedRevision = currentPal.Revision,
                .ErrorCode = "REVISION_CONFLICT",
                .ErrorMessage = "The Pal changed after it was read; refresh before retrying."
            };
        }

        auto requestedPassiveSkills = currentPal.PassiveSkills;
        if (passiveMutation)
        {
            const auto index = static_cast<std::size_t>(*payload.passiveSkillIndex);
            if (*payload.passiveSkillIndex < 0 ||
                index >= currentPal.PassiveSkills.size())
            {
                return Failure(
                    command,
                    "PASSIVE_SKILL_INDEX_OUT_OF_RANGE",
                    "The requested passive skill slot does not exist.");
            }
            if (currentPal.PassiveSkills[index] != *payload.expectedPassiveSkill)
            {
                return Failure(
                    command,
                    "PASSIVE_SKILL_CONFLICT",
                    "The passive skill changed after it was read; refresh before retrying.");
            }
            if (*payload.passiveSkill == *payload.expectedPassiveSkill)
            {
                return Failure(
                    command,
                    "EMPTY_PASSIVE_SKILL_PATCH",
                    "The requested passive skill is already applied.");
            }
            const auto passiveCatalog = ReadPassiveSkillCatalogIds();
            if (!passiveCatalog.contains(*payload.passiveSkill))
            {
                return Failure(
                    command,
                    "PASSIVE_SKILL_NOT_IN_CATALOG",
                    "The requested passive skill is not present in the loaded game catalog.");
            }
            if (std::ranges::find(currentPal.PassiveSkills, *payload.passiveSkill) !=
                currentPal.PassiveSkills.end())
            {
                return Failure(
                    command,
                    "DUPLICATE_PASSIVE_SKILL",
                    "The requested passive skill is already assigned to this Pal.");
            }
            requestedPassiveSkills[index] = *payload.passiveSkill;
        }
        if (passiveSetMutation)
        {
            if (*payload.expectedPassiveSkills != currentPal.PassiveSkills)
            {
                return Failure(
                    command,
                    "PASSIVE_SKILL_SET_CONFLICT",
                    "The passive skill list changed after it was read; refresh before retrying.");
            }
            if (*payload.passiveSkills == currentPal.PassiveSkills)
            {
                return Failure(
                    command,
                    "EMPTY_PASSIVE_SKILL_SET_PATCH",
                    "The requested passive skill list is already applied.");
            }
            const auto passiveCatalog = ReadPassiveSkillCatalogIds();
            std::set<std::string> uniqueSkills;
            for (const auto& skillId : *payload.passiveSkills)
            {
                if (!passiveCatalog.contains(skillId))
                {
                    return Failure(
                        command,
                        "PASSIVE_SKILL_NOT_IN_CATALOG",
                        "A requested passive skill is not present in the loaded game catalog.");
                }
                if (!uniqueSkills.insert(skillId).second)
                {
                    return Failure(
                        command,
                        "DUPLICATE_PASSIVE_SKILL",
                        "The requested passive skill list contains duplicates.");
                }
            }
            requestedPassiveSkills = *payload.passiveSkills;
        }

        std::vector<std::int64_t> requestedActiveSkillValues;
        if (activeMutation)
        {
            if (*payload.equippedActiveSkills == currentPal.EquippedActiveSkills)
            {
                return Failure(
                    command,
                    "EMPTY_ACTIVE_SKILL_PATCH",
                    "The requested active skill loadout is already equipped.");
            }
            std::set<std::string> allowedActiveSkills(
                currentPal.MasteredActiveSkills.begin(),
                currentPal.MasteredActiveSkills.end());
            allowedActiveSkills.insert(
                currentPal.EquippedActiveSkills.begin(),
                currentPal.EquippedActiveSkills.end());
            std::set<std::string> requestedSkillIds;
            for (const auto& skillId : *payload.equippedActiveSkills)
            {
                if (!requestedSkillIds.insert(skillId).second)
                {
                    return Failure(
                        command,
                        "DUPLICATE_ACTIVE_SKILL",
                        "The active skill loadout contains duplicate entries.");
                }
                if (!allowedActiveSkills.contains(skillId))
                {
                    return Failure(
                        command,
                        "ACTIVE_SKILL_NOT_MASTERED",
                        "Only currently equipped or mastered active skills can be equipped.");
                }
                const auto value = ResolveActiveSkillValue(
                    mapping.EquipWaza,
                    skillId);
                if (!value)
                {
                    return Failure(
                        command,
                        "ACTIVE_SKILL_NOT_IN_CATALOG",
                        "The requested active skill is not present in EPalWazaID.");
                }
                requestedActiveSkillValues.push_back(*value);
            }
        }

        if (payload.dryRun)
        {
            const auto settlementFunction = passiveMutation || passiveSetMutation
                ? "RemovePassiveSkill/AddPassiveSkill"
                : activeMutation
                    ? "ClearEquipWaza/AddEquipWaza"
                    : "OnRep_SaveParameter";
            const auto data = std::string{"{"} +
                "\"dryRun\":true," +
                "\"applied\":false," +
                "\"settlement\":{" +
                    "\"function\":\"" + settlementFunction + "\"," +
                    "\"planned\":true," +
                    "\"mirrorSynchronized\":false," +
                    "\"readBackVerified\":false}," +
                "\"pal\":" + currentPal.Json + "}";
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = Contracts::CommandState::Succeeded,
                .ObservedRevision = currentPal.Revision,
                .DataJson = data
            };
        }

        auto* saveMemory = mapping.SaveParameter
            ->ContainerPtrToValuePtr<uint8>(selectedObject);
        auto* mirrorMemory = mapping.SaveParameterMirror
            ? mapping.SaveParameterMirror->ContainerPtrToValuePtr<uint8>(selectedObject)
            : nullptr;

        auto* onRep = selectedObject->GetFunctionByName(
            FName(STR("OnRep_SaveParameter")));
        if (!mirrorMemory || !onRep)
        {
            return Failure(
                command,
                "PAL_NATIVE_SETTLEMENT_UNAVAILABLE",
                "The native SaveParameter settlement chain is unavailable for this game build.");
        }

        const auto* originalNicknameValue = mapping.NickName
            ->ContainerPtrToValuePtr<FString>(saveMemory);
        const auto originalNickname = originalNicknameValue
            ? FString(**originalNicknameValue)
            : FString(STR(""));
        const auto originalFavorite = mapping.IsFavoritePal
            ->GetPropertyValueInContainer(saveMemory);
        const auto originalEquippedActiveSkills = currentPal.EquippedActiveSkills;

        const auto applyPatch = [&](void* target)
        {
            if (!target)
            {
                return;
            }
            if (payload.nickname)
            {
                const auto wideNickname = to_wstring(*payload.nickname);
                const FString nicknameValue(wideNickname.c_str());
                *mapping.NickName->ContainerPtrToValuePtr<FString>(target) = nicknameValue;
                if (mapping.FilteredNickName)
                {
                    *mapping.FilteredNickName->ContainerPtrToValuePtr<FString>(target) =
                        nicknameValue;
                }
            }
            if (payload.favorite)
            {
                mapping.IsFavoritePal->SetPropertyValueInContainer(
                    target,
                    *payload.favorite);
            }
        };
        applyPatch(saveMemory);

        bool skillFunctionsApplied = true;
        if (passiveMutation || passiveSetMutation)
        {
            std::set<std::string> currentUniqueSkills(
                currentPal.PassiveSkills.begin(),
                currentPal.PassiveSkills.end());
            for (const auto& skillId : currentUniqueSkills)
            {
                skillFunctionsApplied = skillFunctionsApplied && InvokeNameFunction(
                    selectedObject,
                    STR("RemovePassiveSkill"),
                    STR("/Script/Pal.PalIndividualCharacterParameter:RemovePassiveSkill"),
                    STR("SkillId"),
                    skillId);
            }
            for (const auto& skillId : requestedPassiveSkills)
            {
                skillFunctionsApplied = skillFunctionsApplied && InvokeNamePairFunction(
                    selectedObject,
                    STR("AddPassiveSkill"),
                    STR("/Script/Pal.PalIndividualCharacterParameter:AddPassiveSkill"),
                    STR("AddSkill"),
                    skillId,
                    STR("OverrideSkill"),
                    "None");
            }
            skillFunctionsApplied = skillFunctionsApplied && WriteNameArray(
                mapping.PassiveSkillList,
                saveMemory,
                requestedPassiveSkills);
        }
        if (activeMutation)
        {
            skillFunctionsApplied = skillFunctionsApplied &&
                InvokeNoParameterFunction(
                    selectedObject,
                    STR("ClearEquipWaza"),
                    STR("/Script/Pal.PalIndividualCharacterParameter:ClearEquipWaza"));
            for (const auto value : requestedActiveSkillValues)
            {
                skillFunctionsApplied = skillFunctionsApplied &&
                    InvokeActiveSkillFunction(
                        selectedObject,
                        STR("AddEquipWaza"),
                        STR("/Script/Pal.PalIndividualCharacterParameter:AddEquipWaza"),
                        value);
            }
        }

        // SaveParameterMirror intentionally remains untouched here. The game's
        // native OnRep function owns diffing, delegate broadcasts and mirror
        // settlement. Writing both structs before OnRep would hide the change
        // from the native notification chain.
        selectedObject->ProcessEvent(onRep, nullptr);

        const auto nicknameMatches = [&](void* target)
        {
            if (!payload.nickname)
            {
                return true;
            }
            const auto* value = mapping.NickName
                ->ContainerPtrToValuePtr<FString>(target);
            return value && to_string(**value) == *payload.nickname;
        };
        const auto favoriteMatches = [&](void* target)
        {
            return !payload.favorite ||
                mapping.IsFavoritePal->GetPropertyValueInContainer(target) ==
                    *payload.favorite;
        };
        const auto passiveMirrorMatches = [&]()
        {
            if (!passiveMutation && !passiveSetMutation)
            {
                return true;
            }
            const auto* skills = mapping.PassiveSkillList
                ->ContainerPtrToValuePtr<TArray<FName>>(mirrorMemory);
            if (!skills || static_cast<std::size_t>(std::max(skills->Num(), 0)) !=
                    requestedPassiveSkills.size())
            {
                return false;
            }
            for (std::size_t index = 0; index < requestedPassiveSkills.size(); ++index)
            {
                if (to_string((*skills)[static_cast<int32>(index)].ToString()) !=
                    requestedPassiveSkills[index])
                {
                    return false;
                }
            }
            return true;
        };
        const auto activeMirrorMatches = [&]()
        {
            return !activeMutation || ReadEnumArray(
                mapping.EquipWaza,
                mirrorMemory,
                MaxReturnedActiveSkills).Names == *payload.equippedActiveSkills;
        };
        const bool saveSynchronized = nicknameMatches(saveMemory) &&
            favoriteMatches(saveMemory);
        const bool mirrorSynchronized = nicknameMatches(mirrorMemory) &&
            favoriteMatches(mirrorMemory) && passiveMirrorMatches() &&
            activeMirrorMatches();

        const auto updatedPal = ReadPalSnapshot(mapping, selectedObject);
        const bool passiveVerified = (!passiveMutation && !passiveSetMutation) ||
            (updatedPal && updatedPal->PassiveSkills == requestedPassiveSkills);
        const bool activeVerified = !activeMutation ||
            (updatedPal && updatedPal->EquippedActiveSkills ==
                *payload.equippedActiveSkills);
        if (!updatedPal || !saveSynchronized || !mirrorSynchronized ||
            !skillFunctionsApplied || !passiveVerified || !activeVerified)
        {
            *mapping.NickName->ContainerPtrToValuePtr<FString>(saveMemory) =
                originalNickname;
            if (mapping.FilteredNickName)
            {
                *mapping.FilteredNickName
                    ->ContainerPtrToValuePtr<FString>(saveMemory) = originalNickname;
            }
            mapping.IsFavoritePal->SetPropertyValueInContainer(
                saveMemory,
                originalFavorite);
            if (passiveMutation || passiveSetMutation)
            {
                std::set<std::string> rollbackRemovals(
                    requestedPassiveSkills.begin(),
                    requestedPassiveSkills.end());
                rollbackRemovals.insert(
                    currentPal.PassiveSkills.begin(),
                    currentPal.PassiveSkills.end());
                for (const auto& skillId : rollbackRemovals)
                {
                    InvokeNameFunction(
                        selectedObject,
                        STR("RemovePassiveSkill"),
                        STR("/Script/Pal.PalIndividualCharacterParameter:RemovePassiveSkill"),
                        STR("SkillId"),
                        skillId);
                }
                for (const auto& skillId : currentPal.PassiveSkills)
                {
                    InvokeNamePairFunction(
                        selectedObject,
                        STR("AddPassiveSkill"),
                        STR("/Script/Pal.PalIndividualCharacterParameter:AddPassiveSkill"),
                        STR("AddSkill"),
                        skillId,
                        STR("OverrideSkill"),
                        "None");
                }
                WriteNameArray(
                    mapping.PassiveSkillList,
                    saveMemory,
                    currentPal.PassiveSkills);
            }
            if (activeMutation)
            {
                InvokeNoParameterFunction(
                    selectedObject,
                    STR("ClearEquipWaza"),
                    STR("/Script/Pal.PalIndividualCharacterParameter:ClearEquipWaza"));
                for (const auto& skillId : originalEquippedActiveSkills)
                {
                    if (const auto value = ResolveActiveSkillValue(
                            mapping.EquipWaza,
                            skillId))
                    {
                        InvokeActiveSkillFunction(
                            selectedObject,
                            STR("AddEquipWaza"),
                            STR("/Script/Pal.PalIndividualCharacterParameter:AddEquipWaza"),
                            *value);
                    }
                }
            }
            selectedObject->ProcessEvent(onRep, nullptr);

            const auto rolledBackPal = ReadPalSnapshot(mapping, selectedObject);
            return Contracts::CommandResult{
                .CommandId = command.CommandId,
                .State = rolledBackPal &&
                        rolledBackPal->Revision == currentPal.Revision
                    ? Contracts::CommandState::Failed
                    : Contracts::CommandState::Uncertain,
                .ObservedRevision = rolledBackPal
                    ? rolledBackPal->Revision
                    : currentPal.Revision,
                .ErrorCode = "PAL_NATIVE_SETTLEMENT_VERIFY_FAILED",
                .ErrorMessage = "The native SaveParameter settlement chain did not synchronize and the mutation was rolled back."
            };
        }

        const auto settlementFunction = passiveMutation || passiveSetMutation
            ? "RemovePassiveSkill/AddPassiveSkill + OnRep_SaveParameter"
            : activeMutation
                ? "ClearEquipWaza/AddEquipWaza + OnRep_SaveParameter"
                : "OnRep_SaveParameter";
        const auto data = std::string{"{"} +
            "\"dryRun\":false," +
            "\"applied\":true," +
            "\"settlement\":{" +
                "\"function\":\"" + settlementFunction + "\"," +
                "\"planned\":false," +
                "\"mirrorSynchronized\":true," +
                "\"readBackVerified\":true}," +
            "\"pal\":" + updatedPal->Json + "}";
        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = updatedPal->Revision,
            .DataJson = data
        };
    }

    Contracts::CommandResult PalworldGameAdapter::ReadPalSchema(
        const Contracts::CommandEnvelope& command) const
    {
        using namespace RC;
        using namespace RC::Unreal;

        struct Candidate
        {
            const RC::File::CharType* Path;
            const RC::File::CharType* ShortName;
        };

        constexpr std::array<Candidate, 13> Candidates{{
            {STR("/Script/Pal.PalIndividualCharacterSaveParameter"), STR("PalIndividualCharacterSaveParameter")},
            {STR("/Script/Pal.PalIndividualCharacterParameter"), STR("PalIndividualCharacterParameter")},
            {STR("/Script/Pal.PalIndividualCharacterHandle"), STR("PalIndividualCharacterHandle")},
            {STR("/Script/Pal.PalCharacterContainer"), STR("PalCharacterContainer")},
            {STR("/Script/Pal.PalCharacterSlot"), STR("PalCharacterSlot")},
            {STR("/Script/Pal.PalCharacterContainerId"), STR("PalCharacterContainerId")},
            {STR("/Script/Pal.PalCharacterSlotId"), STR("PalCharacterSlotId")},
            {STR("/Script/Pal.PalCharacterManager"), STR("PalCharacterManager")},
            {STR("/Script/Pal.PalCharacterManagerSubsystem"), STR("PalCharacterManagerSubsystem")},
            {STR("/Script/Pal.PalNetworkIndividualComponent"), STR("PalNetworkIndividualComponent")},
            {STR("/Script/Pal.PalIndividualCharacterData"), STR("PalIndividualCharacterData")},
            {STR("/Script/Pal.PalPlayerCharacterContainer"), STR("PalPlayerCharacterContainer")},
            {STR("/Script/Pal.PalCharacterContainerManager"), STR("PalCharacterContainerManager")}
        }};

        std::string typesJson{"["};
        std::size_t foundTypeCount = 0;
        for (const auto& candidate : Candidates)
        {
            auto* type = UObjectGlobals::StaticFindObject<UStruct*>(
                nullptr,
                nullptr,
                candidate.Path);
            if (!type)
            {
                continue;
            }
            if (foundTypeCount > 0)
            {
                typesJson += ',';
            }

            std::string propertiesJson{"["};
            std::size_t propertyCount = 0;
            for (FProperty* property : TFieldRange<FProperty>(
                     type,
                     EFieldIterationFlags::IncludeAll))
            {
                if (propertyCount >= MaxPropertiesPerPalType)
                {
                    break;
                }
                if (propertyCount > 0)
                {
                    propertiesJson += ',';
                }
                const auto detailType = PropertyDetailType(property);
                auto* owner = Cast<UStruct>(property->GetOutermostOwner());
                propertiesJson += std::string{"{"} +
                    "\"name\":\"" + EscapeJson(to_string(property->GetName())) + "\"," +
                    "\"propertyClass\":\"" + EscapeJson(to_string(property->GetClass().GetName())) + "\"," +
                    "\"detailType\":" + (detailType.empty()
                        ? std::string{"null"}
                        : "\"" + EscapeJson(detailType) + "\"") + "," +
                    "\"owner\":" + (owner
                        ? "\"" + EscapeJson(to_string(owner->GetName())) + "\""
                        : std::string{"null"}) + "}";
                ++propertyCount;
            }
            propertiesJson += ']';

            std::string functionsJson{"["};
            std::string functionSchemasJson{"["};
            std::size_t functionCount = 0;
            if (Cast<UClass>(type))
            {
                for (UFunction* function : TFieldRange<UFunction>(
                         type,
                         EFieldIterationFlags::IncludeAll))
                {
                    const auto functionName = to_string(function->GetName());
                    if (!IsPalFunction(functionName) ||
                        functionCount >= MaxReturnedFunctions)
                    {
                        continue;
                    }
                    if (functionCount > 0)
                    {
                        functionsJson += ',';
                        functionSchemasJson += ',';
                    }
                    functionsJson += "\"" + EscapeJson(functionName) + "\"";

                    std::string parametersJson{"["};
                    std::size_t parameterCount = 0;
                    for (FProperty* parameter : TFieldRange<FProperty>(
                             function,
                             EFieldIterationFlags::IncludeAll))
                    {
                        if (!parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm))
                        {
                            continue;
                        }
                        if (parameterCount > 0)
                        {
                            parametersJson += ',';
                        }
                        const auto parameterDetail = PropertyDetailType(parameter);
                        parametersJson += std::string{"{"} +
                            "\"name\":\"" + EscapeJson(to_string(parameter->GetName())) + "\"," +
                            "\"propertyClass\":\"" + EscapeJson(to_string(parameter->GetClass().GetName())) + "\"," +
                            "\"detailType\":" + (parameterDetail.empty()
                                ? std::string{"null"}
                                : "\"" + EscapeJson(parameterDetail) + "\"") + "," +
                            "\"out\":" + (parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_OutParm)
                                ? "true" : "false") + "," +
                            "\"return\":" + (parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_ReturnParm)
                                ? "true" : "false") + "}";
                        ++parameterCount;
                    }
                    parametersJson += ']';
                    functionSchemasJson += std::string{"{"} +
                        "\"name\":\"" + EscapeJson(functionName) + "\"," +
                        "\"parameterCount\":" + std::to_string(parameterCount) + "," +
                        "\"parameterSize\":" + std::to_string(function->GetPropertiesSize()) + "," +
                        "\"parameters\":" + parametersJson + "}";
                    ++functionCount;
                }
            }
            functionsJson += ']';
            functionSchemasJson += ']';

            std::vector<UObject*> objects;
            UObjectGlobals::FindAllOf(candidate.ShortName, objects);
            const auto returnedObjectCount = std::min(
                objects.size(),
                MaxPalObjectsPerType);
            std::string objectsJson{"["};
            for (std::size_t objectIndex = 0;
                 objectIndex < returnedObjectCount;
                 ++objectIndex)
            {
                if (objectIndex > 0)
                {
                    objectsJson += ',';
                }
                auto* object = objects[objectIndex];
                objectsJson += std::string{"{"} +
                    "\"ephemeralObjectId\":" + std::to_string(object->GetInternalIndex()) + "," +
                    "\"objectName\":\"" + EscapeJson(to_string(object->GetName())) + "\"," +
                    "\"fullName\":\"" + EscapeJson(to_string(object->GetFullName())) + "\"}";
            }
            objectsJson += ']';

            typesJson += std::string{"{"} +
                "\"path\":\"" + EscapeJson(to_string(candidate.Path)) + "\"," +
                "\"name\":\"" + EscapeJson(to_string(type->GetName())) + "\"," +
                "\"kind\":\"" + (Cast<UClass>(type) ? "class" : "struct") + "\"," +
                "\"propertyCount\":" + std::to_string(propertyCount) + "," +
                "\"properties\":" + propertiesJson + "," +
                "\"candidateFunctions\":" + functionsJson + "," +
                "\"functionSchemas\":" + functionSchemasJson + "," +
                "\"objectCount\":" + std::to_string(objects.size()) + "," +
                "\"objects\":" + objectsJson + "}";
            ++foundTypeCount;
        }
        typesJson += ']';

        std::vector<UObject*> functionObjects;
        UObjectGlobals::FindAllOf(STR("Function"), functionObjects);
        std::string globalFunctionMatchesJson{"["};
        std::size_t globalFunctionMatchCount = 0;
        for (auto* object : functionObjects)
        {
            auto* function = Cast<UFunction>(object);
            if (!function || globalFunctionMatchCount >= MaxReturnedFunctions)
            {
                continue;
            }
            const auto functionName = to_string(function->GetName());
            const auto fullName = to_string(function->GetFullName());
            if (!IsPalMutationFunction(functionName) ||
                fullName.find("/Script/Pal.") == std::string::npos)
            {
                continue;
            }
            if (globalFunctionMatchCount > 0)
            {
                globalFunctionMatchesJson += ',';
            }

            std::string parametersJson{"["};
            std::size_t parameterCount = 0;
            for (FProperty* parameter : TFieldRange<FProperty>(
                     function,
                     EFieldIterationFlags::IncludeAll))
            {
                if (!parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_Parm))
                {
                    continue;
                }
                if (parameterCount > 0)
                {
                    parametersJson += ',';
                }
                const auto parameterDetail = PropertyDetailType(parameter);
                parametersJson += std::string{"{"} +
                    "\"name\":\"" + EscapeJson(to_string(parameter->GetName())) + "\"," +
                    "\"propertyClass\":\"" + EscapeJson(to_string(parameter->GetClass().GetName())) + "\"," +
                    "\"detailType\":" + (parameterDetail.empty()
                        ? std::string{"null"}
                        : "\"" + EscapeJson(parameterDetail) + "\"") + "," +
                    "\"out\":" + (parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_OutParm)
                        ? "true" : "false") + "," +
                    "\"return\":" + (parameter->HasAnyPropertyFlags(EPropertyFlags::CPF_ReturnParm)
                        ? "true" : "false") + "}";
                ++parameterCount;
            }
            parametersJson += ']';
            globalFunctionMatchesJson += std::string{"{"} +
                "\"name\":\"" + EscapeJson(functionName) + "\"," +
                "\"fullName\":\"" + EscapeJson(fullName) + "\"," +
                "\"parameterCount\":" + std::to_string(parameterCount) + "," +
                "\"parameterSize\":" + std::to_string(function->GetPropertiesSize()) + "," +
                "\"parameters\":" + parametersJson + "}";
            ++globalFunctionMatchCount;
        }
        globalFunctionMatchesJson += ']';

        const auto data = std::string{"{"} +
            "\"observedAt\":\"" + UtcNow() + "\"," +
            "\"executionThread\":\"unreal-engine-tick\"," +
            "\"candidateTypeCount\":" + std::to_string(Candidates.size()) + "," +
            "\"foundTypeCount\":" + std::to_string(foundTypeCount) + "," +
            "\"globalFunctionObjectCount\":" + std::to_string(functionObjects.size()) + "," +
            "\"globalFunctionMatchCount\":" + std::to_string(globalFunctionMatchCount) + "," +
            "\"globalFunctionMatches\":" + globalFunctionMatchesJson + "," +
            "\"types\":" + typesJson + "}";

        return Contracts::CommandResult{
            .CommandId = command.CommandId,
            .State = Contracts::CommandState::Succeeded,
            .ObservedRevision = 0,
            .DataJson = data
        };
    }
}

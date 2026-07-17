#include "Runtime/ExecutableIdentity.hpp"

#include <Windows.h>
#include <bcrypt.h>

#include <array>
#include <cstddef>
#include <limits>
#include <vector>

namespace
{
    int ModuleAnchor = 0;

    std::optional<std::wstring> ModulePath(HMODULE module)
    {
        std::vector<wchar_t> buffer(1024);
        while (buffer.size() <= 32768)
        {
            SetLastError(ERROR_SUCCESS);
            const auto length = GetModuleFileNameW(
                module,
                buffer.data(),
                static_cast<DWORD>(buffer.size()));
            if (length == 0)
            {
                return std::nullopt;
            }
            if (length < buffer.size() - 1 ||
                (length < buffer.size() && GetLastError() != ERROR_INSUFFICIENT_BUFFER))
            {
                return std::wstring(buffer.data(), length);
            }
            buffer.resize(buffer.size() * 2);
        }
        return std::nullopt;
    }

    std::string HexLower(const std::array<unsigned char, 32>& digest)
    {
        constexpr char Alphabet[] = "0123456789abcdef";
        std::string result;
        result.resize(digest.size() * 2);
        for (std::size_t index = 0; index < digest.size(); ++index)
        {
            result[index * 2] = Alphabet[digest[index] >> 4];
            result[index * 2 + 1] = Alphabet[digest[index] & 0x0f];
        }
        return result;
    }

    bool SameFileSnapshot(
        const BY_HANDLE_FILE_INFORMATION& before,
        const BY_HANDLE_FILE_INFORMATION& after)
    {
        return before.dwVolumeSerialNumber == after.dwVolumeSerialNumber &&
            before.nFileIndexHigh == after.nFileIndexHigh &&
            before.nFileIndexLow == after.nFileIndexLow &&
            before.nFileSizeHigh == after.nFileSizeHigh &&
            before.nFileSizeLow == after.nFileSizeLow &&
            before.ftLastWriteTime.dwHighDateTime == after.ftLastWriteTime.dwHighDateTime &&
            before.ftLastWriteTime.dwLowDateTime == after.ftLastWriteTime.dwLowDateTime;
    }

    std::optional<PalControl::Runtime::ExecutableIdentity> ReadFileIdentity(
        const std::wstring& path)
    {
        const auto file = CreateFileW(
            path.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return std::nullopt;
        }

        LARGE_INTEGER size{};
        BY_HANDLE_FILE_INFORMATION before{};
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        std::vector<unsigned char> hashObject;
        std::array<unsigned char, 32> digest{};
        bool succeeded = false;

        do
        {
            if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 ||
                !GetFileInformationByHandle(file, &before))
            {
                break;
            }
            if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(
                    &algorithm,
                    BCRYPT_SHA256_ALGORITHM,
                    nullptr,
                    0)))
            {
                break;
            }

            DWORD objectLength = 0;
            DWORD bytesWritten = 0;
            if (!BCRYPT_SUCCESS(BCryptGetProperty(
                    algorithm,
                    BCRYPT_OBJECT_LENGTH,
                    reinterpret_cast<PUCHAR>(&objectLength),
                    sizeof(objectLength),
                    &bytesWritten,
                    0)) ||
                objectLength == 0 || bytesWritten != sizeof(objectLength))
            {
                break;
            }

            DWORD hashLength = 0;
            if (!BCRYPT_SUCCESS(BCryptGetProperty(
                    algorithm,
                    BCRYPT_HASH_LENGTH,
                    reinterpret_cast<PUCHAR>(&hashLength),
                    sizeof(hashLength),
                    &bytesWritten,
                    0)) ||
                hashLength != digest.size() || bytesWritten != sizeof(hashLength))
            {
                break;
            }

            hashObject.resize(objectLength);
            if (!BCRYPT_SUCCESS(BCryptCreateHash(
                    algorithm,
                    &hash,
                    hashObject.data(),
                    static_cast<ULONG>(hashObject.size()),
                    nullptr,
                    0,
                    0)))
            {
                break;
            }

            std::vector<unsigned char> chunk(1024 * 1024);
            for (;;)
            {
                DWORD read = 0;
                if (!ReadFile(
                        file,
                        chunk.data(),
                        static_cast<DWORD>(chunk.size()),
                        &read,
                        nullptr))
                {
                    break;
                }
                if (read == 0)
                {
                    succeeded = BCRYPT_SUCCESS(BCryptFinishHash(
                        hash,
                        digest.data(),
                        static_cast<ULONG>(digest.size()),
                        0));
                    break;
                }
                if (!BCRYPT_SUCCESS(BCryptHashData(hash, chunk.data(), read, 0)))
                {
                    break;
                }
            }
        }
        while (false);

        if (succeeded)
        {
            LARGE_INTEGER finalSize{};
            BY_HANDLE_FILE_INFORMATION after{};
            if (!GetFileSizeEx(file, &finalSize) ||
                !GetFileInformationByHandle(file, &after) ||
                finalSize.QuadPart != size.QuadPart ||
                !SameFileSnapshot(before, after))
            {
                succeeded = false;
            }
        }

        if (hash != nullptr)
        {
            BCryptDestroyHash(hash);
        }
        if (algorithm != nullptr)
        {
            BCryptCloseAlgorithmProvider(algorithm, 0);
        }
        CloseHandle(file);

        if (!succeeded)
        {
            return std::nullopt;
        }
        return PalControl::Runtime::ExecutableIdentity{
            .Sha256 = HexLower(digest),
            .Size = static_cast<std::uint64_t>(size.QuadPart)
        };
    }
}

namespace PalControl::Runtime
{
    std::optional<std::string> ComputeSha256Hex(std::string_view value)
    {
        if (value.size() > std::numeric_limits<ULONG>::max())
        {
            return std::nullopt;
        }

        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        std::vector<unsigned char> hashObject;
        std::array<unsigned char, 32> digest{};
        bool succeeded = false;

        do
        {
            if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(
                    &algorithm,
                    BCRYPT_SHA256_ALGORITHM,
                    nullptr,
                    0)))
            {
                break;
            }
            DWORD objectLength = 0;
            DWORD bytesWritten = 0;
            if (!BCRYPT_SUCCESS(BCryptGetProperty(
                    algorithm,
                    BCRYPT_OBJECT_LENGTH,
                    reinterpret_cast<PUCHAR>(&objectLength),
                    sizeof(objectLength),
                    &bytesWritten,
                    0)) ||
                objectLength == 0 || bytesWritten != sizeof(objectLength))
            {
                break;
            }
            hashObject.resize(objectLength);
            if (!BCRYPT_SUCCESS(BCryptCreateHash(
                    algorithm,
                    &hash,
                    hashObject.data(),
                    static_cast<ULONG>(hashObject.size()),
                    nullptr,
                    0,
                    0)) ||
                !BCRYPT_SUCCESS(BCryptHashData(
                    hash,
                    reinterpret_cast<PUCHAR>(const_cast<char*>(value.data())),
                    static_cast<ULONG>(value.size()),
                    0)) ||
                !BCRYPT_SUCCESS(BCryptFinishHash(
                    hash,
                    digest.data(),
                    static_cast<ULONG>(digest.size()),
                    0)))
            {
                break;
            }
            succeeded = true;
        }
        while (false);

        if (hash != nullptr)
        {
            BCryptDestroyHash(hash);
        }
        if (algorithm != nullptr)
        {
            BCryptCloseAlgorithmProvider(algorithm, 0);
        }
        return succeeded
            ? std::optional<std::string>{HexLower(digest)}
            : std::nullopt;
    }

    std::optional<ExecutableIdentity> ReadCurrentExecutableIdentity()
    {
        const auto path = ModulePath(nullptr);
        return path ? ReadFileIdentity(*path) : std::nullopt;
    }

    std::optional<ExecutableIdentity> ReadCurrentModuleIdentity()
    {
        HMODULE module = nullptr;
        if (!GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                    GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&ModuleAnchor),
                &module))
        {
            return std::nullopt;
        }
        const auto path = ModulePath(module);
        return path ? ReadFileIdentity(*path) : std::nullopt;
    }

    std::optional<ExecutableIdentity> ReadLoadedModuleIdentity(
        const wchar_t* moduleName)
    {
        if (moduleName == nullptr || moduleName[0] == L'\0')
        {
            return std::nullopt;
        }
        const auto module = GetModuleHandleW(moduleName);
        if (module == nullptr)
        {
            return std::nullopt;
        }
        const auto path = ModulePath(module);
        return path ? ReadFileIdentity(*path) : std::nullopt;
    }
}

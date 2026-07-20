#include "aviutl2_mcp/bridge_identity.h"

#include <Windows.h>
#include <ShlObj.h>
#include <rpc.h>

#include <memory>
#include <stdexcept>
#include <system_error>
#include <vector>

namespace aviutl2_mcp {
namespace {

[[noreturn]] void throw_last_error(const char* message) {
    throw std::system_error(
        static_cast<int>(GetLastError()),
        std::system_category(),
        message);
}

[[nodiscard]] std::string create_uuid() {
    UUID uuid{};
    const RPC_STATUS create_status = UuidCreate(&uuid);
    if (create_status != RPC_S_OK && create_status != RPC_S_UUID_LOCAL_ONLY) {
        throw std::runtime_error("UuidCreate failed");
    }
    RPC_CSTR raw_text = nullptr;
    if (UuidToStringA(&uuid, &raw_text) != RPC_S_OK) {
        throw std::runtime_error("UuidToStringA failed");
    }
    try {
        const std::string result(reinterpret_cast<const char*>(raw_text));
        RpcStringFreeA(&raw_text);
        return result;
    } catch (...) {
        RpcStringFreeA(&raw_text);
        throw;
    }
}

[[nodiscard]] std::uint64_t get_process_creation_time() {
    FILETIME creation{};
    FILETIME exit{};
    FILETIME kernel{};
    FILETIME user{};
    if (GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user) == FALSE) {
        throw_last_error("GetProcessTimes failed");
    }
    ULARGE_INTEGER value{};
    value.LowPart = creation.dwLowDateTime;
    value.HighPart = creation.dwHighDateTime;
    return value.QuadPart;
}

[[nodiscard]] bool try_parse_uuid(const std::string& value, UUID& uuid) noexcept {
    if (value.empty()) {
        return false;
    }
    return UuidFromStringA(
               reinterpret_cast<RPC_CSTR>(const_cast<char*>(value.c_str())),
               &uuid)
        == RPC_S_OK;
}

}  // namespace

bridge_identity create_bridge_identity() {
    const std::string instance_id = create_uuid();
    return bridge_identity{
        .instance_id = instance_id,
        .server_epoch = create_uuid(),
        .pipe_name = "AviUtl2MCP.v1." + instance_id,
        .process_id = GetCurrentProcessId(),
        .process_creation_time = get_process_creation_time(),
    };
}

std::filesystem::path get_default_descriptor_directory() {
    PWSTR raw_path = nullptr;
    const HRESULT result = SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &raw_path);
    if (FAILED(result)) {
        throw std::system_error(
            static_cast<int>(result),
            std::system_category(),
            "SHGetKnownFolderPath failed");
    }
    const std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> path(raw_path, CoTaskMemFree);
    return std::filesystem::path(path.get()) / L"AviUtl2MCP" / L"v1" / L"instances";
}

std::filesystem::path get_configured_descriptor_directory() {
    constexpr wchar_t variable_name[] = L"AVIUTL2_MCP_INSTANCE_DIRECTORY";
    SetLastError(ERROR_SUCCESS);
    const DWORD required_size = GetEnvironmentVariableW(variable_name, nullptr, 0);
    if (required_size == 0U) {
        const DWORD error = GetLastError();
        if (error == ERROR_SUCCESS || error == ERROR_ENVVAR_NOT_FOUND) {
            return get_default_descriptor_directory();
        }
        throw std::system_error(
            static_cast<int>(error),
            std::system_category(),
            "GetEnvironmentVariableW failed");
    }

    std::vector<wchar_t> buffer(required_size);
    const DWORD written = GetEnvironmentVariableW(
        variable_name,
        buffer.data(),
        static_cast<DWORD>(buffer.size()));
    if (written == 0U || written >= buffer.size()) {
        throw_last_error("GetEnvironmentVariableW failed");
    }
    return std::filesystem::path(std::wstring(buffer.data(), written));
}

bool uuid_equals(const std::string& left, const std::string& right) noexcept {
    UUID left_uuid{};
    UUID right_uuid{};
    RPC_STATUS status = RPC_S_OK;
    return try_parse_uuid(left, left_uuid)
        && try_parse_uuid(right, right_uuid)
        && UuidEqual(&left_uuid, &right_uuid, &status) != FALSE
        && status == RPC_S_OK;
}

bool is_nonzero_uuid(const std::string& value) noexcept {
    UUID parsed{};
    UUID nil{};
    RPC_STATUS status = RPC_S_OK;
    return try_parse_uuid(value, parsed)
        && UuidEqual(&parsed, &nil, &status) == FALSE
        && status == RPC_S_OK;
}

}  // namespace aviutl2_mcp

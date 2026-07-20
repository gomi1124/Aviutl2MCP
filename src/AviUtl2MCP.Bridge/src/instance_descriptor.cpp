#include "aviutl2_mcp/instance_descriptor.h"

#include "aviutl2_mcp/pipe_security.h"

#include <Windows.h>
#include <nlohmann/json.hpp>

#include <limits>
#include <stdexcept>
#include <system_error>
#include <utility>

namespace aviutl2_mcp {
namespace {

[[noreturn]] void throw_last_error(const char* message) {
    throw std::system_error(
        static_cast<int>(GetLastError()),
        std::system_category(),
        message);
}

void write_file(const std::filesystem::path& path, const std::string& content) {
    user_only_security security;
    const HANDLE file = CreateFileW(
        path.c_str(),
        GENERIC_WRITE,
        0U,
        security.attributes(),
        CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        throw_last_error("CreateFileW failed for the temporary descriptor");
    }

    try {
        std::size_t offset = 0U;
        while (offset < content.size()) {
            const std::size_t remaining = content.size() - offset;
            const DWORD requested = static_cast<DWORD>(
                (std::min)(remaining, static_cast<std::size_t>((std::numeric_limits<DWORD>::max)())));
            DWORD written = 0U;
            if (WriteFile(file, content.data() + offset, requested, &written, nullptr) == FALSE
                || written == 0U) {
                throw_last_error("WriteFile failed for the temporary descriptor");
            }
            offset += written;
        }
        if (FlushFileBuffers(file) == FALSE) {
            throw_last_error("FlushFileBuffers failed for the temporary descriptor");
        }
    } catch (...) {
        CloseHandle(file);
        DeleteFileW(path.c_str());
        throw;
    }
    CloseHandle(file);
}

}  // namespace

instance_descriptor_publisher::instance_descriptor_publisher(
    bridge_identity identity,
    std::filesystem::path directory,
    std::string bridge_version)
    : identity_(std::move(identity)),
      directory_(std::move(directory)),
      path_(directory_ / (identity_.instance_id + ".json")),
      bridge_version_(std::move(bridge_version)) {}

void instance_descriptor_publisher::publish() {
    if (is_published_) {
        throw std::logic_error("instance descriptor was already published");
    }
    ensure_user_only_directory(directory_);
    nlohmann::json document{
        {"instanceId", identity_.instance_id},
        {"processId", identity_.process_id},
        {"processCreationTime", identity_.process_creation_time},
        {"pipeName", identity_.pipe_name},
        {"bridgeVersion", bridge_version_},
        {"protocolMajor", 1U},
    };
    const std::filesystem::path temporary_path = path_.wstring() + L"." + std::to_wstring(GetCurrentThreadId()) + L".tmp";
    DeleteFileW(temporary_path.c_str());
    write_file(temporary_path, document.dump());
    if (MoveFileExW(
            temporary_path.c_str(),
            path_.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)
        == FALSE) {
        const DWORD error = GetLastError();
        DeleteFileW(temporary_path.c_str());
        SetLastError(error);
        throw_last_error("MoveFileExW failed while publishing the descriptor");
    }
    is_published_ = true;
}

void instance_descriptor_publisher::remove() noexcept {
    if (!is_published_) {
        return;
    }
    if (DeleteFileW(path_.c_str()) != FALSE || GetLastError() == ERROR_FILE_NOT_FOUND) {
        is_published_ = false;
    }
}

const std::filesystem::path& instance_descriptor_publisher::path() const noexcept {
    return path_;
}

bool instance_descriptor_publisher::is_published() const noexcept {
    return is_published_;
}

}  // namespace aviutl2_mcp

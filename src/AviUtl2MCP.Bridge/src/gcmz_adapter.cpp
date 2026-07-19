#include "aviutl2_mcp/gcmz_adapter.h"

#include <Windows.h>

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cwctype>
#include <limits>
#include <stdexcept>
#include <string_view>
#include <system_error>
#include <utility>

namespace aviutl2_mcp {
namespace {

static_assert(sizeof(gcmz_shared_data) == 564U, "GCMZDrops API v3 layout changed");

class unique_handle final {
public:
    explicit unique_handle(HANDLE handle = nullptr) noexcept
        : handle_(handle) {}

    ~unique_handle() {
        if (handle_ != nullptr) {
            CloseHandle(handle_);
        }
    }

    unique_handle(const unique_handle&) = delete;
    unique_handle& operator=(const unique_handle&) = delete;

    unique_handle(unique_handle&& other) noexcept
        : handle_(std::exchange(other.handle_, nullptr)) {}

    unique_handle& operator=(unique_handle&& other) noexcept {
        if (this != &other) {
            if (handle_ != nullptr) {
                CloseHandle(handle_);
            }
            handle_ = std::exchange(other.handle_, nullptr);
        }
        return *this;
    }

    [[nodiscard]] HANDLE get() const noexcept {
        return handle_;
    }

private:
    HANDLE handle_;
};

class mapped_view final {
public:
    explicit mapped_view(void* view = nullptr) noexcept
        : view_(view) {}

    ~mapped_view() {
        if (view_ != nullptr) {
            UnmapViewOfFile(view_);
        }
    }

    mapped_view(const mapped_view&) = delete;
    mapped_view& operator=(const mapped_view&) = delete;

    mapped_view(mapped_view&& other) noexcept
        : view_(std::exchange(other.view_, nullptr)) {}

    mapped_view& operator=(mapped_view&& other) noexcept {
        if (this != &other) {
            if (view_ != nullptr) {
                UnmapViewOfFile(view_);
            }
            view_ = std::exchange(other.view_, nullptr);
        }
        return *this;
    }

    [[nodiscard]] const gcmz_shared_data* data() const noexcept {
        return static_cast<const gcmz_shared_data*>(view_);
    }

private:
    void* view_;
};

class mutex_lease final {
public:
    explicit mutex_lease(HANDLE mutex) noexcept
        : mutex_(mutex) {}

    ~mutex_lease() {
        if (mutex_ != nullptr) {
            ReleaseMutex(mutex_);
        }
    }

    mutex_lease(const mutex_lease&) = delete;
    mutex_lease& operator=(const mutex_lease&) = delete;

    mutex_lease(mutex_lease&& other) noexcept
        : mutex_(std::exchange(other.mutex_, nullptr)) {}

    mutex_lease& operator=(mutex_lease&& other) noexcept {
        if (this != &other) {
            if (mutex_ != nullptr) {
                ReleaseMutex(mutex_);
            }
            mutex_ = std::exchange(other.mutex_, nullptr);
        }
        return *this;
    }

private:
    HANDLE mutex_;
};

[[nodiscard]] gcmz_probe_result probe_failure(std::string code, std::string message) {
    return gcmz_probe_result{
        .error_code = std::move(code),
        .error_message = std::move(message),
    };
}

[[nodiscard]] std::wstring normalize_path(const std::filesystem::path& path) {
    std::error_code error;
    std::filesystem::path normalized = std::filesystem::absolute(path, error);
    if (error) {
        normalized = path;
    }
    normalized = normalized.lexically_normal();
    std::wstring value = normalized.native();
    std::ranges::replace(value, L'/', L'\\');
    std::ranges::transform(value, value.begin(), [](const wchar_t character) {
        return static_cast<wchar_t>(std::towlower(character));
    });
    while (value.size() > 3U && value.back() == L'\\') {
        value.pop_back();
    }
    return value;
}

[[nodiscard]] std::string wide_to_utf8(const std::wstring_view value) {
    if (value.empty()) {
        return {};
    }
    if (value.size() > static_cast<std::size_t>((std::numeric_limits<int>::max)())) {
        throw std::invalid_argument("GCMZDrops path is too long");
    }
    const int required = WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (required <= 0) {
        throw std::invalid_argument("GCMZDrops path is not valid UTF-16");
    }
    std::string result(static_cast<std::size_t>(required), '\0');
    if (WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            required,
            nullptr,
            nullptr) != required) {
        throw std::runtime_error("WideCharToMultiByte failed for GCMZDrops path");
    }
    return result;
}

struct locked_mapping final {
    unique_handle mutex;
    std::optional<mutex_lease> lease;
    unique_handle mapping;
    std::optional<mapped_view> view;
    gcmz_probe_result failure;
};

[[nodiscard]] locked_mapping open_locked_mapping(const std::uint32_t timeout_ms) {
    locked_mapping result;
    result.mutex = unique_handle(OpenMutexW(SYNCHRONIZE, FALSE, GCMZ_MUTEX_NAME));
    if (result.mutex.get() == nullptr) {
        result.failure = probe_failure("gcmz_mutex_missing", "GCMZDrops API mutex was not found");
        return result;
    }
    const DWORD wait = WaitForSingleObject(result.mutex.get(), timeout_ms);
    if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) {
        result.failure = probe_failure(
            wait == WAIT_TIMEOUT ? "gcmz_timeout" : "gcmz_mutex_failed",
            wait == WAIT_TIMEOUT ? "GCMZDrops API mutex timed out" : "GCMZDrops API mutex wait failed");
        return result;
    }
    result.lease.emplace(result.mutex.get());
    result.mapping = unique_handle(OpenFileMappingW(FILE_MAP_READ, FALSE, GCMZ_MAPPING_NAME));
    if (result.mapping.get() == nullptr) {
        result.failure = probe_failure("gcmz_mapping_missing", "GCMZDrops API mapping was not found");
        return result;
    }
    result.view.emplace(MapViewOfFile(
        result.mapping.get(),
        FILE_MAP_READ,
        0U,
        0U,
        sizeof(gcmz_shared_data)));
    if (result.view->data() == nullptr) {
        result.failure = probe_failure("gcmz_mapping_failed", "GCMZDrops API mapping could not be read");
    }
    return result;
}

[[nodiscard]] gcmz_probe_result evaluate_mapping(
    const gcmz_shared_data& data,
    const std::uint32_t expected_process_id,
    const std::optional<std::filesystem::path>& expected_project_path) {
    const HWND window = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(data.window));
    DWORD actual_process_id = 0U;
    if (IsWindow(window) != FALSE) {
        static_cast<void>(GetWindowThreadProcessId(window, &actual_process_id));
    }
    return evaluate_gcmz_shared_data(
        data,
        IsWindow(window) != FALSE,
        actual_process_id,
        expected_process_id,
        expected_project_path);
}

}  // namespace

gcmz_probe_result evaluate_gcmz_shared_data(
    const gcmz_shared_data& data,
    const bool is_window,
    const std::uint32_t actual_process_id,
    const std::uint32_t expected_process_id,
    const std::optional<std::filesystem::path>& expected_project_path) {
    if (data.api_version != GCMZ_REQUIRED_API_VERSION) {
        return probe_failure("gcmz_api_unsupported", "GCMZDrops API v3 is required");
    }
    if (data.window == 0U || !is_window) {
        return probe_failure("gcmz_window_invalid", "GCMZDrops target window is invalid");
    }
    if (expected_process_id == 0U || actual_process_id != expected_process_id) {
        return probe_failure("gcmz_target_mismatch", "GCMZDrops target process does not match AviUtl2");
    }
    const auto terminator = std::ranges::find(data.project_path, L'\0');
    if (terminator == data.project_path.end()) {
        return probe_failure("gcmz_project_invalid", "GCMZDrops project path is not terminated");
    }
    const std::filesystem::path project_path(std::wstring(data.project_path.begin(), terminator));
    if (expected_project_path.has_value()
        && normalize_path(project_path) != normalize_path(*expected_project_path)) {
        return probe_failure("gcmz_project_mismatch", "GCMZDrops project does not match AviUtl2");
    }
    return gcmz_probe_result{
        .ok = true,
        .window = data.window,
        .process_id = actual_process_id,
        .api_version = data.api_version,
        .project_path = project_path.empty()
            ? std::optional<std::filesystem::path>{}
            : std::optional<std::filesystem::path>{project_path},
        .aviutl_version = data.aviutl_version,
        .gcmz_version = data.gcmz_version,
    };
}

std::string create_gcmz_drop_payload(const gcmz_drop_request& request) {
    if (request.frame_advance < 0) {
        throw std::invalid_argument("frameAdvance must be non-negative");
    }
    if (request.margin < -1) {
        throw std::invalid_argument("margin must be -1 or non-negative");
    }
    if (request.files.empty()) {
        throw std::invalid_argument("files must contain at least one path");
    }
    nlohmann::json files = nlohmann::json::array();
    for (const std::filesystem::path& file : request.files) {
        if (file.empty() || !file.is_absolute()) {
            throw std::invalid_argument("GCMZDrops file paths must be absolute");
        }
        files.push_back(wide_to_utf8(file.lexically_normal().native()));
    }
    return nlohmann::json{
        {"layer", request.layer},
        {"frameAdvance", request.frame_advance},
        {"margin", request.margin},
        {"files", std::move(files)},
    }.dump();
}

gcmz_probe_result gcmz_adapter::probe(
    const std::uint32_t expected_process_id,
    const std::optional<std::filesystem::path>& expected_project_path,
    const std::uint32_t timeout_ms) const noexcept {
    try {
        locked_mapping mapping = open_locked_mapping(timeout_ms);
        if (!mapping.failure.error_code.empty()) {
            return mapping.failure;
        }
        return evaluate_mapping(*mapping.view->data(), expected_process_id, expected_project_path);
    } catch (const std::exception& exception) {
        return probe_failure("gcmz_probe_failed", exception.what());
    }
}

gcmz_send_result gcmz_adapter::send_files(
    const gcmz_drop_request& request,
    const std::uint32_t expected_process_id,
    const std::optional<std::filesystem::path>& expected_project_path,
    const std::uint32_t timeout_ms) const noexcept {
    try {
        const std::string payload = create_gcmz_drop_payload(request);
        locked_mapping mapping = open_locked_mapping(timeout_ms);
        if (!mapping.failure.error_code.empty()) {
            return gcmz_send_result{
                .target = mapping.failure,
                .payload = payload,
                .error_code = mapping.failure.error_code,
                .error_message = mapping.failure.error_message,
            };
        }
        const gcmz_probe_result target = evaluate_mapping(
            *mapping.view->data(), expected_process_id, expected_project_path);
        if (!target.ok) {
            return gcmz_send_result{
                .target = target,
                .payload = payload,
                .error_code = target.error_code,
                .error_message = target.error_message,
            };
        }
        if (payload.size() > static_cast<std::size_t>((std::numeric_limits<DWORD>::max)())) {
            throw std::invalid_argument("GCMZDrops payload is too large");
        }
        COPYDATASTRUCT copy_data{
            .dwData = 2U,
            .cbData = static_cast<DWORD>(payload.size()),
            .lpData = const_cast<char*>(payload.data()),
        };
        DWORD_PTR message_result = 0U;
        const HWND window = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(target.window));
        if (SendMessageTimeoutW(
                window,
                WM_COPYDATA,
                0U,
                reinterpret_cast<LPARAM>(&copy_data),
                SMTO_ABORTIFHUNG | SMTO_BLOCK,
                timeout_ms,
                &message_result) == 0U) {
            const DWORD error = GetLastError();
            return gcmz_send_result{
                .target = target,
                .payload = payload,
                .error_code = error == ERROR_TIMEOUT ? "gcmz_timeout" : "gcmz_send_failed",
                .error_message = error == ERROR_TIMEOUT
                    ? "GCMZDrops request timed out"
                    : "GCMZDrops request could not be delivered",
            };
        }
        return gcmz_send_result{
            .ok = true,
            .target = target,
            .payload = payload,
        };
    } catch (const std::invalid_argument& exception) {
        return gcmz_send_result{
            .error_code = "invalid_argument",
            .error_message = exception.what(),
        };
    } catch (const std::exception& exception) {
        return gcmz_send_result{
            .error_code = "gcmz_send_failed",
            .error_message = exception.what(),
        };
    }
}

}  // namespace aviutl2_mcp

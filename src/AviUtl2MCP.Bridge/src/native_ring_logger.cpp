#include "aviutl2_mcp/native_ring_logger.h"

#include <Windows.h>

#include "logger2.h"

#include <algorithm>
#include <array>
#include <cstdio>
#include <limits>
#include <regex>
#include <stdexcept>
#include <utility>

namespace aviutl2_mcp {
namespace {

constexpr std::size_t MAXIMUM_QUERY_LIMIT = 2000U;
constexpr std::size_t MAXIMUM_HOST_MESSAGE_CHARS = 1023U;
constexpr std::uint64_t WINDOWS_TO_UNIX_EPOCH_100NS = 116444736000000000ULL;

[[nodiscard]] std::string mask_sensitive_text(const std::string_view value) {
    static const std::regex bearer_pattern(
        R"(\bBearer\s+[A-Za-z0-9._~+/=-]+)",
        std::regex_constants::icase | std::regex_constants::ECMAScript);
    static const std::regex assignment_pattern(
        R"(\b(authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|secret)\b\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;]+))",
        std::regex_constants::icase | std::regex_constants::ECMAScript);
    static const std::regex user_directory_pattern(
        R"(([A-Z]:\\Users\\)[^\\\s"']+)",
        std::regex_constants::icase | std::regex_constants::ECMAScript);

    std::string masked = std::regex_replace(std::string(value), bearer_pattern, "Bearer [REDACTED]");
    masked = std::regex_replace(masked, assignment_pattern, "$1=[REDACTED]");
    return std::regex_replace(masked, user_directory_pattern, "$1[USER]");
}

[[nodiscard]] std::wstring utf8_to_wide(const std::string_view value) {
    if (value.empty()) {
        return {};
    }
    if (value.size() > static_cast<std::size_t>((std::numeric_limits<int>::max)())) {
        return L"[oversized UTF-8 log message]";
    }
    const int required = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0);
    if (required <= 0) {
        return L"[invalid UTF-8 log message]";
    }
    std::wstring result(static_cast<std::size_t>(required), L'\0');
    const int converted = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        result.data(),
        required);
    if (converted != required) {
        return L"[invalid UTF-8 log message]";
    }
    return result;
}

[[nodiscard]] std::string wide_to_utf8(const std::wstring_view value) {
    if (value.empty()) {
        return {};
    }
    if (value.size() > static_cast<std::size_t>((std::numeric_limits<int>::max)())) {
        return "[oversized UTF-16 log message]";
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
        return "[invalid UTF-16 log message]";
    }
    std::string result(static_cast<std::size_t>(required), '\0');
    const int converted = WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        result.data(),
        required,
        nullptr,
        nullptr);
    if (converted != required) {
        return "[invalid UTF-16 log message]";
    }
    return result;
}

[[nodiscard]] std::wstring truncate_message(std::wstring value) {
    if (value.size() <= MAXIMUM_HOST_MESSAGE_CHARS) {
        return value;
    }
    std::size_t prefix_length = MAXIMUM_HOST_MESSAGE_CHARS - 3U;
    const wchar_t final_character = value[prefix_length - 1U];
    if (final_character >= 0xd800 && final_character <= 0xdbff) {
        --prefix_length;
    }
    value.resize(prefix_length);
    value.append(L"...");
    return value;
}

struct native_timestamp final {
    std::string text;
    std::int64_t unix_ms;
};

[[nodiscard]] native_timestamp current_timestamp_utc() {
    FILETIME file_time{};
    GetSystemTimePreciseAsFileTime(&file_time);
    SYSTEMTIME time{};
    if (FileTimeToSystemTime(&file_time, &time) == FALSE) {
        throw std::runtime_error("failed to convert native log timestamp");
    }
    std::array<char, 25> buffer{};
    const int written = std::snprintf(
        buffer.data(),
        buffer.size(),
        "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
        static_cast<unsigned int>(time.wYear),
        static_cast<unsigned int>(time.wMonth),
        static_cast<unsigned int>(time.wDay),
        static_cast<unsigned int>(time.wHour),
        static_cast<unsigned int>(time.wMinute),
        static_cast<unsigned int>(time.wSecond),
        static_cast<unsigned int>(time.wMilliseconds));
    if (written != static_cast<int>(buffer.size() - 1U)) {
        throw std::runtime_error("failed to format native log timestamp");
    }
    ULARGE_INTEGER ticks{};
    ticks.LowPart = file_time.dwLowDateTime;
    ticks.HighPart = file_time.dwHighDateTime;
    if (ticks.QuadPart < WINDOWS_TO_UNIX_EPOCH_100NS) {
        throw std::runtime_error("native log timestamp predates the Unix epoch");
    }
    const std::uint64_t unix_ms = (ticks.QuadPart - WINDOWS_TO_UNIX_EPOCH_100NS) / 10000ULL;
    if (unix_ms > static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)())) {
        throw std::runtime_error("native log timestamp is outside the supported range");
    }
    return {
        .text = std::string(buffer.data(), static_cast<std::size_t>(written)),
        .unix_ms = static_cast<std::int64_t>(unix_ms),
    };
}

[[nodiscard]] bool includes_level(
    const std::vector<native_log_level>& levels,
    const native_log_level level) {
    return levels.empty() || std::ranges::find(levels, level) != levels.end();
}

void write_to_host(
    LOG_HANDLE* host_logger,
    const native_log_level level,
    const std::wstring& message) {
    if (host_logger == nullptr) {
        return;
    }
    switch (level) {
        case native_log_level::trace:
            if (host_logger->verbose != nullptr) {
                host_logger->verbose(host_logger, message.c_str());
            }
            return;
        case native_log_level::information:
            if (host_logger->info != nullptr) {
                host_logger->info(host_logger, message.c_str());
            }
            return;
        case native_log_level::warning:
            if (host_logger->warn != nullptr) {
                host_logger->warn(host_logger, message.c_str());
            }
            return;
        case native_log_level::error:
            if (host_logger->error != nullptr) {
                host_logger->error(host_logger, message.c_str());
            }
            return;
    }
}

}  // namespace

native_ring_logger::native_ring_logger(const std::size_t capacity)
    : capacity_(capacity) {
    if (capacity_ == 0U) {
        throw std::invalid_argument("native log capacity must be positive");
    }
}

void native_ring_logger::attach(LOG_HANDLE* host_logger) noexcept {
    host_logger_.store(host_logger);
}

void native_ring_logger::write(
    const native_log_level level,
    const std::string_view component,
    const std::string_view event_id,
    const std::string_view message,
    const native_log_context context) noexcept {
    try {
        if (component.empty() || event_id.empty()) {
            throw std::invalid_argument("native log component and event ID must not be empty");
        }

        const std::string masked_message = mask_sensitive_text(message);
        const std::wstring ring_message_wide = truncate_message(utf8_to_wide(masked_message));
        const std::string ring_message = wide_to_utf8(ring_message_wide);
        std::wstring host_message = L"[AviUtl2MCP][" + utf8_to_wide(component) + L"]["
            + utf8_to_wide(event_id) + L"]";
        if (context.correlation_id.has_value() && !context.correlation_id->empty()) {
            host_message += L"[correlationId=" + utf8_to_wide(*context.correlation_id) + L"]";
        }
        if (context.instance_id.has_value() && !context.instance_id->empty()) {
            host_message += L"[instanceId=" + utf8_to_wide(*context.instance_id) + L"]";
        }
        if (context.operation.has_value() && !context.operation->empty()) {
            host_message += L"[operation=" + utf8_to_wide(*context.operation) + L"]";
        }
        if (context.duration_ms.has_value()) {
            host_message += L"[durationMs=" + utf8_to_wide(std::to_string(*context.duration_ms)) + L"]";
        }
        if (context.result_code.has_value() && !context.result_code->empty()) {
            host_message += L"[resultCode=" + utf8_to_wide(*context.result_code) + L"]";
        }
        host_message += L" " + ring_message_wide;
        host_message = truncate_message(std::move(host_message));

        const native_timestamp timestamp = current_timestamp_utc();
        native_log_entry entry{
            .sequence = 0U,
            .timestamp_utc = timestamp.text,
            .timestamp_unix_ms = timestamp.unix_ms,
            .level = level,
            .source = "bridge",
            .component = std::string(component),
            .event_id = std::string(event_id),
            .correlation_id = context.correlation_id.has_value() && !context.correlation_id->empty()
                ? std::make_optional(std::string(*context.correlation_id))
                : std::nullopt,
            .instance_id = context.instance_id.has_value() && !context.instance_id->empty()
                ? std::make_optional(std::string(*context.instance_id))
                : std::nullopt,
            .operation = context.operation.has_value() && !context.operation->empty()
                ? std::make_optional(std::string(*context.operation))
                : std::nullopt,
            .duration_ms = context.duration_ms,
            .result_code = context.result_code.has_value() && !context.result_code->empty()
                ? std::make_optional(std::string(*context.result_code))
                : std::nullopt,
            .message = ring_message,
        };
        {
            std::scoped_lock lock(mutex_);
            entry.sequence = next_sequence_++;
            if (entries_.size() == capacity_) {
                entries_.pop_front();
                has_evicted_entries_ = true;
            }
            entries_.push_back(std::move(entry));
        }

        OutputDebugStringW(host_message.c_str());
        OutputDebugStringW(L"\n");
        write_to_host(host_logger_.load(), level, host_message);
    } catch (const std::exception& exception) {
        OutputDebugStringA("AviUtl2MCP native logging failed: ");
        OutputDebugStringA(exception.what());
        OutputDebugStringA("\n");
    } catch (...) {
        OutputDebugStringA("AviUtl2MCP native logging failed with an unknown exception\n");
    }
}

native_log_snapshot native_ring_logger::snapshot(const native_log_query& query) const {
    if (query.limit == 0U || query.limit > MAXIMUM_QUERY_LIMIT) {
        throw std::invalid_argument("native log query limit is invalid");
    }

    native_log_snapshot result;
    std::scoped_lock lock(mutex_);
    result.has_evicted_entries = has_evicted_entries_;
    for (const native_log_entry& entry : entries_) {
        if (query.after_sequence.has_value() && entry.sequence <= *query.after_sequence) {
            continue;
        }
        if (query.since_unix_ms.has_value() && entry.timestamp_unix_ms < *query.since_unix_ms) {
            continue;
        }
        if (query.correlation_id.has_value() && entry.correlation_id != query.correlation_id) {
            continue;
        }
        if (query.component.has_value() && entry.component != *query.component) {
            continue;
        }
        if (!includes_level(query.levels, entry.level)) {
            continue;
        }
        if (result.entries.size() == query.limit) {
            result.is_truncated = true;
            break;
        }
        result.entries.push_back(entry);
    }
    if (result.is_truncated && !result.entries.empty()) {
        result.next_sequence = result.entries.back().sequence;
    }
    return result;
}

std::size_t native_ring_logger::capacity() const noexcept {
    return capacity_;
}

std::string_view native_log_level_name(const native_log_level level) noexcept {
    switch (level) {
        case native_log_level::trace:
            return "trace";
        case native_log_level::information:
            return "information";
        case native_log_level::warning:
            return "warning";
        case native_log_level::error:
            return "error";
    }
    return "unknown";
}

native_ring_logger& get_native_logger() noexcept {
    // DLL lifetime ownership avoids cross-translation-unit destruction ordering with bridge_runtime.
    static native_ring_logger* logger = new native_ring_logger();
    return *logger;
}

}  // namespace aviutl2_mcp

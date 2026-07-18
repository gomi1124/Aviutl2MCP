#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

struct LOG_HANDLE;

namespace aviutl2_mcp {

enum class native_log_level {
    trace,
    information,
    warning,
    error,
};

struct native_log_entry final {
    std::uint64_t sequence;
    std::string timestamp_utc;
    native_log_level level;
    std::string source;
    std::string component;
    std::string event_id;
    std::optional<std::string> correlation_id;
    std::optional<std::string> instance_id;
    std::optional<std::string> operation;
    std::optional<double> duration_ms;
    std::optional<std::string> result_code;
    std::string message;
};

struct native_log_context final {
    std::optional<std::string_view> correlation_id;
    std::optional<std::string_view> instance_id;
    std::optional<std::string_view> operation;
    std::optional<double> duration_ms;
    std::optional<std::string_view> result_code;
};

struct native_log_query final {
    std::size_t limit = 100U;
    std::optional<std::uint64_t> after_sequence;
    std::optional<std::string> correlation_id;
    std::optional<std::string> component;
    std::vector<native_log_level> levels;
};

struct native_log_snapshot final {
    std::vector<native_log_entry> entries;
    bool is_truncated = false;
    bool has_evicted_entries = false;
    std::optional<std::uint64_t> next_sequence;
};

class native_ring_logger final {
public:
    explicit native_ring_logger(std::size_t capacity = 512U);

    native_ring_logger(const native_ring_logger&) = delete;
    native_ring_logger& operator=(const native_ring_logger&) = delete;

    void attach(LOG_HANDLE* host_logger) noexcept;
    void write(
        native_log_level level,
        std::string_view component,
        std::string_view event_id,
        std::string_view message,
        native_log_context context = {}) noexcept;

    [[nodiscard]] native_log_snapshot snapshot(const native_log_query& query) const;
    [[nodiscard]] std::size_t capacity() const noexcept;

private:
    const std::size_t capacity_;
    mutable std::mutex mutex_;
    std::deque<native_log_entry> entries_;
    std::uint64_t next_sequence_ = 1U;
    bool has_evicted_entries_ = false;
    std::atomic<LOG_HANDLE*> host_logger_ = nullptr;
};

[[nodiscard]] std::string_view native_log_level_name(native_log_level level) noexcept;
[[nodiscard]] native_ring_logger& get_native_logger() noexcept;

}  // namespace aviutl2_mcp

#pragma once

#include <array>
#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>

namespace aviutl2_mcp {

enum class cancel_status {
    cancelled,
    too_late,
    not_found,
};

struct cancel_result final {
    cancel_status status;
    bool response_will_follow;
};

class cancellation_registry final {
public:
    [[nodiscard]] bool register_request(const std::array<std::uint8_t, 16>& request_id);
    [[nodiscard]] bool try_begin(const std::array<std::uint8_t, 16>& request_id);
    [[nodiscard]] bool try_reach_commit_point(const std::array<std::uint8_t, 16>& request_id);
    [[nodiscard]] bool is_cancelled(const std::array<std::uint8_t, 16>& request_id) const;
    [[nodiscard]] cancel_result cancel(const std::array<std::uint8_t, 16>& request_id);
    void complete(const std::array<std::uint8_t, 16>& request_id) noexcept;
    void cancel_all() noexcept;

    [[nodiscard]] std::size_t count() const;

private:
    enum class request_state {
        queued,
        executing,
        committed,
        cancelled,
    };

    [[nodiscard]] static std::string create_key(const std::array<std::uint8_t, 16>& request_id);

    mutable std::mutex mutex_;
    std::unordered_map<std::string, request_state> requests_;
};

}  // namespace aviutl2_mcp

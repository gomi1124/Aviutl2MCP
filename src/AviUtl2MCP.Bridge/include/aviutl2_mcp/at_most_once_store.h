#pragma once

#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>

namespace aviutl2_mcp {

struct mutation_key final {
    std::string server_epoch;
    std::string client_instance_id;
    std::array<std::uint8_t, 16> request_id;

    [[nodiscard]] bool operator==(const mutation_key& other) const noexcept = default;
};

enum class mutation_state {
    queued,
    executing,
    completed,
};

enum class mutation_begin_decision {
    accepted,
    attach,
    cached,
    request_id_conflict,
    request_expired,
    result_evicted,
    bridge_busy,
};

struct mutation_record final {
    mutation_state state;
    std::string outcome;
    std::string revision;
    std::string result_digest;
    std::optional<std::string> response_json;
};

class mutation_entry;

class mutation_token final {
public:
    mutation_token() = default;
    [[nodiscard]] bool valid() const noexcept;

private:
    explicit mutation_token(std::shared_ptr<mutation_entry> entry);

    std::shared_ptr<mutation_entry> entry_;
    friend class at_most_once_store;
};

struct mutation_begin_result final {
    mutation_begin_decision decision;
    mutation_token token;
    std::optional<mutation_record> record;
};

struct at_most_once_limits final {
    std::size_t maximum_tombstones = 4096U;
    std::size_t maximum_cached_responses = 256U;
    std::size_t maximum_response_bytes = 64U * 1024U;
};

class at_most_once_store final {
public:
    using clock = std::chrono::system_clock;
    using clock_function = std::function<clock::time_point()>;

    explicit at_most_once_store(
        std::string server_epoch,
        at_most_once_limits limits = {},
        clock_function get_current_time = clock::now);
    ~at_most_once_store();

    at_most_once_store(const at_most_once_store&) = delete;
    at_most_once_store& operator=(const at_most_once_store&) = delete;

    [[nodiscard]] mutation_begin_result begin(
        const mutation_key& key,
        const std::string& payload_hash);
    void mark_executing(const mutation_token& token);
    void complete(
        const mutation_token& token,
        std::string outcome,
        std::string revision,
        const std::string& response_json);
    [[nodiscard]] std::optional<mutation_record> wait_for_completion(
        const mutation_token& token,
        std::chrono::milliseconds timeout);

    [[nodiscard]] std::size_t tombstone_count() const;
    [[nodiscard]] std::size_t cached_response_count() const;

private:
    struct implementation;
    std::unique_ptr<implementation> implementation_;
};

}  // namespace aviutl2_mcp

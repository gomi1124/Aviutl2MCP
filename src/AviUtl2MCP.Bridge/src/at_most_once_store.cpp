#include "aviutl2_mcp/at_most_once_store.h"

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/native_ipc_frame_codec.h"

#include <algorithm>
#include <condition_variable>
#include <list>
#include <mutex>
#include <stdexcept>
#include <unordered_map>
#include <utility>
#include <vector>

namespace aviutl2_mcp {
namespace {

constexpr auto MAXIMUM_REQUEST_AGE = std::chrono::minutes(10);
constexpr auto MAXIMUM_FUTURE_SKEW = std::chrono::minutes(5);

struct mutation_key_hash final {
    [[nodiscard]] std::size_t operator()(const mutation_key& key) const noexcept {
        std::size_t hash = std::hash<std::string>{}(key.server_epoch);
        hash ^= std::hash<std::string>{}(key.client_instance_id) + 0x9e3779b9U + (hash << 6U) + (hash >> 2U);
        for (const std::uint8_t value : key.request_id) {
            hash ^= static_cast<std::size_t>(value) + 0x9e3779b9U + (hash << 6U) + (hash >> 2U);
        }
        return hash;
    }
};

[[nodiscard]] bool is_lower_sha256(const std::string& value) noexcept {
    return value.size() == 64U
        && std::ranges::all_of(value, [](const char character) {
            return (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f');
        });
}

[[nodiscard]] std::optional<at_most_once_store::clock::time_point> get_uuid_v7_time(
    const std::array<std::uint8_t, 16>& request_id) {
    if ((request_id[6] >> 4U) != 7U) {
        return std::nullopt;
    }
    std::uint64_t milliseconds = 0U;
    for (std::size_t index = 0U; index < 6U; ++index) {
        milliseconds = (milliseconds << 8U) | request_id[index];
    }
    return at_most_once_store::clock::time_point(std::chrono::milliseconds(milliseconds));
}

[[nodiscard]] std::vector<std::uint8_t> to_bytes(const std::string& value) {
    return {value.begin(), value.end()};
}

}  // namespace

class mutation_entry final {
public:
    mutation_key key;
    std::string payload_hash;
    at_most_once_store::clock::time_point expires_at;
    mutation_state state = mutation_state::queued;
    std::string outcome;
    std::string revision;
    std::string result_digest;
    std::optional<std::string> response_json;
    bool is_cached = false;
    std::list<mutation_key>::iterator cache_position;
    std::condition_variable completion;
};

struct at_most_once_store::implementation final {
    std::string server_epoch;
    at_most_once_limits limits;
    clock_function get_current_time;
    mutable std::mutex mutex;
    std::unordered_map<mutation_key, std::shared_ptr<mutation_entry>, mutation_key_hash> tombstones;
    std::list<mutation_key> response_lru;

    void remove_expired(const clock::time_point now) {
        for (auto iterator = tombstones.begin(); iterator != tombstones.end();) {
            const std::shared_ptr<mutation_entry>& entry = iterator->second;
            if (entry->state != mutation_state::completed || now <= entry->expires_at) {
                ++iterator;
                continue;
            }
            if (entry->is_cached) {
                response_lru.erase(entry->cache_position);
            }
            iterator = tombstones.erase(iterator);
        }
    }

    void touch_cache(const std::shared_ptr<mutation_entry>& entry) {
        if (!entry->is_cached) {
            return;
        }
        response_lru.erase(entry->cache_position);
        response_lru.push_front(entry->key);
        entry->cache_position = response_lru.begin();
    }

    void cache_response(const std::shared_ptr<mutation_entry>& entry, const std::string& response) {
        if (limits.maximum_cached_responses == 0U || response.size() > limits.maximum_response_bytes) {
            return;
        }
        entry->response_json = response;
        response_lru.push_front(entry->key);
        entry->cache_position = response_lru.begin();
        entry->is_cached = true;
        while (response_lru.size() > limits.maximum_cached_responses) {
            const mutation_key evicted_key = response_lru.back();
            response_lru.pop_back();
            const auto found = tombstones.find(evicted_key);
            if (found != tombstones.end()) {
                found->second->response_json.reset();
                found->second->is_cached = false;
            }
        }
    }

    [[nodiscard]] mutation_record create_record(const std::shared_ptr<mutation_entry>& entry) const {
        return mutation_record{
            .state = entry->state,
            .outcome = entry->outcome,
            .revision = entry->revision,
            .result_digest = entry->result_digest,
            .response_json = entry->response_json,
        };
    }
};

mutation_token::mutation_token(std::shared_ptr<mutation_entry> entry)
    : entry_(std::move(entry)) {}

bool mutation_token::valid() const noexcept {
    return entry_ != nullptr;
}

at_most_once_store::at_most_once_store(
    std::string server_epoch,
    const at_most_once_limits limits,
    clock_function get_current_time)
    : implementation_(std::make_unique<implementation>()) {
    if (!is_nonzero_uuid(server_epoch)) {
        throw std::invalid_argument("at-most-once server epoch must be a nonzero UUID");
    }
    if (limits.maximum_tombstones == 0U || limits.maximum_response_bytes == 0U || !get_current_time) {
        throw std::invalid_argument("at-most-once limits and clock must be valid");
    }
    implementation_->server_epoch = std::move(server_epoch);
    implementation_->limits = limits;
    implementation_->get_current_time = std::move(get_current_time);
}

at_most_once_store::~at_most_once_store() = default;

mutation_begin_result at_most_once_store::begin(
    const mutation_key& key,
    const std::string& payload_hash) {
    if (!uuid_equals(key.server_epoch, implementation_->server_epoch)
        || !is_nonzero_uuid(key.client_instance_id)
        || !is_lower_sha256(payload_hash)) {
        throw std::invalid_argument("at-most-once key or payload hash is invalid");
    }
    const auto request_time = get_uuid_v7_time(key.request_id);
    const auto now = implementation_->get_current_time();
    if (!request_time.has_value()
        || *request_time < now - MAXIMUM_REQUEST_AGE
        || *request_time > now + MAXIMUM_FUTURE_SKEW) {
        return {mutation_begin_decision::request_expired, {}, std::nullopt};
    }

    std::scoped_lock lock(implementation_->mutex);
    implementation_->remove_expired(now);
    const auto existing = implementation_->tombstones.find(key);
    if (existing != implementation_->tombstones.end()) {
        const std::shared_ptr<mutation_entry>& entry = existing->second;
        if (entry->payload_hash != payload_hash) {
            return {mutation_begin_decision::request_id_conflict, {}, std::nullopt};
        }
        if (entry->state != mutation_state::completed) {
            return {mutation_begin_decision::attach, mutation_token(entry), std::nullopt};
        }
        const mutation_record record = implementation_->create_record(entry);
        if (entry->response_json.has_value()) {
            implementation_->touch_cache(entry);
            return {mutation_begin_decision::cached, mutation_token(entry), record};
        }
        return {mutation_begin_decision::result_evicted, mutation_token(entry), record};
    }
    if (implementation_->tombstones.size() >= implementation_->limits.maximum_tombstones) {
        return {mutation_begin_decision::bridge_busy, {}, std::nullopt};
    }

    auto entry = std::make_shared<mutation_entry>();
    entry->key = key;
    entry->payload_hash = payload_hash;
    entry->expires_at = *request_time + MAXIMUM_REQUEST_AGE;
    implementation_->tombstones.emplace(entry->key, entry);
    return {mutation_begin_decision::accepted, mutation_token(entry), std::nullopt};
}

void at_most_once_store::mark_executing(const mutation_token& token) {
    if (!token.valid()) {
        throw std::invalid_argument("mutation token is invalid");
    }
    std::scoped_lock lock(implementation_->mutex);
    if (token.entry_->state != mutation_state::queued) {
        throw std::logic_error("mutation was not queued");
    }
    token.entry_->state = mutation_state::executing;
}

void at_most_once_store::complete(
    const mutation_token& token,
    std::string outcome,
    std::string revision,
    const std::string& response_json) {
    if (!token.valid() || outcome.empty() || response_json.empty()) {
        throw std::invalid_argument("mutation completion is invalid");
    }
    const std::string digest = calculate_sha256(to_bytes(response_json));
    {
        std::scoped_lock lock(implementation_->mutex);
        if (token.entry_->state == mutation_state::completed) {
            throw std::logic_error("mutation was already completed");
        }
        token.entry_->state = mutation_state::completed;
        token.entry_->outcome = std::move(outcome);
        token.entry_->revision = std::move(revision);
        token.entry_->result_digest = digest;
        implementation_->cache_response(token.entry_, response_json);
    }
    token.entry_->completion.notify_all();
}

std::optional<mutation_record> at_most_once_store::wait_for_completion(
    const mutation_token& token,
    const std::chrono::milliseconds timeout) {
    if (!token.valid() || timeout <= std::chrono::milliseconds::zero()) {
        throw std::invalid_argument("mutation wait arguments are invalid");
    }
    std::unique_lock lock(implementation_->mutex);
    if (!token.entry_->completion.wait_for(lock, timeout, [&token] {
            return token.entry_->state == mutation_state::completed;
        })) {
        return std::nullopt;
    }
    return implementation_->create_record(token.entry_);
}

std::size_t at_most_once_store::tombstone_count() const {
    std::scoped_lock lock(implementation_->mutex);
    return implementation_->tombstones.size();
}

std::size_t at_most_once_store::cached_response_count() const {
    std::scoped_lock lock(implementation_->mutex);
    return implementation_->response_lru.size();
}

}  // namespace aviutl2_mcp

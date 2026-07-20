#include "aviutl2_mcp/cancellation_registry.h"

#include <algorithm>

namespace aviutl2_mcp {

bool cancellation_registry::register_request(const std::array<std::uint8_t, 16>& request_id) {
    std::scoped_lock lock(mutex_);
    return requests_.emplace(create_key(request_id), request_state::queued).second;
}

bool cancellation_registry::try_begin(const std::array<std::uint8_t, 16>& request_id) {
    std::scoped_lock lock(mutex_);
    const auto found = requests_.find(create_key(request_id));
    if (found == requests_.end() || found->second == request_state::cancelled) {
        return false;
    }
    if (found->second != request_state::queued) {
        return false;
    }
    found->second = request_state::executing;
    return true;
}

bool cancellation_registry::try_reach_commit_point(const std::array<std::uint8_t, 16>& request_id) {
    std::scoped_lock lock(mutex_);
    const auto found = requests_.find(create_key(request_id));
    if (found == requests_.end() || found->second == request_state::cancelled) {
        return false;
    }
    if (found->second == request_state::committed) {
        return true;
    }
    if (found->second != request_state::executing) {
        return false;
    }
    found->second = request_state::committed;
    return true;
}

bool cancellation_registry::is_cancelled(const std::array<std::uint8_t, 16>& request_id) const {
    std::scoped_lock lock(mutex_);
    const auto found = requests_.find(create_key(request_id));
    return found != requests_.end() && found->second == request_state::cancelled;
}

cancel_result cancellation_registry::cancel(const std::array<std::uint8_t, 16>& request_id) {
    std::scoped_lock lock(mutex_);
    const auto found = requests_.find(create_key(request_id));
    if (found == requests_.end()) {
        return {cancel_status::not_found, false};
    }
    if (found->second == request_state::committed) {
        return {cancel_status::too_late, true};
    }
    found->second = request_state::cancelled;
    return {cancel_status::cancelled, true};
}

void cancellation_registry::complete(const std::array<std::uint8_t, 16>& request_id) noexcept {
    std::scoped_lock lock(mutex_);
    requests_.erase(create_key(request_id));
}

void cancellation_registry::cancel_all() noexcept {
    std::scoped_lock lock(mutex_);
    for (auto& [key, state] : requests_) {
        static_cast<void>(key);
        if (state != request_state::committed) {
            state = request_state::cancelled;
        }
    }
}

std::size_t cancellation_registry::count() const {
    std::scoped_lock lock(mutex_);
    return requests_.size();
}

std::string cancellation_registry::create_key(const std::array<std::uint8_t, 16>& request_id) {
    constexpr char HEX[] = "0123456789abcdef";
    std::string key(request_id.size() * 2U, '0');
    for (std::size_t index = 0U; index < request_id.size(); ++index) {
        key[index * 2U] = HEX[request_id[index] >> 4U];
        key[index * 2U + 1U] = HEX[request_id[index] & 0x0fU];
    }
    return key;
}

}  // namespace aviutl2_mcp

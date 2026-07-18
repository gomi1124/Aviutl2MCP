#include "aviutl2_mcp/revision_tracker.h"

#include "aviutl2_mcp/bridge_identity.h"

#include <limits>
#include <stdexcept>
#include <utility>

namespace aviutl2_mcp {

revision_tracker::revision_tracker(std::string server_epoch)
    : server_epoch_(std::move(server_epoch)),
      project_generation_(create_bridge_identity().instance_id) {
    if (!is_nonzero_uuid(server_epoch_)) {
        throw std::invalid_argument("revision server epoch must be a nonzero UUID");
    }
}

void revision_tracker::reset_project(std::string project_generation) {
    if (!is_nonzero_uuid(project_generation)) {
        throw std::invalid_argument("project generation must be a nonzero UUID");
    }
    std::scoped_lock lock(mutex_);
    project_generation_ = std::move(project_generation);
    content_counter_ = 0U;
    view_counter_ = 0U;
}

std::string revision_tracker::content_revision() const {
    std::scoped_lock lock(mutex_);
    return create_revision(content_counter_);
}

std::string revision_tracker::view_revision() const {
    std::scoped_lock lock(mutex_);
    return create_revision(view_counter_);
}

std::pair<std::string, std::string> revision_tracker::revisions() const {
    std::scoped_lock lock(mutex_);
    return {create_revision(content_counter_), create_revision(view_counter_)};
}

bool revision_tracker::matches_content(const std::string& expected_revision) const {
    std::scoped_lock lock(mutex_);
    return expected_revision == create_revision(content_counter_);
}

bool revision_tracker::matches_view(const std::string& expected_revision) const {
    std::scoped_lock lock(mutex_);
    return expected_revision == create_revision(view_counter_);
}

std::string revision_tracker::commit_content_change() {
    std::scoped_lock lock(mutex_);
    if (content_counter_ == (std::numeric_limits<std::uint64_t>::max)()) {
        throw std::overflow_error("content revision counter overflowed");
    }
    return create_revision(++content_counter_);
}

std::string revision_tracker::commit_view_change() {
    std::scoped_lock lock(mutex_);
    if (view_counter_ == (std::numeric_limits<std::uint64_t>::max)()) {
        throw std::overflow_error("view revision counter overflowed");
    }
    return create_revision(++view_counter_);
}

std::pair<std::string, std::string> revision_tracker::commit_scene_change() {
    std::scoped_lock lock(mutex_);
    if (content_counter_ == (std::numeric_limits<std::uint64_t>::max)()
        || view_counter_ == (std::numeric_limits<std::uint64_t>::max)()) {
        throw std::overflow_error("revision counter overflowed");
    }
    ++content_counter_;
    ++view_counter_;
    return {create_revision(content_counter_), create_revision(view_counter_)};
}

std::string revision_tracker::project_generation() const {
    std::scoped_lock lock(mutex_);
    return project_generation_;
}

std::string revision_tracker::create_revision(const std::uint64_t counter) const {
    return server_epoch_ + ":" + project_generation_ + ":" + std::to_string(counter);
}

}  // namespace aviutl2_mcp

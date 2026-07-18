#pragma once

#include <cstdint>
#include <mutex>
#include <string>
#include <utility>

namespace aviutl2_mcp {

class revision_tracker final {
public:
    explicit revision_tracker(std::string server_epoch);

    void reset_project(std::string project_generation);
    [[nodiscard]] std::string content_revision() const;
    [[nodiscard]] std::string view_revision() const;
    [[nodiscard]] std::pair<std::string, std::string> revisions() const;
    [[nodiscard]] bool matches_content(const std::string& expected_revision) const;
    [[nodiscard]] bool matches_view(const std::string& expected_revision) const;

    [[nodiscard]] std::string commit_content_change();
    [[nodiscard]] std::string commit_view_change();
    [[nodiscard]] std::pair<std::string, std::string> commit_scene_change();

    [[nodiscard]] std::string project_generation() const;

private:
    [[nodiscard]] std::string create_revision(std::uint64_t counter) const;

    mutable std::mutex mutex_;
    std::string server_epoch_;
    std::string project_generation_;
    std::uint64_t content_counter_ = 0U;
    std::uint64_t view_counter_ = 0U;
};

}  // namespace aviutl2_mcp

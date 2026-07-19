#pragma once

#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <utility>
#include <vector>

struct EDIT_HANDLE;
struct HOST_APP_TABLE;
struct PROJECT_FILE;

namespace aviutl2_mcp {

enum class sdk_project_state {
    not_open,
    unsaved,
    saved,
    unknown,
};

enum class sdk_edit_state {
    edit,
    play,
    save,
    unknown,
};

struct sdk_status_snapshot final {
    bool is_sdk_ready = false;
    bool has_query_error = false;
    std::string query_error;
    sdk_project_state project_state = sdk_project_state::unknown;
    sdk_edit_state edit_state = sdk_edit_state::unknown;
    std::optional<std::string> project_path;
};

struct sdk_selection final {
    int start_frame;
    int end_frame;
};

struct sdk_scene_summary final {
    int scene_id;
    std::string name;
};

struct sdk_project_snapshot final {
    std::optional<std::string> path;
    bool is_saved;
    int width;
    int height;
    double frame_rate;
    int sample_rate;
    int current_scene_id;
    int current_frame;
    std::vector<int> selected_layers;
    std::optional<sdk_selection> selection;
    std::vector<sdk_scene_summary> scenes;
};

struct sdk_project_query_result final {
    bool ok = false;
    sdk_project_snapshot project{};
    std::string error_code;
    std::string error_message;
};

class sdk_read_facade final {
public:
    sdk_read_facade() = default;
    ~sdk_read_facade();

    sdk_read_facade(const sdk_read_facade&) = delete;
    sdk_read_facade& operator=(const sdk_read_facade&) = delete;

    [[nodiscard]] bool register_host(HOST_APP_TABLE* host) noexcept;
    void detach() noexcept;

    [[nodiscard]] sdk_status_snapshot query_status() const noexcept;
    [[nodiscard]] sdk_project_query_result query_project(bool include_scenes) const noexcept;

    void capture_project(PROJECT_FILE* project) noexcept;

private:
    mutable std::mutex mutex_;
    EDIT_HANDLE* edit_handle_ = nullptr;
    sdk_project_state project_state_ = sdk_project_state::unknown;
    std::optional<std::string> project_path_;
    std::string project_cache_error_;
};

[[nodiscard]] sdk_read_facade& get_sdk_read_facade() noexcept;

}  // namespace aviutl2_mcp

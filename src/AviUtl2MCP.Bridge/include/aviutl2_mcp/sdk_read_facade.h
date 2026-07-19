#pragma once

#include "aviutl2_mcp/locator_resolver.h"

#include <cstdint>
#include <functional>
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

struct sdk_effect_summary final {
    std::string name;
    int occurrence;
    bool is_enabled;
    bool is_locked;
};

struct sdk_object_snapshot final {
    object_candidate candidate;
    bool is_selected;
    std::optional<std::string> media_path;
    std::vector<sdk_effect_summary> effects;
};

struct sdk_layer_snapshot final {
    int scene_id;
    int layer;
    std::string name;
    bool is_visible;
    bool is_locked;
};

struct sdk_timeline_query final {
    std::optional<int> scene_id;
    std::optional<int> layer_start;
    std::optional<int> layer_end;
    std::optional<int> start_frame;
    std::optional<int> end_frame;
    std::optional<std::string> name_contains;
    std::optional<std::string> effect_name;
    std::optional<std::string> media_path;
    std::size_t offset = 0U;
    std::size_t limit = 100U;
    bool include_effects = false;
    bool use_display_defaults = true;
};

struct sdk_timeline_snapshot final {
    std::vector<sdk_layer_snapshot> layers;
    std::vector<sdk_object_snapshot> objects;
    std::size_t next_offset = 0U;
    bool is_truncated = false;
};

struct sdk_timeline_query_result final {
    bool ok = false;
    sdk_timeline_snapshot timeline{};
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
    [[nodiscard]] sdk_timeline_query_result query_timeline(const sdk_timeline_query& query) const noexcept;

    void capture_project(PROJECT_FILE* project, bool is_load = true) noexcept;
    void set_project_loaded_callback(std::function<void()> callback);
    void clear_project_loaded_callback() noexcept;

private:
    mutable std::mutex mutex_;
    EDIT_HANDLE* edit_handle_ = nullptr;
    sdk_project_state project_state_ = sdk_project_state::unknown;
    std::optional<std::string> project_path_;
    std::string project_cache_error_;
    std::function<void()> project_loaded_callback_;
};

[[nodiscard]] sdk_read_facade& get_sdk_read_facade() noexcept;

}  // namespace aviutl2_mcp

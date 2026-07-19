#pragma once

#include "aviutl2_mcp/locator_resolver.h"

#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <utility>
#include <variant>
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

using sdk_effect_item_value = std::variant<bool, std::int64_t, double, std::string>;

struct sdk_effect_item_snapshot final {
    std::string name;
    std::string type;
    std::string codec;
    bool is_writable;
    std::optional<sdk_effect_item_value> value;
    std::vector<std::string> choices;
};

struct sdk_effect_items_group final {
    sdk_effect_summary effect;
    std::vector<sdk_effect_item_snapshot> items;
};

struct sdk_object_detail_snapshot final {
    sdk_object_snapshot object;
    std::optional<std::string> alias;
    std::vector<sdk_effect_items_group> effect_items;
};

struct sdk_object_query_result final {
    bool ok = false;
    sdk_object_detail_snapshot detail{};
    std::string error_code;
    std::string error_message;
};

struct sdk_effect_definition final {
    std::string name;
    std::string type;
    std::vector<std::string> flags;
    bool is_creatable;
};

struct sdk_module_summary final {
    std::string type;
    std::string name;
    std::string information;
};

struct sdk_effect_catalog_query final {
    std::optional<std::string> category;
    std::optional<std::string> name_contains;
    std::size_t offset = 0U;
    std::size_t limit = 100U;
};

struct sdk_effect_catalog_snapshot final {
    std::vector<sdk_effect_definition> effects;
    std::vector<sdk_module_summary> modules;
    std::vector<std::string> fonts;
    std::vector<std::string> palettes;
    std::size_t next_offset = 0U;
    bool is_truncated = false;
};

struct sdk_effect_catalog_query_result final {
    bool ok = false;
    sdk_effect_catalog_snapshot catalog{};
    std::string error_code;
    std::string error_message;
};

struct sdk_effect_items_query_result final {
    bool ok = false;
    std::vector<sdk_effect_item_snapshot> items;
    std::string error_code;
    std::string error_message;
};

enum class sdk_create_kind {
    effect,
    media,
    alias,
};

struct sdk_create_request final {
    sdk_create_kind kind;
    std::string source;
    int scene_id;
    int layer;
    int start_frame;
    int length;
    std::optional<std::string> name;
};

struct sdk_create_result final {
    bool ok = false;
    bool has_changed = false;
    std::vector<sdk_object_snapshot> objects;
    std::string error_code;
    std::string error_message;
};

enum class sdk_object_edit_kind {
    move,
    delete_object,
    set_name,
};

struct sdk_object_edit_request final {
    sdk_object_edit_kind kind;
    object_locator locator;
    std::optional<int> destination_scene_id;
    std::optional<int> destination_layer;
    std::optional<int> destination_start_frame;
    std::optional<std::string> name;
};

struct sdk_object_edit_result final {
    bool ok = false;
    bool has_changed = false;
    bool was_deleted = false;
    std::optional<sdk_object_snapshot> object;
    std::string error_code;
    std::string error_message;
};

enum class sdk_effect_edit_kind {
    set_item,
    set_state,
};

struct sdk_effect_edit_request final {
    sdk_effect_edit_kind kind;
    object_locator locator;
    std::string effect_name;
    int effect_occurrence;
    std::optional<std::string> item_name;
    std::optional<sdk_effect_item_value> item_value;
    std::optional<bool> is_enabled;
    std::optional<bool> is_locked;
};

struct sdk_effect_edit_result final {
    bool ok = false;
    bool has_changed = false;
    std::optional<sdk_effect_summary> effect;
    std::optional<sdk_effect_item_snapshot> item;
    std::string error_code;
    std::string error_message;
};

struct sdk_layer_edit_request final {
    std::optional<int> scene_id;
    int layer;
    std::optional<std::string> name;
    std::optional<bool> is_visible;
    std::optional<bool> is_locked;
};

struct sdk_layer_edit_result final {
    bool ok = false;
    bool has_changed = false;
    std::optional<sdk_layer_snapshot> layer;
    std::string error_code;
    std::string error_message;
};

struct sdk_view_edit_request final {
    std::optional<int> scene_id;
    std::optional<int> frame;
    std::optional<int> display_frame;
    std::optional<sdk_selection> selection;
};

struct sdk_view_snapshot final {
    int scene_id;
    int frame;
    int display_frame;
    std::optional<sdk_selection> selection;
};

struct sdk_view_edit_result final {
    bool ok = false;
    bool has_changed = false;
    std::optional<sdk_view_snapshot> view;
    std::string error_code;
    std::string error_message;
};

struct sdk_preview_render_result final {
    bool ok = false;
    int frame = 0;
    int width = 0;
    int height = 0;
    std::vector<std::uint8_t> rgba;
    std::string error_code;
    std::string error_message;
};

using sdk_batch_request_value = std::variant<
    sdk_create_request,
    sdk_object_edit_request,
    sdk_effect_edit_request,
    sdk_layer_edit_request>;

struct sdk_batch_operation final {
    std::string client_operation_id;
    sdk_batch_request_value request;
};

using sdk_batch_result_value = std::variant<
    sdk_create_result,
    sdk_object_edit_result,
    sdk_effect_edit_result,
    sdk_layer_edit_result>;

struct sdk_batch_operation_result final {
    bool ok = false;
    bool has_changed = false;
    sdk_batch_result_value result = sdk_create_result{};
    std::string error_code;
    std::string error_message;
};

struct sdk_batch_edit_result final {
    bool ok = false;
    bool has_changed = false;
    std::vector<sdk_batch_operation_result> operations;
    std::optional<std::size_t> failed_index;
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
    [[nodiscard]] sdk_object_query_result query_object(
        const object_locator& locator,
        const std::string& current_instance_id,
        const std::string& current_project_generation,
        bool include_alias,
        bool include_effect_items) const noexcept;
    [[nodiscard]] sdk_effect_catalog_query_result query_effects(
        const sdk_effect_catalog_query& query) const noexcept;
    [[nodiscard]] sdk_effect_items_query_result query_effect_items(
        const std::string& effect_name,
        bool include_choices) const noexcept;
    [[nodiscard]] sdk_create_result create_objects(
        const sdk_create_request& request,
        bool dry_run) const noexcept;
    [[nodiscard]] sdk_object_edit_result edit_object(
        const sdk_object_edit_request& request,
        const std::string& current_instance_id,
        const std::string& current_project_generation,
        bool dry_run) const noexcept;
    [[nodiscard]] sdk_effect_edit_result edit_effect(
        const sdk_effect_edit_request& request,
        const std::string& current_instance_id,
        const std::string& current_project_generation,
        bool dry_run) const noexcept;
    [[nodiscard]] sdk_layer_edit_result edit_layer(
        const sdk_layer_edit_request& request,
        bool dry_run) const noexcept;
    [[nodiscard]] sdk_view_edit_result edit_view(
        const sdk_view_edit_request& request,
        bool dry_run) const noexcept;
    [[nodiscard]] sdk_batch_edit_result edit_batch(
        const std::vector<sdk_batch_operation>& operations,
        const std::string& current_instance_id,
        const std::string& current_project_generation,
        bool dry_run) const noexcept;
    [[nodiscard]] sdk_preview_render_result render_preview(
        int frame,
        std::uint32_t timeout_ms) const noexcept;

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

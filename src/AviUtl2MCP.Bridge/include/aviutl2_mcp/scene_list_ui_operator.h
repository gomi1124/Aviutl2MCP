#pragma once

#include <cstdint>
#include <functional>
#include <optional>
#include <string>

namespace aviutl2_mcp {

struct scene_list_target final {
    std::optional<int> scene_id;
    std::optional<std::string> scene_name;
};

struct scene_list_snapshot final {
    int scene_id;
    std::string name;
};

struct scene_list_open_command_result final {
    bool ok = false;
    bool command_was_dispatched = false;
    std::optional<scene_list_snapshot> target;
    std::string error_code;
    std::string error_message;
};

using scene_list_open_command = std::function<scene_list_open_command_result(
    void* host_window,
    const std::string& project_path,
    const scene_list_target& target,
    std::uint32_t timeout_ms)>;

[[nodiscard]] scene_list_open_command_result open_scene_from_list(
    void* host_window,
    const std::string& project_path,
    const scene_list_target& target,
    std::uint32_t timeout_ms);

}  // namespace aviutl2_mcp

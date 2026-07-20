#include "aviutl2_mcp/native_project_request_handler.h"

#include "aviutl2_mcp/native_operation_result.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include <nlohmann/json.hpp>

#include <stdexcept>

namespace aviutl2_mcp {
namespace {

[[nodiscard]] bool parse_include_scenes(const nlohmann::json& params) {
    const auto value = params.find("includeScenes");
    if (value == params.end() || value->is_null()) {
        return true;
    }
    if (!value->is_boolean()) {
        throw std::invalid_argument("includeScenes must be a boolean");
    }
    return value->get<bool>();
}

[[nodiscard]] nlohmann::json serialize_project(const sdk_project_snapshot& project) {
    nlohmann::json selected_layers = nlohmann::json::array();
    for (const int layer : project.selected_layers) {
        selected_layers.push_back(layer);
    }
    nlohmann::json scenes = nlohmann::json::array();
    for (const sdk_scene_summary& scene : project.scenes) {
        scenes.push_back({
            {"sceneId", scene.scene_id},
            {"name", scene.name},
        });
    }
    const nlohmann::json selection = project.selection.has_value()
        ? nlohmann::json{
            {"startFrame", project.selection->start_frame},
            {"endFrame", project.selection->end_frame},
        }
        : nlohmann::json(nullptr);
    return {
        {"path", project.path.has_value() ? nlohmann::json(*project.path) : nlohmann::json(nullptr)},
        {"isSaved", project.is_saved},
        {"width", project.width},
        {"height", project.height},
        {"frameRate", project.frame_rate},
        {"sampleRate", project.sample_rate},
        {"currentSceneId", project.current_scene_id},
        {"currentFrame", project.current_frame},
        {"selectedLayers", std::move(selected_layers)},
        {"selection", selection},
        {"scenes", std::move(scenes)},
        {"coordinateSystem", {
            {"frameBase", 1},
            {"layerBase", 1},
            {"endInclusive", true},
        }},
    };
}

}  // namespace

native_project_request_handler::native_project_request_handler(sdk_read_facade& sdk)
    : sdk_(sdk) {}

std::string native_project_request_handler::operation() const {
    return "project.get";
}

bool native_project_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_project_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Project query parameters must be an object");
        }
        const sdk_project_query_result result = sdk_.query_project(parse_include_scenes(params));
        if (!result.ok) {
            return create_native_failure(
                result.error_code,
                result.error_message,
                context,
                result.error_code == "read_not_available" || result.error_code == "sdk_query_failed");
        }
        return create_native_success(serialize_project(result.project).dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Project query JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    }
}

}  // namespace aviutl2_mcp

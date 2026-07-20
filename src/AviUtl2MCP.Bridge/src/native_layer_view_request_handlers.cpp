#include "aviutl2_mcp/native_layer_view_request_handlers.h"

#include "aviutl2_mcp/native_operation_result.h"

#include <nlohmann/json.hpp>

#include <cstdint>
#include <limits>
#include <optional>
#include <stdexcept>
#include <string>

namespace aviutl2_mcp {
namespace {

[[nodiscard]] int parse_integer(
    const nlohmann::json& object,
    const char* name,
    const int minimum) {
    const auto value = object.find(name);
    if (value == object.end() || (!value->is_number_integer() && !value->is_number_unsigned())) {
        throw std::invalid_argument(std::string(name) + " must be an integer");
    }
    const std::int64_t parsed = value->get<std::int64_t>();
    if (parsed < minimum || parsed > (std::numeric_limits<int>::max)()) {
        throw std::invalid_argument(std::string(name) + " is outside the supported range");
    }
    return static_cast<int>(parsed);
}

[[nodiscard]] std::optional<int> parse_optional_integer(
    const nlohmann::json& object,
    const char* name,
    const int minimum) {
    return object.contains(name)
        ? std::optional<int>(parse_integer(object, name, minimum))
        : std::nullopt;
}

[[nodiscard]] std::optional<bool> parse_optional_boolean(
    const nlohmann::json& object,
    const char* name) {
    const auto value = object.find(name);
    if (value == object.end()) {
        return std::nullopt;
    }
    if (!value->is_boolean()) {
        throw std::invalid_argument(std::string(name) + " must be a boolean");
    }
    return value->get<bool>();
}

[[nodiscard]] std::optional<std::string> parse_optional_name(const nlohmann::json& object) {
    const auto value = object.find("name");
    if (value == object.end()) {
        return std::nullopt;
    }
    if (!value->is_string()) {
        throw std::invalid_argument("name must be a string");
    }
    std::string name = value->get<std::string>();
    if (name.find('\0') != std::string::npos || name.size() > 64U * 1024U) {
        throw std::invalid_argument("name is outside the supported length");
    }
    return name;
}

[[nodiscard]] sdk_layer_edit_request parse_layer_request(const nlohmann::json& params) {
    sdk_layer_edit_request request{
        .scene_id = parse_optional_integer(params, "sceneId", 0),
        .layer = parse_integer(params, "layer", 1),
        .name = parse_optional_name(params),
        .is_visible = parse_optional_boolean(params, "isVisible"),
        .is_locked = parse_optional_boolean(params, "isLocked"),
    };
    if (!request.name.has_value() && !request.is_visible.has_value()
        && !request.is_locked.has_value()) {
        throw std::invalid_argument("At least one layer property is required");
    }
    return request;
}

[[nodiscard]] sdk_view_edit_request parse_view_request(const nlohmann::json& params) {
    std::optional<sdk_selection> selection;
    const auto raw_selection = params.find("selection");
    if (raw_selection != params.end()) {
        if (!raw_selection->is_object()) {
            throw std::invalid_argument("selection must be an object");
        }
        const int start = parse_integer(*raw_selection, "startFrame", 1);
        const int end = parse_integer(*raw_selection, "endFrame", 1);
        if (end < start) {
            throw std::invalid_argument("selection endFrame must not precede startFrame");
        }
        selection = sdk_selection{.start_frame = start, .end_frame = end};
    }
    sdk_view_edit_request request{
        .scene_id = parse_optional_integer(params, "sceneId", 0),
        .frame = parse_optional_integer(params, "frame", 1),
        .display_frame = parse_optional_integer(params, "displayFrame", 1),
        .selection = selection,
    };
    if (!request.frame.has_value() && !request.display_frame.has_value()
        && !request.selection.has_value()) {
        throw std::invalid_argument("At least one view property is required");
    }
    return request;
}

[[nodiscard]] nlohmann::json serialize_layer(const sdk_layer_snapshot& layer) {
    return {
        {"sceneId", layer.scene_id},
        {"layer", layer.layer},
        {"name", layer.name},
        {"isVisible", layer.is_visible},
        {"isLocked", layer.is_locked},
    };
}

[[nodiscard]] nlohmann::json serialize_view(const sdk_view_snapshot& view) {
    nlohmann::json selection = nullptr;
    if (view.selection.has_value()) {
        selection = {
            {"startFrame", view.selection->start_frame},
            {"endFrame", view.selection->end_frame},
        };
    }
    return {
        {"sceneId", view.scene_id},
        {"frame", view.frame},
        {"displayFrame", view.display_frame},
        {"selection", std::move(selection)},
        {"coordinateSystem", {
            {"frameBase", 1},
            {"layerBase", 1},
            {"endInclusive", true},
        }},
    };
}

[[nodiscard]] nlohmann::json create_layer_changes(
    const sdk_layer_edit_request& request,
    const sdk_layer_snapshot& before) {
    nlohmann::json after{
        {"name", request.name.value_or(before.name)},
        {"isVisible", request.is_visible.value_or(before.is_visible)},
        {"isLocked", request.is_locked.value_or(before.is_locked)},
    };
    return nlohmann::json::array({nlohmann::json{
        {"kind", "setLayer"},
        {"target", "layer:" + std::to_string(before.scene_id)
            + "/" + std::to_string(before.layer)},
        {"before", {
            {"name", before.name},
            {"isVisible", before.is_visible},
            {"isLocked", before.is_locked},
        }},
        {"after", std::move(after)},
    }});
}

[[nodiscard]] operation_result create_partial_failure(
    operation_execution_context& context,
    const char* message,
    const bool undo_recommended) {
    return operation_result{
        .ok = false,
        .outcome = "partial",
        .result_json = {},
        .error_code = "partial_operation",
        .error_message = message,
        .revision = context.revisions().content_revision(),
        .view_revision = context.revisions().view_revision(),
        .retryable = false,
        .undo_recommended = undo_recommended,
    };
}

}  // namespace

native_layer_request_handler::native_layer_request_handler(sdk_read_facade& sdk)
    : sdk_(sdk) {}

std::string native_layer_request_handler::operation() const {
    return "layer.set";
}

bool native_layer_request_handler::is_mutating() const noexcept {
    return true;
}

operation_result native_layer_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        if (!request.expected_revision.has_value()
            || !context.revisions().matches_content(*request.expected_revision)) {
            return create_native_failure(
                "revision_conflict",
                "The expected content revision does not match the current revision",
                context);
        }
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Layer edit parameters must be an object");
        }
        const sdk_layer_edit_request layer_request = parse_layer_request(params);
        const sdk_layer_edit_result preflight = sdk_.edit_layer(layer_request, true);
        if (!preflight.ok || !preflight.layer.has_value()) {
            return create_native_failure(
                preflight.error_code.empty() ? "sdk_query_failed" : preflight.error_code,
                preflight.error_message.empty()
                    ? "Layer edit preflight omitted its state"
                    : preflight.error_message,
                context,
                preflight.error_code == "read_not_available"
                    || preflight.error_code == "sdk_query_failed");
        }
        const nlohmann::json changes = create_layer_changes(layer_request, *preflight.layer);
        if (request.dry_run) {
            return create_native_success(nlohmann::json{{"plannedChanges", changes}}.dump(), context);
        }
        if (!context.reach_commit_point()) {
            return create_native_failure(
                "operation_cancelled", "Layer edit was cancelled before commit", context);
        }
        const sdk_layer_edit_result edited = sdk_.edit_layer(layer_request, false);
        if (!edited.ok) {
            if (edited.has_changed) {
                static_cast<void>(context.revisions().commit_content_change());
                return create_partial_failure(
                    context, "The layer changed but postcondition verification failed", true);
            }
            return create_native_failure(
                edited.error_code, edited.error_message, context,
                edited.error_code == "sdk_query_failed");
        }
        if (!edited.layer.has_value()) {
            if (edited.has_changed) {
                static_cast<void>(context.revisions().commit_content_change());
                return create_partial_failure(
                    context, "The layer changed but its postcondition was omitted", true);
            }
            return create_native_failure(
                "sdk_query_failed", "Layer edit omitted its postcondition", context, true);
        }
        if (edited.has_changed) {
            static_cast<void>(context.revisions().commit_content_change());
        }
        return create_native_success(nlohmann::json{
            {"layer", serialize_layer(*edited.layer)},
            {"appliedChanges", changes},
        }.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Layer edit request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

native_view_request_handler::native_view_request_handler(sdk_read_facade& sdk)
    : sdk_(sdk) {}

std::string native_view_request_handler::operation() const {
    return "view.setCursor";
}

bool native_view_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_view_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("View edit parameters must be an object");
        }
        const auto expected = params.find("expectedViewRevision");
        if (expected != params.end()) {
            if (!expected->is_string()
                || !context.revisions().matches_view(expected->get<std::string>())) {
                return create_native_failure(
                    "revision_conflict",
                    "The expected view revision does not match the current revision",
                    context);
            }
        }
        const sdk_view_edit_request view_request = parse_view_request(params);
        const sdk_view_edit_result preflight = sdk_.edit_view(view_request, true);
        if (!preflight.ok || !preflight.view.has_value()) {
            return create_native_failure(
                preflight.error_code.empty() ? "sdk_query_failed" : preflight.error_code,
                preflight.error_message.empty()
                    ? "View edit preflight omitted its state"
                    : preflight.error_message,
                context,
                preflight.error_code == "read_not_available"
                    || preflight.error_code == "sdk_query_failed");
        }
        if (!context.reach_commit_point()) {
            return create_native_failure(
                "operation_cancelled", "View edit was cancelled before commit", context);
        }
        const sdk_view_edit_result edited = sdk_.edit_view(view_request, false);
        if (!edited.ok) {
            if (edited.has_changed) {
                static_cast<void>(context.revisions().commit_view_change());
                return create_partial_failure(
                    context, "The view changed but postcondition verification failed", false);
            }
            return create_native_failure(
                edited.error_code, edited.error_message, context,
                edited.error_code == "sdk_query_failed");
        }
        if (!edited.view.has_value()) {
            if (edited.has_changed) {
                static_cast<void>(context.revisions().commit_view_change());
                return create_partial_failure(
                    context, "The view changed but its postcondition was omitted", false);
            }
            return create_native_failure(
                "sdk_query_failed", "View edit omitted its postcondition", context, true);
        }
        if (edited.has_changed) {
            static_cast<void>(context.revisions().commit_view_change());
        }
        return create_native_success(serialize_view(*edited.view).dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "View edit request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

#include "aviutl2_mcp/native_psd_request_handlers.h"

#include "aviutl2_mcp/locator_resolver.h"
#include "aviutl2_mcp/native_operation_result.h"
#include "aviutl2_mcp/psd_contract.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cstdint>
#include <limits>
#include <optional>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace aviutl2_mcp {
namespace {

[[nodiscard]] std::optional<int> parse_optional_integer(
    const nlohmann::json& params,
    const char* name,
    const int minimum) {
    const auto value = params.find(name);
    if (value == params.end() || value->is_null()) {
        return std::nullopt;
    }
    if (!value->is_number_integer() && !value->is_number_unsigned()) {
        throw std::invalid_argument(std::string(name) + " must be an integer");
    }
    const std::int64_t parsed = value->get<std::int64_t>();
    if (parsed < minimum || parsed > (std::numeric_limits<int>::max)()) {
        throw std::invalid_argument(std::string(name) + " is outside the supported range");
    }
    return static_cast<int>(parsed);
}

[[nodiscard]] bool parse_create_if_missing(const nlohmann::json& params) {
    const auto value = params.find("createIfMissing");
    if (value == params.end() || value->is_null()) {
        return true;
    }
    if (!value->is_boolean()) {
        throw std::invalid_argument("createIfMissing must be a boolean");
    }
    return value->get<bool>();
}

[[nodiscard]] bool has_effect(
    const sdk_object_snapshot& object,
    const std::string_view effect_name) noexcept {
    return std::ranges::any_of(object.effects, [effect_name](const sdk_effect_summary& effect) {
        return effect.name == effect_name;
    });
}

[[nodiscard]] nlohmann::json serialize_effect(const sdk_effect_summary& effect) {
    return {
        {"name", effect.name},
        {"occurrence", effect.occurrence},
        {"isEnabled", effect.is_enabled},
        {"isLocked", effect.is_locked},
    };
}

[[nodiscard]] nlohmann::json serialize_object(
    const sdk_object_snapshot& object,
    const bridge_identity& identity,
    const std::string& project_generation) {
    const object_locator locator = create_object_locator(
        identity.instance_id,
        project_generation,
        object.candidate);
    nlohmann::json effects = nlohmann::json::array();
    for (const sdk_effect_summary& effect : object.effects) {
        effects.push_back(serialize_effect(effect));
    }
    nlohmann::json result{
        {"locator", {
            {"instanceId", locator.instance_id},
            {"projectGeneration", locator.project_generation},
            {"sceneId", locator.scene_id},
            {"layer", locator.layer},
            {"startFrame", locator.start_frame},
            {"endFrame", locator.end_frame},
            {"name", locator.name},
            {"aliasSha256", locator.alias_sha256},
            {"effectSignatureSha256", locator.effect_signature_sha256},
        }},
        {"name", object.candidate.name},
        {"sceneId", object.candidate.scene_id},
        {"layer", object.candidate.layer},
        {"startFrame", object.candidate.start_frame},
        {"endFrame", object.candidate.end_frame},
        {"isSelected", object.is_selected},
        {"effects", std::move(effects)},
    };
    if (object.media_path.has_value()) {
        result["mediaPath"] = *object.media_path;
    }
    return result;
}

[[nodiscard]] nlohmann::json serialize_objects(
    const std::vector<sdk_object_snapshot>& objects,
    const bridge_identity& identity,
    const std::string& project_generation) {
    nlohmann::json result = nlohmann::json::array();
    for (const sdk_object_snapshot& object : objects) {
        result.push_back(serialize_object(object, identity, project_generation));
    }
    return result;
}

[[nodiscard]] bool is_setup_placement_valid(
    const sdk_object_snapshot& setup,
    const sdk_timeline_snapshot& timeline) noexcept {
    return std::ranges::none_of(timeline.objects, [&setup](const sdk_object_snapshot& object) {
        return has_effect(object, PSD_FILE_EFFECT)
            && object.candidate.layer <= setup.candidate.layer;
    });
}

[[nodiscard]] int get_timeline_end(
    const sdk_timeline_snapshot& timeline,
    const int current_frame) noexcept {
    int end_frame = (std::max)(1, current_frame);
    for (const sdk_object_snapshot& object : timeline.objects) {
        end_frame = (std::max)(end_frame, object.candidate.end_frame);
    }
    return end_frame;
}

[[nodiscard]] bool has_collision(
    const sdk_timeline_snapshot& timeline,
    const int layer,
    const int start_frame,
    const int end_frame) noexcept {
    return std::ranges::any_of(timeline.objects, [=](const sdk_object_snapshot& object) {
        return object.candidate.layer == layer
            && object.candidate.start_frame <= end_frame
            && object.candidate.end_frame >= start_frame;
    });
}

[[nodiscard]] int choose_setup_layer(
    const sdk_timeline_snapshot& timeline,
    const std::optional<int> preferred_layer,
    const int start_frame,
    const int end_frame) {
    if (preferred_layer.has_value()) {
        if (has_collision(timeline, *preferred_layer, start_frame, end_frame)) {
            throw std::invalid_argument("preferredLayer overlaps an existing object");
        }
        return *preferred_layer;
    }
    const int maximum_layer = timeline.layers.empty()
        ? 100
        : (std::max)(100, timeline.layers.back().layer + 1);
    for (int layer = 1; layer <= maximum_layer; ++layer) {
        if (!has_collision(timeline, layer, start_frame, end_frame)) {
            return layer;
        }
    }
    throw std::invalid_argument("No collision-free layer is available for PSD setup");
}

[[nodiscard]] nlohmann::json create_setup_change(
    const int scene_id,
    const int layer,
    const int start_frame,
    const int end_frame) {
    return nlohmann::json::array({nlohmann::json{
        {"kind", "createPsdSetup"},
        {"target", "scene:" + std::to_string(scene_id)
            + "/layer:" + std::to_string(layer)
            + "/frames:" + std::to_string(start_frame) + "-" + std::to_string(end_frame)},
    }});
}

[[nodiscard]] operation_result create_partial_failure(
    const std::string& message,
    operation_execution_context& context) {
    return operation_result{
        .ok = false,
        .outcome = "partial",
        .error_code = "partial_operation",
        .error_message = message,
        .revision = context.revisions().content_revision(),
        .view_revision = context.revisions().view_revision(),
        .undo_recommended = true,
    };
}

}  // namespace

native_psd_setup_request_handler::native_psd_setup_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk)
    : identity_(std::move(identity)),
      sdk_(sdk) {}

std::string native_psd_setup_request_handler::operation() const {
    return "psd.setup";
}

bool native_psd_setup_request_handler::is_mutating() const noexcept {
    return true;
}

operation_result native_psd_setup_request_handler::execute(
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
            throw std::invalid_argument("PSD setup parameters must be an object");
        }
        const std::optional<int> requested_scene = parse_optional_integer(params, "sceneId", 0);
        const std::optional<int> preferred_layer = parse_optional_integer(
            params, "preferredLayer", 1);
        const std::optional<int> preferred_frame = parse_optional_integer(
            params, "preferredFrame", 1);
        const bool create_if_missing = parse_create_if_missing(params);

        const sdk_project_query_result project = sdk_.query_project(false);
        if (!project.ok) {
            return create_native_failure(
                project.error_code, project.error_message, context,
                project.error_code == "sdk_query_failed");
        }
        const int scene_id = requested_scene.value_or(project.project.current_scene_id);
        if (scene_id != project.project.current_scene_id) {
            return create_native_failure(
                "invalid_argument",
                "PSD setup can only target the active SDK scene",
                context);
        }
        const sdk_timeline_query_result timeline_result = sdk_.query_timeline({
            .scene_id = scene_id,
            .offset = 0U,
            .limit = 1'000U,
            .include_effects = true,
            .use_display_defaults = false,
        });
        if (!timeline_result.ok) {
            return create_native_failure(
                timeline_result.error_code,
                timeline_result.error_message,
                context,
                timeline_result.error_code == "sdk_query_failed");
        }
        if (timeline_result.timeline.is_truncated) {
            return create_native_failure(
                "sdk_query_failed",
                "PSD setup scan exceeded the supported timeline page",
                context,
                true);
        }
        std::vector<sdk_object_snapshot> setup_objects;
        for (const sdk_object_snapshot& object : timeline_result.timeline.objects) {
            if (has_effect(object, PSD_SETUP_EFFECT)) {
                setup_objects.push_back(object);
            }
        }
        const std::string project_generation = context.revisions().project_generation();
        if (setup_objects.size() > 1U) {
            return create_native_success(nlohmann::json{
                {"objects", serialize_objects(setup_objects, identity_, project_generation)},
                {"created", false},
                {"placementStatus", "ambiguous"},
            }.dump(), context);
        }
        if (setup_objects.size() == 1U) {
            return create_native_success(nlohmann::json{
                {"objects", serialize_objects(setup_objects, identity_, project_generation)},
                {"created", false},
                {"placementStatus", is_setup_placement_valid(
                    setup_objects.front(), timeline_result.timeline) ? "valid" : "misplaced"},
            }.dump(), context);
        }
        if (!create_if_missing) {
            return create_native_success(nlohmann::json{
                {"objects", nlohmann::json::array()},
                {"created", false},
                {"placementStatus", "missing"},
            }.dump(), context);
        }

        const int start_frame = preferred_frame.value_or(1);
        const int end_frame = (std::max)(
            start_frame,
            get_timeline_end(timeline_result.timeline, project.project.current_frame));
        const int layer = choose_setup_layer(
            timeline_result.timeline,
            preferred_layer,
            start_frame,
            end_frame);
        const sdk_create_request create_request{
            .kind = sdk_create_kind::effect,
            .source = PSD_SETUP_EFFECT,
            .scene_id = scene_id,
            .layer = layer,
            .start_frame = start_frame,
            .length = end_frame - start_frame + 1,
        };
        const sdk_create_result preflight = sdk_.create_objects(create_request, true);
        if (!preflight.ok) {
            return create_native_failure(
                preflight.error_code,
                preflight.error_message,
                context,
                preflight.error_code == "read_not_available"
                    || preflight.error_code == "sdk_query_failed");
        }
        const nlohmann::json changes = create_setup_change(
            scene_id, layer, start_frame, end_frame);
        if (request.dry_run) {
            return create_native_success(nlohmann::json{
                {"objects", nlohmann::json::array()},
                {"created", false},
                {"placementStatus", "missing"},
                {"plannedChanges", changes},
            }.dump(), context);
        }
        if (!context.reach_commit_point()) {
            return create_native_failure(
                "operation_cancelled",
                "PSD setup was cancelled before commit",
                context);
        }
        const sdk_create_result created = sdk_.create_objects(create_request, false);
        if (!created.ok) {
            if (created.has_changed) {
                static_cast<void>(context.revisions().commit_content_change());
                return create_partial_failure(
                    "PSD setup changed the project but verification failed",
                    context);
            }
            return create_native_failure(
                created.error_code,
                created.error_message,
                context,
                created.error_code == "sdk_query_failed");
        }
        if (created.objects.size() != 1U || !has_effect(created.objects.front(), PSD_SETUP_EFFECT)) {
            static_cast<void>(context.revisions().commit_content_change());
            return create_partial_failure(
                "PSD setup creation returned an unexpected object",
                context);
        }
        static_cast<void>(context.revisions().commit_content_change());
        return create_native_success(nlohmann::json{
            {"objects", serialize_objects(
                created.objects, identity_, context.revisions().project_generation())},
            {"created", true},
            {"placementStatus", "valid"},
            {"appliedChanges", changes},
        }.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure(
            "invalid_argument", "PSD setup request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

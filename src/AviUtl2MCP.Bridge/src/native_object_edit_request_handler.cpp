#include "aviutl2_mcp/native_object_edit_request_handler.h"

#include "aviutl2_mcp/locator_resolver.h"
#include "aviutl2_mcp/native_operation_result.h"

#include <nlohmann/json.hpp>

#include <cstdint>
#include <limits>
#include <optional>
#include <stdexcept>
#include <string_view>
#include <utility>

namespace aviutl2_mcp {
namespace {

[[nodiscard]] std::size_t count_utf8_characters(const std::string_view value) noexcept {
    std::size_t count = 0U;
    for (const unsigned char byte : value) {
        if ((byte & 0xc0U) != 0x80U) {
            ++count;
        }
    }
    return count;
}

[[nodiscard]] std::string parse_string(
    const nlohmann::json& object,
    const char* name,
    const bool can_be_empty = false) {
    const auto value = object.find(name);
    if (value == object.end() || !value->is_string()) {
        throw std::invalid_argument(std::string(name) + " must be a string");
    }
    std::string parsed = value->get<std::string>();
    if ((!can_be_empty && parsed.empty()) || parsed.find('\0') != std::string::npos
        || count_utf8_characters(parsed) > 4096U) {
        throw std::invalid_argument(std::string(name) + " is outside the supported length");
    }
    return parsed;
}

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

[[nodiscard]] bool is_sha256(const std::string_view value) noexcept {
    return value.size() == 64U && std::ranges::all_of(value, [](const char character) {
        return (character >= '0' && character <= '9')
            || (character >= 'a' && character <= 'f');
    });
}

[[nodiscard]] object_locator parse_locator(const nlohmann::json& params) {
    const auto value = params.find("locator");
    if (value == params.end() || !value->is_object()) {
        throw std::invalid_argument("locator must be an object");
    }
    const nlohmann::json& locator = *value;
    object_locator result{
        .instance_id = parse_string(locator, "instanceId"),
        .project_generation = parse_string(locator, "projectGeneration"),
        .scene_id = parse_integer(locator, "sceneId", 0),
        .layer = parse_integer(locator, "layer", 1),
        .start_frame = parse_integer(locator, "startFrame", 1),
        .end_frame = parse_integer(locator, "endFrame", 1),
        .name = parse_string(locator, "name", true),
        .alias_sha256 = parse_string(locator, "aliasSha256"),
        .effect_signature_sha256 = parse_string(locator, "effectSignatureSha256"),
    };
    if (!is_nonzero_uuid(result.instance_id) || !is_nonzero_uuid(result.project_generation)
        || result.end_frame < result.start_frame || !is_sha256(result.alias_sha256)
        || !is_sha256(result.effect_signature_sha256)) {
        throw std::invalid_argument("locator fields are invalid");
    }
    return result;
}

[[nodiscard]] sdk_object_edit_request parse_edit_request(
    const nlohmann::json& params,
    const sdk_object_edit_kind kind) {
    sdk_object_edit_request request{
        .kind = kind,
        .locator = parse_locator(params),
    };
    if (kind == sdk_object_edit_kind::move) {
        const auto placement = params.find("placement");
        if (placement == params.end() || !placement->is_object()) {
            throw std::invalid_argument("placement must be an object");
        }
        request.destination_scene_id = parse_integer(*placement, "sceneId", 0);
        request.destination_layer = parse_integer(*placement, "layer", 1);
        request.destination_start_frame = parse_integer(*placement, "startFrame", 1);
    } else if (kind == sdk_object_edit_kind::set_name) {
        request.name = parse_string(params, "name", true);
    }
    return request;
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
    nlohmann::json result = {
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

[[nodiscard]] nlohmann::json create_changes(
    const sdk_object_edit_request& request,
    const sdk_object_snapshot& before) {
    nlohmann::json change{
        {"kind", request.kind == sdk_object_edit_kind::move ? "move"
            : request.kind == sdk_object_edit_kind::delete_object ? "delete"
            : "setName"},
        {"target", "object:" + std::to_string(before.candidate.scene_id)
            + "/" + std::to_string(before.candidate.layer)
            + "/" + std::to_string(before.candidate.start_frame)},
    };
    if (request.kind == sdk_object_edit_kind::move) {
        change["before"] = {
            {"sceneId", before.candidate.scene_id},
            {"layer", before.candidate.layer},
            {"startFrame", before.candidate.start_frame},
        };
        change["after"] = {
            {"sceneId", *request.destination_scene_id},
            {"layer", *request.destination_layer},
            {"startFrame", *request.destination_start_frame},
        };
    } else if (request.kind == sdk_object_edit_kind::set_name) {
        change["before"] = before.candidate.name;
        change["after"] = *request.name;
    } else {
        change["before"] = "present";
        change["after"] = "deleted";
    }
    return nlohmann::json::array({std::move(change)});
}

[[nodiscard]] operation_result create_partial_failure(
    operation_execution_context& context) {
    return operation_result{
        .ok = false,
        .outcome = "partial",
        .result_json = {},
        .error_code = "partial_operation",
        .error_message = "The object changed but postcondition verification failed",
        .revision = context.revisions().content_revision(),
        .view_revision = context.revisions().view_revision(),
        .retryable = false,
        .undo_recommended = true,
    };
}

[[nodiscard]] bool is_noop(
    const sdk_object_edit_request& request,
    const sdk_object_snapshot& before) noexcept {
    if (request.kind == sdk_object_edit_kind::move) {
        return request.destination_scene_id == before.candidate.scene_id
            && request.destination_layer == before.candidate.layer
            && request.destination_start_frame == before.candidate.start_frame;
    }
    return request.kind == sdk_object_edit_kind::set_name
        && request.name == before.candidate.name;
}

}  // namespace

native_object_edit_request_handler::native_object_edit_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk,
    std::string operation,
    const sdk_object_edit_kind kind)
    : identity_(std::move(identity)),
      sdk_(sdk),
      operation_(std::move(operation)),
      kind_(kind) {
    if (operation_.empty()) {
        throw std::invalid_argument("Object edit operation name must not be empty");
    }
}

std::string native_object_edit_request_handler::operation() const {
    return operation_;
}

bool native_object_edit_request_handler::is_mutating() const noexcept {
    return true;
}

operation_result native_object_edit_request_handler::execute(
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
            throw std::invalid_argument("Object edit parameters must be an object");
        }
        const sdk_object_edit_request edit_request = parse_edit_request(params, kind_);
        const std::string project_generation = context.revisions().project_generation();
        const sdk_object_edit_result preflight = sdk_.edit_object(
            edit_request, identity_.instance_id, project_generation, true);
        if (!preflight.ok || !preflight.object.has_value()) {
            return create_native_failure(
                preflight.error_code.empty() ? "sdk_query_failed" : preflight.error_code,
                preflight.error_message.empty()
                    ? "Object edit preflight omitted the target object"
                    : preflight.error_message,
                context,
                preflight.error_code == "read_not_available"
                    || preflight.error_code == "sdk_query_failed");
        }
        const nlohmann::json changes = create_changes(edit_request, *preflight.object);
        if (request.dry_run) {
            return create_native_success(nlohmann::json{{"plannedChanges", changes}}.dump(), context);
        }
        if (is_noop(edit_request, *preflight.object)) {
            return create_native_success(nlohmann::json{
                {"object", serialize_object(*preflight.object, identity_, project_generation)},
                {"appliedChanges", changes},
            }.dump(), context);
        }
        if (!context.reach_commit_point()) {
            return create_native_failure(
                "operation_cancelled",
                "Object edit was cancelled before the edit section",
                context);
        }
        const sdk_object_edit_result edited = sdk_.edit_object(
            edit_request, identity_.instance_id, project_generation, false);
        if (!edited.ok) {
            if (edited.has_changed) {
                static_cast<void>(context.revisions().commit_content_change());
                return create_partial_failure(context);
            }
            return create_native_failure(
                edited.error_code,
                edited.error_message,
                context,
                edited.error_code == "sdk_query_failed");
        }
        if (!edited.object.has_value()) {
            if (edited.has_changed) {
                static_cast<void>(context.revisions().commit_content_change());
                return create_partial_failure(context);
            }
            return create_native_failure(
                "sdk_query_failed", "Object edit omitted its postcondition", context, true);
        }
        if (edited.has_changed) {
            static_cast<void>(context.revisions().commit_content_change());
        }
        nlohmann::json result{
            {"object", serialize_object(
                *edited.object,
                identity_,
                context.revisions().project_generation())},
            {"appliedChanges", changes},
        };
        if (kind_ == sdk_object_edit_kind::delete_object) {
            result["deleted"] = edited.was_deleted;
        }
        return create_native_success(result.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Object edit request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

#include "aviutl2_mcp/native_create_request_handler.h"

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

constexpr std::size_t MAXIMUM_ALIAS_BYTES = 1024U * 1024U;

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
    const std::size_t maximum_characters,
    const bool can_be_empty = false) {
    const auto value = object.find(name);
    if (value == object.end() || !value->is_string()) {
        throw std::invalid_argument(std::string(name) + " must be a string");
    }
    std::string parsed = value->get<std::string>();
    if ((!can_be_empty && parsed.empty()) || parsed.find('\0') != std::string::npos
        || count_utf8_characters(parsed) > maximum_characters) {
        throw std::invalid_argument(std::string(name) + " is outside the supported length");
    }
    return parsed;
}

[[nodiscard]] std::optional<std::string> parse_optional_name(const nlohmann::json& params) {
    const auto value = params.find("name");
    if (value == params.end() || value->is_null()) {
        return std::nullopt;
    }
    return parse_string(params, "name", 4096U, true);
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

[[nodiscard]] sdk_create_request parse_create_request(
    const nlohmann::json& params,
    const sdk_create_kind kind) {
    const auto placement_value = params.find("placement");
    if (placement_value == params.end() || !placement_value->is_object()) {
        throw std::invalid_argument("placement must be an object");
    }
    const nlohmann::json& placement = *placement_value;
    const int scene_id = parse_integer(placement, "sceneId", 0);
    const int layer = parse_integer(placement, "layer", 1);
    const int start_frame = parse_integer(placement, "startFrame", 1);
    const bool has_end = placement.contains("endFrame") && !placement.at("endFrame").is_null();
    const bool has_duration = placement.contains("durationFrames")
        && !placement.at("durationFrames").is_null();
    if (has_end == has_duration) {
        throw std::invalid_argument(
            "placement must contain exactly one of endFrame or durationFrames");
    }
    int length = 0;
    if (has_end) {
        const int end_frame = parse_integer(placement, "endFrame", start_frame);
        const std::int64_t calculated = static_cast<std::int64_t>(end_frame) - start_frame + 1;
        if (calculated > (std::numeric_limits<int>::max)()) {
            throw std::invalid_argument("placement duration is outside the supported range");
        }
        length = static_cast<int>(calculated);
    } else {
        length = parse_integer(placement, "durationFrames", 1);
    }

    std::string source;
    switch (kind) {
        case sdk_create_kind::effect: {
            const auto effect = params.find("effect");
            if (effect == params.end() || !effect->is_object()) {
                throw std::invalid_argument("effect must be an object");
            }
            source = parse_string(*effect, "name", 4096U);
            const auto items = params.find("items");
            if (items != params.end() && !items->is_null()
                && (!items->is_array() || !items->empty())) {
                throw std::invalid_argument(
                    "Initial effect item writes require a verified writable codec");
            }
            break;
        }
        case sdk_create_kind::media:
            source = parse_string(params, "mediaPath", 32'767U);
            break;
        case sdk_create_kind::alias:
            source = parse_string(params, "alias", MAXIMUM_ALIAS_BYTES);
            if (source.size() > MAXIMUM_ALIAS_BYTES) {
                throw std::invalid_argument("alias exceeds the supported UTF-8 byte limit");
            }
            break;
    }
    return sdk_create_request{
        .kind = kind,
        .source = std::move(source),
        .scene_id = scene_id,
        .layer = layer,
        .start_frame = start_frame,
        .length = length,
        .name = parse_optional_name(params),
    };
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
    nlohmann::json sections = nlohmann::json::array();
    for (std::size_t index = 0U; index < object.section_frames.size(); ++index) {
        sections.push_back({{"index", index}, {"startFrame", object.section_frames[index]}});
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
        {"sections", std::move(sections)},
        {"effects", std::move(effects)},
    };
    if (object.media_path.has_value()) {
        result["mediaPath"] = *object.media_path;
    }
    return result;
}

[[nodiscard]] nlohmann::json create_change(const sdk_create_request& request) {
    return {
        {"kind", "create"},
        {"target", "scene:" + std::to_string(request.scene_id)
            + "/layer:" + std::to_string(request.layer)
            + "/frame:" + std::to_string(request.start_frame)},
    };
}

[[nodiscard]] operation_result create_sdk_failure(
    const sdk_create_result& result,
    operation_execution_context& context) {
    return create_native_failure(
        result.error_code,
        result.error_message,
        context,
        result.error_code == "read_not_available" || result.error_code == "sdk_query_failed");
}

[[nodiscard]] operation_result create_partial_failure(
    const std::string& message,
    operation_execution_context& context) {
    return operation_result{
        .ok = false,
        .outcome = "partial",
        .result_json = {},
        .error_code = "partial_operation",
        .error_message = message,
        .revision = context.revisions().content_revision(),
        .view_revision = context.revisions().view_revision(),
        .retryable = false,
        .undo_recommended = true,
    };
}

}  // namespace

native_create_request_handler::native_create_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk,
    std::string operation,
    const sdk_create_kind kind)
    : identity_(std::move(identity)),
      sdk_(sdk),
      operation_(std::move(operation)),
      kind_(kind) {
    if (operation_.empty()) {
        throw std::invalid_argument("Create operation name must not be empty");
    }
}

std::string native_create_request_handler::operation() const {
    return operation_;
}

bool native_create_request_handler::is_mutating() const noexcept {
    return true;
}

operation_result native_create_request_handler::execute(
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
            throw std::invalid_argument("Create parameters must be an object");
        }
        const sdk_create_request create_request = parse_create_request(params, kind_);
        const sdk_create_result preflight = sdk_.create_objects(create_request, true);
        if (!preflight.ok) {
            return create_sdk_failure(preflight, context);
        }
        const nlohmann::json changes = nlohmann::json::array({create_change(create_request)});
        if (request.dry_run) {
            return create_native_success(nlohmann::json{{"plannedChanges", changes}}.dump(), context);
        }
        if (!context.reach_commit_point()) {
            return create_native_failure(
                "operation_cancelled",
                "Object creation was cancelled before the edit section",
                context);
        }
        const sdk_create_result created = sdk_.create_objects(create_request, false);
        if (!created.ok) {
            if (created.has_changed) {
                static_cast<void>(context.revisions().commit_content_change());
                return create_partial_failure(
                    "Object creation changed the project but postcondition verification failed",
                    context);
            }
            return create_sdk_failure(created, context);
        }
        static_cast<void>(context.revisions().commit_content_change());
        const std::string project_generation = context.revisions().project_generation();
        if (kind_ == sdk_create_kind::alias) {
            nlohmann::json objects = nlohmann::json::array();
            for (const sdk_object_snapshot& object : created.objects) {
                objects.push_back(serialize_object(object, identity_, project_generation));
            }
            return create_native_success(nlohmann::json{
                {"objects", std::move(objects)},
                {"appliedChanges", changes},
            }.dump(), context);
        }
        if (created.objects.size() != 1U) {
            return create_partial_failure(
                "Object creation returned an unexpected number of objects",
                context);
        }
        return create_native_success(nlohmann::json{
            {"object", serialize_object(created.objects.front(), identity_, project_generation)},
            {"appliedChanges", changes},
        }.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Create request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

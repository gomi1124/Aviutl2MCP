#include "aviutl2_mcp/native_object_request_handler.h"

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/locator_resolver.h"
#include "aviutl2_mcp/native_operation_result.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include <nlohmann/json.hpp>

#include <cstdint>
#include <limits>
#include <stdexcept>
#include <string_view>
#include <utility>

namespace aviutl2_mcp {
namespace {

[[nodiscard]] bool is_sha256(const std::string_view value) noexcept {
    if (value.size() != 64U) {
        return false;
    }
    for (const char character : value) {
        if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) {
            return false;
        }
    }
    return true;
}

[[nodiscard]] int parse_integer(const nlohmann::json& object, const char* name, const int minimum) {
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

[[nodiscard]] std::string parse_string(
    const nlohmann::json& object,
    const char* name,
    const bool can_be_empty = false) {
    const auto value = object.find(name);
    if (value == object.end() || !value->is_string()) {
        throw std::invalid_argument(std::string(name) + " must be a string");
    }
    std::string parsed = value->get<std::string>();
    std::size_t character_count = 0U;
    for (const unsigned char byte : parsed) {
        if ((byte & 0xc0U) != 0x80U) {
            ++character_count;
        }
    }
    if ((!can_be_empty && parsed.empty()) || character_count > 4096U) {
        throw std::invalid_argument(std::string(name) + " is outside the supported length");
    }
    return parsed;
}

[[nodiscard]] bool parse_boolean(
    const nlohmann::json& params,
    const char* name,
    const bool default_value) {
    const auto value = params.find(name);
    if (value == params.end() || value->is_null()) {
        return default_value;
    }
    if (!value->is_boolean()) {
        throw std::invalid_argument(std::string(name) + " must be a boolean");
    }
    return value->get<bool>();
}

[[nodiscard]] object_locator parse_locator(const nlohmann::json& params) {
    const auto locator_value = params.find("locator");
    if (locator_value == params.end() || !locator_value->is_object()) {
        throw std::invalid_argument("locator must be an object");
    }
    const nlohmann::json& locator = *locator_value;
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

[[nodiscard]] nlohmann::json serialize_item(const sdk_effect_item_snapshot& item) {
    nlohmann::json result = {
        {"name", item.name},
        {"type", item.type},
        {"codec", item.codec},
        {"isWritable", item.is_writable},
    };
    if (item.value.has_value()) {
        result["value"] = std::visit([](const auto& value) -> nlohmann::json {
            return value;
        }, *item.value);
    }
    if (!item.choices.empty()) {
        result["choices"] = item.choices;
    }
    return result;
}

[[nodiscard]] nlohmann::json serialize_detail(
    const sdk_object_detail_snapshot& detail,
    const bridge_identity& identity,
    const std::string& project_generation) {
    nlohmann::json groups = nlohmann::json::array();
    for (const sdk_effect_items_group& group : detail.effect_items) {
        nlohmann::json items = nlohmann::json::array();
        for (const sdk_effect_item_snapshot& item : group.items) {
            items.push_back(serialize_item(item));
        }
        groups.push_back({
            {"effect", serialize_effect(group.effect)},
            {"items", std::move(items)},
        });
    }
    nlohmann::json result = {
        {"object", serialize_object(detail.object, identity, project_generation)},
        {"effectItems", std::move(groups)},
    };
    if (detail.alias.has_value()) {
        result["alias"] = *detail.alias;
    }
    return result;
}

}  // namespace

native_object_request_handler::native_object_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk)
    : identity_(std::move(identity)),
      sdk_(sdk) {}

std::string native_object_request_handler::operation() const {
    return "object.get";
}

bool native_object_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_object_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Object query parameters must be an object");
        }
        const object_locator locator = parse_locator(params);
        const std::string project_generation = context.revisions().project_generation();
        const sdk_object_query_result result = sdk_.query_object(
            locator,
            identity_.instance_id,
            project_generation,
            parse_boolean(params, "includeAlias", false),
            parse_boolean(params, "includeEffectItems", true));
        if (!result.ok) {
            return create_native_failure(
                result.error_code,
                result.error_message,
                context,
                result.error_code == "read_not_available" || result.error_code == "sdk_query_failed");
        }
        return create_native_success(
            serialize_detail(result.detail, identity_, project_generation).dump(),
            context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Object query JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

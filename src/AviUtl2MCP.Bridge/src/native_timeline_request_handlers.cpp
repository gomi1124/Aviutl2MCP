#include "aviutl2_mcp/native_timeline_request_handlers.h"

#include "aviutl2_mcp/locator_resolver.h"
#include "aviutl2_mcp/native_operation_result.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include <nlohmann/json.hpp>

#include <charconv>
#include <cstdint>
#include <limits>
#include <optional>
#include <stdexcept>
#include <string_view>
#include <utility>

namespace aviutl2_mcp {
namespace {

constexpr std::size_t DEFAULT_PAGE_LIMIT = 100U;
constexpr std::size_t MAXIMUM_PAGE_LIMIT = 1'000U;
constexpr std::size_t MAXIMUM_CURSOR_OFFSET = 1'000'000U;

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

[[nodiscard]] std::optional<std::string> parse_optional_string(
    const nlohmann::json& params,
    const char* name,
    const std::size_t maximum_length) {
    const auto value = params.find(name);
    if (value == params.end() || value->is_null()) {
        return std::nullopt;
    }
    if (!value->is_string()) {
        throw std::invalid_argument(std::string(name) + " must be a string");
    }
    std::string parsed = value->get<std::string>();
    std::size_t character_count = 0U;
    for (const unsigned char byte : parsed) {
        if ((byte & 0xc0U) != 0x80U) {
            ++character_count;
        }
    }
    if (parsed.empty() || character_count > maximum_length) {
        throw std::invalid_argument(std::string(name) + " is outside the supported length");
    }
    return parsed;
}

[[nodiscard]] std::size_t parse_limit(const nlohmann::json& params) {
    const auto value = params.find("limit");
    if (value == params.end() || value->is_null()) {
        return DEFAULT_PAGE_LIMIT;
    }
    if (!value->is_number_integer() && !value->is_number_unsigned()) {
        throw std::invalid_argument("limit must be an integer");
    }
    const std::int64_t parsed = value->get<std::int64_t>();
    if (parsed <= 0 || parsed > static_cast<std::int64_t>(MAXIMUM_PAGE_LIMIT)) {
        throw std::invalid_argument("limit is outside the supported range");
    }
    return static_cast<std::size_t>(parsed);
}

[[nodiscard]] std::size_t parse_cursor(
    const nlohmann::json& params,
    const std::string_view prefix) {
    const auto value = params.find("cursor");
    if (value == params.end() || value->is_null()) {
        return 0U;
    }
    if (!value->is_string()) {
        throw std::invalid_argument("cursor must be a string");
    }
    const std::string text = value->get<std::string>();
    if (!text.starts_with(prefix)) {
        throw std::invalid_argument("cursor does not belong to this query");
    }
    const std::string_view offset_text(text.data() + prefix.size(), text.size() - prefix.size());
    std::size_t offset = 0U;
    const auto [position, error] = std::from_chars(
        offset_text.data(),
        offset_text.data() + offset_text.size(),
        offset);
    if (error != std::errc{} || position != offset_text.data() + offset_text.size()
        || offset > MAXIMUM_CURSOR_OFFSET) {
        throw std::invalid_argument("cursor offset is invalid");
    }
    return offset;
}

void validate_ranges(const sdk_timeline_query& query) {
    if (query.layer_start.has_value() && query.layer_end.has_value()
        && *query.layer_start > *query.layer_end) {
        throw std::invalid_argument("layerStart must not exceed layerEnd");
    }
    if (query.start_frame.has_value() && query.end_frame.has_value()
        && *query.start_frame > *query.end_frame) {
        throw std::invalid_argument("startFrame must not exceed endFrame");
    }
}

[[nodiscard]] sdk_timeline_query parse_timeline_query(
    const nlohmann::json& params,
    const bool is_find) {
    sdk_timeline_query query{
        .scene_id = parse_optional_integer(params, "sceneId", 0),
        .layer_start = parse_optional_integer(params, "layerStart", 1),
        .layer_end = parse_optional_integer(params, "layerEnd", 1),
        .start_frame = parse_optional_integer(params, "startFrame", 1),
        .end_frame = parse_optional_integer(params, "endFrame", 1),
        .name_contains = is_find ? parse_optional_string(params, "nameContains", 4096U) : std::nullopt,
        .effect_name = is_find ? parse_optional_string(params, "effectName", 4096U) : std::nullopt,
        .media_path = is_find ? parse_optional_string(params, "mediaPath", 32'767U) : std::nullopt,
        .offset = parse_cursor(params, is_find ? "objects:" : "timeline:"),
        .limit = parse_limit(params),
        .include_effects = is_find,
        .use_display_defaults = !is_find,
    };
    if (!is_find) {
        const auto detail = params.find("detail");
        if (detail != params.end() && !detail->is_null()) {
            if (!detail->is_string()) {
                throw std::invalid_argument("detail must be a string");
            }
            const std::string value = detail->get<std::string>();
            if (value == "effects") {
                query.include_effects = true;
            } else if (value != "summary") {
                throw std::invalid_argument("detail is unknown");
            }
        }
    }
    validate_ranges(query);
    return query;
}

[[nodiscard]] nlohmann::json serialize_effect(const sdk_effect_summary& effect) {
    return {
        {"name", effect.name},
        {"occurrence", effect.occurrence},
        {"isEnabled", effect.is_enabled},
        {"isLocked", effect.is_locked},
    };
}

[[nodiscard]] nlohmann::json serialize_locator(const object_locator& locator) {
    return {
        {"instanceId", locator.instance_id},
        {"projectGeneration", locator.project_generation},
        {"sceneId", locator.scene_id},
        {"layer", locator.layer},
        {"startFrame", locator.start_frame},
        {"endFrame", locator.end_frame},
        {"name", locator.name},
        {"aliasSha256", locator.alias_sha256},
        {"effectSignatureSha256", locator.effect_signature_sha256},
    };
}

[[nodiscard]] nlohmann::json serialize_object(
    const sdk_object_snapshot& object,
    const bridge_identity& identity,
    const std::string& project_generation) {
    nlohmann::json effects = nlohmann::json::array();
    for (const sdk_effect_summary& effect : object.effects) {
        effects.push_back(serialize_effect(effect));
    }
    nlohmann::json result = {
        {"locator", serialize_locator(create_object_locator(
            identity.instance_id,
            project_generation,
            object.candidate))},
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
    const sdk_timeline_snapshot& timeline,
    const bridge_identity& identity,
    const std::string& project_generation) {
    nlohmann::json objects = nlohmann::json::array();
    for (const sdk_object_snapshot& object : timeline.objects) {
        objects.push_back(serialize_object(object, identity, project_generation));
    }
    return objects;
}

[[nodiscard]] nlohmann::json coordinate_system() {
    return {
        {"frameBase", 1},
        {"layerBase", 1},
        {"endInclusive", true},
    };
}

[[nodiscard]] operation_result execute_query(
    const operation_request& request,
    operation_execution_context& context,
    const bridge_identity& identity,
    sdk_read_facade& sdk,
    const bool is_find) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Timeline query parameters must be an object");
        }
        const sdk_timeline_query query = parse_timeline_query(params, is_find);
        const sdk_timeline_query_result response = sdk.query_timeline(query);
        if (!response.ok) {
            return create_native_failure(
                response.error_code,
                response.error_message,
                context,
                response.error_code == "read_not_available" || response.error_code == "sdk_query_failed");
        }
        const std::string project_generation = context.revisions().project_generation();
        nlohmann::json result = {
            {"objects", serialize_objects(response.timeline, identity, project_generation)},
            {"nextCursor", response.timeline.is_truncated
                ? nlohmann::json(std::string(is_find ? "objects:" : "timeline:")
                    + std::to_string(response.timeline.next_offset))
                : nlohmann::json(nullptr)},
            {"isTruncated", response.timeline.is_truncated},
            {"coordinateSystem", coordinate_system()},
        };
        if (!is_find) {
            nlohmann::json layers = nlohmann::json::array();
            for (const sdk_layer_snapshot& layer : response.timeline.layers) {
                layers.push_back({
                    {"sceneId", layer.scene_id},
                    {"layer", layer.layer},
                    {"name", layer.name},
                    {"isVisible", layer.is_visible},
                    {"isLocked", layer.is_locked},
                });
            }
            result["layers"] = std::move(layers);
        }
        return create_native_success(result.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Timeline query JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace

native_timeline_request_handler::native_timeline_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk)
    : identity_(std::move(identity)),
      sdk_(sdk) {}

std::string native_timeline_request_handler::operation() const {
    return "timeline.get";
}

bool native_timeline_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_timeline_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    return execute_query(request, context, identity_, sdk_, false);
}

native_find_objects_request_handler::native_find_objects_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk)
    : identity_(std::move(identity)),
      sdk_(sdk) {}

std::string native_find_objects_request_handler::operation() const {
    return "object.find";
}

bool native_find_objects_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_find_objects_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    return execute_query(request, context, identity_, sdk_, true);
}

}  // namespace aviutl2_mcp

#include "aviutl2_mcp/native_effect_edit_request_handler.h"

#include "aviutl2_mcp/locator_resolver.h"
#include "aviutl2_mcp/native_operation_result.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <optional>
#include <stdexcept>
#include <string_view>
#include <utility>

namespace aviutl2_mcp {
namespace {

[[nodiscard]] std::size_t count_utf8_characters(const std::string_view value) noexcept {
    return static_cast<std::size_t>(std::ranges::count_if(value, [](const unsigned char byte) {
        return (byte & 0xc0U) != 0x80U;
    }));
}

[[nodiscard]] std::string parse_string(
    const nlohmann::json& object,
    const char* name,
    const bool can_be_empty = false,
    const std::size_t maximum_bytes = 64U * 1024U) {
    const auto value = object.find(name);
    if (value == object.end() || !value->is_string()) {
        throw std::invalid_argument(std::string(name) + " must be a string");
    }
    std::string parsed = value->get<std::string>();
    if ((!can_be_empty && parsed.empty()) || parsed.find('\0') != std::string::npos
        || parsed.size() > maximum_bytes || count_utf8_characters(parsed) > 4096U) {
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

[[nodiscard]] sdk_effect_item_value parse_item_value(const nlohmann::json& params) {
    const auto value = params.find("value");
    if (value == params.end()) {
        throw std::invalid_argument("value is required");
    }
    if (value->is_boolean()) {
        return value->get<bool>();
    }
    if (value->is_number_integer() || value->is_number_unsigned()) {
        if (value->is_number_unsigned()
            && value->get<std::uint64_t>() > static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)())) {
            throw std::invalid_argument("value integer is outside the supported range");
        }
        return value->get<std::int64_t>();
    }
    if (value->is_number_float()) {
        const double number = value->get<double>();
        if (!std::isfinite(number)) {
            throw std::invalid_argument("value number must be finite");
        }
        return number;
    }
    if (value->is_string()) {
        return parse_string(params, "value", true, 64U * 1024U);
    }
    throw std::invalid_argument("value must be a boolean, number, or string");
}

[[nodiscard]] sdk_effect_edit_request parse_request(
    const nlohmann::json& params,
    const sdk_effect_edit_kind kind) {
    const auto effect = params.find("effect");
    if (effect == params.end() || !effect->is_object()) {
        throw std::invalid_argument("effect must be an object");
    }
    sdk_effect_edit_request result{
        .kind = kind,
        .locator = parse_locator(params),
        .effect_name = parse_string(*effect, "name"),
        .effect_occurrence = effect->contains("occurrence")
            ? parse_integer(*effect, "occurrence", 0)
            : 0,
    };
    if (kind == sdk_effect_edit_kind::set_item) {
        result.item_name = parse_string(params, "itemName");
        result.item_value = parse_item_value(params);
    } else {
        const auto enabled = params.find("isEnabled");
        const auto locked = params.find("isLocked");
        if (enabled != params.end()) {
            if (!enabled->is_boolean()) {
                throw std::invalid_argument("isEnabled must be a boolean");
            }
            result.is_enabled = enabled->get<bool>();
        }
        if (locked != params.end()) {
            if (!locked->is_boolean()) {
                throw std::invalid_argument("isLocked must be a boolean");
            }
            result.is_locked = locked->get<bool>();
        }
        if (!result.is_enabled.has_value() && !result.is_locked.has_value()) {
            throw std::invalid_argument("At least one effect state property is required");
        }
    }
    return result;
}

[[nodiscard]] nlohmann::json serialize_value(const sdk_effect_item_value& value) {
    return std::visit([](const auto& item) -> nlohmann::json { return item; }, value);
}

[[nodiscard]] nlohmann::json serialize_item(const sdk_effect_item_snapshot& item) {
    nlohmann::json result{
        {"name", item.name},
        {"type", item.type},
        {"codec", item.codec},
        {"isWritable", item.is_writable},
    };
    if (item.value.has_value()) {
        result["value"] = serialize_value(*item.value);
    }
    if (!item.choices.empty()) {
        result["choices"] = item.choices;
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

[[nodiscard]] nlohmann::json create_changes(
    const sdk_effect_edit_request& request,
    const sdk_effect_edit_result& preflight) {
    const std::string target = "effect:" + request.effect_name
        + ":" + std::to_string(request.effect_occurrence);
    if (request.kind == sdk_effect_edit_kind::set_item) {
        if (!preflight.item.has_value() || !request.item_value.has_value()) {
            throw std::runtime_error("Effect item preflight omitted change state");
        }
        nlohmann::json change{
            {"kind", "setEffectItem"},
            {"target", target + "/" + *request.item_name},
            {"after", serialize_value(*request.item_value)},
        };
        if (preflight.item->value.has_value()) {
            change["before"] = serialize_value(*preflight.item->value);
        }
        return nlohmann::json::array({std::move(change)});
    }
    if (!preflight.effect.has_value()) {
        throw std::runtime_error("Effect state preflight omitted change state");
    }
    return nlohmann::json::array({nlohmann::json{
        {"kind", "setEffectState"},
        {"target", target},
        {"before", {
            {"isEnabled", preflight.effect->is_enabled},
            {"isLocked", preflight.effect->is_locked},
        }},
        {"after", {
            {"isEnabled", request.is_enabled.value_or(preflight.effect->is_enabled)},
            {"isLocked", request.is_locked.value_or(preflight.effect->is_locked)},
        }},
    }});
}

[[nodiscard]] operation_result create_partial_failure(operation_execution_context& context) {
    return operation_result{
        .ok = false,
        .outcome = "partial",
        .result_json = {},
        .error_code = "partial_operation",
        .error_message = "The effect changed but postcondition verification failed",
        .revision = context.revisions().content_revision(),
        .view_revision = context.revisions().view_revision(),
        .retryable = false,
        .undo_recommended = true,
    };
}

}  // namespace

native_effect_edit_request_handler::native_effect_edit_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk,
    std::string operation,
    const sdk_effect_edit_kind kind)
    : identity_(std::move(identity)),
      sdk_(sdk),
      operation_(std::move(operation)),
      kind_(kind) {
    if (operation_.empty()) {
        throw std::invalid_argument("Effect edit operation name must not be empty");
    }
}

std::string native_effect_edit_request_handler::operation() const {
    return operation_;
}

bool native_effect_edit_request_handler::is_mutating() const noexcept {
    return true;
}

operation_result native_effect_edit_request_handler::execute(
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
            throw std::invalid_argument("Effect edit parameters must be an object");
        }
        const sdk_effect_edit_request effect_request = parse_request(params, kind_);
        const std::string project_generation = context.revisions().project_generation();
        const sdk_effect_edit_result preflight = sdk_.edit_effect(
            effect_request, identity_.instance_id, project_generation, true);
        if (!preflight.ok) {
            return create_native_failure(
                preflight.error_code,
                preflight.error_message,
                context,
                preflight.error_code == "read_not_available"
                    || preflight.error_code == "sdk_query_failed");
        }
        const nlohmann::json changes = create_changes(effect_request, preflight);
        if (request.dry_run) {
            return create_native_success(nlohmann::json{{"plannedChanges", changes}}.dump(), context);
        }
        if (!context.reach_commit_point()) {
            return create_native_failure(
                "operation_cancelled", "Effect edit was cancelled before commit", context);
        }
        const sdk_effect_edit_result edited = sdk_.edit_effect(
            effect_request, identity_.instance_id, project_generation, false);
        if (!edited.ok) {
            if (edited.has_changed) {
                static_cast<void>(context.revisions().commit_content_change());
                return create_partial_failure(context);
            }
            return create_native_failure(
                edited.error_code, edited.error_message, context,
                edited.error_code == "sdk_query_failed");
        }
        if (edited.has_changed) {
            static_cast<void>(context.revisions().commit_content_change());
        }
        nlohmann::json result{{"appliedChanges", changes}};
        if (kind_ == sdk_effect_edit_kind::set_item) {
            if (!edited.item.has_value()) {
                if (edited.has_changed) {
                    return create_partial_failure(context);
                }
                return create_native_failure(
                    "sdk_query_failed", "Effect edit omitted its item postcondition", context, true);
            }
            result["item"] = serialize_item(*edited.item);
        } else {
            if (!edited.effect.has_value()) {
                if (edited.has_changed) {
                    return create_partial_failure(context);
                }
                return create_native_failure(
                    "sdk_query_failed", "Effect edit omitted its state postcondition", context, true);
            }
            result["effect"] = serialize_effect(*edited.effect);
        }
        return create_native_success(result.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Effect edit request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

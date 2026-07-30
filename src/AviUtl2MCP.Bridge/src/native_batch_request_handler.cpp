#include "aviutl2_mcp/native_batch_request_handler.h"

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
#include <tuple>
#include <utility>

namespace aviutl2_mcp {
namespace {

constexpr std::size_t MAXIMUM_ALIAS_BYTES = 1024U * 1024U;

[[nodiscard]] std::string parse_string(
    const nlohmann::json& object,
    const char* name,
    const std::size_t maximum_bytes,
    const bool can_be_empty = false) {
    const auto value = object.find(name);
    if (value == object.end() || !value->is_string()) {
        throw std::invalid_argument(std::string(name) + " must be a string");
    }
    std::string parsed = value->get<std::string>();
    if ((!can_be_empty && parsed.empty()) || parsed.find('\0') != std::string::npos
        || parsed.size() > maximum_bytes) {
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

[[nodiscard]] std::optional<int> parse_optional_integer(
    const nlohmann::json& object,
    const char* name,
    const int minimum) {
    const auto value = object.find(name);
    if (value == object.end() || value->is_null()) {
        return std::nullopt;
    }
    return parse_integer(object, name, minimum);
}

[[nodiscard]] std::optional<bool> parse_optional_boolean(
    const nlohmann::json& object,
    const char* name) {
    const auto value = object.find(name);
    if (value == object.end() || value->is_null()) {
        return std::nullopt;
    }
    if (!value->is_boolean()) {
        throw std::invalid_argument(std::string(name) + " must be a boolean");
    }
    return value->get<bool>();
}

[[nodiscard]] std::optional<std::string> parse_optional_name(const nlohmann::json& object) {
    const auto value = object.find("name");
    if (value == object.end() || value->is_null()) {
        return std::nullopt;
    }
    return parse_string(object, "name", 64U * 1024U, true);
}

[[nodiscard]] bool is_sha256(const std::string_view value) noexcept {
    return value.size() == 64U && std::ranges::all_of(value, [](const char character) {
        return (character >= '0' && character <= '9')
            || (character >= 'a' && character <= 'f');
    });
}

[[nodiscard]] object_locator parse_locator(const nlohmann::json& args) {
    const auto value = args.find("locator");
    if (value == args.end() || !value->is_object()) {
        throw std::invalid_argument("locator must be an object");
    }
    const nlohmann::json& locator = *value;
    object_locator result{
        .instance_id = parse_string(locator, "instanceId", 128U),
        .project_generation = parse_string(locator, "projectGeneration", 128U),
        .scene_id = parse_integer(locator, "sceneId", 0),
        .layer = parse_integer(locator, "layer", 1),
        .start_frame = parse_integer(locator, "startFrame", 1),
        .end_frame = parse_integer(locator, "endFrame", 1),
        .name = parse_string(locator, "name", 64U * 1024U, true),
        .alias_sha256 = parse_string(locator, "aliasSha256", 64U),
        .effect_signature_sha256 = parse_string(locator, "effectSignatureSha256", 64U),
    };
    if (!is_nonzero_uuid(result.instance_id) || !is_nonzero_uuid(result.project_generation)
        || result.end_frame < result.start_frame || !is_sha256(result.alias_sha256)
        || !is_sha256(result.effect_signature_sha256)) {
        throw std::invalid_argument("locator fields are invalid");
    }
    return result;
}

[[nodiscard]] std::tuple<int, int, int, int> parse_placement(const nlohmann::json& args) {
    const auto value = args.find("placement");
    if (value == args.end() || !value->is_object()) {
        throw std::invalid_argument("placement must be an object");
    }
    const nlohmann::json& placement = *value;
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
    return {scene_id, layer, start_frame, length};
}

[[nodiscard]] sdk_effect_item_value parse_item_value(const nlohmann::json& args) {
    const auto value = args.find("value");
    if (value == args.end()) {
        throw std::invalid_argument("value is required");
    }
    if (value->is_boolean()) {
        return value->get<bool>();
    }
    if (value->is_number_integer() || value->is_number_unsigned()) {
        if (value->is_number_unsigned()
            && value->get<std::uint64_t>()
                > static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)())) {
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
        return parse_string(args, "value", 64U * 1024U, true);
    }
    throw std::invalid_argument("value must be a boolean, number, or string");
}

[[nodiscard]] std::pair<std::string, int> parse_effect_selector(const nlohmann::json& args) {
    const auto value = args.find("effect");
    if (value == args.end() || !value->is_object()) {
        throw std::invalid_argument("effect must be an object");
    }
    return {
        parse_string(*value, "name", 64U * 1024U),
        value->contains("occurrence") ? parse_integer(*value, "occurrence", 0) : 0,
    };
}

[[nodiscard]] sdk_create_request parse_create(
    const nlohmann::json& args,
    const sdk_create_kind kind) {
    const auto [scene_id, layer, start_frame, length] = parse_placement(args);
    std::string source;
    if (kind == sdk_create_kind::effect) {
        const auto value = args.find("effect");
        if (value == args.end() || !value->is_object()) {
            throw std::invalid_argument("effect must be an object");
        }
        source = parse_string(*value, "name", 64U * 1024U);
        const auto items = args.find("items");
        if (items != args.end() && !items->is_null()
            && (!items->is_array() || !items->empty())) {
            throw std::invalid_argument(
                "Initial batch effect item writes require a verified writable codec");
        }
    } else if (kind == sdk_create_kind::media) {
        source = parse_string(args, "mediaPath", 64U * 1024U);
    } else {
        source = parse_string(args, "alias", MAXIMUM_ALIAS_BYTES);
    }
    return sdk_create_request{
        .kind = kind,
        .source = std::move(source),
        .scene_id = scene_id,
        .layer = layer,
        .start_frame = start_frame,
        .length = length,
        .name = parse_optional_name(args),
    };
}

[[nodiscard]] sdk_object_edit_request parse_object_edit(
    const nlohmann::json& args,
    const sdk_object_edit_kind kind) {
    sdk_object_edit_request request{
        .kind = kind,
        .locator = parse_locator(args),
    };
    if (kind == sdk_object_edit_kind::move) {
        const auto placement = args.find("placement");
        if (placement == args.end() || !placement->is_object()) {
            throw std::invalid_argument("placement must be an object");
        }
        request.destination_scene_id = parse_integer(*placement, "sceneId", 0);
        request.destination_layer = parse_integer(*placement, "layer", 1);
        request.destination_start_frame = parse_integer(*placement, "startFrame", 1);
    } else if (kind == sdk_object_edit_kind::set_name) {
        request.name = parse_string(args, "name", 64U * 1024U, true);
    } else if (kind == sdk_object_edit_kind::create_section) {
        request.section_frame = parse_integer(args, "frame", 1);
    } else if (kind == sdk_object_edit_kind::delete_section) {
        request.section_index = parse_integer(args, "section", 1);
    } else if (kind == sdk_object_edit_kind::move_section) {
        request.section_index = parse_integer(args, "section", 1);
        request.section_frame = parse_integer(args, "frame", 1);
    }
    return request;
}

[[nodiscard]] sdk_effect_edit_request parse_effect_edit(
    const nlohmann::json& args,
    const sdk_effect_edit_kind kind) {
    auto [name, occurrence] = parse_effect_selector(args);
    sdk_effect_edit_request request{
        .kind = kind,
        .locator = parse_locator(args),
        .effect_name = std::move(name),
        .effect_occurrence = occurrence,
    };
    if (kind == sdk_effect_edit_kind::set_item) {
        request.item_name = parse_string(args, "itemName", 64U * 1024U);
        request.item_value = parse_item_value(args);
    } else {
        request.is_enabled = parse_optional_boolean(args, "isEnabled");
        request.is_locked = parse_optional_boolean(args, "isLocked");
        if (!request.is_enabled.has_value() && !request.is_locked.has_value()) {
            throw std::invalid_argument("At least one effect state property is required");
        }
    }
    return request;
}

[[nodiscard]] sdk_layer_edit_request parse_layer_edit(const nlohmann::json& args) {
    sdk_layer_edit_request request{
        .scene_id = parse_optional_integer(args, "sceneId", 0),
        .layer = parse_integer(args, "layer", 1),
        .name = parse_optional_name(args),
        .is_visible = parse_optional_boolean(args, "isVisible"),
        .is_locked = parse_optional_boolean(args, "isLocked"),
    };
    if (!request.name.has_value() && !request.is_visible.has_value()
        && !request.is_locked.has_value()) {
        throw std::invalid_argument("At least one layer property is required");
    }
    return request;
}

[[nodiscard]] sdk_batch_request_value parse_operation_request(
    const std::string_view operation,
    const nlohmann::json& args) {
    if (operation == "createObject") {
        return parse_create(args, sdk_create_kind::effect);
    }
    if (operation == "createMediaObject") {
        return parse_create(args, sdk_create_kind::media);
    }
    if (operation == "createAliasObject") {
        return parse_create(args, sdk_create_kind::alias);
    }
    if (operation == "moveObject") {
        return parse_object_edit(args, sdk_object_edit_kind::move);
    }
    if (operation == "deleteObject") {
        return parse_object_edit(args, sdk_object_edit_kind::delete_object);
    }
    if (operation == "setObjectName") {
        return parse_object_edit(args, sdk_object_edit_kind::set_name);
    }
    if (operation == "createObjectSection") {
        return parse_object_edit(args, sdk_object_edit_kind::create_section);
    }
    if (operation == "deleteObjectSection") {
        return parse_object_edit(args, sdk_object_edit_kind::delete_section);
    }
    if (operation == "moveObjectSection") {
        return parse_object_edit(args, sdk_object_edit_kind::move_section);
    }
    if (operation == "setEffectItem") {
        return parse_effect_edit(args, sdk_effect_edit_kind::set_item);
    }
    if (operation == "setEffectState") {
        return parse_effect_edit(args, sdk_effect_edit_kind::set_state);
    }
    if (operation == "setLayer") {
        return parse_layer_edit(args);
    }
    throw std::invalid_argument("Batch operation discriminator is unsupported");
}

[[nodiscard]] std::vector<sdk_batch_operation> parse_batch(const nlohmann::json& params) {
    const auto value = params.find("operations");
    if (value == params.end() || !value->is_array()
        || value->empty() || value->size() > 100U) {
        throw std::invalid_argument("operations must contain between 1 and 100 entries");
    }
    std::vector<sdk_batch_operation> operations;
    operations.reserve(value->size());
    for (const nlohmann::json& entry : *value) {
        if (!entry.is_object()) {
            throw std::invalid_argument("Batch operation must be an object");
        }
        const std::string operation = parse_string(entry, "op", 64U);
        const std::string client_operation_id = parse_string(
            entry, "clientOperationId", 128U);
        const auto args = entry.find("args");
        if (args == entry.end() || !args->is_object()) {
            throw std::invalid_argument("Batch operation args must be an object");
        }
        operations.push_back(sdk_batch_operation{
            .client_operation_id = client_operation_id,
            .request = parse_operation_request(operation, *args),
        });
    }
    return operations;
}

[[nodiscard]] std::string get_operation_name(const sdk_batch_request_value& request) {
    if (const auto* create = std::get_if<sdk_create_request>(&request)) {
        return create->kind == sdk_create_kind::effect ? "createObject"
            : create->kind == sdk_create_kind::media ? "createMediaObject"
            : "createAliasObject";
    }
    if (const auto* object = std::get_if<sdk_object_edit_request>(&request)) {
        switch (object->kind) {
            case sdk_object_edit_kind::move:
                return "moveObject";
            case sdk_object_edit_kind::delete_object:
                return "deleteObject";
            case sdk_object_edit_kind::set_name:
                return "setObjectName";
            case sdk_object_edit_kind::create_section:
                return "createObjectSection";
            case sdk_object_edit_kind::delete_section:
                return "deleteObjectSection";
            case sdk_object_edit_kind::move_section:
                return "moveObjectSection";
        }
    }
    if (const auto* effect = std::get_if<sdk_effect_edit_request>(&request)) {
        return effect->kind == sdk_effect_edit_kind::set_item
            ? "setEffectItem"
            : "setEffectState";
    }
    return "setLayer";
}

[[nodiscard]] nlohmann::json serialize_value(const sdk_effect_item_value& value) {
    return std::visit([](const auto& item) -> nlohmann::json { return item; }, value);
}

[[nodiscard]] const char* get_object_change_kind(const sdk_object_edit_kind kind) noexcept {
    switch (kind) {
        case sdk_object_edit_kind::move:
            return "move";
        case sdk_object_edit_kind::delete_object:
            return "delete";
        case sdk_object_edit_kind::set_name:
            return "setName";
        case sdk_object_edit_kind::create_section:
            return "createObjectSection";
        case sdk_object_edit_kind::delete_section:
            return "deleteObjectSection";
        case sdk_object_edit_kind::move_section:
            return "moveObjectSection";
    }
    return "unknown";
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
        {"sections", std::move(sections)},
        {"effects", std::move(effects)},
    };
    if (object.media_path.has_value()) {
        result["mediaPath"] = *object.media_path;
    }
    return result;
}

[[nodiscard]] nlohmann::json create_changes(
    const sdk_batch_operation& operation,
    const sdk_batch_operation_result& preflight) {
    if (const auto* create = std::get_if<sdk_create_request>(&operation.request)) {
        return nlohmann::json::array({nlohmann::json{
            {"kind", "create"},
            {"target", "scene:" + std::to_string(create->scene_id)
                + "/layer:" + std::to_string(create->layer)
                + "/frame:" + std::to_string(create->start_frame)},
        }});
    }
    if (const auto* object = std::get_if<sdk_object_edit_request>(&operation.request)) {
        const auto& result = std::get<sdk_object_edit_result>(preflight.result);
        if (!result.object.has_value()) {
            throw std::runtime_error("Batch object preflight omitted its target snapshot");
        }
        const sdk_object_snapshot& before = *result.object;
        nlohmann::json change{
            {"kind", get_object_change_kind(object->kind)},
            {"target", "object:" + std::to_string(before.candidate.scene_id)
                + "/" + std::to_string(before.candidate.layer)
                + "/" + std::to_string(before.candidate.start_frame)},
        };
        if (object->kind == sdk_object_edit_kind::move) {
            change["before"] = {
                {"sceneId", before.candidate.scene_id},
                {"layer", before.candidate.layer},
                {"startFrame", before.candidate.start_frame},
            };
            change["after"] = {
                {"sceneId", *object->destination_scene_id},
                {"layer", *object->destination_layer},
                {"startFrame", *object->destination_start_frame},
            };
        } else if (object->kind == sdk_object_edit_kind::set_name) {
            change["before"] = before.candidate.name;
            change["after"] = *object->name;
        } else if (object->kind == sdk_object_edit_kind::create_section) {
            change["before"] = "absent";
            change["after"] = *object->section_frame;
        } else if (object->kind == sdk_object_edit_kind::delete_section) {
            change["before"] = before.section_frames[
                static_cast<std::size_t>(*object->section_index)];
            change["after"] = "deleted";
        } else if (object->kind == sdk_object_edit_kind::move_section) {
            change["before"] = before.section_frames[
                static_cast<std::size_t>(*object->section_index)];
            change["after"] = *object->section_frame;
        } else {
            change["before"] = "present";
            change["after"] = "deleted";
        }
        return nlohmann::json::array({std::move(change)});
    }
    if (const auto* effect = std::get_if<sdk_effect_edit_request>(&operation.request)) {
        const auto& result = std::get<sdk_effect_edit_result>(preflight.result);
        const std::string target = "effect:" + effect->effect_name
            + ":" + std::to_string(effect->effect_occurrence);
        if (effect->kind == sdk_effect_edit_kind::set_item) {
            if (!result.item.has_value() || !effect->item_value.has_value()) {
                throw std::runtime_error("Batch effect item preflight omitted its state");
            }
            nlohmann::json change{
                {"kind", "setEffectItem"},
                {"target", target + "/" + *effect->item_name},
                {"after", serialize_value(*effect->item_value)},
            };
            if (result.item->value.has_value()) {
                change["before"] = serialize_value(*result.item->value);
            }
            return nlohmann::json::array({std::move(change)});
        }
        if (!result.effect.has_value()) {
            throw std::runtime_error("Batch effect state preflight omitted its state");
        }
        return nlohmann::json::array({nlohmann::json{
            {"kind", "setEffectState"},
            {"target", target},
            {"before", {
                {"isEnabled", result.effect->is_enabled},
                {"isLocked", result.effect->is_locked},
            }},
            {"after", {
                {"isEnabled", effect->is_enabled.value_or(result.effect->is_enabled)},
                {"isLocked", effect->is_locked.value_or(result.effect->is_locked)},
            }},
        }});
    }
    const auto& layer = std::get<sdk_layer_edit_request>(operation.request);
    const auto& result = std::get<sdk_layer_edit_result>(preflight.result);
    if (!result.layer.has_value()) {
        throw std::runtime_error("Batch layer preflight omitted its state");
    }
    return nlohmann::json::array({nlohmann::json{
        {"kind", "setLayer"},
        {"target", "layer:" + std::to_string(result.layer->scene_id)
            + "/" + std::to_string(result.layer->layer)},
        {"before", {
            {"name", result.layer->name},
            {"isVisible", result.layer->is_visible},
            {"isLocked", result.layer->is_locked},
        }},
        {"after", {
            {"name", layer.name.value_or(result.layer->name)},
            {"isVisible", layer.is_visible.value_or(result.layer->is_visible)},
            {"isLocked", layer.is_locked.value_or(result.layer->is_locked)},
        }},
    }});
}

void add_operation_payload(
    nlohmann::json& result,
    const sdk_batch_operation& operation,
    const sdk_batch_operation_result& execution,
    const bridge_identity& identity,
    const std::string& project_generation) {
    if (const auto* created = std::get_if<sdk_create_result>(&execution.result)) {
        if (created->objects.empty()) {
            return;
        }
        const auto& request = std::get<sdk_create_request>(operation.request);
        if (request.kind == sdk_create_kind::alias) {
            nlohmann::json objects = nlohmann::json::array();
            for (const sdk_object_snapshot& object : created->objects) {
                objects.push_back(serialize_object(object, identity, project_generation));
            }
            result["objects"] = std::move(objects);
        } else if (created->objects.size() == 1U) {
            result["object"] = serialize_object(
                created->objects.front(), identity, project_generation);
        }
        return;
    }
    if (const auto* edited = std::get_if<sdk_object_edit_result>(&execution.result);
        edited != nullptr && edited->object.has_value()) {
        result["object"] = serialize_object(*edited->object, identity, project_generation);
    }
}

[[nodiscard]] nlohmann::json create_batch_data(
    const std::vector<sdk_batch_operation>& operations,
    const sdk_batch_edit_result& preflight,
    const sdk_batch_edit_result* execution,
    const bridge_identity& identity,
    const std::string& project_generation,
    const bool is_dry_run,
    const bool undo_recommended) {
    nlohmann::json results = nlohmann::json::array();
    nlohmann::json applied_ids = nlohmann::json::array();
    const std::optional<std::size_t> failed_index = execution == nullptr
        ? std::nullopt
        : execution->failed_index;
    for (std::size_t index = 0U; index < operations.size(); ++index) {
        std::string status = is_dry_run ? "planned" : "applied";
        if (failed_index.has_value()) {
            status = index < *failed_index ? "applied"
                : index == *failed_index ? "failed"
                : "skipped";
        }
        nlohmann::json item{
            {"clientOperationId", operations[index].client_operation_id},
            {"op", get_operation_name(operations[index].request)},
            {"status", status},
            {"changes", create_changes(operations[index], preflight.operations[index])},
        };
        if (status == "applied") {
            applied_ids.push_back(operations[index].client_operation_id);
        }
        if (execution != nullptr && index < execution->operations.size()) {
            add_operation_payload(
                item,
                operations[index],
                execution->operations[index],
                identity,
                project_generation);
        }
        if (status == "failed" && execution != nullptr) {
            const sdk_batch_operation_result& failed = execution->operations[index];
            item["error"] = {
                {"code", failed.error_code},
                {"message", failed.error_message},
                {"canRetry", false},
                {"details", nlohmann::json::object()},
            };
        }
        results.push_back(std::move(item));
    }
    return {
        {"results", std::move(results)},
        {"appliedOperationIds", std::move(applied_ids)},
        {"undoRecommended", undo_recommended},
    };
}

[[nodiscard]] bool is_retryable_sdk_error(const std::string_view code) noexcept {
    return code == "read_not_available" || code == "sdk_query_failed"
        || code == "sdk_not_available";
}

}  // namespace

native_batch_request_handler::native_batch_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk)
    : identity_(std::move(identity)), sdk_(sdk) {}

std::string native_batch_request_handler::operation() const {
    return "batch.execute";
}

bool native_batch_request_handler::is_mutating() const noexcept {
    return true;
}

operation_result native_batch_request_handler::execute(
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
            throw std::invalid_argument("Batch parameters must be an object");
        }
        const std::vector<sdk_batch_operation> operations = parse_batch(params);
        const std::string project_generation = context.revisions().project_generation();
        const sdk_batch_edit_result preflight = sdk_.edit_batch(
            operations,
            identity_.instance_id,
            project_generation,
            true);
        if (!preflight.ok || preflight.operations.size() != operations.size()) {
            return create_native_failure(
                preflight.error_code.empty() ? "sdk_query_failed" : preflight.error_code,
                preflight.error_message.empty()
                    ? "Batch preflight did not validate every operation"
                    : preflight.error_message,
                context,
                is_retryable_sdk_error(preflight.error_code));
        }
        if (request.dry_run) {
            return create_native_success(create_batch_data(
                operations,
                preflight,
                nullptr,
                identity_,
                project_generation,
                true,
                false).dump(), context);
        }
        if (!context.reach_commit_point()) {
            return create_native_failure(
                "operation_cancelled", "Batch edit was cancelled before commit", context);
        }
        const sdk_batch_edit_result edited = sdk_.edit_batch(
            operations,
            identity_.instance_id,
            project_generation,
            false);
        if (edited.has_changed) {
            static_cast<void>(context.revisions().commit_content_change());
        }
        const std::string current_generation = context.revisions().project_generation();
        if (!edited.ok) {
            if (!edited.has_changed) {
                return create_native_failure(
                    edited.error_code.empty() ? "sdk_query_failed" : edited.error_code,
                    edited.error_message.empty() ? "Batch edit failed" : edited.error_message,
                    context,
                    is_retryable_sdk_error(edited.error_code));
            }
            const nlohmann::json data = create_batch_data(
                operations,
                preflight,
                &edited,
                identity_,
                current_generation,
                false,
                true);
            return operation_result{
                .ok = false,
                .outcome = "partial",
                .result_json = data.dump(),
                .error_code = "partial_operation",
                .error_message = "The batch partially changed the project; one Undo is recommended",
                .revision = context.revisions().content_revision(),
                .view_revision = context.revisions().view_revision(),
                .retryable = false,
                .undo_recommended = true,
            };
        }
        if (edited.operations.size() != operations.size()) {
            if (edited.has_changed) {
                return operation_result{
                    .ok = false,
                    .outcome = "partial",
                    .result_json = {},
                    .error_code = "partial_operation",
                    .error_message = "Batch result count was invalid after project changes",
                    .revision = context.revisions().content_revision(),
                    .view_revision = context.revisions().view_revision(),
                    .retryable = false,
                    .undo_recommended = true,
                };
            }
            return create_native_failure(
                "sdk_query_failed", "Batch result count was invalid", context, true);
        }
        return create_native_success(create_batch_data(
            operations,
            preflight,
            &edited,
            identity_,
            current_generation,
            false,
            false).dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure(
            "invalid_argument", "Batch request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

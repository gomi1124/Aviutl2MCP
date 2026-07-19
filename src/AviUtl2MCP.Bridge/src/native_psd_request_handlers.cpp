#include "aviutl2_mcp/native_psd_request_handlers.h"

#include "aviutl2_mcp/gcmz_adapter.h"
#include "aviutl2_mcp/locator_resolver.h"
#include "aviutl2_mcp/native_operation_result.h"
#include "aviutl2_mcp/psd_codecs.h"
#include "aviutl2_mcp/psd_contract.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include <Windows.h>

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <chrono>
#include <cstdint>
#include <cwctype>
#include <filesystem>
#include <limits>
#include <optional>
#include <ranges>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
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

[[nodiscard]] std::string parse_required_string(
    const nlohmann::json& object,
    const char* name,
    const bool can_be_empty = false) {
    const auto value = object.find(name);
    if (value == object.end() || !value->is_string()) {
        throw std::invalid_argument(std::string(name) + " must be a string");
    }
    std::string parsed = value->get<std::string>();
    if ((!can_be_empty && parsed.empty()) || parsed.find('\0') != std::string::npos
        || parsed.size() > 64U * 1024U) {
        throw std::invalid_argument(std::string(name) + " is outside the supported length");
    }
    return parsed;
}

[[nodiscard]] int parse_required_integer(
    const nlohmann::json& object,
    const char* name,
    const int minimum) {
    const auto parsed = parse_optional_integer(object, name, minimum);
    if (!parsed.has_value()) {
        throw std::invalid_argument(std::string(name) + " is required");
    }
    return *parsed;
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
        .instance_id = parse_required_string(locator, "instanceId"),
        .project_generation = parse_required_string(locator, "projectGeneration"),
        .scene_id = parse_required_integer(locator, "sceneId", 0),
        .layer = parse_required_integer(locator, "layer", 1),
        .start_frame = parse_required_integer(locator, "startFrame", 1),
        .end_frame = parse_required_integer(locator, "endFrame", 1),
        .name = parse_required_string(locator, "name", true),
        .alias_sha256 = parse_required_string(locator, "aliasSha256"),
        .effect_signature_sha256 = parse_required_string(locator, "effectSignatureSha256"),
    };
    if (!is_nonzero_uuid(result.instance_id) || !is_nonzero_uuid(result.project_generation)
        || result.end_frame < result.start_frame || !is_sha256(result.alias_sha256)
        || !is_sha256(result.effect_signature_sha256)) {
        throw std::invalid_argument("locator fields are invalid");
    }
    return result;
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

[[nodiscard]] std::optional<std::string> extract_psdtoolkit_version(
    const std::vector<sdk_module_summary>& modules) {
    for (const sdk_module_summary& module : modules) {
        if (module.name.find("PSDToolKit") == std::string::npos
            && module.information.find("PSDToolKit") == std::string::npos) {
            continue;
        }
        const std::size_t start = module.information.find_first_of("0123456789");
        if (start == std::string::npos) {
            continue;
        }
        std::size_t end = start;
        while (end < module.information.size()) {
            const unsigned char character = static_cast<unsigned char>(module.information[end]);
            if (std::isalnum(character) == 0 && character != '.') {
                break;
            }
            ++end;
        }
        if (end > start) {
            return module.information.substr(start, end - start);
        }
    }
    return std::nullopt;
}

[[nodiscard]] psd_profile_detection detect_runtime_psd_profile(sdk_read_facade& sdk) {
    const sdk_effect_catalog_query_result catalog = sdk.query_effects({
        .offset = 0U,
        .limit = 1'000U,
    });
    if (!catalog.ok || catalog.catalog.is_truncated) {
        return {.failures = {"sdk_query_failed"}};
    }
    psd_profile_observation observation{
        .version = extract_psdtoolkit_version(catalog.catalog.modules),
    };
    constexpr std::array effect_names{
        std::string_view(PSD_SETUP_EFFECT),
        std::string_view(PSD_FILE_EFFECT),
        std::string_view(PSD_VOICE_EFFECT),
    };
    for (const std::string_view effect_name : effect_names) {
        const auto definition = std::ranges::find_if(
            catalog.catalog.effects,
            [effect_name](const sdk_effect_definition& effect) {
                return effect.name == effect_name;
            });
        if (definition == catalog.catalog.effects.end()) {
            continue;
        }
        psd_observed_effect observed{.name = definition->name};
        const sdk_effect_items_query_result items = sdk.query_effect_items(definition->name, false);
        if (items.ok) {
            for (const sdk_effect_item_snapshot& item : items.items) {
                observed.items.push_back({.name = item.name, .type = item.type});
            }
        }
        observation.effects.push_back(std::move(observed));
    }
    return detect_psd_profile(observation);
}

[[nodiscard]] const sdk_effect_items_group* find_unique_effect_group(
    const sdk_object_detail_snapshot& detail,
    const std::string_view effect_name) noexcept {
    const sdk_effect_items_group* match = nullptr;
    for (const sdk_effect_items_group& group : detail.effect_items) {
        if (group.effect.name != effect_name) {
            continue;
        }
        if (match != nullptr || group.effect.occurrence != 0) {
            return nullptr;
        }
        match = &group;
    }
    return match;
}

[[nodiscard]] const sdk_effect_item_snapshot* find_unique_item(
    const sdk_effect_items_group& group,
    const std::string_view item_name,
    const std::string_view item_type) noexcept {
    const sdk_effect_item_snapshot* match = nullptr;
    for (const sdk_effect_item_snapshot& item : group.items) {
        if (item.name != item_name) {
            continue;
        }
        if (match != nullptr || item.type != item_type) {
            return nullptr;
        }
        match = &item;
    }
    return match;
}

[[nodiscard]] std::optional<std::string> get_string_value(
    const sdk_effect_item_snapshot* item) {
    if (item == nullptr || !item->value.has_value()) {
        return std::nullopt;
    }
    const auto* value = std::get_if<std::string>(&*item->value);
    return value == nullptr ? std::nullopt : std::optional<std::string>(*value);
}

[[nodiscard]] std::optional<bool> get_boolean_value(
    const sdk_effect_item_snapshot* item) noexcept {
    if (item == nullptr || !item->value.has_value()) {
        return std::nullopt;
    }
    const auto* value = std::get_if<bool>(&*item->value);
    return value == nullptr ? std::nullopt : std::optional<bool>(*value);
}

struct psd_object_contract final {
    const sdk_effect_items_group* group = nullptr;
    const sdk_effect_item_snapshot* target_item = nullptr;
    std::optional<std::string> psd_path;
    std::optional<bool> safeguard;
};

[[nodiscard]] std::optional<psd_object_contract> validate_psd_object_contract(
    const sdk_object_detail_snapshot& detail,
    const native_psd_item_operation operation) {
    const sdk_effect_items_group* group = nullptr;
    if (operation == native_psd_item_operation::layer_state) {
        group = find_unique_effect_group(detail, PSD_FILE_EFFECT);
    } else {
        const sdk_effect_items_group* psd = find_unique_effect_group(detail, PSD_FILE_EFFECT);
        const sdk_effect_items_group* voice = find_unique_effect_group(detail, PSD_VOICE_EFFECT);
        if ((psd == nullptr) == (voice == nullptr)) {
            return std::nullopt;
        }
        group = psd == nullptr ? voice : psd;
    }
    const char* target_name = operation == native_psd_item_operation::character
        ? "キャラクターID"
        : "レイヤー";
    const sdk_effect_item_snapshot* target = find_unique_item(*group, target_name, "string");
    if (target == nullptr || !target->is_writable) {
        return std::nullopt;
    }
    psd_object_contract result{
        .group = group,
        .target_item = target,
    };
    if (group->effect.name == PSD_FILE_EFFECT) {
        constexpr std::array<std::pair<std::string_view, std::string_view>, 6> required_items{
            std::pair<std::string_view, std::string_view>{"PSDファイル", "file"},
            std::pair<std::string_view, std::string_view>{"セーフガード", "check"},
            std::pair<std::string_view, std::string_view>{"タグ", "string"},
            std::pair<std::string_view, std::string_view>{"シーンID", "string"},
            std::pair<std::string_view, std::string_view>{"キャラクターID", "string"},
            std::pair<std::string_view, std::string_view>{"レイヤー", "string"},
        };
        for (const auto& [name, type] : required_items) {
            if (find_unique_item(*group, name, type) == nullptr) {
                return std::nullopt;
            }
        }
        result.psd_path = get_string_value(find_unique_item(*group, "PSDファイル", "file"));
        result.safeguard = get_boolean_value(find_unique_item(*group, "セーフガード", "check"));
        if (!result.psd_path.has_value() || !result.safeguard.has_value()) {
            return std::nullopt;
        }
    } else {
        constexpr std::array<std::pair<std::string_view, std::string_view>, 3> required_items{
            std::pair<std::string_view, std::string_view>{"キャラクターID", "string"},
            std::pair<std::string_view, std::string_view>{"テキスト", "text"},
            std::pair<std::string_view, std::string_view>{"音声ファイル", "file"},
        };
        for (const auto& [name, type] : required_items) {
            if (find_unique_item(*group, name, type) == nullptr) {
                return std::nullopt;
            }
        }
    }
    return result;
}

[[nodiscard]] std::filesystem::path utf8_to_filesystem_path(const std::string_view raw_path) {
    std::u8string utf8_path;
    utf8_path.reserve(raw_path.size());
    for (const unsigned char character : raw_path) {
        utf8_path.push_back(static_cast<char8_t>(character));
    }
    return std::filesystem::path(utf8_path).lexically_normal();
}

[[nodiscard]] bool is_supported_psd_file(const std::string& raw_path) {
    const std::filesystem::path path = utf8_to_filesystem_path(raw_path);
    std::wstring extension = path.extension().wstring();
    std::ranges::transform(extension, extension.begin(), [](const wchar_t character) {
        return static_cast<wchar_t>(std::towlower(character));
    });
    std::error_code error;
    return path.is_absolute() && (extension == L".psd" || extension == L".psb")
        && std::filesystem::is_regular_file(path, error) && !error;
}

[[nodiscard]] bool paths_identify_same_file(
    const std::filesystem::path& left,
    const std::filesystem::path& right) noexcept {
    std::error_code error;
    const bool is_equivalent = std::filesystem::equivalent(left, right, error);
    return !error && is_equivalent;
}

struct psd_create_parameters final {
    std::filesystem::path psd_path;
    int scene_id;
    int layer;
    int start_frame;
    std::optional<std::string> name;
};

[[nodiscard]] psd_create_parameters parse_psd_create_parameters(
    const nlohmann::json& params) {
    const std::string raw_path = parse_required_string(params, "psdPath");
    if (!is_supported_psd_file(raw_path)) {
        throw std::invalid_argument("psdPath must identify an existing absolute PSD or PSB file");
    }
    const auto placement_value = params.find("placement");
    if (placement_value == params.end() || !placement_value->is_object()) {
        throw std::invalid_argument("placement must be an object");
    }
    const nlohmann::json& placement = *placement_value;
    const int scene_id = parse_required_integer(placement, "sceneId", 0);
    const int layer = parse_required_integer(placement, "layer", 1);
    const int start_frame = parse_required_integer(placement, "startFrame", 1);
    const bool has_end = placement.contains("endFrame")
        && !placement.at("endFrame").is_null();
    const bool has_duration = placement.contains("durationFrames")
        && !placement.at("durationFrames").is_null();
    if (has_end == has_duration) {
        throw std::invalid_argument(
            "placement must contain exactly one of endFrame or durationFrames");
    }
    if (has_end) {
        static_cast<void>(parse_required_integer(placement, "endFrame", start_frame));
    } else {
        static_cast<void>(parse_required_integer(placement, "durationFrames", 1));
    }
    std::optional<std::string> name;
    const auto name_value = params.find("name");
    if (name_value != params.end() && !name_value->is_null()) {
        name = parse_required_string(params, "name", true);
    }
    return {
        .psd_path = utf8_to_filesystem_path(raw_path),
        .scene_id = scene_id,
        .layer = layer,
        .start_frame = start_frame,
        .name = std::move(name),
    };
}

[[nodiscard]] std::optional<std::filesystem::path> get_project_path(
    const sdk_status_snapshot& status) {
    if (!status.project_path.has_value()) {
        return std::nullopt;
    }
    return utf8_to_filesystem_path(*status.project_path);
}

[[nodiscard]] nlohmann::json create_psd_create_change(
    const psd_create_parameters& parameters) {
    return nlohmann::json::array({nlohmann::json{
        {"kind", "createPsd"},
        {"target", "scene:" + std::to_string(parameters.scene_id)
            + "/layer:" + std::to_string(parameters.layer)
            + "/frame:" + std::to_string(parameters.start_frame)},
    }});
}

[[nodiscard]] std::optional<sdk_object_detail_snapshot> find_created_psd_object(
    sdk_read_facade& sdk,
    const bridge_identity& identity,
    const std::string& project_generation,
    const psd_create_parameters& parameters) {
    const sdk_timeline_query_result timeline = sdk.query_timeline({
        .scene_id = parameters.scene_id,
        .layer_start = parameters.layer,
        .layer_end = parameters.layer,
        .start_frame = parameters.start_frame,
        .end_frame = parameters.start_frame,
        .effect_name = PSD_FILE_EFFECT,
        .offset = 0U,
        .limit = 100U,
        .include_effects = true,
        .use_display_defaults = false,
    });
    if (!timeline.ok || timeline.timeline.is_truncated) {
        return std::nullopt;
    }
    std::optional<sdk_object_detail_snapshot> match;
    for (const sdk_object_snapshot& object : timeline.timeline.objects) {
        if (object.candidate.layer != parameters.layer
            || object.candidate.start_frame != parameters.start_frame
            || !has_effect(object, PSD_FILE_EFFECT)) {
            continue;
        }
        const object_locator locator = create_object_locator(
            identity.instance_id,
            project_generation,
            object.candidate);
        const sdk_object_query_result detail = sdk.query_object(
            locator,
            identity.instance_id,
            project_generation,
            false,
            true);
        if (!detail.ok) {
            return std::nullopt;
        }
        const auto contract = validate_psd_object_contract(
            detail.detail,
            native_psd_item_operation::layer_state);
        if (!contract.has_value() || !contract->psd_path.has_value()
            || !paths_identify_same_file(
                utf8_to_filesystem_path(*contract->psd_path),
                parameters.psd_path)) {
            continue;
        }
        if (match.has_value()) {
            return std::nullopt;
        }
        match = detail.detail;
    }
    return match;
}

[[nodiscard]] operation_result create_external_partial_failure(
    const std::string& message,
    nlohmann::json result,
    operation_execution_context& context) {
    return operation_result{
        .ok = false,
        .outcome = "partial",
        .result_json = std::move(result).dump(),
        .error_code = "partial_operation",
        .error_message = message,
        .revision = context.revisions().content_revision(),
        .view_revision = context.revisions().view_revision(),
        .retryable = false,
        .undo_recommended = true,
    };
}

[[nodiscard]] std::optional<sdk_object_snapshot> find_updated_object(
    sdk_read_facade& sdk,
    const sdk_object_snapshot& before) {
    const sdk_timeline_query_result timeline = sdk.query_timeline({
        .scene_id = before.candidate.scene_id,
        .layer_start = before.candidate.layer,
        .layer_end = before.candidate.layer,
        .start_frame = before.candidate.start_frame,
        .end_frame = before.candidate.end_frame,
        .name_contains = before.candidate.name,
        .offset = 0U,
        .limit = 100U,
        .include_effects = true,
        .use_display_defaults = false,
    });
    if (!timeline.ok || timeline.timeline.is_truncated) {
        return std::nullopt;
    }
    std::optional<sdk_object_snapshot> match;
    for (const sdk_object_snapshot& object : timeline.timeline.objects) {
        if (object.candidate.scene_id != before.candidate.scene_id
            || object.candidate.layer != before.candidate.layer
            || object.candidate.start_frame != before.candidate.start_frame
            || object.candidate.end_frame != before.candidate.end_frame
            || object.candidate.name != before.candidate.name) {
            continue;
        }
        if (match.has_value()) {
            return std::nullopt;
        }
        match = object;
    }
    return match;
}

[[nodiscard]] nlohmann::json create_item_change(
    const sdk_effect_items_group& group,
    const sdk_effect_item_snapshot& item,
    const std::string& after) {
    nlohmann::json change{
        {"kind", "setEffectItem"},
        {"target", "effect:" + group.effect.name + ":0/" + item.name},
        {"after", after},
    };
    if (item.value.has_value()) {
        change["before"] = serialize_value(*item.value);
    }
    return nlohmann::json::array({std::move(change)});
}

[[nodiscard]] std::vector<std::string> parse_validation_checks(
    const nlohmann::json& params) {
    static const std::array allowed{
        std::string_view("setup"),
        std::string_view("character"),
        std::string_view("blink"),
        std::string_view("lipSync"),
        std::string_view("subtitle"),
    };
    const auto value = params.find("checks");
    if (value == params.end() || value->is_null()) {
        return {"setup", "character", "blink", "lipSync", "subtitle"};
    }
    if (!value->is_array() || value->size() > allowed.size()) {
        throw std::invalid_argument("checks must be an array of supported validation names");
    }
    std::vector<std::string> checks;
    for (const nlohmann::json& item : *value) {
        if (!item.is_string()) {
            throw std::invalid_argument("checks entries must be strings");
        }
        const std::string parsed = item.get<std::string>();
        if (std::ranges::find(allowed, parsed) == allowed.end()
            || std::ranges::find(checks, parsed) != checks.end()) {
            throw std::invalid_argument("checks contains an unknown or duplicate validation name");
        }
        checks.push_back(parsed);
    }
    return checks;
}

[[nodiscard]] std::string parse_validation_scope(const nlohmann::json& params) {
    const auto value = params.find("scope");
    if (value == params.end() || value->is_null()) {
        return "object";
    }
    const std::string scope = parse_required_string(params, "scope");
    if (scope != "object" && scope != "scene") {
        throw std::invalid_argument("scope must be object or scene");
    }
    return scope;
}

[[nodiscard]] nlohmann::json create_diagnostic_check(
    std::string check_id,
    std::string status,
    std::vector<std::string> evidence,
    std::string impact,
    std::string recommendation,
    const bool can_retry = false) {
    return {
        {"checkId", std::move(check_id)},
        {"status", std::move(status)},
        {"evidence", std::move(evidence)},
        {"impact", std::move(impact)},
        {"recommendation", std::move(recommendation)},
        {"canRetry", can_retry},
    };
}

[[nodiscard]] bool has_any_effect(
    const sdk_object_detail_snapshot& detail,
    const std::span<const std::string_view> effect_names) noexcept {
    return std::ranges::any_of(detail.object.effects, [effect_names](const auto& effect) {
        return std::ranges::find(effect_names, effect.name) != effect_names.end();
    });
}

[[nodiscard]] std::string profile_evidence(const psd_profile_detection& profile) {
    if (profile.is_match) {
        return "profile=" + profile.profile.value_or(PSD_PROFILE_NAME);
    }
    return "profile=unsupported;failures=" + std::to_string(profile.failures.size());
}

[[nodiscard]] std::string summarize_profile_failures(
    const psd_profile_detection& profile) {
    std::string summary;
    for (const std::string& failure : profile.failures) {
        if (!summary.empty()) {
            summary += ',';
        }
        if (summary.size() + failure.size() > 2'048U) {
            summary += "truncated";
            break;
        }
        summary += failure;
    }
    return summary.empty() ? "unknown" : summary;
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

native_psd_item_request_handler::native_psd_item_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk,
    const native_psd_item_operation item_operation)
    : identity_(std::move(identity)),
      sdk_(sdk),
      item_operation_(item_operation) {}

std::string native_psd_item_request_handler::operation() const {
    return item_operation_ == native_psd_item_operation::character
        ? "psd.setCharacter"
        : "psd.setLayerState";
}

bool native_psd_item_request_handler::is_mutating() const noexcept {
    return true;
}

operation_result native_psd_item_request_handler::execute(
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
            throw std::invalid_argument("PSD item parameters must be an object");
        }
        const object_locator locator = parse_locator(params);
        const char* value_name = item_operation_ == native_psd_item_operation::character
            ? "characterId"
            : "layerState";
        const std::string requested_value = parse_required_string(params, value_name);
        const psd_value_validation value_validation =
            item_operation_ == native_psd_item_operation::character
            ? validate_psd_character_id(requested_value)
            : validate_psd_layer_state(requested_value);
        if (!value_validation.ok) {
            return create_native_failure(
                value_validation.error_code,
                value_validation.error_message,
                context);
        }
        const psd_profile_detection profile = detect_runtime_psd_profile(sdk_);
        if (!profile.is_match) {
            return create_native_failure(
                "capability_not_available",
                "The active PSDToolKit2 effect and item profile is not supported: "
                    + summarize_profile_failures(profile),
                context);
        }
        const std::string project_generation = context.revisions().project_generation();
        const sdk_object_query_result before = sdk_.query_object(
            locator,
            identity_.instance_id,
            project_generation,
            false,
            true);
        if (!before.ok) {
            return create_native_failure(
                before.error_code,
                before.error_message,
                context,
                before.error_code == "read_not_available"
                    || before.error_code == "sdk_query_failed");
        }
        const std::optional<psd_object_contract> before_contract =
            validate_psd_object_contract(before.detail, item_operation_);
        if (!before_contract.has_value()) {
            return create_native_failure(
                "capability_not_available",
                "The target object does not match the supported PSDToolKit2 profile",
                context);
        }
        if (item_operation_ == native_psd_item_operation::layer_state
            && (!before_contract->safeguard.value_or(false)
                || !is_supported_psd_file(*before_contract->psd_path))) {
            return create_native_failure(
                "invalid_media_file",
                "The PSD layer target must preserve safeguard and reference an existing PSD or PSB",
                context);
        }
        const sdk_effect_edit_request edit_request{
            .kind = sdk_effect_edit_kind::set_item,
            .locator = locator,
            .effect_name = before_contract->group->effect.name,
            .effect_occurrence = 0,
            .item_name = before_contract->target_item->name,
            .item_value = requested_value,
        };
        const sdk_effect_edit_result preflight = sdk_.edit_effect(
            edit_request,
            identity_.instance_id,
            project_generation,
            true);
        if (!preflight.ok || !preflight.item.has_value()) {
            return create_native_failure(
                preflight.ok ? "sdk_query_failed" : preflight.error_code,
                preflight.ok ? "PSD item preflight omitted its item" : preflight.error_message,
                context,
                preflight.error_code == "read_not_available"
                    || preflight.error_code == "sdk_query_failed");
        }
        const nlohmann::json changes = create_item_change(
            *before_contract->group,
            *before_contract->target_item,
            requested_value);
        nlohmann::json dry_result{
            {"object", serialize_object(before.detail.object, identity_, project_generation)},
            {value_name, requested_value},
            {"plannedChanges", changes},
        };
        if (item_operation_ == native_psd_item_operation::character) {
            dry_result["item"] = serialize_item(*preflight.item);
        } else {
            dry_result["roundTripMatched"] = nlohmann::json(nullptr);
        }
        if (request.dry_run) {
            return create_native_success(dry_result.dump(), context);
        }
        if (!context.reach_commit_point()) {
            return create_native_failure(
                "operation_cancelled",
                "PSD item edit was cancelled before commit",
                context);
        }
        const sdk_effect_edit_result edited = sdk_.edit_effect(
            edit_request,
            identity_.instance_id,
            project_generation,
            false);
        if (!edited.ok) {
            if (edited.has_changed) {
                static_cast<void>(context.revisions().commit_content_change());
                return create_partial_failure(
                    "PSD item changed but SDK verification failed",
                    context);
            }
            return create_native_failure(
                edited.error_code,
                edited.error_message,
                context,
                edited.error_code == "sdk_query_failed");
        }
        if (edited.has_changed) {
            static_cast<void>(context.revisions().commit_content_change());
        }
        const std::optional<sdk_object_snapshot> updated = find_updated_object(
            sdk_, before.detail.object);
        if (!updated.has_value()) {
            return edited.has_changed
                ? create_partial_failure(
                    "PSD item changed but the updated object could not be identified",
                    context)
                : create_native_failure(
                    "sdk_query_failed",
                    "The unchanged PSD object could not be identified",
                    context,
                    true);
        }
        const object_locator updated_locator = create_object_locator(
            identity_.instance_id,
            project_generation,
            updated->candidate);
        const sdk_object_query_result after = sdk_.query_object(
            updated_locator,
            identity_.instance_id,
            project_generation,
            false,
            true);
        const std::optional<psd_object_contract> after_contract = after.ok
            ? validate_psd_object_contract(after.detail, item_operation_)
            : std::nullopt;
        const std::optional<std::string> round_trip = after_contract.has_value()
            ? get_string_value(after_contract->target_item)
            : std::nullopt;
        const bool preserved_guard = item_operation_ != native_psd_item_operation::layer_state
            || (after_contract->psd_path == before_contract->psd_path
                && after_contract->safeguard == before_contract->safeguard
                && after_contract->safeguard.value_or(false));
        if (!after.ok || !after_contract.has_value()
            || round_trip != requested_value || !preserved_guard
            || !edited.item.has_value()) {
            return edited.has_changed
                ? create_partial_failure(
                    "PSD item changed but its round-trip postcondition failed",
                    context)
                : create_native_failure(
                    "sdk_query_failed",
                    "PSD item round-trip verification failed",
                    context,
                    true);
        }
        nlohmann::json result{
            {"object", serialize_object(after.detail.object, identity_, project_generation)},
            {value_name, *round_trip},
            {"appliedChanges", changes},
        };
        if (item_operation_ == native_psd_item_operation::character) {
            result["item"] = serialize_item(*edited.item);
        } else {
            result["roundTripMatched"] = true;
        }
        return create_native_success(result.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure(
            "invalid_argument", "PSD item request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

native_psd_validate_request_handler::native_psd_validate_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk)
    : identity_(std::move(identity)),
      sdk_(sdk) {}

std::string native_psd_validate_request_handler::operation() const {
    return "psd.validate";
}

bool native_psd_validate_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_psd_validate_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("PSD validation parameters must be an object");
        }
        const std::string scope = parse_validation_scope(params);
        const std::vector<std::string> requested_checks = parse_validation_checks(params);
        std::optional<object_locator> requested_locator;
        if (params.contains("locator") && !params.at("locator").is_null()) {
            requested_locator = parse_locator(params);
        }
        if (scope == "object" && !requested_locator.has_value()) {
            throw std::invalid_argument("locator is required for object scope");
        }
        const sdk_project_query_result project = sdk_.query_project(false);
        if (!project.ok) {
            return create_native_failure(
                project.error_code,
                project.error_message,
                context,
                project.error_code == "sdk_query_failed");
        }
        const int scene_id = requested_locator.has_value()
            ? requested_locator->scene_id
            : project.project.current_scene_id;
        if (scene_id != project.project.current_scene_id) {
            return create_native_failure(
                "invalid_argument",
                "PSD validation can only target the active SDK scene",
                context);
        }
        const sdk_timeline_query_result timeline = sdk_.query_timeline({
            .scene_id = scene_id,
            .offset = 0U,
            .limit = 1'000U,
            .include_effects = true,
            .use_display_defaults = false,
        });
        if (!timeline.ok || timeline.timeline.is_truncated) {
            return create_native_failure(
                timeline.ok ? "sdk_query_failed" : timeline.error_code,
                timeline.ok
                    ? "PSD validation scan exceeded the supported timeline page"
                    : timeline.error_message,
                context,
                true);
        }
        const std::string project_generation = context.revisions().project_generation();
        std::vector<sdk_object_detail_snapshot> targets;
        if (scope == "object") {
            const sdk_object_query_result detail = sdk_.query_object(
                *requested_locator,
                identity_.instance_id,
                project_generation,
                true,
                true);
            if (!detail.ok) {
                return create_native_failure(
                    detail.error_code,
                    detail.error_message,
                    context,
                    detail.error_code == "read_not_available"
                        || detail.error_code == "sdk_query_failed");
            }
            if (!validate_psd_object_contract(
                    detail.detail,
                    native_psd_item_operation::character).has_value()) {
                return create_native_failure(
                    "invalid_argument",
                    "The validation locator does not identify a supported PSDToolKit2 object",
                    context);
            }
            targets.push_back(detail.detail);
        } else {
            for (const sdk_object_snapshot& object : timeline.timeline.objects) {
                if (!has_effect(object, PSD_FILE_EFFECT)
                    && !has_effect(object, PSD_VOICE_EFFECT)) {
                    continue;
                }
                const object_locator locator = create_object_locator(
                    identity_.instance_id,
                    project_generation,
                    object.candidate);
                const sdk_object_query_result detail = sdk_.query_object(
                    locator,
                    identity_.instance_id,
                    project_generation,
                    true,
                    true);
                if (!detail.ok) {
                    return create_native_failure(
                        detail.error_code,
                        detail.error_message,
                        context,
                        detail.error_code == "read_not_available"
                            || detail.error_code == "sdk_query_failed");
                }
                targets.push_back(detail.detail);
            }
        }

        std::size_t subtitle_aliases = 0U;
        std::size_t alias_query_failures = 0U;
        for (const sdk_object_snapshot& object : timeline.timeline.objects) {
            const object_locator locator = create_object_locator(
                identity_.instance_id,
                project_generation,
                object.candidate);
            const sdk_object_query_result detail = sdk_.query_object(
                locator,
                identity_.instance_id,
                project_generation,
                true,
                false);
            if (!detail.ok) {
                ++alias_query_failures;
                continue;
            }
            if (detail.detail.alias.has_value()
                && detail.detail.alias->find("require(\"PSDToolKit\").mes")
                    != std::string::npos) {
                ++subtitle_aliases;
            }
        }

        const psd_profile_detection profile = detect_runtime_psd_profile(sdk_);
        const std::string profile_summary = profile_evidence(profile);
        nlohmann::json checks = nlohmann::json::array();
        for (const std::string& check : requested_checks) {
            if (check == "setup") {
                std::vector<sdk_object_snapshot> setups;
                for (const sdk_object_snapshot& object : timeline.timeline.objects) {
                    if (has_effect(object, PSD_SETUP_EFFECT)) {
                        setups.push_back(object);
                    }
                }
                const bool is_valid = setups.size() == 1U
                    && is_setup_placement_valid(setups.front(), timeline.timeline);
                const std::string status = !profile.is_match ? "fail"
                    : is_valid ? "pass"
                    : setups.empty() ? "warning"
                    : "fail";
                checks.push_back(create_diagnostic_check(
                    "psd.setup",
                    status,
                    {profile_summary, "setupObjects=" + std::to_string(setups.size()),
                        "placementValid=" + std::string(is_valid ? "true" : "false")},
                    status == "pass" ? "The PSD setup ordering is usable."
                        : "PSD rendering can initialize late or ambiguously.",
                    status == "pass" ? "No action is required."
                        : "Run aviutl_psd_setup and resolve duplicate or misplaced setup objects."));
            } else if (check == "character") {
                std::size_t invalid = 0U;
                for (const sdk_object_detail_snapshot& target : targets) {
                    const auto contract = validate_psd_object_contract(
                        target,
                        native_psd_item_operation::character);
                    const std::optional<std::string> character = contract.has_value()
                        ? get_string_value(contract->target_item)
                        : std::nullopt;
                    if (!character.has_value() || !validate_psd_character_id(*character).ok) {
                        ++invalid;
                    }
                }
                const std::string status = targets.empty() ? "skipped"
                    : !profile.is_match || invalid > 0U ? "fail"
                    : "pass";
                checks.push_back(create_diagnostic_check(
                    "psd.character",
                    status,
                    {profile_summary, "targetObjects=" + std::to_string(targets.size()),
                        "invalidCharacterIds=" + std::to_string(invalid)},
                    status == "pass" ? "Character references are available for PSD scripts."
                        : "Blink, lip-sync, or subtitle references may not resolve.",
                    status == "pass" ? "No action is required."
                        : "Assign a valid character ID to every target PSD or voice object."));
            } else if (check == "blink") {
                const std::size_t matches = static_cast<std::size_t>(std::ranges::count_if(
                    targets,
                    [](const sdk_object_detail_snapshot& target) {
                        constexpr std::array effect_names{
                            std::string_view("目パチ@PSDToolKit"),
                        };
                        return has_any_effect(target, effect_names);
                    }));
                const std::string status = targets.empty() ? "skipped"
                    : !profile.is_match ? "fail"
                    : matches > 0U ? "pass"
                    : "warning";
                checks.push_back(create_diagnostic_check(
                    "psd.blink",
                    status,
                    {profile_summary, "blinkEffects=" + std::to_string(matches)},
                    status == "pass" ? "Blink structure was detected."
                        : "Automatic blink is not confirmed for the target.",
                    status == "pass" ? "No action is required."
                        : "Add and configure the PSDToolKit2 blink effect."));
            } else if (check == "lipSync") {
                const std::size_t matches = static_cast<std::size_t>(std::ranges::count_if(
                    targets,
                    [](const sdk_object_detail_snapshot& target) {
                        constexpr std::array effect_names{
                            std::string_view("口パク 開閉のみ@PSDToolKit"),
                            std::string_view("口パク あいうえお@PSDToolKit"),
                        };
                        return has_any_effect(target, effect_names);
                    }));
                const std::string status = targets.empty() ? "skipped"
                    : !profile.is_match ? "fail"
                    : matches > 0U ? "pass"
                    : "warning";
                checks.push_back(create_diagnostic_check(
                    "psd.lipSync",
                    status,
                    {profile_summary, "lipSyncEffects=" + std::to_string(matches)},
                    status == "pass" ? "Lip-sync structure was detected."
                        : "Automatic lip-sync is not confirmed for the target.",
                    status == "pass" ? "No action is required."
                        : "Add a supported PSDToolKit2 lip-sync effect and verify LAB pairing."));
            } else {
                const std::string status = !profile.is_match ? "fail"
                    : alias_query_failures > 0U ? "warning"
                    : subtitle_aliases > 0U ? "pass"
                    : "warning";
                checks.push_back(create_diagnostic_check(
                    "psd.subtitle",
                    status,
                    {profile_summary, "subtitleAliases=" + std::to_string(subtitle_aliases),
                        "aliasQueryFailures=" + std::to_string(alias_query_failures)},
                    status == "pass" ? "A versioned PSD subtitle alias was detected."
                        : "PSD subtitle rendering could not be fully confirmed.",
                    status == "pass" ? "No action is required."
                        : "Create the subtitle from the bundled versioned alias template.",
                    alias_query_failures > 0U));
            }
        }
        return create_native_success(nlohmann::json{
            {"checks", std::move(checks)},
            {"profile", profile.is_match
                ? nlohmann::json(profile.profile.value_or(PSD_PROFILE_NAME))
                : nlohmann::json(nullptr)},
        }.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure(
            "invalid_argument", "PSD validation request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

native_psd_create_request_handler::native_psd_create_request_handler(
    bridge_identity identity,
    sdk_read_facade& sdk,
    std::shared_ptr<gcmz_client> gcmz)
    : identity_(std::move(identity)),
      sdk_(sdk),
      gcmz_(std::move(gcmz)) {
    if (gcmz_ == nullptr) {
        throw std::invalid_argument("PSD create requires a GCMZDrops client");
    }
}

std::string native_psd_create_request_handler::operation() const {
    return "psd.create";
}

bool native_psd_create_request_handler::is_mutating() const noexcept {
    return true;
}

operation_result native_psd_create_request_handler::execute(
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
            throw std::invalid_argument("PSD create parameters must be an object");
        }
        const psd_create_parameters parameters = parse_psd_create_parameters(params);
        const psd_profile_detection profile = detect_runtime_psd_profile(sdk_);
        if (!profile.is_match) {
            return create_native_failure(
                "capability_not_available",
                "The active PSDToolKit2 effect and item profile is not supported: "
                    + summarize_profile_failures(profile),
                context);
        }
        const sdk_status_snapshot status = sdk_.query_status();
        if (!status.is_sdk_ready || status.has_query_error
            || (status.project_state != sdk_project_state::saved
                && status.project_state != sdk_project_state::unsaved)
            || status.edit_state != sdk_edit_state::edit) {
            return create_native_failure(
                status.has_query_error ? "sdk_query_failed" : "edit_not_available",
                status.has_query_error
                    ? status.query_error
                    : "AviUtl2 is not ready for a PSD create operation",
                context,
                status.has_query_error);
        }
        const sdk_project_query_result project = sdk_.query_project(false);
        if (!project.ok) {
            return create_native_failure(
                project.error_code,
                project.error_message,
                context,
                project.error_code == "sdk_query_failed");
        }
        if (parameters.scene_id != project.project.current_scene_id) {
            return create_native_failure(
                "invalid_argument",
                "PSD creation can only target the active SDK scene",
                context);
        }
        const sdk_timeline_query_result occupied = sdk_.query_timeline({
            .scene_id = parameters.scene_id,
            .layer_start = parameters.layer,
            .layer_end = parameters.layer,
            .start_frame = parameters.start_frame,
            .end_frame = parameters.start_frame,
            .offset = 0U,
            .limit = 2U,
            .include_effects = false,
            .use_display_defaults = false,
        });
        if (!occupied.ok || occupied.timeline.is_truncated) {
            return create_native_failure(
                occupied.ok ? "sdk_query_failed" : occupied.error_code,
                occupied.ok
                    ? "PSD placement collision scan was ambiguous"
                    : occupied.error_message,
                context,
                true);
        }
        if (!occupied.timeline.objects.empty()) {
            return create_native_failure(
                "object_collision",
                "PSD placement overlaps an existing object",
                context);
        }
        const std::optional<std::filesystem::path> project_path = get_project_path(status);
        const std::uint32_t process_id = GetCurrentProcessId();
        const std::uint32_t probe_timeout = (std::min)(request.timeout_ms, 2'000U);
        const gcmz_probe_result probe = gcmz_->probe(
            process_id,
            project_path,
            probe_timeout);
        if (!probe.ok) {
            return create_native_failure(
                probe.error_code,
                probe.error_message,
                context,
                probe.error_code == "gcmz_timeout");
        }
        const nlohmann::json changes = create_psd_create_change(parameters);
        if (request.dry_run) {
            return create_native_success(nlohmann::json{
                {"object", nullptr},
                {"plannedChanges", changes},
            }.dump(), context);
        }
        if (!context.reach_commit_point()) {
            return create_native_failure(
                "operation_cancelled",
                "PSD creation was cancelled before cursor positioning",
                context);
        }
        const sdk_view_edit_result cursor = sdk_.edit_view({
            .scene_id = parameters.scene_id,
            .frame = parameters.start_frame,
        }, false);
        if (!cursor.ok) {
            return create_native_failure(
                cursor.error_code,
                cursor.error_message,
                context,
                cursor.error_code == "sdk_query_failed");
        }
        if (cursor.has_changed) {
            static_cast<void>(context.revisions().commit_view_change());
        }
        const sdk_project_query_result positioned = sdk_.query_project(false);
        if (!positioned.ok
            || positioned.project.current_scene_id != parameters.scene_id
            || positioned.project.current_frame != parameters.start_frame) {
            return create_native_failure(
                "view_changed",
                "The AviUtl2 cursor changed before GCMZDrops delivery",
                context);
        }
        const std::uint32_t send_timeout = (std::min)(request.timeout_ms, 10'000U);
        const gcmz_send_result sent = gcmz_->send_files(
            gcmz_drop_request{
                .layer = parameters.layer,
                .frame_advance = 0,
                .margin = -1,
                .files = {parameters.psd_path},
            },
            process_id,
            project_path,
            send_timeout);
        if (!sent.ok && !sent.target.ok) {
            return create_native_failure(
                sent.error_code,
                sent.error_message,
                context,
                sent.error_code == "gcmz_timeout");
        }

        const std::string project_generation = context.revisions().project_generation();
        const auto maximum_poll = std::chrono::milliseconds((std::min)(request.timeout_ms, 10'000U));
        const auto deadline = std::chrono::steady_clock::now() + maximum_poll;
        std::optional<sdk_object_detail_snapshot> created;
        do {
            created = find_created_psd_object(
                sdk_, identity_, project_generation, parameters);
            if (created.has_value()) {
                break;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        } while (std::chrono::steady_clock::now() < deadline);
        if (!created.has_value()) {
            static_cast<void>(context.revisions().commit_content_change());
            return create_external_partial_failure(
                sent.ok
                    ? "GCMZDrops accepted the PSD but its created object could not be verified"
                    : "GCMZDrops timed out and no unique created PSD object could be verified",
                nlohmann::json{
                    {"object", nullptr},
                    {"appliedChanges", changes},
                },
                context);
        }

        sdk_object_snapshot final_object = created->object;
        if (parameters.name.has_value()
            && final_object.candidate.name != *parameters.name) {
            const object_locator locator = create_object_locator(
                identity_.instance_id,
                project_generation,
                final_object.candidate);
            const sdk_object_edit_result named = sdk_.edit_object({
                .kind = sdk_object_edit_kind::set_name,
                .locator = locator,
                .name = parameters.name,
            }, identity_.instance_id, project_generation, false);
            if (!named.ok || !named.object.has_value()) {
                static_cast<void>(context.revisions().commit_content_change());
                return create_external_partial_failure(
                    "The PSD object was created but its requested name could not be verified",
                    nlohmann::json{
                        {"object", serialize_object(
                            final_object, identity_, project_generation)},
                        {"appliedChanges", changes},
                    },
                    context);
            }
            final_object = *named.object;
        }
        static_cast<void>(context.revisions().commit_content_change());
        return create_native_success(nlohmann::json{
            {"object", serialize_object(final_object, identity_, project_generation)},
            {"appliedChanges", changes},
        }.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure(
            "invalid_argument", "PSD create request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

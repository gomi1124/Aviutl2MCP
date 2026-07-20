#include "aviutl2_mcp/native_effect_request_handlers.h"

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

[[nodiscard]] std::size_t count_utf8_characters(const std::string_view value) noexcept {
    std::size_t character_count = 0U;
    for (const unsigned char byte : value) {
        if ((byte & 0xc0U) != 0x80U) {
            ++character_count;
        }
    }
    return character_count;
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
    if (parsed.empty() || count_utf8_characters(parsed) > maximum_length) {
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

[[nodiscard]] std::size_t parse_cursor(const nlohmann::json& params) {
    const auto value = params.find("cursor");
    if (value == params.end() || value->is_null()) {
        return 0U;
    }
    if (!value->is_string()) {
        throw std::invalid_argument("cursor must be a string");
    }
    constexpr std::string_view PREFIX = "effects:";
    const std::string text = value->get<std::string>();
    if (!text.starts_with(PREFIX)) {
        throw std::invalid_argument("cursor does not belong to the effect catalog");
    }
    const std::string_view offset_text(text.data() + PREFIX.size(), text.size() - PREFIX.size());
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

[[nodiscard]] std::string parse_effect_name(const nlohmann::json& params) {
    const auto effect_value = params.find("effect");
    if (effect_value == params.end() || !effect_value->is_object()) {
        throw std::invalid_argument("effect must be an object");
    }
    const std::optional<std::string> name = parse_optional_string(*effect_value, "name", 4096U);
    if (!name.has_value()) {
        throw std::invalid_argument("effect.name is required");
    }
    return *name;
}

[[nodiscard]] nlohmann::json serialize_effect_definition(
    const sdk_effect_definition& definition) {
    return {
        {"name", definition.name},
        {"type", definition.type},
        {"flags", definition.flags},
        {"isCreatable", definition.is_creatable},
    };
}

[[nodiscard]] nlohmann::json serialize_module(const sdk_module_summary& module) {
    return {
        {"type", module.type},
        {"name", module.name},
        {"information", module.information},
    };
}

[[nodiscard]] nlohmann::json serialize_effect_catalog(const sdk_effect_catalog_snapshot& catalog) {
    nlohmann::json effects = nlohmann::json::array();
    for (const sdk_effect_definition& definition : catalog.effects) {
        effects.push_back(serialize_effect_definition(definition));
    }
    nlohmann::json modules = nlohmann::json::array();
    for (const sdk_module_summary& module : catalog.modules) {
        modules.push_back(serialize_module(module));
    }
    return {
        {"effects", std::move(effects)},
        {"modules", std::move(modules)},
        {"fonts", catalog.fonts},
        {"palettes", catalog.palettes},
        {"nextCursor", catalog.is_truncated
            ? nlohmann::json("effects:" + std::to_string(catalog.next_offset))
            : nlohmann::json(nullptr)},
        {"isTruncated", catalog.is_truncated},
    };
}

[[nodiscard]] nlohmann::json serialize_effect_item(const sdk_effect_item_snapshot& item) {
    nlohmann::json result = {
        {"name", item.name},
        {"type", item.type},
        {"codec", item.codec},
        {"isWritable", item.is_writable},
    };
    if (!item.choices.empty()) {
        result["choices"] = item.choices;
    }
    return result;
}

}  // namespace

native_effect_list_request_handler::native_effect_list_request_handler(sdk_read_facade& sdk)
    : sdk_(sdk) {}

std::string native_effect_list_request_handler::operation() const {
    return "effect.list";
}

bool native_effect_list_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_effect_list_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Effect catalog parameters must be an object");
        }
        sdk_effect_catalog_query query{
            .category = parse_optional_string(params, "category", 32U),
            .name_contains = parse_optional_string(params, "nameContains", 4096U),
            .offset = parse_cursor(params),
            .limit = parse_limit(params),
        };
        const sdk_effect_catalog_query_result result = sdk_.query_effects(query);
        if (!result.ok) {
            return create_native_failure(
                result.error_code,
                result.error_message,
                context,
                result.error_code == "sdk_query_failed");
        }
        return create_native_success(serialize_effect_catalog(result.catalog).dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Effect catalog JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

native_effect_items_request_handler::native_effect_items_request_handler(sdk_read_facade& sdk)
    : sdk_(sdk) {}

std::string native_effect_items_request_handler::operation() const {
    return "effect.items.list";
}

bool native_effect_items_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_effect_items_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Effect item parameters must be an object");
        }
        const sdk_effect_items_query_result result = sdk_.query_effect_items(
            parse_effect_name(params),
            parse_boolean(params, "includeChoices", true));
        if (!result.ok) {
            return create_native_failure(
                result.error_code,
                result.error_message,
                context,
                result.error_code == "sdk_query_failed");
        }
        nlohmann::json items = nlohmann::json::array();
        for (const sdk_effect_item_snapshot& item : result.items) {
            items.push_back(serialize_effect_item(item));
        }
        return create_native_success(nlohmann::json{{"items", std::move(items)}}.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Effect item JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("sdk_query_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

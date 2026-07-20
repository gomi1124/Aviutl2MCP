#include "aviutl2_mcp/native_preview_request_handler.h"

#include "aviutl2_mcp/native_ipc_frame_codec.h"
#include "aviutl2_mcp/native_operation_result.h"
#include "aviutl2_mcp/preview_png_encoder.h"

#include <nlohmann/json.hpp>

#include <cstdint>
#include <limits>
#include <optional>
#include <stdexcept>
#include <utility>

namespace aviutl2_mcp {
namespace {

[[nodiscard]] int parse_integer(
    const nlohmann::json& object,
    const char* name,
    const int minimum,
    const int maximum = (std::numeric_limits<int>::max)()) {
    const auto value = object.find(name);
    if (value == object.end() || (!value->is_number_integer() && !value->is_number_unsigned())) {
        throw std::invalid_argument(std::string(name) + " must be an integer");
    }
    const std::int64_t parsed = value->get<std::int64_t>();
    if (parsed < minimum || parsed > maximum) {
        throw std::invalid_argument(std::string(name) + " is outside the supported range");
    }
    return static_cast<int>(parsed);
}

[[nodiscard]] std::optional<int> parse_optional_integer(
    const nlohmann::json& object,
    const char* name,
    const int minimum,
    const int maximum = (std::numeric_limits<int>::max)()) {
    const auto value = object.find(name);
    if (value == object.end() || value->is_null()) {
        return std::nullopt;
    }
    return parse_integer(object, name, minimum, maximum);
}

[[nodiscard]] bool parse_include_alpha(const nlohmann::json& params) {
    const auto value = params.find("includeAlpha");
    if (value == params.end() || value->is_null()) {
        return false;
    }
    if (!value->is_boolean()) {
        throw std::invalid_argument("includeAlpha must be a boolean");
    }
    return value->get<bool>();
}

}  // namespace

native_preview_request_handler::native_preview_request_handler(sdk_read_facade& sdk)
    : sdk_(sdk) {}

std::string native_preview_request_handler::operation() const {
    return "preview.render";
}

bool native_preview_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_preview_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Preview parameters must be an object");
        }
        const int frame = parse_integer(params, "frame", 1);
        const std::optional<int> scene_id = parse_optional_integer(params, "sceneId", 0);
        const std::optional<int> maximum_width = parse_optional_integer(
            params, "maxWidth", 1, 4096);
        const std::optional<int> maximum_height = parse_optional_integer(
            params, "maxHeight", 1, 4096);
        if (maximum_width.has_value() != maximum_height.has_value()) {
            throw std::invalid_argument(
                "maxWidth and maxHeight must be specified together");
        }
        const bool include_alpha = parse_include_alpha(params);
        const sdk_project_query_result project = sdk_.query_project(false);
        if (!project.ok) {
            return create_native_failure(
                project.error_code,
                project.error_message,
                context,
                project.error_code == "read_not_available"
                    || project.error_code == "sdk_query_failed");
        }
        if (scene_id.has_value() && *scene_id != project.project.current_scene_id) {
            return create_native_failure(
                "invalid_argument",
                "Preview can only render the active AviUtl2 scene",
                context);
        }
        if (context.is_cancelled()) {
            return create_native_failure(
                "operation_cancelled", "Preview was cancelled before rendering", context);
        }
        sdk_preview_render_result rendered = sdk_.render_preview(frame, request.timeout_ms);
        if (!rendered.ok) {
            return create_native_failure(
                rendered.error_code,
                rendered.error_message,
                context,
                rendered.error_code == "operation_timeout"
                    || rendered.error_code == "preview_busy"
                    || rendered.error_code == "sdk_query_failed");
        }
        if (rendered.frame != frame) {
            return create_native_failure(
                "preview_frame_mismatch",
                "AviUtl2 returned a different preview frame",
                context,
                true);
        }
        preview_png_image png = encode_preview_png(
            preview_rgba_image{
                .width = rendered.width,
                .height = rendered.height,
                .pixels = std::move(rendered.rgba),
            },
            maximum_width.value_or(1920),
            maximum_height.value_or(1080),
            include_alpha);
        const std::string sha256 = calculate_sha256(png.bytes);
        operation_result result = create_native_success(nlohmann::json{
            {"mimeType", "image/png"},
            {"width", png.width},
            {"height", png.height},
            {"frame", frame},
            {"sha256", sha256},
            {"byteLength", png.bytes.size()},
        }.dump(), context);
        result.binary = std::move(png.bytes);
        return result;
    } catch (const nlohmann::json::exception&) {
        return create_native_failure(
            "invalid_argument", "Preview request JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    } catch (const std::exception& exception) {
        return create_native_failure("preview_encode_failed", exception.what(), context, true);
    }
}

}  // namespace aviutl2_mcp

#include "aviutl2_mcp/native_capabilities_request_handler.h"

#include "aviutl2_mcp/native_operation_result.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include <nlohmann/json.hpp>

#include <array>
#include <stdexcept>
#include <string_view>
#include <utility>

namespace aviutl2_mcp {
namespace {

constexpr std::array ALWAYS_AVAILABLE_OPERATIONS{
    std::string_view("aviutl_get_status"),
    std::string_view("aviutl_get_capabilities"),
    std::string_view("aviutl_get_logs"),
    std::string_view("aviutl_diagnose"),
};

constexpr std::array SDK_OPERATIONS{
    std::string_view("aviutl_list_effects"),
    std::string_view("aviutl_list_effect_items"),
};

constexpr std::array PROJECT_OPERATIONS{
    std::string_view("aviutl_get_project"),
    std::string_view("aviutl_get_timeline"),
    std::string_view("aviutl_find_objects"),
    std::string_view("aviutl_get_object"),
    std::string_view("aviutl_set_cursor"),
    std::string_view("aviutl_render_preview"),
};

constexpr std::array EDIT_OPERATIONS{
    std::string_view("aviutl_create_object"),
    std::string_view("aviutl_create_media_object"),
    std::string_view("aviutl_create_alias_object"),
    std::string_view("aviutl_move_object"),
    std::string_view("aviutl_delete_object"),
    std::string_view("aviutl_set_object_name"),
    std::string_view("aviutl_set_effect_item"),
    std::string_view("aviutl_set_effect_state"),
    std::string_view("aviutl_set_layer"),
    std::string_view("aviutl_execute_batch"),
};

constexpr std::array PSD_TOOLKIT_OPERATIONS{
    std::string_view("aviutl_psd_setup"),
    std::string_view("aviutl_psd_set_character"),
    std::string_view("aviutl_psd_set_layer_state"),
};

void add_operations(
    nlohmann::json& operations,
    const auto& names,
    const bool is_available,
    const char* reason) {
    for (const std::string_view name : names) {
        operations.push_back({
            {"name", name},
            {"available", is_available},
            {"reason", is_available ? nlohmann::json(nullptr) : nlohmann::json(reason)},
            {"constraints", nlohmann::json::array()},
        });
    }
}

[[nodiscard]] const char* get_sdk_reason(const sdk_status_snapshot& status) noexcept {
    return !status.is_sdk_ready ? "sdk_not_available"
        : status.has_query_error ? "sdk_query_failed"
        : nullptr;
}

[[nodiscard]] const char* get_project_reason(const sdk_status_snapshot& status) noexcept {
    const char* sdk_reason = get_sdk_reason(status);
    if (sdk_reason != nullptr) {
        return sdk_reason;
    }
    const bool has_project = status.project_state == sdk_project_state::saved
        || status.project_state == sdk_project_state::unsaved;
    return has_project ? nullptr : "project_not_open";
}

[[nodiscard]] const char* get_edit_reason(const sdk_status_snapshot& status) noexcept {
    const char* project_reason = get_project_reason(status);
    return project_reason != nullptr ? project_reason
        : status.edit_state == sdk_edit_state::edit ? nullptr
        : "edit_not_available";
}

}  // namespace

native_capabilities_request_handler::native_capabilities_request_handler(
    std::string host_version,
    sdk_read_facade& sdk)
    : host_version_(std::move(host_version)),
      sdk_(sdk) {}

std::string native_capabilities_request_handler::operation() const {
    return "capabilities.get";
}

bool native_capabilities_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_capabilities_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Capabilities query parameters must be an object");
        }
        const sdk_status_snapshot status = sdk_.query_status();
        const char* sdk_reason = get_sdk_reason(status);
        const char* project_reason = get_project_reason(status);
        const char* edit_reason = get_edit_reason(status);
        nlohmann::json operations = nlohmann::json::array();
        add_operations(operations, ALWAYS_AVAILABLE_OPERATIONS, true, nullptr);
        add_operations(operations, SDK_OPERATIONS, sdk_reason == nullptr, sdk_reason);
        add_operations(operations, PROJECT_OPERATIONS, project_reason == nullptr, project_reason);
        add_operations(operations, EDIT_OPERATIONS, edit_reason == nullptr, edit_reason);
        add_operations(operations, PSD_TOOLKIT_OPERATIONS, false, "psdtoolkit_not_available");
        add_operations(
            operations,
            std::array{std::string_view("aviutl_psd_validate")},
            false,
            "psdtoolkit_not_available");
        add_operations(
            operations,
            std::array{std::string_view("aviutl_psd_create")},
            false,
            "gcmzdrops_not_available");
        add_operations(
            operations,
            std::array{std::string_view("aviutl_psd_create_voice")},
            false,
            "psdtoolkit_not_available");

        const nlohmann::json result = {
            {"operations", std::move(operations)},
            {"versions", {
                {"server", "0.1.0"},
                {"schema", "1.0.0"},
                {"protocol", "1.0"},
                {"bridge", "0.1.0"},
                {"aviutl", host_version_},
                {"sdk", status.is_sdk_ready ? nlohmann::json("2003300") : nlohmann::json(nullptr)},
                {"psdToolKit", nullptr},
                {"gcmzDrops", nullptr},
            }},
            {"limits", {
                {"ipcJsonBytes", 8 * 1024 * 1024},
                {"ipcBinaryBytes", 16 * 1024 * 1024},
                {"ipcInFlight", 8},
                {"bridgeConnections", 1},
                {"globalQueue", 64},
                {"mutationTombstones", 4096},
                {"mutationResponseCache", 256},
                {"timelineDefaultItems", 100},
                {"timelineMaxItems", 1000},
                {"batchOperations", 100},
                {"aliasUtf8Bytes", 1024 * 1024},
                {"toolStringUtf8Bytes", 64 * 1024},
                {"logDefaultLines", 100},
                {"logMaxLines", 2000},
                {"previewMaxWidth", 4096},
                {"previewMaxHeight", 4096},
                {"pngBytes", 16 * 1024 * 1024},
                {"statusTimeoutMs", 2000},
                {"editTimeoutMs", 10'000},
                {"previewTimeoutMs", 30'000},
                {"pagingCursorTtlSeconds", 300},
            }},
        };
        return create_native_success(result.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Capabilities query JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    }
}

}  // namespace aviutl2_mcp

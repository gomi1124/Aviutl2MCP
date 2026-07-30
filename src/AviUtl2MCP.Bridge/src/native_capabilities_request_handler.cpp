#include "aviutl2_mcp/native_capabilities_request_handler.h"

#include "aviutl2_mcp/gcmz_adapter.h"
#include "aviutl2_mcp/native_environment_probe.h"
#include "aviutl2_mcp/named_pipe_server.h"
#include "aviutl2_mcp/native_operation_result.h"
#include "aviutl2_mcp/psd_contract.h"
#include "aviutl2_mcp/sdk_read_facade.h"
#include "aviutl2_mcp/version.h"

#include <Windows.h>

#include <nlohmann/json.hpp>

#include <array>
#include <cctype>
#include <filesystem>
#include <limits>
#include <optional>
#include <stdexcept>
#include <string>
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
    std::string_view("aviutl_create_object_section"),
    std::string_view("aviutl_delete_object_section"),
    std::string_view("aviutl_move_object_section"),
    std::string_view("aviutl_set_effect_item"),
    std::string_view("aviutl_set_effect_state"),
    std::string_view("aviutl_set_layer"),
    std::string_view("aviutl_execute_batch"),
};

constexpr std::array SAVE_OPERATIONS{
    std::string_view("aviutl_save_project"),
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

[[nodiscard]] const char* get_save_reason(const sdk_status_snapshot& status) noexcept {
    const char* edit_reason = get_edit_reason(status);
    return edit_reason != nullptr ? edit_reason
        : status.project_state == sdk_project_state::saved ? nullptr
        : "project_path_required";
}

[[nodiscard]] const char* choose_reason(
    const char* core_reason,
    const bool has_profile,
    const bool needs_gcmz,
    const bool has_gcmz,
    const bool needs_voice_route,
    const bool has_voice_route) noexcept {
    if (core_reason != nullptr) {
        return core_reason;
    }
    if (needs_gcmz && !has_gcmz) {
        return "gcmzdrops_not_available";
    }
    if (!has_profile) {
        return "psdtoolkit_not_available";
    }
    if (needs_voice_route && !has_voice_route) {
        return "psd_voice_route_unavailable";
    }
    return nullptr;
}

}  // namespace

native_capabilities_request_handler::native_capabilities_request_handler(
    std::string host_version,
    sdk_read_facade& sdk,
    std::string operation)
    : host_version_(std::move(host_version)),
      sdk_(sdk),
      operation_(std::move(operation)) {
    if (operation_.empty()) {
        throw std::invalid_argument("Capabilities operation name must not be empty");
    }
}

std::string native_capabilities_request_handler::operation() const {
    return operation_;
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
        const char* save_reason = get_save_reason(status);
        const native_environment_probe psd = probe_native_environment(sdk_, status);
        const bool has_profile = psd.psd_profile.is_match;
        const bool has_gcmz = psd.gcmz.ok;
        const bool has_voice_route = psd.psdtoolkit_config.ok
            && psd.psdtoolkit_config.voice_route != psd_voice_route::unavailable
            && psd.has_psd_alias;
        nlohmann::json operations = nlohmann::json::array();
        add_operations(operations, ALWAYS_AVAILABLE_OPERATIONS, true, nullptr);
        add_operations(operations, SDK_OPERATIONS, sdk_reason == nullptr, sdk_reason);
        add_operations(operations, PROJECT_OPERATIONS, project_reason == nullptr, project_reason);
        add_operations(operations, EDIT_OPERATIONS, edit_reason == nullptr, edit_reason);
        add_operations(operations, SAVE_OPERATIONS, save_reason == nullptr, save_reason);
        const char* psd_edit_reason = choose_reason(
            edit_reason, has_profile, false, has_gcmz, false, has_voice_route);
        add_operations(
            operations,
            PSD_TOOLKIT_OPERATIONS,
            psd_edit_reason == nullptr,
            psd_edit_reason);
        const char* validate_reason = choose_reason(
            project_reason, has_profile, false, has_gcmz, false, has_voice_route);
        add_operations(
            operations,
            std::array{std::string_view("aviutl_psd_validate")},
            validate_reason == nullptr,
            validate_reason);
        const char* create_reason = choose_reason(
            edit_reason, has_profile, true, has_gcmz, false, has_voice_route);
        add_operations(
            operations,
            std::array{std::string_view("aviutl_psd_create")},
            create_reason == nullptr,
            create_reason);
        const char* voice_reason = choose_reason(
            edit_reason, has_profile, true, has_gcmz, true, has_voice_route);
        add_operations(
            operations,
            std::array{std::string_view("aviutl_psd_create_voice")},
            voice_reason == nullptr,
            voice_reason);

        const nlohmann::json result = {
            {"operations", std::move(operations)},
            {"versions", {
                {"server", PRODUCT_VERSION},
                {"schema", "1.0.0"},
                {"protocol", "1.0"},
                {"bridge", PRODUCT_VERSION},
                {"aviutl", host_version_},
                {"sdk", status.is_sdk_ready
                    ? nlohmann::json(MINIMUM_AVIUTL_VERSION_TEXT)
                    : nlohmann::json(nullptr)},
                {"psdToolKit", psd.psdtoolkit_version.has_value()
                    ? nlohmann::json(*psd.psdtoolkit_version)
                    : nlohmann::json(nullptr)},
                {"gcmzDrops", psd.gcmz.ok
                    ? nlohmann::json(std::to_string(psd.gcmz.gcmz_version))
                    : nlohmann::json(nullptr)},
            }},
            {"limits", {
                {"ipcJsonBytes", 8 * 1024 * 1024},
                {"ipcBinaryBytes", 16 * 1024 * 1024},
                {"ipcInFlight", 8},
                {"bridgeConnections", MAXIMUM_BRIDGE_CONNECTIONS},
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

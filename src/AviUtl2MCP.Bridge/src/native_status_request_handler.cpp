#include "aviutl2_mcp/native_status_request_handler.h"

#include "aviutl2_mcp/native_environment_probe.h"
#include "aviutl2_mcp/native_operation_result.h"
#include "aviutl2_mcp/sdk_read_facade.h"
#include "aviutl2_mcp/version.h"

#include <nlohmann/json.hpp>

#include <stdexcept>
#include <utility>

namespace aviutl2_mcp {
namespace {

[[nodiscard]] const char* project_state_name(const sdk_project_state state) noexcept {
    switch (state) {
        case sdk_project_state::not_open:
            return "notOpen";
        case sdk_project_state::unsaved:
            return "unsaved";
        case sdk_project_state::saved:
            return "saved";
        case sdk_project_state::unknown:
        default:
            return "unknown";
    }
}

[[nodiscard]] const char* edit_state_name(const sdk_edit_state state) noexcept {
    switch (state) {
        case sdk_edit_state::edit:
            return "edit";
        case sdk_edit_state::play:
            return "play";
        case sdk_edit_state::save:
            return "save";
        case sdk_edit_state::unknown:
        default:
            return "unknown";
    }
}

[[nodiscard]] nlohmann::json create_component(
    const char* name,
    const char* status,
    const nlohmann::json& version) {
    return {
        {"name", name},
        {"status", status},
        {"version", version},
    };
}

}  // namespace

native_status_request_handler::native_status_request_handler(
    bridge_identity identity,
    std::string host_version,
    sdk_read_facade& sdk)
    : identity_(std::move(identity)),
      host_version_(std::move(host_version)),
      sdk_(sdk) {}

std::string native_status_request_handler::operation() const {
    return "status.get";
}

bool native_status_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_status_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Status query parameters must be an object");
        }
        const sdk_status_snapshot status = sdk_.query_status();
        const native_environment_probe environment = probe_native_environment(sdk_, status);
        const char* sdk_component_status = !status.is_sdk_ready
            ? "unavailable"
            : status.has_query_error ? "faulted" : "ready";
        nlohmann::json components = nlohmann::json::array({
            create_component("bridge", "ready", PRODUCT_VERSION),
            create_component("aviutl", "ready", host_version_),
            create_component(
                "sdk",
                sdk_component_status,
                status.is_sdk_ready
                    ? nlohmann::json(MINIMUM_AVIUTL_VERSION_TEXT)
                    : nlohmann::json(nullptr)),
        });
        for (const native_component_probe& component : describe_native_environment(environment)) {
            components.push_back(create_component(
                component.name.c_str(),
                component.status.c_str(),
                component.version.has_value()
                    ? nlohmann::json(*component.version)
                    : nlohmann::json(nullptr)));
        }
        if (status.has_query_error) {
            components.push_back(create_component("sdk.query", "faulted", nullptr));
        }

        const nlohmann::json result = {
            {"connectionState", "ready"},
            {"components", std::move(components)},
            {"projectState", project_state_name(status.project_state)},
            {"editState", edit_state_name(status.edit_state)},
            {"selectedInstance", identity_.instance_id},
            {"instances", nlohmann::json::array({{
                {"instanceId", identity_.instance_id},
                {"processId", identity_.process_id},
                {"bridgeVersion", PRODUCT_VERSION},
                {"state", "ready"},
            }})},
        };
        return create_native_success(result.dump(), context);
    } catch (const nlohmann::json::exception&) {
        return create_native_failure("invalid_argument", "Status query JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_native_failure("invalid_argument", exception.what(), context);
    }
}

}  // namespace aviutl2_mcp

#include "aviutl2_mcp/bridge_version.h"
#include "aviutl2_mcp/bridge_runtime.h"
#include "aviutl2_mcp/native_ring_logger.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include <Windows.h>

#include "logger2.h"
#include "plugin2.h"

#include <cstdint>

#if defined(_WIN32)
#define AVIUTL2_MCP_EXPORT extern "C" __declspec(dllexport)
#else
#define AVIUTL2_MCP_EXPORT extern "C"
#endif

AVIUTL2_MCP_EXPORT std::uint32_t AviUtl2McpBridgeAbiVersion() noexcept {
    return aviutl2_mcp::get_bridge_abi_version();
}

namespace {

COMMON_PLUGIN_TABLE PLUGIN_TABLE{
    L"AviUtl2 MCP Bridge",
    L"AviUtl2 MCP Bridge version 0.1.0",
};

}  // namespace

AVIUTL2_MCP_EXPORT DWORD RequiredVersion() noexcept {
    return 2003300U;
}

AVIUTL2_MCP_EXPORT void InitializeLogger(LOG_HANDLE* logger) noexcept {
    aviutl2_mcp::get_native_logger().attach(logger);
}

AVIUTL2_MCP_EXPORT bool InitializePlugin(const DWORD version) noexcept {
    return aviutl2_mcp::get_bridge_runtime().start(version);
}

AVIUTL2_MCP_EXPORT void RegisterPlugin(HOST_APP_TABLE* host) noexcept {
    if (!aviutl2_mcp::get_sdk_read_facade().register_host(host)) {
        aviutl2_mcp::get_native_logger().write(
            aviutl2_mcp::native_log_level::error,
            "sdk",
            "sdk.registration_failed",
            "AviUtl2 SDK host registration failed",
            aviutl2_mcp::native_log_context{.result_code = "sdk_not_available"});
        return;
    }
    aviutl2_mcp::get_native_logger().write(
        aviutl2_mcp::native_log_level::information,
        "sdk",
        "sdk.registered",
        "AviUtl2 SDK read facade registered",
        aviutl2_mcp::native_log_context{.result_code = "ok"});
}

AVIUTL2_MCP_EXPORT void UninitializePlugin() noexcept {
    aviutl2_mcp::get_bridge_runtime().stop();
    aviutl2_mcp::get_sdk_read_facade().detach();
}

AVIUTL2_MCP_EXPORT COMMON_PLUGIN_TABLE* GetCommonPluginTable() noexcept {
    return &PLUGIN_TABLE;
}

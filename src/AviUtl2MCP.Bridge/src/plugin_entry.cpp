#include "aviutl2_mcp/bridge_version.h"
#include "aviutl2_mcp/bridge_runtime.h"
#include "aviutl2_mcp/native_ring_logger.h"

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

AVIUTL2_MCP_EXPORT void UninitializePlugin() noexcept {
    aviutl2_mcp::get_bridge_runtime().stop();
}

AVIUTL2_MCP_EXPORT COMMON_PLUGIN_TABLE* GetCommonPluginTable() noexcept {
    return &PLUGIN_TABLE;
}

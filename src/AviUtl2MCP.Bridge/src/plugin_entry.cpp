#include "aviutl2_mcp/bridge_version.h"

#include <cstdint>

#if defined(_WIN32)
#define AVIUTL2_MCP_EXPORT extern "C" __declspec(dllexport)
#else
#define AVIUTL2_MCP_EXPORT extern "C"
#endif

AVIUTL2_MCP_EXPORT std::uint32_t AviUtl2McpBridgeAbiVersion() noexcept {
    return aviutl2_mcp::get_bridge_abi_version();
}

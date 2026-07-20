#include "aviutl2_mcp/bridge_version.h"

namespace aviutl2_mcp {

std::uint32_t get_bridge_abi_version() noexcept {
    return BRIDGE_ABI_VERSION;
}

} // namespace aviutl2_mcp

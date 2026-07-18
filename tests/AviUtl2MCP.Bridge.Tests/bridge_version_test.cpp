#include "aviutl2_mcp/bridge_version.h"

int main() {
    return aviutl2_mcp::get_bridge_abi_version() == aviutl2_mcp::BRIDGE_ABI_VERSION ? 0 : 1;
}

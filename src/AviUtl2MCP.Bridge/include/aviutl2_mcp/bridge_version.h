#pragma once

#include <cstdint>

namespace aviutl2_mcp {

inline constexpr std::uint32_t BRIDGE_ABI_VERSION = 1U;

[[nodiscard]] std::uint32_t get_bridge_abi_version() noexcept;

} // namespace aviutl2_mcp

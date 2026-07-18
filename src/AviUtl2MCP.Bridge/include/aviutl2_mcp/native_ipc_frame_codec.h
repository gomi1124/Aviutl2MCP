#pragma once

#include "aviutl2_mcp/ipc_header.h"

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <vector>

namespace aviutl2_mcp {

class byte_transport {
public:
    virtual ~byte_transport() = default;
    [[nodiscard]] virtual std::size_t read_some(std::span<std::uint8_t> buffer) = 0;
    [[nodiscard]] virtual std::size_t write_some(std::span<const std::uint8_t> buffer) = 0;
};

struct ipc_frame final {
    frame_header header;
    std::vector<std::uint8_t> json;
    std::vector<std::uint8_t> binary;
    std::string payload_hash;
};

[[nodiscard]] ipc_frame read_frame(byte_transport& transport);
void write_frame(byte_transport& transport, const ipc_frame& frame);
[[nodiscard]] std::string calculate_payload_hash(
    const frame_header& header,
    std::span<const std::uint8_t> json,
    std::span<const std::uint8_t> binary);
[[nodiscard]] std::string calculate_sha256(std::span<const std::uint8_t> bytes);
void validate_utf8(std::span<const std::uint8_t> bytes);

}  // namespace aviutl2_mcp

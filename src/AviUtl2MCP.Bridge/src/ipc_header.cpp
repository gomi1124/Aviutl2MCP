#include "aviutl2_mcp/ipc_header.h"

#include <algorithm>
#include <stdexcept>

namespace aviutl2_mcp {
namespace {

constexpr std::uint8_t KNOWN_FLAGS = 0x07U;

void validate_header(const frame_header& header) {
    const auto kind = static_cast<std::uint8_t>(header.kind);
    if (kind < static_cast<std::uint8_t>(message_kind::client_hello)
        || kind > static_cast<std::uint8_t>(message_kind::close)) {
        throw std::invalid_argument("message kind is unknown");
    }

    const auto flags = static_cast<std::uint8_t>(header.flags);
    if ((flags & static_cast<std::uint8_t>(~KNOWN_FLAGS)) != 0U) {
        throw std::invalid_argument("frame flags contain unknown bits");
    }

    const bool is_zero_request = std::ranges::all_of(header.request_id, [](const std::uint8_t value) {
        return value == 0U;
    });
    if (is_zero_request && header.kind != message_kind::close) {
        throw std::invalid_argument("only close may use a zero request ID");
    }

    if (header.json_length > IPC_MAX_JSON_BYTES || header.binary_length > IPC_MAX_BINARY_BYTES) {
        throw std::length_error("frame payload exceeds the protocol limit");
    }

    const bool has_binary_flag = (flags & static_cast<std::uint8_t>(frame_flags::has_binary)) != 0U;
    if (has_binary_flag != (header.binary_length > 0U)) {
        throw std::invalid_argument("binary length and flag do not match");
    }
}

void write_uint32_le(
    std::array<std::uint8_t, IPC_HEADER_BYTES>& bytes,
    const std::size_t offset,
    const std::uint32_t value) noexcept {
    for (std::size_t index = 0; index < sizeof(value); ++index) {
        bytes[offset + index] = static_cast<std::uint8_t>(value >> (index * 8U));
    }
}

void write_uint64_le(
    std::array<std::uint8_t, IPC_HEADER_BYTES>& bytes,
    const std::size_t offset,
    const std::uint64_t value) noexcept {
    for (std::size_t index = 0; index < sizeof(value); ++index) {
        bytes[offset + index] = static_cast<std::uint8_t>(value >> (index * 8U));
    }
}

}  // namespace

std::array<std::uint8_t, IPC_HEADER_BYTES> encode_header(const frame_header& header) {
    validate_header(header);

    std::array<std::uint8_t, IPC_HEADER_BYTES> bytes{};
    bytes[0] = 'A';
    bytes[1] = '2';
    bytes[2] = 'M';
    bytes[3] = 'P';
    bytes[4] = static_cast<std::uint8_t>(IPC_HEADER_BYTES);
    bytes[6] = 1U;
    bytes[7] = 0U;
    bytes[8] = static_cast<std::uint8_t>(header.kind);
    bytes[9] = static_cast<std::uint8_t>(header.flags);
    std::ranges::copy(header.request_id, bytes.begin() + 12);
    write_uint32_le(bytes, 28, header.json_length);
    write_uint64_le(bytes, 32, header.binary_length);
    return bytes;
}

}  // namespace aviutl2_mcp

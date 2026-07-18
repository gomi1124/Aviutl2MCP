#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace aviutl2_mcp {

inline constexpr std::size_t IPC_HEADER_BYTES = 40;
inline constexpr std::uint32_t IPC_MAX_JSON_BYTES = 8U * 1024U * 1024U;
inline constexpr std::uint64_t IPC_MAX_BINARY_BYTES = 16ULL * 1024ULL * 1024ULL;

enum class message_kind : std::uint8_t {
    client_hello = 1,
    server_hello = 2,
    request = 3,
    response = 4,
    cancel = 5,
    cancel_ack = 6,
    ping = 7,
    pong = 8,
    close = 9,
};

enum class frame_flags : std::uint8_t {
    none = 0,
    has_binary = 1U << 0U,
    error_response = 1U << 1U,
    partial_response = 1U << 2U,
};

struct frame_header final {
    message_kind kind;
    frame_flags flags;
    std::array<std::uint8_t, 16> request_id;
    std::uint32_t json_length;
    std::uint64_t binary_length;
};

[[nodiscard]] std::array<std::uint8_t, IPC_HEADER_BYTES> encode_header(const frame_header& header);
[[nodiscard]] frame_header decode_header(std::span<const std::uint8_t, IPC_HEADER_BYTES> bytes);

}  // namespace aviutl2_mcp

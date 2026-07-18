#pragma once

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/native_ipc_frame_codec.h"

#include <cstdint>
#include <span>
#include <string>

namespace aviutl2_mcp {

struct protocol_range final {
    std::uint16_t min_major;
    std::uint16_t min_minor;
    std::uint16_t max_major;
    std::uint16_t max_minor;
};

struct handshake_limits final {
    std::uint32_t json_bytes;
    std::uint32_t binary_bytes;
    std::uint32_t in_flight;
};

struct client_hello final {
    std::string client_instance_id;
    std::uint32_t client_process_id;
    std::string target_instance_id;
    protocol_range protocol;
    std::string client_version;
    handshake_limits limits;
};

struct handshake_result final {
    bool accepted;
    handshake_limits limits;
    std::string error_code;
    std::string error_message;
};

[[nodiscard]] client_hello parse_client_hello(std::span<const std::uint8_t> json_bytes);

class handshake_handler final {
public:
    handshake_handler(bridge_identity identity, std::string host_version);

    [[nodiscard]] handshake_result negotiate(
        const client_hello& hello,
        std::uint32_t actual_client_process_id) const;
    [[nodiscard]] std::string create_server_hello_json(
        const client_hello& hello,
        const handshake_result& result) const;

private:
    bridge_identity identity_;
    std::string host_version_;
};

}  // namespace aviutl2_mcp

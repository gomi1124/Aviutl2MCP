#include "aviutl2_mcp/handshake.h"

#include "aviutl2_mcp/ipc_header.h"
#include "aviutl2_mcp/version.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <limits>
#include <stdexcept>
#include <utility>

namespace aviutl2_mcp {
namespace {

constexpr handshake_limits SERVER_LIMITS{
    .json_bytes = IPC_MAX_JSON_BYTES,
    .binary_bytes = static_cast<std::uint32_t>(IPC_MAX_BINARY_BYTES),
    .in_flight = 8U,
};

[[nodiscard]] std::uint16_t get_uint16(const nlohmann::json& value, const char* name) {
    const auto number = value.at(name).get<std::uint32_t>();
    if (number > (std::numeric_limits<std::uint16_t>::max)()) {
        throw std::out_of_range("handshake protocol version is outside uint16 range");
    }
    return static_cast<std::uint16_t>(number);
}

[[nodiscard]] protocol_range parse_protocol_range(const nlohmann::json& value) {
    if (!value.is_object()) {
        throw std::invalid_argument("client protocol range must be an object");
    }
    return protocol_range{
        .min_major = get_uint16(value, "minMajor"),
        .min_minor = get_uint16(value, "minMinor"),
        .max_major = get_uint16(value, "maxMajor"),
        .max_minor = get_uint16(value, "maxMinor"),
    };
}

[[nodiscard]] handshake_limits parse_limits(const nlohmann::json& value) {
    if (!value.is_object()) {
        throw std::invalid_argument("client limits must be an object");
    }
    return handshake_limits{
        .json_bytes = value.at("jsonBytes").get<std::uint32_t>(),
        .binary_bytes = value.at("binaryBytes").get<std::uint32_t>(),
        .in_flight = value.at("inFlight").get<std::uint32_t>(),
    };
}

[[nodiscard]] bool supports_v1_0(const protocol_range& range) noexcept {
    if (range.min_major > range.max_major) {
        return false;
    }
    if (range.min_major == range.max_major && range.min_minor > range.max_minor) {
        return false;
    }
    if (range.min_major > 1U || range.max_major < 1U) {
        return false;
    }
    if (range.min_major == 1U && range.min_minor > 0U) {
        return false;
    }
    return true;
}

[[nodiscard]] nlohmann::json to_json(const protocol_range& range) {
    return nlohmann::json{
        {"minMajor", range.min_major},
        {"minMinor", range.min_minor},
        {"maxMajor", range.max_major},
        {"maxMinor", range.max_minor},
    };
}

}  // namespace

client_hello parse_client_hello(const std::span<const std::uint8_t> json_bytes) {
    if (json_bytes.empty()) {
        throw std::invalid_argument("ClientHello JSON must not be empty");
    }
    const nlohmann::json document = nlohmann::json::parse(json_bytes.begin(), json_bytes.end());
    if (!document.is_object()) {
        throw std::invalid_argument("ClientHello must be a JSON object");
    }
    client_hello hello{
        .client_instance_id = document.at("clientInstanceId").get<std::string>(),
        .client_process_id = document.at("clientProcessId").get<std::uint32_t>(),
        .target_instance_id = document.at("targetInstanceId").get<std::string>(),
        .protocol = parse_protocol_range(document.at("protocol")),
        .client_version = document.at("clientVersion").get<std::string>(),
        .limits = parse_limits(document.at("limits")),
    };
    if (!is_nonzero_uuid(hello.client_instance_id)
        || !is_nonzero_uuid(hello.target_instance_id)
        || hello.client_process_id == 0U
        || hello.client_version.empty()
        || hello.client_version.size() > 64U
        || hello.limits.json_bytes == 0U
        || hello.limits.binary_bytes == 0U
        || hello.limits.in_flight == 0U) {
        throw std::invalid_argument("ClientHello fields were outside the supported constraints");
    }
    return hello;
}

handshake_handler::handshake_handler(bridge_identity identity, std::string host_version)
    : identity_(std::move(identity)),
      host_version_(std::move(host_version)) {}

handshake_result handshake_handler::negotiate(
    const client_hello& hello,
    const std::uint32_t actual_client_process_id) const {
    const handshake_limits negotiated{
        .json_bytes = (std::min)(hello.limits.json_bytes, SERVER_LIMITS.json_bytes),
        .binary_bytes = (std::min)(hello.limits.binary_bytes, SERVER_LIMITS.binary_bytes),
        .in_flight = (std::min)(hello.limits.in_flight, SERVER_LIMITS.in_flight),
    };
    if (hello.client_process_id != actual_client_process_id) {
        return {false, negotiated, "client_pid_mismatch", "ClientHello PID did not match the named pipe client PID"};
    }
    if (!uuid_equals(hello.target_instance_id, identity_.instance_id)) {
        return {false, negotiated, "target_instance_mismatch", "ClientHello target instance did not match this bridge"};
    }
    if (!supports_v1_0(hello.protocol)) {
        return {false, negotiated, "protocol_incompatible", "Client protocol range does not include protocol 1.0"};
    }
    return {true, negotiated, {}, {}};
}

std::string handshake_handler::create_server_hello_json(
    const client_hello& hello,
    const handshake_result& result) const {
    if (!result.accepted) {
        return nlohmann::json{
            {"accepted", false},
            {"error", {{"code", result.error_code}, {"message", result.error_message}}},
            {"clientRange", to_json(hello.protocol)},
            {"serverRange", {{"minMajor", 1U}, {"minMinor", 0U}, {"maxMajor", 1U}, {"maxMinor", 0U}}},
        }.dump();
    }
    return nlohmann::json{
        {"accepted", true},
        {"instanceId", identity_.instance_id},
        {"serverEpoch", identity_.server_epoch},
        {"aviutlProcessId", identity_.process_id},
        {"aviutlProcessCreationTime", identity_.process_creation_time},
        {"protocol", {{"major", 1U}, {"minor", 0U}}},
        {"versions", {
            {"bridge", PRODUCT_VERSION},
            {"sdk", MINIMUM_AVIUTL_VERSION_TEXT},
            {"aviutl", host_version_}}},
        {"limits", {
            {"jsonBytes", result.limits.json_bytes},
            {"binaryBytes", result.limits.binary_bytes},
            {"inFlight", result.limits.in_flight}}},
        {"capabilities", nlohmann::json::object()},
    }.dump();
}

}  // namespace aviutl2_mcp

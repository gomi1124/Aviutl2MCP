#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/bridge_runtime.h"
#include "aviutl2_mcp/bridge_version.h"
#include "aviutl2_mcp/handshake.h"
#include "aviutl2_mcp/instance_descriptor.h"
#include "aviutl2_mcp/ipc_header.h"
#include "aviutl2_mcp/named_pipe_server.h"
#include "aviutl2_mcp/native_ipc_frame_codec.h"
#include "aviutl2_mcp/pipe_security.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <span>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace {

using aviutl2_mcp::byte_transport;

void require(const bool condition, const char* message) {
    if (!condition) {
        throw std::runtime_error(message);
    }
}

template <typename Function>
void require_throws(Function&& function, const char* message) {
    try {
        std::forward<Function>(function)();
    } catch (const std::exception&) {
        return;
    }
    throw std::runtime_error(message);
}

class memory_transport final : public byte_transport {
public:
    explicit memory_transport(std::vector<std::uint8_t> input, const std::size_t fragment_bytes = 1U)
        : input_(std::move(input)),
          fragment_bytes_(fragment_bytes) {}

    [[nodiscard]] std::size_t read_some(const std::span<std::uint8_t> buffer) override {
        const std::size_t available = input_.size() - read_offset_;
        const std::size_t count = (std::min)({available, buffer.size(), fragment_bytes_});
        std::ranges::copy_n(input_.begin() + static_cast<std::ptrdiff_t>(read_offset_), count, buffer.begin());
        read_offset_ += count;
        return count;
    }

    [[nodiscard]] std::size_t write_some(const std::span<const std::uint8_t> buffer) override {
        const std::size_t count = (std::min)(buffer.size(), fragment_bytes_);
        output_.insert(output_.end(), buffer.begin(), buffer.begin() + static_cast<std::ptrdiff_t>(count));
        return count;
    }

    [[nodiscard]] const std::vector<std::uint8_t>& output() const noexcept {
        return output_;
    }

private:
    std::vector<std::uint8_t> input_;
    std::vector<std::uint8_t> output_;
    std::size_t read_offset_ = 0U;
    std::size_t fragment_bytes_;
};

class handle_transport final : public byte_transport {
public:
    explicit handle_transport(const HANDLE handle)
        : handle_(handle) {}

    [[nodiscard]] std::size_t read_some(const std::span<std::uint8_t> buffer) override {
        DWORD read = 0U;
        if (ReadFile(handle_, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr) == FALSE) {
            throw std::runtime_error("test pipe read failed");
        }
        return read;
    }

    [[nodiscard]] std::size_t write_some(const std::span<const std::uint8_t> buffer) override {
        DWORD written = 0U;
        if (WriteFile(handle_, buffer.data(), static_cast<DWORD>(buffer.size()), &written, nullptr) == FALSE) {
            throw std::runtime_error("test pipe write failed");
        }
        return written;
    }

private:
    HANDLE handle_;
};

[[nodiscard]] aviutl2_mcp::frame_header create_header(
    const aviutl2_mcp::message_kind kind,
    const aviutl2_mcp::frame_flags flags,
    const std::uint32_t json_bytes,
    const std::uint64_t binary_bytes) {
    return aviutl2_mcp::frame_header{
        .kind = kind,
        .flags = flags,
        .request_id = {0x00U, 0x11U, 0x22U, 0x33U, 0x44U, 0x55U, 0x66U, 0x77U,
                       0x88U, 0x99U, 0xaaU, 0xbbU, 0xccU, 0xddU, 0xeeU, 0xffU},
        .json_length = json_bytes,
        .binary_length = binary_bytes,
    };
}

[[nodiscard]] std::filesystem::path create_test_directory(const std::string& instance_id) {
    wchar_t temporary_path[MAX_PATH]{};
    require(GetTempPathW(MAX_PATH, temporary_path) != 0U, "GetTempPathW failed");
    return std::filesystem::path(temporary_path) / L"AviUtl2MCP.Tests" / std::filesystem::path(instance_id);
}

class directory_cleanup final {
public:
    explicit directory_cleanup(std::filesystem::path path)
        : path_(std::move(path)) {}

    ~directory_cleanup() {
        std::error_code error;
        std::filesystem::remove_all(path_, error);
    }

private:
    std::filesystem::path path_;
};

void test_bridge_version() {
    require(
        aviutl2_mcp::get_bridge_abi_version() == aviutl2_mcp::BRIDGE_ABI_VERSION,
        "bridge ABI version mismatch");
}

void test_header_golden_vector() {
    const auto header = create_header(
        aviutl2_mcp::message_kind::response,
        aviutl2_mcp::frame_flags::has_binary,
        0x00010203U,
        0x0000000000040506ULL);
    const auto bytes = aviutl2_mcp::encode_header(header);
    const std::array<std::uint8_t, aviutl2_mcp::IPC_HEADER_BYTES> expected{
        0x41U, 0x32U, 0x4dU, 0x50U, 0x28U, 0x00U, 0x01U, 0x00U,
        0x04U, 0x01U, 0x00U, 0x00U, 0x00U, 0x11U, 0x22U, 0x33U,
        0x44U, 0x55U, 0x66U, 0x77U, 0x88U, 0x99U, 0xaaU, 0xbbU,
        0xccU, 0xddU, 0xeeU, 0xffU, 0x03U, 0x02U, 0x01U, 0x00U,
        0x06U, 0x05U, 0x04U, 0x00U, 0x00U, 0x00U, 0x00U, 0x00U,
    };
    require(bytes == expected, "native encoded header did not match the C# golden vector");
    require(aviutl2_mcp::decode_header(bytes).binary_length == header.binary_length, "header round trip failed");

    auto invalid = bytes;
    invalid[10] = 1U;
    require_throws([&invalid] { (void)aviutl2_mcp::decode_header(invalid); }, "reserved bytes were accepted");
}

void test_frame_fragmentation_and_hash() {
    const std::string json_text = R"({"ok":true})";
    const std::vector<std::uint8_t> json(json_text.begin(), json_text.end());
    const std::vector<std::uint8_t> binary{0U, 1U, 2U, 255U};
    aviutl2_mcp::ipc_frame original{
        .header = create_header(
            aviutl2_mcp::message_kind::response,
            aviutl2_mcp::frame_flags::has_binary,
            static_cast<std::uint32_t>(json.size()),
            binary.size()),
        .json = json,
        .binary = binary,
        .payload_hash = {},
    };
    original.payload_hash = aviutl2_mcp::calculate_payload_hash(original.header, json, binary);
    require(
        original.payload_hash == "5c9fa6681c50bcb59d11129d25e96c3e35c043e6e99ba08cd201292123093cd2",
        "payload hash did not match the independent C# algorithm fixture");

    memory_transport writer({}, 1U);
    aviutl2_mcp::write_frame(writer, original);
    memory_transport reader(writer.output(), 1U);
    const aviutl2_mcp::ipc_frame decoded = aviutl2_mcp::read_frame(reader);
    require(decoded.json == json, "fragmented JSON read failed");
    require(decoded.binary == binary, "fragmented binary read failed");
    require(decoded.payload_hash == original.payload_hash, "fragmented frame hash changed");
}

void test_invalid_utf8() {
    const std::array<std::uint8_t, 3> invalid{0xedU, 0xa0U, 0x80U};
    require_throws(
        [&invalid] { aviutl2_mcp::validate_utf8(invalid); },
        "UTF-8 surrogate sequence was accepted");
}

void test_user_only_security() {
    aviutl2_mcp::user_only_security security;
    require(security.attributes()->bInheritHandle == FALSE, "security attributes allowed handle inheritance");
    ACL_SIZE_INFORMATION information{};
    require(
        GetAclInformation(security.acl(), &information, sizeof(information), AclSizeInformation) != FALSE,
        "GetAclInformation failed");
    require(information.AceCount == 2U, "user-only DACL did not contain exactly logon SID and SYSTEM");
}

void test_descriptor_publish_remove() {
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    const std::filesystem::path directory = create_test_directory(identity.instance_id);
    directory_cleanup cleanup(directory);
    aviutl2_mcp::instance_descriptor_publisher publisher(identity, directory, "0.1.0");
    publisher.publish();
    require(std::filesystem::exists(publisher.path()), "descriptor was not published");

    std::ifstream stream(publisher.path(), std::ios::binary);
    const std::string document(
        (std::istreambuf_iterator<char>(stream)),
        std::istreambuf_iterator<char>());
    require(document.find(identity.instance_id) != std::string::npos, "descriptor omitted instance ID");
    require(document.find(identity.pipe_name) != std::string::npos, "descriptor omitted pipe name");
    stream.close();
    publisher.remove();
    require(!std::filesystem::exists(publisher.path()), "descriptor was not removed");
}

[[nodiscard]] aviutl2_mcp::client_hello create_client_hello(
    const aviutl2_mcp::bridge_identity& identity,
    const std::uint32_t process_id) {
    return aviutl2_mcp::client_hello{
        .client_instance_id = aviutl2_mcp::create_bridge_identity().instance_id,
        .client_process_id = process_id,
        .target_instance_id = identity.instance_id,
        .protocol = {1U, 0U, 1U, 0U},
        .client_version = "0.1.0",
        .limits = {1024U, 2048U, 16U},
    };
}

void test_handshake_negotiation() {
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    const aviutl2_mcp::handshake_handler handler(identity, "2003300");
    aviutl2_mcp::client_hello hello = create_client_hello(identity, GetCurrentProcessId());
    const aviutl2_mcp::handshake_result accepted = handler.negotiate(hello, GetCurrentProcessId());
    require(accepted.accepted, "compatible ClientHello was rejected");
    require(accepted.limits.in_flight == 8U, "in-flight limit was not negotiated to the smaller value");

    const aviutl2_mcp::handshake_result pid_rejected = handler.negotiate(hello, GetCurrentProcessId() + 1U);
    require(pid_rejected.error_code == "client_pid_mismatch", "client PID mismatch was not rejected");
    hello.protocol = {2U, 0U, 2U, 0U};
    const aviutl2_mcp::handshake_result protocol_rejected = handler.negotiate(hello, GetCurrentProcessId());
    require(protocol_rejected.error_code == "protocol_incompatible", "incompatible protocol was not rejected");
}

void test_named_pipe_handshake() {
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    aviutl2_mcp::named_pipe_server server(identity, "2003300");
    server.start();

    const std::wstring path = L"\\\\.\\pipe\\" + std::wstring(identity.pipe_name.begin(), identity.pipe_name.end());
    const HANDLE pipe = CreateFileW(
        path.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        0U,
        nullptr,
        OPEN_EXISTING,
        0U,
        nullptr);
    require(pipe != INVALID_HANDLE_VALUE, "test client could not connect to the secured named pipe");
    handle_transport transport(pipe);

    const aviutl2_mcp::client_hello hello = create_client_hello(identity, GetCurrentProcessId());
    const std::string hello_json = "{\"clientInstanceId\":\"" + hello.client_instance_id
        + "\",\"clientProcessId\":" + std::to_string(hello.client_process_id)
        + ",\"targetInstanceId\":\"" + hello.target_instance_id
        + "\",\"protocol\":{\"minMajor\":1,\"minMinor\":0,\"maxMajor\":1,\"maxMinor\":0}"
          ",\"clientVersion\":\"0.1.0\",\"limits\":{\"jsonBytes\":8388608,\"binaryBytes\":16777216,\"inFlight\":8}}";
    const std::vector<std::uint8_t> hello_bytes(hello_json.begin(), hello_json.end());
    aviutl2_mcp::ipc_frame request{
        .header = create_header(
            aviutl2_mcp::message_kind::client_hello,
            aviutl2_mcp::frame_flags::none,
            static_cast<std::uint32_t>(hello_bytes.size()),
            0U),
        .json = hello_bytes,
        .binary = {},
        .payload_hash = {},
    };
    aviutl2_mcp::write_frame(transport, request);
    const aviutl2_mcp::ipc_frame response = aviutl2_mcp::read_frame(transport);
    require(response.header.kind == aviutl2_mcp::message_kind::server_hello, "server did not return ServerHello");
    const std::string response_json(response.json.begin(), response.json.end());
    require(response_json.find("\"accepted\":true") != std::string::npos, "server rejected valid handshake");
    require(response_json.find(identity.server_epoch) != std::string::npos, "ServerHello omitted stable epoch");

    aviutl2_mcp::ipc_frame close{
        .header = aviutl2_mcp::frame_header{
            .kind = aviutl2_mcp::message_kind::close,
            .flags = aviutl2_mcp::frame_flags::none,
            .request_id = {},
            .json_length = 0U,
            .binary_length = 0U,
        },
        .json = {},
        .binary = {},
        .payload_hash = {},
    };
    aviutl2_mcp::write_frame(transport, close);
    CloseHandle(pipe);
    server.stop();
    const auto diagnostics = server.last_session();
    require(diagnostics.has_value() && diagnostics->handshake_accepted, "accepted session was not recorded");
    require(diagnostics->client_process_id == GetCurrentProcessId(), "actual named pipe client PID was not recorded");
}

void test_runtime_lifecycle() {
    const aviutl2_mcp::bridge_identity directory_identity = aviutl2_mcp::create_bridge_identity();
    const std::filesystem::path directory = create_test_directory(directory_identity.instance_id);
    directory_cleanup cleanup(directory);
    aviutl2_mcp::bridge_runtime runtime(directory);
    require(runtime.start(2003300U), "runtime did not start");
    require(!runtime.start(2003300U), "runtime started more than once");
    const std::filesystem::path descriptor = runtime.descriptor_path();
    require(std::filesystem::exists(descriptor), "runtime did not publish its descriptor");
    runtime.stop();
    require(!std::filesystem::exists(descriptor), "runtime did not remove its descriptor before shutdown");
    runtime.stop();
}

}  // namespace

int main() {
    const std::array tests{
        std::pair{"bridge version", &test_bridge_version},
        std::pair{"header golden vector", &test_header_golden_vector},
        std::pair{"frame fragmentation and hash", &test_frame_fragmentation_and_hash},
        std::pair{"strict UTF-8", &test_invalid_utf8},
        std::pair{"user-only security", &test_user_only_security},
        std::pair{"descriptor publish/remove", &test_descriptor_publish_remove},
        std::pair{"handshake negotiation", &test_handshake_negotiation},
        std::pair{"named pipe handshake", &test_named_pipe_handshake},
        std::pair{"runtime lifecycle", &test_runtime_lifecycle},
    };
    int failures = 0;
    for (const auto& [name, test] : tests) {
        try {
            test();
            std::cout << "PASS " << name << '\n';
        } catch (const std::exception& exception) {
            ++failures;
            std::cerr << "FAIL " << name << ": " << exception.what() << '\n';
        }
    }
    return failures;
}

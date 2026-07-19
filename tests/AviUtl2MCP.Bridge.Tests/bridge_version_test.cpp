#include "aviutl2_mcp/at_most_once_store.h"
#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/bridge_runtime.h"
#include "aviutl2_mcp/bridge_version.h"
#include "aviutl2_mcp/cancellation_registry.h"
#include "aviutl2_mcp/command_gate.h"
#include "aviutl2_mcp/handshake.h"
#include "aviutl2_mcp/instance_descriptor.h"
#include "aviutl2_mcp/ipc_header.h"
#include "aviutl2_mcp/locator_resolver.h"
#include "aviutl2_mcp/named_pipe_server.h"
#include "aviutl2_mcp/native_capabilities_request_handler.h"
#include "aviutl2_mcp/native_ipc_frame_codec.h"
#include "aviutl2_mcp/native_log_request_handler.h"
#include "aviutl2_mcp/native_project_request_handler.h"
#include "aviutl2_mcp/native_ring_logger.h"
#include "aviutl2_mcp/native_status_request_handler.h"
#include "aviutl2_mcp/pipe_security.h"
#include "aviutl2_mcp/request_dispatcher.h"
#include "aviutl2_mcp/revision_tracker.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include <Windows.h>

#include "logger2.h"
#include "plugin2.h"
#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <mutex>
#include <span>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace {

using aviutl2_mcp::byte_transport;

std::mutex HOST_LOG_MUTEX;
std::vector<std::pair<std::string, std::wstring>> HOST_LOG_MESSAGES;

void capture_host_message(const char* level, const LPCWSTR message) {
    std::scoped_lock lock(HOST_LOG_MUTEX);
    HOST_LOG_MESSAGES.emplace_back(level, message == nullptr ? L"" : message);
}

void capture_host_log(LOG_HANDLE*, const LPCWSTR message) {
    capture_host_message("log", message);
}

void capture_host_info(LOG_HANDLE*, const LPCWSTR message) {
    capture_host_message("information", message);
}

void capture_host_warning(LOG_HANDLE*, const LPCWSTR message) {
    capture_host_message("warning", message);
}

void capture_host_error(LOG_HANDLE*, const LPCWSTR message) {
    capture_host_message("error", message);
}

void capture_host_trace(LOG_HANDLE*, const LPCWSTR message) {
    capture_host_message("trace", message);
}

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

[[nodiscard]] std::array<std::uint8_t, 16> create_uuid_v7_bytes(
    const std::chrono::system_clock::time_point time,
    const std::uint8_t discriminator = 1U) {
    const auto milliseconds = static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(time.time_since_epoch()).count());
    std::array<std::uint8_t, 16> bytes{};
    for (std::size_t index = 0U; index < 6U; ++index) {
        bytes[5U - index] = static_cast<std::uint8_t>(milliseconds >> (index * 8U));
    }
    bytes[6] = 0x70U;
    bytes[8] = 0x80U;
    bytes[15] = discriminator;
    return bytes;
}

[[nodiscard]] aviutl2_mcp::ipc_frame create_request_frame(
    const std::array<std::uint8_t, 16>& request_id,
    const std::string& method,
    const std::string& correlation_id,
    const std::string& params = "{}") {
    const std::string json = "{\"method\":\"" + method
        + "\",\"correlationId\":\"" + correlation_id
        + "\",\"timeoutMs\":5000,\"dryRun\":false,\"params\":" + params + "}";
    aviutl2_mcp::ipc_frame frame{
        .header = aviutl2_mcp::frame_header{
            .kind = aviutl2_mcp::message_kind::request,
            .flags = aviutl2_mcp::frame_flags::none,
            .request_id = request_id,
            .json_length = static_cast<std::uint32_t>(json.size()),
            .binary_length = 0U,
        },
        .json = {json.begin(), json.end()},
        .binary = {},
        .payload_hash = {},
    };
    frame.payload_hash = aviutl2_mcp::calculate_payload_hash(frame.header, frame.json, frame.binary);
    return frame;
}

[[nodiscard]] std::string get_json(const aviutl2_mcp::ipc_frame& frame) {
    return {frame.json.begin(), frame.json.end()};
}

class lambda_operation_handler final : public aviutl2_mcp::operation_handler {
public:
    using function_type = std::function<aviutl2_mcp::operation_result(
        const aviutl2_mcp::operation_request&,
        aviutl2_mcp::operation_execution_context&)>;

    lambda_operation_handler(std::string operation, const bool is_mutating, function_type execute)
        : operation_(std::move(operation)),
          is_mutating_(is_mutating),
          execute_(std::move(execute)) {}

    [[nodiscard]] std::string operation() const override {
        return operation_;
    }

    [[nodiscard]] bool is_mutating() const noexcept override {
        return is_mutating_;
    }

    [[nodiscard]] aviutl2_mcp::operation_result execute(
        const aviutl2_mcp::operation_request& request,
        aviutl2_mcp::operation_execution_context& context) override {
        return execute_(request, context);
    }

private:
    std::string operation_;
    bool is_mutating_;
    function_type execute_;
};

struct fake_sdk_state final {
    HOST_APP_TABLE host{};
    EDIT_HANDLE edit_handle{};
    EDIT_SECTION edit_section{};
    EDIT_INFO edit_info{};
    PROJECT_FILE project_file{};
    std::wstring project_path = L"D:\\Video\\fixture.aup2";
    std::wstring scene_name = L"Main";
    int edit_state = EDIT_HANDLE::EDIT_STATE_EDIT;
    int first_object = 1;
    int second_object = 2;
    void (*project_load_handler)(PROJECT_FILE*) = nullptr;
    void (*project_save_handler)(PROJECT_FILE*) = nullptr;
    bool should_throw_edit_state = false;
};

fake_sdk_state* ACTIVE_FAKE_SDK = nullptr;

[[nodiscard]] EDIT_HANDLE* create_fake_edit_handle() {
    return &ACTIVE_FAKE_SDK->edit_handle;
}

void register_fake_project_load_handler(void (*handler)(PROJECT_FILE*)) {
    ACTIVE_FAKE_SDK->project_load_handler = handler;
}

void register_fake_project_save_handler(void (*handler)(PROJECT_FILE*)) {
    ACTIVE_FAKE_SDK->project_save_handler = handler;
}

void get_fake_edit_info(EDIT_INFO* info, const int info_size) {
    require(info != nullptr && info_size == sizeof(EDIT_INFO), "fake SDK received invalid edit info storage");
    *info = ACTIVE_FAKE_SDK->edit_info;
}

[[nodiscard]] int get_fake_edit_state() {
    if (ACTIVE_FAKE_SDK->should_throw_edit_state) {
        throw std::runtime_error("fake edit state failure");
    }
    return ACTIVE_FAKE_SDK->edit_state;
}

[[nodiscard]] bool call_fake_read_section(
    void* parameter,
    void (*callback)(void*, EDIT_SECTION*)) {
    callback(parameter, &ACTIVE_FAKE_SDK->edit_section);
    return true;
}

[[nodiscard]] LPCWSTR get_fake_project_path() {
    return ACTIVE_FAKE_SDK->project_path.c_str();
}

[[nodiscard]] int get_fake_selected_object_count() {
    return 2;
}

[[nodiscard]] OBJECT_HANDLE get_fake_selected_object(const int index) {
    return index == 0 ? &ACTIVE_FAKE_SDK->first_object
        : index == 1 ? &ACTIVE_FAKE_SDK->second_object
        : nullptr;
}

[[nodiscard]] OBJECT_LAYER_FRAME get_fake_object_position(const OBJECT_HANDLE object) {
    if (object == &ACTIVE_FAKE_SDK->first_object) {
        return {.layer = 1, .start = 9, .end = 19};
    }
    if (object == &ACTIVE_FAKE_SDK->second_object) {
        return {.layer = 3, .start = 20, .end = 29};
    }
    return {.layer = -1, .start = -1, .end = -1};
}

[[nodiscard]] LPCWSTR get_fake_scene_name() {
    return ACTIVE_FAKE_SDK->scene_name.c_str();
}

void configure_fake_sdk(fake_sdk_state& state) {
    ACTIVE_FAKE_SDK = &state;
    state.edit_info = {
        .width = 1920,
        .height = 1080,
        .rate = 30'000,
        .scale = 1'001,
        .sample_rate = 48'000,
        .frame = 14,
        .layer = 1,
        .frame_max = 299,
        .layer_max = 9,
        .display_frame_start = 0,
        .display_layer_start = 0,
        .display_frame_num = 100,
        .display_layer_num = 10,
        .select_range_start = 10,
        .select_range_end = 20,
        .grid_bpm_tempo = 120.0F,
        .grid_bpm_beat = 4,
        .grid_bpm_offset = 0.0F,
        .scene_id = 7,
    };
    state.edit_handle.get_edit_info = &get_fake_edit_info;
    state.edit_handle.get_edit_state = &get_fake_edit_state;
    state.edit_handle.call_read_section_param = &call_fake_read_section;
    state.edit_section.info = &state.edit_info;
    state.edit_section.get_selected_object_num = &get_fake_selected_object_count;
    state.edit_section.get_selected_object = &get_fake_selected_object;
    state.edit_section.get_object_layer_frame = &get_fake_object_position;
    state.edit_section.get_scene_name = &get_fake_scene_name;
    state.project_file.get_project_file_path = &get_fake_project_path;
    state.host.create_edit_handle = &create_fake_edit_handle;
    state.host.register_project_load_handler = &register_fake_project_load_handler;
    state.host.register_project_save_handler = &register_fake_project_save_handler;
}

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

    const std::string correlation_id = aviutl2_mcp::create_bridge_identity().instance_id;
    aviutl2_mcp::ipc_frame unsupported_request = create_request_frame(
        create_uuid_v7_bytes(std::chrono::system_clock::now(), 7U),
        "status.missing",
        correlation_id);
    aviutl2_mcp::write_frame(transport, unsupported_request);
    const aviutl2_mcp::ipc_frame unsupported_response = aviutl2_mcp::read_frame(transport);
    require(
        get_json(unsupported_response).find("operation_not_supported") != std::string::npos,
        "established named pipe session did not route Request through dispatcher");

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

void test_command_gate_serialization_and_shutdown() {
    aviutl2_mcp::command_gate gate(1U);
    std::promise<void> first_started;
    std::promise<void> release_first;
    std::shared_future<void> release_signal = release_first.get_future().share();
    std::promise<void> first_completed;
    std::atomic<int> cancelled_count = 0;

    require(
        gate.try_enqueue(
            [&first_started, &release_signal, &first_completed] {
                first_started.set_value();
                release_signal.wait();
                first_completed.set_value();
            },
            [] {}) == aviutl2_mcp::gate_enqueue_result::accepted,
        "command gate rejected the first task");
    require(
        first_started.get_future().wait_for(std::chrono::seconds(2)) == std::future_status::ready,
        "command gate did not start its worker task");
    require(
        gate.try_enqueue([] {}, [&cancelled_count] { ++cancelled_count; })
            == aviutl2_mcp::gate_enqueue_result::accepted,
        "command gate rejected a queued task");
    require(
        gate.try_enqueue([] {}, [] {}) == aviutl2_mcp::gate_enqueue_result::busy,
        "command gate did not enforce queue capacity");

    std::future<void> stop = std::async(std::launch::async, [&gate] { gate.stop(); });
    for (int attempt = 0; attempt < 200 && cancelled_count.load() == 0; ++attempt) {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    const bool was_queued_work_cancelled = cancelled_count.load() == 1;
    release_first.set_value();
    require(
        first_completed.get_future().wait_for(std::chrono::seconds(2)) == std::future_status::ready,
        "command gate did not drain executing work");
    require(stop.wait_for(std::chrono::seconds(2)) == std::future_status::ready, "command gate stop did not join");
    require(was_queued_work_cancelled, "command gate did not cancel queued work during shutdown");
}

void test_cancellation_state_machine() {
    aviutl2_mcp::cancellation_registry registry;
    const auto before_commit = create_uuid_v7_bytes(std::chrono::system_clock::now(), 10U);
    require(registry.register_request(before_commit), "cancellation registry rejected a new request");
    require(registry.try_begin(before_commit), "cancellation registry did not begin queued request");
    const auto cancelled = registry.cancel(before_commit);
    require(
        cancelled.status == aviutl2_mcp::cancel_status::cancelled && cancelled.response_will_follow,
        "pre-commit cancellation did not return cancelled");
    require(!registry.try_reach_commit_point(before_commit), "cancelled request reached commit point");
    registry.complete(before_commit);

    const auto after_commit = create_uuid_v7_bytes(std::chrono::system_clock::now(), 11U);
    require(registry.register_request(after_commit), "cancellation registry rejected committed test request");
    require(registry.try_begin(after_commit), "committed test request did not begin");
    require(registry.try_reach_commit_point(after_commit), "request did not reach commit point");
    const auto too_late = registry.cancel(after_commit);
    require(
        too_late.status == aviutl2_mcp::cancel_status::too_late && too_late.response_will_follow,
        "post-commit cancellation did not return tooLate");
    registry.complete(after_commit);
    require(
        registry.cancel(after_commit).status == aviutl2_mcp::cancel_status::not_found,
        "completed request was still cancellable");
}

void test_at_most_once_store() {
    using clock = aviutl2_mcp::at_most_once_store::clock;
    const clock::time_point now = clock::time_point(std::chrono::milliseconds(1780000000000LL));
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    const std::string client_id = aviutl2_mcp::create_bridge_identity().instance_id;
    aviutl2_mcp::at_most_once_store store(
        identity.server_epoch,
        {.maximum_tombstones = 3U, .maximum_cached_responses = 1U, .maximum_response_bytes = 1024U},
        [now] { return now; });
    const std::array<std::uint8_t, 1> first_payload{1U};
    const std::array<std::uint8_t, 1> second_payload{2U};
    const std::string payload_hash = aviutl2_mcp::calculate_sha256(first_payload);

    aviutl2_mcp::mutation_key first_key{identity.server_epoch, client_id, create_uuid_v7_bytes(now, 1U)};
    const auto accepted = store.begin(first_key, payload_hash);
    require(accepted.decision == aviutl2_mcp::mutation_begin_decision::accepted, "mutation was not accepted");
    const auto attached = store.begin(first_key, payload_hash);
    require(attached.decision == aviutl2_mcp::mutation_begin_decision::attach, "duplicate did not attach");
    require(
        store.begin(first_key, aviutl2_mcp::calculate_sha256(second_payload)).decision
            == aviutl2_mcp::mutation_begin_decision::request_id_conflict,
        "different payload reused a request ID");
    store.mark_executing(accepted.token);
    store.complete(accepted.token, "completed", "revision-1", "{\"ok\":true}");
    const auto completed = store.wait_for_completion(attached.token, std::chrono::seconds(1));
    require(completed.has_value() && completed->response_json.has_value(), "attached mutation missed completion");
    require(
        store.begin(first_key, payload_hash).decision == aviutl2_mcp::mutation_begin_decision::cached,
        "completed mutation did not return cached response");

    aviutl2_mcp::mutation_key second_key{identity.server_epoch, client_id, create_uuid_v7_bytes(now, 2U)};
    const auto second = store.begin(second_key, payload_hash);
    store.mark_executing(second.token);
    store.complete(second.token, "completed", "revision-2", "{\"ok\":true,\"n\":2}");
    require(
        store.begin(first_key, payload_hash).decision == aviutl2_mcp::mutation_begin_decision::result_evicted,
        "LRU eviction allowed mutation re-execution");

    aviutl2_mcp::mutation_key third_key{identity.server_epoch, client_id, create_uuid_v7_bytes(now, 3U)};
    require(
        store.begin(third_key, payload_hash).decision == aviutl2_mcp::mutation_begin_decision::accepted,
        "third tombstone was not accepted");
    aviutl2_mcp::mutation_key fourth_key{identity.server_epoch, client_id, create_uuid_v7_bytes(now, 4U)};
    require(
        store.begin(fourth_key, payload_hash).decision == aviutl2_mcp::mutation_begin_decision::bridge_busy,
        "full tombstone store did not return bridge_busy");
    aviutl2_mcp::mutation_key expired_key{
        identity.server_epoch,
        client_id,
        create_uuid_v7_bytes(now - std::chrono::minutes(11), 5U)};
    require(
        store.begin(expired_key, payload_hash).decision == aviutl2_mcp::mutation_begin_decision::request_expired,
        "old UUIDv7 mutation was accepted");
}

void test_revision_tracker() {
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    aviutl2_mcp::revision_tracker tracker(identity.server_epoch);
    const std::string initial_content = tracker.content_revision();
    const std::string initial_view = tracker.view_revision();
    require(tracker.matches_content(initial_content), "initial content revision did not match itself");
    require(tracker.commit_content_change() != initial_content, "content commit did not advance revision");
    require(tracker.view_revision() == initial_view, "content commit changed view revision");
    const auto scene_revisions = tracker.commit_scene_change();
    require(scene_revisions.first == tracker.content_revision(), "scene commit content revision mismatch");
    require(scene_revisions.second == tracker.view_revision(), "scene commit view revision mismatch");

    const std::string next_project = aviutl2_mcp::create_bridge_identity().instance_id;
    tracker.reset_project(next_project);
    require(tracker.project_generation() == next_project, "project generation did not reset");
    require(!tracker.matches_content(initial_content), "old project revision remained valid");
}

void test_locator_resolution() {
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    const std::string project_generation = aviutl2_mcp::create_bridge_identity().instance_id;
    const aviutl2_mcp::object_candidate candidate{
        .scene_id = 0,
        .layer = 2,
        .start_frame = 10,
        .end_frame = 39,
        .name = "character",
        .alias = {'[', 'O', 'b', 'j', 'e', 'c', 't', ']'},
        .effects = {{"PSDToolKit", {{"characterId", "string"}, {"layerState", "string"}}}},
    };
    const aviutl2_mcp::object_locator locator = aviutl2_mcp::create_object_locator(
        identity.instance_id,
        project_generation,
        candidate);
    const std::array one_candidate{candidate};
    const auto resolved = aviutl2_mcp::resolve_object_locator(
        locator,
        identity.instance_id,
        project_generation,
        one_candidate);
    require(
        resolved.status == aviutl2_mcp::locator_resolution_status::resolved
            && resolved.candidate_index == 0U,
        "unique locator did not resolve");

    aviutl2_mcp::object_candidate changed = candidate;
    changed.effects[0].items[0].type = "integer";
    const std::array changed_candidate{changed};
    require(
        aviutl2_mcp::resolve_object_locator(
            locator, identity.instance_id, project_generation, changed_candidate).status
            == aviutl2_mcp::locator_resolution_status::not_found,
        "effect signature mismatch resolved an object");
    const std::array duplicates{candidate, candidate};
    require(
        aviutl2_mcp::resolve_object_locator(
            locator, identity.instance_id, project_generation, duplicates).status
            == aviutl2_mcp::locator_resolution_status::ambiguous,
        "duplicate fingerprint was resolved by enumeration order");
}

void test_request_dispatcher_and_at_most_once() {
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    const std::string client_id = aviutl2_mcp::create_bridge_identity().instance_id;
    aviutl2_mcp::request_dispatcher dispatcher(identity);
    std::atomic<int> mutation_count = 0;
    std::atomic<int> attached_count = 0;
    auto attached_started = std::make_shared<std::promise<void>>();
    auto attached_release = std::make_shared<std::promise<void>>();
    std::shared_future<void> attached_release_signal = attached_release->get_future().share();
    dispatcher.register_handler(std::make_unique<lambda_operation_handler>(
        "status.echo",
        false,
        [](const aviutl2_mcp::operation_request& request, aviutl2_mcp::operation_execution_context& context) {
            return aviutl2_mcp::operation_result{
                .ok = true,
                .outcome = "completed",
                .result_json = request.params_json,
                .error_code = {},
                .error_message = {},
                .revision = context.revisions().content_revision(),
                .view_revision = context.revisions().view_revision(),
            };
        }));
    dispatcher.register_handler(std::make_unique<lambda_operation_handler>(
        "object.attach",
        true,
        [&attached_count, attached_started, attached_release_signal](
            const aviutl2_mcp::operation_request&,
            aviutl2_mcp::operation_execution_context& context) {
            ++attached_count;
            attached_started->set_value();
            attached_release_signal.wait();
            if (!context.reach_commit_point()) {
                throw std::runtime_error("attached mutation was cancelled");
            }
            const std::string revision = context.revisions().commit_content_change();
            return aviutl2_mcp::operation_result{
                .ok = true,
                .outcome = "completed",
                .result_json = "{\"attached\":true}",
                .error_code = {},
                .error_message = {},
                .revision = revision,
                .view_revision = context.revisions().view_revision(),
            };
        }));
    dispatcher.register_handler(std::make_unique<lambda_operation_handler>(
        "object.mutate",
        true,
        [&mutation_count](const aviutl2_mcp::operation_request&, aviutl2_mcp::operation_execution_context& context) {
            ++mutation_count;
            if (!context.reach_commit_point()) {
                throw std::runtime_error("mutation was cancelled");
            }
            const std::string revision = context.revisions().commit_content_change();
            return aviutl2_mcp::operation_result{
                .ok = true,
                .outcome = "completed",
                .result_json = "{\"changed\":true}",
                .error_code = {},
                .error_message = {},
                .revision = revision,
                .view_revision = context.revisions().view_revision(),
            };
        }));

    const std::string correlation_id = aviutl2_mcp::create_bridge_identity().instance_id;
    const auto read_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 21U);
    const auto read_response = dispatcher.dispatch(
        create_request_frame(read_id, "status.echo", correlation_id, "{\"value\":1}"),
        client_id).get();
    require(get_json(read_response).find("\"value\":1") != std::string::npos, "dispatcher did not route read handler");

    const auto mutation_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 22U);
    const auto mutation_frame = create_request_frame(mutation_id, "object.mutate", correlation_id);
    const auto first = dispatcher.dispatch(mutation_frame, client_id).get();
    const auto retry = dispatcher.dispatch(mutation_frame, client_id).get();
    require(get_json(first) == get_json(retry), "mutation retry did not return identical cached response");
    require(mutation_count.load() == 1, "mutation retry executed more than once");

    const auto conflict = dispatcher.dispatch(
        create_request_frame(mutation_id, "object.mutate", correlation_id, "{\"different\":true}"),
        client_id).get();
    require(
        get_json(conflict).find("request_id_conflict") != std::string::npos,
        "payload conflict was not rejected");

    const auto attached_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 23U);
    const auto attached_frame = create_request_frame(attached_id, "object.attach", correlation_id);
    std::future<aviutl2_mcp::ipc_frame> original = dispatcher.dispatch(attached_frame, client_id);
    require(
        attached_started->get_future().wait_for(std::chrono::seconds(2)) == std::future_status::ready,
        "original attached mutation did not start");
    std::future<aviutl2_mcp::ipc_frame> duplicate = dispatcher.dispatch(attached_frame, client_id);
    attached_release->set_value();
    require(
        original.wait_for(std::chrono::seconds(2)) == std::future_status::ready
            && duplicate.wait_for(std::chrono::seconds(2)) == std::future_status::ready,
        "attached mutation responses did not complete");
    require(get_json(original.get()) == get_json(duplicate.get()), "attached retry response differed");
    require(attached_count.load() == 1, "in-flight attached mutation executed twice");
    const aviutl2_mcp::native_log_snapshot correlation_logs =
        aviutl2_mcp::get_native_logger().snapshot({
            .limit = 100U,
            .after_sequence = std::nullopt,
            .correlation_id = correlation_id,
        });
    require(
        std::ranges::any_of(correlation_logs.entries, [](const aviutl2_mcp::native_log_entry& entry) {
            return entry.event_id == "request.received";
        }),
        "dispatcher did not record the request correlation ID");
    require(
        std::ranges::any_of(correlation_logs.entries, [](const aviutl2_mcp::native_log_entry& entry) {
            return entry.event_id == "request.completed" || entry.event_id == "request.rejected";
        }),
        "dispatcher did not record a correlated request outcome");
    const auto completed_log = std::ranges::find_if(
        correlation_logs.entries,
        [](const aviutl2_mcp::native_log_entry& entry) {
            return entry.event_id == "request.completed";
        });
    require(completed_log != correlation_logs.entries.end(), "dispatcher completion log was missing");
    require(completed_log->instance_id == identity.instance_id, "dispatcher log omitted the instance ID");
    require(completed_log->operation.has_value(), "dispatcher log omitted the operation");
    require(completed_log->duration_ms.has_value() && *completed_log->duration_ms >= 0.0,
        "dispatcher log omitted the duration");
    require(completed_log->result_code == "ok", "dispatcher log omitted the result code");
    dispatcher.stop();
}

void test_request_dispatcher_cancellation() {
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    const std::string client_id = aviutl2_mcp::create_bridge_identity().instance_id;
    aviutl2_mcp::request_dispatcher dispatcher(identity);
    auto started = std::make_shared<std::promise<void>>();
    auto release = std::make_shared<std::promise<void>>();
    std::shared_future<void> release_signal = release->get_future().share();
    dispatcher.register_handler(std::make_unique<lambda_operation_handler>(
        "object.cancel-before",
        true,
        [started, release_signal](
            const aviutl2_mcp::operation_request&,
            aviutl2_mcp::operation_execution_context& context) {
            started->set_value();
            release_signal.wait();
            if (!context.reach_commit_point()) {
                return aviutl2_mcp::operation_result{
                    .ok = false,
                    .outcome = "unchanged",
                    .result_json = {},
                    .error_code = "operation_cancelled",
                    .error_message = "cancelled",
                    .revision = context.revisions().content_revision(),
                    .view_revision = context.revisions().view_revision(),
                };
            }
            throw std::runtime_error("cancelled handler reached commit point");
        }));

    const std::string correlation_id = aviutl2_mcp::create_bridge_identity().instance_id;
    const auto request_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 31U);
    std::future<aviutl2_mcp::ipc_frame> response = dispatcher.dispatch(
        create_request_frame(request_id, "object.cancel-before", correlation_id),
        client_id);
    require(
        started->get_future().wait_for(std::chrono::seconds(2)) == std::future_status::ready,
        "cancellation handler did not start");
    aviutl2_mcp::ipc_frame cancel_frame{
        .header = aviutl2_mcp::frame_header{
            .kind = aviutl2_mcp::message_kind::cancel,
            .flags = aviutl2_mcp::frame_flags::none,
            .request_id = request_id,
            .json_length = 0U,
            .binary_length = 0U,
        },
        .json = {},
        .binary = {},
        .payload_hash = {},
    };
    const auto ack = dispatcher.cancel(cancel_frame);
    require(get_json(ack).find("\"status\":\"cancelled\"") != std::string::npos, "CancelAck was not cancelled");
    release->set_value();
    require(
        response.wait_for(std::chrono::seconds(2)) == std::future_status::ready,
        "cancelled operation did not return its single final response");
    require(
        get_json(response.get()).find("operation_cancelled") != std::string::npos,
        "cancelled operation returned success");
    dispatcher.stop();
}

void test_native_ring_logger_and_host_sink() {
    {
        std::scoped_lock lock(HOST_LOG_MUTEX);
        HOST_LOG_MESSAGES.clear();
    }
    LOG_HANDLE host_logger{
        &capture_host_log,
        &capture_host_info,
        &capture_host_warning,
        &capture_host_error,
        &capture_host_trace,
    };
    aviutl2_mcp::native_ring_logger logger(2U);
    logger.attach(&host_logger);

    logger.write(
        aviutl2_mcp::native_log_level::information,
        "dispatcher",
        "request.accepted",
        "token=alpha accepted",
        {.correlation_id = "correlation-one"});
    logger.write(
        aviutl2_mcp::native_log_level::warning,
        "dispatcher",
        "request.failed",
        "password: beta C:\\Users\\alice\\project.aup",
        {
            .correlation_id = "correlation-two",
            .instance_id = "instance-one",
            .operation = "object.move",
            .duration_ms = 9.5,
            .result_code = "revision_conflict",
        });
    logger.write(
        aviutl2_mcp::native_log_level::error,
        "runtime",
        "bridge.failed",
        "Bearer gamma",
        {.correlation_id = "correlation-two"});

    aviutl2_mcp::native_log_snapshot complete = logger.snapshot({.limit = 10U});
    require(logger.capacity() == 2U, "native log capacity changed");
    require(complete.entries.size() == 2U, "native log ring did not evict its oldest entry");
    require(complete.has_evicted_entries, "native log ring did not report prior eviction");
    require(complete.entries[0].sequence == 2U && complete.entries[1].sequence == 3U,
        "native log sequence was not monotonic");
    require(complete.entries[0].source == "bridge", "native log source was incorrect");
    require(complete.entries[0].instance_id == "instance-one", "native log omitted the instance ID");
    require(complete.entries[0].operation == "object.move", "native log omitted the operation");
    require(complete.entries[0].duration_ms == 9.5, "native log omitted the duration");
    require(complete.entries[0].result_code == "revision_conflict", "native log omitted the result code");
    require(complete.entries[0].message.find("beta") == std::string::npos,
        "native log retained a password");
    require(complete.entries[0].message.find("alice") == std::string::npos,
        "native log retained a user directory");
    require(complete.entries[0].message.find("[USER]") != std::string::npos,
        "native log did not mask a user directory");
    require(complete.entries[1].message.find("gamma") == std::string::npos,
        "native log retained a bearer token");
    require(complete.entries[0].message.find("[REDACTED]") != std::string::npos,
        "native log did not mark a redacted value");
    require(!complete.entries[0].timestamp_utc.empty() && complete.entries[0].timestamp_utc.back() == 'Z',
        "native log timestamp was not UTC");

    const aviutl2_mcp::native_log_snapshot first_page = logger.snapshot({
        .limit = 1U,
        .after_sequence = std::nullopt,
        .correlation_id = "correlation-two",
        .component = std::nullopt,
        .levels = {
            aviutl2_mcp::native_log_level::warning,
            aviutl2_mcp::native_log_level::error,
        },
    });
    require(first_page.entries.size() == 1U && first_page.is_truncated,
        "native log paging did not report truncation");
    require(first_page.next_sequence == 2U, "native log paging cursor was incorrect");
    const aviutl2_mcp::native_log_snapshot second_page = logger.snapshot({
        .limit = 1U,
        .after_sequence = first_page.next_sequence,
        .correlation_id = "correlation-two",
    });
    require(second_page.entries.size() == 1U && second_page.entries[0].sequence == 3U,
        "native log cursor did not continue after the previous page");
    require(!second_page.is_truncated, "native log final page was incorrectly truncated");

    {
        std::scoped_lock lock(HOST_LOG_MUTEX);
        require(HOST_LOG_MESSAGES.size() == 3U, "host logger did not receive every native entry");
        require(HOST_LOG_MESSAGES[0].first == "information"
                && HOST_LOG_MESSAGES[1].first == "warning"
                && HOST_LOG_MESSAGES[2].first == "error",
            "native log level used the wrong host callback");
        require(HOST_LOG_MESSAGES[0].second.find(L"alpha") == std::wstring::npos,
            "host logger retained a secret");
        require(HOST_LOG_MESSAGES[2].second.find(L"correlationId=correlation-two") != std::wstring::npos,
            "host logger omitted the correlation ID");
        require(HOST_LOG_MESSAGES[1].second.find(L"operation=object.move") != std::wstring::npos,
            "host logger omitted the operation");
    }

    logger.attach(nullptr);
    logger.write(
        aviutl2_mcp::native_log_level::trace,
        "test",
        "detached",
        "detached host");
    {
        std::scoped_lock lock(HOST_LOG_MUTEX);
        require(HOST_LOG_MESSAGES.size() == 3U, "detached host logger was called");
    }
    require_throws(
        [&logger] { static_cast<void>(logger.snapshot({.limit = 0U})); },
        "native log accepted a zero query limit");
    require_throws(
        [] { static_cast<void>(aviutl2_mcp::native_ring_logger(0U)); },
        "native log accepted a zero capacity");
}

void test_native_log_request_handler() {
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    aviutl2_mcp::request_dispatcher dispatcher(identity);
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_log_request_handler>());
    const std::string correlation_id = aviutl2_mcp::create_bridge_identity().instance_id;
    const std::string request_correlation_id = aviutl2_mcp::create_bridge_identity().instance_id;
    aviutl2_mcp::get_native_logger().write(
        aviutl2_mcp::native_log_level::warning,
        "diagnostics-test",
        "fixture.warning",
        "password=secret native log fixture",
        {.correlation_id = correlation_id});
    aviutl2_mcp::get_native_logger().write(
        aviutl2_mcp::native_log_level::error,
        "diagnostics-test",
        "fixture.error",
        "second native log fixture",
        {.correlation_id = correlation_id});

    const auto first_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 41U);
    const std::string params = nlohmann::json{
        {"sources", {"bridge"}},
        {"levels", {"warning", "error"}},
        {"correlationId", correlation_id},
        {"limit", 1},
    }.dump();
    const auto first_response = dispatcher.dispatch(
        create_request_frame(first_id, "logs.get", request_correlation_id, params),
        identity.instance_id).get();
    const nlohmann::json first = nlohmann::json::parse(get_json(first_response));
    require(first.at("ok").get<bool>(), "native log request failed");
    require(first.at("result").at("entries").size() == 1U, "native log limit was ignored");
    require(first.at("result").at("isTruncated").get<bool>(), "native log truncation was omitted");
    require(first.at("result").at("entries")[0].at("message").get<std::string>().find("secret")
            == std::string::npos,
        "native log query exposed a secret");
    require(first.at("result").at("entries")[0].at("correlationId") == correlation_id,
        "native log query omitted correlation ID");
    const std::string cursor = first.at("result").at("nextCursor").get<std::string>();

    const auto second_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 42U);
    const std::string second_params = nlohmann::json{
        {"sources", {"bridge"}},
        {"correlationId", correlation_id},
        {"limit", 1},
        {"cursor", cursor},
    }.dump();
    const nlohmann::json second = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(second_id, "logs.get", request_correlation_id, second_params),
        identity.instance_id).get()));
    require(second.at("result").at("entries").size() == 1U,
        "native log cursor did not return the next page");
    require(!second.at("result").at("isTruncated").get<bool>(),
        "native log final page was truncated");

    const auto invalid_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 43U);
    const nlohmann::json invalid = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(invalid_id, "logs.get", request_correlation_id, "{\"limit\":0}"),
        identity.instance_id).get()));
    require(!invalid.at("ok").get<bool>()
            && invalid.at("error").at("code") == "invalid_argument",
        "native log handler accepted an invalid limit");

    const auto future_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 44U);
    const std::string future_params = nlohmann::json{
        {"sources", {"bridge"}},
        {"correlationId", correlation_id},
        {"since", "9999-12-31T23:59:59.9999999+00:00"},
    }.dump();
    const nlohmann::json future = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(future_id, "logs.get", request_correlation_id, future_params),
        identity.instance_id).get()));
    require(future.at("result").at("entries").empty(),
        "native log handler ignored the since timestamp");

    const auto malformed_since_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 45U);
    const nlohmann::json malformed_since = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            malformed_since_id,
            "logs.get",
            request_correlation_id,
            "{\"since\":\"2026-07-19\"}"),
        identity.instance_id).get()));
    require(!malformed_since.at("ok").get<bool>()
            && malformed_since.at("error").at("code") == "invalid_argument",
        "native log handler accepted an invalid since timestamp");
    dispatcher.stop();
}

void test_sdk_read_facade() {
    fake_sdk_state fake;
    configure_fake_sdk(fake);
    aviutl2_mcp::sdk_read_facade facade;

    require(facade.register_host(&fake.host), "SDK facade rejected a complete host table");
    require(fake.project_load_handler != nullptr && fake.project_save_handler != nullptr,
        "SDK facade did not register project callbacks");
    fake.project_load_handler(&fake.project_file);

    const aviutl2_mcp::sdk_status_snapshot saved_status = facade.query_status();
    require(saved_status.is_sdk_ready && !saved_status.has_query_error,
        "SDK facade did not report a healthy SDK");
    require(saved_status.project_state == aviutl2_mcp::sdk_project_state::saved,
        "SDK facade did not distinguish a saved project");
    require(saved_status.edit_state == aviutl2_mcp::sdk_edit_state::edit,
        "SDK facade did not map the edit state");
    require(saved_status.project_path == "D:\\Video\\fixture.aup2",
        "SDK facade did not copy the project path as UTF-8");

    fake.project_path = L"D:\\Video\\mutated.aup2";
    require(facade.query_status().project_path == "D:\\Video\\fixture.aup2",
        "SDK facade retained a callback-owned project path pointer");

    const aviutl2_mcp::sdk_project_query_result project = facade.query_project(true);
    require(project.ok, "SDK facade failed to copy a readable project");
    require(project.project.width == 1920 && project.project.height == 1080,
        "SDK facade copied incorrect project dimensions");
    require(project.project.current_frame == 15 && project.project.current_scene_id == 7,
        "SDK facade did not convert the current frame to the public coordinate system");
    require(project.project.selected_layers == std::vector<int>({2, 4}),
        "SDK facade did not copy selected layers");
    require(project.project.selection.has_value()
            && project.project.selection->start_frame == 11
            && project.project.selection->end_frame == 21,
        "SDK facade did not copy the selected frame range");
    require(project.project.scenes.size() == 1U
            && project.project.scenes[0].scene_id == 7
            && project.project.scenes[0].name == "Main",
        "SDK facade did not copy the current scene");

    fake.project_path.clear();
    fake.project_save_handler(&fake.project_file);
    require(facade.query_status().project_state == aviutl2_mcp::sdk_project_state::unsaved,
        "SDK facade did not distinguish an unsaved project");
    fake.project_load_handler(nullptr);
    require(facade.query_status().project_state == aviutl2_mcp::sdk_project_state::not_open,
        "SDK facade did not distinguish a missing project");
    require(facade.query_project(true).error_code == "project_not_open",
        "SDK facade returned an empty success for a missing project");

    fake.project_path = L"D:\\Video\\fixture.aup2";
    fake.project_load_handler(&fake.project_file);
    fake.should_throw_edit_state = true;
    const aviutl2_mcp::sdk_status_snapshot failed_status = facade.query_status();
    require(failed_status.has_query_error && failed_status.edit_state == aviutl2_mcp::sdk_edit_state::unknown,
        "SDK facade allowed an SDK exception to escape its boundary");
    fake.should_throw_edit_state = false;

    facade.detach();
    require(!facade.query_status().is_sdk_ready, "SDK facade retained the edit handle after detach");
    ACTIVE_FAKE_SDK = nullptr;
}

void test_native_query_request_handlers() {
    fake_sdk_state fake;
    configure_fake_sdk(fake);
    aviutl2_mcp::sdk_read_facade facade;
    require(facade.register_host(&fake.host), "query handler fixture SDK registration failed");
    fake.project_load_handler(&fake.project_file);

    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    aviutl2_mcp::request_dispatcher dispatcher(identity);
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_status_request_handler>(
        identity,
        "2003300",
        facade));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_capabilities_request_handler>(
        "2003300",
        facade));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_project_request_handler>(facade));
    const std::string correlation_id = aviutl2_mcp::create_bridge_identity().instance_id;

    const nlohmann::json status = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 51U),
            "status.get",
            correlation_id),
        identity.instance_id).get()));
    require(status.at("ok").get<bool>(), "native status query failed");
    require(status.at("result").at("connectionState") == "ready"
            && status.at("result").at("projectState") == "saved"
            && status.at("result").at("editState") == "edit",
        "native status query returned incorrect state");
    require(status.at("result").at("selectedInstance") == identity.instance_id
            && status.at("result").at("instances").size() == 1U,
        "native status query omitted the selected instance");

    const nlohmann::json capabilities = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 52U),
            "capabilities.get",
            correlation_id),
        identity.instance_id).get()));
    require(capabilities.at("ok").get<bool>(), "native capabilities query failed");
    const nlohmann::json& operations = capabilities.at("result").at("operations");
    require(operations.size() == 28U, "native capabilities query did not return all 28 operations");
    const auto find_operation = [&operations](const std::string& name) -> const nlohmann::json& {
        const auto match = std::ranges::find_if(operations, [&name](const nlohmann::json& operation) {
            return operation.at("name") == name;
        });
        if (match == operations.end()) {
            throw std::runtime_error("native capabilities omitted an operation");
        }
        return *match;
    };
    require(find_operation("aviutl_get_project").at("available").get<bool>(),
        "native capabilities disabled a readable project");
    require(!find_operation("aviutl_psd_create").at("available").get<bool>()
            && find_operation("aviutl_psd_create").at("reason") == "gcmzdrops_not_available",
        "native capabilities claimed an unprobed GCMZDrops integration");
    require(capabilities.at("result").at("versions").at("sdk") == "2003300"
            && capabilities.at("result").at("limits").at("pagingCursorTtlSeconds") == 300,
        "native capabilities returned incorrect versions or limits");

    const nlohmann::json project = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 53U),
            "project.get",
            correlation_id,
            R"({"includeScenes":true})"),
        identity.instance_id).get()));
    require(project.at("ok").get<bool>(), "native project query failed");
    require(project.at("result").at("path") == "D:\\Video\\fixture.aup2"
            && project.at("result").at("currentFrame") == 15
            && project.at("result").at("coordinateSystem").at("frameBase") == 1,
        "native project query returned an invalid public DTO");

    const nlohmann::json invalid = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 54U),
            "project.get",
            correlation_id,
            R"({"includeScenes":"yes"})"),
        identity.instance_id).get()));
    require(!invalid.at("ok").get<bool>() && invalid.at("error").at("code") == "invalid_argument",
        "native project query accepted an invalid includeScenes value");

    dispatcher.stop();
    facade.detach();
    ACTIVE_FAKE_SDK = nullptr;
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
        std::pair{"command gate serialization and shutdown", &test_command_gate_serialization_and_shutdown},
        std::pair{"cancellation state machine", &test_cancellation_state_machine},
        std::pair{"at-most-once store", &test_at_most_once_store},
        std::pair{"revision tracker", &test_revision_tracker},
        std::pair{"locator resolution", &test_locator_resolution},
        std::pair{"request dispatcher and at-most-once", &test_request_dispatcher_and_at_most_once},
        std::pair{"request dispatcher cancellation", &test_request_dispatcher_cancellation},
        std::pair{"native ring logger and host sink", &test_native_ring_logger_and_host_sink},
        std::pair{"native log request handler", &test_native_log_request_handler},
        std::pair{"SDK read facade", &test_sdk_read_facade},
        std::pair{"native query request handlers", &test_native_query_request_handlers},
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
#include <functional>
#include <future>

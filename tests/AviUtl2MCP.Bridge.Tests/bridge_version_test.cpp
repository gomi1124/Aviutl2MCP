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
#include "aviutl2_mcp/native_create_request_handler.h"
#include "aviutl2_mcp/native_effect_request_handlers.h"
#include "aviutl2_mcp/native_ipc_frame_codec.h"
#include "aviutl2_mcp/native_log_request_handler.h"
#include "aviutl2_mcp/native_object_request_handler.h"
#include "aviutl2_mcp/native_object_edit_request_handler.h"
#include "aviutl2_mcp/native_project_request_handler.h"
#include "aviutl2_mcp/native_ring_logger.h"
#include "aviutl2_mcp/native_status_request_handler.h"
#include "aviutl2_mcp/native_timeline_request_handlers.h"
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
#include <optional>
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
    const std::string& params = "{}",
    const std::optional<std::string>& expected_revision = std::nullopt,
    const bool dry_run = false) {
    nlohmann::json document{
        {"method", method},
        {"correlationId", correlation_id},
        {"timeoutMs", 5000},
        {"dryRun", dry_run},
        {"params", nlohmann::json::parse(params)},
    };
    if (expected_revision.has_value()) {
        document["expectedRevision"] = *expected_revision;
    }
    const std::string json = document.dump();
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
    struct created_object final {
        int handle = 0;
        OBJECT_LAYER_FRAME position{};
        std::string alias;
        std::wstring name;
    };

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
    OBJECT_LAYER_FRAME first_position{.layer = 1, .start = 9, .end = 19};
    OBJECT_LAYER_FRAME second_position{.layer = 3, .start = 20, .end = 29};
    bool is_first_deleted = false;
    bool is_second_deleted = false;
    int first_effect = 3;
    int second_effect = 4;
    int third_effect = 5;
    std::string first_alias = "[Object]\r\nfile=C:\\Media\\Voice.wav\r\n";
    std::string second_alias = "[Object]\r\ntext=hello\r\n";
    std::wstring first_name = L"Voice";
    std::wstring second_name = L"Caption";
    void (*project_load_handler)(PROJECT_FILE*) = nullptr;
    void (*project_save_handler)(PROJECT_FILE*) = nullptr;
    bool should_throw_edit_state = false;
    bool has_duplicate_effect_name = false;
    bool is_read_active = false;
    std::array<created_object, 8> created_objects{};
    std::size_t created_object_count = 0U;
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
    ACTIVE_FAKE_SDK->is_read_active = true;
    callback(parameter, &ACTIVE_FAKE_SDK->edit_section);
    ACTIVE_FAKE_SDK->is_read_active = false;
    return true;
}

[[nodiscard]] bool call_fake_edit_section(
    void* parameter,
    void (*callback)(void*, EDIT_SECTION*)) {
    ACTIVE_FAKE_SDK->is_read_active = true;
    callback(parameter, &ACTIVE_FAKE_SDK->edit_section);
    ACTIVE_FAKE_SDK->is_read_active = false;
    return true;
}

[[nodiscard]] fake_sdk_state::created_object* find_created_object(const OBJECT_HANDLE object) {
    for (std::size_t index = 0U; index < ACTIVE_FAKE_SDK->created_object_count; ++index) {
        fake_sdk_state::created_object& candidate = ACTIVE_FAKE_SDK->created_objects[index];
        if (object == &candidate.handle) {
            return &candidate;
        }
    }
    return nullptr;
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
        return ACTIVE_FAKE_SDK->first_position;
    }
    if (object == &ACTIVE_FAKE_SDK->second_object) {
        return ACTIVE_FAKE_SDK->second_position;
    }
    if (fake_sdk_state::created_object* created = find_created_object(object)) {
        return created->position;
    }
    return {.layer = -1, .start = -1, .end = -1};
}

[[nodiscard]] LPCWSTR get_fake_scene_name() {
    return ACTIVE_FAKE_SDK->scene_name.c_str();
}

[[nodiscard]] OBJECT_HANDLE find_fake_object(const int layer, const int frame) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake object handle was used outside a read callback");
    OBJECT_HANDLE result = nullptr;
    int result_start = (std::numeric_limits<int>::max)();
    const auto consider = [&](const OBJECT_HANDLE object, const OBJECT_LAYER_FRAME position) {
        if (position.layer == layer && position.end >= frame && position.start < result_start) {
            result = object;
            result_start = position.start;
        }
    };
    if (!ACTIVE_FAKE_SDK->is_first_deleted) {
        consider(&ACTIVE_FAKE_SDK->first_object, ACTIVE_FAKE_SDK->first_position);
    }
    if (!ACTIVE_FAKE_SDK->is_second_deleted) {
        consider(&ACTIVE_FAKE_SDK->second_object, ACTIVE_FAKE_SDK->second_position);
    }
    for (std::size_t index = 0U; index < ACTIVE_FAKE_SDK->created_object_count; ++index) {
        fake_sdk_state::created_object& created = ACTIVE_FAKE_SDK->created_objects[index];
        consider(&created.handle, created.position);
    }
    return result;
}

[[nodiscard]] LPCSTR get_fake_object_alias(const OBJECT_HANDLE object) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake alias was read outside a read callback");
    if (fake_sdk_state::created_object* created = find_created_object(object)) {
        return created->alias.c_str();
    }
    return object == &ACTIVE_FAKE_SDK->first_object ? ACTIVE_FAKE_SDK->first_alias.c_str()
        : object == &ACTIVE_FAKE_SDK->second_object ? ACTIVE_FAKE_SDK->second_alias.c_str()
        : nullptr;
}

[[nodiscard]] LPCWSTR get_fake_object_name(const OBJECT_HANDLE object) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake object name was read outside a read callback");
    if (fake_sdk_state::created_object* created = find_created_object(object)) {
        return created->name.empty() ? nullptr : created->name.c_str();
    }
    return object == &ACTIVE_FAKE_SDK->first_object ? ACTIVE_FAKE_SDK->first_name.c_str()
        : object == &ACTIVE_FAKE_SDK->second_object ? ACTIVE_FAKE_SDK->second_name.c_str()
        : nullptr;
}

[[nodiscard]] int get_fake_effect_list(
    const OBJECT_HANDLE object,
    EFFECT_HANDLE* effects,
    const int effect_count) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake effect list was read outside a read callback");
    const std::array first_effects{
        static_cast<EFFECT_HANDLE>(&ACTIVE_FAKE_SDK->first_effect),
        static_cast<EFFECT_HANDLE>(&ACTIVE_FAKE_SDK->second_effect),
    };
    const std::array second_effects{
        static_cast<EFFECT_HANDLE>(&ACTIVE_FAKE_SDK->third_effect),
    };
    const auto copy_effects = [effects, effect_count](const auto& source) {
        if (effects == nullptr) {
            return static_cast<int>(source.size());
        }
        const int copy_count = (std::min)(effect_count, static_cast<int>(source.size()));
        std::ranges::copy_n(source.begin(), copy_count, effects);
        return copy_count;
    };
    if (find_created_object(object) != nullptr) {
        return copy_effects(first_effects);
    }
    return object == &ACTIVE_FAKE_SDK->first_object ? copy_effects(first_effects)
        : object == &ACTIVE_FAKE_SDK->second_object ? copy_effects(second_effects)
        : 0;
}

[[nodiscard]] LPCWSTR get_fake_effect_name(const EFFECT_HANDLE effect) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake effect name was read outside a read callback");
    return effect == &ACTIVE_FAKE_SDK->first_effect ? L"Audio File"
        : effect == &ACTIVE_FAKE_SDK->second_effect ? L"Standard Playback"
        : effect == &ACTIVE_FAKE_SDK->third_effect ? L"Text"
        : nullptr;
}

[[nodiscard]] bool get_fake_effect_enable(const EFFECT_HANDLE effect) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake effect state was read outside a read callback");
    return effect != &ACTIVE_FAKE_SDK->second_effect;
}

[[nodiscard]] bool get_fake_effect_lock(const EFFECT_HANDLE effect) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake effect lock was read outside a read callback");
    return effect == &ACTIVE_FAKE_SDK->second_effect;
}

[[nodiscard]] bool enumerate_fake_effect_items(
    const LPCWSTR effect,
    void* parameter,
    void (*callback)(void*, LPCWSTR, int)) {
    if (effect == nullptr) {
        return false;
    }
    if (std::wstring_view(effect) == L"Audio File") {
        callback(parameter, L"File", EDIT_HANDLE::EFFECT_ITEM_TYPE_FILE);
        callback(parameter, L"Volume", EDIT_HANDLE::EFFECT_ITEM_TYPE_INTEGER);
        callback(parameter, L"Gain", EDIT_HANDLE::EFFECT_ITEM_TYPE_NUMBER);
        callback(parameter, L"Enabled", EDIT_HANDLE::EFFECT_ITEM_TYPE_CHECK);
        callback(parameter, L"Blob", EDIT_HANDLE::EFFECT_ITEM_TYPE_DATA);
    } else if (std::wstring_view(effect) == L"Text") {
        callback(parameter, L"Text", EDIT_HANDLE::EFFECT_ITEM_TYPE_TEXT);
        callback(parameter, L"Font", EDIT_HANDLE::EFFECT_ITEM_TYPE_FONT);
    }
    return true;
}

void enumerate_fake_effect_names(
    void* parameter,
    void (*callback)(void*, LPCWSTR, int, int)) {
    callback(
        parameter,
        L"Audio File",
        EDIT_HANDLE::EFFECT_TYPE_INPUT,
        EDIT_HANDLE::EFFECT_FLAG_AUDIO);
    callback(
        parameter,
        L"Standard Playback",
        EDIT_HANDLE::EFFECT_TYPE_OUTPUT,
        EDIT_HANDLE::EFFECT_FLAG_AUDIO);
    callback(
        parameter,
        L"Text",
        EDIT_HANDLE::EFFECT_TYPE_FILTER,
        EDIT_HANDLE::EFFECT_FLAG_VIDEO | EDIT_HANDLE::EFFECT_FLAG_FILTER);
    callback(
        parameter,
        L"Camera Control",
        EDIT_HANDLE::EFFECT_TYPE_CONTROL,
        EDIT_HANDLE::EFFECT_FLAG_CAMERA | 0x20);
    if (ACTIVE_FAKE_SDK->has_duplicate_effect_name) {
        callback(
            parameter,
            L"Text",
            EDIT_HANDLE::EFFECT_TYPE_FILTER,
            EDIT_HANDLE::EFFECT_FLAG_VIDEO);
    }
}

void enumerate_fake_modules(
    void* parameter,
    void (*callback)(void*, MODULE_INFO*)) {
    MODULE_INFO psd_toolkit{
        .type = MODULE_INFO::TYPE_PLUGIN_FILTER,
        .name = L"PSDToolKit2",
        .information = L"PSDToolKit 2.0.0alpha10",
    };
    MODULE_INFO script{
        .type = MODULE_INFO::TYPE_SCRIPT_MODULE,
        .name = L"PSDToolKit",
        .information = L"Animation scripts",
    };
    callback(parameter, &psd_toolkit);
    callback(parameter, &script);
}

void enumerate_fake_fonts(void* parameter, void (*callback)(void*, LPCWSTR)) {
    callback(parameter, L"Yu Gothic UI");
    callback(parameter, L"Noto Sans JP");
}

void enumerate_fake_palettes(void* parameter, void (*callback)(void*, LPCWSTR)) {
    callback(parameter, L"Default");
    callback(parameter, L"Vivid");
}

[[nodiscard]] LPCSTR get_fake_effect_item_value(
    const EFFECT_HANDLE effect,
    const LPCWSTR item) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake effect item value was read outside a read callback");
    if (item == nullptr) {
        return nullptr;
    }
    const std::wstring_view name(item);
    if (effect == &ACTIVE_FAKE_SDK->first_effect) {
        if (name == L"File") {
            return "C:\\Media\\Voice.wav";
        }
        if (name == L"Volume") {
            return "42";
        }
        if (name == L"Gain") {
            return "1.5";
        }
        if (name == L"Enabled") {
            return "1";
        }
        if (name == L"Blob") {
            return "opaque";
        }
    }
    if (effect == &ACTIVE_FAKE_SDK->third_effect && name == L"Text") {
        return "hello";
    }
    if (effect == &ACTIVE_FAKE_SDK->third_effect && name == L"Font") {
        return "Yu Gothic UI";
    }
    return nullptr;
}

[[nodiscard]] LPCWSTR get_fake_layer_name(const int layer) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake layer name was read outside a read callback");
    return layer == 1 ? L"Voice Layer" : nullptr;
}

[[nodiscard]] bool get_fake_layer_enable(const int layer) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake layer visibility was read outside a read callback");
    return layer != 3;
}

[[nodiscard]] bool get_fake_layer_lock(const int layer) {
    require(ACTIVE_FAKE_SDK->is_read_active, "fake layer lock was read outside a read callback");
    return layer == 3;
}

[[nodiscard]] fake_sdk_state::created_object* add_created_object(
    const int layer,
    const int frame,
    const int length,
    std::string alias) {
    require(ACTIVE_FAKE_SDK->created_object_count < ACTIVE_FAKE_SDK->created_objects.size(),
        "fake SDK created object capacity was exceeded");
    fake_sdk_state::created_object& object =
        ACTIVE_FAKE_SDK->created_objects[ACTIVE_FAKE_SDK->created_object_count++];
    object.handle = 100 + static_cast<int>(ACTIVE_FAKE_SDK->created_object_count);
    object.position = {.layer = layer, .start = frame, .end = frame + length - 1};
    object.alias = std::move(alias);
    object.name.clear();
    return &object;
}

[[nodiscard]] OBJECT_HANDLE create_fake_effect_object(
    const LPCWSTR effect,
    const int layer,
    const int frame,
    const int length) {
    if (effect == nullptr || std::wstring_view(effect) != L"Text") {
        return nullptr;
    }
    return &add_created_object(layer, frame, length, "[Object]\r\neffect.name=Text\r\n")->handle;
}

[[nodiscard]] bool is_fake_supported_media(const LPCWSTR file, const bool strict) {
    static_cast<void>(strict);
    return file != nullptr && std::wstring_view(file).ends_with(L".wav");
}

[[nodiscard]] OBJECT_HANDLE create_fake_media_object(
    const LPCWSTR file,
    const int layer,
    const int frame,
    const int length) {
    if (!is_fake_supported_media(file, true)) {
        return nullptr;
    }
    std::string alias = "[Object]\r\nfile=C:\\Media\\created.wav\r\n";
    return &add_created_object(layer, frame, length, std::move(alias))->handle;
}

[[nodiscard]] OBJECT_HANDLE create_fake_alias_object(
    const LPCSTR alias,
    const int layer,
    const int frame,
    const int length) {
    if (alias == nullptr) {
        return nullptr;
    }
    const std::string source(alias);
    std::size_t object_count = 0U;
    std::size_t offset = 0U;
    while ((offset = source.find("[Object]", offset)) != std::string::npos) {
        ++object_count;
        offset += 8U;
    }
    if (object_count == 0U) {
        return nullptr;
    }
    fake_sdk_state::created_object* first = nullptr;
    for (std::size_t index = 0U; index < object_count; ++index) {
        fake_sdk_state::created_object* created = add_created_object(
            layer + static_cast<int>(index), frame, length, source);
        if (first == nullptr) {
            first = created;
        }
    }
    return &first->handle;
}

void set_fake_object_name(const OBJECT_HANDLE object, const LPCWSTR name) {
    if (object == &ACTIVE_FAKE_SDK->first_object) {
        ACTIVE_FAKE_SDK->first_name = name == nullptr ? L"" : name;
        return;
    }
    if (object == &ACTIVE_FAKE_SDK->second_object) {
        ACTIVE_FAKE_SDK->second_name = name == nullptr ? L"" : name;
        return;
    }
    if (fake_sdk_state::created_object* created = find_created_object(object)) {
        created->name = name == nullptr ? L"" : name;
    }
}

[[nodiscard]] bool move_fake_object(
    const OBJECT_HANDLE object,
    const int layer,
    const int frame) {
    OBJECT_LAYER_FRAME* position = nullptr;
    if (object == &ACTIVE_FAKE_SDK->first_object && !ACTIVE_FAKE_SDK->is_first_deleted) {
        position = &ACTIVE_FAKE_SDK->first_position;
    } else if (object == &ACTIVE_FAKE_SDK->second_object && !ACTIVE_FAKE_SDK->is_second_deleted) {
        position = &ACTIVE_FAKE_SDK->second_position;
    } else if (fake_sdk_state::created_object* created = find_created_object(object)) {
        position = &created->position;
    }
    if (position == nullptr) {
        return false;
    }
    const int length = position->end - position->start + 1;
    *position = {.layer = layer, .start = frame, .end = frame + length - 1};
    return true;
}

void delete_fake_object(const OBJECT_HANDLE object) {
    if (object == &ACTIVE_FAKE_SDK->first_object) {
        ACTIVE_FAKE_SDK->is_first_deleted = true;
    } else if (object == &ACTIVE_FAKE_SDK->second_object) {
        ACTIVE_FAKE_SDK->is_second_deleted = true;
    }
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
    state.edit_handle.call_edit_section_param = &call_fake_edit_section;
    state.edit_handle.enum_effect_name = &enumerate_fake_effect_names;
    state.edit_handle.enum_module_info = &enumerate_fake_modules;
    state.edit_handle.enum_effect_item = &enumerate_fake_effect_items;
    state.edit_handle.enum_font_name = &enumerate_fake_fonts;
    state.edit_handle.enum_palette_name = &enumerate_fake_palettes;
    state.edit_section.info = &state.edit_info;
    state.edit_section.find_object = &find_fake_object;
    state.edit_section.get_object_alias = &get_fake_object_alias;
    state.edit_section.get_selected_object_num = &get_fake_selected_object_count;
    state.edit_section.get_selected_object = &get_fake_selected_object;
    state.edit_section.get_object_layer_frame = &get_fake_object_position;
    state.edit_section.get_object_name = &get_fake_object_name;
    state.edit_section.get_layer_name = &get_fake_layer_name;
    state.edit_section.get_layer_enable = &get_fake_layer_enable;
    state.edit_section.get_layer_lock = &get_fake_layer_lock;
    state.edit_section.get_scene_name = &get_fake_scene_name;
    state.edit_section.get_effect_list = &get_fake_effect_list;
    state.edit_section.get_effect_name = &get_fake_effect_name;
    state.edit_section.get_effect_enable = &get_fake_effect_enable;
    state.edit_section.get_effect_lock = &get_fake_effect_lock;
    state.edit_section.get_effect_item_value = &get_fake_effect_item_value;
    state.edit_section.create_object = &create_fake_effect_object;
    state.edit_section.is_support_media_file = &is_fake_supported_media;
    state.edit_section.create_object_from_media_file = &create_fake_media_object;
    state.edit_section.create_object_from_alias = &create_fake_alias_object;
    state.edit_section.set_object_name = &set_fake_object_name;
    state.edit_section.move_object = &move_fake_object;
    state.edit_section.delete_object = &delete_fake_object;
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

void test_project_load_resets_revision_generation() {
    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    aviutl2_mcp::named_pipe_server server(identity, "2003300");
    const std::string initial_generation = server.dispatcher().revisions().project_generation();
    aviutl2_mcp::get_sdk_read_facade().capture_project(nullptr, true);
    const std::string loaded_generation = server.dispatcher().revisions().project_generation();
    require(initial_generation != loaded_generation,
        "project load did not reset the revision generation");
    aviutl2_mcp::get_sdk_read_facade().capture_project(nullptr, false);
    require(server.dispatcher().revisions().project_generation() == loaded_generation,
        "project save unexpectedly reset the revision generation");
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

    aviutl2_mcp::sdk_timeline_query timeline_query{
        .offset = 0U,
        .limit = 1U,
        .include_effects = true,
        .use_display_defaults = true,
    };
    const aviutl2_mcp::sdk_timeline_query_result first_page = facade.query_timeline(timeline_query);
    require(first_page.ok && first_page.timeline.objects.size() == 1U
            && first_page.timeline.is_truncated && first_page.timeline.next_offset == 1U,
        "SDK facade did not page the timeline");
    const aviutl2_mcp::sdk_object_snapshot& voice = first_page.timeline.objects[0];
    require(voice.candidate.layer == 2 && voice.candidate.start_frame == 10
            && voice.candidate.end_frame == 20 && voice.candidate.name == "Voice",
        "SDK facade copied incorrect object coordinates or name");
    require(voice.is_selected && voice.media_path == "C:\\Media\\Voice.wav",
        "SDK facade did not copy selection or media path");
    require(voice.effects.size() == 2U
            && voice.effects[0].name == "Audio File"
            && voice.effects[1].occurrence == 0
            && !voice.effects[1].is_enabled
            && voice.effects[1].is_locked,
        "SDK facade did not copy ordered effect state");
    require(voice.candidate.effects.size() == 2U
            && voice.candidate.effects[0].items.size() == 5U,
        "SDK facade did not copy effect fingerprints inside the callback");

    timeline_query.offset = first_page.timeline.next_offset;
    const aviutl2_mcp::sdk_timeline_query_result second_page = facade.query_timeline(timeline_query);
    require(second_page.ok && second_page.timeline.objects.size() == 1U
            && !second_page.timeline.is_truncated
            && second_page.timeline.objects[0].candidate.name == "Caption",
        "SDK facade did not resume timeline paging");
    fake.second_name = L"Mutated after callback";
    require(second_page.timeline.objects[0].candidate.name == "Caption",
        "SDK facade retained a callback-owned object name pointer");

    aviutl2_mcp::sdk_timeline_query find_query{
        .name_contains = "Voice",
        .effect_name = "Audio File",
        .media_path = "c:/media/voice.wav",
        .limit = 100U,
        .include_effects = true,
        .use_display_defaults = false,
    };
    const aviutl2_mcp::sdk_timeline_query_result found = facade.query_timeline(find_query);
    require(found.ok && found.timeline.objects.size() == 1U
            && found.timeline.objects[0].candidate.name == "Voice",
        "SDK facade did not apply object search filters");

    const std::string object_instance_id = aviutl2_mcp::create_bridge_identity().instance_id;
    const std::string object_project_generation = aviutl2_mcp::create_bridge_identity().instance_id;
    const aviutl2_mcp::object_locator object_locator = aviutl2_mcp::create_object_locator(
        object_instance_id,
        object_project_generation,
        voice.candidate);
    const aviutl2_mcp::sdk_object_query_result object_detail = facade.query_object(
        object_locator,
        object_instance_id,
        object_project_generation,
        true,
        true);
    require(object_detail.ok && object_detail.detail.alias == fake.first_alias,
        "SDK facade did not return a resolved object alias");
    require(object_detail.detail.effect_items.size() == 2U
            && object_detail.detail.effect_items[0].items.size() == 5U,
        "SDK facade did not group object effect items");
    const auto& audio_items = object_detail.detail.effect_items[0].items;
    require(audio_items[0].type == "file" && audio_items[0].codec == "aliasString"
            && std::get<std::string>(*audio_items[0].value) == "C:\\Media\\Voice.wav"
            && !audio_items[0].is_writable,
        "SDK facade did not decode an alias string effect item");
    require(std::get<std::int64_t>(*audio_items[1].value) == 42
            && std::get<double>(*audio_items[2].value) == 1.5
            && std::get<bool>(*audio_items[3].value)
            && !audio_items[1].is_writable
            && !audio_items[2].is_writable
            && !audio_items[3].is_writable,
        "SDK facade did not decode integer, number, and check codecs");
    require(audio_items[4].type == "data" && audio_items[4].codec == "unsupported"
            && !audio_items[4].is_writable && !audio_items[4].value.has_value(),
        "SDK facade exposed an unsupported data item as writable");

    const aviutl2_mcp::sdk_object_query_result compact_object_detail = facade.query_object(
        object_locator,
        object_instance_id,
        object_project_generation,
        false,
        false);
    require(compact_object_detail.ok
            && !compact_object_detail.detail.alias.has_value()
            && compact_object_detail.detail.effect_items.empty(),
        "SDK facade ignored object detail inclusion flags");

    aviutl2_mcp::object_locator stale_locator = object_locator;
    stale_locator.name = "stale";
    require(facade.query_object(
                stale_locator,
                object_instance_id,
                object_project_generation,
                false,
                false).error_code == "object_not_found",
        "SDK facade accepted a stale object fingerprint");
    require(facade.query_object(
                object_locator,
                object_instance_id,
                aviutl2_mcp::create_bridge_identity().instance_id,
                false,
                false).error_code == "invalid_argument",
        "SDK facade accepted a locator from another project generation");

    aviutl2_mcp::sdk_effect_catalog_query effect_query{
        .offset = 0U,
        .limit = 2U,
    };
    const aviutl2_mcp::sdk_effect_catalog_query_result first_effect_page =
        facade.query_effects(effect_query);
    require(first_effect_page.ok && first_effect_page.catalog.effects.size() == 2U
            && first_effect_page.catalog.is_truncated
            && first_effect_page.catalog.next_offset == 2U,
        "SDK facade did not page effect definitions");
    require(first_effect_page.catalog.effects[0].name == "Audio File"
            && first_effect_page.catalog.effects[0].type == "input"
            && first_effect_page.catalog.effects[0].flags == std::vector<std::string>({"audio"})
            && first_effect_page.catalog.effects[0].is_creatable,
        "SDK facade did not map effect type, flags, or creatability");
    require(first_effect_page.catalog.modules.size() == 2U
            && first_effect_page.catalog.modules[0].type == "pluginFilter"
            && first_effect_page.catalog.modules[0].name == "PSDToolKit2"
            && first_effect_page.catalog.fonts.size() == 2U
            && first_effect_page.catalog.palettes.size() == 2U,
        "SDK facade did not copy module, font, and palette catalogs");

    effect_query.category = "filter";
    effect_query.name_contains = "Text";
    effect_query.limit = 100U;
    const aviutl2_mcp::sdk_effect_catalog_query_result filtered_effects =
        facade.query_effects(effect_query);
    require(filtered_effects.ok && filtered_effects.catalog.effects.size() == 1U
            && filtered_effects.catalog.effects[0].name == "Text"
            && filtered_effects.catalog.effects[0].is_creatable
            && filtered_effects.catalog.effects[0].flags
                == std::vector<std::string>({"video", "filter"}),
        "SDK facade did not filter or map filter effect definitions");

    const aviutl2_mcp::sdk_effect_items_query_result text_items =
        facade.query_effect_items("Text", true);
    require(text_items.ok && text_items.items.size() == 2U
            && text_items.items[0].name == "Text"
            && text_items.items[0].codec == "aliasString"
            && !text_items.items[0].is_writable
            && text_items.items[1].type == "font"
            && text_items.items[1].choices
                == std::vector<std::string>({"Yu Gothic UI", "Noto Sans JP"}),
        "SDK facade did not return effect item codecs and public font choices");
    const aviutl2_mcp::sdk_effect_items_query_result text_items_without_choices =
        facade.query_effect_items("Text", false);
    require(text_items_without_choices.ok
            && text_items_without_choices.items[1].choices.empty(),
        "SDK facade ignored includeChoices=false");
    require(facade.query_effect_items("Missing", true).error_code == "effect_not_found",
        "SDK facade returned items for an unknown effect");
    fake.has_duplicate_effect_name = true;
    require(facade.query_effect_items("Text", true).error_code == "effect_ambiguous",
        "SDK facade selected an ambiguous effect definition");
    fake.has_duplicate_effect_name = false;

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
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_timeline_request_handler>(
        identity,
        facade));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_find_objects_request_handler>(
        identity,
        facade));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_object_request_handler>(
        identity,
        facade));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_effect_list_request_handler>(facade));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_effect_items_request_handler>(facade));
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

    const nlohmann::json effects = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 61U),
            "effect.list",
            correlation_id,
            R"({"limit":2})"),
        identity.instance_id).get()));
    require(effects.at("ok").get<bool>()
            && effects.at("result").at("effects").size() == 2U
            && effects.at("result").at("nextCursor") == "effects:2"
            && effects.at("result").at("isTruncated").get<bool>(),
        "native effect handler did not return the first catalog page");
    require(effects.at("result").at("modules")[0].at("name") == "PSDToolKit2"
            && effects.at("result").at("fonts").size() == 2U
            && effects.at("result").at("palettes").size() == 2U,
        "native effect handler omitted independent SDK catalogs");

    const nlohmann::json second_effect_page = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 65U),
            "effect.list",
            correlation_id,
            R"({"cursor":"effects:2","limit":2})"),
        identity.instance_id).get()));
    require(second_effect_page.at("ok").get<bool>()
            && second_effect_page.at("result").at("effects").size() == 2U
            && second_effect_page.at("result").at("nextCursor").is_null()
            && !second_effect_page.at("result").at("isTruncated").get<bool>()
            && second_effect_page.at("result").at("effects")[1].at("flags")
                == nlohmann::json::array({"camera", "unknown"}),
        "native effect handler did not resume paging or preserve unknown flags");

    const nlohmann::json filtered_effects = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 62U),
            "effect.list",
            correlation_id,
            R"({"category":"filter","nameContains":"Text"})"),
        identity.instance_id).get()));
    require(filtered_effects.at("ok").get<bool>()
            && filtered_effects.at("result").at("effects").size() == 1U
            && filtered_effects.at("result").at("effects")[0].at("name") == "Text"
            && filtered_effects.at("result").at("effects")[0].at("isCreatable").get<bool>(),
        "native effect handler did not apply category and name filters");

    const nlohmann::json effect_items = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 63U),
            "effect.items.list",
            correlation_id,
            R"({"effect":{"name":"Text"},"includeChoices":true})"),
        identity.instance_id).get()));
    require(effect_items.at("ok").get<bool>()
            && effect_items.at("result").at("items").size() == 2U
            && effect_items.at("result").at("items")[1].at("type") == "font"
            && effect_items.at("result").at("items")[1].at("choices").size() == 2U
            && !effect_items.at("result").at("items")[1].at("isWritable").get<bool>(),
        "native effect item handler omitted codec or font choices");

    const nlohmann::json missing_effect_items = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 64U),
            "effect.items.list",
            correlation_id,
            R"({"effect":{"name":"Missing"}})"),
        identity.instance_id).get()));
    require(!missing_effect_items.at("ok").get<bool>()
            && missing_effect_items.at("error").at("code") == "effect_not_found",
        "native effect item handler accepted an unknown effect");

    const nlohmann::json invalid_effect_cursor = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 66U),
            "effect.list",
            correlation_id,
            R"({"cursor":"timeline:1"})"),
        identity.instance_id).get()));
    require(!invalid_effect_cursor.at("ok").get<bool>()
            && invalid_effect_cursor.at("error").at("code") == "invalid_argument",
        "native effect handler accepted a cursor from another query");

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

    const nlohmann::json timeline = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 55U),
            "timeline.get",
            correlation_id,
            R"({"detail":"effects","limit":1})"),
        identity.instance_id).get()));
    require(timeline.at("ok").get<bool>()
            && timeline.at("result").at("objects").size() == 1U
            && timeline.at("result").at("isTruncated").get<bool>()
            && timeline.at("result").at("nextCursor") == "timeline:1",
        "native timeline handler did not return the first page");
    const nlohmann::json& first_object = timeline.at("result").at("objects")[0];
    require(first_object.at("locator").at("instanceId") == identity.instance_id
            && first_object.at("locator").at("projectGeneration")
                == dispatcher.revisions().project_generation()
            && first_object.at("locator").at("aliasSha256").get<std::string>().size() == 64U
            && first_object.at("effects").size() == 2U,
        "native timeline handler returned an invalid locator or effect summary");

    const std::string object_params = nlohmann::json{
        {"locator", first_object.at("locator")},
        {"includeAlias", true},
        {"includeEffectItems", true},
    }.dump();
    const nlohmann::json object = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 59U),
            "object.get",
            correlation_id,
            object_params),
        identity.instance_id).get()));
    require(object.at("ok").get<bool>()
            && object.at("result").at("alias") == fake.first_alias
            && object.at("result").at("effectItems").size() == 2U,
        "native object handler did not return alias and grouped effect items");
    const nlohmann::json& item_values = object.at("result").at("effectItems")[0].at("items");
    require(item_values[1].at("value") == 42
            && item_values[2].at("value") == 1.5
            && item_values[3].at("value").get<bool>()
            && !item_values[4].contains("value")
            && !item_values[4].at("isWritable").get<bool>(),
        "native object handler did not serialize effect item codecs");

    nlohmann::json stale_object_params = nlohmann::json::parse(object_params);
    stale_object_params["locator"]["name"] = "stale";
    const nlohmann::json stale_object = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 60U),
            "object.get",
            correlation_id,
            stale_object_params.dump()),
        identity.instance_id).get()));
    require(!stale_object.at("ok").get<bool>()
            && stale_object.at("error").at("code") == "object_not_found",
        "native object handler accepted a stale locator");

    const nlohmann::json second_timeline_page = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 56U),
            "timeline.get",
            correlation_id,
            R"({"detail":"summary","limit":1,"cursor":"timeline:1"})"),
        identity.instance_id).get()));
    require(second_timeline_page.at("ok").get<bool>()
            && second_timeline_page.at("result").at("objects")[0].at("name") == "Caption"
            && second_timeline_page.at("result").at("objects")[0].at("effects").empty(),
        "native timeline handler did not resume or honor summary detail");

    const nlohmann::json found_objects = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 57U),
            "object.find",
            correlation_id,
            R"({"effectName":"Audio File","mediaPath":"c:/media/voice.wav"})"),
        identity.instance_id).get()));
    require(found_objects.at("ok").get<bool>()
            && found_objects.at("result").at("objects").size() == 1U
            && found_objects.at("result").at("objects")[0].at("name") == "Voice",
        "native find objects handler did not apply filters");

    const nlohmann::json invalid_cursor = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 58U),
            "object.find",
            correlation_id,
            R"({"cursor":"timeline:1"})"),
        identity.instance_id).get()));
    require(!invalid_cursor.at("ok").get<bool>()
            && invalid_cursor.at("error").at("code") == "invalid_argument",
        "native find objects handler accepted a foreign cursor");

    dispatcher.stop();
    facade.detach();
    ACTIVE_FAKE_SDK = nullptr;
}

void test_native_create_request_handlers() {
    fake_sdk_state fake;
    configure_fake_sdk(fake);
    aviutl2_mcp::sdk_read_facade facade;
    require(facade.register_host(&fake.host), "create handler fixture SDK registration failed");
    fake.project_load_handler(&fake.project_file);

    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    aviutl2_mcp::request_dispatcher dispatcher(identity);
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_create_request_handler>(
        identity, facade, "object.create", aviutl2_mcp::sdk_create_kind::effect));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_create_request_handler>(
        identity, facade, "object.createMedia", aviutl2_mcp::sdk_create_kind::media));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_create_request_handler>(
        identity, facade, "object.createAlias", aviutl2_mcp::sdk_create_kind::alias));
    const std::string correlation_id = aviutl2_mcp::create_bridge_identity().instance_id;
    const std::string initial_revision = dispatcher.revisions().content_revision();
    const std::string effect_params = nlohmann::json{
        {"effect", {{"name", "Text"}}},
        {"placement", {
            {"sceneId", 7},
            {"layer", 6},
            {"startFrame", 31},
            {"endFrame", 40},
        }},
        {"name", "Created text"},
    }.dump();

    const nlohmann::json dry_run = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 71U),
            "object.create",
            correlation_id,
            effect_params,
            initial_revision,
            true),
        identity.instance_id).get()));
    require(dry_run.at("ok").get<bool>()
            && dry_run.at("result").contains("plannedChanges")
            && !dry_run.at("result").contains("object")
            && fake.created_object_count == 0U
            && dispatcher.revisions().content_revision() == initial_revision,
        "native create dry-run changed SDK or revision state");

    const nlohmann::json created_effect = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 72U),
            "object.create",
            correlation_id,
            effect_params,
            initial_revision),
        identity.instance_id).get()));
    require(created_effect.at("ok").get<bool>()
            && created_effect.at("result").at("object").at("name") == "Created text"
            && created_effect.at("result").at("object").at("layer") == 6
            && created_effect.at("result").at("object").at("startFrame") == 31
            && created_effect.at("result").contains("appliedChanges")
            && fake.created_object_count == 1U
            && created_effect.at("revision") != initial_revision,
        "native effect create did not return the created object and new revision");

    const std::string second_revision = created_effect.at("revision").get<std::string>();
    const std::string media_params = nlohmann::json{
        {"mediaPath", "C:\\Media\\voice.wav"},
        {"placement", {
            {"sceneId", 7},
            {"layer", 7},
            {"startFrame", 31},
            {"durationFrames", 10},
        }},
    }.dump();
    const nlohmann::json stale_media = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 73U),
            "object.createMedia",
            correlation_id,
            media_params,
            initial_revision),
        identity.instance_id).get()));
    require(!stale_media.at("ok").get<bool>()
            && stale_media.at("error").at("code") == "revision_conflict"
            && fake.created_object_count == 1U,
        "native media create accepted a stale revision");

    const nlohmann::json created_media = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 74U),
            "object.createMedia",
            correlation_id,
            media_params,
            second_revision),
        identity.instance_id).get()));
    require(created_media.at("ok").get<bool>()
            && created_media.at("result").at("object").at("mediaPath")
                == "C:\\Media\\created.wav"
            && fake.created_object_count == 2U,
        "native media create did not validate and create the media object");

    const std::string third_revision = created_media.at("revision").get<std::string>();
    const std::string alias_params = nlohmann::json{
        {"alias", "[Object]\r\neffect.name=Text\r\n[Object]\r\neffect.name=Text\r\n"},
        {"placement", {
            {"sceneId", 7},
            {"layer", 8},
            {"startFrame", 41},
            {"durationFrames", 10},
        }},
    }.dump();
    const auto alias_request_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 75U);
    const aviutl2_mcp::ipc_frame alias_frame = create_request_frame(
        alias_request_id,
        "object.createAlias",
        correlation_id,
        alias_params,
        third_revision);
    const nlohmann::json created_alias = nlohmann::json::parse(get_json(dispatcher.dispatch(
        alias_frame,
        identity.instance_id).get()));
    require(created_alias.at("ok").get<bool>()
            && created_alias.at("result").at("objects").size() == 2U
            && fake.created_object_count == 4U,
        "native alias create did not return every created object");
    const std::size_t count_after_alias = fake.created_object_count;
    const nlohmann::json replayed_alias = nlohmann::json::parse(get_json(dispatcher.dispatch(
        alias_frame,
        identity.instance_id).get()));
    require(replayed_alias == created_alias && fake.created_object_count == count_after_alias,
        "native alias create did not preserve at-most-once behavior");

    const std::string collision_params = nlohmann::json{
        {"effect", {{"name", "Text"}}},
        {"placement", {
            {"sceneId", 7},
            {"layer", 6},
            {"startFrame", 35},
            {"durationFrames", 2},
        }},
    }.dump();
    const nlohmann::json collision = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 76U),
            "object.create",
            correlation_id,
            collision_params,
            created_alias.at("revision").get<std::string>(),
            true),
        identity.instance_id).get()));
    require(!collision.at("ok").get<bool>()
            && collision.at("error").at("code") == "object_collision",
        "native create preflight missed an object collision");

    const std::string partial_params = nlohmann::json{
        {"effect", {{"name", "Text"}}},
        {"placement", {
            {"sceneId", 7},
            {"layer", 10},
            {"startFrame", 51},
            {"durationFrames", 2},
        }},
    }.dump();
    fake.edit_section.get_effect_list = nullptr;
    const std::string revision_before_partial = dispatcher.revisions().content_revision();
    const nlohmann::json partial = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 77U),
            "object.create",
            correlation_id,
            partial_params,
            revision_before_partial),
        identity.instance_id).get()));
    require(!partial.at("ok").get<bool>()
            && partial.at("error").at("code") == "partial_operation"
            && partial.at("error").at("outcome") == "partial"
            && partial.at("error").at("undoRecommended").get<bool>()
            && partial.at("revision") != revision_before_partial,
        "native create did not classify a failed postcondition after mutation as partial");

    dispatcher.stop();
    facade.detach();
    ACTIVE_FAKE_SDK = nullptr;
}

void test_native_object_edit_request_handlers() {
    fake_sdk_state fake;
    configure_fake_sdk(fake);
    aviutl2_mcp::sdk_read_facade facade;
    require(facade.register_host(&fake.host), "object edit fixture SDK registration failed");
    fake.project_load_handler(&fake.project_file);

    const aviutl2_mcp::bridge_identity identity = aviutl2_mcp::create_bridge_identity();
    aviutl2_mcp::request_dispatcher dispatcher(identity);
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_object_edit_request_handler>(
        identity, facade, "object.move", aviutl2_mcp::sdk_object_edit_kind::move));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_object_edit_request_handler>(
        identity, facade, "object.delete", aviutl2_mcp::sdk_object_edit_kind::delete_object));
    dispatcher.register_handler(std::make_unique<aviutl2_mcp::native_object_edit_request_handler>(
        identity, facade, "object.setName", aviutl2_mcp::sdk_object_edit_kind::set_name));
    const std::string project_generation = dispatcher.revisions().project_generation();
    const std::string correlation_id = aviutl2_mcp::create_bridge_identity().instance_id;
    const aviutl2_mcp::sdk_timeline_query_result timeline = facade.query_timeline(
        aviutl2_mcp::sdk_timeline_query{
            .limit = 100U,
            .include_effects = true,
            .use_display_defaults = false,
        });
    require(timeline.ok && timeline.timeline.objects.size() == 2U,
        "object edit fixture timeline was unavailable");
    const auto serialize_test_locator = [](const aviutl2_mcp::object_locator& locator) {
        return nlohmann::json{
            {"instanceId", locator.instance_id},
            {"projectGeneration", locator.project_generation},
            {"sceneId", locator.scene_id},
            {"layer", locator.layer},
            {"startFrame", locator.start_frame},
            {"endFrame", locator.end_frame},
            {"name", locator.name},
            {"aliasSha256", locator.alias_sha256},
            {"effectSignatureSha256", locator.effect_signature_sha256},
        };
    };
    const aviutl2_mcp::object_locator first_locator = aviutl2_mcp::create_object_locator(
        identity.instance_id, project_generation, timeline.timeline.objects[0].candidate);
    const aviutl2_mcp::object_locator second_locator = aviutl2_mcp::create_object_locator(
        identity.instance_id, project_generation, timeline.timeline.objects[1].candidate);
    const std::string initial_revision = dispatcher.revisions().content_revision();
    const std::string move_params = nlohmann::json{
        {"locator", serialize_test_locator(first_locator)},
        {"placement", {{"sceneId", 7}, {"layer", 3}, {"startFrame", 31}}},
    }.dump();

    const nlohmann::json dry_move = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 81U),
            "object.move",
            correlation_id,
            move_params,
            initial_revision,
            true),
        identity.instance_id).get()));
    require(dry_move.at("ok").get<bool>()
            && dry_move.at("result").contains("plannedChanges")
            && fake.first_position.layer == 1
            && dispatcher.revisions().content_revision() == initial_revision,
        "native move dry-run changed the object or revision");

    const nlohmann::json moved = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 82U),
            "object.move",
            correlation_id,
            move_params,
            initial_revision),
        identity.instance_id).get()));
    require(moved.at("ok").get<bool>()
            && moved.at("result").at("object").at("layer") == 3
            && moved.at("result").at("object").at("startFrame") == 31
            && fake.first_position.layer == 2
            && fake.first_position.start == 30,
        "native object move did not preserve length at the destination");

    const nlohmann::json moved_locator = moved.at("result").at("object").at("locator");
    const std::string moved_revision = moved.at("revision").get<std::string>();
    static_cast<void>(add_created_object(
        4, 20, 10, "[Object]\r\neffect.name=Text\r\n"));
    const std::string collision_params = nlohmann::json{
        {"locator", moved_locator},
        {"placement", {{"sceneId", 7}, {"layer", 5}, {"startFrame", 21}}},
    }.dump();
    const nlohmann::json collision = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 83U),
            "object.move",
            correlation_id,
            collision_params,
            moved_revision,
            true),
        identity.instance_id).get()));
    require(!collision.at("ok").get<bool>()
            && collision.at("error").at("code") == "object_collision",
        "native object move preflight missed a destination collision");

    const std::string locked_delete_params = nlohmann::json{
        {"locator", serialize_test_locator(second_locator)},
    }.dump();
    const nlohmann::json locked_delete = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 84U),
            "object.delete",
            correlation_id,
            locked_delete_params,
            moved_revision,
            true),
        identity.instance_id).get()));
    require(!locked_delete.at("ok").get<bool>()
            && locked_delete.at("error").at("code") == "edit_not_available",
        "native object delete accepted a locked source layer");

    const std::string name_params = nlohmann::json{
        {"locator", moved_locator},
        {"name", "Renamed voice"},
    }.dump();
    const nlohmann::json renamed = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 85U),
            "object.setName",
            correlation_id,
            name_params,
            moved_revision),
        identity.instance_id).get()));
    require(renamed.at("ok").get<bool>()
            && renamed.at("result").at("object").at("name") == "Renamed voice"
            && fake.first_name == L"Renamed voice",
        "native object naming did not return the updated fingerprint");

    const std::string renamed_revision = renamed.at("revision").get<std::string>();
    const nlohmann::json renamed_locator = renamed.at("result").at("object").at("locator");
    const std::string noop_name_params = nlohmann::json{
        {"locator", renamed_locator},
        {"name", "Renamed voice"},
    }.dump();
    const nlohmann::json noop_name = nlohmann::json::parse(get_json(dispatcher.dispatch(
        create_request_frame(
            create_uuid_v7_bytes(std::chrono::system_clock::now(), 86U),
            "object.setName",
            correlation_id,
            noop_name_params,
            renamed_revision),
        identity.instance_id).get()));
    require(noop_name.at("ok").get<bool>()
            && noop_name.at("revision") == renamed_revision,
        "native no-op object naming advanced the content revision");

    const std::string delete_params = nlohmann::json{{"locator", renamed_locator}}.dump();
    const auto delete_request_id = create_uuid_v7_bytes(std::chrono::system_clock::now(), 87U);
    const aviutl2_mcp::ipc_frame delete_frame = create_request_frame(
        delete_request_id,
        "object.delete",
        correlation_id,
        delete_params,
        renamed_revision);
    const nlohmann::json deleted = nlohmann::json::parse(get_json(dispatcher.dispatch(
        delete_frame,
        identity.instance_id).get()));
    require(deleted.at("ok").get<bool>()
            && deleted.at("result").at("deleted").get<bool>()
            && deleted.at("result").at("object").at("name") == "Renamed voice"
            && fake.is_first_deleted,
        "native object delete omitted the deletion pre-state");
    const nlohmann::json replayed_delete = nlohmann::json::parse(get_json(dispatcher.dispatch(
        delete_frame,
        identity.instance_id).get()));
    require(replayed_delete == deleted && fake.is_first_deleted,
        "native object delete did not preserve at-most-once behavior");

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
        std::pair{"project load revision reset", &test_project_load_resets_revision_generation},
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
        std::pair{"native create request handlers", &test_native_create_request_handlers},
        std::pair{"native object edit request handlers", &test_native_object_edit_request_handlers},
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

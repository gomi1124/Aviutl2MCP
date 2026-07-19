#include "aviutl2_mcp/named_pipe_server.h"
#include "aviutl2_mcp/native_batch_request_handler.h"
#include "aviutl2_mcp/native_capabilities_request_handler.h"
#include "aviutl2_mcp/native_create_request_handler.h"
#include "aviutl2_mcp/native_effect_edit_request_handler.h"
#include "aviutl2_mcp/native_effect_request_handlers.h"
#include "aviutl2_mcp/native_layer_view_request_handlers.h"
#include "aviutl2_mcp/native_project_request_handler.h"
#include "aviutl2_mcp/native_ring_logger.h"
#include "aviutl2_mcp/native_log_request_handler.h"
#include "aviutl2_mcp/native_object_request_handler.h"
#include "aviutl2_mcp/native_object_edit_request_handler.h"
#include "aviutl2_mcp/native_status_request_handler.h"
#include "aviutl2_mcp/native_timeline_request_handlers.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include "aviutl2_mcp/handshake.h"
#include "aviutl2_mcp/native_ipc_frame_codec.h"
#include "aviutl2_mcp/pipe_security.h"

#include <algorithm>
#include <array>
#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <system_error>
#include <utility>
#include <vector>

namespace aviutl2_mcp {

struct project_load_callback_state final {
    std::mutex mutex;
    revision_tracker* revisions = nullptr;
};

namespace {

constexpr DWORD PIPE_BUFFER_BYTES = 64U * 1024U;

[[noreturn]] void throw_last_error(const char* message) {
    throw std::system_error(
        static_cast<int>(GetLastError()),
        std::system_category(),
        message);
}

class event_handle final {
public:
    event_handle() {
        handle_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (handle_ == nullptr) {
            throw_last_error("CreateEventW failed");
        }
    }

    ~event_handle() {
        if (handle_ != nullptr) {
            CloseHandle(handle_);
        }
    }

    event_handle(const event_handle&) = delete;
    event_handle& operator=(const event_handle&) = delete;

    [[nodiscard]] HANDLE get() const noexcept {
        return handle_;
    }

private:
    HANDLE handle_ = nullptr;
};

class named_pipe_transport final : public byte_transport {
public:
    named_pipe_transport(const HANDLE pipe, const HANDLE stop_event)
        : pipe_(pipe), stop_event_(stop_event) {}

    [[nodiscard]] std::size_t read_some(const std::span<std::uint8_t> buffer) override {
        return perform_io(buffer.data(), buffer.size(), true);
    }

    [[nodiscard]] std::size_t write_some(const std::span<const std::uint8_t> buffer) override {
        return perform_io(const_cast<std::uint8_t*>(buffer.data()), buffer.size(), false);
    }

private:
    [[nodiscard]] std::size_t perform_io(void* data, const std::size_t size, const bool is_read) const {
        if (size == 0U) {
            return 0U;
        }
        event_handle completion;
        OVERLAPPED overlapped{};
        overlapped.hEvent = completion.get();
        DWORD transferred = 0U;
        const DWORD requested = static_cast<DWORD>((std::min)(size, static_cast<std::size_t>(MAXDWORD)));
        const BOOL started = is_read
            ? ReadFile(pipe_, data, requested, &transferred, &overlapped)
            : WriteFile(pipe_, data, requested, &transferred, &overlapped);
        if (started != FALSE) {
            return transferred;
        }

        const DWORD error = GetLastError();
        if (error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED) {
            return 0U;
        }
        if (error != ERROR_IO_PENDING) {
            throw_last_error(is_read ? "ReadFile failed" : "WriteFile failed");
        }

        const std::array<HANDLE, 2> events{stop_event_, completion.get()};
        const DWORD wait = WaitForMultipleObjects(static_cast<DWORD>(events.size()), events.data(), FALSE, INFINITE);
        if (wait == WAIT_OBJECT_0) {
            CancelIoEx(pipe_, &overlapped);
            WaitForSingleObject(completion.get(), INFINITE);
            throw std::runtime_error("named pipe operation was cancelled");
        }
        if (wait != WAIT_OBJECT_0 + 1U) {
            throw_last_error("WaitForMultipleObjects failed");
        }
        if (GetOverlappedResult(pipe_, &overlapped, &transferred, FALSE) == FALSE) {
            const DWORD completion_error = GetLastError();
            if (completion_error == ERROR_BROKEN_PIPE || completion_error == ERROR_PIPE_NOT_CONNECTED) {
                return 0U;
            }
            throw_last_error(is_read ? "overlapped pipe read failed" : "overlapped pipe write failed");
        }
        return transferred;
    }

    HANDLE pipe_;
    HANDLE stop_event_;
};

class session_writer final {
public:
    session_writer(byte_transport& transport, const HANDLE pipe)
        : transport_(transport),
          pipe_(pipe),
          worker_(&session_writer::run, this) {}

    ~session_writer() {
        close();
    }

    session_writer(const session_writer&) = delete;
    session_writer& operator=(const session_writer&) = delete;

    void enqueue(ipc_frame frame) {
        {
            std::scoped_lock lock(mutex_);
            if (is_closed_) {
                return;
            }
            queue_.push_back(std::move(frame));
        }
        condition_.notify_one();
    }

    void close() noexcept {
        bool should_cancel = false;
        {
            std::scoped_lock lock(mutex_);
            if (!is_closed_) {
                is_closed_ = true;
                queue_.clear();
                should_cancel = true;
            }
        }
        if (should_cancel) {
            CancelIoEx(pipe_, nullptr);
            condition_.notify_all();
        }
        if (worker_.joinable()) {
            worker_.join();
        }
    }

private:
    void run() noexcept {
        while (true) {
            ipc_frame frame;
            {
                std::unique_lock lock(mutex_);
                condition_.wait(lock, [this] { return is_closed_ || !queue_.empty(); });
                if (is_closed_) {
                    return;
                }
                frame = std::move(queue_.front());
                queue_.pop_front();
            }
            try {
                write_frame(transport_, frame);
            } catch (const std::exception& exception) {
                get_native_logger().write(
                    native_log_level::error,
                    "pipe",
                    "session.write_failed",
                    exception.what());
                std::scoped_lock lock(mutex_);
                is_closed_ = true;
                queue_.clear();
                return;
            }
        }
    }

    byte_transport& transport_;
    HANDLE pipe_;
    std::mutex mutex_;
    std::condition_variable condition_;
    std::deque<ipc_frame> queue_;
    bool is_closed_ = false;
    std::thread worker_;
};

class session_writer_guard final {
public:
    explicit session_writer_guard(std::shared_ptr<session_writer> writer)
        : writer_(std::move(writer)) {}

    ~session_writer_guard() {
        writer_->close();
    }

private:
    std::shared_ptr<session_writer> writer_;
};

[[nodiscard]] std::vector<std::uint8_t> to_bytes(const std::string& value) {
    return {value.begin(), value.end()};
}

}  // namespace

named_pipe_server::named_pipe_server(bridge_identity identity, std::string host_version)
    : identity_(std::move(identity)),
      host_version_(std::move(host_version)),
      dispatcher_(identity_) {
    dispatcher_.register_handler(std::make_unique<native_log_request_handler>());
    dispatcher_.register_handler(std::make_unique<native_status_request_handler>(
        identity_,
        host_version_,
        get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_capabilities_request_handler>(
        host_version_,
        get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_project_request_handler>(get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_timeline_request_handler>(
        identity_,
        get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_find_objects_request_handler>(
        identity_,
        get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_object_request_handler>(
        identity_,
        get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_effect_list_request_handler>(
        get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_effect_items_request_handler>(
        get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_create_request_handler>(
        identity_, get_sdk_read_facade(), "object.create", sdk_create_kind::effect));
    dispatcher_.register_handler(std::make_unique<native_create_request_handler>(
        identity_, get_sdk_read_facade(), "object.createMedia", sdk_create_kind::media));
    dispatcher_.register_handler(std::make_unique<native_create_request_handler>(
        identity_, get_sdk_read_facade(), "object.createAlias", sdk_create_kind::alias));
    dispatcher_.register_handler(std::make_unique<native_object_edit_request_handler>(
        identity_, get_sdk_read_facade(), "object.move", sdk_object_edit_kind::move));
    dispatcher_.register_handler(std::make_unique<native_object_edit_request_handler>(
        identity_, get_sdk_read_facade(), "object.delete", sdk_object_edit_kind::delete_object));
    dispatcher_.register_handler(std::make_unique<native_object_edit_request_handler>(
        identity_, get_sdk_read_facade(), "object.setName", sdk_object_edit_kind::set_name));
    dispatcher_.register_handler(std::make_unique<native_effect_edit_request_handler>(
        identity_, get_sdk_read_facade(), "effect.setItem", sdk_effect_edit_kind::set_item));
    dispatcher_.register_handler(std::make_unique<native_effect_edit_request_handler>(
        identity_, get_sdk_read_facade(), "effect.setState", sdk_effect_edit_kind::set_state));
    dispatcher_.register_handler(std::make_unique<native_layer_request_handler>(get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_view_request_handler>(get_sdk_read_facade()));
    dispatcher_.register_handler(std::make_unique<native_batch_request_handler>(
        identity_, get_sdk_read_facade()));
    project_load_callback_state_ = std::make_shared<project_load_callback_state>();
    project_load_callback_state_->revisions = &dispatcher_.revisions();
    const std::weak_ptr<project_load_callback_state> callback_state(project_load_callback_state_);
    get_sdk_read_facade().set_project_loaded_callback([callback_state] {
        const std::shared_ptr<project_load_callback_state> state = callback_state.lock();
        if (state == nullptr) {
            return;
        }
        std::scoped_lock lock(state->mutex);
        if (state->revisions != nullptr) {
            state->revisions->reset_project(create_bridge_identity().instance_id);
        }
    });
}

named_pipe_server::~named_pipe_server() {
    get_sdk_read_facade().clear_project_loaded_callback();
    if (project_load_callback_state_ != nullptr) {
        std::scoped_lock lock(project_load_callback_state_->mutex);
        project_load_callback_state_->revisions = nullptr;
    }
    stop();
}

void named_pipe_server::start() {
    if (is_running_.load() || worker_.joinable()) {
        throw std::logic_error("named pipe server is already running");
    }
    stop_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (stop_event_ == nullptr) {
        throw_last_error("CreateEventW failed for server stop event");
    }
    try {
        const HANDLE pipe = create_pipe();
        active_pipe_.store(pipe);
        is_running_.store(true);
        worker_ = std::thread(&named_pipe_server::run, this, pipe);
    } catch (...) {
        is_running_.store(false);
        const HANDLE pipe = active_pipe_.exchange(nullptr);
        if (pipe != nullptr && pipe != INVALID_HANDLE_VALUE) {
            CloseHandle(pipe);
        }
        CloseHandle(stop_event_);
        stop_event_ = nullptr;
        throw;
    }
}

void named_pipe_server::stop() noexcept {
    const bool was_running = is_running_.exchange(false);
    if (!was_running && !worker_.joinable()) {
        return;
    }
    if (stop_event_ != nullptr) {
        SetEvent(stop_event_);
    }
    const HANDLE pipe = active_pipe_.load();
    if (pipe != nullptr && pipe != INVALID_HANDLE_VALUE) {
        CancelIoEx(pipe, nullptr);
    }
    if (worker_.joinable()) {
        worker_.join();
    }
    dispatcher_.stop();
    active_pipe_.store(nullptr);
    if (stop_event_ != nullptr) {
        CloseHandle(stop_event_);
        stop_event_ = nullptr;
    }
}

bool named_pipe_server::is_running() const noexcept {
    return is_running_.load();
}

std::optional<pipe_session_diagnostics> named_pipe_server::last_session() const {
    std::scoped_lock lock(diagnostics_mutex_);
    return last_session_;
}

request_dispatcher& named_pipe_server::dispatcher() noexcept {
    return dispatcher_;
}

HANDLE named_pipe_server::create_pipe() const {
    user_only_security security;
    const std::wstring pipe_path = L"\\\\.\\pipe\\" + std::wstring(identity_.pipe_name.begin(), identity_.pipe_name.end());
    const HANDLE pipe = CreateNamedPipeW(
        pipe_path.c_str(),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1U,
        PIPE_BUFFER_BYTES,
        PIPE_BUFFER_BYTES,
        0U,
        security.attributes());
    if (pipe == INVALID_HANDLE_VALUE) {
        throw_last_error("CreateNamedPipeW failed");
    }
    return pipe;
}

void named_pipe_server::run(HANDLE pipe) noexcept {
    while (pipe != INVALID_HANDLE_VALUE) {
        active_pipe_.store(pipe);
        try {
            if (connect_client(pipe)) {
                serve_client(pipe);
            }
        } catch (const std::exception& exception) {
            get_native_logger().write(
                native_log_level::error,
                "pipe",
                "session.failed",
                exception.what());
        }
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
        active_pipe_.store(nullptr);

        if (!is_running_.load() || WaitForSingleObject(stop_event_, 0U) == WAIT_OBJECT_0) {
            break;
        }
        try {
            pipe = create_pipe();
        } catch (const std::exception& exception) {
            get_native_logger().write(
                native_log_level::error,
                "pipe",
                "listener.recreate_failed",
                exception.what());
            break;
        }
    }
    is_running_.store(false);
}

bool named_pipe_server::connect_client(const HANDLE pipe) const {
    event_handle completion;
    OVERLAPPED overlapped{};
    overlapped.hEvent = completion.get();
    if (ConnectNamedPipe(pipe, &overlapped) != FALSE) {
        return true;
    }
    const DWORD error = GetLastError();
    if (error == ERROR_PIPE_CONNECTED) {
        return true;
    }
    if (error != ERROR_IO_PENDING) {
        throw_last_error("ConnectNamedPipe failed");
    }

    const std::array<HANDLE, 2> events{stop_event_, completion.get()};
    const DWORD wait = WaitForMultipleObjects(static_cast<DWORD>(events.size()), events.data(), FALSE, INFINITE);
    if (wait == WAIT_OBJECT_0) {
        CancelIoEx(pipe, &overlapped);
        WaitForSingleObject(completion.get(), INFINITE);
        return false;
    }
    if (wait != WAIT_OBJECT_0 + 1U) {
        throw_last_error("WaitForMultipleObjects failed while accepting a client");
    }
    DWORD transferred = 0U;
    if (GetOverlappedResult(pipe, &overlapped, &transferred, FALSE) == FALSE) {
        throw_last_error("overlapped ConnectNamedPipe failed");
    }
    return true;
}

void named_pipe_server::serve_client(const HANDLE pipe) {
    ULONG client_process_id = 0U;
    ULONG client_session_id = 0U;
    if (GetNamedPipeClientProcessId(pipe, &client_process_id) == FALSE
        || GetNamedPipeClientSessionId(pipe, &client_session_id) == FALSE) {
        throw_last_error("named pipe client identity query failed");
    }

    named_pipe_transport transport(pipe, stop_event_);
    ipc_frame request = read_frame(transport);
    if (request.header.kind != message_kind::client_hello
        || request.header.flags != frame_flags::none
        || request.json.empty()
        || !request.binary.empty()) {
        set_last_session({client_process_id, client_session_id, false});
        throw std::invalid_argument("first IPC frame was not a valid ClientHello");
    }

    const client_hello hello = parse_client_hello(request.json);
    const handshake_handler handler(identity_, host_version_);
    const handshake_result result = handler.negotiate(hello, client_process_id);
    const std::vector<std::uint8_t> response_json = to_bytes(handler.create_server_hello_json(hello, result));
    ipc_frame response{
        .header = frame_header{
            .kind = message_kind::server_hello,
            .flags = frame_flags::none,
            .request_id = request.header.request_id,
            .json_length = static_cast<std::uint32_t>(response_json.size()),
            .binary_length = 0U,
        },
        .json = response_json,
        .binary = {},
        .payload_hash = {},
    };
    response.payload_hash = calculate_payload_hash(response.header, response.json, response.binary);
    write_frame(transport, response);
    set_last_session({client_process_id, client_session_id, result.accepted});
    get_native_logger().write(
        result.accepted ? native_log_level::information : native_log_level::warning,
        "pipe",
        result.accepted ? "handshake.accepted" : "handshake.rejected",
        "clientProcessId=" + std::to_string(client_process_id)
            + " clientSessionId=" + std::to_string(client_session_id)
            + " clientInstanceId=" + hello.client_instance_id,
        native_log_context{
            .instance_id = identity_.instance_id,
            .result_code = result.accepted ? std::string_view("accepted") : std::string_view("rejected"),
        });
    if (!result.accepted) {
        return;
    }

    auto writer = std::make_shared<session_writer>(transport, pipe);
    session_writer_guard writer_guard(writer);
    auto in_flight = std::make_shared<std::atomic<std::uint32_t>>(0U);
    while (is_running_.load()) {
        ipc_frame frame = read_frame(transport);
        if (frame.header.kind == message_kind::close) {
            get_native_logger().write(
                native_log_level::information,
                "pipe",
                "session.closed",
                "clientInstanceId=" + hello.client_instance_id,
                native_log_context{
                    .instance_id = identity_.instance_id,
                    .result_code = "closed",
                });
            return;
        }
        if (frame.header.kind == message_kind::request) {
            const std::uint32_t previous = in_flight->fetch_add(1U);
            if (previous >= result.limits.in_flight) {
                in_flight->fetch_sub(1U);
                writer->enqueue(dispatcher_.reject_busy(frame));
                continue;
            }
            try {
                dispatcher_.dispatch_async(
                    std::move(frame),
                    hello.client_instance_id,
                    [writer, in_flight](ipc_frame completed) {
                        in_flight->fetch_sub(1U);
                        writer->enqueue(std::move(completed));
                    });
            } catch (...) {
                in_flight->fetch_sub(1U);
                throw;
            }
            continue;
        }
        if (frame.header.kind == message_kind::cancel) {
            writer->enqueue(dispatcher_.cancel(frame));
            continue;
        }
        if (frame.header.kind == message_kind::ping
            && frame.header.flags == frame_flags::none
            && frame.binary.empty()) {
            frame.header.kind = message_kind::pong;
            frame.payload_hash = calculate_payload_hash(frame.header, frame.json, frame.binary);
            writer->enqueue(std::move(frame));
            continue;
        }
        throw std::invalid_argument("unsupported frame received in established session");
    }
}

void named_pipe_server::set_last_session(const pipe_session_diagnostics diagnostics) {
    std::scoped_lock lock(diagnostics_mutex_);
    last_session_ = diagnostics;
}

}  // namespace aviutl2_mcp

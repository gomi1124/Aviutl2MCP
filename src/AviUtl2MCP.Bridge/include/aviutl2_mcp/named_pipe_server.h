#pragma once

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/request_dispatcher.h"

#include <Windows.h>

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

namespace aviutl2_mcp {

inline constexpr std::uint32_t MAXIMUM_BRIDGE_CONNECTIONS = 8U;

struct project_load_callback_state;

struct pipe_session_diagnostics final {
    std::uint32_t client_process_id;
    std::uint32_t client_session_id;
    bool handshake_accepted;
};

class named_pipe_server final {
public:
    named_pipe_server(bridge_identity identity, std::string host_version);
    ~named_pipe_server();

    named_pipe_server(const named_pipe_server&) = delete;
    named_pipe_server& operator=(const named_pipe_server&) = delete;

    void start();
    void stop() noexcept;

    [[nodiscard]] bool is_running() const noexcept;
    [[nodiscard]] std::optional<pipe_session_diagnostics> last_session() const;
    [[nodiscard]] request_dispatcher& dispatcher() noexcept;

private:
    [[nodiscard]] HANDLE create_pipe(bool is_first_instance) const;
    void run(HANDLE initial_pipe) noexcept;
    [[nodiscard]] bool connect_client(HANDLE pipe) const;
    void serve_client(HANDLE pipe);
    void register_pipe(HANDLE pipe);
    void close_pipe(HANDLE pipe) noexcept;
    void set_last_session(pipe_session_diagnostics diagnostics);

    bridge_identity identity_;
    std::string host_version_;
    request_dispatcher dispatcher_;
    std::shared_ptr<project_load_callback_state> project_load_callback_state_;
    HANDLE stop_event_ = nullptr;
    std::atomic<bool> is_running_ = false;
    std::atomic<std::uint32_t> active_listener_count_ = 0U;
    std::mutex pipes_mutex_;
    std::vector<HANDLE> active_pipes_;
    std::vector<std::thread> workers_;
    mutable std::mutex diagnostics_mutex_;
    std::optional<pipe_session_diagnostics> last_session_;
};

}  // namespace aviutl2_mcp

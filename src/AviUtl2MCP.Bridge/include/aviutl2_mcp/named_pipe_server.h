#pragma once

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/request_dispatcher.h"

#include <Windows.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

namespace aviutl2_mcp {

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
    [[nodiscard]] HANDLE create_pipe() const;
    void run(HANDLE initial_pipe) noexcept;
    [[nodiscard]] bool connect_client(HANDLE pipe) const;
    void serve_client(HANDLE pipe);
    void set_last_session(pipe_session_diagnostics diagnostics);

    bridge_identity identity_;
    std::string host_version_;
    request_dispatcher dispatcher_;
    HANDLE stop_event_ = nullptr;
    std::atomic<HANDLE> active_pipe_ = nullptr;
    std::atomic<bool> is_running_ = false;
    std::thread worker_;
    mutable std::mutex diagnostics_mutex_;
    std::optional<pipe_session_diagnostics> last_session_;
};

}  // namespace aviutl2_mcp

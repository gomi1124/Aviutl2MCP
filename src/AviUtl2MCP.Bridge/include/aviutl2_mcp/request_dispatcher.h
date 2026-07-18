#pragma once

#include "aviutl2_mcp/at_most_once_store.h"
#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/cancellation_registry.h"
#include "aviutl2_mcp/command_gate.h"
#include "aviutl2_mcp/native_ipc_frame_codec.h"
#include "aviutl2_mcp/revision_tracker.h"

#include <cstdint>
#include <atomic>
#include <functional>
#include <future>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace aviutl2_mcp {

struct operation_request final {
    std::array<std::uint8_t, 16> request_id;
    std::string method;
    std::string correlation_id;
    std::uint32_t timeout_ms;
    std::optional<std::string> expected_revision;
    bool dry_run;
    std::string params_json;
    std::vector<std::uint8_t> binary;
};

struct operation_result final {
    bool ok;
    std::string outcome;
    std::string result_json;
    std::string error_code;
    std::string error_message;
    std::string revision;
    std::string view_revision;
    bool retryable = false;
    bool undo_recommended = false;
};

class operation_execution_context final {
public:
    operation_execution_context(
        cancellation_registry& cancellations,
        revision_tracker& revisions,
        std::array<std::uint8_t, 16> request_id);

    [[nodiscard]] bool is_cancelled() const;
    [[nodiscard]] bool reach_commit_point();
    [[nodiscard]] bool has_reached_commit_point() const noexcept;
    [[nodiscard]] revision_tracker& revisions() noexcept;

private:
    cancellation_registry& cancellations_;
    revision_tracker& revisions_;
    std::array<std::uint8_t, 16> request_id_;
    bool has_reached_commit_point_ = false;
};

class operation_handler {
public:
    virtual ~operation_handler() = default;
    [[nodiscard]] virtual std::string operation() const = 0;
    [[nodiscard]] virtual bool is_mutating() const noexcept = 0;
    [[nodiscard]] virtual operation_result execute(
        const operation_request& request,
        operation_execution_context& context) = 0;
};

class request_dispatcher final {
public:
    explicit request_dispatcher(
        bridge_identity identity,
        std::size_t maximum_queue_depth = 64U,
        at_most_once_limits store_limits = {});
    ~request_dispatcher();

    request_dispatcher(const request_dispatcher&) = delete;
    request_dispatcher& operator=(const request_dispatcher&) = delete;

    void register_handler(std::unique_ptr<operation_handler> handler);
    [[nodiscard]] std::future<ipc_frame> dispatch(
        ipc_frame request,
        std::string client_instance_id);
    void dispatch_async(
        ipc_frame request,
        std::string client_instance_id,
        std::function<void(ipc_frame)> completion);
    [[nodiscard]] ipc_frame reject_busy(const ipc_frame& frame) const;
    [[nodiscard]] ipc_frame cancel(const ipc_frame& frame);
    void stop() noexcept;

    [[nodiscard]] revision_tracker& revisions() noexcept;
    [[nodiscard]] cancellation_registry& cancellations() noexcept;
    [[nodiscard]] at_most_once_store& mutations() noexcept;

private:
    struct async_worker final {
        std::thread thread;
        std::shared_ptr<std::atomic<bool>> is_completed;
    };

    [[nodiscard]] operation_request parse_request(const ipc_frame& frame) const;
    [[nodiscard]] ipc_frame create_response_frame(
        const operation_request& request,
        const operation_result& result) const;
    [[nodiscard]] ipc_frame create_error_frame(
        const std::array<std::uint8_t, 16>& request_id,
        const std::string& correlation_id,
        const std::string& code,
        const std::string& message,
        const std::string& outcome,
        bool retryable,
        const std::optional<mutation_record>& record = std::nullopt) const;
    [[nodiscard]] ipc_frame create_frame_from_json(
        const std::array<std::uint8_t, 16>& request_id,
        const std::string& response_json) const;
    [[nodiscard]] std::future<ipc_frame> create_ready_future(ipc_frame frame) const;

    bridge_identity identity_;
    command_gate gate_;
    cancellation_registry cancellations_;
    revision_tracker revisions_;
    at_most_once_store mutations_;
    std::mutex handlers_mutex_;
    std::unordered_map<std::string, std::unique_ptr<operation_handler>> handlers_;
    std::mutex async_workers_mutex_;
    std::vector<async_worker> async_workers_;
    std::atomic<bool> is_stopping_ = false;
};

}  // namespace aviutl2_mcp

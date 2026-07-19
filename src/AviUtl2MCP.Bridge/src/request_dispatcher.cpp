#include "aviutl2_mcp/request_dispatcher.h"
#include "aviutl2_mcp/native_ring_logger.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <chrono>
#include <set>
#include <stdexcept>
#include <utility>

namespace aviutl2_mcp {
namespace {

constexpr std::uint32_t MAXIMUM_TIMEOUT_MS = 120U * 1000U;

[[nodiscard]] std::string request_id_to_uuid(const std::array<std::uint8_t, 16>& bytes) {
    constexpr char HEX[] = "0123456789abcdef";
    constexpr std::array<std::size_t, 4> HYPHEN_OFFSETS{8U, 13U, 18U, 23U};
    std::string value(36U, '0');
    std::size_t output = 0U;
    for (std::size_t index = 0U; index < bytes.size(); ++index) {
        if (std::ranges::find(HYPHEN_OFFSETS, output) != HYPHEN_OFFSETS.end()) {
            value[output++] = '-';
        }
        value[output++] = HEX[bytes[index] >> 4U];
        value[output++] = HEX[bytes[index] & 0x0fU];
    }
    return value;
}

[[nodiscard]] std::vector<std::uint8_t> to_bytes(const std::string& value) {
    return {value.begin(), value.end()};
}

[[nodiscard]] std::string cancel_status_name(const cancel_status status) {
    switch (status) {
        case cancel_status::cancelled:
            return "cancelled";
        case cancel_status::too_late:
            return "tooLate";
        case cancel_status::not_found:
            return "notFound";
    }
    throw std::logic_error("cancel status is unknown");
}

}  // namespace

operation_execution_context::operation_execution_context(
    cancellation_registry& cancellations,
    revision_tracker& revisions,
    std::array<std::uint8_t, 16> request_id)
    : cancellations_(cancellations),
      revisions_(revisions),
      request_id_(request_id) {}

bool operation_execution_context::is_cancelled() const {
    return cancellations_.is_cancelled(request_id_);
}

bool operation_execution_context::reach_commit_point() {
    has_reached_commit_point_ = cancellations_.try_reach_commit_point(request_id_);
    return has_reached_commit_point_;
}

bool operation_execution_context::has_reached_commit_point() const noexcept {
    return has_reached_commit_point_;
}

revision_tracker& operation_execution_context::revisions() noexcept {
    return revisions_;
}

request_dispatcher::request_dispatcher(
    bridge_identity identity,
    const std::size_t maximum_queue_depth,
    const at_most_once_limits store_limits)
    : identity_(std::move(identity)),
      gate_(maximum_queue_depth),
      revisions_(identity_.server_epoch),
      mutations_(identity_.server_epoch, store_limits) {}

request_dispatcher::~request_dispatcher() {
    stop();
}

void request_dispatcher::register_handler(std::unique_ptr<operation_handler> handler) {
    if (handler == nullptr) {
        throw std::invalid_argument("operation handler must not be null");
    }
    const std::string operation = handler->operation();
    if (operation.empty() || operation.size() > 128U) {
        throw std::invalid_argument("operation handler name is invalid");
    }
    std::scoped_lock lock(handlers_mutex_);
    if (!handlers_.emplace(operation, std::move(handler)).second) {
        throw std::invalid_argument("operation handler was already registered");
    }
}

std::future<ipc_frame> request_dispatcher::dispatch(
    ipc_frame frame,
    std::string client_instance_id) {
    if (frame.header.kind != message_kind::request) {
        throw std::invalid_argument("dispatcher only accepts Request frames");
    }

    operation_request request;
    try {
        request = parse_request(frame);
    } catch (const std::exception& exception) {
        return create_ready_future(create_error_frame(
            frame.header.request_id,
            request_id_to_uuid(frame.header.request_id),
            "invalid_request",
            exception.what(),
            "not_started",
            false));
    }
    get_native_logger().write(
        native_log_level::trace,
        "dispatcher",
        "request.received",
        "method=" + request.method,
        native_log_context{
            .correlation_id = request.correlation_id,
            .instance_id = identity_.instance_id,
            .operation = request.method,
        });

    operation_handler* handler = nullptr;
    {
        std::scoped_lock lock(handlers_mutex_);
        const auto found = handlers_.find(request.method);
        if (found != handlers_.end()) {
            handler = found->second.get();
        }
    }
    if (handler == nullptr) {
        return create_ready_future(create_error_frame(
            request.request_id,
            request.correlation_id,
            "operation_not_supported",
            "No native handler is registered for the requested operation",
            "not_started",
            false));
    }

    mutation_token mutation;
    mutation_begin_decision mutation_decision = mutation_begin_decision::accepted;
    if (handler->is_mutating()) {
        mutation_key key{
            .server_epoch = identity_.server_epoch,
            .client_instance_id = client_instance_id,
            .request_id = request.request_id,
        };
        mutation_begin_result begin = mutations_.begin(key, frame.payload_hash);
        mutation = begin.token;
        mutation_decision = begin.decision;
        switch (begin.decision) {
            case mutation_begin_decision::cached:
                return create_ready_future(create_frame_from_json(
                    request.request_id,
                    *begin.record->response_json));
            case mutation_begin_decision::request_id_conflict:
                return create_ready_future(create_error_frame(
                    request.request_id,
                    request.correlation_id,
                    "request_id_conflict",
                    "Request ID was already used with a different payload",
                    "not_started",
                    false));
            case mutation_begin_decision::request_expired:
                return create_ready_future(create_error_frame(
                    request.request_id,
                    request.correlation_id,
                    "request_expired",
                    "Mutation request timestamp is outside the accepted window",
                    "not_started",
                    false));
            case mutation_begin_decision::result_evicted:
                return create_ready_future(create_error_frame(
                    request.request_id,
                    request.correlation_id,
                    "request_result_evicted",
                    "Mutation result is known but its full response was evicted",
                    begin.record->outcome,
                    false,
                    begin.record));
            case mutation_begin_decision::bridge_busy:
                return create_ready_future(create_error_frame(
                    request.request_id,
                    request.correlation_id,
                    "bridge_busy",
                    "Mutation tombstone capacity is full",
                    "not_started",
                    true));
            case mutation_begin_decision::accepted:
            case mutation_begin_decision::attach:
                break;
        }
    }

    auto promise = std::make_shared<std::promise<ipc_frame>>();
    std::future<ipc_frame> future = promise->get_future();
    if (handler->is_mutating() && mutation.valid()) {
        if (mutation_decision == mutation_begin_decision::attach) {
            const gate_enqueue_result attached = gate_.try_enqueue(
                [this, promise, request, mutation] {
                    const auto record = mutations_.wait_for_completion(mutation, std::chrono::milliseconds(request.timeout_ms));
                    if (record.has_value() && record->response_json.has_value()) {
                        promise->set_value(create_frame_from_json(request.request_id, *record->response_json));
                    } else {
                        promise->set_value(create_error_frame(
                            request.request_id,
                            request.correlation_id,
                            record.has_value() ? "request_result_evicted" : "operation_timeout",
                            record.has_value() ? "Mutation response was evicted" : "Timed out waiting for attached mutation",
                            record.has_value() ? record->outcome : "unknown",
                            false,
                            record));
                    }
                },
                [this, promise, request] {
                    promise->set_value(create_error_frame(
                        request.request_id,
                        request.correlation_id,
                        "bridge_stopping",
                        "Bridge stopped while waiting for an attached mutation",
                        "unknown",
                        true));
                });
            if (attached == gate_enqueue_result::accepted) {
                return future;
            }
            promise->set_value(create_error_frame(
                request.request_id,
                request.correlation_id,
                "bridge_busy",
                "Bridge queue is unavailable",
                "unknown",
                true));
            return future;
        }
    }

    if (!cancellations_.register_request(request.request_id)) {
        promise->set_value(create_error_frame(
            request.request_id,
            request.correlation_id,
            "request_id_conflict",
            "Request ID is already active",
            "not_started",
            false));
        return future;
    }

    const gate_enqueue_result enqueue = gate_.try_enqueue(
        [this, promise, request, handler, mutation] {
            if (!cancellations_.try_begin(request.request_id)) {
                const ipc_frame cancelled = create_error_frame(
                    request.request_id,
                    request.correlation_id,
                    "operation_cancelled",
                    "Operation was cancelled before its commit point",
                    "unchanged",
                    false);
                if (mutation.valid()) {
                    mutations_.complete(mutation, "unchanged", revisions_.content_revision(),
                        std::string(cancelled.json.begin(), cancelled.json.end()));
                }
                cancellations_.complete(request.request_id);
                promise->set_value(cancelled);
                return;
            }
            if (mutation.valid()) {
                mutations_.mark_executing(mutation);
            }

            operation_execution_context context(cancellations_, revisions_, request.request_id);
            operation_result result;
            try {
                result = handler->execute(request, context);
                if (context.is_cancelled() && !context.has_reached_commit_point()) {
                    result = operation_result{
                        .ok = false,
                        .outcome = "unchanged",
                        .result_json = {},
                        .error_code = "operation_cancelled",
                        .error_message = "Operation was cancelled before its commit point",
                        .revision = revisions_.content_revision(),
                        .view_revision = revisions_.view_revision(),
                    };
                }
            } catch (const std::exception&) {
                result = operation_result{
                    .ok = false,
                    .outcome = context.has_reached_commit_point() ? "unknown" : "unchanged",
                    .result_json = {},
                    .error_code = "internal_error",
                    .error_message = "Native operation handler failed",
                    .revision = revisions_.content_revision(),
                    .view_revision = revisions_.view_revision(),
                    .retryable = false,
                    .undo_recommended = context.has_reached_commit_point(),
                };
            } catch (...) {
                result = operation_result{
                    .ok = false,
                    .outcome = context.has_reached_commit_point() ? "unknown" : "unchanged",
                    .result_json = {},
                    .error_code = "internal_error",
                    .error_message = "Native operation handler failed",
                    .revision = revisions_.content_revision(),
                    .view_revision = revisions_.view_revision(),
                    .retryable = false,
                    .undo_recommended = context.has_reached_commit_point(),
                };
            }
            if (result.outcome.empty()) {
                result.ok = false;
                result.outcome = context.has_reached_commit_point() ? "unknown" : "unchanged";
                result.error_code = "internal_error";
                result.error_message = "Native operation result omitted its outcome";
                result.retryable = false;
                result.undo_recommended = context.has_reached_commit_point();
            }
            ipc_frame response;
            try {
                response = create_response_frame(request, result);
            } catch (const std::exception&) {
                result = operation_result{
                    .ok = false,
                    .outcome = context.has_reached_commit_point() ? "unknown" : "unchanged",
                    .result_json = {},
                    .error_code = "internal_error",
                    .error_message = "Native operation result was invalid",
                    .revision = revisions_.content_revision(),
                    .view_revision = revisions_.view_revision(),
                    .retryable = false,
                    .undo_recommended = context.has_reached_commit_point(),
                };
                response = create_response_frame(request, result);
            }
            if (mutation.valid()) {
                const std::string completed_revision = result.revision.empty()
                    ? revisions_.content_revision()
                    : result.revision;
                mutations_.complete(
                    mutation,
                    result.outcome,
                    completed_revision,
                    std::string(response.json.begin(), response.json.end()));
            }
            cancellations_.complete(request.request_id);
            promise->set_value(std::move(response));
        },
        [this, promise, request, mutation] {
            const ipc_frame response = create_error_frame(
                request.request_id,
                request.correlation_id,
                "bridge_stopping",
                "Bridge stopped before operation execution",
                "not_started",
                true);
            if (mutation.valid()) {
                mutations_.complete(
                    mutation,
                    "not_started",
                    revisions_.content_revision(),
                    std::string(response.json.begin(), response.json.end()));
            }
            cancellations_.complete(request.request_id);
            promise->set_value(response);
        });
    if (enqueue == gate_enqueue_result::accepted) {
        return future;
    }

    const ipc_frame response = create_error_frame(
        request.request_id,
        request.correlation_id,
        enqueue == gate_enqueue_result::busy ? "bridge_busy" : "bridge_stopping",
        "Bridge command queue is unavailable",
        "not_started",
        true);
    if (mutation.valid()) {
        mutations_.complete(
            mutation,
            "not_started",
            revisions_.content_revision(),
            std::string(response.json.begin(), response.json.end()));
    }
    cancellations_.complete(request.request_id);
    promise->set_value(response);
    return future;
}

void request_dispatcher::dispatch_async(
    ipc_frame request,
    std::string client_instance_id,
    std::function<void(ipc_frame)> completion) {
    if (!completion || is_stopping_.load()) {
        throw std::invalid_argument("async dispatcher completion is unavailable");
    }
    std::future<ipc_frame> response = dispatch(std::move(request), std::move(client_instance_id));
    std::scoped_lock lock(async_workers_mutex_);
    if (is_stopping_.load()) {
        throw std::runtime_error("dispatcher is stopping");
    }
    for (auto iterator = async_workers_.begin(); iterator != async_workers_.end();) {
        if (!iterator->is_completed->load()) {
            ++iterator;
            continue;
        }
        iterator->thread.join();
        iterator = async_workers_.erase(iterator);
    }
    auto is_completed = std::make_shared<std::atomic<bool>>(false);
    std::thread worker(
        [response = std::move(response), completion = std::move(completion), is_completed]() mutable {
            try {
                completion(response.get());
            } catch (const std::exception& exception) {
                get_native_logger().write(
                    native_log_level::error,
                    "dispatcher",
                    "response.completion_failed",
                    exception.what());
            } catch (...) {
                get_native_logger().write(
                    native_log_level::error,
                    "dispatcher",
                    "response.completion_failed",
                    "Unknown async response completion failure");
            }
            is_completed->store(true);
        });
    async_workers_.push_back({std::move(worker), std::move(is_completed)});
}

ipc_frame request_dispatcher::reject_busy(const ipc_frame& frame) const {
    try {
        const operation_request request = parse_request(frame);
        return create_error_frame(
            request.request_id,
            request.correlation_id,
            "bridge_busy",
            "Negotiated in-flight request limit was reached",
            "not_started",
            true);
    } catch (const std::exception&) {
        return create_error_frame(
            frame.header.request_id,
            request_id_to_uuid(frame.header.request_id),
            "bridge_busy",
            "Negotiated in-flight request limit was reached",
            "not_started",
            true);
    }
}

ipc_frame request_dispatcher::cancel(const ipc_frame& frame) {
    if (frame.header.kind != message_kind::cancel
        || frame.header.flags != frame_flags::none
        || !frame.binary.empty()) {
        throw std::invalid_argument("Cancel frame is invalid");
    }
    const cancel_result result = cancellations_.cancel(frame.header.request_id);
    const std::string json = nlohmann::json{
        {"status", cancel_status_name(result.status)},
        {"responseWillFollow", result.response_will_follow},
    }.dump();
    ipc_frame response{
        .header = frame_header{
            .kind = message_kind::cancel_ack,
            .flags = frame_flags::none,
            .request_id = frame.header.request_id,
            .json_length = static_cast<std::uint32_t>(json.size()),
            .binary_length = 0U,
        },
        .json = to_bytes(json),
        .binary = {},
        .payload_hash = {},
    };
    response.payload_hash = calculate_payload_hash(response.header, response.json, response.binary);
    return response;
}

void request_dispatcher::stop() noexcept {
    if (is_stopping_.exchange(true)) {
        return;
    }
    cancellations_.cancel_all();
    gate_.stop();
    std::vector<async_worker> workers;
    {
        std::scoped_lock lock(async_workers_mutex_);
        workers = std::move(async_workers_);
    }
    for (auto& worker : workers) {
        if (worker.thread.joinable()) {
            worker.thread.join();
        }
    }
}

revision_tracker& request_dispatcher::revisions() noexcept {
    return revisions_;
}

cancellation_registry& request_dispatcher::cancellations() noexcept {
    return cancellations_;
}

at_most_once_store& request_dispatcher::mutations() noexcept {
    return mutations_;
}

operation_request request_dispatcher::parse_request(const ipc_frame& frame) const {
    const auto flags = static_cast<std::uint8_t>(frame.header.flags);
    const auto response_flags = static_cast<std::uint8_t>(frame_flags::error_response)
        | static_cast<std::uint8_t>(frame_flags::partial_response);
    if ((flags & response_flags) != 0U) {
        throw std::invalid_argument("Request frame contains response-only flags");
    }
    const nlohmann::json document = nlohmann::json::parse(frame.json.begin(), frame.json.end());
    if (!document.is_object()) {
        throw std::invalid_argument("Request envelope must be a JSON object");
    }
    static const std::set<std::string> ALLOWED_FIELDS{
        "method", "correlationId", "timeoutMs", "expectedRevision", "dryRun", "params"};
    for (const auto& [name, value] : document.items()) {
        static_cast<void>(value);
        if (!ALLOWED_FIELDS.contains(name)) {
            throw std::invalid_argument("Request envelope contains an unknown field");
        }
    }

    operation_request request{
        .request_id = frame.header.request_id,
        .method = document.at("method").get<std::string>(),
        .correlation_id = document.at("correlationId").get<std::string>(),
        .timeout_ms = document.at("timeoutMs").get<std::uint32_t>(),
        .expected_revision = std::nullopt,
        .dry_run = document.value("dryRun", false),
        .params_json = document.at("params").dump(),
        .binary = frame.binary,
        .received_at = std::chrono::steady_clock::now(),
    };
    if (document.contains("expectedRevision") && !document.at("expectedRevision").is_null()) {
        request.expected_revision = document.at("expectedRevision").get<std::string>();
    }
    if (request.method.empty()
        || request.method.size() > 128U
        || !is_nonzero_uuid(request.correlation_id)
        || request.timeout_ms == 0U
        || request.timeout_ms > MAXIMUM_TIMEOUT_MS
        || !document.at("params").is_object()) {
        throw std::invalid_argument("Request envelope fields are outside supported constraints");
    }
    return request;
}

ipc_frame request_dispatcher::create_response_frame(
    const operation_request& request,
    const operation_result& result) const {
    nlohmann::json document{
        {"ok", result.ok},
        {"correlationId", request.correlation_id},
        {"instanceId", identity_.instance_id},
        {"revision", result.revision.empty() ? revisions_.content_revision() : result.revision},
        {"viewRevision", result.view_revision.empty() ? revisions_.view_revision() : result.view_revision},
    };
    if (result.ok) {
        document["result"] = result.result_json.empty()
            ? nlohmann::json::object()
            : nlohmann::json::parse(result.result_json);
        document["warnings"] = nlohmann::json::array();
    } else {
        if (!result.result_json.empty()) {
            document["result"] = nlohmann::json::parse(result.result_json);
        }
        document["error"] = {
            {"code", result.error_code},
            {"message", result.error_message},
            {"retryable", result.retryable},
            {"phase", "sdk"},
            {"outcome", result.outcome},
            {"undoRecommended", result.undo_recommended},
            {"details", nlohmann::json::object()},
        };
    }
    ipc_frame response = create_frame_from_json(
        request.request_id,
        document.dump(),
        result.binary);
    const double duration_ms = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - request.received_at).count();
    get_native_logger().write(
        result.ok ? native_log_level::information : native_log_level::warning,
        "dispatcher",
        result.ok ? "request.completed" : "request.failed",
        "method=" + request.method + " outcome=" + result.outcome,
        native_log_context{
            .correlation_id = request.correlation_id,
            .instance_id = identity_.instance_id,
            .operation = request.method,
            .duration_ms = duration_ms,
            .result_code = result.ok ? std::string_view("ok") : std::string_view(result.error_code),
        });
    return response;
}

ipc_frame request_dispatcher::create_error_frame(
    const std::array<std::uint8_t, 16>& request_id,
    const std::string& correlation_id,
    const std::string& code,
    const std::string& message,
    const std::string& outcome,
    const bool retryable,
    const std::optional<mutation_record>& record) const {
    nlohmann::json details = nlohmann::json::object();
    if (record.has_value()) {
        details = {
            {"revision", record->revision},
            {"resultDigest", record->result_digest},
        };
    }
    const std::string json = nlohmann::json{
        {"ok", false},
        {"correlationId", correlation_id},
        {"instanceId", identity_.instance_id},
        {"revision", record.has_value() ? record->revision : revisions_.content_revision()},
        {"viewRevision", revisions_.view_revision()},
        {"error", {
            {"code", code},
            {"message", message},
            {"retryable", retryable},
            {"phase", "preflight"},
            {"outcome", outcome},
            {"undoRecommended", false},
            {"details", details}}},
    }.dump();
    ipc_frame response = create_frame_from_json(request_id, json);
    get_native_logger().write(
        native_log_level::warning,
        "dispatcher",
        "request.rejected",
        "code=" + code + " outcome=" + outcome,
        native_log_context{
            .correlation_id = correlation_id,
            .instance_id = identity_.instance_id,
            .result_code = code,
        });
    return response;
}

ipc_frame request_dispatcher::create_frame_from_json(
    const std::array<std::uint8_t, 16>& request_id,
    const std::string& response_json,
    std::vector<std::uint8_t> binary) const {
    const nlohmann::json document = nlohmann::json::parse(response_json);
    const bool is_error = !document.at("ok").get<bool>();
    const auto flags = static_cast<frame_flags>(
        (is_error ? static_cast<std::uint8_t>(frame_flags::error_response) : 0U)
        | (!binary.empty() ? static_cast<std::uint8_t>(frame_flags::has_binary) : 0U));
    ipc_frame frame{
        .header = frame_header{
            .kind = message_kind::response,
            .flags = flags,
            .request_id = request_id,
            .json_length = static_cast<std::uint32_t>(response_json.size()),
            .binary_length = binary.size(),
        },
        .json = to_bytes(response_json),
        .binary = std::move(binary),
        .payload_hash = {},
    };
    frame.payload_hash = calculate_payload_hash(frame.header, frame.json, frame.binary);
    return frame;
}

std::future<ipc_frame> request_dispatcher::create_ready_future(ipc_frame frame) const {
    std::promise<ipc_frame> promise;
    std::future<ipc_frame> future = promise.get_future();
    promise.set_value(std::move(frame));
    return future;
}

}  // namespace aviutl2_mcp

#include "aviutl2_mcp/command_gate.h"

#include <Windows.h>

#include <stdexcept>
#include <utility>
#include <vector>

namespace aviutl2_mcp {

command_gate::command_gate(const std::size_t maximum_queue_depth)
    : maximum_queue_depth_(maximum_queue_depth) {
    if (maximum_queue_depth_ == 0U) {
        throw std::invalid_argument("command gate queue depth must be positive");
    }
    worker_ = std::thread(&command_gate::run, this);
}

command_gate::~command_gate() {
    stop();
}

gate_enqueue_result command_gate::try_enqueue(
    std::function<void()> execute,
    std::function<void()> cancel) {
    if (!execute || !cancel) {
        throw std::invalid_argument("command gate callbacks must not be empty");
    }
    {
        std::scoped_lock lock(mutex_);
        if (is_stopping_) {
            return gate_enqueue_result::stopping;
        }
        if (queue_.size() >= maximum_queue_depth_) {
            return gate_enqueue_result::busy;
        }
        queue_.push_back({std::move(execute), std::move(cancel)});
    }
    condition_.notify_one();
    return gate_enqueue_result::accepted;
}

void command_gate::stop() noexcept {
    std::vector<std::function<void()>> cancellations;
    {
        std::scoped_lock lock(mutex_);
        if (!is_stopping_) {
            is_stopping_ = true;
            cancellations.reserve(queue_.size());
            while (!queue_.empty()) {
                cancellations.push_back(std::move(queue_.front().cancel));
                queue_.pop_front();
            }
        }
    }
    for (auto& cancel : cancellations) {
        try {
            cancel();
        } catch (const std::exception& exception) {
            OutputDebugStringA("AviUtl2MCP queued command cancellation failed: ");
            OutputDebugStringA(exception.what());
            OutputDebugStringA("\n");
        }
    }
    condition_.notify_all();
    if (worker_.joinable()) {
        worker_.join();
    }
}

std::size_t command_gate::queued_count() const {
    std::scoped_lock lock(mutex_);
    return queue_.size();
}

bool command_gate::is_stopping() const {
    std::scoped_lock lock(mutex_);
    return is_stopping_;
}

void command_gate::run() noexcept {
    while (true) {
        queued_command command;
        {
            std::unique_lock lock(mutex_);
            condition_.wait(lock, [this] { return is_stopping_ || !queue_.empty(); });
            if (queue_.empty()) {
                if (is_stopping_) {
                    return;
                }
                continue;
            }
            command = std::move(queue_.front());
            queue_.pop_front();
        }
        try {
            command.execute();
        } catch (const std::exception& exception) {
            OutputDebugStringA("AviUtl2MCP command gate task failed: ");
            OutputDebugStringA(exception.what());
            OutputDebugStringA("\n");
        }
    }
}

}  // namespace aviutl2_mcp

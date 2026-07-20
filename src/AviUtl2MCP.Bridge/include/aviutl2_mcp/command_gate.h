#pragma once

#include <cstddef>
#include <functional>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>

namespace aviutl2_mcp {

enum class gate_enqueue_result {
    accepted,
    busy,
    stopping,
};

class command_gate final {
public:
    explicit command_gate(std::size_t maximum_queue_depth = 64U);
    ~command_gate();

    command_gate(const command_gate&) = delete;
    command_gate& operator=(const command_gate&) = delete;

    [[nodiscard]] gate_enqueue_result try_enqueue(
        std::function<void()> execute,
        std::function<void()> cancel);
    void stop() noexcept;

    [[nodiscard]] std::size_t queued_count() const;
    [[nodiscard]] bool is_stopping() const;

private:
    struct queued_command final {
        std::function<void()> execute;
        std::function<void()> cancel;
    };

    void run() noexcept;

    const std::size_t maximum_queue_depth_;
    mutable std::mutex mutex_;
    std::condition_variable condition_;
    std::deque<queued_command> queue_;
    bool is_stopping_ = false;
    std::thread worker_;
};

}  // namespace aviutl2_mcp

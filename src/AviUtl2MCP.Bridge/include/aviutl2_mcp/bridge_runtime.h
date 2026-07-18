#pragma once

#include "aviutl2_mcp/bridge_identity.h"

#include <cstdint>
#include <filesystem>
#include <memory>
#include <mutex>
#include <optional>

namespace aviutl2_mcp {

class instance_descriptor_publisher;
class named_pipe_server;

class bridge_runtime final {
public:
    bridge_runtime();
    explicit bridge_runtime(std::filesystem::path descriptor_directory);
    ~bridge_runtime();

    bridge_runtime(const bridge_runtime&) = delete;
    bridge_runtime& operator=(const bridge_runtime&) = delete;

    [[nodiscard]] bool start(std::uint32_t host_version) noexcept;
    void stop() noexcept;

    [[nodiscard]] bool is_running() const noexcept;
    [[nodiscard]] std::optional<bridge_identity> identity() const;
    [[nodiscard]] std::filesystem::path descriptor_path() const;

private:
    mutable std::mutex mutex_;
    std::filesystem::path descriptor_directory_;
    std::optional<bridge_identity> identity_;
    std::unique_ptr<instance_descriptor_publisher> publisher_;
    std::unique_ptr<named_pipe_server> server_;
    bool is_running_ = false;
};

[[nodiscard]] bridge_runtime& get_bridge_runtime() noexcept;

}  // namespace aviutl2_mcp

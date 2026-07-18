#include "aviutl2_mcp/bridge_runtime.h"

#include "aviutl2_mcp/instance_descriptor.h"
#include "aviutl2_mcp/named_pipe_server.h"

#include <Windows.h>

#include <string>
#include <utility>

namespace aviutl2_mcp {

bridge_runtime::bridge_runtime() = default;

bridge_runtime::bridge_runtime(std::filesystem::path descriptor_directory)
    : descriptor_directory_(std::move(descriptor_directory)) {}

bridge_runtime::~bridge_runtime() {
    stop();
}

bool bridge_runtime::start(const std::uint32_t host_version) noexcept {
    std::scoped_lock lock(mutex_);
    if (is_running_) {
        return false;
    }

    try {
        if (descriptor_directory_.empty()) {
            descriptor_directory_ = get_default_descriptor_directory();
        }
        identity_ = create_bridge_identity();
        server_ = std::make_unique<named_pipe_server>(*identity_, std::to_string(host_version));
        publisher_ = std::make_unique<instance_descriptor_publisher>(
            *identity_,
            descriptor_directory_,
            "0.1.0");
        server_->start();
        try {
            publisher_->publish();
        } catch (...) {
            server_->stop();
            throw;
        }
        is_running_ = true;
        return true;
    } catch (const std::exception& exception) {
        OutputDebugStringA("AviUtl2MCP bridge startup failed: ");
        OutputDebugStringA(exception.what());
        OutputDebugStringA("\n");
        publisher_.reset();
        server_.reset();
        identity_.reset();
        is_running_ = false;
        return false;
    }
}

void bridge_runtime::stop() noexcept {
    std::scoped_lock lock(mutex_);
    if (!is_running_ && publisher_ == nullptr && server_ == nullptr) {
        return;
    }
    is_running_ = false;
    if (publisher_ != nullptr) {
        publisher_->remove();
    }
    if (server_ != nullptr) {
        server_->stop();
    }
    publisher_.reset();
    server_.reset();
    identity_.reset();
}

bool bridge_runtime::is_running() const noexcept {
    std::scoped_lock lock(mutex_);
    return is_running_;
}

std::optional<bridge_identity> bridge_runtime::identity() const {
    std::scoped_lock lock(mutex_);
    return identity_;
}

std::filesystem::path bridge_runtime::descriptor_path() const {
    std::scoped_lock lock(mutex_);
    return publisher_ == nullptr ? std::filesystem::path{} : publisher_->path();
}

bridge_runtime& get_bridge_runtime() noexcept {
    static bridge_runtime runtime;
    return runtime;
}

}  // namespace aviutl2_mcp

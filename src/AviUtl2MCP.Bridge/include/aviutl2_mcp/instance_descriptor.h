#pragma once

#include "aviutl2_mcp/bridge_identity.h"

#include <filesystem>

namespace aviutl2_mcp {

class instance_descriptor_publisher final {
public:
    instance_descriptor_publisher(
        bridge_identity identity,
        std::filesystem::path directory,
        std::string bridge_version);

    void publish();
    void remove() noexcept;

    [[nodiscard]] const std::filesystem::path& path() const noexcept;
    [[nodiscard]] bool is_published() const noexcept;

private:
    bridge_identity identity_;
    std::filesystem::path directory_;
    std::filesystem::path path_;
    std::string bridge_version_;
    bool is_published_ = false;
};

}  // namespace aviutl2_mcp

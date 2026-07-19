#pragma once

#include <cstdint>
#include <filesystem>
#include <string>

namespace aviutl2_mcp {

struct bridge_identity final {
    std::string instance_id;
    std::string server_epoch;
    std::string pipe_name;
    std::uint32_t process_id;
    std::uint64_t process_creation_time;
};

[[nodiscard]] bridge_identity create_bridge_identity();
[[nodiscard]] std::filesystem::path get_default_descriptor_directory();
[[nodiscard]] std::filesystem::path get_configured_descriptor_directory();
[[nodiscard]] bool uuid_equals(const std::string& left, const std::string& right) noexcept;
[[nodiscard]] bool is_nonzero_uuid(const std::string& value) noexcept;

}  // namespace aviutl2_mcp

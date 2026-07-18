#pragma once

#include <Windows.h>

#include <cstddef>
#include <filesystem>
#include <vector>

namespace aviutl2_mcp {

class user_only_security final {
public:
    user_only_security();
    ~user_only_security();

    user_only_security(const user_only_security&) = delete;
    user_only_security& operator=(const user_only_security&) = delete;
    user_only_security(user_only_security&&) = delete;
    user_only_security& operator=(user_only_security&&) = delete;

    [[nodiscard]] SECURITY_ATTRIBUTES* attributes() noexcept;
    [[nodiscard]] PACL acl() const noexcept;

private:
    std::vector<std::byte> logon_sid_;
    std::vector<std::byte> system_sid_;
    PACL acl_ = nullptr;
    SECURITY_DESCRIPTOR descriptor_{};
    SECURITY_ATTRIBUTES attributes_{};
};

void ensure_user_only_directory(const std::filesystem::path& directory);

}  // namespace aviutl2_mcp

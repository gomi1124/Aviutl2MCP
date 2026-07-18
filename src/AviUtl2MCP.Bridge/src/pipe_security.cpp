#include "aviutl2_mcp/pipe_security.h"

#include <Aclapi.h>

#include <array>
#include <memory>
#include <stdexcept>
#include <system_error>

namespace aviutl2_mcp {
namespace {

class handle_closer final {
public:
    void operator()(void* handle) const noexcept {
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE) {
            CloseHandle(handle);
        }
    }
};

using unique_handle = std::unique_ptr<void, handle_closer>;

[[noreturn]] void throw_last_error(const char* message) {
    throw std::system_error(
        static_cast<int>(GetLastError()),
        std::system_category(),
        message);
}

[[nodiscard]] std::vector<std::byte> get_logon_sid() {
    HANDLE raw_token = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw_token) == FALSE) {
        throw_last_error("OpenProcessToken failed");
    }
    unique_handle token(raw_token);

    DWORD required_bytes = 0U;
    if (GetTokenInformation(token.get(), TokenGroups, nullptr, 0U, &required_bytes) != FALSE
        || GetLastError() != ERROR_INSUFFICIENT_BUFFER) {
        throw_last_error("GetTokenInformation size query failed");
    }
    std::vector<std::byte> groups_buffer(required_bytes);
    if (GetTokenInformation(
            token.get(),
            TokenGroups,
            groups_buffer.data(),
            required_bytes,
            &required_bytes)
        == FALSE) {
        throw_last_error("GetTokenInformation failed");
    }

    const auto* groups = reinterpret_cast<const TOKEN_GROUPS*>(groups_buffer.data());
    for (DWORD index = 0U; index < groups->GroupCount; ++index) {
        const SID_AND_ATTRIBUTES& group = groups->Groups[index];
        if ((group.Attributes & SE_GROUP_LOGON_ID) != SE_GROUP_LOGON_ID) {
            continue;
        }
        const DWORD sid_bytes = GetLengthSid(group.Sid);
        std::vector<std::byte> sid(sid_bytes);
        if (CopySid(sid_bytes, sid.data(), group.Sid) == FALSE) {
            throw_last_error("CopySid failed");
        }
        return sid;
    }
    throw std::runtime_error("current process token did not include a logon SID");
}

[[nodiscard]] std::vector<std::byte> get_system_sid() {
    DWORD sid_bytes = SECURITY_MAX_SID_SIZE;
    std::vector<std::byte> sid(sid_bytes);
    if (CreateWellKnownSid(WinLocalSystemSid, nullptr, sid.data(), &sid_bytes) == FALSE) {
        throw_last_error("CreateWellKnownSid failed");
    }
    sid.resize(sid_bytes);
    return sid;
}

}  // namespace

user_only_security::user_only_security()
    : logon_sid_(get_logon_sid()),
      system_sid_(get_system_sid()) {
    std::array<EXPLICIT_ACCESSW, 2> entries{};
    entries[0].grfAccessPermissions = GENERIC_ALL;
    entries[0].grfAccessMode = SET_ACCESS;
    entries[0].grfInheritance = NO_INHERITANCE;
    entries[0].Trustee.TrusteeForm = TRUSTEE_IS_SID;
    entries[0].Trustee.TrusteeType = TRUSTEE_IS_USER;
    entries[0].Trustee.ptstrName = reinterpret_cast<LPWSTR>(logon_sid_.data());
    entries[1].grfAccessPermissions = GENERIC_ALL;
    entries[1].grfAccessMode = SET_ACCESS;
    entries[1].grfInheritance = NO_INHERITANCE;
    entries[1].Trustee.TrusteeForm = TRUSTEE_IS_SID;
    entries[1].Trustee.TrusteeType = TRUSTEE_IS_WELL_KNOWN_GROUP;
    entries[1].Trustee.ptstrName = reinterpret_cast<LPWSTR>(system_sid_.data());

    PACL candidate_acl = nullptr;
    const DWORD acl_error = SetEntriesInAclW(
        static_cast<ULONG>(entries.size()),
        entries.data(),
        nullptr,
        &candidate_acl);
    if (acl_error != ERROR_SUCCESS) {
        SetLastError(acl_error);
        throw_last_error("SetEntriesInAclW failed");
    }
    if (InitializeSecurityDescriptor(&descriptor_, SECURITY_DESCRIPTOR_REVISION) == FALSE
        || SetSecurityDescriptorDacl(&descriptor_, TRUE, candidate_acl, FALSE) == FALSE) {
        const DWORD descriptor_error = GetLastError();
        LocalFree(candidate_acl);
        SetLastError(descriptor_error);
        throw_last_error("security descriptor initialization failed");
    }
    acl_ = candidate_acl;

    attributes_.nLength = sizeof(attributes_);
    attributes_.lpSecurityDescriptor = &descriptor_;
    attributes_.bInheritHandle = FALSE;
}

user_only_security::~user_only_security() {
    if (acl_ != nullptr) {
        LocalFree(acl_);
    }
}

SECURITY_ATTRIBUTES* user_only_security::attributes() noexcept {
    return &attributes_;
}

PACL user_only_security::acl() const noexcept {
    return acl_;
}

void ensure_user_only_directory(const std::filesystem::path& directory) {
    std::error_code error;
    std::filesystem::create_directories(directory, error);
    if (error) {
        throw std::system_error(error, "failed to create the instance descriptor directory");
    }

    user_only_security security;
    const DWORD acl_error = SetNamedSecurityInfoW(
        const_cast<LPWSTR>(directory.c_str()),
        SE_FILE_OBJECT,
        DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
        nullptr,
        nullptr,
        security.acl(),
        nullptr);
    if (acl_error != ERROR_SUCCESS) {
        SetLastError(acl_error);
        throw_last_error("SetNamedSecurityInfoW failed for the descriptor directory");
    }
}

}  // namespace aviutl2_mcp

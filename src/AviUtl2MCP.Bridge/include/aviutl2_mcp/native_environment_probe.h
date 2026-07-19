#pragma once

#include "aviutl2_mcp/gcmz_adapter.h"
#include "aviutl2_mcp/psd_contract.h"

#include <optional>
#include <string>
#include <vector>

namespace aviutl2_mcp {

class sdk_read_facade;
struct sdk_status_snapshot;

struct native_environment_probe final {
    std::optional<std::string> psdtoolkit_version;
    psd_profile_detection psd_profile;
    psdtoolkit_config_result psdtoolkit_config;
    bool has_psd_alias = false;
    gcmz_probe_result gcmz;
};

struct native_component_probe final {
    std::string name;
    std::string status;
    std::optional<std::string> version;
};

[[nodiscard]] native_environment_probe probe_native_environment(
    sdk_read_facade& sdk,
    const sdk_status_snapshot& status);

[[nodiscard]] std::vector<native_component_probe> describe_native_environment(
    const native_environment_probe& probe);

}  // namespace aviutl2_mcp

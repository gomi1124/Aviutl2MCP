#include "aviutl2_mcp/native_environment_probe.h"

#include "aviutl2_mcp/sdk_read_facade.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <filesystem>
#include <limits>
#include <ranges>
#include <string_view>

namespace aviutl2_mcp {
namespace {

constexpr std::uintmax_t MAXIMUM_ALIAS_BYTES = 64U * 1024U;

[[nodiscard]] std::optional<std::string> extract_psdtoolkit_version(
    const std::vector<sdk_module_summary>& modules) {
    for (const sdk_module_summary& module : modules) {
        if (module.name.find("PSDToolKit") == std::string::npos
            && module.information.find("PSDToolKit") == std::string::npos) {
            continue;
        }
        const std::size_t version_start = module.information.find_first_of("0123456789");
        if (version_start == std::string::npos) {
            continue;
        }
        std::size_t version_end = version_start;
        while (version_end < module.information.size()) {
            const unsigned char character = static_cast<unsigned char>(module.information[version_end]);
            if (std::isalnum(character) == 0 && character != '.') {
                break;
            }
            ++version_end;
        }
        if (version_end > version_start) {
            return module.information.substr(version_start, version_end - version_start);
        }
    }
    return std::nullopt;
}

[[nodiscard]] std::optional<std::filesystem::path> get_loaded_module_path(
    const wchar_t* module_name) {
    const HMODULE module = GetModuleHandleW(module_name);
    if (module == nullptr) {
        return std::nullopt;
    }
    std::wstring buffer(512U, L'\0');
    while (buffer.size() <= 32'768U) {
        const DWORD copied = GetModuleFileNameW(
            module,
            buffer.data(),
            static_cast<DWORD>(buffer.size()));
        if (copied == 0U) {
            return std::nullopt;
        }
        if (copied < buffer.size() - 1U) {
            buffer.resize(copied);
            return std::filesystem::path(buffer);
        }
        buffer.resize(buffer.size() * 2U);
    }
    return std::nullopt;
}

[[nodiscard]] std::optional<std::filesystem::path> utf8_to_path(
    const std::optional<std::string>& value) {
    if (!value.has_value() || value->empty()
        || value->size() > static_cast<std::size_t>((std::numeric_limits<int>::max)())) {
        return std::nullopt;
    }
    const int characters = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value->data(),
        static_cast<int>(value->size()),
        nullptr,
        0);
    if (characters <= 0) {
        return std::nullopt;
    }
    std::wstring result(static_cast<std::size_t>(characters), L'\0');
    if (MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            value->data(),
            static_cast<int>(value->size()),
            result.data(),
            characters) != characters) {
        return std::nullopt;
    }
    return std::filesystem::path(result);
}

[[nodiscard]] psd_profile_observation observe_psd_profile(
    sdk_read_facade& sdk,
    const sdk_effect_catalog_snapshot& catalog,
    const std::optional<std::string>& version) {
    psd_profile_observation observation{.version = version};
    constexpr std::array effect_names{
        std::string_view(PSD_SETUP_EFFECT),
        std::string_view(PSD_FILE_EFFECT),
        std::string_view(PSD_VOICE_EFFECT),
    };
    for (const std::string_view name : effect_names) {
        const auto effect = std::ranges::find_if(
            catalog.effects,
            [name](const sdk_effect_definition& definition) { return definition.name == name; });
        if (effect == catalog.effects.end()) {
            continue;
        }
        psd_observed_effect observed{.name = effect->name};
        const sdk_effect_items_query_result items = sdk.query_effect_items(effect->name, false);
        if (items.ok) {
            for (const sdk_effect_item_snapshot& item : items.items) {
                observed.items.push_back({.name = item.name, .type = item.type});
            }
        }
        observation.effects.push_back(std::move(observed));
    }
    return observation;
}

[[nodiscard]] bool probe_psd_alias() {
    const std::optional<std::filesystem::path> bridge =
        get_loaded_module_path(L"AviUtl2MCP.Bridge.aux2");
    if (!bridge.has_value()) {
        return false;
    }
    const std::filesystem::path alias = bridge->parent_path()
        / L"assets" / L"psdtoolkit2" / L"v1" / L"subtitle.object";
    std::error_code error;
    const std::uintmax_t size = std::filesystem::file_size(alias, error);
    return !error && size > 0U && size <= MAXIMUM_ALIAS_BYTES;
}

[[nodiscard]] native_component_probe create_component(
    std::string name,
    std::string status,
    const std::optional<std::string>& version = std::nullopt) {
    return {
        .name = std::move(name),
        .status = std::move(status),
        .version = version,
    };
}

[[nodiscard]] std::string get_psd_status(
    const native_environment_probe& probe,
    const bool needs_alias) {
    if (!probe.psdtoolkit_version.has_value()) {
        return "missing";
    }
    if (!probe.psd_profile.is_match) {
        return "incompatible";
    }
    return !needs_alias || probe.has_psd_alias ? "ready" : "missing";
}

[[nodiscard]] std::string get_gcmz_mutex_status(const gcmz_probe_result& probe) {
    if (probe.ok) {
        return "ready";
    }
    if (probe.error_code == "gcmz_mutex_missing") {
        return "missing";
    }
    if (probe.error_code == "gcmz_timeout" || probe.error_code == "gcmz_mutex_failed") {
        return "unavailable";
    }
    return probe.error_code == "gcmz_probe_failed" ? "faulted" : "ready";
}

[[nodiscard]] std::string get_gcmz_fmo_status(
    const gcmz_probe_result& probe,
    const std::string_view mutex_status) {
    if (probe.ok) {
        return "ready";
    }
    if (mutex_status != "ready") {
        return "unavailable";
    }
    if (probe.error_code == "gcmz_mapping_missing") {
        return "missing";
    }
    if (probe.error_code == "gcmz_mapping_failed") {
        return "faulted";
    }
    return "ready";
}

[[nodiscard]] std::string get_gcmz_api_status(
    const gcmz_probe_result& probe,
    const std::string_view fmo_status) {
    if (probe.ok) {
        return "ready";
    }
    if (fmo_status != "ready") {
        return "unavailable";
    }
    return probe.error_code == "gcmz_api_unsupported" ? "incompatible" : "ready";
}

[[nodiscard]] std::string get_gcmz_target_status(
    const gcmz_probe_result& probe,
    const std::string_view api_status) {
    if (probe.ok) {
        return "ready";
    }
    if (api_status != "ready") {
        return "unavailable";
    }
    if (probe.error_code == "gcmz_window_invalid"
        || probe.error_code == "gcmz_target_mismatch") {
        return "incompatible";
    }
    return "ready";
}

}  // namespace

native_environment_probe probe_native_environment(
    sdk_read_facade& sdk,
    const sdk_status_snapshot& status) {
    native_environment_probe probe;
    const sdk_effect_catalog_query_result catalog = sdk.query_effects({
        .offset = 0U,
        .limit = 1'000U,
    });
    if (catalog.ok) {
        probe.psdtoolkit_version = extract_psdtoolkit_version(catalog.catalog.modules);
        probe.psd_profile = detect_psd_profile(observe_psd_profile(
            sdk,
            catalog.catalog,
            probe.psdtoolkit_version));
    } else {
        probe.psd_profile.failures.emplace_back("sdk_query_failed");
    }
    const std::optional<std::filesystem::path> module_path =
        get_loaded_module_path(L"PSDToolKit.aux2");
    probe.psdtoolkit_config = module_path.has_value()
        ? read_psdtoolkit_config(*module_path)
        : psdtoolkit_config_result{
            .error_code = "module_missing",
            .error_message = "PSDToolKit.aux2 is not loaded",
        };
    probe.has_psd_alias = probe_psd_alias();
    probe.gcmz = gcmz_adapter().probe(
        GetCurrentProcessId(),
        utf8_to_path(status.project_path));
    return probe;
}

std::vector<native_component_probe> describe_native_environment(
    const native_environment_probe& probe) {
    const std::optional<std::string> gcmz_version = probe.gcmz.ok
        ? std::make_optional(std::to_string(probe.gcmz.gcmz_version))
        : std::nullopt;
    const std::string mutex_status = get_gcmz_mutex_status(probe.gcmz);
    const std::string fmo_status = get_gcmz_fmo_status(probe.gcmz, mutex_status);
    const std::string api_status = get_gcmz_api_status(probe.gcmz, fmo_status);
    const std::string target_status = get_gcmz_target_status(probe.gcmz, api_status);
    return {
        create_component(
            "psdtoolkit.effect",
            get_psd_status(probe, false),
            probe.psdtoolkit_version),
        create_component(
            "psdtoolkit.alias",
            get_psd_status(probe, true),
            probe.psdtoolkit_version),
        create_component("gcmzdrops.mutex", mutex_status, gcmz_version),
        create_component("gcmzdrops.fmo", fmo_status, gcmz_version),
        create_component("gcmzdrops.api.v3", api_status, gcmz_version),
        create_component("gcmzdrops.hwnd-pid", target_status, gcmz_version),
    };
}

}  // namespace aviutl2_mcp

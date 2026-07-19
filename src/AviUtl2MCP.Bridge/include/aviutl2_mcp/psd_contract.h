#pragma once

#include <filesystem>
#include <optional>
#include <string>
#include <vector>

namespace aviutl2_mcp {

inline constexpr char PSD_PROFILE_NAME[] = "ptk2-2.0.0alpha10-ja";
inline constexpr char PSD_PROFILE_VERSION[] = "2.0.0alpha10";
inline constexpr char PSD_SETUP_EFFECT[] = "最初に置くやつ@PSDToolKit";
inline constexpr char PSD_FILE_EFFECT[] = "PSDファイル@PSDToolKit";
inline constexpr char PSD_VOICE_EFFECT[] = "セリフ準備@PSDToolKit";

struct psd_observed_item final {
    std::string name;
    std::string type;
};

struct psd_observed_effect final {
    std::string name;
    std::vector<psd_observed_item> items;
};

struct psd_profile_observation final {
    std::optional<std::string> version;
    std::vector<psd_observed_effect> effects;
};

struct psd_profile_detection final {
    bool is_match = false;
    std::optional<std::string> profile;
    std::vector<std::string> failures;
};

enum class psd_voice_route {
    unavailable,
    direct_wav_txt,
    intermediate_object_audio_text_v1,
};

struct psdtoolkit_config_result final {
    bool ok = false;
    std::filesystem::path path;
    psd_voice_route voice_route = psd_voice_route::unavailable;
    bool external_wav_txt_pair = false;
    bool external_object_audio_text = false;
    std::string error_code;
    std::string error_message;
};

[[nodiscard]] psd_profile_detection detect_psd_profile(
    const psd_profile_observation& observation);

[[nodiscard]] psdtoolkit_config_result read_psdtoolkit_config(
    const std::filesystem::path& module_path);

[[nodiscard]] const char* to_string(psd_voice_route route) noexcept;

}  // namespace aviutl2_mcp

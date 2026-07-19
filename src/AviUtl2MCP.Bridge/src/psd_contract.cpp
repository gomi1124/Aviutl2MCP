#include "aviutl2_mcp/psd_contract.h"

#include <nlohmann/json.hpp>

#include <array>
#include <fstream>
#include <iterator>
#include <string_view>

namespace aviutl2_mcp {
namespace {

constexpr std::uintmax_t MAX_CONFIG_BYTES = 64U * 1024U;

struct required_item final {
    std::string_view effect;
    std::string_view item;
    std::string_view type;
};

constexpr std::array REQUIRED_EFFECTS{
    std::string_view(PSD_SETUP_EFFECT),
    std::string_view(PSD_FILE_EFFECT),
    std::string_view(PSD_VOICE_EFFECT),
};

constexpr std::array REQUIRED_ITEMS{
    required_item{PSD_FILE_EFFECT, "PSDファイル", "file"},
    required_item{PSD_FILE_EFFECT, "セーフガード", "check"},
    required_item{PSD_FILE_EFFECT, "タグ", "string"},
    required_item{PSD_FILE_EFFECT, "シーンID", "integer"},
    required_item{PSD_FILE_EFFECT, "キャラクターID", "string"},
    required_item{PSD_FILE_EFFECT, "レイヤー", "string"},
    required_item{PSD_VOICE_EFFECT, "キャラクターID", "string"},
    required_item{PSD_VOICE_EFFECT, "テキスト", "text"},
    required_item{PSD_VOICE_EFFECT, "音声ファイル", "file"},
};

[[nodiscard]] const psd_observed_effect* find_effect(
    const psd_profile_observation& observation,
    const std::string_view name) noexcept {
    for (const psd_observed_effect& effect : observation.effects) {
        if (effect.name == name) {
            return &effect;
        }
    }
    return nullptr;
}

[[nodiscard]] const psd_observed_item* find_item(
    const psd_observed_effect& effect,
    const std::string_view name) noexcept {
    for (const psd_observed_item& item : effect.items) {
        if (item.name == name) {
            return &item;
        }
    }
    return nullptr;
}

[[nodiscard]] psdtoolkit_config_result config_failure(
    const std::filesystem::path& path,
    std::string code,
    std::string message) {
    return psdtoolkit_config_result{
        .ok = false,
        .path = path,
        .voice_route = psd_voice_route::unavailable,
        .error_code = std::move(code),
        .error_message = std::move(message),
    };
}

}  // namespace

psd_profile_detection detect_psd_profile(const psd_profile_observation& observation) {
    psd_profile_detection result;
    if (!observation.version.has_value()) {
        result.failures.emplace_back("version_missing");
    } else if (*observation.version != PSD_PROFILE_VERSION) {
        result.failures.emplace_back("version_unsupported");
    }

    for (const std::string_view effect_name : REQUIRED_EFFECTS) {
        if (find_effect(observation, effect_name) == nullptr) {
            result.failures.emplace_back("effect_missing:" + std::string(effect_name));
        }
    }
    for (const required_item& required : REQUIRED_ITEMS) {
        const psd_observed_effect* effect = find_effect(observation, required.effect);
        if (effect == nullptr) {
            continue;
        }
        const psd_observed_item* item = find_item(*effect, required.item);
        if (item == nullptr) {
            result.failures.emplace_back(
                "item_missing:" + std::string(required.effect) + ":" + std::string(required.item));
        } else if (item->type != required.type) {
            result.failures.emplace_back(
                "item_type_mismatch:" + std::string(required.effect) + ":"
                + std::string(required.item));
        }
    }
    result.is_match = result.failures.empty();
    if (result.is_match) {
        result.profile = PSD_PROFILE_NAME;
    }
    return result;
}

psdtoolkit_config_result read_psdtoolkit_config(const std::filesystem::path& module_path) {
    const std::filesystem::path config_path = module_path.parent_path() / L"PSDToolKit.json";
    std::error_code error;
    const bool exists = std::filesystem::is_regular_file(config_path, error);
    if (error || !exists) {
        return config_failure(config_path, "config_missing", "PSDToolKit.json was not found");
    }
    const std::uintmax_t size = std::filesystem::file_size(config_path, error);
    if (error) {
        return config_failure(config_path, "config_read_failed", "PSDToolKit.json size could not be read");
    }
    if (size == 0U || size > MAX_CONFIG_BYTES) {
        return config_failure(config_path, "config_invalid", "PSDToolKit.json size is invalid");
    }

    std::ifstream input(config_path, std::ios::binary);
    if (!input) {
        return config_failure(config_path, "config_read_failed", "PSDToolKit.json could not be opened");
    }
    const std::string bytes{
        std::istreambuf_iterator<char>(input),
        std::istreambuf_iterator<char>()};
    try {
        const nlohmann::json document = nlohmann::json::parse(bytes);
        if (!document.is_object()
            || !document.contains("external_wav_txt_pair")
            || !document.at("external_wav_txt_pair").is_boolean()
            || !document.contains("external_object_audio_text")
            || !document.at("external_object_audio_text").is_boolean()) {
            return config_failure(
                config_path,
                "config_invalid",
                "PSDToolKit.json external route properties are missing or invalid");
        }
        const bool direct = document.at("external_wav_txt_pair").get<bool>();
        const bool intermediate = document.at("external_object_audio_text").get<bool>();
        return psdtoolkit_config_result{
            .ok = true,
            .path = config_path,
            .voice_route = direct
                ? psd_voice_route::direct_wav_txt
                : intermediate
                    ? psd_voice_route::intermediate_object_audio_text_v1
                    : psd_voice_route::unavailable,
            .external_wav_txt_pair = direct,
            .external_object_audio_text = intermediate,
        };
    } catch (const nlohmann::json::exception&) {
        return config_failure(config_path, "config_invalid", "PSDToolKit.json is not valid UTF-8 JSON");
    }
}

const char* to_string(const psd_voice_route route) noexcept {
    switch (route) {
        case psd_voice_route::direct_wav_txt:
            return "direct-wav-txt";
        case psd_voice_route::intermediate_object_audio_text_v1:
            return "intermediate-object-audio-text-v1";
        case psd_voice_route::unavailable:
        default:
            return "unavailable";
    }
}

}  // namespace aviutl2_mcp

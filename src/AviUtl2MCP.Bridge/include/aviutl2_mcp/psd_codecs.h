#pragma once

#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>

namespace aviutl2_mcp {

struct psd_value_validation final {
    bool ok = false;
    std::string error_code;
    std::string error_message;
};

[[nodiscard]] psd_value_validation validate_psd_character_id(std::string_view value);

[[nodiscard]] psd_value_validation validate_psd_layer_state(std::string_view value);

[[nodiscard]] std::string normalize_psd_voice_text(std::string_view text);

[[nodiscard]] std::string create_intermediate_voice_object(
    const std::filesystem::path& normalized_audio_path,
    std::string_view text);

[[nodiscard]] std::string create_psd_drop_object(
    std::string_view normalized_psd_path,
    std::uint32_t tag);

[[nodiscard]] std::string create_psd_subtitle_alias(
    std::string_view template_text,
    std::string_view character_id,
    int length);

[[nodiscard]] bool psd_subtitle_alias_matches(
    std::string_view expected_alias,
    std::string_view actual_alias) noexcept;

}  // namespace aviutl2_mcp

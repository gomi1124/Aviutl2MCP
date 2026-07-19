#pragma once

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

[[nodiscard]] std::string create_intermediate_voice_object(
    const std::filesystem::path& normalized_audio_path,
    std::string_view text);

[[nodiscard]] std::string create_psd_subtitle_alias(
    std::string_view template_text,
    std::string_view character_id);

}  // namespace aviutl2_mcp

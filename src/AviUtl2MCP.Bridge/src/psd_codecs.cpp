#include "aviutl2_mcp/psd_codecs.h"

#include "aviutl2_mcp/native_ipc_frame_codec.h"

#include <Windows.h>

#include <algorithm>
#include <cstdint>
#include <limits>
#include <span>
#include <stdexcept>

namespace aviutl2_mcp {
namespace {

constexpr std::size_t MAXIMUM_LAYER_STATE_BYTES = 65'536U;
constexpr std::size_t MAXIMUM_TEXT_BYTES = 64U * 1024U;
constexpr std::string_view SUBTITLE_PLACEHOLDER = "__AVIUTL2_MCP_CHARACTER_ID__";

[[nodiscard]] psd_value_validation validation_failure(
    std::string code,
    std::string message) {
    return {
        .error_code = std::move(code),
        .error_message = std::move(message),
    };
}

[[nodiscard]] bool has_forbidden_line_character(const std::string_view value) noexcept {
    return value.find('\0') != std::string_view::npos
        || value.find('\r') != std::string_view::npos
        || value.find('\n') != std::string_view::npos;
}

[[nodiscard]] std::size_t count_utf8_characters(const std::string_view value) noexcept {
    return static_cast<std::size_t>(std::ranges::count_if(value, [](const unsigned char byte) {
        return (byte & 0xc0U) != 0x80U;
    }));
}

[[nodiscard]] bool is_valid_utf8(const std::string_view value) noexcept {
    try {
        validate_utf8(std::span(
            reinterpret_cast<const std::uint8_t*>(value.data()),
            value.size()));
        return true;
    } catch (const std::invalid_argument&) {
        return false;
    }
}

[[nodiscard]] std::string wide_to_utf8(const std::wstring_view value) {
    if (value.empty()
        || value.size() > static_cast<std::size_t>((std::numeric_limits<int>::max)())) {
        throw std::invalid_argument("audio path is empty or too long");
    }
    const int required = WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (required <= 0) {
        throw std::invalid_argument("audio path is not valid UTF-16");
    }
    std::string result(static_cast<std::size_t>(required), '\0');
    if (WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            required,
            nullptr,
            nullptr) != required) {
        throw std::runtime_error("audio path UTF-8 conversion failed");
    }
    return result;
}

[[nodiscard]] std::string escape_voice_text(const std::string_view text) {
    if (text.size() > MAXIMUM_TEXT_BYTES || text.find('\0') != std::string_view::npos
        || !is_valid_utf8(text)) {
        throw std::invalid_argument("voice text is invalid or exceeds 64 KiB");
    }
    std::string escaped;
    escaped.reserve(text.size());
    for (std::size_t index = 0U; index < text.size(); ++index) {
        const char character = text[index];
        if (character == '\r') {
            if (index + 1U < text.size() && text[index + 1U] == '\n') {
                ++index;
            }
            escaped += "\\n";
        } else if (character == '\n') {
            escaped += "\\n";
        } else {
            escaped.push_back(character);
        }
    }
    return escaped;
}

[[nodiscard]] std::string escape_lua_string(const std::string_view value) {
    std::string escaped;
    escaped.reserve(value.size());
    constexpr char HEX[] = "0123456789abcdef";
    for (const unsigned char byte : value) {
        if (byte == '\\' || byte == '"') {
            escaped.push_back('\\');
            escaped.push_back(static_cast<char>(byte));
        } else if (byte < 0x20U || byte == 0x7fU) {
            escaped += "\\x";
            escaped.push_back(HEX[(byte >> 4U) & 0x0fU]);
            escaped.push_back(HEX[byte & 0x0fU]);
        } else {
            escaped.push_back(static_cast<char>(byte));
        }
    }
    return escaped;
}

[[nodiscard]] std::size_t count_occurrences(
    const std::string_view text,
    const std::string_view needle) noexcept {
    if (needle.empty()) {
        return 0U;
    }
    std::size_t count = 0U;
    std::size_t offset = 0U;
    while ((offset = text.find(needle, offset)) != std::string_view::npos) {
        ++count;
        offset += needle.size();
    }
    return count;
}

}  // namespace

psd_value_validation validate_psd_character_id(const std::string_view value) {
    if (value.empty() || has_forbidden_line_character(value) || !is_valid_utf8(value)) {
        return validation_failure(
            "invalid_argument",
            "characterId must be a non-empty single-line UTF-8 string");
    }
    if (count_utf8_characters(value) > 256U) {
        return validation_failure("invalid_argument", "characterId exceeds 256 characters");
    }
    return {.ok = true};
}

psd_value_validation validate_psd_layer_state(const std::string_view value) {
    if (value.empty() || value.size() > MAXIMUM_LAYER_STATE_BYTES
        || has_forbidden_line_character(value) || !is_valid_utf8(value)) {
        return validation_failure(
            "invalid_argument",
            "layerState must be a single-line UTF-8 value of at most 65536 bytes");
    }
    if (value != "L.0"
        && value.find("v0.") == std::string_view::npos
        && value.find("v1.") == std::string_view::npos) {
        return validation_failure(
            "invalid_argument",
            "layerState is not a canonical PSDToolKit2 state");
    }
    return {.ok = true};
}

std::string create_intermediate_voice_object(
    const std::filesystem::path& normalized_audio_path,
    const std::string_view text) {
    if (normalized_audio_path.empty() || !normalized_audio_path.is_absolute()) {
        throw std::invalid_argument("audio path must be absolute");
    }
    const std::string audio_path = wide_to_utf8(normalized_audio_path.lexically_normal().native());
    if (has_forbidden_line_character(audio_path)) {
        throw std::invalid_argument("audio path contains a forbidden character");
    }
    const std::string escaped_text = escape_voice_text(text);
    return "[0]\r\n"
        "frame=0,0\r\n"
        "[0.0]\r\n"
        "effect.name=音声ファイル\r\n"
        "ファイル=" + audio_path + "\r\n"
        "[1]\r\n"
        "frame=0,0\r\n"
        "[1.0]\r\n"
        "effect.name=テキスト\r\n"
        "テキスト=" + escaped_text + "\r\n";
}

std::string create_psd_subtitle_alias(
    const std::string_view template_text,
    const std::string_view character_id) {
    const psd_value_validation validation = validate_psd_character_id(character_id);
    if (!validation.ok) {
        throw std::invalid_argument(validation.error_message);
    }
    if (template_text.empty() || template_text.size() > MAXIMUM_TEXT_BYTES
        || !is_valid_utf8(template_text)
        || count_occurrences(template_text, "[Object]") != 1U
        || count_occurrences(template_text, "effect.name=テキスト") != 1U
        || count_occurrences(template_text, SUBTITLE_PLACEHOLDER) != 1U
        || count_occurrences(template_text, "require(\"PSDToolKit\").mes") != 1U) {
        throw std::invalid_argument("subtitle template does not match the V1 contract");
    }
    std::string alias(template_text);
    alias.replace(
        alias.find(SUBTITLE_PLACEHOLDER),
        SUBTITLE_PLACEHOLDER.size(),
        escape_lua_string(character_id));
    return alias;
}

}  // namespace aviutl2_mcp

#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace aviutl2_mcp {

struct preview_rgba_image final {
    int width;
    int height;
    std::vector<std::uint8_t> pixels;
};

struct preview_png_image final {
    int width;
    int height;
    std::vector<std::uint8_t> bytes;
};

[[nodiscard]] preview_rgba_image copy_preview_rgba(
    const void* buffer,
    int width,
    int height,
    int pitch);

[[nodiscard]] preview_png_image encode_preview_png(
    const preview_rgba_image& image,
    int maximum_width,
    int maximum_height,
    bool include_alpha);

}  // namespace aviutl2_mcp

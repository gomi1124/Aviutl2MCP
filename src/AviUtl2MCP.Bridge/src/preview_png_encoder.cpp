#include "aviutl2_mcp/preview_png_encoder.h"

#include "aviutl2_mcp/ipc_header.h"

#include <Windows.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <string>

namespace aviutl2_mcp {
namespace {

constexpr int MAXIMUM_SOURCE_DIMENSION = 16'384;
constexpr std::size_t MAXIMUM_SOURCE_BYTES = 512U * 1024U * 1024U;

void require_hresult(const HRESULT result, const char* operation) {
    if (FAILED(result)) {
        throw std::runtime_error(std::string(operation) + " failed");
    }
}

class com_scope final {
public:
    com_scope() {
        const HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (result == RPC_E_CHANGED_MODE) {
            return;
        }
        require_hresult(result, "CoInitializeEx");
        should_uninitialize_ = true;
    }

    ~com_scope() {
        if (should_uninitialize_) {
            CoUninitialize();
        }
    }

private:
    bool should_uninitialize_ = false;
};

[[nodiscard]] std::pair<int, int> calculate_output_size(
    const int width,
    const int height,
    const int maximum_width,
    const int maximum_height) {
    const double scale = (std::min)({
        1.0,
        static_cast<double>(maximum_width) / width,
        static_cast<double>(maximum_height) / height,
    });
    return {
        (std::max)(1, static_cast<int>(std::floor(width * scale))),
        (std::max)(1, static_cast<int>(std::floor(height * scale))),
    };
}

[[nodiscard]] std::vector<std::uint8_t> composite_opaque_black(
    const preview_rgba_image& image) {
    std::vector<std::uint8_t> pixels = image.pixels;
    for (std::size_t offset = 0U; offset < pixels.size(); offset += 4U) {
        const unsigned int alpha = pixels[offset + 3U];
        pixels[offset] = static_cast<std::uint8_t>((pixels[offset] * alpha + 127U) / 255U);
        pixels[offset + 1U] = static_cast<std::uint8_t>(
            (pixels[offset + 1U] * alpha + 127U) / 255U);
        pixels[offset + 2U] = static_cast<std::uint8_t>(
            (pixels[offset + 2U] * alpha + 127U) / 255U);
        pixels[offset + 3U] = 255U;
    }
    return pixels;
}

}  // namespace

preview_rgba_image copy_preview_rgba(
    const void* buffer,
    const int width,
    const int height,
    const int pitch) {
    if (buffer == nullptr || width <= 0 || height <= 0
        || width > MAXIMUM_SOURCE_DIMENSION || height > MAXIMUM_SOURCE_DIMENSION
        || pitch == (std::numeric_limits<int>::min)()) {
        throw std::invalid_argument("Preview RGBA dimensions or buffer are invalid");
    }
    const std::size_t row_bytes = static_cast<std::size_t>(width) * 4U;
    const std::size_t stride = static_cast<std::size_t>(pitch < 0 ? -pitch : pitch);
    const std::size_t pixel_bytes = row_bytes * static_cast<std::size_t>(height);
    if (stride < row_bytes || pixel_bytes > MAXIMUM_SOURCE_BYTES
        || stride > MAXIMUM_SOURCE_BYTES
        || stride * static_cast<std::size_t>(height) > MAXIMUM_SOURCE_BYTES) {
        throw std::invalid_argument("Preview RGBA pitch exceeds the supported limits");
    }
    const auto* source = static_cast<const std::uint8_t*>(buffer);
    std::vector<std::uint8_t> pixels(pixel_bytes);
    for (int row = 0; row < height; ++row) {
        const int source_row = pitch < 0 ? height - 1 - row : row;
        std::memcpy(
            pixels.data() + static_cast<std::size_t>(row) * row_bytes,
            source + static_cast<std::size_t>(source_row) * stride,
            row_bytes);
    }
    return preview_rgba_image{
        .width = width,
        .height = height,
        .pixels = std::move(pixels),
    };
}

preview_png_image encode_preview_png(
    const preview_rgba_image& image,
    const int maximum_width,
    const int maximum_height,
    const bool include_alpha) {
    const std::size_t expected_bytes = image.width > 0 && image.height > 0
        ? static_cast<std::size_t>(image.width) * static_cast<std::size_t>(image.height) * 4U
        : 0U;
    if (image.width <= 0 || image.height <= 0
        || image.width > MAXIMUM_SOURCE_DIMENSION || image.height > MAXIMUM_SOURCE_DIMENSION
        || image.pixels.size() != expected_bytes
        || maximum_width < 1 || maximum_width > 4096
        || maximum_height < 1 || maximum_height > 4096) {
        throw std::invalid_argument("Preview PNG input is outside the supported limits");
    }
    const auto [output_width, output_height] = calculate_output_size(
        image.width, image.height, maximum_width, maximum_height);
    std::vector<std::uint8_t> source_pixels = include_alpha
        ? image.pixels
        : composite_opaque_black(image);
    const UINT source_stride = static_cast<UINT>(image.width * 4);
    const UINT source_size = static_cast<UINT>(source_pixels.size());

    com_scope com;
    Microsoft::WRL::ComPtr<IWICImagingFactory> factory;
    require_hresult(CoCreateInstance(
        CLSID_WICImagingFactory,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&factory)), "CoCreateInstance WIC factory");
    Microsoft::WRL::ComPtr<IWICBitmap> bitmap;
    require_hresult(factory->CreateBitmapFromMemory(
        static_cast<UINT>(image.width),
        static_cast<UINT>(image.height),
        GUID_WICPixelFormat32bppRGBA,
        source_stride,
        source_size,
        source_pixels.data(),
        &bitmap), "CreateBitmapFromMemory");

    Microsoft::WRL::ComPtr<IWICBitmapSource> scaled_source = bitmap;
    Microsoft::WRL::ComPtr<IWICBitmapScaler> scaler;
    if (output_width != image.width || output_height != image.height) {
        require_hresult(factory->CreateBitmapScaler(&scaler), "CreateBitmapScaler");
        require_hresult(scaler->Initialize(
            bitmap.Get(),
            static_cast<UINT>(output_width),
            static_cast<UINT>(output_height),
            WICBitmapInterpolationModeFant), "Initialize bitmap scaler");
        scaled_source = scaler;
    }

    const WICPixelFormatGUID desired_format = include_alpha
        ? GUID_WICPixelFormat32bppRGBA
        : GUID_WICPixelFormat24bppRGB;
    IStream* raw_stream = nullptr;
    require_hresult(CreateStreamOnHGlobal(nullptr, TRUE, &raw_stream), "CreateStreamOnHGlobal");
    Microsoft::WRL::ComPtr<IStream> stream;
    stream.Attach(raw_stream);
    Microsoft::WRL::ComPtr<IWICBitmapEncoder> encoder;
    require_hresult(factory->CreateEncoder(
        GUID_ContainerFormatPng, nullptr, &encoder), "Create PNG encoder");
    require_hresult(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache),
        "Initialize PNG encoder");
    Microsoft::WRL::ComPtr<IWICBitmapFrameEncode> frame;
    require_hresult(encoder->CreateNewFrame(&frame, nullptr), "Create PNG frame");
    require_hresult(frame->Initialize(nullptr), "Initialize PNG frame");
    require_hresult(frame->SetSize(
        static_cast<UINT>(output_width),
        static_cast<UINT>(output_height)), "Set PNG size");
    WICPixelFormatGUID frame_format = desired_format;
    require_hresult(frame->SetPixelFormat(&frame_format), "Set PNG pixel format");
    Microsoft::WRL::ComPtr<IWICFormatConverter> converter;
    require_hresult(factory->CreateFormatConverter(&converter), "CreateFormatConverter");
    require_hresult(converter->Initialize(
        scaled_source.Get(),
        frame_format,
        WICBitmapDitherTypeNone,
        nullptr,
        0.0,
        WICBitmapPaletteTypeCustom), "Initialize format converter");
    require_hresult(frame->WriteSource(converter.Get(), nullptr), "Write PNG pixels");
    require_hresult(frame->Commit(), "Commit PNG frame");
    require_hresult(encoder->Commit(), "Commit PNG encoder");

    HGLOBAL global = nullptr;
    require_hresult(GetHGlobalFromStream(stream.Get(), &global), "GetHGlobalFromStream");
    const SIZE_T byte_count = GlobalSize(global);
    if (byte_count == 0U || byte_count > IPC_MAX_BINARY_BYTES) {
        throw std::runtime_error("Encoded PNG exceeds the IPC binary limit");
    }
    const void* bytes = GlobalLock(global);
    if (bytes == nullptr) {
        throw std::runtime_error("GlobalLock failed for encoded PNG");
    }
    std::vector<std::uint8_t> png(byte_count);
    std::memcpy(png.data(), bytes, byte_count);
    GlobalUnlock(global);
    return preview_png_image{
        .width = output_width,
        .height = output_height,
        .bytes = std::move(png),
    };
}

}  // namespace aviutl2_mcp

#pragma once

#include <array>
#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>

namespace aviutl2_mcp {

inline constexpr wchar_t GCMZ_MUTEX_NAME[] = L"GCMZDropsMutex";
inline constexpr wchar_t GCMZ_MAPPING_NAME[] = L"GCMZDrops";
inline constexpr std::int32_t GCMZ_REQUIRED_API_VERSION = 3;

struct gcmz_shared_data final {
    std::uint32_t window;
    std::int32_t width;
    std::int32_t height;
    std::int32_t video_rate;
    std::int32_t video_scale;
    std::int32_t audio_rate;
    std::int32_t audio_channels;
    std::int32_t api_version;
    std::array<wchar_t, 260> project_path;
    std::uint32_t flags;
    std::uint32_t aviutl_version;
    std::uint32_t gcmz_version;
};

struct gcmz_probe_result final {
    bool ok = false;
    std::uint32_t window = 0U;
    std::uint32_t process_id = 0U;
    std::int32_t api_version = 0;
    std::optional<std::filesystem::path> project_path;
    std::uint32_t aviutl_version = 0U;
    std::uint32_t gcmz_version = 0U;
    std::string error_code;
    std::string error_message;
};

struct gcmz_drop_request final {
    int layer = 0;
    int frame_advance = 0;
    int margin = -1;
    std::vector<std::filesystem::path> files;
};

struct gcmz_send_result final {
    bool ok = false;
    gcmz_probe_result target;
    std::string payload;
    std::string error_code;
    std::string error_message;
};

[[nodiscard]] gcmz_probe_result evaluate_gcmz_shared_data(
    const gcmz_shared_data& data,
    bool is_window,
    std::uint32_t actual_process_id,
    std::uint32_t expected_process_id,
    const std::optional<std::filesystem::path>& expected_project_path);

[[nodiscard]] std::string create_gcmz_drop_payload(const gcmz_drop_request& request);

class gcmz_client {
public:
    virtual ~gcmz_client() = default;

    [[nodiscard]] virtual gcmz_probe_result probe(
        std::uint32_t expected_process_id,
        const std::optional<std::filesystem::path>& expected_project_path,
        std::uint32_t timeout_ms = 2'000U) const noexcept = 0;

    [[nodiscard]] virtual gcmz_send_result send_files(
        const gcmz_drop_request& request,
        std::uint32_t expected_process_id,
        const std::optional<std::filesystem::path>& expected_project_path,
        std::uint32_t timeout_ms = 10'000U) const noexcept = 0;
};

class gcmz_adapter final : public gcmz_client {
public:
    [[nodiscard]] gcmz_probe_result probe(
        std::uint32_t expected_process_id,
        const std::optional<std::filesystem::path>& expected_project_path,
        std::uint32_t timeout_ms = 2'000U) const noexcept override;

    [[nodiscard]] gcmz_send_result send_files(
        const gcmz_drop_request& request,
        std::uint32_t expected_process_id,
        const std::optional<std::filesystem::path>& expected_project_path,
        std::uint32_t timeout_ms = 10'000U) const noexcept override;
};

}  // namespace aviutl2_mcp

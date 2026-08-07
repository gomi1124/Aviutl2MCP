#include "aviutl2_mcp/scene_list_ui_operator.h"

#include <Windows.h>
#include <ShlObj.h>

#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <cstddef>
#include <cstdlib>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <limits>
#include <map>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <system_error>
#include <utility>
#include <vector>

namespace aviutl2_mcp {
namespace {

constexpr wchar_t SCENE_LIST_SECTION[] = L"Window.scene.list";
constexpr wchar_t SCENE_LIST_MENU_LABEL[] = L"シーンリスト";
constexpr wchar_t SCENE_LIST_MENU_LABEL_ENGLISH[] = L"Scene List";
constexpr std::uintmax_t MAXIMUM_PROJECT_FILE_BYTES = 64U * 1024U * 1024U;
constexpr std::size_t MAXIMUM_SCENES = 4096U;
constexpr int STATUS_BAR_HEIGHT_AT_96_DPI = 23;
constexpr int PANEL_HEADER_HEIGHT_AT_96_DPI = 24;
constexpr int SCENE_ROW_HEIGHT_AT_96_DPI = 28;
constexpr int SCENE_ROW_CLICK_X_AT_96_DPI = 160;

struct scene_catalog_entry final {
    std::size_t row_index;
    int scene_id;
    std::string name;
};

struct scene_list_layout final {
    double left;
    double top;
    double right;
    double bottom;
    bool is_hidden;
    bool is_floating;
};

struct menu_match final {
    UINT command_id = 0U;
    bool is_enabled = false;
    std::size_t count = 0U;
};

[[nodiscard]] scene_list_open_command_result create_failure(
    std::string code,
    std::string message,
    const bool was_dispatched = false) {
    return {
        .command_was_dispatched = was_dispatched,
        .error_code = std::move(code),
        .error_message = std::move(message),
    };
}

[[nodiscard]] std::wstring normalize_menu_label(std::wstring label) {
    if (const std::size_t shortcut = label.find(L'\t'); shortcut != std::wstring::npos) {
        label.erase(shortcut);
    }
    std::erase(label, L'&');
    while (!label.empty() && std::iswspace(label.front()) != 0) {
        label.erase(label.begin());
    }
    while (!label.empty() && std::iswspace(label.back()) != 0) {
        label.pop_back();
    }
    return label;
}

void find_scene_list_menu_command(const HMENU menu, menu_match& result) {
    if (menu == nullptr) {
        return;
    }
    const int count = GetMenuItemCount(menu);
    for (int position = 0; position < count; ++position) {
        MENUITEMINFOW info{};
        info.cbSize = sizeof(info);
        info.fMask = MIIM_ID | MIIM_STATE | MIIM_SUBMENU | MIIM_STRING;
        if (GetMenuItemInfoW(menu, static_cast<UINT>(position), TRUE, &info) == FALSE) {
            continue;
        }
        std::vector<wchar_t> text(static_cast<std::size_t>(info.cch) + 1U, L'\0');
        info.dwTypeData = text.data();
        info.cch = static_cast<UINT>(text.size());
        if (GetMenuItemInfoW(menu, static_cast<UINT>(position), TRUE, &info) != FALSE) {
            const std::wstring label = normalize_menu_label(text.data());
            if (label == SCENE_LIST_MENU_LABEL
                || label == SCENE_LIST_MENU_LABEL_ENGLISH) {
                ++result.count;
                result.command_id = info.wID;
                result.is_enabled = (info.fState & (MFS_DISABLED | MFS_GRAYED)) == 0U;
            }
        }
        find_scene_list_menu_command(info.hSubMenu, result);
    }
}

[[nodiscard]] std::wstring utf8_to_wide(const std::string& value) {
    if (value.empty()) {
        return {};
    }
    const int count = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0);
    if (count <= 0) {
        throw std::invalid_argument("Project path is not valid UTF-8");
    }
    std::wstring result(static_cast<std::size_t>(count), L'\0');
    if (MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            count) != count) {
        throw std::invalid_argument("Project path conversion failed");
    }
    return result;
}

[[nodiscard]] std::filesystem::path get_executable_directory() {
    std::vector<wchar_t> buffer(1024U, L'\0');
    while (buffer.size() <= 32U * 1024U) {
        const DWORD written = GetModuleFileNameW(
            nullptr,
            buffer.data(),
            static_cast<DWORD>(buffer.size()));
        if (written == 0U) {
            throw std::runtime_error("AviUtl2 executable path is unavailable");
        }
        if (written < buffer.size() - 1U) {
            return std::filesystem::path(buffer.data(), buffer.data() + written).parent_path();
        }
        buffer.resize(buffer.size() * 2U, L'\0');
    }
    throw std::runtime_error("AviUtl2 executable path is too long");
}

[[nodiscard]] std::filesystem::path locate_layout_file() {
    const std::filesystem::path portable =
        get_executable_directory() / L"data" / L"aviutl2.ini";
    std::error_code error;
    if (std::filesystem::is_regular_file(portable, error)) {
        return portable;
    }

    PWSTR raw_program_data = nullptr;
    if (SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &raw_program_data) != S_OK
        || raw_program_data == nullptr) {
        throw std::runtime_error("AviUtl2 application data directory is unavailable");
    }
    const std::filesystem::path installed =
        std::filesystem::path(raw_program_data) / L"aviutl2" / L"aviutl2.ini";
    CoTaskMemFree(raw_program_data);
    error.clear();
    if (!std::filesystem::is_regular_file(installed, error)) {
        throw std::runtime_error("AviUtl2 layout file was not found");
    }
    return installed;
}

[[nodiscard]] std::wstring read_layout_value(
    const std::filesystem::path& path,
    const wchar_t* key) {
    std::array<wchar_t, 128U> buffer{};
    const DWORD written = GetPrivateProfileStringW(
        SCENE_LIST_SECTION,
        key,
        L"",
        buffer.data(),
        static_cast<DWORD>(buffer.size()),
        path.c_str());
    if (written == 0U || written >= buffer.size() - 1U) {
        throw std::runtime_error("AviUtl2 scene list layout is incomplete");
    }
    return std::wstring(buffer.data(), written);
}

[[nodiscard]] double read_layout_ratio(
    const std::filesystem::path& path,
    const wchar_t* key) {
    const std::wstring value = read_layout_value(path, key);
    wchar_t* end = nullptr;
    const double parsed = std::wcstod(value.c_str(), &end);
    if (end == value.c_str() || *end != L'\0' || !std::isfinite(parsed)
        || parsed < 0.0 || parsed > 1.0) {
        throw std::runtime_error("AviUtl2 scene list layout ratio is invalid");
    }
    return parsed;
}

[[nodiscard]] bool read_layout_boolean(
    const std::filesystem::path& path,
    const wchar_t* key) {
    const std::wstring value = read_layout_value(path, key);
    if (value == L"0") {
        return false;
    }
    if (value == L"1") {
        return true;
    }
    throw std::runtime_error("AviUtl2 scene list layout flag is invalid");
}

[[nodiscard]] scene_list_layout read_scene_list_layout() {
    const std::filesystem::path path = locate_layout_file();
    scene_list_layout layout{
        .left = read_layout_ratio(path, L"left"),
        .top = read_layout_ratio(path, L"top"),
        .right = read_layout_ratio(path, L"right"),
        .bottom = read_layout_ratio(path, L"bottom"),
        .is_hidden = read_layout_boolean(path, L"hide"),
        .is_floating = read_layout_boolean(path, L"floating"),
    };
    if (layout.right <= layout.left || layout.bottom <= layout.top) {
        throw std::runtime_error("AviUtl2 scene list layout rectangle is invalid");
    }
    return layout;
}

[[nodiscard]] std::optional<std::size_t> parse_scene_section(std::string_view line) {
    constexpr std::string_view prefix = "[scene.";
    if (!line.starts_with(prefix) || !line.ends_with(']')) {
        return std::nullopt;
    }
    line.remove_prefix(prefix.size());
    line.remove_suffix(1U);
    std::size_t value = 0U;
    const auto parsed = std::from_chars(line.data(), line.data() + line.size(), value);
    if (parsed.ec != std::errc{} || parsed.ptr != line.data() + line.size()
        || value >= MAXIMUM_SCENES) {
        throw std::runtime_error("AviUtl2 project has an invalid scene section");
    }
    return value;
}

[[nodiscard]] int parse_scene_id(std::string_view value) {
    int scene_id = -1;
    const auto parsed = std::from_chars(value.data(), value.data() + value.size(), scene_id);
    if (parsed.ec != std::errc{} || parsed.ptr != value.data() + value.size()
        || scene_id < 0) {
        throw std::runtime_error("AviUtl2 project has an invalid scene ID");
    }
    return scene_id;
}

[[nodiscard]] std::vector<scene_catalog_entry> read_scene_catalog(
    const std::filesystem::path& project_path) {
    std::error_code error;
    const std::uintmax_t file_size = std::filesystem::file_size(project_path, error);
    if (error || file_size > MAXIMUM_PROJECT_FILE_BYTES) {
        throw std::runtime_error("AviUtl2 project scene catalog is unavailable");
    }
    std::ifstream input(project_path, std::ios::binary);
    if (!input) {
        throw std::runtime_error("AviUtl2 project could not be opened for scene lookup");
    }

    std::map<std::size_t, scene_catalog_entry> entries;
    std::optional<std::size_t> current_row;
    std::optional<int> current_scene_id;
    std::optional<std::string> current_name;
    const auto finish_current = [&]() {
        if (!current_row.has_value()) {
            return;
        }
        if (!current_scene_id.has_value() || !current_name.has_value()) {
            throw std::runtime_error("AviUtl2 project scene metadata is incomplete");
        }
        scene_catalog_entry entry{
            .row_index = *current_row,
            .scene_id = *current_scene_id,
            .name = *current_name,
        };
        if (!entries.emplace(*current_row, std::move(entry)).second) {
            throw std::runtime_error("AviUtl2 project has duplicate scene rows");
        }
        current_row.reset();
        current_scene_id.reset();
        current_name.reset();
    };

    std::string line;
    bool is_first_line = true;
    while (std::getline(input, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        if (is_first_line && line.starts_with("\xEF\xBB\xBF")) {
            line.erase(0U, 3U);
        }
        is_first_line = false;
        if (!line.empty() && line.front() == '[') {
            finish_current();
            current_row = parse_scene_section(line);
            continue;
        }
        if (!current_row.has_value()) {
            continue;
        }
        if (line.starts_with("scene=")) {
            current_scene_id = parse_scene_id(std::string_view(line).substr(6U));
        } else if (line.starts_with("name=")) {
            current_name = line.substr(5U);
        }
    }
    finish_current();
    if (input.bad() || entries.empty()) {
        throw std::runtime_error("AviUtl2 project scene catalog is empty or unreadable");
    }
    if (entries.size() > MAXIMUM_SCENES) {
        throw std::runtime_error("AviUtl2 project has too many scenes");
    }

    std::vector<scene_catalog_entry> result;
    result.reserve(entries.size());
    std::size_t expected_row = 0U;
    for (auto& [row, entry] : entries) {
        if (row != expected_row++) {
            throw std::runtime_error("AviUtl2 project scene rows are not contiguous");
        }
        result.push_back(std::move(entry));
    }
    return result;
}

[[nodiscard]] const scene_catalog_entry& resolve_target(
    const std::vector<scene_catalog_entry>& scenes,
    const scene_list_target& target) {
    const bool has_scene_id = target.scene_id.has_value();
    const bool has_scene_name = target.scene_name.has_value();
    if (has_scene_id == has_scene_name) {
        throw std::invalid_argument("Exactly one scene selector is required");
    }
    const scene_catalog_entry* match = nullptr;
    for (const scene_catalog_entry& scene : scenes) {
        const bool is_match = has_scene_id
            ? scene.scene_id == *target.scene_id
            : scene.name == *target.scene_name;
        if (!is_match) {
            continue;
        }
        if (match != nullptr) {
            throw std::domain_error("The scene selector matched multiple scenes");
        }
        match = &scene;
    }
    if (match == nullptr) {
        throw std::out_of_range("The requested scene was not found");
    }
    return *match;
}

[[nodiscard]] bool send_message_with_timeout(
    const HWND window,
    const UINT message,
    const WPARAM wparam,
    const LPARAM lparam,
    const std::uint64_t deadline) {
    const std::uint64_t now = GetTickCount64();
    if (now >= deadline) {
        SetLastError(ERROR_TIMEOUT);
        return false;
    }
    const std::uint64_t remaining = deadline - now;
    DWORD_PTR result = 0U;
    return SendMessageTimeoutW(
        window,
        message,
        wparam,
        lparam,
        SMTO_ABORTIFHUNG | SMTO_BLOCK,
        static_cast<UINT>((std::min)(remaining, std::uint64_t{60'000U})),
        &result) != 0;
}

[[nodiscard]] scene_list_open_command_result activate_scene_row(
    const HWND host_window,
    const scene_list_layout& layout,
    const std::size_t row_index,
    const std::size_t scene_count,
    const std::uint32_t timeout_ms,
    const scene_list_snapshot& target) {
    if (layout.is_floating) {
        return create_failure(
            "ui_automation_failed",
            "Floating AviUtl2 scene list layouts are not supported");
    }
    if (layout.is_hidden) {
        menu_match match;
        find_scene_list_menu_command(GetMenu(host_window), match);
        if (match.count != 1U || !match.is_enabled) {
            return create_failure(
                "ui_automation_failed",
                "AviUtl2 scene list display command is unavailable");
        }
        const std::uint64_t show_deadline = GetTickCount64() + timeout_ms;
        if (!send_message_with_timeout(
                host_window,
                WM_COMMAND,
                MAKEWPARAM(match.command_id, 0),
                0,
                show_deadline)) {
            return create_failure(
                GetLastError() == ERROR_TIMEOUT ? "operation_timeout" : "ui_automation_failed",
                "AviUtl2 scene list could not be displayed",
                true);
        }
    }

    RECT client{};
    if (GetClientRect(host_window, &client) == FALSE
        || client.right <= client.left || client.bottom <= client.top) {
        return create_failure(
            "ui_automation_failed",
            "AviUtl2 host client rectangle is unavailable");
    }
    const UINT dpi = (std::max)(GetDpiForWindow(host_window), 96U);
    const int status_height = MulDiv(STATUS_BAR_HEIGHT_AT_96_DPI, dpi, 96);
    const int header_height = MulDiv(PANEL_HEADER_HEIGHT_AT_96_DPI, dpi, 96);
    const int row_height = MulDiv(SCENE_ROW_HEIGHT_AT_96_DPI, dpi, 96);
    const int layout_width = client.right - client.left;
    const int layout_height = client.bottom - client.top - status_height;
    if (layout_height <= header_height || row_height <= 0) {
        return create_failure("ui_automation_failed", "AviUtl2 scene list layout is too small");
    }

    const int panel_left = static_cast<int>(std::lround(layout.left * layout_width));
    const int panel_top = static_cast<int>(std::lround(layout.top * layout_height));
    const int panel_right = static_cast<int>(std::lround(layout.right * layout_width));
    const int panel_bottom = static_cast<int>(std::lround(layout.bottom * layout_height));
    const int panel_width = panel_right - panel_left;
    const int panel_height = panel_bottom - panel_top;
    const int visible_rows = (panel_height - header_height) / row_height;
    if (panel_width <= 0 || visible_rows <= 0) {
        return create_failure("ui_automation_failed", "AviUtl2 scene list has no visible rows");
    }

    const int click_x = panel_left + (std::min)(
        panel_width / 2,
        MulDiv(SCENE_ROW_CLICK_X_AT_96_DPI, dpi, 96));
    const int wheel_y = panel_top + header_height + row_height / 2;
    POINT wheel_screen{.x = click_x, .y = wheel_y};
    if (ClientToScreen(host_window, &wheel_screen) == FALSE) {
        return create_failure("ui_automation_failed", "AviUtl2 scene list coordinates are unavailable");
    }

    UINT scroll_lines = 3U;
    if (SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, &scroll_lines, 0) == FALSE
        || scroll_lines == 0U) {
        scroll_lines = 3U;
    }
    if (scroll_lines == WHEEL_PAGESCROLL) {
        scroll_lines = static_cast<UINT>(visible_rows);
    }
    scroll_lines = (std::max)(scroll_lines, 1U);
    const std::size_t maximum_offset = scene_count > static_cast<std::size_t>(visible_rows)
        ? scene_count - static_cast<std::size_t>(visible_rows)
        : 0U;
    const std::size_t desired_offset = row_index >= static_cast<std::size_t>(visible_rows)
        ? (std::min)(
            maximum_offset,
            row_index - static_cast<std::size_t>(visible_rows / 2))
        : 0U;
    const std::size_t scroll_notches = desired_offset == 0U
        ? 0U
        : (desired_offset + scroll_lines - 1U) / scroll_lines;
    const std::size_t actual_offset = (std::min)(
        maximum_offset,
        scroll_notches * scroll_lines);
    const std::size_t visible_row = row_index - actual_offset;
    if (visible_row >= static_cast<std::size_t>(visible_rows)) {
        return create_failure(
            "ui_automation_failed",
            "AviUtl2 scene row could not be placed in the visible list area");
    }

    const std::uint64_t deadline = GetTickCount64() + timeout_ms;
    const std::size_t reset_notches = scene_count / scroll_lines + 2U;
    const LPARAM wheel_position = MAKELPARAM(wheel_screen.x, wheel_screen.y);
    for (std::size_t index = 0U; index < reset_notches; ++index) {
        if (!send_message_with_timeout(
                host_window,
                WM_MOUSEWHEEL,
                MAKEWPARAM(0, static_cast<WORD>(WHEEL_DELTA)),
                wheel_position,
                deadline)) {
            return create_failure(
                GetLastError() == ERROR_TIMEOUT ? "operation_timeout" : "ui_automation_failed",
                "AviUtl2 scene list did not scroll to its first row");
        }
    }
    for (std::size_t index = 0U; index < scroll_notches; ++index) {
        if (!send_message_with_timeout(
                host_window,
                WM_MOUSEWHEEL,
                MAKEWPARAM(0, static_cast<WORD>(static_cast<SHORT>(-WHEEL_DELTA))),
                wheel_position,
                deadline)) {
            return create_failure(
                GetLastError() == ERROR_TIMEOUT ? "operation_timeout" : "ui_automation_failed",
                "AviUtl2 scene list did not scroll to the requested row");
        }
    }

    const int click_y = panel_top + header_height
        + static_cast<int>(visible_row) * row_height + row_height / 2;
    const LPARAM click_position = MAKELPARAM(click_x, click_y);
    const std::array<std::pair<UINT, WPARAM>, 4U> messages{{
        {WM_LBUTTONDOWN, MK_LBUTTON},
        {WM_LBUTTONUP, 0U},
        {WM_LBUTTONDBLCLK, MK_LBUTTON},
        {WM_LBUTTONUP, 0U},
    }};
    bool was_dispatched = false;
    for (const auto& [message, wparam] : messages) {
        if (!send_message_with_timeout(
                host_window,
                message,
                wparam,
                click_position,
                deadline)) {
            return create_failure(
                GetLastError() == ERROR_TIMEOUT ? "operation_timeout" : "ui_automation_failed",
                "AviUtl2 scene list did not accept the selection command",
                was_dispatched);
        }
        was_dispatched = true;
    }
    return {
        .ok = true,
        .command_was_dispatched = true,
        .target = target,
    };
}

}  // namespace

scene_list_open_command_result open_scene_from_list(
    void* raw_host_window,
    const std::string& project_path,
    const scene_list_target& target,
    const std::uint32_t timeout_ms) {
    const HWND host_window = static_cast<HWND>(raw_host_window);
    if (host_window == nullptr || IsWindow(host_window) == FALSE) {
        return create_failure(
            "sdk_not_available",
            "AviUtl2 host application window is unavailable");
    }
    DWORD process_id = 0U;
    if (GetWindowThreadProcessId(host_window, &process_id) == 0U
        || process_id != GetCurrentProcessId()) {
        return create_failure(
            "ui_automation_failed",
            "AviUtl2 host application window ownership is invalid");
    }
    if (timeout_ms == 0U) {
        return create_failure("invalid_argument", "Scene open timeout must be positive");
    }
    try {
        const std::filesystem::path path(utf8_to_wide(project_path));
        const std::vector<scene_catalog_entry> scenes = read_scene_catalog(path);
        const scene_catalog_entry& entry = resolve_target(scenes, target);
        const scene_list_snapshot expected{
            .scene_id = entry.scene_id,
            .name = entry.name,
        };
        return activate_scene_row(
            host_window,
            read_scene_list_layout(),
            entry.row_index,
            scenes.size(),
            timeout_ms,
            expected);
    } catch (const std::invalid_argument& exception) {
        return create_failure("invalid_argument", exception.what());
    } catch (const std::domain_error& exception) {
        return create_failure("scene_ambiguous", exception.what());
    } catch (const std::out_of_range& exception) {
        return create_failure("scene_not_found", exception.what());
    } catch (const std::exception& exception) {
        return create_failure("ui_automation_failed", exception.what());
    }
}

}  // namespace aviutl2_mcp

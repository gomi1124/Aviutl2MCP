#include "aviutl2_mcp/sdk_read_facade.h"

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/native_ipc_frame_codec.h"

#include <Windows.h>

#include "plugin2.h"

#include <algorithm>
#include <atomic>
#include <charconv>
#include <cmath>
#include <exception>
#include <limits>
#include <span>
#include <stdexcept>
#include <unordered_map>

namespace aviutl2_mcp {
namespace {

constexpr int MAXIMUM_SELECTED_OBJECTS = 100'000;
constexpr int MAXIMUM_EFFECTS_PER_OBJECT = 1'024;
constexpr std::size_t MAXIMUM_TIMELINE_ITEMS = 1'000U;
constexpr std::size_t MAXIMUM_TIMELINE_OFFSET = 1'000'000U;
constexpr std::size_t MAXIMUM_TIMELINE_SCAN = 1'000'000U;
constexpr std::size_t MAXIMUM_ALIAS_BYTES = 1024U * 1024U;
constexpr std::size_t MAXIMUM_EFFECT_ITEMS = 1'000U;
constexpr std::size_t MAXIMUM_EFFECT_ITEM_VALUE_BYTES = 64U * 1024U;
std::atomic<sdk_read_facade*> REGISTERED_FACADE = nullptr;

[[nodiscard]] std::string to_utf8(const LPCWSTR value) {
    if (value == nullptr || *value == L'\0') {
        return {};
    }
    const int byte_count = WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        value,
        -1,
        nullptr,
        0,
        nullptr,
        nullptr);
    if (byte_count <= 1) {
        throw std::runtime_error("WideCharToMultiByte failed while sizing SDK text");
    }
    std::string result(static_cast<std::size_t>(byte_count), '\0');
    if (WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value,
            -1,
            result.data(),
            byte_count,
            nullptr,
            nullptr)
        == 0) {
        throw std::runtime_error("WideCharToMultiByte failed while copying SDK text");
    }
    result.pop_back();
    return result;
}

[[nodiscard]] std::string copy_utf8(const LPCSTR value) {
    if (value == nullptr) {
        return {};
    }
    std::string result(value);
    validate_utf8(std::span(
        reinterpret_cast<const std::uint8_t*>(result.data()),
        result.size()));
    return result;
}

[[nodiscard]] std::optional<std::string> find_media_path(const std::string& alias) {
    std::size_t line_start = 0U;
    while (line_start < alias.size()) {
        std::size_t line_end = alias.find_first_of("\r\n", line_start);
        if (line_end == std::string::npos) {
            line_end = alias.size();
        }
        const std::string_view line(alias.data() + line_start, line_end - line_start);
        const std::size_t separator = line.find('=');
        if (separator != std::string_view::npos && separator + 1U < line.size()) {
            const std::string_view value = line.substr(separator + 1U);
            const bool is_drive_path = value.size() >= 3U
                && ((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z'))
                && value[1] == ':'
                && (value[2] == '\\' || value[2] == '/');
            const bool is_unc_path = value.size() >= 2U
                && value[0] == '\\'
                && value[1] == '\\';
            if (is_drive_path || is_unc_path) {
                return std::string(value);
            }
        }
        line_start = line_end;
        while (line_start < alias.size() && (alias[line_start] == '\r' || alias[line_start] == '\n')) {
            ++line_start;
        }
    }
    return std::nullopt;
}

[[nodiscard]] char fold_ascii(const char value) noexcept {
    return value >= 'A' && value <= 'Z' ? static_cast<char>(value - 'A' + 'a') : value;
}

[[nodiscard]] bool path_equals(const std::string& left, const std::string& right) noexcept {
    if (left.size() != right.size()) {
        return false;
    }
    for (std::size_t index = 0U; index < left.size(); ++index) {
        const char left_value = left[index] == '/' ? '\\' : fold_ascii(left[index]);
        const char right_value = right[index] == '/' ? '\\' : fold_ascii(right[index]);
        if (left_value != right_value) {
            return false;
        }
    }
    return true;
}

[[nodiscard]] std::string effect_item_type_name(const int type) {
    return std::to_string(type);
}

struct effect_item_copy_context final {
    effect_fingerprint* effect;
    std::string error;
};

void copy_effect_item(void* raw_context, const LPCWSTR name, const int type) noexcept {
    auto* context = static_cast<effect_item_copy_context*>(raw_context);
    try {
        context->effect->items.push_back(effect_item_fingerprint{
            .name = to_utf8(name),
            .type = effect_item_type_name(type),
        });
    } catch (const std::exception& exception) {
        context->error = exception.what();
    } catch (...) {
        context->error = "SDK effect item callback failed with an unknown exception";
    }
}

[[nodiscard]] std::vector<OBJECT_HANDLE> copy_selected_objects(EDIT_SECTION& edit) {
    std::vector<OBJECT_HANDLE> result;
    if (edit.get_selected_object_num == nullptr || edit.get_selected_object == nullptr) {
        return result;
    }
    const int count = edit.get_selected_object_num();
    if (count < 0 || count > MAXIMUM_SELECTED_OBJECTS) {
        throw std::runtime_error("SDK returned an invalid selected object count");
    }
    result.reserve(static_cast<std::size_t>(count));
    for (int index = 0; index < count; ++index) {
        OBJECT_HANDLE object = edit.get_selected_object(index);
        if (object != nullptr) {
            result.push_back(object);
        }
    }
    return result;
}

[[nodiscard]] bool is_selected_object(
    const OBJECT_HANDLE object,
    const std::vector<OBJECT_HANDLE>& selected_objects) noexcept {
    return std::ranges::find(selected_objects, object) != selected_objects.end();
}

[[nodiscard]] std::vector<sdk_effect_summary> copy_effects(
    EDIT_HANDLE& edit_handle,
    EDIT_SECTION& edit,
    const OBJECT_HANDLE object,
    std::vector<effect_fingerprint>& fingerprints,
    std::vector<EFFECT_HANDLE>* copied_handles = nullptr) {
    if (edit.get_effect_list == nullptr || edit.get_effect_name == nullptr) {
        throw std::runtime_error("SDK effect enumeration functions are unavailable");
    }
    const int effect_count = edit.get_effect_list(object, nullptr, 0);
    if (effect_count < 0 || effect_count > MAXIMUM_EFFECTS_PER_OBJECT) {
        throw std::runtime_error("SDK returned an invalid effect count");
    }
    std::vector<EFFECT_HANDLE> handles(static_cast<std::size_t>(effect_count));
    const int copied_count = effect_count == 0
        ? 0
        : edit.get_effect_list(object, handles.data(), effect_count);
    if (copied_count < 0 || copied_count > effect_count) {
        throw std::runtime_error("SDK copied an invalid effect handle count");
    }
    handles.resize(static_cast<std::size_t>(copied_count));
    if (copied_handles != nullptr) {
        *copied_handles = handles;
    }

    std::vector<sdk_effect_summary> summaries;
    summaries.reserve(handles.size());
    fingerprints.reserve(handles.size());
    std::unordered_map<std::string, int> occurrences;
    for (const EFFECT_HANDLE effect : handles) {
        const LPCWSTR raw_name = edit.get_effect_name(effect);
        if (raw_name == nullptr) {
            throw std::runtime_error("SDK effect name was unavailable");
        }
        const std::wstring wide_name(raw_name);
        const std::string name = to_utf8(wide_name.c_str());
        const int occurrence = occurrences[name]++;
        effect_fingerprint fingerprint{
            .name = name,
            .items = {},
        };
        if (edit_handle.enum_effect_item != nullptr) {
            effect_item_copy_context item_context{.effect = &fingerprint};
            const bool was_enumerated = edit_handle.enum_effect_item(
                wide_name.c_str(),
                &item_context,
                &copy_effect_item);
            if (!item_context.error.empty()) {
                throw std::runtime_error(item_context.error);
            }
            if (!was_enumerated) {
                fingerprint.items.clear();
            }
        }
        fingerprints.push_back(std::move(fingerprint));
        summaries.push_back(sdk_effect_summary{
            .name = name,
            .occurrence = occurrence,
            .is_enabled = edit.get_effect_enable == nullptr || edit.get_effect_enable(effect),
            .is_locked = edit.get_effect_lock != nullptr && edit.get_effect_lock(effect),
        });
    }
    return summaries;
}

[[nodiscard]] sdk_object_snapshot copy_object_snapshot(
    EDIT_HANDLE& edit_handle,
    EDIT_SECTION& edit,
    const EDIT_INFO& info,
    const OBJECT_HANDLE object,
    const OBJECT_LAYER_FRAME& position,
    const std::vector<OBJECT_HANDLE>& selected_objects,
    const bool include_effects,
    std::vector<EFFECT_HANDLE>* copied_effect_handles = nullptr) {
    const std::string alias = copy_utf8(edit.get_object_alias(object));
    if (alias.size() > MAXIMUM_ALIAS_BYTES) {
        throw std::runtime_error("SDK object alias exceeded the supported limit");
    }
    const LPCWSTR raw_name = edit.get_object_name == nullptr ? nullptr : edit.get_object_name(object);
    std::vector<effect_fingerprint> fingerprints;
    std::vector<sdk_effect_summary> effects = copy_effects(
        edit_handle,
        edit,
        object,
        fingerprints,
        copied_effect_handles);
    if (!include_effects) {
        effects.clear();
    }
    return sdk_object_snapshot{
        .candidate = object_candidate{
            .scene_id = info.scene_id,
            .layer = position.layer + 1,
            .start_frame = position.start + 1,
            .end_frame = position.end + 1,
            .name = to_utf8(raw_name),
            .alias = {alias.begin(), alias.end()},
            .effects = std::move(fingerprints),
        },
        .is_selected = is_selected_object(object, selected_objects),
        .media_path = find_media_path(alias),
        .effects = std::move(effects),
    };
}

struct effect_item_codec final {
    const char* type;
    const char* codec;
    bool is_writable;
};

[[nodiscard]] effect_item_codec get_effect_item_codec(const int type) noexcept {
    switch (type) {
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_INTEGER:
            return {"integer", "integer", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_NUMBER:
            return {"number", "number", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_CHECK:
            return {"check", "check01", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_TEXT:
            return {"text", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_STRING:
            return {"string", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_FILE:
            return {"file", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_COLOR:
            return {"color", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_SELECT:
            return {"select", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_SCENE:
            return {"scene", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_RANGE:
            return {"range", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_COMBO:
            return {"combo", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_MASK:
            return {"mask", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_FONT:
            return {"font", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_FIGURE:
            return {"figure", "aliasString", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_DATA:
            return {"data", "unsupported", false};
        case EDIT_HANDLE::EFFECT_ITEM_TYPE_FOLDER:
            return {"folder", "unsupported", false};
        default:
            return {"unknown", "unsupported", false};
    }
}

[[nodiscard]] std::optional<sdk_effect_item_value> decode_effect_item_value(
    const effect_item_codec& codec,
    const std::string& raw_value,
    bool& is_writable) {
    if (raw_value.size() > MAXIMUM_EFFECT_ITEM_VALUE_BYTES) {
        throw std::runtime_error("SDK effect item value exceeded the supported limit");
    }
    if (std::string_view(codec.codec) == "integer") {
        std::int64_t value = 0;
        const auto [position, error] = std::from_chars(
            raw_value.data(),
            raw_value.data() + raw_value.size(),
            value);
        if (error != std::errc{} || position != raw_value.data() + raw_value.size()) {
            is_writable = false;
            return std::nullopt;
        }
        return value;
    }
    if (std::string_view(codec.codec) == "number") {
        double value = 0.0;
        const auto [position, error] = std::from_chars(
            raw_value.data(),
            raw_value.data() + raw_value.size(),
            value,
            std::chars_format::general);
        if (error != std::errc{} || position != raw_value.data() + raw_value.size()
            || !std::isfinite(value)) {
            is_writable = false;
            return std::nullopt;
        }
        return value;
    }
    if (std::string_view(codec.codec) == "check01") {
        if (raw_value == "0") {
            return false;
        }
        if (raw_value == "1") {
            return true;
        }
        is_writable = false;
        return std::nullopt;
    }
    if (std::string_view(codec.codec) == "aliasString") {
        return raw_value;
    }
    return std::nullopt;
}

struct effect_item_value_context final {
    EDIT_SECTION* edit;
    EFFECT_HANDLE effect;
    std::vector<sdk_effect_item_snapshot>* items;
    std::string error;
};

void copy_effect_item_value(void* raw_context, const LPCWSTR raw_name, const int type) noexcept {
    auto* context = static_cast<effect_item_value_context*>(raw_context);
    if (!context->error.empty()) {
        return;
    }
    try {
        if (context->items->size() >= MAXIMUM_EFFECT_ITEMS) {
            throw std::runtime_error("SDK effect item count exceeded the supported limit");
        }
        const std::wstring name(raw_name == nullptr ? L"" : raw_name);
        const effect_item_codec codec = get_effect_item_codec(type);
        const LPCSTR raw_value = context->edit->get_effect_item_value == nullptr
            ? nullptr
            : context->edit->get_effect_item_value(context->effect, name.c_str());
        bool is_writable = codec.is_writable;
        std::optional<sdk_effect_item_value> value;
        if (raw_value != nullptr && std::string_view(codec.codec) != "unsupported") {
            value = decode_effect_item_value(codec, copy_utf8(raw_value), is_writable);
        }
        context->items->push_back(sdk_effect_item_snapshot{
            .name = to_utf8(name.c_str()),
            .type = codec.type,
            .codec = codec.codec,
            .is_writable = is_writable,
            .value = std::move(value),
            .choices = {},
        });
    } catch (const std::exception& exception) {
        context->error = exception.what();
    } catch (...) {
        context->error = "SDK effect item value callback failed with an unknown exception";
    }
}

[[nodiscard]] std::vector<sdk_effect_items_group> copy_object_effect_items(
    EDIT_HANDLE& edit_handle,
    EDIT_SECTION& edit,
    const std::vector<EFFECT_HANDLE>& effect_handles,
    const std::vector<sdk_effect_summary>& effect_summaries) {
    if (effect_handles.size() != effect_summaries.size()) {
        throw std::runtime_error("SDK effect handle and summary counts differed");
    }
    std::vector<sdk_effect_items_group> groups;
    groups.reserve(effect_handles.size());
    for (std::size_t index = 0U; index < effect_handles.size(); ++index) {
        sdk_effect_items_group group{
            .effect = effect_summaries[index],
            .items = {},
        };
        if (edit_handle.enum_effect_item != nullptr) {
            std::wstring wide_name;
            const LPCWSTR raw_name = edit.get_effect_name(effect_handles[index]);
            if (raw_name == nullptr) {
                throw std::runtime_error("SDK effect name was unavailable while copying items");
            }
            wide_name = raw_name;
            effect_item_value_context item_context{
                .edit = &edit,
                .effect = effect_handles[index],
                .items = &group.items,
            };
            const bool was_enumerated = edit_handle.enum_effect_item(
                wide_name.c_str(),
                &item_context,
                &copy_effect_item_value);
            if (!item_context.error.empty()) {
                throw std::runtime_error(item_context.error);
            }
            if (!was_enumerated) {
                group.items.clear();
            }
        }
        groups.push_back(std::move(group));
    }
    return groups;
}

struct object_read_context final {
    EDIT_HANDLE* edit_handle;
    const object_locator* locator;
    bool include_alias;
    bool include_effect_items;
    sdk_object_detail_snapshot* detail;
    bool was_called = false;
    bool found_candidate = false;
    std::string error;
};

void copy_object_detail(void* raw_context, EDIT_SECTION* edit) noexcept {
    auto* context = static_cast<object_read_context*>(raw_context);
    context->was_called = true;
    try {
        if (edit == nullptr || edit->info == nullptr
            || edit->find_object == nullptr || edit->get_object_layer_frame == nullptr
            || edit->get_object_alias == nullptr) {
            throw std::runtime_error("SDK object detail functions are unavailable");
        }
        const EDIT_INFO& info = *edit->info;
        if (info.scene_id != context->locator->scene_id) {
            return;
        }
        const int sdk_layer = context->locator->layer - 1;
        const int sdk_frame = context->locator->start_frame - 1;
        OBJECT_HANDLE object = edit->find_object(sdk_layer, sdk_frame);
        if (object == nullptr) {
            return;
        }
        const OBJECT_LAYER_FRAME position = edit->get_object_layer_frame(object);
        if (position.layer != sdk_layer || position.start != sdk_frame) {
            return;
        }
        const std::vector<OBJECT_HANDLE> selected_objects = copy_selected_objects(*edit);
        std::vector<EFFECT_HANDLE> effect_handles;
        context->detail->object = copy_object_snapshot(
            *context->edit_handle,
            *edit,
            info,
            object,
            position,
            selected_objects,
            true,
            &effect_handles);
        if (context->include_alias) {
            const std::vector<std::uint8_t>& alias = context->detail->object.candidate.alias;
            context->detail->alias = std::string(alias.begin(), alias.end());
        }
        if (context->include_effect_items) {
            context->detail->effect_items = copy_object_effect_items(
                *context->edit_handle,
                *edit,
                effect_handles,
                context->detail->object.effects);
        }
        context->found_candidate = true;
    } catch (const std::exception& exception) {
        context->error = exception.what();
    } catch (...) {
        context->error = "SDK object detail callback failed with an unknown exception";
    }
}

[[nodiscard]] bool matches_object_query(
    const sdk_object_snapshot& object,
    const sdk_timeline_query& query) {
    if (query.name_contains.has_value()
        && object.candidate.name.find(*query.name_contains) == std::string::npos) {
        return false;
    }
    if (query.effect_name.has_value()
        && std::ranges::none_of(object.candidate.effects, [&query](const effect_fingerprint& effect) {
            return effect.name == *query.effect_name;
        })) {
        return false;
    }
    if (query.media_path.has_value()
        && (!object.media_path.has_value() || !path_equals(*object.media_path, *query.media_path))) {
        return false;
    }
    return true;
}

struct timeline_read_context final {
    EDIT_HANDLE* edit_handle;
    const sdk_timeline_query* query;
    sdk_timeline_snapshot* timeline;
    bool was_called = false;
    std::string error;
};

[[nodiscard]] int calculate_default_range_end(const int start, const int count) {
    const std::int64_t normalized_count = (std::max)(1, count);
    const std::int64_t end = static_cast<std::int64_t>(start) + normalized_count - 1;
    if (end > (std::numeric_limits<int>::max)()) {
        throw std::runtime_error("SDK display range overflowed");
    }
    return static_cast<int>(end);
}

void copy_timeline(void* raw_context, EDIT_SECTION* edit) noexcept {
    auto* context = static_cast<timeline_read_context*>(raw_context);
    context->was_called = true;
    try {
        if (edit == nullptr || edit->info == nullptr) {
            throw std::runtime_error("SDK timeline read omitted edit information");
        }
        if (edit->find_object == nullptr || edit->get_object_layer_frame == nullptr
            || edit->get_object_alias == nullptr) {
            throw std::runtime_error("SDK timeline enumeration functions are unavailable");
        }
        const EDIT_INFO& info = *edit->info;
        const sdk_timeline_query& query = *context->query;
        if (query.scene_id.has_value() && *query.scene_id != info.scene_id) {
            return;
        }

        const int default_layer_start = query.use_display_defaults
            ? (std::max)(0, info.display_layer_start)
            : 0;
        const int default_layer_end = query.use_display_defaults
            ? calculate_default_range_end(default_layer_start, info.display_layer_num)
            : (std::max)(default_layer_start, info.layer_max);
        const int layer_start = query.layer_start.has_value()
            ? *query.layer_start - 1
            : default_layer_start;
        const int layer_end = query.layer_end.has_value()
            ? *query.layer_end - 1
            : default_layer_end;
        const int default_frame_start = query.use_display_defaults
            ? (std::max)(0, info.display_frame_start)
            : 0;
        const int default_frame_end = query.use_display_defaults
            ? calculate_default_range_end(default_frame_start, info.display_frame_num)
            : (std::max)(default_frame_start, info.frame_max);
        const int frame_start = query.start_frame.has_value()
            ? *query.start_frame - 1
            : default_frame_start;
        const int frame_end = query.end_frame.has_value()
            ? *query.end_frame - 1
            : default_frame_end;
        if (layer_start < 0 || layer_end < layer_start || layer_end - layer_start >= 1'000
            || frame_start < 0 || frame_end < frame_start) {
            throw std::invalid_argument("Timeline range is outside the supported limits");
        }

        context->timeline->layers.reserve(static_cast<std::size_t>(layer_end - layer_start + 1));
        for (int layer = layer_start; layer <= layer_end; ++layer) {
            const LPCWSTR raw_name = edit->get_layer_name == nullptr ? nullptr : edit->get_layer_name(layer);
            context->timeline->layers.push_back(sdk_layer_snapshot{
                .scene_id = info.scene_id,
                .layer = layer + 1,
                .name = to_utf8(raw_name),
                .is_visible = edit->get_layer_enable == nullptr || edit->get_layer_enable(layer),
                .is_locked = edit->get_layer_lock != nullptr && edit->get_layer_lock(layer),
            });
        }

        const std::vector<OBJECT_HANDLE> selected_objects = copy_selected_objects(*edit);
        std::size_t matching_count = 0U;
        std::size_t scanned_count = 0U;
        bool page_complete = false;
        for (int layer = layer_start; layer <= layer_end && !page_complete; ++layer) {
            int search_frame = 0;
            while (!page_complete) {
                if (++scanned_count > MAXIMUM_TIMELINE_SCAN) {
                    throw std::runtime_error("SDK timeline scan exceeded the safety limit");
                }
                OBJECT_HANDLE object = edit->find_object(layer, search_frame);
                if (object == nullptr) {
                    break;
                }
                const OBJECT_LAYER_FRAME position = edit->get_object_layer_frame(object);
                if (position.layer != layer || position.start < search_frame || position.end < position.start) {
                    throw std::runtime_error("SDK timeline object order was invalid");
                }
                if (position.start > frame_end) {
                    break;
                }
                if (position.end == (std::numeric_limits<int>::max)()) {
                    page_complete = true;
                } else {
                    search_frame = position.end + 1;
                }
                if (position.end < frame_start) {
                    continue;
                }

                sdk_object_snapshot snapshot = copy_object_snapshot(
                    *context->edit_handle,
                    *edit,
                    info,
                    object,
                    position,
                    selected_objects,
                    query.include_effects);
                if (!matches_object_query(snapshot, query)) {
                    continue;
                }
                if (matching_count++ < query.offset) {
                    continue;
                }
                context->timeline->objects.push_back(std::move(snapshot));
                if (context->timeline->objects.size() > query.limit) {
                    context->timeline->objects.pop_back();
                    context->timeline->is_truncated = true;
                    context->timeline->next_offset = query.offset + query.limit;
                    page_complete = true;
                }
            }
        }
    } catch (const std::exception& exception) {
        context->error = exception.what();
    } catch (...) {
        context->error = "SDK timeline callback failed with an unknown exception";
    }
}

void capture_loaded_project(PROJECT_FILE* project) noexcept {
    sdk_read_facade* facade = REGISTERED_FACADE.load();
    if (facade != nullptr) {
        facade->capture_project(project, true);
    }
}

void capture_saved_project(PROJECT_FILE* project) noexcept {
    sdk_read_facade* facade = REGISTERED_FACADE.load();
    if (facade != nullptr) {
        facade->capture_project(project, false);
    }
}

[[nodiscard]] sdk_edit_state map_edit_state(const int value) noexcept {
    switch (value) {
        case EDIT_HANDLE::EDIT_STATE_EDIT:
            return sdk_edit_state::edit;
        case EDIT_HANDLE::EDIT_STATE_PLAY:
            return sdk_edit_state::play;
        case EDIT_HANDLE::EDIT_STATE_SAVE:
            return sdk_edit_state::save;
        default:
            return sdk_edit_state::unknown;
    }
}

struct project_read_context final {
    sdk_project_snapshot* project;
    bool include_scenes;
    bool was_called = false;
    std::string error;
};

void copy_project(void* raw_context, EDIT_SECTION* edit) noexcept {
    auto* context = static_cast<project_read_context*>(raw_context);
    context->was_called = true;
    try {
        if (edit == nullptr || edit->info == nullptr) {
            throw std::runtime_error("SDK read section omitted edit information");
        }
        const EDIT_INFO& info = *edit->info;
        if (info.width <= 0 || info.height <= 0 || info.rate <= 0 || info.scale <= 0
            || info.sample_rate <= 0 || info.frame < 0 || info.scene_id < 0) {
            throw std::runtime_error("SDK returned invalid project dimensions or timing");
        }

        sdk_project_snapshot& project = *context->project;
        project.width = info.width;
        project.height = info.height;
        project.frame_rate = static_cast<double>(info.rate) / static_cast<double>(info.scale);
        project.sample_rate = info.sample_rate;
        project.current_scene_id = info.scene_id;
        project.current_frame = info.frame + 1;
        if (info.select_range_start >= 0 && info.select_range_end >= info.select_range_start) {
            project.selection = sdk_selection{
                .start_frame = info.select_range_start + 1,
                .end_frame = info.select_range_end + 1,
            };
        }

        if (edit->get_selected_object_num != nullptr
            && edit->get_selected_object != nullptr
            && edit->get_object_layer_frame != nullptr) {
            const int selected_count = edit->get_selected_object_num();
            if (selected_count < 0 || selected_count > MAXIMUM_SELECTED_OBJECTS) {
                throw std::runtime_error("SDK returned an invalid selected object count");
            }
            for (int index = 0; index < selected_count; ++index) {
                OBJECT_HANDLE object = edit->get_selected_object(index);
                if (object == nullptr) {
                    continue;
                }
                const OBJECT_LAYER_FRAME position = edit->get_object_layer_frame(object);
                if (position.layer < 0 || position.layer == (std::numeric_limits<int>::max)()) {
                    throw std::runtime_error("SDK returned an invalid selected layer");
                }
                project.selected_layers.push_back(position.layer + 1);
            }
            std::ranges::sort(project.selected_layers);
            const auto unique_end = std::ranges::unique(project.selected_layers).begin();
            project.selected_layers.erase(unique_end, project.selected_layers.end());
        } else if (info.layer >= 0) {
            project.selected_layers.push_back(info.layer + 1);
        }

        if (context->include_scenes) {
            const LPCWSTR scene_name = edit->get_scene_name == nullptr
                ? nullptr
                : edit->get_scene_name();
            project.scenes.push_back(sdk_scene_summary{
                .scene_id = info.scene_id,
                .name = to_utf8(scene_name),
            });
        }
    } catch (const std::exception& exception) {
        context->error = exception.what();
    } catch (...) {
        context->error = "SDK project callback failed with an unknown exception";
    }
}

}  // namespace

sdk_read_facade::~sdk_read_facade() {
    detach();
}

bool sdk_read_facade::register_host(HOST_APP_TABLE* host) noexcept {
    if (host == nullptr
        || host->create_edit_handle == nullptr
        || host->register_project_load_handler == nullptr
        || host->register_project_save_handler == nullptr) {
        return false;
    }

    try {
        EDIT_HANDLE* edit_handle = host->create_edit_handle();
        if (edit_handle == nullptr
            || edit_handle->get_edit_info == nullptr
            || edit_handle->get_edit_state == nullptr
            || edit_handle->call_read_section_param == nullptr) {
            return false;
        }
        {
            std::scoped_lock lock(mutex_);
            edit_handle_ = edit_handle;
            project_state_ = sdk_project_state::unknown;
            project_path_.reset();
            project_cache_error_.clear();
        }
        REGISTERED_FACADE.store(this);
        host->register_project_load_handler(&capture_loaded_project);
        host->register_project_save_handler(&capture_saved_project);
        return true;
    } catch (...) {
        detach();
        return false;
    }
}

void sdk_read_facade::detach() noexcept {
    sdk_read_facade* expected = this;
    static_cast<void>(REGISTERED_FACADE.compare_exchange_strong(expected, nullptr));
    std::scoped_lock lock(mutex_);
    edit_handle_ = nullptr;
    project_state_ = sdk_project_state::unknown;
    project_path_.reset();
    project_cache_error_.clear();
}

sdk_status_snapshot sdk_read_facade::query_status() const noexcept {
    sdk_status_snapshot result;
    EDIT_HANDLE* edit_handle = nullptr;
    {
        std::scoped_lock lock(mutex_);
        edit_handle = edit_handle_;
        result.is_sdk_ready = edit_handle != nullptr;
        result.project_state = project_state_;
        result.project_path = project_path_;
        if (!project_cache_error_.empty()) {
            result.has_query_error = true;
            result.query_error = project_cache_error_;
        }
    }
    if (edit_handle == nullptr) {
        return result;
    }

    try {
        EDIT_INFO info{};
        edit_handle->get_edit_info(&info, sizeof(info));
        result.edit_state = map_edit_state(edit_handle->get_edit_state());
    } catch (const std::exception& exception) {
        result.has_query_error = true;
        result.query_error = exception.what();
        result.edit_state = sdk_edit_state::unknown;
    } catch (...) {
        result.has_query_error = true;
        result.query_error = "SDK status query failed with an unknown exception";
        result.edit_state = sdk_edit_state::unknown;
    }
    return result;
}

sdk_project_query_result sdk_read_facade::query_project(const bool include_scenes) const noexcept {
    const sdk_status_snapshot status = query_status();
    if (!status.is_sdk_ready) {
        return {
            .ok = false,
            .error_code = "sdk_not_available",
            .error_message = "AviUtl2 SDK edit handle is not available",
        };
    }
    if (status.has_query_error) {
        return {
            .ok = false,
            .error_code = "sdk_query_failed",
            .error_message = status.query_error,
        };
    }
    if (status.project_state != sdk_project_state::saved
        && status.project_state != sdk_project_state::unsaved) {
        return {
            .ok = false,
            .error_code = "project_not_open",
            .error_message = "No AviUtl2 project is open",
        };
    }

    EDIT_HANDLE* edit_handle = nullptr;
    {
        std::scoped_lock lock(mutex_);
        edit_handle = edit_handle_;
    }
    sdk_project_snapshot project{
        .path = status.project_path,
        .is_saved = status.project_state == sdk_project_state::saved,
    };
    project_read_context callback_context{
        .project = &project,
        .include_scenes = include_scenes,
    };
    try {
        const bool was_scheduled = edit_handle->call_read_section_param(&callback_context, &copy_project);
        if (!was_scheduled) {
            return {
                .ok = false,
                .error_code = "read_not_available",
                .error_message = "AviUtl2 rejected the read section",
            };
        }
        if (!callback_context.was_called) {
            return {
                .ok = false,
                .error_code = "sdk_query_failed",
                .error_message = "AviUtl2 did not invoke the read callback",
            };
        }
        if (!callback_context.error.empty()) {
            return {
                .ok = false,
                .error_code = "sdk_query_failed",
                .error_message = callback_context.error,
            };
        }
        return {
            .ok = true,
            .project = std::move(project),
        };
    } catch (const std::exception& exception) {
        return {
            .ok = false,
            .error_code = "sdk_query_failed",
            .error_message = exception.what(),
        };
    } catch (...) {
        return {
            .ok = false,
            .error_code = "sdk_query_failed",
            .error_message = "SDK project query failed with an unknown exception",
        };
    }
}

sdk_timeline_query_result sdk_read_facade::query_timeline(const sdk_timeline_query& query) const noexcept {
    if (query.limit == 0U || query.limit > MAXIMUM_TIMELINE_ITEMS
        || query.offset > MAXIMUM_TIMELINE_OFFSET
        || (query.layer_start.has_value() && query.layer_end.has_value()
            && *query.layer_end - *query.layer_start >= 1'000)) {
        return {
            .ok = false,
            .error_code = "invalid_argument",
            .error_message = "Timeline page is outside the supported limits",
        };
    }
    const sdk_status_snapshot status = query_status();
    if (!status.is_sdk_ready) {
        return {
            .ok = false,
            .error_code = "sdk_not_available",
            .error_message = "AviUtl2 SDK edit handle is not available",
        };
    }
    if (status.has_query_error) {
        return {
            .ok = false,
            .error_code = "sdk_query_failed",
            .error_message = status.query_error,
        };
    }
    if (status.project_state != sdk_project_state::saved
        && status.project_state != sdk_project_state::unsaved) {
        return {
            .ok = false,
            .error_code = "project_not_open",
            .error_message = "No AviUtl2 project is open",
        };
    }

    EDIT_HANDLE* edit_handle = nullptr;
    {
        std::scoped_lock lock(mutex_);
        edit_handle = edit_handle_;
    }
    sdk_timeline_snapshot timeline;
    timeline_read_context callback_context{
        .edit_handle = edit_handle,
        .query = &query,
        .timeline = &timeline,
    };
    try {
        const bool was_scheduled = edit_handle->call_read_section_param(&callback_context, &copy_timeline);
        if (!was_scheduled) {
            return {
                .ok = false,
                .error_code = "read_not_available",
                .error_message = "AviUtl2 rejected the timeline read section",
            };
        }
        if (!callback_context.was_called) {
            return {
                .ok = false,
                .error_code = "sdk_query_failed",
                .error_message = "AviUtl2 did not invoke the timeline callback",
            };
        }
        if (!callback_context.error.empty()) {
            return {
                .ok = false,
                .error_code = "sdk_query_failed",
                .error_message = callback_context.error,
            };
        }
        return {
            .ok = true,
            .timeline = std::move(timeline),
        };
    } catch (const std::exception& exception) {
        return {
            .ok = false,
            .error_code = "sdk_query_failed",
            .error_message = exception.what(),
        };
    } catch (...) {
        return {
            .ok = false,
            .error_code = "sdk_query_failed",
            .error_message = "SDK timeline query failed with an unknown exception",
        };
    }
}

sdk_object_query_result sdk_read_facade::query_object(
    const object_locator& locator,
    const std::string& current_instance_id,
    const std::string& current_project_generation,
    const bool include_alias,
    const bool include_effect_items) const noexcept {
    if (!uuid_equals(locator.instance_id, current_instance_id)
        || !uuid_equals(locator.project_generation, current_project_generation)
        || locator.scene_id < 0 || locator.layer < 1 || locator.start_frame < 1
        || locator.end_frame < locator.start_frame) {
        return {
            .ok = false,
            .error_code = "invalid_argument",
            .error_message = "Object locator identity or coordinates are invalid",
        };
    }
    const sdk_status_snapshot status = query_status();
    if (!status.is_sdk_ready) {
        return {
            .ok = false,
            .error_code = "sdk_not_available",
            .error_message = "AviUtl2 SDK edit handle is not available",
        };
    }
    if (status.has_query_error) {
        return {
            .ok = false,
            .error_code = "sdk_query_failed",
            .error_message = status.query_error,
        };
    }
    if (status.project_state != sdk_project_state::saved
        && status.project_state != sdk_project_state::unsaved) {
        return {
            .ok = false,
            .error_code = "project_not_open",
            .error_message = "No AviUtl2 project is open",
        };
    }

    EDIT_HANDLE* edit_handle = nullptr;
    {
        std::scoped_lock lock(mutex_);
        edit_handle = edit_handle_;
    }
    if (edit_handle == nullptr || edit_handle->call_read_section_param == nullptr) {
        return {
            .ok = false,
            .error_code = "sdk_not_available",
            .error_message = "AviUtl2 SDK edit handle is not available",
        };
    }
    sdk_object_detail_snapshot detail;
    object_read_context callback_context{
        .edit_handle = edit_handle,
        .locator = &locator,
        .include_alias = include_alias,
        .include_effect_items = include_effect_items,
        .detail = &detail,
    };
    try {
        const bool was_scheduled = edit_handle->call_read_section_param(&callback_context, &copy_object_detail);
        if (!was_scheduled) {
            return {
                .ok = false,
                .error_code = "read_not_available",
                .error_message = "AviUtl2 rejected the object read section",
            };
        }
        if (!callback_context.was_called) {
            return {
                .ok = false,
                .error_code = "sdk_query_failed",
                .error_message = "AviUtl2 did not invoke the object callback",
            };
        }
        if (!callback_context.error.empty()) {
            return {
                .ok = false,
                .error_code = "sdk_query_failed",
                .error_message = callback_context.error,
            };
        }
        if (!callback_context.found_candidate) {
            return {
                .ok = false,
                .error_code = "object_not_found",
                .error_message = "The object locator could not be resolved",
            };
        }
        const object_candidate& candidate = detail.object.candidate;
        const locator_resolution resolution = resolve_object_locator(
            locator,
            current_instance_id,
            current_project_generation,
            std::span<const object_candidate>(&candidate, 1U));
        if (resolution.status != locator_resolution_status::resolved) {
            return {
                .ok = false,
                .error_code = resolution.status == locator_resolution_status::ambiguous
                    ? "object_ambiguous"
                    : "object_not_found",
                .error_message = resolution.status == locator_resolution_status::ambiguous
                    ? "The object locator matched multiple candidates"
                    : "The object locator no longer matches the object",
            };
        }
        return {
            .ok = true,
            .detail = std::move(detail),
        };
    } catch (const std::exception& exception) {
        return {
            .ok = false,
            .error_code = "sdk_query_failed",
            .error_message = exception.what(),
        };
    } catch (...) {
        return {
            .ok = false,
            .error_code = "sdk_query_failed",
            .error_message = "SDK object query failed with an unknown exception",
        };
    }
}

void sdk_read_facade::capture_project(PROJECT_FILE* project, const bool is_load) noexcept {
    sdk_project_state state = sdk_project_state::not_open;
    std::optional<std::string> path;
    std::string error;
    try {
        if (project != nullptr) {
            if (project->get_project_file_path == nullptr) {
                throw std::runtime_error("SDK project path function is unavailable");
            }
            const std::string copied_path = to_utf8(project->get_project_file_path());
            if (copied_path.empty()) {
                state = sdk_project_state::unsaved;
            } else {
                state = sdk_project_state::saved;
                path = copied_path;
            }
        }
    } catch (const std::exception& exception) {
        state = sdk_project_state::unknown;
        error = exception.what();
    } catch (...) {
        state = sdk_project_state::unknown;
        error = "SDK project callback failed with an unknown exception";
    }

    std::function<void()> callback;
    {
        std::scoped_lock lock(mutex_);
        project_state_ = state;
        project_path_ = std::move(path);
        project_cache_error_ = std::move(error);
        if (is_load) {
            callback = project_loaded_callback_;
        }
    }
    if (callback) {
        try {
            callback();
        } catch (...) {
            return;
        }
    }
}

void sdk_read_facade::set_project_loaded_callback(std::function<void()> callback) {
    if (!callback) {
        throw std::invalid_argument("Project loaded callback must not be empty");
    }
    std::scoped_lock lock(mutex_);
    project_loaded_callback_ = std::move(callback);
}

void sdk_read_facade::clear_project_loaded_callback() noexcept {
    std::scoped_lock lock(mutex_);
    project_loaded_callback_ = {};
}

sdk_read_facade& get_sdk_read_facade() noexcept {
    static sdk_read_facade facade;
    return facade;
}

}  // namespace aviutl2_mcp

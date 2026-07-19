#include "aviutl2_mcp/sdk_read_facade.h"

#include <Windows.h>

#include "plugin2.h"

#include <algorithm>
#include <atomic>
#include <exception>
#include <limits>
#include <stdexcept>

namespace aviutl2_mcp {
namespace {

constexpr int MAXIMUM_SELECTED_OBJECTS = 100'000;
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

void capture_loaded_project(PROJECT_FILE* project) noexcept {
    sdk_read_facade* facade = REGISTERED_FACADE.load();
    if (facade != nullptr) {
        facade->capture_project(project);
    }
}

void capture_saved_project(PROJECT_FILE* project) noexcept {
    capture_loaded_project(project);
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

void sdk_read_facade::capture_project(PROJECT_FILE* project) noexcept {
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

    std::scoped_lock lock(mutex_);
    project_state_ = state;
    project_path_ = std::move(path);
    project_cache_error_ = std::move(error);
}

sdk_read_facade& get_sdk_read_facade() noexcept {
    static sdk_read_facade facade;
    return facade;
}

}  // namespace aviutl2_mcp

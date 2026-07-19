add_library(AviUtl2MCP.Bridge.Core STATIC
  src/at_most_once_store.cpp
  src/bridge_identity.cpp
  src/bridge_runtime.cpp
  src/bridge_version.cpp
  src/cancellation_registry.cpp
  src/command_gate.cpp
  src/handshake.cpp
  src/instance_descriptor.cpp
  src/ipc_header.cpp
  src/locator_resolver.cpp
  src/named_pipe_server.cpp
  src/native_batch_request_handler.cpp
  src/native_capabilities_request_handler.cpp
  src/native_create_request_handler.cpp
  src/native_effect_edit_request_handler.cpp
  src/native_effect_request_handlers.cpp
  src/native_ipc_frame_codec.cpp
  src/native_log_request_handler.cpp
  src/native_layer_view_request_handlers.cpp
  src/native_operation_result.cpp
  src/native_object_request_handler.cpp
  src/native_object_edit_request_handler.cpp
  src/native_project_request_handler.cpp
  src/native_preview_request_handler.cpp
  src/native_ring_logger.cpp
  src/native_status_request_handler.cpp
  src/native_timeline_request_handlers.cpp
  src/pipe_security.cpp
  src/preview_png_encoder.cpp
  src/psd_contract.cpp
  src/request_dispatcher.cpp
  src/revision_tracker.cpp
  src/sdk_read_facade.cpp
)

target_include_directories(AviUtl2MCP.Bridge.Core
  PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
)

target_include_directories(AviUtl2MCP.Bridge.Core SYSTEM
  PRIVATE
    "${PROJECT_SOURCE_DIR}/external/aviutl2_sdk/include/aviutl2_sdk"
    "${PROJECT_SOURCE_DIR}/external/nlohmann_json/single_include"
)

target_compile_features(AviUtl2MCP.Bridge.Core PUBLIC cxx_std_20)
target_compile_definitions(AviUtl2MCP.Bridge.Core PRIVATE UNICODE _UNICODE)

if(WIN32)
  target_link_libraries(AviUtl2MCP.Bridge.Core PUBLIC Advapi32 Bcrypt Rpcrt4 Shell32 Ole32 Windowscodecs)
endif()

add_library(AviUtl2MCP.Bridge MODULE
  src/plugin_entry.cpp
)

target_include_directories(AviUtl2MCP.Bridge SYSTEM
  PRIVATE
    "${PROJECT_SOURCE_DIR}/external/aviutl2_sdk/include/aviutl2_sdk"
)

target_link_libraries(AviUtl2MCP.Bridge PRIVATE AviUtl2MCP.Bridge.Core)
target_compile_definitions(AviUtl2MCP.Bridge PRIVATE UNICODE _UNICODE)
set_target_properties(AviUtl2MCP.Bridge PROPERTIES
  PREFIX ""
  SUFFIX ".aux2"
)

foreach(target AviUtl2MCP.Bridge.Core AviUtl2MCP.Bridge)
  if(MSVC)
    target_compile_options(${target} PRIVATE /W4 /WX /permissive- /utf-8 /EHsc)
  else()
    target_compile_options(${target} PRIVATE -Wall -Wextra -Wpedantic -Werror)
  endif()
endforeach()

add_executable(AviUtl2MCP.Bridge.Tests
  bridge_version_test.cpp
)

target_link_libraries(AviUtl2MCP.Bridge.Tests PRIVATE AviUtl2MCP.Bridge.Core)

target_include_directories(AviUtl2MCP.Bridge.Tests SYSTEM
  PRIVATE
    "${PROJECT_SOURCE_DIR}/external/aviutl2_sdk/include/aviutl2_sdk"
)

if(MSVC)
  target_compile_options(AviUtl2MCP.Bridge.Tests PRIVATE /W4 /WX /permissive- /utf-8 /EHsc)
else()
  target_compile_options(AviUtl2MCP.Bridge.Tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

add_test(NAME bridge.version COMMAND AviUtl2MCP.Bridge.Tests)

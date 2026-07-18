add_library(AviUtl2MCP.Bridge.Core STATIC
  src/bridge_version.cpp
)

target_include_directories(AviUtl2MCP.Bridge.Core
  PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
)

target_compile_features(AviUtl2MCP.Bridge.Core PUBLIC cxx_std_20)

add_library(AviUtl2MCP.Bridge MODULE
  src/plugin_entry.cpp
)

target_link_libraries(AviUtl2MCP.Bridge PRIVATE AviUtl2MCP.Bridge.Core)
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

cmake_minimum_required(VERSION 3.31)

file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/VERSION" AVIUTL2_MCP_VERSION LIMIT_COUNT 1)
string(STRIP "${AVIUTL2_MCP_VERSION}" AVIUTL2_MCP_VERSION)
if(NOT AVIUTL2_MCP_VERSION MATCHES "^[0-9]+\\.[0-9]+\\.[0-9]+$")
  message(FATAL_ERROR "VERSION must contain a semantic version such as 0.1.1")
endif()

project(AviUtl2MCP VERSION "${AVIUTL2_MCP_VERSION}" LANGUAGES CXX)

set(AVIUTL2_MCP_MINIMUM_AVIUTL_VERSION 2010300)
set(AVIUTL2_MCP_GENERATED_INCLUDE_DIRECTORY
  "${CMAKE_CURRENT_BINARY_DIR}/generated/include"
)
file(MAKE_DIRECTORY
  "${AVIUTL2_MCP_GENERATED_INCLUDE_DIRECTORY}/aviutl2_mcp"
)
configure_file(
  src/AviUtl2MCP.Bridge/include/aviutl2_mcp/version.h.in
  "${AVIUTL2_MCP_GENERATED_INCLUDE_DIRECTORY}/aviutl2_mcp/version.h"
  @ONLY
)

option(AVIUTL2_MCP_BUILD_TESTS "Build native bridge tests" ON)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts")

add_subdirectory(src/AviUtl2MCP.Bridge)

if(AVIUTL2_MCP_BUILD_TESTS)
  enable_testing()
  add_subdirectory(tests/AviUtl2MCP.Bridge.Tests)
endif()

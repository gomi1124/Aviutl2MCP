cmake_minimum_required(VERSION 3.31)

project(AviUtl2MCP VERSION 0.1.0 LANGUAGES CXX)

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

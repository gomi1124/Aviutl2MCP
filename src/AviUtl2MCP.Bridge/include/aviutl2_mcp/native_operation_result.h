#pragma once

#include "aviutl2_mcp/request_dispatcher.h"

#include <string>

namespace aviutl2_mcp {

[[nodiscard]] operation_result create_native_success(
    std::string result_json,
    operation_execution_context& context);

[[nodiscard]] operation_result create_native_failure(
    std::string code,
    std::string message,
    operation_execution_context& context,
    bool retryable = false);

}  // namespace aviutl2_mcp

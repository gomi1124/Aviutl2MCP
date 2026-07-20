#include "aviutl2_mcp/native_operation_result.h"

#include <utility>

namespace aviutl2_mcp {

operation_result create_native_success(
    std::string result_json,
    operation_execution_context& context) {
    return operation_result{
        .ok = true,
        .outcome = "completed",
        .result_json = std::move(result_json),
        .error_code = {},
        .error_message = {},
        .revision = context.revisions().content_revision(),
        .view_revision = context.revisions().view_revision(),
    };
}

operation_result create_native_failure(
    std::string code,
    std::string message,
    operation_execution_context& context,
    const bool retryable) {
    return operation_result{
        .ok = false,
        .outcome = "unchanged",
        .result_json = {},
        .error_code = std::move(code),
        .error_message = std::move(message),
        .revision = context.revisions().content_revision(),
        .view_revision = context.revisions().view_revision(),
        .retryable = retryable,
    };
}

}  // namespace aviutl2_mcp

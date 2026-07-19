#pragma once

#include "aviutl2_mcp/request_dispatcher.h"

#include <string>

namespace aviutl2_mcp {

class native_log_request_handler final : public operation_handler {
public:
    [[nodiscard]] std::string operation() const override;
    [[nodiscard]] bool is_mutating() const noexcept override;
    [[nodiscard]] operation_result execute(
        const operation_request& request,
        operation_execution_context& context) override;
};

}  // namespace aviutl2_mcp

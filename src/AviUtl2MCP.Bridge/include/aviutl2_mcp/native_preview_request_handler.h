#pragma once

#include "aviutl2_mcp/request_dispatcher.h"
#include "aviutl2_mcp/sdk_read_facade.h"

namespace aviutl2_mcp {

class native_preview_request_handler final : public operation_handler {
public:
    explicit native_preview_request_handler(sdk_read_facade& sdk);

    [[nodiscard]] std::string operation() const override;
    [[nodiscard]] bool is_mutating() const noexcept override;
    [[nodiscard]] operation_result execute(
        const operation_request& request,
        operation_execution_context& context) override;

private:
    sdk_read_facade& sdk_;
};

}  // namespace aviutl2_mcp

#pragma once

#include "aviutl2_mcp/request_dispatcher.h"

#include <string>

namespace aviutl2_mcp {

class sdk_read_facade;

class native_capabilities_request_handler final : public operation_handler {
public:
    native_capabilities_request_handler(
        std::string host_version,
        sdk_read_facade& sdk,
        std::string operation = "capabilities.get");

    [[nodiscard]] std::string operation() const override;
    [[nodiscard]] bool is_mutating() const noexcept override;
    [[nodiscard]] operation_result execute(
        const operation_request& request,
        operation_execution_context& context) override;

private:
    std::string host_version_;
    sdk_read_facade& sdk_;
    std::string operation_;
};

}  // namespace aviutl2_mcp

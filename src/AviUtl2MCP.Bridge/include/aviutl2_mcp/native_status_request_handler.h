#pragma once

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/request_dispatcher.h"

#include <string>

namespace aviutl2_mcp {

class sdk_read_facade;

class native_status_request_handler final : public operation_handler {
public:
    native_status_request_handler(
        bridge_identity identity,
        std::string host_version,
        sdk_read_facade& sdk);

    [[nodiscard]] std::string operation() const override;
    [[nodiscard]] bool is_mutating() const noexcept override;
    [[nodiscard]] operation_result execute(
        const operation_request& request,
        operation_execution_context& context) override;

private:
    bridge_identity identity_;
    std::string host_version_;
    sdk_read_facade& sdk_;
};

}  // namespace aviutl2_mcp

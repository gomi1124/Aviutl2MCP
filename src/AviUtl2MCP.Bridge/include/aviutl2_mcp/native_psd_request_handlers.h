#pragma once

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/request_dispatcher.h"

namespace aviutl2_mcp {

class sdk_read_facade;

class native_psd_setup_request_handler final : public operation_handler {
public:
    native_psd_setup_request_handler(bridge_identity identity, sdk_read_facade& sdk);

    [[nodiscard]] std::string operation() const override;
    [[nodiscard]] bool is_mutating() const noexcept override;
    [[nodiscard]] operation_result execute(
        const operation_request& request,
        operation_execution_context& context) override;

private:
    bridge_identity identity_;
    sdk_read_facade& sdk_;
};

}  // namespace aviutl2_mcp

#pragma once

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/request_dispatcher.h"
#include "aviutl2_mcp/sdk_read_facade.h"

#include <string>

namespace aviutl2_mcp {

class native_object_edit_request_handler final : public operation_handler {
public:
    native_object_edit_request_handler(
        bridge_identity identity,
        sdk_read_facade& sdk,
        std::string operation,
        sdk_object_edit_kind kind);

    [[nodiscard]] std::string operation() const override;
    [[nodiscard]] bool is_mutating() const noexcept override;
    [[nodiscard]] operation_result execute(
        const operation_request& request,
        operation_execution_context& context) override;

private:
    bridge_identity identity_;
    sdk_read_facade& sdk_;
    std::string operation_;
    sdk_object_edit_kind kind_;
};

}  // namespace aviutl2_mcp

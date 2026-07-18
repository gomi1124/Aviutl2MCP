#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <vector>

namespace aviutl2_mcp {

struct effect_item_fingerprint final {
    std::string name;
    std::string type;
};

struct effect_fingerprint final {
    std::string name;
    std::vector<effect_item_fingerprint> items;
};

struct object_candidate final {
    std::int32_t scene_id;
    std::int32_t layer;
    std::int32_t start_frame;
    std::int32_t end_frame;
    std::string name;
    std::vector<std::uint8_t> alias;
    std::vector<effect_fingerprint> effects;
};

struct object_locator final {
    std::string instance_id;
    std::string project_generation;
    std::int32_t scene_id;
    std::int32_t layer;
    std::int32_t start_frame;
    std::int32_t end_frame;
    std::string name;
    std::string alias_sha256;
    std::string effect_signature_sha256;
};

enum class locator_resolution_status {
    resolved,
    instance_mismatch,
    project_mismatch,
    not_found,
    ambiguous,
};

struct locator_resolution final {
    locator_resolution_status status;
    std::optional<std::size_t> candidate_index;
};

[[nodiscard]] std::string calculate_effect_signature(
    const std::vector<effect_fingerprint>& effects);
[[nodiscard]] object_locator create_object_locator(
    const std::string& instance_id,
    const std::string& project_generation,
    const object_candidate& candidate);
[[nodiscard]] locator_resolution resolve_object_locator(
    const object_locator& locator,
    const std::string& current_instance_id,
    const std::string& current_project_generation,
    std::span<const object_candidate> candidates);

}  // namespace aviutl2_mcp

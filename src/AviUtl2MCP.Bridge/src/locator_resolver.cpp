#include "aviutl2_mcp/locator_resolver.h"

#include "aviutl2_mcp/bridge_identity.h"
#include "aviutl2_mcp/native_ipc_frame_codec.h"

#include <array>
#include <limits>
#include <stdexcept>

namespace aviutl2_mcp {
namespace {

void append_string(std::vector<std::uint8_t>& bytes, const std::string& value) {
    if (value.size() > (std::numeric_limits<std::uint32_t>::max)()) {
        throw std::length_error("effect fingerprint string is too large");
    }
    validate_utf8(std::span(
        reinterpret_cast<const std::uint8_t*>(value.data()),
        value.size()));
    const auto length = static_cast<std::uint32_t>(value.size());
    for (std::size_t index = 0U; index < sizeof(length); ++index) {
        bytes.push_back(static_cast<std::uint8_t>(length >> (index * 8U)));
    }
    bytes.insert(bytes.end(), value.begin(), value.end());
}

[[nodiscard]] bool matches(const object_locator& locator, const object_candidate& candidate) {
    if (locator.scene_id != candidate.scene_id
        || locator.layer != candidate.layer
        || locator.start_frame != candidate.start_frame
        || locator.end_frame != candidate.end_frame
        || locator.name != candidate.name) {
        return false;
    }
    return locator.alias_sha256 == calculate_sha256(candidate.alias)
        && locator.effect_signature_sha256 == calculate_effect_signature(candidate.effects);
}

void validate_candidate(const object_candidate& candidate) {
    if (candidate.scene_id < 0
        || candidate.layer < 1
        || candidate.start_frame < 1
        || candidate.end_frame < candidate.start_frame
        || candidate.name.size() > 4096U) {
        throw std::invalid_argument("object candidate fields were outside locator constraints");
    }
    validate_utf8(std::span(
        reinterpret_cast<const std::uint8_t*>(candidate.name.data()),
        candidate.name.size()));
    validate_utf8(candidate.alias);
}

}  // namespace

std::string calculate_effect_signature(const std::vector<effect_fingerprint>& effects) {
    std::vector<std::uint8_t> bytes;
    for (const auto& effect : effects) {
        append_string(bytes, effect.name);
        if (effect.items.size() > (std::numeric_limits<std::uint32_t>::max)()) {
            throw std::length_error("effect fingerprint contains too many items");
        }
        const auto item_count = static_cast<std::uint32_t>(effect.items.size());
        for (std::size_t index = 0U; index < sizeof(item_count); ++index) {
            bytes.push_back(static_cast<std::uint8_t>(item_count >> (index * 8U)));
        }
        for (const auto& item : effect.items) {
            append_string(bytes, item.name);
            append_string(bytes, item.type);
        }
    }
    return calculate_sha256(bytes);
}

object_locator create_object_locator(
    const std::string& instance_id,
    const std::string& project_generation,
    const object_candidate& candidate) {
    if (!is_nonzero_uuid(instance_id) || !is_nonzero_uuid(project_generation)) {
        throw std::invalid_argument("locator identity must contain nonzero UUIDs");
    }
    validate_candidate(candidate);
    return object_locator{
        .instance_id = instance_id,
        .project_generation = project_generation,
        .scene_id = candidate.scene_id,
        .layer = candidate.layer,
        .start_frame = candidate.start_frame,
        .end_frame = candidate.end_frame,
        .name = candidate.name,
        .alias_sha256 = calculate_sha256(candidate.alias),
        .effect_signature_sha256 = calculate_effect_signature(candidate.effects),
    };
}

locator_resolution resolve_object_locator(
    const object_locator& locator,
    const std::string& current_instance_id,
    const std::string& current_project_generation,
    const std::span<const object_candidate> candidates) {
    if (!uuid_equals(locator.instance_id, current_instance_id)) {
        return {locator_resolution_status::instance_mismatch, std::nullopt};
    }
    if (!uuid_equals(locator.project_generation, current_project_generation)) {
        return {locator_resolution_status::project_mismatch, std::nullopt};
    }

    std::optional<std::size_t> match;
    for (std::size_t index = 0U; index < candidates.size(); ++index) {
        validate_candidate(candidates[index]);
        if (!matches(locator, candidates[index])) {
            continue;
        }
        if (match.has_value()) {
            return {locator_resolution_status::ambiguous, std::nullopt};
        }
        match = index;
    }
    return match.has_value()
        ? locator_resolution{locator_resolution_status::resolved, match}
        : locator_resolution{locator_resolution_status::not_found, std::nullopt};
}

}  // namespace aviutl2_mcp

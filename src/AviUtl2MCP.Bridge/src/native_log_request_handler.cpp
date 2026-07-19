#include "aviutl2_mcp/native_log_request_handler.h"

#include "aviutl2_mcp/native_ring_logger.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <charconv>
#include <chrono>
#include <cstdint>
#include <optional>
#include <stdexcept>
#include <string_view>
#include <vector>

namespace aviutl2_mcp {
namespace {

constexpr std::size_t DEFAULT_LOG_LIMIT = 100U;
constexpr std::size_t MAXIMUM_LOG_LIMIT = 2000U;
constexpr std::string_view CURSOR_PREFIX = "bridge:";

[[nodiscard]] operation_result create_failure(
    const std::string& code,
    const std::string& message,
    operation_execution_context& context) {
    return operation_result{
        .ok = false,
        .outcome = "unchanged",
        .result_json = {},
        .error_code = code,
        .error_message = message,
        .revision = context.revisions().content_revision(),
        .view_revision = context.revisions().view_revision(),
    };
}

[[nodiscard]] std::optional<std::uint64_t> parse_cursor(const nlohmann::json& params) {
    const auto cursor = params.find("cursor");
    if (cursor == params.end() || cursor->is_null()) {
        return std::nullopt;
    }
    if (!cursor->is_string()) {
        throw std::invalid_argument("Log cursor must be a string");
    }
    const std::string value = cursor->get<std::string>();
    if (!value.starts_with(CURSOR_PREFIX)) {
        throw std::invalid_argument("Log cursor does not belong to the bridge source");
    }
    const std::string_view sequence(value.data() + CURSOR_PREFIX.size(), value.size() - CURSOR_PREFIX.size());
    std::uint64_t parsed = 0U;
    const auto [position, error] = std::from_chars(sequence.data(), sequence.data() + sequence.size(), parsed);
    if (error != std::errc{} || position != sequence.data() + sequence.size() || parsed == 0U) {
        throw std::invalid_argument("Log cursor position is invalid");
    }
    return parsed;
}

[[nodiscard]] bool includes_bridge_source(const nlohmann::json& params) {
    const auto sources = params.find("sources");
    if (sources == params.end() || sources->is_null()) {
        return true;
    }
    if (!sources->is_array()) {
        throw std::invalid_argument("Log sources must be an array");
    }
    bool includes_bridge = false;
    for (const auto& source : *sources) {
        if (!source.is_string()) {
            throw std::invalid_argument("Log source must be a string");
        }
        const std::string value = source.get<std::string>();
        if (value == "bridge") {
            includes_bridge = true;
        } else if (value != "server" && value != "aviutl") {
            throw std::invalid_argument("Log source is unknown");
        }
    }
    return includes_bridge;
}

struct parsed_levels final {
    std::vector<native_log_level> values;
    bool has_filter = false;
    bool has_supported_level = true;
};

[[nodiscard]] parsed_levels parse_levels(const nlohmann::json& params) {
    const auto levels = params.find("levels");
    if (levels == params.end() || levels->is_null()) {
        return {};
    }
    if (!levels->is_array()) {
        throw std::invalid_argument("Log levels must be an array");
    }

    parsed_levels result{
        .values = {},
        .has_filter = true,
        .has_supported_level = false,
    };
    for (const auto& level : *levels) {
        if (!level.is_string()) {
            throw std::invalid_argument("Log level must be a string");
        }
        const std::string value = level.get<std::string>();
        std::optional<native_log_level> native_level;
        if (value == "trace") {
            native_level = native_log_level::trace;
        } else if (value == "information") {
            native_level = native_log_level::information;
        } else if (value == "warning") {
            native_level = native_log_level::warning;
        } else if (value == "error") {
            native_level = native_log_level::error;
        } else if (value != "debug" && value != "critical") {
            throw std::invalid_argument("Log level is unknown");
        }
        if (native_level.has_value()
            && std::ranges::find(result.values, *native_level) == result.values.end()) {
            result.values.push_back(*native_level);
            result.has_supported_level = true;
        }
    }
    return result;
}

[[nodiscard]] std::size_t parse_limit(const nlohmann::json& params) {
    const auto limit = params.find("limit");
    if (limit == params.end() || limit->is_null()) {
        return DEFAULT_LOG_LIMIT;
    }
    if (!limit->is_number_unsigned() && !limit->is_number_integer()) {
        throw std::invalid_argument("Log limit must be an integer");
    }
    const std::int64_t value = limit->get<std::int64_t>();
    if (value <= 0 || value > static_cast<std::int64_t>(MAXIMUM_LOG_LIMIT)) {
        throw std::invalid_argument("Log limit is outside the supported range");
    }
    return static_cast<std::size_t>(value);
}

[[nodiscard]] std::optional<std::string> parse_correlation_id(const nlohmann::json& params) {
    const auto correlation_id = params.find("correlationId");
    if (correlation_id == params.end() || correlation_id->is_null()) {
        return std::nullopt;
    }
    if (!correlation_id->is_string()) {
        throw std::invalid_argument("Correlation ID must be a string");
    }
    return correlation_id->get<std::string>();
}

[[nodiscard]] int parse_fixed_integer(
    const std::string_view value,
    const std::size_t position,
    const std::size_t length) {
    if (position + length > value.size()) {
        throw std::invalid_argument("Log since timestamp is incomplete");
    }
    int result = 0;
    const char* first = value.data() + position;
    const char* last = first + length;
    const auto [parsed, error] = std::from_chars(first, last, result);
    if (error != std::errc{} || parsed != last) {
        throw std::invalid_argument("Log since timestamp contains an invalid number");
    }
    return result;
}

[[nodiscard]] std::optional<std::int64_t> parse_since(const nlohmann::json& params) {
    const auto since = params.find("since");
    if (since == params.end() || since->is_null()) {
        return std::nullopt;
    }
    if (!since->is_string()) {
        throw std::invalid_argument("Log since timestamp must be a string");
    }
    const std::string text = since->get<std::string>();
    const std::string_view value(text);
    if (value.size() < 20U || value[4] != '-' || value[7] != '-'
        || (value[10] != 'T' && value[10] != 't') || value[13] != ':' || value[16] != ':') {
        throw std::invalid_argument("Log since timestamp is not RFC 3339");
    }

    const int year = parse_fixed_integer(value, 0U, 4U);
    const unsigned int month = static_cast<unsigned int>(parse_fixed_integer(value, 5U, 2U));
    const unsigned int day = static_cast<unsigned int>(parse_fixed_integer(value, 8U, 2U));
    const int hour = parse_fixed_integer(value, 11U, 2U);
    const int minute = parse_fixed_integer(value, 14U, 2U);
    const int second = parse_fixed_integer(value, 17U, 2U);
    const std::chrono::year_month_day date{
        std::chrono::year(year),
        std::chrono::month(month),
        std::chrono::day(day),
    };
    if (!date.ok() || hour > 23 || minute > 59 || second > 59) {
        throw std::invalid_argument("Log since timestamp is outside the supported range");
    }

    std::size_t position = 19U;
    int milliseconds = 0;
    if (position < value.size() && value[position] == '.') {
        ++position;
        const std::size_t fraction_start = position;
        while (position < value.size() && value[position] >= '0' && value[position] <= '9') {
            ++position;
        }
        const std::size_t fraction_length = position - fraction_start;
        if (fraction_length == 0U || fraction_length > 7U) {
            throw std::invalid_argument("Log since timestamp fraction is invalid");
        }
        const std::size_t millisecond_digits = (std::min)(fraction_length, std::size_t{3U});
        milliseconds = parse_fixed_integer(value, fraction_start, millisecond_digits);
        for (std::size_t digit = millisecond_digits; digit < 3U; ++digit) {
            milliseconds *= 10;
        }
    }

    int offset_minutes = 0;
    if (position < value.size() && (value[position] == 'Z' || value[position] == 'z')) {
        ++position;
    } else if (position + 6U == value.size()
        && (value[position] == '+' || value[position] == '-')
        && value[position + 3U] == ':') {
        const int offset_hour = parse_fixed_integer(value, position + 1U, 2U);
        const int offset_minute = parse_fixed_integer(value, position + 4U, 2U);
        if (offset_hour > 14 || offset_minute > 59 || (offset_hour == 14 && offset_minute != 0)) {
            throw std::invalid_argument("Log since timestamp offset is invalid");
        }
        offset_minutes = offset_hour * 60 + offset_minute;
        if (value[position] == '-') {
            offset_minutes = -offset_minutes;
        }
        position += 6U;
    } else {
        throw std::invalid_argument("Log since timestamp offset is missing");
    }
    if (position != value.size()) {
        throw std::invalid_argument("Log since timestamp contains trailing data");
    }

    const std::int64_t days_since_epoch = std::chrono::sys_days(date).time_since_epoch().count();
    const std::int64_t local_unix_ms = days_since_epoch * 24LL * 60LL * 60LL * 1000LL
        + static_cast<std::int64_t>(hour) * 60LL * 60LL * 1000LL
        + static_cast<std::int64_t>(minute) * 60LL * 1000LL
        + static_cast<std::int64_t>(second) * 1000LL
        + milliseconds;
    return local_unix_ms
        - static_cast<std::int64_t>(offset_minutes) * 60LL * 1000LL;
}

[[nodiscard]] nlohmann::json serialize_entry(const native_log_entry& entry) {
    return {
        {"timestamp", entry.timestamp_utc},
        {"level", native_log_level_name(entry.level)},
        {"source", entry.source},
        {"eventId", entry.event_id},
        {"correlationId", entry.correlation_id.has_value()
            ? nlohmann::json(*entry.correlation_id)
            : nlohmann::json(nullptr)},
        {"message", entry.message},
    };
}

}  // namespace

std::string native_log_request_handler::operation() const {
    return "logs.get";
}

bool native_log_request_handler::is_mutating() const noexcept {
    return false;
}

operation_result native_log_request_handler::execute(
    const operation_request& request,
    operation_execution_context& context) {
    try {
        const nlohmann::json params = nlohmann::json::parse(request.params_json);
        if (!params.is_object()) {
            throw std::invalid_argument("Log query parameters must be an object");
        }
        const std::size_t limit = parse_limit(params);
        const std::optional<std::uint64_t> cursor = parse_cursor(params);
        const parsed_levels levels = parse_levels(params);
        const bool should_query = includes_bridge_source(params)
            && (!levels.has_filter || levels.has_supported_level);

        native_log_snapshot snapshot;
        if (should_query) {
            snapshot = get_native_logger().snapshot({
                .limit = limit,
                .after_sequence = cursor,
                .since_unix_ms = parse_since(params),
                .correlation_id = parse_correlation_id(params),
                .component = std::nullopt,
                .levels = levels.values,
            });
        }

        nlohmann::json entries = nlohmann::json::array();
        for (const native_log_entry& entry : snapshot.entries) {
            entries.push_back(serialize_entry(entry));
        }
        const nlohmann::json result = {
            {"entries", std::move(entries)},
            {"nextCursor", snapshot.next_sequence.has_value()
                ? nlohmann::json(std::string(CURSOR_PREFIX) + std::to_string(*snapshot.next_sequence))
                : nlohmann::json(nullptr)},
            {"isTruncated", snapshot.is_truncated},
        };
        return operation_result{
            .ok = true,
            .outcome = "completed",
            .result_json = result.dump(),
            .error_code = {},
            .error_message = {},
            .revision = context.revisions().content_revision(),
            .view_revision = context.revisions().view_revision(),
        };
    } catch (const nlohmann::json::exception&) {
        return create_failure("invalid_argument", "Log query JSON is invalid", context);
    } catch (const std::invalid_argument& exception) {
        return create_failure("invalid_argument", exception.what(), context);
    }
}

}  // namespace aviutl2_mcp

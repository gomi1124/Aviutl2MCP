using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;

namespace AviUtl2MCP.Server.Diagnostics;

internal static class LogSourceFilter
{
    public static LogSourcePage CreatePage(
        IReadOnlyList<LogEntry> entries,
        LogSourceQuery query,
        string generation)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, 2000);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);

        IEnumerable<LogEntry> filtered = entries;
        if (query.Levels is { Count: > 0 })
        {
            filtered = filtered.Where(entry => query.Levels.Any(level =>
                string.Equals(entry.Level, level.ToString(), StringComparison.OrdinalIgnoreCase)));
        }
        if (query.Since.HasValue)
        {
            filtered = filtered.Where(entry => entry.Timestamp >= query.Since.Value);
        }
        if (query.CorrelationId.HasValue)
        {
            string correlationId = query.CorrelationId.Value.ToString("D");
            filtered = filtered.Where(entry =>
                string.Equals(entry.CorrelationId, correlationId, StringComparison.OrdinalIgnoreCase));
        }

        LogEntry[] materialized = filtered
            .OrderBy(entry => entry.Timestamp)
            .ToArray();
        if (query.Offset >= materialized.LongLength)
        {
            return new LogSourcePage([], null, false, generation);
        }

        int offset = checked((int)query.Offset);
        LogEntry[] pageEntries = materialized
            .Skip(offset)
            .Take(query.Limit)
            .ToArray();
        long nextOffset = query.Offset + pageEntries.LongLength;
        bool isTruncated = nextOffset < materialized.LongLength;
        return new LogSourcePage(
            pageEntries,
            isTruncated ? nextOffset : null,
            isTruncated,
            generation);
    }
}

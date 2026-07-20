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
        long offset = ParseOffset(query.Cursor);

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
        if (offset >= materialized.LongLength)
        {
            return new LogSourcePage([], null, false, generation);
        }

        int pageOffset = checked((int)offset);
        LogEntry[] pageEntries = materialized
            .Skip(pageOffset)
            .Take(query.Limit)
            .ToArray();
        long nextOffset = offset + pageEntries.LongLength;
        bool isTruncated = nextOffset < materialized.LongLength;
        return new LogSourcePage(
            pageEntries,
            isTruncated ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            isTruncated,
            generation);
    }

    private static long ParseOffset(string? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }
        if (!long.TryParse(
                cursor,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long offset)
            || offset < 0)
        {
            throw new LogSourceReadException("cursor_invalid", "The file log cursor is invalid.");
        }
        return offset;
    }
}

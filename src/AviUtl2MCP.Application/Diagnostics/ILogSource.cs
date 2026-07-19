using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed record LogSourceQuery(
    IReadOnlyList<ContractLogLevel>? Levels,
    DateTimeOffset? Since,
    Guid? CorrelationId,
    int Limit,
    long Offset = 0);

public sealed record LogSourcePage(
    IReadOnlyList<LogEntry> Entries,
    long? NextOffset,
    bool IsTruncated,
    string Generation);

public interface ILogSource
{
    LogSource Source { get; }

    ValueTask<LogSourcePage> ReadAsync(
        LogSourceQuery query,
        CancellationToken cancellationToken);
}

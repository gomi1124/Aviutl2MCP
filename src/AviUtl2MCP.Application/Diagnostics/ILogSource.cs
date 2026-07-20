using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed record LogSourceQuery(
    IReadOnlyList<ContractLogLevel>? Levels,
    DateTimeOffset? Since,
    Guid? CorrelationId,
    int Limit,
    string? Cursor,
    Guid? InstanceId,
    Guid RequestCorrelationId,
    DateTimeOffset Deadline,
    int TimeoutMs);

public sealed record LogSourcePage(
    IReadOnlyList<LogEntry> Entries,
    string? NextCursor,
    bool IsTruncated,
    string Generation);

public interface ILogSource
{
    LogSource Source { get; }

    ValueTask<LogSourcePage> ReadAsync(
        LogSourceQuery query,
        CancellationToken cancellationToken);
}

public sealed class LogSourceReadException(
    string code,
    string message,
    bool canRetry = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;

    public bool CanRetry { get; } = canRetry;
}

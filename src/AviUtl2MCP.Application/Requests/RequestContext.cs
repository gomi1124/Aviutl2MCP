namespace AviUtl2MCP.Application.Requests;

public sealed class RequestContext : IDisposable
{
    private readonly CancellationTokenSource cancellationSource;

    internal RequestContext(
        Guid correlationId,
        Guid? requestedInstanceId,
        DateTimeOffset deadline,
        int timeoutMs,
        CancellationTokenSource cancellationSource)
    {
        CorrelationId = correlationId;
        RequestedInstanceId = requestedInstanceId;
        Deadline = deadline;
        TimeoutMs = timeoutMs;
        this.cancellationSource = cancellationSource;
    }

    public Guid CorrelationId { get; }

    public Guid? RequestedInstanceId { get; }

    public DateTimeOffset Deadline { get; }

    public int TimeoutMs { get; }

    public CancellationToken CancellationToken => cancellationSource.Token;

    public void Dispose()
    {
        cancellationSource.Dispose();
    }
}

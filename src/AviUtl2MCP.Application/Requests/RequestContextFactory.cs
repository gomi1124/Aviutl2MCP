namespace AviUtl2MCP.Application.Requests;

public sealed class RequestContextFactory
{
    private readonly TimeProvider timeProvider;

    public RequestContextFactory(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RequestContext CreateContext(
        Guid? requestedInstanceId,
        int? timeoutMs,
        int defaultTimeoutMs,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultTimeoutMs, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(defaultTimeoutMs, 120_000);
        int effectiveTimeoutMs = timeoutMs ?? defaultTimeoutMs;
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveTimeoutMs, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(effectiveTimeoutMs, 120_000);

        DateTimeOffset now = timeProvider.GetUtcNow();
        CancellationTokenSource cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationSource.CancelAfter(effectiveTimeoutMs);
        return new RequestContext(
            Guid.CreateVersion7(now),
            requestedInstanceId,
            now.AddMilliseconds(effectiveTimeoutMs),
            cancellationSource);
    }
}

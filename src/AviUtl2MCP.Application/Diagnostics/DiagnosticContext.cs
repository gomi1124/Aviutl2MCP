using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Instances;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed record DiagnosticRunContext(
    Guid ServerEpoch,
    InstanceDescriptor Instance,
    Guid CorrelationId,
    DateTimeOffset Deadline,
    int TimeoutMs);

public sealed record DiagnosticProbeFailure(
    string Code,
    string Message,
    bool CanRetry);

public sealed class DiagnosticSmokeResult
{
    private DiagnosticSmokeResult(
        bool wasRequested,
        bool succeeded,
        IReadOnlyList<string> evidence,
        DiagnosticProbeFailure? failure)
    {
        WasRequested = wasRequested;
        Succeeded = succeeded;
        Evidence = evidence;
        Failure = failure;
    }

    public bool WasRequested { get; }

    public bool Succeeded { get; }

    public IReadOnlyList<string> Evidence { get; }

    public DiagnosticProbeFailure? Failure { get; }

    public static DiagnosticSmokeResult NotRequested() =>
        new(false, false, [], null);

    public static DiagnosticSmokeResult Success(params string[] evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new DiagnosticSmokeResult(true, true, evidence, null);
    }

    public static DiagnosticSmokeResult Failed(
        string code,
        string message,
        bool canRetry,
        params string[] evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(evidence);
        return new DiagnosticSmokeResult(
            true,
            false,
            evidence,
            new DiagnosticProbeFailure(code, message, canRetry));
    }
}

public sealed record DiagnosticContext(
    InstanceDescriptor Instance,
    StatusData? Status,
    DiagnosticProbeFailure? StatusFailure,
    CapabilitiesData? Capabilities,
    DiagnosticProbeFailure? CapabilitiesFailure,
    IReadOnlyList<LogEntry> Logs,
    IReadOnlyList<ToolWarning> LogWarnings,
    DiagnosticProbeFailure? LogFailure,
    IReadOnlyList<KnownLogMatch> KnownLogMatches,
    DiagnosticSmokeResult ReadSmoke,
    DiagnosticSmokeResult PreviewSmoke);

public interface IDiagnosticSmokeProbe
{
    ValueTask<DiagnosticSmokeResult> RunReadSmokeAsync(
        DiagnosticRunContext context,
        CancellationToken cancellationToken);

    ValueTask<DiagnosticSmokeResult> RunPreviewSmokeAsync(
        DiagnosticRunContext context,
        CancellationToken cancellationToken);
}

public sealed class UnavailableDiagnosticSmokeProbe : IDiagnosticSmokeProbe
{
    public static UnavailableDiagnosticSmokeProbe Instance { get; } = new();

    private UnavailableDiagnosticSmokeProbe()
    {
    }

    public ValueTask<DiagnosticSmokeResult> RunReadSmokeAsync(
        DiagnosticRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DiagnosticSmokeResult.Failed(
            "read_smoke_unavailable",
            "The read smoke probe is not registered.",
            canRetry: false));
    }

    public ValueTask<DiagnosticSmokeResult> RunPreviewSmokeAsync(
        DiagnosticRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DiagnosticSmokeResult.Failed(
            "preview_smoke_unavailable",
            "The preview smoke probe is not registered.",
            canRetry: false));
    }
}

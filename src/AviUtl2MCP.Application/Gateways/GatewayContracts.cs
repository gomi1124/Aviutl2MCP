using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Gateways;

public sealed record GatewayRequest<TParameters>(
    Guid InstanceId,
    Guid CorrelationId,
    DateTimeOffset Deadline,
    int TimeoutMs,
    Revision? ExpectedRevision,
    bool DryRun,
    TParameters Parameters);

public sealed record GatewayError(
    string Code,
    string Message,
    bool CanRetry,
    string Phase,
    string Outcome,
    bool UndoRecommended,
    System.Text.Json.JsonElement Details);

public sealed record GatewayResponse<TData>(
    bool Ok,
    Guid CorrelationId,
    Guid InstanceId,
    Revision? Revision,
    Revision? ViewRevision,
    TData? Data,
    IReadOnlyList<ToolWarning> Warnings,
    GatewayError? Error,
    ReadOnlyMemory<byte> Binary);

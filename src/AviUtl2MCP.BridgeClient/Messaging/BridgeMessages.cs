using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.BridgeClient.Protocol;

namespace AviUtl2MCP.BridgeClient.Messaging;

public sealed record BridgeRequest(
    string Method,
    Guid CorrelationId,
    int TimeoutMs,
    Revision? ExpectedRevision,
    bool DryRun,
    JsonElement Params);

public sealed record BridgeResponseError(
    string Code,
    string Message,
    bool Retryable,
    string Phase,
    string Outcome,
    bool UndoRecommended,
    JsonElement Details);

public sealed record BridgeResponseEnvelope(
    bool Ok,
    Guid CorrelationId,
    Guid InstanceId,
    Revision? Revision,
    Revision? ViewRevision,
    JsonElement? Result,
    IReadOnlyList<ToolWarning>? Warnings,
    BridgeResponseError? Error);

public sealed record BridgeResponse(BridgeResponseEnvelope Envelope, IpcFrame Frame);

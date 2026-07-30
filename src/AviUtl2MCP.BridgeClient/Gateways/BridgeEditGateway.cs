using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Connections;

namespace AviUtl2MCP.BridgeClient.Gateways;

public sealed class BridgeEditGateway(BridgeConnectionRegistry connectionRegistry)
    : BridgeGatewayBase(connectionRegistry), IAviUtlEditGateway
{
    private static readonly HashSet<string> allowedEditOperations = new(StringComparer.Ordinal)
    {
        "object.create",
        "object.createMedia",
        "object.createAlias",
        "object.move",
        "object.delete",
        "object.setName",
        "object.createSection",
        "object.deleteSection",
        "object.moveSection",
        "effect.setItem",
        "effect.setState",
        "layer.set",
    };

    public ValueTask<GatewayResponse<TData>> ExecuteEditAsync<TParameters, TData>(
        string operation,
        GatewayRequest<TParameters> request,
        CancellationToken cancellationToken)
    {
        if (!allowedEditOperations.Contains(operation))
        {
            throw new ArgumentException("Operation is not an edit gateway operation.", nameof(operation));
        }

        return SendOperationAsync<TParameters, TData>(operation, request, cancellationToken);
    }

    public ValueTask<GatewayResponse<BatchData>> ExecuteBatchAsync(
        GatewayRequest<ExecuteBatchInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<ExecuteBatchInput, BatchData>("batch.execute", request, cancellationToken);

    public ValueTask<GatewayResponse<SaveProjectData>> SaveProjectAsync(
        GatewayRequest<SaveProjectArgs> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<SaveProjectArgs, SaveProjectData>(
            "project.save",
            request,
            cancellationToken);

    public ValueTask<GatewayResponse<CursorData>> SetCursorAsync(
        GatewayRequest<SetCursorInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<SetCursorInput, CursorData>("view.setCursor", request, cancellationToken);
}

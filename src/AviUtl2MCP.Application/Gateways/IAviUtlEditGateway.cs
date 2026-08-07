using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Gateways;

public interface IAviUtlEditGateway
{
    ValueTask<GatewayResponse<TData>> ExecuteEditAsync<TParameters, TData>(
        string operation,
        GatewayRequest<TParameters> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<BatchData>> ExecuteBatchAsync(
        GatewayRequest<ExecuteBatchInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<SaveProjectData>> SaveProjectAsync(
        GatewayRequest<SaveProjectArgs> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<CursorData>> SetCursorAsync(
        GatewayRequest<SetCursorInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<OpenSceneData>> OpenSceneAsync(
        GatewayRequest<OpenSceneInput> request,
        CancellationToken cancellationToken);
}

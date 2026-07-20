using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Connections;

namespace AviUtl2MCP.BridgeClient.Gateways;

public sealed class BridgePreviewGateway(BridgeConnectionRegistry connectionRegistry)
    : BridgeGatewayBase(connectionRegistry), IAviUtlPreviewGateway
{
    public ValueTask<GatewayResponse<PreviewData>> RenderPreviewAsync(
        GatewayRequest<RenderPreviewInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<RenderPreviewInput, PreviewData>("preview.render", request, cancellationToken);
}

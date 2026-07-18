using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Gateways;

public interface IAviUtlPreviewGateway
{
    ValueTask<GatewayResponse<PreviewData>> RenderPreviewAsync(
        GatewayRequest<RenderPreviewInput> request,
        CancellationToken cancellationToken);
}

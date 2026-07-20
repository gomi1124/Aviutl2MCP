using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Gateways;

public interface IAviUtlQueryGateway
{
    ValueTask<GatewayResponse<ProjectData>> GetProjectAsync(
        GatewayRequest<GetProjectInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<TimelineData>> GetTimelineAsync(
        GatewayRequest<GetTimelineInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<ObjectsPageData>> FindObjectsAsync(
        GatewayRequest<FindObjectsInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<ObjectData>> GetObjectAsync(
        GatewayRequest<GetObjectInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<EffectsData>> ListEffectsAsync(
        GatewayRequest<ListEffectsInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<EffectItemsData>> ListEffectItemsAsync(
        GatewayRequest<ListEffectItemsInput> request,
        CancellationToken cancellationToken);
}

using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Connections;

namespace AviUtl2MCP.BridgeClient.Gateways;

public sealed class BridgeQueryGateway(BridgeConnectionRegistry connectionRegistry)
    : BridgeGatewayBase(connectionRegistry), IAviUtlQueryGateway
{
    public ValueTask<GatewayResponse<ProjectData>> GetProjectAsync(
        GatewayRequest<GetProjectInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<GetProjectInput, ProjectData>("project.get", request, cancellationToken);

    public ValueTask<GatewayResponse<TimelineData>> GetTimelineAsync(
        GatewayRequest<GetTimelineInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<GetTimelineInput, TimelineData>("timeline.get", request, cancellationToken);

    public ValueTask<GatewayResponse<ObjectsPageData>> FindObjectsAsync(
        GatewayRequest<FindObjectsInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<FindObjectsInput, ObjectsPageData>("object.find", request, cancellationToken);

    public ValueTask<GatewayResponse<ObjectData>> GetObjectAsync(
        GatewayRequest<GetObjectInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<GetObjectInput, ObjectData>("object.get", request, cancellationToken);

    public ValueTask<GatewayResponse<EffectsData>> ListEffectsAsync(
        GatewayRequest<ListEffectsInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<ListEffectsInput, EffectsData>("effect.list", request, cancellationToken);

    public ValueTask<GatewayResponse<EffectItemsData>> ListEffectItemsAsync(
        GatewayRequest<ListEffectItemsInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<ListEffectItemsInput, EffectItemsData>("effect.items.list", request, cancellationToken);
}

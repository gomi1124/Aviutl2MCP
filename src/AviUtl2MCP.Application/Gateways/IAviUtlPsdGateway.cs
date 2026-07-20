using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Gateways;

public interface IAviUtlPsdGateway
{
    ValueTask<GatewayResponse<CapabilitiesData>> GetPsdCapabilitiesAsync(
        GatewayRequest<GetCapabilitiesInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<TData>> ExecutePsdAsync<TParameters, TData>(
        string operation,
        GatewayRequest<TParameters> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<PsdValidateData>> ValidatePsdAsync(
        GatewayRequest<PsdValidateInput> request,
        CancellationToken cancellationToken);
}

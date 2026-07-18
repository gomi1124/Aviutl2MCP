using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Gateways;

public interface IBridgeDiagnosticsGateway
{
    ValueTask<GatewayResponse<StatusData>> GetStatusAsync(
        GatewayRequest<GetStatusInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<CapabilitiesData>> GetCapabilitiesAsync(
        GatewayRequest<GetCapabilitiesInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<LogsData>> GetLogsAsync(
        GatewayRequest<GetLogsInput> request,
        CancellationToken cancellationToken);

    ValueTask<GatewayResponse<DiagnoseData>> DiagnoseAsync(
        GatewayRequest<DiagnoseInput> request,
        CancellationToken cancellationToken);
}

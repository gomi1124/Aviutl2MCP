using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Connections;

namespace AviUtl2MCP.BridgeClient.Gateways;

public sealed class BridgeDiagnosticsGateway(BridgeConnectionRegistry connectionRegistry)
    : BridgeGatewayBase(connectionRegistry), IBridgeDiagnosticsGateway
{
    public ValueTask<GatewayResponse<StatusData>> GetStatusAsync(
        GatewayRequest<GetStatusInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<GetStatusInput, StatusData>("status.get", request, cancellationToken);

    public ValueTask<GatewayResponse<CapabilitiesData>> GetCapabilitiesAsync(
        GatewayRequest<GetCapabilitiesInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<GetCapabilitiesInput, CapabilitiesData>("capabilities.get", request, cancellationToken);

    public ValueTask<GatewayResponse<LogsData>> GetLogsAsync(
        GatewayRequest<GetLogsInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<GetLogsInput, LogsData>("logs.get", request, cancellationToken);

    public ValueTask<GatewayResponse<DiagnoseData>> DiagnoseAsync(
        GatewayRequest<DiagnoseInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<DiagnoseInput, DiagnoseData>("diagnostics.run", request, cancellationToken);
}

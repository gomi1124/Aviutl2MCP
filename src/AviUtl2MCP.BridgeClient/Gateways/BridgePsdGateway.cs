using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Connections;

namespace AviUtl2MCP.BridgeClient.Gateways;

public sealed class BridgePsdGateway(BridgeConnectionRegistry connectionRegistry)
    : BridgeGatewayBase(connectionRegistry), IAviUtlPsdGateway
{
    private static readonly HashSet<string> allowedPsdOperations = new(StringComparer.Ordinal)
    {
        "psd.create",
        "psd.setup",
        "psd.setCharacter",
        "psd.setLayerState",
        "psd.createVoice",
    };

    public ValueTask<GatewayResponse<CapabilitiesData>> GetPsdCapabilitiesAsync(
        GatewayRequest<GetCapabilitiesInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<GetCapabilitiesInput, CapabilitiesData>("psd.capabilities", request, cancellationToken);

    public ValueTask<GatewayResponse<TData>> ExecutePsdAsync<TParameters, TData>(
        string operation,
        GatewayRequest<TParameters> request,
        CancellationToken cancellationToken)
    {
        if (!allowedPsdOperations.Contains(operation))
        {
            throw new ArgumentException("Operation is not a PSD gateway operation.", nameof(operation));
        }

        return SendOperationAsync<TParameters, TData>(operation, request, cancellationToken);
    }

    public ValueTask<GatewayResponse<PsdValidateData>> ValidatePsdAsync(
        GatewayRequest<PsdValidateInput> request,
        CancellationToken cancellationToken) =>
        SendOperationAsync<PsdValidateInput, PsdValidateData>("psd.validate", request, cancellationToken);
}

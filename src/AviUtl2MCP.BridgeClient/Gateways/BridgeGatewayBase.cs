using System.Text.Json;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Messaging;

namespace AviUtl2MCP.BridgeClient.Gateways;

public abstract class BridgeGatewayBase
{
    private readonly BridgeConnectionRegistry connectionRegistry;

    protected BridgeGatewayBase(BridgeConnectionRegistry connectionRegistry)
    {
        ArgumentNullException.ThrowIfNull(connectionRegistry);
        this.connectionRegistry = connectionRegistry;
    }

    protected async ValueTask<GatewayResponse<TData>> SendOperationAsync<TParameters, TData>(
        string operation,
        GatewayRequest<TParameters> request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(operation, request);
        JsonElement parameters = SerializeParameters(request.Parameters);
        IBridgeConnection connection = await connectionRegistry.GetConnectionAsync(
            request.InstanceId,
            cancellationToken).ConfigureAwait(false);
        BridgeRequest bridgeRequest = new(
            operation,
            request.CorrelationId,
            request.TimeoutMs,
            request.ExpectedRevision,
            request.DryRun,
            parameters);
        BridgeResponse response = await connection.SendAsync(
            bridgeRequest,
            ReadOnlyMemory<byte>.Empty,
            request.Deadline,
            cancellationToken).ConfigureAwait(false);
        return MapResponse<TData>(response);
    }

    private static void ValidateRequest<TParameters>(string operation, GatewayRequest<TParameters> request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfEqual(request.InstanceId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(request.CorrelationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.TimeoutMs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.TimeoutMs, 120_000);
        if (request.Parameters is null)
        {
            throw new ArgumentException("Gateway request parameters must not be null.", nameof(request));
        }
    }

    private static JsonElement SerializeParameters<TParameters>(TParameters parameters)
    {
        string json = ContractJsonSerializer.SerializeContract(parameters!);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Gateway parameters must serialize to a JSON object.");
        }

        return document.RootElement.Clone();
    }

    private static GatewayResponse<TData> MapResponse<TData>(BridgeResponse response)
    {
        BridgeResponseEnvelope envelope = response.Envelope;
        TData? data = default;
        if (envelope.Result is JsonElement result)
        {
            data = ContractJsonSerializer.DeserializeContract<TData>(result.GetRawText());
        }
        else if (envelope.Ok)
        {
            throw new InvalidDataException("Successful bridge response omitted result data.");
        }

        GatewayError? error = envelope.Error is null
            ? null
            : new GatewayError(
                envelope.Error.Code,
                envelope.Error.Message,
                envelope.Error.Retryable,
                envelope.Error.Phase,
                envelope.Error.Outcome,
                envelope.Error.UndoRecommended,
                envelope.Error.Details.Clone());
        return new GatewayResponse<TData>(
            envelope.Ok,
            envelope.CorrelationId,
            envelope.InstanceId,
            envelope.Revision,
            envelope.ViewRevision,
            data,
            envelope.Warnings ?? [],
            error,
            response.Frame.BinaryBytes);
    }
}

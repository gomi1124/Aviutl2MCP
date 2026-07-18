using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Protocol;
using AviUtl2MCP.BridgeClient.Transport;

namespace AviUtl2MCP.BridgeClient.Handshake;

public sealed class BridgeHandshakeClient
{
    private const int DEFAULT_IN_FLIGHT_LIMIT = 8;
    private static readonly ProtocolRange supportedProtocol = new(
        BridgeProtocol.MAJOR_VERSION,
        BridgeProtocol.MINOR_VERSION,
        BridgeProtocol.MAJOR_VERSION,
        BridgeProtocol.MINOR_VERSION);
    private static readonly HandshakeLimits offeredLimits = new(
        BridgeProtocol.MAX_JSON_BYTES,
        BridgeProtocol.MAX_BINARY_BYTES,
        DEFAULT_IN_FLIGHT_LIMIT);
    private readonly IBridgeTransport transport;
    private readonly Guid clientInstanceId;
    private readonly string clientVersion;

    public BridgeHandshakeClient(
        IBridgeTransport transport,
        Guid clientInstanceId,
        string clientVersion)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfEqual(clientInstanceId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        this.transport = transport;
        this.clientInstanceId = clientInstanceId;
        this.clientVersion = clientVersion;
    }

    public async ValueTask<BridgeSessionInfo> HandshakeAsync(
        string pipeName,
        Guid targetInstanceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentOutOfRangeException.ThrowIfEqual(targetInstanceId, Guid.Empty);
        if (!transport.IsConnected)
        {
            await transport.ConnectAsync(pipeName, cancellationToken).ConfigureAwait(false);
        }

        Guid requestId = Guid.CreateVersion7();
        ClientHello clientHello = new(
            clientInstanceId,
            Environment.ProcessId,
            targetInstanceId,
            supportedProtocol,
            clientVersion,
            offeredLimits);
        byte[] helloBytes = Encoding.UTF8.GetBytes(ContractJsonSerializer.SerializeContract(clientHello));
        IpcEncodedFrame encodedHello = IpcFrameCodec.EncodeFrame(
            IpcMessageKind.ClientHello,
            IpcFrameOption.None,
            requestId,
            helloBytes,
            []);
        await transport.WriteAsync(encodedHello.Bytes, cancellationToken).ConfigureAwait(false);

        IpcFrame response = await IpcFrameCodec.DecodeFrameAsync(transport, cancellationToken).ConfigureAwait(false);
        ValidateFrame(response, requestId);
        ServerHello serverHello = DeserializeServerHello(response.JsonBytes.Span);
        if (!serverHello.Accepted)
        {
            HandshakeError error = serverHello.Error
                ?? throw new InvalidDataException("Rejected ServerHello did not include an error.");
            throw new BridgeHandshakeRejectedException(error, serverHello.ClientRange, serverHello.ServerRange);
        }

        return ValidateAcceptedHello(serverHello, targetInstanceId);
    }

    private static void ValidateFrame(IpcFrame frame, Guid requestId)
    {
        if (frame.Header.MessageKind != IpcMessageKind.ServerHello)
        {
            throw new InvalidDataException("The first bridge response was not ServerHello.");
        }

        if (frame.Header.RequestId != requestId)
        {
            throw new InvalidDataException("ServerHello request ID did not match ClientHello.");
        }

        if (frame.Header.Flags != IpcFrameOption.None || !frame.BinaryBytes.IsEmpty)
        {
            throw new InvalidDataException("ServerHello must not contain flags or binary data.");
        }
    }

    private static ServerHello DeserializeServerHello(ReadOnlySpan<byte> jsonBytes)
    {
        try
        {
            return ContractJsonSerializer.DeserializeContract<ServerHello>(Encoding.UTF8.GetString(jsonBytes));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("ServerHello JSON did not match the handshake contract.", exception);
        }
    }

    private static BridgeSessionInfo ValidateAcceptedHello(ServerHello hello, Guid targetInstanceId)
    {
        Guid instanceId = RequireNonEmpty(hello.InstanceId, "instanceId");
        if (instanceId != targetInstanceId)
        {
            throw new InvalidDataException("ServerHello instance ID did not match the target instance.");
        }

        Guid serverEpoch = RequireNonEmpty(hello.ServerEpoch, "serverEpoch");
        int processId = hello.AviutlProcessId
            ?? throw new InvalidDataException("Accepted ServerHello omitted aviutlProcessId.");
        long processCreationTime = hello.AviutlProcessCreationTime
            ?? throw new InvalidDataException("Accepted ServerHello omitted aviutlProcessCreationTime.");
        if (processId <= 0 || processCreationTime <= 0)
        {
            throw new InvalidDataException("ServerHello process identity was invalid.");
        }

        NegotiatedProtocol protocol = hello.Protocol
            ?? throw new InvalidDataException("Accepted ServerHello omitted protocol.");
        if (protocol.Major != BridgeProtocol.MAJOR_VERSION || protocol.Minor > BridgeProtocol.MINOR_VERSION)
        {
            throw new InvalidDataException("ServerHello selected an unsupported protocol version.");
        }

        BridgeVersions versions = hello.Versions
            ?? throw new InvalidDataException("Accepted ServerHello omitted versions.");
        if (string.IsNullOrWhiteSpace(versions.Bridge)
            || string.IsNullOrWhiteSpace(versions.Sdk)
            || string.IsNullOrWhiteSpace(versions.Aviutl))
        {
            throw new InvalidDataException("ServerHello versions must not be empty.");
        }

        HandshakeLimits limits = hello.Limits
            ?? throw new InvalidDataException("Accepted ServerHello omitted limits.");
        if (limits.JsonBytes <= 0
            || limits.JsonBytes > offeredLimits.JsonBytes
            || limits.BinaryBytes <= 0
            || limits.BinaryBytes > offeredLimits.BinaryBytes
            || limits.InFlight <= 0
            || limits.InFlight > offeredLimits.InFlight)
        {
            throw new InvalidDataException("ServerHello limits were outside the offered range.");
        }

        JsonElement capabilities = hello.Capabilities
            ?? throw new InvalidDataException("Accepted ServerHello omitted capabilities.");
        if (capabilities.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("ServerHello capabilities must be a JSON object.");
        }

        return new BridgeSessionInfo(
            instanceId,
            serverEpoch,
            processId,
            processCreationTime,
            protocol,
            versions,
            limits,
            capabilities.Clone());
    }

    private static Guid RequireNonEmpty(Guid? value, string fieldName)
    {
        if (value is null || value == Guid.Empty)
        {
            throw new InvalidDataException($"Accepted ServerHello omitted or provided an invalid {fieldName}.");
        }

        return value.Value;
    }
}

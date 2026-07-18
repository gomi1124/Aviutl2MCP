using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Handshake;
using AviUtl2MCP.BridgeClient.Transport;

namespace AviUtl2MCP.BridgeClient.Connections;

public sealed class BridgeConnection : IBridgeConnection
{
    private readonly IBridgeTransport transport;
    private readonly BridgeHandshakeClient handshakeClient;
    private BridgeSessionInfo? sessionInfo;

    public BridgeConnection(
        BridgeInstanceDescriptor descriptor,
        IBridgeTransport transport,
        Guid clientInstanceId,
        string clientVersion)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(transport);
        Descriptor = descriptor;
        this.transport = transport;
        handshakeClient = new BridgeHandshakeClient(transport, clientInstanceId, clientVersion);
    }

    public BridgeInstanceDescriptor Descriptor { get; }

    public BridgeSessionInfo SessionInfo => sessionInfo
        ?? throw new InvalidOperationException("Bridge handshake has not completed.");

    public bool IsConnected => sessionInfo is not null && transport.IsConnected;

    public async ValueTask HandshakeAsync(CancellationToken cancellationToken)
    {
        if (sessionInfo is not null)
        {
            throw new InvalidOperationException("Bridge handshake has already completed.");
        }

        BridgeSessionInfo session = await handshakeClient.HandshakeAsync(
            Descriptor.PipeName,
            Descriptor.InstanceId,
            cancellationToken).ConfigureAwait(false);
        if (session.AviutlProcessId != Descriptor.ProcessId
            || session.AviutlProcessCreationTime != Descriptor.ProcessCreationTime)
        {
            throw new InvalidDataException("Handshake process identity did not match the instance descriptor.");
        }

        sessionInfo = session;
    }

    public ValueTask DisposeAsync()
    {
        sessionInfo = null;
        return transport.DisposeAsync();
    }
}

using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Handshake;
using AviUtl2MCP.BridgeClient.Messaging;

namespace AviUtl2MCP.BridgeClient.Connections;

public interface IBridgeConnection : IAsyncDisposable
{
    BridgeInstanceDescriptor Descriptor { get; }

    BridgeSessionInfo SessionInfo { get; }

    bool IsConnected { get; }

    ValueTask<BridgeResponse> SendAsync(
        BridgeRequest request,
        ReadOnlyMemory<byte> binary,
        DateTimeOffset deadline,
        CancellationToken cancellationToken);

    ValueTask CancelAsync(Guid requestId, CancellationToken cancellationToken);
}

public interface IBridgeConnectionFactory
{
    ValueTask<IBridgeConnection> CreateConnectionAsync(
        BridgeInstanceDescriptor descriptor,
        CancellationToken cancellationToken);
}

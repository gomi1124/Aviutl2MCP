using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Handshake;

namespace AviUtl2MCP.BridgeClient.Connections;

public interface IBridgeConnection : IAsyncDisposable
{
    BridgeInstanceDescriptor Descriptor { get; }

    BridgeSessionInfo SessionInfo { get; }

    bool IsConnected { get; }
}

public interface IBridgeConnectionFactory
{
    ValueTask<IBridgeConnection> CreateConnectionAsync(
        BridgeInstanceDescriptor descriptor,
        CancellationToken cancellationToken);
}

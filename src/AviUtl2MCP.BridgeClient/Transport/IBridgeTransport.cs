using AviUtl2MCP.BridgeClient.Protocol;

namespace AviUtl2MCP.BridgeClient.Transport;

public interface IBridgeTransport : IIpcFrameReader, IAsyncDisposable
{
    bool IsConnected { get; }

    ValueTask ConnectAsync(string pipeName, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

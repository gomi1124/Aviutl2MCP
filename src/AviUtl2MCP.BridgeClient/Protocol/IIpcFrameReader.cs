namespace AviUtl2MCP.BridgeClient.Protocol;

public interface IIpcFrameReader
{
    ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

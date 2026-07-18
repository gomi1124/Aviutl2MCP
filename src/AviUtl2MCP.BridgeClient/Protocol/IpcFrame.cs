namespace AviUtl2MCP.BridgeClient.Protocol;

public sealed record IpcEncodedFrame(byte[] Bytes, string PayloadSha256);

public sealed record IpcFrame(
    IpcFrameHeader Header,
    ReadOnlyMemory<byte> JsonBytes,
    ReadOnlyMemory<byte> BinaryBytes,
    string PayloadSha256);

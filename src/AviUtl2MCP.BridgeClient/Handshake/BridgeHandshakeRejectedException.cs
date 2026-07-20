namespace AviUtl2MCP.BridgeClient.Handshake;

public sealed class BridgeHandshakeRejectedException : IOException
{
    public BridgeHandshakeRejectedException(
        HandshakeError error,
        ProtocolRange? clientRange,
        ProtocolRange? serverRange)
        : base($"Bridge handshake was rejected ({error.Code}): {error.Message}")
    {
        Error = error;
        ClientRange = clientRange;
        ServerRange = serverRange;
    }

    public HandshakeError Error { get; }

    public ProtocolRange? ClientRange { get; }

    public ProtocolRange? ServerRange { get; }
}

namespace AviUtl2MCP.BridgeClient.Tracking;

public enum CancelStatus
{
    Cancelled,
    TooLate,
    NotFound,
}

public sealed record CancelAcknowledgement(CancelStatus Status, bool ResponseWillFollow);

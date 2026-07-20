using AviUtl2MCP.BridgeClient.Protocol;

namespace AviUtl2MCP.BridgeClient.Tracking;

public sealed class IpcRequestRegistration
{
    internal IpcRequestRegistration(
        Guid requestId,
        Task<IpcFrame> completion,
        Task<CancelAcknowledgement> cancellationAcknowledged)
    {
        RequestId = requestId;
        Completion = completion;
        CancellationAcknowledged = cancellationAcknowledged;
    }

    public Guid RequestId { get; }

    public Task<IpcFrame> Completion { get; }

    public Task<CancelAcknowledgement> CancellationAcknowledged { get; }
}

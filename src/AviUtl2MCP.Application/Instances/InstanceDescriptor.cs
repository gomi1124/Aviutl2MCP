namespace AviUtl2MCP.Application.Instances;

public sealed record InstanceDescriptor(
    Guid InstanceId,
    int ProcessId,
    DateTimeOffset ProcessCreationTime,
    string BridgeVersion,
    bool IsAvailable);

using AviUtl2MCP.Application.Instances;

namespace AviUtl2MCP.BridgeClient.Discovery;

public sealed record BridgeInstanceDescriptor(
    Guid InstanceId,
    int ProcessId,
    long ProcessCreationTime,
    string PipeName,
    string BridgeVersion,
    ushort ProtocolMajor)
{
    public InstanceDescriptor ToApplicationDescriptor()
    {
        return new InstanceDescriptor(
            InstanceId,
            ProcessId,
            DateTimeOffset.FromFileTime(ProcessCreationTime),
            BridgeVersion,
            true);
    }
}

public enum DescriptorIssueCode
{
    DirectoryUnavailable,
    FileTooLarge,
    ReparsePointRejected,
    Malformed,
    FileNameMismatch,
    ProtocolIncompatible,
    ProcessUnavailable,
    ProcessIdentityMismatch,
    DuplicateInstance,
}

public sealed record DescriptorIssue(
    string FileName,
    DescriptorIssueCode Code,
    string Message);

public sealed record DescriptorSnapshot(
    IReadOnlyList<BridgeInstanceDescriptor> Instances,
    IReadOnlyList<DescriptorIssue> Issues);

public sealed record InstanceDescriptorDocument(
    Guid InstanceId,
    int ProcessId,
    long ProcessCreationTime,
    string PipeName,
    string BridgeVersion,
    ushort ProtocolMajor);

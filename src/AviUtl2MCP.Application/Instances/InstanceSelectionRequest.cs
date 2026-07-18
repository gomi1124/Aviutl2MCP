using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Instances;

public sealed record InstanceSelectionRequest(
    Guid? RequestedInstanceId,
    IReadOnlyList<ObjectLocator> Locators,
    Guid? EnvironmentInstanceId,
    IReadOnlyList<InstanceDescriptor> Candidates);

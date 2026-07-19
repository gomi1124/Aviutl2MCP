using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;

namespace AviUtl2MCP.Application.Instances;

public interface IInstanceResolver
{
    ValueTask<ApplicationResult<InstanceDescriptor>> ResolveAsync(
        Guid? requestedInstanceId,
        IReadOnlyList<ObjectLocator> locators,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<InstanceDescriptor>> ListCandidatesAsync(
        CancellationToken cancellationToken);
}

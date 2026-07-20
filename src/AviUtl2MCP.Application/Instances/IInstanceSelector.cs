using AviUtl2MCP.Application.Errors;

namespace AviUtl2MCP.Application.Instances;

public interface IInstanceSelector
{
    ValueTask<ApplicationResult<InstanceDescriptor>> SelectInstanceAsync(
        InstanceSelectionRequest request,
        CancellationToken cancellationToken);
}

using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.BridgeClient.Connections;

namespace AviUtl2MCP.Server;

public sealed class ServerInstanceResolver(
    BridgeConnectionRegistry connectionRegistry,
    IInstanceSelector instanceSelector,
    ServerRuntimeIdentity runtimeIdentity)
{
    private readonly BridgeConnectionRegistry _connectionRegistry = connectionRegistry;
    private readonly IInstanceSelector _instanceSelector = instanceSelector;
    private readonly ServerRuntimeIdentity _runtimeIdentity = runtimeIdentity;

    public async ValueTask<ApplicationResult<InstanceDescriptor>> ResolveAsync(
        Guid? requestedInstanceId,
        CancellationToken cancellationToken)
    {
        _ = await _connectionRegistry.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return await _instanceSelector.SelectInstanceAsync(
            new InstanceSelectionRequest(
                requestedInstanceId,
                [],
                _runtimeIdentity.EnvironmentInstanceId,
                _connectionRegistry.GetCandidates()),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Guid?> TryResolveIdAsync(
        Guid? requestedInstanceId,
        CancellationToken cancellationToken)
    {
        ApplicationResult<InstanceDescriptor> result = await ResolveAsync(
            requestedInstanceId,
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value!.InstanceId : null;
    }
}

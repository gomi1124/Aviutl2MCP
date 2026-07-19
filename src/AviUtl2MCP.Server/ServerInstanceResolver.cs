using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.BridgeClient.Connections;

namespace AviUtl2MCP.Server;

public sealed class ServerInstanceResolver(
    BridgeConnectionRegistry connectionRegistry,
    IInstanceSelector instanceSelector,
    ServerRuntimeIdentity runtimeIdentity) : IInstanceResolver
{
    private readonly BridgeConnectionRegistry _connectionRegistry = connectionRegistry;
    private readonly IInstanceSelector _instanceSelector = instanceSelector;
    private readonly ServerRuntimeIdentity _runtimeIdentity = runtimeIdentity;

    public async ValueTask<ApplicationResult<InstanceDescriptor>> ResolveAsync(
        Guid? requestedInstanceId,
        CancellationToken cancellationToken)
    {
        return await ResolveAsync(requestedInstanceId, [], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ApplicationResult<InstanceDescriptor>> ResolveAsync(
        Guid? requestedInstanceId,
        IReadOnlyList<ObjectLocator> locators,
        CancellationToken cancellationToken)
    {
        _ = await _connectionRegistry.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return await _instanceSelector.SelectInstanceAsync(
            new InstanceSelectionRequest(
                requestedInstanceId,
                locators,
                _runtimeIdentity.EnvironmentInstanceId,
                _connectionRegistry.GetCandidates()),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<InstanceDescriptor>> ListCandidatesAsync(
        CancellationToken cancellationToken)
    {
        _ = await _connectionRegistry.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return _connectionRegistry.GetCandidates();
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

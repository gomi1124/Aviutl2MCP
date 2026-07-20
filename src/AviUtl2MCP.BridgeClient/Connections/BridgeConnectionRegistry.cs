using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.BridgeClient.Discovery;

namespace AviUtl2MCP.BridgeClient.Connections;

public sealed class BridgeConnectionRegistry : IAsyncDisposable
{
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly InstanceDescriptorWatcher descriptorWatcher;
    private readonly IBridgeConnectionFactory connectionFactory;
    private readonly Dictionary<Guid, BridgeInstanceDescriptor> descriptors = [];
    private readonly Dictionary<Guid, IBridgeConnection> connections = [];
    private DescriptorIssue[] discoveryIssues = [];
    private bool isDisposed;

    public BridgeConnectionRegistry(
        InstanceDescriptorWatcher descriptorWatcher,
        IBridgeConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(descriptorWatcher);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        this.descriptorWatcher = descriptorWatcher;
        this.connectionFactory = connectionFactory;
    }

    public async ValueTask<DescriptorSnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        DescriptorSnapshot snapshot = descriptorWatcher.ReadDescriptors();
        List<IBridgeConnection> staleConnections = [];
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            Dictionary<Guid, BridgeInstanceDescriptor> current = snapshot.Instances
                .ToDictionary(descriptor => descriptor.InstanceId);
            foreach ((Guid instanceId, IBridgeConnection connection) in connections.ToArray())
            {
                if (!current.TryGetValue(instanceId, out BridgeInstanceDescriptor? descriptor)
                    || descriptor != connection.Descriptor)
                {
                    _ = connections.Remove(instanceId);
                    staleConnections.Add(connection);
                }
            }

            descriptors.Clear();
            foreach ((Guid instanceId, BridgeInstanceDescriptor descriptor) in current)
            {
                descriptors.Add(instanceId, descriptor);
            }

            discoveryIssues = [.. snapshot.Issues];
        }
        finally
        {
            stateGate.Release();
        }

        foreach (IBridgeConnection connection in staleConnections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        return snapshot;
    }

    public async ValueTask<IBridgeConnection> GetConnectionAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(instanceId, Guid.Empty);
        _ = await DiscoverAsync(cancellationToken).ConfigureAwait(false);

        BridgeInstanceDescriptor descriptor;
        IBridgeConnection? staleConnection = null;
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!descriptors.TryGetValue(instanceId, out descriptor!))
            {
                throw new KeyNotFoundException("The requested AviUtl2 instance is not available.");
            }

            if (connections.TryGetValue(instanceId, out IBridgeConnection? existing))
            {
                if (existing.IsConnected)
                {
                    return existing;
                }

                _ = connections.Remove(instanceId);
                staleConnection = existing;
            }
        }
        finally
        {
            stateGate.Release();
        }

        if (staleConnection is not null)
        {
            await staleConnection.DisposeAsync().ConfigureAwait(false);
        }

        IBridgeConnection created = await connectionFactory.CreateConnectionAsync(
            descriptor,
            cancellationToken).ConfigureAwait(false);
        IBridgeConnection? connectionToDispose = null;
        try
        {
            await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await created.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (!descriptors.TryGetValue(instanceId, out BridgeInstanceDescriptor? currentDescriptor)
                || currentDescriptor != descriptor)
            {
                connectionToDispose = created;
                throw new IOException("The instance descriptor changed while the bridge was connecting.");
            }

            if (connections.TryGetValue(instanceId, out IBridgeConnection? concurrentConnection)
                && concurrentConnection.IsConnected)
            {
                connectionToDispose = created;
                return concurrentConnection;
            }

            connections[instanceId] = created;
            return created;
        }
        finally
        {
            stateGate.Release();
            if (connectionToDispose is not null)
            {
                await connectionToDispose.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public IReadOnlyList<InstanceDescriptor> GetCandidates()
    {
        stateGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            return descriptors.Values
                .OrderBy(descriptor => descriptor.InstanceId)
                .Select(descriptor => descriptor.ToApplicationDescriptor())
                .ToArray();
        }
        finally
        {
            stateGate.Release();
        }
    }

    public IReadOnlyList<DescriptorIssue> GetDiscoveryIssues()
    {
        stateGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            return discoveryIssues.ToArray();
        }
        finally
        {
            stateGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        IBridgeConnection[] connectionsToDispose;
        await stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            connectionsToDispose = [.. connections.Values];
            connections.Clear();
            descriptors.Clear();
            discoveryIssues = [];
        }
        finally
        {
            stateGate.Release();
        }

        foreach (IBridgeConnection connection in connectionsToDispose)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

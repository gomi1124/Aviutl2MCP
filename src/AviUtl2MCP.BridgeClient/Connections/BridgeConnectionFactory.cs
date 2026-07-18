using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Transport;

namespace AviUtl2MCP.BridgeClient.Connections;

public sealed class BridgeConnectionFactory : IBridgeConnectionFactory
{
    private readonly Guid clientInstanceId;
    private readonly string clientVersion;
    private readonly Func<IBridgeTransport> transportFactory;

    public BridgeConnectionFactory(
        Guid clientInstanceId,
        string clientVersion,
        Func<IBridgeTransport>? transportFactory = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(clientInstanceId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        this.clientInstanceId = clientInstanceId;
        this.clientVersion = clientVersion;
        this.transportFactory = transportFactory ?? (static () => new NamedPipeBridgeTransport());
    }

    public async ValueTask<IBridgeConnection> CreateConnectionAsync(
        BridgeInstanceDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        BridgeConnection connection = new(
            descriptor,
            transportFactory(),
            clientInstanceId,
            clientVersion);
        try
        {
            await connection.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

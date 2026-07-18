using System.IO.Pipes;
using AviUtl2MCP.BridgeClient.Transport;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class NamedPipeBridgeTransportTests
{
    [TestMethod]
    public async Task ConnectReadAndWriteAsyncUseLocalDuplexPipe()
    {
        // Arrange
        string pipeName = $"AviUtl2MCP.test.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await using NamedPipeBridgeTransport transport = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task waitForConnection = server.WaitForConnectionAsync(timeout.Token);

        // Act
        await transport.ConnectAsync(pipeName, timeout.Token);
        await waitForConnection;
        byte[] serverPayload = [1, 2, 3, 4];
        Task serverWrite = WriteSplitAsync(server, serverPayload, timeout.Token);
        byte[] clientBuffer = new byte[serverPayload.Length];
        await transport.ReadExactAsync(clientBuffer, timeout.Token);
        await serverWrite;

        byte[] clientPayload = [5, 6, 7];
        ValueTask clientWrite = transport.WriteAsync(clientPayload, timeout.Token);
        byte[] serverBuffer = new byte[clientPayload.Length];
        await server.ReadExactlyAsync(serverBuffer, timeout.Token);
        await clientWrite;

        // Assert
        Assert.IsTrue(transport.IsConnected);
        CollectionAssert.AreEqual(serverPayload, clientBuffer);
        CollectionAssert.AreEqual(clientPayload, serverBuffer);
    }

    [TestMethod]
    public async Task ConnectAsyncCanBeCancelledAndRetried()
    {
        // Arrange
        string unavailablePipeName = $"AviUtl2MCP.missing.{Guid.NewGuid():N}";
        await using NamedPipeBridgeTransport transport = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        // Act
        Func<Task> cancelledConnect = async () =>
            await transport.ConnectAsync(unavailablePipeName, cancellation.Token);

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(cancelledConnect);
        Assert.IsFalse(transport.IsConnected);

        string retryPipeName = $"AviUtl2MCP.retry.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(retryPipeName);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task waitForConnection = server.WaitForConnectionAsync(timeout.Token);
        await transport.ConnectAsync(retryPipeName, timeout.Token);
        await waitForConnection;
        Assert.IsTrue(transport.IsConnected);
    }

    [TestMethod]
    public async Task ReadExactAsyncReportsPrematureDisconnect()
    {
        // Arrange
        string pipeName = $"AviUtl2MCP.disconnect.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await using NamedPipeBridgeTransport transport = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task waitForConnection = server.WaitForConnectionAsync(timeout.Token);
        await transport.ConnectAsync(pipeName, timeout.Token);
        await waitForConnection;
        Task serverWrite = WriteAndDisconnectAsync(server, timeout.Token);

        // Act
        Func<Task> read = async () =>
            await transport.ReadExactAsync(new byte[3], timeout.Token);

        // Assert
        EndOfStreamException exception = await Assert.ThrowsExactlyAsync<EndOfStreamException>(read);
        await serverWrite;
        StringAssert.Contains(exception.Message, "2 of 3", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DisposeAsyncStopsPendingConnectWithoutRestoringConnectionState()
    {
        // Arrange
        string unavailablePipeName = $"AviUtl2MCP.dispose.{Guid.NewGuid():N}";
        NamedPipeBridgeTransport transport = new();
        Task connect = transport.ConnectAsync(unavailablePipeName, CancellationToken.None).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(25));

        // Act
        await transport.DisposeAsync();
        Task completed = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(5)));

        // Assert
        Assert.AreSame(connect, completed);
        Assert.IsTrue(connect.IsFaulted || connect.IsCanceled);
        Func<Task> waitForConnect = async () => await connect;
        _ = await Assert.ThrowsAsync<Exception>(waitForConnect);
        Assert.IsFalse(transport.IsConnected);
        Func<Task> reconnect = async () =>
            await transport.ConnectAsync(unavailablePipeName, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(reconnect);
    }

    private static async Task WriteSplitAsync(
        NamedPipeServerStream server,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await server.WriteAsync(payload.AsMemory(0, 1), cancellationToken);
        await server.WriteAsync(payload.AsMemory(1), cancellationToken);
        await server.FlushAsync(cancellationToken);
    }

    private static async Task WriteAndDisconnectAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        await server.WriteAsync(new byte[] { 1, 2 }, cancellationToken);
        await server.FlushAsync(cancellationToken);
        server.Disconnect();
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
}

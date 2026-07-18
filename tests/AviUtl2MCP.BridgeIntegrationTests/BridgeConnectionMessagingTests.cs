using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Handshake;
using AviUtl2MCP.BridgeClient.Messaging;
using AviUtl2MCP.BridgeClient.Protocol;
using AviUtl2MCP.BridgeClient.Tracking;
using AviUtl2MCP.BridgeClient.Transport;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class BridgeConnectionMessagingTests
{
    [TestMethod]
    public async Task SendAsyncCorrelatesResponsesReturnedOutOfOrder()
    {
        // Arrange
        Guid instanceId = Guid.NewGuid();
        string pipeName = $"AviUtl2MCP.messages.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(pipeName);
        BridgeInstanceDescriptor descriptor = new(instanceId, 1234, 5678, pipeName, "0.1.0-test", 1);
        await using BridgeConnection connection = new(
            descriptor,
            new NamedPipeBridgeTransport(),
            Guid.NewGuid(),
            "0.1.0-test");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        BridgeRequest firstRequest = CreateRequest("project.get");
        BridgeRequest secondRequest = CreateRequest("timeline.get");
        Task fakeBridge = RunOutOfOrderBridgeAsync(
            server,
            instanceId,
            firstRequest.CorrelationId,
            secondRequest.CorrelationId,
            timeout.Token);
        await connection.HandshakeAsync(timeout.Token);

        // Act
        Task<BridgeResponse> first = connection.SendAsync(
            firstRequest,
            ReadOnlyMemory<byte>.Empty,
            DateTimeOffset.UtcNow.AddSeconds(5),
            timeout.Token).AsTask();
        Task<BridgeResponse> second = connection.SendAsync(
            secondRequest,
            ReadOnlyMemory<byte>.Empty,
            DateTimeOffset.UtcNow.AddSeconds(5),
            timeout.Token).AsTask();
        await fakeBridge;
        BridgeResponse firstResponse = await first;
        BridgeResponse secondResponse = await second;

        // Assert
        Assert.AreEqual(firstRequest.CorrelationId, firstResponse.Envelope.CorrelationId);
        Assert.AreEqual(secondRequest.CorrelationId, secondResponse.Envelope.CorrelationId);
        Assert.AreEqual(1, firstResponse.Envelope.Result?.GetProperty("sequence").GetInt32());
        Assert.AreEqual(2, secondResponse.Envelope.Result?.GetProperty("sequence").GetInt32());
        Assert.IsTrue(connection.IsConnected);
    }

    [TestMethod]
    public async Task SendAsyncTimesOutWithoutClosingHealthyConnection()
    {
        // Arrange
        Guid instanceId = Guid.NewGuid();
        string pipeName = $"AviUtl2MCP.timeout.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(pipeName);
        BridgeInstanceDescriptor descriptor = new(instanceId, 1234, 5678, pipeName, "0.1.0-test", 1);
        await using BridgeConnection connection = new(
            descriptor,
            new NamedPipeBridgeTransport(),
            Guid.NewGuid(),
            "0.1.0-test");
        using CancellationTokenSource testTimeout = new(TimeSpan.FromSeconds(5));
        Task<IpcFrame> fakeBridge = AcceptRequestAndTimeoutCancelAsync(server, instanceId, testTimeout.Token);
        await connection.HandshakeAsync(testTimeout.Token);
        BridgeRequest request = CreateRequest("diagnostics.status");

        // Act
        Func<Task> send = async () => await connection.SendAsync(
            request,
            ReadOnlyMemory<byte>.Empty,
            DateTimeOffset.UtcNow.AddMilliseconds(50),
            testTimeout.Token);

        // Assert
        await Assert.ThrowsExactlyAsync<TimeoutException>(send);
        IpcFrame received = await fakeBridge;
        Assert.AreEqual(request.CorrelationId, received.Header.RequestId);
        Assert.AreEqual(IpcMessageKind.Cancel, received.Header.MessageKind);
        Assert.IsTrue(connection.IsConnected);
    }

    [TestMethod]
    public async Task CancellationSendsCancelAndAcceptsLateResponse()
    {
        // Arrange
        Guid instanceId = Guid.NewGuid();
        string pipeName = $"AviUtl2MCP.cancel.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(pipeName);
        BridgeInstanceDescriptor descriptor = new(instanceId, 1234, 5678, pipeName, "0.1.0-test", 1);
        await using BridgeConnection connection = new(
            descriptor,
            new NamedPipeBridgeTransport(),
            Guid.NewGuid(),
            "0.1.0-test");
        using CancellationTokenSource testTimeout = new(TimeSpan.FromSeconds(5));
        TaskCompletionSource requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Guid> fakeBridge = RunCancellationBridgeAsync(
            server,
            instanceId,
            requestReceived,
            testTimeout.Token);
        await connection.HandshakeAsync(testTimeout.Token);
        BridgeRequest request = CreateRequest("preview.render");
        using CancellationTokenSource callerCancellation = new();
        Task<BridgeResponse> send = connection.SendAsync(
            request,
            ReadOnlyMemory<byte>.Empty,
            DateTimeOffset.UtcNow.AddSeconds(5),
            callerCancellation.Token).AsTask();
        await requestReceived.Task.WaitAsync(testTimeout.Token);

        // Act
        callerCancellation.Cancel();
        Func<Task> waitForSend = async () => await send;

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(waitForSend);
        Assert.AreEqual(request.CorrelationId, await fakeBridge);
        Assert.IsTrue(connection.IsConnected);
    }

    [TestMethod]
    public async Task RegistryReconnectsSameDescriptorAfterBridgeDisconnect()
    {
        // Arrange
        string directoryPath = CreateTestDirectory();
        try
        {
            Guid instanceId = Guid.NewGuid();
            Guid firstEpoch = Guid.NewGuid();
            Guid secondEpoch = Guid.NewGuid();
            string pipeName = $"AviUtl2MCP.v1.{instanceId:D}";
            long processCreationTime = GetCurrentProcessCreationTime();
            WriteDescriptor(directoryPath, instanceId, pipeName, processCreationTime);
            InstanceDescriptorWatcher watcher = new(directoryPath);
            BridgeConnectionFactory factory = new(Guid.NewGuid(), "0.1.0-test");
            await using BridgeConnectionRegistry registry = new(watcher, factory);
            TaskCompletionSource closeFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task firstServer = RunHandshakeUntilCloseAsync(
                pipeName,
                instanceId,
                firstEpoch,
                processCreationTime,
                closeFirst.Task,
                timeout.Token);
            IBridgeConnection first = await registry.GetConnectionAsync(instanceId, timeout.Token);
            Guid observedFirstEpoch = first.SessionInfo.ServerEpoch;

            // Act
            closeFirst.SetResult();
            await firstServer;
            await WaitForDisconnectAsync(first, timeout.Token);
            TaskCompletionSource closeSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task secondServer = RunHandshakeUntilCloseAsync(
                pipeName,
                instanceId,
                secondEpoch,
                processCreationTime,
                closeSecond.Task,
                timeout.Token);
            IBridgeConnection second = await registry.GetConnectionAsync(instanceId, timeout.Token);

            // Assert
            Assert.AreNotSame(first, second);
            Assert.AreEqual(firstEpoch, observedFirstEpoch);
            Assert.AreEqual(secondEpoch, second.SessionInfo.ServerEpoch);
            Assert.IsTrue(second.IsConnected);
            closeSecond.SetResult();
            await secondServer;
        }
        finally
        {
            DeleteTestDirectory(directoryPath);
        }
    }

    [TestMethod]
    public async Task PendingRequestFailsWhenBridgeClosesConnection()
    {
        // Arrange
        Guid instanceId = Guid.NewGuid();
        string pipeName = $"AviUtl2MCP.close.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(pipeName);
        BridgeInstanceDescriptor descriptor = new(instanceId, 1234, 5678, pipeName, "0.1.0-test", 1);
        await using BridgeConnection connection = new(
            descriptor,
            new NamedPipeBridgeTransport(),
            Guid.NewGuid(),
            "0.1.0-test");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task fakeBridge = CloseAfterRequestAsync(server, instanceId, timeout.Token);
        await connection.HandshakeAsync(timeout.Token);
        BridgeRequest request = CreateRequest("project.get");

        // Act
        Func<Task> send = async () => await connection.SendAsync(
            request,
            ReadOnlyMemory<byte>.Empty,
            DateTimeOffset.UtcNow.AddSeconds(5),
            timeout.Token);

        // Assert
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(send);
        await fakeBridge;
        Assert.IsFalse(connection.IsConnected);
    }

    private static async Task RunOutOfOrderBridgeAsync(
        NamedPipeServerStream server,
        Guid instanceId,
        Guid firstRequestId,
        Guid secondRequestId,
        CancellationToken cancellationToken)
    {
        await AcceptHandshakeAsync(server, instanceId, cancellationToken);
        IpcFrame first = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        IpcFrame second = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        await WriteSuccessAsync(
            server,
            second.Header.RequestId,
            instanceId,
            second.Header.RequestId == firstRequestId ? 1 : 2,
            cancellationToken);
        await WriteSuccessAsync(
            server,
            first.Header.RequestId,
            instanceId,
            first.Header.RequestId == secondRequestId ? 2 : 1,
            cancellationToken);
    }

    private static async Task RunHandshakeUntilCloseAsync(
        string pipeName,
        Guid instanceId,
        Guid serverEpoch,
        long processCreationTime,
        Task closeSignal,
        CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(cancellationToken);
        IpcFrame hello = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        ServerHello response = new(
            true,
            instanceId,
            serverEpoch,
            Environment.ProcessId,
            processCreationTime,
            new NegotiatedProtocol(1, 0),
            new BridgeVersions("0.1.0", "2.1.0", "2.1.0"),
            new HandshakeLimits(1024 * 1024, 1024 * 1024, 8),
            CreateJsonElement("{}"));
        await WriteFrameAsync(
            server,
            IpcMessageKind.ServerHello,
            hello.Header.RequestId,
            ContractJsonSerializer.SerializeContract(response),
            IpcFrameOption.None,
            cancellationToken);
        await closeSignal.WaitAsync(cancellationToken);
        await WriteFrameAsync(
            server,
            IpcMessageKind.Close,
            Guid.Empty,
            "{}",
            IpcFrameOption.None,
            cancellationToken);
    }

    private static async Task<IpcFrame> AcceptRequestAndTimeoutCancelAsync(
        NamedPipeServerStream server,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await AcceptHandshakeAsync(server, instanceId, cancellationToken);
        IpcFrame request = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        IpcFrame cancel = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        Assert.AreEqual(request.Header.RequestId, cancel.Header.RequestId);
        return cancel;
    }

    private static async Task CloseAfterRequestAsync(
        NamedPipeServerStream server,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await AcceptHandshakeAsync(server, instanceId, cancellationToken);
        _ = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        await WriteFrameAsync(
            server,
            IpcMessageKind.Close,
            Guid.Empty,
            "{}",
            IpcFrameOption.None,
            cancellationToken);
    }

    private static async Task<Guid> RunCancellationBridgeAsync(
        NamedPipeServerStream server,
        Guid instanceId,
        TaskCompletionSource requestReceived,
        CancellationToken cancellationToken)
    {
        await AcceptHandshakeAsync(server, instanceId, cancellationToken);
        IpcFrame request = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        requestReceived.SetResult();
        IpcFrame cancel = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        Assert.AreEqual(IpcMessageKind.Cancel, cancel.Header.MessageKind);
        Assert.AreEqual(request.Header.RequestId, cancel.Header.RequestId);
        CancelAcknowledgement acknowledgement = new(CancelStatus.TooLate, true);
        await WriteFrameAsync(
            server,
            IpcMessageKind.CancelAck,
            request.Header.RequestId,
            ContractJsonSerializer.SerializeContract(acknowledgement),
            IpcFrameOption.None,
            cancellationToken);
        await WriteSuccessAsync(server, request.Header.RequestId, instanceId, 1, cancellationToken);
        return cancel.Header.RequestId;
    }

    private static async Task AcceptHandshakeAsync(
        NamedPipeServerStream server,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await server.WaitForConnectionAsync(cancellationToken);
        IpcFrame hello = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        ServerHello response = new(
            true,
            instanceId,
            Guid.NewGuid(),
            1234,
            5678,
            new NegotiatedProtocol(1, 0),
            new BridgeVersions("0.1.0", "2.1.0", "2.1.0"),
            new HandshakeLimits(1024 * 1024, 1024 * 1024, 8),
            CreateJsonElement("{}"));
        await WriteFrameAsync(
            server,
            IpcMessageKind.ServerHello,
            hello.Header.RequestId,
            ContractJsonSerializer.SerializeContract(response),
            IpcFrameOption.None,
            cancellationToken);
    }

    private static async Task WriteSuccessAsync(
        NamedPipeServerStream server,
        Guid requestId,
        Guid instanceId,
        int sequence,
        CancellationToken cancellationToken)
    {
        BridgeResponseEnvelope response = new(
            true,
            requestId,
            instanceId,
            null,
            null,
            CreateJsonElement($"{{\"sequence\":{sequence}}}"),
            [],
            null);
        await WriteFrameAsync(
            server,
            IpcMessageKind.Response,
            requestId,
            ContractJsonSerializer.SerializeContract(response),
            IpcFrameOption.None,
            cancellationToken);
    }

    private static async Task WriteFrameAsync(
        NamedPipeServerStream server,
        IpcMessageKind kind,
        Guid requestId,
        string json,
        IpcFrameOption options,
        CancellationToken cancellationToken)
    {
        IpcEncodedFrame encoded = IpcFrameCodec.EncodeFrame(
            kind,
            options,
            requestId,
            Encoding.UTF8.GetBytes(json),
            []);
        await server.WriteAsync(encoded.Bytes, cancellationToken);
        await server.FlushAsync(cancellationToken);
    }

    private static BridgeRequest CreateRequest(string method)
    {
        return new BridgeRequest(
            method,
            Guid.CreateVersion7(),
            5000,
            null,
            false,
            CreateJsonElement("{}"));
    }

    private static JsonElement CreateJsonElement(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task WaitForDisconnectAsync(
        IBridgeConnection connection,
        CancellationToken cancellationToken)
    {
        while (connection.IsConnected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "AviUtl2MCP.ReconnectTests",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static long GetCurrentProcessCreationTime()
    {
        using Process process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().ToFileTimeUtc();
    }

    private static void WriteDescriptor(
        string directoryPath,
        Guid instanceId,
        string pipeName,
        long processCreationTime)
    {
        InstanceDescriptorDocument descriptor = new(
            instanceId,
            Environment.ProcessId,
            processCreationTime,
            pipeName,
            "0.1.0-test",
            1);
        File.WriteAllText(
            Path.Combine(directoryPath, $"{instanceId:D}.json"),
            ContractJsonSerializer.SerializeContract(descriptor));
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

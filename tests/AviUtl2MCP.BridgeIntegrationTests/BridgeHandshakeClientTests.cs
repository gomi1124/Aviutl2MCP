using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Handshake;
using AviUtl2MCP.BridgeClient.Protocol;
using AviUtl2MCP.BridgeClient.Transport;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class BridgeHandshakeClientTests
{
    [TestMethod]
    public async Task HandshakeAsyncNegotiatesSessionOverNamedPipe()
    {
        // Arrange
        Guid targetInstanceId = Guid.NewGuid();
        Guid serverEpoch = Guid.NewGuid();
        string pipeName = $"AviUtl2MCP.handshake.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await using NamedPipeBridgeTransport transport = new();
        BridgeHandshakeClient client = new(transport, Guid.NewGuid(), "0.1.0-test");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<ClientHello> serverHandshake = RespondAsync(
            server,
            new ServerHello(
                true,
                targetInstanceId,
                serverEpoch,
                1234,
                134135000000000000,
                new NegotiatedProtocol(1, 0),
                new BridgeVersions("0.1.0", "2.1.0", "2.1.0"),
                new HandshakeLimits(4 * 1024 * 1024, 8 * 1024 * 1024, 4),
                CreateCapabilities()),
            timeout.Token);

        // Act
        BridgeSessionInfo session = await client.HandshakeAsync(pipeName, targetInstanceId, timeout.Token);
        ClientHello receivedHello = await serverHandshake;

        // Assert
        Assert.AreEqual(targetInstanceId, receivedHello.TargetInstanceId);
        Assert.AreEqual(Environment.ProcessId, receivedHello.ClientProcessId);
        Assert.AreEqual(targetInstanceId, session.InstanceId);
        Assert.AreEqual(serverEpoch, session.ServerEpoch);
        Assert.AreEqual(4, session.Limits.InFlight);
        Assert.AreEqual(JsonValueKind.Object, session.Capabilities.ValueKind);
    }

    [TestMethod]
    public async Task HandshakeAsyncPreservesProtocolRejectionDetails()
    {
        // Arrange
        Guid targetInstanceId = Guid.NewGuid();
        string pipeName = $"AviUtl2MCP.reject.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await using NamedPipeBridgeTransport transport = new();
        BridgeHandshakeClient client = new(transport, Guid.NewGuid(), "0.1.0-test");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        ProtocolRange clientRange = new(1, 0, 1, 0);
        ProtocolRange serverRange = new(2, 0, 2, 0);
        Task<ClientHello> serverHandshake = RespondAsync(
            server,
            new ServerHello(
                false,
                Error: new HandshakeError("protocol_incompatible", "No compatible protocol major."),
                ClientRange: clientRange,
                ServerRange: serverRange),
            timeout.Token);

        // Act
        Func<Task> handshake = async () =>
            await client.HandshakeAsync(pipeName, targetInstanceId, timeout.Token);

        // Assert
        BridgeHandshakeRejectedException exception =
            await Assert.ThrowsExactlyAsync<BridgeHandshakeRejectedException>(handshake);
        _ = await serverHandshake;
        Assert.AreEqual("protocol_incompatible", exception.Error.Code);
        Assert.AreEqual(clientRange, exception.ClientRange);
        Assert.AreEqual(serverRange, exception.ServerRange);
    }

    [TestMethod]
    public async Task HandshakeAsyncRejectsMismatchedTargetInstance()
    {
        // Arrange
        Guid targetInstanceId = Guid.NewGuid();
        string pipeName = $"AviUtl2MCP.target.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await using NamedPipeBridgeTransport transport = new();
        BridgeHandshakeClient client = new(transport, Guid.NewGuid(), "0.1.0-test");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<ClientHello> serverHandshake = RespondAsync(
            server,
            new ServerHello(
                true,
                Guid.NewGuid(),
                Guid.NewGuid(),
                1234,
                134135000000000000,
                new NegotiatedProtocol(1, 0),
                new BridgeVersions("0.1.0", "2.1.0", "2.1.0"),
                new HandshakeLimits(1024, 1024, 1),
                CreateCapabilities()),
            timeout.Token);

        // Act
        Func<Task> handshake = async () =>
            await client.HandshakeAsync(pipeName, targetInstanceId, timeout.Token);

        // Assert
        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(handshake);
        _ = await serverHandshake;
        StringAssert.Contains(exception.Message, "target instance", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task BridgeConnectionRejectsDescriptorAndHandshakeProcessMismatch()
    {
        // Arrange
        Guid targetInstanceId = Guid.NewGuid();
        string pipeName = $"AviUtl2MCP.identity.{Guid.NewGuid():N}";
        BridgeInstanceDescriptor descriptor = new(
            targetInstanceId,
            1234,
            111,
            pipeName,
            "0.1.0-test",
            1);
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await using NamedPipeBridgeTransport transport = new();
        await using BridgeConnection connection = new(
            descriptor,
            transport,
            Guid.NewGuid(),
            "0.1.0-test");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<ClientHello> serverHandshake = RespondAsync(
            server,
            new ServerHello(
                true,
                targetInstanceId,
                Guid.NewGuid(),
                4321,
                222,
                new NegotiatedProtocol(1, 0),
                new BridgeVersions("0.1.0", "2.1.0", "2.1.0"),
                new HandshakeLimits(1024, 1024, 1),
                CreateCapabilities()),
            timeout.Token);

        // Act
        Func<Task> handshake = async () => await connection.HandshakeAsync(timeout.Token);

        // Assert
        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(handshake);
        _ = await serverHandshake;
        StringAssert.Contains(exception.Message, "process identity", StringComparison.Ordinal);
        Assert.IsFalse(connection.IsConnected);
    }

    private static async Task<ClientHello> RespondAsync(
        NamedPipeServerStream server,
        ServerHello response,
        CancellationToken cancellationToken)
    {
        await server.WaitForConnectionAsync(cancellationToken);
        IpcFrame request = await IpcFrameCodec.DecodeFrameAsync(server, cancellationToken);
        Assert.AreEqual(IpcMessageKind.ClientHello, request.Header.MessageKind);
        ClientHello clientHello = ContractJsonSerializer.DeserializeContract<ClientHello>(
            Encoding.UTF8.GetString(request.JsonBytes.Span));
        byte[] responseJson = Encoding.UTF8.GetBytes(ContractJsonSerializer.SerializeContract(response));
        IpcEncodedFrame encoded = IpcFrameCodec.EncodeFrame(
            IpcMessageKind.ServerHello,
            IpcFrameOption.None,
            request.Header.RequestId,
            responseJson,
            []);
        await server.WriteAsync(encoded.Bytes, cancellationToken);
        await server.FlushAsync(cancellationToken);
        return clientHello;
    }

    private static JsonElement CreateCapabilities()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
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

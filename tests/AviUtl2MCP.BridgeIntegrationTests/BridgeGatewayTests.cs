using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Gateways;
using AviUtl2MCP.BridgeClient.Handshake;
using AviUtl2MCP.BridgeClient.Messaging;
using AviUtl2MCP.BridgeClient.Protocol;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class BridgeGatewayTests
{
    private static readonly byte[] expectedPngSignature = [0x89, 0x50, 0x4e, 0x47];
    private static readonly string[] expectedOperations =
        ["project.get", "project.save", "object.delete", "batch.execute", "preview.render", "psd.validate", "logs.get"];

    [TestMethod]
    public async Task FiveDomainGatewaysShareRegistryAndMapTypedResults()
    {
        // Arrange
        string directoryPath = CreateTestDirectory();
        try
        {
            Guid instanceId = Guid.NewGuid();
            long creationTime = GetCurrentProcessCreationTime();
            WriteDescriptor(directoryPath, instanceId, Environment.ProcessId, creationTime);
            InstanceDescriptorWatcher watcher = new(directoryPath);
            RecordingConnectionFactory connectionFactory = new();
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);
            BridgeQueryGateway query = new(registry);
            BridgeEditGateway edit = new(registry);
            BridgePreviewGateway preview = new(registry);
            BridgePsdGateway psd = new(registry);
            BridgeDiagnosticsGateway diagnostics = new(registry);

            // Act
            GatewayResponse<ProjectData> project = await query.GetProjectAsync(
                CreateRequest(instanceId, new GetProjectInput()),
                CancellationToken.None);
            GatewayResponse<SaveProjectData> saved = await edit.SaveProjectAsync(
                new GatewayRequest<SaveProjectArgs>(
                    instanceId,
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow.AddSeconds(5),
                    5000,
                    new Revision("revision-1"),
                    false,
                    new SaveProjectArgs()),
                CancellationToken.None);
            GatewayResponse<DeleteData> deleted = await edit.ExecuteEditAsync<DeleteObjectArgs, DeleteData>(
                "object.delete",
                CreateRequest(instanceId, new DeleteObjectArgs(CreateLocator(instanceId)), isMutation: true),
                CancellationToken.None);
            GatewayResponse<BatchData> partialBatch = await edit.ExecuteBatchAsync(
                CreateRequest(
                    instanceId,
                    new ExecuteBatchInput
                    {
                        ExpectedRevision = new Revision("revision-1"),
                        Operations =
                        [
                            new BatchDeleteObject(
                                "delete",
                                new DeleteObjectArgs(CreateLocator(instanceId))),
                        ],
                    },
                    isMutation: true),
                CancellationToken.None);
            GatewayResponse<PreviewData> rendered = await preview.RenderPreviewAsync(
                CreateRequest(instanceId, new RenderPreviewInput { Frame = 1 }),
                CancellationToken.None);
            GatewayResponse<PsdValidateData> validated = await psd.ValidatePsdAsync(
                CreateRequest(instanceId, new PsdValidateInput()),
                CancellationToken.None);
            GatewayResponse<LogsData> logs = await diagnostics.GetLogsAsync(
                CreateRequest(instanceId, new GetLogsInput()),
                CancellationToken.None);

            // Assert
            Assert.AreEqual(1920, project.Data?.Width);
            Assert.IsTrue(saved.Data?.Saved);
            Assert.IsTrue(deleted.Data?.Deleted);
            Assert.IsFalse(partialBatch.Ok);
            Assert.AreEqual("partial_operation", partialBatch.Error?.Code);
            Assert.IsTrue(partialBatch.Data?.UndoRecommended);
            Assert.AreEqual("image/png", rendered.Data?.MimeType);
            CollectionAssert.AreEqual(expectedPngSignature, rendered.Binary.ToArray());
            Assert.IsNotNull(validated.Data);
            Assert.IsNotNull(logs.Data);
            Assert.AreEqual(1, connectionFactory.CreateCount);
            CollectionAssert.AreEqual(
                expectedOperations,
                connectionFactory.Connection.Requests.Select(request => request.Method).ToArray());
            Assert.IsFalse(connectionFactory.Connection.Requests[1].DryRun);
            Assert.IsTrue(connectionFactory.Connection.Requests[1].ExpectedRevision == new Revision("revision-1"));
            Assert.IsTrue(connectionFactory.Connection.Requests[2].DryRun);
            Assert.IsTrue(connectionFactory.Connection.Requests[2].ExpectedRevision == new Revision("revision-1"));
        }
        finally
        {
            DeleteTestDirectory(directoryPath);
        }
    }

    [TestMethod]
    public async Task EditAndPsdGatewaysRejectCrossDomainOperations()
    {
        // Arrange
        string directoryPath = CreateTestDirectory();
        try
        {
            InstanceDescriptorWatcher watcher = new(directoryPath);
            RecordingConnectionFactory connectionFactory = new();
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);
            BridgeEditGateway edit = new(registry);
            BridgePsdGateway psd = new(registry);
            GatewayRequest<object> request = CreateRequest(Guid.NewGuid(), new object());

            // Act
            Func<Task> editAction = async () => await edit.ExecuteEditAsync<object, DeleteData>(
                "psd.create",
                request,
                CancellationToken.None);
            Func<Task> psdAction = async () => await psd.ExecutePsdAsync<object, PsdSetupData>(
                "object.delete",
                request,
                CancellationToken.None);

            // Assert
            _ = await Assert.ThrowsExactlyAsync<ArgumentException>(editAction);
            _ = await Assert.ThrowsExactlyAsync<ArgumentException>(psdAction);
        }
        finally
        {
            DeleteTestDirectory(directoryPath);
        }
    }

    private static GatewayRequest<T> CreateRequest<T>(Guid instanceId, T parameters, bool isMutation = false)
    {
        return new GatewayRequest<T>(
            instanceId,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddSeconds(5),
            5000,
            isMutation ? new Revision("revision-1") : null,
            isMutation,
            parameters);
    }

    private static ObjectLocator CreateLocator(Guid instanceId)
    {
        return new ObjectLocator(
            instanceId,
            Guid.NewGuid(),
            0,
            1,
            1,
            10,
            "object",
            new string('a', 64),
            new string('b', 64));
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "AviUtl2MCP.GatewayTests",
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

    private static void WriteDescriptor(string directoryPath, Guid instanceId, int processId, long creationTime)
    {
        InstanceDescriptorDocument document = new(
            instanceId,
            processId,
            creationTime,
            $"AviUtl2MCP.v1.{instanceId:D}",
            "0.1.0-test",
            1);
        File.WriteAllText(
            Path.Combine(directoryPath, $"{instanceId:D}.json"),
            ContractJsonSerializer.SerializeContract(document));
    }

    private sealed class RecordingConnectionFactory : IBridgeConnectionFactory
    {
        public RecordingConnection Connection { get; } = new();

        public int CreateCount { get; private set; }

        public ValueTask<IBridgeConnection> CreateConnectionAsync(
            BridgeInstanceDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connection.Initialize(descriptor);
            CreateCount++;
            return ValueTask.FromResult<IBridgeConnection>(Connection);
        }
    }

    private sealed class RecordingConnection : IBridgeConnection
    {
        private BridgeInstanceDescriptor? descriptor;

        public List<BridgeRequest> Requests { get; } = [];

        public BridgeInstanceDescriptor Descriptor => descriptor
            ?? throw new InvalidOperationException("Connection was not initialized.");

        public BridgeSessionInfo SessionInfo => throw new NotSupportedException();

        public bool IsConnected { get; private set; }

        public void Initialize(BridgeInstanceDescriptor value)
        {
            descriptor = value;
            IsConnected = true;
        }

        public ValueTask<BridgeResponse> SendAsync(
            BridgeRequest request,
            ReadOnlyMemory<byte> binary,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.Method == "batch.execute")
            {
                BatchData partialData = new(
                    [
                        new BatchResult(
                            "delete",
                            BatchOperationKind.DeleteObject,
                            BatchResultStatus.Failed,
                            []),
                    ],
                    [],
                    true);
                BridgeResponseError error = new(
                    "partial_operation",
                    "partial",
                    false,
                    "sdk",
                    "partial",
                    true,
                    SerializeElement(new { }));
                BridgeResponseEnvelope failedEnvelope = new(
                    false,
                    request.CorrelationId,
                    Descriptor.InstanceId,
                    new Revision("revision-2"),
                    null,
                    SerializeElement(partialData),
                    null,
                    error);
                byte[] failedJson = Encoding.UTF8.GetBytes(
                    ContractJsonSerializer.SerializeContract(failedEnvelope));
                IpcFrameHeader failedHeader = new(
                    IpcMessageKind.Response,
                    IpcFrameOption.ErrorResponse,
                    request.CorrelationId,
                    (uint)failedJson.Length,
                    0);
                byte[] failedBinary = [];
                IpcFrame failedFrame = new(
                    failedHeader,
                    failedJson,
                    failedBinary,
                    IpcFrameCodec.CalculatePayloadHash(failedHeader, failedJson, failedBinary));
                return ValueTask.FromResult(new BridgeResponse(failedEnvelope, failedFrame));
            }
            object data = request.Method switch
            {
                "project.get" => new ProjectData(
                    null,
                    false,
                    1920,
                    1080,
                    30,
                    48000,
                    0,
                    1,
                    [],
                    null,
                    [],
                    new CoordinateSystem(1, 1, true)),
                "project.save" => new SaveProjectData(@"C:\fixture\project.aup2", true),
                "object.delete" => new DeleteData { Deleted = true, AppliedChanges = [] },
                "preview.render" => new PreviewData("image/png", 16, 16, 1, new string('a', 64), 4),
                "psd.validate" => new PsdValidateData([], null),
                "logs.get" => new LogsData([], null, false),
                _ => throw new InvalidOperationException("Unexpected test operation."),
            };
            JsonElement result = SerializeElement(data);
            byte[] responseBinary = request.Method == "preview.render"
                ? [0x89, 0x50, 0x4e, 0x47]
                : [];
            IpcFrameOption options = responseBinary.Length == 0
                ? IpcFrameOption.None
                : IpcFrameOption.HasBinary;
            BridgeResponseEnvelope envelope = new(
                true,
                request.CorrelationId,
                Descriptor.InstanceId,
                null,
                null,
                result,
                [],
                null);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(ContractJsonSerializer.SerializeContract(envelope));
            IpcFrameHeader header = new(
                IpcMessageKind.Response,
                options,
                request.CorrelationId,
                (uint)jsonBytes.Length,
                (ulong)responseBinary.Length);
            IpcFrame frame = new(
                header,
                jsonBytes,
                responseBinary,
                IpcFrameCodec.CalculatePayloadHash(header, jsonBytes, responseBinary));
            return ValueTask.FromResult(new BridgeResponse(envelope, frame));
        }

        public ValueTask CancelAsync(Guid requestId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        private static JsonElement SerializeElement(object value)
        {
            using JsonDocument document = JsonDocument.Parse(
                ContractJsonSerializer.SerializeContract(value, value.GetType()));
            return document.RootElement.Clone();
        }
    }
}

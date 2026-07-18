using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Handshake;
using AviUtl2MCP.BridgeClient.Messaging;
using System.Text.Json;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class InstanceDescriptorWatcherTests
{
    [TestMethod]
    public void ReadDescriptorsKeepsOnlyUniqueLiveProcessIdentities()
    {
        // Arrange
        string directoryPath = CreateTestDirectory();
        try
        {
            FakeProcessIdentityProbe processProbe = new();
            Guid validId = Guid.NewGuid();
            processProbe.AddProcess(1001, 101);
            WriteDescriptor(directoryPath, validId, 1001, 101);

            Guid staleId = Guid.NewGuid();
            WriteDescriptor(directoryPath, staleId, 1002, 102);

            Guid reusedPidId = Guid.NewGuid();
            processProbe.AddProcess(1003, 999);
            WriteDescriptor(directoryPath, reusedPidId, 1003, 103);

            Guid duplicateId = Guid.NewGuid();
            processProbe.AddProcess(1004, 104);
            WriteDescriptor(directoryPath, duplicateId, 1004, 104, "D");
            WriteDescriptor(directoryPath, duplicateId, 1004, 104, "N");

            File.WriteAllText(Path.Combine(directoryPath, $"{Guid.NewGuid():D}.json"), "{invalid");
            File.WriteAllBytes(Path.Combine(directoryPath, $"{Guid.NewGuid():D}.json"), [0xff]);
            InstanceDescriptorWatcher watcher = new(directoryPath, processProbe);

            // Act
            DescriptorSnapshot snapshot = watcher.ReadDescriptors();

            // Assert
            Assert.HasCount(1, snapshot.Instances);
            Assert.AreEqual(validId, snapshot.Instances[0].InstanceId);
            Assert.AreEqual(101, snapshot.Instances[0].ProcessCreationTime);
            CollectionAssert.IsSubsetOf(
                new[]
                {
                    DescriptorIssueCode.ProcessUnavailable,
                    DescriptorIssueCode.ProcessIdentityMismatch,
                    DescriptorIssueCode.DuplicateInstance,
                    DescriptorIssueCode.Malformed,
                },
                snapshot.Issues.Select(issue => issue.Code).ToArray());
        }
        finally
        {
            DeleteTestDirectory(directoryPath);
        }
    }

    [TestMethod]
    public async Task WatchAsyncYieldsWhenDescriptorSetChanges()
    {
        // Arrange
        string directoryPath = CreateTestDirectory();
        try
        {
            FakeProcessIdentityProbe processProbe = new();
            InstanceDescriptorWatcher watcher = new(
                directoryPath,
                processProbe,
                TimeSpan.FromMilliseconds(10));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            await using IAsyncEnumerator<DescriptorSnapshot> snapshots =
                watcher.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.IsTrue(await snapshots.MoveNextAsync());
            Assert.IsEmpty(snapshots.Current.Instances);

            Guid instanceId = Guid.NewGuid();
            processProbe.AddProcess(2001, 201);
            WriteDescriptor(directoryPath, instanceId, 2001, 201);

            // Act
            bool hasChangedSnapshot = await snapshots.MoveNextAsync();

            // Assert
            Assert.IsTrue(hasChangedSnapshot);
            Assert.HasCount(1, snapshots.Current.Instances);
            Assert.AreEqual(instanceId, snapshots.Current.Instances[0].InstanceId);
        }
        finally
        {
            DeleteTestDirectory(directoryPath);
        }
    }

    [TestMethod]
    public void ReadDescriptorsRejectsWrongProtocolAndPipeName()
    {
        // Arrange
        string directoryPath = CreateTestDirectory();
        try
        {
            FakeProcessIdentityProbe processProbe = new();
            Guid incompatibleId = Guid.NewGuid();
            processProbe.AddProcess(3001, 301);
            WriteDescriptor(directoryPath, incompatibleId, 3001, 301, protocolMajor: 2);

            Guid wrongPipeId = Guid.NewGuid();
            processProbe.AddProcess(3002, 302);
            WriteDescriptor(directoryPath, wrongPipeId, 3002, 302, pipeName: "UnexpectedPipe");
            InstanceDescriptorWatcher watcher = new(directoryPath, processProbe);

            // Act
            DescriptorSnapshot snapshot = watcher.ReadDescriptors();

            // Assert
            Assert.IsEmpty(snapshot.Instances);
            Assert.IsTrue(snapshot.Issues.Any(issue => issue.Code == DescriptorIssueCode.ProtocolIncompatible));
            Assert.IsTrue(snapshot.Issues.Any(issue => issue.Code == DescriptorIssueCode.Malformed));
        }
        finally
        {
            DeleteTestDirectory(directoryPath);
        }
    }

    [TestMethod]
    public async Task RegistryReplacesConnectionsWhenDescriptorIdentityChanges()
    {
        // Arrange
        string directoryPath = CreateTestDirectory();
        try
        {
            FakeProcessIdentityProbe processProbe = new();
            Guid instanceId = Guid.NewGuid();
            processProbe.AddProcess(4001, 401);
            WriteDescriptor(directoryPath, instanceId, 4001, 401);
            InstanceDescriptorWatcher watcher = new(directoryPath, processProbe);
            FakeBridgeConnectionFactory connectionFactory = new();
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);

            // Act
            DescriptorSnapshot initial = await registry.DiscoverAsync(CancellationToken.None);
            IBridgeConnection first = await registry.GetConnectionAsync(instanceId, CancellationToken.None);
            IBridgeConnection reused = await registry.GetConnectionAsync(instanceId, CancellationToken.None);
            processProbe.AddProcess(4002, 402);
            WriteDescriptor(directoryPath, instanceId, 4002, 402);
            _ = await registry.DiscoverAsync(CancellationToken.None);
            IBridgeConnection replacement = await registry.GetConnectionAsync(instanceId, CancellationToken.None);
            File.Delete(Path.Combine(directoryPath, $"{instanceId:D}.json"));
            _ = await registry.DiscoverAsync(CancellationToken.None);

            // Assert
            Assert.HasCount(1, initial.Instances);
            Assert.IsEmpty(registry.GetDiscoveryIssues());
            Assert.AreSame(first, reused);
            Assert.AreNotSame(first, replacement);
            Assert.IsFalse(first.IsConnected);
            Assert.IsFalse(replacement.IsConnected);
            Assert.IsEmpty(registry.GetCandidates());
            Assert.AreEqual(2, connectionFactory.CreateCount);
        }
        finally
        {
            DeleteTestDirectory(directoryPath);
        }
    }

    private static string CreateTestDirectory()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            "AviUtl2MCP.BridgeIntegrationTests",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static void DeleteTestDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static void WriteDescriptor(
        string directoryPath,
        Guid instanceId,
        int processId,
        long processCreationTime,
        string fileNameFormat = "D",
        ushort protocolMajor = 1,
        string? pipeName = null)
    {
        InstanceDescriptorDocument document = new(
            instanceId,
            processId,
            processCreationTime,
            pipeName ?? $"AviUtl2MCP.v1.{instanceId:D}",
            "0.1.0-test",
            protocolMajor);
        File.WriteAllText(
            Path.Combine(directoryPath, $"{instanceId.ToString(fileNameFormat)}.json"),
            ContractJsonSerializer.SerializeContract(document));
    }

    private sealed class FakeProcessIdentityProbe : IProcessIdentityProbe
    {
        private readonly Dictionary<int, long> creationTimes = [];

        public void AddProcess(int processId, long creationTime)
        {
            creationTimes.Add(processId, creationTime);
        }

        public ProcessIdentityProbeResult ProbeProcess(int processId)
        {
            return creationTimes.TryGetValue(processId, out long creationTime)
                ? new ProcessIdentityProbeResult(true, creationTime, ProcessProbeFailure.None)
                : new ProcessIdentityProbeResult(false, 0, ProcessProbeFailure.NotRunning);
        }
    }

    private sealed class FakeBridgeConnectionFactory : IBridgeConnectionFactory
    {
        public int CreateCount { get; private set; }

        public ValueTask<IBridgeConnection> CreateConnectionAsync(
            BridgeInstanceDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            return ValueTask.FromResult<IBridgeConnection>(new FakeBridgeConnection(descriptor));
        }
    }

    private sealed class FakeBridgeConnection : IBridgeConnection
    {
        public FakeBridgeConnection(BridgeInstanceDescriptor descriptor)
        {
            Descriptor = descriptor;
            using JsonDocument document = JsonDocument.Parse("{}");
            SessionInfo = new BridgeSessionInfo(
                descriptor.InstanceId,
                Guid.NewGuid(),
                descriptor.ProcessId,
                descriptor.ProcessCreationTime,
                new NegotiatedProtocol(1, 0),
                new BridgeVersions("0.1.0", "2.1.0", "2.1.0"),
                new HandshakeLimits(1024, 1024, 1),
                document.RootElement.Clone());
            IsConnected = true;
        }

        public BridgeInstanceDescriptor Descriptor { get; }

        public BridgeSessionInfo SessionInfo { get; }

        public bool IsConnected { get; private set; }

        public ValueTask<BridgeResponse> SendAsync(
            BridgeRequest request,
            ReadOnlyMemory<byte> binary,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask CancelAsync(Guid requestId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}

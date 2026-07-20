using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Gateways;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class BridgeLogSourceTests
{
    private static readonly LogSource[] EXPECTED_BRIDGE_SOURCES = [LogSource.Bridge];

    [TestMethod]
    public async Task ReadAsyncMapsQueryAndReturnsNativeCursorGeneration()
    {
        // Arrange
        Guid instanceId = Guid.NewGuid();
        Guid requestCorrelationId = Guid.CreateVersion7();
        Guid targetCorrelationId = Guid.CreateVersion7();
        Guid serverEpoch = Guid.NewGuid();
        RecordingDiagnosticsGateway gateway = new(new GatewayResponse<LogsData>(
            true,
            requestCorrelationId,
            instanceId,
            new Revision($"{serverEpoch:D}:{Guid.NewGuid():D}:2"),
            null,
            new LogsData(
                [new LogEntry(DateTimeOffset.UtcNow, "Warning", "bridge", "fixture", targetCorrelationId.ToString("D"), "message")],
                "bridge:42",
                true),
            [],
            null,
            ReadOnlyMemory<byte>.Empty));
        BridgeLogSource source = new(gateway);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        LogSourceQuery query = new(
            [ContractLogLevel.Warning],
            DateTimeOffset.UtcNow.AddMinutes(-1),
            targetCorrelationId,
            25,
            "bridge:10",
            instanceId,
            requestCorrelationId,
            deadline,
            30_000);

        // Act
        LogSourcePage page = await source.ReadAsync(query, CancellationToken.None);

        // Assert
        Assert.AreEqual(LogSource.Bridge, source.Source);
        Assert.HasCount(1, page.Entries);
        Assert.AreEqual("bridge:42", page.NextCursor);
        Assert.IsTrue(page.IsTruncated);
        Assert.AreEqual(serverEpoch.ToString("D"), page.Generation);
        GatewayRequest<GetLogsInput> request = gateway.Request!;
        Assert.AreEqual(instanceId, request.InstanceId);
        Assert.AreEqual(requestCorrelationId, request.CorrelationId);
        Assert.AreEqual(deadline, request.Deadline);
        Assert.AreEqual(25, request.Parameters.Limit);
        Assert.AreEqual("bridge:10", request.Parameters.Cursor);
        CollectionAssert.AreEqual(
            EXPECTED_BRIDGE_SOURCES,
            request.Parameters.Sources!.ToArray());
    }

    [TestMethod]
    public async Task ReadAsyncRejectsMissingInstanceBeforeGatewayCall()
    {
        // Arrange
        RecordingDiagnosticsGateway gateway = new(CreateUnusedResponse());
        BridgeLogSource source = new(gateway);
        LogSourceQuery query = new(
            null,
            null,
            null,
            10,
            null,
            null,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddSeconds(30),
            30_000);

        // Act
        LogSourceReadException exception = await Assert.ThrowsAsync<LogSourceReadException>(async () =>
            await source.ReadAsync(query, CancellationToken.None));

        // Assert
        Assert.AreEqual("aviutl_not_running", exception.Code);
        Assert.IsTrue(exception.CanRetry);
        Assert.IsNull(gateway.Request);
    }

    [TestMethod]
    public async Task ReadAsyncMapsBridgeFailureToSourceFailure()
    {
        // Arrange
        Guid instanceId = Guid.NewGuid();
        using JsonDocument detailsDocument = JsonDocument.Parse("{}");
        JsonElement details = detailsDocument.RootElement.Clone();
        RecordingDiagnosticsGateway gateway = new(new GatewayResponse<LogsData>(
            false,
            Guid.CreateVersion7(),
            instanceId,
            null,
            null,
            null,
            [],
            new GatewayError("bridge_busy", "Bridge is busy.", true, "preflight", "unchanged", false, details),
            ReadOnlyMemory<byte>.Empty));
        BridgeLogSource source = new(gateway);
        LogSourceQuery query = new(
            null,
            null,
            null,
            10,
            null,
            instanceId,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddSeconds(30),
            30_000);

        // Act
        LogSourceReadException exception = await Assert.ThrowsAsync<LogSourceReadException>(async () =>
            await source.ReadAsync(query, CancellationToken.None));

        // Assert
        Assert.AreEqual("bridge_busy", exception.Code);
        Assert.IsTrue(exception.CanRetry);
    }

    [TestMethod]
    public async Task ReadAsyncMapsStaleDescriptorToNotRunning()
    {
        // Arrange
        BridgeLogSource source = new(new ThrowingDiagnosticsGateway());
        LogSourceQuery query = new(
            null,
            null,
            null,
            10,
            null,
            Guid.NewGuid(),
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddSeconds(30),
            30_000);

        // Act
        LogSourceReadException exception = await Assert.ThrowsAsync<LogSourceReadException>(async () =>
            await source.ReadAsync(query, CancellationToken.None));

        // Assert
        Assert.AreEqual("aviutl_not_running", exception.Code);
        Assert.IsTrue(exception.CanRetry);
    }

    private static GatewayResponse<LogsData> CreateUnusedResponse() =>
        new(
            true,
            Guid.CreateVersion7(),
            Guid.NewGuid(),
            new Revision("unused"),
            null,
            new LogsData([], null, false),
            [],
            null,
            ReadOnlyMemory<byte>.Empty);

    private sealed class RecordingDiagnosticsGateway(GatewayResponse<LogsData> response)
        : IBridgeDiagnosticsGateway
    {
        public GatewayRequest<GetLogsInput>? Request { get; private set; }

        public ValueTask<GatewayResponse<LogsData>> GetLogsAsync(
            GatewayRequest<GetLogsInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(response);
        }

        public ValueTask<GatewayResponse<StatusData>> GetStatusAsync(
            GatewayRequest<GetStatusInput> request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GatewayResponse<CapabilitiesData>> GetCapabilitiesAsync(
            GatewayRequest<GetCapabilitiesInput> request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GatewayResponse<DiagnoseData>> DiagnoseAsync(
            GatewayRequest<DiagnoseInput> request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingDiagnosticsGateway : IBridgeDiagnosticsGateway
    {
        public ValueTask<GatewayResponse<LogsData>> GetLogsAsync(
            GatewayRequest<GetLogsInput> request,
            CancellationToken cancellationToken) =>
            throw new KeyNotFoundException("fixture");

        public ValueTask<GatewayResponse<StatusData>> GetStatusAsync(
            GatewayRequest<GetStatusInput> request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GatewayResponse<CapabilitiesData>> GetCapabilitiesAsync(
            GatewayRequest<GetCapabilitiesInput> request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GatewayResponse<DiagnoseData>> DiagnoseAsync(
            GatewayRequest<DiagnoseInput> request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

using System.Security.Cryptography;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Paging;
using AviUtl2MCP.Application.Previews;
using AviUtl2MCP.Application.Queries;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class AviUtlDiagnosticSmokeProbeTests
{
    private static readonly byte[] VALID_RGB_PNG = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAAC0lEQVR42mP8/x8AAusB9Y9Z5wAAAABJRU5ErkJggg==");

    [TestMethod]
    public async Task SmokeProbeRecordsCorrelationRevisionAndPreviewHash()
    {
        // Arrange
        InstanceDescriptor instance = CreateDescriptor();
        Revision revision = new("epoch:generation:11");
        FakeInstanceResolver resolver = new(instance);
        FakeQueryGateway queryGateway = new(revision);
        AviUtlQueryService queryService = new(
            resolver,
            queryGateway,
            new UnusedDiagnosticsGateway(),
            new PagingCursorCodec(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray()),
            Guid.Parse("019f0000-0000-7000-8000-000000000100"));
        string sha256 = Convert.ToHexString(SHA256.HashData(VALID_RGB_PNG)).ToLowerInvariant();
        FakePreviewGateway previewGateway = new(revision, sha256);
        AviUtlDiagnosticSmokeProbe probe = new(
            queryService,
            new AviUtlPreviewService(resolver, previewGateway));
        Guid readCorrelationId = Guid.Parse("019f0000-0000-7000-8000-000000000101");
        Guid previewCorrelationId = Guid.Parse("019f0000-0000-7000-8000-000000000102");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        // Act
        DiagnosticSmokeResult read = await probe.RunReadSmokeAsync(
            new DiagnosticRunContext(
                Guid.Parse("019f0000-0000-7000-8000-000000000103"),
                instance,
                readCorrelationId,
                deadline,
                2_000),
            CancellationToken.None);
        DiagnosticSmokeResult preview = await probe.RunPreviewSmokeAsync(
            new DiagnosticRunContext(
                Guid.Parse("019f0000-0000-7000-8000-000000000103"),
                instance,
                previewCorrelationId,
                deadline,
                2_000),
            CancellationToken.None);

        // Assert
        Assert.IsTrue(read.Succeeded);
        CollectionAssert.Contains(read.Evidence.ToArray(), $"correlationId={readCorrelationId:D}");
        CollectionAssert.Contains(read.Evidence.ToArray(), $"revision={revision.Value}");
        CollectionAssert.Contains(read.Evidence.ToArray(), "dimensions=1920x1080");
        Assert.IsTrue(preview.Succeeded);
        CollectionAssert.Contains(preview.Evidence.ToArray(), $"correlationId={previewCorrelationId:D}");
        CollectionAssert.Contains(preview.Evidence.ToArray(), $"revision={revision.Value}");
        CollectionAssert.Contains(preview.Evidence.ToArray(), $"previewSha256={sha256}");
        Assert.AreEqual(readCorrelationId, queryGateway.LastRequest!.CorrelationId);
        Assert.IsTrue(queryGateway.LastRequest.Parameters.IncludeScenes);
        Assert.AreEqual(previewCorrelationId, previewGateway.LastRequest!.CorrelationId);
        Assert.AreEqual(1, previewGateway.LastRequest.Parameters.Frame);
    }

    private static InstanceDescriptor CreateDescriptor() => new(
        Guid.Parse("019f0000-0000-7000-8000-000000000001"),
        1234,
        new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
        "0.1.0",
        true);

    private sealed class FakeInstanceResolver(InstanceDescriptor instance) : IInstanceResolver
    {
        public ValueTask<ApplicationResult<InstanceDescriptor>> ResolveAsync(
            Guid? requestedInstanceId,
            IReadOnlyList<ObjectLocator> locators,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ApplicationResult.Success(instance));
        }

        public ValueTask<IReadOnlyList<InstanceDescriptor>> ListCandidatesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<InstanceDescriptor>>([instance]);
        }
    }

    private sealed class FakeQueryGateway(Revision revision) : IAviUtlQueryGateway
    {
        public GatewayRequest<GetProjectInput>? LastRequest { get; private set; }

        public ValueTask<GatewayResponse<ProjectData>> GetProjectAsync(
            GatewayRequest<GetProjectInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            ProjectData project = new(
                "D:\\project.aup2",
                true,
                1920,
                1080,
                30,
                48_000,
                0,
                10,
                [],
                null,
                [new SceneSummary(0, "Scene 1")],
                new CoordinateSystem(1, 1, true));
            return ValueTask.FromResult(new GatewayResponse<ProjectData>(
                true,
                request.CorrelationId,
                request.InstanceId,
                revision,
                revision,
                project,
                [],
                null,
                ReadOnlyMemory<byte>.Empty));
        }

        public ValueTask<GatewayResponse<TimelineData>> GetTimelineAsync(
            GatewayRequest<GetTimelineInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<ObjectsPageData>> FindObjectsAsync(
            GatewayRequest<FindObjectsInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<ObjectData>> GetObjectAsync(
            GatewayRequest<GetObjectInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<EffectsData>> ListEffectsAsync(
            GatewayRequest<ListEffectsInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<EffectItemsData>> ListEffectItemsAsync(
            GatewayRequest<ListEffectItemsInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePreviewGateway(Revision revision, string sha256) : IAviUtlPreviewGateway
    {
        public GatewayRequest<RenderPreviewInput>? LastRequest { get; private set; }

        public ValueTask<GatewayResponse<PreviewData>> RenderPreviewAsync(
            GatewayRequest<RenderPreviewInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(new GatewayResponse<PreviewData>(
                true,
                request.CorrelationId,
                request.InstanceId,
                revision,
                revision,
                new PreviewData("image/png", 1, 1, request.Parameters.Frame, sha256, VALID_RGB_PNG.Length),
                [],
                null,
                VALID_RGB_PNG));
        }
    }

    private sealed class UnusedDiagnosticsGateway : IBridgeDiagnosticsGateway
    {
        public ValueTask<GatewayResponse<StatusData>> GetStatusAsync(
            GatewayRequest<GetStatusInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<CapabilitiesData>> GetCapabilitiesAsync(
            GatewayRequest<GetCapabilitiesInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<LogsData>> GetLogsAsync(
            GatewayRequest<GetLogsInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<DiagnoseData>> DiagnoseAsync(
            GatewayRequest<DiagnoseInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

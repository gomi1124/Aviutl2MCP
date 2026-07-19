using System.Security.Cryptography;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Previews;
using AviUtl2MCP.Application.Requests;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class AviUtlPreviewServiceTests
{
    private static readonly byte[] VALID_RGBA_PNG = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+Xw3pWQAAAABJRU5ErkJggg==");

    [TestMethod]
    public async Task RenderPreviewReturnsVerifiedPngBinary()
    {
        // Arrange
        InstanceDescriptor instance = CreateDescriptor();
        Revision revision = new("epoch:generation:7");
        FakePreviewGateway gateway = new()
        {
            Handler = request => CreateSuccess(
                request,
                revision,
                VALID_RGBA_PNG,
                sha256: CalculateSha256(VALID_RGBA_PNG)),
        };
        AviUtlPreviewService service = new(new FakeInstanceResolver(instance), gateway);
        using RequestContext context = CreateContext(instance.InstanceId);
        RenderPreviewInput input = new()
        {
            InstanceId = instance.InstanceId,
            TimeoutMs = context.TimeoutMs,
            Frame = 1,
            MaxWidth = 640,
            MaxHeight = 360,
            IncludeAlpha = true,
        };

        // Act
        PreviewExecutionResult execution = await service.RenderPreviewAsync(input, context);

        // Assert
        Assert.IsTrue(execution.Result.IsSuccess);
        Assert.AreEqual(instance.InstanceId, execution.InstanceId);
        Assert.AreEqual(revision, execution.Revision);
        CollectionAssert.AreEqual(VALID_RGBA_PNG, execution.PngBytes.ToArray());
        Assert.AreEqual(context.CorrelationId, gateway.LastRequest!.CorrelationId);
        Assert.AreEqual(instance.InstanceId, gateway.LastRequest.InstanceId);
    }

    [TestMethod]
    public async Task RenderPreviewRejectsHashMismatchWithoutExposingBinary()
    {
        // Arrange
        InstanceDescriptor instance = CreateDescriptor();
        FakePreviewGateway gateway = new()
        {
            Handler = request => CreateSuccess(
                request,
                new Revision("epoch:generation:8"),
                VALID_RGBA_PNG,
                sha256: new string('0', 64)),
        };
        AviUtlPreviewService service = new(new FakeInstanceResolver(instance), gateway);
        using RequestContext context = CreateContext(instance.InstanceId);
        RenderPreviewInput input = new()
        {
            InstanceId = instance.InstanceId,
            TimeoutMs = context.TimeoutMs,
            Frame = 1,
            IncludeAlpha = true,
        };

        // Act
        PreviewExecutionResult execution = await service.RenderPreviewAsync(input, context);

        // Assert
        Assert.IsFalse(execution.Result.IsSuccess);
        Assert.AreEqual("preview_invalid_png", execution.Result.Error!.Code);
        Assert.IsTrue(execution.PngBytes.IsEmpty);
    }

    [TestMethod]
    public async Task RenderPreviewRejectsAlphaContractMismatch()
    {
        // Arrange
        InstanceDescriptor instance = CreateDescriptor();
        FakePreviewGateway gateway = new()
        {
            Handler = request => CreateSuccess(
                request,
                new Revision("epoch:generation:9"),
                VALID_RGBA_PNG,
                CalculateSha256(VALID_RGBA_PNG)),
        };
        AviUtlPreviewService service = new(new FakeInstanceResolver(instance), gateway);
        using RequestContext context = CreateContext(instance.InstanceId);
        RenderPreviewInput input = new()
        {
            InstanceId = instance.InstanceId,
            TimeoutMs = context.TimeoutMs,
            Frame = 1,
            IncludeAlpha = false,
        };

        // Act
        PreviewExecutionResult execution = await service.RenderPreviewAsync(input, context);

        // Assert
        Assert.IsFalse(execution.Result.IsSuccess);
        Assert.AreEqual("preview_invalid_png", execution.Result.Error!.Code);
        Assert.IsTrue(execution.PngBytes.IsEmpty);
    }

    [TestMethod]
    public async Task RenderPreviewMapsGatewayTimeout()
    {
        // Arrange
        InstanceDescriptor instance = CreateDescriptor();
        FakePreviewGateway gateway = new()
        {
            Handler = _ => throw new TimeoutException("preview timeout"),
        };
        AviUtlPreviewService service = new(new FakeInstanceResolver(instance), gateway);
        using RequestContext context = CreateContext(instance.InstanceId);
        RenderPreviewInput input = new()
        {
            InstanceId = instance.InstanceId,
            TimeoutMs = context.TimeoutMs,
            Frame = 1,
        };

        // Act
        PreviewExecutionResult execution = await service.RenderPreviewAsync(input, context);

        // Assert
        Assert.IsFalse(execution.Result.IsSuccess);
        Assert.AreEqual("operation_timeout", execution.Result.Error!.Code);
        Assert.IsTrue(execution.Result.Error.CanRetry);
        Assert.IsTrue(execution.PngBytes.IsEmpty);
    }

    private static GatewayResponse<PreviewData> CreateSuccess(
        GatewayRequest<RenderPreviewInput> request,
        Revision revision,
        byte[] png,
        string sha256) => new(
            true,
            request.CorrelationId,
            request.InstanceId,
            revision,
            revision,
            new PreviewData("image/png", 1, 1, request.Parameters.Frame, sha256, png.Length),
            [],
            null,
            png);

    private static string CalculateSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static RequestContext CreateContext(Guid instanceId) =>
        new RequestContextFactory().CreateContext(
            instanceId,
            timeoutMs: 2_000,
            defaultTimeoutMs: 2_000,
            CancellationToken.None);

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

    private sealed class FakePreviewGateway : IAviUtlPreviewGateway
    {
        public required Func<GatewayRequest<RenderPreviewInput>, GatewayResponse<PreviewData>> Handler { get; init; }

        public GatewayRequest<RenderPreviewInput>? LastRequest { get; private set; }

        public ValueTask<GatewayResponse<PreviewData>> RenderPreviewAsync(
            GatewayRequest<RenderPreviewInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(Handler(request));
        }
    }
}

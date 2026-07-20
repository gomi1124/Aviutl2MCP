using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Paging;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class AviUtlQueryServiceTests
{
    [TestMethod]
    public async Task GetStatusAsyncReturnsDisconnectedStateWithoutAviUtl()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        FakeInstanceResolver resolver = new([]);
        AviUtlQueryService service = CreateService(now, resolver, new FakeQueryGateway(), new FakeDiagnosticsGateway());
        using RequestContext context = CreateContext(now);

        // Act
        QueryExecutionResult<StatusData> result = await service.GetStatusAsync(
            new GetStatusInput(),
            context);

        // Assert
        Assert.IsTrue(result.Result.IsSuccess);
        Assert.AreEqual(ConnectionState.Disconnected, result.Result.Value!.ConnectionState);
        Assert.IsNull(result.Result.Value.SelectedInstance);
        Assert.HasCount(0, result.Result.Value.Instances);
    }

    [TestMethod]
    public async Task GetObjectAsyncSelectsLocatorInstanceAndMapsMetadata()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        InstanceDescriptor instance = CreateDescriptor();
        FakeInstanceResolver resolver = new([instance]);
        ObjectLocator locator = CreateLocator(instance.InstanceId);
        Revision revision = CreateRevision();
        FakeQueryGateway gateway = new()
        {
            GetObjectHandler = request => CreateSuccess(
                request,
                new ObjectData(CreateObject(locator), []),
                revision),
        };
        AviUtlQueryService service = CreateService(now, resolver, gateway, new FakeDiagnosticsGateway());
        using RequestContext context = CreateContext(now);

        // Act
        QueryExecutionResult<ObjectData> result = await service.GetObjectAsync(
            new GetObjectInput { Locator = locator },
            context);

        // Assert
        Assert.IsTrue(result.Result.IsSuccess);
        Assert.AreEqual(instance.InstanceId, result.InstanceId);
        Assert.AreEqual(revision, result.Revision);
        Assert.AreEqual(instance.InstanceId, resolver.LastLocators.Single().InstanceId);
        Assert.AreEqual(instance.InstanceId, gateway.LastObjectRequest!.InstanceId);
    }

    [TestMethod]
    public async Task GetTimelineAsyncSignsAndResumesBridgeCursor()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        InstanceDescriptor instance = CreateDescriptor();
        Revision revision = CreateRevision();
        FakeInstanceResolver resolver = new([instance]);
        FakeQueryGateway gateway = new();
        int requestCount = 0;
        gateway.GetTimelineHandler = request =>
        {
            requestCount++;
            string? nextCursor = requestCount == 1 ? "timeline:1" : null;
            if (requestCount == 2)
            {
                Assert.AreEqual("timeline:1", request.Parameters.Cursor);
            }
            return CreateSuccess(
                request,
                new TimelineData([], [], nextCursor, nextCursor is not null, new CoordinateSystem(1, 1, true)),
                revision);
        };
        FakeDiagnosticsGateway diagnostics = new()
        {
            GetStatusHandler = request => CreateSuccess(request, CreateReadyStatus(instance), revision),
        };
        AviUtlQueryService service = CreateService(now, resolver, gateway, diagnostics);
        using RequestContext firstContext = CreateContext(now);
        GetTimelineInput firstInput = new() { Limit = 1 };

        // Act
        QueryExecutionResult<TimelineData> first = await service.GetTimelineAsync(firstInput, firstContext);
        using RequestContext secondContext = CreateContext(now);
        QueryExecutionResult<TimelineData> second = await service.GetTimelineAsync(
            firstInput with { Cursor = first.Result.Value!.NextCursor },
            secondContext);

        // Assert
        Assert.IsTrue(first.Result.IsSuccess);
        Assert.IsNotNull(first.Result.Value!.NextCursor);
        Assert.AreNotEqual("timeline:1", first.Result.Value.NextCursor);
        Assert.Contains('.', first.Result.Value.NextCursor);
        Assert.IsTrue(second.Result.IsSuccess);
        Assert.IsNull(second.Result.Value!.NextCursor);
        Assert.AreEqual(2, requestCount);
    }

    [TestMethod]
    public async Task GetTimelineAsyncRejectsCursorForChangedQuery()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        InstanceDescriptor instance = CreateDescriptor();
        Revision revision = CreateRevision();
        FakeInstanceResolver resolver = new([instance]);
        FakeQueryGateway gateway = new()
        {
            GetTimelineHandler = request => CreateSuccess(
                request,
                new TimelineData([], [], "timeline:1", true, new CoordinateSystem(1, 1, true)),
                revision),
        };
        FakeDiagnosticsGateway diagnostics = new()
        {
            GetStatusHandler = request => CreateSuccess(request, CreateReadyStatus(instance), revision),
        };
        AviUtlQueryService service = CreateService(now, resolver, gateway, diagnostics);
        using RequestContext firstContext = CreateContext(now);
        QueryExecutionResult<TimelineData> first = await service.GetTimelineAsync(
            new GetTimelineInput { Limit = 1 },
            firstContext);

        // Act
        using RequestContext secondContext = CreateContext(now);
        QueryExecutionResult<TimelineData> second = await service.GetTimelineAsync(
            new GetTimelineInput { Limit = 2, Cursor = first.Result.Value!.NextCursor },
            secondContext);

        // Assert
        Assert.IsFalse(second.Result.IsSuccess);
        Assert.AreEqual("cursor_invalid", second.Result.Error!.Code);
        Assert.AreEqual(1, gateway.TimelineRequestCount);
    }

    [TestMethod]
    public async Task GetCapabilitiesAsyncPreservesBridgeErrorDetails()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        InstanceDescriptor instance = CreateDescriptor();
        using JsonDocument details = JsonDocument.Parse("""{"component":"sdk"}""");
        FakeDiagnosticsGateway diagnostics = new()
        {
            GetCapabilitiesHandler = request => new GatewayResponse<CapabilitiesData>(
                false,
                request.CorrelationId,
                request.InstanceId,
                CreateRevision(),
                CreateRevision(),
                null,
                [],
                new GatewayError(
                    "sdk_not_available",
                    "SDK unavailable",
                    true,
                    "preflight",
                    "unchanged",
                    false,
                    details.RootElement.Clone()),
                ReadOnlyMemory<byte>.Empty),
        };
        AviUtlQueryService service = CreateService(
            now,
            new FakeInstanceResolver([instance]),
            new FakeQueryGateway(),
            diagnostics);
        using RequestContext context = CreateContext(now);

        // Act
        QueryExecutionResult<CapabilitiesData> result = await service.GetCapabilitiesAsync(
            new GetCapabilitiesInput(),
            context);

        // Assert
        Assert.IsFalse(result.Result.IsSuccess);
        Assert.AreEqual("sdk_not_available", result.Result.Error!.Code);
        Assert.IsTrue(result.Result.Error.CanRetry);
        Assert.AreEqual("sdk", result.Result.Error.Details["component"]!.GetValue<string>());
    }

    private static AviUtlQueryService CreateService(
        DateTimeOffset now,
        IInstanceResolver resolver,
        IAviUtlQueryGateway queryGateway,
        IBridgeDiagnosticsGateway diagnosticsGateway)
    {
        FixedTimeProvider timeProvider = new(now);
        return new AviUtlQueryService(
            resolver,
            queryGateway,
            diagnosticsGateway,
            new PagingCursorCodec(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(), timeProvider),
            Guid.Parse("019f0000-0000-7000-8000-000000000100"),
            timeProvider);
    }

    private static RequestContext CreateContext(DateTimeOffset now)
    {
        return new RequestContextFactory(new FixedTimeProvider(now)).CreateContext(
            requestedInstanceId: null,
            timeoutMs: null,
            defaultTimeoutMs: 2_000,
            CancellationToken.None);
    }

    private static InstanceDescriptor CreateDescriptor()
    {
        return new InstanceDescriptor(
            Guid.Parse("019f0000-0000-7000-8000-000000000001"),
            1234,
            TestTime.CreateReferenceUtc(),
            "0.1.0",
            true);
    }

    private static Revision CreateRevision()
    {
        return new Revision(
            "019f0000-0000-7000-8000-000000000010:019f0000-0000-7000-8000-000000000020:0");
    }

    private static ObjectLocator CreateLocator(Guid instanceId)
    {
        return new ObjectLocator(
            instanceId,
            Guid.Parse("019f0000-0000-7000-8000-000000000020"),
            0,
            1,
            1,
            30,
            "object",
            new string('0', 64),
            new string('1', 64));
    }

    private static ObjectSummary CreateObject(ObjectLocator locator)
    {
        return new ObjectSummary(locator, "object", 0, 1, 1, 30, false, []);
    }

    private static StatusData CreateReadyStatus(InstanceDescriptor instance)
    {
        return new StatusData(
            ConnectionState.Ready,
            [],
            ProjectState.Saved,
            EditState.Edit,
            instance.InstanceId,
            [new AviUtlInstance(instance.InstanceId, instance.ProcessId, instance.BridgeVersion, "ready")]);
    }

    private static GatewayResponse<TData> CreateSuccess<TInput, TData>(
        GatewayRequest<TInput> request,
        TData data,
        Revision revision)
    {
        return new GatewayResponse<TData>(
            true,
            request.CorrelationId,
            request.InstanceId,
            revision,
            revision,
            data,
            [],
            null,
            ReadOnlyMemory<byte>.Empty);
    }

    private sealed class FakeInstanceResolver(IReadOnlyList<InstanceDescriptor> candidates) : IInstanceResolver
    {
        public IReadOnlyList<ObjectLocator> LastLocators { get; private set; } = [];

        public ValueTask<ApplicationResult<InstanceDescriptor>> ResolveAsync(
            Guid? requestedInstanceId,
            IReadOnlyList<ObjectLocator> locators,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastLocators = locators;
            Guid? effectiveId = requestedInstanceId ?? (locators.Count > 0 ? locators[0].InstanceId : null);
            InstanceDescriptor? selected = effectiveId.HasValue
                ? candidates.FirstOrDefault(candidate => candidate.InstanceId == effectiveId)
                : candidates.Count == 1 ? candidates[0] : null;
            ApplicationResult<InstanceDescriptor> result = selected is null
                ? ApplicationResult.Failure<InstanceDescriptor>(
                    candidates.Count > 1
                        ? ApplicationErrors.CreateInstanceAmbiguous(candidates.Select(candidate => candidate.InstanceId).ToArray())
                        : ApplicationErrors.CreateAviUtlNotRunning())
                : ApplicationResult.Success(selected);
            return ValueTask.FromResult(result);
        }

        public ValueTask<IReadOnlyList<InstanceDescriptor>> ListCandidatesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(candidates);
        }
    }

    private sealed class FakeQueryGateway : IAviUtlQueryGateway
    {
        public Func<GatewayRequest<GetTimelineInput>, GatewayResponse<TimelineData>>? GetTimelineHandler { get; set; }

        public Func<GatewayRequest<GetObjectInput>, GatewayResponse<ObjectData>>? GetObjectHandler { get; set; }

        public GatewayRequest<GetObjectInput>? LastObjectRequest { get; private set; }

        public int TimelineRequestCount { get; private set; }

        public ValueTask<GatewayResponse<ProjectData>> GetProjectAsync(
            GatewayRequest<GetProjectInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<TimelineData>> GetTimelineAsync(
            GatewayRequest<GetTimelineInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimelineRequestCount++;
            return ValueTask.FromResult(GetTimelineHandler!(request));
        }

        public ValueTask<GatewayResponse<ObjectsPageData>> FindObjectsAsync(
            GatewayRequest<FindObjectsInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<ObjectData>> GetObjectAsync(
            GatewayRequest<GetObjectInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastObjectRequest = request;
            return ValueTask.FromResult(GetObjectHandler!(request));
        }

        public ValueTask<GatewayResponse<EffectsData>> ListEffectsAsync(
            GatewayRequest<ListEffectsInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<EffectItemsData>> ListEffectItemsAsync(
            GatewayRequest<ListEffectItemsInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeDiagnosticsGateway : IBridgeDiagnosticsGateway
    {
        public Func<GatewayRequest<GetStatusInput>, GatewayResponse<StatusData>>? GetStatusHandler { get; init; }

        public Func<GatewayRequest<GetCapabilitiesInput>, GatewayResponse<CapabilitiesData>>? GetCapabilitiesHandler { get; init; }

        public ValueTask<GatewayResponse<StatusData>> GetStatusAsync(
            GatewayRequest<GetStatusInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetStatusHandler!(request));
        }

        public ValueTask<GatewayResponse<CapabilitiesData>> GetCapabilitiesAsync(
            GatewayRequest<GetCapabilitiesInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetCapabilitiesHandler!(request));
        }

        public ValueTask<GatewayResponse<LogsData>> GetLogsAsync(
            GatewayRequest<GetLogsInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<DiagnoseData>> DiagnoseAsync(
            GatewayRequest<DiagnoseInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

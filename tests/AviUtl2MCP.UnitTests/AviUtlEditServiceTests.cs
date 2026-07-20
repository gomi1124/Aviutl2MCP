using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Edits;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class AviUtlEditServiceTests
{
    private static readonly Guid INSTANCE_ID = Guid.Parse("019f1000-0000-7000-8000-000000000001");
    private static readonly Guid PROJECT_GENERATION = Guid.Parse("019f1000-0000-7000-8000-000000000002");
    private static readonly Revision EXPECTED_REVISION = new("epoch:generation:4");

    [TestMethod]
    public async Task CreateObjectPassesRevisionDryRunAndArguments()
    {
        // Arrange
        CapturingEditGateway gateway = new(CreateSuccess(new CreateObjectData
        {
            PlannedChanges = [new Change("create", "scene:0")],
        }));
        AviUtlEditService service = new(new StubResolver(CreateInstance()), gateway);
        CreateObjectInput input = new()
        {
            ExpectedRevision = EXPECTED_REVISION,
            DryRun = true,
            Effect = new EffectDefinitionSelector("Text"),
            Placement = new Placement(0, 1, 1, DurationFrames: 30),
            Name = "caption",
        };
        using RequestContext context = CreateContext();

        // Act
        QueryExecutionResult<CreateObjectData> result = await service.CreateObjectAsync(input, context);

        // Assert
        Assert.IsTrue(result.Result.IsSuccess);
        Assert.AreEqual("object.create", gateway.Operation);
        Assert.AreEqual(EXPECTED_REVISION, gateway.ExpectedRevision!.Value);
        Assert.IsTrue(gateway.DryRun);
        CreateObjectArgs args = Assert.IsInstanceOfType<CreateObjectArgs>(gateway.Parameters);
        Assert.AreEqual("Text", args.Effect.Name);
        Assert.AreEqual("caption", args.Name);
    }

    [TestMethod]
    public async Task MoveUsesLocatorForInstanceSelection()
    {
        // Arrange
        ObjectLocator locator = CreateLocator();
        StubResolver resolver = new(CreateInstance());
        CapturingEditGateway gateway = new(CreateSuccess(new UpdatedObjectData
        {
            PlannedChanges = [new Change("move", "object")],
        }));
        AviUtlEditService service = new(resolver, gateway);
        MoveObjectInput input = new()
        {
            ExpectedRevision = EXPECTED_REVISION,
            Locator = locator,
            Placement = new MovePlacement(0, 2, 31),
            DryRun = true,
        };
        using RequestContext context = CreateContext();

        // Act
        QueryExecutionResult<UpdatedObjectData> result = await service.MoveObjectAsync(input, context);

        // Assert
        Assert.IsTrue(result.Result.IsSuccess);
        Assert.AreEqual(locator, resolver.Locators.Single());
        Assert.AreEqual("object.move", gateway.Operation);
    }

    [TestMethod]
    public async Task GatewayErrorPreservesRevisionAndDetails()
    {
        // Arrange
        JsonElement details = JsonDocument.Parse("""{"target":"layer:1"}""").RootElement.Clone();
        GatewayResponse<DeleteData> response = new(
            false,
            Guid.CreateVersion7(),
            INSTANCE_ID,
            new Revision("epoch:generation:5"),
            new Revision("epoch:generation:2"),
            null,
            [],
            new GatewayError(
                "object_collision",
                "collision",
                false,
                "preflight",
                "unchanged",
                false,
                details),
            ReadOnlyMemory<byte>.Empty);
        AviUtlEditService service = new(
            new StubResolver(CreateInstance()),
            new CapturingEditGateway(response));
        using RequestContext context = CreateContext();

        // Act
        QueryExecutionResult<DeleteData> result = await service.DeleteObjectAsync(
            new DeleteObjectInput
            {
                ExpectedRevision = EXPECTED_REVISION,
                Locator = CreateLocator(),
            },
            context);

        // Assert
        Assert.IsFalse(result.Result.IsSuccess);
        Assert.AreEqual("object_collision", result.Result.Error!.Code);
        Assert.AreEqual("layer:1", result.Result.Error.Details["target"]!.GetValue<string>());
        Assert.AreEqual("epoch:generation:5", result.Revision!.Value.Value);
    }

    [TestMethod]
    public async Task SelectionFailureDoesNotCallGateway()
    {
        // Arrange
        StubResolver resolver = new(ApplicationResult.Failure<InstanceDescriptor>(
            ApplicationErrors.CreateAviUtlNotRunning()));
        CapturingEditGateway gateway = new(response: null);
        AviUtlEditService service = new(resolver, gateway);
        using RequestContext context = CreateContext();

        // Act
        QueryExecutionResult<DeleteData> result = await service.DeleteObjectAsync(
            new DeleteObjectInput
            {
                ExpectedRevision = EXPECTED_REVISION,
                Locator = CreateLocator(),
            },
            context);

        // Assert
        Assert.AreEqual("aviutl_not_running", result.Result.Error!.Code);
        Assert.IsNull(gateway.Operation);
    }

    [TestMethod]
    public async Task SetCursorUsesViewRevisionWithoutContentRevision()
    {
        // Arrange
        Revision expectedViewRevision = new("epoch:generation:2");
        CapturingEditGateway gateway = new(CreateSuccess(new CursorData(
            0,
            25,
            5,
            new Selection(30, 35),
            new CoordinateSystem(1, 1, true))));
        AviUtlEditService service = new(new StubResolver(CreateInstance()), gateway);
        SetCursorInput input = new()
        {
            Frame = 25,
            DisplayFrame = 5,
            ExpectedViewRevision = expectedViewRevision,
        };
        using RequestContext context = CreateContext();

        // Act
        QueryExecutionResult<CursorData> result = await service.SetCursorAsync(input, context);

        // Assert
        Assert.IsTrue(result.Result.IsSuccess);
        Assert.AreEqual("view.setCursor", gateway.Operation);
        Assert.IsNull(gateway.ExpectedRevision);
        SetCursorInput parameters = Assert.IsInstanceOfType<SetCursorInput>(gateway.Parameters);
        Assert.AreEqual(expectedViewRevision, parameters.ExpectedViewRevision);
    }

    [TestMethod]
    public async Task ExecuteBatchUsesLocatorsAndPreservesPartialData()
    {
        // Arrange
        ObjectLocator locator = CreateLocator();
        BatchData partialData = new(
            [
                new BatchResult(
                    "rename",
                    BatchOperationKind.SetObjectName,
                    BatchResultStatus.Applied,
                    [new Change("setName", "object")]),
                new BatchResult(
                    "move",
                    BatchOperationKind.MoveObject,
                    BatchResultStatus.Failed,
                    [new Change("move", "object")]),
            ],
            ["rename"],
            true);
        JsonElement details = JsonDocument.Parse("{}").RootElement.Clone();
        GatewayResponse<BatchData> response = new(
            false,
            Guid.CreateVersion7(),
            INSTANCE_ID,
            new Revision("epoch:generation:5"),
            new Revision("epoch:generation:2"),
            partialData,
            [],
            new GatewayError(
                "partial_operation",
                "partial",
                false,
                "sdk",
                "partial",
                true,
                details),
            ReadOnlyMemory<byte>.Empty);
        StubResolver resolver = new(CreateInstance());
        CapturingEditGateway gateway = new(response);
        AviUtlEditService service = new(resolver, gateway);
        ExecuteBatchInput input = new()
        {
            ExpectedRevision = EXPECTED_REVISION,
            Operations =
            [
                new BatchSetObjectName(
                    "rename",
                    new SetObjectNameArgs(locator, "renamed")),
                new BatchMoveObject(
                    "move",
                    new MoveObjectArgs(locator, new MovePlacement(0, 2, 31))),
            ],
        };
        using RequestContext context = CreateContext();

        // Act
        QueryExecutionResult<BatchData> result = await service.ExecuteBatchAsync(input, context);

        // Assert
        Assert.IsFalse(result.Result.IsSuccess);
        Assert.AreEqual("partial_operation", result.Result.Error!.Code);
        Assert.AreSame(partialData, result.Result.Value);
        Assert.AreEqual(2, resolver.Locators.Count);
        Assert.AreEqual("batch.execute", gateway.Operation);
        Assert.AreEqual(EXPECTED_REVISION, gateway.ExpectedRevision!.Value);
    }

    private static GatewayResponse<TData> CreateSuccess<TData>(TData data) => new(
        true,
        Guid.CreateVersion7(),
        INSTANCE_ID,
        new Revision("epoch:generation:5"),
        new Revision("epoch:generation:2"),
        data,
        [],
        null,
        ReadOnlyMemory<byte>.Empty);

    private static ApplicationResult<InstanceDescriptor> CreateInstance() => ApplicationResult.Success(
        new InstanceDescriptor(INSTANCE_ID, 1234, DateTimeOffset.UtcNow, "0.1.0", true));

    private static ObjectLocator CreateLocator() => new(
        INSTANCE_ID,
        PROJECT_GENERATION,
        0,
        1,
        1,
        30,
        "voice",
        new string('a', 64),
        new string('b', 64));

    private static RequestContext CreateContext() => new RequestContextFactory().CreateContext(
        INSTANCE_ID,
        timeoutMs: null,
        defaultTimeoutMs: 10_000,
        CancellationToken.None);

    private sealed class StubResolver(ApplicationResult<InstanceDescriptor> result) : IInstanceResolver
    {
        public IReadOnlyList<ObjectLocator> Locators { get; private set; } = [];

        public ValueTask<ApplicationResult<InstanceDescriptor>> ResolveAsync(
            Guid? requestedInstanceId,
            IReadOnlyList<ObjectLocator> locators,
            CancellationToken cancellationToken)
        {
            Locators = locators;
            return ValueTask.FromResult(result);
        }

        public ValueTask<IReadOnlyList<InstanceDescriptor>> ListCandidatesAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<InstanceDescriptor>>([]);
    }

    private sealed class CapturingEditGateway(object? response) : IAviUtlEditGateway
    {
        public string? Operation { get; private set; }

        public Revision? ExpectedRevision { get; private set; }

        public bool DryRun { get; private set; }

        public object? Parameters { get; private set; }

        public ValueTask<GatewayResponse<TData>> ExecuteEditAsync<TParameters, TData>(
            string operation,
            GatewayRequest<TParameters> request,
            CancellationToken cancellationToken)
        {
            Operation = operation;
            ExpectedRevision = request.ExpectedRevision;
            DryRun = request.DryRun;
            Parameters = request.Parameters;
            return ValueTask.FromResult((GatewayResponse<TData>)response!);
        }

        public ValueTask<GatewayResponse<BatchData>> ExecuteBatchAsync(
            GatewayRequest<ExecuteBatchInput> request,
            CancellationToken cancellationToken)
        {
            Operation = "batch.execute";
            ExpectedRevision = request.ExpectedRevision;
            DryRun = request.DryRun;
            Parameters = request.Parameters;
            return ValueTask.FromResult((GatewayResponse<BatchData>)response!);
        }

        public ValueTask<GatewayResponse<CursorData>> SetCursorAsync(
            GatewayRequest<SetCursorInput> request,
            CancellationToken cancellationToken)
        {
            Operation = "view.setCursor";
            ExpectedRevision = request.ExpectedRevision;
            DryRun = request.DryRun;
            Parameters = request.Parameters;
            return ValueTask.FromResult((GatewayResponse<CursorData>)response!);
        }
    }
}

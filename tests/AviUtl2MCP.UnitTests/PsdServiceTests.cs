using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Psd;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class PsdServiceTests
{
    private static readonly Guid INSTANCE_ID = Guid.CreateVersion7();
    private static readonly Guid PROJECT_GENERATION = Guid.CreateVersion7();
    private static readonly Revision EXPECTED_REVISION = new("epoch:generation:4");

    [TestMethod]
    public async Task CreateVoiceNormalizesCompanionsAndForwardsLocator()
    {
        // Arrange
        string directory = CreateTemporaryDirectory();
        try
        {
            string audioPath = Path.Combine(directory, "alice.wav");
            string textPath = Path.Combine(directory, "alice.txt");
            string labPath = Path.Combine(directory, "alice.lab");
            await File.WriteAllBytesAsync(audioPath, [1, 2, 3]);
            await File.WriteAllTextAsync(textPath, "hello");
            await File.WriteAllTextAsync(labPath, "0 1000000 a");
            PsdVoiceData data = new()
            {
                VoiceObjects = [],
                SubtitleObjects = [],
                CompanionFiles = new PsdCompanionFiles(audioPath, textPath, labPath),
            };
            CapturingPsdGateway gateway = new(CreateSuccess(data));
            StubResolver resolver = new(CreateInstance());
            PsdService service = new(resolver, gateway);
            ObjectLocator locator = CreateLocator();
            PsdCreateVoiceInput input = new()
            {
                ExpectedRevision = EXPECTED_REVISION,
                DryRun = true,
                AudioPath = audioPath,
                CharacterId = "alice",
                PsdLocator = locator,
                Placement = new Placement(0, 5, 10, DurationFrames: 30),
            };
            using RequestContext context = CreateContext();

            // Act
            QueryExecutionResult<PsdVoiceData> result = await service.CreateVoiceAsync(input, context);

            // Assert
            Assert.IsTrue(result.Result.IsSuccess);
            Assert.AreEqual("psd.createVoice", gateway.Operation);
            Assert.IsNotNull(gateway.ExpectedRevision);
            Assert.AreEqual("epoch:generation:4", gateway.ExpectedRevision.Value.Value);
            Assert.IsTrue(gateway.DryRun);
            PsdCreateVoiceArgs parameters = Assert.IsInstanceOfType<PsdCreateVoiceArgs>(gateway.Parameters);
            Assert.AreEqual(Path.GetFullPath(audioPath), parameters.AudioPath);
            Assert.AreEqual(Path.GetFullPath(textPath), parameters.TextPath);
            Assert.AreEqual(Path.GetFullPath(labPath), parameters.LabPath);
            Assert.AreEqual(locator, parameters.PsdLocator);
            CollectionAssert.AreEqual(new[] { locator }, resolver.Locators.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SetCharacterPreservesPartialGatewayData()
    {
        // Arrange
        PsdCharacterData partialData = new() { CharacterId = "alice" };
        JsonElement details = JsonDocument.Parse("{}").RootElement.Clone();
        GatewayResponse<PsdCharacterData> response = new(
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
        CapturingPsdGateway gateway = new(response);
        ObjectLocator locator = CreateLocator();
        PsdService service = new(new StubResolver(CreateInstance()), gateway);
        using RequestContext context = CreateContext();

        // Act
        QueryExecutionResult<PsdCharacterData> result = await service.SetCharacterAsync(
            new PsdSetCharacterInput
            {
                ExpectedRevision = EXPECTED_REVISION,
                Locator = locator,
                CharacterId = "alice",
            },
            context);

        // Assert
        Assert.IsFalse(result.Result.IsSuccess);
        Assert.AreEqual("partial_operation", result.Result.Error!.Code);
        Assert.AreSame(partialData, result.Result.Value);
        Assert.AreEqual("psd.setCharacter", gateway.Operation);
        PsdSetCharacterArgs parameters = Assert.IsInstanceOfType<PsdSetCharacterArgs>(gateway.Parameters);
        Assert.AreEqual(locator, parameters.Locator);
        Assert.AreEqual("alice", parameters.CharacterId);
    }

    [TestMethod]
    public async Task ValidateUsesReadOnlyGatewayWithoutRevision()
    {
        // Arrange
        PsdValidateData data = new([], "ptk2-2.0.0alpha10-ja");
        CapturingPsdGateway gateway = new(response: null)
        {
            ValidateResponse = CreateSuccess(data),
        };
        ObjectLocator locator = CreateLocator();
        StubResolver resolver = new(CreateInstance());
        PsdService service = new(resolver, gateway);
        using RequestContext context = CreateContext();
        PsdValidateInput input = new()
        {
            Locator = locator,
            Checks = [PsdValidationCheck.Character, PsdValidationCheck.Subtitle],
        };

        // Act
        QueryExecutionResult<PsdValidateData> result = await service.ValidateAsync(input, context);

        // Assert
        Assert.IsTrue(result.Result.IsSuccess);
        Assert.AreEqual("psd.validate", gateway.Operation);
        Assert.IsNull(gateway.ExpectedRevision);
        Assert.IsFalse(gateway.DryRun);
        CollectionAssert.AreEqual(new[] { locator }, resolver.Locators.ToArray());
    }

    [TestMethod]
    public async Task SelectionFailurePreventsPsdGatewayCall()
    {
        // Arrange
        StubResolver resolver = new(ApplicationResult.Failure<InstanceDescriptor>(
            ApplicationErrors.CreateAviUtlNotRunning()));
        CapturingPsdGateway gateway = new(response: null);
        PsdService service = new(resolver, gateway);
        using RequestContext context = CreateContext();

        // Act
        QueryExecutionResult<PsdSetupData> result = await service.SetupAsync(
            new PsdSetupInput { ExpectedRevision = EXPECTED_REVISION },
            context);

        // Assert
        Assert.AreEqual("aviutl_not_running", result.Result.Error!.Code);
        Assert.IsNull(gateway.Operation);
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
        "alice",
        new string('a', 64),
        new string('b', 64));

    private static RequestContext CreateContext() => new RequestContextFactory().CreateContext(
        INSTANCE_ID,
        timeoutMs: null,
        defaultTimeoutMs: 10_000,
        CancellationToken.None);

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"AviUtl2MCP-{Guid.CreateVersion7():D}");
        Directory.CreateDirectory(directory);
        return directory;
    }

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

    private sealed class CapturingPsdGateway(object? response) : IAviUtlPsdGateway
    {
        public string? Operation { get; private set; }

        public Revision? ExpectedRevision { get; private set; }

        public bool DryRun { get; private set; }

        public object? Parameters { get; private set; }

        public GatewayResponse<PsdValidateData>? ValidateResponse { get; init; }

        public ValueTask<GatewayResponse<CapabilitiesData>> GetPsdCapabilitiesAsync(
            GatewayRequest<GetCapabilitiesInput> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GatewayResponse<TData>> ExecutePsdAsync<TParameters, TData>(
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

        public ValueTask<GatewayResponse<PsdValidateData>> ValidatePsdAsync(
            GatewayRequest<PsdValidateInput> request,
            CancellationToken cancellationToken)
        {
            Operation = "psd.validate";
            ExpectedRevision = request.ExpectedRevision;
            DryRun = request.DryRun;
            Parameters = request.Parameters;
            return ValueTask.FromResult(ValidateResponse!);
        }
    }
}

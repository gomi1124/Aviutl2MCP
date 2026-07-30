using System.Globalization;
using AviUtl2MCP.Application.Capabilities;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class DiagnosticsServiceTests
{
    private static readonly byte[] SIGNING_KEY = Enumerable.Range(1, 32)
        .Select(value => checked((byte)value))
        .ToArray();

    [TestMethod]
    public async Task RunAsyncReturnsHealthyForReadyInstalledEnvironment()
    {
        // Arrange
        DiagnosticContext context = CreateHealthyContext();
        DiagnosticsService service = new(DiagnosticsService.CreateDefaultRules());

        // Act
        ApplicationResult<DiagnoseData> result = await service.RunAsync(
            context,
            CancellationToken.None);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DiagnosticOverallStatus.Healthy, result.Value!.Status);
        Assert.HasCount(8, result.Value.Checks);
        Assert.IsTrue(result.Value.Checks.All(check =>
            check.Status is DiagnosticCheckStatus.Pass or DiagnosticCheckStatus.Skipped));
        Assert.HasCount(6, result.Value.Components);
        Assert.IsTrue(result.Value.Components.All(component =>
            component.Status == DiagnosticComponentStatus.Detected));
    }

    [TestMethod]
    public async Task RunAsyncDegradesForKnownPsdErrorWithoutHidingOtherChecks()
    {
        // Arrange
        DiagnosticContext healthy = CreateHealthyContext();
        KnownLogMatch match = new(
            "psdtoolkit.pipe-exited",
            "aviutl",
            DiagnosticSeverity.Error,
            ["fixture evidence"],
            "PSD is unavailable.",
            "Restart AviUtl2.");
        DiagnosticContext context = healthy with { KnownLogMatches = [match] };
        DiagnosticsService service = new(DiagnosticsService.CreateDefaultRules());

        // Act
        ApplicationResult<DiagnoseData> result = await service.RunAsync(
            context,
            CancellationToken.None);

        // Assert
        Assert.AreEqual(DiagnosticOverallStatus.Degraded, result.Value!.Status);
        DiagnosticCheck knownLogs = GetCheck(result.Value, "known-logs");
        Assert.AreEqual(DiagnosticCheckStatus.Fail, knownLogs.Status);
        Assert.AreEqual(DiagnosticCheckStatus.Pass, GetCheck(result.Value, "connection").Status);
        Assert.AreSame(match, result.Value.KnownLogMatches.Single());
    }

    [TestMethod]
    public async Task RunAsyncReturnsUnavailableWhenConnectionIdentityFails()
    {
        // Arrange
        DiagnosticContext healthy = CreateHealthyContext();
        StatusData faulted = healthy.Status! with
        {
            ConnectionState = ConnectionState.Faulted,
            SelectedInstance = null,
            Instances = [],
        };
        DiagnosticsService service = new(DiagnosticsService.CreateDefaultRules());

        // Act
        ApplicationResult<DiagnoseData> result = await service.RunAsync(
            healthy with { Status = faulted },
            CancellationToken.None);

        // Assert
        Assert.AreEqual(DiagnosticOverallStatus.Unavailable, result.Value!.Status);
        Assert.AreEqual(DiagnosticCheckStatus.Fail, GetCheck(result.Value, "connection").Status);
    }

    [TestMethod]
    public async Task RunAsyncEvaluatesPsdAndGcmzFailuresIndependently()
    {
        // Arrange
        DiagnosticContext healthy = CreateHealthyContext();
        CapabilityVersions versions = healthy.Capabilities!.Versions with
        {
            PsdToolKit = null,
            GcmzDrops = null,
        };
        CapabilitiesData unavailable = CapabilityService.GetCapabilities(new CapabilityEnvironment(
            IsBridgeReady: true,
            IsProjectOpen: true,
            IsProjectSaved: true,
            CanEdit: true,
            HasPsdToolKit: false,
            HasGcmzDrops: false,
            versions));
        StatusData status = healthy.Status! with
        {
            Components = healthy.Status.Components
                .Where(component => !component.Name.Contains("PSDToolKit", StringComparison.OrdinalIgnoreCase)
                    && !component.Name.Contains("GCMZ", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };
        DiagnosticsService service = new(DiagnosticsService.CreateDefaultRules());

        // Act
        ApplicationResult<DiagnoseData> result = await service.RunAsync(
            healthy with { Status = status, Capabilities = unavailable },
            CancellationToken.None);

        // Assert
        DiagnoseData data = result.Value!;
        Assert.AreEqual(DiagnosticCheckStatus.Fail, GetCheck(data, "psdtoolkit").Status);
        Assert.AreEqual(DiagnosticCheckStatus.Fail, GetCheck(data, "gcmzdrops").Status);
        Assert.AreEqual(
            DiagnosticComponentStatus.Missing,
            data.Components.Single(component => component.Name == "psdtoolkit2").Status);
        Assert.AreEqual(
            DiagnosticComponentStatus.Missing,
            data.Components.Single(component => component.Name == "gcmzdrops").Status);
    }

    [TestMethod]
    public async Task RunAsyncKeepsGcmzPassingWhenOnlyPsdIsMissing()
    {
        // Arrange
        DiagnosticContext healthy = CreateHealthyContext();
        CapabilityVersions versions = healthy.Capabilities!.Versions with { PsdToolKit = null };
        CapabilitiesData capabilities = CapabilityService.GetCapabilities(new CapabilityEnvironment(
            IsBridgeReady: true,
            IsProjectOpen: true,
            IsProjectSaved: true,
            CanEdit: true,
            HasPsdToolKit: false,
            HasGcmzDrops: true,
            versions));
        StatusData status = healthy.Status! with
        {
            Components = healthy.Status.Components
                .Where(component => !component.Name.Contains("PSDToolKit", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };
        DiagnosticsService service = new(DiagnosticsService.CreateDefaultRules());

        // Act
        ApplicationResult<DiagnoseData> result = await service.RunAsync(
            healthy with { Status = status, Capabilities = capabilities },
            CancellationToken.None);

        // Assert
        DiagnoseData data = result.Value!;
        Assert.AreEqual(DiagnosticCheckStatus.Fail, GetCheck(data, "psdtoolkit").Status);
        Assert.AreEqual(DiagnosticCheckStatus.Pass, GetCheck(data, "gcmzdrops").Status);
    }

    [TestMethod]
    public async Task RunAsyncWarnsWhenDetailedPluginProbesAreIncomplete()
    {
        // Arrange
        DiagnosticContext healthy = CreateHealthyContext();
        StatusData status = healthy.Status! with
        {
            Components = healthy.Status.Components
                .Where(component => component.Name is not "PSDToolKit.Alias" and not "GCMZDrops.FMO")
                .ToArray(),
        };
        DiagnosticsService service = new(DiagnosticsService.CreateDefaultRules());

        // Act
        ApplicationResult<DiagnoseData> result = await service.RunAsync(
            healthy with { Status = status },
            CancellationToken.None);

        // Assert
        DiagnoseData data = result.Value!;
        Assert.AreEqual(DiagnosticCheckStatus.Warning, GetCheck(data, "psdtoolkit").Status);
        Assert.AreEqual(DiagnosticCheckStatus.Warning, GetCheck(data, "gcmzdrops").Status);
        Assert.AreEqual(DiagnosticOverallStatus.Degraded, data.Status);
    }

    [TestMethod]
    public async Task RunAsyncReportsRequestedSmokeFailureAndLeavesOtherSmokeSkipped()
    {
        // Arrange
        DiagnosticContext context = CreateHealthyContext() with
        {
            PreviewSmoke = DiagnosticSmokeResult.Failed(
                "preview_invalid_png",
                "PNG validation failed.",
                canRetry: true),
        };
        DiagnosticsService service = new(DiagnosticsService.CreateDefaultRules());

        // Act
        ApplicationResult<DiagnoseData> result = await service.RunAsync(
            context,
            CancellationToken.None);

        // Assert
        Assert.AreEqual(DiagnosticOverallStatus.Degraded, result.Value!.Status);
        Assert.AreEqual(DiagnosticCheckStatus.Fail, GetCheck(result.Value, "preview-smoke").Status);
        Assert.AreEqual(DiagnosticCheckStatus.Skipped, GetCheck(result.Value, "read-smoke").Status);
    }

    [TestMethod]
    public async Task RunAsyncIsolatesRuleException()
    {
        // Arrange
        DiagnosticsService service = new([new ThrowingDiagnosticRule()]);

        // Act
        ApplicationResult<DiagnoseData> result = await service.RunAsync(
            CreateHealthyContext(),
            CancellationToken.None);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DiagnosticOverallStatus.Degraded, result.Value!.Status);
        DiagnosticCheck check = result.Value.Checks.Single();
        Assert.AreEqual("throwing", check.CheckId);
        Assert.AreEqual(DiagnosticCheckStatus.Fail, check.Status);
        StringAssert.Contains(check.Evidence.Single(), nameof(InvalidOperationException));
    }

    [TestMethod]
    public async Task ContextFactoryUsesUniqueChildCorrelationsAndClassifiesAllLogSources()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        DiagnosticContext healthy = CreateHealthyContext();
        RecordingDiagnosticsGateway gateway = new(healthy.Status!, healthy.Capabilities!);
        RecordingLogSource server = new(LogSource.Server, [CreateLog("server", "server fixture", now)]);
        RecordingLogSource bridge = new(LogSource.Bridge, [CreateLog("bridge", "bridge fixture", now.AddSeconds(1))]);
        RecordingLogSource aviutl = new(
            LogSource.Aviutl,
            [CreateLog("aviutl", "PSDToolKit pipe exited unexpectedly", now.AddSeconds(2))]);
        LogQueryService logQueryService = new(
            [server, bridge, aviutl],
            new LogCursorCodec(SIGNING_KEY, new FixedTimeProvider(now)),
            new FixedTimeProvider(now));
        RecordingSmokeProbe smoke = new();
        DiagnosticContextFactory factory = new(
            gateway,
            logQueryService,
            smoke,
            new FixedTimeProvider(now));
        Guid parentCorrelationId = Guid.CreateVersion7(now);
        DiagnosticRunContext runContext = new(
            Guid.NewGuid(),
            healthy.Instance,
            parentCorrelationId,
            now.AddSeconds(30),
            30_000);
        DiagnoseInput input = new()
        {
            InstanceId = healthy.Instance.InstanceId,
            MaxLogLines = 2,
            IncludeReadSmoke = true,
            IncludePreviewSmoke = true,
        };

        // Act
        ApplicationResult<DiagnosticContext> result = await factory.CreateAsync(
            input,
            runContext,
            CancellationToken.None);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(3, result.Value!.Logs);
        Assert.HasCount(1, result.Value.KnownLogMatches);
        Assert.AreEqual("psdtoolkit.pipe-exited", result.Value.KnownLogMatches[0].RuleId);
        Assert.AreEqual(2, server.Query!.Limit);
        Assert.AreEqual(2, bridge.Query!.Limit);
        Assert.AreEqual(2, aviutl.Query!.Limit);
        Assert.AreEqual(healthy.Instance.ProcessCreationTime, server.Query.Since);
        Assert.AreEqual(healthy.Instance.ProcessCreationTime, bridge.Query.Since);
        Assert.AreEqual(healthy.Instance.ProcessCreationTime, aviutl.Query.Since);
        Guid[] operationIds =
        [
            gateway.StatusCorrelationId,
            gateway.CapabilitiesCorrelationId,
            server.Query.RequestCorrelationId,
            bridge.Query.RequestCorrelationId,
            aviutl.Query.RequestCorrelationId,
            smoke.ReadCorrelationId,
            smoke.PreviewCorrelationId,
        ];
        Assert.AreEqual(operationIds.Length, operationIds.Distinct().Count());
        Assert.IsFalse(operationIds.Contains(parentCorrelationId));
    }

    [TestMethod]
    public async Task ContextFactoryRejectsInvalidLogLimitBeforeGatewayCall()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        DiagnosticContext healthy = CreateHealthyContext();
        RecordingDiagnosticsGateway gateway = new(healthy.Status!, healthy.Capabilities!);
        LogQueryService logQueryService = new(
            [new RecordingLogSource(LogSource.Server, [])],
            new LogCursorCodec(SIGNING_KEY, new FixedTimeProvider(now)),
            new FixedTimeProvider(now));
        DiagnosticContextFactory factory = new(
            gateway,
            logQueryService,
            timeProvider: new FixedTimeProvider(now));
        DiagnosticRunContext runContext = new(
            Guid.NewGuid(),
            healthy.Instance,
            Guid.CreateVersion7(now),
            now.AddSeconds(30),
            30_000);

        // Act
        ApplicationResult<DiagnosticContext> result = await factory.CreateAsync(
            new DiagnoseInput { MaxLogLines = 0 },
            runContext,
            CancellationToken.None);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("invalid_argument", result.Error!.Code);
        Assert.AreEqual(Guid.Empty, gateway.StatusCorrelationId);
    }

    private static DiagnosticContext CreateHealthyContext()
    {
        Guid instanceId = Guid.NewGuid();
        InstanceDescriptor instance = new(
            instanceId,
            ProcessId: 4242,
            TestTime.CreateReferenceUtc().AddHours(-1),
            BridgeVersion: "1.0.0",
            IsAvailable: true);
        StatusData status = new(
            ConnectionState.Ready,
            [
                new ComponentStatus("server", "ready", "1.0.0"),
                new ComponentStatus("bridge", "ready", "1.0.0"),
                new ComponentStatus("aviutl", "ready", "2.0.0"),
                new ComponentStatus("sdk", "ready", "2.0.0"),
                new ComponentStatus("PSDToolKit.Effect", "ready", "2.0.0"),
                new ComponentStatus("PSDToolKit.Alias", "ready", "2.0.0"),
                new ComponentStatus("GCMZDrops.Mutex", "ready", "3.0.0"),
                new ComponentStatus("GCMZDrops.FMO", "ready", "3.0.0"),
                new ComponentStatus("GCMZDrops.APIv3", "ready", "3.0.0"),
                new ComponentStatus("GCMZDrops.HWND-PID", "ready", "3.0.0"),
            ],
            ProjectState.Saved,
            EditState.Edit,
            instanceId,
            [new AviUtlInstance(instanceId, instance.ProcessId, instance.BridgeVersion, "ready")]);
        CapabilityVersions versions = new(
            Server: "1.0.0",
            Schema: "1.0.0",
            Protocol: "1.0",
            Bridge: "1.0.0",
            Aviutl: "2.0.0",
            Sdk: "2.0.0",
            PsdToolKit: "2.0.0",
            GcmzDrops: "3.0.0");
        CapabilitiesData capabilities = CapabilityService.GetCapabilities(new CapabilityEnvironment(
            IsBridgeReady: true,
            IsProjectOpen: true,
            IsProjectSaved: true,
            CanEdit: true,
            HasPsdToolKit: true,
            HasGcmzDrops: true,
            versions));
        return new DiagnosticContext(
            instance,
            status,
            null,
            capabilities,
            null,
            [],
            [],
            null,
            [],
            DiagnosticSmokeResult.NotRequested(),
            DiagnosticSmokeResult.NotRequested());
    }

    private static DiagnosticCheck GetCheck(DiagnoseData data, string checkId) =>
        data.Checks.Single(check => check.CheckId == checkId);

    private static LogEntry CreateLog(string source, string message, DateTimeOffset timestamp) =>
        new(
            timestamp,
            nameof(ContractLogLevel.Error),
            source,
            $"fixture-{timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
            null,
            message);

    private sealed class ThrowingDiagnosticRule : IDiagnosticRule
    {
        public string RuleId => "throwing";

        public int Order => 1;

        public ValueTask<DiagnosticCheck> EvaluateAsync(
            DiagnosticContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("fixture");
    }

    private sealed class RecordingLogSource : ILogSource
    {
        private readonly IReadOnlyList<LogEntry> _entries;

        public RecordingLogSource(LogSource source, IReadOnlyList<LogEntry> entries)
        {
            Source = source;
            _entries = entries;
        }

        public LogSource Source { get; }

        public LogSourceQuery? Query { get; private set; }

        public ValueTask<LogSourcePage> ReadAsync(
            LogSourceQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Query = query;
            return ValueTask.FromResult(new LogSourcePage(
                _entries.Take(query.Limit).ToArray(),
                null,
                false,
                $"{Source}-generation"));
        }
    }

    private sealed class RecordingSmokeProbe : IDiagnosticSmokeProbe
    {
        public Guid ReadCorrelationId { get; private set; }

        public Guid PreviewCorrelationId { get; private set; }

        public ValueTask<DiagnosticSmokeResult> RunReadSmokeAsync(
            DiagnosticRunContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCorrelationId = context.CorrelationId;
            return ValueTask.FromResult(DiagnosticSmokeResult.Success("projectRevision=fixture"));
        }

        public ValueTask<DiagnosticSmokeResult> RunPreviewSmokeAsync(
            DiagnosticRunContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreviewCorrelationId = context.CorrelationId;
            return ValueTask.FromResult(DiagnosticSmokeResult.Success("previewSha256=fixture"));
        }
    }

    private sealed class RecordingDiagnosticsGateway(
        StatusData status,
        CapabilitiesData capabilities) : IBridgeDiagnosticsGateway
    {
        public Guid StatusCorrelationId { get; private set; }

        public Guid CapabilitiesCorrelationId { get; private set; }

        public ValueTask<GatewayResponse<StatusData>> GetStatusAsync(
            GatewayRequest<GetStatusInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusCorrelationId = request.CorrelationId;
            return ValueTask.FromResult(CreateResponse(request, status));
        }

        public ValueTask<GatewayResponse<CapabilitiesData>> GetCapabilitiesAsync(
            GatewayRequest<GetCapabilitiesInput> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapabilitiesCorrelationId = request.CorrelationId;
            return ValueTask.FromResult(CreateResponse(request, capabilities));
        }

        public ValueTask<GatewayResponse<LogsData>> GetLogsAsync(
            GatewayRequest<GetLogsInput> request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GatewayResponse<DiagnoseData>> DiagnoseAsync(
            GatewayRequest<DiagnoseInput> request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static GatewayResponse<TData> CreateResponse<TInput, TData>(
            GatewayRequest<TInput> request,
            TData data) =>
            new(
                true,
                request.CorrelationId,
                request.InstanceId,
                new Revision("fixture"),
                null,
                data,
                [],
                null,
                ReadOnlyMemory<byte>.Empty);
    }
}

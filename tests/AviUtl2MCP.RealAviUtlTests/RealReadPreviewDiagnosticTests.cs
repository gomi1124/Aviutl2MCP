using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Gateways;
using AviUtl2MCP.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AviUtl2MCP.RealAviUtlTests;

[TestClass]
[DoNotParallelize]
public sealed class RealReadPreviewDiagnosticTests
{
    private const uint MINIMUM_TESTED_AVIUTL_VERSION = 2010300U;
    private static readonly byte[] PNG_SIGNATURE =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly string[] REQUIRED_READY_COMPONENTS =
    [
        "bridge",
        "aviutl",
        "sdk",
        "psdtoolkit.effect",
        "psdtoolkit.alias",
    ];
    private static readonly string[] GCMZ_EXCLUSIVE_OPERATION_NAMES =
    [
        "aviutl_psd_create",
        "aviutl_psd_create_voice",
    ];
    private static readonly string[] VALID_DIAGNOSTIC_STATUSES = ["pass", "degraded"];

    [TestMethod]
    [TestCategory("RealAviUtl2")]
    [TestProperty("TestId", "real.timeline-read")]
    [TestProperty("TestId", "real.preview-image")]
    [TestProperty("TestId", "smoke.before-after-diff")]
    [TestProperty("TestId", "bridge.render-lifetime-stress")]
    [TestProperty("TestId", "bridge.concurrent-sessions")]
    [Timeout(180_000)]
    public async Task RealAviUtlReadsRendersAndDiagnosesIsolatedFixture()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive("Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(timeout.Token);
        harness.RecordAcceptanceTestIds(
            "real.timeline-read",
            "real.preview-image",
            "smoke.before-after-diff",
            "bridge.render-lifetime-stress",
            "bridge.concurrent-sessions",
            "real.fixture-process-guard");
        try
        {
        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Name = "AviUtl2MCP real isolated test",
            Command = "dotnet",
            Arguments = [typeof(ServerMarker).Assembly.Location],
            WorkingDirectory = Path.GetDirectoryName(typeof(ServerMarker).Assembly.Location),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["AVIUTL2_MCP_INSTANCE_DIRECTORY"] = harness.InstanceDirectory,
                ["AVIUTL2_MCP_LOG_DIRECTORY"] = harness.ServerLogDirectory,
                ["AVIUTL2_LOG_DIRECTORY"] = harness.AviUtlLogDirectory,
            },
        });
        await using McpClient client = await McpClient.CreateAsync(
            transport,
            cancellationToken: timeout.Token);

        IList<McpClientTool> tools = await client.ListToolsAsync(
            cancellationToken: timeout.Token);
        Assert.AreEqual(33, tools.Count);

        JsonElement project = await WaitForProjectAsync(
            client,
            harness.InstanceId,
            timeout.Token);
        JsonElement status = await WaitForRequiredComponentsReadyAsync(
            client,
            harness.InstanceId,
            timeout.Token);
        Assert.AreEqual("ready", status.GetProperty("data").GetProperty("connectionState").GetString());
        Assert.AreEqual(
            harness.InstanceId,
            status.GetProperty("data").GetProperty("selectedInstance").GetGuid());

        StdioClientTransport secondTransport = new(new StdioClientTransportOptions
        {
            Name = "AviUtl2MCP second real isolated session",
            Command = "dotnet",
            Arguments = [typeof(ServerMarker).Assembly.Location],
            WorkingDirectory = Path.GetDirectoryName(typeof(ServerMarker).Assembly.Location),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["AVIUTL2_MCP_INSTANCE_DIRECTORY"] = harness.InstanceDirectory,
                ["AVIUTL2_MCP_LOG_DIRECTORY"] = harness.ServerLogDirectory,
                ["AVIUTL2_LOG_DIRECTORY"] = harness.AviUtlLogDirectory,
            },
        });
        await using McpClient secondClient = await McpClient.CreateAsync(
            secondTransport,
            cancellationToken: timeout.Token);
        JsonElement secondStatus = RequireSuccess(await secondClient.CallToolAsync(
            "aviutl_get_status",
            CreateInstanceArguments(harness.InstanceId),
            cancellationToken: timeout.Token));
        Assert.AreEqual(
            harness.InstanceId,
            secondStatus.GetProperty("data").GetProperty("selectedInstance").GetGuid());

        JsonElement capabilities = RequireSuccess(await client.CallToolAsync(
            "aviutl_get_capabilities",
            CreateInstanceArguments(harness.InstanceId),
            cancellationToken: timeout.Token));
        Assert.AreEqual(
            8,
            capabilities.GetProperty("data").GetProperty("limits").GetProperty("bridgeConnections").GetInt32());
        AssertAviUtl212Compatibility(capabilities);
        Assert.AreEqual(1920, project.GetProperty("data").GetProperty("width").GetInt32());
        Assert.AreEqual(1080, project.GetProperty("data").GetProperty("height").GetInt32());
        int contentFrame = project.GetProperty("data").GetProperty("currentFrame").GetInt32();
        Assert.IsGreaterThanOrEqualTo(1, contentFrame);

        JsonElement timeline = RequireSuccess(await client.CallToolAsync(
            "aviutl_get_timeline",
            new Dictionary<string, object?>
            {
                ["instanceId"] = harness.InstanceId,
                ["limit"] = 100,
                ["timeoutMs"] = 60_000,
            },
            cancellationToken: timeout.Token));
        Assert.IsGreaterThan(
            0,
            timeline.GetProperty("data").GetProperty("objects").GetArrayLength());

        (JsonElement BlankEnvelope, byte[] BlankPng) blank = await RenderPreviewAsync(
            client,
            harness.InstanceId,
            frame: 1,
            timeout.Token);
        (JsonElement ContentEnvelope, byte[] ContentPng) content = await RenderPreviewAsync(
            client,
            harness.InstanceId,
            contentFrame,
            timeout.Token);
        string expectedPreviewHash = content.ContentEnvelope
            .GetProperty("data")
            .GetProperty("sha256")
            .GetString()!;
        Assert.AreEqual(expectedPreviewHash, CalculateSha256(content.ContentPng));
        BeforeAfterVerification visualDifference = BeforeAfterVerifier.Verify(
            new Revision("real-preview-before"),
            new Revision("real-preview-after"),
            blank.BlankPng,
            content.ContentPng);
        Assert.IsTrue(visualDifference.Preview.IsDifferent);

        JsonElement diagnostics = RequireSuccess(await client.CallToolAsync(
            "aviutl_diagnose",
            new Dictionary<string, object?>
            {
                ["instanceId"] = harness.InstanceId,
                ["includeReadSmoke"] = true,
                ["includePreviewSmoke"] = true,
                ["maxLogLines"] = 500,
                ["timeoutMs"] = 60_000,
            },
            cancellationToken: timeout.Token));
        JsonElement diagnosticData = diagnostics.GetProperty("data");
        CollectionAssert.Contains(
            VALID_DIAGNOSTIC_STATUSES,
            diagnosticData.GetProperty("status").GetString());
        JsonElement[] knownLogMatches = diagnosticData
            .GetProperty("knownLogMatches")
            .EnumerateArray()
            .ToArray();
        AssertDiagnosticStatus(
            diagnostics,
            "known-logs",
            knownLogMatches.Length == 0 ? "pass" : "warning");
        foreach (JsonElement knownLogMatch in knownLogMatches)
        {
            Assert.AreEqual("warning", knownLogMatch.GetProperty("severity").GetString());
        }
        AssertDiagnosticPassed(diagnostics, "read-smoke");
        AssertDiagnosticPassed(diagnostics, "preview-smoke");

        Guid previewCorrelationId = content.ContentEnvelope.GetProperty("correlationId").GetGuid();
        string revision = content.ContentEnvelope.GetProperty("revision").GetString()!;
        string reportPath = await CreateDebugReportAsync(
            client,
            harness,
            capabilities,
            previewCorrelationId,
            revision,
            content.ContentPng,
            expectedPreviewHash,
            timeout.Token);
        Assert.IsTrue(File.Exists(reportPath));
        using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(
            reportPath,
            timeout.Token));
        Assert.AreEqual(
            previewCorrelationId,
            report.RootElement.GetProperty("correlationId").GetGuid());
        Assert.AreEqual(
            expectedPreviewHash,
            report.RootElement
                .GetProperty("previews")
                .GetProperty("after")
                .GetProperty("sha256")
                .GetString());
        }
        catch (Exception exception)
        {
            harness.RecordFailure(exception);
            throw;
        }
    }

    [TestMethod]
    [TestCategory("RealAviUtl2")]
    [TestProperty("TestId", "real.psd-setup")]
    [Timeout(180_000)]
    public async Task RealAviUtlCreatesPsdSetupInIsolatedFixture()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive("Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(timeout.Token);
        harness.RecordAcceptanceTestIds(
            "real.psd-setup",
            "real.fixture-process-guard");
        try
        {
            InstanceDescriptorWatcher watcher = new(harness.InstanceDirectory);
            BridgeConnectionFactory connectionFactory = new(Guid.NewGuid(), "0.1.0-real-test");
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);
            BridgeQueryGateway query = new(registry);
            BridgeEditGateway edit = new(registry);
            BridgePsdGateway psd = new(registry);

            GatewayResponse<ProjectData> project = await WaitForProjectAsync(
                query,
                harness.InstanceId,
                timeout.Token);
            Assert.IsTrue(project.Ok, project.Error?.Message);
            Assert.IsNotNull(project.Revision);
            Revision beforeRevision = project.Revision.Value;

            GatewayResponse<ObjectsPageData> existingSetups = await query.FindObjectsAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new FindObjectsInput
                    {
                        EffectName = "最初に置くやつ@PSDToolKit",
                        Limit = 100,
                    }),
                timeout.Token);
            Assert.IsTrue(existingSetups.Ok, existingSetups.Error?.Message);
            foreach (ObjectSummary existingSetup in existingSetups.Data!.Objects)
            {
                DeleteObjectInput deleteInput = new()
                {
                    ExpectedRevision = beforeRevision,
                    Locator = existingSetup.Locator,
                };
                GatewayResponse<DeleteData> deleted =
                    await edit.ExecuteEditAsync<DeleteObjectInput, DeleteData>(
                        "object.delete",
                        CreateGatewayRequest(
                            harness.InstanceId,
                            deleteInput,
                            beforeRevision),
                        timeout.Token);
                Assert.IsTrue(deleted.Ok, deleted.Error?.Message);
                Assert.AreEqual(true, deleted.Data!.Deleted);
                Assert.IsNotNull(deleted.Revision);
                beforeRevision = deleted.Revision.Value;
            }

            PsdSetupInput parameters = new()
            {
                ExpectedRevision = beforeRevision,
                CreateIfMissing = true,
                DryRun = true,
            };
            GatewayResponse<PsdSetupData> dryRun = await psd.ExecutePsdAsync<PsdSetupInput, PsdSetupData>(
                "psd.setup",
                CreateGatewayRequest(
                    harness.InstanceId,
                    parameters,
                    beforeRevision,
                    dryRun: true),
                timeout.Token);
            Assert.IsTrue(dryRun.Ok, dryRun.Error?.Message);
            Assert.IsFalse(dryRun.Data!.Created);
            Assert.AreEqual(PsdPlacementStatus.Missing, dryRun.Data.PlacementStatus);
            Assert.HasCount(1, dryRun.Data.PlannedChanges!);
            Assert.AreEqual(beforeRevision, dryRun.Revision);

            parameters = parameters with { DryRun = false };
            GatewayResponse<PsdSetupData> created = await psd.ExecutePsdAsync<PsdSetupInput, PsdSetupData>(
                "psd.setup",
                CreateGatewayRequest(
                    harness.InstanceId,
                    parameters,
                    beforeRevision,
                    dryRun: false),
                timeout.Token);
            Assert.IsTrue(created.Ok, DescribeGatewayFailure(created));
            Assert.IsTrue(created.Data!.Created);
            Assert.AreEqual(PsdPlacementStatus.Valid, created.Data.PlacementStatus);
            Assert.HasCount(1, created.Data.Objects);
            Assert.IsTrue(created.Data.Objects[0].Effects.Any(effect =>
                effect.Name == "最初に置くやつ@PSDToolKit"));
            Assert.AreNotEqual(beforeRevision, created.Revision);

            GatewayResponse<ObjectsPageData> found = await query.FindObjectsAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new FindObjectsInput
                    {
                        SceneId = created.Data.Objects[0].SceneId,
                        EffectName = "最初に置くやつ@PSDToolKit",
                        Limit = 100,
                    }),
                timeout.Token);
            Assert.IsTrue(found.Ok, found.Error?.Message);
            Assert.HasCount(1, found.Data!.Objects);
        }
        catch (Exception exception)
        {
            harness.RecordFailure(exception);
            throw;
        }
    }

    [TestMethod]
    [TestCategory("RealAviUtl2")]
    [TestProperty("TestId", "real.psd-character-layer")]
    [Timeout(180_000)]
    public async Task RealAviUtlRoundTripsPsdCharacterAndLayerInIsolatedFixture()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive("Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(timeout.Token);
        harness.RecordAcceptanceTestIds(
            "real.psd-character-layer",
            "real.fixture-process-guard");
        try
        {
            InstanceDescriptorWatcher watcher = new(harness.InstanceDirectory);
            BridgeConnectionFactory connectionFactory = new(Guid.NewGuid(), "0.1.0-real-test");
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);
            BridgeQueryGateway query = new(registry);
            BridgePsdGateway psd = new(registry);

            GatewayResponse<ProjectData> project = await WaitForProjectAsync(
                query,
                harness.InstanceId,
                timeout.Token);
            Assert.IsTrue(project.Ok, project.Error?.Message);
            Assert.IsNotNull(project.Revision);
            Revision beforeRevision = project.Revision.Value;

            GatewayResponse<ObjectsPageData> found = await query.FindObjectsAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new FindObjectsInput
                    {
                        EffectName = "PSDファイル@PSDToolKit",
                        Limit = 100,
                    }),
                timeout.Token);
            Assert.IsTrue(found.Ok, found.Error?.Message);
            ObjectSummary target = found.Data!.Objects.Single();

            const string characterId = "aviutl2-mcp-real";
            PsdSetCharacterInput characterParameters = new()
            {
                ExpectedRevision = beforeRevision,
                Locator = target.Locator,
                CharacterId = characterId,
                DryRun = true,
            };
            GatewayResponse<PsdCharacterData> characterDryRun =
                await psd.ExecutePsdAsync<PsdSetCharacterInput, PsdCharacterData>(
                    "psd.setCharacter",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        characterParameters,
                        beforeRevision,
                        dryRun: true),
                    timeout.Token);
            Assert.IsTrue(characterDryRun.Ok, characterDryRun.Error?.Message);
            Assert.AreEqual(characterId, characterDryRun.Data!.CharacterId);
            Assert.HasCount(1, characterDryRun.Data.PlannedChanges!);
            Assert.AreEqual(beforeRevision, characterDryRun.Revision);

            characterParameters = characterParameters with { DryRun = false };
            GatewayResponse<PsdCharacterData> character =
                await psd.ExecutePsdAsync<PsdSetCharacterInput, PsdCharacterData>(
                    "psd.setCharacter",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        characterParameters,
                        beforeRevision),
                    timeout.Token);
            Assert.IsTrue(character.Ok, character.Error?.Message);
            Assert.AreEqual(characterId, character.Data!.CharacterId);
            Assert.AreEqual(characterId, character.Data.Item!.Value?.GetString());
            Assert.IsNotNull(character.Data.TimelineObject);
            Assert.AreNotEqual(beforeRevision, character.Revision);

            Revision characterRevision = character.Revision!.Value;
            PsdSetLayerStateInput layerParameters = new()
            {
                ExpectedRevision = characterRevision,
                Locator = character.Data.TimelineObject!.Locator,
                LayerState = "L.0",
            };
            GatewayResponse<PsdLayerStateData> layer =
                await psd.ExecutePsdAsync<PsdSetLayerStateInput, PsdLayerStateData>(
                    "psd.setLayerState",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        layerParameters,
                        characterRevision),
                    timeout.Token);
            Assert.IsTrue(layer.Ok, layer.Error?.Message);
            Assert.AreEqual("L.0", layer.Data!.LayerState);
            Assert.AreEqual(true, layer.Data.RoundTripMatched);
            Assert.IsNotNull(layer.Data.TimelineObject);
            Assert.AreNotEqual(characterRevision, layer.Revision);

            GatewayResponse<PsdValidateData> validation = await psd.ValidatePsdAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new PsdValidateInput
                    {
                        Locator = layer.Data.TimelineObject!.Locator,
                        Scope = PsdValidationScope.SingleObject,
                        Checks = [PsdValidationCheck.Character],
                    }),
                timeout.Token);
            Assert.IsTrue(validation.Ok, validation.Error?.Message);
            Assert.AreEqual("ptk2-2.0.0alpha10-ja", validation.Data!.Profile);
            Assert.HasCount(1, validation.Data.Checks);
            Assert.AreEqual(DiagnosticCheckStatus.Pass, validation.Data.Checks[0].Status);
        }
        catch (Exception exception)
        {
            harness.RecordFailure(exception);
            throw;
        }
    }

    [TestMethod]
    [TestCategory("RealAviUtl2Gcmz")]
    [TestProperty("TestId", "real.psd-create")]
    [Timeout(180_000)]
    public async Task RealAviUtlCreatesPsdThroughGcmzInIsolatedFixture()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive("Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(timeout.Token);
        harness.RecordAcceptanceTestIds(
            "real.psd-create",
            "real.fixture-process-guard");
        try
        {
            InstanceDescriptorWatcher watcher = new(harness.InstanceDirectory);
            BridgeConnectionFactory connectionFactory = new(Guid.NewGuid(), "0.1.0-real-test");
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);
            BridgeQueryGateway query = new(registry);
            BridgePsdGateway psd = new(registry);

            GatewayResponse<ProjectData> project = await WaitForProjectAsync(
                query,
                harness.InstanceId,
                timeout.Token);
            Assert.IsTrue(project.Ok, project.Error?.Message);
            Assert.IsNotNull(project.Revision);
            Revision beforeRevision = project.Revision.Value;
            (ObjectSummary _, ObjectData _, string psdPath) = await GetSinglePsdObjectAsync(
                query,
                harness.InstanceId,
                timeout.Token);

            PsdCreateInput parameters = new()
            {
                ExpectedRevision = beforeRevision,
                PsdPath = psdPath,
                Placement = new Placement(
                    project.Data!.CurrentSceneId,
                    Layer: 10,
                    StartFrame: 100,
                    DurationFrames: 60),
                Name = "AviUtl2 MCP PSD create E2E",
                DryRun = true,
            };
            GatewayResponse<CreateObjectData> dryRun =
                await psd.ExecutePsdAsync<PsdCreateInput, CreateObjectData>(
                    "psd.create",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        parameters,
                        beforeRevision,
                        dryRun: true),
                    timeout.Token);
            Assert.IsTrue(dryRun.Ok, dryRun.Error?.Message);
            Assert.IsNull(dryRun.Data!.TimelineObject);
            Assert.HasCount(1, dryRun.Data.PlannedChanges!);
            Assert.AreEqual(beforeRevision, dryRun.Revision);

            parameters = parameters with { DryRun = false };
            GatewayResponse<CreateObjectData> created =
                await psd.ExecutePsdAsync<PsdCreateInput, CreateObjectData>(
                    "psd.create",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        parameters,
                        beforeRevision),
                    timeout.Token);
            Assert.IsTrue(created.Ok, DescribeGatewayFailure(created));
            Assert.IsNotNull(created.Data!.TimelineObject);
            Assert.AreEqual("AviUtl2 MCP PSD create E2E", created.Data.TimelineObject.Name);
            Assert.AreEqual(10, created.Data.TimelineObject.Layer);
            Assert.AreEqual(100, created.Data.TimelineObject.StartFrame);
            Assert.IsTrue(created.Data.TimelineObject.Effects.Any(
                effect => effect.Name == "PSDファイル@PSDToolKit"));
            Assert.HasCount(1, created.Data.AppliedChanges!);
            Assert.AreNotEqual(beforeRevision, created.Revision);

            GatewayResponse<ObjectData> verified = await query.GetObjectAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new GetObjectInput
                    {
                        Locator = created.Data.TimelineObject.Locator,
                        IncludeEffectItems = true,
                    }),
                timeout.Token);
            Assert.IsTrue(verified.Ok, verified.Error?.Message);
            EffectItemsGroup psdItems = verified.Data!.EffectItems.Single(
                group => group.Effect.Name == "PSDファイル@PSDToolKit");
            EffectItem pathItem = psdItems.Items.Single(item => item.Name == "PSDファイル");
            Assert.IsTrue(pathItem.Value.HasValue);
            Assert.IsTrue(string.Equals(
                Path.GetFullPath(psdPath),
                Path.GetFullPath(pathItem.Value.Value.GetString()!),
                StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            harness.RecordFailure(exception);
            throw;
        }
    }

    [TestMethod]
    [TestCategory("RealAviUtl2Gcmz")]
    [TestProperty("TestId", "real.psd-voice-subtitle")]
    [TestProperty("TestId", "real.psd-lipsync-lab")]
    [Timeout(180_000)]
    public async Task RealAviUtlCreatesVoiceSubtitleAndValidatesLabLipSync()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive("Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(
            timeout.Token,
            AddLipSyncLabEffect);
        harness.RecordAcceptanceTestIds(
            "real.psd-voice-subtitle",
            "real.psd-lipsync-lab",
            "real.fixture-process-guard");
        try
        {
            InstanceDescriptorWatcher watcher = new(harness.InstanceDirectory);
            BridgeConnectionFactory connectionFactory = new(Guid.NewGuid(), "0.1.0-real-test");
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);
            BridgeQueryGateway query = new(registry);
            BridgePsdGateway psd = new(registry);

            GatewayResponse<ProjectData> project = await WaitForProjectAsync(
                query,
                harness.InstanceId,
                timeout.Token);
            Assert.IsTrue(project.Ok, project.Error?.Message);
            Assert.IsNotNull(project.Revision);
            Revision beforeRevision = project.Revision.Value;
            (ObjectSummary psdSummary, ObjectData psdDetail, string _) =
                await GetSinglePsdObjectAsync(query, harness.InstanceId, timeout.Token);
            Assert.IsTrue(psdDetail.TimelineObject.Effects.Any(
                effect => effect.Name == "口パク あいうえお@PSDToolKit"));

            const string characterId = "aviutl2-mcp-voice-e2e";
            PsdSetCharacterInput characterParameters = new()
            {
                ExpectedRevision = beforeRevision,
                Locator = psdSummary.Locator,
                CharacterId = characterId,
            };
            GatewayResponse<PsdCharacterData> character =
                await psd.ExecutePsdAsync<PsdSetCharacterInput, PsdCharacterData>(
                    "psd.setCharacter",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        characterParameters,
                        beforeRevision),
                    timeout.Token);
            Assert.IsTrue(character.Ok, character.Error?.Message);
            Assert.IsNotNull(character.Revision);
            Assert.IsNotNull(character.Data!.TimelineObject);
            Assert.AreEqual(characterId, character.Data.CharacterId);

            (string audioPath, string textPath, string labPath) = CreateVoiceFixtureFiles(harness);
            Revision characterRevision = character.Revision.Value;
            PsdCreateVoiceInput voiceParameters = new()
            {
                ExpectedRevision = characterRevision,
                AudioPath = audioPath,
                TextPath = textPath,
                LabPath = labPath,
                CharacterId = characterId,
                PsdLocator = character.Data.TimelineObject.Locator,
                Placement = new Placement(
                    project.Data!.CurrentSceneId,
                    Layer: 2,
                    StartFrame: 300,
                    DurationFrames: 30),
                DryRun = true,
            };
            GatewayResponse<PsdVoiceData> dryRun =
                await psd.ExecutePsdAsync<PsdCreateVoiceInput, PsdVoiceData>(
                    "psd.createVoice",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        voiceParameters,
                        characterRevision,
                        dryRun: true),
                    timeout.Token);
            Assert.IsTrue(dryRun.Ok, dryRun.Error?.Message);
            Assert.IsNull(dryRun.Data!.VoiceObjects);
            Assert.IsNull(dryRun.Data.SubtitleObjects);
            Assert.HasCount(3, dryRun.Data.PlannedChanges!);
            Assert.AreEqual(characterRevision, dryRun.Revision);
            Assert.AreEqual(Path.GetFullPath(labPath), dryRun.Data.CompanionFiles!.LabPath);

            voiceParameters = voiceParameters with { DryRun = false };
            GatewayResponse<PsdVoiceData> created =
                await psd.ExecutePsdAsync<PsdCreateVoiceInput, PsdVoiceData>(
                    "psd.createVoice",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        voiceParameters,
                        characterRevision),
                    timeout.Token);
            Assert.IsTrue(created.Ok, DescribeGatewayFailure(created));
            Assert.IsNotNull(created.Data!.VoiceObjects);
            Assert.IsNotNull(created.Data.SubtitleObjects);
            Assert.HasCount(2, created.Data.VoiceObjects);
            Assert.HasCount(1, created.Data.SubtitleObjects);
            Assert.HasCount(3, created.Data.AppliedChanges!);
            Assert.AreNotEqual(characterRevision, created.Revision);
            Assert.AreEqual(Path.GetFullPath(audioPath), created.Data.CompanionFiles!.AudioPath);
            Assert.AreEqual(Path.GetFullPath(textPath), created.Data.CompanionFiles.TextPath);
            Assert.AreEqual(Path.GetFullPath(labPath), created.Data.CompanionFiles.LabPath);

            ObjectSummary prep = created.Data.VoiceObjects.Single(candidate =>
                candidate.Effects.Any(effect => effect.Name == "セリフ準備@PSDToolKit"));
            GatewayResponse<ObjectData> prepDetail = await query.GetObjectAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new GetObjectInput
                    {
                        Locator = prep.Locator,
                        IncludeEffectItems = true,
                    }),
                timeout.Token);
            Assert.IsTrue(prepDetail.Ok, prepDetail.Error?.Message);
            EffectItemsGroup prepItems = prepDetail.Data!.EffectItems.Single(
                group => group.Effect.Name == "セリフ準備@PSDToolKit");
            AssertEffectItemValue(prepItems, "キャラクターID", characterId);
            AssertEffectItemValue(prepItems, "テキスト", "AviUtl2 MCP 自動音声テスト");
            AssertEffectItemPath(prepItems, "音声ファイル", audioPath);

            ObjectSummary subtitle = created.Data.SubtitleObjects.Single();
            GatewayResponse<ObjectData> subtitleDetail = await query.GetObjectAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new GetObjectInput
                    {
                        Locator = subtitle.Locator,
                        IncludeAlias = true,
                        IncludeEffectItems = true,
                    }),
                timeout.Token);
            Assert.IsTrue(subtitleDetail.Ok, subtitleDetail.Error?.Message);
            Assert.IsNotNull(subtitleDetail.Data!.Alias);
            StringAssert.Contains(subtitleDetail.Data.Alias, "require(\"PSDToolKit\").mes");
            StringAssert.Contains(subtitleDetail.Data.Alias, characterId);

            GatewayResponse<PsdValidateData> validation = await psd.ValidatePsdAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new PsdValidateInput
                    {
                        Locator = character.Data.TimelineObject.Locator,
                        Scope = PsdValidationScope.SingleObject,
                        Checks =
                        [
                            PsdValidationCheck.Character,
                            PsdValidationCheck.LipSync,
                            PsdValidationCheck.Subtitle,
                        ],
                    }),
                timeout.Token);
            Assert.IsTrue(validation.Ok, validation.Error?.Message);
            Assert.HasCount(3, validation.Data!.Checks);
            Assert.IsTrue(validation.Data.Checks.All(
                check => check.Status == DiagnosticCheckStatus.Pass));
        }
        catch (Exception exception)
        {
            harness.RecordFailure(exception);
            throw;
        }
    }

    private static GatewayRequest<T> CreateGatewayRequest<T>(
        Guid instanceId,
        T parameters,
        Revision? expectedRevision = null,
        bool dryRun = false) =>
        new(
            instanceId,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddSeconds(60),
            60_000,
            expectedRevision,
        dryRun,
        parameters);

    private static string DescribeGatewayFailure<TData>(GatewayResponse<TData> response)
    {
        if (response.Error is null)
        {
            return "The Bridge operation failed without structured error details.";
        }
        return $"{response.Error.Message}\n"
            + $"code={response.Error.Code}; phase={response.Error.Phase}; "
            + $"outcome={response.Error.Outcome}; undoRecommended={response.Error.UndoRecommended}\n"
            + $"result={JsonSerializer.Serialize(response.Data)}\n"
            + $"details={response.Error.Details.GetRawText()}";
    }

    private static async Task<(ObjectSummary Summary, ObjectData Detail, string PsdPath)>
        GetSinglePsdObjectAsync(
            BridgeQueryGateway query,
            Guid instanceId,
            CancellationToken cancellationToken)
    {
        GatewayResponse<ObjectsPageData> found = await query.FindObjectsAsync(
            CreateGatewayRequest(
                instanceId,
                new FindObjectsInput
                {
                    EffectName = "PSDファイル@PSDToolKit",
                    Limit = 100,
                }),
            cancellationToken);
        Assert.IsTrue(found.Ok, found.Error?.Message);
        Assert.HasCount(1, found.Data!.Objects);
        ObjectSummary summary = found.Data.Objects.Single();
        GatewayResponse<ObjectData> detail = await query.GetObjectAsync(
            CreateGatewayRequest(
                instanceId,
                new GetObjectInput
                {
                    Locator = summary.Locator,
                    IncludeEffectItems = true,
                }),
            cancellationToken);
        Assert.IsTrue(detail.Ok, detail.Error?.Message);
        EffectItemsGroup psdItems = detail.Data!.EffectItems.Single(
            group => group.Effect.Name == "PSDファイル@PSDToolKit");
        EffectItem pathItem = psdItems.Items.Single(item => item.Name == "PSDファイル");
        Assert.IsTrue(pathItem.Value.HasValue);
        string psdPath = pathItem.Value.Value.GetString()!;
        Assert.IsTrue(Path.IsPathFullyQualified(psdPath));
        Assert.IsTrue(File.Exists(psdPath));
        return (summary, detail.Data, Path.GetFullPath(psdPath));
    }

    private static (string AudioPath, string TextPath, string LabPath)
        CreateVoiceFixtureFiles(RealAviUtlHarness harness)
    {
        string directory = Path.Combine(harness.RuntimeDirectory, "voice-fixture");
        Directory.CreateDirectory(directory);
        string audioPath = Path.Combine(directory, "voice.wav");
        string textPath = Path.Combine(directory, "voice.txt");
        string labPath = Path.Combine(directory, "voice.lab");
        const int sampleRate = 8_000;
        const int sampleCount = 8_000;
        const short channelCount = 1;
        const short bitsPerSample = 16;
        int dataSize = sampleCount * channelCount * bitsPerSample / 8;
        using (FileStream stream = new(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channelCount);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channelCount * bitsPerSample / 8);
            writer.Write((short)(channelCount * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }
        File.WriteAllText(
            textPath,
            "AviUtl2 MCP 自動音声テスト",
            new UTF8Encoding(false));
        File.WriteAllText(
            labPath,
            "0 5000000 sil\r\n5000000 10000000 a\r\n",
            new UTF8Encoding(false));
        return (audioPath, textPath, labPath);
    }

    private static void AssertEffectItemValue(
        EffectItemsGroup group,
        string itemName,
        string expectedValue)
    {
        EffectItem item = group.Items.Single(candidate => candidate.Name == itemName);
        Assert.IsTrue(item.Value.HasValue);
        Assert.AreEqual(expectedValue, item.Value.Value.GetString());
    }

    private static void AssertEffectItemPath(
        EffectItemsGroup group,
        string itemName,
        string expectedPath)
    {
        EffectItem item = group.Items.Single(candidate => candidate.Name == itemName);
        Assert.IsTrue(item.Value.HasValue);
        Assert.IsTrue(string.Equals(
            Path.GetFullPath(expectedPath),
            Path.GetFullPath(item.Value.Value.GetString()!),
            StringComparison.OrdinalIgnoreCase));
    }

    private static void AddLipSyncLabEffect(string projectPath)
    {
        UTF8Encoding encoding = new(false, true);
        string[] sourceLines = encoding.GetString(File.ReadAllBytes(projectPath))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        List<string> lines = [.. sourceLines];
        if (lines.Contains("effect.name=口パク あいうえお@PSDToolKit", StringComparer.Ordinal))
        {
            return;
        }

        int effectLineIndex = lines.FindIndex(
            line => line == "effect.name=PSDファイル@PSDToolKit");
        if (effectLineIndex < 0)
        {
            throw new InvalidDataException("The real GCMZDrops fixture requires one PSDToolKit2 PSD object.");
        }
        int sectionLineIndex = effectLineIndex - 1;
        while (sectionLineIndex >= 0 && !lines[sectionLineIndex].StartsWith('['))
        {
            --sectionLineIndex;
        }
        if (sectionLineIndex < 0)
        {
            throw new InvalidDataException("The PSDToolKit2 fixture effect has no object subsection.");
        }
        string section = lines[sectionLineIndex];
        int separatorIndex = section.IndexOf('.', StringComparison.Ordinal);
        if (separatorIndex <= 1 || !section.EndsWith(']'))
        {
            throw new InvalidDataException("The PSDToolKit2 fixture subsection is invalid.");
        }
        string objectId = section[1..separatorIndex];
        string subsectionPrefix = $"[{objectId}.";
        int maximumSubsection = -1;
        foreach (string line in lines)
        {
            if (!line.StartsWith(subsectionPrefix, StringComparison.Ordinal) || !line.EndsWith(']'))
            {
                continue;
            }
            string value = line[subsectionPrefix.Length..^1];
            if (int.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int subsection))
            {
                maximumSubsection = Math.Max(maximumSubsection, subsection);
            }
        }
        if (maximumSubsection < 0)
        {
            throw new InvalidDataException("The PSDToolKit2 fixture has no valid effect subsection.");
        }

        int insertionIndex = lines.Count;
        for (int index = sectionLineIndex + 1; index < lines.Count; index++)
        {
            string candidate = lines[index];
            if (candidate.Length > 2
                && candidate[0] == '['
                && candidate[^1] == ']'
                && !candidate.Contains('.', StringComparison.Ordinal)
                && int.TryParse(
                    candidate.AsSpan(1, candidate.Length - 2),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                insertionIndex = index;
                break;
            }
        }
        string[] lipSyncSection =
        [
            $"[{objectId}.{maximumSubsection + 1}]",
            "effect.name=口パク あいうえお@PSDToolKit",
            "あ~ptkl=",
            "い~ptkl=",
            "う~ptkl=",
            "え~ptkl=",
            "お~ptkl=",
            "ん~ptkl=",
            "子音処理=1",
            "発声がなくても有効=1",
        ];
        lines.InsertRange(insertionIndex, lipSyncSection);
        File.WriteAllText(projectPath, string.Join("\r\n", lines), encoding);
    }

    private static async Task<(JsonElement Envelope, byte[] Png)> RenderPreviewAsync(
        McpClient client,
        Guid instanceId,
        int frame,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            "aviutl_render_preview",
            new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId,
                ["frame"] = frame,
                ["maxWidth"] = 640,
                ["maxHeight"] = 360,
                ["includeAlpha"] = true,
                ["timeoutMs"] = 60_000,
            },
            cancellationToken: cancellationToken);
        JsonElement envelope = RequireSuccess(result);
        ImageContentBlock image = result.Content.OfType<ImageContentBlock>().Single();
        Assert.AreEqual("image/png", image.MimeType);
        byte[] png = image.DecodedData.ToArray();
        Assert.IsGreaterThan(PNG_SIGNATURE.Length, png.Length);
        CollectionAssert.AreEqual(PNG_SIGNATURE, png[..PNG_SIGNATURE.Length]);
        Assert.AreEqual(
            png.Length,
            envelope.GetProperty("data").GetProperty("byteLength").GetInt32());
        return (envelope, png);
    }

    private static void AssertRequiredComponentsReady(JsonElement status)
    {
        JsonElement[] components = status
            .GetProperty("data")
            .GetProperty("components")
            .EnumerateArray()
            .ToArray();
        foreach (string componentName in REQUIRED_READY_COMPONENTS)
        {
            JsonElement component = components.Single(candidate =>
                candidate.GetProperty("name").GetString() == componentName);
            Assert.AreEqual(
                "ready",
                component.GetProperty("status").GetString(),
                $"Component {componentName} was not ready on the tested AviUtl2 build: "
                    + component.GetRawText());
        }
    }

    private static void AssertAviUtl212Compatibility(JsonElement capabilities)
    {
        JsonElement data = capabilities.GetProperty("data");
        JsonElement versions = data.GetProperty("versions");
        string? aviUtlVersionText = versions.GetProperty("aviutl").GetString();
        Assert.IsNotNull(aviUtlVersionText);
        Assert.IsTrue(
            uint.TryParse(
                aviUtlVersionText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out uint aviUtlVersion),
            $"AviUtl2 version was not numeric: {aviUtlVersionText}");
        Assert.IsGreaterThanOrEqualTo(MINIMUM_TESTED_AVIUTL_VERSION, aviUtlVersion);
        Assert.AreEqual("2010300", versions.GetProperty("sdk").GetString());

        JsonElement[] operations = data.GetProperty("operations").EnumerateArray().ToArray();
        Assert.AreEqual(33, operations.Length);
        int availableOperationCount = 0;
        foreach (JsonElement operation in operations)
        {
            if (operation.GetProperty("available").GetBoolean())
            {
                ++availableOperationCount;
                continue;
            }
            string operationName = operation.GetProperty("name").GetString()!;
            CollectionAssert.Contains(
                GCMZ_EXCLUSIVE_OPERATION_NAMES,
                operationName,
                $"Core operation {operationName} was unavailable on AviUtl2 {aviUtlVersionText}.");
            Assert.AreEqual(
                "gcmzdrops_not_available",
                operation.GetProperty("reason").GetString());
        }
        Assert.IsGreaterThanOrEqualTo(27, availableOperationCount);
    }

    private static async Task<JsonElement> WaitForProjectAsync(
        McpClient client,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        CallToolResult? lastResult = null;
        do
        {
            lastResult = await client.CallToolAsync(
                "aviutl_get_project",
                new Dictionary<string, object?>
                {
                    ["instanceId"] = instanceId,
                    ["includeScenes"] = true,
                    ["timeoutMs"] = 60_000,
                },
                cancellationToken: cancellationToken);
            if (lastResult.IsError != true)
            {
                return RequireSuccess(lastResult);
            }
            if (!lastResult.StructuredContent.HasValue
                || lastResult.StructuredContent.Value
                    .GetProperty("error")
                    .GetProperty("code")
                    .GetString() != "project_not_open")
            {
                return RequireSuccess(lastResult);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return RequireSuccess(lastResult!);
    }

    private static async Task<GatewayResponse<ProjectData>> WaitForProjectAsync(
        BridgeQueryGateway query,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        GatewayResponse<ProjectData>? lastResponse = null;
        do
        {
            lastResponse = await query.GetProjectAsync(
                CreateGatewayRequest(instanceId, new GetProjectInput()),
                cancellationToken);
            if (lastResponse.Ok || lastResponse.Error?.Code != "project_not_open")
            {
                return lastResponse;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return lastResponse!;
    }

    private static async Task<JsonElement> WaitForRequiredComponentsReadyAsync(
        McpClient client,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        JsonElement lastStatus;
        do
        {
            lastStatus = RequireSuccess(await client.CallToolAsync(
                "aviutl_get_status",
                CreateInstanceArguments(instanceId),
                cancellationToken: cancellationToken));
            JsonElement[] components = lastStatus
                .GetProperty("data")
                .GetProperty("components")
                .EnumerateArray()
                .ToArray();
            bool areRequiredComponentsReady = REQUIRED_READY_COMPONENTS.All(componentName =>
                components.Any(component =>
                    component.GetProperty("name").GetString() == componentName
                    && component.GetProperty("status").GetString() == "ready"));
            if (areRequiredComponentsReady)
            {
                return lastStatus;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        AssertRequiredComponentsReady(lastStatus);
        return lastStatus;
    }

    private static async Task<string> CreateDebugReportAsync(
        McpClient client,
        RealAviUtlHarness harness,
        JsonElement capabilities,
        Guid correlationId,
        string revision,
        byte[] previewPng,
        string previewSha256,
        CancellationToken cancellationToken)
    {
        string evidenceDirectory = Path.Combine(harness.RuntimeDirectory, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        string previewPath = Path.Combine(evidenceDirectory, "preview.png");
        await File.WriteAllBytesAsync(previewPath, previewPng, cancellationToken);
        Dictionary<string, string> logPaths = [];
        foreach (string source in new[] { "server", "bridge", "aviutl" })
        {
            JsonElement logs = RequireSuccess(await client.CallToolAsync(
                "aviutl_get_logs",
                new Dictionary<string, object?>
                {
                    ["instanceId"] = harness.InstanceId,
                    ["sources"] = new[] { source },
                    ["correlationId"] = correlationId,
                    ["limit"] = 2000,
                    ["timeoutMs"] = 60_000,
                },
                cancellationToken: cancellationToken));
            string path = Path.Combine(evidenceDirectory, $"{source}.jsonl");
            string[] lines = logs.GetProperty("data").GetProperty("entries")
                .EnumerateArray()
                .Select(entry => entry.GetRawText())
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false), cancellationToken);
            logPaths.Add(source, path);
        }

        string checksPath = Path.Combine(evidenceDirectory, "checks.json");
        object[] checks =
        [
            new
            {
                name = "real.timeline-read",
                status = "pass",
                evidence = new[] { $"instanceId={harness.InstanceId:D}", $"revision={revision}" },
            },
            new
            {
                name = "real.preview-image",
                status = "pass",
                evidence = new[] { $"previewSha256={previewSha256}", $"byteLength={previewPng.Length}" },
            },
            new
            {
                name = "real.diagnostic-smoke",
                status = "pass",
                evidence = new[] { $"correlationId={correlationId:D}" },
            },
        ];
        await File.WriteAllTextAsync(
            checksPath,
            JsonSerializer.Serialize(checks),
            new UTF8Encoding(false),
            cancellationToken);
        string versionsPath = Path.Combine(evidenceDirectory, "versions.json");
        await File.WriteAllTextAsync(
            versionsPath,
            capabilities.GetProperty("data").GetProperty("versions").GetRawText(),
            new UTF8Encoding(false),
            cancellationToken);

        string repositoryRoot = GetRequiredEnvironmentPath("AVIUTL2_MCP_REPOSITORY_ROOT", true);
        string reportRoot = Path.Combine(repositoryRoot, "artifacts", "real-e2e");
        string scriptPath = Path.Combine(repositoryRoot, "scripts", "New-DebugReport.ps1");
        ProcessStartInfo startInfo = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        string[] arguments =
        [
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "-CorrelationId",
            correlationId.ToString("D"),
            "-OutputDirectory",
            reportRoot,
            "-Command",
            "real.read-preview-diagnose",
            "-BeforeRevision",
            revision,
            "-AfterRevision",
            revision,
            "-AfterPreviewPath",
            previewPath,
            "-ServerLogPath",
            logPaths["server"],
            "-BridgeLogPath",
            logPaths["bridge"],
            "-AviUtlLogPath",
            logPaths["aviutl"],
            "-ArtifactPath",
            previewPath,
            "-ChecksPath",
            checksPath,
            "-ComponentVersionsPath",
            versionsPath,
            "-LaunchedProcessId",
            harness.LaunchedProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-RepositoryRoot",
            repositoryRoot,
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The debug-report generator did not start.");
        string standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Debug-report generation failed ({process.ExitCode}): {standardError}{standardOutput}");
        }
        return Path.Combine(reportRoot, correlationId.ToString("D"), "debug-report.json");
    }

    private static JsonElement RequireSuccess(CallToolResult result)
    {
        string diagnostic = result.StructuredContent.HasValue
            ? result.StructuredContent.Value.GetRawText()
            : string.Join(
                Environment.NewLine,
                result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.IsFalse(result.IsError, diagnostic);
        Assert.IsTrue(result.StructuredContent.HasValue, diagnostic);
        JsonElement envelope = result.StructuredContent.Value;
        Assert.IsTrue(envelope.GetProperty("ok").GetBoolean(), diagnostic);
        return envelope;
    }

    private static void AssertDiagnosticPassed(JsonElement diagnostics, string checkId)
    {
        AssertDiagnosticStatus(diagnostics, checkId, "pass");
    }

    private static void AssertDiagnosticStatus(
        JsonElement diagnostics,
        string checkId,
        string expectedStatus)
    {
        JsonElement check = diagnostics.GetProperty("data").GetProperty("checks")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("checkId").GetString() == checkId);
        Assert.AreEqual(expectedStatus, check.GetProperty("status").GetString());
        Assert.IsGreaterThan(0, check.GetProperty("evidence").GetArrayLength());
    }

    private static Dictionary<string, object?> CreateInstanceArguments(Guid instanceId) =>
        new()
        {
            ["instanceId"] = instanceId,
            ["timeoutMs"] = 60_000,
        };

    private static string GetRequiredEnvironmentPath(string variableName, bool isDirectory)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{variableName} is required.");
        }
        string path = Path.GetFullPath(value);
        if (isDirectory ? !Directory.Exists(path) : !File.Exists(path))
        {
            throw new InvalidOperationException($"{variableName} does not identify the expected path.");
        }
        return path;
    }

    private static string CalculateSha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}

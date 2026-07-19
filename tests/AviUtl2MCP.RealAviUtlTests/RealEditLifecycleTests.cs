using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Gateways;

namespace AviUtl2MCP.RealAviUtlTests;

[TestClass]
public sealed class RealEditLifecycleTests
{
    private static readonly string[] EXPECTED_BATCH_OPERATION_IDS =
        ["create-batch-object", "set-batch-layer"];

    [TestMethod]
    [TestCategory("RealAviUtl2")]
    [TestProperty("TestId", "real.object-create-three-ways")]
    [TestProperty("TestId", "real.object-edit-lifecycle")]
    [Timeout(240_000)]
    public async Task RealAviUtlCreatesEditsBatchesAndDeletesIsolatedObjects()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive(
                "Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }

        string? mediaPath = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(
            timeout.Token,
            fixtureProjectPath =>
            {
                mediaPath = Path.Combine(
                    Path.GetDirectoryName(fixtureProjectPath)!,
                    "mcp-real-silence.wav");
                CreateSilentWave(mediaPath);
            });
        try
        {
            InstanceDescriptorWatcher watcher = new(GetDescriptorDirectory());
            BridgeConnectionFactory connectionFactory = new(Guid.NewGuid(), "0.1.0-real-test");
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);
            BridgeQueryGateway query = new(registry);
            BridgeEditGateway edit = new(registry);
            BridgePreviewGateway preview = new(registry);

            GatewayResponse<ProjectData> project = await WaitForProjectAsync(
                query,
                harness.InstanceId,
                timeout.Token);
            Assert.IsTrue(project.Ok, project.Error?.Message);
            Assert.IsNotNull(project.Revision);
            Revision revision = project.Revision.Value;
            int editFrame = project.Data!.CurrentFrame;
            Assert.IsGreaterThanOrEqualTo(1, editFrame);
            harness.RecordRevision(revision.Value);

            GatewayResponse<EffectsData> effects = await query.ListEffectsAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new ListEffectsInput { Limit = 1_000 }),
                timeout.Token);
            Assert.IsTrue(effects.Ok, effects.Error?.Message);
            EffectDefinition textEffect = effects.Data!.Effects.Single(candidate =>
                candidate.Name == "テキスト" && candidate.IsCreatable);

            string setupAlias = await GetSetupAliasAsync(
                query,
                harness.InstanceId,
                timeout.Token);
            (string beforePreviewPath, string beforePreviewHash) =
                await SavePreviewAsync(
                    preview,
                    harness,
                    frame: editFrame,
                    "preview-before.png",
                    timeout.Token);
            const string dryRunName = "aviutl2-mcp-real-dry-run";
            CreateObjectInput dryRunInput = new()
            {
                ExpectedRevision = revision,
                DryRun = true,
                Effect = new EffectDefinitionSelector(textEffect.Name),
                Placement = new Placement(0, 30, editFrame, DurationFrames: 30),
                Name = dryRunName,
            };
            GatewayResponse<CreateObjectData> dryRun =
                await edit.ExecuteEditAsync<CreateObjectInput, CreateObjectData>(
                    "object.create",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        dryRunInput,
                        revision,
                        dryRun: true),
                    timeout.Token);
            Assert.IsTrue(dryRun.Ok, dryRun.Error?.Message);
            Assert.IsNull(dryRun.Data!.TimelineObject);
            Assert.HasCount(1, dryRun.Data.PlannedChanges!);
            Assert.AreEqual(revision, dryRun.Revision);
            Assert.AreEqual(
                0,
                (await FindByNameAsync(query, harness.InstanceId, dryRunName, timeout.Token)).Count);

            const string effectName = "aviutl2-mcp-real-effect";
            CreateObjectInput createEffect = dryRunInput with
            {
                DryRun = false,
                Name = effectName,
            };
            GatewayResponse<CreateObjectData> createdEffect =
                await edit.ExecuteEditAsync<CreateObjectInput, CreateObjectData>(
                    "object.create",
                    CreateGatewayRequest(harness.InstanceId, createEffect, revision),
                    timeout.Token);
            Assert.IsTrue(createdEffect.Ok, createdEffect.Error?.Message);
            Assert.IsNotNull(createdEffect.Data!.TimelineObject);
            Assert.AreEqual(effectName, createdEffect.Data.TimelineObject.Name);
            Assert.IsTrue(createdEffect.Data.TimelineObject.Effects.Any(effect =>
                effect.Name == textEffect.Name));
            revision = RequireChangedRevision(harness, revision, createdEffect.Revision);

            const string mediaName = "aviutl2-mcp-real-media";
            CreateMediaObjectInput createMedia = new()
            {
                ExpectedRevision = revision,
                MediaPath = mediaPath!,
                Placement = new Placement(0, 31, editFrame, DurationFrames: 30),
                Name = mediaName,
            };
            GatewayResponse<CreateObjectData> createdMedia =
                await edit.ExecuteEditAsync<CreateMediaObjectInput, CreateObjectData>(
                    "object.createMedia",
                    CreateGatewayRequest(harness.InstanceId, createMedia, revision),
                    timeout.Token);
            Assert.IsTrue(createdMedia.Ok, createdMedia.Error?.Message);
            Assert.IsNotNull(createdMedia.Data!.TimelineObject);
            Assert.AreEqual(mediaName, createdMedia.Data.TimelineObject.Name);
            Assert.AreEqual(Path.GetFullPath(mediaPath!), createdMedia.Data.TimelineObject.MediaPath);
            revision = RequireChangedRevision(harness, revision, createdMedia.Revision);

            const string aliasName = "aviutl2-mcp-real-alias";
            CreateAliasObjectInput createAlias = new()
            {
                ExpectedRevision = revision,
                Alias = setupAlias,
                Placement = new Placement(0, 32, editFrame, DurationFrames: 30),
                Name = aliasName,
            };
            GatewayResponse<CreateObjectsData> createdAlias =
                await edit.ExecuteEditAsync<CreateAliasObjectInput, CreateObjectsData>(
                    "object.createAlias",
                    CreateGatewayRequest(harness.InstanceId, createAlias, revision),
                    timeout.Token);
            Assert.IsTrue(createdAlias.Ok, createdAlias.Error?.Message);
            Assert.HasCount(1, createdAlias.Data!.Objects!);
            Assert.AreEqual(aliasName, createdAlias.Data.Objects![0].Name);
            revision = RequireChangedRevision(harness, revision, createdAlias.Revision);

            Assert.HasCount(
                1,
                await FindByNameAsync(query, harness.InstanceId, effectName, timeout.Token));
            Assert.HasCount(
                1,
                await FindByNameAsync(query, harness.InstanceId, mediaName, timeout.Token));
            Assert.HasCount(
                1,
                await FindByNameAsync(query, harness.InstanceId, aliasName, timeout.Token));

            ObjectSummary lifecycleObject = createdEffect.Data.TimelineObject;
            Revision beforeRename = revision;
            const string renamed = "aviutl2-mcp-real-renamed";
            SetObjectNameInput renameInput = new()
            {
                ExpectedRevision = revision,
                Locator = lifecycleObject.Locator,
                Name = renamed,
            };
            GatewayResponse<UpdatedObjectData> rename =
                await edit.ExecuteEditAsync<SetObjectNameInput, UpdatedObjectData>(
                    "object.setName",
                    CreateGatewayRequest(harness.InstanceId, renameInput, revision),
                    timeout.Token);
            Assert.IsTrue(rename.Ok, rename.Error?.Message);
            Assert.AreEqual(renamed, rename.Data!.TimelineObject!.Name);
            lifecycleObject = rename.Data.TimelineObject;
            revision = RequireChangedRevision(harness, revision, rename.Revision);

            MoveObjectInput staleMoveInput = new()
            {
                ExpectedRevision = beforeRename,
                Locator = lifecycleObject.Locator,
                Placement = new MovePlacement(0, 33, editFrame),
            };
            GatewayResponse<UpdatedObjectData> staleMove =
                await edit.ExecuteEditAsync<MoveObjectInput, UpdatedObjectData>(
                    "object.move",
                    CreateGatewayRequest(
                        harness.InstanceId,
                        staleMoveInput,
                        beforeRename),
                    timeout.Token);
            Assert.IsFalse(staleMove.Ok);
            Assert.AreEqual("revision_conflict", staleMove.Error?.Code);

            MoveObjectInput moveInput = staleMoveInput with { ExpectedRevision = revision };
            GatewayResponse<UpdatedObjectData> move =
                await edit.ExecuteEditAsync<MoveObjectInput, UpdatedObjectData>(
                    "object.move",
                    CreateGatewayRequest(harness.InstanceId, moveInput, revision),
                    timeout.Token);
            Assert.IsTrue(move.Ok, move.Error?.Message);
            Assert.AreEqual(33, move.Data!.TimelineObject!.Layer);
            Assert.AreEqual(editFrame, move.Data.TimelineObject.StartFrame);
            lifecycleObject = move.Data.TimelineObject;
            revision = RequireChangedRevision(harness, revision, move.Revision);

            GatewayResponse<ObjectData> detail = await query.GetObjectAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new GetObjectInput
                    {
                        Locator = lifecycleObject.Locator,
                        IncludeEffectItems = true,
                    }),
                timeout.Token);
            Assert.IsTrue(detail.Ok, detail.Error?.Message);
            EffectItemsGroup textGroup = detail.Data!.EffectItems.Single(group =>
                group.Effect.Name == textEffect.Name);
            EffectItem writableText = textGroup.Items.Single(item =>
                item.Name == "テキスト" && item.IsWritable);
            using JsonDocument textValue = JsonDocument.Parse("\"AviUtl2 MCP real edit\"");
            SetEffectItemInput setItemInput = new()
            {
                ExpectedRevision = revision,
                Locator = lifecycleObject.Locator,
                Effect = new EffectInstanceSelector(textEffect.Name),
                ItemName = writableText.Name,
                Value = textValue.RootElement.Clone(),
            };
            GatewayResponse<EffectItemUpdateData> setItem =
                await edit.ExecuteEditAsync<SetEffectItemInput, EffectItemUpdateData>(
                    "effect.setItem",
                    CreateGatewayRequest(harness.InstanceId, setItemInput, revision),
                    timeout.Token);
            Assert.IsTrue(setItem.Ok, setItem.Error?.Message);
            Assert.AreEqual("AviUtl2 MCP real edit", setItem.Data!.Item!.Value?.GetString());
            revision = RequireChangedRevision(harness, revision, setItem.Revision);
            lifecycleObject = (await FindByNameAsync(
                query,
                harness.InstanceId,
                renamed,
                timeout.Token)).Single();
            (string afterPreviewPath, string afterPreviewHash) =
                await SavePreviewAsync(
                    preview,
                    harness,
                    frame: editFrame,
                    "preview-after.png",
                    timeout.Token);
            Assert.AreNotEqual(beforePreviewHash, afterPreviewHash);
            harness.RecordPreviewArtifacts(beforePreviewPath, afterPreviewPath);

            SetEffectStateInput stateInput = new()
            {
                ExpectedRevision = revision,
                Locator = lifecycleObject.Locator,
                Effect = new EffectInstanceSelector(textEffect.Name),
                IsEnabled = false,
            };
            GatewayResponse<EffectStateUpdateData> state =
                await edit.ExecuteEditAsync<SetEffectStateInput, EffectStateUpdateData>(
                    "effect.setState",
                    CreateGatewayRequest(harness.InstanceId, stateInput, revision),
                    timeout.Token);
            Assert.IsTrue(state.Ok, state.Error?.Message);
            Assert.AreEqual(false, state.Data!.Effect!.IsEnabled);
            revision = RequireChangedRevision(harness, revision, state.Revision);
            lifecycleObject = (await FindByNameAsync(
                query,
                harness.InstanceId,
                renamed,
                timeout.Token)).Single();

            SetLayerInput layerInput = new()
            {
                ExpectedRevision = revision,
                SceneId = 0,
                Layer = 33,
                Name = "AviUtl2 MCP Real Layer",
                IsVisible = true,
            };
            GatewayResponse<LayerUpdateData> layer =
                await edit.ExecuteEditAsync<SetLayerInput, LayerUpdateData>(
                    "layer.set",
                    CreateGatewayRequest(harness.InstanceId, layerInput, revision),
                    timeout.Token);
            Assert.IsTrue(layer.Ok, layer.Error?.Message);
            Assert.AreEqual("AviUtl2 MCP Real Layer", layer.Data!.Layer!.Name);
            revision = RequireChangedRevision(harness, revision, layer.Revision);

            const string batchName = "aviutl2-mcp-real-batch";
            ExecuteBatchInput batchInput = new()
            {
                ExpectedRevision = revision,
                Operations =
                [
                    new BatchCreateObject(
                        "create-batch-object",
                        new CreateObjectArgs(
                            new EffectDefinitionSelector(textEffect.Name),
                            new Placement(0, 33, checked(editFrame + 40), DurationFrames: 20))
                        {
                            Name = batchName,
                        }),
                    new BatchSetLayer(
                        "set-batch-layer",
                        new SetLayerArgs(33)
                        {
                            SceneId = 0,
                            Name = "AviUtl2 MCP Batch Layer",
                            IsVisible = true,
                        }),
                ],
            };
            GatewayResponse<BatchData> batch = await edit.ExecuteBatchAsync(
                CreateGatewayRequest(harness.InstanceId, batchInput, revision),
                timeout.Token);
            Assert.IsTrue(batch.Ok, batch.Error?.Message);
            CollectionAssert.AreEqual(
                EXPECTED_BATCH_OPERATION_IDS,
                batch.Data!.AppliedOperationIds.ToArray());
            Assert.IsFalse(batch.Data.UndoRecommended);
            revision = RequireChangedRevision(harness, revision, batch.Revision);
            Assert.HasCount(
                1,
                await FindByNameAsync(query, harness.InstanceId, batchName, timeout.Token));

            foreach (ObjectSummary target in new[]
            {
                lifecycleObject,
                createdMedia.Data.TimelineObject,
                createdAlias.Data.Objects[0],
                (await FindByNameAsync(
                    query,
                    harness.InstanceId,
                    batchName,
                    timeout.Token)).Single(),
            })
            {
                DeleteObjectInput deleteInput = new()
                {
                    ExpectedRevision = revision,
                    Locator = target.Locator,
                };
                GatewayResponse<DeleteData> deleted =
                    await edit.ExecuteEditAsync<DeleteObjectInput, DeleteData>(
                        "object.delete",
                        CreateGatewayRequest(harness.InstanceId, deleteInput, revision),
                        timeout.Token);
                Assert.IsTrue(deleted.Ok, deleted.Error?.Message);
                Assert.AreEqual(true, deleted.Data!.Deleted);
                revision = RequireChangedRevision(harness, revision, deleted.Revision);
            }

            Assert.AreEqual(
                0,
                (await FindByNameAsync(query, harness.InstanceId, renamed, timeout.Token)).Count);
            Assert.AreEqual(
                0,
                (await FindByNameAsync(query, harness.InstanceId, mediaName, timeout.Token)).Count);
            Assert.AreEqual(
                0,
                (await FindByNameAsync(query, harness.InstanceId, aliasName, timeout.Token)).Count);
            Assert.AreEqual(
                0,
                (await FindByNameAsync(query, harness.InstanceId, batchName, timeout.Token)).Count);
        }
        catch (Exception exception)
        {
            harness.RecordFailure(exception);
            throw;
        }
    }

    private static async Task<string> GetSetupAliasAsync(
        BridgeQueryGateway query,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        GatewayResponse<ObjectsPageData> setups = await query.FindObjectsAsync(
            CreateGatewayRequest(
                instanceId,
                new FindObjectsInput
                {
                    EffectName = "最初に置くやつ@PSDToolKit",
                    Limit = 100,
                }),
            cancellationToken);
        Assert.IsTrue(setups.Ok, setups.Error?.Message);
        ObjectSummary setup = setups.Data!.Objects.Single();
        GatewayResponse<ObjectData> detail = await query.GetObjectAsync(
            CreateGatewayRequest(
                instanceId,
                new GetObjectInput
                {
                    Locator = setup.Locator,
                    IncludeAlias = true,
                    IncludeEffectItems = false,
                }),
            cancellationToken);
        Assert.IsTrue(detail.Ok, detail.Error?.Message);
        Assert.IsFalse(string.IsNullOrWhiteSpace(detail.Data!.Alias));
        return detail.Data.Alias!;
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

    private static async Task<IReadOnlyList<ObjectSummary>> FindByNameAsync(
        BridgeQueryGateway query,
        Guid instanceId,
        string name,
        CancellationToken cancellationToken)
    {
        GatewayResponse<ObjectsPageData> found = await query.FindObjectsAsync(
            CreateGatewayRequest(
                instanceId,
                new FindObjectsInput
                {
                    NameContains = name,
                    Limit = 100,
                }),
            cancellationToken);
        Assert.IsTrue(found.Ok, found.Error?.Message);
        return found.Data!.Objects.Where(candidate => candidate.Name == name).ToArray();
    }

    private static async Task<(string Path, string Sha256)> SavePreviewAsync(
        BridgePreviewGateway preview,
        RealAviUtlHarness harness,
        int frame,
        string fileName,
        CancellationToken cancellationToken)
    {
        GatewayResponse<PreviewData> response = await preview.RenderPreviewAsync(
            CreateGatewayRequest(
                harness.InstanceId,
                new RenderPreviewInput
                {
                    Frame = frame,
                    MaxWidth = 640,
                    MaxHeight = 360,
                    IncludeAlpha = true,
                }),
            cancellationToken);
        Assert.IsTrue(response.Ok, response.Error?.Message);
        Assert.AreEqual("image/png", response.Data!.MimeType);
        byte[] png = response.Binary.ToArray();
        Assert.AreEqual(response.Data.ByteLength, png.Length);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(png));
        Assert.AreEqual(response.Data.Sha256, sha256);
        string path = Path.Combine(harness.RuntimeDirectory, fileName);
        await File.WriteAllBytesAsync(path, png, cancellationToken);
        return (path, sha256);
    }

    private static Revision RequireChangedRevision(
        RealAviUtlHarness harness,
        Revision before,
        Revision? after)
    {
        Assert.IsNotNull(after);
        Assert.AreNotEqual(before, after.Value);
        harness.RecordRevision(after.Value.Value);
        return after.Value;
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

    private static string GetDescriptorDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AviUtl2MCP",
        "v1",
        "instances");

    private static void CreateSilentWave(string path)
    {
        const int sampleRate = 44_100;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int sampleCount = sampleRate / 10;
        const int dataLength = sampleCount * channels * (bitsPerSample / 8);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
    }
}

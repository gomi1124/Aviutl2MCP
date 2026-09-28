using System.Security.Cryptography;
using System.Text.Json;
using AviUtl2MCP.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AviUtl2MCP.RealAviUtlTests;

[TestClass]
[DoNotParallelize]
public sealed class RealCreateSceneTests
{
    [TestMethod]
    [TestCategory("RealAviUtl2")]
    [TestProperty("TestId", "real.create-scene")]
    [TestProperty("TestId", "real.fixture-process-guard")]
    [Timeout(180_000)]
    public async Task RealAviUtlCreatesConfiguredSceneThroughMcpAndSavesIt()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive("Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(timeout.Token);
        harness.RecordAcceptanceTestIds("real.create-scene", "real.fixture-process-guard");
        try
        {
            string? packagedServer = Environment.GetEnvironmentVariable("AVIUTL2_MCP_REAL_SERVER_PATH");
            bool hasPackagedServer = !string.IsNullOrWhiteSpace(packagedServer);
            if (hasPackagedServer)
            {
                Assert.IsTrue(Path.IsPathFullyQualified(packagedServer!) && File.Exists(packagedServer),
                    "The packaged MCP server must be an existing absolute executable path.");
            }
            StdioClientTransport transport = new(new StdioClientTransportOptions
            {
                Name = "AviUtl2MCP real scene creation test",
                Command = hasPackagedServer ? packagedServer! : "dotnet",
                Arguments = hasPackagedServer ? [] : [typeof(ServerMarker).Assembly.Location],
                WorkingDirectory = Path.GetDirectoryName(
                    hasPackagedServer ? packagedServer! : typeof(ServerMarker).Assembly.Location),
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
            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.IsTrue(tools.Any(tool => tool.Name == "aviutl_create_scene"));

            JsonElement initial = await WaitForProjectAsync(client, harness.InstanceId, timeout.Token);
            JsonElement initialData = initial.GetProperty("data");
            int initialSceneId = initialData.GetProperty("currentSceneId").GetInt32();
            int[] originalSceneIds = GetSceneIds(initial);
            string beforeRevision = GetRevision(initial);
            string beforeViewRevision = GetViewRevision(initial);
            harness.RecordRevision(beforeRevision);

            const string sceneName = "MCP実機テスト 新しいシーン";
            const string sceneLabel = "MCP実機検証";
            Dictionary<string, object?> createArguments = CreateArguments(harness.InstanceId);
            createArguments["expectedRevision"] = beforeRevision;
            createArguments["name"] = sceneName;
            createArguments["width"] = 1280;
            createArguments["height"] = 720;
            createArguments["frameRate"] = 29.97;
            createArguments["sampleRate"] = 48_000;
            createArguments["label"] = sceneLabel;
            createArguments["dryRun"] = true;

            JsonElement capabilities = RequireSuccess(await client.CallToolAsync(
                "aviutl_get_capabilities",
                CreateArguments(harness.InstanceId),
                cancellationToken: timeout.Token));
            JsonElement operation = capabilities.GetProperty("data").GetProperty("operations")
                .EnumerateArray().Single(item => item.GetProperty("name").GetString() == "aviutl_create_scene");
            if (!operation.GetProperty("available").GetBoolean())
            {
                Assert.AreEqual("version_not_supported", operation.GetProperty("reason").GetString());
                RequireFailure(await client.CallToolAsync(
                    "aviutl_create_scene", createArguments, cancellationToken: timeout.Token),
                    "version_not_supported");
                AssertProjectUnchanged(initial, await GetProjectAsync(client, harness.InstanceId, timeout.Token));
                return;
            }

            string beforePreview = await SavePreviewAsync(client, harness, "preview-before.png", timeout.Token);
            JsonElement planned = RequireSuccess(await client.CallToolAsync(
                "aviutl_create_scene", createArguments, cancellationToken: timeout.Token));
            AssertSceneSettings(planned.GetProperty("data"), sceneName);
            Assert.IsFalse(planned.GetProperty("data").GetProperty("created").GetBoolean());
            Assert.IsFalse(planned.GetProperty("data").GetProperty("activated").GetBoolean());
            Assert.AreEqual(beforeRevision, GetRevision(planned));
            Assert.AreEqual(beforeViewRevision, GetViewRevision(planned));
            AssertProjectUnchanged(initial, await GetProjectAsync(client, harness.InstanceId, timeout.Token));

            createArguments["dryRun"] = false;
            JsonElement created = RequireSuccess(await client.CallToolAsync(
                "aviutl_create_scene", createArguments, cancellationToken: timeout.Token));
            JsonElement createdData = created.GetProperty("data");
            AssertSceneSettings(createdData, sceneName);
            Assert.IsTrue(createdData.GetProperty("created").GetBoolean());
            Assert.IsTrue(createdData.GetProperty("activated").GetBoolean());
            int createdSceneId = createdData.GetProperty("sceneId").GetInt32();
            Assert.IsGreaterThanOrEqualTo(0, createdSceneId);
            CollectionAssert.DoesNotContain(originalSceneIds, createdSceneId);
            Assert.AreNotEqual(beforeRevision, GetRevision(created));
            Assert.AreNotEqual(beforeViewRevision, GetViewRevision(created));
            harness.RecordRevision(GetRevision(created));

            JsonElement after = await GetProjectAsync(client, harness.InstanceId, timeout.Token);
            Assert.AreEqual(createdSceneId, after.GetProperty("data").GetProperty("currentSceneId").GetInt32());
            AssertSceneSettings(after.GetProperty("data"), null);
            int[] afterSceneIds = GetSceneIds(after);
            Assert.HasCount(originalSceneIds.Length + 1, afterSceneIds);
            foreach (int originalSceneId in originalSceneIds)
            {
                CollectionAssert.Contains(afterSceneIds, originalSceneId);
            }
            Assert.AreEqual(sceneName, after.GetProperty("data").GetProperty("scenes")
                .EnumerateArray().Single(scene => scene.GetProperty("sceneId").GetInt32() == createdSceneId)
                .GetProperty("name").GetString());

            createArguments["expectedRevision"] = GetRevision(after);
            RequireFailure(await client.CallToolAsync(
                "aviutl_create_scene", createArguments, cancellationToken: timeout.Token),
                "scene_already_exists");
            AssertProjectUnchanged(after, await GetProjectAsync(client, harness.InstanceId, timeout.Token));

            createArguments["name"] = "MCP拒否される古いリビジョン";
            createArguments["expectedRevision"] = beforeRevision;
            RequireFailure(await client.CallToolAsync(
                "aviutl_create_scene", createArguments, cancellationToken: timeout.Token),
                "revision_conflict");
            AssertProjectUnchanged(after, await GetProjectAsync(client, harness.InstanceId, timeout.Token));

            JsonElement originalOpened = await OpenSceneAsync(
                client, harness.InstanceId, initialSceneId, GetViewRevision(after), timeout.Token);
            Assert.AreEqual(initialSceneId, originalOpened.GetProperty("data").GetProperty("sceneId").GetInt32());
            Assert.AreEqual(GetRevision(after), GetRevision(originalOpened));
            JsonElement newOpened = await OpenSceneAsync(
                client, harness.InstanceId, createdSceneId, GetViewRevision(originalOpened), timeout.Token);
            Assert.AreEqual(createdSceneId, newOpened.GetProperty("data").GetProperty("sceneId").GetInt32());
            Assert.AreEqual(sceneName, newOpened.GetProperty("data").GetProperty("name").GetString());
            Assert.AreEqual(GetRevision(after), GetRevision(newOpened));

            string afterPreview = await SavePreviewAsync(client, harness, "preview-after.png", timeout.Token);
            harness.RecordPreviewArtifacts(beforePreview, afterPreview);
            JsonElement beforeSave = await GetProjectAsync(client, harness.InstanceId, timeout.Token);
            Dictionary<string, object?> saveArguments = CreateArguments(harness.InstanceId);
            saveArguments["expectedRevision"] = GetRevision(newOpened);
            JsonElement saved = RequireSuccess(await client.CallToolAsync(
                "aviutl_save_project", saveArguments, cancellationToken: timeout.Token));
            Assert.IsTrue(saved.GetProperty("data").GetProperty("saved").GetBoolean());
            Assert.AreEqual(Path.GetFullPath(harness.FixtureProjectPath), saved.GetProperty("data").GetProperty("path").GetString());
            Assert.AreEqual(GetRevision(newOpened), GetRevision(saved));
            string savedContent = await File.ReadAllTextAsync(harness.FixtureProjectPath, timeout.Token);
            StringAssert.Contains(savedContent, $"[scene.{createdSceneId}]");
            StringAssert.Contains(savedContent, $"name={sceneName}");
            StringAssert.Contains(savedContent, sceneLabel);
            AssertProjectUnchanged(beforeSave, await GetProjectAsync(client, harness.InstanceId, timeout.Token));
        }
        catch (Exception exception)
        {
            harness.RecordFailure(exception);
            throw;
        }
    }

    private static void AssertSceneSettings(JsonElement data, string? name)
    {
        if (name is not null)
        {
            Assert.AreEqual(name, data.GetProperty("name").GetString());
        }
        Assert.AreEqual(1280, data.GetProperty("width").GetInt32());
        Assert.AreEqual(720, data.GetProperty("height").GetInt32());
        Assert.AreEqual(29.97, data.GetProperty("frameRate").GetDouble(), 0.000001);
        Assert.AreEqual(48_000, data.GetProperty("sampleRate").GetInt32());
    }

    private static void AssertProjectUnchanged(JsonElement before, JsonElement after)
    {
        Assert.AreEqual(GetRevision(before), GetRevision(after));
        Assert.AreEqual(GetViewRevision(before), GetViewRevision(after));
        Assert.AreEqual(before.GetProperty("data").GetProperty("currentSceneId").GetInt32(),
            after.GetProperty("data").GetProperty("currentSceneId").GetInt32());
        CollectionAssert.AreEqual(GetSceneIds(before), GetSceneIds(after));
    }

    private static int[] GetSceneIds(JsonElement envelope) => envelope.GetProperty("data")
        .GetProperty("scenes").EnumerateArray().Select(scene => scene.GetProperty("sceneId").GetInt32()).Order().ToArray();

    private static string GetRevision(JsonElement envelope) => envelope.GetProperty("revision").GetString()!;

    private static string GetViewRevision(JsonElement envelope) => envelope.GetProperty("viewRevision").GetString()!;

    private static async Task<JsonElement> OpenSceneAsync(
        McpClient client, Guid instanceId, int sceneId, string expectedViewRevision, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> arguments = CreateArguments(instanceId);
        arguments["sceneId"] = sceneId;
        arguments["expectedViewRevision"] = expectedViewRevision;
        return RequireSuccess(await client.CallToolAsync("aviutl_open_scene", arguments, cancellationToken: cancellationToken));
    }

    private static async Task<JsonElement> GetProjectAsync(McpClient client, Guid instanceId, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> arguments = CreateArguments(instanceId);
        arguments["includeScenes"] = true;
        return RequireSuccess(await client.CallToolAsync("aviutl_get_project", arguments, cancellationToken: cancellationToken));
    }

    private static async Task<JsonElement> WaitForProjectAsync(McpClient client, Guid instanceId, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Dictionary<string, object?> arguments = CreateArguments(instanceId);
        arguments["includeScenes"] = true;
        CallToolResult? last = null;
        do
        {
            last = await client.CallToolAsync("aviutl_get_project", arguments, cancellationToken: cancellationToken);
            if (last.IsError != true || !last.StructuredContent.HasValue
                || last.StructuredContent.Value.GetProperty("error").GetProperty("code").GetString() != "project_not_open")
            {
                return RequireSuccess(last);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);
        return RequireSuccess(last!);
    }

    private static async Task<string> SavePreviewAsync(McpClient client, RealAviUtlHarness harness, string fileName, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> arguments = CreateArguments(harness.InstanceId);
        arguments["frame"] = 1;
        arguments["maxWidth"] = 320;
        arguments["maxHeight"] = 180;
        CallToolResult result = await client.CallToolAsync("aviutl_render_preview", arguments, cancellationToken: cancellationToken);
        JsonElement envelope = RequireSuccess(result);
        ImageContentBlock image = result.Content.OfType<ImageContentBlock>().Single();
        byte[] png = image.DecodedData.ToArray();
        Assert.AreEqual("image/png", image.MimeType);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(png)), envelope.GetProperty("data").GetProperty("sha256").GetString());
        string path = Path.Combine(harness.RuntimeDirectory, fileName);
        await File.WriteAllBytesAsync(path, png, cancellationToken);
        return path;
    }

    private static JsonElement RequireSuccess(CallToolResult result)
    {
        string diagnostic = result.StructuredContent?.GetRawText()
            ?? string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.IsFalse(result.IsError, diagnostic);
        Assert.IsTrue(result.StructuredContent.HasValue, diagnostic);
        JsonElement envelope = result.StructuredContent.Value;
        Assert.IsTrue(envelope.GetProperty("ok").GetBoolean(), diagnostic);
        return envelope;
    }

    private static void RequireFailure(CallToolResult result, string errorCode)
    {
        Assert.IsTrue(result.IsError, result.StructuredContent?.GetRawText());
        Assert.IsTrue(result.StructuredContent.HasValue);
        JsonElement envelope = result.StructuredContent.Value;
        Assert.IsFalse(envelope.GetProperty("ok").GetBoolean());
        Assert.AreEqual(errorCode, envelope.GetProperty("error").GetProperty("code").GetString(), envelope.GetRawText());
    }

    private static Dictionary<string, object?> CreateArguments(Guid instanceId) => new()
    {
        ["instanceId"] = instanceId,
        ["timeoutMs"] = 60_000,
    };
}

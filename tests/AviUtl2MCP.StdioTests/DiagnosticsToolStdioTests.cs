using System.Text.Json;
using AviUtl2MCP.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AviUtl2MCP.StdioTests;

[TestClass]
public sealed class DiagnosticsToolStdioTests
{
    private static readonly string[] READ_TOOL_NAMES =
    [
        "aviutl_get_status",
        "aviutl_get_capabilities",
        "aviutl_get_project",
        "aviutl_get_timeline",
        "aviutl_find_objects",
        "aviutl_get_object",
        "aviutl_list_effects",
        "aviutl_list_effect_items",
        "aviutl_render_preview",
    ];
    private static readonly string[] EDIT_TOOL_NAMES =
    [
        "aviutl_create_object",
        "aviutl_create_media_object",
        "aviutl_create_alias_object",
        "aviutl_move_object",
        "aviutl_delete_object",
        "aviutl_set_object_name",
        "aviutl_create_object_section",
        "aviutl_delete_object_section",
        "aviutl_move_object_section",
        "aviutl_set_effect_item",
        "aviutl_set_effect_state",
        "aviutl_set_layer",
        "aviutl_execute_batch",
    ];
    private static readonly string[] PSD_EDIT_TOOL_NAMES =
    [
        "aviutl_psd_create",
        "aviutl_psd_setup",
        "aviutl_psd_set_character",
        "aviutl_psd_set_layer_state",
        "aviutl_psd_create_voice",
    ];
    private static readonly string[] PROMPT_NAMES =
    [
        "edit_timeline_safely",
        "setup_psd_character",
        "add_voice_and_subtitle",
        "diagnose_aviutl",
    ];
    private static readonly string[] RESOURCE_URIS =
    [
        "aviutl://status",
        "aviutl://capabilities",
        "aviutl://project/current",
        "aviutl://timeline/current",
        "aviutl://diagnostics/latest",
    ];
    private static readonly string[] SERVER_SOURCE = ["server"];
    private static readonly string[] EFFECT_ITEM_VALUE_TYPES =
        ["boolean", "integer", "number", "string"];

    [TestMethod]
    public async Task StdioListsAndCallsReadOnlyDiagnosticTools()
    {
        // Arrange
        string correlationDirectory = CreateCorrelationDirectory();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            StdioClientTransport transport = new(new StdioClientTransportOptions
            {
                Name = "AviUtl2MCP stdio test",
                Command = "dotnet",
                Arguments = [typeof(ServerMarker).Assembly.Location],
                WorkingDirectory = Path.GetDirectoryName(typeof(ServerMarker).Assembly.Location),
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["AVIUTL2_MCP_LOG_DIRECTORY"] = Path.Combine(correlationDirectory, "server-logs"),
                    ["AVIUTL2_LOG_DIRECTORY"] = Path.Combine(correlationDirectory, "aviutl-logs"),
                    ["AVIUTL2_MCP_INSTANCE_DIRECTORY"] = Path.Combine(correlationDirectory, "instances"),
                },
            });
            await using McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: timeout.Token);

            // Act
            IList<McpClientTool> tools = await client.ListToolsAsync(
                cancellationToken: timeout.Token);
            IList<McpClientResource> resources = await client.ListResourcesAsync(
                cancellationToken: timeout.Token);
            IList<McpClientPrompt> prompts = await client.ListPromptsAsync(
                cancellationToken: timeout.Token);
            McpClientTool logsTool = tools.Single(tool => tool.Name == "aviutl_get_logs");
            McpClientTool diagnoseTool = tools.Single(tool => tool.Name == "aviutl_diagnose");
            McpClientTool statusTool = tools.Single(tool => tool.Name == "aviutl_get_status");
            McpClientTool projectTool = tools.Single(tool => tool.Name == "aviutl_get_project");
            McpClientTool saveTool = tools.Single(tool => tool.Name == "aviutl_save_project");
            McpClientTool effectItemTool = tools.Single(
                tool => tool.Name == "aviutl_set_effect_item");
            McpClientTool previewTool = tools.Single(tool => tool.Name == "aviutl_render_preview");
            McpClientTool timelineTool = tools.Single(tool => tool.Name == "aviutl_get_timeline");
            McpClientTool deleteTool = tools.Single(tool => tool.Name == "aviutl_delete_object");
            McpClientTool batchTool = tools.Single(tool => tool.Name == "aviutl_execute_batch");
            McpClientTool psdValidateTool = tools.Single(
                tool => tool.Name == "aviutl_psd_validate");
            ReadResourceResult statusResource = await client.ReadResourceAsync(
                "aviutl://status",
                cancellationToken: timeout.Token);
            ReadResourceResult diagnosticsResource = await client.ReadResourceAsync(
                "aviutl://diagnostics/latest",
                cancellationToken: timeout.Token);
            CallToolResult statusResult = await client.CallToolAsync(
                statusTool.Name,
                new Dictionary<string, object?>(),
                cancellationToken: timeout.Token);
            CallToolResult projectResult = await client.CallToolAsync(
                projectTool.Name,
                new Dictionary<string, object?>(),
                cancellationToken: timeout.Token);
            CallToolResult previewResult = await client.CallToolAsync(
                previewTool.Name,
                new Dictionary<string, object?> { ["frame"] = 1 },
                cancellationToken: timeout.Token);
            CallToolResult invalidTimelineResult = await client.CallToolAsync(
                timelineTool.Name,
                new Dictionary<string, object?> { ["limit"] = 0 },
                cancellationToken: timeout.Token);
            CallToolResult offlineDeleteResult = await client.CallToolAsync(
                deleteTool.Name,
                new Dictionary<string, object?>
                {
                    ["expectedRevision"] = "epoch:generation:0",
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["instanceId"] = Guid.CreateVersion7(),
                        ["projectGeneration"] = Guid.CreateVersion7(),
                        ["sceneId"] = 0,
                        ["layer"] = 1,
                        ["startFrame"] = 1,
                        ["endFrame"] = 30,
                        ["name"] = "voice",
                        ["aliasSha256"] = new string('a', 64),
                        ["effectSignatureSha256"] = new string('b', 64),
                    },
                },
                cancellationToken: timeout.Token);
            CallToolResult offlineBatchResult = await client.CallToolAsync(
                batchTool.Name,
                new Dictionary<string, object?>
                {
                    ["expectedRevision"] = "epoch:generation:0",
                    ["operations"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["op"] = "deleteObject",
                            ["clientOperationId"] = "delete-1",
                            ["args"] = new Dictionary<string, object?>
                            {
                                ["locator"] = new Dictionary<string, object?>
                                {
                                    ["instanceId"] = Guid.CreateVersion7(),
                                    ["projectGeneration"] = Guid.CreateVersion7(),
                                    ["sceneId"] = 0,
                                    ["layer"] = 1,
                                    ["startFrame"] = 1,
                                    ["endFrame"] = 30,
                                    ["name"] = "voice",
                                    ["aliasSha256"] = new string('a', 64),
                                    ["effectSignatureSha256"] = new string('b', 64),
                                },
                            },
                        },
                    },
                },
                cancellationToken: timeout.Token);
            CallToolResult offlinePsdValidateResult = await client.CallToolAsync(
                psdValidateTool.Name,
                new Dictionary<string, object?> { ["scope"] = "scene" },
                cancellationToken: timeout.Token);
            CallToolResult logsResult = await client.CallToolAsync(
                logsTool.Name,
                new Dictionary<string, object?>
                {
                    ["sources"] = SERVER_SOURCE,
                    ["limit"] = 10,
                },
                cancellationToken: timeout.Token);
            CallToolResult diagnoseResult = await client.CallToolAsync(
                diagnoseTool.Name,
                new Dictionary<string, object?>(),
                cancellationToken: timeout.Token);
            ReadResourceResult completedDiagnosticsResource = await client.ReadResourceAsync(
                "aviutl://diagnostics/latest",
                cancellationToken: timeout.Token);
            CallToolResult invalidResult = await client.CallToolAsync(
                logsTool.Name,
                new Dictionary<string, object?> { ["timeoutMs"] = 99 },
                cancellationToken: timeout.Token);
            GetPromptResult editPrompt = await client.GetPromptAsync(
                "edit_timeline_safely",
                new Dictionary<string, object?> { ["objective"] = "字幕を移動する" },
                cancellationToken: timeout.Token);
            GetPromptResult psdPrompt = await client.GetPromptAsync(
                "setup_psd_character",
                new Dictionary<string, object?>
                {
                    ["psdPath"] = "C:\\fixture\\alice.psd",
                    ["characterId"] = "alice",
                },
                cancellationToken: timeout.Token);
            GetPromptResult voicePrompt = await client.GetPromptAsync(
                "add_voice_and_subtitle",
                new Dictionary<string, object?>
                {
                    ["audioPath"] = "C:\\fixture\\alice.wav",
                    ["characterId"] = "alice",
                },
                cancellationToken: timeout.Token);
            GetPromptResult diagnosePrompt = await client.GetPromptAsync(
                "diagnose_aviutl",
                new Dictionary<string, object?> { ["includePreview"] = true },
                cancellationToken: timeout.Token);

            // Assert
            Assert.AreEqual(32, tools.Count);
            CollectionAssert.IsSubsetOf(
                READ_TOOL_NAMES,
                tools.Select(tool => tool.Name).ToArray());
            foreach (string readToolName in READ_TOOL_NAMES)
            {
                AssertToolMetadata(tools.Single(tool => tool.Name == readToolName));
            }
            CollectionAssert.IsSubsetOf(
                EDIT_TOOL_NAMES,
                tools.Select(tool => tool.Name).ToArray());
            foreach (string editToolName in EDIT_TOOL_NAMES)
            {
                AssertEditToolMetadata(tools.Single(tool => tool.Name == editToolName));
            }
            AssertSaveToolMetadata(saveTool);
            AssertEffectItemValueSchema(effectItemTool);
            CollectionAssert.IsSubsetOf(
                PSD_EDIT_TOOL_NAMES,
                tools.Select(tool => tool.Name).ToArray());
            foreach (string psdEditToolName in PSD_EDIT_TOOL_NAMES)
            {
                AssertEditToolMetadata(tools.Single(tool => tool.Name == psdEditToolName));
            }
            AssertToolMetadata(psdValidateTool, "scope", "checks", "locator");
            AssertCursorToolMetadata(tools.Single(tool => tool.Name == "aviutl_set_cursor"));
            AssertToolMetadata(logsTool, "sources", "limit");
            AssertToolMetadata(diagnoseTool, "includeReadSmoke", "includePreviewSmoke", "maxLogLines");
            AssertToolMetadata(previewTool, "frame", "maxWidth", "maxHeight", "includeAlpha");

            Assert.AreEqual(5, resources.Count);
            CollectionAssert.AreEquivalent(
                RESOURCE_URIS,
                resources.Select(resource => resource.Uri.ToString()).ToArray());
            Assert.IsTrue(resources.All(resource => resource.MimeType == "application/json"));
            Assert.AreEqual(4, prompts.Count);
            CollectionAssert.AreEquivalent(
                PROMPT_NAMES,
                prompts.Select(prompt => prompt.Name).ToArray());
            AssertPromptText(editPrompt, "dryRun=true", "字幕を移動する");
            AssertPromptText(psdPrompt, "aviutl_psd_setup", "alice.psd");
            AssertPromptText(voicePrompt, "aviutl_psd_create_voice", "alice.wav");
            AssertPromptText(diagnosePrompt, "includePreviewSmoke=true", "自動修復");
            JsonElement statusResourceEnvelope = ParseResourceEnvelope(statusResource);
            Assert.IsTrue(statusResourceEnvelope.GetProperty("ok").GetBoolean());
            Assert.AreEqual(
                "disconnected",
                statusResourceEnvelope.GetProperty("data").GetProperty("connectionState").GetString());
            JsonElement diagnosticsResourceEnvelope = ParseResourceEnvelope(diagnosticsResource);
            Assert.IsTrue(diagnosticsResourceEnvelope.GetProperty("ok").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, diagnosticsResourceEnvelope.GetProperty("data").ValueKind);

            Assert.AreEqual(false, statusResult.IsError);
            JsonElement statusEnvelope = statusResult.StructuredContent!.Value;
            Assert.IsTrue(statusEnvelope.GetProperty("ok").GetBoolean());
            Assert.AreEqual(
                "disconnected",
                statusEnvelope.GetProperty("data").GetProperty("connectionState").GetString());

            Assert.AreEqual(true, projectResult.IsError);
            JsonElement projectEnvelope = projectResult.StructuredContent!.Value;
            Assert.AreEqual(
                "aviutl_not_running",
                projectEnvelope.GetProperty("error").GetProperty("code").GetString());

            Assert.AreEqual(true, previewResult.IsError);
            JsonElement previewEnvelope = previewResult.StructuredContent!.Value;
            Assert.AreEqual(
                "aviutl_not_running",
                previewEnvelope.GetProperty("error").GetProperty("code").GetString());
            Assert.IsInstanceOfType<TextContentBlock>(previewResult.Content.Single());

            Assert.AreEqual(true, invalidTimelineResult.IsError);
            JsonElement invalidTimelineEnvelope = invalidTimelineResult.StructuredContent!.Value;
            Assert.AreEqual(
                "invalid_argument",
                invalidTimelineEnvelope.GetProperty("error").GetProperty("code").GetString());

            Assert.AreEqual(true, offlineDeleteResult.IsError);
            JsonElement offlineDeleteEnvelope = offlineDeleteResult.StructuredContent!.Value;
            Assert.AreEqual(
                "aviutl_not_running",
                offlineDeleteEnvelope.GetProperty("error").GetProperty("code").GetString());

            Assert.AreEqual(true, offlineBatchResult.IsError);
            JsonElement offlineBatchEnvelope = offlineBatchResult.StructuredContent!.Value;
            Assert.AreEqual(
                "aviutl_not_running",
                offlineBatchEnvelope.GetProperty("error").GetProperty("code").GetString());

            Assert.AreEqual(true, offlinePsdValidateResult.IsError);
            JsonElement offlinePsdValidateEnvelope =
                offlinePsdValidateResult.StructuredContent!.Value;
            Assert.AreEqual(
                "aviutl_not_running",
                offlinePsdValidateEnvelope.GetProperty("error").GetProperty("code").GetString());

            Assert.AreEqual(false, logsResult.IsError);
            JsonElement logsEnvelope = logsResult.StructuredContent!.Value;
            Assert.IsTrue(logsEnvelope.GetProperty("ok").GetBoolean());
            Assert.AreEqual(7, logsEnvelope.GetProperty("correlationId").GetGuid().Version);
            Assert.IsTrue(logsEnvelope.GetProperty("data").TryGetProperty("entries", out _));
            Assert.IsInstanceOfType<TextContentBlock>(logsResult.Content.Single());

            Assert.AreEqual(true, diagnoseResult.IsError);
            JsonElement diagnoseEnvelope = diagnoseResult.StructuredContent!.Value;
            Assert.IsFalse(diagnoseEnvelope.GetProperty("ok").GetBoolean());
            Assert.AreEqual(
                "aviutl_not_running",
                diagnoseEnvelope.GetProperty("error").GetProperty("code").GetString());
            JsonElement completedDiagnosticsEnvelope = ParseResourceEnvelope(
                completedDiagnosticsResource);
            Assert.IsFalse(completedDiagnosticsEnvelope.GetProperty("ok").GetBoolean());
            Assert.AreEqual(
                diagnoseEnvelope.GetProperty("correlationId").GetGuid(),
                completedDiagnosticsEnvelope.GetProperty("correlationId").GetGuid());
            Assert.AreEqual(
                "aviutl_not_running",
                completedDiagnosticsEnvelope.GetProperty("error").GetProperty("code").GetString());

            Assert.AreEqual(true, invalidResult.IsError);
            JsonElement invalidEnvelope = invalidResult.StructuredContent!.Value;
            Assert.AreEqual(
                "invalid_argument",
                invalidEnvelope.GetProperty("error").GetProperty("code").GetString());
            Assert.AreEqual(7, invalidEnvelope.GetProperty("correlationId").GetGuid().Version);
        }
        finally
        {
            DeleteOwnedCorrelationDirectory(correlationDirectory);
        }
    }

    private static void AssertToolMetadata(
        McpClientTool tool,
        params string[] expectedProperties)
    {
        Assert.AreEqual(true, tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.AreEqual(false, tool.ProtocolTool.Annotations.DestructiveHint);
        Assert.AreEqual(false, tool.ProtocolTool.Annotations.OpenWorldHint);
        Assert.IsTrue(tool.ProtocolTool.OutputSchema.HasValue);
        JsonElement properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (string property in expectedProperties)
        {
            Assert.IsTrue(properties.TryGetProperty(property, out _), $"Missing input property {property}.");
        }
        Assert.IsFalse(properties.TryGetProperty("input", out _));
    }

    private static JsonElement ParseResourceEnvelope(ReadResourceResult result)
    {
        ResourceContents content = result.Contents.Single();
        Assert.IsInstanceOfType<TextResourceContents>(content);
        TextResourceContents textContent = (TextResourceContents)content;
        using JsonDocument document = JsonDocument.Parse(textContent.Text);
        return document.RootElement.Clone();
    }

    private static void AssertPromptText(GetPromptResult result, params string[] expectedText)
    {
        PromptMessage message = result.Messages.Single();
        TextContentBlock content = Assert.IsInstanceOfType<TextContentBlock>(message.Content);
        foreach (string expected in expectedText)
        {
            StringAssert.Contains(content.Text, expected);
        }
    }

    private static void AssertEditToolMetadata(McpClientTool tool)
    {
        Assert.AreEqual(false, tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.AreEqual(true, tool.ProtocolTool.Annotations.DestructiveHint);
        Assert.AreEqual(false, tool.ProtocolTool.Annotations.OpenWorldHint);
        Assert.IsTrue(tool.ProtocolTool.OutputSchema.HasValue);
        JsonElement properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.IsTrue(properties.TryGetProperty("expectedRevision", out _));
        Assert.IsTrue(properties.TryGetProperty("dryRun", out _));
        Assert.IsFalse(properties.TryGetProperty("input", out _));
    }

    private static void AssertCursorToolMetadata(McpClientTool tool)
    {
        Assert.AreEqual(false, tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.AreEqual(false, tool.ProtocolTool.Annotations.DestructiveHint);
        Assert.AreEqual(true, tool.ProtocolTool.Annotations.IdempotentHint);
        Assert.AreEqual(false, tool.ProtocolTool.Annotations.OpenWorldHint);
        Assert.IsTrue(tool.ProtocolTool.OutputSchema.HasValue);
        JsonElement properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.IsTrue(properties.TryGetProperty("expectedViewRevision", out _));
        Assert.IsFalse(properties.TryGetProperty("dryRun", out _));
        Assert.IsFalse(properties.TryGetProperty("input", out _));
    }

    private static void AssertSaveToolMetadata(McpClientTool tool)
    {
        Assert.AreEqual(false, tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.AreEqual(false, tool.ProtocolTool.Annotations.DestructiveHint);
        Assert.AreEqual(true, tool.ProtocolTool.Annotations.IdempotentHint);
        Assert.AreEqual(false, tool.ProtocolTool.Annotations.OpenWorldHint);
        Assert.IsTrue(tool.ProtocolTool.OutputSchema.HasValue);
        JsonElement properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.IsTrue(properties.TryGetProperty("expectedRevision", out _));
        Assert.IsFalse(properties.TryGetProperty("dryRun", out _));
        Assert.IsFalse(properties.TryGetProperty("input", out _));
    }

    private static void AssertEffectItemValueSchema(McpClientTool tool)
    {
        JsonElement valueSchema = tool.ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("value");
        JsonElement[] options = valueSchema.GetProperty("oneOf").EnumerateArray().ToArray();
        foreach (JsonElement option in options)
        {
            Assert.AreEqual(
                JsonValueKind.Object,
                option.ValueKind,
                valueSchema.GetRawText());
        }
        string[] types = options
            .Select(option => option.GetProperty("type").GetString()!)
            .ToArray();
        CollectionAssert.AreEquivalent(
            EFFECT_ITEM_VALUE_TYPES,
            types);
    }

    private static string CreateCorrelationDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "AviUtl2MCP-tests");
        string directory = Path.Combine(root, Guid.CreateVersion7().ToString("D"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteOwnedCorrelationDirectory(string directory)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AviUtl2MCP-tests"))
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the owned test root.");
        }
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }
}

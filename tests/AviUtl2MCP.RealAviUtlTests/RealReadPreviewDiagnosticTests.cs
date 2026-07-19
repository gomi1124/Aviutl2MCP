using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AviUtl2MCP.RealAviUtlTests;

[TestClass]
public sealed class RealReadPreviewDiagnosticTests
{
    private static readonly byte[] PNG_SIGNATURE =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    [TestMethod]
    [TestCategory("RealAviUtl2")]
    [Timeout(180_000)]
    public async Task RealAviUtlReadsRendersAndDiagnosesIsolatedFixture()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive("Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(timeout.Token);
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
                ["AVIUTL2_MCP_INSTANCE_DIRECTORY"] = GetDescriptorDirectory(),
                ["AVIUTL2_MCP_LOG_DIRECTORY"] = harness.ServerLogDirectory,
                ["AVIUTL2_LOG_DIRECTORY"] = harness.AviUtlLogDirectory,
            },
        });
        await using McpClient client = await McpClient.CreateAsync(
            transport,
            cancellationToken: timeout.Token);

        IList<McpClientTool> tools = await client.ListToolsAsync(
            cancellationToken: timeout.Token);
        Assert.AreEqual(22, tools.Count);

        JsonElement status = RequireSuccess(await client.CallToolAsync(
            "aviutl_get_status",
            CreateInstanceArguments(harness.InstanceId),
            cancellationToken: timeout.Token));
        Assert.AreEqual("ready", status.GetProperty("data").GetProperty("connectionState").GetString());
        Assert.AreEqual(
            harness.InstanceId,
            status.GetProperty("data").GetProperty("selectedInstance").GetGuid());

        JsonElement capabilities = RequireSuccess(await client.CallToolAsync(
            "aviutl_get_capabilities",
            CreateInstanceArguments(harness.InstanceId),
            cancellationToken: timeout.Token));
        JsonElement project = await WaitForProjectAsync(client, harness.InstanceId, timeout.Token);
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
        JsonElement check = diagnostics.GetProperty("data").GetProperty("checks")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("checkId").GetString() == checkId);
        Assert.AreEqual("pass", check.GetProperty("status").GetString());
        Assert.IsGreaterThan(0, check.GetProperty("evidence").GetArrayLength());
    }

    private static Dictionary<string, object?> CreateInstanceArguments(Guid instanceId) =>
        new()
        {
            ["instanceId"] = instanceId,
            ["timeoutMs"] = 60_000,
        };

    private static string GetDescriptorDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AviUtl2MCP",
        "v1",
        "instances");

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

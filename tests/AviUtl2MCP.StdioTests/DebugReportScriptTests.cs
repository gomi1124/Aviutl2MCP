using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AviUtl2MCP.StdioTests;

[TestClass]
public sealed class DebugReportScriptTests
{
    private const string CorrelationId = "019beabc-49b0-7000-8000-000000000010";

    [TestMethod]
    public async Task GenerateReportAggregatesCorrelatedMaskedEvidenceAndHashes()
    {
        // Arrange
        string repositoryRoot = FindRepositoryRoot();
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "AviUtl2MCP.Tests",
            Guid.NewGuid().ToString("N"));
        string outputDirectory = Path.Combine(temporaryDirectory, "reports");
        Directory.CreateDirectory(temporaryDirectory);
        string serverLogPath = Path.Combine(temporaryDirectory, "server.jsonl");
        string bridgeLogPath = Path.Combine(temporaryDirectory, "bridge.log");
        string aviUtlLogPath = Path.Combine(temporaryDirectory, "aviutl.log");
        string beforePreviewPath = Path.Combine(temporaryDirectory, "before.bin");
        string afterPreviewPath = Path.Combine(temporaryDirectory, "after.bin");
        string artifactPath = Path.Combine(temporaryDirectory, "trace.bin");
        string checksPath = Path.Combine(temporaryDirectory, "checks.json");
        string versionsPath = Path.Combine(temporaryDirectory, "versions.json");
        File.WriteAllLines(serverLogPath, [
            $"{{\"correlationId\":\"{CorrelationId}\",\"message\":\"token=server-secret ready\"}}",
            "unrelated server line",
        ], new UTF8Encoding(false));
        File.WriteAllLines(bridgeLogPath, [
            $"[correlationId={CorrelationId}] Bearer bridge-secret C:\\Users\\alice\\project.aup",
        ], new UTF8Encoding(false));
        File.WriteAllLines(aviUtlLogPath, [
            $"[correlationId={CorrelationId}] password=aviutl-secret",
        ], new UTF8Encoding(false));
        File.WriteAllBytes(beforePreviewPath, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(afterPreviewPath, [0x01, 0x02, 0x04]);
        File.WriteAllBytes(artifactPath, [0x05, 0x06]);
        File.WriteAllText(
            checksPath,
            "[{\"name\":\"bridge\",\"status\":\"pass\",\"evidence\":[\"token=evidence-secret ready\"]}]",
            new UTF8Encoding(false));
        File.WriteAllText(
            versionsPath,
            "{\"aviutl2\":\"2.0-test\",\"psdtoolkit2\":\"2-test\",\"gcmzdrops\":\"1-test\"}",
            new UTF8Encoding(false));

        try
        {
            // Act
            ProcessResult result = await RunScriptAsync(repositoryRoot, [
                "-CorrelationId", CorrelationId,
                "-OutputDirectory", outputDirectory,
                "-Command", "dotnet test token=command-secret",
                "-BeforeRevision", "revision-before",
                "-AfterRevision", "revision-after",
                "-BeforePreviewPath", beforePreviewPath,
                "-AfterPreviewPath", afterPreviewPath,
                "-ServerLogPath", serverLogPath,
                "-BridgeLogPath", bridgeLogPath,
                "-AviUtlLogPath", aviUtlLogPath,
                "-ArtifactPath", artifactPath,
                "-ChecksPath", checksPath,
                "-ComponentVersionsPath", versionsPath,
                "-LaunchedProcessId", "1234",
                "-MaxLogLines", "2",
                "-RepositoryRoot", repositoryRoot,
            ]);

            // Assert
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            string reportPath = Path.Combine(outputDirectory, CorrelationId, "debug-report.json");
            Assert.IsTrue(File.Exists(reportPath));
            Assert.AreEqual(reportPath, result.StandardOutput.Trim());
            byte[] reportBytes = File.ReadAllBytes(reportPath);
            Assert.IsFalse(reportBytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            string reportText = Encoding.UTF8.GetString(reportBytes);
            Assert.IsFalse(reportText.Contains("server-secret", StringComparison.Ordinal));
            Assert.IsFalse(reportText.Contains("bridge-secret", StringComparison.Ordinal));
            Assert.IsFalse(reportText.Contains("aviutl-secret", StringComparison.Ordinal));
            Assert.IsFalse(reportText.Contains("evidence-secret", StringComparison.Ordinal));
            Assert.IsFalse(reportText.Contains("command-secret", StringComparison.Ordinal));
            Assert.IsFalse(reportText.Contains("alice", StringComparison.Ordinal));
            Assert.IsTrue(reportText.Contains("[USER]", StringComparison.Ordinal));
            Assert.IsFalse(reportText.Contains(temporaryDirectory, StringComparison.OrdinalIgnoreCase));

            using JsonDocument document = JsonDocument.Parse(reportText);
            JsonElement root = document.RootElement;
            Assert.AreEqual("1.0", root.GetProperty("schemaVersion").GetString());
            Assert.AreEqual(CorrelationId, root.GetProperty("correlationId").GetString());
            Assert.AreEqual("passed", root.GetProperty("status").GetString());
            Assert.AreEqual("revision-before", root.GetProperty("revisions").GetProperty("before").GetString());
            Assert.AreEqual("revision-after", root.GetProperty("revisions").GetProperty("after").GetString());
            Assert.IsTrue(root.GetProperty("revisions").GetProperty("changed").GetBoolean());
            Assert.IsTrue(root.GetProperty("previews").GetProperty("hashChanged").GetBoolean());
            Assert.AreNotEqual(
                root.GetProperty("previews").GetProperty("before").GetProperty("sha256").GetString(),
                root.GetProperty("previews").GetProperty("after").GetProperty("sha256").GetString());
            Assert.AreEqual(
                "2-test",
                root.GetProperty("versions").GetProperty("components").GetProperty("psdtoolkit2").GetString());
            Assert.AreEqual(
                1,
                root.GetProperty("logs").GetProperty("server").GetProperty("entries").GetArrayLength());
            JsonElement serverLogFile = root
                .GetProperty("logs")
                .GetProperty("server")
                .GetProperty("files")[0];
            Assert.AreEqual(JsonValueKind.Object, serverLogFile.ValueKind);
            Assert.AreEqual("server.jsonl", serverLogFile.GetProperty("name").GetString());
            Assert.IsTrue(serverLogFile.GetProperty("byteLength").GetInt64() > 0);
            Assert.AreEqual(
                1234,
                root.GetProperty("cleanupScope").GetProperty("launchedProcessIds")[0].GetInt32());
            Assert.AreEqual(3, root.GetProperty("artifacts").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GenerateReportAcceptsSingleArtifact()
    {
        // Arrange
        const string correlationId = "019beabc-49b0-7000-8000-000000000011";
        string repositoryRoot = FindRepositoryRoot();
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "AviUtl2MCP.Tests",
            Guid.NewGuid().ToString("N"));
        string outputDirectory = Path.Combine(temporaryDirectory, "reports");
        string artifactPath = Path.Combine(temporaryDirectory, "single.bin");
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllBytes(artifactPath, [0x01]);

        try
        {
            // Act
            ProcessResult result = await RunScriptAsync(repositoryRoot, [
                "-CorrelationId", correlationId,
                "-OutputDirectory", outputDirectory,
                "-ArtifactPath", artifactPath,
                "-RepositoryRoot", repositoryRoot,
            ]);

            // Assert
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            string reportPath = Path.Combine(outputDirectory, correlationId, "debug-report.json");
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.AreEqual(1, document.RootElement.GetProperty("artifacts").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RejectNonVersionSevenCorrelationBeforeCreatingArtifacts()
    {
        // Arrange
        string repositoryRoot = FindRepositoryRoot();
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "AviUtl2MCP.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            // Act
            ProcessResult result = await RunScriptAsync(repositoryRoot, [
                "-CorrelationId", Guid.NewGuid().ToString("D"),
                "-OutputDirectory", temporaryDirectory,
                "-RepositoryRoot", repositoryRoot,
            ]);

            // Assert
            Assert.AreNotEqual(0, result.ExitCode);
            Assert.IsFalse(Directory.Exists(temporaryDirectory));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static async Task<ProcessResult> RunScriptAsync(
        string repositoryRoot,
        IReadOnlyList<string> scriptArguments)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "scripts", "New-DebugReport.ps1"));
        foreach (string argument in scriptArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        Assert.IsTrue(process.Start());
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AviUtl2MCP.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("AviUtl2MCP repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

using System.Text.Json;
using AviUtl2MCP.Server.Logging;
using Microsoft.Extensions.Logging;

namespace AviUtl2MCP.StdioTests;

[TestClass]
public sealed class JsonLineLoggerTests
{
    private static readonly Action<ILogger, string, double, string, Exception?> LogCompletedOperation =
        LoggerMessage.Define<string, double, string>(
            LogLevel.Information,
            new EventId(1201, "request.completed"),
            "Operation completed with token={Token} durationMs={DurationMs} resultCode={ResultCode}");

    [TestMethod]
    public void WritePreservesCorrelationAndMasksSecretsInBothDestinations()
    {
        // Arrange
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "AviUtl2MCP.Tests",
            Guid.NewGuid().ToString("N"));
        string logFilePath = Path.Combine(temporaryDirectory, "server.jsonl");
        StringWriter standardError = new();
        DateTimeOffset timestamp = new(2026, 7, 19, 1, 2, 3, TimeSpan.Zero);

        try
        {
            {
                using JsonLineLoggerProvider provider = new(
                    logFilePath,
                    standardError,
                    new FixedTimeProvider(timestamp));
                using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Trace);
                    builder.AddProvider(provider);
                });
                ILogger logger = loggerFactory.CreateLogger("AviUtl2MCP.TestComponent");

                using (logger.BeginScope(new Dictionary<string, object?>
                       {
                           ["correlationId"] = "019beabc-49b0-7000-8000-000000000001",
                           ["instanceId"] = "019beabc-49b0-7000-8000-000000000002",
                           ["operation"] = "status.get",
                           ["authorization"] = "Bearer scope-secret",
                           ["projectPath"] = @"C:\Users\alice\project.aup",
                       }))
                {
                    // Act
                    LogCompletedOperation(logger, "message-secret", 12.5, "ok", null);
                }
            }

            // Assert
            string standardErrorLine = standardError.ToString().TrimEnd();
            string fileLine = File.ReadAllText(logFilePath).TrimEnd();
            Assert.AreEqual(standardErrorLine, fileLine);
            Assert.IsFalse(standardErrorLine.Contains("scope-secret", StringComparison.Ordinal));
            Assert.IsFalse(standardErrorLine.Contains("message-secret", StringComparison.Ordinal));
            Assert.IsFalse(standardErrorLine.Contains("alice", StringComparison.Ordinal));

            using JsonDocument document = JsonDocument.Parse(standardErrorLine);
            JsonElement root = document.RootElement;
            Assert.AreEqual(timestamp, root.GetProperty("timestamp").GetDateTimeOffset());
            Assert.AreEqual("Information", root.GetProperty("level").GetString());
            Assert.AreEqual("AviUtl2MCP.TestComponent", root.GetProperty("component").GetString());
            Assert.AreEqual(1201, root.GetProperty("eventId").GetInt32());
            Assert.AreEqual("request.completed", root.GetProperty("eventName").GetString());
            Assert.AreEqual(
                "019beabc-49b0-7000-8000-000000000001",
                root.GetProperty("correlationId").GetString());
            Assert.AreEqual(
                "019beabc-49b0-7000-8000-000000000002",
                root.GetProperty("instanceId").GetString());
            Assert.AreEqual("status.get", root.GetProperty("operation").GetString());
            Assert.AreEqual(12.5, root.GetProperty("durationMs").GetDouble());
            Assert.AreEqual("ok", root.GetProperty("resultCode").GetString());
            Assert.AreEqual("[REDACTED]", root.GetProperty("properties").GetProperty("authorization").GetString());
            Assert.AreEqual("[REDACTED]", root.GetProperty("properties").GetProperty("Token").GetString());
            Assert.AreEqual(
                @"C:\Users\[USER]\project.aup",
                root.GetProperty("properties").GetProperty("projectPath").GetString());
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
    [DoNotParallelize]
    public void CreateDefaultUsesProcessScopedJsonLineFile()
    {
        // Arrange
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "AviUtl2MCP.Tests",
            Guid.NewGuid().ToString("N"));
        string? originalDirectory = Environment.GetEnvironmentVariable("AVIUTL2_MCP_LOG_DIRECTORY");

        try
        {
            Environment.SetEnvironmentVariable("AVIUTL2_MCP_LOG_DIRECTORY", temporaryDirectory);

            // Act
            using JsonLineLoggerProvider provider = JsonLineLoggerProvider.CreateDefault();

            // Assert
            Assert.IsTrue(File.Exists(Path.Combine(
                temporaryDirectory,
                $"server-{Environment.ProcessId}.jsonl")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AVIUTL2_MCP_LOG_DIRECTORY", originalDirectory);
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}

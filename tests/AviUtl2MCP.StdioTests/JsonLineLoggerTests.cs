using System.Text.Json;
using AviUtl2MCP.Server.Logging;
using Microsoft.Extensions.Logging;

namespace AviUtl2MCP.StdioTests;

[TestClass]
public sealed class JsonLineLoggerTests
{
    private static readonly Action<ILogger, string, Exception?> LogCompletedOperation =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1201, "request.completed"),
            "Operation completed with token={Token}");

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
                           ["authorization"] = "Bearer scope-secret",
                       }))
                {
                    // Act
                    LogCompletedOperation(logger, "message-secret", null);
                }
            }

            // Assert
            string standardErrorLine = standardError.ToString().TrimEnd();
            string fileLine = File.ReadAllText(logFilePath).TrimEnd();
            Assert.AreEqual(standardErrorLine, fileLine);
            Assert.IsFalse(standardErrorLine.Contains("scope-secret", StringComparison.Ordinal));
            Assert.IsFalse(standardErrorLine.Contains("message-secret", StringComparison.Ordinal));

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
            Assert.AreEqual("[REDACTED]", root.GetProperty("properties").GetProperty("authorization").GetString());
            Assert.AreEqual("[REDACTED]", root.GetProperty("properties").GetProperty("Token").GetString());
        }
        finally
        {
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

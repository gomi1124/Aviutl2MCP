using System.Globalization;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Server.Diagnostics;

namespace AviUtl2MCP.StdioTests;

[TestClass]
public sealed class LogSourceTests
{
    [TestMethod]
    public async Task ServerSourceFiltersAndPagesBoundedJsonLines()
    {
        // Arrange
        using TemporaryDirectory directory = new();
        string logPath = Path.Combine(directory.Path, "server-123.jsonl");
        Guid correlationId = Guid.CreateVersion7();
        string[] lines =
        [
            CreateServerLine("2026-07-19T00:00:00Z", "Information", 10, "started", null, "ready"),
            "{malformed",
            CreateServerLine("2026-07-19T00:00:01Z", "Error", 20, "failed", correlationId, "token=secret first"),
            CreateServerLine("2026-07-19T00:00:02Z", "Error", 21, null, correlationId, "second"),
        ];
        await File.WriteAllLinesAsync(logPath, lines, new UTF8Encoding(false));
        ServerJsonLogSource source = new(logPath);
        LogSourceQuery firstQuery = new(
            [ContractLogLevel.Error],
            DateTimeOffset.Parse("2026-07-19T00:00:00Z", CultureInfo.InvariantCulture),
            correlationId,
            Limit: 1,
            Cursor: null,
            InstanceId: null,
            RequestCorrelationId: Guid.CreateVersion7(),
            Deadline: DateTimeOffset.UtcNow.AddSeconds(10),
            TimeoutMs: 10_000);

        // Act
        LogSourcePage first = await source.ReadAsync(firstQuery, CancellationToken.None);
        LogSourcePage second = await source.ReadAsync(
            firstQuery with { Cursor = first.NextCursor },
            CancellationToken.None);

        // Assert
        Assert.AreEqual(LogSource.Server, source.Source);
        Assert.HasCount(1, first.Entries);
        Assert.IsTrue(first.IsTruncated);
        Assert.AreEqual("1", first.NextCursor);
        Assert.AreEqual("failed", first.Entries[0].EventId);
        Assert.IsFalse(first.Entries[0].Message.Contains("secret", StringComparison.Ordinal));
        Assert.HasCount(1, second.Entries);
        Assert.IsFalse(second.IsTruncated);
        Assert.IsNull(second.NextCursor);
        Assert.AreEqual("21", second.Entries[0].EventId);
        Assert.AreEqual(first.Generation, second.Generation);
    }

    [TestMethod]
    public async Task AviUtlSourceParsesContinuationCorrelationAndKnownPsdLogs()
    {
        // Arrange
        using TemporaryDirectory directory = new();
        Guid correlationId = Guid.CreateVersion7();
        string olderPath = Path.Combine(directory.Path, "aviutl2_2026-07-19_07-14-51-481.log");
        string newerPath = Path.Combine(directory.Path, "aviutl2_2026-07-19_07-16-19-986.log");
        await File.WriteAllLinesAsync(
            olderPath,
            [
                "[07/19 07:14:53] [ERROR] [Exception] can not open file. in Plugin::InputService::openFile() [C:\\ProgramData\\aviutl2\\Script\\PSDToolKit\\6da6efb5ad790000.ptkcache]",
                "  cache trace password=secret",
            ],
            new UTF8Encoding(false));
        await File.WriteAllLinesAsync(
            newerPath,
            [
                $"[07/19 07:16:19] [WARN] [AviUtl2MCP] [correlationId={correlationId:D}] bridge warning",
                "[07/19 07:16:20] [INFO] [Plugin] unrelated",
            ],
            new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(olderPath, new DateTime(2026, 7, 19, 0, 14, 53, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerPath, new DateTime(2026, 7, 19, 0, 16, 20, DateTimeKind.Utc));
        AviUtlLogSource source = new(directory.Path);

        // Act
        LogSourcePage correlated = await source.ReadAsync(
            CreateQuery([ContractLogLevel.Warning], correlationId),
            CancellationToken.None);
        LogSourcePage errors = await source.ReadAsync(
            CreateQuery([ContractLogLevel.Error], null),
            CancellationToken.None);
        IReadOnlyList<KnownLogMatch> knownMatches = KnownLogClassifier.Classify(errors.Entries);

        // Assert
        Assert.AreEqual(LogSource.Aviutl, source.Source);
        Assert.HasCount(1, correlated.Entries);
        Assert.AreEqual(correlationId.ToString("D"), correlated.Entries[0].CorrelationId);
        Assert.HasCount(1, errors.Entries);
        StringAssert.Contains(errors.Entries[0].Message, "cache trace");
        Assert.IsFalse(errors.Entries[0].Message.Contains("secret", StringComparison.Ordinal));
        KnownLogMatch match = knownMatches.Single();
        Assert.AreEqual("psdtoolkit.cache-missing", match.RuleId);
    }

    private static LogSourceQuery CreateQuery(
        IReadOnlyList<ContractLogLevel> levels,
        Guid? correlationId) =>
        new(
            levels,
            null,
            correlationId,
            Limit: 10,
            Cursor: null,
            InstanceId: null,
            RequestCorrelationId: Guid.CreateVersion7(),
            Deadline: DateTimeOffset.UtcNow.AddSeconds(10),
            TimeoutMs: 10_000);

    private static string CreateServerLine(
        string timestamp,
        string level,
        int eventId,
        string? eventName,
        Guid? correlationId,
        string message)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp,
            level,
            component = "fixture",
            eventId,
            eventName,
            correlationId,
            instanceId = (string?)null,
            operation = (string?)null,
            durationMs = (double?)null,
            resultCode = (string?)null,
            message,
            properties = new { },
            exception = (string?)null,
        });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AviUtl2MCP.StdioTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

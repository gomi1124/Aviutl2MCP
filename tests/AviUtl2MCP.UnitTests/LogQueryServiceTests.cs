using System.Globalization;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Application.Errors;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class LogQueryServiceTests
{
    private static readonly byte[] SIGNING_KEY = Enumerable.Range(0, 32)
        .Select(value => (byte)(value + 1))
        .ToArray();
    private static readonly string[] EXPECTED_SERVER_EVENT_IDS = ["server-0", "server-1"];
    private static readonly string[] EXPECTED_BRIDGE_EVENT_IDS = ["bridge-0", "bridge-1"];
    private static readonly string[] EXPECTED_AVIUTL_EVENT_IDS = ["aviutl-0"];

    [TestMethod]
    public void LogCursorCodecRejectsTamperingMismatchAndExpiry()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        LogCursorCodec codec = new(SIGNING_KEY, new FixedTimeProvider(now));
        Guid serverEpoch = Guid.NewGuid();
        LogCursorState state = new(
            serverEpoch,
            new string('a', 64),
            1,
            "bridge:42",
            "generation-1",
            now.AddMinutes(5));
        string cursor = codec.EncodeCursor(state);
        string tamperedCursor = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');

        // Act
        ApplicationResult<LogCursorState> accepted = codec.DecodeCursor(
            cursor,
            new LogCursorBinding(serverEpoch, state.QueryHash));
        ApplicationResult<LogCursorState> tampered = codec.DecodeCursor(
            tamperedCursor,
            new LogCursorBinding(serverEpoch, state.QueryHash));
        ApplicationResult<LogCursorState> mismatch = codec.DecodeCursor(
            cursor,
            new LogCursorBinding(serverEpoch, new string('b', 64)));
        string expiredCursor = codec.EncodeCursor(state with { ExpiresAt = now });
        ApplicationResult<LogCursorState> expired = codec.DecodeCursor(
            expiredCursor,
            new LogCursorBinding(serverEpoch, state.QueryHash));

        // Assert
        Assert.IsTrue(accepted.IsSuccess);
        Assert.AreEqual("bridge:42", accepted.Value!.SourceCursor);
        Assert.AreEqual("cursor_invalid", tampered.Error!.Code);
        Assert.AreEqual("cursor_invalid", mismatch.Error!.Code);
        Assert.AreEqual("cursor_invalid", expired.Error!.Code);
    }

    [TestMethod]
    public async Task ReadAsyncPagesAcrossAllSourcesInStableOrder()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        FakeLogSource server = new(LogSource.Server, "server-generation", CreateEntries("server", 2, now));
        FakeLogSource bridge = new(LogSource.Bridge, "bridge-generation", CreateEntries("bridge", 2, now.AddMinutes(1)));
        FakeLogSource aviutl = new(LogSource.Aviutl, "aviutl-generation", CreateEntries("aviutl", 1, now.AddMinutes(2)));
        LogQueryService service = CreateService(now, server, bridge, aviutl);
        GetLogsInput input = new() { Limit = 2 };
        LogReadContext context = CreateContext(now);

        // Act
        ApplicationResult<LogsData> first = await service.ReadAsync(input, context, CancellationToken.None);
        ApplicationResult<LogsData> second = await service.ReadAsync(
            input with { Cursor = first.Value!.NextCursor },
            context,
            CancellationToken.None);
        ApplicationResult<LogsData> third = await service.ReadAsync(
            input with { Cursor = second.Value!.NextCursor },
            context,
            CancellationToken.None);

        // Assert
        CollectionAssert.AreEqual(EXPECTED_SERVER_EVENT_IDS, GetEventIds(first));
        CollectionAssert.AreEqual(EXPECTED_BRIDGE_EVENT_IDS, GetEventIds(second));
        CollectionAssert.AreEqual(EXPECTED_AVIUTL_EVENT_IDS, GetEventIds(third));
        Assert.IsTrue(first.Value!.IsTruncated);
        Assert.IsTrue(second.Value!.IsTruncated);
        Assert.IsFalse(third.Value!.IsTruncated);
        Assert.IsNull(third.Value.NextCursor);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsChangedGenerationOnResume()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        FakeLogSource server = new(LogSource.Server, "generation-1", CreateEntries("server", 3, now));
        LogQueryService service = CreateService(now, server);
        GetLogsInput input = new() { Sources = [LogSource.Server], Limit = 1 };
        LogReadContext context = CreateContext(now);
        ApplicationResult<LogsData> first = await service.ReadAsync(input, context, CancellationToken.None);
        server.Generation = "generation-2";

        // Act
        ApplicationResult<LogsData> resumed = await service.ReadAsync(
            input with { Cursor = first.Value!.NextCursor },
            context,
            CancellationToken.None);

        // Assert
        Assert.IsFalse(resumed.IsSuccess);
        Assert.AreEqual("cursor_invalid", resumed.Error!.Code);
    }

    [TestMethod]
    public async Task ReadAsyncReturnsPartialLogsAndWarningWhenOneSourceFails()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        FakeLogSource server = new(LogSource.Server, "server", CreateEntries("server", 1, now));
        FakeLogSource bridge = new(
            LogSource.Bridge,
            "bridge",
            [],
            new LogSourceReadException("bridge_unavailable", "bridge failed", canRetry: true));
        LogQueryService service = CreateService(now, server, bridge);
        GetLogsInput input = new() { Sources = [LogSource.Server, LogSource.Bridge], Limit = 10 };

        // Act
        ApplicationResult<LogsData> result = await service.ReadAsync(
            input,
            CreateContext(now),
            CancellationToken.None);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Value!.Entries);
        Assert.HasCount(1, result.Warnings);
        Assert.AreEqual("log_source_unavailable", result.Warnings[0].Code);
    }

    [TestMethod]
    public async Task ReadAsyncReturnsFailureWhenOnlySourceFails()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        FakeLogSource bridge = new(
            LogSource.Bridge,
            "bridge",
            [],
            new LogSourceReadException("bridge_unavailable", "bridge failed", canRetry: true));
        LogQueryService service = CreateService(now, bridge);
        GetLogsInput input = new() { Sources = [LogSource.Bridge], Limit = 10 };

        // Act
        ApplicationResult<LogsData> result = await service.ReadAsync(
            input,
            CreateContext(now),
            CancellationToken.None);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("bridge_unavailable", result.Error!.Code);
        Assert.IsTrue(result.Error.CanRetry);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsCursorWhenQueryChanges()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        FakeLogSource server = new(LogSource.Server, "generation", CreateEntries("server", 3, now));
        LogQueryService service = CreateService(now, server);
        GetLogsInput input = new() { Sources = [LogSource.Server], Limit = 1 };
        LogReadContext context = CreateContext(now);
        ApplicationResult<LogsData> first = await service.ReadAsync(input, context, CancellationToken.None);

        // Act
        ApplicationResult<LogsData> changed = await service.ReadAsync(
            input with
            {
                Cursor = first.Value!.NextCursor,
                Levels = [ContractLogLevel.Error],
            },
            context,
            CancellationToken.None);

        // Assert
        Assert.IsFalse(changed.IsSuccess);
        Assert.AreEqual("cursor_invalid", changed.Error!.Code);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsServiceCursorAfterFiveMinutes()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        FakeLogSource server = new(LogSource.Server, "generation", CreateEntries("server", 3, now));
        GetLogsInput input = new() { Sources = [LogSource.Server], Limit = 1 };
        LogReadContext context = CreateContext(now);
        LogQueryService issuingService = CreateService(now, server);
        ApplicationResult<LogsData> first = await issuingService.ReadAsync(
            input,
            context,
            CancellationToken.None);
        LogQueryService resumedService = CreateService(now.AddMinutes(5), server);

        // Act
        ApplicationResult<LogsData> resumed = await resumedService.ReadAsync(
            input with { Cursor = first.Value!.NextCursor },
            context,
            CancellationToken.None);

        // Assert
        Assert.IsFalse(resumed.IsSuccess);
        Assert.AreEqual("cursor_invalid", resumed.Error!.Code);
    }

    private static LogQueryService CreateService(DateTimeOffset now, params ILogSource[] sources) =>
        new(
            sources,
            new LogCursorCodec(SIGNING_KEY, new FixedTimeProvider(now)),
            new FixedTimeProvider(now));

    private static LogReadContext CreateContext(DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.CreateVersion7(),
            now.AddSeconds(30),
            30_000);

    private static LogEntry[] CreateEntries(
        string source,
        int count,
        DateTimeOffset start) =>
        Enumerable.Range(0, count)
            .Select(index => new LogEntry(
                start.AddSeconds(index),
                nameof(ContractLogLevel.Information),
                source,
                $"{source}-{index.ToString(CultureInfo.InvariantCulture)}",
                null,
                "fixture"))
            .ToArray();

    private static string[] GetEventIds(ApplicationResult<LogsData> result) =>
        result.Value!.Entries.Select(entry => entry.EventId).ToArray();

    private sealed class FakeLogSource(
        LogSource source,
        string generation,
        IReadOnlyList<LogEntry> entries,
        LogSourceReadException? error = null) : ILogSource
    {
        public LogSource Source { get; } = source;

        public string Generation { get; set; } = generation;

        public ValueTask<LogSourcePage> ReadAsync(
            LogSourceQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (error is not null)
            {
                throw error;
            }
            int offset = query.Cursor is null
                ? 0
                : int.Parse(query.Cursor, CultureInfo.InvariantCulture);
            LogEntry[] pageEntries = entries.Skip(offset).Take(query.Limit).ToArray();
            int nextOffset = offset + pageEntries.Length;
            bool isTruncated = nextOffset < entries.Count;
            return ValueTask.FromResult(new LogSourcePage(
                pageEntries,
                isTruncated ? nextOffset.ToString(CultureInfo.InvariantCulture) : null,
                isTruncated,
                Generation));
        }
    }
}

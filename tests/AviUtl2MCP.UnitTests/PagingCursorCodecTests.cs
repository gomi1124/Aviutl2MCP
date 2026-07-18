using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Paging;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class PagingCursorCodecTests
{
    private static readonly byte[] signingKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [TestMethod]
    public void DecodeCursorAcceptsMatchingBinding()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        PagingCursorCodec codec = new(signingKey, new FixedTimeProvider(now));
        PagingCursorState state = CreateState(now.AddMinutes(5));
        PagingCursorBinding binding = new(state.ServerEpoch, state.InstanceId, state.ProjectGeneration, state.QueryHash, state.Revision);
        string cursor = codec.EncodeCursor(state);

        // Act
        ApplicationResult<PagingCursorState> result = codec.DecodeCursor(cursor, binding);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("position-1", result.Value!.Position);
    }

    [TestMethod]
    public void DecodeCursorRejectsTamperingAndExpiry()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        PagingCursorCodec codec = new(signingKey, new FixedTimeProvider(now));
        PagingCursorState expiredState = CreateState(now);
        PagingCursorBinding binding = new(expiredState.ServerEpoch, expiredState.InstanceId, expiredState.ProjectGeneration, expiredState.QueryHash, expiredState.Revision);
        string expiredCursor = codec.EncodeCursor(expiredState);
        string tamperedCursor = expiredCursor[..^1] + (expiredCursor[^1] == 'A' ? 'B' : 'A');

        // Act
        ApplicationResult<PagingCursorState> expired = codec.DecodeCursor(expiredCursor, binding);
        ApplicationResult<PagingCursorState> tampered = codec.DecodeCursor(tamperedCursor, binding);

        // Assert
        Assert.AreEqual("cursor_invalid", expired.Error!.Code);
        Assert.AreEqual("cursor_invalid", tampered.Error!.Code);
    }

    [TestMethod]
    public void DecodeCursorRejectsQueryMismatch()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        PagingCursorCodec codec = new(signingKey, new FixedTimeProvider(now));
        PagingCursorState state = CreateState(now.AddMinutes(5));
        PagingCursorBinding binding = new(state.ServerEpoch, state.InstanceId, state.ProjectGeneration, new string('f', 64), state.Revision);

        // Act
        ApplicationResult<PagingCursorState> result = codec.DecodeCursor(codec.EncodeCursor(state), binding);

        // Assert
        Assert.AreEqual("cursor_invalid", result.Error!.Code);
    }

    private static PagingCursorState CreateState(DateTimeOffset expiresAt)
    {
        return new PagingCursorState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('0', 64),
            new Revision("r1"),
            expiresAt,
            "position-1");
    }
}

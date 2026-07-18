namespace AviUtl2MCP.UnitTests;

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal static class TestTime
{
    public static DateTimeOffset CreateReferenceUtc()
    {
        return new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
    }
}

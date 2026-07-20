namespace AviUtl2MCP.BridgeClient.Protocol;

public static class BridgeProtocol
{
#pragma warning disable CA1707 // Project convention requires SCREAMING_SNAKE_CASE constants.
    public const ushort MAJOR_VERSION = 1;
    public const ushort MINOR_VERSION = 0;
    public const int HEADER_BYTES = 40;
    public const int MAX_JSON_BYTES = 8 * 1024 * 1024;
    public const int MAX_BINARY_BYTES = 16 * 1024 * 1024;
#pragma warning restore CA1707
}

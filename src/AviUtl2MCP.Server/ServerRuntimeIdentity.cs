using System.Security.Cryptography;

namespace AviUtl2MCP.Server;

public sealed class ServerRuntimeIdentity
{
    private const int CURSOR_SIGNING_KEY_BYTES = 32;
    private readonly byte[] _cursorSigningKey = RandomNumberGenerator.GetBytes(CURSOR_SIGNING_KEY_BYTES);

    public ServerRuntimeIdentity()
    {
        ServerVersion = typeof(ServerMarker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        EnvironmentInstanceId = ParseEnvironmentInstanceId(
            Environment.GetEnvironmentVariable("AVIUTL2_MCP_INSTANCE_ID"));
    }

    public Guid ServerEpoch { get; } = Guid.NewGuid();

    public Guid ClientInstanceId { get; } = Guid.NewGuid();

    public string ServerVersion { get; }

    public Guid? EnvironmentInstanceId { get; }

    public ReadOnlyMemory<byte> CursorSigningKey => _cursorSigningKey;

    private static Guid? ParseEnvironmentInstanceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Guid.TryParse(value, out Guid instanceId) && instanceId != Guid.Empty
            ? instanceId
            : throw new InvalidOperationException(
                "AVIUTL2_MCP_INSTANCE_ID must be a non-empty GUID when specified.");
    }
}

using System.Security.Cryptography;

namespace AviUtl2MCP.Server;

public sealed class ServerRuntimeIdentity
{
    private const int CURSOR_SIGNING_KEY_BYTES = 32;
    private const string INSTANCE_VARIABLE = "AVIUTL2_MCP_INSTANCE";
    private const string COMPATIBILITY_INSTANCE_VARIABLE = "AVIUTL2_MCP_INSTANCE_ID";
    private readonly byte[] _cursorSigningKey = RandomNumberGenerator.GetBytes(CURSOR_SIGNING_KEY_BYTES);

    public ServerRuntimeIdentity()
    {
        ServerVersion = typeof(ServerMarker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        EnvironmentInstanceId = ResolveEnvironmentInstanceId();
    }

    public Guid ServerEpoch { get; } = Guid.NewGuid();

    public Guid ClientInstanceId { get; } = Guid.NewGuid();

    public string ServerVersion { get; }

    public Guid? EnvironmentInstanceId { get; }

    public ReadOnlyMemory<byte> CursorSigningKey => _cursorSigningKey;

    private static Guid? ResolveEnvironmentInstanceId()
    {
        Guid? configured = ParseEnvironmentInstanceId(
            INSTANCE_VARIABLE,
            Environment.GetEnvironmentVariable(INSTANCE_VARIABLE));
        Guid? compatibility = ParseEnvironmentInstanceId(
            COMPATIBILITY_INSTANCE_VARIABLE,
            Environment.GetEnvironmentVariable(COMPATIBILITY_INSTANCE_VARIABLE));
        if (configured.HasValue
            && compatibility.HasValue
            && configured != compatibility)
        {
            throw new InvalidOperationException(
                $"{INSTANCE_VARIABLE} and {COMPATIBILITY_INSTANCE_VARIABLE} must identify the same instance when both are specified.");
        }
        return configured ?? compatibility;
    }

    private static Guid? ParseEnvironmentInstanceId(string variableName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Guid.TryParse(value, out Guid instanceId) && instanceId != Guid.Empty
            ? instanceId
            : throw new InvalidOperationException(
                $"{variableName} must be a non-empty GUID when specified.");
    }
}

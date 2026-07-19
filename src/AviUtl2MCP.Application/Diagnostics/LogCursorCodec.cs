using System.Security.Cryptography;
using System.Text;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Serialization;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed record LogCursorState(
    Guid ServerEpoch,
    string QueryHash,
    int SourceIndex,
    string? SourceCursor,
    string? SourceGeneration,
    DateTimeOffset ExpiresAt);

public sealed record LogCursorBinding(Guid ServerEpoch, string QueryHash);

public sealed class LogCursorCodec
{
    private const int MINIMUM_KEY_BYTES = 32;
    private const int MAXIMUM_CURSOR_CHARACTERS = 4096;
    private readonly byte[] _signingKey;
    private readonly TimeProvider _timeProvider;

    public LogCursorCodec(ReadOnlySpan<byte> signingKey, TimeProvider? timeProvider = null)
    {
        if (signingKey.Length < MINIMUM_KEY_BYTES)
        {
            throw new ArgumentException("Log cursor signing key must contain at least 32 bytes.", nameof(signingKey));
        }
        _signingKey = signingKey.ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string EncodeCursor(LogCursorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string payload = ContractJsonSerializer.SerializeContract(state);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = HMACSHA256.HashData(_signingKey, payloadBytes);
        string cursor = $"{EncodeBase64Url(payloadBytes)}.{EncodeBase64Url(signature)}";
        if (cursor.Length > MAXIMUM_CURSOR_CHARACTERS)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Encoded log cursor exceeds the contract limit.");
        }
        return cursor;
    }

    public ApplicationResult<LogCursorState> DecodeCursor(
        string cursor,
        LogCursorBinding expectedBinding)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MAXIMUM_CURSOR_CHARACTERS)
        {
            return CreateInvalidResult("format");
        }
        string[] segments = cursor.Split('.', StringSplitOptions.None);
        if (segments.Length != 2)
        {
            return CreateInvalidResult("format");
        }

        try
        {
            byte[] payloadBytes = DecodeBase64Url(segments[0]);
            byte[] providedSignature = DecodeBase64Url(segments[1]);
            byte[] expectedSignature = HMACSHA256.HashData(_signingKey, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
            {
                return CreateInvalidResult("signature");
            }
            string payload = new UTF8Encoding(false, true).GetString(payloadBytes);
            LogCursorState state = ContractJsonSerializer.DeserializeContract<LogCursorState>(payload);
            if (state.ServerEpoch != expectedBinding.ServerEpoch)
            {
                return CreateInvalidResult("serverEpoch");
            }
            if (!string.Equals(state.QueryHash, expectedBinding.QueryHash, StringComparison.Ordinal))
            {
                return CreateInvalidResult("query");
            }
            if (state.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                return CreateInvalidResult("expired");
            }
            return ApplicationResult.Success(state);
        }
        catch (Exception exception) when (
            exception is FormatException
            or DecoderFallbackException
            or System.Text.Json.JsonException
            or ArgumentException)
        {
            return CreateInvalidResult("payload");
        }
    }

    private static ApplicationResult<LogCursorState> CreateInvalidResult(string reason) =>
        ApplicationResult.Failure<LogCursorState>(ApplicationErrors.CreateCursorInvalid(reason));

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        int padding = (4 - (base64.Length % 4)) % 4;
        byte[] decoded = Convert.FromBase64String(base64 + new string('=', padding));
        if (!string.Equals(EncodeBase64Url(decoded), value, StringComparison.Ordinal))
        {
            throw new FormatException("Log cursor contains a non-canonical base64url segment.");
        }
        return decoded;
    }
}

using System.Security.Cryptography;
using System.Text;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Serialization;

namespace AviUtl2MCP.Application.Paging;

public sealed class PagingCursorCodec
{
    private const int MINIMUM_KEY_BYTES = 32;
    private const int MAXIMUM_CURSOR_CHARACTERS = 4096;
    private readonly byte[] signingKey;
    private readonly TimeProvider timeProvider;

    public PagingCursorCodec(ReadOnlySpan<byte> signingKey, TimeProvider? timeProvider = null)
    {
        if (signingKey.Length < MINIMUM_KEY_BYTES)
        {
            throw new ArgumentException("Paging cursor signing key must contain at least 32 bytes.", nameof(signingKey));
        }

        this.signingKey = signingKey.ToArray();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string EncodeCursor(PagingCursorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string payload = ContractJsonSerializer.SerializeContract(state);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = HMACSHA256.HashData(signingKey, payloadBytes);
        string cursor = $"{EncodeBase64Url(payloadBytes)}.{EncodeBase64Url(signature)}";
        if (cursor.Length > MAXIMUM_CURSOR_CHARACTERS)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Encoded paging cursor exceeds the contract limit.");
        }

        return cursor;
    }

    public ApplicationResult<PagingCursorState> DecodeCursor(
        string cursor,
        PagingCursorBinding expectedBinding)
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
            byte[] expectedSignature = HMACSHA256.HashData(signingKey, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
            {
                return CreateInvalidResult("signature");
            }

            string payload = new UTF8Encoding(false, true).GetString(payloadBytes);
            PagingCursorState state = ContractJsonSerializer.DeserializeContract<PagingCursorState>(payload);
            string? mismatch = FindBindingMismatch(state, expectedBinding);
            if (mismatch is not null)
            {
                return CreateInvalidResult(mismatch);
            }

            if (state.ExpiresAt <= timeProvider.GetUtcNow())
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

    private static string? FindBindingMismatch(PagingCursorState state, PagingCursorBinding expectedBinding)
    {
        if (state.ServerEpoch != expectedBinding.ServerEpoch)
        {
            return "serverEpoch";
        }

        if (state.InstanceId != expectedBinding.InstanceId)
        {
            return "instanceId";
        }

        if (state.ProjectGeneration != expectedBinding.ProjectGeneration)
        {
            return "projectGeneration";
        }

        if (!string.Equals(state.QueryHash, expectedBinding.QueryHash, StringComparison.Ordinal))
        {
            return "query";
        }

        return state.Revision != expectedBinding.Revision ? "revision" : null;
    }

    private static ApplicationResult<PagingCursorState> CreateInvalidResult(string reason)
    {
        return ApplicationResult.Failure<PagingCursorState>(ApplicationErrors.CreateCursorInvalid(reason));
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        int padding = (4 - (base64.Length % 4)) % 4;
        byte[] decoded = Convert.FromBase64String(base64 + new string('=', padding));
        if (!string.Equals(EncodeBase64Url(decoded), value, StringComparison.Ordinal))
        {
            throw new FormatException("Paging cursor contains a non-canonical base64url segment.");
        }
        return decoded;
    }
}

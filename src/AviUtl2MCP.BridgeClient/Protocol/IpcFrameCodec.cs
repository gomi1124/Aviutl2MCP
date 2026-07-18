using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AviUtl2MCP.BridgeClient.Protocol;

public static class IpcFrameCodec
{
    private static readonly UTF8Encoding strictUtf8 = new(false, true);

    public static IpcEncodedFrame EncodeFrame(
        IpcMessageKind messageKind,
        IpcFrameOption options,
        Guid requestId,
        ReadOnlySpan<byte> jsonBytes,
        ReadOnlySpan<byte> binaryBytes)
    {
        ValidateUtf8(jsonBytes);
        IpcFrameHeader header = new(
            messageKind,
            options,
            requestId,
            checked((uint)jsonBytes.Length),
            checked((ulong)binaryBytes.Length));
        byte[] headerBytes = IpcHeaderCodec.EncodeHeader(header);
        byte[] frameBytes = GC.AllocateUninitializedArray<byte>(
            checked(BridgeProtocol.HEADER_BYTES + jsonBytes.Length + binaryBytes.Length));
        headerBytes.CopyTo(frameBytes, 0);
        jsonBytes.CopyTo(frameBytes.AsSpan(BridgeProtocol.HEADER_BYTES));
        binaryBytes.CopyTo(frameBytes.AsSpan(BridgeProtocol.HEADER_BYTES + jsonBytes.Length));
        return new IpcEncodedFrame(frameBytes, CalculatePayloadHash(header, jsonBytes, binaryBytes));
    }

    public static async ValueTask<IpcFrame> DecodeFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] headerBytes = new byte[BridgeProtocol.HEADER_BYTES];
        await ReadExactlyAsync(stream, headerBytes, cancellationToken).ConfigureAwait(false);
        IpcFrameHeader header = IpcHeaderCodec.DecodeHeader(headerBytes);

        byte[] jsonBytes = GC.AllocateUninitializedArray<byte>(checked((int)header.JsonLength));
        byte[] binaryBytes = GC.AllocateUninitializedArray<byte>(checked((int)header.BinaryLength));
        await ReadExactlyAsync(stream, jsonBytes, cancellationToken).ConfigureAwait(false);
        await ReadExactlyAsync(stream, binaryBytes, cancellationToken).ConfigureAwait(false);
        ValidateUtf8(jsonBytes);
        return new IpcFrame(
            header,
            jsonBytes,
            binaryBytes,
            CalculatePayloadHash(header, jsonBytes, binaryBytes));
    }

    public static string CalculatePayloadHash(
        IpcFrameHeader header,
        ReadOnlySpan<byte> jsonBytes,
        ReadOnlySpan<byte> binaryBytes)
    {
        if (header.JsonLength != jsonBytes.Length || header.BinaryLength != (ulong)binaryBytes.Length)
        {
            throw new ArgumentException("Header lengths do not match the frame body.", nameof(header));
        }

        Span<byte> prefix = stackalloc byte[6];
        prefix[0] = checked((byte)BridgeProtocol.MAJOR_VERSION);
        prefix[1] = (byte)header.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[2..], header.JsonLength);
        Span<byte> binaryLength = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(binaryLength, header.BinaryLength);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix);
        hash.AppendData(jsonBytes);
        hash.AppendData(binaryLength);
        hash.AppendData(binaryBytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[bytesRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"IPC frame ended after {bytesRead} of {buffer.Length} required bytes.");
            }

            bytesRead = checked(bytesRead + read);
        }
    }

    private static void ValidateUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = strictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("IPC JSON payload is not valid UTF-8.", exception);
        }
    }
}

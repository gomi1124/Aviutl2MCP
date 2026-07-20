using System.Buffers.Binary;

namespace AviUtl2MCP.BridgeClient.Protocol;

public enum IpcMessageKind : byte
{
    ClientHello = 1,
    ServerHello = 2,
    Request = 3,
    Response = 4,
    Cancel = 5,
    CancelAck = 6,
    Ping = 7,
    Pong = 8,
    Close = 9,
}

[Flags]
public enum IpcFrameOption : byte
{
    None = 0,
    HasBinary = 1 << 0,
    ErrorResponse = 1 << 1,
    PartialResponse = 1 << 2,
}

public readonly record struct IpcFrameHeader(
    IpcMessageKind MessageKind,
    IpcFrameOption Flags,
    Guid RequestId,
    uint JsonLength,
    ulong BinaryLength);

public static class IpcHeaderCodec
{
    private static ReadOnlySpan<byte> MagicBytes => "A2MP"u8;

    public static byte[] EncodeHeader(IpcFrameHeader header)
    {
        ValidateHeader(header);

        byte[] bytes = new byte[BridgeProtocol.HEADER_BYTES];
        MagicBytes.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), BridgeProtocol.HEADER_BYTES);
        bytes[6] = checked((byte)BridgeProtocol.MAJOR_VERSION);
        bytes[7] = checked((byte)BridgeProtocol.MINOR_VERSION);
        bytes[8] = (byte)header.MessageKind;
        bytes[9] = (byte)header.Flags;
        header.RequestId.TryWriteBytes(bytes.AsSpan(12, 16), bigEndian: true, out int bytesWritten);
        if (bytesWritten != 16)
        {
            throw new InvalidOperationException("Request ID did not encode to 16 bytes.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), header.JsonLength);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32, 8), header.BinaryLength);
        return bytes;
    }

    public static IpcFrameHeader DecodeHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != BridgeProtocol.HEADER_BYTES)
        {
            throw new ArgumentException("IPC header must be exactly 40 bytes.", nameof(bytes));
        }

        if (!bytes[..4].SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("IPC header magic is invalid.");
        }

        ushort headerBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2));
        if (headerBytes != BridgeProtocol.HEADER_BYTES)
        {
            throw new InvalidDataException("IPC header size is unsupported.");
        }

        if (bytes[6] != BridgeProtocol.MAJOR_VERSION || bytes[7] != BridgeProtocol.MINOR_VERSION)
        {
            throw new InvalidDataException("IPC protocol version is unsupported.");
        }

        if (bytes[10] != 0 || bytes[11] != 0)
        {
            throw new InvalidDataException("IPC reserved header bytes must be zero.");
        }

        IpcFrameHeader header = new(
            (IpcMessageKind)bytes[8],
            (IpcFrameOption)bytes[9],
            new Guid(bytes.Slice(12, 16), bigEndian: true),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32, 8)));
        ValidateHeader(header);
        return header;
    }

    private static void ValidateHeader(IpcFrameHeader header)
    {
        if (!Enum.IsDefined(header.MessageKind))
        {
            throw new ArgumentOutOfRangeException(nameof(header), "Message kind is unknown.");
        }

        const IpcFrameOption knownFlags = IpcFrameOption.HasBinary
            | IpcFrameOption.ErrorResponse
            | IpcFrameOption.PartialResponse;
        if ((header.Flags & ~knownFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(header), "Frame flags contain unknown bits.");
        }

        if (header.RequestId == Guid.Empty && header.MessageKind != IpcMessageKind.Close)
        {
            throw new ArgumentException("Only Close may use a zero request ID.", nameof(header));
        }

        if (header.JsonLength > BridgeProtocol.MAX_JSON_BYTES)
        {
            throw new ArgumentOutOfRangeException(nameof(header), "JSON payload exceeds the protocol limit.");
        }

        if (header.BinaryLength > BridgeProtocol.MAX_BINARY_BYTES)
        {
            throw new ArgumentOutOfRangeException(nameof(header), "Binary payload exceeds the protocol limit.");
        }

        bool hasBinaryFlag = (header.Flags & IpcFrameOption.HasBinary) != 0;
        if (hasBinaryFlag != (header.BinaryLength > 0))
        {
            throw new ArgumentException("Binary length and HasBinary flag do not match.", nameof(header));
        }
    }
}

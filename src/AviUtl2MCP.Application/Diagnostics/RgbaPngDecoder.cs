using System.Buffers.Binary;
using System.IO.Compression;

namespace AviUtl2MCP.Application.Diagnostics;

internal sealed record DecodedRgbaImage(int Width, int Height, byte[] Pixels);

internal static class RgbaPngDecoder
{
    private const int MaximumDimension = 4096;
    private const int MaximumCompressedBytes = 16 * 1024 * 1024;
    private static ReadOnlySpan<byte> Signature => [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
    ];
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static DecodedRgbaImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Preview is not a PNG file.");
        }

        int offset = Signature.Length;
        int? width = null;
        int? height = null;
        bool hasEnded = false;
        bool hasIdat = false;
        bool hasFinishedIdat = false;
        using MemoryStream compressed = new();
        while (offset < png.Length)
        {
            if (png.Length - offset < 12)
            {
                throw new InvalidDataException("PNG chunk header is truncated.");
            }
            uint chunkLengthValue = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (chunkLengthValue > int.MaxValue)
            {
                throw new InvalidDataException("PNG chunk is too large.");
            }
            int chunkLength = (int)chunkLengthValue;
            int totalChunkBytes = checked(chunkLength + 12);
            if (png.Length - offset < totalChunkBytes)
            {
                throw new InvalidDataException("PNG chunk is truncated.");
            }

            ReadOnlySpan<byte> chunkType = png.Slice(offset + 4, 4);
            ReadOnlySpan<byte> chunkData = png.Slice(offset + 8, chunkLength);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + chunkLength, 4));
            if (CalculateCrc(chunkType, chunkData) != expectedCrc)
            {
                throw new InvalidDataException("PNG chunk CRC is invalid.");
            }

            string chunkName = System.Text.Encoding.ASCII.GetString(chunkType);
            switch (chunkName)
            {
                case "IHDR":
                    if (width.HasValue || offset != Signature.Length || chunkLength != 13)
                    {
                        throw new InvalidDataException("PNG IHDR is invalid.");
                    }
                    uint widthValue = BinaryPrimitives.ReadUInt32BigEndian(chunkData);
                    uint heightValue = BinaryPrimitives.ReadUInt32BigEndian(chunkData[4..]);
                    if (widthValue is 0 or > MaximumDimension || heightValue is 0 or > MaximumDimension)
                    {
                        throw new InvalidDataException("Preview dimensions are outside supported limits.");
                    }
                    if (chunkData[8] != 8
                        || chunkData[9] != 6
                        || chunkData[10] != 0
                        || chunkData[11] != 0
                        || chunkData[12] != 0)
                    {
                        throw new InvalidDataException("Preview PNG must be non-interlaced 8-bit RGBA.");
                    }
                    width = (int)widthValue;
                    height = (int)heightValue;
                    break;

                case "IDAT":
                    if (!width.HasValue || hasFinishedIdat)
                    {
                        throw new InvalidDataException("PNG IDAT order is invalid.");
                    }
                    hasIdat = true;
                    if (compressed.Length + chunkData.Length > MaximumCompressedBytes)
                    {
                        throw new InvalidDataException("Preview PNG compressed data exceeds 16 MiB.");
                    }
                    compressed.Write(chunkData);
                    break;

                case "IEND":
                    if (!width.HasValue || !hasIdat || chunkLength != 0)
                    {
                        throw new InvalidDataException("PNG IEND is invalid.");
                    }
                    hasEnded = true;
                    offset += totalChunkBytes;
                    if (offset != png.Length)
                    {
                        throw new InvalidDataException("PNG contains trailing data.");
                    }
                    break;

                case "PLTE":
                    if (!width.HasValue || hasIdat)
                    {
                        throw new InvalidDataException("PNG PLTE order is invalid.");
                    }
                    break;

                default:
                    if ((chunkType[0] & 0x20) == 0)
                    {
                        throw new InvalidDataException($"Unsupported critical PNG chunk: {chunkName}.");
                    }
                    if (hasIdat)
                    {
                        hasFinishedIdat = true;
                    }
                    break;
            }
            if (hasEnded)
            {
                break;
            }
            offset += totalChunkBytes;
        }

        if (!hasEnded || !width.HasValue || !height.HasValue)
        {
            throw new InvalidDataException("PNG is incomplete.");
        }
        return DecodePixels(width.Value, height.Value, compressed.ToArray());
    }

    private static DecodedRgbaImage DecodePixels(int width, int height, byte[] compressed)
    {
        int stride = checked(width * 4);
        int filteredLength = checked((stride + 1) * height);
        byte[] filtered = new byte[filteredLength];
        using MemoryStream input = new(compressed, writable: false);
        using ZLibStream zlib = new(input, CompressionMode.Decompress);
        int totalRead = 0;
        while (totalRead < filtered.Length)
        {
            int read = zlib.Read(filtered, totalRead, filtered.Length - totalRead);
            if (read == 0)
            {
                throw new InvalidDataException("PNG pixel data is truncated.");
            }
            totalRead += read;
        }
        if (zlib.ReadByte() != -1)
        {
            throw new InvalidDataException("PNG pixel data exceeds the declared dimensions.");
        }

        byte[] pixels = new byte[checked(stride * height)];
        int filteredOffset = 0;
        for (int row = 0; row < height; row++)
        {
            byte filter = filtered[filteredOffset++];
            if (filter > 4)
            {
                throw new InvalidDataException("PNG row uses an unsupported filter.");
            }
            int rowOffset = row * stride;
            int previousRowOffset = rowOffset - stride;
            for (int columnByte = 0; columnByte < stride; columnByte++)
            {
                byte source = filtered[filteredOffset++];
                byte left = columnByte >= 4 ? pixels[rowOffset + columnByte - 4] : (byte)0;
                byte above = row > 0 ? pixels[previousRowOffset + columnByte] : (byte)0;
                byte upperLeft = row > 0 && columnByte >= 4
                    ? pixels[previousRowOffset + columnByte - 4]
                    : (byte)0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    4 => Paeth(left, above, upperLeft),
                    _ => throw new InvalidDataException("PNG filter is invalid."),
                };
                pixels[rowOffset + columnByte] = unchecked((byte)(source + predictor));
            }
        }
        return new DecodedRgbaImage(width, height, pixels);
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        int prediction = left + above - upperLeft;
        int leftDistance = Math.Abs(prediction - left);
        int aboveDistance = Math.Abs(prediction - above);
        int upperLeftDistance = Math.Abs(prediction - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left
            : aboveDistance <= upperLeftDistance
                ? above
                : upperLeft;
    }

    private static uint CalculateCrc(ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> chunkData)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in chunkType)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }
        foreach (byte value in chunkData)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            }
            table[index] = value;
        }
        return table;
    }
}

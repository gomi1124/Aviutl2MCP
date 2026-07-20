using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class BeforeAfterVerifierTests
{
    [TestMethod]
    public void VerifyIdenticalPixelsAndRevisionReportNoDifference()
    {
        // Arrange
        byte[] pixels = [
            0, 0, 0, 255, 255, 0, 0, 255,
            0, 255, 0, 255, 0, 0, 255, 255,
        ];
        byte[] png = CreateRgbaPng(2, 2, pixels);
        Revision revision = new("server:project:1");

        // Act
        BeforeAfterVerification result = BeforeAfterVerifier.Verify(revision, revision, png, png);

        // Assert
        Assert.IsFalse(result.IsRevisionChanged);
        Assert.IsFalse(result.Preview.IsDifferent);
        Assert.IsFalse(result.Preview.AreDimensionsDifferent);
        Assert.AreEqual(0L, result.Preview.ChangedPixelCount);
        Assert.AreEqual(0.0, result.Preview.ChangedPixelRatio);
        Assert.AreEqual(0, result.Preview.MaximumChannelDelta);
        Assert.AreEqual(result.Preview.BeforePixelSha256, result.Preview.AfterPixelSha256);
    }

    [TestMethod]
    public void VerifyChangedPixelAndRevisionReportsQuantifiedDifference()
    {
        // Arrange
        byte[] beforePixels = [
            0, 0, 0, 255, 255, 0, 0, 255,
            0, 255, 0, 255, 0, 0, 255, 255,
        ];
        byte[] afterPixels = (byte[])beforePixels.Clone();
        afterPixels[8] = 64;

        // Act
        BeforeAfterVerification result = BeforeAfterVerifier.Verify(
            new Revision("server:project:1"),
            new Revision("server:project:2"),
            CreateRgbaPng(2, 2, beforePixels),
            CreateRgbaPng(2, 2, afterPixels));

        // Assert
        Assert.IsTrue(result.IsRevisionChanged);
        Assert.IsTrue(result.Preview.IsDifferent);
        Assert.AreEqual(1L, result.Preview.ChangedPixelCount);
        Assert.AreEqual(0.25, result.Preview.ChangedPixelRatio);
        Assert.AreEqual(64, result.Preview.MaximumChannelDelta);
        Assert.AreEqual(4.0, result.Preview.MeanAbsoluteChannelDelta);
        Assert.AreNotEqual(result.Preview.BeforePixelSha256, result.Preview.AfterPixelSha256);
    }

    [TestMethod]
    public void VerifyDimensionChangeDoesNotInventComparablePixelCounts()
    {
        // Arrange
        byte[] before = CreateRgbaPng(1, 1, [1, 2, 3, 255]);
        byte[] after = CreateRgbaPng(2, 1, [1, 2, 3, 255, 1, 2, 3, 255]);

        // Act
        BeforeAfterVerification result = BeforeAfterVerifier.Verify(
            new Revision("server:project:1"),
            new Revision("server:project:1"),
            before,
            after);

        // Assert
        Assert.IsTrue(result.Preview.IsDifferent);
        Assert.IsTrue(result.Preview.AreDimensionsDifferent);
        Assert.IsNull(result.Preview.ChangedPixelCount);
        Assert.IsNull(result.Preview.ChangedPixelRatio);
        Assert.AreEqual(1, result.Preview.BeforeWidth);
        Assert.AreEqual(2, result.Preview.AfterWidth);
    }

    [TestMethod]
    public void VerifyRejectsCorruptCrcAndUnsupportedFilter()
    {
        // Arrange
        byte[] corruptCrc = CreateRgbaPng(1, 1, [1, 2, 3, 255]);
        corruptCrc[41] ^= 0x01;
        byte[] unsupportedFilter = CreateRgbaPng(1, 1, [1, 2, 3, 255], filter: 5);

        // Act / Assert
        Assert.ThrowsExactly<InvalidDataException>(() => BeforeAfterVerifier.Verify(
            new Revision("server:project:1"),
            new Revision("server:project:2"),
            corruptCrc,
            corruptCrc));
        Assert.ThrowsExactly<InvalidDataException>(() => BeforeAfterVerifier.Verify(
            new Revision("server:project:1"),
            new Revision("server:project:2"),
            unsupportedFilter,
            unsupportedFilter));
    }

    [TestMethod]
    public void VerifyAcceptsEveryPngRowFilter()
    {
        // Arrange
        byte[] pixels = [
            4, 8, 12, 255, 16, 20, 24, 255,
            28, 32, 36, 255, 40, 44, 48, 255,
        ];

        for (byte filter = 0; filter <= 4; filter++)
        {
            byte[] png = CreateRgbaPng(2, 2, pixels, filter);

            // Act
            BeforeAfterVerification result = BeforeAfterVerifier.Verify(
                new Revision("server:project:1"),
                new Revision("server:project:1"),
                png,
                png);

            // Assert
            Assert.IsFalse(result.Preview.IsDifferent, $"PNG filter {filter} changed decoded pixels.");
        }
    }

    private static byte[] CreateRgbaPng(
        int width,
        int height,
        ReadOnlySpan<byte> pixels,
        byte filter = 0)
    {
        int stride = checked(width * 4);
        Assert.AreEqual(stride * height, pixels.Length);
        byte[] filtered = new byte[checked((stride + 1) * height)];
        for (int row = 0; row < height; row++)
        {
            int filteredOffset = row * (stride + 1);
            filtered[filteredOffset] = filter;
            int rowOffset = row * stride;
            int previousRowOffset = rowOffset - stride;
            for (int columnByte = 0; columnByte < stride; columnByte++)
            {
                byte source = pixels[rowOffset + columnByte];
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
                    4 => CalculateTestPaeth(left, above, upperLeft),
                    _ => 0,
                };
                filtered[filteredOffset + 1 + columnByte] = unchecked((byte)(source - predictor));
            }
        }

        byte[] compressed;
        using (MemoryStream compressedStream = new())
        {
            using (ZLibStream zlib = new(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(filtered);
            }
            compressed = compressedStream.ToArray();
        }

        using MemoryStream output = new();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, CalculateTestCrc(typeBytes, data));
        output.Write(crc);
    }

    private static uint CalculateTestCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in type)
        {
            crc = UpdateTestCrc(crc, value);
        }
        foreach (byte value in data)
        {
            crc = UpdateTestCrc(crc, value);
        }
        return ~crc;
    }

    private static uint UpdateTestCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }

    private static int CalculateTestPaeth(int left, int above, int upperLeft)
    {
        int prediction = left + above - upperLeft;
        int leftDistance = Math.Abs(prediction - left);
        int aboveDistance = Math.Abs(prediction - above);
        int upperLeftDistance = Math.Abs(prediction - upperLeft);
        if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
        {
            return left;
        }
        return aboveDistance <= upperLeftDistance ? above : upperLeft;
    }
}

using System.Text;
using AviUtl2MCP.BridgeClient.Protocol;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class IpcFrameCodecTests
{
    [TestMethod]
    public void EncodeFrameMatchesGoldenBytesAndHash()
    {
        // Arrange
        Guid requestId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        byte[] jsonBytes = "{}"u8.ToArray();
        byte[] expected =
        [
            0x41, 0x32, 0x4d, 0x50, 0x28, 0x00, 0x01, 0x00,
            0x03, 0x00, 0x00, 0x00, 0x00, 0x11, 0x22, 0x33,
            0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb,
            0xcc, 0xdd, 0xee, 0xff, 0x02, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x7b, 0x7d,
        ];

        // Act
        IpcEncodedFrame frame = IpcFrameCodec.EncodeFrame(
            IpcMessageKind.Request,
            IpcFrameOption.None,
            requestId,
            jsonBytes,
            []);

        // Assert
        CollectionAssert.AreEqual(expected, frame.Bytes);
        Assert.AreEqual("40641fef542d92418b07825761127d601916b6ac32f0df873fade8220509d8a0", frame.PayloadSha256);
    }

    [TestMethod]
    public async Task DecodeFrameAsyncSupportsOneByteReads()
    {
        // Arrange
        byte[] binaryBytes = [1, 2, 3, 4];
        IpcEncodedFrame encoded = IpcFrameCodec.EncodeFrame(
            IpcMessageKind.Response,
            IpcFrameOption.HasBinary,
            Guid.NewGuid(),
            "{\"ok\":true}"u8,
            binaryBytes);
        using OneByteReadStream stream = new(encoded.Bytes);

        // Act
        IpcFrame decoded = await IpcFrameCodec.DecodeFrameAsync(stream, CancellationToken.None);

        // Assert
        Assert.AreEqual(encoded.PayloadSha256, decoded.PayloadSha256);
        Assert.AreEqual("{\"ok\":true}", Encoding.UTF8.GetString(decoded.JsonBytes.Span));
        CollectionAssert.AreEqual(binaryBytes, decoded.BinaryBytes.ToArray());
    }

    [TestMethod]
    public async Task DecodeFrameAsyncRejectsTruncation()
    {
        // Arrange
        IpcEncodedFrame encoded = IpcFrameCodec.EncodeFrame(
            IpcMessageKind.Request,
            IpcFrameOption.None,
            Guid.NewGuid(),
            "{}"u8,
            []);
        using MemoryStream stream = new(encoded.Bytes[..^1], writable: false);

        // Act
        Func<Task> action = async () => await IpcFrameCodec.DecodeFrameAsync(stream, CancellationToken.None);

        // Assert
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(action);
    }

    [TestMethod]
    public async Task DecodeFrameAsyncRejectsInvalidUtf8()
    {
        // Arrange
        IpcFrameHeader header = new(IpcMessageKind.Request, IpcFrameOption.None, Guid.NewGuid(), 1, 0);
        byte[] bytes = [.. IpcHeaderCodec.EncodeHeader(header), 0xff];
        using MemoryStream stream = new(bytes, writable: false);

        // Act
        Func<Task> action = async () => await IpcFrameCodec.DecodeFrameAsync(stream, CancellationToken.None);

        // Assert
        await Assert.ThrowsExactlyAsync<InvalidDataException>(action);
    }

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Memory<byte> oneByteBuffer = buffer[..Math.Min(1, buffer.Length)];
            return base.ReadAsync(oneByteBuffer, cancellationToken);
        }
    }
}

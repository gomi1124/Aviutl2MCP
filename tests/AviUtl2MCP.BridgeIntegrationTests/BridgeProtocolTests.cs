using System.Reflection;
using AviUtl2MCP.BridgeClient.Protocol;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class BridgeProtocolTests
{
    [TestMethod]
    public void EncodeHeaderMatchesGoldenBytes()
    {
        // Arrange
        IpcFrameHeader header = new(
            IpcMessageKind.Response,
            IpcFrameOption.HasBinary,
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            0x00010203,
            0x0000000000040506);
        byte[] expected =
        [
            0x41, 0x32, 0x4d, 0x50, 0x28, 0x00, 0x01, 0x00,
            0x04, 0x01, 0x00, 0x00, 0x00, 0x11, 0x22, 0x33,
            0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb,
            0xcc, 0xdd, 0xee, 0xff, 0x03, 0x02, 0x01, 0x00,
            0x06, 0x05, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];

        // Act
        byte[] actual = IpcHeaderCodec.EncodeHeader(header);

        // Assert
        CollectionAssert.AreEqual(expected, actual);
        Assert.AreEqual(header, IpcHeaderCodec.DecodeHeader(actual));
    }

    [TestMethod]
    public void DecodeHeaderRejectsUnknownFlags()
    {
        // Arrange
        IpcFrameHeader header = new(
            IpcMessageKind.Request,
            IpcFrameOption.None,
            Guid.NewGuid(),
            2,
            0);
        byte[] bytes = IpcHeaderCodec.EncodeHeader(header);
        bytes[9] = 0x80;

        // Act
        Action action = () => IpcHeaderCodec.DecodeHeader(bytes);

        // Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(action);
    }

    [TestMethod]
    public void VerifyVersionOneLimitsMatchContract()
    {
        // Arrange
        Dictionary<string, object> expectedConstants = new(StringComparer.Ordinal)
        {
            [nameof(BridgeProtocol.MAJOR_VERSION)] = (ushort)1,
            [nameof(BridgeProtocol.MINOR_VERSION)] = (ushort)0,
            [nameof(BridgeProtocol.HEADER_BYTES)] = 40,
            [nameof(BridgeProtocol.MAX_JSON_BYTES)] = 8 * 1024 * 1024,
            [nameof(BridgeProtocol.MAX_BINARY_BYTES)] = 16 * 1024 * 1024,
        };

        // Act
        Dictionary<string, object?> actualConstants = typeof(BridgeProtocol)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(field => field.Name, field => field.GetRawConstantValue(), StringComparer.Ordinal);

        // Assert
        CollectionAssert.AreEquivalent(expectedConstants, actualConstants);
    }
}

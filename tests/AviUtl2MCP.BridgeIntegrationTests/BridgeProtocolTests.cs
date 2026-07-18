using System.Reflection;
using AviUtl2MCP.BridgeClient.Protocol;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class BridgeProtocolTests
{
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

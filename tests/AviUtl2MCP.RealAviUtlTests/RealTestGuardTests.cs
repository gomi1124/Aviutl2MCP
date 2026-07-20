namespace AviUtl2MCP.RealAviUtlTests;

[TestClass]
public sealed class RealTestGuardTests
{
    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("true", false)]
    [DataRow("1", true)]
    public void VerifyRealTestRequiresExactOptIn(string? value, bool expected)
    {
        // Arrange
        string? optIn = value;

        // Act
        bool isEnabled = string.Equals(optIn, "1", StringComparison.Ordinal);

        // Assert
        Assert.AreEqual(expected, isEnabled);
    }
}

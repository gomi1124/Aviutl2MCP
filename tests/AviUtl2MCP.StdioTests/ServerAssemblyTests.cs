using AviUtl2MCP.Server;

namespace AviUtl2MCP.StdioTests;

[TestClass]
public sealed class ServerAssemblyTests
{
    [TestMethod]
    public void VerifyServerAssemblyHasExecutableEntryPoint()
    {
        // Arrange
        System.Reflection.Assembly serverAssembly = typeof(ServerMarker).Assembly;

        // Act
        System.Reflection.MethodInfo? entryPoint = serverAssembly.EntryPoint;

        // Assert
        Assert.IsNotNull(entryPoint);
    }
}

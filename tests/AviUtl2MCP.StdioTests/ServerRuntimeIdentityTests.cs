using AviUtl2MCP.Server;

namespace AviUtl2MCP.StdioTests;

[TestClass]
[DoNotParallelize]
public sealed class ServerRuntimeIdentityTests
{
    private const string INSTANCE_VARIABLE = "AVIUTL2_MCP_INSTANCE";
    private const string COMPATIBILITY_INSTANCE_VARIABLE = "AVIUTL2_MCP_INSTANCE_ID";

    [TestMethod]
    public void RuntimeIdentityUsesDocumentedInstanceVariable()
    {
        // Arrange
        Guid expectedInstanceId = Guid.CreateVersion7();

        // Act
        Guid? actualInstanceId = RunWithInstanceVariables(
            expectedInstanceId.ToString("D"),
            null,
            () => new ServerRuntimeIdentity().EnvironmentInstanceId);

        // Assert
        Assert.AreEqual(expectedInstanceId, actualInstanceId);
    }

    [TestMethod]
    public void RuntimeIdentityKeepsCompatibilityInstanceVariable()
    {
        // Arrange
        Guid expectedInstanceId = Guid.CreateVersion7();

        // Act
        Guid? actualInstanceId = RunWithInstanceVariables(
            null,
            expectedInstanceId.ToString("D"),
            () => new ServerRuntimeIdentity().EnvironmentInstanceId);

        // Assert
        Assert.AreEqual(expectedInstanceId, actualInstanceId);
    }

    [TestMethod]
    public void RuntimeIdentityRejectsConflictingInstanceVariables()
    {
        // Arrange
        Guid documentedInstanceId = Guid.CreateVersion7();
        Guid compatibilityInstanceId = Guid.CreateVersion7();

        // Act
        Action action = () => RunWithInstanceVariables(
            documentedInstanceId.ToString("D"),
            compatibilityInstanceId.ToString("D"),
            () => new ServerRuntimeIdentity().EnvironmentInstanceId);

        // Assert
        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(action);
        StringAssert.Contains(exception.Message, INSTANCE_VARIABLE, StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, COMPATIBILITY_INSTANCE_VARIABLE, StringComparison.Ordinal);
    }

    private static T RunWithInstanceVariables<T>(
        string? instanceValue,
        string? compatibilityValue,
        Func<T> action)
    {
        string? savedInstanceValue = Environment.GetEnvironmentVariable(
            INSTANCE_VARIABLE,
            EnvironmentVariableTarget.Process);
        string? savedCompatibilityValue = Environment.GetEnvironmentVariable(
            COMPATIBILITY_INSTANCE_VARIABLE,
            EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(
                INSTANCE_VARIABLE,
                instanceValue,
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                COMPATIBILITY_INSTANCE_VARIABLE,
                compatibilityValue,
                EnvironmentVariableTarget.Process);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                INSTANCE_VARIABLE,
                savedInstanceValue,
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                COMPATIBILITY_INSTANCE_VARIABLE,
                savedCompatibilityValue,
                EnvironmentVariableTarget.Process);
        }
    }
}

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class PackageBuildScriptTests
{
    [TestMethod]
    public void PackageBuildReconfiguresCMakeBeforeNativeBuild()
    {
        // Arrange
        string repositoryRoot = FindRepositoryRoot();
        string scriptPath = Path.Combine(repositoryRoot, "scripts", "Build-Package.ps1");

        // Act
        string script = File.ReadAllText(scriptPath);
        int configureIndex = script.IndexOf(
            "& $cmake -S $repositoryRoot -B $nativeBuildDirectory -A x64",
            StringComparison.Ordinal);
        int buildIndex = script.IndexOf(
            "& $cmake --build $nativeBuildDirectory --config $Configuration",
            StringComparison.Ordinal);

        // Assert
        Assert.IsTrue(configureIndex >= 0, "Native CMake configure command is missing.");
        Assert.IsTrue(buildIndex > configureIndex, "CMake must configure before the native build.");
        Assert.IsFalse(
            script.Contains("CMakeCache.txt", StringComparison.Ordinal),
            "An existing CMake cache must not suppress VERSION reconfiguration.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AviUtl2MCP.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the AviUtl2MCP repository root.");
    }
}

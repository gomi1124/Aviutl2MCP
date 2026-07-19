using System.Text.RegularExpressions;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class CiWorkflowContractTests
{
    private static readonly string[] REQUIRED_JOB_NAMES =
        ["managed", "contract", "integration", "native", "package"];

    [TestMethod]
    [TestProperty("TestId", "build.clean-windows")]
    [TestProperty("TestId", "ci.required-jobs")]
    public void WorkflowBuildsAndTestsEveryRequiredWindowsJob()
    {
        // Arrange
        string repositoryRoot = FindRepositoryRoot();
        string workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml");

        // Act
        string workflow = File.ReadAllText(workflowPath);
        int windowsRunnerCount = Regex.Count(
            workflow,
            "^    runs-on: windows-latest\\r?$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // Assert
        foreach (string jobName in REQUIRED_JOB_NAMES)
        {
            Assert.IsTrue(
                Regex.IsMatch(
                    workflow,
                    $"^  {Regex.Escape(jobName)}:\\r?$",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant),
                $"Required CI job '{jobName}' is missing.");
        }
        Assert.AreEqual(REQUIRED_JOB_NAMES.Length, windowsRunnerCount);
        StringAssert.Contains(workflow, "dotnet restore AviUtl2MCP.slnx --locked-mode");
        StringAssert.Contains(workflow, "dotnet build AviUtl2MCP.slnx --no-restore --configuration Release");
        StringAssert.Contains(workflow, "cmake --build build/native --config Release");
        StringAssert.Contains(workflow, "ctest --test-dir build/native -C Release --output-on-failure");
        foreach (string dependency in REQUIRED_JOB_NAMES.Take(4))
        {
            StringAssert.Contains(workflow, $"      - {dependency}");
        }
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

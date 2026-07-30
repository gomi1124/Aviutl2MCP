using System.Text.Json;

namespace AviUtl2MCP.McpContractTests;

[TestClass]
public sealed class SchemaCatalogTests
{
    [TestMethod]
    public void VerifyCatalogContainsUniqueVersionOneTools()
    {
        // Arrange
        string repositoryRoot = FindRepositoryRoot();
        string catalogPath = Path.Combine(repositoryRoot, "schemas", "mcp", "v1", "catalog.json");

        // Act
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(catalogPath));
        string[] toolNames = document.RootElement
            .GetProperty("x-tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToArray();

        // Assert
        Assert.HasCount(32, toolNames);
        Assert.HasCount(32, toolNames.Distinct(StringComparer.Ordinal).ToArray());
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

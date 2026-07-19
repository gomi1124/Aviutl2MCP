namespace AviUtl2MCP.Server;

public static class ServerPathResolver
{
    public static string GetAviUtlLogDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("AVIUTL2_LOG_DIRECTORY");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "aviutl2",
                "Log")
            : Path.GetFullPath(configured);
    }

    public static string? GetInstanceDescriptorDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("AVIUTL2_MCP_INSTANCE_DIRECTORY");
        return string.IsNullOrWhiteSpace(configured)
            ? null
            : Path.GetFullPath(configured);
    }
}

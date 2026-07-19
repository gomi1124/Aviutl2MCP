using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.BridgeClient.Discovery;

namespace AviUtl2MCP.RealAviUtlTests;

internal sealed class RealAviUtlHarness : IAsyncDisposable
{
    private const string OPT_IN_VARIABLE = "AVIUTL2_MCP_REAL_TEST";
    private const string AVIUTL_PATH_VARIABLE = "AVIUTL2_MCP_REAL_AVIUTL_PATH";
    private const string DATA_PATH_VARIABLE = "AVIUTL2_MCP_REAL_DATA_PATH";
    private const string PROJECT_PATH_VARIABLE = "AVIUTL2_MCP_REAL_PROJECT_PATH";
    private const string TEMP_ROOT_VARIABLE = "AVIUTL2_MCP_REAL_TEMP_ROOT";
    private const string BRIDGE_PATH_VARIABLE = "AVIUTL2_MCP_NATIVE_BRIDGE_PATH";
    private static readonly UTF8Encoding UTF8_NO_BOM = new(false, true);
    private static readonly JsonSerializerOptions INDENTED_JSON_OPTIONS = new()
    {
        WriteIndented = true,
    };
    private readonly string sourceProjectHash;
    private readonly DateTime processStartTimeUtc;
    private Exception? recordedFailure;
    private bool isDisposed;

    private RealAviUtlHarness(
        Guid setupCorrelationId,
        string temporaryRoot,
        string runtimeDirectory,
        string sourceProjectPath,
        string sourceProjectHash,
        string fixtureProjectPath,
        string portableDataDirectory,
        Process launchedProcess,
        DateTime processStartTimeUtc,
        Guid instanceId)
    {
        SetupCorrelationId = setupCorrelationId;
        TemporaryRoot = temporaryRoot;
        RuntimeDirectory = runtimeDirectory;
        SourceProjectPath = sourceProjectPath;
        this.sourceProjectHash = sourceProjectHash;
        FixtureProjectPath = fixtureProjectPath;
        PortableDataDirectory = portableDataDirectory;
        LaunchedProcess = launchedProcess;
        this.processStartTimeUtc = processStartTimeUtc;
        InstanceId = instanceId;
    }

    public Guid SetupCorrelationId { get; }

    public string TemporaryRoot { get; }

    public string RuntimeDirectory { get; }

    public string SourceProjectPath { get; }

    public string FixtureProjectPath { get; }

    public string PortableDataDirectory { get; }

    public string AviUtlLogDirectory => Path.Combine(PortableDataDirectory, "Log");

    public string ServerLogDirectory => Path.Combine(RuntimeDirectory, "server-logs");

    public Process LaunchedProcess { get; }

    public Guid InstanceId { get; }

    public static bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable(OPT_IN_VARIABLE),
        "1",
        StringComparison.Ordinal);

    public void RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        recordedFailure ??= exception;
    }

    public static async Task<RealAviUtlHarness> StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException($"{OPT_IN_VARIABLE}=1 is required.");
        }

        string aviUtlPath = GetRequiredFile(AVIUTL_PATH_VARIABLE, "aviutl2.exe");
        string dataPath = GetRequiredDirectory(DATA_PATH_VARIABLE);
        string sourceProjectPath = GetRequiredFile(PROJECT_PATH_VARIABLE, ".aup2");
        string bridgePath = GetRequiredFile(BRIDGE_PATH_VARIABLE, ".aux2");
        string temporaryRoot = ValidateTemporaryRoot(GetRequiredDirectory(TEMP_ROOT_VARIABLE));
        Guid setupCorrelationId = Guid.CreateVersion7();
        string runtimeDirectory = Path.Combine(temporaryRoot, setupCorrelationId.ToString("D"));
        if (Directory.Exists(runtimeDirectory))
        {
            throw new IOException("The real-test correlation directory already exists.");
        }

        string sourceProjectHash = CalculateSha256(sourceProjectPath);
        string portableApplicationDirectory = Path.Combine(runtimeDirectory, "aviutl2");
        string portableDataDirectory = Path.Combine(portableApplicationDirectory, "data");
        string fixtureDirectory = Path.Combine(runtimeDirectory, "fixture");
        string fixtureProjectPath = Path.Combine(fixtureDirectory, "fixture.aup2");
        Process? launchedProcess = null;
        try
        {
            Directory.CreateDirectory(portableApplicationDirectory);
            Directory.CreateDirectory(portableDataDirectory);
            Directory.CreateDirectory(fixtureDirectory);
            CopyDirectory(Path.GetDirectoryName(aviUtlPath)!, portableApplicationDirectory);
            CopyRequiredData(dataPath, portableDataDirectory);
            string bridgeDirectory = Path.Combine(
                portableDataDirectory,
                "Plugin",
                "AviUtl2MCP");
            Directory.CreateDirectory(bridgeDirectory);
            File.Copy(
                bridgePath,
                Path.Combine(bridgeDirectory, "AviUtl2MCP.Bridge.aux2"),
                overwrite: false);
            PrepareModuleTrust(dataPath, portableDataDirectory);
            CreateFixtureProject(sourceProjectPath, fixtureProjectPath);

            string portableAviUtlPath = Path.Combine(
                portableApplicationDirectory,
                Path.GetFileName(aviUtlPath));
            ProcessStartInfo startInfo = new(portableAviUtlPath)
            {
                WorkingDirectory = portableApplicationDirectory,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(fixtureProjectPath);
            launchedProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("AviUtl2 did not return a process handle.");
            DateTime processStartTimeUtc = launchedProcess.StartTime.ToUniversalTime();
            Guid instanceId = await WaitForInstanceAsync(
                launchedProcess,
                cancellationToken).ConfigureAwait(false);
            return new RealAviUtlHarness(
                setupCorrelationId,
                temporaryRoot,
                runtimeDirectory,
                sourceProjectPath,
                sourceProjectHash,
                fixtureProjectPath,
                portableDataDirectory,
                launchedProcess,
                processStartTimeUtc,
                instanceId);
        }
        catch (Exception exception)
        {
            if (launchedProcess is not null)
            {
                StopOwnedProcess(launchedProcess, launchedProcess.StartTime.ToUniversalTime());
                launchedProcess.Dispose();
            }
            try
            {
                PreserveStartupFailureArtifacts(
                    runtimeDirectory,
                    setupCorrelationId,
                    launchedProcess?.Id,
                    exception);
            }
            catch (Exception preserveException) when (preserveException is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                Trace.WriteLine($"Real-test failure artifact capture failed: {preserveException.Message}");
            }
            DeleteOwnedRuntimeDirectory(temporaryRoot, runtimeDirectory, setupCorrelationId);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        if (IsOwnedProcessRunning())
        {
            _ = LaunchedProcess.CloseMainWindow();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            try
            {
                await LaunchedProcess.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                StopOwnedProcess(LaunchedProcess, processStartTimeUtc);
            }
        }
        int launchedProcessId = LaunchedProcess.Id;
        LaunchedProcess.Dispose();
        string currentSourceHash = CalculateSha256(SourceProjectPath);
        if (recordedFailure is not null)
        {
            try
            {
                PreserveStartupFailureArtifacts(
                    RuntimeDirectory,
                    SetupCorrelationId,
                    launchedProcessId,
                    recordedFailure);
            }
            catch (Exception preserveException) when (preserveException is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                Trace.WriteLine($"Real-test failure artifact capture failed: {preserveException.Message}");
            }
        }
        DeleteOwnedRuntimeDirectory(
            TemporaryRoot,
            RuntimeDirectory,
            SetupCorrelationId);
        if (!string.Equals(sourceProjectHash, currentSourceHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The source fixture project changed during the real test.");
        }
    }

    private bool IsOwnedProcessRunning()
    {
        try
        {
            return !LaunchedProcess.HasExited
                && LaunchedProcess.StartTime.ToUniversalTime() == processStartTimeUtc;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<Guid> WaitForInstanceAsync(
        Process launchedProcess,
        CancellationToken cancellationToken)
    {
        InstanceDescriptorWatcher watcher = new();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (launchedProcess.HasExited)
            {
                throw new InvalidOperationException(
                    $"AviUtl2 exited before publishing its Bridge descriptor ({launchedProcess.ExitCode}).");
            }
            BridgeInstanceDescriptor? descriptor = watcher.ReadDescriptors().Instances
                .SingleOrDefault(candidate => candidate.ProcessId == launchedProcess.Id);
            if (descriptor is not null)
            {
                return descriptor.InstanceId;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("AviUtl2 did not publish an MCP Bridge descriptor within 30 seconds.");
    }

    private static void CopyRequiredData(string sourceDataPath, string destinationDataPath)
    {
        string[] requiredDirectories =
        [
            Path.Combine("Plugin", "PSDToolKit"),
            Path.Combine("Plugin", "GCMZDrops"),
            Path.Combine("Script", "PSDToolKit"),
        ];
        foreach (string relativePath in requiredDirectories)
        {
            string source = Path.Combine(sourceDataPath, relativePath);
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException(
                    $"Required real-test component is missing: {relativePath}");
            }
            CopyDirectory(source, Path.Combine(destinationDataPath, relativePath));
        }
    }

    private static void PrepareModuleTrust(
        string sourceDataPath,
        string destinationDataPath)
    {
        string sourcePath = Path.Combine(sourceDataPath, "module.ini");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "The AviUtl2 module trust file is required for isolated real tests.",
                sourcePath);
        }
        string destinationPath = Path.Combine(destinationDataPath, "module.ini");
        string content = UTF8_NO_BOM.GetString(File.ReadAllBytes(sourcePath));
        const string section = "[AviUtl2MCP\\AviUtl2MCP.Bridge.aux2]";
        if (!content.Contains(section, StringComparison.OrdinalIgnoreCase))
        {
            content = content.TrimEnd('\r', '\n')
                + $"\r\n{section}\r\ntrust=1\r\n";
        }
        File.WriteAllText(destinationPath, content, UTF8_NO_BOM);
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        DirectoryInfo source = new(sourcePath);
        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Real-test source directories cannot be reparse points.");
        }
        Directory.CreateDirectory(destinationPath);
        foreach (FileInfo file in source.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Real-test source files cannot be reparse points.");
            }
            file.CopyTo(Path.Combine(destinationPath, file.Name), overwrite: false);
        }
        foreach (DirectoryInfo directory in source.EnumerateDirectories())
        {
            CopyDirectory(directory.FullName, Path.Combine(destinationPath, directory.Name));
        }
    }

    private static void CreateFixtureProject(string sourcePath, string destinationPath)
    {
        string content = UTF8_NO_BOM.GetString(File.ReadAllBytes(sourcePath));
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int fileLineIndex = Array.FindIndex(
            lines,
            line => line.StartsWith("file=", StringComparison.Ordinal));
        if (fileLineIndex < 0)
        {
            throw new InvalidDataException("The source fixture project omitted its file field.");
        }
        lines[fileLineIndex] = $"file={destinationPath}";
        File.WriteAllText(destinationPath, string.Join("\r\n", lines), UTF8_NO_BOM);
    }

    private static string GetRequiredFile(string variableName, string expectedExtensionOrName)
    {
        string value = GetRequiredEnvironmentValue(variableName);
        string path = Path.GetFullPath(value);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{variableName} does not identify a file.", path);
        }
        bool matches = expectedExtensionOrName.Length > 0 && expectedExtensionOrName[0] == '.'
            ? string.Equals(Path.GetExtension(path), expectedExtensionOrName, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetFileName(path), expectedExtensionOrName, StringComparison.OrdinalIgnoreCase);
        if (!matches)
        {
            throw new InvalidDataException($"{variableName} has an unexpected file type.");
        }
        return path;
    }

    private static string GetRequiredDirectory(string variableName)
    {
        string path = Path.GetFullPath(GetRequiredEnvironmentValue(variableName));
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{variableName} does not identify a directory.");
        }
        return path;
    }

    private static string GetRequiredEnvironmentValue(string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{variableName} is required for real tests.");
        }
        return value;
    }

    private static string ValidateTemporaryRoot(string path)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string systemTemporaryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string cTemporaryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(@"C:\tmp"));
        bool isAllowed = normalized.StartsWith(
                systemTemporaryPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(
                cTemporaryPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, cTemporaryPath, StringComparison.OrdinalIgnoreCase);
        if (!isAllowed || Path.GetPathRoot(normalized) == normalized)
        {
            throw new InvalidOperationException(
                $"{TEMP_ROOT_VARIABLE} must be a dedicated directory under the system temp or C:\\tmp.");
        }
        return normalized;
    }

    private static void StopOwnedProcess(Process process, DateTime expectedStartTimeUtc)
    {
        try
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime() == expectedStartTimeUtc)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void DeleteOwnedRuntimeDirectory(
        string temporaryRoot,
        string runtimeDirectory,
        Guid setupCorrelationId)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryRoot));
        string normalizedRuntime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory));
        string expectedRuntime = Path.Combine(normalizedRoot, setupCorrelationId.ToString("D"));
        if (setupCorrelationId.Version != 7
            || !string.Equals(normalizedRuntime, expectedRuntime, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete an unowned real-test directory.");
        }
        if (Directory.Exists(normalizedRuntime))
        {
            const int maximumAttempts = 25;
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(normalizedRuntime, recursive: true);
                    return;
                }
                catch (Exception exception) when (
                    attempt < maximumAttempts
                    && exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                    if (!Directory.Exists(normalizedRuntime))
                    {
                        return;
                    }
                }
            }
        }
    }

    private static string CalculateSha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void PreserveStartupFailureArtifacts(
        string runtimeDirectory,
        Guid setupCorrelationId,
        int? processId,
        Exception exception)
    {
        string? repositoryValue = Environment.GetEnvironmentVariable(
            "AVIUTL2_MCP_REPOSITORY_ROOT");
        if (string.IsNullOrWhiteSpace(repositoryValue) || !Directory.Exists(repositoryValue))
        {
            return;
        }
        string repositoryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryValue));
        string artifactRoot = Path.Combine(
            repositoryRoot,
            "artifacts",
            "real-e2e-fail",
            setupCorrelationId.ToString("D"));
        Directory.CreateDirectory(artifactRoot);
        string logSource = Path.Combine(runtimeDirectory, "aviutl2", "data", "Log");
        if (Directory.Exists(logSource))
        {
            CopyDirectory(logSource, Path.Combine(artifactRoot, "aviutl-log"));
        }
        string dataDirectory = Path.Combine(runtimeDirectory, "aviutl2", "data");
        string[] metadataFiles = ["aviutl2.ini", "module.ini"];
        foreach (string fileName in metadataFiles)
        {
            string source = Path.Combine(dataDirectory, fileName);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(artifactRoot, fileName), overwrite: false);
            }
        }
        string pluginDirectory = Path.Combine(dataDirectory, "Plugin");
        string[] pluginFiles = Directory.Exists(pluginDirectory)
            ? Directory.GetFiles(pluginDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(pluginDirectory, path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        object document = new
        {
            correlationId = setupCorrelationId,
            processId,
            exceptionType = exception.GetType().FullName,
            exception.Message,
            pluginFiles,
            capturedAt = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(
            Path.Combine(artifactRoot, "startup-failure.json"),
            JsonSerializer.Serialize(document, INDENTED_JSON_OPTIONS),
            UTF8_NO_BOM);
    }
}

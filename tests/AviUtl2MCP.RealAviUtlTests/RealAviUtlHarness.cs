using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.BridgeClient.Discovery;

namespace AviUtl2MCP.RealAviUtlTests;

internal sealed partial class RealAviUtlHarness : IAsyncDisposable
{
    private const string OPT_IN_VARIABLE = "AVIUTL2_MCP_REAL_TEST";
    private const string AVIUTL_PATH_VARIABLE = "AVIUTL2_MCP_REAL_AVIUTL_PATH";
    private const string DATA_PATH_VARIABLE = "AVIUTL2_MCP_REAL_DATA_PATH";
    private const string PROJECT_PATH_VARIABLE = "AVIUTL2_MCP_REAL_PROJECT_PATH";
    private const string TEMP_ROOT_VARIABLE = "AVIUTL2_MCP_REAL_TEMP_ROOT";
    private const string BRIDGE_PATH_VARIABLE = "AVIUTL2_MCP_NATIVE_BRIDGE_PATH";
    private const string INSTANCE_DIRECTORY_VARIABLE =
        "AVIUTL2_MCP_INSTANCE_DIRECTORY";
    private const string BRIDGE_PACKAGE_PATH_VARIABLE =
        "AVIUTL2_MCP_REAL_BRIDGE_PACKAGE_PATH";
    private static readonly UTF8Encoding UTF8_NO_BOM = new(false, true);
    private static readonly JsonSerializerOptions INDENTED_JSON_OPTIONS = new()
    {
        WriteIndented = true,
    };
    private const uint MF_BYPOSITION = 0x00000400;
    private const uint MF_DISABLED = 0x00000002;
    private const uint MF_GRAYED = 0x00000001;
    private const uint INVALID_MENU_ITEM_ID = 0xffffffff;
    private const uint WM_COMMAND = 0x00000111;
    private const uint SMTO_ABORTIFHUNG = 0x00000002;
    private readonly string sourceProjectHash;
    private readonly DateTime processStartTimeUtc;
    private readonly List<string> acceptanceTestIds = [];
    private string? beforeRevision;
    private string? afterRevision;
    private string? beforePreviewPath;
    private string? afterPreviewPath;
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
        string instanceDirectory,
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
        InstanceDirectory = instanceDirectory;
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

    public string InstanceDirectory { get; }

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

    public void RecordAcceptanceTestIds(params string[] testIds)
    {
        ArgumentNullException.ThrowIfNull(testIds);
        foreach (string testId in testIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(testId);
            if (testId.Length > 128 || testId.Any(character =>
                    character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '.'
                    and not '-'))
            {
                throw new ArgumentException(
                    "Acceptance test IDs must use lowercase ASCII letters, digits, dots, and hyphens.",
                    nameof(testIds));
            }
            if (!acceptanceTestIds.Contains(testId, StringComparer.Ordinal))
            {
                acceptanceTestIds.Add(testId);
            }
        }
    }

    public void RecordRevision(string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        beforeRevision ??= revision;
        afterRevision = revision;
    }

    public void RecordPreviewArtifacts(string beforePath, string afterPath)
    {
        beforePreviewPath = ValidateOwnedArtifact(beforePath);
        afterPreviewPath = ValidateOwnedArtifact(afterPath);
    }

    public string InvokeUndo()
    {
        if (!IsOwnedProcessRunning())
        {
            throw new InvalidOperationException("The owned AviUtl2 process is not running.");
        }
        LaunchedProcess.Refresh();
        nint window = LaunchedProcess.MainWindowHandle;
        if (window == nint.Zero)
        {
            throw new InvalidOperationException("The owned AviUtl2 main window is unavailable.");
        }
        _ = GetWindowThreadProcessId(window, out uint windowProcessId);
        if (windowProcessId != (uint)LaunchedProcess.Id)
        {
            throw new InvalidOperationException("The AviUtl2 window process identity changed.");
        }
        nint menu = GetMenu(window);
        if (menu == nint.Zero)
        {
            throw new InvalidOperationException("The owned AviUtl2 window has no standard menu.");
        }
        List<string> observedLabels = [];
        (uint CommandId, string Label)? undo = FindUndoMenuItem(menu, observedLabels);
        if (undo is null)
        {
            throw new InvalidOperationException(
                $"The AviUtl2 Undo menu item was not found. Menus: {string.Join(" | ", observedLabels.Take(64))}");
        }
        nint sent = SendMessageTimeout(
            window,
            WM_COMMAND,
            undo.Value.CommandId,
            nint.Zero,
            SMTO_ABORTIFHUNG,
            5_000,
            out _);
        if (sent == nint.Zero)
        {
            throw new InvalidOperationException(
                $"The AviUtl2 Undo command timed out ({Marshal.GetLastPInvokeError()}).");
        }
        return undo.Value.Label;
    }

    public static async Task<RealAviUtlHarness> StartAsync(
        CancellationToken cancellationToken,
        Action<string>? prepareFixture = null)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException($"{OPT_IN_VARIABLE}=1 is required.");
        }

        string aviUtlPath = GetRequiredFile(AVIUTL_PATH_VARIABLE, "aviutl2.exe");
        string dataPath = GetRequiredDirectory(DATA_PATH_VARIABLE);
        string sourceProjectPath = GetRequiredFile(PROJECT_PATH_VARIABLE, ".aup2");
        string? bridgePackagePath = GetOptionalPackagePath();
        string? bridgePath = bridgePackagePath is null
            ? GetRequiredFile(BRIDGE_PATH_VARIABLE, ".aux2")
            : null;
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
        string instanceDirectory = Path.Combine(runtimeDirectory, "instances");
        string fixtureDirectory = Path.Combine(runtimeDirectory, "fixture");
        string fixtureProjectPath = Path.Combine(fixtureDirectory, "fixture.aup2");
        Process? launchedProcess = null;
        try
        {
            Directory.CreateDirectory(portableApplicationDirectory);
            Directory.CreateDirectory(portableDataDirectory);
            Directory.CreateDirectory(instanceDirectory);
            Directory.CreateDirectory(fixtureDirectory);
            CopyDirectory(Path.GetDirectoryName(aviUtlPath)!, portableApplicationDirectory);
            CopyRequiredData(dataPath, portableDataDirectory);
            if (bridgePackagePath is null)
            {
                InstallBuiltBridge(bridgePath!, portableDataDirectory);
            }
            else
            {
                InstallPackagedBridge(bridgePackagePath, portableDataDirectory);
            }
            PrepareModuleTrust(dataPath, portableDataDirectory);
            CreateFixtureProject(sourceProjectPath, fixtureProjectPath);
            prepareFixture?.Invoke(fixtureProjectPath);

            string portableAviUtlPath = Path.Combine(
                portableApplicationDirectory,
                Path.GetFileName(aviUtlPath));
            ProcessStartInfo startInfo = new(portableAviUtlPath)
            {
                WorkingDirectory = portableApplicationDirectory,
                UseShellExecute = false,
            };
            startInfo.Environment[INSTANCE_DIRECTORY_VARIABLE] = instanceDirectory;
            startInfo.ArgumentList.Add(fixtureProjectPath);
            launchedProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("AviUtl2 did not return a process handle.");
            DateTime processStartTimeUtc = launchedProcess.StartTime.ToUniversalTime();
            Guid instanceId = await WaitForInstanceAsync(
                launchedProcess,
                instanceDirectory,
                cancellationToken).ConfigureAwait(false);
            return new RealAviUtlHarness(
                setupCorrelationId,
                temporaryRoot,
                runtimeDirectory,
                sourceProjectPath,
                sourceProjectHash,
                fixtureProjectPath,
                portableDataDirectory,
                instanceDirectory,
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
        Exception? debugReportFailure = null;
        try
        {
            await CreateLifecycleDebugReportAsync(
                launchedProcessId,
                currentSourceHash).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            debugReportFailure = exception;
            Trace.WriteLine($"Real-test debug report generation failed: {exception.Message}");
        }
        DeleteOwnedRuntimeDirectory(
            TemporaryRoot,
            RuntimeDirectory,
            SetupCorrelationId);
        if (!string.Equals(sourceProjectHash, currentSourceHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The source fixture project changed during the real test.");
        }
        if (recordedFailure is null && debugReportFailure is not null)
        {
            throw new InvalidOperationException(
                "The real test passed but its debug report could not be generated.",
                debugReportFailure);
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

    private static (uint CommandId, string Label)? FindUndoMenuItem(
        nint menu,
        List<string> observedLabels)
    {
        int count = GetMenuItemCount(menu);
        if (count < 0)
        {
            return null;
        }
        for (int position = 0; position < count; position++)
        {
            StringBuilder buffer = new(512);
            int length = GetMenuString(
                menu,
                (uint)position,
                buffer,
                buffer.Capacity,
                MF_BYPOSITION);
            string label = length > 0 ? buffer.ToString() : string.Empty;
            if (!string.IsNullOrWhiteSpace(label))
            {
                observedLabels.Add(label);
            }
            nint subMenu = GetSubMenu(menu, position);
            if (subMenu != nint.Zero)
            {
                (uint CommandId, string Label)? nested = FindUndoMenuItem(
                    subMenu,
                    observedLabels);
                if (nested is not null)
                {
                    return nested;
                }
            }
            uint commandId = GetMenuItemID(menu, position);
            bool isUndo = label.Contains("元に戻す", StringComparison.OrdinalIgnoreCase)
                || label.Contains("Undo", StringComparison.OrdinalIgnoreCase);
            if (!isUndo || commandId == INVALID_MENU_ITEM_ID)
            {
                continue;
            }
            uint state = GetMenuState(menu, (uint)position, MF_BYPOSITION);
            if ((state & (MF_DISABLED | MF_GRAYED)) != 0)
            {
                throw new InvalidOperationException(
                    $"The AviUtl2 Undo menu item is disabled: {label}");
            }
            return (commandId, label);
        }
        return null;
    }

#pragma warning disable CA1838, SYSLIB1054
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetMenu(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMenuItemCount(nint menu);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetMenuStringW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetMenuString(
        nint menu,
        uint item,
        StringBuilder buffer,
        int maximumCount,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetSubMenu(nint menu, int position);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetMenuItemID(nint menu, int position);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetMenuState(nint menu, uint item, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);
#pragma warning restore CA1838, SYSLIB1054

    private static async Task<Guid> WaitForInstanceAsync(
        Process launchedProcess,
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        InstanceDescriptorWatcher watcher = new(instanceDirectory);
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

    private async Task CreateLifecycleDebugReportAsync(
        int launchedProcessId,
        string currentSourceHash)
    {
        string repositoryRoot = GetRequiredDirectory("AVIUTL2_MCP_REPOSITORY_ROOT");
        string evidenceDirectory = Path.Combine(RuntimeDirectory, "debug-evidence");
        Directory.CreateDirectory(evidenceDirectory);
        bool isSourceUnchanged = string.Equals(
            sourceProjectHash,
            currentSourceHash,
            StringComparison.Ordinal);
        string testStatus = recordedFailure is null ? "pass" : "fail";
        string checksPath = Path.Combine(evidenceDirectory, "checks.json");
        List<object> checks =
        [
            new
            {
                name = "real.test-outcome",
                status = testStatus,
                evidence = recordedFailure is null
                    ? new[] { $"instanceId={InstanceId:D}" }
                    : new[]
                    {
                        $"instanceId={InstanceId:D}",
                        $"exception={recordedFailure.GetType().FullName}",
                        recordedFailure.Message,
                    },
            },
            new
            {
                name = "real.source-fixture-unchanged",
                status = isSourceUnchanged ? "pass" : "fail",
                evidence = new[]
                {
                    $"beforeSha256={sourceProjectHash}",
                    $"afterSha256={currentSourceHash}",
                },
            },
            new
            {
                name = "real.owned-process",
                status = "pass",
                evidence = new[]
                {
                    $"processId={launchedProcessId}",
                    $"setupCorrelationId={SetupCorrelationId:D}",
                },
            },
        ];
        string acceptanceStatus = recordedFailure is null && isSourceUnchanged
            ? "pass"
            : "fail";
        foreach (string testId in acceptanceTestIds)
        {
            string testIdStatus = string.Equals(
                testId,
                "real.fixture-process-guard",
                StringComparison.Ordinal)
                ? isSourceUnchanged ? "pass" : "fail"
                : acceptanceStatus;
            checks.Add(new
            {
                name = testId,
                status = testIdStatus,
                evidence = new[]
                {
                    $"setupCorrelationId={SetupCorrelationId:D}",
                    $"instanceId={InstanceId:D}",
                },
            });
        }
        await File.WriteAllTextAsync(
            checksPath,
            JsonSerializer.Serialize(checks, INDENTED_JSON_OPTIONS),
            UTF8_NO_BOM).ConfigureAwait(false);

        string versionsPath = Path.Combine(evidenceDirectory, "versions.json");
        await File.WriteAllTextAsync(
            versionsPath,
            JsonSerializer.Serialize(
                new { realAviUtlHarness = "1.0.0" },
                INDENTED_JSON_OPTIONS),
            UTF8_NO_BOM).ConfigureAwait(false);

        string serverSummaryPath = await WriteComponentSummaryAsync(
            evidenceDirectory,
            "server",
            "Direct Bridge gateway tests may not launch the MCP stdio server.").ConfigureAwait(false);
        string bridgeSummaryPath = await WriteComponentSummaryAsync(
            evidenceDirectory,
            "bridge",
            $"Bridge instance {InstanceId:D} completed the isolated run.").ConfigureAwait(false);
        string aviUtlSummaryPath = await WriteComponentSummaryAsync(
            evidenceDirectory,
            "aviutl",
            $"Owned AviUtl2 process {launchedProcessId} completed the isolated run.").ConfigureAwait(false);
        string scriptPath = Path.Combine(repositoryRoot, "scripts", "New-DebugReport.ps1");
        string reportRoot = Path.Combine(repositoryRoot, "artifacts", "real-e2e");
        ProcessStartInfo startInfo = new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        AddArguments(
            startInfo,
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "-CorrelationId",
            SetupCorrelationId.ToString("D"),
            "-OutputDirectory",
            reportRoot,
            "-Command",
            "real.aviutl2-harness",
            "-ExitCode",
            acceptanceStatus == "pass" ? "0" : "1");
        if (beforeRevision is not null)
        {
            AddArguments(startInfo, "-BeforeRevision", beforeRevision);
        }
        if (afterRevision is not null)
        {
            AddArguments(startInfo, "-AfterRevision", afterRevision);
        }
        if (beforePreviewPath is not null)
        {
            AddArguments(startInfo, "-BeforePreviewPath", beforePreviewPath);
        }
        if (afterPreviewPath is not null)
        {
            AddArguments(startInfo, "-AfterPreviewPath", afterPreviewPath);
        }
        AddArguments(
            startInfo,
            "-ServerLogPath",
            serverSummaryPath,
            "-BridgeLogPath",
            bridgeSummaryPath,
            "-AviUtlLogPath",
            aviUtlSummaryPath,
            "-ChecksPath",
            checksPath,
            "-ComponentVersionsPath",
            versionsPath,
            "-LaunchedProcessId",
            launchedProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-RepositoryRoot",
            repositoryRoot);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The lifecycle debug-report generator did not start.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw new TimeoutException("Lifecycle debug-report generation exceeded 30 seconds.");
        }
        string output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Lifecycle debug-report generation failed ({process.ExitCode}): {error}{output}");
        }
        string reportPath = Path.Combine(
            reportRoot,
            SetupCorrelationId.ToString("D"),
            "debug-report.json");
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException(
                "The lifecycle debug-report generator did not produce its output.",
                reportPath);
        }
    }

    private async Task<string> WriteComponentSummaryAsync(
        string evidenceDirectory,
        string component,
        string message)
    {
        string path = Path.Combine(evidenceDirectory, $"{component}.jsonl");
        string line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            level = recordedFailure is null ? "information" : "error",
            source = component,
            correlationId = SetupCorrelationId,
            message,
        });
        await File.WriteAllTextAsync(
            path,
            line + Environment.NewLine,
            UTF8_NO_BOM).ConfigureAwait(false);
        return path;
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private string ValidateOwnedArtifact(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = Path.GetFullPath(path);
        string normalizedRuntime = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(RuntimeDirectory));
        if (!normalizedPath.StartsWith(
                normalizedRuntime + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(normalizedPath))
        {
            throw new InvalidOperationException(
                "Debug preview artifacts must be existing files in the owned runtime directory.");
        }
        return normalizedPath;
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

    private static void InstallBuiltBridge(
        string bridgePath,
        string portableDataDirectory)
    {
        string bridgeDirectory = Path.Combine(
            portableDataDirectory,
            "Plugin",
            "AviUtl2MCP");
        Directory.CreateDirectory(bridgeDirectory);
        File.Copy(
            bridgePath,
            Path.Combine(bridgeDirectory, "AviUtl2MCP.Bridge.aux2"),
            overwrite: false);
        string bridgeAssetsDirectory = Path.Combine(
            Path.GetDirectoryName(bridgePath)!,
            "assets");
        if (!Directory.Exists(bridgeAssetsDirectory))
        {
            throw new DirectoryNotFoundException(
                "The native Bridge PSDToolKit2 assets directory was not produced.");
        }
        CopyDirectory(
            bridgeAssetsDirectory,
            Path.Combine(bridgeDirectory, "assets"));
    }

    private static void InstallPackagedBridge(
        string packagePath,
        string portableDataDirectory)
    {
        const string packagePrefix = "Plugin/AviUtl2MCP/";
        string normalizedData = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(portableDataDirectory));
        int extractedFileCount = 0;
        long extractedByteCount = 0;
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string entryName = entry.FullName.Replace('\\', '/');
            if (!entryName.StartsWith(packagePrefix, StringComparison.Ordinal)
                || entryName.EndsWith('/'))
            {
                continue;
            }
            string[] segments = entryName.Split('/');
            if (segments.Any(segment =>
                    segment.Length == 0
                    || segment == "."
                    || segment == ".."
                    || segment.Contains(':')))
            {
                throw new InvalidDataException("The Bridge package contains an unsafe path.");
            }
            if (++extractedFileCount > 128)
            {
                throw new InvalidDataException("The Bridge package contains too many files.");
            }
            extractedByteCount = checked(extractedByteCount + entry.Length);
            if (extractedByteCount > 128L * 1024L * 1024L)
            {
                throw new InvalidDataException("The Bridge package exceeds the extraction limit.");
            }
            string destinationPath = Path.GetFullPath(Path.Combine(
                normalizedData,
                entryName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(
                    normalizedData + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The Bridge package escaped the data directory.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using Stream input = entry.Open();
            using FileStream output = new(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            input.CopyTo(output);
        }
        string bridgeDirectory = Path.Combine(
            normalizedData,
            "Plugin",
            "AviUtl2MCP");
        string bridgePath = Path.Combine(bridgeDirectory, "AviUtl2MCP.Bridge.aux2");
        string manifestPath = Path.Combine(bridgeDirectory, "manifest.json");
        if (!File.Exists(bridgePath) || !File.Exists(manifestPath))
        {
            throw new InvalidDataException(
                "The Bridge package omitted the plugin binary or manifest.");
        }
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        if (manifest.RootElement.GetProperty("packageId").GetString()
                != "gomi1124.AviUtl2MCP"
            || string.IsNullOrWhiteSpace(
                manifest.RootElement.GetProperty("version").GetString()))
        {
            throw new InvalidDataException("The Bridge package manifest is invalid.");
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

    private static string? GetOptionalPackagePath()
    {
        string? value = Environment.GetEnvironmentVariable(BRIDGE_PACKAGE_PATH_VARIABLE);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string path = Path.GetFullPath(value);
        if (!File.Exists(path)
            || !path.EndsWith(".au2pkg.zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{BRIDGE_PACKAGE_PATH_VARIABLE} must identify an .au2pkg.zip file.");
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

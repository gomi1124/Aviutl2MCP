using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Protocol;

namespace AviUtl2MCP.BridgeClient.Discovery;

public sealed class InstanceDescriptorWatcher
{
    private const int MAX_DESCRIPTOR_BYTES = 16 * 1024;
    private static readonly TimeSpan defaultPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly UTF8Encoding strictUtf8 = new(false, true);
    private readonly string directoryPath;
    private readonly IProcessIdentityProbe processIdentityProbe;
    private readonly TimeSpan pollInterval;

    public InstanceDescriptorWatcher(
        string? directoryPath = null,
        IProcessIdentityProbe? processIdentityProbe = null,
        TimeSpan? pollInterval = null)
    {
        this.directoryPath = directoryPath ?? GetDefaultDirectoryPath();
        this.processIdentityProbe = processIdentityProbe ?? new SystemProcessIdentityProbe();
        this.pollInterval = pollInterval ?? defaultPollInterval;
        ArgumentException.ThrowIfNullOrWhiteSpace(this.directoryPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(this.pollInterval, TimeSpan.FromMilliseconds(10));
    }

    public string DirectoryPath => directoryPath;

    public DescriptorSnapshot ReadDescriptors()
    {
        if (!Directory.Exists(directoryPath))
        {
            return new DescriptorSnapshot([], []);
        }

        List<BridgeInstanceDescriptor> candidates = [];
        List<DescriptorIssue> issues = [];
        string[] files;
        try
        {
            files = Directory.GetFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(new DescriptorIssue(
                string.Empty,
                DescriptorIssueCode.DirectoryUnavailable,
                $"Descriptor directory could not be enumerated ({exception.GetType().Name})."));
            return new DescriptorSnapshot([], issues);
        }

        foreach (string filePath in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            ReadDescriptor(filePath, candidates, issues);
        }

        BridgeInstanceDescriptor[] validInstances = RemoveDuplicates(candidates, issues);
        return new DescriptorSnapshot(validInstances, issues.ToArray());
    }

    public async IAsyncEnumerable<DescriptorSnapshot> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DescriptorSnapshot previous = ReadDescriptors();
        string previousFingerprint = CalculateFingerprint(previous);
        yield return previous;
        while (true)
        {
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            DescriptorSnapshot current = ReadDescriptors();
            string currentFingerprint = CalculateFingerprint(current);
            if (!string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                previousFingerprint = currentFingerprint;
                yield return current;
            }
        }
    }

    private static string GetDefaultDirectoryPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AviUtl2MCP",
            "v1",
            "instances");
    }

    private static BridgeInstanceDescriptor[] RemoveDuplicates(
        List<BridgeInstanceDescriptor> candidates,
        List<DescriptorIssue> issues)
    {
        HashSet<Guid> duplicateIds = candidates
            .GroupBy(candidate => candidate.InstanceId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        foreach (Guid duplicateId in duplicateIds.Order())
        {
            issues.Add(new DescriptorIssue(
                $"{duplicateId:D}.json",
                DescriptorIssueCode.DuplicateInstance,
                "Multiple valid descriptors declared the same instance ID."));
        }

        return candidates
            .Where(candidate => !duplicateIds.Contains(candidate.InstanceId))
            .OrderBy(candidate => candidate.InstanceId)
            .ToArray();
    }

    private static string CalculateFingerprint(DescriptorSnapshot snapshot)
    {
        return string.Join(
            '\n',
            snapshot.Instances.Select(instance =>
                $"I:{instance.InstanceId:D}:{instance.ProcessId}:{instance.ProcessCreationTime}:{instance.PipeName}:{instance.BridgeVersion}:{instance.ProtocolMajor}")
                .Concat(snapshot.Issues.Select(issue =>
                    $"E:{issue.FileName}:{issue.Code}:{issue.Message}")));
    }

    private void ReadDescriptor(
        string filePath,
        List<BridgeInstanceDescriptor> candidates,
        List<DescriptorIssue> issues)
    {
        string fileName = Path.GetFileName(filePath);
        try
        {
            FileInfo file = new(filePath);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                issues.Add(new DescriptorIssue(
                    fileName,
                    DescriptorIssueCode.ReparsePointRejected,
                    "Descriptor reparse points are not accepted."));
                return;
            }

            byte[] descriptorBytes = File.ReadAllBytes(filePath);
            if (descriptorBytes.Length is <= 0 or > MAX_DESCRIPTOR_BYTES)
            {
                issues.Add(new DescriptorIssue(
                    fileName,
                    DescriptorIssueCode.FileTooLarge,
                    "Descriptor size was empty or exceeded 16 KiB."));
                return;
            }

            InstanceDescriptorDocument document = ContractJsonSerializer.DeserializeContract<InstanceDescriptorDocument>(
                strictUtf8.GetString(descriptorBytes));
            ValidateAndAdd(fileName, document, candidates, issues);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
        {
            issues.Add(new DescriptorIssue(
                fileName,
                DescriptorIssueCode.Malformed,
                $"Descriptor could not be read ({exception.GetType().Name})."));
        }
    }

    private void ValidateAndAdd(
        string fileName,
        InstanceDescriptorDocument document,
        List<BridgeInstanceDescriptor> candidates,
        List<DescriptorIssue> issues)
    {
        if (!TryParseDescriptorFileName(fileName, out Guid fileInstanceId)
            || fileInstanceId != document.InstanceId)
        {
            issues.Add(new DescriptorIssue(
                fileName,
                DescriptorIssueCode.FileNameMismatch,
                "Descriptor file name and instance ID did not match."));
            return;
        }

        if (document.InstanceId == Guid.Empty
            || document.ProcessId <= 0
            || document.ProcessCreationTime <= 0
            || string.IsNullOrWhiteSpace(document.BridgeVersion)
            || document.BridgeVersion.Length > 64
            || !IsExpectedPipeName(document.PipeName, document.InstanceId))
        {
            issues.Add(new DescriptorIssue(
                fileName,
                DescriptorIssueCode.Malformed,
                "Descriptor fields were outside the supported constraints."));
            return;
        }

        if (document.ProtocolMajor != BridgeProtocol.MAJOR_VERSION)
        {
            issues.Add(new DescriptorIssue(
                fileName,
                DescriptorIssueCode.ProtocolIncompatible,
                "Descriptor protocol major was incompatible."));
            return;
        }

        ProcessIdentityProbeResult process = processIdentityProbe.ProbeProcess(document.ProcessId);
        if (!process.IsRunning)
        {
            issues.Add(new DescriptorIssue(
                fileName,
                DescriptorIssueCode.ProcessUnavailable,
                $"Descriptor process was unavailable ({process.Failure})."));
            return;
        }

        if (process.CreationTime != document.ProcessCreationTime)
        {
            issues.Add(new DescriptorIssue(
                fileName,
                DescriptorIssueCode.ProcessIdentityMismatch,
                "Descriptor process creation time did not match the live process."));
            return;
        }

        candidates.Add(new BridgeInstanceDescriptor(
            document.InstanceId,
            document.ProcessId,
            document.ProcessCreationTime,
            document.PipeName,
            document.BridgeVersion,
            document.ProtocolMajor));
    }

    private static bool TryParseDescriptorFileName(string fileName, out Guid instanceId)
    {
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return Guid.TryParseExact(nameWithoutExtension, "D", out instanceId)
            || Guid.TryParseExact(nameWithoutExtension, "N", out instanceId);
    }

    private static bool IsExpectedPipeName(string pipeName, Guid instanceId)
    {
        return string.Equals(pipeName, $"AviUtl2MCP.v1.{instanceId:D}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pipeName, $"AviUtl2MCP.v1.{instanceId:N}", StringComparison.OrdinalIgnoreCase);
    }
}

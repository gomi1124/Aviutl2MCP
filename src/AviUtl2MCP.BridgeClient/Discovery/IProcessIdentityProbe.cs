using System.ComponentModel;
using System.Diagnostics;

namespace AviUtl2MCP.BridgeClient.Discovery;

public enum ProcessProbeFailure
{
    None,
    NotRunning,
    AccessDenied,
    Unavailable,
}

public readonly record struct ProcessIdentityProbeResult(
    bool IsRunning,
    long CreationTime,
    ProcessProbeFailure Failure);

public interface IProcessIdentityProbe
{
    ProcessIdentityProbeResult ProbeProcess(int processId);
}

public sealed class SystemProcessIdentityProbe : IProcessIdentityProbe
{
    public ProcessIdentityProbeResult ProbeProcess(int processId)
    {
        if (processId <= 0)
        {
            return new ProcessIdentityProbeResult(false, 0, ProcessProbeFailure.NotRunning);
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return new ProcessIdentityProbeResult(false, 0, ProcessProbeFailure.NotRunning);
            }

            return new ProcessIdentityProbeResult(
                true,
                process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                ProcessProbeFailure.None);
        }
        catch (ArgumentException)
        {
            return new ProcessIdentityProbeResult(false, 0, ProcessProbeFailure.NotRunning);
        }
        catch (InvalidOperationException)
        {
            return new ProcessIdentityProbeResult(false, 0, ProcessProbeFailure.NotRunning);
        }
        catch (Win32Exception)
        {
            return new ProcessIdentityProbeResult(false, 0, ProcessProbeFailure.AccessDenied);
        }
        catch (NotSupportedException)
        {
            return new ProcessIdentityProbeResult(false, 0, ProcessProbeFailure.Unavailable);
        }
    }
}

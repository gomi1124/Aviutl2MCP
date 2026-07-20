using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Server.Diagnostics;

public sealed class LatestDiagnosticsStore
{
    private readonly object _gate = new();
    private ToolEnvelope<DiagnoseData>? _latest;

    public void Save(ToolEnvelope<DiagnoseData> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            _latest = envelope;
        }
    }

    public ToolEnvelope<DiagnoseData>? GetLatest()
    {
        lock (_gate)
        {
            return _latest;
        }
    }
}

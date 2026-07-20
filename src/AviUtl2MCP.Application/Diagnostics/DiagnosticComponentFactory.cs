using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Diagnostics;

internal static class DiagnosticComponentFactory
{
    public static IReadOnlyList<DiagnosticComponent> CreateComponents(DiagnosticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CapabilityVersions? versions = context.Capabilities?.Versions;
        return
        [
            CreateComponent("server", versions?.Server, context, "server"),
            CreateComponent("bridge", versions?.Bridge ?? context.Instance.BridgeVersion, context, "bridge"),
            CreateComponent("aviutl", versions?.Aviutl, context, "aviutl"),
            CreateComponent("sdk", versions?.Sdk, context, "sdk"),
            CreateComponent("psdtoolkit2", versions?.PsdToolKit, context, "psdtoolkit"),
            CreateComponent("gcmzdrops", versions?.GcmzDrops, context, "gcmz"),
        ];
    }

    private static DiagnosticComponent CreateComponent(
        string name,
        string? version,
        DiagnosticContext context,
        string statusFragment)
    {
        ComponentStatus[] reported = context.Status?.Components
            .Where(component => component.Name.Contains(statusFragment, StringComparison.OrdinalIgnoreCase))
            .ToArray()
            ?? [];
        List<string> evidence = reported
            .Select(component => $"{component.Name}={component.Status}")
            .ToList();
        if (evidence.Count == 0)
        {
            evidence.Add(version is null ? "version=missing" : $"version={version}");
        }

        DiagnosticComponentStatus status = reported
            .Select(component => MapStatus(component.Status))
            .OrderByDescending(GetSeverity)
            .FirstOrDefault(version is null
                ? context.Capabilities is null
                    ? DiagnosticComponentStatus.Unavailable
                    : DiagnosticComponentStatus.Missing
                : DiagnosticComponentStatus.Detected);
        return new DiagnosticComponent(name, status, version, evidence);
    }

    private static DiagnosticComponentStatus MapStatus(string status) =>
        status.ToUpperInvariant() switch
        {
            "DETECTED" or "READY" or "AVAILABLE" or "OK" or "PASS" => DiagnosticComponentStatus.Detected,
            "MISSING" => DiagnosticComponentStatus.Missing,
            "INCOMPATIBLE" => DiagnosticComponentStatus.Incompatible,
            "UNAVAILABLE" or "UNKNOWN" => DiagnosticComponentStatus.Unavailable,
            "ERROR" or "FAULTED" or "FAIL" => DiagnosticComponentStatus.Error,
            _ => DiagnosticComponentStatus.Unavailable,
        };

    private static int GetSeverity(DiagnosticComponentStatus status) =>
        status switch
        {
            DiagnosticComponentStatus.Error => 5,
            DiagnosticComponentStatus.Incompatible => 4,
            DiagnosticComponentStatus.Missing => 3,
            DiagnosticComponentStatus.Unavailable => 2,
            _ => 1,
        };
}

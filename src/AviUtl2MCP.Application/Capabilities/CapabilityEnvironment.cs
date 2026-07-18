using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Capabilities;

public sealed record CapabilityEnvironment(
    bool IsBridgeReady,
    bool IsProjectOpen,
    bool CanEdit,
    bool HasPsdToolKit,
    bool HasGcmzDrops,
    CapabilityVersions Versions);

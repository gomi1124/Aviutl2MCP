namespace AviUtl2MCP.Application.Contracts;

public sealed record ObjectLocator(
    Guid InstanceId,
    Guid ProjectGeneration,
    int SceneId,
    int Layer,
    int StartFrame,
    int EndFrame,
    string Name,
    string AliasSha256,
    string EffectSignatureSha256);

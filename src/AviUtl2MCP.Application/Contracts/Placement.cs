namespace AviUtl2MCP.Application.Contracts;

public sealed record Placement(
    int SceneId,
    int Layer,
    int StartFrame,
    int? EndFrame = null,
    int? DurationFrames = null);

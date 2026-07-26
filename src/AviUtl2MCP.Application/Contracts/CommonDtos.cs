using System.Text.Json;
using System.Text.Json.Nodes;

namespace AviUtl2MCP.Application.Contracts;

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class EffectItemValueAttribute : Attribute;

public sealed record MovePlacement(int SceneId, int Layer, int StartFrame);

public sealed record EffectDefinitionSelector(string Name);

public sealed record EffectInstanceSelector(string Name, int Occurrence = 0);

public sealed record EffectItemAssignment(
    string Name,
    [property: EffectItemValue] JsonElement Value);

public sealed record Selection(int StartFrame, int EndFrame);

public sealed record ToolWarning(
    string Code,
    string Message,
    IReadOnlyDictionary<string, JsonNode?> Details);

public sealed record ToolError(
    string Code,
    string Message,
    bool CanRetry,
    IReadOnlyDictionary<string, JsonNode?> Details);

public sealed record ToolEnvelope<TData>(
    bool Ok,
    Guid CorrelationId,
    IReadOnlyList<ToolWarning> Warnings)
{
    public Guid? InstanceId { get; init; }

    public Revision? Revision { get; init; }

    public Revision? ViewRevision { get; init; }

    public TData? Data { get; init; }

    public ToolError? Error { get; init; }
}

public sealed record CoordinateSystem(int FrameBase, int LayerBase, bool EndInclusive);

public sealed record Change(string Kind, string Target)
{
    public JsonNode? Before { get; init; }

    public JsonNode? After { get; init; }
}

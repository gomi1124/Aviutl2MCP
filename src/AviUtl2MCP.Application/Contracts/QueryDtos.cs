using System.Text.Json;
using System.Text.Json.Serialization;

namespace AviUtl2MCP.Application.Contracts;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Ready,
    Incompatible,
    Faulted,
}

public enum ProjectState
{
    NotOpen,
    Unsaved,
    Saved,
    Unknown,
}

public enum EditState
{
    Edit,
    Play,
    Save,
    Unknown,
}

public enum EffectDefinitionType
{
    Filter,
    Input,
    Transition,
    Control,
    Output,
    Unknown,
}

public enum EffectTarget
{
    Video,
    Audio,
    Filter,
    Camera,
    Unknown,
}

public enum EffectItemType
{
    [JsonStringEnumMemberName("integer")]
    WholeNumber,
    Number,
    Check,
    Text,
    [JsonStringEnumMemberName("string")]
    StringValue,
    File,
    Color,
    Select,
    Scene,
    Range,
    Combo,
    Mask,
    Font,
    Figure,
    Data,
    Folder,
    Unknown,
}

public enum EffectItemCodec
{
    [JsonStringEnumMemberName("integer")]
    WholeNumber,
    Number,
    Check01,
    AliasString,
    Unsupported,
}

public sealed record ComponentStatus(string Name, string Status, string? Version);

public sealed record AviUtlInstance(Guid InstanceId, int ProcessId, string BridgeVersion, string State);

public sealed record StatusData(
    ConnectionState ConnectionState,
    IReadOnlyList<ComponentStatus> Components,
    ProjectState ProjectState,
    EditState EditState,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? SelectedInstance,
    IReadOnlyList<AviUtlInstance> Instances);

public sealed record CapabilityConstraint(
    string Name,
    JsonElement Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Unit);

public sealed record CapabilityOperation(
    string Name,
    bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    IReadOnlyList<CapabilityConstraint> Constraints);

public sealed record CapabilityVersions(
    string Server,
    string Schema,
    string Protocol,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Bridge,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Aviutl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sdk,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PsdToolKit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? GcmzDrops);

public sealed record CapabilityLimits(
    int IpcJsonBytes,
    int IpcBinaryBytes,
    int IpcInFlight,
    int BridgeConnections,
    int GlobalQueue,
    int MutationTombstones,
    int MutationResponseCache,
    int TimelineDefaultItems,
    int TimelineMaxItems,
    int BatchOperations,
    int AliasUtf8Bytes,
    int ToolStringUtf8Bytes,
    int LogDefaultLines,
    int LogMaxLines,
    int PreviewMaxWidth,
    int PreviewMaxHeight,
    int PngBytes,
    int StatusTimeoutMs,
    int EditTimeoutMs,
    int PreviewTimeoutMs,
    int PagingCursorTtlSeconds);

public sealed record CapabilitiesData(
    IReadOnlyList<CapabilityOperation> Operations,
    CapabilityVersions Versions,
    CapabilityLimits Limits);

public sealed record SceneSummary(int SceneId, string Name);

public sealed record ProjectData(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path,
    bool IsSaved,
    int Width,
    int Height,
    double FrameRate,
    int SampleRate,
    int CurrentSceneId,
    int CurrentFrame,
    IReadOnlyList<int> SelectedLayers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Selection? Selection,
    IReadOnlyList<SceneSummary> Scenes,
    CoordinateSystem CoordinateSystem);

public sealed record EffectSummary(
    string Name,
    int Occurrence,
    bool IsEnabled,
    bool IsLocked);

public sealed record ObjectSummary(
    ObjectLocator Locator,
    string Name,
    int SceneId,
    int Layer,
    int StartFrame,
    int EndFrame,
    bool IsSelected,
    IReadOnlyList<EffectSummary> Effects)
{
    public string? MediaPath { get; init; }
}

public sealed record LayerSummary(
    int SceneId,
    int Layer,
    string Name,
    bool IsVisible,
    bool IsLocked);

public sealed record TimelineData(
    IReadOnlyList<LayerSummary> Layers,
    IReadOnlyList<ObjectSummary> Objects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor,
    bool IsTruncated,
    CoordinateSystem CoordinateSystem);

public sealed record ObjectsPageData(
    IReadOnlyList<ObjectSummary> Objects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor,
    bool IsTruncated,
    CoordinateSystem CoordinateSystem);

public sealed record EffectItemsGroup(EffectSummary Effect, IReadOnlyList<EffectItem> Items);

public sealed record ObjectData(
    [property: JsonPropertyName("object")] ObjectSummary TimelineObject,
    IReadOnlyList<EffectItemsGroup> EffectItems)
{
    public string? Alias { get; init; }
}

public sealed record EffectDefinition(
    string Name,
    EffectDefinitionType Type,
    IReadOnlyList<EffectTarget> Flags,
    bool IsCreatable);

public sealed record ModuleSummary(string Type, string Name, string Information);

public sealed record EffectsData(
    IReadOnlyList<EffectDefinition> Effects,
    IReadOnlyList<ModuleSummary> Modules,
    IReadOnlyList<string> Fonts,
    IReadOnlyList<string> Palettes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor,
    bool IsTruncated);

public sealed record EffectItem(
    string Name,
    EffectItemType Type,
    EffectItemCodec Codec,
    bool IsWritable)
{
    public JsonElement? Value { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }
}

public sealed record EffectItemsData(IReadOnlyList<EffectItem> Items);

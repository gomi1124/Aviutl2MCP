using System.Text.Json;
using System.Text.Json.Serialization;

namespace AviUtl2MCP.Application.Contracts;

public sealed record CreateObjectArgs(
    EffectDefinitionSelector Effect,
    Placement Placement)
{
    public string? Name { get; init; }

    public IReadOnlyList<EffectItemAssignment>? Items { get; init; }
}

public sealed record CreateMediaObjectArgs(string MediaPath, Placement Placement)
{
    public string? Name { get; init; }
}

public sealed record CreateAliasObjectArgs(string Alias, Placement Placement)
{
    public string? Name { get; init; }
}

public sealed record MoveObjectArgs(ObjectLocator Locator, MovePlacement Placement);

public sealed record DeleteObjectArgs(ObjectLocator Locator);

public sealed record SetObjectNameArgs(ObjectLocator Locator, string Name);

public sealed record SetEffectItemArgs(
    ObjectLocator Locator,
    EffectInstanceSelector Effect,
    string ItemName,
    JsonElement Value);

public sealed record SetEffectStateArgs(ObjectLocator Locator, EffectInstanceSelector Effect)
{
    public bool? IsEnabled { get; init; }

    public bool? IsLocked { get; init; }
}

public sealed record SetLayerArgs(int Layer)
{
    public int? SceneId { get; init; }

    public string? Name { get; init; }

    public bool? IsVisible { get; init; }

    public bool? IsLocked { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(BatchCreateObject), "createObject")]
[JsonDerivedType(typeof(BatchCreateMediaObject), "createMediaObject")]
[JsonDerivedType(typeof(BatchCreateAliasObject), "createAliasObject")]
[JsonDerivedType(typeof(BatchMoveObject), "moveObject")]
[JsonDerivedType(typeof(BatchDeleteObject), "deleteObject")]
[JsonDerivedType(typeof(BatchSetObjectName), "setObjectName")]
[JsonDerivedType(typeof(BatchSetEffectItem), "setEffectItem")]
[JsonDerivedType(typeof(BatchSetEffectState), "setEffectState")]
[JsonDerivedType(typeof(BatchSetLayer), "setLayer")]
public abstract record BatchOperation(string ClientOperationId);

public sealed record BatchCreateObject(string ClientOperationId, CreateObjectArgs Args)
    : BatchOperation(ClientOperationId);

public sealed record BatchCreateMediaObject(string ClientOperationId, CreateMediaObjectArgs Args)
    : BatchOperation(ClientOperationId);

public sealed record BatchCreateAliasObject(string ClientOperationId, CreateAliasObjectArgs Args)
    : BatchOperation(ClientOperationId);

public sealed record BatchMoveObject(string ClientOperationId, MoveObjectArgs Args)
    : BatchOperation(ClientOperationId);

public sealed record BatchDeleteObject(string ClientOperationId, DeleteObjectArgs Args)
    : BatchOperation(ClientOperationId);

public sealed record BatchSetObjectName(string ClientOperationId, SetObjectNameArgs Args)
    : BatchOperation(ClientOperationId);

public sealed record BatchSetEffectItem(string ClientOperationId, SetEffectItemArgs Args)
    : BatchOperation(ClientOperationId);

public sealed record BatchSetEffectState(string ClientOperationId, SetEffectStateArgs Args)
    : BatchOperation(ClientOperationId);

public sealed record BatchSetLayer(string ClientOperationId, SetLayerArgs Args)
    : BatchOperation(ClientOperationId);

public sealed record CreateObjectData
{
    [JsonPropertyName("object")]
    public ObjectSummary? TimelineObject { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record CreateObjectsData
{
    public IReadOnlyList<ObjectSummary>? Objects { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record UpdatedObjectData
{
    [JsonPropertyName("object")]
    public ObjectSummary? TimelineObject { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record DeleteData
{
    [JsonPropertyName("object")]
    public ObjectSummary? TimelineObject { get; init; }

    public bool? Deleted { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record EffectItemUpdateData
{
    public EffectItem? Item { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record EffectStateUpdateData
{
    public EffectSummary? Effect { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record LayerUpdateData
{
    public LayerSummary? Layer { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record CursorData(
    int SceneId,
    int Frame,
    int DisplayFrame,
    Selection? Selection,
    CoordinateSystem CoordinateSystem);

public enum BatchOperationKind
{
    CreateObject,
    CreateMediaObject,
    CreateAliasObject,
    MoveObject,
    DeleteObject,
    SetObjectName,
    SetEffectItem,
    SetEffectState,
    SetLayer,
}

public enum BatchResultStatus
{
    Planned,
    Applied,
    Failed,
    Skipped,
}

public sealed record BatchResult(
    string ClientOperationId,
    BatchOperationKind Op,
    BatchResultStatus Status,
    IReadOnlyList<Change> Changes)
{
    [JsonPropertyName("object")]
    public ObjectSummary? TimelineObject { get; init; }

    public IReadOnlyList<ObjectSummary>? Objects { get; init; }

    public ToolError? Error { get; init; }
}

public sealed record BatchData(
    IReadOnlyList<BatchResult> Results,
    IReadOnlyList<string> AppliedOperationIds,
    bool UndoRecommended);

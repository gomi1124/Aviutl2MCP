using System.Text.Json.Serialization;

namespace AviUtl2MCP.Application.Contracts;

public abstract record CommonInput
{
    public Guid? InstanceId { get; init; }

    public int? TimeoutMs { get; init; }
}

public abstract record MutationInput : CommonInput
{
    public required Revision ExpectedRevision { get; init; }

    public bool DryRun { get; init; }
}

public abstract record PageInput : CommonInput
{
    public int Limit { get; init; } = 100;

    public string? Cursor { get; init; }
}

public sealed record GetStatusInput : CommonInput;

public sealed record GetCapabilitiesInput : CommonInput;

public sealed record GetProjectInput : CommonInput
{
    public bool IncludeScenes { get; init; } = true;
}

public sealed record SaveProjectInput : CommonInput
{
    public required Revision ExpectedRevision { get; init; }
}

public enum TimelineDetail
{
    Summary,
    Effects,
}

public sealed record GetTimelineInput : PageInput
{
    public int? SceneId { get; init; }

    public int? LayerStart { get; init; }

    public int? LayerEnd { get; init; }

    public int? StartFrame { get; init; }

    public int? EndFrame { get; init; }

    public TimelineDetail Detail { get; init; } = TimelineDetail.Summary;
}

public sealed record FindObjectsInput : PageInput
{
    public int? SceneId { get; init; }

    public int? LayerStart { get; init; }

    public int? LayerEnd { get; init; }

    public int? StartFrame { get; init; }

    public int? EndFrame { get; init; }

    public string? NameContains { get; init; }

    public string? EffectName { get; init; }

    public string? MediaPath { get; init; }
}

public sealed record GetObjectInput : CommonInput
{
    public required ObjectLocator Locator { get; init; }

    public bool IncludeAlias { get; init; }

    public bool IncludeEffectItems { get; init; } = true;
}

public sealed record ListEffectsInput : PageInput
{
    public EffectDefinitionType? Category { get; init; }

    public string? NameContains { get; init; }
}

public sealed record ListEffectItemsInput : CommonInput
{
    public required EffectDefinitionSelector Effect { get; init; }

    public bool IncludeChoices { get; init; } = true;
}

public sealed record CreateObjectInput : MutationInput
{
    public required EffectDefinitionSelector Effect { get; init; }

    public required Placement Placement { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<EffectItemAssignment>? Items { get; init; }
}

public sealed record CreateMediaObjectInput : MutationInput
{
    public required string MediaPath { get; init; }

    public required Placement Placement { get; init; }

    public string? Name { get; init; }
}

public sealed record CreateAliasObjectInput : MutationInput
{
    public required string Alias { get; init; }

    public required Placement Placement { get; init; }

    public string? Name { get; init; }
}

public sealed record MoveObjectInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }

    public required MovePlacement Placement { get; init; }
}

public sealed record DeleteObjectInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }
}

public sealed record SetObjectNameInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }

    public required string Name { get; init; }
}

public sealed record CreateObjectSectionInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }

    public required int Frame { get; init; }
}

public sealed record DeleteObjectSectionInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }

    public required int Section { get; init; }
}

public sealed record MoveObjectSectionInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }

    public required int Section { get; init; }

    public required int Frame { get; init; }
}

public sealed record SetEffectItemInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }

    public required EffectInstanceSelector Effect { get; init; }

    public required string ItemName { get; init; }

    [EffectItemValue]
    public required System.Text.Json.JsonElement Value { get; init; }
}

public sealed record SetEffectStateInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }

    public required EffectInstanceSelector Effect { get; init; }

    public bool? IsEnabled { get; init; }

    public bool? IsLocked { get; init; }
}

public sealed record SetLayerInput : MutationInput
{
    public int? SceneId { get; init; }

    public required int Layer { get; init; }

    public string? Name { get; init; }

    public bool? IsVisible { get; init; }

    public bool? IsLocked { get; init; }
}

public sealed record SetCursorInput : CommonInput
{
    public int? SceneId { get; init; }

    public int? Frame { get; init; }

    public int? DisplayFrame { get; init; }

    public Selection? Selection { get; init; }

    public Revision? ExpectedViewRevision { get; init; }
}

public sealed record ExecuteBatchInput : MutationInput
{
    public required IReadOnlyList<BatchOperation> Operations { get; init; }
}

public sealed record RenderPreviewInput : CommonInput
{
    public int? SceneId { get; init; }

    public required int Frame { get; init; }

    public int? MaxWidth { get; init; }

    public int? MaxHeight { get; init; }

    public bool IncludeAlpha { get; init; }
}

public enum LogSource
{
    Server,
    Bridge,
    Aviutl,
}

public enum ContractLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

public sealed record GetLogsInput : CommonInput
{
    public IReadOnlyList<LogSource>? Sources { get; init; }

    public IReadOnlyList<ContractLogLevel>? Levels { get; init; }

    public DateTimeOffset? Since { get; init; }

    public Guid? CorrelationId { get; init; }

    public int Limit { get; init; } = 100;

    public string? Cursor { get; init; }
}

public sealed record DiagnoseInput : CommonInput
{
    public bool IncludeReadSmoke { get; init; }

    public bool IncludePreviewSmoke { get; init; }

    public int MaxLogLines { get; init; } = 100;
}

public sealed record PsdCreateInput : MutationInput
{
    public required string PsdPath { get; init; }

    public required Placement Placement { get; init; }

    public string? Name { get; init; }
}

public sealed record PsdSetupInput : MutationInput
{
    public int? SceneId { get; init; }

    public int? PreferredLayer { get; init; }

    public int? PreferredFrame { get; init; }

    public bool CreateIfMissing { get; init; } = true;
}

public sealed record PsdSetCharacterInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }

    public required string CharacterId { get; init; }
}

public sealed record PsdSetLayerStateInput : MutationInput
{
    public required ObjectLocator Locator { get; init; }

    public required string LayerState { get; init; }
}

public sealed record PsdCreateVoiceInput : MutationInput
{
    public required string AudioPath { get; init; }

    public string? TextPath { get; init; }

    public string? LabPath { get; init; }

    public required string CharacterId { get; init; }

    public ObjectLocator? PsdLocator { get; init; }

    public required Placement Placement { get; init; }
}

public enum PsdValidationScope
{
    [JsonStringEnumMemberName("object")]
    SingleObject,
    Scene,
}

public enum PsdValidationCheck
{
    Setup,
    Character,
    Blink,
    LipSync,
    Subtitle,
}

public sealed record PsdValidateInput : CommonInput
{
    public ObjectLocator? Locator { get; init; }

    public PsdValidationScope Scope { get; init; } = PsdValidationScope.SingleObject;

    public IReadOnlyList<PsdValidationCheck>? Checks { get; init; }
}

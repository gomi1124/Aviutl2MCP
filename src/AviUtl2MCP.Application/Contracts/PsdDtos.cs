using System.Text.Json.Serialization;

namespace AviUtl2MCP.Application.Contracts;

public enum PsdPlacementStatus
{
    Valid,
    Missing,
    Misplaced,
    Ambiguous,
}

public sealed record PsdSetupData(
    IReadOnlyList<ObjectSummary> Objects,
    bool Created,
    PsdPlacementStatus PlacementStatus)
{
    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record PsdCharacterData
{
    [JsonPropertyName("object")]
    public ObjectSummary? TimelineObject { get; init; }

    public string? CharacterId { get; init; }

    public EffectItem? Item { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record PsdLayerStateData
{
    [JsonPropertyName("object")]
    public ObjectSummary? TimelineObject { get; init; }

    public string? LayerState { get; init; }

    public bool? RoundTripMatched { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record PsdCompanionFiles(
    string AudioPath,
    string TextPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LabPath);

public sealed record PsdVoiceData
{
    public IReadOnlyList<ObjectSummary>? VoiceObjects { get; init; }

    public IReadOnlyList<ObjectSummary>? SubtitleObjects { get; init; }

    public PsdCompanionFiles? CompanionFiles { get; init; }

    public IReadOnlyList<Change>? PlannedChanges { get; init; }

    public IReadOnlyList<Change>? AppliedChanges { get; init; }
}

public sealed record PsdValidateData(
    IReadOnlyList<DiagnosticCheck> Checks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Profile);

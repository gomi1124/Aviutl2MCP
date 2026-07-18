using System.Text.Json.Serialization;

namespace AviUtl2MCP.Application.Contracts;

public sealed record PreviewData(
    string MimeType,
    int Width,
    int Height,
    int Frame,
    string Sha256,
    int ByteLength);

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string EventId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CorrelationId,
    string Message);

public sealed record LogsData(
    IReadOnlyList<LogEntry> Entries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor,
    bool IsTruncated);

public enum DiagnosticCheckStatus
{
    Pass,
    Warning,
    Fail,
    Skipped,
}

public enum DiagnosticComponentStatus
{
    Detected,
    Missing,
    Incompatible,
    Unavailable,
    Error,
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public enum DiagnosticOverallStatus
{
    Healthy,
    Degraded,
    Unavailable,
}

public sealed record DiagnosticCheck(
    string CheckId,
    DiagnosticCheckStatus Status,
    IReadOnlyList<string> Evidence,
    string Impact,
    string Recommendation,
    bool CanRetry);

public sealed record DiagnosticComponent(
    string Name,
    DiagnosticComponentStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version,
    IReadOnlyList<string> Evidence);

public sealed record KnownLogMatch(
    string RuleId,
    string Source,
    DiagnosticSeverity Severity,
    IReadOnlyList<string> Evidence,
    string Impact,
    string Recommendation);

public sealed record DiagnoseData(
    DiagnosticOverallStatus Status,
    IReadOnlyList<DiagnosticCheck> Checks,
    IReadOnlyList<DiagnosticComponent> Components,
    IReadOnlyList<KnownLogMatch> KnownLogMatches);

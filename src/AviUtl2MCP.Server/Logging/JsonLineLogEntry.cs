namespace AviUtl2MCP.Server.Logging;

public sealed record JsonLineLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Component,
    int EventId,
    string? EventName,
    string? CorrelationId,
    string Message,
    IReadOnlyDictionary<string, string?> Properties,
    string? Exception);

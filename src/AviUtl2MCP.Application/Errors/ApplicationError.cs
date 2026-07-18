namespace AviUtl2MCP.Application.Errors;

public sealed record ApplicationError(
    string Code,
    string Message,
    bool CanRetry,
    IReadOnlyDictionary<string, object?> Details);

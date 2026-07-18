using System.Text.Json.Nodes;

namespace AviUtl2MCP.Application.Errors;

public static class ApplicationErrors
{
    public static ApplicationError CreateError(
        string code,
        string message,
        bool canRetry = false,
        IReadOnlyDictionary<string, JsonNode?>? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ApplicationError(code, message, canRetry, details ?? new Dictionary<string, JsonNode?>());
    }

    public static ApplicationError CreateInvalidArgument(string message)
    {
        return CreateError("invalid_argument", message);
    }

    public static ApplicationError CreateInstanceAmbiguous(IReadOnlyList<Guid> candidateIds)
    {
        JsonArray candidates = new(candidateIds.Select(id => JsonValue.Create(id)).ToArray());
        return CreateError(
            "instance_ambiguous",
            "Multiple AviUtl2 instances are available; specify instanceId.",
            details: new Dictionary<string, JsonNode?> { ["candidateIds"] = candidates });
    }

    public static ApplicationError CreateAviUtlNotRunning()
    {
        return CreateError("aviutl_not_running", "No available AviUtl2 instance was found.", true);
    }

    public static ApplicationError CreateCursorInvalid(string reason)
    {
        return CreateError(
            "cursor_invalid",
            "The paging cursor is invalid for the current request.",
            details: new Dictionary<string, JsonNode?> { ["reason"] = reason });
    }
}

using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Errors;

public sealed record ApplicationResult<T>
{
    internal ApplicationResult(
        bool isSuccess,
        T? value,
        ApplicationError? error,
        IReadOnlyList<ToolWarning> warnings)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        Warnings = warnings;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public ApplicationError? Error { get; }

    public IReadOnlyList<ToolWarning> Warnings { get; }
}

public static class ApplicationResult
{
    public static ApplicationResult<T> Success<T>(
        T value,
        IReadOnlyList<ToolWarning>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ApplicationResult<T>(true, value, null, warnings ?? []);
    }

    public static ApplicationResult<T> Failure<T>(
        ApplicationError error,
        T? partialData = default,
        IReadOnlyList<ToolWarning>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ApplicationResult<T>(false, partialData, error, warnings ?? []);
    }
}

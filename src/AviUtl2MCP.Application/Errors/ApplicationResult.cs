namespace AviUtl2MCP.Application.Errors;

public sealed record ApplicationResult<T>
{
    internal ApplicationResult(bool isSuccess, T? value, ApplicationError? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public ApplicationError? Error { get; }

}

public static class ApplicationResult
{
    public static ApplicationResult<T> Success<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ApplicationResult<T>(true, value, null);
    }

    public static ApplicationResult<T> Failure<T>(ApplicationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ApplicationResult<T>(false, default, error);
    }
}

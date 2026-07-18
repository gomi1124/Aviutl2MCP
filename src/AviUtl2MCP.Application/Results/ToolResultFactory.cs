using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Requests;

namespace AviUtl2MCP.Application.Results;

public static class ToolResultFactory
{
    public static ToolEnvelope<TData> CreateEnvelope<TData>(
        ApplicationResult<TData> result,
        RequestContext context,
        Guid? instanceId = null,
        Revision? revision = null,
        Revision? viewRevision = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        ToolEnvelope<TData> envelope = new(result.IsSuccess, context.CorrelationId, result.Warnings)
        {
            InstanceId = instanceId,
            Revision = revision,
            ViewRevision = viewRevision,
            Data = result.Value,
            Error = result.Error is null ? null : MapError(result.Error),
        };
        return envelope;
    }

    private static ToolError MapError(ApplicationError error)
    {
        return new ToolError(error.Code, error.Message, error.CanRetry, error.Details);
    }
}

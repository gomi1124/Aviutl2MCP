using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;

namespace AviUtl2MCP.Application.Queries;

public sealed record QueryExecutionResult<TData>(
    ApplicationResult<TData> Result,
    Guid? InstanceId,
    Revision? Revision,
    Revision? ViewRevision);

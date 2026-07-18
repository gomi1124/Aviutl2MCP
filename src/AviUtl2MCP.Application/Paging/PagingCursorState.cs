using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Paging;

public sealed record PagingCursorState(
    Guid ServerEpoch,
    Guid InstanceId,
    Guid ProjectGeneration,
    string QueryHash,
    Revision Revision,
    DateTimeOffset ExpiresAt,
    string Position);

public sealed record PagingCursorBinding(
    Guid ServerEpoch,
    Guid InstanceId,
    Guid ProjectGeneration,
    string QueryHash,
    Revision Revision);

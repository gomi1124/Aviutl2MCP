using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Paging;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Serialization;

namespace AviUtl2MCP.Application.Queries;

public sealed class AviUtlQueryService
{
    private static readonly TimeSpan CURSOR_LIFETIME = TimeSpan.FromMinutes(5);
    private readonly IInstanceResolver _instanceResolver;
    private readonly IAviUtlQueryGateway _queryGateway;
    private readonly IBridgeDiagnosticsGateway _diagnosticsGateway;
    private readonly PagingCursorCodec _cursorCodec;
    private readonly Guid _serverEpoch;
    private readonly TimeProvider _timeProvider;

    public AviUtlQueryService(
        IInstanceResolver instanceResolver,
        IAviUtlQueryGateway queryGateway,
        IBridgeDiagnosticsGateway diagnosticsGateway,
        PagingCursorCodec cursorCodec,
        Guid serverEpoch,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(instanceResolver);
        ArgumentNullException.ThrowIfNull(queryGateway);
        ArgumentNullException.ThrowIfNull(diagnosticsGateway);
        ArgumentNullException.ThrowIfNull(cursorCodec);
        ArgumentOutOfRangeException.ThrowIfEqual(serverEpoch, Guid.Empty);
        _instanceResolver = instanceResolver;
        _queryGateway = queryGateway;
        _diagnosticsGateway = diagnosticsGateway;
        _cursorCodec = cursorCodec;
        _serverEpoch = serverEpoch;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<QueryExecutionResult<StatusData>> GetStatusAsync(
        GetStatusInput input,
        RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            IReadOnlyList<InstanceDescriptor> candidates = await _instanceResolver.ListCandidatesAsync(
                context.CancellationToken).ConfigureAwait(false);
            ApplicationResult<InstanceDescriptor> selection = await _instanceResolver.ResolveAsync(
                input.InstanceId,
                [],
                context.CancellationToken).ConfigureAwait(false);
            if (!selection.IsSuccess)
            {
                bool canReturnCandidates = input.InstanceId is null
                    && (selection.Error!.Code == "aviutl_not_running"
                        || selection.Error.Code == "instance_ambiguous");
                return canReturnCandidates
                    ? CreateDisconnectedStatus(candidates)
                    : CreateSelectionFailure<StatusData>(selection);
            }

            InstanceDescriptor instance = selection.Value!;
            GatewayResponse<StatusData> response = await _diagnosticsGateway.GetStatusAsync(
                CreateRequest(
                    instance,
                    context,
                    input with { InstanceId = instance.InstanceId, TimeoutMs = context.TimeoutMs }),
                context.CancellationToken).ConfigureAwait(false);
            return MapGatewayResponse(response);
        }
        catch (Exception exception) when (IsGatewayException(exception))
        {
            return CreateExceptionFailure<StatusData>(exception);
        }
    }

    public ValueTask<QueryExecutionResult<CapabilitiesData>> GetCapabilitiesAsync(
        GetCapabilitiesInput input,
        RequestContext context)
    {
        return ExecuteSelectedAsync(
            input.InstanceId,
            [],
            context,
            (instance, cancellationToken) => _diagnosticsGateway.GetCapabilitiesAsync(
                CreateRequest(
                    instance,
                    context,
                    input with { InstanceId = instance.InstanceId, TimeoutMs = context.TimeoutMs }),
                cancellationToken));
    }

    public ValueTask<QueryExecutionResult<ProjectData>> GetProjectAsync(
        GetProjectInput input,
        RequestContext context)
    {
        return ExecuteSelectedAsync(
            input.InstanceId,
            [],
            context,
            (instance, cancellationToken) => _queryGateway.GetProjectAsync(
                CreateRequest(
                    instance,
                    context,
                    input with { InstanceId = instance.InstanceId, TimeoutMs = context.TimeoutMs }),
                cancellationToken));
    }

    public ValueTask<QueryExecutionResult<TimelineData>> GetTimelineAsync(
        GetTimelineInput input,
        RequestContext context)
    {
        return ExecutePagedAsync(
            input,
            context,
            [],
            (instance, cursor) => input with
            {
                InstanceId = instance.InstanceId,
                TimeoutMs = context.TimeoutMs,
                Cursor = cursor,
            },
            data => data.NextCursor,
            (data, cursor) => data with { NextCursor = cursor },
            (request, cancellationToken) => _queryGateway.GetTimelineAsync(request, cancellationToken));
    }

    public ValueTask<QueryExecutionResult<ObjectsPageData>> FindObjectsAsync(
        FindObjectsInput input,
        RequestContext context)
    {
        return ExecutePagedAsync(
            input,
            context,
            [],
            (instance, cursor) => input with
            {
                InstanceId = instance.InstanceId,
                TimeoutMs = context.TimeoutMs,
                Cursor = cursor,
            },
            data => data.NextCursor,
            (data, cursor) => data with { NextCursor = cursor },
            (request, cancellationToken) => _queryGateway.FindObjectsAsync(request, cancellationToken));
    }

    public ValueTask<QueryExecutionResult<ObjectData>> GetObjectAsync(
        GetObjectInput input,
        RequestContext context)
    {
        return ExecuteSelectedAsync(
            input.InstanceId,
            [input.Locator],
            context,
            (instance, cancellationToken) => _queryGateway.GetObjectAsync(
                CreateRequest(
                    instance,
                    context,
                    input with { InstanceId = instance.InstanceId, TimeoutMs = context.TimeoutMs }),
                cancellationToken));
    }

    public ValueTask<QueryExecutionResult<EffectsData>> ListEffectsAsync(
        ListEffectsInput input,
        RequestContext context)
    {
        return ExecutePagedAsync(
            input,
            context,
            [],
            (instance, cursor) => input with
            {
                InstanceId = instance.InstanceId,
                TimeoutMs = context.TimeoutMs,
                Cursor = cursor,
            },
            data => data.NextCursor,
            (data, cursor) => data with { NextCursor = cursor },
            (request, cancellationToken) => _queryGateway.ListEffectsAsync(request, cancellationToken));
    }

    public ValueTask<QueryExecutionResult<EffectItemsData>> ListEffectItemsAsync(
        ListEffectItemsInput input,
        RequestContext context)
    {
        return ExecuteSelectedAsync(
            input.InstanceId,
            [],
            context,
            (instance, cancellationToken) => _queryGateway.ListEffectItemsAsync(
                CreateRequest(
                    instance,
                    context,
                    input with { InstanceId = instance.InstanceId, TimeoutMs = context.TimeoutMs }),
                cancellationToken));
    }

    private async ValueTask<QueryExecutionResult<TData>> ExecuteSelectedAsync<TData>(
        Guid? requestedInstanceId,
        IReadOnlyList<ObjectLocator> locators,
        RequestContext context,
        Func<InstanceDescriptor, CancellationToken, ValueTask<GatewayResponse<TData>>> operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            ApplicationResult<InstanceDescriptor> selection = await _instanceResolver.ResolveAsync(
                requestedInstanceId,
                locators,
                context.CancellationToken).ConfigureAwait(false);
            if (!selection.IsSuccess)
            {
                return CreateSelectionFailure<TData>(selection);
            }

            GatewayResponse<TData> response = await operation(
                selection.Value!,
                context.CancellationToken).ConfigureAwait(false);
            return MapGatewayResponse(response);
        }
        catch (Exception exception) when (IsGatewayException(exception))
        {
            return CreateExceptionFailure<TData>(exception);
        }
    }

    private async ValueTask<QueryExecutionResult<TData>> ExecutePagedAsync<TInput, TData>(
        TInput input,
        RequestContext context,
        IReadOnlyList<ObjectLocator> locators,
        Func<InstanceDescriptor, string?, TInput> createParameters,
        Func<TData, string?> getNextCursor,
        Func<TData, string?, TData> setNextCursor,
        Func<GatewayRequest<TInput>, CancellationToken, ValueTask<GatewayResponse<TData>>> operation)
        where TInput : PageInput
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            ApplicationResult<InstanceDescriptor> selection = await _instanceResolver.ResolveAsync(
                input.InstanceId,
                locators,
                context.CancellationToken).ConfigureAwait(false);
            if (!selection.IsSuccess)
            {
                return CreateSelectionFailure<TData>(selection);
            }

            InstanceDescriptor instance = selection.Value!;
            string queryHash = CalculateQueryHash(input);
            PagingCursorState? incomingCursor = null;
            string? bridgeCursor = null;
            if (input.Cursor is not null)
            {
                GatewayResponse<StatusData> status = await _diagnosticsGateway.GetStatusAsync(
                    CreateRequest(
                        instance,
                        context,
                        new GetStatusInput
                        {
                            InstanceId = instance.InstanceId,
                            TimeoutMs = context.TimeoutMs,
                        }),
                    context.CancellationToken).ConfigureAwait(false);
                if (!status.Ok)
                {
                    return MapGatewayFailureAs<TData, StatusData>(status);
                }
                if (!TryCreateCursorBinding(
                        status,
                        _serverEpoch,
                        instance.InstanceId,
                        queryHash,
                        out PagingCursorBinding? binding))
                {
                    return CreateProtocolFailure<TData>(instance.InstanceId);
                }

                ApplicationResult<PagingCursorState> decoded = _cursorCodec.DecodeCursor(input.Cursor, binding!);
                if (!decoded.IsSuccess)
                {
                    return new QueryExecutionResult<TData>(
                        ApplicationResult.Failure<TData>(decoded.Error!),
                        instance.InstanceId,
                        status.Revision,
                        status.ViewRevision);
                }
                incomingCursor = decoded.Value!;
                bridgeCursor = incomingCursor.Position;
            }

            TInput parameters = createParameters(instance, bridgeCursor);
            GatewayResponse<TData> response = await operation(
                CreateRequest(instance, context, parameters),
                context.CancellationToken).ConfigureAwait(false);
            if (!response.Ok)
            {
                return MapGatewayResponse(response);
            }
            if (response.Data is null)
            {
                return CreateProtocolFailure<TData>(instance.InstanceId);
            }

            if (incomingCursor is not null
                && (response.Revision != incomingCursor.Revision
                    || !TryGetProjectGeneration(response.Revision, out Guid projectGeneration)
                    || projectGeneration != incomingCursor.ProjectGeneration))
            {
                return new QueryExecutionResult<TData>(
                    ApplicationResult.Failure<TData>(ApplicationErrors.CreateCursorInvalid("revision")),
                    instance.InstanceId,
                    response.Revision,
                    response.ViewRevision);
            }

            string? rawNextCursor = getNextCursor(response.Data);
            string? publicNextCursor = null;
            if (rawNextCursor is not null)
            {
                if (!TryGetProjectGeneration(response.Revision, out Guid nextProjectGeneration)
                    || response.Revision is null)
                {
                    return CreateProtocolFailure<TData>(instance.InstanceId);
                }
                publicNextCursor = _cursorCodec.EncodeCursor(new PagingCursorState(
                    _serverEpoch,
                    instance.InstanceId,
                    nextProjectGeneration,
                    queryHash,
                    response.Revision.Value,
                    _timeProvider.GetUtcNow().Add(CURSOR_LIFETIME),
                    rawNextCursor));
            }

            TData data = setNextCursor(response.Data, publicNextCursor);
            return new QueryExecutionResult<TData>(
                ApplicationResult.Success(data, response.Warnings),
                response.InstanceId,
                response.Revision,
                response.ViewRevision);
        }
        catch (Exception exception) when (IsGatewayException(exception))
        {
            return CreateExceptionFailure<TData>(exception);
        }
    }

    private static GatewayRequest<TInput> CreateRequest<TInput>(
        InstanceDescriptor instance,
        RequestContext context,
        TInput parameters)
    {
        return new GatewayRequest<TInput>(
            instance.InstanceId,
            context.CorrelationId,
            context.Deadline,
            context.TimeoutMs,
            ExpectedRevision: null,
            DryRun: false,
            parameters);
    }

    private static QueryExecutionResult<TData> MapGatewayResponse<TData>(
        GatewayResponse<TData> response)
    {
        if (!response.Ok)
        {
            return MapGatewayFailureAs<TData, TData>(response);
        }
        if (response.Data is null)
        {
            return CreateProtocolFailure<TData>(response.InstanceId);
        }
        return new QueryExecutionResult<TData>(
            ApplicationResult.Success(response.Data, response.Warnings),
            response.InstanceId,
            response.Revision,
            response.ViewRevision);
    }

    private static QueryExecutionResult<TData> MapGatewayFailureAs<TData, TGatewayData>(
        GatewayResponse<TGatewayData> response)
    {
        GatewayError? gatewayError = response.Error;
        ApplicationError error = gatewayError is null
            ? ApplicationErrors.CreateError(
                "bridge_protocol_error",
                "The Bridge returned a failed response without error details.",
                true)
            : ApplicationErrors.CreateError(
                gatewayError.Code,
                gatewayError.Message,
                gatewayError.CanRetry,
                ConvertDetails(gatewayError.Details));
        return new QueryExecutionResult<TData>(
            ApplicationResult.Failure<TData>(error, warnings: response.Warnings),
            response.InstanceId,
            response.Revision,
            response.ViewRevision);
    }

    private static QueryExecutionResult<TData> CreateSelectionFailure<TData>(
        ApplicationResult<InstanceDescriptor> selection)
    {
        return new QueryExecutionResult<TData>(
            ApplicationResult.Failure<TData>(selection.Error!, warnings: selection.Warnings),
            null,
            null,
            null);
    }

    private static QueryExecutionResult<StatusData> CreateDisconnectedStatus(
        IReadOnlyList<InstanceDescriptor> candidates)
    {
        string bridgeStatus = candidates.Count == 0 ? "disconnected" : "selectionRequired";
        StatusData status = new(
            ConnectionState.Disconnected,
            [new ComponentStatus("bridge", bridgeStatus, null)],
            ProjectState.Unknown,
            EditState.Unknown,
            SelectedInstance: null,
            candidates.Select(candidate => new AviUtlInstance(
                candidate.InstanceId,
                candidate.ProcessId,
                candidate.BridgeVersion,
                "available")).ToArray());
        return new QueryExecutionResult<StatusData>(
            ApplicationResult.Success(status),
            null,
            null,
            null);
    }

    private static QueryExecutionResult<TData> CreateProtocolFailure<TData>(Guid instanceId)
    {
        return new QueryExecutionResult<TData>(
            ApplicationResult.Failure<TData>(ApplicationErrors.CreateError(
                "bridge_protocol_error",
                "The Bridge response omitted paging revision metadata.",
                true)),
            instanceId,
            null,
            null);
    }

    private static QueryExecutionResult<TData> CreateExceptionFailure<TData>(Exception exception)
    {
        ApplicationError error = exception is OperationCanceledException
            ? ApplicationErrors.CreateError("timeout", "The AviUtl2 request timed out.", true)
            : ApplicationErrors.CreateError(
                exception is InvalidDataException or JsonException
                    ? "bridge_protocol_error"
                    : "bridge_unavailable",
                exception.Message,
                true);
        return new QueryExecutionResult<TData>(
            ApplicationResult.Failure<TData>(error),
            null,
            null,
            null);
    }

    private static bool TryCreateCursorBinding<TData>(
        GatewayResponse<TData> response,
        Guid serverEpoch,
        Guid instanceId,
        string queryHash,
        out PagingCursorBinding? binding)
    {
        binding = null;
        if (!response.Ok || response.Revision is null
            || !TryGetProjectGeneration(response.Revision, out Guid projectGeneration))
        {
            return false;
        }
        binding = new PagingCursorBinding(
            serverEpoch,
            instanceId,
            projectGeneration,
            queryHash,
            response.Revision.Value);
        return true;
    }

    private static bool TryGetProjectGeneration(Revision? revision, out Guid projectGeneration)
    {
        projectGeneration = Guid.Empty;
        if (revision is null)
        {
            return false;
        }
        string[] segments = revision.Value.Value.Split(':', StringSplitOptions.None);
        return segments.Length == 3
            && Guid.TryParse(segments[1], out projectGeneration)
            && projectGeneration != Guid.Empty;
    }

    private static string CalculateQueryHash<TInput>(TInput input)
    {
        JsonObject normalized = JsonNode.Parse(ContractJsonSerializer.SerializeContract(input))!.AsObject();
        _ = normalized.Remove("instanceId");
        _ = normalized.Remove("timeoutMs");
        _ = normalized.Remove("cursor");
        byte[] bytes = Encoding.UTF8.GetBytes(normalized.ToJsonString());
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static Dictionary<string, JsonNode?> ConvertDetails(JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonNode?>
            {
                ["value"] = JsonNode.Parse(details.GetRawText()),
            };
        }
        return details.EnumerateObject().ToDictionary(
            property => property.Name,
            property => JsonNode.Parse(property.Value.GetRawText()),
            StringComparer.Ordinal);
    }

    private static bool IsGatewayException(Exception exception)
    {
        return exception is OperationCanceledException
            or TimeoutException
            or IOException
            or KeyNotFoundException
            or InvalidDataException
            or JsonException;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Validation;

namespace AviUtl2MCP.Application.Edits;

public sealed class AviUtlEditService(
    IInstanceResolver instanceResolver,
    IAviUtlEditGateway editGateway)
{
    private readonly IInstanceResolver _instanceResolver = instanceResolver
        ?? throw new ArgumentNullException(nameof(instanceResolver));
    private readonly IAviUtlEditGateway _editGateway = editGateway
        ?? throw new ArgumentNullException(nameof(editGateway));

    public ValueTask<QueryExecutionResult<CreateObjectData>> CreateObjectAsync(
        CreateObjectInput input,
        RequestContext context) => ExecuteAsync<CreateObjectArgs, CreateObjectData>(
            "object.create",
            input,
            [],
            new CreateObjectArgs(input.Effect, input.Placement)
            {
                Name = input.Name,
                Items = input.Items,
            },
            context);

    public ValueTask<QueryExecutionResult<CreateObjectData>> CreateMediaObjectAsync(
        CreateMediaObjectInput input,
        RequestContext context) => ExecuteAsync<CreateMediaObjectArgs, CreateObjectData>(
            "object.createMedia",
            input,
            [],
            new CreateMediaObjectArgs(
                RequestValidator.NormalizePath(input.MediaPath),
                input.Placement) { Name = input.Name },
            context);

    public ValueTask<QueryExecutionResult<CreateObjectsData>> CreateAliasObjectAsync(
        CreateAliasObjectInput input,
        RequestContext context) => ExecuteAsync<CreateAliasObjectArgs, CreateObjectsData>(
            "object.createAlias",
            input,
            [],
            new CreateAliasObjectArgs(input.Alias, input.Placement) { Name = input.Name },
            context);

    public ValueTask<QueryExecutionResult<UpdatedObjectData>> MoveObjectAsync(
        MoveObjectInput input,
        RequestContext context) => ExecuteAsync<MoveObjectArgs, UpdatedObjectData>(
            "object.move",
            input,
            [input.Locator],
            new MoveObjectArgs(input.Locator, input.Placement),
            context);

    public ValueTask<QueryExecutionResult<DeleteData>> DeleteObjectAsync(
        DeleteObjectInput input,
        RequestContext context) => ExecuteAsync<DeleteObjectArgs, DeleteData>(
            "object.delete",
            input,
            [input.Locator],
            new DeleteObjectArgs(input.Locator),
            context);

    public ValueTask<QueryExecutionResult<UpdatedObjectData>> SetObjectNameAsync(
        SetObjectNameInput input,
        RequestContext context) => ExecuteAsync<SetObjectNameArgs, UpdatedObjectData>(
            "object.setName",
            input,
            [input.Locator],
            new SetObjectNameArgs(input.Locator, input.Name),
            context);

    public ValueTask<QueryExecutionResult<UpdatedObjectData>> CreateObjectSectionAsync(
        CreateObjectSectionInput input,
        RequestContext context) => ExecuteAsync<CreateObjectSectionArgs, UpdatedObjectData>(
            "object.createSection",
            input,
            [input.Locator],
            new CreateObjectSectionArgs(input.Locator, input.Frame),
            context);

    public ValueTask<QueryExecutionResult<UpdatedObjectData>> DeleteObjectSectionAsync(
        DeleteObjectSectionInput input,
        RequestContext context) => ExecuteAsync<DeleteObjectSectionArgs, UpdatedObjectData>(
            "object.deleteSection",
            input,
            [input.Locator],
            new DeleteObjectSectionArgs(input.Locator, input.Section),
            context);

    public ValueTask<QueryExecutionResult<UpdatedObjectData>> MoveObjectSectionAsync(
        MoveObjectSectionInput input,
        RequestContext context) => ExecuteAsync<MoveObjectSectionArgs, UpdatedObjectData>(
            "object.moveSection",
            input,
            [input.Locator],
            new MoveObjectSectionArgs(input.Locator, input.Section, input.Frame),
            context);

    public ValueTask<QueryExecutionResult<EffectItemUpdateData>> SetEffectItemAsync(
        SetEffectItemInput input,
        RequestContext context) => ExecuteAsync<SetEffectItemArgs, EffectItemUpdateData>(
            "effect.setItem",
            input,
            [input.Locator],
            new SetEffectItemArgs(input.Locator, input.Effect, input.ItemName, input.Value),
            context);

    public ValueTask<QueryExecutionResult<SaveProjectData>> SaveProjectAsync(
        SaveProjectInput input,
        RequestContext context) => ExecuteCoreAsync(
            input,
            [],
            context,
            (instance, cancellationToken) => _editGateway.SaveProjectAsync(
                new GatewayRequest<SaveProjectArgs>(
                    instance.InstanceId,
                    context.CorrelationId,
                    context.Deadline,
                    context.TimeoutMs,
                    input.ExpectedRevision,
                    false,
                    new SaveProjectArgs()),
                cancellationToken));

    public ValueTask<QueryExecutionResult<EffectStateUpdateData>> SetEffectStateAsync(
        SetEffectStateInput input,
        RequestContext context) => ExecuteAsync<SetEffectStateArgs, EffectStateUpdateData>(
            "effect.setState",
            input,
            [input.Locator],
            new SetEffectStateArgs(input.Locator, input.Effect)
            {
                IsEnabled = input.IsEnabled,
                IsLocked = input.IsLocked,
            },
            context);

    public ValueTask<QueryExecutionResult<LayerUpdateData>> SetLayerAsync(
        SetLayerInput input,
        RequestContext context) => ExecuteAsync<SetLayerArgs, LayerUpdateData>(
            "layer.set",
            input,
            [],
            new SetLayerArgs(input.Layer)
            {
                SceneId = input.SceneId,
                Name = input.Name,
                IsVisible = input.IsVisible,
                IsLocked = input.IsLocked,
            },
            context);

    public ValueTask<QueryExecutionResult<CursorData>> SetCursorAsync(
        SetCursorInput input,
        RequestContext context) => ExecuteCoreAsync(
            input,
            [],
            context,
            (instance, cancellationToken) => _editGateway.SetCursorAsync(
                new GatewayRequest<SetCursorInput>(
                    instance.InstanceId,
                    context.CorrelationId,
                    context.Deadline,
                    context.TimeoutMs,
                    null,
                    false,
                    input),
                cancellationToken));

    public ValueTask<QueryExecutionResult<BatchData>> ExecuteBatchAsync(
        ExecuteBatchInput input,
        RequestContext context)
    {
        ExecuteBatchInput normalized = input with
        {
            Operations = input.Operations.Select(NormalizeBatchOperation).ToArray(),
        };
        IReadOnlyList<ObjectLocator> locators = normalized.Operations
            .Select(GetBatchLocator)
            .Where(locator => locator is not null)
            .Cast<ObjectLocator>()
            .ToArray();
        return ExecuteCoreAsync(
            normalized,
            locators,
            context,
            (instance, cancellationToken) => _editGateway.ExecuteBatchAsync(
                new GatewayRequest<ExecuteBatchInput>(
                    instance.InstanceId,
                    context.CorrelationId,
                    context.Deadline,
                    context.TimeoutMs,
                    normalized.ExpectedRevision,
                    normalized.DryRun,
                    normalized),
                cancellationToken));
    }

    private ValueTask<QueryExecutionResult<TData>> ExecuteAsync<TArgs, TData>(
        string operation,
        MutationInput input,
        IReadOnlyList<ObjectLocator> locators,
        TArgs args,
        RequestContext context) => ExecuteCoreAsync(
            input,
            locators,
            context,
            (instance, cancellationToken) => _editGateway.ExecuteEditAsync<TArgs, TData>(
                operation,
                new GatewayRequest<TArgs>(
                    instance.InstanceId,
                    context.CorrelationId,
                    context.Deadline,
                    context.TimeoutMs,
                    input.ExpectedRevision,
                    input.DryRun,
                    args),
                cancellationToken));

    private async ValueTask<QueryExecutionResult<TData>> ExecuteCoreAsync<TInput, TData>(
        TInput input,
        IReadOnlyList<ObjectLocator> locators,
        RequestContext context,
        Func<InstanceDescriptor, CancellationToken, ValueTask<GatewayResponse<TData>>> execute)
        where TInput : CommonInput
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(execute);
        try
        {
            ApplicationResult<InstanceDescriptor> selection = await _instanceResolver.ResolveAsync(
                input.InstanceId,
                locators,
                context.CancellationToken).ConfigureAwait(false);
            if (!selection.IsSuccess)
            {
                return new QueryExecutionResult<TData>(
                    ApplicationResult.Failure<TData>(selection.Error!, warnings: selection.Warnings),
                    null,
                    null,
                    null);
            }
            InstanceDescriptor instance = selection.Value!;
            GatewayResponse<TData> response = await execute(
                instance,
                context.CancellationToken).ConfigureAwait(false);
            if (!response.Ok)
            {
                GatewayError? gatewayError = response.Error;
                ApplicationError error = gatewayError is null
                    ? ApplicationErrors.CreateError(
                        "bridge_protocol_error",
                        "The Bridge returned a failed edit response without error details.",
                        true)
                    : ApplicationErrors.CreateError(
                        gatewayError.Code,
                        gatewayError.Message,
                        gatewayError.CanRetry,
                        ConvertDetails(gatewayError.Details));
                return new QueryExecutionResult<TData>(
                    ApplicationResult.Failure(error, response.Data, response.Warnings),
                    response.InstanceId,
                    response.Revision,
                    response.ViewRevision);
            }
            if (response.Data is null)
            {
                return new QueryExecutionResult<TData>(
                    ApplicationResult.Failure<TData>(ApplicationErrors.CreateError(
                        "bridge_protocol_error",
                        "The Bridge edit response omitted result data.",
                        true)),
                    response.InstanceId,
                    response.Revision,
                    response.ViewRevision);
            }
            return new QueryExecutionResult<TData>(
                ApplicationResult.Success(response.Data, response.Warnings),
                response.InstanceId,
                response.Revision,
                response.ViewRevision);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or IOException
            or InvalidDataException
            or JsonException
            or TimeoutException)
        {
            ApplicationError error = exception is OperationCanceledException or TimeoutException
                ? ApplicationErrors.CreateError("operation_timeout", "The edit request timed out.", true)
                : ApplicationErrors.CreateError(
                    exception is InvalidDataException or JsonException
                        ? "bridge_protocol_error"
                        : "bridge_not_connected",
                    exception.Message,
                    true);
            return new QueryExecutionResult<TData>(
                ApplicationResult.Failure<TData>(error),
                null,
                null,
                null);
        }
    }

    private static Dictionary<string, JsonNode?> ConvertDetails(JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonNode?>();
        }
        return details.EnumerateObject().ToDictionary(
            property => property.Name,
            property => JsonNode.Parse(property.Value.GetRawText()),
            StringComparer.Ordinal);
    }

    private static BatchOperation NormalizeBatchOperation(BatchOperation operation) => operation switch
    {
        BatchCreateMediaObject value => value with
        {
            Args = value.Args with { MediaPath = RequestValidator.NormalizePath(value.Args.MediaPath) },
        },
        _ => operation,
    };

    private static ObjectLocator? GetBatchLocator(BatchOperation operation) => operation switch
    {
        BatchMoveObject value => value.Args.Locator,
        BatchDeleteObject value => value.Args.Locator,
        BatchSetObjectName value => value.Args.Locator,
        BatchCreateObjectSection value => value.Args.Locator,
        BatchDeleteObjectSection value => value.Args.Locator,
        BatchMoveObjectSection value => value.Args.Locator,
        BatchSetEffectItem value => value.Args.Locator,
        BatchSetEffectState value => value.Args.Locator,
        _ => null,
    };
}

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

    private async ValueTask<QueryExecutionResult<TData>> ExecuteAsync<TArgs, TData>(
        string operation,
        MutationInput input,
        IReadOnlyList<ObjectLocator> locators,
        TArgs args,
        RequestContext context)
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
                return new QueryExecutionResult<TData>(
                    ApplicationResult.Failure<TData>(selection.Error!, warnings: selection.Warnings),
                    null,
                    null,
                    null);
            }
            InstanceDescriptor instance = selection.Value!;
            GatewayResponse<TData> response = await _editGateway.ExecuteEditAsync<TArgs, TData>(
                operation,
                new GatewayRequest<TArgs>(
                    instance.InstanceId,
                    context.CorrelationId,
                    context.Deadline,
                    context.TimeoutMs,
                    input.ExpectedRevision,
                    input.DryRun,
                    args),
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
                    ApplicationResult.Failure<TData>(error, warnings: response.Warnings),
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
}

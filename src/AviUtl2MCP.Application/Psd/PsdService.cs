using System.Text.Json;
using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Validation;

namespace AviUtl2MCP.Application.Psd;

public sealed class PsdService(
    IInstanceResolver instanceResolver,
    IAviUtlPsdGateway psdGateway)
{
    private readonly IInstanceResolver _instanceResolver = instanceResolver
        ?? throw new ArgumentNullException(nameof(instanceResolver));
    private readonly IAviUtlPsdGateway _psdGateway = psdGateway
        ?? throw new ArgumentNullException(nameof(psdGateway));

    public ValueTask<QueryExecutionResult<CreateObjectData>> CreateAsync(
        PsdCreateInput input,
        RequestContext context)
    {
        RequestValidator.ValidatePsdMutationInput(input);
        string psdPath = RequestValidator.NormalizePath(input.PsdPath);
        return ExecuteMutationAsync<PsdCreateArgs, CreateObjectData>(
            "psd.create",
            input,
            [],
            new PsdCreateArgs(psdPath, input.Placement) { Name = input.Name },
            context);
    }

    public ValueTask<QueryExecutionResult<PsdSetupData>> SetupAsync(
        PsdSetupInput input,
        RequestContext context)
    {
        RequestValidator.ValidatePsdMutationInput(input);
        return ExecuteMutationAsync<PsdSetupArgs, PsdSetupData>(
            "psd.setup",
            input,
            [],
            new PsdSetupArgs
            {
                SceneId = input.SceneId,
                PreferredLayer = input.PreferredLayer,
                PreferredFrame = input.PreferredFrame,
                CreateIfMissing = input.CreateIfMissing,
            },
            context);
    }

    public ValueTask<QueryExecutionResult<PsdCharacterData>> SetCharacterAsync(
        PsdSetCharacterInput input,
        RequestContext context)
    {
        RequestValidator.ValidatePsdMutationInput(input);
        return ExecuteMutationAsync<PsdSetCharacterArgs, PsdCharacterData>(
            "psd.setCharacter",
            input,
            [input.Locator],
            new PsdSetCharacterArgs(input.Locator, input.CharacterId),
            context);
    }

    public ValueTask<QueryExecutionResult<PsdLayerStateData>> SetLayerStateAsync(
        PsdSetLayerStateInput input,
        RequestContext context)
    {
        RequestValidator.ValidatePsdMutationInput(input);
        return ExecuteMutationAsync<PsdSetLayerStateArgs, PsdLayerStateData>(
            "psd.setLayerState",
            input,
            [input.Locator],
            new PsdSetLayerStateArgs(input.Locator, input.LayerState),
            context);
    }

    public ValueTask<QueryExecutionResult<PsdVoiceData>> CreateVoiceAsync(
        PsdCreateVoiceInput input,
        RequestContext context)
    {
        RequestValidator.ValidatePsdMutationInput(input);
        string audioPath = RequestValidator.NormalizePath(input.AudioPath);
        string textPath = RequestValidator.NormalizePath(
            input.TextPath ?? Path.ChangeExtension(audioPath, ".txt"));
        string? labPath = input.LabPath is null
            ? GetExistingLabCompanion(audioPath)
            : RequestValidator.NormalizePath(input.LabPath);
        IReadOnlyList<ObjectLocator> locators = input.PsdLocator is null
            ? []
            : [input.PsdLocator];
        return ExecuteMutationAsync<PsdCreateVoiceArgs, PsdVoiceData>(
            "psd.createVoice",
            input,
            locators,
            new PsdCreateVoiceArgs(
                audioPath,
                textPath,
                input.CharacterId,
                input.Placement)
            {
                LabPath = labPath,
                PsdLocator = input.PsdLocator,
            },
            context);
    }

    public async ValueTask<QueryExecutionResult<PsdValidateData>> ValidateAsync(
        PsdValidateInput input,
        RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequestValidator.ValidatePsdValidateInput(input);
        IReadOnlyList<ObjectLocator> locators = input.Locator is null
            ? []
            : [input.Locator];
        return await ExecuteCoreAsync(
            input.InstanceId,
            locators,
            context,
            (instance, cancellationToken) => _psdGateway.ValidatePsdAsync(
                new GatewayRequest<PsdValidateInput>(
                    instance.InstanceId,
                    context.CorrelationId,
                    context.Deadline,
                    context.TimeoutMs,
                    null,
                    false,
                    input with
                    {
                        InstanceId = instance.InstanceId,
                        TimeoutMs = context.TimeoutMs,
                    }),
                cancellationToken)).ConfigureAwait(false);
    }

    private ValueTask<QueryExecutionResult<TData>> ExecuteMutationAsync<TParameters, TData>(
        string operation,
        MutationInput input,
        IReadOnlyList<ObjectLocator> locators,
        TParameters parameters,
        RequestContext context) => ExecuteCoreAsync(
            input.InstanceId,
            locators,
            context,
            (instance, cancellationToken) => _psdGateway.ExecutePsdAsync<TParameters, TData>(
                operation,
                new GatewayRequest<TParameters>(
                    instance.InstanceId,
                    context.CorrelationId,
                    context.Deadline,
                    context.TimeoutMs,
                    input.ExpectedRevision,
                    input.DryRun,
                    parameters),
                cancellationToken));

    private async ValueTask<QueryExecutionResult<TData>> ExecuteCoreAsync<TData>(
        Guid? instanceId,
        IReadOnlyList<ObjectLocator> locators,
        RequestContext context,
        Func<InstanceDescriptor, CancellationToken, ValueTask<GatewayResponse<TData>>> execute)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(execute);
        try
        {
            ApplicationResult<InstanceDescriptor> selection = await _instanceResolver.ResolveAsync(
                instanceId,
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
                        "The Bridge returned a failed PSD response without error details.",
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
                        "The Bridge PSD response omitted result data.",
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
                ? ApplicationErrors.CreateError("operation_timeout", "The PSD request timed out.", true)
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

    private static string? GetExistingLabCompanion(string audioPath)
    {
        string labPath = Path.ChangeExtension(audioPath, ".lab");
        return File.Exists(labPath) ? Path.GetFullPath(labPath) : null;
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

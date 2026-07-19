using System.ComponentModel;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Edits;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Results;
using AviUtl2MCP.Application.Validation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AviUtl2MCP.Server.Tools;

[McpServerToolType]
public sealed class EditToolSet(
    RequestContextFactory requestContextFactory,
    AviUtlEditService editService)
{
    private const int EDIT_DEFAULT_TIMEOUT_MS = 10_000;
    private readonly RequestContextFactory _requestContextFactory = requestContextFactory;
    private readonly AviUtlEditService _editService = editService;

    [McpServerTool(
        Name = "aviutl_create_object",
        Title = "AviUtl2 object作成",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<CreateObjectData>))]
    [Description("effectからobjectを作成します。dryRunで予定差分だけを検証できます。")]
    public ValueTask<CallToolResult> CreateObjectAsync(
        Revision expectedRevision,
        EffectDefinitionSelector effect,
        Placement placement,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        string? name = null,
        IReadOnlyList<EffectItemAssignment>? items = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
            new CreateObjectInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                Effect = effect,
                Placement = placement,
                Name = name,
                Items = items,
            },
            _editService.CreateObjectAsync,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_create_media_object",
        Title = "AviUtl2 media object作成",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<CreateObjectData>))]
    [Description("既存のローカルmedia fileからobjectを作成します。")]
    public ValueTask<CallToolResult> CreateMediaObjectAsync(
        Revision expectedRevision,
        string mediaPath,
        Placement placement,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        string? name = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
            new CreateMediaObjectInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                MediaPath = mediaPath,
                Placement = placement,
                Name = name,
            },
            _editService.CreateMediaObjectAsync,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_create_alias_object",
        Title = "AviUtl2 alias object作成",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<CreateObjectsData>))]
    [Description("UTF-8 object aliasから1件以上のobjectを作成します。")]
    public ValueTask<CallToolResult> CreateAliasObjectAsync(
        Revision expectedRevision,
        string alias,
        Placement placement,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        string? name = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
            new CreateAliasObjectInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                Alias = alias,
                Placement = placement,
                Name = name,
            },
            _editService.CreateAliasObjectAsync,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_move_object",
        Title = "AviUtl2 object移動",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<UpdatedObjectData>))]
    [Description("Locatorを再解決し、objectの長さを保って移動します。")]
    public ValueTask<CallToolResult> MoveObjectAsync(
        Revision expectedRevision,
        ObjectLocator locator,
        MovePlacement placement,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default) => ExecuteAsync(
            new MoveObjectInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                Locator = locator,
                Placement = placement,
            },
            _editService.MoveObjectAsync,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_delete_object",
        Title = "AviUtl2 object削除",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<DeleteData>))]
    [Description("Locatorを再解決してobjectを削除し、削除前snapshotを返します。")]
    public ValueTask<CallToolResult> DeleteObjectAsync(
        Revision expectedRevision,
        ObjectLocator locator,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default) => ExecuteAsync(
            new DeleteObjectInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                Locator = locator,
            },
            _editService.DeleteObjectAsync,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_set_object_name",
        Title = "AviUtl2 object名称変更",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<UpdatedObjectData>))]
    [Description("Locatorを再解決してobject名を変更します。空文字は標準名へ戻します。")]
    public ValueTask<CallToolResult> SetObjectNameAsync(
        Revision expectedRevision,
        ObjectLocator locator,
        string name,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default) => ExecuteAsync(
            new SetObjectNameInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                Locator = locator,
                Name = name,
            },
            _editService.SetObjectNameAsync,
            cancellationToken);

    private async ValueTask<CallToolResult> ExecuteAsync<TInput, TData>(
        TInput input,
        Func<TInput, RequestContext, ValueTask<QueryExecutionResult<TData>>> execute,
        CancellationToken cancellationToken)
        where TInput : MutationInput
    {
        RequestContext context;
        try
        {
            context = _requestContextFactory.CreateContext(
                input.InstanceId,
                input.TimeoutMs,
                EDIT_DEFAULT_TIMEOUT_MS,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return CreateInvalidArgument<TData>(exception.Message, cancellationToken);
        }
        using (context)
        {
            input = (TInput)(input with { TimeoutMs = context.TimeoutMs });
            try
            {
                RequestValidator.ValidateEditInput(input);
            }
            catch (Exception exception) when (exception is ArgumentException
                or OverflowException
                or IOException
                or UnauthorizedAccessException)
            {
                return CreateInvalidArgument<TData>(context, exception.Message);
            }
            QueryExecutionResult<TData> execution = await execute(input, context).ConfigureAwait(false);
            ToolEnvelope<TData> envelope = ToolResultFactory.CreateEnvelope(
                execution.Result,
                context,
                execution.InstanceId,
                execution.Revision,
                execution.ViewRevision);
            return McpToolResultFactory.Create(envelope);
        }
    }

    private CallToolResult CreateInvalidArgument<TData>(
        string message,
        CancellationToken cancellationToken)
    {
        using RequestContext context = _requestContextFactory.CreateContext(
            null,
            null,
            EDIT_DEFAULT_TIMEOUT_MS,
            cancellationToken);
        return CreateInvalidArgument<TData>(context, message);
    }

    private static CallToolResult CreateInvalidArgument<TData>(RequestContext context, string message)
    {
        ToolEnvelope<TData> envelope = ToolResultFactory.CreateEnvelope(
            ApplicationResult.Failure<TData>(ApplicationErrors.CreateInvalidArgument(message)),
            context);
        return McpToolResultFactory.Create(envelope);
    }
}

using System.ComponentModel;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Results;
using AviUtl2MCP.Application.Validation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AviUtl2MCP.Server.Tools;

[McpServerToolType]
public sealed class ReadToolSet(
    RequestContextFactory requestContextFactory,
    AviUtlQueryService queryService)
{
    private const int STATUS_DEFAULT_TIMEOUT_MS = 2_000;
    private const int READ_DEFAULT_TIMEOUT_MS = 5_000;
    private readonly RequestContextFactory _requestContextFactory = requestContextFactory;
    private readonly AviUtlQueryService _queryService = queryService;

    [McpServerTool(
        Name = "aviutl_get_status",
        Title = "AviUtl2状態取得",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<StatusData>))]
    [Description("AviUtl2接続、component、project、編集状態と候補instanceを取得します。未接続も構造化された成功結果です。")]
    public ValueTask<CallToolResult> GetStatusAsync(
        [Description("対象AviUtl2 instance。省略時は候補を安全に選択します。")]
        Guid? instanceId = null,
        [Description("timeout（100～120000ミリ秒）。")]
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            instanceId,
            timeoutMs,
            STATUS_DEFAULT_TIMEOUT_MS,
            context => new GetStatusInput { InstanceId = instanceId, TimeoutMs = context.TimeoutMs },
            (input, context) => _queryService.GetStatusAsync(input, context),
            cancellationToken);
    }

    [McpServerTool(
        Name = "aviutl_get_capabilities",
        Title = "AviUtl2能力取得",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<CapabilitiesData>))]
    [Description("28操作の利用可否、component version、V1制限値を取得します。")]
    public ValueTask<CallToolResult> GetCapabilitiesAsync(
        Guid? instanceId = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            instanceId,
            timeoutMs,
            STATUS_DEFAULT_TIMEOUT_MS,
            context => new GetCapabilitiesInput { InstanceId = instanceId, TimeoutMs = context.TimeoutMs },
            (input, context) => _queryService.GetCapabilitiesAsync(input, context),
            cancellationToken);
    }

    [McpServerTool(
        Name = "aviutl_get_project",
        Title = "AviUtl2 project取得",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<ProjectData>))]
    [Description("現在のproject、scene、cursor、選択範囲を1-based座標で取得します。")]
    public ValueTask<CallToolResult> GetProjectAsync(
        Guid? instanceId = null,
        int? timeoutMs = null,
        [Description("scene一覧を含めるか。")]
        bool includeScenes = true,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            instanceId,
            timeoutMs,
            READ_DEFAULT_TIMEOUT_MS,
            context => new GetProjectInput
            {
                InstanceId = instanceId,
                TimeoutMs = context.TimeoutMs,
                IncludeScenes = includeScenes,
            },
            (input, context) => _queryService.GetProjectAsync(input, context),
            cancellationToken);
    }

    [McpServerTool(
        Name = "aviutl_get_timeline",
        Title = "AviUtl2 timeline取得",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<TimelineData>))]
    [Description("timelineを範囲指定し、改ざん防止cursor付きでpage取得します。")]
    public ValueTask<CallToolResult> GetTimelineAsync(
        Guid? instanceId = null,
        int? timeoutMs = null,
        int? sceneId = null,
        int? layerStart = null,
        int? layerEnd = null,
        int? startFrame = null,
        int? endFrame = null,
        TimelineDetail detail = TimelineDetail.Summary,
        int limit = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            instanceId,
            timeoutMs,
            READ_DEFAULT_TIMEOUT_MS,
            context => new GetTimelineInput
            {
                InstanceId = instanceId,
                TimeoutMs = context.TimeoutMs,
                SceneId = sceneId,
                LayerStart = layerStart,
                LayerEnd = layerEnd,
                StartFrame = startFrame,
                EndFrame = endFrame,
                Detail = detail,
                Limit = limit,
                Cursor = cursor,
            },
            (input, context) => _queryService.GetTimelineAsync(input, context),
            cancellationToken);
    }

    [McpServerTool(
        Name = "aviutl_find_objects",
        Title = "AviUtl2 object検索",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<ObjectsPageData>))]
    [Description("座標、名称、effect、media pathでobjectを検索します。")]
    public ValueTask<CallToolResult> FindObjectsAsync(
        Guid? instanceId = null,
        int? timeoutMs = null,
        int? sceneId = null,
        int? layerStart = null,
        int? layerEnd = null,
        int? startFrame = null,
        int? endFrame = null,
        string? nameContains = null,
        string? effectName = null,
        string? mediaPath = null,
        int limit = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            instanceId,
            timeoutMs,
            READ_DEFAULT_TIMEOUT_MS,
            context => new FindObjectsInput
            {
                InstanceId = instanceId,
                TimeoutMs = context.TimeoutMs,
                SceneId = sceneId,
                LayerStart = layerStart,
                LayerEnd = layerEnd,
                StartFrame = startFrame,
                EndFrame = endFrame,
                NameContains = nameContains,
                EffectName = effectName,
                MediaPath = mediaPath,
                Limit = limit,
                Cursor = cursor,
            },
            (input, context) => _queryService.FindObjectsAsync(input, context),
            cancellationToken);
    }

    [McpServerTool(
        Name = "aviutl_get_object",
        Title = "AviUtl2 object詳細取得",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<ObjectData>))]
    [Description("安全なLocatorを再解決し、aliasとeffect itemを取得します。")]
    public ValueTask<CallToolResult> GetObjectAsync(
        ObjectLocator locator,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool includeAlias = false,
        bool includeEffectItems = true,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            instanceId,
            timeoutMs,
            READ_DEFAULT_TIMEOUT_MS,
            context => new GetObjectInput
            {
                InstanceId = instanceId,
                TimeoutMs = context.TimeoutMs,
                Locator = locator,
                IncludeAlias = includeAlias,
                IncludeEffectItems = includeEffectItems,
            },
            (input, context) => _queryService.GetObjectAsync(input, context),
            cancellationToken);
    }

    [McpServerTool(
        Name = "aviutl_list_effects",
        Title = "AviUtl2 effect一覧",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<EffectsData>))]
    [Description("effect definitionと独立したmodule、font、palette catalogを取得します。")]
    public ValueTask<CallToolResult> ListEffectsAsync(
        Guid? instanceId = null,
        int? timeoutMs = null,
        EffectDefinitionType? category = null,
        string? nameContains = null,
        int limit = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            instanceId,
            timeoutMs,
            READ_DEFAULT_TIMEOUT_MS,
            context => new ListEffectsInput
            {
                InstanceId = instanceId,
                TimeoutMs = context.TimeoutMs,
                Category = category,
                NameContains = nameContains,
                Limit = limit,
                Cursor = cursor,
            },
            (input, context) => _queryService.ListEffectsAsync(input, context),
            cancellationToken);
    }

    [McpServerTool(
        Name = "aviutl_list_effect_items",
        Title = "AviUtl2 effect item一覧",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<EffectItemsData>))]
    [Description("effect itemのtype、codec、書込可否と公開choiceを取得します。")]
    public ValueTask<CallToolResult> ListEffectItemsAsync(
        EffectDefinitionSelector effect,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool includeChoices = true,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            instanceId,
            timeoutMs,
            READ_DEFAULT_TIMEOUT_MS,
            context => new ListEffectItemsInput
            {
                InstanceId = instanceId,
                TimeoutMs = context.TimeoutMs,
                Effect = effect,
                IncludeChoices = includeChoices,
            },
            (input, context) => _queryService.ListEffectItemsAsync(input, context),
            cancellationToken);
    }

    private async ValueTask<CallToolResult> ExecuteAsync<TInput, TData>(
        Guid? instanceId,
        int? timeoutMs,
        int defaultTimeoutMs,
        Func<RequestContext, TInput> createInput,
        Func<TInput, RequestContext, ValueTask<QueryExecutionResult<TData>>> execute,
        CancellationToken cancellationToken)
        where TInput : CommonInput
    {
        RequestContext context;
        try
        {
            context = _requestContextFactory.CreateContext(
                instanceId,
                timeoutMs,
                defaultTimeoutMs,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return CreateInvalidArgumentResult<TData>(
                instanceId,
                defaultTimeoutMs,
                exception.Message,
                cancellationToken);
        }

        using (context)
        {
            TInput input = createInput(context);
            try
            {
                RequestValidator.ValidateReadInput(input);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                return CreateInvalidArgumentResult<TData>(context, exception.Message);
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

    private CallToolResult CreateInvalidArgumentResult<TData>(
        Guid? instanceId,
        int defaultTimeoutMs,
        string message,
        CancellationToken cancellationToken)
    {
        using RequestContext context = _requestContextFactory.CreateContext(
            instanceId,
            timeoutMs: null,
            defaultTimeoutMs,
            cancellationToken);
        return CreateInvalidArgumentResult<TData>(context, message);
    }

    private static CallToolResult CreateInvalidArgumentResult<TData>(
        RequestContext context,
        string message)
    {
        ToolEnvelope<TData> envelope = ToolResultFactory.CreateEnvelope(
            ApplicationResult.Failure<TData>(ApplicationErrors.CreateInvalidArgument(message)),
            context);
        return McpToolResultFactory.Create(envelope);
    }
}

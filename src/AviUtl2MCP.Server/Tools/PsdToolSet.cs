using System.ComponentModel;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Psd;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Results;
using AviUtl2MCP.Application.Validation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AviUtl2MCP.Server.Tools;

[McpServerToolType]
public sealed class PsdToolSet(
    RequestContextFactory requestContextFactory,
    PsdService psdService)
{
    private const int PSD_DEFAULT_TIMEOUT_MS = 10_000;
    private const int PSD_VOICE_DEFAULT_TIMEOUT_MS = 30_000;
    private readonly RequestContextFactory _requestContextFactory = requestContextFactory;
    private readonly PsdService _psdService = psdService;

    [McpServerTool(
        Name = "aviutl_psd_create",
        Title = "AviUtl2 PSD作成",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<CreateObjectData>))]
    [Description("GCMZDropsでPSD/PSBを配置し、PSDToolKit2 objectをSDKで再検索します。")]
    public ValueTask<CallToolResult> CreateAsync(
        Revision expectedRevision,
        string psdPath,
        Placement placement,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        string? name = null,
        CancellationToken cancellationToken = default) => ExecuteMutationAsync(
            new PsdCreateInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                PsdPath = psdPath,
                Placement = placement,
                Name = name,
            },
            _psdService.CreateAsync,
            PSD_DEFAULT_TIMEOUT_MS,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_psd_setup",
        Title = "AviUtl2 PSD初期化",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<PsdSetupData>))]
    [Description("PSDToolKit2の初期化objectを検証し、必要な場合だけ安全なlayerへ作成します。")]
    public ValueTask<CallToolResult> SetupAsync(
        Revision expectedRevision,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        int? sceneId = null,
        int? preferredLayer = null,
        int? preferredFrame = null,
        bool createIfMissing = true,
        CancellationToken cancellationToken = default) => ExecuteMutationAsync(
            new PsdSetupInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                SceneId = sceneId,
                PreferredLayer = preferredLayer,
                PreferredFrame = preferredFrame,
                CreateIfMissing = createIfMissing,
            },
            _psdService.SetupAsync,
            PSD_DEFAULT_TIMEOUT_MS,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_psd_set_character",
        Title = "AviUtl2 PSDキャラクターID変更",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<PsdCharacterData>))]
    [Description("対象PSDまたはvoice objectのキャラクターIDを設定し、SDKでround-trip確認します。")]
    public ValueTask<CallToolResult> SetCharacterAsync(
        Revision expectedRevision,
        ObjectLocator locator,
        string characterId,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default) => ExecuteMutationAsync(
            new PsdSetCharacterInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                Locator = locator,
                CharacterId = characterId,
            },
            _psdService.SetCharacterAsync,
            PSD_DEFAULT_TIMEOUT_MS,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_psd_set_layer_state",
        Title = "AviUtl2 PSDレイヤー状態変更",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<PsdLayerStateData>))]
    [Description("canonicalなPSDToolKit2レイヤー状態を設定し、safeguard維持とround-tripを確認します。")]
    public ValueTask<CallToolResult> SetLayerStateAsync(
        Revision expectedRevision,
        ObjectLocator locator,
        string layerState,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default) => ExecuteMutationAsync(
            new PsdSetLayerStateInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                Locator = locator,
                LayerState = layerState,
            },
            _psdService.SetLayerStateAsync,
            PSD_DEFAULT_TIMEOUT_MS,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_psd_create_voice",
        Title = "AviUtl2 PSD音声・字幕作成",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<PsdVoiceData>))]
    [Description("WAV/TXT/LABを検証し、音声・セリフ準備・字幕を作成して個別に再確認します。")]
    public ValueTask<CallToolResult> CreateVoiceAsync(
        Revision expectedRevision,
        string audioPath,
        string characterId,
        Placement placement,
        Guid? instanceId = null,
        int? timeoutMs = null,
        bool dryRun = false,
        string? textPath = null,
        string? labPath = null,
        ObjectLocator? psdLocator = null,
        CancellationToken cancellationToken = default) => ExecuteMutationAsync(
            new PsdCreateVoiceInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                ExpectedRevision = expectedRevision,
                DryRun = dryRun,
                AudioPath = audioPath,
                TextPath = textPath,
                LabPath = labPath,
                CharacterId = characterId,
                PsdLocator = psdLocator,
                Placement = placement,
            },
            _psdService.CreateVoiceAsync,
            PSD_VOICE_DEFAULT_TIMEOUT_MS,
            cancellationToken);

    [McpServerTool(
        Name = "aviutl_psd_validate",
        Title = "AviUtl2 PSD構成検証",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<PsdValidateData>))]
    [Description("setup、character、目パチ、口パク、字幕のPSDToolKit2構成を読取専用で検証します。")]
    public ValueTask<CallToolResult> ValidateAsync(
        Guid? instanceId = null,
        int? timeoutMs = null,
        ObjectLocator? locator = null,
        PsdValidationScope scope = PsdValidationScope.SingleObject,
        IReadOnlyList<PsdValidationCheck>? checks = null,
        CancellationToken cancellationToken = default) => ExecuteValidateAsync(
            new PsdValidateInput
            {
                InstanceId = instanceId,
                TimeoutMs = timeoutMs,
                Locator = locator,
                Scope = scope,
                Checks = checks,
            },
            cancellationToken);

    private async ValueTask<CallToolResult> ExecuteMutationAsync<TInput, TData>(
        TInput input,
        Func<TInput, RequestContext, ValueTask<QueryExecutionResult<TData>>> execute,
        int defaultTimeoutMs,
        CancellationToken cancellationToken)
        where TInput : MutationInput
    {
        RequestContext context;
        try
        {
            context = _requestContextFactory.CreateContext(
                input.InstanceId,
                input.TimeoutMs,
                defaultTimeoutMs,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return CreateInvalidArgument<TData>(defaultTimeoutMs, exception.Message, cancellationToken);
        }
        using (context)
        {
            input = (TInput)(input with { TimeoutMs = context.TimeoutMs });
            try
            {
                RequestValidator.ValidatePsdMutationInput(input);
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

    private async ValueTask<CallToolResult> ExecuteValidateAsync(
        PsdValidateInput input,
        CancellationToken cancellationToken)
    {
        RequestContext context;
        try
        {
            context = _requestContextFactory.CreateContext(
                input.InstanceId,
                input.TimeoutMs,
                PSD_DEFAULT_TIMEOUT_MS,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return CreateInvalidArgument<PsdValidateData>(
                PSD_DEFAULT_TIMEOUT_MS,
                exception.Message,
                cancellationToken);
        }
        using (context)
        {
            input = input with { TimeoutMs = context.TimeoutMs };
            try
            {
                RequestValidator.ValidatePsdValidateInput(input);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                return CreateInvalidArgument<PsdValidateData>(context, exception.Message);
            }
            QueryExecutionResult<PsdValidateData> execution = await _psdService.ValidateAsync(
                input,
                context).ConfigureAwait(false);
            ToolEnvelope<PsdValidateData> envelope = ToolResultFactory.CreateEnvelope(
                execution.Result,
                context,
                execution.InstanceId,
                execution.Revision,
                execution.ViewRevision);
            return McpToolResultFactory.Create(envelope);
        }
    }

    private CallToolResult CreateInvalidArgument<TData>(
        int defaultTimeoutMs,
        string message,
        CancellationToken cancellationToken)
    {
        using RequestContext context = _requestContextFactory.CreateContext(
            null,
            null,
            defaultTimeoutMs,
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

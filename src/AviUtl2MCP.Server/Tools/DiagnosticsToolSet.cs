using System.ComponentModel;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Results;
using AviUtl2MCP.Application.Validation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AviUtl2MCP.Server.Tools;

[McpServerToolType]
public sealed class DiagnosticsToolSet(
    RequestContextFactory requestContextFactory,
    ServerInstanceResolver instanceResolver,
    ServerRuntimeIdentity runtimeIdentity,
    LogQueryService logQueryService,
    DiagnosticsService diagnosticsService)
{
    private const int LOG_DEFAULT_TIMEOUT_MS = 2_000;
    private const int DIAGNOSE_DEFAULT_TIMEOUT_MS = 30_000;
    private readonly RequestContextFactory _requestContextFactory = requestContextFactory;
    private readonly ServerInstanceResolver _instanceResolver = instanceResolver;
    private readonly ServerRuntimeIdentity _runtimeIdentity = runtimeIdentity;
    private readonly LogQueryService _logQueryService = logQueryService;
    private readonly DiagnosticsService _diagnosticsService = diagnosticsService;

    [McpServerTool(
        Name = "aviutl_get_logs",
        Title = "AviUtl2ログ取得",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<LogsData>))]
    [Description("MCP server、Bridge、AviUtl2の構造化ログを安全な上限と署名cursorで取得します。")]
    public async ValueTask<CallToolResult> GetLogsAsync(
        [Description("対象AviUtl2 instance。省略時は利用可能な単一instanceを選択します。")]
        Guid? instanceId = null,
        [Description("timeout（100～120000ミリ秒）。")]
        int? timeoutMs = null,
        [Description("取得するlog source。省略時はserver、bridge、aviutlの全てです。")]
        IReadOnlyList<LogSource>? sources = null,
        [Description("取得するlog level。省略時は全levelです。")]
        IReadOnlyList<ContractLogLevel>? levels = null,
        [Description("このRFC 3339日時以降のlogだけを取得します。")]
        DateTimeOffset? since = null,
        [Description("このcorrelation IDに一致するlogだけを取得します。")]
        Guid? correlationId = null,
        [Description("1 pageの最大行数（1～2000）。")]
        int limit = 100,
        [Description("前の応答で返された署名付きpaging cursor。")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        RequestContext requestContext;
        try
        {
            requestContext = _requestContextFactory.CreateContext(
                instanceId,
                timeoutMs,
                LOG_DEFAULT_TIMEOUT_MS,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return CreateInvalidArgumentResult<LogsData>(
                instanceId,
                LOG_DEFAULT_TIMEOUT_MS,
                exception.Message,
                cancellationToken);
        }
        using (requestContext)
        {
            GetLogsInput requestedInput = new()
            {
                InstanceId = instanceId,
                TimeoutMs = requestContext.TimeoutMs,
                Sources = sources,
                Levels = levels,
                Since = since,
                CorrelationId = correlationId,
                Limit = limit,
                Cursor = cursor,
            };
            try
            {
                ValidateLogsInput(requestedInput);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                return CreateInvalidArgumentResult<LogsData>(
                    requestContext,
                    exception.Message);
            }

            Guid? selectedInstanceId = await _instanceResolver.TryResolveIdAsync(
                instanceId,
                requestContext.CancellationToken).ConfigureAwait(false);
            GetLogsInput input = requestedInput with { InstanceId = selectedInstanceId };
            ApplicationResult<LogsData> result = await _logQueryService.ReadAsync(
                input,
                new LogReadContext(
                    _runtimeIdentity.ServerEpoch,
                    selectedInstanceId,
                    requestContext.CorrelationId,
                    requestContext.Deadline,
                    requestContext.TimeoutMs),
                requestContext.CancellationToken).ConfigureAwait(false);
            ToolEnvelope<LogsData> envelope = ToolResultFactory.CreateEnvelope(
                result,
                requestContext,
                selectedInstanceId);
            return McpToolResultFactory.Create(envelope);
        }
    }

    [McpServerTool(
        Name = "aviutl_diagnose",
        Title = "AviUtl2自動診断",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<DiagnoseData>))]
    [Description("AviUtl2、Bridge、PSDToolKit2、GCMZDrops、既知ログと任意smokeを読取専用で診断します。自動修復は行いません。")]
    public async ValueTask<CallToolResult> DiagnoseAsync(
        [Description("対象AviUtl2 instance。省略時は利用可能な単一instanceを選択します。")]
        Guid? instanceId = null,
        [Description("timeout（100～120000ミリ秒）。preview smokeを含む場合は十分な値を指定します。")]
        int? timeoutMs = null,
        [Description("read-only project query smokeを実行するか。")]
        bool includeReadSmoke = false,
        [Description("preview PNG smokeを実行するか。")]
        bool includePreviewSmoke = false,
        [Description("各log sourceから診断へ渡す最大行数（1～2000）。")]
        int maxLogLines = 100,
        CancellationToken cancellationToken = default)
    {
        RequestContext requestContext;
        try
        {
            requestContext = _requestContextFactory.CreateContext(
                instanceId,
                timeoutMs,
                DIAGNOSE_DEFAULT_TIMEOUT_MS,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return CreateInvalidArgumentResult<DiagnoseData>(
                instanceId,
                DIAGNOSE_DEFAULT_TIMEOUT_MS,
                exception.Message,
                cancellationToken);
        }
        using (requestContext)
        {
            DiagnoseInput requestedInput = new()
            {
                InstanceId = instanceId,
                TimeoutMs = requestContext.TimeoutMs,
                IncludeReadSmoke = includeReadSmoke,
                IncludePreviewSmoke = includePreviewSmoke,
                MaxLogLines = maxLogLines,
            };
            try
            {
                ValidateDiagnoseInput(requestedInput);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                return CreateInvalidArgumentResult<DiagnoseData>(
                    requestContext,
                    exception.Message);
            }

            ApplicationResult<InstanceDescriptor> selection = await _instanceResolver.ResolveAsync(
                instanceId,
                requestContext.CancellationToken).ConfigureAwait(false);
            if (!selection.IsSuccess)
            {
                ToolEnvelope<DiagnoseData> failedEnvelope = ToolResultFactory.CreateEnvelope(
                    ApplicationResult.Failure<DiagnoseData>(selection.Error!),
                    requestContext);
                return McpToolResultFactory.Create(failedEnvelope);
            }

            InstanceDescriptor instance = selection.Value!;
            DiagnoseInput input = requestedInput with { InstanceId = instance.InstanceId };
            ApplicationResult<DiagnoseData> result = await _diagnosticsService.RunAsync(
                input,
                new DiagnosticRunContext(
                    _runtimeIdentity.ServerEpoch,
                    instance,
                    requestContext.CorrelationId,
                    requestContext.Deadline,
                    requestContext.TimeoutMs),
                requestContext.CancellationToken).ConfigureAwait(false);
            ToolEnvelope<DiagnoseData> envelope = ToolResultFactory.CreateEnvelope(
                result,
                requestContext,
                instance.InstanceId);
            return McpToolResultFactory.Create(envelope);
        }
    }

    private static void ValidateLogsInput(GetLogsInput input)
    {
        RequestValidator.ValidateCommonInput(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.Limit, 2000);
        if (input.Cursor is not null)
        {
            RequestValidator.ValidateString(input.Cursor, nameof(input.Cursor), 4096, 4096);
        }
        if (input.Sources is { Count: 0 }
            || input.Sources?.Distinct().Count() != input.Sources?.Count)
        {
            throw new ArgumentException("Log sources must be non-empty and unique.", nameof(input));
        }
        if (input.Levels is { Count: 0 }
            || input.Levels?.Distinct().Count() != input.Levels?.Count)
        {
            throw new ArgumentException("Log levels must be non-empty and unique.", nameof(input));
        }
    }

    private static void ValidateDiagnoseInput(DiagnoseInput input)
    {
        RequestValidator.ValidateCommonInput(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.MaxLogLines, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.MaxLogLines, 2000);
    }

    private CallToolResult CreateInvalidArgumentResult<TData>(
        Guid? instanceId,
        int defaultTimeoutMs,
        string message,
        CancellationToken cancellationToken)
    {
        using RequestContext errorContext = _requestContextFactory.CreateContext(
            instanceId,
            timeoutMs: null,
            defaultTimeoutMs,
            cancellationToken);
        return CreateInvalidArgumentResult<TData>(errorContext, message);
    }

    private static CallToolResult CreateInvalidArgumentResult<TData>(
        RequestContext requestContext,
        string message)
    {
        ToolEnvelope<TData> envelope = ToolResultFactory.CreateEnvelope(
            ApplicationResult.Failure<TData>(ApplicationErrors.CreateInvalidArgument(message)),
            requestContext);
        return McpToolResultFactory.Create(envelope);
    }
}

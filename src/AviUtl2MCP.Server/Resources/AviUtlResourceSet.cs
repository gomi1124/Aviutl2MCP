using System.ComponentModel;
using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Results;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.Server.Diagnostics;
using ModelContextProtocol.Server;

namespace AviUtl2MCP.Server.Resources;

[McpServerResourceType]
public sealed class AviUtlResourceSet(
    RequestContextFactory requestContextFactory,
    AviUtlQueryService queryService,
    LatestDiagnosticsStore latestDiagnosticsStore)
{
    private readonly RequestContextFactory _requestContextFactory = requestContextFactory;
    private readonly AviUtlQueryService _queryService = queryService;
    private readonly LatestDiagnosticsStore _latestDiagnosticsStore = latestDiagnosticsStore;

    [McpServerResource(
        Name = "aviutl_status",
        Title = "AviUtl2 status",
        UriTemplate = "aviutl://status",
        MimeType = "application/json")]
    [Description("AviUtl2の接続・component・project・編集状態です。未接続も構造化状態を返します。")]
    public ValueTask<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteQueryAsync(
            2_000,
            (context) => _queryService.GetStatusAsync(
                new GetStatusInput { TimeoutMs = context.TimeoutMs },
                context),
            cancellationToken);
    }

    [McpServerResource(
        Name = "aviutl_capabilities",
        Title = "AviUtl2 capabilities",
        UriTemplate = "aviutl://capabilities",
        MimeType = "application/json")]
    [Description("AviUtl2MCPの操作能力、version、制限値です。")]
    public ValueTask<string> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteQueryAsync(
            2_000,
            context => _queryService.GetCapabilitiesAsync(
                new GetCapabilitiesInput { TimeoutMs = context.TimeoutMs },
                context),
            cancellationToken);
    }

    [McpServerResource(
        Name = "aviutl_project_current",
        Title = "AviUtl2 current project",
        UriTemplate = "aviutl://project/current",
        MimeType = "application/json")]
    [Description("現在のAviUtl2 project概要です。")]
    public ValueTask<string> GetCurrentProjectAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteQueryAsync(
            5_000,
            context => _queryService.GetProjectAsync(
                new GetProjectInput { TimeoutMs = context.TimeoutMs, IncludeScenes = true },
                context),
            cancellationToken);
    }

    [McpServerResource(
        Name = "aviutl_timeline_current",
        Title = "AviUtl2 current timeline",
        UriTemplate = "aviutl://timeline/current",
        MimeType = "application/json")]
    [Description("現在表示中のtimeline概要を最大100件返します。")]
    public ValueTask<string> GetCurrentTimelineAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteQueryAsync(
            5_000,
            context => _queryService.GetTimelineAsync(
                new GetTimelineInput
                {
                    TimeoutMs = context.TimeoutMs,
                    Detail = TimelineDetail.Summary,
                    Limit = 100,
                },
                context),
            cancellationToken);
    }

    [McpServerResource(
        Name = "aviutl_diagnostics_latest",
        Title = "AviUtl2 latest diagnostics",
        UriTemplate = "aviutl://diagnostics/latest",
        MimeType = "application/json")]
    [Description("最後に完了した自動診断結果です。未実行時はdataがnullです。")]
    public string GetLatestDiagnostics()
    {
        ToolEnvelope<DiagnoseData>? latest = _latestDiagnosticsStore.GetLatest();
        if (latest is not null)
        {
            return ContractJsonSerializer.SerializeContract(latest);
        }
        using RequestContext context = _requestContextFactory.CreateContext(
            requestedInstanceId: null,
            timeoutMs: null,
            defaultTimeoutMs: 2_000,
            CancellationToken.None);
        ToolEnvelope<DiagnoseData> empty = new(true, context.CorrelationId, [])
        {
            Data = null,
        };
        JsonObject payload = JsonNode.Parse(
            ContractJsonSerializer.SerializeContract(empty))!.AsObject();
        payload.Add("data", null);
        return payload.ToJsonString(ContractJsonSerializer.CreateSerializerOptions());
    }

    private async ValueTask<string> ExecuteQueryAsync<TData>(
        int defaultTimeoutMs,
        Func<RequestContext, ValueTask<QueryExecutionResult<TData>>> execute,
        CancellationToken cancellationToken)
    {
        using RequestContext context = _requestContextFactory.CreateContext(
            requestedInstanceId: null,
            timeoutMs: null,
            defaultTimeoutMs,
            cancellationToken);
        QueryExecutionResult<TData> execution = await execute(context).ConfigureAwait(false);
        ToolEnvelope<TData> envelope = ToolResultFactory.CreateEnvelope(
            execution.Result,
            context,
            execution.InstanceId,
            execution.Revision,
            execution.ViewRevision);
        return ContractJsonSerializer.SerializeContract(envelope);
    }
}

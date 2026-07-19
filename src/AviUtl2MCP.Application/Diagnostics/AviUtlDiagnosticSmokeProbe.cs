using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Previews;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed class AviUtlDiagnosticSmokeProbe(
    AviUtlQueryService queryService,
    AviUtlPreviewService previewService) : IDiagnosticSmokeProbe
{
    private readonly AviUtlQueryService _queryService = queryService
        ?? throw new ArgumentNullException(nameof(queryService));
    private readonly AviUtlPreviewService _previewService = previewService
        ?? throw new ArgumentNullException(nameof(previewService));

    public async ValueTask<DiagnosticSmokeResult> RunReadSmokeAsync(
        DiagnosticRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using RequestContext requestContext = CreateRequestContext(context, cancellationToken);
        QueryExecutionResult<ProjectData> execution = await _queryService.GetProjectAsync(
            new GetProjectInput
            {
                InstanceId = context.Instance.InstanceId,
                TimeoutMs = context.TimeoutMs,
                IncludeScenes = true,
            },
            requestContext).ConfigureAwait(false);
        string[] evidence = CreateReadEvidence(context, execution);
        if (!execution.Result.IsSuccess)
        {
            return DiagnosticSmokeResult.Failed(
                execution.Result.Error!.Code,
                execution.Result.Error.Message,
                execution.Result.Error.CanRetry,
                evidence);
        }
        return DiagnosticSmokeResult.Success(evidence);
    }

    public async ValueTask<DiagnosticSmokeResult> RunPreviewSmokeAsync(
        DiagnosticRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using RequestContext requestContext = CreateRequestContext(context, cancellationToken);
        PreviewExecutionResult execution = await _previewService.RenderPreviewAsync(
            new RenderPreviewInput
            {
                InstanceId = context.Instance.InstanceId,
                TimeoutMs = context.TimeoutMs,
                Frame = 1,
                MaxWidth = 640,
                MaxHeight = 360,
                IncludeAlpha = false,
            },
            requestContext).ConfigureAwait(false);
        List<string> evidence =
        [
            $"correlationId={context.CorrelationId:D}",
            $"revision={execution.Revision?.Value ?? "none"}",
        ];
        if (!execution.Result.IsSuccess)
        {
            return DiagnosticSmokeResult.Failed(
                execution.Result.Error!.Code,
                execution.Result.Error.Message,
                execution.Result.Error.CanRetry,
                evidence.ToArray());
        }

        PreviewData data = execution.Result.Value!;
        evidence.Add($"previewSha256={data.Sha256}");
        evidence.Add($"frame={data.Frame}");
        evidence.Add($"dimensions={data.Width}x{data.Height}");
        evidence.Add($"byteLength={data.ByteLength}");
        return DiagnosticSmokeResult.Success(evidence.ToArray());
    }

    private static string[] CreateReadEvidence(
        DiagnosticRunContext context,
        QueryExecutionResult<ProjectData> execution)
    {
        List<string> evidence =
        [
            $"correlationId={context.CorrelationId:D}",
            $"revision={execution.Revision?.Value ?? "none"}",
        ];
        if (execution.Result.IsSuccess)
        {
            ProjectData project = execution.Result.Value!;
            evidence.Add($"sceneId={project.CurrentSceneId}");
            evidence.Add($"frame={project.CurrentFrame}");
            evidence.Add($"dimensions={project.Width}x{project.Height}");
        }
        return evidence.ToArray();
    }

    private static RequestContext CreateRequestContext(
        DiagnosticRunContext context,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource cancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan remaining = context.Deadline - TimeProvider.System.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            cancellationSource.Cancel();
        }
        else
        {
            cancellationSource.CancelAfter(remaining);
        }
        return new RequestContext(
            context.CorrelationId,
            context.Instance.InstanceId,
            context.Deadline,
            context.TimeoutMs,
            cancellationSource);
    }
}

using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Validation;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed class DiagnosticContextFactory
{
    private static readonly LogSource[] LOG_SOURCES =
    [
        LogSource.Server,
        LogSource.Bridge,
        LogSource.Aviutl,
    ];
    private readonly IBridgeDiagnosticsGateway _diagnosticsGateway;
    private readonly LogQueryService _logQueryService;
    private readonly IDiagnosticSmokeProbe _smokeProbe;
    private readonly TimeProvider _timeProvider;

    public DiagnosticContextFactory(
        IBridgeDiagnosticsGateway diagnosticsGateway,
        LogQueryService logQueryService,
        IDiagnosticSmokeProbe? smokeProbe = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsGateway);
        ArgumentNullException.ThrowIfNull(logQueryService);
        _diagnosticsGateway = diagnosticsGateway;
        _logQueryService = logQueryService;
        _smokeProbe = smokeProbe ?? UnavailableDiagnosticSmokeProbe.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ApplicationResult<DiagnosticContext>> CreateAsync(
        DiagnoseInput input,
        DiagnosticRunContext runContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(runContext);
        try
        {
            ValidateInput(input, runContext);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return ApplicationResult.Failure<DiagnosticContext>(
                ApplicationErrors.CreateInvalidArgument(exception.Message));
        }

        FetchResult<StatusData> status = await FetchGatewayAsync(
            request => _diagnosticsGateway.GetStatusAsync(
                new GatewayRequest<GetStatusInput>(
                    runContext.Instance.InstanceId,
                    request,
                    runContext.Deadline,
                    runContext.TimeoutMs,
                    ExpectedRevision: null,
                    DryRun: false,
                    new GetStatusInput
                    {
                        InstanceId = runContext.Instance.InstanceId,
                        TimeoutMs = runContext.TimeoutMs,
                    }),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        FetchResult<CapabilitiesData> capabilities = await FetchGatewayAsync(
            request => _diagnosticsGateway.GetCapabilitiesAsync(
                new GatewayRequest<GetCapabilitiesInput>(
                    runContext.Instance.InstanceId,
                    request,
                    runContext.Deadline,
                    runContext.TimeoutMs,
                    ExpectedRevision: null,
                    DryRun: false,
                    new GetCapabilitiesInput
                    {
                        InstanceId = runContext.Instance.InstanceId,
                        TimeoutMs = runContext.TimeoutMs,
                    }),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        LogFetchResult logs = await ReadLogsAsync(
            input.MaxLogLines,
            runContext,
            cancellationToken).ConfigureAwait(false);
        DiagnosticSmokeResult readSmoke = input.IncludeReadSmoke
            ? await RunSmokeAsync(
                context => _smokeProbe.RunReadSmokeAsync(context, cancellationToken),
                runContext,
                "read_smoke_failed",
                cancellationToken).ConfigureAwait(false)
            : DiagnosticSmokeResult.NotRequested();
        DiagnosticSmokeResult previewSmoke = input.IncludePreviewSmoke
            ? await RunSmokeAsync(
                context => _smokeProbe.RunPreviewSmokeAsync(context, cancellationToken),
                runContext,
                "preview_smoke_failed",
                cancellationToken).ConfigureAwait(false)
            : DiagnosticSmokeResult.NotRequested();

        IReadOnlyList<KnownLogMatch> knownMatches = KnownLogClassifier.Classify(logs.Entries);
        DiagnosticContext context = new(
            runContext.Instance,
            status.Value,
            status.Failure,
            capabilities.Value,
            capabilities.Failure,
            logs.Entries,
            logs.Warnings,
            logs.Failure,
            knownMatches,
            readSmoke,
            previewSmoke);
        return ApplicationResult.Success(context);
    }

    private async ValueTask<LogFetchResult> ReadLogsAsync(
        int maximumLines,
        DiagnosticRunContext runContext,
        CancellationToken cancellationToken)
    {
        List<LogEntry> entries = [];
        List<ToolWarning> warnings = [];
        DiagnosticProbeFailure? firstFailure = null;
        int successCount = 0;
        foreach (LogSource source in LOG_SOURCES)
        {
            ApplicationResult<LogsData> result = await _logQueryService.ReadAsync(
                new GetLogsInput
                {
                    InstanceId = runContext.Instance.InstanceId,
                    TimeoutMs = runContext.TimeoutMs,
                    Sources = [source],
                    Since = runContext.Instance.ProcessCreationTime,
                    Limit = maximumLines,
                },
                new LogReadContext(
                    runContext.ServerEpoch,
                    runContext.Instance.InstanceId,
                    CreateOperationCorrelationId(),
                    runContext.Deadline,
                    runContext.TimeoutMs),
                cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                ++successCount;
                entries.AddRange(result.Value!.Entries);
                warnings.AddRange(result.Warnings);
                continue;
            }

            ApplicationError error = result.Error!;
            firstFailure ??= new DiagnosticProbeFailure(error.Code, error.Message, error.CanRetry);
            warnings.Add(new ToolWarning(
                "log_source_unavailable",
                $"The {source} log source could not be read: {error.Message}",
                new Dictionary<string, JsonNode?>
                {
                    ["source"] = JsonValue.Create(source.ToString()),
                    ["code"] = JsonValue.Create(error.Code),
                }));
        }

        LogEntry[] orderedEntries = entries
            .OrderBy(entry => entry.Timestamp)
            .ToArray();
        return new LogFetchResult(
            orderedEntries,
            warnings,
            successCount == 0 ? firstFailure : null);
    }

    private async ValueTask<FetchResult<TData>> FetchGatewayAsync<TData>(
        Func<Guid, ValueTask<GatewayResponse<TData>>> fetch,
        CancellationToken cancellationToken)
    {
        try
        {
            GatewayResponse<TData> response = await fetch(CreateOperationCorrelationId()).ConfigureAwait(false);
            if (!response.Ok)
            {
                GatewayError? error = response.Error;
                return new FetchResult<TData>(
                    default,
                    error is null
                        ? new DiagnosticProbeFailure(
                            "bridge_response_invalid",
                            "A failed bridge response omitted its error.",
                            false)
                        : new DiagnosticProbeFailure(error.Code, error.Message, error.CanRetry));
            }
            return response.Data is null
                ? new FetchResult<TData>(
                    default,
                    new DiagnosticProbeFailure(
                        "bridge_response_invalid",
                        "A successful bridge response omitted its data.",
                        false))
                : new FetchResult<TData>(response.Data, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            return new FetchResult<TData>(
                default,
                new DiagnosticProbeFailure(
                    "aviutl_not_running",
                    "The selected AviUtl2 instance is no longer available.",
                    true));
        }
        catch (TimeoutException)
        {
            return new FetchResult<TData>(
                default,
                new DiagnosticProbeFailure(
                    "operation_timeout",
                    "The bridge diagnostic request timed out.",
                    true));
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            return new FetchResult<TData>(
                default,
                new DiagnosticProbeFailure(
                    "bridge_unavailable",
                    "The bridge diagnostic request failed.",
                    true));
        }
    }

    private async ValueTask<DiagnosticSmokeResult> RunSmokeAsync(
        Func<DiagnosticRunContext, ValueTask<DiagnosticSmokeResult>> run,
        DiagnosticRunContext runContext,
        string fallbackCode,
        CancellationToken cancellationToken)
    {
        try
        {
            DiagnosticRunContext operationContext = runContext with
            {
                CorrelationId = CreateOperationCorrelationId(),
            };
            return await run(operationContext).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or InvalidOperationException
            or TimeoutException
            or ObjectDisposedException)
        {
            return DiagnosticSmokeResult.Failed(
                fallbackCode,
                $"The smoke probe failed with {exception.GetType().Name}.",
                canRetry: true);
        }
    }

    private Guid CreateOperationCorrelationId() =>
        Guid.CreateVersion7(_timeProvider.GetUtcNow());

    private static void ValidateInput(DiagnoseInput input, DiagnosticRunContext runContext)
    {
        RequestValidator.ValidateCommonInput(input);
        ArgumentNullException.ThrowIfNull(runContext.Instance);
        ArgumentOutOfRangeException.ThrowIfEqual(runContext.ServerEpoch, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(runContext.Instance.InstanceId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(runContext.Instance.ProcessId, 1);
        ArgumentOutOfRangeException.ThrowIfEqual(runContext.CorrelationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(runContext.TimeoutMs, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(runContext.TimeoutMs, 120_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.MaxLogLines, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.MaxLogLines, 2000);
        if (input.InstanceId.HasValue && input.InstanceId != runContext.Instance.InstanceId)
        {
            throw new ArgumentException("Diagnose input instance ID does not match the selected instance.", nameof(input));
        }
    }

    private sealed record FetchResult<TData>(TData? Value, DiagnosticProbeFailure? Failure);

    private sealed record LogFetchResult(
        IReadOnlyList<LogEntry> Entries,
        IReadOnlyList<ToolWarning> Warnings,
        DiagnosticProbeFailure? Failure);
}

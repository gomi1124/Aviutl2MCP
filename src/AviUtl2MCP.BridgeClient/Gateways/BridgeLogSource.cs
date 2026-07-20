using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Application.Gateways;

namespace AviUtl2MCP.BridgeClient.Gateways;

public sealed class BridgeLogSource(IBridgeDiagnosticsGateway diagnosticsGateway) : ILogSource
{
    private static readonly LogSource[] BRIDGE_SOURCE = [LogSource.Bridge];
    private readonly IBridgeDiagnosticsGateway _diagnosticsGateway = diagnosticsGateway;

    public LogSource Source => LogSource.Bridge;

    public async ValueTask<LogSourcePage> ReadAsync(
        LogSourceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.InstanceId.HasValue || query.InstanceId == Guid.Empty)
        {
            throw new LogSourceReadException(
                "aviutl_not_running",
                "A bridge log source requires a selected AviUtl2 instance.",
                canRetry: true);
        }

        GetLogsInput input = new()
        {
            InstanceId = query.InstanceId,
            TimeoutMs = query.TimeoutMs,
            Sources = BRIDGE_SOURCE,
            Levels = query.Levels,
            Since = query.Since,
            CorrelationId = query.CorrelationId,
            Limit = query.Limit,
            Cursor = query.Cursor,
        };
        GatewayResponse<LogsData> response;
        try
        {
            response = await _diagnosticsGateway.GetLogsAsync(
                new GatewayRequest<GetLogsInput>(
                    query.InstanceId.Value,
                    query.RequestCorrelationId,
                    query.Deadline,
                    query.TimeoutMs,
                    ExpectedRevision: null,
                    DryRun: false,
                    input),
                cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            throw new LogSourceReadException(
                "aviutl_not_running",
                "The selected AviUtl2 instance is no longer available.",
                canRetry: true,
                exception);
        }
        catch (Exception exception) when (exception is IOException
            or TimeoutException
            or ObjectDisposedException)
        {
            throw new LogSourceReadException(
                "bridge_unavailable",
                "The bridge log request failed.",
                canRetry: true,
                exception);
        }

        if (!response.Ok)
        {
            GatewayError error = response.Error
                ?? throw new InvalidDataException("Failed bridge log response omitted its error.");
            throw new LogSourceReadException(error.Code, error.Message, error.CanRetry);
        }
        LogsData data = response.Data
            ?? throw new InvalidDataException("Successful bridge log response omitted its data.");
        if (data.IsTruncated && string.IsNullOrWhiteSpace(data.NextCursor))
        {
            throw new InvalidDataException("Truncated bridge log response omitted its cursor.");
        }

        string generation = response.Revision.HasValue
            ? ExtractServerEpoch(response.Revision.Value.Value)
            : response.InstanceId.ToString("D");
        return new LogSourcePage(
            data.Entries,
            data.NextCursor,
            data.IsTruncated,
            generation);
    }

    private static string ExtractServerEpoch(string revision)
    {
        int separator = revision.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 ? revision[..separator] : revision;
    }
}

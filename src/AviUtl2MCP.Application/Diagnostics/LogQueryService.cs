using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.Application.Validation;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed record LogReadContext(
    Guid ServerEpoch,
    Guid? InstanceId,
    Guid RequestCorrelationId,
    DateTimeOffset Deadline,
    int TimeoutMs);

public sealed class LogQueryService
{
    private static readonly LogSource[] DEFAULT_SOURCE_ORDER =
    [
        LogSource.Server,
        LogSource.Bridge,
        LogSource.Aviutl,
    ];
    private static readonly TimeSpan CURSOR_LIFETIME = TimeSpan.FromMinutes(15);
    private readonly Dictionary<LogSource, ILogSource> _sources;
    private readonly LogCursorCodec _cursorCodec;
    private readonly TimeProvider _timeProvider;

    public LogQueryService(
        IEnumerable<ILogSource> sources,
        LogCursorCodec cursorCodec,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(cursorCodec);
        ILogSource[] materialized = sources.ToArray();
        if (materialized.Length == 0 || materialized.Select(source => source.Source).Distinct().Count() != materialized.Length)
        {
            throw new ArgumentException("Log sources must be non-empty and unique by source kind.", nameof(sources));
        }
        _sources = materialized.ToDictionary(source => source.Source);
        _cursorCodec = cursorCodec;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ApplicationResult<LogsData>> ReadAsync(
        GetLogsInput input,
        LogReadContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            ValidateInput(input, context);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return ApplicationResult.Failure<LogsData>(
                ApplicationErrors.CreateInvalidArgument(exception.Message));
        }

        LogSource[] selectedSources = SelectSources(input.Sources);
        string queryHash = CalculateQueryHash(input, selectedSources, context.InstanceId);
        LogCursorBinding binding = new(context.ServerEpoch, queryHash);
        int sourceIndex = 0;
        string? sourceCursor = null;
        string? expectedGeneration = null;
        if (input.Cursor is not null)
        {
            ApplicationResult<LogCursorState> decoded = _cursorCodec.DecodeCursor(input.Cursor, binding);
            if (!decoded.IsSuccess)
            {
                return ApplicationResult.Failure<LogsData>(decoded.Error!);
            }
            LogCursorState state = decoded.Value!;
            if (state.SourceIndex < 0 || state.SourceIndex >= selectedSources.Length)
            {
                return ApplicationResult.Failure<LogsData>(ApplicationErrors.CreateCursorInvalid("sourceIndex"));
            }
            sourceIndex = state.SourceIndex;
            sourceCursor = state.SourceCursor;
            expectedGeneration = state.SourceGeneration;
        }

        List<LogEntry> entries = [];
        List<ToolWarning> warnings = [];
        LogSourceReadException? firstSourceError = null;
        bool hasSuccessfulSource = false;
        for (int index = sourceIndex; index < selectedSources.Length; ++index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogSource sourceKind = selectedSources[index];
            if (!_sources.TryGetValue(sourceKind, out ILogSource? source))
            {
                return ApplicationResult.Failure<LogsData>(ApplicationErrors.CreateError(
                    "internal_error",
                    $"The {sourceKind} log source is not registered."));
            }

            int remaining = input.Limit - entries.Count;
            LogSourcePage page;
            try
            {
                page = await source.ReadAsync(
                    new LogSourceQuery(
                        input.Levels,
                        input.Since,
                        input.CorrelationId,
                        remaining,
                        index == sourceIndex ? sourceCursor : null,
                        context.InstanceId,
                        context.RequestCorrelationId,
                        context.Deadline,
                        context.TimeoutMs),
                    cancellationToken).ConfigureAwait(false);
                hasSuccessfulSource = true;
            }
            catch (LogSourceReadException exception)
            {
                firstSourceError ??= exception;
                warnings.Add(CreateSourceWarning(sourceKind, exception));
                expectedGeneration = null;
                sourceCursor = null;
                continue;
            }

            if (index == sourceIndex
                && expectedGeneration is not null
                && !string.Equals(expectedGeneration, page.Generation, StringComparison.Ordinal))
            {
                return ApplicationResult.Failure<LogsData>(ApplicationErrors.CreateCursorInvalid("generation"));
            }
            if (page.IsTruncated && string.IsNullOrWhiteSpace(page.NextCursor))
            {
                return ApplicationResult.Failure<LogsData>(ApplicationErrors.CreateError(
                    "internal_error",
                    $"The {sourceKind} log source returned a truncated page without a cursor."));
            }

            entries.AddRange(page.Entries);
            if (page.IsTruncated)
            {
                string nextCursor = CreateCursor(
                    binding,
                    index,
                    page.NextCursor,
                    page.Generation);
                return ApplicationResult.Success(
                    new LogsData(entries, nextCursor, true),
                    warnings);
            }
            if (entries.Count == input.Limit && index + 1 < selectedSources.Length)
            {
                string nextCursor = CreateCursor(binding, index + 1, null, null);
                return ApplicationResult.Success(
                    new LogsData(entries, nextCursor, true),
                    warnings);
            }
            expectedGeneration = null;
            sourceCursor = null;
        }

        if (!hasSuccessfulSource && firstSourceError is not null)
        {
            return ApplicationResult.Failure<LogsData>(ApplicationErrors.CreateError(
                firstSourceError.Code,
                firstSourceError.Message,
                firstSourceError.CanRetry));
        }
        return ApplicationResult.Success(new LogsData(entries, null, false), warnings);
    }

    private static void ValidateInput(GetLogsInput input, LogReadContext context)
    {
        RequestValidator.ValidateCommonInput(input);
        ArgumentOutOfRangeException.ThrowIfEqual(context.ServerEpoch, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(context.RequestCorrelationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(context.TimeoutMs, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(context.TimeoutMs, 120_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.Limit, 2000);
        if (input.Cursor is not null)
        {
            RequestValidator.ValidateString(input.Cursor, nameof(input.Cursor), 4096, 4096);
        }
        if (input.Sources is { Count: 0 } || input.Sources?.Distinct().Count() != input.Sources?.Count)
        {
            throw new ArgumentException("Log sources must be non-empty and unique.", nameof(input));
        }
        if (input.Levels is { Count: 0 } || input.Levels?.Distinct().Count() != input.Levels?.Count)
        {
            throw new ArgumentException("Log levels must be non-empty and unique.", nameof(input));
        }
    }

    private static LogSource[] SelectSources(IReadOnlyList<LogSource>? requestedSources)
    {
        if (requestedSources is null)
        {
            return DEFAULT_SOURCE_ORDER;
        }
        return requestedSources.OrderBy(source => Array.IndexOf(DEFAULT_SOURCE_ORDER, source)).ToArray();
    }

    private static string CalculateQueryHash(
        GetLogsInput input,
        IReadOnlyList<LogSource> selectedSources,
        Guid? instanceId)
    {
        string normalized = ContractJsonSerializer.SerializeContract(new LogQueryBindingData(
            selectedSources,
            input.Levels?.OrderBy(level => level).ToArray(),
            input.Since,
            input.CorrelationId,
            input.Limit,
            instanceId));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private string CreateCursor(
        LogCursorBinding binding,
        int sourceIndex,
        string? sourceCursor,
        string? sourceGeneration)
    {
        return _cursorCodec.EncodeCursor(new LogCursorState(
            binding.ServerEpoch,
            binding.QueryHash,
            sourceIndex,
            sourceCursor,
            sourceGeneration,
            _timeProvider.GetUtcNow().Add(CURSOR_LIFETIME)));
    }

    private static ToolWarning CreateSourceWarning(
        LogSource source,
        LogSourceReadException exception) =>
        new(
            "log_source_unavailable",
            $"The {source} log source could not be read: {exception.Message}",
            new Dictionary<string, JsonNode?>
            {
                ["source"] = JsonValue.Create(source.ToString()),
                ["code"] = JsonValue.Create(exception.Code),
            });

    private sealed record LogQueryBindingData(
        IReadOnlyList<LogSource> Sources,
        IReadOnlyList<ContractLogLevel>? Levels,
        DateTimeOffset? Since,
        Guid? CorrelationId,
        int Limit,
        Guid? InstanceId);
}

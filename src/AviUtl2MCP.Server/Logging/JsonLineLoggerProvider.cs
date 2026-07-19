using Microsoft.Extensions.Logging;

namespace AviUtl2MCP.Server.Logging;

public sealed class JsonLineLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly JsonLineLogSink _sink;
    private readonly TimeProvider _timeProvider;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public JsonLineLoggerProvider(
        string logFilePath,
        TextWriter standardError,
        TimeProvider? timeProvider = null)
    {
        _sink = new JsonLineLogSink(logFilePath, standardError);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static JsonLineLoggerProvider CreateDefault()
    {
        return new JsonLineLoggerProvider(GetDefaultLogFilePath(), Console.Error);
    }

    public static string GetDefaultLogFilePath()
    {
        string? configuredDirectory = Environment.GetEnvironmentVariable("AVIUTL2_MCP_LOG_DIRECTORY");
        string logDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AviUtl2MCP",
                "logs")
            : Path.GetFullPath(configuredDirectory);
        return Path.Combine(logDirectory, $"server-{Environment.ProcessId}.jsonl");
    }

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return new JsonLineLogger(categoryName, _sink, _timeProvider, () => _scopeProvider);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);
        _scopeProvider = scopeProvider;
    }

    public void Dispose() => _sink.Dispose();

    private sealed class JsonLineLogger(
        string component,
        JsonLineLogSink sink,
        TimeProvider timeProvider,
        Func<IExternalScopeProvider> getScopeProvider) : ILogger
    {
        private readonly string _component = component;
        private readonly JsonLineLogSink _sink = sink;
        private readonly TimeProvider _timeProvider = timeProvider;
        private readonly Func<IExternalScopeProvider> _getScopeProvider = getScopeProvider;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            _getScopeProvider().Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            Dictionary<string, string?> properties = new(StringComparer.Ordinal);
            LogPropertyCollector collector = new(properties);
            _getScopeProvider().ForEachScope(
                (scope, propertyCollector) => CollectProperties(scope, propertyCollector),
                collector);
            CollectProperties(state, collector);

            JsonLineLogEntry entry = new(
                _timeProvider.GetUtcNow(),
                logLevel.ToString(),
                _component,
                eventId.Id,
                eventId.Name,
                collector.CorrelationId,
                collector.InstanceId,
                collector.Operation,
                collector.DurationMs,
                collector.ResultCode,
                LogSecretMasker.MaskText(formatter(state, exception)) ?? string.Empty,
                properties,
                LogSecretMasker.MaskText(exception?.ToString()));
            _sink.Write(entry);
        }

        private static void CollectProperties(
            object? state,
            LogPropertyCollector collector)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values)
            {
                return;
            }

            foreach (KeyValuePair<string, object?> value in values)
            {
                if (string.Equals(value.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(value.Key, "correlationId", StringComparison.OrdinalIgnoreCase))
                {
                    collector.CorrelationId = value.Value?.ToString();
                    continue;
                }
                if (string.Equals(value.Key, "instanceId", StringComparison.OrdinalIgnoreCase))
                {
                    collector.InstanceId = value.Value?.ToString();
                    continue;
                }
                if (string.Equals(value.Key, "operation", StringComparison.OrdinalIgnoreCase))
                {
                    collector.Operation = value.Value?.ToString();
                    continue;
                }
                if (string.Equals(value.Key, "durationMs", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(
                        Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double durationMs))
                {
                    collector.DurationMs = durationMs;
                    continue;
                }
                if (string.Equals(value.Key, "resultCode", StringComparison.OrdinalIgnoreCase))
                {
                    collector.ResultCode = value.Value?.ToString();
                    continue;
                }

                collector.Properties[value.Key] = LogSecretMasker.MaskValue(value.Key, value.Value);
            }
        }

        private sealed class LogPropertyCollector(IDictionary<string, string?> properties)
        {
            public IDictionary<string, string?> Properties { get; } = properties;

            public string? CorrelationId { get; set; }

            public string? InstanceId { get; set; }

            public string? Operation { get; set; }

            public double? DurationMs { get; set; }

            public string? ResultCode { get; set; }
        }
    }
}

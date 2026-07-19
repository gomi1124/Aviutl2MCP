using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Server.Logging;

namespace AviUtl2MCP.Server.Diagnostics;

public sealed partial class AviUtlLogSource(string logDirectory) : ILogSource
{
    private const int MAXIMUM_FILES = 8;
    private const long MAXIMUM_BYTES_PER_FILE = 16L * 1024L * 1024L;
    private readonly string _logDirectory = Path.GetFullPath(logDirectory);

    public LogSource Source => LogSource.Aviutl;

    public async ValueTask<LogSourcePage> ReadAsync(
        LogSourceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        FileInfo[] files = Directory.Exists(_logDirectory)
            ? new DirectoryInfo(_logDirectory)
                .EnumerateFiles("aviutl2_*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MAXIMUM_FILES)
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToArray()
            : [];
        List<LogEntry> entries = [];
        foreach (FileInfo file in files)
        {
            IReadOnlyList<string> lines = await BoundedLogFileReader.ReadLinesAsync(
                file.FullName,
                MAXIMUM_BYTES_PER_FILE,
                cancellationToken).ConfigureAwait(false);
            entries.AddRange(ParseEntries(lines, ResolveLogYear(file)));
        }
        return LogSourceFilter.CreatePage(entries, query, CreateGeneration(files));
    }

    private static LogEntry[] ParseEntries(IReadOnlyList<string> lines, int year)
    {
        List<MutableEntry> parsed = [];
        foreach (string line in lines)
        {
            Match match = LogHeaderRegex().Match(line);
            if (!match.Success)
            {
                if (parsed.Count > 0 && !string.IsNullOrWhiteSpace(line))
                {
                    parsed[^1].Append(line);
                }
                continue;
            }

            if (!TryParseTimestamp(year, match.Groups["timestamp"].Value, out DateTimeOffset timestamp))
            {
                continue;
            }
            string? level = NormalizeLevel(match.Groups["level"].Value);
            if (level is null)
            {
                continue;
            }
            string message = match.Groups["message"].Value;
            parsed.Add(new MutableEntry(
                timestamp,
                level,
                match.Groups["component"].Value,
                ExtractCorrelationId(message),
                message));
        }
        return parsed.Select(entry => entry.ToContract()).ToArray();
    }

    private static bool TryParseTimestamp(int year, string value, out DateTimeOffset timestamp)
    {
        return DateTimeOffset.TryParseExact(
            $"{year:D4}/{value}",
            "yyyy/MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out timestamp);
    }

    private static int ResolveLogYear(FileInfo file)
    {
        Match match = LogFileNameRegex().Match(file.Name);
        return match.Success
            && int.TryParse(
                match.Groups["year"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int year)
            ? year
            : file.LastWriteTimeUtc.Year;
    }

    private static string? NormalizeLevel(string value)
    {
        return value.ToUpperInvariant() switch
        {
            "TRACE" or "VERBOSE" => nameof(ContractLogLevel.Trace),
            "DEBUG" => nameof(ContractLogLevel.Debug),
            "INFO" or "INFORMATION" => nameof(ContractLogLevel.Information),
            "WARN" or "WARNING" => nameof(ContractLogLevel.Warning),
            "ERROR" => nameof(ContractLogLevel.Error),
            "CRITICAL" or "FATAL" => nameof(ContractLogLevel.Critical),
            _ => null,
        };
    }

    private static string? ExtractCorrelationId(string message)
    {
        Match match = CorrelationIdRegex().Match(message);
        return match.Success && Guid.TryParse(match.Groups["value"].Value, out Guid correlationId)
            ? correlationId.ToString("D")
            : null;
    }

    private static string CreateGeneration(IReadOnlyList<FileInfo> files)
    {
        string identity = string.Join(
            '\n',
            files.Select(file => $"{file.Name}:{file.CreationTimeUtc.Ticks}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    [GeneratedRegex("^\\[(?<timestamp>\\d{2}/\\d{2} \\d{2}:\\d{2}:\\d{2})\\] \\[(?<level>[A-Z]+)\\] \\[(?<component>[^\\]]+)\\](?: (?<message>.*))?$")]
    private static partial Regex LogHeaderRegex();

    [GeneratedRegex("^aviutl2_(?<year>\\d{4})-\\d{2}-\\d{2}_", RegexOptions.CultureInvariant)]
    private static partial Regex LogFileNameRegex();

    [GeneratedRegex("\\bcorrelationId=(?<value>[0-9a-fA-F-]{36})\\b", RegexOptions.CultureInvariant)]
    private static partial Regex CorrelationIdRegex();

    private sealed class MutableEntry(
        DateTimeOffset timestamp,
        string level,
        string eventId,
        string? correlationId,
        string message)
    {
        private readonly StringBuilder _message = new(message);

        public void Append(string line)
        {
            if (_message.Length + line.Length + 1 <= BoundedLogFileReader.MAXIMUM_LINE_CHARACTERS)
            {
                _message.Append('\n').Append(line);
            }
        }

        public LogEntry ToContract() => new(
            timestamp,
            level,
            "aviutl",
            eventId,
            correlationId,
            LogSecretMasker.MaskText(_message.ToString())!);
    }
}

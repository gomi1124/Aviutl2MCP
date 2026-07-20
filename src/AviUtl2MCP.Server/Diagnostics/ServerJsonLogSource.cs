using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Server.Logging;

namespace AviUtl2MCP.Server.Diagnostics;

public sealed class ServerJsonLogSource(string logFilePath) : ILogSource
{
    private const long MAXIMUM_LOG_BYTES = 16L * 1024L * 1024L;
    private readonly string _logFilePath = Path.GetFullPath(logFilePath);

    public LogSource Source => LogSource.Server;

    public async ValueTask<LogSourcePage> ReadAsync(
        LogSourceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!File.Exists(_logFilePath))
        {
            return LogSourceFilter.CreatePage([], query, CreateGeneration(_logFilePath, null));
        }

        FileInfo file = new(_logFilePath);
        IReadOnlyList<string> lines = await BoundedLogFileReader.ReadLinesAsync(
            _logFilePath,
            MAXIMUM_LOG_BYTES,
            cancellationToken).ConfigureAwait(false);
        List<LogEntry> entries = [];
        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogEntry? entry = TryParseEntry(line);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }
        return LogSourceFilter.CreatePage(entries, query, CreateGeneration(_logFilePath, file));
    }

    private static LogEntry? TryParseEntry(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("timestamp", out JsonElement timestampElement)
                || !timestampElement.TryGetDateTimeOffset(out DateTimeOffset timestamp)
                || !root.TryGetProperty("level", out JsonElement levelElement)
                || levelElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("message", out JsonElement messageElement)
                || messageElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string level = levelElement.GetString()!;
            string message = LogSecretMasker.MaskText(messageElement.GetString())!;
            string eventId = ReadOptionalString(root, "eventName")
                ?? (root.TryGetProperty("eventId", out JsonElement eventIdElement)
                    && eventIdElement.TryGetInt32(out int eventIdNumber)
                        ? eventIdNumber.ToString(CultureInfo.InvariantCulture)
                        : "server");
            return new LogEntry(
                timestamp,
                level,
                "server",
                eventId,
                ReadOptionalString(root, "correlationId"),
                message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string CreateGeneration(string filePath, FileInfo? file)
    {
        string identity = $"{Path.GetFileName(filePath)}\n{file?.CreationTimeUtc.Ticks ?? 0}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}

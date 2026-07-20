using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AviUtl2MCP.Server.Logging;

public sealed class JsonLineLogSink : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _sync = new();
    private readonly TextWriter _standardError;
    private readonly StreamWriter _fileWriter;
    private bool _isDisposed;

    public JsonLineLogSink(string logFilePath, TextWriter standardError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        ArgumentNullException.ThrowIfNull(standardError);

        string fullPath = Path.GetFullPath(logFilePath);
        string? directoryPath = Path.GetDirectoryName(fullPath);
        if (directoryPath is null)
        {
            throw new ArgumentException("The log file path must have a parent directory.", nameof(logFilePath));
        }

        Directory.CreateDirectory(directoryPath);
        FileStream fileStream = new(
            fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _fileWriter = new StreamWriter(fileStream, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        _standardError = standardError;
    }

    public void Write(JsonLineLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string jsonLine = JsonSerializer.Serialize(entry, SerializerOptions);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _standardError.WriteLine(jsonLine);
            _standardError.Flush();
            _fileWriter.WriteLine(jsonLine);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _fileWriter.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

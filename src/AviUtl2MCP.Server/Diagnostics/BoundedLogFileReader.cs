using System.Text;

namespace AviUtl2MCP.Server.Diagnostics;

internal static class BoundedLogFileReader
{
    internal const int MAXIMUM_LINE_CHARACTERS = 64 * 1024;

    public static async ValueTask<IReadOnlyList<string>> ReadLinesAsync(
        string filePath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        bool startsMidLine = stream.Length > maximumBytes;
        if (startsMidLine)
        {
            stream.Seek(-maximumBytes, SeekOrigin.End);
        }

        using StreamReader reader = new(
            stream,
            new UTF8Encoding(false, false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        if (startsMidLine)
        {
            await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        List<string> lines = [];
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length <= MAXIMUM_LINE_CHARACTERS)
            {
                lines.Add(line);
            }
        }
        return lines;
    }
}

using System.IO.Pipes;

namespace AviUtl2MCP.BridgeClient.Transport;

public sealed class NamedPipeBridgeTransport : IBridgeTransport
{
    private const int MAX_PIPE_NAME_CHARACTERS = 256;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private CancellationTokenSource? connectCancellationSource;
    private NamedPipeClientStream? pipeStream;
    private int connectionState;

    public bool IsConnected => Volatile.Read(ref connectionState) == 2 && pipeStream?.IsConnected == true;

    public async ValueTask ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.Length > MAX_PIPE_NAME_CHARACTERS || pipeName.Contains('\\') || pipeName.Contains('/'))
        {
            throw new ArgumentException("Pipe name must be a local simple name with at most 256 characters.", nameof(pipeName));
        }

        CancellationTokenSource connectionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        NamedPipeClientStream candidate;
        try
        {
            candidate = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        }
        catch
        {
            connectionCancellation.Dispose();
            throw;
        }

        if (Interlocked.CompareExchange(ref connectionState, 1, 0) != 0)
        {
            connectionCancellation.Dispose();
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Bridge transport has already been connected or disposed.");
        }

        connectCancellationSource = connectionCancellation;
        pipeStream = candidate;
        if (Volatile.Read(ref connectionState) != 1)
        {
            connectionCancellation.Cancel();
        }

        try
        {
            await candidate.ConnectAsync(connectionCancellation.Token).ConfigureAwait(false);
            bool isDisposed = Interlocked.CompareExchange(ref connectionState, 2, 1) != 1;
            ObjectDisposedException.ThrowIf(isDisposed, this);
        }
        catch
        {
            _ = Interlocked.CompareExchange(ref pipeStream, null, candidate);
            await candidate.DisposeAsync().ConfigureAwait(false);
            _ = Interlocked.CompareExchange(ref connectionState, 0, 1);
            throw;
        }
        finally
        {
            CancellationTokenSource? ownedCancellation = Interlocked.CompareExchange(
                ref connectCancellationSource,
                null,
                connectionCancellation);
            if (ReferenceEquals(ownedCancellation, connectionCancellation))
            {
                connectionCancellation.Dispose();
            }
        }
    }

    public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        NamedPipeClientStream stream = GetConnectedStream();
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[bytesRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Named pipe ended after {bytesRead} of {buffer.Length} required bytes.");
            }

            bytesRead = checked(bytesRead + read);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NamedPipeClientStream stream = GetConnectedStream();
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref connectionState, 3) == 3)
        {
            return;
        }

        CancellationTokenSource? connectionCancellation =
            Interlocked.Exchange(ref connectCancellationSource, null);
        if (connectionCancellation is not null)
        {
            await connectionCancellation.CancelAsync().ConfigureAwait(false);
            connectionCancellation.Dispose();
        }

        NamedPipeClientStream? stream = Interlocked.Exchange(ref pipeStream, null);
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private NamedPipeClientStream GetConnectedStream()
    {
        return IsConnected && pipeStream is not null
            ? pipeStream
            : throw new InvalidOperationException("Bridge transport is not connected.");
    }
}

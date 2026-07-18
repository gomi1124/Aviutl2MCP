using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Handshake;
using AviUtl2MCP.BridgeClient.Messaging;
using AviUtl2MCP.BridgeClient.Protocol;
using AviUtl2MCP.BridgeClient.Tracking;
using AviUtl2MCP.BridgeClient.Transport;

namespace AviUtl2MCP.BridgeClient.Connections;

public sealed class BridgeConnection : IBridgeConnection
{
    private readonly IBridgeTransport transport;
    private readonly BridgeHandshakeClient handshakeClient;
    private CancellationTokenSource? lifetimeCancellation;
    private IpcRequestTracker? requestTracker;
    private Task? readLoop;
    private BridgeSessionInfo? sessionInfo;
    private int lifecycleState;

    public BridgeConnection(
        BridgeInstanceDescriptor descriptor,
        IBridgeTransport transport,
        Guid clientInstanceId,
        string clientVersion)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(transport);
        Descriptor = descriptor;
        this.transport = transport;
        handshakeClient = new BridgeHandshakeClient(transport, clientInstanceId, clientVersion);
    }

    public BridgeInstanceDescriptor Descriptor { get; }

    public BridgeSessionInfo SessionInfo => sessionInfo
        ?? throw new InvalidOperationException("Bridge handshake has not completed.");

    public bool IsConnected => Volatile.Read(ref lifecycleState) == 1 && transport.IsConnected;

    public async ValueTask HandshakeAsync(CancellationToken cancellationToken)
    {
        if (sessionInfo is not null)
        {
            throw new InvalidOperationException("Bridge handshake has already completed.");
        }

        BridgeSessionInfo session = await handshakeClient.HandshakeAsync(
            Descriptor.PipeName,
            Descriptor.InstanceId,
            cancellationToken).ConfigureAwait(false);
        if (session.AviutlProcessId != Descriptor.ProcessId
            || session.AviutlProcessCreationTime != Descriptor.ProcessCreationTime)
        {
            throw new InvalidDataException("Handshake process identity did not match the instance descriptor.");
        }

        sessionInfo = session;
        requestTracker = new IpcRequestTracker(session.Limits.InFlight);
        lifetimeCancellation = new CancellationTokenSource();
        Volatile.Write(ref lifecycleState, 1);
        readLoop = ReadLoopAsync(lifetimeCancellation.Token);
    }

    public async ValueTask<BridgeResponse> SendAsync(
        BridgeRequest request,
        ReadOnlyMemory<byte> binary,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Method);
        ArgumentOutOfRangeException.ThrowIfEqual(request.CorrelationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.TimeoutMs, 1);
        if (request.Method.Length > 128 || request.Params.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Bridge request method or params were invalid.", nameof(request));
        }

        if (!IsConnected || requestTracker is null)
        {
            throw new IOException("Bridge connection is not available.");
        }

        byte[] jsonBytes = Encoding.UTF8.GetBytes(ContractJsonSerializer.SerializeContract(request));
        IpcFrameOption options = binary.IsEmpty ? IpcFrameOption.None : IpcFrameOption.HasBinary;
        IpcEncodedFrame encoded = IpcFrameCodec.EncodeFrame(
            IpcMessageKind.Request,
            options,
            request.CorrelationId,
            jsonBytes,
            binary.Span);
        IpcRequestRegistration registration = requestTracker.Register(request.CorrelationId, deadline);
        try
        {
            await transport.WriteAsync(encoded.Bytes, cancellationToken).ConfigureAwait(false);
            IpcFrame response = await registration.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return ParseResponse(request, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = requestTracker.TryCancel(request.CorrelationId, cancellationToken);
            await TrySendCancelAsync(request.CorrelationId).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            await TrySendCancelAsync(request.CorrelationId).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            requestTracker.FailConnection(exception);
            Volatile.Write(ref lifecycleState, 2);
            throw;
        }
    }

    public async ValueTask CancelAsync(Guid requestId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(requestId, Guid.Empty);
        IpcEncodedFrame encoded = IpcFrameCodec.EncodeFrame(
            IpcMessageKind.Cancel,
            IpcFrameOption.None,
            requestId,
            "{}"u8,
            []);
        await transport.WriteAsync(encoded.Bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref lifecycleState, 3) == 3)
        {
            return;
        }

        CancellationTokenSource? cancellation = Interlocked.Exchange(ref lifetimeCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        await transport.DisposeAsync().ConfigureAwait(false);
        Task? loop = Interlocked.Exchange(ref readLoop, null);
        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }

        requestTracker?.Dispose();
        requestTracker = null;
        cancellation?.Dispose();
        sessionInfo = null;
    }

    private BridgeResponse ParseResponse(BridgeRequest request, IpcFrame frame)
    {
        BridgeResponseEnvelope envelope;
        try
        {
            envelope = ContractJsonSerializer.DeserializeContract<BridgeResponseEnvelope>(
                Encoding.UTF8.GetString(frame.JsonBytes.Span));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Bridge response JSON did not match the response contract.", exception);
        }

        if (envelope.CorrelationId != request.CorrelationId)
        {
            throw new InvalidDataException("Bridge response correlation ID did not match the request.");
        }

        if (envelope.InstanceId != Descriptor.InstanceId)
        {
            throw new InvalidDataException("Bridge response instance ID did not match the connection.");
        }

        bool hasErrorFlag = (frame.Header.Flags & IpcFrameOption.ErrorResponse) != 0;
        if (envelope.Ok == hasErrorFlag || (envelope.Ok ? envelope.Error is not null : envelope.Error is null))
        {
            throw new InvalidDataException("Bridge response success state and error fields were inconsistent.");
        }

        return new BridgeResponse(envelope, frame);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IpcFrame frame = await IpcFrameCodec.DecodeFrameAsync(transport, cancellationToken).ConfigureAwait(false);
                switch (frame.Header.MessageKind)
                {
                    case IpcMessageKind.Response:
                        _ = requestTracker?.TryComplete(frame);
                        break;
                    case IpcMessageKind.CancelAck:
                        _ = requestTracker?.TryAcknowledgeCancellation(frame);
                        break;
                    case IpcMessageKind.Ping:
                        await SendPongAsync(frame.Header.RequestId, cancellationToken).ConfigureAwait(false);
                        break;
                    case IpcMessageKind.Close:
                        throw new EndOfStreamException("Bridge closed the IPC session.");
                    default:
                        throw new InvalidDataException($"Unexpected bridge frame kind {frame.Header.MessageKind}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            requestTracker?.FailConnection(exception);
            Volatile.Write(ref lifecycleState, 2);
        }
    }

    private async ValueTask TrySendCancelAsync(Guid requestId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
        CancellationToken lifetimeToken = lifetimeCancellation?.Token ?? CancellationToken.None;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            lifetimeToken);
        try
        {
            await CancelAsync(requestId, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
    }

    private async ValueTask SendPongAsync(Guid requestId, CancellationToken cancellationToken)
    {
        IpcEncodedFrame encoded = IpcFrameCodec.EncodeFrame(
            IpcMessageKind.Pong,
            IpcFrameOption.None,
            requestId,
            "{}"u8,
            []);
        await transport.WriteAsync(encoded.Bytes, cancellationToken).ConfigureAwait(false);
    }
}

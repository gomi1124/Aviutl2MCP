using System.Text;
using System.Text.Json;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Protocol;

namespace AviUtl2MCP.BridgeClient.Tracking;

public sealed class IpcRequestTracker : IDisposable
{
    private readonly object stateGate = new();
    private readonly Dictionary<Guid, PendingRequest> pendingRequests = [];
    private readonly int maximumInFlight;
    private readonly TimeProvider timeProvider;
    private Exception? terminalError;
    private bool isDisposed;

    public IpcRequestTracker(int maximumInFlight, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumInFlight, 1);
        this.maximumInFlight = maximumInFlight;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Count
    {
        get
        {
            lock (stateGate)
            {
                return pendingRequests.Count;
            }
        }
    }

    public IpcRequestRegistration Register(Guid requestId, DateTimeOffset deadline)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(requestId, Guid.Empty);
        PendingRequest pending = new(requestId);
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (terminalError is not null)
            {
                throw new InvalidOperationException("The bridge connection is no longer available.", terminalError);
            }

            if (pendingRequests.Count >= maximumInFlight)
            {
                throw new InvalidOperationException($"The in-flight request limit of {maximumInFlight} was reached.");
            }

            if (!pendingRequests.TryAdd(requestId, pending))
            {
                throw new ArgumentException("The request ID is already in flight.", nameof(requestId));
            }
        }

        TimeSpan dueTime = deadline - timeProvider.GetUtcNow();
        if (dueTime <= TimeSpan.Zero)
        {
            TimeoutRequest(requestId);
        }
        else
        {
            try
            {
                ITimer timer = timeProvider.CreateTimer(
                    static state =>
                    {
                        TimeoutCallbackState callbackState = (TimeoutCallbackState)state!;
                        callbackState.Tracker.TimeoutRequest(callbackState.RequestId);
                    },
                    new TimeoutCallbackState(this, requestId),
                    dueTime,
                    Timeout.InfiniteTimeSpan);
                pending.AttachTimer(timer);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                RemoveAndFail(requestId, exception);
                throw;
            }
        }

        return pending.Registration;
    }

    public bool TryComplete(IpcFrame response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Header.MessageKind != IpcMessageKind.Response)
        {
            throw new ArgumentException("Only Response frames can complete an IPC request.", nameof(response));
        }

        PendingRequest? pending = Remove(response.Header.RequestId);
        if (pending is null)
        {
            return false;
        }

        pending.Complete(response);
        return true;
    }

    public bool TryCancel(Guid requestId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(requestId, Guid.Empty);
        PendingRequest? pending;
        lock (stateGate)
        {
            _ = pendingRequests.TryGetValue(requestId, out pending);
        }

        return pending?.Cancel(cancellationToken) == true;
    }

    public bool TryAcknowledgeCancellation(IpcFrame cancelAckFrame)
    {
        ArgumentNullException.ThrowIfNull(cancelAckFrame);
        ValidateCancelAckFrame(cancelAckFrame);
        CancelAcknowledgement acknowledgement = DeserializeCancelAcknowledgement(cancelAckFrame.JsonBytes.Span);
        PendingRequest? pending;
        lock (stateGate)
        {
            if (!pendingRequests.TryGetValue(cancelAckFrame.Header.RequestId, out pending))
            {
                return false;
            }

            pending.AcknowledgeCancellation(acknowledgement);
            if (!acknowledgement.ResponseWillFollow)
            {
                _ = pendingRequests.Remove(cancelAckFrame.Header.RequestId);
            }
        }

        if (!acknowledgement.ResponseWillFollow)
        {
            pending.Fail(new IOException("Bridge did not accept the request before cancellation."));
        }

        return true;
    }

    public void FailConnection(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        PendingRequest[] pending;
        lock (stateGate)
        {
            if (terminalError is not null)
            {
                return;
            }

            terminalError = error;
            pending = [.. pendingRequests.Values];
            pendingRequests.Clear();
        }

        foreach (PendingRequest request in pending)
        {
            request.Fail(error);
        }
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
        }

        FailConnection(new ObjectDisposedException(nameof(IpcRequestTracker)));
        GC.SuppressFinalize(this);
    }

    private static void ValidateCancelAckFrame(IpcFrame frame)
    {
        if (frame.Header.MessageKind != IpcMessageKind.CancelAck)
        {
            throw new ArgumentException("The frame is not a CancelAck.", nameof(frame));
        }

        if (frame.Header.Flags != IpcFrameOption.None || !frame.BinaryBytes.IsEmpty)
        {
            throw new InvalidDataException("CancelAck must not contain flags or binary data.");
        }
    }

    private static CancelAcknowledgement DeserializeCancelAcknowledgement(ReadOnlySpan<byte> jsonBytes)
    {
        CancelAcknowledgement acknowledgement;
        try
        {
            acknowledgement = ContractJsonSerializer.DeserializeContract<CancelAcknowledgement>(
                Encoding.UTF8.GetString(jsonBytes));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("CancelAck JSON did not match the protocol contract.", exception);
        }

        bool expectedResponseWillFollow = acknowledgement.Status != CancelStatus.NotFound;
        if (acknowledgement.ResponseWillFollow != expectedResponseWillFollow)
        {
            throw new InvalidDataException("CancelAck status and responseWillFollow were inconsistent.");
        }

        return acknowledgement;
    }

    private void TimeoutRequest(Guid requestId)
    {
        RemoveAndFail(requestId, new TimeoutException($"IPC request {requestId} exceeded its deadline."));
    }

    private void RemoveAndFail(Guid requestId, Exception error)
    {
        PendingRequest? pending = Remove(requestId);
        pending?.Fail(error);
    }

    private PendingRequest? Remove(Guid requestId)
    {
        lock (stateGate)
        {
            if (!pendingRequests.Remove(requestId, out PendingRequest? pending))
            {
                return null;
            }

            return pending;
        }
    }

    private sealed record TimeoutCallbackState(IpcRequestTracker Tracker, Guid RequestId);

    private sealed class PendingRequest : IDisposable
    {
        private readonly object resourceGate = new();
        private readonly TaskCompletionSource<IpcFrame> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CancelAcknowledgement> cancellationAcknowledged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ITimer? timer;
        private bool isDisposed;

        public PendingRequest(Guid requestId)
        {
            Registration = new IpcRequestRegistration(
                requestId,
                completion.Task,
                cancellationAcknowledged.Task);
        }

        public IpcRequestRegistration Registration { get; }

        public void AttachTimer(ITimer value)
        {
            lock (resourceGate)
            {
                if (isDisposed)
                {
                    value.Dispose();
                    return;
                }

                timer = value;
            }
        }

        public void Complete(IpcFrame response)
        {
            _ = completion.TrySetResult(response);
            _ = cancellationAcknowledged.TrySetCanceled();
            Dispose();
        }

        public bool Cancel(CancellationToken cancellationToken)
        {
            return completion.TrySetCanceled(cancellationToken);
        }

        public void AcknowledgeCancellation(CancelAcknowledgement acknowledgement)
        {
            _ = cancellationAcknowledged.TrySetResult(acknowledgement);
        }

        public void Fail(Exception error)
        {
            _ = completion.TrySetException(error);
            _ = cancellationAcknowledged.TrySetCanceled();
            Dispose();
        }

        public void Dispose()
        {
            ITimer? timerToDispose;
            lock (resourceGate)
            {
                if (isDisposed)
                {
                    return;
                }

                isDisposed = true;
                timerToDispose = timer;
                timer = null;
            }

            timerToDispose?.Dispose();
        }
    }
}

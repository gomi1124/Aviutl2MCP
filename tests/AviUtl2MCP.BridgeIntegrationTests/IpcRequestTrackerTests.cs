using System.Text;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Protocol;
using AviUtl2MCP.BridgeClient.Tracking;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class IpcRequestTrackerTests
{
    [TestMethod]
    public async Task TryCompleteCorrelatesOutOfOrderResponses()
    {
        // Arrange
        ManualTimeProvider timeProvider = new(TestTime.CreateReferenceUtc());
        using IpcRequestTracker tracker = new(2, timeProvider);
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        IpcRequestRegistration first = tracker.Register(firstId, timeProvider.GetUtcNow().AddMinutes(1));
        IpcRequestRegistration second = tracker.Register(secondId, timeProvider.GetUtcNow().AddMinutes(1));
        IpcFrame secondResponse = CreateFrame(IpcMessageKind.Response, secondId, "{\"order\":2}");
        IpcFrame firstResponse = CreateFrame(IpcMessageKind.Response, firstId, "{\"order\":1}");

        // Act
        bool completedSecond = tracker.TryComplete(secondResponse);
        bool completedFirst = tracker.TryComplete(firstResponse);

        // Assert
        Assert.IsTrue(completedSecond);
        Assert.IsTrue(completedFirst);
        Assert.AreSame(secondResponse, await second.Completion);
        Assert.AreSame(firstResponse, await first.Completion);
        Assert.AreEqual(0, tracker.Count);
    }

    [TestMethod]
    public async Task RegisterTimesOutAtDeadline()
    {
        // Arrange
        ManualTimeProvider timeProvider = new(TestTime.CreateReferenceUtc());
        using IpcRequestTracker tracker = new(1, timeProvider);
        IpcRequestRegistration registration = tracker.Register(
            Guid.NewGuid(),
            timeProvider.GetUtcNow().AddSeconds(10));

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        Func<Task> waitForResponse = async () => await registration.Completion;

        // Assert
        await Assert.ThrowsExactlyAsync<TimeoutException>(waitForResponse);
        Assert.AreEqual(0, tracker.Count);
    }

    [TestMethod]
    public async Task FailConnectionFailsEveryPendingRequest()
    {
        // Arrange
        ManualTimeProvider timeProvider = new(TestTime.CreateReferenceUtc());
        using IpcRequestTracker tracker = new(2, timeProvider);
        IpcRequestRegistration first = tracker.Register(Guid.NewGuid(), timeProvider.GetUtcNow().AddMinutes(1));
        IpcRequestRegistration second = tracker.Register(Guid.NewGuid(), timeProvider.GetUtcNow().AddMinutes(1));
        IOException disconnect = new("Pipe disconnected.");

        // Act
        tracker.FailConnection(disconnect);
        Func<Task> waitForFirst = async () => await first.Completion;
        Func<Task> waitForSecond = async () => await second.Completion;

        // Assert
        Assert.AreSame(disconnect, await Assert.ThrowsExactlyAsync<IOException>(waitForFirst));
        Assert.AreSame(disconnect, await Assert.ThrowsExactlyAsync<IOException>(waitForSecond));
        Assert.AreEqual(0, tracker.Count);
    }

    [TestMethod]
    public async Task CancelAckIsCorrelatedBeforeFinalResponse()
    {
        // Arrange
        ManualTimeProvider timeProvider = new(TestTime.CreateReferenceUtc());
        using IpcRequestTracker tracker = new(1, timeProvider);
        Guid requestId = Guid.NewGuid();
        IpcRequestRegistration registration = tracker.Register(
            requestId,
            timeProvider.GetUtcNow().AddMinutes(1));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // Act
        bool cancelled = tracker.TryCancel(requestId, cancellation.Token);
        bool acknowledged = tracker.TryAcknowledgeCancellation(CreateCancelAck(
            requestId,
            new CancelAcknowledgement(CancelStatus.TooLate, true)));
        CancelAcknowledgement result = await registration.CancellationAcknowledged;
        IpcFrame response = CreateFrame(IpcMessageKind.Response, requestId, "{\"ok\":true}");
        bool completed = tracker.TryComplete(response);

        // Assert
        Assert.IsTrue(cancelled);
        Assert.IsTrue(acknowledged);
        Assert.AreEqual(CancelStatus.TooLate, result.Status);
        Assert.IsTrue(result.ResponseWillFollow);
        Assert.IsTrue(completed);
        Assert.IsTrue(registration.Completion.IsCanceled);
        Assert.AreEqual(0, tracker.Count);
    }

    [TestMethod]
    public async Task NotFoundCancelAckEndsTrackingWithoutResponse()
    {
        // Arrange
        ManualTimeProvider timeProvider = new(TestTime.CreateReferenceUtc());
        using IpcRequestTracker tracker = new(1, timeProvider);
        Guid requestId = Guid.NewGuid();
        IpcRequestRegistration registration = tracker.Register(
            requestId,
            timeProvider.GetUtcNow().AddMinutes(1));
        IpcFrame cancelAck = CreateCancelAck(
            requestId,
            new CancelAcknowledgement(CancelStatus.NotFound, false));

        // Act
        bool acknowledged = tracker.TryAcknowledgeCancellation(cancelAck);
        CancelAcknowledgement result = await registration.CancellationAcknowledged;
        Func<Task> waitForResponse = async () => await registration.Completion;

        // Assert
        Assert.IsTrue(acknowledged);
        Assert.AreEqual(CancelStatus.NotFound, result.Status);
        await Assert.ThrowsExactlyAsync<IOException>(waitForResponse);
        Assert.AreEqual(0, tracker.Count);
    }

    [TestMethod]
    public void RegisterEnforcesNegotiatedInFlightLimit()
    {
        // Arrange
        ManualTimeProvider timeProvider = new(TestTime.CreateReferenceUtc());
        using IpcRequestTracker tracker = new(1, timeProvider);
        _ = tracker.Register(Guid.NewGuid(), timeProvider.GetUtcNow().AddMinutes(1));

        // Act
        Action registerSecond = () =>
            tracker.Register(Guid.NewGuid(), timeProvider.GetUtcNow().AddMinutes(1));

        // Assert
        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(registerSecond);
        StringAssert.Contains(exception.Message, "in-flight", StringComparison.Ordinal);
    }

    private static IpcFrame CreateCancelAck(Guid requestId, CancelAcknowledgement acknowledgement)
    {
        return CreateFrame(
            IpcMessageKind.CancelAck,
            requestId,
            ContractJsonSerializer.SerializeContract(acknowledgement));
    }

    private static IpcFrame CreateFrame(IpcMessageKind messageKind, Guid requestId, string json)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        IpcFrameHeader header = new(messageKind, IpcFrameOption.None, requestId, (uint)jsonBytes.Length, 0);
        return new IpcFrame(
            header,
            jsonBytes,
            ReadOnlyMemory<byte>.Empty,
            IpcFrameCodec.CalculatePayloadHash(header, jsonBytes, []));
    }

    private static class TestTime
    {
        public static DateTimeOffset CreateReferenceUtc()
        {
            return new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state, dueTime, period);
            timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            utcNow += duration;
            foreach (ManualTimer timer in timers.ToArray())
            {
                timer.FireIfDue(utcNow);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider owner;
            private readonly TimerCallback callback;
            private readonly object? state;
            private DateTimeOffset dueAt;
            private TimeSpan period;
            private bool isDisposed;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                this.owner = owner;
                this.callback = callback;
                this.state = state;
                dueAt = owner.GetUtcNow() + dueTime;
                this.period = period;
            }

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                if (isDisposed)
                {
                    return false;
                }

                dueAt = owner.GetUtcNow() + dueTime;
                period = newPeriod;
                return true;
            }

            public void Dispose()
            {
                isDisposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue(DateTimeOffset now)
            {
                if (isDisposed || dueAt > now)
                {
                    return;
                }

                if (period == Timeout.InfiniteTimeSpan)
                {
                    dueAt = DateTimeOffset.MaxValue;
                }
                else
                {
                    dueAt += period;
                }

                callback(state);
            }
        }
    }
}

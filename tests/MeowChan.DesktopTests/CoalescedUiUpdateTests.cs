using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>No real delays or thread-pool settling: timer expiry and callback delivery are separate controls.</summary>
public sealed class CoalescedUiUpdateTests
{
    private sealed class ManualTime : TimeProvider
    {
        private long _now;
        private readonly List<ManualTimer> _timers = [];
        public override long GetTimestamp() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal int ActiveTimers => _timers.Count(timer => timer.Due != null);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            _now += duration.Ticks;
            foreach (Action callback in CaptureDue()) callback();
        }
        internal Action[] CaptureDue()
        {
            ManualTimer[] due = _timers.Where(timer => timer.Due is long due && due <= _now).ToArray();
            foreach (ManualTimer timer in due) timer.Due = null;
            return due.Select(timer => (Action)timer.Deliver).ToArray();
        }
        // Simulate an OS callback already queued before timer disposal.
        internal Action CaptureTimerDelivery() => _timers.Last().Deliver;
        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal long? Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime.Ticks;
                return true;
            }
            internal void Deliver() => callback(state);
            public void Dispose() => Due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Harness : IDisposable
    {
        internal readonly ManualTime Clock = new();
        internal readonly List<(Action Action, DispatcherPriority Priority)> Posted = [];
        internal int Applied;
        internal Action? Applying;
        internal readonly CoalescedUiUpdate Update;
        internal Harness(int milliseconds = 100) => Update = new(() => { Applied++; Applying?.Invoke(); },
            TimeSpan.FromMilliseconds(milliseconds), Clock, (action, priority) => Posted.Add((action, priority)));
        internal void RunOne(int index = 0)
        {
            var item = Posted[index]; Posted.RemoveAt(index); item.Action();
        }
        internal void RunPosted() { while (Posted.Count != 0) RunOne(); }
        internal void RunPriority(DispatcherPriority priority)
        {
            int index;
            while ((index = Posted.FindIndex(item => item.Priority == priority)) >= 0) RunOne(index);
        }
        public void Dispose() => Update.Dispose();
    }

    [Fact]
    public void FirstRequestIsAppliedWithoutDelayAndBurstFoldsIntoOne()
    {
        using Harness h = new();
        for (int index = 0; index < 50; index++) h.Update.Request();
        Assert.Single(h.Posted);
        h.RunPosted();
        Assert.Equal(1, h.Applied);
        Assert.Equal(50, h.Update.Requests);
        Assert.Equal(49, h.Update.SuppressedRequests);
    }

    [Fact]
    public void ProgressWaitsForTheIntervalAndLastChangeIsAppliedOnce()
    {
        using Harness h = new();
        h.Update.Request(); h.RunPosted();
        h.Clock.Advance(TimeSpan.FromMilliseconds(30));
        for (int index = 0; index < 20; index++) h.Update.Request();
        Assert.Empty(h.Posted);
        Assert.Equal(1, h.Clock.ActiveTimers);
        h.Clock.Advance(TimeSpan.FromMilliseconds(69));
        Assert.Empty(h.Posted);
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Single(h.Posted);
        Assert.Equal(DispatcherPriority.Background, h.Posted[0].Priority);
        h.RunPosted();
        Assert.Equal(2, h.Applied);
        Assert.Equal(0, h.Clock.ActiveTimers);
    }

    [Fact]
    public void SupersededTimerAt100CannotConsumeNewRequestDueAt120()
    {
        using Harness h = new();
        h.Update.Request(); h.RunPosted();
        h.Update.Request();
        Action oldTimer = h.Clock.CaptureTimerDelivery();
        h.Clock.Advance(TimeSpan.FromMilliseconds(20));
        h.Update.RequestImmediate(); h.RunPosted();
        h.Clock.Advance(TimeSpan.FromMilliseconds(10));
        h.Update.Request(); // t=30: B is due at t=120, following the t=20 immediate application.
        h.Clock.Advance(TimeSpan.FromMilliseconds(70)); // t=100: obsolete A was due now.
        oldTimer();
        Assert.Empty(h.Posted);
        Assert.Equal(2, h.Applied);
        Assert.Equal(1, h.Clock.ActiveTimers);
        h.Clock.Advance(TimeSpan.FromMilliseconds(20)); h.RunPosted(); // t=120: exactly B applies.
        Assert.Equal(3, h.Applied);
    }

    [Fact]
    public void ImmediatePromotesAnAlreadyPostedBackgroundCallback()
    {
        using Harness h = new();
        int latest = 1, shown = 0;
        h.Applying = () => shown = latest;
        h.Update.Request(); // Background is deliberately starved.
        latest = 2;
        h.Update.RequestImmediate();
        Assert.Equal(2, h.Posted.Count); // One obsolete physical callback, one current ticket.
        h.RunPriority(DispatcherPriority.Normal); // Do not service Background at all.
        Assert.Equal(2, shown);
        Assert.Equal(1, h.Applied);
        Assert.Equal(DispatcherPriority.Background, Assert.Single(h.Posted).Priority);
        h.Update.Request();
        h.RunOne(); // The stale background callback cannot steal this newer waiting request.
        Assert.Equal(1, h.Applied);
        h.Clock.Advance(TimeSpan.FromMilliseconds(100)); h.RunPosted();
        Assert.Equal(2, h.Applied);
    }

    [Fact]
    public void TimerPostedBeforeImmediateCannotApplyAgainOrStealLaterRequest()
    {
        using Harness h = new();
        h.Update.Request(); h.RunPosted();
        h.Update.Request(); h.Clock.Advance(TimeSpan.FromMilliseconds(100));
        h.Update.RequestImmediate();
        h.RunOne(1);
        h.Update.Request();
        h.RunPosted();
        Assert.Equal(2, h.Applied);
        h.Clock.Advance(TimeSpan.FromMilliseconds(100)); h.RunPosted();
        Assert.Equal(3, h.Applied);
    }

    [Fact]
    public void RequestsDuringApplicationRetainOneNonReentrantUrgentFollowUp()
    {
        using Harness h = new();
        h.Applying = () =>
        {
            if (h.Applied != 1) return;
            for (int i = 0; i < 20; i++) h.Update.Request();
            h.Update.RequestImmediate();
            Assert.Empty(h.Posted); // Even a nested dispatcher pump cannot reenter the update.
        };
        h.Update.Request(); h.RunOne();
        Assert.Single(h.Posted);
        Assert.Equal(DispatcherPriority.Normal, h.Posted[0].Priority);
        h.RunPosted();
        Assert.Equal(2, h.Applied);
        Assert.Equal(0, h.Clock.ActiveTimers);
    }

    [Fact]
    public void ProgressRequestedDuringApplicationWaitsFromApplicationStart()
    {
        using Harness h = new();
        h.Applying = () => { if (h.Applied == 1) h.Update.Request(); };
        h.Update.Request(); h.RunPosted();
        Assert.Equal(1, h.Applied);
        h.Clock.Advance(TimeSpan.FromMilliseconds(100)); h.RunPosted();
        Assert.Equal(2, h.Applied);
        h.Clock.Advance(TimeSpan.FromSeconds(1)); h.RunPosted();
        Assert.Equal(2, h.Applied);
    }

    [Fact]
    public void ThrowingApplicationStillSchedulesItsFollowUp()
    {
        using Harness h = new();
        h.Applying = () => { if (h.Applied == 1) { h.Update.RequestImmediate(); throw new InvalidOperationException(); } };
        h.Update.Request();
        Assert.Throws<InvalidOperationException>(() => h.RunOne());
        h.RunPosted();
        Assert.Equal(2, h.Applied);
        Assert.Equal(2, h.Update.Applications);
    }

    [Fact]
    public void DisposeCancelsTimerAndRejectsQueuedAndLateCallbacks()
    {
        using Harness h = new();
        h.Update.Request(); h.RunPosted(); h.Update.Request();
        Action oldTimer = h.Clock.CaptureTimerDelivery();
        h.Update.RequestImmediate();
        h.Update.Dispose();
        Assert.Equal(0, h.Clock.ActiveTimers);
        oldTimer(); h.RunPosted(); h.Update.Request(); h.Update.RequestImmediate();
        Assert.Equal(1, h.Applied);
        Assert.Empty(h.Posted);
    }

    [Fact]
    public void ZeroIntervalRetainsNormalPriorityAndMeasuresApplicationDuration()
    {
        using Harness h = new(0);
        h.Applying = () => h.Clock.Advance(TimeSpan.FromMilliseconds(7));
        h.Update.Request(); h.Update.Request();
        Assert.Single(h.Posted);
        Assert.Equal(DispatcherPriority.Normal, h.Posted[0].Priority);
        h.RunPosted();
        Assert.Equal(TimeSpan.FromMilliseconds(7), h.Update.LongestApplication);
        h.Update.Request(); Assert.Single(h.Posted);
    }
}

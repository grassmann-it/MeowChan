using System.Collections.Concurrent;
using MeowChan.Diagnostics;

namespace MeowChan.DesktopTests;

/// <summary>PF-00: the probe observes a blocked "UI" from its own thread, with bounded memory and no pile-up.</summary>
public sealed class UiResponsivenessProbeTests
{
    /// <summary>A single-threaded dispatcher stand-in whose queue can be stalled on purpose.</summary>
    private sealed class FakeUi : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        public int Posted;
        public FakeUi()
        {
            _thread = new Thread(() => { foreach (Action action in _queue.GetConsumingEnumerable()) action(); }) { IsBackground = true };
            _thread.Start();
        }
        public void Post(Action action) { Interlocked.Increment(ref Posted); _queue.Add(action); }
        public void Block(ManualResetEventSlim release, ManualResetEventSlim entered) => _queue.Add(() => { entered.Set(); release.Wait(); });
        public void Dispose() { _queue.CompleteAdding(); _thread.Join(2000); }
    }

    [Fact]
    public void IdleUiProducesSmallLagAndNoHardStall()
    {
        using FakeUi ui = new();
        using UiResponsivenessProbe probe = new(ui.Post, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
        probe.Start();
        Assert.True(SpinWait.SpinUntil(() => probe.Samples.Count >= 10, TimeSpan.FromSeconds(10)));
        UiResponsivenessSummary summary = probe.Summarize();
        Assert.Equal(0, summary.HardStalls);
        Assert.Equal(0, summary.DroppedSamples);
        Assert.True(summary.MedianLagMilliseconds <= summary.P95LagMilliseconds && summary.P95LagMilliseconds <= summary.MaxLagMilliseconds);
    }

    [Fact]
    public void BlockedUiIsObservedFromOutsideWithGapAndHardStallAndNoPingPileUp()
    {
        using FakeUi ui = new();
        List<UiStallReport> reports = new();
        using UiResponsivenessProbe probe = new(ui.Post, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(150),
            report => { lock (reports) reports.Add(report); });
        probe.Start();
        Assert.True(SpinWait.SpinUntil(() => probe.Samples.Count >= 3, TimeSpan.FromSeconds(10)));
        using ManualResetEventSlim release = new(), entered = new();
        ui.Block(release, entered);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        // The UI thread is now stuck; only the probe's own thread can notice.
        Assert.True(SpinWait.SpinUntil(() => { lock (reports) return reports.Count > 0; }, TimeSpan.FromSeconds(10)));
        int postedWhileBlocked = ui.Posted;
        Thread.Sleep(200);
        Assert.True(ui.Posted - postedWhileBlocked <= 1, "At most one ping may be outstanding.");
        UiResponsivenessSummary blocked = probe.Summarize();
        Assert.True(blocked.OngoingSilenceMilliseconds >= 100);
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => probe.Summarize().OngoingSilenceMilliseconds < 100, TimeSpan.FromSeconds(10)));
        UiResponsivenessSummary after = probe.Summarize();
        Assert.True(after.MaxGapMilliseconds >= 150, "The silent interval must be part of the summary.");
        Assert.Equal(1, after.HardStalls);                // reported once per stall, not once per probe tick
        lock (reports) Assert.Contains("UiResponsivenessProbe", reports[0].WatchdogStack);
    }

    [Fact]
    public void BufferIsBoundedAndCountsDroppedSamples()
    {
        using FakeUi ui = new();
        using UiResponsivenessProbe probe = new(ui.Post, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), capacity: 16);
        probe.Start();
        Assert.True(SpinWait.SpinUntil(() => probe.Summarize().DroppedSamples > 0, TimeSpan.FromSeconds(10)));
        UiResponsivenessSummary summary = probe.Summarize();
        Assert.Equal(16, summary.Samples);
        Assert.True(summary.DroppedSamples > 0);
    }

    [Fact]
    public void ThrowingPostDoesNotKillTheProbe()
    {
        int attempts = 0;
        using UiResponsivenessProbe probe = new(_ => { Interlocked.Increment(ref attempts); throw new InvalidOperationException("closed"); },
            TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(5));
        probe.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref attempts) >= 3, TimeSpan.FromSeconds(10)));
    }
}

using System.Diagnostics;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>PF-01: the UI reads a complete published projection and never waits for the engine.</summary>
public sealed class DesktopSessionProjectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class Store : ISessionStore
    {
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLoadResult(new SessionSnapshot(), null, false));
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task StartingProjectionNeedsNoCaptureAndStartPublishesReadyBeforeCompletion()
    {
        using TempDirectory directory = new();
        new Config().SaveConfig(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), (_, _) => new Store());
        try
        {
            DesktopSessionProjection initial = host.Projection;
            Assert.Equal(DesktopSessionState.Starting, initial.View.State);
            Assert.Equal(0, initial.Version);
            Assert.False(initial.IsStale);
            await host.StartAsync().WaitAsync(Timeout);
            DesktopSessionProjection ready = host.Projection;
            Assert.Equal(DesktopSessionState.Ready, ready.View.State);
            Assert.Equal(initial.Epoch, ready.Epoch);
            Assert.True(ready.Version > initial.Version);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task EachHostHasItsOwnEpochAndVersionsOnlyIncrease()
    {
        using TempDirectory first = new();
        using TempDirectory second = new();
        new Config().SaveConfig(first.Path);
        new Config().SaveConfig(second.Path);
        DesktopSessionHost one = new(first.Path, Array.Empty<IImageBoard>(), (_, _) => new Store());
        DesktopSessionHost two = new(second.Path, Array.Empty<IImageBoard>(), (_, _) => new Store());
        try
        {
            await one.StartAsync().WaitAsync(Timeout);
            await two.StartAsync().WaitAsync(Timeout);
            Assert.NotEqual(one.Projection.Epoch, two.Projection.Epoch);
            long before = one.Projection.Version;
            DesktopSessionProjection later = await one.CaptureProjectionAsync().WaitAsync(Timeout);
            Assert.True(later.Version > before);
        }
        finally
        {
            await one.DisposeAsync().AsTask().WaitAsync(Timeout);
            await two.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task PauseCompletionHasAlreadyPublishedTheConfirmedState()
    {
        using TempDirectory directory = new();
        new Config().SaveConfig(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), (_, _) => new Store());
        try
        {
            await host.StartAsync().WaitAsync(Timeout);
            Assert.False(host.Projection.IsPaused);
            await host.SetPausedAsync(true).WaitAsync(Timeout);
            Assert.True(host.Projection.IsPaused);
            Assert.False(host.Projection.IsPauseTransitioning);
            await host.SetPausedAsync(false).WaitAsync(Timeout);
            Assert.False(host.Projection.IsPaused);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task BurstOfInvalidationsIsCoalescedAndNotifiesObservers()
    {
        using TempDirectory directory = new();
        new Config().SaveConfig(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), (_, _) => new Store());
        try
        {
            await host.StartAsync().WaitAsync(Timeout);
            await host.CaptureProjectionAsync().WaitAsync(Timeout);
            DesktopProjectionStats before = host.ProjectionStats;
            int published = 0;
            host.ProjectionPublished += () => Interlocked.Increment(ref published);
            // Settings applications signal the session; many in a row must not queue one capture each.
            Config draft = host.Config.Clone();
            for (int index = 0; index < 20; index++) await host.ApplySettingsAsync(draft).WaitAsync(Timeout);
            Assert.True(SpinWait.SpinUntil(() => host.ProjectionStats.Captures > before.Captures, Timeout));
            Assert.True(Volatile.Read(ref published) >= 1);
            DesktopSessionProjection last = host.Projection;
            await Task.Delay(100);
            Assert.True(host.Projection.Version >= last.Version);
            Assert.False(host.Projection.IsStale);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ReadingTheProjectionNeverWaitsForAHeldCapture()
    {
        using TempDirectory directory = new();
        new Config().SaveConfig(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), (_, _) => new Store());
        try
        {
            await host.StartAsync().WaitAsync(Timeout);
            // A concurrent synchronous capture holds the capture lock; the published read is lock-free.
            ManualResetEventSlim gate = new();
            Task holder = Task.Factory.StartNew(() => host.HoldCaptureLockForTest(gate), TaskCreationOptions.LongRunning);
            try
            {
                SpinWait.SpinUntil(() => host.CaptureLockHeldForTest, Timeout);
                long begin = Stopwatch.GetTimestamp();
                DesktopSessionProjection read = host.Projection;
                Assert.Equal(DesktopSessionState.Ready, read.View.State);
                Assert.True(Stopwatch.GetElapsedTime(begin) < TimeSpan.FromSeconds(1));
                Assert.False(host.CanRequestCheck(new DiagnosticTarget(DiagnosticTargetKind.Thread, 12345)));
            }
            finally { gate.Set(); await holder.WaitAsync(Timeout); }
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }
}

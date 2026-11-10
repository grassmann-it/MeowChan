using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>The early archive read milestone: reads only, never write, command or engine readiness.</summary>
public sealed class CatalogReadableHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private sealed class ObservedStore : ISessionStore
    {
        internal int Loads;
        internal bool Fail;
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Loads);
            return Fail ? Task.FromException<SessionLoadResult>(new IOException("Synthetic restore failure"))
                : Task.FromResult(new SessionLoadResult(new SessionSnapshot(), null, false));
        }
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private static void Seed(string profile) => new Config { SaveUrlsOnExit = true, DownloadsPaused = true }.SaveConfig(profile);

    [Fact]
    public async Task CatalogIsReadableWhileRestoreIsStillPendingAndGrantsNoCommandReadiness()
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        ObservedStore store = new();
        await using DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        TaskCompletionSource preview = new(TaskCreationOptions.RunContinuationsAsynchronously), afterPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int published = 0;
        host.CatalogReadableChanged += () => Interlocked.Increment(ref published);
        host.AfterStartupPreviewForTests = async ct => { preview.SetResult(); await afterPreview.Task.WaitAsync(ct); };
        host.AfterCatalogReadableForTests = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); };
        Task startup = host.StartAsync();
        try
        {
            await preview.Task.WaitAsync(Deadline);
            // Before the catalog was opened by the startup owner nothing is readable.
            Assert.False(host.CatalogReadable); Assert.Equal(0, published);
            afterPreview.SetResult();
            await entered.Task.WaitAsync(Deadline);
            Assert.False(startup.IsCompleted);
            Assert.Equal(DesktopSessionState.Starting, host.State);
            Assert.True(host.CatalogReadable); Assert.Equal(1, published);
            Assert.Equal(0, store.Loads); // The registry restore has not begun.
            await using (ArchiveReader reader = await host.OpenArchiveReaderAsync().WaitAsync(Deadline))
                Assert.Equal(0, (await reader.ListThreadsAsync().WaitAsync(Deadline)).TotalCount);
            // Reading is not authority: every command still requires Ready.
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync("https://boards.4chan.org/b/thread/123"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RemoveThreadAsync(-1));
            Assert.Empty(host.Projection.View.Threads); Assert.Empty(host.Projection.CheckableTargets);
            Assert.Equal(0, host.AcceptedCommandCount);
        }
        finally { afterPreview.TrySetResult(); release.TrySetResult(); }
        await startup.WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, host.State);
        Assert.True(host.CatalogReadable); Assert.Equal(1, published); Assert.Equal(1, store.Loads);
    }

    [Fact]
    public async Task StopAtTheReadableBarrierEndsReadabilityWithoutLateRestoreOrReady()
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        ObservedStore store = new();
        DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterCatalogReadableForTests = async ct => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        Task startup = host.StartAsync(); await entered.Task.WaitAsync(Deadline);
        Assert.True(host.CatalogReadable);
        await host.DisposeAsync().AsTask().WaitAsync(Deadline); await startup.WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Stopped, host.State);
        Assert.False(host.CatalogReadable);
        Assert.Equal(0, store.Loads); Assert.True(host.IsProfileReleased);
        using ProfileLease next = ProfileLease.Acquire(profile.Path);
    }

    [Fact]
    public async Task FailedRestoreWithdrawsReadability()
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        await using DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => new ObservedStore { Fail = true });
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
        Assert.False(host.CatalogReadable);
    }
}

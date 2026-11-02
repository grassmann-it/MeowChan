using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class StartupPreviewHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static StartupPreviewSnapshot Preview() => new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), 1, 1,
        Array.AsReadOnly(new[] { new StartupPreviewRow(false, "Cached source", "b", "Unverified cached title", 15, 7),
            new StartupPreviewRow(true, "Cached source", "g", "Old board", 0, 0) }));
    private static void Seed(string profile, bool enabled = true)
    {
        new Config { SaveUrlsOnExit = enabled, DownloadsPaused = true }.SaveConfig(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        new StartupPreviewStore(profile, lease).Save(Preview());
    }
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
    [Fact]
    public async Task CachedTextIsPublishedBeforeStartupDependenciesButNeverBecomesWatchIntentOrAuthority()
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        byte[] cache = File.ReadAllBytes(Path.Combine(profile.Path, "startup-preview.json"));
        ObservedStore store = new();
        await using DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterStartupPreviewForTests = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); };
        Task startup = host.StartAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.False(startup.IsCompleted); Assert.Equal(0, store.Loads);
            Assert.Equal(DesktopSessionState.Starting, host.State);
            Assert.Equal("Unverified cached title", host.StartupPreview!.Rows[0].Title);
            Assert.Empty(host.Projection.View.Threads); Assert.Empty(host.Projection.View.Boards);
            Assert.Empty(host.Projection.CheckableTargets); Assert.Empty(host.Projection.Providers);
            Assert.False(host.CanRequestCheck(new(DiagnosticTargetKind.Thread, -1)));
            Assert.False(host.CanRetryMissingMedia(-1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync("https://boards.4chan.org/b/thread/123"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetUserThreadPriorityAsync(-1, true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RemoveThreadAsync(-1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RetryMissingMediaAsync(-1));
            Assert.Equal(cache, File.ReadAllBytes(Path.Combine(profile.Path, "startup-preview.json")));
        }
        finally { release.TrySetResult(); }
        await startup.WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, host.State); Assert.Equal(1, store.Loads);
        Assert.Empty(host.Projection.View.Threads); Assert.Empty(host.Projection.View.Boards); // forged display cannot add a watch
    }
    [Theory]
    [InlineData("missing")] [InlineData("malformed")] [InlineData("future")] [InlineData("oversize")] [InlineData("disabled")]
    public async Task DisplayCacheMissOrInvalidityDoesNotPreventNormalRestore(string mode)
    {
        using TempDirectory profile = new(); Seed(profile.Path, mode != "disabled");
        string path = Path.Combine(profile.Path, "startup-preview.json");
        if (mode == "missing") File.Delete(path);
        if (mode == "malformed") File.WriteAllText(path, "{bad");
        if (mode == "future")
        {
            System.Text.Json.Nodes.JsonObject document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document["schemaVersion"] = 99; // otherwise-valid exact schema, not a malformed-field proxy
            File.WriteAllText(path, document.ToJsonString());
        }
        if (mode == "oversize") File.WriteAllBytes(path, new byte[StartupPreviewStore.MaximumBytes + 1]);
        byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        ObservedStore store = new();
        await using DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, host.State); Assert.Null(host.StartupPreview);
        // Existing registry contract skips saved-session loading when retention is off.
        // The disposable preview must not override that authoritative preference.
        Assert.Equal(mode == "disabled" ? 0 : 1, store.Loads);
        if (before != null) Assert.Equal(before, File.ReadAllBytes(path)); // startup itself never repairs displaycache
    }
    [Fact]
    public async Task StopAtPreviewBarrierCancelsStartupWithoutLateActivationOrOverwritingGoodCache()
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        string path = Path.Combine(profile.Path, "startup-preview.json"); byte[] before = File.ReadAllBytes(path);
        ObservedStore store = new();
        DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterStartupPreviewForTests = async ct => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        Task startup = host.StartAsync(); await entered.Task.WaitAsync(Deadline);
        await host.DisposeAsync().AsTask().WaitAsync(Deadline); await startup.WaitAsync(Deadline);
        Assert.Equal(0, store.Loads); Assert.Equal(DesktopSessionState.Stopped, host.State);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.True(host.IsProfileReleased);
        using ProfileLease next = ProfileLease.Acquire(profile.Path);
    }
    [Fact]
    public async Task FailedRestoreRetainsOldPreviewFileWithoutSavingAFakeSuccessfulState()
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        string path = Path.Combine(profile.Path, "startup-preview.json"); byte[] before = File.ReadAllBytes(path);
        DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => new ObservedStore { Fail = true });
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetUserThreadPriorityAsync(-1, true));
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task CleanShutdownCheckpointsActualCompleteReadyFrameAndRestartsWithoutInventingWatches()
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        await using (DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => new ObservedStore()))
            await host.StartAsync().WaitAsync(Deadline);
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            StartupPreviewSnapshot saved = new StartupPreviewStore(profile.Path, lease).Load()!;
            Assert.Empty(saved.Rows); Assert.Equal(0, saved.ThreadCount); Assert.Equal(0, saved.BoardCount);
            Assert.NotEqual(Preview().CapturedAtUtc, saved.CapturedAtUtc);
        }
        await using DesktopSessionHost next = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => new ObservedStore());
        await next.StartAsync().WaitAsync(Deadline);
        Assert.NotNull(next.StartupPreview); Assert.Empty(next.StartupPreview.Rows);
        Assert.Empty(next.Projection.View.Threads);
    }
    [Fact]
    public async Task RealPausedWatchesCheckpointTheirTitlesButCacheDoesNotActivateNetwork()
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        using HttpClient client = new(new NoRequests());
        await using (DesktopSessionHost first = new(profile.Path, new[] { new MeowChan.Boards.FourChan.FourChanImageBoard(client) }))
        {
            await first.StartAsync().WaitAsync(Deadline);
            await first.AddAsync("https://boards.4chan.org/b/thread/123").WaitAsync(Deadline);
            await first.AddAsync("https://boards.4chan.org/g/").WaitAsync(Deadline);
            Assert.Single(first.Projection.View.Threads); Assert.Single(first.Projection.View.Boards);
        }
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            StartupPreviewSnapshot checkpoint = new StartupPreviewStore(profile.Path, lease).Load()!;
            Assert.Equal(1, checkpoint.ThreadCount); Assert.Equal(1, checkpoint.BoardCount);
            Assert.Equal(2, checkpoint.Rows.Count);
            Assert.DoesNotContain(checkpoint.Rows, r => r.Title == "Unverified cached title");
        }
        await using DesktopSessionHost next = new(profile.Path, new[] { new MeowChan.Boards.FourChan.FourChanImageBoard(client) });
        await next.StartAsync().WaitAsync(Deadline);
        Assert.Equal(2, next.StartupPreview!.Rows.Count);
        Assert.Single(next.Projection.View.Threads); Assert.Single(next.Projection.View.Boards);
        Assert.True(next.IsPaused);
    }
    [Fact]
    public async Task CacheWriteFailureCannotPreventAuthoritativeShutdownOrReleaseProfile()
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(), (_, _) => new ObservedStore());
        await host.StartAsync().WaitAsync(Deadline);
        string cache = Path.Combine(profile.Path, "startup-preview.json");
        File.Delete(cache); Directory.CreateDirectory(cache); // deterministic same-path directory refusal
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Stopped, host.State); Assert.True(host.IsProfileReleased);
        Assert.True(Directory.Exists(cache));
        using ProfileLease next = ProfileLease.Acquire(profile.Path);
    }
    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Paused startup and cached text must not issue HTTP requests.");
    }

}

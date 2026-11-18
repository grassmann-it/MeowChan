using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class VideoPlaybackEngineLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public void ProductionMediaDrainLeavesEngineOwnershipToTerminalRelease()
    {
        // Wiring guard: coordinator-only tests cannot catch a native engine disposal
        // accidentally inserted inside its per-medium cleanup callback (27526f0659).
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Desktop", "GalleryVideoPlayer.cs"))) root = root.Parent;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root.FullName, "Desktop", "GalleryVideoPlayer.cs"));
        string Between(string start, string end)
        {
            int first = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(first >= 0, start);
            int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
            Assert.True(last > first, end);
            return source[first..last];
        }
        string drain = Between("private async Task ClearPlaybackAsync", "private void RetainCleanupFailure");
        Assert.DoesNotContain("_engine.Dispose", drain);
        Assert.DoesNotContain("_engine?.Dispose", drain);
        Assert.DoesNotContain("_engine =", drain);
        Assert.Contains("playback.Player.Stop();", drain);
        Assert.Contains("playback.Player.Dispose();", drain);
        Assert.Contains("playback.Surface?.Dispose();", drain);
        Assert.Contains("playback.Stream?.Dispose();", drain);
        Assert.Contains("playback.NativeFile?.Dispose();", drain);
        Assert.Contains("_engine ??=", Between("private async Task ShowCoreAsync", "private bool IsCurrent"));
        Assert.Contains("_engine?.Dispose(); _engine = null;", Between("private async Task ReleaseEngineAsync", "public ValueTask DisposeAsync"));
    }

    [Fact]
    public async Task MediaTransitionsCompleteWithoutEngineJoinButOwnerDisposalStillWaitsForIt()
    {
        TaskCompletionSource engineEntered = Signal(), releaseEngine = Signal();
        int retiredMedia = 0, engineReleases = 0;
        VideoPlaybackLifetime lifetime = new(_ =>
        {
            Interlocked.Increment(ref retiredMedia);
            Assert.Equal(0, engineReleases); // Native media must retire before engine release starts.
            return Task.CompletedTask;
        }, async () =>
        {
            Interlocked.Increment(ref engineReleases);
            engineEntered.TrySetResult();
            await releaseEngine.Task;
        });
        try
        {
            // Video -> image -> video, twice. Each Stop joins its real media drain,
            // but none can be stalled by the intentionally non-returning engine join.
            for (int i = 0; i < 2; i++)
            {
                var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
                await show.PreviousCleanup.WaitAsync(Deadline);
                lifetime.FinishShow(show);
                await lifetime.StopAsync().WaitAsync(Deadline);
                Assert.False(engineEntered.Task.IsCompleted);
                Assert.Equal(i + 1, retiredMedia);
            }
            var last = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
            lifetime.FinishShow(last);
            Task disposing = lifetime.DisposeAsync();
            await engineEntered.Task.WaitAsync(Deadline);
            Assert.Equal(3, retiredMedia);
            Assert.Equal(1, engineReleases);
            Assert.False(disposing.IsCompleted);
            Assert.Null(lifetime.AcceptShow(CancellationToken.None));
            Assert.Same(disposing, lifetime.DisposeAsync());
            releaseEngine.TrySetResult();
            await disposing.WaitAsync(Deadline);
        }
        finally
        {
            releaseEngine.TrySetResult();
            await lifetime.DisposeAsync().WaitAsync(Deadline);
        }
    }
}

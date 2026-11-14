using System.Collections.Concurrent;
using System.Text;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>Static/profile-host coverage for NH-3; no window, GUI loop, LibVLC or native decoder is used.</summary>
public sealed class VideoScrubProfileHostTests
{
    [Fact]
    public void ConfigUsesTheExistingScrubOptionBoundsAndCopiesTheBudget()
    {
        Config config = new();
        Assert.Equal(VideoScrubPreviewOptions.CacheLimitMiBDefault, Config.VideoScrubCacheLimitMiBDefault);
        Assert.Equal(VideoScrubPreviewOptions.CacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMinimum);
        Assert.Equal(VideoScrubPreviewOptions.CacheLimitMiBMaximum, Config.VideoScrubCacheLimitMiBMaximum);
        Assert.Equal(128, config.VideoScrubCacheLimitMiB);

        Config clone = config.Clone();
        clone.VideoScrubCacheLimitMiB = 512;
        Assert.Equal(128, config.VideoScrubCacheLimitMiB);
        Assert.Equal(512, clone.VideoScrubCacheLimitMiB);

        Config copy = new();
        copy.CopyFrom(clone);
        Assert.Equal(512, copy.VideoScrubCacheLimitMiB);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Config invalid = new() { VideoScrubCacheLimitMiB = Config.VideoScrubCacheLimitMiBMinimum - 1 };
            invalid.Validate();
        });
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(128, 128)]
    [InlineData(99999, 4096)]
    public void LoadAndHostOptionProjectionClampOnlyTheNewBudget(int stored, int expected)
    {
        using TempDirectory profile = new();
        string settings = "{\"SchemaVersion\":1,\"VideoScrubCacheLimitMiB\":" + stored + "}";
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), settings, Encoding.UTF8);

        Config loaded = Config.LoadConfig(profile.Path);
        Assert.Equal(expected, loaded.VideoScrubCacheLimitMiB);
        Assert.Equal(expected, GalleryScrubPreviewHost.OptionsFor(loaded).CacheLimitMiB);
    }

    [Fact]
    public async Task EachProfileGetsOnlyItsOwnScrubDirectoryAndNoHomeFallback()
    {
        using TempDirectory firstProfile = new();
        using TempDirectory secondProfile = new();
        await using GalleryScrubPreviewHost first = new(firstProfile.Path);
        await using GalleryScrubPreviewHost second = new(secondProfile.Path);

        Assert.Equal(Path.Combine(Path.GetFullPath(firstProfile.Path), "cache", "video-scrub-v1"), first.CacheDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(secondProfile.Path), "cache", "video-scrub-v1"), second.CacheDirectory);
        Assert.NotEqual(first.CacheDirectory, second.CacheDirectory);
        Assert.DoesNotContain(Path.Combine(".oChan", "cache"), first.CacheDirectory, StringComparison.Ordinal);
        Assert.False(Directory.Exists(first.CacheDirectory)); // construction does not synchronously scan/create on the UI thread
    }

    [Fact]
    public async Task NewerConfigurationWinsAfterAnOlderBackgroundTrimAndCloseWaitsForOwnership()
    {
        using TempDirectory profile = new();
        VideoScrubPreviewCache cache = new(Path.Combine(profile.Path, "cache", "video-scrub-v1"), VideoScrubPreviewOptions.Default);
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<int> applied = new();
        Func<VideoScrubPreviewOptions, CancellationToken, Task> fakeConfigure = async (options, _) =>
        {
            applied.Enqueue(options.CacheLimitMiB);
            if (options.CacheLimitMiB == 8)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        };
        await using GalleryScrubPreviewHost host = new(cache, fakeConfigure);

        Task first = host.ConfigureAsync(new VideoScrubPreviewOptions(8));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = host.ConfigureAsync(new VideoScrubPreviewOptions(4096));
        releaseFirst.TrySetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(new[] { 8, 4096 }, applied.ToArray());
        await host.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.ConfigureAsync(new VideoScrubPreviewOptions(128)));
    }

    [Fact]
    public async Task ClosingCancelsSupersededWorkButWaitsForTheRetiringOwner()
    {
        using TempDirectory profile = new();
        VideoScrubPreviewCache cache = new(Path.Combine(profile.Path, "cache", "video-scrub-v1"), VideoScrubPreviewOptions.Default);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<int> applied = new();
        Func<VideoScrubPreviewOptions, CancellationToken, Task> fakeConfigure = async (options, _) =>
        {
            applied.Enqueue(options.CacheLimitMiB);
            started.TrySetResult();
            await release.Task;
        };
        await using GalleryScrubPreviewHost host = new(cache, fakeConfigure);

        Task work = host.ConfigureAsync(new VideoScrubPreviewOptions(128));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task close = host.DisposeAsync().AsTask();
        Assert.False(close.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(work, close);

        Assert.Equal(new[] { 128 }, applied.ToArray());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.ConfigureAsync(new VideoScrubPreviewOptions(128)));
    }
}

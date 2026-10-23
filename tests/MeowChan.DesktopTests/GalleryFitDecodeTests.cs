using Avalonia;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class GalleryFitDecodeTests
{
    [Theory]
    [InlineData(10001, 7999, 30000, 30000)]
    [InlineData(7999, 10001, 30000, 30000)]
    [InlineData(1, 80000000, 100000000, 100000000)]
    [InlineData(80000000, 1, 100000000, 100000000)]
    [InlineData(17, 11, 30000, 30000)]
    [InlineData(10001, 7999, 1, 1)]
    public void RoundedTargetsStayInsideSourceAndPixelBudget(int width, int height, double viewportWidth, double viewportHeight)
    {
        GalleryImageInfo info = new(width, height, "PNG", false);
        PixelSize target = GalleryFitTarget.Calculate(info, new(viewportWidth, viewportHeight));
        Assert.InRange(target.Width, 1, width);
        Assert.InRange(target.Height, 1, height);
        Assert.True((long)target.Width * target.Height <= GalleryFitTarget.MaximumPixels);
        // Rounding on the short axis stays within one pixel of source aspect.
        if (width >= height) Assert.InRange(target.Height - (double)target.Width * height / width, 0, 1);
        else Assert.InRange(target.Width - (double)target.Height * width / height, 0, 1);
        Assert.Equal(target, GalleryFitTarget.Calculate(info, new(target.Width, target.Height)));
    }

    [Theory]
    [InlineData(1, 1000, 625)]
    [InlineData(1.5, 1500, 937)]
    [InlineData(2, 2000, 1250)]
    public void PhysicalViewportDeterminesFitBuffer(double scale, int expectedWidth, int expectedHeight)
    {
        PixelSize target = GalleryFitTarget.Calculate(new(8000, 5000, "PNG", false), new(1000 * scale, 700 * scale));
        Assert.Equal(expectedWidth, target.Width);
        Assert.InRange(target.Height, expectedHeight, expectedHeight + 1);
    }

    [Fact]
    public async Task LatestDesiredDecodeWaitsForCanceledCodecToActuallyExit()
    {
        GalleryDecodeQueue queue = new();
        TaskCompletionSource entered = new(), release = new();
        int active = 0, maximumActive = 0, secondRuns = 0, finalRuns = 0;
        CancellationToken obsolete = default;
        Task first = queue.Replace(async token =>
        {
            obsolete = token;
            maximumActive = Math.Max(maximumActive, ++active);
            entered.SetResult();
            await release.Task; // Models a native codec ignoring cancellation.
            active--;
        }, debounce: false);
        await entered.Task;
        Task second = queue.Replace(_ => { secondRuns++; return Task.CompletedTask; }, debounce: false);
        Task final = queue.Replace(_ =>
        {
            maximumActive = Math.Max(maximumActive, ++active);
            finalRuns++; active--;
            return Task.CompletedTask;
        }, debounce: false);
        Assert.True(obsolete.IsCancellationRequested);
        Assert.Equal(0, finalRuns);
        release.SetResult();
        await Task.WhenAll(first, second, final).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, secondRuns);
        Assert.Equal(1, finalRuns);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task NewViewportRestartsTrailingDelayAndOnlyLatestDecodes()
    {
        List<TaskCompletionSource> delays = new();
        TaskCompletionSource replacementDelay = new();
        GalleryDecodeQueue queue = new(token =>
        {
            TaskCompletionSource delay = new();
            delays.Add(delay);
            if (delays.Count == 2) replacementDelay.TrySetResult();
            token.Register(() => delay.TrySetCanceled(token));
            return delay.Task;
        });
        int decoded = 0;
        Task first = queue.Replace(_ => { decoded++; return Task.CompletedTask; }, debounce: true);
        Assert.Single(delays);
        Task second = queue.Replace(_ => { decoded++; return Task.CompletedTask; }, debounce: true);
        // Let the canceled delay exit and the replacement begin without timing sleeps.
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await replacementDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, delays.Count);
        Assert.Equal(0, decoded);
        delays[1].SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, decoded);
    }

    [Fact]
    public async Task OriginalCanReplacePendingFitWithoutWaitingForDebounce()
    {
        GalleryDecodeQueue queue = new(token => Task.Delay(Timeout.Infinite, token));
        int fitRuns = 0, originalRuns = 0;
        Task fit = queue.Replace(_ => { fitRuns++; return Task.CompletedTask; }, debounce: true);
        Task original = queue.Replace(_ => { originalRuns++; return Task.CompletedTask; }, debounce: false);
        await Task.WhenAll(fit, original).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fitRuns);
        Assert.Equal(1, originalRuns);
    }
}

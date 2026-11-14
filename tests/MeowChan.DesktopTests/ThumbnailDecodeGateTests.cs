using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ThumbnailDecodeGateTests
{
    private sealed class Preview : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }

    private static async Task Occupy(ThumbnailDecodeGate gate, int slots, List<TaskCompletionSource> releases, List<Task> running)
    {
        for (int index = 0; index < slots; index++)
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releases.Add(release);
            running.Add(gate.RunAsync(ThumbnailDecodePriority.Image, async () => { entered.SetResult(); await release.Task; return 0; }, CancellationToken.None));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task AKeptPreviewIsLeasedWhileEverySlotIsBusyAndWithoutRunningADecode()
    {
        ThumbnailDecodeGate gate = new(2);
        List<TaskCompletionSource> releases = new(); List<Task> running = new();
        await Occupy(gate, 2, releases, running);
        MediaThumbnailCache<Preview> cache = new(4);
        Preview kept = cache.Add("bytes", new Preview());
        cache.Release(kept);
        int decodes = 0;
        Task<Preview?> hit = MediaTileThumbnails.KeepGatedAsync(cache, gate, ThumbnailDecodePriority.Image, "bytes",
            () => { decodes++; return Task.FromResult<Preview?>(new()); }, CancellationToken.None);
        Assert.True(hit.IsCompletedSuccessfully); // synchronous: no slot, no thread hop
        Assert.Same(kept, hit.Result);
        Assert.Equal(0, decodes);
        Assert.Equal(0, gate.Free);
        cache.Release(hit.Result!);
        foreach (TaskCompletionSource release in releases) release.SetResult();
        await Task.WhenAll(running);
        Assert.Equal(2, gate.Free);
    }

    [Fact]
    public async Task AMissWaitsForASlotDecodesOnceAndKeepsTheResult()
    {
        ThumbnailDecodeGate gate = new(1);
        List<TaskCompletionSource> releases = new(); List<Task> running = new();
        await Occupy(gate, 1, releases, running);
        MediaThumbnailCache<Preview> cache = new(4);
        int decodes = 0;
        Task<Preview?> miss = MediaTileThumbnails.KeepGatedAsync(cache, gate, ThumbnailDecodePriority.Image, "bytes",
            () => { decodes++; return Task.FromResult<Preview?>(new()); }, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(miss.IsCompleted);
        Assert.Equal(0, decodes);
        releases[0].SetResult();
        Preview shown = (await miss.WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.Equal(1, decodes);
        Assert.Same(shown, cache.TryAcquire("bytes"));
        Assert.Equal(1, gate.Free);
    }

    [Fact]
    public async Task APreviewStoredWhileQueuedIsLeasedInsteadOfDecodedAgain()
    {
        ThumbnailDecodeGate gate = new(1);
        List<TaskCompletionSource> releases = new(); List<Task> running = new();
        await Occupy(gate, 1, releases, running);
        MediaThumbnailCache<Preview> cache = new(4);
        int decodes = 0;
        Task<Preview?> queued = MediaTileThumbnails.KeepGatedAsync(cache, gate, ThumbnailDecodePriority.Image, "bytes",
            () => { decodes++; return Task.FromResult<Preview?>(new()); }, CancellationToken.None);
        Preview other = cache.Add("bytes", new Preview()); // another view finished the same bytes meanwhile
        releases[0].SetResult();
        Preview shown = (await queued.WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.Same(other, shown);
        Assert.Equal(0, decodes);
    }

    [Fact]
    public async Task QueuedImagesPassQueuedPostersButNeverStarveThem()
    {
        ThumbnailDecodeGate gate = new(1);
        List<TaskCompletionSource> releases = new(); List<Task> running = new();
        await Occupy(gate, 1, releases, running);
        List<string> order = new();
        Task Queue(ThumbnailDecodePriority priority, string name) =>
            gate.RunAsync(priority, () => { lock (order) order.Add(name); return Task.FromResult(0); }, CancellationToken.None);
        List<Task> queued = new() { Queue(ThumbnailDecodePriority.Poster, "poster") };
        for (int index = 0; index < ThumbnailDecodeGate.MaximumConsecutiveImages + 3; index++)
            queued.Add(Queue(ThumbnailDecodePriority.Image, "image" + index));
        releases[0].SetResult();
        await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("image0", order[0]); // an image overtakes the poster queued before it
        Assert.Equal("poster", order[ThumbnailDecodeGate.MaximumConsecutiveImages]); // after N images in a row the poster goes next
        Assert.Equal(ThumbnailDecodeGate.MaximumConsecutiveImages + 4, order.Count);
        Assert.Equal(1, gate.Free);
    }

    [Fact]
    public async Task ACancelledWaiterLeavesTheQueueAndNeverHoldsASlot()
    {
        ThumbnailDecodeGate gate = new(1);
        List<TaskCompletionSource> releases = new(); List<Task> running = new();
        await Occupy(gate, 1, releases, running);
        using CancellationTokenSource stop = new();
        bool ran = false;
        Task<int> cancelled = gate.RunAsync(ThumbnailDecodePriority.Poster, () => { ran = true; return Task.FromResult(0); }, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Task<int> next = gate.RunAsync(ThumbnailDecodePriority.Image, () => Task.FromResult(7), CancellationToken.None);
        releases[0].SetResult();
        Assert.Equal(7, await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(ran);
        Assert.Equal(1, gate.Free);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.RunAsync(ThumbnailDecodePriority.Image, () => Task.FromResult(0), stop.Token));
        Assert.Equal(1, gate.Free);
    }

    [Fact]
    public async Task AFailingDecodeGivesItsSlotBack()
    {
        ThumbnailDecodeGate gate = new(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync<int>(ThumbnailDecodePriority.Image, () => throw new InvalidOperationException(), CancellationToken.None));
        Assert.Equal(1, gate.Free);
    }

    [Fact]
    public async Task APosterDecodeTakesASlotOnlyAfterTheIdentityCheckAndAWarmPosterTakesNone()
    {
        ThumbnailDecodeGate gate = new(1);
        List<TaskCompletionSource> releases = new(); List<Task> running = new();
        await Occupy(gate, 1, releases, running);
        MediaThumbnailCache<Preview> cache = new(4);
        Preview kept = cache.Add("poster", new Preview());
        cache.Release(kept);
        int verifications = 0, decodes = 0;
        Task<Preview?> Load() => MediaTileThumbnails.KeepVerifiedPosterAsync(cache, "poster",
            _ => { verifications++; return Task.FromResult<byte[]?>(new byte[] { 1 }); },
            (_, _) => { decodes++; return Task.FromResult<Preview?>(new()); }, CancellationToken.None, gate);
        Preview warm = (await Load().WaitAsync(TimeSpan.FromSeconds(10)))!; // all slots busy, memory hit still returns
        Assert.Same(kept, warm);
        Assert.Equal(1, verifications); // the original is still checked first
        Assert.Equal(0, decodes);
        cache.Release(warm);
        releases[0].SetResult();
        await Task.WhenAll(running);
    }

    // R21 (review 14.11. night): the archive's decode parallelism is a named, documented technical limit, not a hidden literal.
    [Fact]
    public void TheArchivePageUsesTheDocumentedDecodeSlots()
    {
        Assert.Equal(2, ThumbnailDecodeGate.ArchiveSlots);
        Assert.Equal(ThumbnailDecodeGate.ArchiveSlots, new ThumbnailDecodeGate(ThumbnailDecodeGate.ArchiveSlots).Free);
        string page = File.ReadAllText(SourcePath("Desktop/Views/Archive/ArchivePage.axaml.cs"));
        Assert.Contains("new ThumbnailDecodeGate(ThumbnailDecodeGate.ArchiveSlots)", page);
        Assert.DoesNotMatch(@"new ThumbnailDecodeGate\(\d", page);
    }

    private static string SourcePath(string relativePath, [System.Runtime.CompilerServices.CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "../..", relativePath));
}

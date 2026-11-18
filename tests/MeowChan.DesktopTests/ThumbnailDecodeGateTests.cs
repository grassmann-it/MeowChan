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

    // M2 (review 14.11. night): speculative preloads never get a slot before a visible preview.
    [Fact]
    public async Task PrefetchNeverStartsWhileAnImageOrPosterWaits()
    {
        ThumbnailDecodeGate gate = new(1);
        List<TaskCompletionSource> releases = new(); List<Task> running = new();
        await Occupy(gate, 1, releases, running);
        List<string> order = new();
        Task Queue(ThumbnailDecodePriority priority, string name) =>
            gate.RunAsync(priority, () => { lock (order) order.Add(name); return Task.FromResult(0); }, CancellationToken.None);
        List<Task> queued = new();
        for (int index = 0; index < 50; index++) queued.Add(Queue(ThumbnailDecodePriority.Prefetch, "prefetch" + index)); // queued first
        queued.Add(Queue(ThumbnailDecodePriority.Poster, "poster")); // a visible video poster behind 50 waiting prefetches
        queued.Add(Queue(ThumbnailDecodePriority.Image, "image"));
        releases[0].SetResult();
        await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "image", "poster" }, order.Take(2));
        Assert.Equal(Enumerable.Range(0, 50).Select(index => "prefetch" + index), order.Skip(2)); // then FIFO among themselves
        Assert.Equal(1, gate.Free);
    }

    [Fact]
    public async Task PrefetchGetsNoAntiStarvationBoostFromTheImageStreak()
    {
        ThumbnailDecodeGate gate = new(1);
        List<TaskCompletionSource> releases = new(); List<Task> running = new();
        await Occupy(gate, 1, releases, running);
        List<string> order = new();
        Task Queue(ThumbnailDecodePriority priority, string name) =>
            gate.RunAsync(priority, () => { lock (order) order.Add(name); return Task.FromResult(0); }, CancellationToken.None);
        List<Task> queued = new() { Queue(ThumbnailDecodePriority.Prefetch, "prefetch") };
        int images = ThumbnailDecodeGate.MaximumConsecutiveImages * 3;
        for (int index = 0; index < images; index++) queued.Add(Queue(ThumbnailDecodePriority.Image, "image" + index));
        releases[0].SetResult();
        await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("prefetch", order[^1]); // passed by every image, however long the streak
        Assert.Equal(images + 1, order.Count);
    }

    [Fact]
    public async Task ARunningPrefetchIsNotInterruptedButAVisibleImageIsNextInLine()
    {
        ThumbnailDecodeGate gate = new(1);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = gate.RunAsync(ThumbnailDecodePriority.Prefetch, async () => { entered.SetResult(); await release.Task; return 0; }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        List<string> order = new();
        Task waitingPrefetch = gate.RunAsync(ThumbnailDecodePriority.Prefetch, () => { lock (order) order.Add("prefetch"); return Task.FromResult(0); }, CancellationToken.None);
        Task image = gate.RunAsync(ThumbnailDecodePriority.Image, () => { lock (order) order.Add("image"); return Task.FromResult(0); }, CancellationToken.None);
        Assert.Empty(order);
        release.SetResult();
        await Task.WhenAll(running, waitingPrefetch, image).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "image", "prefetch" }, order);
    }

    [Fact]
    public async Task CancellingPrefetchesFreesTheirQueueEntriesAndSlots()
    {
        ThumbnailDecodeGate gate = new(1);
        // One prefetch holds the slot and observes its cancellation; three more wait behind it.
        using CancellationTokenSource stop = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> holder = gate.RunAsync(ThumbnailDecodePriority.Prefetch, async () =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, stop.Token);
            return 0;
        }, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int ran = 0;
        List<Task<int>> waiting = new();
        for (int index = 0; index < 3; index++)
            waiting.Add(gate.RunAsync(ThumbnailDecodePriority.Prefetch, () => { Interlocked.Increment(ref ran); return Task.FromResult(0); }, stop.Token));
        Task<int> survivor = gate.RunAsync(ThumbnailDecodePriority.Prefetch, () => Task.FromResult(5), CancellationToken.None);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holder);
        foreach (Task<int> task in waiting) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(5, await survivor.WaitAsync(TimeSpan.FromSeconds(10))); // the slot went on to the one that was not cancelled
        Assert.Equal(0, ran);
        Assert.Equal(1, gate.Free);
    }

    [Fact]
    public void TheWaitingListHandsOutAtMostTheInFlightCapAndFeedsAsJobsFinish()
    {
        Assert.InRange(ReaderPreviewReservation.PrefetchInFlight, 2, 4);
        Assert.True(ReaderPreviewReservation.PrefetchInFlight > ThumbnailDecodeGate.ArchiveSlots - 1);
        ReaderPreviewPrefetchQueue<int> queue = new();
        queue.Replace(Enumerable.Range(0, 192));
        List<int> started = new();
        void Feed() { while (queue.TryTake(started.Count, out int next)) started.Add(next); }
        Feed();
        Assert.Equal(Enumerable.Range(0, ReaderPreviewReservation.PrefetchInFlight), started);
        Assert.Equal(192 - ReaderPreviewReservation.PrefetchInFlight, queue.Waiting); // the rest waits here, not in the gate
        Feed();
        Assert.Equal(ReaderPreviewReservation.PrefetchInFlight, started.Count); // nothing more while the cap is reached
        started.RemoveAt(0); // one finished
        Feed();
        Assert.Equal(ReaderPreviewReservation.PrefetchInFlight, started.Count);
        Assert.Equal(ReaderPreviewReservation.PrefetchInFlight, started[^1]); // the next in plan order took its place
        queue.Replace(new[] { 100, 101 }); // a new plan after scrolling replaces what was waiting
        started.Clear();
        Feed();
        Assert.Equal(new[] { 100, 101 }, started);
        queue.Clear();
        Assert.Equal(0, queue.Waiting);
        Assert.False(queue.TryTake(0, out _));
    }

    [Fact]
    public async Task APreloadWindowThroughTheGateNeverHasMoreThanTheCapInFlightAndAVisiblePosterGoesBeforeTheRest()
    {
        ThumbnailDecodeGate gate = new(ThumbnailDecodeGate.ArchiveSlots);
        ReaderPreviewPrefetchQueue<int> queue = new();
        queue.Replace(Enumerable.Range(0, 60));
        int inFlight = 0, maxInFlight = 0, finished = 0;
        List<string> startOrder = new();
        List<Task> jobs = new();
        object sync = new();
        int startedWhenPosterWaited;
        void Feed()
        {
            while (true)
            {
                int next;
                lock (sync) { if (!queue.TryTake(inFlight, out next)) return; inFlight++; maxInFlight = Math.Max(maxInFlight, inFlight); }
                Task job = Task.Run(async () =>
                {
                    await gate.RunAsync(ThumbnailDecodePriority.Prefetch, async () =>
                    {
                        lock (sync) startOrder.Add("p" + next);
                        await Task.Delay(5);
                        return 0;
                    }, CancellationToken.None);
                    lock (sync) { inFlight--; finished++; }
                    Feed();
                });
                lock (sync) jobs.Add(job);
            }
        }
        Feed();
        // A visible poster arrives while the window is being preloaded.
        await Task.Delay(20);
        lock (sync) startedWhenPosterWaited = startOrder.Count;
        Task poster = gate.RunAsync(ThumbnailDecodePriority.Poster, () =>
        {
            lock (sync) startOrder.Add("poster");
            return Task.FromResult(0);
        }, CancellationToken.None);
        await poster.WaitAsync(TimeSpan.FromSeconds(10));
        SpinWait.SpinUntil(() => { lock (sync) return finished == 60; }, TimeSpan.FromSeconds(20));
        Task[] snapshot; lock (sync) snapshot = jobs.ToArray();
        await Task.WhenAll(snapshot).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(60, finished);
        Assert.True(maxInFlight <= ReaderPreviewReservation.PrefetchInFlight, $"max in flight {maxInFlight}");
        // The poster passes the preload waiting in the gate and the whole waiting list: at most the slots' worth of preloads started in
        // the moment between taking the count and queueing (a running one finishing) before it.
        Assert.InRange(startOrder.IndexOf("poster"), startedWhenPosterWaited, startedWhenPosterWaited + ThumbnailDecodeGate.ArchiveSlots);
        Assert.Equal(ThumbnailDecodeGate.ArchiveSlots, gate.Free);
    }

    [Fact]
    public void TheReaderPreloadsWithThePrefetchPriorityNotThePosterOne()
    {
        string page = File.ReadAllText(SourcePath("Desktop/Views/Archive/ArchivePage.axaml.cs"));
        Assert.Contains("priority: ThumbnailDecodePriority.Prefetch", page);
        Assert.DoesNotContain("priority: ThumbnailDecodePriority.Poster", page);
        string reservation = File.ReadAllText(SourcePath("Desktop/Views/Archive/ArchivePage.PreviewReservation.cs"));
        Assert.Contains("_previewPrefetchQueue.TryTake(_previewPrefetches.Count", reservation);
        Assert.DoesNotContain("StartPreviewPrefetch(row, index)", reservation); // a planning pass only fills the waiting list
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

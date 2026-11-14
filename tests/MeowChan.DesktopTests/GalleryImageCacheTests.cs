using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

public sealed class GalleryImageCacheTests
{
    private sealed class Img(int width, int height) : IDisposable
    {
        internal PixelSize Size { get; } = new(width, height);
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static GalleryImageCache<Img> Cache(long budget) => new(image => image.Size, budget);
    private static GalleryImageInfo Info(int width = 1000, int height = 1000) => new(width, height, "Png", false);
    private static GalleryDecodeRequest.Fit Fit(double width = 100, double height = 100) => new(width, height);
    // A 100x100 image is 40000 bytes.
    private const long Hundred = 100 * 100 * 4;

    [Fact]
    public void TheLeastRecentlyUsedImagesAreDroppedAtTheMemoryBudgetAndDisposed()
    {
        GalleryImageCache<Img> cache = Cache(Hundred * 2);
        Img a = new(100, 100), b = new(100, 100), c = new(100, 100);
        cache.Release(cache.Add("a", a, Info()));
        cache.Release(cache.Add("b", b, Info()));
        Assert.True(cache.TryAcquire("a", Fit(), out Img? again, out _)); // a is used again, b is the oldest
        Assert.Same(a, again);
        cache.Release(again);
        cache.Release(cache.Add("c", c, Info()));
        Assert.Equal(2, cache.Count);
        Assert.Equal(Hundred * 2, cache.Bytes);
        Assert.True(b.Disposed);
        Assert.False(a.Disposed);
        Assert.False(cache.TryAcquire("b", Fit(), out _, out _));
    }

    [Fact]
    public void AnImageTheViewerStillShowsIsNeverDisposedWhenItIsPushedOut()
    {
        GalleryImageCache<Img> cache = Cache(Hundred);
        Img shown = cache.Add("a", new Img(100, 100), Info()); // leased by the viewer
        cache.Release(cache.Add("b", new Img(100, 100), Info()));
        Assert.False(shown.Disposed); // pushed out of the set, but still on screen
        Assert.Equal(Hundred, cache.Bytes); // and not counted any more
        cache.Release(shown);
        Assert.True(shown.Disposed); // the last lease let go of it
        cache.BudgetBytes = 0;
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ALowerBudgetDropsImagesAtOnceAndZeroKeepsNothing()
    {
        GalleryImageCache<Img> cache = Cache(Hundred * 3);
        Img[] images = { new(100, 100), new(100, 100), new(100, 100) };
        for (int i = 0; i < 3; i++) cache.Release(cache.Add("k" + i, images[i], Info()));
        cache.BudgetBytes = Hundred;
        Assert.Equal(1, cache.Count);
        Assert.True(images[0].Disposed && images[1].Disposed);
        Assert.False(images[2].Disposed);
        cache.BudgetBytes = 0;
        Assert.True(images[2].Disposed);
        Img none = new(10, 10);
        Img shown = cache.Add("z", none, Info());
        Assert.Same(none, shown); // nothing is kept: the caller shows its own bitmap
        Assert.Equal(0, cache.Count);
        cache.Release(shown);
        Assert.True(none.Disposed);
    }

    [Fact]
    public void AnImageLargerThanTheWholeBudgetIsNotKept()
    {
        GalleryImageCache<Img> cache = Cache(Hundred);
        cache.Release(cache.Add("small", new Img(100, 100), Info()));
        Img big = new(200, 200);
        Assert.Same(big, cache.Add("big", big, Info()));
        Assert.Equal(1, cache.Count); // the small one stays: a hopeless candidate does not push anything out
        cache.Release(big);
        Assert.True(big.Disposed);
    }

    [Fact]
    public void ADuplicateAddSharesTheKeptImageAndALargerDecodeReplacesASmallerOne()
    {
        GalleryImageCache<Img> cache = Cache(Hundred * 10);
        Img first = cache.Add("k", new Img(100, 100), Info()), smaller = new(50, 50);
        Assert.Same(first, cache.Add("k", smaller, Info()));
        Assert.True(smaller.Disposed);
        Assert.Equal(Hundred, cache.Bytes);
        Img larger = cache.Add("k", new Img(200, 200), Info());
        Assert.NotSame(first, larger);
        Assert.Equal(Hundred * 4, cache.Bytes);
        Assert.Equal(1, cache.Count);
        Assert.False(first.Disposed); // two leases on it (Add twice)
        cache.Release(first);
        Assert.False(first.Disposed);
        cache.Release(first);
        Assert.True(first.Disposed);
    }

    [Fact]
    public void AKeptImageIsAHitOnlyWhenItCoversTheTargetOfTheRequest()
    {
        GalleryImageCache<Img> cache = Cache(Hundred * 10);
        cache.Release(cache.Add("k", new Img(100, 100), Info())); // decoded for a 100x100 viewport of a 1000x1000 image
        Assert.True(cache.TryAcquire("k", Fit(80, 80), out Img? smaller, out GalleryImageInfo? info));
        cache.Release(smaller);
        Assert.Equal(1000, info.Width);
        Assert.False(cache.TryAcquire("k", Fit(300, 300), out _, out _)); // the window grew: decode again
        Assert.Equal(Hundred, cache.Touch("k", Fit(100, 100)));
        Assert.Equal(-1, cache.Touch("k", Fit(300, 300)));
        Assert.Equal(-1, cache.Touch("missing", Fit()));
    }

    [Fact]
    public void ReleasingABitmapTheSetDoesNotKnowDisposesIt()
    {
        GalleryImageCache<Img> cache = Cache(Hundred);
        Img own = new(5, 5);
        cache.Release(own);
        Assert.True(own.Disposed);
    }

    private static readonly GalleryDecodeRequest.Original OriginalRequest = new();

    [Fact]
    public async Task DisplaySizeDecodesAreKeptAndAskedForOnlyOnceWhileTheOriginalIsNeverKept()
    {
        using TempDirectory temp = new();
        GalleryEntry entry = (await EntriesAsync(temp.Path, "a.png"))[0];
        GalleryImageCache<Img> cache = Cache(Hundred * 10);
        int calls = 0;
        Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry _, GalleryDecodeRequest request, CancellationToken token)
        { calls++; return Task.FromResult((new Img(100, 100), Info())); }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit(), default);
        (Img second, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit(), default);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        (Img original, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, OriginalRequest, default);
        Assert.NotSame(first, original);
        Assert.Equal(2, calls);
        Assert.Equal(1, cache.Count);
        cache.Release(original); // not kept: disposed with its viewer
        Assert.True(original.Disposed);
        cache.Release(first); cache.Release(second);
        Assert.False(first.Disposed);
    }

    [Fact]
    public void AnImageWhoseFileChangedIsDroppedAtTheNextUseAndNeverShown()
    {
        GalleryImageCache<Img> cache = Cache(Hundred * 10);
        bool unchanged = true;
        Img old = new(100, 100);
        cache.Release(cache.Add("k", old, Info(), () => unchanged));
        Assert.True(cache.TryAcquire("k", Fit(), out Img? hit, out _));
        cache.Release(hit);
        unchanged = false;
        Assert.Equal(-1, cache.Touch("k", Fit()));
        Assert.True(old.Disposed); // nobody showed it: gone at once
        Assert.False(cache.TryAcquire("k", Fit(), out _, out _));
        Assert.Equal(0, cache.Count);
        // A fresh decode replaces a kept image whose file changed since, instead of sharing it.
        unchanged = true;
        Img first = cache.Add("j", new Img(100, 100), Info(), () => unchanged);
        unchanged = false;
        Img fresh = cache.Add("j", new Img(100, 100), Info(), () => true);
        Assert.NotSame(first, fresh);
        Assert.False(first.Disposed); // still leased by its viewer
        cache.Release(first);
        Assert.True(first.Disposed);
        Assert.True(cache.TryAcquire("j", Fit(), out Img? again, out _));
        Assert.Same(fresh, again);
    }

    [Fact]
    public async Task AFileThatChangedAfterItWasVerifiedIsDecodedAndVerifiedAgainNotShownFromTheCache()
    {
        using TempDirectory temp = new();
        GalleryEntry entry = (await EntriesAsync(temp.Path, "a.png"))[0];
        GalleryImageCache<Img> cache = Cache(Hundred * 10);
        int calls = 0;
        Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry _, GalleryDecodeRequest __, CancellationToken ___)
        { calls++; return Task.FromResult((new Img(100, 100), Info())); }
        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit(), default);
        cache.Release(first);
        (Img again, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit(), default);
        cache.Release(again);
        Assert.Equal(1, calls); // unchanged file: a hit
        await File.WriteAllBytesAsync(entry.Asset.Path, "changed"u8.ToArray());
        (Img third, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit(), default);
        Assert.Equal(2, calls); // the decode path (which verifies the bytes) runs again
        Assert.NotSame(first, third);
        // Same length, later time: also not trusted.
        byte[] same = new byte[entry.Asset.Length];
        File.SetLastWriteTimeUtc(entry.Asset.Path, DateTime.UtcNow.AddHours(-3));
        Assert.Null(GalleryImageCache.Validity(entry)); // length differs from the manifest: nothing is kept for it
        File.Delete(entry.Asset.Path);
        Assert.Null(GalleryImageCache.Validity(entry));
    }

    [Fact]
    public async Task EqualBytesInAnotherFolderShareTheKeptImage()
    {
        using TempDirectory temp = new();
        GalleryEntry one = (await EntriesAsync(Path.Combine(temp.Path, "x"), "a.png"))[0];
        GalleryEntry two = (await EntriesAsync(Path.Combine(temp.Path, "y"), "a.png"))[0];
        Assert.NotEqual(one.Key, two.Key);
        Assert.Equal(GalleryImageCache.KeyOf(one), GalleryImageCache.KeyOf(two));
    }

    [Fact]
    public async Task APreloadWindowCopiesOnlyItsNeighborsAndKeepsTheSourceIsolated()
    {
        using TempDirectory temp = new();
        List<GalleryEntry> live = (await EntriesAsync(temp.Path, "a.png", "b.png", "c.png", "d.png", "e.png", "f.png", "g.png", "h.png")).ToList();
        GalleryPreloadWindow window = GalleryPreloadWindow.Capture(live, index: 4, ahead: 2, behind: 1);

        live.Clear();

        Assert.Equal(4, window.Entries.Count);
        Assert.Equal(new[] { "d.png", "e.png", "f.png", "g.png" },
            window.Entries.Select(entry => Path.GetFileName(entry.Asset.Path)));
        Assert.Equal(1, window.Index);
        Assert.Throws<NotSupportedException>(() => ((IList<GalleryEntry>)window.Entries).Clear());

        GalleryPreloadWindow first = GalleryPreloadWindow.Capture(window.Entries, 0, ahead: 2, behind: 3);
        GalleryPreloadWindow last = GalleryPreloadWindow.Capture(window.Entries, window.Entries.Count - 1, ahead: 3, behind: 2);
        Assert.Equal(new[] { "d.png", "e.png", "f.png" }, first.Entries.Select(entry => Path.GetFileName(entry.Asset.Path)));
        Assert.Equal(new[] { "e.png", "f.png", "g.png" }, last.Entries.Select(entry => Path.GetFileName(entry.Asset.Path)));
    }

    [Fact]
    public async Task PreloaderRunsFromTheBoundedSnapshotAfterTheLiveListChanges()
    {
        using TempDirectory temp = new();
        List<GalleryEntry> live = (await EntriesAsync(temp.Path, "a.png", "b.png", "c.png", "d.png", "e.png", "f.png")).ToList();
        GalleryPreloadWindow window = GalleryPreloadWindow.Capture(live, index: 3, ahead: 2, behind: 1);
        live.Clear();
        List<string> decoded = new();
        GalleryPreloader<Img> preloader = new(Cache(Hundred * 20), (entry, _, _) =>
        {
            decoded.Add(Path.GetFileName(entry.Asset.Path));
            return Task.FromResult((new Img(100, 100), Info()));
        }, TimeSpan.Zero);

        await preloader.Schedule(window, Fit());

        Assert.Equal(new[] { "e.png", "c.png", "f.png" }, decoded);
    }

    [Fact]
    public void PreloadingGoesOutwardFromTheShownImageAndAlternatesTheDirections()
    {
        Assert.Equal(new[] { 6, 4, 7, 3, 8 }, GalleryPreload.Plan(10, 5, 3, 2));
        Assert.Equal(new[] { 1, 2 }, GalleryPreload.Plan(10, 0, 2, 2));
        Assert.Equal(new[] { 1, 0 }, GalleryPreload.Plan(3, 2, 3, 3));
        Assert.Equal(new[] { 7, 8, 9 }, GalleryPreload.Plan(10, 6, 3, 0));
        Assert.Empty(GalleryPreload.Plan(10, 5, 0, 0));
        Assert.Equal(new[] { 4, 3 }, GalleryPreload.Plan(10, 5, 0, 2));
    }

    [Fact]
    public async Task OnlyStillImagesArePreloadedNeverVideosAnimationsOtherFilesOrHugeOnes()
    {
        using TempDirectory temp = new();
        IReadOnlyList<GalleryEntry> entries = await EntriesAsync(temp.Path, "a.png", "b.JPG", "c.gif", "d.webm", "e.pdf", "f.webp");
        Assert.Equal(new[] { true, true, false, false, false, true }, entries.Select(GalleryPreload.Eligible));
        Assert.False(GalleryPreload.Eligible(entries[0] with { Asset = entries[0].Asset with { Length = GalleryImageDecoder.MaximumBytes + 1 } }));
    }

    [Fact]
    public async Task ThePreloaderDecodesTheNeighborsNearestFirstSkipsWhatItMustAndKeepsWhatItDecodes()
    {
        using TempDirectory temp = new();
        IReadOnlyList<GalleryEntry> entries = await EntriesAsync(temp.Path, "a.png", "b.png", "c.gif", "d.png", "e.png", "f.webm", "g.png", "h.png");
        GalleryImageCache<Img> cache = Cache(Hundred * 20);
        List<string> decoded = new();
        GalleryPreloader<Img> preloader = new(cache, (entry, request, token) =>
        {
            decoded.Add(Path.GetFileName(entry.Asset.Path));
            return Task.FromResult((new Img(100, 100), Info()));
        }, TimeSpan.Zero);
        // Shown: d (3). Ahead 3: e, f (video), g; behind 2: c (gif), b.
        await preloader.Schedule(entries, 3, Fit(), ahead: 3, behind: 2);
        Assert.Equal(new[] { "e.png", "b.png", "g.png" }, decoded);
        Assert.Equal(3, cache.Count);
        Assert.Equal(3, preloader.Decoded);
        // Again: everything is kept, nothing is decoded a second time.
        await preloader.Schedule(entries, 3, Fit(), ahead: 3, behind: 2);
        Assert.Equal(3, decoded.Count);
    }

    [Fact]
    public async Task ThePreloaderStopsBeforeItsImagesWouldNotFitTheBudget()
    {
        using TempDirectory temp = new();
        IReadOnlyList<GalleryEntry> entries = await EntriesAsync(temp.Path, "a.png", "b.png", "c.png", "d.png", "e.png", "f.png");
        GalleryImageCache<Img> cache = Cache(Hundred * 2 + 100);
        int calls = 0;
        GalleryPreloader<Img> preloader = new(cache, (_, _, _) => { calls++; return Task.FromResult((new Img(100, 100), Info())); }, TimeSpan.Zero);
        await preloader.Schedule(entries, 2, Fit(), ahead: 3, behind: 3);
        Assert.Equal(2, calls); // two fit; a third would push out the nearer ones
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task NothingIsPreloadedWithTheCacheOffOrBothCountsAtZero()
    {
        using TempDirectory temp = new();
        IReadOnlyList<GalleryEntry> entries = await EntriesAsync(temp.Path, "a.png", "b.png", "c.png");
        int calls = 0;
        GalleryImageCache<Img> off = Cache(0), on = Cache(Hundred * 10);
        Task<(Img, GalleryImageInfo)> Decode(GalleryEntry _, GalleryDecodeRequest __, CancellationToken ___) { calls++; return Task.FromResult((new Img(100, 100), Info())); }
        await new GalleryPreloader<Img>(off, Decode, TimeSpan.Zero).Schedule(entries, 1, Fit(), 2, 2);
        await new GalleryPreloader<Img>(on, Decode, TimeSpan.Zero).Schedule(entries, 1, Fit(), 0, 0);
        await new GalleryPreloader<Img>(on, Decode, TimeSpan.Zero).Schedule(entries, 9, Fit(), 2, 2);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MovingOnCancelsThePreloadAndAFailedImageDoesNotStopTheOthers()
    {
        using TempDirectory temp = new();
        IReadOnlyList<GalleryEntry> entries = await EntriesAsync(temp.Path, "a.png", "b.png", "c.png", "d.png");
        GalleryImageCache<Img> cache = Cache(Hundred * 10);
        List<string> started = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GalleryPreloader<Img> preloader = new(cache, async (entry, _, token) =>
        {
            started.Add(Path.GetFileName(entry.Asset.Path));
            if (entry.Asset.Path.EndsWith("b.png")) throw new InvalidDataException("broken");
            await release.Task.WaitAsync(token);
            return (new Img(100, 100), Info());
        }, TimeSpan.Zero);
        Task running = preloader.Schedule(entries, 0, Fit(), ahead: 3, behind: 0);
        while (started.Count < 2) await Task.Delay(5);
        Assert.Equal(new[] { "b.png", "c.png" }, started); // b failed, c is being decoded
        preloader.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, started.Count); // d never started
        Assert.Equal(0, cache.Count);
        release.SetResult();
    }

    [Fact]
    public async Task ThePreloaderWaitsAQuietMomentSoHoldingTheArrowKeyDecodesNothing()
    {
        using TempDirectory temp = new();
        IReadOnlyList<GalleryEntry> entries = await EntriesAsync(temp.Path, "a.png", "b.png", "c.png");
        int calls = 0;
        GalleryPreloader<Img> preloader = new(Cache(Hundred * 10), (_, _, _) => { calls++; return Task.FromResult((new Img(100, 100), Info())); }, TimeSpan.FromMinutes(5));
        Task first = preloader.Schedule(entries, 1, Fit(), 1, 1);
        Task second = preloader.Schedule(entries, 2, Fit(), 1, 1); // the user moved on: the first one is replaced
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        preloader.Cancel();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ArchiveTilesTheReaderAndTheMediaPageKeepTheirPreviewsUnderTheSameBytesKey()
    {
        using TempDirectory temp = new();
        GalleryEntry entry = (await EntriesAsync(temp.Path, "a.png"))[0];
        // The key of the media page's tiles (MediaLibraryGrouping.ByteKey): lowercase hash, a colon and the length.
        Assert.Equal(entry.Asset.Sha256.ToLowerInvariant() + ":" + entry.Asset.Length, MediaTileThumbnails.ByteKey(entry.Asset));
        Assert.NotEqual(MediaTileThumbnails.ByteKey(entry.Asset), MediaTileThumbnails.ByteKey(entry.Asset, "@160x120"));
        Assert.Null(MediaTileThumbnails.ByteKey(entry.Asset with { Sha256 = string.Empty }));
    }

    // One manifest folder with a small file for each name (the bytes differ by name); entries in the order of the names.
    private static async Task<IReadOnlyList<GalleryEntry>> EntriesAsync(string root, params string[] names)
    {
        Directory.CreateDirectory(root);
        Dictionary<string, object> manifestEntries = new();
        foreach (string name in names)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("bytes of " + name);
            await File.WriteAllBytesAsync(Path.Combine(root, name), bytes);
            manifestEntries[name] = new
            {
                MediaIdentifier = name, SourceUrl = "https://offline.invalid/never-request/" + name, FileName = name,
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes))
            };
        }
        await File.WriteAllTextAsync(Path.Combine(root, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = manifestEntries }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(root);
        Dictionary<string, MediaAssetReference> byName = manifest.GetPage().ToDictionary(asset => Path.GetFileName(asset.Path), StringComparer.Ordinal);
        return names.Select(name => new GalleryEntry(new GalleryAssetKey(new ResourceId("t", "g", root), root, byName[name].AssetId),
            manifest, byName[name], null, null, false, true)).ToArray();
    }
}

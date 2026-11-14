using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

public sealed class GalleryCacheIntegrityTests
{
    private sealed class Img(int width, int height) : IDisposable
    {
        internal PixelSize Size { get; } = new(width, height);
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static readonly GalleryDecodeRequest.Fit Fit = new(100, 100);
    private static readonly GalleryImageInfo Info = new(1000, 1000, "Png", false);
    private const long Budget = 100 * 100 * 4 * 4;

    [Fact]
    public async Task SameLengthRewriteWithRestoredWriteTimeInvalidatesTheCapturedIdentity()
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await EntryAsync(Path.Combine(temp.Path, "one"), "image.png", "original image"u8.ToArray());
        Func<bool> valid = Assert.IsType<Func<bool>>(GalleryImageCache.Validity(entry));
        DateTime writeTime = File.GetLastWriteTimeUtc(entry.Asset.Path);
        await File.WriteAllBytesAsync(entry.Asset.Path, "replaced image"u8.ToArray());
        File.SetLastWriteTimeUtc(entry.Asset.Path, writeTime);

        Assert.Equal(entry.Asset.Length, new FileInfo(entry.Asset.Path).Length);
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(entry.Asset.Path));
        Assert.False(valid());

        // A rename-over has a new regular-file identity too, even when its bytes and timestamps look like the old file.
        await File.WriteAllBytesAsync(entry.Asset.Path, "original image"u8.ToArray());
        File.SetLastWriteTimeUtc(entry.Asset.Path, writeTime);
        Func<bool> renameValid = Assert.IsType<Func<bool>>(GalleryImageCache.Validity(entry));
        string replacement = entry.Asset.Path + ".replacement";
        await File.WriteAllBytesAsync(replacement, "replaced image"u8.ToArray());
        File.SetLastWriteTimeUtc(replacement, writeTime);
        File.Move(replacement, entry.Asset.Path, overwrite: true);

        Assert.False(renameValid());
    }

    [Fact]
    public async Task AWarmBytesHitProvesTheCurrentOccurrenceBeforeSharingAcrossFolders()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        int decodes = 0;

        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            decodes++;
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        (Img shared, _) = await GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, CancellationToken.None);

        Assert.Same(first, shared);
        Assert.Equal(1, decodes);
        cache.Release(shared);
    }

    [Fact]
    public async Task AChangedOtherOccurrenceCannotUseTheWarmBytesHit()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        int decodes = 0;

        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            decodes++;
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        DateTime writeTime = File.GetLastWriteTimeUtc(secondEntry.Asset.Path);
        await File.WriteAllBytesAsync(secondEntry.Asset.Path, "changed image!!!"u8.ToArray());
        File.SetLastWriteTimeUtc(secondEntry.Asset.Path, writeTime);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, CancellationToken.None));
        Assert.Equal(2, decodes);
        Assert.Equal(1, cache.Count); // The valid first occurrence remains available; the failed second one took no lease.
    }

    [Fact]
    public async Task MissingOrSymlinkedOccurrenceNeverBecomesAWarmHit()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        int decodes = 0;

        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            decodes++;
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        File.Delete(secondEntry.Asset.Path);
        Assert.Null(GalleryImageCache.Validity(secondEntry));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, CancellationToken.None));
        Assert.Equal(2, decodes);

        // A final symlink is rejected by the regular-file identity before the cache can even attempt a warm hit.
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(secondEntry.Asset.Path, firstEntry.Asset.Path);
        Assert.Null(GalleryImageCache.Validity(secondEntry));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, CancellationToken.None));
        Assert.Equal(3, decodes);
    }

    [Fact]
    public async Task CancelledWarmVerificationAcquiresNoLease()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await AssertCanceledAsync(GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, canceled.Token));

        Assert.True(cache.TryAcquire(GalleryImageCache.KeyOf(firstEntry)!, Fit, out Img? stillThere, out _));
        Assert.Same(first, stillThere);
        cache.Release(stillThere);
    }

    [Fact]
    public async Task PreloaderTouchProvesAnOtherFolderBeforeKeepingItsWarmImage()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        int preloaded = 0;
        GalleryPreloader<Img> preloader = new(cache, async (entry, request, token) =>
        {
            preloaded++;
            return await Decode(entry, request, token);
        }, TimeSpan.Zero);

        await preloader.Schedule(new[] { firstEntry, secondEntry }, 0, Fit, ahead: 1, behind: 0);

        Assert.Equal(0, preloaded);
        Assert.Equal(1, cache.Count);
    }

    // S02: the viewer's decode queue starts DecodeThrough on the UI thread. Reading an original's identity can stall on a sleeping
    // disk or a network share, so it must never run there, not even for an image that is already in memory.

    [Fact]
    public Task AWarmHitStartedOnTheUiContextReadsTheOriginalOnlyOffItAndOnce() => OnUiContextAsync(async ui =>
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await EntryAsync(Path.Combine(temp.Path, "one"), "image.png", "original image"u8.ToArray());
        IdentityProbe probe = new(ui);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget) { IdentityOf = probe.Read };
        int decodes = 0;
        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry decoded, GalleryDecodeRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref decodes);
            await using Stream verified = await decoded.Manifest.OpenVerifiedReadAsync(decoded.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, CancellationToken.None);
        cache.Release(first);
        Assert.Equal(2, probe.Calls); // cold: captured before the decode and confirmed after it

        probe.Close();
        Task<(Img Bitmap, GalleryImageInfo Info)> warm = GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, CancellationToken.None);
        // The call is back on the UI thread while the identity read of the original still waits at the closed gate on the pool.
        Assert.False(warm.IsCompleted);
        probe.Open();
        (Img hit, _) = await warm.WaitAsync(Patience);

        Assert.Same(first, hit);
        Assert.Equal(1, decodes);
        Assert.Equal(3, probe.Calls); // the unchanged warm hit reads the original once and hashes nothing
        Assert.Equal(0, probe.OnUi);
        cache.Release(hit);
    });

    [Fact]
    public Task AColdDecodeThatCompletesOnTheUiContextConfirmsItsOriginalOffIt() => OnUiContextAsync(async ui =>
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await EntryAsync(Path.Combine(temp.Path, "one"), "image.png", "original image"u8.ToArray());
        IdentityProbe probe = new(ui);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget) { IdentityOf = probe.Read };
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(Img Bitmap, GalleryImageInfo Info)> decoded = new(); // continuations run on the completing thread
        Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry _, GalleryDecodeRequest __, CancellationToken ___)
        {
            started.TrySetResult();
            return decoded.Task;
        }

        Task<(Img Bitmap, GalleryImageInfo Info)> cold = GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, CancellationToken.None);
        await started.Task.WaitAsync(Patience);
        Img image = new(100, 100);
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { decoded.SetResult((image, Info)); } // UI thread without a context: presence is not a safe ownership test
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        (Img shown, _) = await cold.WaitAsync(Patience);

        Assert.Same(image, shown);
        Assert.Equal(2, probe.Calls);
        Assert.Equal(0, probe.OnUi);
        Assert.Equal(1, cache.Count); // the confirmation after the decode passed: kept
        cache.Release(shown);
    });

    [Fact]
    public Task CancellingAWarmHitWhileItsSlotIsCheckedHandsOutNothingAndKeepsNoLease() => OnUiContextAsync(async ui =>
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        IdentityProbe probe = new(ui);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget) { IdentityOf = probe.Read };
        int decodes = 0;
        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry decoded, GalleryDecodeRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref decodes);
            await using Stream verified = await decoded.Manifest.OpenVerifiedReadAsync(decoded.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        (Img shared, _) = await GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, CancellationToken.None);
        Assert.Same(first, shared); // the second folder is proven once and remembered
        cache.Release(shared);

        // The kept image belongs to the first folder, so a hit for the second still checks the first original. The viewer moves on
        // exactly then: the hit is already leased when the request learns about it.
        using CancellationTokenSource moved = new();
        probe.OnRead = path => { if (path == firstEntry.Asset.Path) moved.Cancel(); };
        await AssertCanceledAsync(GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, moved.Token));
        probe.OnRead = null;

        Assert.Equal(1, decodes);
        Assert.Equal(0, probe.OnUi);
        Assert.False(first.Disposed);
        cache.BudgetBytes = 0; // a leftover lease would keep the pushed-out image alive
        Assert.True(first.Disposed);
    });

    [Fact]
    public Task CancellingAColdDecodeWhileItsOriginalIsConfirmedHandsOutNothingAndKeepsNoLease() => OnUiContextAsync(async ui =>
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await EntryAsync(Path.Combine(temp.Path, "one"), "image.png", "original image"u8.ToArray());
        IdentityProbe probe = new(ui);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget) { IdentityOf = probe.Read };
        Img image = new(100, 100);
        Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry _, GalleryDecodeRequest __, CancellationToken ___) =>
            Task.FromResult((image, Info));

        using CancellationTokenSource moved = new();
        int reads = 0;
        probe.OnRead = _ => { if (Interlocked.Increment(ref reads) == 2) moved.Cancel(); }; // the read after the decode
        await AssertCanceledAsync(GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, moved.Token));

        Assert.Equal(0, probe.OnUi);
        Assert.Equal(1, cache.Count); // the verified decode stays available to the next request
        Assert.False(image.Disposed);
        cache.BudgetBytes = 0;
        Assert.True(image.Disposed); // but the cancelled request holds no lease on it
    });

    [Fact]
    public Task CancellingAColdDecodeAfterItsOriginalBecomesInvalidHandsOutNothing() => OnUiContextAsync(async ui =>
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await EntryAsync(Path.Combine(temp.Path, "one"), "image.png", "original image"u8.ToArray());
        IdentityProbe probe = new(ui);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget) { IdentityOf = probe.Read };
        Img image = new(100, 100);
        Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry _, GalleryDecodeRequest __, CancellationToken ___) =>
            Task.FromResult((image, Info));

        using CancellationTokenSource canceled = new();
        int reads = 0;
        probe.OnRead = path =>
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                canceled.Cancel();
                File.Delete(path); // make the post-decode identity check reject the occurrence
            }
        };

        await AssertCanceledAsync(GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, canceled.Token));

        Assert.Equal(0, probe.OnUi);
        Assert.Equal(0, cache.Count);
        Assert.True(image.Disposed);
    });

    [Fact]
    public Task AWarmHitStartedOnTheUiContextNeverAnswersARewrittenOrDeletedOriginalWithTheOldImage() => OnUiContextAsync(async ui =>
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await EntryAsync(Path.Combine(temp.Path, "one"), "image.png", "original image"u8.ToArray());
        IdentityProbe probe = new(ui);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget) { IdentityOf = probe.Read };
        int decodes = 0;
        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry decoded, GalleryDecodeRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref decodes);
            await using Stream verified = await decoded.Manifest.OpenVerifiedReadAsync(decoded.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, CancellationToken.None);
        cache.Release(first);
        (Img warm, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, CancellationToken.None);
        Assert.Same(first, warm);
        cache.Release(warm);

        // Same length, write time restored: the identity changed, the proof fails, the decoder (which verifies) runs and refuses.
        DateTime writeTime = File.GetLastWriteTimeUtc(entry.Asset.Path);
        await File.WriteAllBytesAsync(entry.Asset.Path, "replaced image"u8.ToArray());
        File.SetLastWriteTimeUtc(entry.Asset.Path, writeTime);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, CancellationToken.None));
        Assert.Equal(2, decodes);

        // The original bytes again, with a new change time: they are proven, but the kept image was made from the earlier identity
        // and is dropped instead of shown.
        await File.WriteAllBytesAsync(entry.Asset.Path, "original image"u8.ToArray());
        File.SetLastWriteTimeUtc(entry.Asset.Path, writeTime);
        (Img fresh, _) = await GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, CancellationToken.None);
        Assert.NotSame(first, fresh);
        Assert.True(first.Disposed);
        Assert.Equal(3, decodes);
        cache.Release(fresh);

        File.Delete(entry.Asset.Path);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            GalleryImageCache.DecodeThrough(cache, Decode, entry, Fit, CancellationToken.None));
        Assert.Equal(4, decodes);
        // The next lookup (here: the preloader's touch, on the pool) drops the image of the vanished original.
        string key = Assert.IsType<string>(GalleryImageCache.KeyOf(entry));
        Assert.Equal(-1, await Task.Run(() => cache.Touch(key, Fit)));
        Assert.True(fresh.Disposed);
        Assert.Equal(0, probe.OnUi);
    });

    private static async Task AssertCanceledAsync(Task task)
    {
        // Task.Run/await may surface either OperationCanceledException or its TaskCanceledException subclass.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // Identity reads of originals: counts where they run and, while closed, makes the off-UI ones wait at a gate. A read on the UI
    // thread is counted and never waits, so a regression fails the assertions instead of hanging the test.
    private sealed class IdentityProbe(int uiThread)
    {
        private readonly ManualResetEventSlim _gate = new(true);
        private int _onUi, _offUi;
        internal Action<string>? OnRead { get; set; }
        internal int OnUi => Volatile.Read(ref _onUi);
        internal int Calls => Volatile.Read(ref _onUi) + Volatile.Read(ref _offUi);
        internal void Close() => _gate.Reset();
        internal void Open() => _gate.Set();

        internal RegularProfileFileIdentity? Read(string path)
        {
            if (Environment.CurrentManagedThreadId == uiThread) Interlocked.Increment(ref _onUi);
            else
            {
                Interlocked.Increment(ref _offUi);
                if (!_gate.Wait(Patience)) throw new TimeoutException("The identity gate was never opened.");
            }
            OnRead?.Invoke(path);
            return RegularProfileFile.TryGetIdentity(path);
        }
    }

    // Runs the test on one thread that owns a single-threaded SynchronizationContext, like the UI thread that starts the viewer's
    // decode queue: awaits in the test come back to it, work sent to the thread pool does not. No sleeps: the loop waits for posts.
    private static Task OnUiContextAsync(Func<int, Task> test) => Task.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        using UiContext ui = new();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            Task running = test(Environment.CurrentManagedThreadId);
            while (!running.IsCompleted) ui.RunOne();
            running.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private sealed class UiContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = new();
        public override void Post(SendOrPostCallback callback, object? state) => _posted.Add((callback, state));
        internal void RunOne()
        {
            if (!_posted.TryTake(out var work, Patience)) throw new TimeoutException("The UI context made no progress.");
            work.Callback(work.State);
        }
        public void Dispose() => _posted.Dispose();
    }

    private static async Task<GalleryEntry> EntryAsync(string directory, string name, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        Dictionary<string, object> entries = new()
        {
            [name] = new
            {
                MediaIdentifier = name, SourceUrl = "https://offline.invalid/never-request/" + name, FileName = name,
                Length = bytes.Length, Sha256 = sha, Md5 = Convert.ToBase64String(MD5.HashData(bytes))
            }
        };
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = manifest.GetPage()[0];
        return new GalleryEntry(new GalleryAssetKey(new ResourceId("test", "gallery", directory), directory, asset.AssetId),
            manifest, asset, null, null, false, true);
    }
}

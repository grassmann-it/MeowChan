using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

/// <summary>
/// NH-3 scrub preview core with a synthetic decoder: no LibVLC, display, audio or network. Each test owns its decode gate,
/// so none of them waits on, or blocks, the process-wide poster gate.
/// </summary>
public sealed class VideoScrubPreviewTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void SlotsAreFivePercentStepsAndClampEveryFraction()
    {
        Assert.Equal(20, VideoScrubSlots.Count);
        Assert.Equal(0, VideoScrubSlots.FromFraction(0));
        Assert.Equal(0, VideoScrubSlots.FromFraction(0.0499));
        Assert.Equal(1, VideoScrubSlots.FromFraction(0.05));
        Assert.Equal(10, VideoScrubSlots.FromFraction(0.52));
        Assert.Equal(19, VideoScrubSlots.FromFraction(0.999));
        Assert.Equal(19, VideoScrubSlots.FromFraction(1));
        Assert.Equal(19, VideoScrubSlots.FromFraction(7));
        Assert.Equal(0, VideoScrubSlots.FromFraction(-0.5));
        Assert.Equal(0, VideoScrubSlots.FromFraction(double.NaN));
        Assert.Equal(19, VideoScrubSlots.FromFraction(double.PositiveInfinity));
        Assert.Equal(0.25, VideoScrubSlots.Fraction(5));
        Assert.Equal(TimeSpan.FromSeconds(25), VideoScrubSlots.Start(5, Duration));
        Assert.Equal(TimeSpan.Zero, VideoScrubSlots.Start(0, Duration));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoScrubSlots.Validate(20));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoScrubSlots.Validate(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoScrubSlots.Start(1, TimeSpan.Zero));
    }

    [Fact]
    public void BudgetIsCallerConfiguredWithinDocumentedDefaultMinimumAndMaximum()
    {
        Assert.Equal(VideoScrubPreviewOptions.CacheLimitMiBDefault, VideoScrubPreviewOptions.Default.CacheLimitMiB);
        Assert.Equal(128L * 1024 * 1024, VideoScrubPreviewOptions.Default.CacheLimitBytes);
        Assert.Equal(VideoScrubPreviewOptions.DecodeTimeoutDefault, VideoScrubPreviewOptions.Default.DecodeTimeout);
        Assert.Equal(8, new VideoScrubPreviewOptions(VideoScrubPreviewOptions.CacheLimitMiBMinimum).CacheLimitMiB);
        Assert.Equal(4096, new VideoScrubPreviewOptions(VideoScrubPreviewOptions.CacheLimitMiBMaximum).CacheLimitMiB);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoScrubPreviewOptions(VideoScrubPreviewOptions.CacheLimitMiBMinimum - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoScrubPreviewOptions(VideoScrubPreviewOptions.CacheLimitMiBMaximum + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoScrubPreviewOptions(64, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoScrubPreviewOptions(64, TimeSpan.FromMinutes(3)));
        Assert.Equal(8, VideoScrubPreviewOptions.ClampCacheLimit(1));
        Assert.Equal(4096, VideoScrubPreviewOptions.ClampCacheLimit(int.MaxValue));
        Assert.Equal(300, VideoScrubPreviewOptions.ClampCacheLimit(300));
    }

    [Fact]
    public async Task KeysCarryContentRevisionSlotBoundsAndVersionAndOnlyRecordedVideosAreEligible()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        VideoScrubFrameKey key = VideoScrubFrameKey.For(asset, 7);
        Assert.Equal("v1-" + asset.Sha256.ToLowerInvariant() + "-4-320x180-s07.png", key.FileName);
        Assert.NotEqual(key.FileName, (key with { Length = 5 }).FileName);
        Assert.NotEqual(key.FileName, VideoScrubFrameKey.For(asset, 8).FileName);
        Assert.True(VideoScrubFrameKey.IsEligible(manifest, asset));
        // An altered record is not this manifest's record (and here also malformed or oversized).
        Assert.False(VideoScrubFrameKey.IsEligible(manifest, asset with { Sha256 = new string('z', 64) }));
        Assert.False(VideoScrubFrameKey.IsEligible(manifest, asset with { Length = VideoPosterCache.MaximumVideoBytes + 1 }));

        using TempDirectory other = new();
        var (imageManifest, image) = await ManifestAsync(other.Path, "still.png", new byte[] { 9, 9 });
        Assert.False(VideoScrubFrameKey.IsEligible(imageManifest, image));
        Assert.False(VideoScrubFrameKey.IsEligible(imageManifest, asset)); // not part of this manifest
        Assert.Null(Cache(temp.Path, new FakeDecoder()).OpenSession(imageManifest, image));
    }

    [Fact]
    public async Task ThumbnailsAreDecodedLazilyOncePerSlotAtTheSlotPositionAndPersisted()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        FakeDecoder decoder = new();
        VideoScrubPreviewCache cache = Cache(temp.Path, decoder);
        await using VideoScrubPreviewSession session = cache.OpenSession(manifest, asset)!;
        Assert.Empty(decoder.Calls);
        Assert.False(session.TryGetReady(0.52, out _));
        Assert.Null(await session.GetAsync(0.52, TimeSpan.Zero, CancellationToken.None)); // duration unknown yet
        Assert.Empty(decoder.Calls);

        VideoScrubFrame frame = Assert.IsType<VideoScrubFrame>(await session.GetAsync(0.52, Duration, CancellationToken.None));
        Assert.Equal(10, frame.Slot);
        Assert.Equal(0.5, frame.Fraction);
        Assert.Equal(WidthOf(10), Decode(frame.Png).Width);
        var call = Assert.Single(decoder.Calls);
        Assert.Equal((TimeSpan.FromSeconds(50), 320, 180), call);
        Assert.True(decoder.AllStreamsDisposed);

        Assert.Same(frame.Png, (await session.GetAsync(0.54, Duration, CancellationToken.None))!.Png);
        Assert.True(session.TryGetReady(0.5, out VideoScrubFrame? ready));
        Assert.Same(frame.Png, ready!.Png);
        Assert.Single(decoder.Calls);
        Assert.Equal(1, session.DecodeCount);
        await session.GetAsync(0.97, Duration, CancellationToken.None);
        Assert.Equal(2, decoder.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(95), decoder.Calls.Last().Start);
        Assert.Equal(new[] { VideoScrubFrameKey.For(asset, 10).FileName, VideoScrubFrameKey.For(asset, 19).FileName }.Order(),
            Directory.GetFiles(cache.Directory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task StoredThumbnailsServeALaterSessionWithoutDecodingButOnlyForAVerifiedOriginal()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        await using (VideoScrubPreviewSession first = Cache(temp.Path, new FakeDecoder()).OpenSession(manifest, asset)!)
            Assert.NotNull(await first.GetSlotAsync(4, Duration, CancellationToken.None));
        string stored = Path.Combine(temp.Path, "scrub", VideoScrubFrameKey.For(asset, 4).FileName);
        DateTime written = File.GetLastWriteTimeUtc(stored);

        // A restarted reader: fresh manifest, fresh cache instance, a decoder that must not be used.
        MediaAssetSnapshot reopened = await MediaAssetReader.OpenAsync(temp.Path);
        FakeDecoder unused = new() { Fail = true };
        await using (VideoScrubPreviewSession second = Cache(temp.Path, unused).OpenSession(reopened, reopened.GetPage().Single())!)
        {
            VideoScrubFrame frame = Assert.IsType<VideoScrubFrame>(await second.GetSlotAsync(4, Duration, CancellationToken.None));
            Assert.Equal(WidthOf(4), Decode(frame.Png).Width);
        }
        Assert.Empty(unused.Calls);
        Assert.Equal(written, File.GetLastWriteTimeUtc(stored));

        // The original changed on disk: the recorded revision no longer verifies, so even the stored thumbnail is withheld.
        await File.WriteAllBytesAsync(asset.Path, new byte[] { 5, 6, 7, 8 });
        await using (VideoScrubPreviewSession changed = Cache(temp.Path, unused).OpenSession(reopened, reopened.GetPage().Single())!)
        {
            Assert.Null(await changed.GetSlotAsync(4, Duration, CancellationToken.None));
            Assert.True(changed.IsUnavailable);
            Assert.Null(await changed.GetSlotAsync(5, Duration, CancellationToken.None));
        }
        Assert.Empty(unused.Calls);
        Assert.True(File.Exists(stored)); // a derived file of other bytes is not deleted by a failed verification

        // The new bytes recorded as a new revision get thumbnails of their own, never the old revision's.
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".meowchan-manifest.json"), ManifestJson("clip.webm", new byte[] { 5, 6, 7, 8 }));
        MediaAssetSnapshot revised = await MediaAssetReader.OpenAsync(temp.Path);
        MediaAssetReference revisedAsset = revised.GetPage().Single();
        Assert.NotEqual(VideoScrubFrameKey.For(asset, 4).FileName, VideoScrubFrameKey.For(revisedAsset, 4).FileName);
        FakeDecoder fresh = new();
        await using (VideoScrubPreviewSession next = Cache(temp.Path, fresh).OpenSession(revised, revisedAsset)!)
            Assert.NotNull(await next.GetSlotAsync(4, Duration, CancellationToken.None));
        Assert.Single(fresh.Calls);
    }

    [Fact]
    public async Task AnInvalidStoredThumbnailIsDiscardedAndDecodedAgain()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        string directory = Path.Combine(temp.Path, "scrub"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, VideoScrubFrameKey.For(asset, 3).FileName);
        await File.WriteAllBytesAsync(path, Png(640, 360)); // larger than the scrub bounds
        FakeDecoder decoder = new();
        await using VideoScrubPreviewSession session = Cache(temp.Path, decoder).OpenSession(manifest, asset)!;
        VideoScrubFrame frame = Assert.IsType<VideoScrubFrame>(await session.GetSlotAsync(3, Duration, CancellationToken.None));
        Assert.Single(decoder.Calls);
        Assert.Equal(frame.Png, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ConcurrentRequestsForOneSlotShareOneDecode()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        FakeDecoder decoder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using VideoScrubPreviewSession session = Cache(temp.Path, decoder).OpenSession(manifest, asset)!;
        Task<VideoScrubFrame?>[] requests = Enumerable.Range(0, 8)
            .Select(index => Task.Run(() => session.GetAsync(0.31 + index * 0.001, Duration, CancellationToken.None))).ToArray();
        await decoder.Started.Task.WaitAsync(Patience);
        decoder.Hold.SetResult();
        VideoScrubFrame?[] frames = await Task.WhenAll(requests).WaitAsync(Patience);
        Assert.Single(decoder.Calls);
        Assert.All(frames, frame => Assert.Same(frames[0]!.Png, frame!.Png));
        Assert.All(frames, frame => Assert.Equal(6, frame!.Slot));
    }

    [Fact]
    public async Task ARequestAbandonedBeforeItsDecodeBeganIsDroppedSoTheLatestHoverWins()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        FakeDecoder decoder = new();
        SemaphoreSlim gate = new(1, 1);
        await gate.WaitAsync(); // another native decode (a poster, another video) holds the gate
        await using VideoScrubPreviewSession session = Cache(temp.Path, decoder, gate).OpenSession(manifest, asset)!;
        using CancellationTokenSource hover = new();
        Task<VideoScrubFrame?> stale = session.GetSlotAsync(3, Duration, hover.Token);
        await Task.Delay(50);
        hover.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale.WaitAsync(Patience));
        Task<VideoScrubFrame?> latest = session.GetSlotAsync(7, Duration, CancellationToken.None);
        gate.Release();
        Assert.Equal(7, (await latest.WaitAsync(Patience))!.Slot);
        Assert.Equal(new[] { TimeSpan.FromSeconds(35) }, decoder.Calls.Select(call => call.Start));

        // Cancellation recorded nothing negative: the dropped slot decodes once it is asked for again.
        Assert.Equal(3, (await session.GetSlotAsync(3, Duration, CancellationToken.None))!.Slot);
        Assert.Equal(2, decoder.Calls.Count);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task ADecodeThatBeganFinishesAndFillsTheCacheWhenItsCallerLeaves()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        FakeDecoder decoder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        VideoScrubPreviewCache cache = Cache(temp.Path, decoder);
        await using VideoScrubPreviewSession session = cache.OpenSession(manifest, asset)!;
        using CancellationTokenSource hover = new();
        Task<VideoScrubFrame?> request = session.GetSlotAsync(12, Duration, hover.Token);
        await decoder.Started.Task.WaitAsync(Patience);
        hover.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Patience));
        decoder.Hold.SetResult();
        await Eventually(() => session.TryGetReady(0.6, out _));
        Assert.True(File.Exists(cache.PathOf(VideoScrubFrameKey.For(asset, 12))));
        Assert.Single(decoder.Calls);
        Assert.NotNull(await session.GetSlotAsync(12, Duration, CancellationToken.None));
        Assert.Single(decoder.Calls);
    }

    [Fact]
    public async Task AFailedOrOversizedDecodeLeavesOnlyThatSlotEmptyForTheSessionAndStoresNothing()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        FakeDecoder decoder = new() { Fail = true };
        VideoScrubPreviewCache cache = Cache(temp.Path, decoder);
        await using VideoScrubPreviewSession session = cache.OpenSession(manifest, asset)!;
        Assert.Null(await session.GetSlotAsync(2, Duration, CancellationToken.None));
        Assert.Null(await session.GetSlotAsync(2, Duration, CancellationToken.None));
        Assert.Single(decoder.Calls);
        Assert.False(session.IsUnavailable);

        decoder.Fail = false;
        decoder.Oversized = true;
        Assert.Null(await session.GetSlotAsync(4, Duration, CancellationToken.None));
        decoder.Oversized = false;
        Assert.NotNull(await session.GetSlotAsync(5, Duration, CancellationToken.None));
        Assert.Equal(3, decoder.Calls.Count);
        Assert.True(decoder.AllStreamsDisposed);
        Assert.Equal(new[] { VideoScrubFrameKey.For(asset, 5).FileName }, Directory.GetFiles(cache.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ADecoderIgnoringCancellationIsOutwaitedWhileTheGateStaysHeldUntilItReturns()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        FakeDecoder decoder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCancellation = true };
        SemaphoreSlim gate = new(1, 1);
        VideoScrubPreviewCache cache = Cache(temp.Path, decoder, gate, new VideoScrubPreviewOptions(16, TimeSpan.FromMilliseconds(100)));
        await using VideoScrubPreviewSession session = cache.OpenSession(manifest, asset)!;
        Assert.Null(await session.GetSlotAsync(1, Duration, CancellationToken.None).WaitAsync(Patience));
        Assert.Equal(0, gate.CurrentCount); // the native session still runs
        Assert.Null(await session.GetSlotAsync(1, Duration, CancellationToken.None)); // timed out slot stays empty
        Assert.Single(decoder.Calls);
        decoder.Hold.SetResult();
        await Eventually(() => gate.CurrentCount == 1);
        Assert.Empty(Directory.Exists(cache.Directory) ? Directory.GetFiles(cache.Directory) : Array.Empty<string>());
    }

    [Fact]
    public async Task DisposalCancelsQueuedAndRunningLoadsReleasesTheGateAndRejectsLaterRequests()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        FakeDecoder decoder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        SemaphoreSlim gate = new(1, 1);
        VideoScrubPreviewSession session = Cache(temp.Path, decoder, gate).OpenSession(manifest, asset)!;
        Task<VideoScrubFrame?> running = session.GetSlotAsync(2, Duration, CancellationToken.None);
        await decoder.Started.Task.WaitAsync(Patience);
        Task<VideoScrubFrame?> queued = session.GetSlotAsync(9, Duration, CancellationToken.None);
        await Task.Delay(50);
        Task disposing = session.DisposeAsync().AsTask();
        Task again = session.DisposeAsync().AsTask();
        await Task.WhenAll(disposing, again).WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Patience));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Patience));
        Assert.Equal(1, gate.CurrentCount);
        Assert.Single(decoder.Calls);
        Assert.True(decoder.AllStreamsDisposed);
        Assert.False(session.TryGetReady(0.1, out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetSlotAsync(2, Duration, CancellationToken.None));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposalWaitsForCooperativeDecoderCleanupBeforeReleasingItsGate()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        TaskCompletionSource cleanupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cleanupRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeDecoder decoder = new()
        {
            Hold = new(TaskCreationOptions.RunContinuationsAsynchronously),
            CleanupStarted = cleanupStarted, CleanupHold = cleanupRelease
        };
        using SemaphoreSlim gate = new(1, 1);
        VideoScrubPreviewSession session = Cache(temp.Path, decoder, gate).OpenSession(manifest, asset)!;
        Task<VideoScrubFrame?> request = session.GetSlotAsync(2, Duration, CancellationToken.None);
        try
        {
            await decoder.Started.Task.WaitAsync(Patience);
            Task disposing = session.DisposeAsync().AsTask();
            await cleanupStarted.Task.WaitAsync(Patience);
            Assert.False(disposing.IsCompleted);
            Assert.False(decoder.AllStreamsDisposed);
            Assert.Equal(0, gate.CurrentCount);
            cleanupRelease.TrySetResult();
            await disposing.WaitAsync(Patience);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Patience));
            Assert.True(decoder.AllStreamsDisposed);
            Assert.Equal(1, gate.CurrentCount);
        }
        finally
        {
            cleanupRelease.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposalIsBoundedForAnUncooperativeDecoderAndKeepsItsGateUntilItReturns()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeDecoder decoder = new() { Hold = release, IgnoreCancellation = true };
        using SemaphoreSlim gate = new(1, 1);
        VideoScrubPreviewCache cache = Cache(temp.Path, decoder, gate);
        VideoScrubPreviewSession session = cache.OpenSession(manifest, asset)!;
        Task<VideoScrubFrame?> request = session.GetSlotAsync(2, Duration, CancellationToken.None);
        try
        {
            await decoder.Started.Task.WaitAsync(Patience);
            await session.DisposeAsync().AsTask().WaitAsync(Patience);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Patience));
            Assert.False(decoder.AllStreamsDisposed);
            Assert.Equal(0, gate.CurrentCount);
            Assert.False(session.TryGetReady(0.1, out _));
            release.TrySetResult();
            await Eventually(() => gate.CurrentCount == 1);
            Assert.True(decoder.AllStreamsDisposed);
            Assert.False(File.Exists(cache.PathOf(VideoScrubFrameKey.For(asset, 2))));
        }
        finally
        {
            release.TrySetResult();
            await session.DisposeAsync();
            await Eventually(() => gate.CurrentCount == 1);
        }
    }

    [Fact]
    public async Task ALoweredBudgetEvictsTheOldestThumbnailsAndNeverTouchesOriginals()
    {
        using TempDirectory temp = new();
        var (_, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        VideoScrubPreviewCache cache = Cache(temp.Path, new FakeDecoder(), options: new VideoScrubPreviewOptions(64));
        Directory.CreateDirectory(cache.Directory);
        for (int index = 0; index < 12; index++)
        {
            string path = Path.Combine(cache.Directory, index.ToString("D2") + ".png");
            using (FileStream stream = File.Create(path)) stream.SetLength(1024 * 1024);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 100));
        }
        cache.TrimToBudget();
        Assert.Equal(12, Directory.GetFiles(cache.Directory).Length);
        cache.Configure(new VideoScrubPreviewOptions(VideoScrubPreviewOptions.CacheLimitMiBMinimum));
        Assert.Equal(8, cache.Options.CacheLimitMiB);
        cache.TrimToBudget();
        string[] left = Directory.GetFiles(cache.Directory).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(8, left.Length);
        Assert.Equal("04.png", left[0]);
        Assert.Equal("11.png", left[^1]);
        Assert.True(File.Exists(asset.Path));

        // Storing a new thumbnail makes room for it under the same budget.
        byte[] png = Png(100, 50);
        await cache.StoreAsync(VideoScrubFrameKey.For(asset, 0), png, CancellationToken.None);
        Assert.Equal(8, Directory.GetFiles(cache.Directory).Length);
        Assert.False(File.Exists(Path.Combine(cache.Directory, "04.png")));
        Assert.Equal(png, await cache.ReadAsync(VideoScrubFrameKey.For(asset, 0), CancellationToken.None));
    }

    [Fact]
    public async Task ThePositionedDecoderEntryRejectsBadArgumentsBeforeAnyNativeWorkAndReleasesTheStream()
    {
        TrackingStream negative = new();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            VideoPosterDecoder.DecodeAtAsync(negative, TimeSpan.FromSeconds(-1), 320, 180, CancellationToken.None));
        Assert.True(negative.Disposed);
        TrackingStream tooLarge = new();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            VideoPosterDecoder.DecodeAtAsync(tooLarge, TimeSpan.FromSeconds(1), VideoPosterDecoder.PosterMaximumWidth + 1, 180, CancellationToken.None));
        Assert.True(tooLarge.Disposed);
        TrackingStream tooTall = new();
        ArgumentOutOfRangeException height = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            VideoPosterDecoder.DecodeAtAsync(tooTall, TimeSpan.FromSeconds(1), 320, VideoPosterDecoder.PosterMaximumHeight + 1, CancellationToken.None));
        Assert.Equal("maximumHeight", height.ParamName);
        Assert.True(tooTall.Disposed);
        // The first-frame poster keeps the bounds its cache validates against.
        Assert.True(VideoPosterCache.Valid(Png(VideoPosterDecoder.PosterMaximumWidth, VideoPosterDecoder.PosterMaximumHeight)));
        Assert.False(VideoPosterCache.Valid(Png(VideoPosterDecoder.PosterMaximumWidth + 1, VideoPosterDecoder.PosterMaximumHeight)));
    }

    [Fact]
    public async Task AnOriginalLostAtAnotherSlotEmptiesAlreadyHeldSlotsInBothApis()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        FakeDecoder decoder = new();
        await using VideoScrubPreviewSession session = Cache(temp.Path, decoder).OpenSession(manifest, asset)!;
        Assert.NotNull(await session.GetSlotAsync(4, Duration, CancellationToken.None));
        Assert.True(session.TryGetReady(0.2, out _));

        await File.WriteAllBytesAsync(asset.Path, new byte[] { 5, 6, 7, 8 });
        Assert.Null(await session.GetSlotAsync(9, Duration, CancellationToken.None));
        Assert.True(session.IsUnavailable);

        Assert.False(session.TryGetReady(0.2, out VideoScrubFrame? held));
        Assert.Null(held);
        Assert.Null(await session.GetSlotAsync(4, Duration, CancellationToken.None));
        Assert.Null(await session.GetAsync(0.21, Duration, CancellationToken.None));
        Assert.Single(decoder.Calls);
    }

    [Fact]
    public async Task ADecodeFinishingAfterTheSessionLostItsOriginalIsNeitherPublishedNorStoredEvenIfTheFileVerifiesAgain()
    {
        using TempDirectory temp = new();
        byte[] original = { 1, 2, 3, 4 };
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", original);
        FakeDecoder decoder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseBeforeHold = true };
        VideoScrubPreviewCache cache = Cache(temp.Path, decoder);
        await using VideoScrubPreviewSession session = cache.OpenSession(manifest, asset)!;
        Task<VideoScrubFrame?> late = session.GetSlotAsync(2, Duration, CancellationToken.None);
        await decoder.Started.Task.WaitAsync(Patience);

        await File.WriteAllBytesAsync(asset.Path, new byte[] { 5, 6, 7, 8 });
        Assert.Null(await session.GetSlotAsync(9, Duration, CancellationToken.None).WaitAsync(Patience));
        Assert.True(session.IsUnavailable);
        // The bytes come back, so the late decode's own check after decoding passes: the session-wide cut alone must hold.
        await File.WriteAllBytesAsync(asset.Path, original);
        decoder.Hold.SetResult();

        Assert.Null(await late.WaitAsync(Patience));
        Assert.False(session.TryGetReady(0.1, out _));
        Assert.Null(await session.GetSlotAsync(2, Duration, CancellationToken.None));
        Assert.False(File.Exists(cache.PathOf(VideoScrubFrameKey.For(asset, 2))));
        Assert.Single(decoder.Calls);
        Assert.True(decoder.AllStreamsDisposed);
    }

    [Fact]
    public async Task AnOriginalChangedWhileItsFrameDecodesYieldsNoFrameAndStoresNothing()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        FakeDecoder decoder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseBeforeHold = true };
        VideoScrubPreviewCache cache = Cache(temp.Path, decoder);
        await using VideoScrubPreviewSession session = cache.OpenSession(manifest, asset)!;
        Task<VideoScrubFrame?> request = session.GetSlotAsync(6, Duration, CancellationToken.None);
        await decoder.Started.Task.WaitAsync(Patience);
        await File.WriteAllBytesAsync(asset.Path, new byte[] { 5, 6, 7, 8 });
        decoder.Hold.SetResult();

        Assert.Null(await request.WaitAsync(Patience));
        Assert.True(session.IsUnavailable);
        Assert.False(session.TryGetReady(0.3, out _));
        Assert.False(File.Exists(cache.PathOf(VideoScrubFrameKey.For(asset, 6))));
    }

    [Fact]
    public async Task ADecoderThrowingBeforeItReturnsATaskStillHasTheVerifiedHandleAndTheGateReleased()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        SynchronouslyThrowingDecoder decoder = new();
        SemaphoreSlim gate = new(1, 1);
        await using VideoScrubPreviewSession session = new VideoScrubPreviewCache(Path.Combine(temp.Path, "scrub"),
            VideoScrubPreviewOptions.Default, decoder, gate).OpenSession(manifest, asset)!;
        Assert.Null(await session.GetSlotAsync(3, Duration, CancellationToken.None).WaitAsync(Patience));
        Stream handed = Assert.Single(decoder.Streams);
        Assert.False(handed.CanRead); // disposed although the decoder never took ownership
        Assert.Equal(1, gate.CurrentCount);
        Assert.False(session.IsUnavailable);
        Assert.Null(await session.GetSlotAsync(3, Duration, CancellationToken.None));
        Assert.Single(decoder.Streams);
    }

    [Fact]
    public async Task DisposalRacingCallerCancellationSettlesEveryRequestAndReleasesEveryHandle()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path, "clip.webm", new byte[] { 1, 2, 3, 4 });
        for (int round = 0; round < 40; round++)
        {
            FakeDecoder decoder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            SemaphoreSlim gate = new(1, 1);
            VideoScrubPreviewSession session = Cache(Path.Combine(temp.Path, "round-" + round), decoder, gate).OpenSession(manifest, asset)!;
            CancellationTokenSource[] hovers = Enumerable.Range(0, 8).Select(_ => new CancellationTokenSource()).ToArray();
            Task<VideoScrubFrame?>[] requests = Enumerable.Range(0, 8)
                .Select(index => Task.Run(() => session.GetSlotAsync(index % 5, Duration, hovers[index].Token))).ToArray();
            Task cancels = Task.Run(() => { for (int index = round % 2; index < hovers.Length; index += 2) hovers[index].Cancel(); });
            Task release = Task.Run(() => decoder.Hold.TrySetResult());
            Task disposing = Task.Run(async () => await session.DisposeAsync());
            await Task.WhenAll(cancels, release, disposing).WaitAsync(Patience);
            foreach (Task<VideoScrubFrame?> request in requests)
            {
                try { await request.WaitAsync(Patience); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
            Assert.Equal(1, gate.CurrentCount);
            Assert.True(decoder.AllStreamsDisposed);
            Assert.False(session.TryGetReady(0.1, out _));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetSlotAsync(1, Duration, CancellationToken.None));
            foreach (CancellationTokenSource hover in hovers) hover.Dispose();
        }
    }

    private static VideoScrubPreviewCache Cache(string root, FakeDecoder decoder, SemaphoreSlim? gate = null, VideoScrubPreviewOptions? options = null) =>
        new(Path.Combine(root, "scrub"), options ?? VideoScrubPreviewOptions.Default, decoder, gate ?? new SemaphoreSlim(1, 1));

    private static int WidthOf(int slot) => 100 + slot;

    private sealed class FakeDecoder : IVideoScrubFrameDecoder
    {
        private int _open;
        internal ConcurrentQueue<(TimeSpan Start, int Width, int Height)> Calls { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Hold { get; init; }
        internal bool IgnoreCancellation { get; init; }
        internal TaskCompletionSource? CleanupStarted { get; init; }
        internal TaskCompletionSource? CleanupHold { get; init; }
        // Releases the verified handle before holding, so a test can rewrite the original on platforms that enforce sharing.
        internal bool ReleaseBeforeHold { get; init; }
        internal volatile bool Fail, Oversized;
        internal bool AllStreamsDisposed => Volatile.Read(ref _open) == 0;

        public async Task<byte[]> DecodeAsync(Stream verifiedVideo, TimeSpan start, int maximumWidth, int maximumHeight, CancellationToken token)
        {
            Interlocked.Increment(ref _open);
            try
            {
                await using (verifiedVideo)
                {
                    Calls.Enqueue((start, maximumWidth, maximumHeight));
                    Started.TrySetResult();
                    Assert.True(verifiedVideo.CanRead);
                    if (Hold != null)
                    {
                        if (ReleaseBeforeHold) await verifiedVideo.DisposeAsync();
                        if (IgnoreCancellation) await Hold.Task;
                        else await Hold.Task.WaitAsync(token);
                    }
                    if (Fail) throw new InvalidDataException("synthetic decode failure");
                    int slot = (int)(start.Ticks / (Duration.Ticks / VideoScrubSlots.Count));
                    return Oversized ? Png(maximumWidth * 2, maximumHeight) : Png(WidthOf(slot), 60);
                }
            }
            finally
            {
                CleanupStarted?.TrySetResult();
                if (CleanupHold != null) await CleanupHold.Task;
                Interlocked.Decrement(ref _open);
            }
        }
    }

    private sealed class SynchronouslyThrowingDecoder : IVideoScrubFrameDecoder
    {
        internal ConcurrentQueue<Stream> Streams { get; } = new();

        // Not async: throws before any task exists and leaves the handle as it got it.
        public Task<byte[]> DecodeAsync(Stream verifiedVideo, TimeSpan start, int maximumWidth, int maximumHeight, CancellationToken token)
        {
            Streams.Enqueue(verifiedVideo);
            throw new InvalidOperationException("synthetic synchronous decoder failure");
        }
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using CancellationTokenSource limit = new(Patience);
        while (!condition())
            await Task.Delay(10, limit.Token);
    }

    private static SKBitmap Decode(byte[] png) => SKBitmap.Decode(png);

    private static byte[] Png(int width, int height)
    {
        using SKBitmap bitmap = new(width, height);
        using SKCanvas canvas = new(bitmap); canvas.Clear(SKColors.SteelBlue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static string ManifestJson(string fileName, byte[] bytes) => JsonSerializer.Serialize(new
    {
        Version = 1, Entries = new Dictionary<string, object> { [fileName] = new {
            MediaIdentifier = "video", SourceUrl = "https://offline.invalid/never-request", FileName = fileName,
            Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) } }
    });

    private static async Task<(MediaAssetSnapshot Manifest, MediaAssetReference Asset)> ManifestAsync(string directory, string fileName, byte[] bytes)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, fileName), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), ManifestJson(fileName, bytes));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        return (manifest, manifest.GetPage().Single());
    }
}

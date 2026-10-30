using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Testing;

namespace MeowChan.DesktopTests;

// Codec tests only. No Avalonia app/window/player, display, audio, network or UI acceptance.
public sealed class GifFrameDecoderTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("animated-loop.gif")]
    [InlineData("finite-loop.gif")]
    [InlineData("disposal-transparency.gif")]
    [InlineData("single-frame.gif")]
    public async Task ActualDecodedPixelsMatchIndependentPillowCompositesInSequentialReverseAndJumpOrders(string name)
    {
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Fixtures(), "manifest.json")));
        JsonElement golden = manifest.RootElement.GetProperty("fixtures").EnumerateArray().Single(row => row.GetProperty("file").GetString() == name);
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(Fixtures(), name));
        Assert.Equal(golden.GetProperty("sha256").GetString(), Hash(bytes));
        await using AssetFixture asset = await AssetFixture.CreateAsync(bytes, "misleading-name.bin");
        await using GifFrameDecoder decoder = await GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None)
            ?? throw new InvalidOperationException("A GIF must be recognized from bytes, not extension.");
        Assert.Equal(golden.GetProperty("width").GetInt32(), decoder.Info.Width);
        Assert.Equal(golden.GetProperty("height").GetInt32(), decoder.Info.Height);
        Assert.Equal("GIF", decoder.Info.Format);
        Assert.False(decoder.Info.StillFrame);
        Assert.Equal(golden.GetProperty("frame_count").GetInt32(), decoder.FrameCount);
        Assert.Equal(golden.GetProperty("repetition_count").GetInt32(), decoder.RepetitionCount);
        var frames = golden.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(decoder.FrameCount, decoder.DurationsMs.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)decoder.DurationsMs)[0] = 999);
        int[] order = Enumerable.Range(0, decoder.FrameCount)
            .Concat(Enumerable.Range(0, decoder.FrameCount).Reverse())
            .Concat(new[] { decoder.FrameCount - 1, 0, decoder.FrameCount - 1 }).ToArray();
        foreach (int index in order)
        {
            using GifDecodedFrame frame = await decoder.DecodeAsync(index, CancellationToken.None).WaitAsync(Deadline);
            AssertFrame(decoder, frame, frames[index]);
        }
    }

    [Fact]
    public async Task PublishedPixelCopyDoesNotAliasTheReusedCanvasAndReturnsItsPoolBufferOnce()
    {
        await using AssetFixture asset = await FixtureAsset("animated-loop.gif");
        await using GifFrameDecoder decoder = (await GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None))!;
        GifDecodedFrame first = await decoder.DecodeAsync(0, CancellationToken.None);
        try
        {
            string original = Hash(first.Pixels.AsSpan(0, first.PixelLength));
            using GifDecodedFrame second = await decoder.DecodeAsync(1, CancellationToken.None);
            Assert.NotSame(first.Pixels, second.Pixels);
            Assert.Equal(original, Hash(first.Pixels.AsSpan(0, first.PixelLength)));
            Assert.NotEqual(original, Hash(second.Pixels.AsSpan(0, second.PixelLength)));
            await decoder.DisposeAsync();
            Assert.Equal(original, Hash(first.Pixels.AsSpan(0, first.PixelLength))); // Frame owns its copy beyond codec lifetime.
        }
        finally { first.Dispose(); first.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => first.Pixels);
    }

    [Fact]
    public async Task OpenClosesVerifiedSourceAndLaterFramesUseTheOwnedSnapshotWithoutReopeningThePath()
    {
        await using AssetFixture asset = await FixtureAsset("animated-loop.gif");
        await using GifFrameDecoder decoder = (await GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None))!;
        using (FileStream exclusive = new(asset.Entry.Asset.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            exclusive.SetLength(8); // Our private copy only. Original verified FileStream must already be closed.
            exclusive.Write(new byte[8]);
        }
        using GifDecodedFrame frame = await decoder.DecodeAsync(2, CancellationToken.None);
        Assert.Equal("e1cd7d26547060b1f6c0b93d4252500cef416454d3559ae86f973ae5737d1ac3", Hash(frame.Pixels.AsSpan(0, frame.PixelLength)));
    }

    [Fact]
    public async Task ManifestVerificationCannotBeBypassedByAValidGifHeaderOrExtension()
    {
        await using AssetFixture asset = await FixtureAsset("animated-loop.gif");
        byte[] changed = await File.ReadAllBytesAsync(asset.Entry.Asset.Path);
        changed[^2] ^= 1;
        await File.WriteAllBytesAsync(asset.Entry.Asset.Path, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None));
    }

    [Fact]
    public async Task ActualNonGifReturnsNullEvenWithGifExtension()
    {
        // Valid fixed synthetic 1x1 PNG, not an extension-based GIF shortcut.
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1sAAAAASUVORK5CYII=");
        await using AssetFixture asset = await AssetFixture.CreateAsync(png, "not-a-gif.gif");
        Assert.Null(await GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None));
    }

    [Fact]
    public async Task EightByteTruncatedFixtureCannotBecomeAStillFrameOrIncompleteAnimation()
    {
        await using AssetFixture asset = await FixtureAsset("truncated.gif");
        await Assert.ThrowsAsync<InvalidDataException>(() => GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None));
    }

    [Fact]
    public async Task IncompleteImageDataIsRejectedRatherThanPublishingIncompleteInputPixels()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(Fixtures(), "single-frame.gif"));
        await using AssetFixture asset = await AssetFixture.CreateAsync(bytes[..^5]);
        GifFrameDecoder? decoder = null;
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                decoder = await GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None);
                Assert.NotNull(decoder);
                using GifDecodedFrame frame = await decoder.DecodeAsync(0, CancellationToken.None);
            });
        }
        finally { if (decoder != null) await decoder.DisposeAsync(); }
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 100)]
    [InlineData(10, 100)]
    [InlineData(19, 100)]
    [InlineData(20, 20)]
    [InlineData(100, 100)]
    [InlineData(5000, 5000)]
    public void BrowserStyleMinimumDurationDoesNotRewriteLegitimateLongFrames(int raw, int expected) =>
        Assert.Equal(expected, GifFrameDecoder.NormalizeDuration(raw));

    [Fact]
    public void ExactResourceBoundariesAreAcceptedAndAnyExcessOrInvalidDimensionIsRejected()
    {
        GifFrameDecoder.ValidateLimits(GifFrameDecoder.MaximumBytes, 4000, 4000, 1024);
        Assert.Throws<InvalidDataException>(() => GifFrameDecoder.ValidateLimits(GifFrameDecoder.MaximumBytes + 1, 1, 1, 1));
        Assert.Throws<InvalidDataException>(() => GifFrameDecoder.ValidateLimits(1, 4001, 4000, 1));
        Assert.Throws<InvalidDataException>(() => GifFrameDecoder.ValidateLimits(1, 1, 1, 1025));
        Assert.Throws<InvalidDataException>(() => GifFrameDecoder.ValidateLimits(1, 0, 1, 1));
        Assert.Throws<InvalidDataException>(() => GifFrameDecoder.ValidateLimits(1, 1, -1, 1));
        Assert.Throws<InvalidDataException>(() => GifFrameDecoder.ValidateLimits(1, int.MaxValue, int.MaxValue, 1));
    }

    [Fact]
    public async Task ActualOversizedCanvasHeaderAndActual1025FrameGifAreRejectedBeforeCanvasAllocation()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(Fixtures(), "animated-loop.gif"));
        bytes[6] = bytes[7] = bytes[8] = bytes[9] = 255;
        await using (AssetFixture huge = await AssetFixture.CreateAsync(bytes))
            await Assert.ThrowsAsync<InvalidDataException>(() => GifFrameDecoder.OpenAsync(huge.Entry, CancellationToken.None));
        await using (AssetFixture many = await AssetFixture.CreateAsync(ManyFrames(1025)))
            await Assert.ThrowsAsync<InvalidDataException>(() => GifFrameDecoder.OpenAsync(many.Entry, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedDeclaredSourceIsRejectedBeforeOpeningAndCanceledOpenNeverPublishesACodec()
    {
        await using AssetFixture asset = await FixtureAsset("animated-loop.gif");
        GalleryEntry tooLarge = asset.Entry with { Asset = asset.Entry.Asset with { Length = GifFrameDecoder.MaximumBytes + 1 } };
        await Assert.ThrowsAsync<InvalidDataException>(() => GifFrameDecoder.OpenAsync(tooLarge, CancellationToken.None));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GifFrameDecoder.OpenAsync(asset.Entry, canceled.Token));
    }

    [Fact]
    public async Task InvalidFrameIndicesAndPreCanceledRequestsNeverEnterTheNativeOperation()
    {
        await using AssetFixture asset = await FixtureAsset("animated-loop.gif");
        await using GifFrameDecoder decoder = (await GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None))!;
        int calls = 0;
        decoder.BeforeNativeDecode = () => Interlocked.Increment(ref calls);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = decoder.DecodeAsync(-1, CancellationToken.None); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = decoder.DecodeAsync(decoder.FrameCount, CancellationToken.None); });
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.DurationMs(-1));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoder.DecodeAsync(0, canceled.Token));
        Assert.Equal(0, calls);
        using GifDecodedFrame valid = await decoder.DecodeAsync(0, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(0, valid.Index);
    }

    [Fact]
    public async Task CanceledInFlightFrameIsNotPublishedAndFollowingDecodeResetsTheCanvas()
    {
        await using AssetFixture asset = await FixtureAsset("disposal-transparency.gif");
        await using GifFrameDecoder decoder = (await GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None))!;
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        decoder.BeforeNativeDecode = () => { entered.TrySetResult(); release.Wait(); };
        using CancellationTokenSource canceled = new();
        Task<GifDecodedFrame> stale = decoder.DecodeAsync(1, canceled.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            canceled.Cancel();
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale.WaitAsync(Deadline));
        decoder.BeforeNativeDecode = null;
        using GifDecodedFrame current = await decoder.DecodeAsync(2, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal("4a8bce7ab3afb6db15089af33c9f22041db6081699d6973b19184dee2e1dd526", Hash(current.Pixels.AsSpan(0, current.PixelLength)));
    }

    [Fact]
    public async Task DisposeCancelsQueuedWaitersButCannotFreeCodecUntilTheOwnedOperationActuallyReturns()
    {
        await using AssetFixture asset = await FixtureAsset("animated-loop.gif");
        await using GifFrameDecoder decoder = (await GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None))!;
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        decoder.BeforeNativeDecode = () =>
        {
            Assert.True(Thread.CurrentThread.IsThreadPoolThread);
            entered.TrySetResult(); release.Wait();
        };
        Task<GifDecodedFrame> active = decoder.DecodeAsync(0, CancellationToken.None);
        Task? drain = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Task<GifDecodedFrame> queued = decoder.DecodeAsync(1, CancellationToken.None);
            drain = decoder.DisposeAsync().AsTask();
            Assert.False(drain.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
            Assert.False(drain.IsCompleted); // Cancellation is not a native/canvas release certificate.
            Assert.Equal(3, decoder.FrameCount); // Immutable metadata remains readable.
            Assert.Throws<ObjectDisposedException>(() => { _ = decoder.DecodeAsync(2, CancellationToken.None); });
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(Deadline));
        await (drain ?? decoder.DisposeAsync().AsTask()).WaitAsync(Deadline);
        Assert.Same(drain, decoder.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task CanceledFactoryWaitingBehindAnotherGifOperationDoesNotBlockItsOwner()
    {
        await using AssetFixture asset = await FixtureAsset("animated-loop.gif");
        await using GifFrameDecoder decoder = (await GifFrameDecoder.OpenAsync(asset.Entry, CancellationToken.None))!;
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        decoder.BeforeNativeDecode = () => { entered.TrySetResult(); release.Wait(); };
        Task<GifDecodedFrame> active = decoder.DecodeAsync(0, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            using CancellationTokenSource canceled = new();
            Task<GifFrameDecoder?> waiting = GifFrameDecoder.OpenAsync(asset.Entry, canceled.Token);
            await Task.Delay(50);
            Assert.False(waiting.IsCompleted);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Deadline));
        }
        finally { release.Set(); }
        using GifDecodedFrame frame = await active.WaitAsync(Deadline);
        Assert.Equal(0, frame.Index);
    }

    private static void AssertFrame(GifFrameDecoder decoder, GifDecodedFrame frame, JsonElement golden)
    {
        Assert.Equal(golden.GetProperty("index").GetInt32(), frame.Index);
        Assert.Equal(decoder.Info.Width, frame.Width); Assert.Equal(decoder.Info.Height, frame.Height);
        Assert.Equal(decoder.DurationMs(frame.Index), frame.DurationMs);
        Assert.Equal(golden.GetProperty("effective_duration_ms").GetInt32(), frame.DurationMs);
        Assert.True(frame.RowBytes >= frame.Width * 4);
        // Canonical hashes exclude implementation-specific trailing row padding.
        byte[] tight = new byte[frame.Width * frame.Height * 4];
        for (int row = 0; row < frame.Height; row++)
            frame.Pixels.AsSpan(row * frame.RowBytes, frame.Width * 4).CopyTo(tight.AsSpan(row * frame.Width * 4));
        Assert.Equal(golden.GetProperty("bgra_premul_sha256").GetString(), Hash(tight));
    }

    private static byte[] ManyFrames(int count)
    {
        // Original generated 1x1 GIF: two-color global table, valid clear/0/end LZW.
        using MemoryStream bytes = new();
        bytes.Write(Encoding.ASCII.GetBytes("GIF89a"));
        bytes.Write(new byte[] { 1, 0, 1, 0, 128, 0, 0, 0, 0, 0, 255, 0, 0 });
        byte[] frame = { 33, 249, 4, 4, 10, 0, 0, 0, 44, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 68, 1, 0 };
        for (int index = 0; index < count; index++) bytes.Write(frame);
        bytes.WriteByte(59);
        return bytes.ToArray();
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Task<AssetFixture> FixtureAsset(string name) => AssetFixture.CreateAsync(File.ReadAllBytes(Path.Combine(Fixtures(), name)));
    private static string Fixtures()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (DirectoryInfo? directory = new(start); directory != null; directory = directory.Parent)
            {
                string fixtures = Path.Combine(directory.FullName, "tests", "Fixtures", "gif-playback");
                if (File.Exists(Path.Combine(fixtures, "manifest.json"))) return fixtures;
            }
        throw new DirectoryNotFoundException("Source-bound tests/Fixtures/gif-playback was not found; do not substitute external images.");
    }

    private sealed class AssetFixture : IAsyncDisposable
    {
        private readonly string _directory;
        internal GalleryEntry Entry { get; }
        private AssetFixture(string directory, GalleryEntry entry) { _directory = directory; Entry = entry; }
        internal static async Task<AssetFixture> CreateAsync(byte[] bytes, string name = "fixture.gif")
        {
            string directory = TemporaryWorkspacePaths.CreateDirectory("gif-decoder-unit-");
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
                var record = new { MediaIdentifier = name, SourceUrl = "https://offline.invalid/" + name,
                    FileName = name, Length = bytes.Length, Sha256 = Hash(bytes), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
                await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
                { Version = 1, Entries = new Dictionary<string, object> { [name] = record } }));
                MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
                MediaAssetReference asset = manifest.GetPage().Single();
                return new(directory, new(new GalleryAssetKey(new ResourceId("fixture", "b", "gif"), directory, asset.AssetId),
                    manifest, asset, null, null, false, false));
            }
            catch { Directory.Delete(directory, recursive: true); throw; }
        }
        public ValueTask DisposeAsync() { Directory.Delete(_directory, recursive: true); return ValueTask.CompletedTask; }
    }
}

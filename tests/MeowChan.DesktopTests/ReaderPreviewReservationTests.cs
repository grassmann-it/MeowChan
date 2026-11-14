using Avalonia;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using SkiaSharp;

namespace MeowChan.DesktopTests;

// Stable reader previews: the frame is reserved at the picture's size before its pixels exist (size memory, header read), the decoded
// thumbnail keeps that reservation exactly, preloading picks a bounded window around the viewport. The GUI evidence is the
// reader-preview smoke (post height before and after the preview arrives).
public sealed class ReaderPreviewReservationTests
{
    [Theory]
    [InlineData(1920, 1080, 240, 135)]   // 16:9 decoded at the reader width
    [InlineData(1000, 333, 240, 80)]     // a rounded height: 79.92 drawn as 80 pixels
    [InlineData(400, 600, 120, 180)]     // portrait limited by the height cap
    [InlineData(5, 900, 1, 180)]         // a one-pixel strip
    public void ADecodeThatAgreesWithTheReservedSizeKeepsItExactly(int width, int height, int decodedWidth, int decodedHeight)
    {
        double reserved = (double)width / height;
        Assert.Equal(reserved, ReaderPreviewReservation.Settle(reserved, decodedWidth, decodedHeight));
    }

    [Fact]
    public void ADifferentPictureOrNoReservationTakesTheDecodedAspect()
    {
        Assert.Equal(120d / 180, ReaderPreviewReservation.Settle(16d / 9, 120, 180));
        Assert.Equal(240d / 135, ReaderPreviewReservation.Settle(null, 240, 135));
        Assert.Equal(1.5, ReaderPreviewReservation.Settle(1.5, 0, 0));
        Assert.Null(ReaderPreviewReservation.Settle(null, 0, 10));
        Assert.Equal(2d, ReaderPreviewReservation.Settle(double.NaN, 20, 10));
    }

    [Theory]
    [InlineData(1920, 1080, 240, 135, false)]
    [InlineData(1000, 333, 240, 80, false)]
    [InlineData(400, 600, 120, 180, false)]
    [InlineData(400, 600, 480, 720, true)]    // the large reader decodes up to 1040 x 720
    [InlineData(1000, 333, 239, 80, false)]   // a decoder that floors instead of rounding
    public void TheMeasuredHeightBeforeAndAfterTheLoadIsEqualWhenTheSizeIsKnown(int width, int height, int decodedWidth, int decodedHeight, bool large)
    {
        double reserved = (double)width / height;
        MediaFramePanel frame = new() { Large = large, Aspect = reserved, Children = { new Border() } };
        frame.Measure(new Size(600, double.PositiveInfinity));
        Size before = frame.DesiredSize;

        frame.Aspect = ReaderPreviewReservation.Settle(frame.Aspect, decodedWidth, decodedHeight);
        frame.InvalidateMeasure();
        frame.Measure(new Size(600, double.PositiveInfinity));
        Assert.Equal(before, frame.DesiredSize);

        // Without the reservation the wide-screen box would have been measured first (the old jump), except for 16:9 itself.
        Size legacy = MediaSlotGeometry.Fit(null, 600, MediaFramePanel.MaximumWidth(large), MediaFramePanel.MaximumHeight(large));
        Assert.Equal(Math.Abs(reserved - 16d / 9) > 0.01, Math.Abs(legacy.Height - before.Height) > 0.5);
    }

    [Fact]
    public void ARowRemembersAndAnnouncesALearnedSizeOnce()
    {
        ArchiveAttachmentRow row = new(new CapturedAttachment(0, "image", OriginalFileName: "a.png"));
        List<double> remembered = new();
        int changes = 0;
        row.RememberAspect = remembered.Add;
        row.KnownAspectChanged += () => changes++;
        row.KnownAspect = 1.5;
        row.KnownAspect = 1.5;
        Assert.Equal(new[] { 1.5 }, remembered);
        Assert.Equal(1, changes);
        row.KnownAspect = null;
        Assert.Equal(2, changes);
        Assert.Single(remembered);
    }

    [Fact]
    public void ThePrefetchWindowSurroundsTheViewportWithinThePage()
    {
        Assert.Equal(new ReaderPreviewReservation.PostWindow(0, 18), ReaderPreviewReservation.PrefetchWindow(5, 9, 50, 8));
        Assert.Equal(new ReaderPreviewReservation.PostWindow(32, 50), ReaderPreviewReservation.PrefetchWindow(40, 45, 50, 8));
        Assert.Equal(new ReaderPreviewReservation.PostWindow(16, 28), ReaderPreviewReservation.PrefetchWindow(20, 23, 50, 4));
        Assert.Equal(0, ReaderPreviewReservation.PrefetchWindow(5, 9, 50, 0).Count);
        Assert.Equal(0, ReaderPreviewReservation.PrefetchWindow(-1, -1, 50, 8).Count);
        Assert.Equal(0, ReaderPreviewReservation.PrefetchWindow(0, 0, 0, 8).Count);
        // A stale index past the end of a shorter page is clamped, never out of range.
        Assert.Equal(new ReaderPreviewReservation.PostWindow(1, 10), ReaderPreviewReservation.PrefetchWindow(30, 40, 10, 8));
    }

    [Fact]
    public void PreloadingGoesNearestFirstTowardsTheScrollDirectionAndSkipsVisiblePosts()
    {
        ReaderPreviewReservation.PostWindow window = ReaderPreviewReservation.PrefetchWindow(10, 12, 50, 2);
        Assert.Equal(new[] { 13, 9, 14, 8 }, ReaderPreviewReservation.NearestFirst(10, 12, window, forward: true));
        Assert.Equal(new[] { 9, 13, 8, 14 }, ReaderPreviewReservation.NearestFirst(10, 12, window, forward: false));
        ReaderPreviewReservation.PostWindow top = ReaderPreviewReservation.PrefetchWindow(0, 1, 50, 3);
        Assert.Equal(new[] { 2, 3, 4 }, ReaderPreviewReservation.NearestFirst(0, 1, top, forward: false));
        Assert.Empty(ReaderPreviewReservation.NearestFirst(0, 1, new ReaderPreviewReservation.PostWindow(0, 0), true));
    }

    [Fact]
    public void PreloadingStaysWithinHalfThePreviewCacheAndOnlyWherePreviewsAreKept()
    {
        Assert.Equal(192, ReaderPreviewReservation.PrefetchBudget(MediaThumbnailCache.DefaultCapacity));
        Assert.Equal(16, ReaderPreviewReservation.PrefetchBudget(32));
        Assert.Equal(0, ReaderPreviewReservation.PrefetchBudget(1));
        Assert.True(ReaderPreviewReservation.Prefetches(largePreview: false, posts: 8));
        Assert.False(ReaderPreviewReservation.Prefetches(largePreview: true, posts: 8));
        Assert.False(ReaderPreviewReservation.Prefetches(largePreview: false, posts: 0));
    }

    [Fact]
    public void TheSettingsHaveDefaultsAndInclusiveBounds()
    {
        Assert.Equal((0, 8, 50), (ReaderPreviewReservation.PrefetchPostsMinimum, ReaderPreviewReservation.PrefetchPostsDefault, ReaderPreviewReservation.PrefetchPostsMaximum));
        Assert.Equal((1_000, 20_000, 200_000), (ReaderPreviewReservation.SizeMemoryMinimum, ReaderPreviewReservation.SizeMemoryDefault, ReaderPreviewReservation.SizeMemoryMaximum));
        Assert.Equal(50, ReaderPreviewReservation.ClampPrefetchPosts(999));
        Assert.Equal(0, ReaderPreviewReservation.ClampPrefetchPosts(-3));
        Assert.Equal(1_000, ReaderPreviewReservation.ClampSizeMemory(1));
        Assert.Equal(512 * 1024, ReaderPreviewReservation.HeaderReadCeilingBytes);
        Config config = new();
        Assert.Equal(8, config.ReaderPreviewPrefetchPosts);
        Assert.Equal(20_000, config.ReaderPreviewSizeMemory);
    }

    [Fact]
    public void TheSizeMemoryNeverHoldsMoreThanItsBoundAndKeepsWhatIsUsed()
    {
        ReaderPreviewAspectMemo memo = new(10);
        memo.Remember("kept", 2);
        for (int index = 0; index < 100; index++)
        {
            memo.Remember("item" + index, 1 + index / 100d);
            Assert.True(memo.Count <= 10);
            Assert.True(memo.TryGet("kept", out double kept)); // used all along: never forgotten
            Assert.Equal(2, kept);
        }
        Assert.False(memo.TryGet("item0", out _));
        Assert.True(memo.TryGet("item99", out double last));
        Assert.Equal(1.99, last, 5);

        memo.Capacity = 4;
        Assert.True(memo.Count <= 4);
        memo.Remember("bad", double.NaN); memo.Remember("zero", 0); memo.Remember("negative", -1);
        Assert.False(memo.TryGet("bad", out _) || memo.TryGet("zero", out _) || memo.TryGet("negative", out _));
        memo.Clear();
        Assert.Equal(0, memo.Count);
    }

    [Fact]
    public void TheIdentityOfAnAttachmentsBytesKeysItsSize()
    {
        ResourceId thread = new("4chan", "g", "1");
        CapturedAttachment legacy = new(0, "123", ExpectedMd5: "md5", ExpectedSize: 10);
        string? key = ReaderPreviewReservation.AspectKey(thread, "5", legacy);
        Assert.NotNull(key);
        Assert.Equal(key, ReaderPreviewReservation.AspectKey(thread, "5", legacy with { }));
        Assert.NotEqual(key, ReaderPreviewReservation.AspectKey(thread, "6", legacy));
        Assert.NotEqual(key, ReaderPreviewReservation.AspectKey(thread, "5", legacy with { Position = 1 }));
        Assert.NotEqual(key, ReaderPreviewReservation.AspectKey(thread, "5", legacy with { ExpectedMd5 = "other" }));
        Assert.NotEqual(key, ReaderPreviewReservation.AspectKey(thread, "5", legacy with { AttachmentRevisionKey = "revision" }));
        Assert.Null(ReaderPreviewReservation.AspectKey(thread, "5", new CapturedAttachment(0, null, IsDeleted: true)));
        Assert.Null(ReaderPreviewReservation.AspectKey(thread, "5", new CapturedAttachment(0, null)));
    }

    [Fact]
    public void TheHeaderReadLearnsTheShownSizeWithoutDecodingAndRefusesChangedFiles()
    {
        using TempDirectory directory = new();
        string png = Path.Combine(directory.Path, "wide.png");
        byte[] bytes = Encode(1800, 1000, SKEncodedImageFormat.Png);
        File.WriteAllBytes(png, bytes);
        Assert.Equal(1.8, ReaderPreviewHeaderProbe.TryReadAspect(png, bytes.Length)!.Value, 6);
        string jpeg = Path.Combine(directory.Path, "tall.jpg");
        byte[] jpegBytes = Encode(300, 900, SKEncodedImageFormat.Jpeg);
        File.WriteAllBytes(jpeg, jpegBytes);
        Assert.Equal(1d / 3, ReaderPreviewHeaderProbe.TryReadAspect(jpeg, jpegBytes.Length)!.Value, 6);

        Assert.Null(ReaderPreviewHeaderProbe.TryReadAspect(png, bytes.Length + 1)); // not the file the manifest recorded
        string video = Path.Combine(directory.Path, "clip.webm");
        File.WriteAllBytes(video, bytes);
        Assert.Null(ReaderPreviewHeaderProbe.TryReadAspect(video, bytes.Length)); // videos have posters, not headers here
        string damaged = Path.Combine(directory.Path, "damaged.png");
        File.WriteAllBytes(damaged, new byte[64]);
        Assert.Null(ReaderPreviewHeaderProbe.TryReadAspect(damaged, 64));
        Assert.Null(ReaderPreviewHeaderProbe.TryReadAspect(Path.Combine(directory.Path, "missing.png"), 10));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReaderPreviewHeaderProbe.TryReadAspect(png, bytes.Length, canceled.Token));
    }

    [Fact]
    public void TheHeaderWindowEndsAtItsLimit()
    {
        using MemoryStream inner = new(new byte[1000]);
        using ReaderPreviewHeaderProbe.HeaderWindowStream window = new(inner, 100);
        Assert.Equal(100, window.Length);
        byte[] buffer = new byte[400];
        Assert.Equal(100, window.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, window.Read(buffer, 0, buffer.Length));
        Assert.Equal(100, window.Seek(500, SeekOrigin.Begin));
        Assert.Equal(40, window.Seek(-60, SeekOrigin.End));
        Assert.Equal(60, window.Read(buffer, 0, buffer.Length));
    }

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(format, 90);
        return data.ToArray();
    }
}

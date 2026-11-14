using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using MeowChan.Desktop;
using SkiaSharp;

namespace MeowChan.DesktopTests;

// EXIF Orientation of local images: phone photos are stored sideways with a tag; every decode path must show them upright.
// Synthetic fixtures only: a 64x32 JPEG of four flat quadrants (top left red, top right green, bottom left blue, bottom right
// yellow, 16-pixel aligned so chroma never bleeds) with a minimal EXIF APP1 segment carrying the Orientation tag.
public sealed class ExifOrientationTests
{
    private const int W = 64, H = 32;

    // Quadrant colours as they must appear after the tag is applied: top-left, top-right, bottom-left, bottom-right.
    // Orientation 1..8 as in the EXIF specification (2 = mirror, 3 = turn 180, 4 = flip, 5 = transpose, 6 = turn 90 clockwise,
    // 7 = transverse, 8 = turn 270 clockwise).
    private static readonly (string Corners, bool Swapped)[] Expected =
    [
        ("RGBY", false), ("GRYB", false), ("YBGR", false), ("BYRG", false),
        ("RBGY", true), ("BRYG", true), ("YGBR", true), ("GYRB", true),
    ];

    public static TheoryData<int> Orientations => new() { 1, 2, 3, 4, 5, 6, 7, 8 };

    // ---- fixtures ----

    private static SKBitmap Quadrants()
    {
        SKBitmap bitmap = new(W, H, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                bitmap.SetPixel(x, y, y < H / 2 ? (x < W / 2 ? new SKColor(255, 0, 0) : new SKColor(0, 255, 0))
                    : (x < W / 2 ? new SKColor(0, 0, 255) : new SKColor(255, 255, 0)));
        return bitmap;
    }

    internal static byte[] PlainJpeg(SKBitmap? bitmap = null)
    {
        using SKBitmap? own = bitmap == null ? Quadrants() : null;
        using SKData data = (bitmap ?? own!).Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }

    /// <summary>EXIF APP1 (big-endian TIFF, one IFD0 entry: Orientation, SHORT) for <paramref name="orientation"/>.</summary>
    private static byte[] ExifSegment(int orientation)
    {
        byte[] segment =
        [
            0xFF, 0xE1, 0x00, 0x00, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0, 0, 0, 8, // TIFF header, IFD0 at offset 8
            0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, // one entry: tag 0x0112, SHORT, count 1, value
            0, 0, 0, 0, // no next IFD
        ];
        segment[3] = (byte)(segment.Length - 2);
        return segment;
    }

    /// <summary>Inserts <paramref name="segment"/> right after the JPEG start-of-image marker.</summary>
    private static byte[] WithSegment(byte[] jpeg, byte[] segment)
    {
        byte[] result = new byte[jpeg.Length + segment.Length];
        result[0] = jpeg[0]; result[1] = jpeg[1];
        segment.CopyTo(result, 2);
        Array.Copy(jpeg, 2, result, 2 + segment.Length, jpeg.Length - 2);
        return result;
    }

    internal static byte[] Jpeg(int orientation) => WithSegment(PlainJpeg(), ExifSegment(orientation));

    private static string Letters(SKBitmap bitmap) => string.Concat(
        Letter(bitmap.GetPixel(2, 2)), Letter(bitmap.GetPixel(bitmap.Width - 3, 2)),
        Letter(bitmap.GetPixel(2, bitmap.Height - 3)), Letter(bitmap.GetPixel(bitmap.Width - 3, bitmap.Height - 3)));

    private static string Letters(Bitmap bitmap)
    {
        using MemoryStream png = new();
        bitmap.Save(png);
        png.Position = 0;
        using SKBitmap decoded = SKBitmap.Decode(png);
        return Letters(decoded);
    }

    private static char Letter(SKColor c) =>
        c.Red > 160 && c.Green < 96 && c.Blue < 96 ? 'R' : c.Green > 160 && c.Red < 96 && c.Blue < 96 ? 'G'
        : c.Blue > 160 && c.Red < 96 && c.Green < 96 ? 'B' : c.Red > 160 && c.Green > 160 && c.Blue < 96 ? 'Y' : '?';

    private static readonly object PlatformGate = new();
    private static void Platform()
    {
        lock (PlatformGate)
            if (Application.Current == null)
                AppBuilder.Configure<Application>().UseSkia().UseStandardRuntimePlatformSubsystem().UseHarfBuzz()
                    .UseWindowingSubsystem(() => { }).SetupWithoutStarting();
    }

    /// <summary>Counts the bytes handed to the decoder.</summary>
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal long BytesRead;
        public override int Read(byte[] buffer, int offset, int count) { int n = base.Read(buffer, offset, count); BytesRead += n; return n; }
        public override int Read(Span<byte> buffer) { int n = base.Read(buffer); BytesRead += n; return n; }
    }

    // ---- the transform ----

    [Theory, MemberData(nameof(Orientations))]
    public void TheCodecReadsTheTagAndApplyMovesTheQuadrantsUpright(int orientation)
    {
        byte[] bytes = Jpeg(orientation);
        using MemoryStream stream = new(bytes);
        using SKCodec codec = SKCodec.Create(stream)!;
        Assert.Equal((SKEncodedOrigin)orientation, codec.EncodedOrigin);
        using SKBitmap raw = SKBitmap.Decode(codec, new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        Assert.Equal("RGBY", Letters(raw)); // stored order, untouched by the codec: this is what used to be shown

        using SKBitmap upright = ImageOrientation.Apply(raw, codec.EncodedOrigin);
        (string corners, bool swapped) = Expected[orientation - 1];
        Assert.Equal(swapped ? (H, W) : (W, H), (upright.Width, upright.Height));
        Assert.Equal(corners, Letters(upright));
    }

    [Fact]
    public void AnUprightImageIsNeverCopied()
    {
        using SKBitmap raw = Quadrants();
        Assert.Same(raw, ImageOrientation.Apply(raw, SKEncodedOrigin.TopLeft));
        Assert.Same(raw, ImageOrientation.Apply(raw, SKEncodedOrigin.Default));
        Assert.Same(raw, ImageOrientation.Apply(raw, (SKEncodedOrigin)0));
        Assert.Same(raw, ImageOrientation.Apply(raw, (SKEncodedOrigin)9));
    }

    [Fact]
    public void PixelsKeepTheirValueAndAlphaWhileTheyMove()
    {
        using SKBitmap raw = new(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
                raw.SetPixel(x, y, new SKColor((byte)(10 * (x + 1)), (byte)(20 * (y + 1)), (byte)(x * 7 + y * 3), (byte)(200 + x + y)));
        using SKBitmap turned = ImageOrientation.Apply(raw, SKEncodedOrigin.RightTop); // 90 clockwise: (x, y) -> (h - 1 - y, x)
        Assert.Equal((2, 3), (turned.Width, turned.Height));
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(raw.GetPixel(x, y), turned.GetPixel(1 - y, x));
        using SKBitmap twice = ImageOrientation.Apply(ImageOrientation.Apply(raw, SKEncodedOrigin.BottomRight), SKEncodedOrigin.BottomRight);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(raw.GetPixel(x, y), twice.GetPixel(x, y)); // turning by 180 twice is the identity
    }

    // ---- the decode paths ----

    [Theory, MemberData(nameof(Orientations))]
    public void EveryGalleryAndThumbnailDecodePathShowsTheImageUpright(int orientation)
    {
        Platform();
        byte[] bytes = Jpeg(orientation);
        (string corners, bool swapped) = Expected[orientation - 1];
        PixelSize shown = swapped ? new PixelSize(H, W) : new PixelSize(W, H);

        GalleryImageInfo info = GalleryImageDecoder.Inspect(new MemoryStream(bytes));
        Assert.Equal((shown.Width, shown.Height), (info.Width, info.Height)); // zoom, pan and fit are computed on the shown size

        using (Bitmap original = ImageOrientation.DecodeOriginal(new MemoryStream(bytes)))
        {
            Assert.Equal(shown, original.PixelSize);
            Assert.Equal(corners, Letters(original));
        }
        using (Bitmap fit = GalleryImageDecoder.DecodeFit(new MemoryStream(bytes), shown))
        {
            Assert.Equal(shown, fit.PixelSize);
            Assert.Equal(corners, Letters(fit));
        }
        using (Bitmap thumbnail = ImageOrientation.DecodeToWidth(new MemoryStream(bytes), shown.Width / 2))
        {
            Assert.Equal(new PixelSize(shown.Width / 2, shown.Height / 2), thumbnail.PixelSize);
            Assert.Equal(corners, Letters(thumbnail));
        }
    }

    [Theory]
    [InlineData(1, 40, 40, 40, 20)]
    [InlineData(6, 40, 40, 20, 40)]
    [InlineData(8, 32, 32, 16, 32)]
    public void AFitIsComputedOnTheShownSizeAndDecodedToExactlyThatSize(int orientation, int viewportWidth, int viewportHeight, int expectedWidth, int expectedHeight)
    {
        Platform();
        byte[] bytes = Jpeg(orientation);
        GalleryImageInfo info = GalleryImageDecoder.Inspect(new MemoryStream(bytes));
        PixelSize target = GalleryFitTarget.Calculate(info, new GalleryDecodeRequest.Fit(viewportWidth, viewportHeight));
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), target);
        using Bitmap fit = GalleryImageDecoder.DecodeFit(new MemoryStream(bytes), target);
        Assert.Equal(target, fit.PixelSize);
        Assert.Equal(Expected[orientation - 1].Corners, Letters(fit));
    }

    // ---- malformed and other formats ----

    public static TheoryData<string> Malformed => new() { "orientation0", "orientation9", "orientation255", "truncated", "badByteOrder", "hugeEntryCount", "loopingOffset", "noExifHeader" };

    private static byte[] MalformedSegment(string kind)
    {
        byte[] good = ExifSegment(6);
        switch (kind)
        {
            case "orientation0": return ExifSegment(0);
            case "orientation9": return ExifSegment(9);
            case "orientation255": return ExifSegment(255);
            case "truncated": { byte[] cut = good[..14]; cut[3] = (byte)(cut.Length - 2); return cut; }
            case "badByteOrder": { byte[] bad = (byte[])good.Clone(); bad[10] = bad[11] = (byte)'X'; return bad; }
            case "hugeEntryCount": { byte[] bad = (byte[])good.Clone(); bad[18] = 0xFF; bad[19] = 0xFF; return bad; }
            case "loopingOffset": { byte[] bad = (byte[])good.Clone(); bad[14] = 0; bad[15] = 0; bad[16] = 0; bad[17] = 0; return bad; } // IFD0 offset 0
            default: { byte[] bad = (byte[])good.Clone(); bad[4] = (byte)'N'; return bad; }
        }
    }

    [Theory, MemberData(nameof(Malformed))]
    public void MalformedExifNeverBreaksTheDecodeAndLeavesTheImageUpright(string kind)
    {
        Platform();
        byte[] bytes = WithSegment(PlainJpeg(), MalformedSegment(kind));
        SKEncodedOrigin origin = ImageOrientation.ReadOrigin(new MemoryStream(bytes));
        if (kind.StartsWith("orientation", StringComparison.Ordinal)) Assert.False(ImageOrientation.NeedsTransform(origin));
        if (kind != "truncated" && kind != "hugeEntryCount") Assert.Equal(SKEncodedOrigin.TopLeft, origin);
        // Whatever the origin comes out as, nothing throws and the shown size matches the bitmap that is decoded.
        GalleryImageInfo info = GalleryImageDecoder.Inspect(new MemoryStream(bytes));
        using Bitmap original = ImageOrientation.DecodeOriginal(new MemoryStream(bytes));
        Assert.Equal((info.Width, info.Height), (original.PixelSize.Width, original.PixelSize.Height));
        using Bitmap fit = GalleryImageDecoder.DecodeFit(new MemoryStream(bytes), new PixelSize(info.Width, info.Height));
        Assert.Equal(original.PixelSize, fit.PixelSize);
        if (!ImageOrientation.NeedsTransform(origin)) Assert.Equal("RGBY", Letters(original));
    }

    [Fact]
    public void ARandomOrEmptyStreamIsAnOrdinaryUnsupportedImage()
    {
        Assert.Equal(SKEncodedOrigin.TopLeft, ImageOrientation.ReadOrigin(new MemoryStream([])));
        Assert.Equal(SKEncodedOrigin.TopLeft, ImageOrientation.ReadOrigin(new MemoryStream(new byte[300])));
        Assert.Throws<InvalidDataException>(() => GalleryImageDecoder.Inspect(new MemoryStream(new byte[300])));
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void PngAndWebpWithoutExifKeepTheirPixelsAndSize(SKEncodedImageFormat format)
    {
        Platform();
        using SKBitmap source = Quadrants();
        using SKData encoded = source.Encode(format, 100);
        byte[] bytes = encoded.ToArray();
        Assert.Equal(SKEncodedOrigin.TopLeft, ImageOrientation.ReadOrigin(new MemoryStream(bytes)));
        GalleryImageInfo info = GalleryImageDecoder.Inspect(new MemoryStream(bytes));
        Assert.Equal((W, H), (info.Width, info.Height));
        using Bitmap original = ImageOrientation.DecodeOriginal(new MemoryStream(bytes));
        Assert.Equal(new PixelSize(W, H), original.PixelSize);
        Assert.Equal("RGBY", Letters(original));
        using Bitmap fit = GalleryImageDecoder.DecodeFit(new MemoryStream(bytes), new PixelSize(W / 2, H / 2));
        Assert.Equal(new PixelSize(W / 2, H / 2), fit.PixelSize);
        Assert.Equal("RGBY", Letters(fit));
        using Bitmap thumbnail = ImageOrientation.DecodeToWidth(new MemoryStream(bytes), W / 4);
        Assert.Equal(W / 4, thumbnail.PixelSize.Width);
        Assert.Equal("RGBY", Letters(thumbnail));
    }

    [Fact]
    public void AJpegWithoutAnyExifIsUnchanged()
    {
        Platform();
        byte[] bytes = PlainJpeg();
        Assert.Equal(SKEncodedOrigin.TopLeft, ImageOrientation.ReadOrigin(new MemoryStream(bytes)));
        using Bitmap original = ImageOrientation.DecodeOriginal(new MemoryStream(bytes));
        Assert.Equal(new PixelSize(W, H), original.PixelSize);
        Assert.Equal("RGBY", Letters(original));
    }

    // ---- cost ----

    [Fact]
    public void ReadingTheOrientationTouchesOnlyTheHeaderOfALargeFile()
    {
        // Noise makes a JPEG of several hundred KiB; the tag sits before the pixel data, so reading it must not read the pixels.
        using SKBitmap noise = new(1200, 800, SKColorType.Bgra8888, SKAlphaType.Opaque);
        Random random = new(7);
        for (int y = 0; y < noise.Height; y++)
            for (int x = 0; x < noise.Width; x++)
                noise.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        byte[] bytes = WithSegment(PlainJpeg(noise), ExifSegment(6));
        Assert.True(bytes.Length > 400 * 1024, $"fixture is only {bytes.Length} bytes");

        using CountingStream counting = new(bytes);
        Assert.Equal(SKEncodedOrigin.RightTop, ImageOrientation.ReadOrigin(counting));
        Assert.True(counting.BytesRead <= 64 * 1024, $"read {counting.BytesRead} of {bytes.Length} bytes for the orientation");

        // Inspect (the header check every gallery decode already makes) now reports the shown size from the same single open.
        using CountingStream inspected = new(bytes);
        GalleryImageInfo info = GalleryImageDecoder.Inspect(inspected);
        Assert.Equal((800, 1200), (info.Width, info.Height));
        Assert.True(inspected.BytesRead <= 64 * 1024, $"inspect read {inspected.BytesRead} of {bytes.Length} bytes");
    }

    [Fact]
    public void AnUprightLargePhotoStillTakesAvaloniasOwnDecoder()
    {
        Platform();
        byte[] bytes = WithSegment(PlainJpeg(), ExifSegment(1));
        using Bitmap original = ImageOrientation.DecodeOriginal(new MemoryStream(bytes));
        using Bitmap viaAvalonia = new(new MemoryStream(bytes));
        Assert.Equal(viaAvalonia.PixelSize, original.PixelSize);
        Assert.Equal(Letters(viaAvalonia), Letters(original));
    }
}

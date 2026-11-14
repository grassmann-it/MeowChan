using System.Runtime.InteropServices;
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
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
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
        using SKBitmap once = ImageOrientation.Apply(raw, SKEncodedOrigin.BottomRight);
        using SKBitmap twice = ImageOrientation.Apply(once, SKEncodedOrigin.BottomRight);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(raw.GetPixel(x, y), twice.GetPixel(x, y)); // turning by 180 twice is the identity
    }

    private static (int X, int Y) MovedPixel(int x, int y, int width, int height, int orientation) => orientation switch
    {
        2 => (width - 1 - x, y), 3 => (width - 1 - x, height - 1 - y), 4 => (x, height - 1 - y),
        5 => (y, x), 6 => (height - 1 - y, x), 7 => (height - 1 - y, width - 1 - x), 8 => (y, width - 1 - x),
        _ => (x, y),
    };

    [Theory, MemberData(nameof(Orientations))]
    public void DestinationTransformPreservesEveryPixelAlphaAndBothRowPaddings(int orientation)
    {
        // Odd, non-square pixels and different padded strides catch addressing mistakes hidden by JPEG quadrants.
        const int width = 3, height = 2;
        SKEncodedOrigin origin = (SKEncodedOrigin)orientation;
        PixelSize shown = ImageOrientation.DisplaySize(width, height, origin);
        using SKBitmap source = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), (width + 2) * 4);
        using SKBitmap target = new(new SKImageInfo(shown.Width, shown.Height, SKColorType.Bgra8888, SKAlphaType.Premul), (shown.Width + 3) * 4);
        target.GetPixelSpan().Fill(0xCD);
        source.GetPixelSpan().Fill(0xAB);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Marshal.WriteInt32(source.GetPixels(), y * source.RowBytes + x * 4, unchecked((int)0x80402010) + y * 16 + x);
        byte[] before = source.GetPixelSpan().ToArray();
        IntPtr destination = target.GetPixels();

        ImageOrientation.Apply(source, target, origin);

        Assert.Equal(destination, target.GetPixels()); // writes into the caller's allocation, never replaces it
        Assert.Equal(before, source.GetPixelSpan().ToArray());
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                (int dx, int dy) = MovedPixel(x, y, width, height, orientation);
                Assert.Equal(Marshal.ReadInt32(source.GetPixels(), y * source.RowBytes + x * 4),
                    Marshal.ReadInt32(destination, dy * target.RowBytes + dx * 4));
            }
        byte[] after = target.GetPixelSpan().ToArray();
        for (int y = 0; y < target.Height; y++)
            for (int padding = target.Width * 4; padding < target.RowBytes && y * target.RowBytes + padding < after.Length; padding++)
                Assert.Equal((byte)0xCD, after[y * target.RowBytes + padding]); // Skia need not allocate the last row's trailing padding
    }

    [Theory, MemberData(nameof(Orientations))]
    public void DirectDisplayBufferKeepsExactPremultipliedPixelsAfterTheSourceIsDisposed(int orientation)
    {
        Platform();
        using SKBitmap source = new(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        int[] pixels = [0, 0x01010000, 0x40201008, unchecked((int)0x80402010), unchecked((int)0xFFFEFDFC), 0x7F7E7D7C];
        Marshal.Copy(pixels, 0, source.GetPixels(), pixels.Length);
        using Bitmap result = ImageOrientation.CopyUpright(source, (SKEncodedOrigin)orientation);
        source.Dispose();
        PixelSize shown = ImageOrientation.DisplaySize(3, 2, (SKEncodedOrigin)orientation);
        Assert.Equal(shown, result.PixelSize);
        if (orientation != 1)
        {
            using var framebuffer = Assert.IsType<WriteableBitmap>(result).Lock();
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 3; x++)
                {
                    (int dx, int dy) = MovedPixel(x, y, 3, 2, orientation);
                    Assert.Equal(pixels[y * 3 + x], Marshal.ReadInt32(framebuffer.Address, dy * framebuffer.RowBytes + dx * 4));
                }
        }
        using MemoryStream encoded = new();
        result.Save(encoded, PngBitmapEncoderOptions.Default); // the returned bitmap, not the disposed borrowed SKBitmap view, owns the display pixels
        Assert.True(encoded.Length > 0);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(1, 1)]
    public void EveryOrientationSupportsSinglePixelAxes(int width, int height)
    {
        using SKBitmap source = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                source.SetPixel(x, y, new SKColor((byte)(x + 17), (byte)(y + 23), 42));
        foreach (int orientation in Enumerable.Range(1, 8))
        {
            PixelSize shown = ImageOrientation.DisplaySize(width, height, (SKEncodedOrigin)orientation);
            using SKBitmap target = new(shown.Width, shown.Height, source.ColorType, source.AlphaType);
            ImageOrientation.Apply(source, target, (SKEncodedOrigin)orientation);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    (int dx, int dy) = MovedPixel(x, y, width, height, orientation);
                    Assert.Equal(source.GetPixel(x, y), target.GetPixel(dx, dy));
                }
        }
    }

    [Fact]
    public void DestinationTransformRejectsAliasingWrongSizeAndWrongPixelFormat()
    {
        using SKBitmap source = Quadrants();
        using SKBitmap wrongSize = new(W, H, source.ColorType, source.AlphaType);
        using SKBitmap wrongFormat = new(H, W, SKColorType.Gray8, SKAlphaType.Opaque);
        Assert.Throws<ArgumentException>(() => ImageOrientation.Apply(source, source, SKEncodedOrigin.TopRight));
        Assert.Throws<ArgumentException>(() => ImageOrientation.Apply(source, wrongSize, SKEncodedOrigin.RightTop));
        Assert.Throws<ArgumentException>(() => ImageOrientation.Apply(source, wrongFormat, SKEncodedOrigin.RightTop));
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

    [Theory, MemberData(nameof(Orientations))]
    public void AResizedFitUsesTheFinalOrientedBufferAndLeavesTheStreamOpen(int orientation)
    {
        Platform();
        using MemoryStream stream = new(Jpeg(orientation));
        PixelSize target = Expected[orientation - 1].Swapped ? new(13, 25) : new(25, 13);
        using Bitmap fit = GalleryImageDecoder.DecodeFit(stream, target);
        Assert.Equal(target, fit.PixelSize);
        Assert.Equal(Expected[orientation - 1].Corners, Letters(fit));
        if (orientation != 1) Assert.IsType<WriteableBitmap>(fit);
        Assert.True(stream.CanRead);
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

    [Theory, MemberData(nameof(Orientations))]
    public void ScanlineOriginalMatchesTheWholeBufferDecoderPixelForPixel(int orientation)
    {
        Platform();
        byte[] bytes = Jpeg(orientation);
        using MemoryStream encoded = new(bytes);
        using SKCodec codec = SKCodec.Create(encoded)!;
        using SKBitmap stored = SKBitmap.Decode(codec, new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        using SKBitmap expected = ImageOrientation.Apply(stored, (SKEncodedOrigin)orientation);
        using MemoryStream stream = new(bytes);
        using SKCodec scanlineCodec = SKCodec.Create(stream)!;
        // Require the small-scratch path itself to succeed, rather than letting a fallback hide a regression.
        using Bitmap actual = Assert.IsType<WriteableBitmap>(ImageOrientation.TryDecodeScanlines(scanlineCodec));
        using MemoryStream png = new();
        actual.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        using SKBitmap decoded = SKBitmap.Decode(png);
        Assert.Equal((expected.Width, expected.Height), (decoded.Width, decoded.Height));
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.Equal(expected.GetPixel(x, y), decoded.GetPixel(x, y));
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public void IncompleteJpegCanRewindForTheWholeBufferFallback(int orientation)
    {
        Platform();
        byte[] bytes = Jpeg(orientation)[..^32];
        using MemoryStream stream = new(bytes);
        using SKCodec codec = SKCodec.Create(stream)!;
        using SKBitmap stored = SKBitmap.Decode(codec, new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        Assert.NotNull(stored); // the old whole-buffer decoder accepts incomplete pixel input
        using SKBitmap expected = ImageOrientation.Apply(stored, (SKEncodedOrigin)orientation);
        using Bitmap actual = ImageOrientation.DecodeOriginal(new MemoryStream(bytes));
        Assert.Equal(new PixelSize(expected.Width, expected.Height), actual.PixelSize);
        Assert.Equal(Letters(expected), Letters(actual));
    }

    [Theory, MemberData(nameof(Orientations))]
    public void NonJpegCodecPixelsUseTheDirectDestinationFallback(int orientation)
    {
        Platform();
        using SKBitmap source = Quadrants();
        using SKData png = source.Encode(SKEncodedImageFormat.Png, 100);
        using SKCodec codec = SKCodec.Create(png)!;
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
        Assert.Null(ImageOrientation.TryDecodeScanlines(codec));
        using SKBitmap stored = SKBitmap.Decode(codec, new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        // Supply the origin explicitly: the pinned PNG codec ignores standard eXIf chunks, so such a fixture cannot exercise rotation.
        // This checks the codec-to-final-buffer fallback, not new PNG EXIF parsing/semantics.
        using Bitmap original = ImageOrientation.CopyUpright(stored, (SKEncodedOrigin)orientation);
        Assert.Equal(ImageOrientation.DisplaySize(W, H, (SKEncodedOrigin)orientation), original.PixelSize);
        Assert.Equal(Expected[orientation - 1].Corners, Letters(original));
        if (orientation != 1) Assert.IsType<WriteableBitmap>(original);
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

    // ---- allocation boundaries (header-only fixtures; no giant pixel allocations) ----

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(10000, 8001)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void InvalidOrOverBudgetDisplayTargetsAreRejectedBeforeDecoding(int width, int height)
    {
        using MemoryStream stream = new(Jpeg(6));
        using SKCodec codec = SKCodec.Create(stream)!;
        Assert.Throws<InvalidDataException>(() => ImageOrientation.DecodeScaled(codec, new PixelSize(width, height)));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void AThumbnailWidthCannotOverflowTheHeightOrAllocateAnUnboundedBuffer(int orientation)
    {
        using MemoryStream stream = new(Jpeg(orientation));
        Assert.Throws<InvalidDataException>(() => ImageOrientation.DecodeToWidth(stream, int.MaxValue));
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public void OversizedStoredDimensionsAreRejectedBeforeAllocatingOriginalOrFitPixels(int orientation)
    {
        byte[] bytes = Jpeg(orientation);
        // Rewrite the baseline JPEG's SOF dimensions, not the pixel payload. The codec reads this without allocating the image.
        int sof = -1;
        for (int i = 0; i < bytes.Length - 9; i++)
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xC0) { sof = i; break; }
        Assert.True(sof >= 0);
        const int width = 10000, height = 8001; // just over the existing 80 MP technical ceiling
        bytes[sof + 5] = (byte)(height >> 8); bytes[sof + 6] = (byte)(height & 255);
        bytes[sof + 7] = (byte)(width >> 8); bytes[sof + 8] = (byte)(width & 255);
        using MemoryStream stream = new(bytes);
        using SKCodec codec = SKCodec.Create(stream)!;
        Assert.Equal((width, height), (codec.Info.Width, codec.Info.Height));
        Assert.Throws<InvalidDataException>(() => ImageOrientation.TryDecodeScanlines(codec));
        Assert.Throws<InvalidDataException>(() => ImageOrientation.DecodeScaled(codec, new PixelSize(16, 16)));
        using MemoryStream original = new(bytes);
        Assert.Throws<InvalidDataException>(() => ImageOrientation.DecodeOriginal(original));
        using MemoryStream thumbnail = new(bytes);
        Assert.Throws<InvalidDataException>(() => ImageOrientation.DecodeToWidth(thumbnail, 16));
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

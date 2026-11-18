using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace MeowChan.Desktop;

/// <summary>
/// The one place where the encoded orientation of a still image (JPEG/WebP/PNG EXIF Orientation, 1-8) is applied. Avalonia's own
/// decoders (<c>new Bitmap(stream)</c>, <c>Bitmap.DecodeToWidth</c>) and the gallery's Skia path keep the stored pixel order, so a
/// phone photo taken upright arrives sideways. Every local image decode (gallery original and fit, archive/media/sidebar/filmstrip
/// thumbnails, reader attachments, Booru previews) goes through this class and so shows and measures the image as it is meant to be
/// seen. The orientation is read by Skia's codec from the image header it opens anyway (the EXIF segment sits before the pixel data,
/// the codec stops at the first scan, malformed EXIF yields <see cref="SKEncodedOrigin.TopLeft"/>): no second read of the file and no
/// parser of our own. An unrotated image takes the exact path it always took.
/// </summary>
internal static class ImageOrientation
{
    /// <summary>Whether the origin rotates by a quarter turn, so the displayed width is the stored height (EXIF 5-8).</summary>
    internal static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>Whether pixels have to move at all: only the eight defined origins other than "upright" do.</summary>
    internal static bool NeedsTransform(SKEncodedOrigin origin) => origin is >= SKEncodedOrigin.TopRight and <= SKEncodedOrigin.LeftBottom;

    /// <summary>The size the image is shown at: the stored size, width and height exchanged for a quarter turn.</summary>
    internal static PixelSize DisplaySize(SKCodec codec) => DisplaySize(codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);

    internal static PixelSize DisplaySize(int storedWidth, int storedHeight, SKEncodedOrigin origin) =>
        SwapsAxes(origin) ? new PixelSize(storedHeight, storedWidth) : new PixelSize(storedWidth, storedHeight);

    // The existing gallery inspection ceiling is also a technical allocation guard here: some thumbnail callers do not inspect
    // first. At four bytes per pixel a buffer is at most 320 MB; all stride/index arithmetic then fits in a signed Int32.
    internal const long MaximumPixels = 80_000_000;

    /// <summary>
    /// <paramref name="source"/> shown upright. The very instance when nothing has to move; otherwise a new bitmap that the caller
    /// disposes (the caller also keeps disposing <paramref name="source"/>). Decode paths instead write straight into their final buffer.
    /// </summary>
    internal static SKBitmap Apply(SKBitmap source, SKEncodedOrigin origin)
    {
        if (!NeedsTransform(origin)) return source;
        ValidateSize(new PixelSize(source.Width, source.Height));
        if (source.BytesPerPixel != 4) throw new ArgumentException("Orientation is applied to 32-bit pixels only.", nameof(source));
        PixelSize shown = DisplaySize(source.Width, source.Height, origin);
        SKBitmap target = new(new SKImageInfo(shown.Width, shown.Height, source.ColorType, source.AlphaType));
        try { Apply(source, target, origin); return target; }
        catch { target.Dispose(); throw; }
    }

    /// <summary>Move the pixels into an already allocated, distinct destination, respecting both row strides. No pixel allocation.</summary>
    internal static void Apply(SKBitmap source, SKBitmap target, SKEncodedOrigin origin)
    {
        ValidateSize(new PixelSize(source.Width, source.Height));
        if (source.BytesPerPixel != 4 || target.BytesPerPixel != 4 || source.ColorType != target.ColorType || source.AlphaType != target.AlphaType)
            throw new ArgumentException("Orientation requires matching 32-bit pixel formats.");
        if (new PixelSize(target.Width, target.Height) != DisplaySize(source.Width, source.Height, origin))
            throw new ArgumentException("Orientation destination has the wrong dimensions.", nameof(target));
        if (source.GetPixels() == IntPtr.Zero || target.GetPixels() == IntPtr.Zero || source.GetPixels() == target.GetPixels())
            throw new ArgumentException("Orientation requires distinct, allocated pixel buffers.");
        if (source.RowBytes % 4 != 0 || target.RowBytes % 4 != 0 ||
            (long)source.RowBytes * source.Height > int.MaxValue || (long)target.RowBytes * target.Height > int.MaxValue)
            throw new ArgumentException("Orientation requires addressable, 32-bit-aligned row strides.");
        Span<uint> from = MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan());
        Span<uint> to = MemoryMarshal.Cast<byte, uint>(target.GetPixelSpan());
        int w = source.Width, h = source.Height;
        int fromStride = source.RowBytes / 4, toStride = target.RowBytes / 4;
        (int origin0, int xStep, int yStep) = DestinationOffsets(w, h, toStride, origin);
        for (int y = 0; y < h; y++)
            CopyRow(from.Slice(y * fromStride, w), to, origin0 + y * yStep, xStep);
    }

    private static void CopyRow(ReadOnlySpan<uint> row, Span<uint> target, int at, int step)
    {
        for (int x = 0; x < row.Length; x++, at += step) target[at] = row[x];
    }

    private static (int Origin, int XStep, int YStep) DestinationOffsets(int w, int h, int stride, SKEncodedOrigin origin)
    {
        // Destination (dx, dy) = (dx0 + dxx * x + dxy * y, dy0 + dyx * x + dyy * y) for the stored pixel (x, y).
        (int dx0, int dxx, int dxy, int dy0, int dyx, int dyy) = origin switch
        {
            SKEncodedOrigin.TopRight => (w - 1, -1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => (w - 1, -1, 0, h - 1, 0, -1),
            SKEncodedOrigin.BottomLeft => (0, 1, 0, h - 1, 0, -1),
            SKEncodedOrigin.LeftTop => (0, 0, 1, 0, 1, 0),
            SKEncodedOrigin.RightTop => (h - 1, 0, -1, 0, 1, 0),
            SKEncodedOrigin.RightBottom => (h - 1, 0, -1, w - 1, -1, 0),
            SKEncodedOrigin.LeftBottom => (0, 0, 1, w - 1, -1, 0),
            _ => (0, 1, 0, 0, 0, 1),
        };
        return (dy0 * stride + dx0, dyx * stride + dxx, dyy * stride + dxy);
    }

    /// <summary>
    /// Write straight into Avalonia's final pixel buffer. The SKBitmap view borrows the locked framebuffer: InstallPixels allocates
    /// no pixels and does not own/free them. Dispose the view and lock before publishing the Bitmap; dispose the Bitmap on failure.
    /// The whole-buffer fallback needs only decoded + displayed, not decoded + rotated + Avalonia's copied display buffer.
    /// </summary>
    internal static Bitmap CopyUpright(SKBitmap source, SKEncodedOrigin origin)
    {
        if (!NeedsTransform(origin)) return CopyPixels(source);
        return FillDisplayBuffer(DisplaySize(source.Width, source.Height, origin), target =>
        {
            Apply(source, target, origin);
            return true;
        }) ?? throw Unsupported();
    }

    // JPEG originals can be oriented one scanline at a time. No full stored-order bitmap is needed, even at 100% zoom.
    // Other codecs, unsupported scanline modes and incomplete input take the bounded whole-buffer fallback exactly once.
    internal static Bitmap? TryDecodeScanlines(SKCodec codec)
    {
        ValidateSize(new PixelSize(codec.Info.Width, codec.Info.Height));
        if (codec.EncodedFormat != SKEncodedImageFormat.Jpeg) return null;
        SKImageInfo info = new(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (codec.StartScanlineDecode(info) != SKCodecResult.Success ||
            codec.ScanlineOrder is not (SKCodecScanlineOrder.TopDown or SKCodecScanlineOrder.BottomUp)) return null;
        return FillDisplayBuffer(DisplaySize(codec), target =>
        {
            using SKBitmap row = new(info.Width, 1, info.ColorType, info.AlphaType);
            if (row.GetPixels() == IntPtr.Zero) throw Unsupported();
            Span<uint> from = MemoryMarshal.Cast<byte, uint>(row.GetPixelSpan()).Slice(0, info.Width);
            Span<uint> to = MemoryMarshal.Cast<byte, uint>(target.GetPixelSpan());
            (int origin0, int xStep, int yStep) = DestinationOffsets(info.Width, info.Height, target.RowBytes / 4, codec.EncodedOrigin);
            for (int y = 0; y < info.Height; y++)
            {
                int outputY = codec.GetOutputScanline(y);
                if ((uint)outputY >= (uint)info.Height) throw Unsupported();
                if (codec.GetScanlines(row.GetPixels(), 1, row.RowBytes) != 1) return false;
                CopyRow(from, to, origin0 + outputY * yStep, xStep);
            }
            return true;
        });
    }

    private static Bitmap? FillDisplayBuffer(PixelSize shown, Func<SKBitmap, bool> fill)
    {
        ValidateSize(shown);
        WriteableBitmap bitmap = new(shown, new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
        try
        {
            bool complete;
            {
                using ILockedFramebuffer framebuffer = bitmap.Lock();
                using SKBitmap target = new();
                if (!target.InstallPixels(new SKImageInfo(shown.Width, shown.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
                    framebuffer.Address, framebuffer.RowBytes)) throw Unsupported();
                if (target.RowBytes % 4 != 0 || (long)target.RowBytes * target.Height > int.MaxValue) throw Unsupported();
                complete = fill(target);
            }
            if (complete) return bitmap;
            bitmap.Dispose();
            return null;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void ValidateSize(PixelSize size)
    {
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > MaximumPixels)
            throw PixelLimit();
    }

    private static InvalidDataException PixelLimit() => new(Localization.Texts.T("gallery.image.error.pixelLimit"));

    /// <summary>
    /// The still image of <paramref name="stream"/> at its stored size, upright. An upright file takes Avalonia's own decoder; a rotated
    /// one is decoded by Skia straight into the final display buffer (JPEG: one stored scanline at a time), retaining full resolution
    /// for 100% zoom. Codecs without scanline support need one full stored-order buffer, but never a second rotated copy.
    /// </summary>
    internal static Bitmap DecodeOriginal(Stream stream)
    {
        SKEncodedOrigin origin = ReadOrigin(stream);
        stream.Position = 0;
        if (!NeedsTransform(origin)) return new Bitmap(stream);
        using SKManagedStream managed = new(stream, disposeManagedStream: false);
        using SKCodec codec = OpenCodec(managed);
        ValidateSize(new PixelSize(codec.Info.Width, codec.Info.Height));
        Bitmap? scanlines = TryDecodeScanlines(codec);
        if (scanlines != null) return scanlines;
        using SKBitmap decoded = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw Unsupported();
        return CopyUpright(decoded, origin);
    }

    /// <summary>A still image decoded to <paramref name="displayWidth"/> pixels wide as shown (<c>Bitmap.DecodeToWidth</c> for upright files).</summary>
    internal static Bitmap DecodeToWidth(Stream stream, int displayWidth)
    {
        displayWidth = Math.Max(1, displayWidth);
        SKEncodedOrigin origin = ReadOrigin(stream);
        stream.Position = 0;
        if (!NeedsTransform(origin)) return Bitmap.DecodeToWidth(stream, displayWidth);
        using SKManagedStream managed = new(stream, disposeManagedStream: false);
        using SKCodec codec = OpenCodec(managed);
        PixelSize shown = DisplaySize(codec);
        double height = Math.Max(1, Math.Round((double)displayWidth * shown.Height / shown.Width));
        if (height > int.MaxValue || displayWidth * height > MaximumPixels) throw PixelLimit();
        return DecodeScaled(codec, new PixelSize(displayWidth, (int)height));
    }

    /// <summary>
    /// <paramref name="codec"/> decoded to exactly <paramref name="displayTarget"/> as shown. The codec scales natively where it can;
    /// the rest is a resize of the stored pixels, which are moved last so the move touches the smaller image.
    /// </summary>
    internal static Bitmap DecodeScaled(SKCodec codec, PixelSize displayTarget)
    {
        ValidateSize(new PixelSize(codec.Info.Width, codec.Info.Height));
        ValidateSize(displayTarget);
        SKEncodedOrigin origin = codec.EncodedOrigin;
        PixelSize target = SwapsAxes(origin) ? new PixelSize(displayTarget.Height, displayTarget.Width) : displayTarget;
        SKSizeI supported = codec.GetScaledDimensions(Math.Min((float)target.Width / codec.Info.Width,
            (float)target.Height / codec.Info.Height));
        ValidateSize(new PixelSize(supported.Width, supported.Height));
        using SKBitmap decoded = SKBitmap.Decode(codec,
            new SKImageInfo(supported.Width, supported.Height, SKColorType.Bgra8888, SKAlphaType.Premul)) ?? throw Unsupported();
        if (decoded.Width == target.Width && decoded.Height == target.Height)
            return CopyUpright(decoded, origin);
        using SKBitmap scaled = decoded.Resize(new SKImageInfo(target.Width, target.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw Unsupported();
        // The source-sized codec buffer is no longer needed when the final buffer is allocated (not every codec scales natively).
        decoded.Dispose();
        return CopyUpright(scaled, origin);
    }

    /// <summary>The encoded origin of an image (upright when the format has none, the data is not an image or the EXIF is malformed).</summary>
    internal static SKEncodedOrigin ReadOrigin(Stream stream)
    {
        stream.Position = 0;
        using SKManagedStream managed = new(stream, disposeManagedStream: false);
        using SKCodec? codec = SKCodec.Create(managed);
        return codec?.EncodedOrigin ?? SKEncodedOrigin.TopLeft;
    }

    internal static SKCodec OpenCodec(SKStream stream) => SKCodec.Create(stream) ?? throw Unsupported();

    internal static Bitmap CopyPixels(SKBitmap bitmap) => new(PixelFormats.Bgra8888, AlphaFormat.Premul,
        bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);

    private static InvalidDataException Unsupported() => new(Localization.Texts.T("gallery.image.error.unsupported"));
}

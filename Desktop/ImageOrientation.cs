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

    /// <summary>
    /// <paramref name="source"/> shown upright. The very instance when nothing has to move; otherwise a new bitmap that the caller
    /// disposes (the caller also keeps disposing <paramref name="source"/>). Four bytes per pixel only: every decoder here asks for BGRA.
    /// </summary>
    internal static SKBitmap Apply(SKBitmap source, SKEncodedOrigin origin)
    {
        if (!NeedsTransform(origin)) return source;
        if (source.BytesPerPixel != 4) throw new ArgumentException("Orientation is applied to 32-bit pixels only.", nameof(source));
        int w = source.Width, h = source.Height;
        bool swap = SwapsAxes(origin);
        SKBitmap target = new(new SKImageInfo(swap ? h : w, swap ? w : h, source.ColorType, source.AlphaType));
        try
        {
            Span<uint> from = MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan());
            Span<uint> to = MemoryMarshal.Cast<byte, uint>(target.GetPixelSpan());
            int fromStride = source.RowBytes / 4, toStride = target.RowBytes / 4;
            // Destination (dx, dy) = (dx0 + dxx * x + dxy * y, dy0 + dyx * x + dyy * y) for the stored pixel (x, y); linear in x and y.
            (int dx0, int dxx, int dxy, int dy0, int dyx, int dyy) = origin switch
            {
                SKEncodedOrigin.TopRight => (w - 1, -1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => (w - 1, -1, 0, h - 1, 0, -1),
                SKEncodedOrigin.BottomLeft => (0, 1, 0, h - 1, 0, -1),
                SKEncodedOrigin.LeftTop => (0, 0, 1, 0, 1, 0),
                SKEncodedOrigin.RightTop => (h - 1, 0, -1, 0, 1, 0),
                SKEncodedOrigin.RightBottom => (h - 1, 0, -1, w - 1, -1, 0),
                _ => (0, 0, 1, w - 1, -1, 0), // LeftBottom
            };
            int xStep = dyx * toStride + dxx, yStep = dyy * toStride + dxy, origin0 = dy0 * toStride + dx0;
            for (int y = 0; y < h; y++)
            {
                ReadOnlySpan<uint> row = from.Slice(y * fromStride, w);
                int at = origin0 + y * yStep;
                for (int x = 0; x < w; x++, at += xStep) to[at] = row[x];
            }
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    /// <summary>
    /// The still image of <paramref name="stream"/> at its stored size, upright. An upright file takes Avalonia's own decoder; a rotated
    /// one is decoded by Skia, moved and copied once (the peak holds the stored and the shown pixels together).
    /// </summary>
    internal static Bitmap DecodeOriginal(Stream stream)
    {
        SKEncodedOrigin origin = ReadOrigin(stream);
        stream.Position = 0;
        if (!NeedsTransform(origin)) return new Bitmap(stream);
        using SKManagedStream managed = new(stream, disposeManagedStream: false);
        using SKCodec codec = OpenCodec(managed);
        using SKBitmap decoded = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw Unsupported();
        using SKBitmap upright = Apply(decoded, origin);
        return CopyPixels(upright);
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
        int height = Math.Max(1, (int)Math.Round((double)displayWidth * shown.Height / shown.Width));
        return DecodeScaled(codec, new PixelSize(displayWidth, height));
    }

    /// <summary>
    /// <paramref name="codec"/> decoded to exactly <paramref name="displayTarget"/> as shown. The codec scales natively where it can;
    /// the rest is a resize of the stored pixels, which are moved last so the move touches the smaller image.
    /// </summary>
    internal static Bitmap DecodeScaled(SKCodec codec, PixelSize displayTarget)
    {
        SKEncodedOrigin origin = codec.EncodedOrigin;
        PixelSize target = SwapsAxes(origin) ? new PixelSize(displayTarget.Height, displayTarget.Width) : displayTarget;
        SKSizeI supported = codec.GetScaledDimensions(Math.Min((float)target.Width / codec.Info.Width,
            (float)target.Height / codec.Info.Height));
        using SKBitmap decoded = SKBitmap.Decode(codec,
            new SKImageInfo(supported.Width, supported.Height, SKColorType.Bgra8888, SKAlphaType.Premul)) ?? throw Unsupported();
        if (decoded.Width == target.Width && decoded.Height == target.Height)
        {
            using SKBitmap same = Apply(decoded, origin);
            return CopyPixels(same);
        }
        using SKBitmap scaled = decoded.Resize(new SKImageInfo(target.Width, target.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw Unsupported();
        using SKBitmap upright = Apply(scaled, origin);
        return CopyPixels(upright);
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

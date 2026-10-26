using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MeowChan.Gallery;
using MeowChan.Diagnostics;
using SkiaSharp;

namespace MeowChan.Desktop;

internal sealed record GalleryImageInfo(int Width, int Height, string Format, bool StillFrame)
{
    internal long Pixels => (long)Width * Height;
}

internal abstract record GalleryDecodeRequest
{
    internal sealed record Fit(double PhysicalWidth, double PhysicalHeight) : GalleryDecodeRequest;
    internal sealed record Original : GalleryDecodeRequest;
}

internal static class GalleryFitTarget
{
    internal const long MaximumPixels = 16_000_000;

    // Round the long axis down and the other axis up. The conservative bounds
    // also cover the codec's aspect-ratio rounding, including very thin images.
    internal static PixelSize Calculate(GalleryImageInfo source, GalleryDecodeRequest.Fit viewport)
    {
        double width = double.IsFinite(viewport.PhysicalWidth) ? Math.Max(1, viewport.PhysicalWidth) : 1;
        double height = double.IsFinite(viewport.PhysicalHeight) ? Math.Max(1, viewport.PhysicalHeight) : 1;
        double ratio = Math.Min(1, Math.Min(width / source.Width, height / source.Height));
        ratio = Math.Min(ratio, Math.Sqrt((double)MaximumPixels / source.Pixels));
        bool wide = source.Width >= source.Height;
        int major = wide ? source.Width : source.Height;
        int minor = wide ? source.Height : source.Width;
        int length = Math.Max(1, (int)Math.Floor(major * ratio + 1e-8));
        int other = Math.Max(1, (int)Math.Ceiling((double)length * minor / major));
        if ((long)length * other > MaximumPixels)
        {
            int low = 1, high = length;
            while (low < high)
            {
                int candidate = low + (high - low + 1) / 2;
                int shortAxis = Math.Max(1, (int)Math.Ceiling((double)candidate * minor / major));
                if ((long)candidate * shortAxis <= MaximumPixels) low = candidate;
                else high = candidate - 1;
            }
            length = low;
            other = Math.Max(1, (int)Math.Ceiling((double)length * minor / major));
        }
        return wide ? new PixelSize(length, other) : new PixelSize(other, length);
    }

    internal static bool Covers(PixelSize buffer, PixelSize target) =>
        buffer.Width >= target.Width && buffer.Height >= target.Height;
}

/// <summary>Only verified manifest handles enter this decoder. No path/filename cache or network access.</summary>
internal static class GalleryImageDecoder
{
    internal const long MaximumBytes = 64L * 1024 * 1024;
    // A canceled waiter leaves immediately; a noncancelable codec holds just this single worker.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static Action<GalleryDecodeRequest>? DecodeStarted { get; set; }

    internal static GalleryImageInfo Inspect(Stream stream, long maximumPixels = 80_000_000)
    {
        using SKManagedStream managed = new(stream, disposeManagedStream: false);
        using SKCodec? codec = SKCodec.Create(managed);
        if (codec == null) throw new InvalidDataException(GalleryViewerText.Get("Unsupported or damaged image.", "Nicht unterstütztes oder beschädigtes Bild."));
        GalleryImageInfo info = new(codec.Info.Width, codec.Info.Height, codec.EncodedFormat.ToString(),
            codec.EncodedFormat == SKEncodedImageFormat.Gif || codec.FrameCount > 1);
        if (info.Width <= 0 || info.Height <= 0 || info.Pixels > maximumPixels)
            throw new InvalidDataException(GalleryViewerText.Get("Image exceeds the safe pixel limit.", "Das Bild überschreitet die sichere Pixelgrenze."));
        return info;
    }

    internal static Task<(Bitmap Bitmap, GalleryImageInfo Info)> DecodeAsync(GalleryEntry entry,
        GalleryDecodeRequest request, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using (PerformanceDiagnostics.Measure("gallery.decoder.gate-wait", asset: entry.Asset.AssetId))
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DecodeStarted?.Invoke(request);
            if (entry.Asset.Length > MaximumBytes) throw new InvalidDataException(GalleryViewerText.Get(
                "Image exceeds the 64 MiB file limit.", "Das Bild überschreitet die Dateigrenze von 64 MiB."));
            await using Stream stream = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, cancellationToken).ConfigureAwait(false);
            GalleryImageInfo info;
            using (PerformanceDiagnostics.Measure("gallery.decoder.inspect", asset: entry.Asset.AssetId))
                info = Inspect(stream);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            PixelSize target = request is GalleryDecodeRequest.Fit fit
                ? GalleryFitTarget.Calculate(info, fit) : new PixelSize(info.Width, info.Height);
            Bitmap bitmap;
            using (PerformanceDiagnostics.Measure(request is GalleryDecodeRequest.Original
                ? "gallery.decoder.original" : "gallery.decoder.fit", asset: entry.Asset.AssetId))
                bitmap = request is GalleryDecodeRequest.Original ? new Bitmap(stream) : DecodeFit(stream, target);
            PerformanceDiagnostics.Record("gallery.decoder.pixels", "decoded", asset: entry.Asset.AssetId,
                value: (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height);
            if (cancellationToken.IsCancellationRequested) { bitmap.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
            return (bitmap, info);
        }
        finally { Gate.Release(); }
    }, cancellationToken);

    // Avalonia's width/height convenience decoders truncate the other dimension
    // to zero on very thin images. Explicit dimensions keep the published Fit
    // buffer and adequacy checks on the same rounded target. Codecs without
    // native scaling (such as PNG) still need a source-sized temporary buffer.
    internal static Bitmap DecodeFit(Stream stream, PixelSize target)
    {
        using SKManagedStream managed = new(stream, disposeManagedStream: false);
        using SKCodec codec = SKCodec.Create(managed) ?? throw new InvalidDataException(
            GalleryViewerText.Get("Unsupported or damaged image.", "Nicht unterstütztes oder beschädigtes Bild."));
        SKSizeI supported = codec.GetScaledDimensions(Math.Min((float)target.Width / codec.Info.Width,
            (float)target.Height / codec.Info.Height));
        using SKBitmap decoded = SKBitmap.Decode(codec,
            new SKImageInfo(supported.Width, supported.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidDataException(GalleryViewerText.Get("Unsupported or damaged image.", "Nicht unterstütztes oder beschädigtes Bild."));
        if (decoded.Width == target.Width && decoded.Height == target.Height)
            return CopyPixels(decoded);
        using SKBitmap scaled = decoded.Resize(new SKImageInfo(target.Width, target.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidDataException(GalleryViewerText.Get("Unsupported or damaged image.", "Nicht unterstütztes oder beschädigtes Bild."));
        return CopyPixels(scaled);
    }

    private static Bitmap CopyPixels(SKBitmap bitmap) => new(PixelFormats.Bgra8888, AlphaFormat.Premul,
        bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);

}

internal static class GalleryViewerText
{
    internal static string Get(string english, string german) => Localization.Texts.Language == "de" ? german : english;
}

/// <summary>Scale is physical display pixels per source pixel; pan is in device-independent units.</summary>
internal static class GalleryGeometry
{
    internal static double WheelFactor(double delta) => double.IsFinite(delta)
        ? Math.Exp(Math.Clamp(delta, -4, 4) * Math.Log(1.1)) : 1;
    internal static double ClampZoom(double requested, double fit) =>
        Math.Clamp(double.IsFinite(requested) ? requested : fit, fit, Math.Max(16, fit));
    internal static double Fit(int width, int height, double viewportWidth, double viewportHeight, double displayScale) =>
        Math.Min(Math.Max(1, viewportWidth) * displayScale / width, Math.Max(1, viewportHeight) * displayScale / height);
    internal static double Pan(double pan, int sourcePixels, double physicalZoom, double displayScale, double viewport) =>
        Math.Clamp(pan, -Math.Max(0, (sourcePixels * physicalZoom / displayScale - viewport) / 2),
            Math.Max(0, (sourcePixels * physicalZoom / displayScale - viewport) / 2));
}

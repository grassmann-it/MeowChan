using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using MeowChan.Gallery;
using SkiaSharp;

namespace MeowChan.Desktop;

internal sealed record GalleryImageInfo(int Width, int Height, string Format, bool StillFrame)
{
    internal long Pixels => (long)Width * Height;
}

/// <summary>Only verified manifest handles enter this decoder. No path/filename cache or network access.</summary>
internal static class GalleryImageDecoder
{
    internal const long MaximumBytes = 64L * 1024 * 1024;
    // A canceled waiter leaves immediately; a noncancelable codec holds just this single worker.
    private static readonly SemaphoreSlim Gate = new(1, 1);

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
        int maximumWidth, int maximumHeight, bool original, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Asset.Length > MaximumBytes) throw new InvalidDataException(GalleryViewerText.Get(
                "Image exceeds the 64 MiB file limit.", "Das Bild überschreitet die Dateigrenze von 64 MiB."));
            await using Stream stream = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, cancellationToken).ConfigureAwait(false);
            GalleryImageInfo info = Inspect(stream);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            double ratio = Math.Min(1, Math.Min((double)Math.Max(1, maximumWidth) / info.Width,
                (double)Math.Max(1, maximumHeight) / info.Height));
            // Fit buffers are capped at 16 MP (64 MB), including on exceptionally large displays.
            ratio = Math.Min(ratio, Math.Sqrt(16_000_000d / info.Pixels));
            Bitmap bitmap = original ? new Bitmap(stream) : Bitmap.DecodeToWidth(stream, Math.Max(1, (int)Math.Ceiling(info.Width * ratio)));
            if (cancellationToken.IsCancellationRequested) { bitmap.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
            return (bitmap, info);
        }
        finally { Gate.Release(); }
    }, cancellationToken);
}

internal static class GalleryViewerText
{
    internal static string Get(string english, string german) => Localization.Texts.Language == "de" ? german : english;
}

/// <summary>Scale is physical display pixels per source pixel; pan is in device-independent units.</summary>
internal static class GalleryGeometry
{
    internal static double Fit(int width, int height, double viewportWidth, double viewportHeight, double displayScale) =>
        Math.Min(1, Math.Min(Math.Max(1, viewportWidth) * displayScale / width, Math.Max(1, viewportHeight) * displayScale / height));
    internal static double Pan(double pan, int sourcePixels, double physicalZoom, double displayScale, double viewport) =>
        Math.Clamp(pan, -Math.Max(0, (sourcePixels * physicalZoom / displayScale - viewport) / 2),
            Math.Max(0, (sourcePixels * physicalZoom / displayScale - viewport) / 2));
}

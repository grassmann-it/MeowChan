using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace MeowChan.Desktop.Booru;

/// <summary>Decodes captured preview bytes only; no provider or original-file access.</summary>
internal sealed class BooruPreviewDecoder
{
    private readonly Func<Stream, PixelSize, Bitmap> _decode;

    // Instance-local seam for deterministic slow-codec smoke fixtures.
    internal BooruPreviewDecoder(Func<Stream, PixelSize, Bitmap>? decode = null) =>
        _decode = decode ?? GalleryImageDecoder.DecodeFit;

    internal Task<(Bitmap Bitmap, GalleryImageInfo Info)> DecodeAsync(byte[] bytes,
        GalleryDecodeRequest request, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length is 0 or > 8388608) throw new InvalidDataException("Invalid Booru preview.");
        using MemoryStream stream = new(bytes, writable: false);
        GalleryImageInfo info = GalleryImageDecoder.Inspect(stream, maximumPixels: 40_000_000) with
        { Format = "Preview", StillFrame = false };
        token.ThrowIfCancellationRequested();
        // Even detail/actual-size stays a bounded preview, never an original download.
        GalleryDecodeRequest.Fit fit = request is GalleryDecodeRequest.Fit requested
            ? new(Math.Min(2048, requested.PhysicalWidth), Math.Min(2048, requested.PhysicalHeight))
            : new(2048, 2048);
        PixelSize target = GalleryFitTarget.Calculate(info, fit);
        stream.Position = 0;
        Bitmap bitmap = _decode(stream, target);
        // Native codecs cannot be interrupted mid-call. Retire their pixels immediately on
        // completion if navigation/close canceled them; never pass canceled ownership to UI.
        if (token.IsCancellationRequested)
        {
            bitmap.Dispose();
            token.ThrowIfCancellationRequested();
        }
        return (bitmap, info);
    }, token);
}

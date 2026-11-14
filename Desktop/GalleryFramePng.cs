using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;
using SkiaSharp;

namespace MeowChan.Desktop;

/// <summary>The pixels of one saved video frame: tightly packed BGRA rows (premultiplied), owned by nobody else.</summary>
internal sealed record GalleryFramePixels(int Width, int Height, byte[] Bgra)
{
    internal int RowBytes => Width * 4;
}

/// <summary>
/// "Save frame as image" for the software video surface: the shown bitmap is copied on the UI thread (the next frame
/// replaces or disposes it right after), and the PNG encoding and the file write run on a worker thread, so a large frame
/// never stalls the UI. Failures are logged and reported as false; the caller shows "The frame could not be saved."
/// </summary>
internal static class GalleryFramePng
{
    /// <summary>Copies <paramref name="bitmap"/> into private memory. Call on the thread that owns it (the UI thread).</summary>
    internal static GalleryFramePixels Capture(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        PixelSize size = bitmap.PixelSize;
        if (size.Width < 1 || size.Height < 1) throw new ArgumentException("The frame is empty.", nameof(bitmap));
        byte[] bgra = new byte[checked(size.Width * 4 * size.Height)];
        GCHandle pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using ArrayFramebuffer target = new(pin.AddrOfPinnedObject(), size, size.Width * 4, bitmap.Dpi);
            bitmap.CopyPixels(target);
        }
        finally { pin.Free(); }
        return new GalleryFramePixels(size.Width, size.Height, bgra);
    }

    /// <summary>
    /// Copies the shown bitmap here (UI thread) and writes it as a PNG at <paramref name="path"/> on a worker thread.
    /// True when the file was written; false (logged) otherwise.
    /// </summary>
    internal static async Task<bool> SaveAsync(Bitmap shown, string path)
    {
        GalleryFramePixels pixels;
        try { pixels = Capture(shown); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warning(error, "The shown video frame could not be copied for saving.");
            return false;
        }
        return await WriteAsync(pixels, path).ConfigureAwait(true);
    }

    /// <summary>Encodes and writes on a worker thread; never throws for an IO or encoding failure (logged, false).</summary>
    internal static async Task<bool> WriteAsync(GalleryFramePixels pixels, string path)
    {
        try
        {
            await Task.Run(() => Write(pixels, path)).ConfigureAwait(true);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warning(error, "A video frame could not be saved as PNG.");
            return false;
        }
    }

    /// <summary>Test seam: observes the thread that encodes and writes (it must never be the UI thread).</summary>
    internal static Action? Writing { get; set; }

    /// <summary>
    /// Encodes as PNG and writes <paramref name="path"/>, the caller's own temporary file (a refused native snapshot may have
    /// left a partial one there). Any thread.
    /// </summary>
    internal static void Write(GalleryFramePixels pixels, string path)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Writing?.Invoke();
        SKImageInfo info = new(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKImage image = SKImage.FromPixelCopy(info, pixels.Bgra, pixels.RowBytes)
            ?? throw new InvalidOperationException("The frame pixels could not be read.");
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The frame could not be encoded as PNG.");
        using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        png.SaveTo(file);
    }

    // A framebuffer over pinned managed memory: Bitmap.CopyPixels transcodes into it (BGRA, premultiplied).
    private sealed class ArrayFramebuffer(IntPtr address, PixelSize size, int rowBytes, Vector dpi) : ILockedFramebuffer
    {
        public IntPtr Address { get; } = address;
        public PixelSize Size { get; } = size;
        public int RowBytes { get; } = rowBytes;
        public Vector Dpi { get; } = dpi;
        public PixelFormat Format => PixelFormat.Bgra8888;
        public AlphaFormat AlphaFormat => AlphaFormat.Premul;
        public void Dispose() { }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;
using Serilog;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

/// <summary>Disposable derived PNGs keyed by verified video content. Never fetches source URLs or changes originals.</summary>
internal static class VideoPosterCache
{
    internal const int MaximumPngBytes = 4 * 1024 * 1024;
    internal const long MaximumCacheBytes = 128L * 1024 * 1024;
    internal const int MaximumCacheFiles = 256;
    private const long MaximumVideoBytes = 512L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object FailuresLock = new();
    private static readonly Dictionary<string, DateTime> Failures = new(StringComparer.Ordinal);
    private static int _pending;

    internal static async Task<Bitmap?> LoadAsync(MediaAssetSnapshot manifest, MediaAssetReference asset,
        string cacheDirectory, int maximumWidth, int maximumHeight, CancellationToken token)
    {
        byte[]? png = await GetPngAsync(manifest, asset, cacheDirectory, token).ConfigureAwait(false);
        if (png == null) return null;
        return await Task.Run(() =>
        {
            using var measurement = PerformanceDiagnostics.Measure("gallery.poster.bitmap-decode", asset: asset.AssetId);
            token.ThrowIfCancellationRequested();
            using MemoryStream stream = new(png, writable: false);
            using SKCodec? codec = SKCodec.Create(stream);
            if (codec == null) return null;
            double ratio = Math.Min(1, Math.Min((double)Math.Max(1, maximumWidth) / codec.Info.Width,
                (double)Math.Max(1, maximumHeight) / codec.Info.Height));
            stream.Position = 0;
            Bitmap bitmap = Bitmap.DecodeToWidth(stream, Math.Max(1, (int)(codec.Info.Width * ratio)));
            if (token.IsCancellationRequested) { bitmap.Dispose(); token.ThrowIfCancellationRequested(); }
            return bitmap;
        }, token).ConfigureAwait(false);
    }

    // Kept independent of Avalonia initialization so native decoder/cache acceptance can run without any display.
    internal static async Task<byte[]?> GetPngAsync(MediaAssetSnapshot manifest, MediaAssetReference asset,
        string cacheDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (manifest.FindById(asset.AssetId) != asset) return null;
        if (SavedMediaTypes.Classify(asset.OriginalFileName ?? asset.Path) != SavedMediaKind.Video
            || asset.Length <= 0 || asset.Length > MaximumVideoBytes || asset.Sha256.Length != 64
            || asset.Sha256.Any(character => !char.IsAsciiHexDigit(character))) return null;
        string directory = Path.GetFullPath(cacheDirectory);
        string path = Path.Combine(directory, asset.Sha256.ToLowerInvariant() + ".png");
        lock (FailuresLock)
            if (Failures.TryGetValue(path, out DateTime retryAt) && retryAt > DateTime.UtcNow) return null;
        if (Interlocked.Increment(ref _pending) > 8) { Interlocked.Decrement(ref _pending); return null; }
        long request = PerformanceDiagnostics.NextId();
        PerformanceDiagnostics.Record("gallery.poster", "accepted", request, asset: asset.AssetId);
        Task<byte[]?> operation = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
                bounded.CancelAfter(TimeSpan.FromSeconds(12));
                using (PerformanceDiagnostics.Measure("gallery.poster.queue-wait", request, asset: asset.AssetId))
                    await Gate.WaitAsync(bounded.Token).ConfigureAwait(false);
                try
                {
                    bounded.Token.ThrowIfCancellationRequested();
                    // Even a cache hit requires the recorded original to be present and fully verified.
                    Stream? video;
                    using (PerformanceDiagnostics.Measure("gallery.poster.verify", request, asset: asset.AssetId))
                        video = await manifest.OpenVerifiedReadAsync(asset.AssetId, bounded.Token).ConfigureAwait(false);
                    PerformanceDiagnostics.Record("gallery.poster.verified-bytes", request: request, asset: asset.AssetId, value: asset.Length);
                    byte[] png;
                    try
                    {
                        byte[]? cached;
                        using (PerformanceDiagnostics.Measure("gallery.poster.cache-read", request, asset: asset.AssetId))
                            cached = await ReadAsync(path, bounded.Token).ConfigureAwait(false);
                        if (cached != null)
                        {
                            PerformanceDiagnostics.Record("gallery.poster", "verified-hit", request, asset: asset.AssetId);
                            return cached;
                        }
                        Stream ownedVideo = video;
                        video = null; // Decoder owns the verified handle through native release, including failures.
                        using (PerformanceDiagnostics.Measure("gallery.poster.decode", request, asset: asset.AssetId))
                            png = await VideoPosterDecoder.DecodeAsync(ownedVideo, bounded.Token).ConfigureAwait(false);
                    }
                    finally { if (video != null) await video.DisposeAsync().ConfigureAwait(false); }
                    if (!Valid(png)) throw new InvalidDataException("The video poster exceeds its safe limits.");
                    bounded.Token.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(directory);
                    Trim(directory, png.Length);
                    string temporary = Path.Combine(directory, ".poster-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, png, bounded.Token).ConfigureAwait(false);
                        bounded.Token.ThrowIfCancellationRequested();
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally { try { File.Delete(temporary); } catch (IOException) { } }
                    return png;
                }
                finally { Gate.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                PerformanceDiagnostics.Record("gallery.poster", "canceled", request, asset: asset.AssetId);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                PerformanceDiagnostics.Record("gallery.poster", "failed", request, asset: asset.AssetId);
                Log.Warning(exception, "Could not generate the local video poster; the video remains available without a poster.");
                // A terminal placeholder stays stable during this view. A later request can retry;
                // cancellation never records a negative result and failed generations publish no PNG.
                lock (FailuresLock)
                {
                    if (Failures.Count >= 256) Failures.Remove(Failures.MinBy(pair => pair.Value).Key);
                    Failures[path] = DateTime.UtcNow.AddSeconds(30);
                }
                return null;
            }
            finally { Interlocked.Decrement(ref _pending); }
        }, CancellationToken.None);
        // If a native plugin takes too long to stop, only its single worker remains occupied.
        // The caller leaves promptly; callbacks/buffers/verified stream remain owned until native release finishes.
        try { return await operation.WaitAsync(TimeSpan.FromSeconds(14), token).ConfigureAwait(false); }
        catch (TimeoutException) { PerformanceDiagnostics.Record("gallery.poster", "caller-timeout", request, asset: asset.AssetId); _ = operation.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); return null; }
        catch (OperationCanceledException) { _ = operation.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); throw; }
    }

    private static async Task<byte[]?> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists) return null;
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.Length is > 0 and <= MaximumPngBytes)
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                if (Valid(bytes)) return bytes;
            }
            File.Delete(path);
            return null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    internal static bool Valid(byte[] png)
    {
        if (png.Length == 0 || png.Length > MaximumPngBytes) return false;
        using MemoryStream encoded = new(png, writable: false);
        using SKCodec? codec = SKCodec.Create(encoded);
        if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.Info.Width is < 1 or > 960
            || codec.Info.Height is < 1 or > 640) return false;
        using SKBitmap bitmap = SKBitmap.Decode(png);
        return bitmap != null && bitmap.Width == codec.Info.Width && bitmap.Height == codec.Info.Height;
    }
    internal static void Trim(string directory, int incomingBytes)
    {
        FileInfo[] files = Directory.EnumerateFiles(directory, "*.png").Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        long bytes = files.Sum(file => file.Length);
        int count = files.Length;
        foreach (FileInfo file in files)
        {
            if (count < MaximumCacheFiles && bytes + incomingBytes <= MaximumCacheBytes) break;
            long length = file.Length;
            file.Delete(); count--; bytes -= length;
        }
        foreach (string temporary in Directory.EnumerateFiles(directory, ".poster-*.tmp"))
            if (File.GetLastWriteTimeUtc(temporary) < DateTime.UtcNow.AddDays(-1)) File.Delete(temporary);
    }
}

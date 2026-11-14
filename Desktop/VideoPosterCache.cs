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
internal sealed class VideoPosterCacheTestSeam
{
    // These hooks are supplied only by deterministic tests. Production callers leave the seam null.
    internal Action<Task>? NativeGateWaitStarted { get; init; }
    internal Func<Stream, CancellationToken, Task<byte[]>>? Decoder { get; init; }
    // Tests may shorten the watchdog; production keeps the fixed 12-second bound.
    internal TimeSpan? BoundedTimeout { get; init; }
}

internal static class VideoPosterCache
{
    internal const int MaximumPngBytes = 4 * 1024 * 1024;
    /// <summary>Default size of the cache (the limit is a setting, Settings, Storage and limits).</summary>
    internal const long DefaultCacheBytes = 512L * 1024 * 1024;
    /// <summary>Default number of files in the cache (the limit is a setting).</summary>
    internal const int DefaultCacheFiles = 2048;
    // Fixed technical limit (documented in Help.md): videos above this size never get a preview.
    internal const long MaximumVideoBytes = 512L * 1024 * 1024;
    private static long _cacheBytes = DefaultCacheBytes;
    private static int _cacheFiles = DefaultCacheFiles;

    /// <summary>The cache limits in force (tests, diagnostics).</summary>
    internal static (long Bytes, int Files) Limits => (Interlocked.Read(ref _cacheBytes), Volatile.Read(ref _cacheFiles));

    /// <summary>The configured cache limits. Lowering them evicts the oldest previews when the next one is saved; originals are never touched.</summary>
    internal static void Configure(long maximumBytes, int maximumFiles)
    {
        Interlocked.Exchange(ref _cacheBytes, Math.Max(MaximumPngBytes, maximumBytes));
        Volatile.Write(ref _cacheFiles, Math.Max(1, maximumFiles));
    }
    private static readonly SemaphoreSlim Gate = new(1, 1);
    /// <summary>One native decode session at a time across posters and scrub previews (<see cref="VideoScrubPreviewCache"/> shares it).</summary>
    internal static SemaphoreSlim NativeDecodeGate => Gate;
    private static readonly object FailuresLock = new();
    private static readonly Dictionary<string, DateTime> Failures = new(StringComparer.Ordinal);
    private static int _pending;

    internal static async Task<Bitmap?> LoadAsync(MediaAssetSnapshot manifest, MediaAssetReference asset,
        string cacheDirectory, int maximumWidth, int maximumHeight, CancellationToken token)
    {
        byte[]? png = await GetPngAsync(manifest, asset, cacheDirectory, token).ConfigureAwait(false);
        if (png == null) return null;
        return await DecodeBitmapAsync(png, asset.AssetId, maximumWidth, maximumHeight, token).ConfigureAwait(false);
    }

    internal static Task<Bitmap?> DecodeBitmapAsync(byte[] png, string assetId, int maximumWidth, int maximumHeight, CancellationToken token) =>
        Task.Run(() =>
        {
            using var measurement = PerformanceDiagnostics.Measure("gallery.poster.bitmap-decode", asset: assetId);
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
        }, token);

    // Kept independent of Avalonia initialization so native decoder/cache acceptance can run without any display.
    internal static async Task<byte[]?> GetPngAsync(MediaAssetSnapshot manifest, MediaAssetReference asset,
        string cacheDirectory, CancellationToken token, VerifiedFileMemo? verifiedFiles = null,
        VideoPosterCacheTestSeam? testSeam = null)
    {
        token.ThrowIfCancellationRequested();
        if (manifest.FindById(asset.AssetId) != asset) return null;
        if (SavedMediaTypes.Classify(asset.OriginalFileName ?? asset.Path) != SavedMediaKind.Video
            || asset.Length <= 0 || asset.Length > MaximumVideoBytes || asset.Sha256.Length != 64
            || asset.Sha256.Any(character => !char.IsAsciiHexDigit(character))) return null;
        string directory = Path.GetFullPath(cacheDirectory);
        string path = Path.Combine(directory, asset.Sha256.ToLowerInvariant() + ".png");
        // A failure belongs to one saved file: another occurrence of the same bytes may still verify and decode.
        string failureKey = path + "|" + Path.GetFullPath(asset.Path);
        lock (FailuresLock)
            if (Failures.TryGetValue(failureKey, out DateTime retryAt) && retryAt > DateTime.UtcNow) return null;
        if (Interlocked.Increment(ref _pending) > 8) { Interlocked.Decrement(ref _pending); return null; }
        long request = PerformanceDiagnostics.NextId();
        PerformanceDiagnostics.Record("gallery.poster", "accepted", request, asset: asset.AssetId);
        Task<byte[]?> operation = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
                bounded.CancelAfter(testSeam?.BoundedTimeout ?? TimeSpan.FromSeconds(12));
                // A caller without a shared memo still gets a per-request identity memo. It avoids hashing the same
                // original again when the gate wait or native decode itself did not change it; a changed identity
                // necessarily falls back to the full verification in MediaAssetSnapshot.
                VerifiedFileMemo verificationMemo = verifiedFiles ?? new VerifiedFileMemo(capacity: 1);

                // Read the derived file before waiting for native work. A warm hit still needs a complete original
                // verification, but neither that potentially long hash nor the cache hit needs the native gate.
                byte[]? cached;
                using (PerformanceDiagnostics.Measure("gallery.poster.cache-read", request, asset: asset.AssetId))
                    cached = await ReadAsync(path, bounded.Token).ConfigureAwait(false);
                if (cached != null)
                {
                    await VerifyOriginalAsync(manifest, asset, verificationMemo, bounded.Token, request);
                    PerformanceDiagnostics.Record("gallery.poster", "verified-hit", request, asset: asset.AssetId);
                    return cached;
                }

                // This first verification is intentionally outside NativeDecodeGate. It prevents a cold poster's
                // large checksum from holding up an already-running native session (or being held behind one).
                await VerifyOriginalAsync(manifest, asset, verificationMemo, bounded.Token, request);

                using (PerformanceDiagnostics.Measure("gallery.poster.queue-wait", request, asset: asset.AssetId))
                {
                    Task gateWait = Gate.WaitAsync(bounded.Token);
                    testSeam?.NativeGateWaitStarted?.Invoke(gateWait);
                    await gateWait.ConfigureAwait(false);
                }
                try
                {
                    bounded.Token.ThrowIfCancellationRequested();
                    // Another poster may have filled the cache while this request waited. Re-check it before
                    // entering native decode, but verify again after the wait so an original changed or removed
                    // during the wait can never authorize the old derived bytes.
                    using (PerformanceDiagnostics.Measure("gallery.poster.cache-read", request, asset: asset.AssetId))
                        cached = await ReadAsync(path, bounded.Token).ConfigureAwait(false);
                    if (cached != null)
                    {
                        await VerifyOriginalAsync(manifest, asset, verificationMemo, bounded.Token, request);
                        PerformanceDiagnostics.Record("gallery.poster", "verified-hit", request, asset: asset.AssetId);
                        return cached;
                    }

                    Stream? video = await OpenVerifiedOriginalAsync(manifest, asset, verificationMemo, bounded.Token, request);
                    byte[] png;
                    try
                    {
                        // Transfer the handle only after the decoder has accepted the call and returned its task.
                        // A decoder can throw synchronously before it owns the stream; that path remains ours to dispose.
                        Func<Stream, CancellationToken, Task<byte[]>> decoder =
                            testSeam?.Decoder ?? VideoPosterDecoder.DecodeAsync;
                        Task<byte[]> decoding;
                        using (PerformanceDiagnostics.Measure("gallery.poster.decode", request, asset: asset.AssetId))
                        {
                            decoding = decoder(video!, bounded.Token);
                            if (decoding == null) throw new InvalidOperationException("The local video decoder returned no task.");
                            video = null;
                            png = await decoding.ConfigureAwait(false);
                        }
                    }
                    finally { if (video != null) await video.DisposeAsync().ConfigureAwait(false); }
                    if (!Valid(png)) throw new InvalidDataException("The video poster exceeds its safe limits.");

                    // The decoder held a verified stream, but the path may have been replaced while native work
                    // was running. Do not publish or return that frame until the original is verified once more.
                    await VerifyOriginalAsync(manifest, asset, verificationMemo, bounded.Token, request);
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
            catch (OperationCanceledException)
            {
                PerformanceDiagnostics.Record("gallery.poster", "canceled", request, asset: asset.AssetId);
                // Caller cancellation remains observable. Internal watchdog/decoder cancellation is a safe
                // missing-poster result and must not enter the persistent negative cache below.
                if (token.IsCancellationRequested) throw;
                return null;
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
                    Failures[failureKey] = DateTime.UtcNow.AddSeconds(30);
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

    private static async Task<Stream> OpenVerifiedOriginalAsync(MediaAssetSnapshot manifest, MediaAssetReference asset,
        VerifiedFileMemo verificationMemo, CancellationToken token, long request)
    {
        using (PerformanceDiagnostics.Measure("gallery.poster.verify", request, asset: asset.AssetId))
        {
            Stream video = await manifest.OpenSessionVerifiedReadAsync(asset.AssetId, verificationMemo, token)
                .ConfigureAwait(false);
            PerformanceDiagnostics.Record("gallery.poster.verified-bytes", request: request, asset: asset.AssetId, value: asset.Length);
            return video;
        }
    }

    private static async Task VerifyOriginalAsync(MediaAssetSnapshot manifest, MediaAssetReference asset,
        VerifiedFileMemo verificationMemo, CancellationToken token, long request)
    {
        await using Stream verified = await OpenVerifiedOriginalAsync(manifest, asset, verificationMemo, token, request)
            .ConfigureAwait(false);
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
    internal static void Trim(string directory, int incomingBytes) => Trim(directory, incomingBytes, Interlocked.Read(ref _cacheBytes), Volatile.Read(ref _cacheFiles));

    internal static void Trim(string directory, int incomingBytes, long maximumBytes, int maximumFiles)
    {
        FileInfo[] files = Directory.EnumerateFiles(directory, "*.png").Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        long bytes = files.Sum(file => file.Length);
        int count = files.Length;
        foreach (FileInfo file in files)
        {
            if (count < maximumFiles && bytes + incomingBytes <= maximumBytes) break;
            long length = file.Length;
            file.Delete(); count--; bytes -= length;
        }
        foreach (string temporary in Directory.EnumerateFiles(directory, ".poster-*.tmp"))
            if (File.GetLastWriteTimeUtc(temporary) < DateTime.UtcNow.AddDays(-1)) File.Delete(temporary);
    }
}

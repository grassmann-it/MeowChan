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
    // A private admission lets a test fill the bounded queue without touching the process-wide one.
    internal VideoPosterAdmission? Admission { get; init; }
    // Observes each complete pixel decode used for validation (warm hits must not perform one).
    internal Action? FullDecodeValidation { get; init; }
    // Replaces File.Delete for eviction candidates so a locked/in-use candidate can be simulated on every platform.
    internal Action<string>? DeleteCacheFile { get; init; }
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

    /// <summary>
    /// The configured cache limits. Lowering them evicts the oldest previews when the next one is saved; originals are never touched.
    /// A save that brings the estimated directory size over a limit evicts down to a low-water mark (1/16 below the limit), so a full
    /// cache does not rescan its directory on every save.
    /// </summary>
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
    /// <summary>Process-wide request admission: 8 working requests, at most 64 more waiting for a slot.</summary>
    internal static VideoPosterAdmission Admission { get; } = new(activeLimit: 8, queueLimit: 64);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    // Eviction runs outside NativeDecodeGate, at most one directory scan per directory at a time; a save that finds a scan already
    // running does not wait for it (its bytes are added to that scan's result). The ledger is only an estimate that decides when a
    // scan is due: it can overestimate (files deleted or replaced elsewhere), which only causes an earlier scan, and it is rebuilt from
    // the real directory by every scan and at least every RescanInterval saves, so external additions cannot hide indefinitely.
    private static readonly object LedgersLock = new();
    private static readonly Dictionary<string, DirectoryLedger> Ledgers = new(PathComparer);
    private const int MaximumLedgers = 32;
    private const int RescanInterval = 256;

    private sealed class DirectoryLedger
    {
        internal readonly SemaphoreSlim TrimGate = new(1, 1);
        internal bool Known;
        internal long Bytes;
        internal int Files;
        internal int SavesSinceScan;
        // Monotonic totals of saves; a scan adds the saves that happened while it ran (they may or may not have been enumerated).
        internal long SavedBytes;
        internal long SavedFiles;
    }

    internal readonly record struct TrimResult(long Bytes, int Files);

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
        VideoPosterCacheTestSeam? testSeam = null, bool useFailureBackoff = true)
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
        if (useFailureBackoff)
            lock (FailuresLock)
                if (Failures.TryGetValue(failureKey, out DateTime retryAt) && retryAt > DateTime.UtcNow) return null;
        // A burst beyond the working slots waits (caller token only, outside the watchdog below) instead of returning an untracked
        // missing poster; only a full bounded queue refuses, without a negative cache entry, so a later request can still succeed.
        VideoPosterAdmission admission = testSeam?.Admission ?? Admission;
        if (!await admission.TryEnterAsync(token).ConfigureAwait(false))
        {
            PerformanceDiagnostics.Record("gallery.poster", "queue-full", asset: asset.AssetId);
            return null;
        }
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
                byte[] published;
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
                        Func<Stream, CancellationToken, Task<byte[]>> decoder = testSeam?.Decoder
                            ?? ((verified, cancellation) => VideoPosterDecoder.DecodeAsync(verified, asset.Path, cancellation));
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
                    if (!Valid(png, testSeam?.FullDecodeValidation))
                        throw new InvalidDataException("The video poster exceeds its safe limits.");

                    // The decoder held a verified stream, but the path may have been replaced while native work
                    // was running. Do not publish or return that frame until the original is verified once more.
                    await VerifyOriginalAsync(manifest, asset, verificationMemo, bounded.Token, request);
                    bounded.Token.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(directory);
                    string temporary = Path.Combine(directory, ".poster-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, png, bounded.Token).ConfigureAwait(false);
                        bounded.Token.ThrowIfCancellationRequested();
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally
                    {
                        // Cleanup must never replace the original failure (or a successful publish) with its own error.
                        try { File.Delete(temporary); }
                        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                    }
                    published = png;
                }
                finally { Gate.Release(); }

                // Eviction is bookkeeping for the derived directory, not native work: it runs after the gate is released and its
                // failures (a vanished, locked or protected candidate) never turn the poster just published into a failure.
                TrimAfterPublish(directory, path, published.Length, testSeam?.DeleteCacheFile);
                return published;
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
                if (useFailureBackoff && !token.IsCancellationRequested)
                {
                    lock (FailuresLock)
                    {
                        if (Failures.Count >= 256) Failures.Remove(Failures.MinBy(pair => pair.Value).Key);
                        Failures[failureKey] = DateTime.UtcNow.AddSeconds(30);
                    }
                }
                return null;
            }
            finally { admission.Exit(); }
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

    /// <summary>
    /// A stored poster that is complete and CRC-intact, or null. No pixel decode happens here: the caller's display decode is the only
    /// full decode of a warm hit. A file that fails the check is deleted, unless it was replaced while it was being read.
    /// </summary>
    private static async Task<byte[]?> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists) return null;
            long observedLength = file.Length;
            DateTime observedWrite = file.LastWriteTimeUtc;
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && observedLength is > 0 and <= MaximumPngBytes)
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                if (ValidStored(bytes)) return bytes;
            }
            DeleteIfUnchanged(path, observedLength, observedWrite);
            return null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        // An unreadable derived file is a miss, never a poster; generation may still replace it.
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void DeleteIfUnchanged(string path, long length, DateTime lastWriteUtc)
    {
        try
        {
            FileInfo current = new(path);
            if (current.Exists && current.Length == length && current.LastWriteTimeUtc == lastWriteUtc) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Publish check for fresh decoder output: complete structure, CRC-32, bounds, and one successful full decode.</summary>
    internal static bool Valid(byte[] png) => Valid(png, fullDecodeObserver: null);

    private static bool Valid(byte[] png, Action? fullDecodeObserver) =>
        PreviewPngIntegrity.IsCompleteDecodable(png, MaximumPngBytes, VideoPosterDecoder.PosterMaximumWidth,
            VideoPosterDecoder.PosterMaximumHeight, fullDecodeObserver);

    /// <summary>Warm-hit check: complete structure, CRC-32, bounds and codec header; no pixel decode.</summary>
    internal static bool ValidStored(byte[] png) =>
        PreviewPngIntegrity.IsIntactStored(png, MaximumPngBytes, VideoPosterDecoder.PosterMaximumWidth, VideoPosterDecoder.PosterMaximumHeight);

    private static void TrimAfterPublish(string directory, string published, int length, Action<string>? delete)
    {
        try
        {
            DirectoryLedger ledger;
            lock (LedgersLock)
            {
                ledger = LedgerForLocked(directory);
                ledger.SavedBytes += length;
                ledger.SavedFiles++;
                if (ledger.Known)
                {
                    ledger.Bytes += length;
                    if (ledger.Files < int.MaxValue) ledger.Files++;
                    ledger.SavesSinceScan++;
                }
                if (!ScanDueLocked(ledger)) return;
            }

            // A running scan of this directory accounts for this save (SavedBytes below); never queue saves behind it.
            if (!ledger.TrimGate.Wait(0)) return;
            try
            {
                (long maximumBytes, int maximumFiles) = Limits;
                long savedBytes;
                long savedFiles;
                lock (LedgersLock)
                {
                    // A scan that finished while this save waited may already have brought the directory under its limits.
                    if (!ScanDueLocked(ledger)) return;
                    savedBytes = ledger.SavedBytes;
                    savedFiles = ledger.SavedFiles;
                }

                TrimResult result = TrimCore(directory, published, LowWater(maximumBytes), LowWater(maximumFiles), delete);
                lock (LedgersLock)
                {
                    ledger.Bytes = result.Bytes + (ledger.SavedBytes - savedBytes);
                    ledger.Files = (int)Math.Min(int.MaxValue, result.Files + (ledger.SavedFiles - savedFiles));
                    ledger.SavesSinceScan = 0;
                    ledger.Known = true;
                }
            }
            finally { ledger.TrimGate.Release(); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warning(error, "Could not reduce the video poster cache to its limits; the new poster stays available.");
        }
    }

    private static bool ScanDueLocked(DirectoryLedger ledger)
    {
        (long maximumBytes, int maximumFiles) = Limits;
        return !ledger.Known || ledger.Bytes > maximumBytes || ledger.Files > maximumFiles || ledger.SavesSinceScan >= RescanInterval;
    }

    private static DirectoryLedger LedgerForLocked(string directory)
    {
        if (Ledgers.TryGetValue(directory, out DirectoryLedger? ledger)) return ledger;
        // Dropping estimates is always safe: an unknown ledger only schedules a scan.
        if (Ledgers.Count >= MaximumLedgers) Ledgers.Clear();
        ledger = new DirectoryLedger();
        Ledgers.Add(directory, ledger);
        return ledger;
    }

    private static long LowWater(long maximumBytes) => maximumBytes - maximumBytes / 16;
    private static int LowWater(int maximumFiles) => Math.Max(1, maximumFiles - maximumFiles / 16);

    /// <summary>Makes room for <paramref name="incomingBytes"/> more under the configured limits (explicit callers, tests).</summary>
    internal static void Trim(string directory, int incomingBytes) => Trim(directory, incomingBytes, Interlocked.Read(ref _cacheBytes), Volatile.Read(ref _cacheFiles));

    internal static void Trim(string directory, int incomingBytes, long maximumBytes, int maximumFiles) =>
        Trim(directory, incomingBytes, maximumBytes, maximumFiles, delete: null);

    /// <param name="delete">Test seam replacing <see cref="File.Delete"/> for eviction candidates.</param>
    internal static TrimResult Trim(string directory, int incomingBytes, long maximumBytes, int maximumFiles, Action<string>? delete)
    {
        string fullDirectory = Path.GetFullPath(directory);
        DirectoryLedger ledger;
        lock (LedgersLock)
            ledger = LedgerForLocked(fullDirectory);

        // Publish-triggered trims and explicit maintenance trims must observe one directory at a time. Without this gate two
        // concurrent scans can both decide that the same oldest candidate is present, then publish stale byte/file totals.
        ledger.TrimGate.Wait();
        try
        {
            long savedBytes;
            long savedFiles;
            lock (LedgersLock)
            {
                savedBytes = ledger.SavedBytes;
                savedFiles = ledger.SavedFiles;
            }

            TrimResult result = TrimCore(fullDirectory, protectedPath: null, maximumBytes - incomingBytes, maximumFiles - 1, delete);
            lock (LedgersLock)
            {
                ledger.Bytes = result.Bytes + (ledger.SavedBytes - savedBytes);
                ledger.Files = (int)Math.Min(int.MaxValue, result.Files + (ledger.SavedFiles - savedFiles));
                ledger.SavesSinceScan = 0;
                ledger.Known = true;
            }
            return result;
        }
        finally { ledger.TrimGate.Release(); }
    }

    /// <summary>
    /// Evicts the oldest <c>*.png</c> files (by write time) until at most <paramref name="targetFiles"/> files and
    /// <paramref name="targetBytes"/> bytes remain, never evicting <paramref name="protectedPath"/>. A candidate that vanished counts
    /// as evicted; one that cannot be read or deleted (in use, locked, no permission) is kept and the next candidate is tried.
    /// Returns what remains according to this scan.
    /// </summary>
    private static TrimResult TrimCore(string directory, string? protectedPath, long targetBytes, int targetFiles, Action<string>? delete)
    {
        List<(string Path, long Length, DateTime Written)> files = new();
        long bytes = 0;
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, "*.png"))
            {
                try
                {
                    FileInfo info = new(candidate);
                    long length = info.Length;
                    files.Add((candidate, length, info.LastWriteTimeUtc));
                    bytes += length;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (DirectoryNotFoundException) { return new TrimResult(0, 0); }

        int count = files.Count;
        foreach ((string candidate, long length, _) in files.OrderBy(file => file.Written).ThenBy(file => file.Path, StringComparer.Ordinal))
        {
            if (count <= targetFiles && bytes <= targetBytes) break;
            if (protectedPath != null && PathComparer.Equals(candidate, protectedPath)) continue;
            try { (delete ?? File.Delete)(candidate); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            count--; bytes -= length;
        }

        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string temporary in Directory.EnumerateFiles(directory, ".poster-*.tmp"))
            {
                try { if (File.GetLastWriteTimeUtc(temporary) < cutoff) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return new TrimResult(bytes, count);
    }
}

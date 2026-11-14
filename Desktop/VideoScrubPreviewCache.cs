using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.Desktop;

/// <summary>Decodes one scrub thumbnail. Injectable so the cache and its concurrency are testable without a native decoder.</summary>
internal interface IVideoScrubFrameDecoder
{
    /// <summary>
    /// Returns a PNG of the frame near <paramref name="start"/> that fits the bounds. Owns <paramref name="verifiedVideo"/> from the call on,
    /// including failures and cancellation. Should observe <paramref name="token"/>; one that does not is outwaited by the cache's
    /// decode timeout while the native gate stays occupied until it really returns.
    /// </summary>
    Task<byte[]> DecodeAsync(Stream verifiedVideo, TimeSpan start, int maximumWidth, int maximumHeight, CancellationToken token);
}

/// <summary>The production decoder: <see cref="VideoPosterDecoder.DecodeAtAsync"/> (memory output, no window, audio or network).</summary>
internal sealed class NativeVideoScrubFrameDecoder : IVideoScrubFrameDecoder
{
    internal static NativeVideoScrubFrameDecoder Instance { get; } = new();
    private NativeVideoScrubFrameDecoder() { }

    public Task<byte[]> DecodeAsync(Stream verifiedVideo, TimeSpan start, int maximumWidth, int maximumHeight, CancellationToken token) =>
        VideoPosterDecoder.DecodeAtAsync(verifiedVideo, start, maximumWidth, maximumHeight, token);
}

/// <summary>
/// Disposable derived scrub thumbnails on disk, keyed by <see cref="VideoScrubFrameKey"/>, inside one directory of their own (keep it apart
/// from the poster directory: both evict every <c>*.png</c> of their directory). Never fetches source URLs or touches originals. The
/// budget comes from the caller (<see cref="Configure"/>); a lower budget is enforced while it is configured and at the next store or
/// <see cref="TrimToBudget"/>. Per-video work, coalescing and cancellation live in <see cref="VideoScrubPreviewSession"/>.
/// </summary>
internal sealed class VideoScrubPreviewCache
{
    private const string TemporaryPrefix = ".poster-scrub-";
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    // All instances in this process use the same coordinator for a directory. The filesystem is scanned only when a coordinator is
    // first used, or when an explicit resync/trim is requested; ordinary stores only reconcile their destination path. The values are
    // weak so a short-lived cache directory does not retain a SemaphoreSlim forever; each cache keeps its coordinator strongly while it
    // is alive, which also prevents a new coordinator from being created for a live directory.
    private static readonly ConcurrentDictionary<string, WeakReference<SharedDirectoryState>> SharedStates = new(PathComparer);

    private readonly SharedDirectoryState _state;
    private readonly Func<VideoScrubFrameKey, CancellationToken, Task<byte[]?>>? _readOverride;
    private readonly Func<VideoScrubFrameKey, byte[], CancellationToken, Task>? _storeOverride;
    private VideoScrubPreviewOptions _options;

    private sealed class CacheEntry
    {
        internal CacheEntry(string path, long length) { Path = path; Length = length; }
        internal string Path { get; }
        internal long Length { get; set; }
        internal LinkedListNode<CacheEntry>? Node { get; set; }
    }

    private readonly record struct DiskFile(string Path, long Length, DateTime LastWriteTimeUtc);

    /// <summary>
    /// Process-wide bookkeeping and serialization for one directory. Keeping this shared is important: two cache objects must not each
    /// believe they own the whole byte budget. A caller that changes files outside this class can repair the ledger with <see cref="Resync"/>.
    /// </summary>
    private sealed class SharedDirectoryState
    {
        internal SharedDirectoryState(string directory) { Directory = directory; }

        internal string Directory { get; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal Dictionary<string, CacheEntry> Entries { get; } = new(PathComparer);
        internal LinkedList<CacheEntry> Lru { get; } = new();
        internal bool Initialized { get; private set; }
        private bool _directoryPresent;
        private ulong _bytes;

        internal void EnsureInitializedLocked()
        {
            bool directoryPresent = System.IO.Directory.Exists(Directory);
            if (!Initialized || directoryPresent != _directoryPresent)
                ResyncLocked();
        }

        internal void ResyncLocked()
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                ClearLocked();
                Initialized = true;
                _directoryPresent = false;
                return;
            }

            RemoveStaleTemporaries();
            List<DiskFile> files = EnumerateFiles();
            if (!Initialized)
            {
                ClearLocked();
                foreach (DiskFile file in files.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Path, PathComparer))
                    AddLocked(file.Path, file.Length);
            }
            else
            {
                Dictionary<string, DiskFile> onDisk = new(PathComparer);
                foreach (DiskFile file in files) onDisk[file.Path] = file;

                foreach (CacheEntry entry in Entries.Values.ToArray())
                {
                    if (!onDisk.TryGetValue(entry.Path, out DiskFile file))
                        RemoveLocked(entry);
                    else
                        UpdateLengthLocked(entry, file.Length);
                }

                // Preserve the LRU order of known files so an explicit resync repairs accounting without turning a read hit back
                // into a write-time entry. Newly discovered files are appended in deterministic write-time order.
                foreach (DiskFile file in files.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Path, PathComparer))
                    if (!Entries.ContainsKey(file.Path)) AddLocked(file.Path, file.Length);
            }

            Initialized = true;
            _directoryPresent = true;
        }

        internal void RefreshDestinationLocked(string path)
        {
            if (TryGetLength(path, out long length))
            {
                if (Entries.TryGetValue(path, out CacheEntry? entry))
                    UpdateLengthLocked(entry, length);
                else
                    AddLocked(path, length);
            }
            else if (Entries.TryGetValue(path, out CacheEntry? missing))
            {
                RemoveLocked(missing);
            }
        }

        internal void RecordHitLocked(string path, long length)
        {
            if (Entries.TryGetValue(path, out CacheEntry? entry))
            {
                UpdateLengthLocked(entry, length);
                TouchLocked(entry);
            }
            else
            {
                AddLocked(path, length);
            }
        }

        internal void RemovePathLocked(string path)
        {
            if (Entries.TryGetValue(path, out CacheEntry? entry)) RemoveLocked(entry);
        }

        internal void MakeRoomForLocked(string destination, long incomingLength, long maximumBytes)
        {
            if (incomingLength > maximumBytes)
                throw new IOException("The scrub preview is larger than the configured cache budget.");

            RefreshDestinationLocked(destination);
            long oldLength = Entries.TryGetValue(destination, out CacheEntry? existing) ? existing.Length : 0;
            ulong limit = (ulong)maximumBytes;
            while (PredictedBytes(oldLength, incomingLength) > limit)
            {
                CacheEntry? candidate = FirstExcept(destination);
                if (candidate == null) break;
                if (!TryRefreshCandidateLocked(candidate)) continue;
                DeleteAndRemoveLocked(candidate);
            }

            if (PredictedBytes(oldLength, incomingLength) > limit)
                throw new IOException("The scrub preview cache could not make room within its configured byte budget.");
        }

        internal void TrimToBudgetLocked(long maximumBytes)
        {
            ulong limit = (ulong)maximumBytes;
            while (_bytes > limit)
            {
                CacheEntry? candidate = FirstExcept(protectedPath: null);
                if (candidate == null) break;
                if (!TryRefreshCandidateLocked(candidate)) continue;
                DeleteAndRemoveLocked(candidate);
            }

            if (_bytes > limit)
                throw new IOException("The scrub preview cache could not be reduced to its configured byte budget.");
        }

        internal void TrimToBudgetLocked(long maximumBytes, string protectedPath)
        {
            ulong limit = (ulong)maximumBytes;
            while (_bytes > limit)
            {
                CacheEntry? candidate = FirstExcept(protectedPath);
                if (candidate == null) break;
                if (!TryRefreshCandidateLocked(candidate)) continue;
                DeleteAndRemoveLocked(candidate);
            }

            if (_bytes > limit)
                throw new IOException("The scrub preview cache could not be reduced to its configured byte budget.");
        }

        private ulong PredictedBytes(long oldLength, long incomingLength)
        {
            ulong current = _bytes;
            ulong old = (ulong)Math.Max(0, oldLength);
            if (current >= old) current -= old;
            else current = 0;
            ulong incoming = (ulong)incomingLength;
            return ulong.MaxValue - current < incoming ? ulong.MaxValue : current + incoming;
        }

        private CacheEntry? FirstExcept(string? protectedPath)
        {
            for (LinkedListNode<CacheEntry>? node = Lru.First; node != null; node = node.Next)
                if (protectedPath == null || !PathComparer.Equals(node.Value.Path, protectedPath)) return node.Value;
            return null;
        }

        private bool TryRefreshCandidateLocked(CacheEntry entry)
        {
            if (!Entries.ContainsKey(entry.Path)) return false;
            if (!TryGetLength(entry.Path, out long length))
            {
                RemoveLocked(entry);
                return false;
            }
            UpdateLengthLocked(entry, length);
            return true;
        }

        private void DeleteAndRemoveLocked(CacheEntry entry)
        {
            try { File.Delete(entry.Path); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            RemoveLocked(entry);
        }

        private void RemoveStaleTemporaries()
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-1);
            try
            {
                foreach (string temporary in System.IO.Directory.EnumerateFiles(Directory, TemporaryPrefix + "*.tmp", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(temporary) < cutoff) File.Delete(temporary);
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private List<DiskFile> EnumerateFiles()
        {
            List<DiskFile> files = new();
            try
            {
                foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.png", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        FileInfo info = new(path);
                        files.Add(new DiskFile(path, info.Length, info.LastWriteTimeUtc));
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
            }
            catch (DirectoryNotFoundException) { }
            return files;
        }

        private static bool TryGetLength(string path, out long length)
        {
            try
            {
                length = new FileInfo(path).Length;
                return true;
            }
            catch (FileNotFoundException) { length = 0; return false; }
            catch (DirectoryNotFoundException) { length = 0; return false; }
        }

        private void AddLocked(string path, long length)
        {
            if (Entries.ContainsKey(path)) return;
            CacheEntry entry = new(path, length);
            entry.Node = Lru.AddLast(entry);
            Entries.Add(path, entry);
            AddBytes(length);
        }

        private void UpdateLengthLocked(CacheEntry entry, long length)
        {
            if (length == entry.Length) return;
            if (length > entry.Length)
                AddBytes(length - entry.Length);
            else
                _bytes -= Math.Min(_bytes, (ulong)(entry.Length - length));
            entry.Length = length;
        }

        private void TouchLocked(CacheEntry entry)
        {
            if (entry.Node == Lru.Last) return;
            if (entry.Node != null) Lru.Remove(entry.Node);
            entry.Node = Lru.AddLast(entry);
        }

        private void RemoveLocked(CacheEntry entry)
        {
            if (!Entries.Remove(entry.Path)) return;
            if (entry.Node != null) Lru.Remove(entry.Node);
            _bytes -= Math.Min(_bytes, (ulong)Math.Max(0, entry.Length));
            entry.Node = null;
        }

        private void ClearLocked()
        {
            Entries.Clear();
            Lru.Clear();
            _bytes = 0;
        }

        private void AddBytes(long length)
        {
            ulong value = (ulong)Math.Max(0, length);
            _bytes = ulong.MaxValue - _bytes < value ? ulong.MaxValue : _bytes + value;
        }
    }

    /// <param name="decodeGate">Serializes native decode sessions; null shares <see cref="VideoPosterCache.NativeDecodeGate"/> with posters.</param>
    /// <param name="readOverride">Internal deterministic test seam; production callers leave cache reads on the file system.</param>
    /// <param name="storeOverride">Internal deterministic test seam; production callers leave cache writes on the file system.</param>
    internal VideoScrubPreviewCache(string directory, VideoScrubPreviewOptions options, IVideoScrubFrameDecoder? decoder = null,
        SemaphoreSlim? decodeGate = null, Func<VideoScrubFrameKey, CancellationToken, Task<byte[]?>>? readOverride = null,
        Func<VideoScrubFrameKey, byte[], CancellationToken, Task>? storeOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _state = StateFor(Directory);
        Decoder = decoder ?? NativeVideoScrubFrameDecoder.Instance;
        DecodeGate = decodeGate ?? VideoPosterCache.NativeDecodeGate;
        _readOverride = readOverride;
        _storeOverride = storeOverride;
    }

    internal string Directory { get; }
    internal IVideoScrubFrameDecoder Decoder { get; }
    internal SemaphoreSlim DecodeGate { get; }
    internal VideoScrubPreviewOptions Options => Volatile.Read(ref _options);

    /// <summary>Applies new limits and immediately enforces a lower byte budget.</summary>
    internal void Configure(VideoScrubPreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VideoScrubPreviewOptions previous = Options;
        Volatile.Write(ref _options, options);
        if (options.CacheLimitBytes < previous.CacheLimitBytes)
            TrimToBudget(options.CacheLimitBytes);
    }

    /// <summary>
    /// A session for one recorded video, or null when it has no scrub previews (not a video of this manifest, too large, malformed checksum).
    /// <paramref name="verifiedFiles"/> lets the caller share its memory of verified files; otherwise the session keeps its own, so the
    /// original is hashed once per session and then only its file identity is compared.
    /// </summary>
    internal VideoScrubPreviewSession? OpenSession(MediaAssetSnapshot manifest, MediaAssetReference asset, VerifiedFileMemo? verifiedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(asset);
        return VideoScrubFrameKey.IsEligible(manifest, asset)
            ? new VideoScrubPreviewSession(this, manifest, asset, verifiedFiles ?? new VerifiedFileMemo(capacity: 4))
            : null;
    }

    internal string PathOf(VideoScrubFrameKey key) => Path.Combine(Directory, key.FileName);

    /// <summary>A stored thumbnail that is still valid, or null; an invalid one (wrong size, not a PNG, link) is deleted.</summary>
    internal async Task<byte[]?> ReadAsync(VideoScrubFrameKey key, CancellationToken token)
    {
        if (_readOverride is { } readOverride)
            return await readOverride(key, token).ConfigureAwait(false);

        string path = PathOf(key);
        SharedDirectoryState state = _state;
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            state.EnsureInitializedLocked();
            FileInfo file = new(path);
            if (!file.Exists)
            {
                state.RemovePathLocked(path);
                return null;
            }
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.Length is > 0 and <= VideoScrubPreviewOptions.MaximumThumbnailBytes)
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                if (Valid(bytes))
                {
                    state.RecordHitLocked(path, bytes.LongLength);
                    // A valid file discovered after the initial scan is still brought under the live budget. This is a cheap
                    // ledger operation; only an explicit TrimToBudget/Resync performs a directory enumeration.
                    state.TrimToBudgetLocked(Options.CacheLimitBytes, path);
                    return bytes;
                }
            }

            try { File.Delete(path); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            state.RemovePathLocked(path);
            return null;
        }
        catch (FileNotFoundException)
        {
            state.RemovePathLocked(path);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            state.RemovePathLocked(path);
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { state.Gate.Release(); }
    }

    /// <summary>
    /// Writes a validated thumbnail through a temporary file, then accounts and publishes it in one shared directory critical section.
    /// The temporary file is never entered into the byte ledger, so cancellation or a failed write cannot create phantom bytes.
    /// </summary>
    internal async Task StoreAsync(VideoScrubFrameKey key, byte[] png, CancellationToken token)
    {
        if (!Valid(png)) throw new InvalidDataException("The scrub thumbnail exceeds its safe limits.");
        if (_storeOverride is { } storeOverride)
        {
            await storeOverride(key, png, token).ConfigureAwait(false);
            return;
        }

        SharedDirectoryState state = _state;
        // Observe a directory deletion before recreating it. Otherwise CreateDirectory would hide the transition and stale ledger
        // entries could survive a whole-directory cleanup. This is only a cheap presence check for an initialized directory; the
        // expensive enumeration still happens only on initialization, a presence transition, or explicit repair.
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try { state.EnsureInitializedLocked(); }
        finally { state.Gate.Release(); }

        token.ThrowIfCancellationRequested();
        System.IO.Directory.CreateDirectory(Directory);
        string temporary = Path.Combine(Directory, TemporaryPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, png, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await state.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                state.EnsureInitializedLocked();
                state.MakeRoomForLocked(PathOf(key), png.LongLength, Options.CacheLimitBytes);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, PathOf(key), overwrite: true);
                state.RecordHitLocked(PathOf(key), png.LongLength);
            }
            finally { state.Gate.Release(); }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Re-reads the directory and then evicts the oldest (least recently hit) files until the current budget fits. This is intentionally
    /// the expensive repair path for files removed or changed outside the cache, and is not used by every store.
    /// </summary>
    internal void TrimToBudget() => TrimToBudget(Options.CacheLimitBytes);

    /// <summary>Explicitly repairs the shared ledger after an external file operation, without changing the current budget.</summary>
    internal void Resync()
    {
        SharedDirectoryState state = _state;
        state.Gate.Wait();
        try { state.ResyncLocked(); }
        finally { state.Gate.Release(); }
    }

    private void TrimToBudget(long maximumBytes)
    {
        SharedDirectoryState state = _state;
        state.Gate.Wait();
        try
        {
            state.ResyncLocked();
            state.TrimToBudgetLocked(maximumBytes);
        }
        finally { state.Gate.Release(); }
    }

    private static SharedDirectoryState StateFor(string directory)
    {
        while (true)
        {
            if (SharedStates.TryGetValue(directory, out WeakReference<SharedDirectoryState>? weak)
                && weak.TryGetTarget(out SharedDirectoryState? existing))
                return existing;

            SharedDirectoryState created = new(directory);
            WeakReference<SharedDirectoryState> replacement = new(created);
            if (SharedStates.TryAdd(directory, replacement)) return created;
            if (weak != null && SharedStates.TryUpdate(directory, replacement, weak)) return created;
        }
    }

    internal static bool Valid(byte[] png)
    {
        if (png.Length == 0 || png.Length > VideoScrubPreviewOptions.MaximumThumbnailBytes) return false;
        using MemoryStream encoded = new(png, writable: false);
        using SKCodec? codec = SKCodec.Create(encoded);
        if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Png
            || codec.Info.Width is < 1 or > VideoScrubPreviewOptions.ThumbnailMaximumWidth
            || codec.Info.Height is < 1 or > VideoScrubPreviewOptions.ThumbnailMaximumHeight) return false;
        using SKBitmap bitmap = SKBitmap.Decode(png);
        return bitmap != null && bitmap.Width == codec.Info.Width && bitmap.Height == codec.Info.Height;
    }
}

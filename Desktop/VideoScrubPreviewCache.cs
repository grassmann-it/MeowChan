using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;

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

/// <summary>Deterministic hooks for direct cache tests. Production callers pass none.</summary>
internal sealed class VideoScrubPreviewCacheTestSeam
{
    // Runs after a stored thumbnail was read from disk and before it is validated and accounted, i.e. while no coordinator is held.
    internal Func<string, CancellationToken, Task>? AfterFileRead { get; init; }
    // Observes each complete pixel decode used for validation (warm hits must not perform one).
    internal Action? FullDecodeValidation { get; init; }
    // Replaces File.Delete for eviction candidates so a locked/in-use candidate can be simulated on every platform.
    internal Action<string>? DeleteCacheFile { get; init; }
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
    private readonly VideoScrubPreviewCacheTestSeam? _testSeam;
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

        // Test seam replacing File.Delete for eviction candidates; the last cache constructed with a seam sets it.
        internal Action<string>? DeleteOverride { get; set; }

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

        internal void TouchPathLocked(string path)
        {
            if (Entries.TryGetValue(path, out CacheEntry? entry)) TouchLocked(entry);
        }

        internal void MakeRoomForLocked(string destination, long incomingLength, long maximumBytes)
        {
            if (incomingLength > maximumBytes)
                throw new IOException("The scrub preview is larger than the configured cache budget.");

            RefreshDestinationLocked(destination);
            long oldLength = Entries.TryGetValue(destination, out CacheEntry? existing) ? existing.Length : 0;
            ulong limit = (ulong)maximumBytes;
            EvictLocked(() => PredictedBytes(oldLength, incomingLength) > limit, destination);

            // The byte budget is a hard invariant for stores: if locked candidates leave too little room, nothing is published.
            if (PredictedBytes(oldLength, incomingLength) > limit)
                throw new IOException("The scrub preview cache could not make room within its configured byte budget.");
        }

        /// <summary>
        /// Evicts least recently used entries (never <paramref name="protectedPath"/>) until the ledger fits. Returns false when locked
        /// or otherwise undeletable candidates left it over budget; those stay accounted and are retried by the next trim.
        /// </summary>
        internal bool TrimToBudgetLocked(long maximumBytes, string? protectedPath = null)
        {
            ulong limit = (ulong)maximumBytes;
            EvictLocked(() => _bytes > limit, protectedPath);
            return _bytes <= limit;
        }

        private void EvictLocked(Func<bool> overBudget, string? protectedPath)
        {
            LinkedListNode<CacheEntry>? node = Lru.First;
            while (node != null && overBudget())
            {
                LinkedListNode<CacheEntry>? next = node.Next;
                CacheEntry candidate = node.Value;
                if ((protectedPath == null || !PathComparer.Equals(candidate.Path, protectedPath)) && TryRefreshCandidateLocked(candidate))
                    TryDeleteAndRemoveLocked(candidate);
                node = next;
            }
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

        private bool TryRefreshCandidateLocked(CacheEntry entry)
        {
            if (!Entries.ContainsKey(entry.Path)) return false;
            long length;
            try
            {
                if (!TryGetLength(entry.Path, out length))
                {
                    RemoveLocked(entry);
                    return false;
                }
            }
            // An unreadable candidate keeps its last known length and is simply not evicted now.
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            UpdateLengthLocked(entry, length);
            return true;
        }

        private bool TryDeleteAndRemoveLocked(CacheEntry entry)
        {
            try { (DeleteOverride ?? File.Delete)(entry.Path); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            // In use (Windows readers hold FileShare.Read without Delete), locked or protected: keep it accounted, try the next one.
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            RemoveLocked(entry);
            return true;
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
    /// <param name="testSeam">Internal deterministic hooks for direct cache tests; production callers pass none.</param>
    internal VideoScrubPreviewCache(string directory, VideoScrubPreviewOptions options, IVideoScrubFrameDecoder? decoder = null,
        SemaphoreSlim? decodeGate = null, Func<VideoScrubFrameKey, CancellationToken, Task<byte[]?>>? readOverride = null,
        Func<VideoScrubFrameKey, byte[], CancellationToken, Task>? storeOverride = null, VideoScrubPreviewCacheTestSeam? testSeam = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _state = StateFor(Directory);
        Decoder = decoder ?? NativeVideoScrubFrameDecoder.Instance;
        DecodeGate = decodeGate ?? VideoPosterCache.NativeDecodeGate;
        _readOverride = readOverride;
        _storeOverride = storeOverride;
        _testSeam = testSeam;
        if (testSeam?.DeleteCacheFile is { } delete) _state.DeleteOverride = delete;
    }

    internal string Directory { get; }
    internal IVideoScrubFrameDecoder Decoder { get; }
    internal SemaphoreSlim DecodeGate { get; }
    internal VideoScrubPreviewOptions Options => Volatile.Read(ref _options);

    /// <summary>
    /// Applies new limits and immediately enforces a lower byte budget. Synchronous for callers that already run off the UI thread
    /// (the profile host serializes these calls on the thread pool); reads never hold the coordinator over file I/O or decoding, so the
    /// wait is bounded by bookkeeping, a directory scan and deletions. UI-thread callers use <see cref="ConfigureAsync"/>.
    /// </summary>
    internal void Configure(VideoScrubPreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VideoScrubPreviewOptions previous = Options;
        Volatile.Write(ref _options, options);
        if (options.CacheLimitBytes < previous.CacheLimitBytes)
            TrimToBudget(options.CacheLimitBytes);
    }

    /// <summary>Like <see cref="Configure"/>, but waits for the coordinator asynchronously and runs the trim on the thread pool.</summary>
    internal async Task ConfigureAsync(VideoScrubPreviewOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);
        VideoScrubPreviewOptions previous = Options;
        Volatile.Write(ref _options, options);
        if (options.CacheLimitBytes < previous.CacheLimitBytes)
            await TrimToBudgetAsync(options.CacheLimitBytes, token).ConfigureAwait(false);
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

    /// <summary>
    /// A stored thumbnail that is still valid, or null; an invalid one (wrong size, not a complete CRC-intact PNG, link) is deleted.
    /// The coordinator is held only for bookkeeping: reading the file and checking it run outside it, so a slow disk read never blocks
    /// stores, trims or a budget change. Because files only change under the coordinator, ownership is re-checked there: a hit is
    /// accounted only while its file still exists, and an invalid file is deleted only if it was not replaced in the meantime.
    /// A warm hit performs no pixel decode; the caller's display decode is the only full decode.
    /// </summary>
    internal async Task<byte[]?> ReadAsync(VideoScrubFrameKey key, CancellationToken token)
    {
        if (_readOverride is { } readOverride)
            return await readOverride(key, token).ConfigureAwait(false);

        string path = PathOf(key);
        SharedDirectoryState state = _state;
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try { state.EnsureInitializedLocked(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { state.Gate.Release(); }

        long observedLength;
        DateTime observedWrite;
        byte[]? bytes = null;
        try
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                await ForgetIfMissingAsync(state, path).ConfigureAwait(false);
                return null;
            }
            observedLength = file.Length;
            observedWrite = file.LastWriteTimeUtc;
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0
                && observedLength is > 0 and <= VideoScrubPreviewOptions.MaximumThumbnailBytes)
            {
                bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                if (_testSeam?.AfterFileRead is { } afterRead) await afterRead(path, token).ConfigureAwait(false);
                if (!ValidStored(bytes)) bytes = null;
            }
        }
        catch (FileNotFoundException)
        {
            await ForgetIfMissingAsync(state, path).ConfigureAwait(false);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            await ForgetIfMissingAsync(state, path).ConfigureAwait(false);
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        // Bookkeeping only. A caller that is canceled now still gets a consistent ledger: this wait is not cancelable.
        await state.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (bytes != null)
            {
                // The verified bytes stay valid for this key even if a concurrent trim removed the file after the read; only an
                // existing file is (re)entered into the ledger, so a deleted one cannot leave phantom bytes behind.
                state.RefreshDestinationLocked(path);
                state.TouchPathLocked(path);
                // A valid file discovered after the initial scan is still brought under the live budget. This is a cheap ledger
                // operation; a locked candidate only delays it and never turns a valid hit into a miss.
                state.TrimToBudgetLocked(Options.CacheLimitBytes, path);
            }
            else
            {
                if (SameFile(path, observedLength, observedWrite))
                {
                    try { File.Delete(path); }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                state.RefreshDestinationLocked(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { state.Gate.Release(); }
        return bytes;
    }

    private static async Task ForgetIfMissingAsync(SharedDirectoryState state, string path)
    {
        await state.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { state.RefreshDestinationLocked(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { state.Gate.Release(); }
    }

    private static bool SameFile(string path, long length, DateTime lastWriteUtc)
    {
        try
        {
            FileInfo current = new(path);
            return current.Exists && current.Length == length && current.LastWriteTimeUtc == lastWriteUtc;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Writes a validated thumbnail through a temporary file, then accounts and publishes it in one shared directory critical section.
    /// The temporary file is never entered into the byte ledger, so cancellation or a failed write cannot create phantom bytes.
    /// </summary>
    internal async Task StoreAsync(VideoScrubFrameKey key, byte[] png, CancellationToken token)
    {
        if (!Valid(png, _testSeam?.FullDecodeValidation)) throw new InvalidDataException("The scrub thumbnail exceeds its safe limits.");
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
    /// the expensive repair path for files removed or changed outside the cache, and is not used by every store. Throws
    /// <see cref="IOException"/> when locked candidates kept the directory over budget (everything deletable was still evicted).
    /// </summary>
    internal void TrimToBudget() => TrimToBudget(Options.CacheLimitBytes);

    /// <summary>The asynchronous form of <see cref="TrimToBudget()"/>: waits for the coordinator without blocking a thread.</summary>
    internal Task TrimToBudgetAsync(CancellationToken token) => TrimToBudgetAsync(Options.CacheLimitBytes, token);

    /// <summary>Explicitly repairs the shared ledger after an external file operation, without changing the current budget.</summary>
    internal void Resync()
    {
        SharedDirectoryState state = _state;
        state.Gate.Wait();
        try { state.ResyncLocked(); }
        finally { state.Gate.Release(); }
    }

    /// <summary>The asynchronous form of <see cref="Resync"/>.</summary>
    internal async Task ResyncAsync(CancellationToken token)
    {
        SharedDirectoryState state = _state;
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(() => state.ResyncLocked(), CancellationToken.None).ConfigureAwait(false); }
        finally { state.Gate.Release(); }
    }

    private void TrimToBudget(long maximumBytes)
    {
        SharedDirectoryState state = _state;
        state.Gate.Wait();
        try { ResyncAndTrimLocked(state, maximumBytes); }
        finally { state.Gate.Release(); }
    }

    private async Task TrimToBudgetAsync(long maximumBytes, CancellationToken token)
    {
        SharedDirectoryState state = _state;
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        // Once started the scan/eviction completes, so the ledger never stops halfway through a deletion.
        try { await Task.Run(() => ResyncAndTrimLocked(state, maximumBytes), CancellationToken.None).ConfigureAwait(false); }
        finally { state.Gate.Release(); }
    }

    private static void ResyncAndTrimLocked(SharedDirectoryState state, long maximumBytes)
    {
        state.ResyncLocked();
        if (!state.TrimToBudgetLocked(maximumBytes))
            throw new IOException("The scrub preview cache could not be reduced to its configured byte budget.");
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
            if (SharedStates.TryAdd(directory, replacement)) { PruneDeadStates(); return created; }
            if (weak != null && SharedStates.TryUpdate(directory, replacement, weak)) { PruneDeadStates(); return created; }
        }
    }

    /// <summary>
    /// Removes entries whose coordinator was collected. The value-matching removal only takes out that exact dead reference, so a live
    /// coordinator published concurrently for the same directory is never dropped (it would otherwise split the shared budget).
    /// </summary>
    private static void PruneDeadStates()
    {
        foreach (KeyValuePair<string, WeakReference<SharedDirectoryState>> entry in SharedStates)
            if (!entry.Value.TryGetTarget(out _))
                SharedStates.TryRemove(entry);
    }

    /// <summary>Whether a coordinator entry (alive or not yet pruned) is registered for the directory (tests).</summary>
    internal static bool HasRegisteredState(string directory) =>
        SharedStates.ContainsKey(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));

    /// <summary>Publish check for fresh decoder output: complete structure, CRC-32, bounds, and one successful full decode.</summary>
    internal static bool Valid(byte[] png) => Valid(png, fullDecodeObserver: null);

    private static bool Valid(byte[] png, Action? fullDecodeObserver) =>
        PreviewPngIntegrity.IsCompleteDecodable(png, VideoScrubPreviewOptions.MaximumThumbnailBytes,
            VideoScrubPreviewOptions.ThumbnailMaximumWidth, VideoScrubPreviewOptions.ThumbnailMaximumHeight, fullDecodeObserver);

    /// <summary>Warm-hit check: complete structure, CRC-32, bounds and codec header; no pixel decode.</summary>
    internal static bool ValidStored(byte[] png) =>
        PreviewPngIntegrity.IsIntactStored(png, VideoScrubPreviewOptions.MaximumThumbnailBytes,
            VideoScrubPreviewOptions.ThumbnailMaximumWidth, VideoScrubPreviewOptions.ThumbnailMaximumHeight);
}

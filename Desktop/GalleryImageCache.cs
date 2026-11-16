using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Desktop;

/// <summary>
/// Decoded gallery images (decoded at display size), kept by the bytes they were made from (hash and length) and bounded by a
/// memory budget in bytes, so stepping back and forth shows an image at once instead of reading, verifying and decoding it again.
/// A viewer takes a lease with <see cref="TryAcquire"/> or <see cref="Add"/> and gives it back with <see cref="Release"/>; an image
/// that was pushed out of the set (or replaced by a larger decode of the same bytes) is disposed when its last lease is given back,
/// never while a viewer still shows it. A bitmap that is shown but no longer kept is not counted against the budget. A bitmap the
/// set does not know is disposed by <see cref="Release"/>, so callers can release every bitmap the same way. No file is read here.
/// </summary>
internal sealed class GalleryImageCache<T> where T : class, IDisposable
{
    private sealed class Slot(string key, T bitmap, GalleryImageInfo info, PixelSize size, Func<bool>? stillValid,
        GalleryImageCache.FileValidity? source)
    {
        /// <summary>Checked at every use: false when the file the image was decoded from has changed since, which drops the image.</summary>
        internal Func<bool>? StillValid { get; } = stillValid;
        /// <summary>The occurrence the image was decoded from, when known; lets a request that just proved this very occurrence skip a second stat.</summary>
        internal GalleryImageCache.FileValidity? Source { get; } = source;
        internal string Key { get; } = key;
        internal T Bitmap { get; } = bitmap;
        internal GalleryImageInfo Info { get; } = info;
        internal PixelSize Size { get; } = size;
        internal long Bytes => GalleryImageCache.BytesOf(Size);
        internal int Leases;
        internal bool Evicted;
        internal LinkedListNode<Slot>? Node;
    }

    private readonly object _gate = new();
    private readonly Func<T, PixelSize> _sizeOf;
    // The decoder verifies a file once; later cache validation compares its regular-file identity
    // and uses this memory instead of hashing an unchanged original on every scroll.
    internal VerifiedFileMemo VerifiedFiles { get; } = new();
    /// <summary>
    /// Reads the regular-file identity of an original (<see cref="RegularProfileFile.TryGetIdentity"/>). It is a blocking file-system
    /// call, so <c>GalleryImageCache.DecodeThrough</c> only calls it on the thread pool; tests observe the calling thread here.
    /// </summary>
    internal Func<string, RegularProfileFileIdentity?> IdentityOf { get; init; } = RegularProfileFile.TryGetIdentity;
    private readonly Dictionary<string, Slot> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<T, Slot> _byBitmap = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Slot> _recent = new(); // front: most recently used
    private long _budget;
    private long _bytes;

    internal GalleryImageCache(Func<T, PixelSize> sizeOf, long budgetBytes = GalleryImageCache.DefaultBudgetBytes)
    {
        _sizeOf = sizeOf;
        _budget = Math.Max(0, budgetBytes);
    }

    /// <summary>The memory the kept images may use in bytes; 0 keeps nothing. Lowering it drops the least recently used images at once.</summary>
    internal long BudgetBytes
    {
        get { lock (_gate) return _budget; }
        set
        {
            List<T> dropped;
            lock (_gate) { _budget = Math.Max(0, value); dropped = Trim(); }
            foreach (T bitmap in dropped) bitmap.Dispose();
        }
    }

    /// <summary>Memory of the kept images in bytes (leased and not; a replaced or pushed-out image that is still shown is not counted).</summary>
    internal long Bytes { get { lock (_gate) return _bytes; } }

    /// <summary>Drops every kept image (an image that is still shown is disposed by its last <see cref="Release"/>); tests and diagnostics.</summary>
    internal void Clear()
    {
        long budget;
        lock (_gate) budget = _budget;
        BudgetBytes = 0;
        BudgetBytes = budget;
    }

    /// <summary>The memory a decoded image of this bitmap uses in bytes.</summary>
    internal long BytesOf(T bitmap) => GalleryImageCache.BytesOf(_sizeOf(bitmap));

    /// <summary>Images in the set.</summary>
    internal int Count { get { lock (_gate) return _byKey.Count; } }

    /// <summary>
    /// The kept image of these bytes if one is kept that covers the target of <paramref name="fit"/>, leased to the caller; false
    /// otherwise. Counts as a use.
    /// </summary>
    internal bool TryAcquire(string key, GalleryDecodeRequest.Fit fit, out T bitmap, out GalleryImageInfo info,
        Func<bool>? currentValid = null)
    {
        return TryLease(FindValid(key, fit, currentValid, null), out bitmap, out info);
    }

    /// <summary>
    /// Like the <c>TryAcquire</c> above for a request whose
    /// own occurrence was confirmed current just now: <paramref name="proven"/> was captured, or re-checked after its proof, immediately
    /// before, so it is not read again. The kept image's own source occurrence is still checked, unless it is that same occurrence
    /// (same path and identity) and therefore covered by the same fresh check.
    /// </summary>
    internal bool TryAcquire(string key, GalleryDecodeRequest.Fit fit, out T bitmap, out GalleryImageInfo info,
        GalleryImageCache.FileValidity proven) =>
        TryLease(FindValid(key, fit, null, proven), out bitmap, out info);

    private bool TryLease(Slot? found, out T bitmap, out GalleryImageInfo info)
    {
        if (found is { } slot)
            lock (_gate)
                if (!slot.Evicted)
                {
                    slot.Leases++;
                    Touch(slot);
                    bitmap = slot.Bitmap; info = slot.Info;
                    return true;
                }
        bitmap = null!; info = null!;
        return false;
    }

    /// <summary>
    /// Marks a kept image that covers the target as recently used without leasing it, and returns its size in bytes; -1 when no
    /// such image is kept. Used by the preloader to keep the images around the shown one at the young end of the set.
    /// </summary>
    internal long Touch(string key, GalleryDecodeRequest.Fit fit, Func<bool>? currentValid = null)
    {
        if (FindValid(key, fit, currentValid, null) is not { } slot) return -1;
        lock (_gate)
        {
            if (slot.Evicted) return -1;
            Touch(slot);
            return slot.Bytes;
        }
    }

    /// <summary>
    /// Like the delegate-based <c>Touch</c> overload for a request whose own occurrence was just
    /// proven. Reuses that proof for the kept slot when it came from the same occurrence, while still rechecking the requested file.
    /// </summary>
    internal long Touch(string key, GalleryDecodeRequest.Fit fit, GalleryImageCache.FileValidity proven)
    {
        if (FindValid(key, fit, proven.IsCurrent, proven) is not { } slot) return -1;
        lock (_gate)
        {
            if (slot.Evicted) return -1;
            Touch(slot);
            return slot.Bytes;
        }
    }

    /// <summary>Whether a fitting candidate exists, without validating either file. Used to avoid hashing on a cold miss.</summary>
    internal bool HasCandidate(string key, GalleryDecodeRequest.Fit fit)
    {
        lock (_gate) return _byKey.TryGetValue(key, out Slot? slot) && Covers(slot, fit);
    }

    /// <summary>
    /// A failed proof of the slot's own source retires it even if the filesystem still reports its old metadata.
    /// A different folder's failed proof must not evict a valid shared bitmap. Existing viewer leases remain alive.
    /// </summary>
    internal void InvalidateSource(string key, string path)
    {
        T? dead = null;
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out Slot? slot) && slot.Source is { } source
                && string.Equals(source.Path, path, StringComparison.Ordinal))
                dead = Evict(slot);
        }
        dead?.Dispose();
    }

    // The kept image of these bytes that covers the target and whose source file is unchanged. The source file of the requested
    // entry is checked separately: a bytes-key may be shared by another folder, but an unverified requested original is never a hit.
    // Both file checks run outside the lock. A failed currentValid check does not evict the slot because it may remain valid for its
    // own source occurrence and can be reused after that occurrence is proven. A freshly proven requested occurrence is not read
    // again, and stands in for the slot's own check when it is the slot's source occurrence.
    private Slot? FindValid(string key, GalleryDecodeRequest.Fit fit, Func<bool>? currentValid, GalleryImageCache.FileValidity? proven)
    {
        Slot? slot;
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out slot) || !Covers(slot, fit)) return null;
        }
        bool valid;
        if (proven != null && slot.Source is { } source && source.IsSameOccurrence(proven)) valid = true;
        else
        {
            try { valid = slot.StillValid?.Invoke() ?? true; }
            catch (Exception) { valid = false; }
        }
        if (!valid)
        {
            T? dead = null;
            lock (_gate)
            {
                if (_byKey.TryGetValue(key, out Slot? current) && ReferenceEquals(current, slot)) dead = Evict(slot);
            }
            dead?.Dispose();
            return null;
        }
        try { valid = currentValid?.Invoke() ?? true; }
        catch (Exception) { valid = false; }
        return valid ? slot : null;
    }

    /// <summary>
    /// Keeps a freshly decoded image and leases it to the caller. When the bytes are kept already at the same or a larger size, that
    /// image is leased instead and <paramref name="bitmap"/> is disposed; a larger decode replaces a smaller kept one. An image
    /// larger than the whole budget is not kept: the caller gets it back unleased-by-the-set and <see cref="Release"/> disposes it.
    /// <paramref name="stillValid"/> (optional) is asked at every later use; when it says false the image is dropped, so a file that
    /// changed after it was verified and decoded is never shown from here. The result is the bitmap to show.
    /// </summary>
    internal T Add(string key, T bitmap, GalleryImageInfo info, Func<bool>? stillValid = null) =>
        AddCore(key, bitmap, info, stillValid, null);

    /// <summary>
    /// The <c>Add</c> above for an image decoded from <paramref name="source"/>: the image is kept
    /// only while that exact occurrence is current, and a later request that proves the same occurrence needs no second stat.
    /// </summary>
    internal T Add(string key, T bitmap, GalleryImageInfo info, GalleryImageCache.FileValidity source) =>
        AddCore(key, bitmap, info, source.IsCurrent, source);

    private T AddCore(string key, T bitmap, GalleryImageInfo info, Func<bool>? stillValid, GalleryImageCache.FileValidity? source)
    {
        PixelSize size = _sizeOf(bitmap);
        List<T> dropped = new();
        // A kept image of a file that changed since must not be shared with this fresh decode.
        Slot? kept;
        lock (_gate) _byKey.TryGetValue(key, out kept);
        if (kept?.StillValid is { } check)
        {
            bool valid;
            try { valid = check(); } catch (Exception) { valid = false; }
            if (!valid) lock (_gate) { if (ReferenceEquals(_byKey.GetValueOrDefault(key), kept) && Evict(kept) is { } stale) dropped.Add(stale); }
        }
        T shown = bitmap;
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out Slot? existing) && existing.Size.Width >= size.Width && existing.Size.Height >= size.Height)
            {
                existing.Leases++;
                Touch(existing);
                shown = existing.Bitmap;
            }
            else if (GalleryImageCache.BytesOf(size) <= _budget)
            {
                if (existing != null && Evict(existing) is { } replaced) dropped.Add(replaced);
                Slot slot = new(key, bitmap, info, size, stillValid, source) { Leases = 1 };
                slot.Node = _recent.AddFirst(slot);
                _byKey[key] = slot;
                _byBitmap[bitmap] = slot;
                _bytes += slot.Bytes;
                dropped.AddRange(Trim());
            }
        }
        if (!ReferenceEquals(shown, bitmap)) bitmap.Dispose();
        foreach (T old in dropped) old.Dispose();
        return shown;
    }

    /// <summary>Gives a lease back. A bitmap this set does not know (or one that was pushed out and has no other lease) is disposed.</summary>
    internal void Release(T bitmap)
    {
        bool dispose;
        lock (_gate)
        {
            if (!_byBitmap.TryGetValue(bitmap, out Slot? slot)) dispose = true;
            else
            {
                if (slot.Leases > 0) slot.Leases--;
                dispose = slot.Evicted && slot.Leases == 0;
                if (dispose) _byBitmap.Remove(bitmap);
            }
        }
        if (dispose) bitmap.Dispose();
    }

    private static bool Covers(Slot slot, GalleryDecodeRequest.Fit fit) =>
        GalleryFitTarget.Covers(slot.Size, GalleryFitTarget.Calculate(slot.Info, fit));

    private void Touch(Slot slot)
    {
        _recent.Remove(slot.Node!);
        slot.Node = _recent.AddFirst(slot);
    }

    // Under the lock. Takes a slot out of the set; the bitmap to dispose now (none while a viewer still leases it).
    private T? Evict(Slot slot)
    {
        _recent.Remove(slot.Node!);
        _byKey.Remove(slot.Key);
        _bytes -= slot.Bytes;
        slot.Evicted = true;
        if (slot.Leases > 0) return null;
        _byBitmap.Remove(slot.Bitmap);
        return slot.Bitmap;
    }

    // Under the lock. Returns the images to dispose now; one that is still shown is disposed by its last Release.
    private List<T> Trim()
    {
        List<T> dropped = new();
        while (_bytes > _budget && _recent.Last is { } oldest)
            if (Evict(oldest.Value) is { } bitmap) dropped.Add(bitmap);
        return dropped;
    }
}

/// <summary>The shared cache of the gallery viewers and its settings (Settings, Storage and limits).</summary>
internal static class GalleryImageCache
{
    /// <summary>Default memory of the kept gallery images: 256 MiB (a setting).</summary>
    internal const long DefaultBudgetBytes = 256L * 1024 * 1024;
    internal const int DefaultPreloadAhead = 3, DefaultPreloadBehind = 2;

    /// <summary>Decoded images of this session, shared by every gallery viewer.</summary>
    internal static GalleryImageCache<Bitmap> Shared { get; } = new(bitmap => bitmap.PixelSize);

    private static int _ahead = DefaultPreloadAhead, _behind = DefaultPreloadBehind;

    /// <summary>How many images after the shown one are decoded in the background (a setting).</summary>
    internal static int PreloadAhead => Volatile.Read(ref _ahead);
    /// <summary>How many images before the shown one are decoded in the background (a setting).</summary>
    internal static int PreloadBehind => Volatile.Read(ref _behind);

    /// <summary>Applies the settings: memory in MiB (0 keeps nothing), images preloaded after and before the shown one.</summary>
    internal static void Configure(int cacheMiB, int preloadAhead, int preloadBehind)
    {
        Volatile.Write(ref _ahead, Math.Max(0, preloadAhead));
        Volatile.Write(ref _behind, Math.Max(0, preloadBehind));
        Shared.BudgetBytes = Math.Max(0, cacheMiB) * 1024L * 1024;
    }

    /// <summary>Bytes of a decoded 32-bit image.</summary>
    internal static long BytesOf(PixelSize size) => (long)size.Width * size.Height * 4;

    /// <summary>The bytes an entry's image is kept by: its hash and length; null when the entry has no hash.</summary>
    internal static string? KeyOf(GalleryEntry entry) =>
        string.IsNullOrEmpty(entry.Asset.Sha256) ? null : entry.Asset.Sha256.ToLowerInvariant() + ":" + entry.Asset.Length;

    /// <summary>
    /// The identity captured before a decode includes object identity and status-change metadata, not only length and write time.
    /// Unix can use it to reject same-length rewrites and rename-over replacements. Windows warm hits also require a full hash,
    /// because its metadata can remain unchanged across rapid rewrites.
    /// Reading the identity is a blocking file-system call (a sleeping disk or a network share can stall it); the gallery cache
    /// path calls it only on the thread pool.
    /// </summary>
    internal sealed class FileValidity
    {
        private readonly Func<string, RegularProfileFileIdentity?> _identityOf;

        internal FileValidity(string path, long length, string sha256, RegularProfileFileIdentity identity,
            Func<string, RegularProfileFileIdentity?>? identityOf = null)
        {
            Path = path;
            Length = length;
            Sha256 = sha256;
            Identity = identity;
            _identityOf = identityOf ?? RegularProfileFile.TryGetIdentity;
        }

        internal string Path { get; }
        internal long Length { get; }
        internal string Sha256 { get; }
        internal RegularProfileFileIdentity Identity { get; }

        /// <summary>Reads the file's identity again: true only while it is still exactly the captured regular file.</summary>
        internal bool IsCurrent()
        {
            try
            {
                RegularProfileFileIdentity? current = _identityOf(Path);
                if (current is not { } value || value.Size != Length) return false;
                return value == Identity;
            }
            catch (Exception) { return false; }
        }

        internal bool IsVerified(VerifiedFileMemo memo) =>
            IsCurrent() && IsRemembered(memo);

        /// <summary>Whether this session fully verified exactly the captured identity; reads no file.</summary>
        internal bool IsRemembered(VerifiedFileMemo memo) => memo.IsVerified(Path, Identity, Sha256);

        /// <summary>The same path with the same regular-file identity; reads no file.</summary>
        internal bool IsSameOccurrence(FileValidity other) =>
            string.Equals(Path, other.Path, StringComparison.Ordinal) && Length == other.Length && Identity == other.Identity;
    }

    internal static FileValidity? Capture(GalleryEntry entry) => Capture(entry, RegularProfileFile.TryGetIdentity);

    internal static FileValidity? Capture(GalleryEntry entry, Func<string, RegularProfileFileIdentity?> identityOf)
    {
        try
        {
            string path = entry.Asset.Path;
            RegularProfileFileIdentity? identity = identityOf(path);
            if (identity is not { } value || value.Size != entry.Asset.Length) return null;
            return new FileValidity(path, entry.Asset.Length, entry.Asset.Sha256, value, identityOf);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// A check that the entry's file is still the regular file a decode starting now reads. The identity rejects missing files,
    /// symbolic links and (on Unix) same-length rewrites with restored times and rename-over replacements. This metadata-only
    /// predicate is not sufficient for Windows warm hits, which also require full verification. Null means the file cannot be inspected and the
    /// image must not be kept.
    /// </summary>
    internal static Func<bool>? Validity(GalleryEntry entry)
    {
        FileValidity? validity = Capture(entry);
        return validity == null ? null : validity.IsCurrent;
    }

    /// <summary>Records the successful decoder's proof only when the exact pre-decode regular file is still present afterwards.</summary>
    internal static void RememberVerified<T>(GalleryImageCache<T> cache, FileValidity? validity) where T : class, IDisposable
    {
        if (validity is { } file && file.IsCurrent())
            cache.VerifiedFiles.Remember(file.Path, file.Identity, file.Sha256);
    }

    /// <summary>
    /// Checks an entry before a warm hit. A failed proof retires a bitmap owned by this source, not one shared from another folder.
    /// <paramref name="validity"/> must have been captured immediately before (<c>Capture</c>):
    /// an unchanged remembered occurrence is matched against that fresh identity without reading the file again. An occurrence not
    /// remembered in this cache is fully verified once through the existing manifest reader and its identity is read once more
    /// afterwards. The stream is deliberately not used for decoding: it is only the proof that permits sharing a bitmap made for
    /// identical bytes. Every step may block on the file system; call it off the UI thread.
    /// </summary>
    internal static async Task<bool> VerifyForCacheAsync<T>(GalleryImageCache<T> cache, GalleryEntry entry, FileValidity? validity,
        CancellationToken cancellationToken) where T : class, IDisposable
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (validity == null) return RejectSource();
        // Windows file change times can remain unchanged for a rapid same-size rewrite. A warm image must therefore
        // take the full manifest hash path on Windows; the identity memo remains a safe fast path on Unix.
        if (!OperatingSystem.IsWindows() && validity.IsRemembered(cache.VerifiedFiles)) return true;
        try
        {
            await using Stream verified = await entry.Manifest.OpenSessionVerifiedReadAsync(
                entry.Asset.AssetId, cache.VerifiedFiles, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return RejectSource(); }
        // OpenSessionVerifiedReadAsync remembers only a stable regular file. Require the captured identity too: a swap while the
        // proof was being opened must cause a miss rather than authorize bytes from a different occurrence.
        bool current = OperatingSystem.IsWindows() ? validity.IsCurrent() : validity.IsVerified(cache.VerifiedFiles);
        return current || RejectSource();

        bool RejectSource()
        {
            // Do not let a later restoration of the bytes/metadata resurrect a source whose proof already failed.
            // In particular, Windows may retain its earlier change time across a same-length rewrite.
            if (KeyOf(entry) is { } key) cache.InvalidateSource(key, entry.Asset.Path);
            return false;
        }
    }

    /// <summary>
    /// Images decoded at display size are shown from and kept in <see cref="Shared"/>; the result is leased (give it back with
    /// <c>Shared.Release</c>). The original size is never kept: it can be huge and is asked for explicitly.
    /// </summary>
    internal static Task<(Bitmap Bitmap, GalleryImageInfo Info)> DecodeAsync(GalleryEntry entry, GalleryDecodeRequest request,
        CancellationToken cancellationToken) => DecodeThrough(Shared, GalleryImageDecoder.DecodeAsync, entry, request, cancellationToken);

    /// <summary>
    /// Shows a display-size request from <paramref name="cache"/> when the requested occurrence is proven, otherwise decodes it and
    /// keeps the result. Callers start here on the UI thread (the viewer's decode queue), so everything that reads an original's
    /// identity, verifies it or re-checks it after the decode runs on the thread pool: an image that is already in memory never waits
    /// on a stalled disk or network share on the UI thread. A request cancelled before the result is handed out gets no bitmap and
    /// holds no lease; an original that changed or disappeared is never answered from the cache.
    /// </summary>
    internal static Task<(T Bitmap, GalleryImageInfo Info)> DecodeThrough<T>(GalleryImageCache<T> cache,
        Func<GalleryEntry, GalleryDecodeRequest, CancellationToken, Task<(T Bitmap, GalleryImageInfo Info)>> decode,
        GalleryEntry entry, GalleryDecodeRequest request, CancellationToken cancellationToken) where T : class, IDisposable
    {
        if (request is not GalleryDecodeRequest.Fit fit || KeyOf(entry) is not { } key) return decode(entry, request, cancellationToken);
        Func<string, RegularProfileFileIdentity?> identityOf = cache.IdentityOf;
        // Task.Run (not Task.Yield, which resumes on the caller's context) moves the first stat off the caller's thread. No token
        // is passed: a cancelled request surfaces as the OperationCanceledException thrown inside, like every other cancellation.
        return Task.Run(() => DecodeOrHitAsync(), CancellationToken.None);

        async Task<(T Bitmap, GalleryImageInfo Info)> DecodeOrHitAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileValidity? validity;
            // A cold key goes directly to the decoder. For a warm bytes-key, prove this particular occurrence before sharing the
            // bitmap; the slot's own validity check independently protects its original occurrence. The capture is fresh when it
            // reaches TryAcquire (directly, or re-read after a full verification), so the requested file is read once per hit.
            if (cache.HasCandidate(key, fit))
            {
                validity = Capture(entry, identityOf);
                if (await VerifyForCacheAsync(cache, entry, validity, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cache.TryAcquire(key, fit, out T? hit, out GalleryImageInfo? info, validity!))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cache.Release(hit); // cancelled while the slot was checked: hand out nothing, keep no lease
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        return (hit, info);
                    }
                }
                // A failed warm attempt still captured before the decoder opens the original; reuse it rather than read again.
            }
            else validity = Capture(entry, identityOf); // capture before the decoder opens the original
            (T bitmap, GalleryImageInfo decodedInfo) = await decode(entry, request, cancellationToken).ConfigureAwait(false);
            // The decoder may complete on a UI thread even though this operation started on the pool. Always hop before the
            // post-decode identity check instead of using SynchronizationContext presence as a proxy for UI-thread ownership.
            return await Task.Run(() => Keep(bitmap, decodedInfo, validity), CancellationToken.None).ConfigureAwait(false);
        }

        (T Bitmap, GalleryImageInfo Info) Keep(T bitmap, GalleryImageInfo decodedInfo, FileValidity? validity)
        {
            try { cancellationToken.ThrowIfCancellationRequested(); }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            // A decode may have raced a replace. Do not memoize or keep a bitmap whose source identity no longer matches.
            if (validity == null) return (bitmap, decodedInfo);
            bool current = validity.IsCurrent();
            // Cancellation during the identity read must not hand out an invalid, unkept bitmap. A valid decode can still
            // be retained for the next request; the post-Add cancellation check releases its lease before throwing.
            if (!current)
            {
                try { cancellationToken.ThrowIfCancellationRequested(); }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
                return (bitmap, decodedInfo);
            }
            // Just confirmed current: the decoder's proof belongs to exactly this occurrence (as RememberVerified, without a second stat).
            cache.VerifiedFiles.Remember(validity.Path, validity.Identity, validity.Sha256);
            T shown = cache.Add(key, bitmap, decodedInfo, validity);
            if (cancellationToken.IsCancellationRequested)
            {
                cache.Release(shown); // stays kept for the next request if the set holds it; disposed otherwise
                cancellationToken.ThrowIfCancellationRequested();
            }
            return (shown, decodedInfo);
        }
    }
}

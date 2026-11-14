using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia;
using MeowChan.Archive;
using SkiaSharp;

namespace MeowChan.Desktop;

/// <summary>
/// The reader reserves each preview at the size the picture will have, before its pixels exist, so a post does not jump when the
/// thumbnail arrives. The capture records no pixel size, so the size comes from (in this order) the session memory of preview sizes
/// (<see cref="ReaderPreviewAspectMemo"/>), a header read of the saved file (<see cref="ReaderPreviewHeaderProbe"/>, off the UI thread)
/// and finally the decoded thumbnail itself; until one is known the old wide-screen box stays. Pure rules only: no control, no file.
/// </summary>
internal static class ReaderPreviewReservation
{
    /// <summary>Posts before and after the visible ones whose previews are decoded ahead ("Preload previews"); 0 turns it off.</summary>
    internal const int PrefetchPostsDefault = 8;
    internal const int PrefetchPostsMinimum = 0;
    internal const int PrefetchPostsMaximum = 50;

    /// <summary>Preview sizes remembered for this session (about 50 bytes each); the oldest are forgotten first.</summary>
    internal const int SizeMemoryDefault = 20_000;
    internal const int SizeMemoryMinimum = 1_000;
    internal const int SizeMemoryMaximum = 200_000;

    /// <summary>
    /// Technical ceiling, deliberately not a setting: the most bytes a header read may touch (512 KiB). PNG, GIF, WebP and BMP state
    /// their size in the first few dozen bytes; a JPEG states it after its EXIF and colour profile segments, which stay well below this.
    /// A header that is not found within it leaves the size unknown (the old reservation), never an error.
    /// </summary>
    internal const int HeaderReadCeilingBytes = 512 * 1024;

    internal static int ClampPrefetchPosts(int value) => Math.Clamp(value, PrefetchPostsMinimum, PrefetchPostsMaximum);
    internal static int ClampSizeMemory(int value) => Math.Clamp(value, SizeMemoryMinimum, SizeMemoryMaximum);

    /// <summary>
    /// The identity of an attachment's bytes for the size memory: the host revision when the capture has one, otherwise the provider's
    /// own identity and checksums. Null for a deleted attachment or one without a media identity (nothing to reserve).
    /// </summary>
    internal static string? AspectKey(ResourceId resource, string postId, CapturedAttachment attachment)
    {
        if (attachment.IsDeleted || attachment.MediaId == null) return null;
        string bytes = attachment.AttachmentRevisionKey
            ?? string.Join('|', attachment.MediaId, attachment.Variant, attachment.ExpectedSha256, attachment.ExpectedMd5, attachment.ExpectedSize);
        return string.Join('\u001f', resource.Source, resource.Board, resource.Thread, postId, attachment.Position, bytes);
    }

    /// <summary>
    /// The aspect the frame keeps once a thumbnail of <paramref name="decodedWidth"/> × <paramref name="decodedHeight"/> pixels arrived.
    /// A reserved aspect that this decode agrees with (within the whole pixel a scaled decode rounds to) stays exactly as it was, so the
    /// measured height does not move by a fraction either; a different picture (another file, a damaged header) takes the decoded aspect.
    /// Null when there is neither.
    /// </summary>
    internal static double? Settle(double? reserved, int decodedWidth, int decodedHeight)
    {
        double? decoded = MediaSlotGeometry.AspectOf(decodedWidth, decodedHeight);
        if (reserved is not { } aspect || aspect <= 0 || !double.IsFinite(aspect)) return decoded;
        if (decoded == null) return aspect;
        // A thumbnail is decoded to a whole width and its height rounded from it: the reserved picture at that width is within a pixel.
        return Math.Abs(decodedWidth / aspect - decodedHeight) <= 1 + 1e-9 ? aspect : decoded;
    }

    /// <summary>A range of post positions on the page: <see cref="Start"/> inclusive, <see cref="End"/> exclusive.</summary>
    internal readonly record struct PostWindow(int Start, int End)
    {
        internal bool Contains(int index) => index >= Start && index < End;
        internal int Count => Math.Max(0, End - Start);
    }

    /// <summary>
    /// The posts whose previews are decoded ahead: <paramref name="posts"/> before the first visible one and after the last, within the
    /// page. Empty when nothing is visible or preloading is off.
    /// </summary>
    internal static PostWindow PrefetchWindow(int firstVisible, int lastVisible, int count, int posts)
    {
        if (count <= 0 || posts <= 0 || firstVisible < 0 || lastVisible < firstVisible) return new PostWindow(0, 0);
        firstVisible = Math.Min(firstVisible, count - 1);
        lastVisible = Math.Min(lastVisible, count - 1);
        return new PostWindow(Math.Max(0, firstVisible - posts), Math.Min(count, lastVisible + 1 + posts));
    }

    /// <summary>
    /// The positions of <paramref name="window"/> outside the visible range, nearest first; on a tie the side the reader scrolls to
    /// (<paramref name="forward"/>: down) comes first. The visible posts load their own previews and are not listed.
    /// </summary>
    internal static IReadOnlyList<int> NearestFirst(int firstVisible, int lastVisible, PostWindow window, bool forward)
    {
        List<int> order = new(window.Count);
        for (int distance = 1; ; distance++)
        {
            int after = lastVisible + distance, before = firstVisible - distance;
            bool afterIn = window.Contains(after), beforeIn = window.Contains(before);
            if (!afterIn && !beforeIn) break;
            if (forward) { if (afterIn) order.Add(after); if (beforeIn) order.Add(before); }
            else { if (beforeIn) order.Add(before); if (afterIn) order.Add(after); }
        }
        return order;
    }

    /// <summary>
    /// Previews that preloading may hold in the shared preview cache at once: half of its configured tile count, so what is ahead never
    /// pushes out what is on screen. The cache's own tile and byte limits stay the memory bound.
    /// </summary>
    internal static int PrefetchBudget(int cacheCapacity) => Math.Max(0, cacheCapacity / 2);

    /// <summary>
    /// Preloading only helps where the decoded preview is kept: the ordinary reader keeps its previews by their bytes; the large reader
    /// does not keep its full-size decodes, so preloading there would decode and drop.
    /// </summary>
    internal static bool Prefetches(bool largePreview, int posts) => !largePreview && posts > 0;
}

/// <summary>
/// Preview sizes (width over height) of this session by attachment identity (<see cref="ReaderPreviewReservation.AspectKey"/>), so a
/// page that is built again, a thread opened again or a recycled row is reserved at its final size at once. Bounded by a count setting;
/// two generations approximate least-recently-used order: a lookup moves an entry to the young generation, and when that is full the
/// old one is forgotten. Keys are kept as 64-bit hashes (a collision only reserves a wrong box, which the decoded picture corrects).
/// Thread-safe: the header reads remember off the UI thread.
/// </summary>
internal sealed class ReaderPreviewAspectMemo
{
    private readonly object _gate = new();
    private Dictionary<ulong, float> _young = new(), _old = new();
    private int _capacity;

    internal ReaderPreviewAspectMemo(int capacity) => _capacity = Math.Max(2, capacity);

    /// <summary>The session memory the reader uses (configured from the setting).</summary>
    internal static ReaderPreviewAspectMemo Shared { get; } = new(ReaderPreviewReservation.SizeMemoryDefault);

    /// <summary>How many sizes are remembered at most; lowering it forgets at once.</summary>
    internal int Capacity
    {
        get { lock (_gate) return _capacity; }
        set
        {
            lock (_gate)
            {
                _capacity = Math.Max(2, value);
                if (_young.Count + _old.Count <= _capacity) return;
                _old = new();
                if (_young.Count > _capacity / 2) { _old = _young; _young = new(); }
                if (_old.Count > _capacity) _old = new();
            }
        }
    }

    internal int Count { get { lock (_gate) return _young.Count + _old.Count; } }

    internal bool TryGet(string key, out double aspect)
    {
        ulong hash = Hash(key);
        lock (_gate)
        {
            if (_young.TryGetValue(hash, out float young)) { aspect = young; return true; }
            if (_old.Remove(hash, out float old)) { Add(hash, old); aspect = old; return true; }
        }
        aspect = 0;
        return false;
    }

    internal void Remember(string key, double aspect)
    {
        if (!(aspect > 0) || !double.IsFinite(aspect) || aspect > float.MaxValue) return;
        ulong hash = Hash(key);
        lock (_gate)
        {
            if (_young.ContainsKey(hash)) { _young[hash] = (float)aspect; return; }
            _old.Remove(hash);
            Add(hash, (float)aspect);
        }
    }

    internal void Clear() { lock (_gate) { _young = new(); _old = new(); } }

    // Caller holds the lock. The young generation takes half the capacity; a full one becomes the old one.
    private void Add(ulong hash, float aspect)
    {
        if (_young.Count >= Math.Max(1, _capacity / 2)) { _old = _young; _young = new(); }
        _young[hash] = aspect;
    }

    /// <summary>FNV-1a over the UTF-16 code units: stable for the session, no allocation.</summary>
    internal static ulong Hash(string key)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in key) { hash ^= c; hash *= 1099511628211UL; }
        return hash;
    }
}

/// <summary>
/// Reads only the header of a saved still image to learn the size it is shown at (EXIF orientation applied, as the decode does), without
/// decoding pixels and without hashing the file: the size only reserves the frame, and the verified decode that follows decides what is
/// drawn (<see cref="ReaderPreviewReservation.Settle"/>). Never on the UI thread. Symbolic links are not followed and a file whose length
/// differs from the manifest is not read; at most <see cref="ReaderPreviewReservation.HeaderReadCeilingBytes"/> are touched.
/// </summary>
internal static class ReaderPreviewHeaderProbe
{
    /// <summary>The still-image types the reader decodes itself (the same list as its thumbnail decode); videos have posters instead.</summary>
    internal static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    /// <summary>Width over height as shown, or null when the file is not a supported image, has changed or its header is not readable.</summary>
    internal static double? TryReadAspect(string path, long expectedLength, CancellationToken token = default)
    {
        if (!Supports(path)) return null;
        try
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.RandomAccess);
            if (file.Length != expectedLength) return null;
            using HeaderWindowStream window = new(file, ReaderPreviewReservation.HeaderReadCeilingBytes);
            using SKManagedStream managed = new(window, disposeManagedStream: false);
            using SKCodec? codec = SKCodec.Create(managed);
            if (codec == null) return null;
            PixelSize shown = ImageOrientation.DisplaySize(codec);
            return MediaSlotGeometry.AspectOf(shown.Width, shown.Height);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; } // IO, access, a damaged header: the size stays unknown, the decode reports real problems.
    }

    /// <summary>A read-only window on the first bytes of a stream: reading past <c>limit</c> ends there, so a header read stays bounded.</summary>
    internal sealed class HeaderWindowStream(Stream inner, long limit) : Stream
    {
        private readonly long _length = Math.Min(inner.Length, Math.Max(0, limit));
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = Math.Clamp(value, 0, _length);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = _length - inner.Position;
            if (left <= 0) return 0;
            return inner.Read(buffer, offset, (int)Math.Min(count, left));
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => inner.Position + offset, _ => _length + offset };
            return inner.Position = Math.Clamp(target, 0, _length);
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

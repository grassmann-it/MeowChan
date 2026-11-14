using System;
using System.IO;
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
/// budget comes from the caller (<see cref="Configure"/>); a lower budget evicts the oldest thumbnails at the next store or
/// <see cref="TrimToBudget"/>. Per-video work, coalescing and cancellation live in <see cref="VideoScrubPreviewSession"/>.
/// </summary>
internal sealed class VideoScrubPreviewCache
{
    private const string TemporaryPrefix = ".poster-scrub-"; // matches VideoPosterCache.Trim's stale ".poster-*.tmp" sweep
    private readonly object _storeLock = new();
    private readonly Func<VideoScrubFrameKey, CancellationToken, Task<byte[]?>>? _readOverride;
    private readonly Func<VideoScrubFrameKey, byte[], CancellationToken, Task>? _storeOverride;
    private VideoScrubPreviewOptions _options;

    /// <param name="decodeGate">Serializes native decode sessions; null shares <see cref="VideoPosterCache.NativeDecodeGate"/> with posters.</param>
    /// <param name="readOverride">Internal deterministic test seam; production callers leave cache reads on the file system.</param>
    /// <param name="storeOverride">Internal deterministic test seam; production callers leave cache writes on the file system.</param>
    internal VideoScrubPreviewCache(string directory, VideoScrubPreviewOptions options, IVideoScrubFrameDecoder? decoder = null,
        SemaphoreSlim? decodeGate = null, Func<VideoScrubFrameKey, CancellationToken, Task<byte[]?>>? readOverride = null,
        Func<VideoScrubFrameKey, byte[], CancellationToken, Task>? storeOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Decoder = decoder ?? NativeVideoScrubFrameDecoder.Instance;
        DecodeGate = decodeGate ?? VideoPosterCache.NativeDecodeGate;
        _readOverride = readOverride;
        _storeOverride = storeOverride;
    }

    internal string Directory { get; }
    internal IVideoScrubFrameDecoder Decoder { get; }
    internal SemaphoreSlim DecodeGate { get; }
    internal VideoScrubPreviewOptions Options => Volatile.Read(ref _options);

    /// <summary>Applies new limits (for example after the setting changed); running sessions use them from their next decode on.</summary>
    internal void Configure(VideoScrubPreviewOptions options) => Volatile.Write(ref _options, options ?? throw new ArgumentNullException(nameof(options)));

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
        string path = PathOf(key);
        try
        {
            if (_readOverride is { } readOverride)
                return await readOverride(key, token).ConfigureAwait(false);
            FileInfo file = new(path);
            if (!file.Exists) return null;
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.Length is > 0 and <= VideoScrubPreviewOptions.MaximumThumbnailBytes)
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                if (Valid(bytes)) return bytes;
            }
            File.Delete(path);
            return null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Evicts the oldest thumbnails until <paramref name="png"/> fits the budget, then publishes it atomically.</summary>
    internal async Task StoreAsync(VideoScrubFrameKey key, byte[] png, CancellationToken token)
    {
        if (!Valid(png)) throw new InvalidDataException("The scrub thumbnail exceeds its safe limits.");
        if (_storeOverride is { } storeOverride)
        {
            await storeOverride(key, png, token).ConfigureAwait(false);
            return;
        }
        System.IO.Directory.CreateDirectory(Directory);
        string temporary = Path.Combine(Directory, TemporaryPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Write before taking the store lock. The temporary file is deliberately not a PNG, so concurrent writers
            // do not make Trim's size accounting optimistic. Trim and the final rename are one critical section.
            await File.WriteAllBytesAsync(temporary, png, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_storeLock)
            {
                token.ThrowIfCancellationRequested();
                VideoPosterCache.Trim(Directory, png.Length, Options.CacheLimitBytes, int.MaxValue);
                File.Move(temporary, PathOf(key), overwrite: true);
            }
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    /// <summary>Evicts the oldest thumbnails until the directory fits the current budget (for example right after lowering it).</summary>
    internal void TrimToBudget()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        lock (_storeLock) VideoPosterCache.Trim(Directory, 0, Options.CacheLimitBytes, int.MaxValue);
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

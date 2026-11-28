using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using MeowChan.Gallery;
using SkiaSharp;

namespace MeowChan.Desktop;

/// <summary>A presentation-owned pixel copy. No codec/canvas/native pointer leaves the decoder.</summary>
internal sealed class GifDecodedFrame : IDisposable
{
    private byte[]? _pixels;
    internal byte[] Pixels => Volatile.Read(ref _pixels) ?? throw new ObjectDisposedException(nameof(GifDecodedFrame));
    internal int Width { get; }
    internal int Height { get; }
    internal int RowBytes { get; }
    internal int Index { get; }
    internal int DurationMs { get; }
    internal int PixelLength => checked(RowBytes * Height);

    internal GifDecodedFrame(byte[] pixels, int width, int height, int rowBytes, int index, int durationMs)
    {
        _pixels = pixels; Width = width; Height = height; RowBytes = rowBytes; Index = index; DurationMs = durationMs;
    }
    public void Dispose()
    {
        byte[]? pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels != null) ArrayPool<byte>.Shared.Return(pixels, clearArray: true);
    }
}

/// <summary>
/// One bounded animation canvas. Verified source is copied once and closed;
/// owned encoded bytes/stream/codec/canvas remain alive until actual noncancelable work drains.
/// </summary>
internal sealed class GifFrameDecoder : IGifFrameDecoder
{
    internal const long MaximumBytes = 64L * 1024 * 1024;
    internal const long MaximumPixels = 16_000_000;
    internal const int MaximumFrames = 1024;
    private static readonly SemaphoreSlim GlobalGifGate = new(1, 1);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _state = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _retirement;
    private bool _closed;
    private int _pending;
    private MemoryStream? _encoded;
    private SKManagedStream? _managed;
    private SKCodec? _codec;
    private SKBitmap? _canvas;
    private SKBitmap? _preview;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly SKImageInfo _output;
    private int _lastFrame = -1;

    internal GalleryImageInfo Info { get; }
    internal int FrameCount => _frames.Length;
    internal int RepetitionCount { get; }
    internal IReadOnlyList<int> DurationsMs { get; }
    internal int DurationMs(int index)
    {
        if ((uint)index >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        return DurationsMs[index];
    }
    // Deterministic unit barrier inside the owned operation, before the real native call.
    // No pointer/codec is provided and production presentation does not use this seam.
    internal Action? BeforeNativeDecode { get; set; }

    private GifFrameDecoder(MemoryStream encoded, SKManagedStream managed, SKCodec codec, SKBitmap canvas, SKBitmap? preview,
        GalleryImageInfo info, SKCodecFrameInfo[] frames, int repetitionCount)
    {
        _encoded = encoded; _managed = managed; _codec = codec; _canvas = canvas; _preview = preview; _frames = frames;
        _output = canvas.Info;
        Info = info;
        RepetitionCount = repetitionCount;
        int[] durations = new int[frames.Length];
        bool gif = codec.EncodedFormat == SKEncodedImageFormat.Gif;
        for (int index = 0; index < frames.Length; index++)
            durations[index] = gif ? NormalizeDuration(frames[index].Duration) : frames[index].Duration;
        DurationsMs = Array.AsReadOnly(durations);
    }

    GalleryImageInfo IGifFrameDecoder.Info => Info;
    int IGifFrameDecoder.FrameCount => FrameCount;
    int IGifFrameDecoder.RepetitionCount => RepetitionCount;
    int IGifFrameDecoder.DurationMs(int index) => DurationMs(index);
    Task<GifDecodedFrame> IGifFrameDecoder.DecodeAsync(int index, CancellationToken token) => DecodeAsync(index, token);

    internal static int NormalizeDuration(int durationMs) => durationMs < 20 ? 100 : durationMs;
    internal static void ValidateLimits(long bytes, int width, int height, int frames)
    {
        if (bytes < 0 || bytes > MaximumBytes) throw new InvalidDataException("Animation exceeds the 64 MiB source limit.");
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
            throw new InvalidDataException("Animation exceeds the 16 million pixel canvas limit.");
        if (frames <= 0 || frames > MaximumFrames) throw new InvalidDataException("Animation exceeds the 1024 frame limit.");
    }

    internal static bool HasGifHeader(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8);

    private static bool HasWebpHeader(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8);

    private static long ValidateWebpLength(ReadOnlySpan<byte> header, long length)
    {
        // Skia can report the complete frames from a partial WebP. Reject a truncated RIFF
        // before a shortened animation or its first frame can be published.
        long declared = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        if (declared > length)
            throw new InvalidDataException("WebP data is incomplete.");
        return declared;
    }

    private static async Task<bool> NeedsWebpFrameCountAsync(Stream source, CancellationToken token)
    {
        const int extendedHeaderLength = 30;
        byte[] header = new byte[(int)Math.Min(source.Length, extendedHeaderLength)];
        await source.ReadExactlyAsync(header.AsMemory(), token).ConfigureAwait(false);
        if (!HasWebpHeader(header)) return false;
        long declared = ValidateWebpLength(header, source.Length);
        ReadOnlySpan<byte> chunk = header.AsSpan(12);
        if (chunk.StartsWith("VP8 "u8) || chunk.StartsWith("VP8L"u8)) return false;
        // Leave unrecognized chunk layouts to the codec.
        if (!chunk.StartsWith("VP8X"u8)) return true;
        if (header.Length < extendedHeaderLength || declared < extendedHeaderLength
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)) != 10)
            throw new InvalidDataException("WebP extended header is incomplete or invalid.");
        const byte animationFlag = 0x02;
        return (header[20] & animationFlag) != 0;
    }

    internal static SKEncodedImageFormat? AnimationFormat(GalleryEntry entry)
    {
        string extension = Path.GetExtension(entry.Asset.OriginalFileName ?? entry.Asset.Path);
        if (string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase)) return SKEncodedImageFormat.Gif;
        if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase)) return SKEncodedImageFormat.Webp;
        return null;
    }

    internal static Task<GifFrameDecoder?> OpenAnimationAsync(GalleryEntry entry, CancellationToken cancellationToken,
        bool allowStillFallback = false) =>
        AnimationFormat(entry) switch
        {
            SKEncodedImageFormat.Webp => Task.Run(() => OpenWebpAnimationAsync(entry, cancellationToken, allowStillFallback), cancellationToken),
            SKEncodedImageFormat.Gif => OpenAsync(token => ReadVerifiedAsync(entry, token), MaximumPixels,
                viewport: null, cancellationToken, SKEncodedImageFormat.Gif, allowStillFallback),
            _ => Task.FromResult<GifFrameDecoder?>(null)
        };

    private static async Task<GifFrameDecoder?> OpenWebpAnimationAsync(GalleryEntry entry, CancellationToken token, bool allowStillFallback)
    {
        ValidateSourceLength(entry.Asset.Length);
        bool gateHeld = false;
        try
        {
            byte[] bytes;
            await using (Stream source = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token).ConfigureAwait(false))
            {
                if (!await NeedsWebpFrameCountAsync(source, token).ConfigureAwait(false)) return null;
                await GlobalGifGate.WaitAsync(token).ConfigureAwait(false);
                gateHeld = true;
                source.Position = 0;
                bytes = await CopyVerifiedAsync(source, entry.Asset.Length, token).ConfigureAwait(false);
            }
            return OpenCore(bytes, MaximumPixels, viewport: null, SKEncodedImageFormat.Webp, token, allowStillFallback);
        }
        finally { if (gateHeld) GlobalGifGate.Release(); }
    }

    internal static async Task<bool> IsAnimatedWebpAsync(GalleryEntry entry, CancellationToken token)
    {
        if (AnimationFormat(entry) != SKEncodedImageFormat.Webp) return false;
        ValidateSourceLength(entry.Asset.Length);
        await using Stream source = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token).ConfigureAwait(false);
        if (!await NeedsWebpFrameCountAsync(source, token).ConfigureAwait(false)) return false;
        await GlobalGifGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            source.Position = 0;
            using SKManagedStream managed = new(source, disposeManagedStream: false);
            using SKCodec? codec = SKCodec.Create(managed);
            bool animated = codec is { EncodedFormat: SKEncodedImageFormat.Webp, FrameCount: > 1 };
            token.ThrowIfCancellationRequested();
            return animated;
        }
        finally { GlobalGifGate.Release(); }
    }

    internal static Task<GifFrameDecoder?> OpenAsync(GalleryEntry entry, CancellationToken cancellationToken, PixelSize? viewport = null) =>
        OpenAsync(token => ReadVerifiedAsync(entry, token), MaximumPixels, viewport, cancellationToken);

    // Booru display variants have already been fetched through the bounded preview/sample port.
    internal static Task<GifFrameDecoder?> OpenPreviewAsync(byte[] bytes, int maximumPixels, PixelSize viewport,
        CancellationToken cancellationToken) =>
        OpenAsync(_ => Task.FromResult(bytes), Math.Min(maximumPixels, MaximumPixels), viewport, cancellationToken);

    private static async Task<byte[]> ReadVerifiedAsync(GalleryEntry entry, CancellationToken token)
    {
        ValidateSourceLength(entry.Asset.Length);
        await using Stream source = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token).ConfigureAwait(false);
        return await CopyVerifiedAsync(source, entry.Asset.Length, token).ConfigureAwait(false);
    }

    private static void ValidateSourceLength(long length)
    {
        if (length < 0 || length > MaximumBytes)
            throw new InvalidDataException("Animation source exceeds the 64 MiB limit.");
    }

    private static async Task<byte[]> CopyVerifiedAsync(Stream source, long length, CancellationToken token)
    {
        byte[] bytes = new byte[checked((int)length)];
        await source.ReadExactlyAsync(bytes.AsMemory(), token).ConfigureAwait(false);
        byte[] extra = new byte[1];
        if (await source.ReadAsync(extra.AsMemory(), token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Verified media length changed while copying the animation source.");
        return bytes;
    }

    private static Task<GifFrameDecoder?> OpenAsync(Func<CancellationToken, Task<byte[]>> read, long maximumPixels,
        PixelSize? viewport, CancellationToken cancellationToken, SKEncodedImageFormat? expectedAnimation = null,
        bool allowStillFallback = false) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GlobalGifGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] bytes = await read(cancellationToken).ConfigureAwait(false);
            return OpenCore(bytes, maximumPixels, viewport, expectedAnimation, cancellationToken, allowStillFallback);
        }
        finally { GlobalGifGate.Release(); }
    }, CancellationToken.None); // The worker owns cleanup once scheduled.

    private static GifFrameDecoder? OpenCore(byte[] bytes, long maximumPixels, PixelSize? viewport,
        SKEncodedImageFormat? expectedAnimation, CancellationToken cancellationToken, bool allowStillFallback)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSourceLength(bytes.LongLength);
        bool webp = HasWebpHeader(bytes);
        if (!webp && !HasGifHeader(bytes)) return null;
        SKEncodedImageFormat format = webp ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Gif;
        if (expectedAnimation is { } expected && expected != format) return null;
        if (webp) ValidateWebpLength(bytes, bytes.LongLength);

        MemoryStream? encoded = null;
        SKManagedStream? managed = null;
        SKCodec? codec = null;
        SKBitmap? canvas = null;
        SKBitmap? preview = null;
        bool releasing = false;
        try
        {
            try
            {
                encoded = new MemoryStream(bytes, writable: false);
                managed = new SKManagedStream(encoded, disposeManagedStream: false);
                codec = SKCodec.Create(managed) ?? throw new InvalidDataException("Unsupported or damaged animation.");
                if (codec.EncodedFormat != format) throw new InvalidDataException("Animation header/codec format mismatch.");
                int count = Math.Max(1, codec.FrameCount); // Skia may report zero for a single-frame image.
                if (count == 1 && webp) return null;
                SKImageInfo source = codec.Info;
                // The gallery can display the first frame of an animation outside the playback budget.
                if (allowStillFallback && count > 1 && source.Width > 0 && source.Height > 0
                    && (count > MaximumFrames || (long)source.Width * source.Height > maximumPixels))
                    return null;
                ValidateLimits(bytes.LongLength, source.Width, source.Height, count);
                if ((long)source.Width * source.Height > maximumPixels)
                    throw new InvalidDataException("Animation canvas exceeds the preview pixel limit.");
                if (count == 1 && expectedAnimation.HasValue) return null;
                SKCodecFrameInfo[] frames = codec.FrameInfo;
                if (frames.Length == 0 && count == 1)
                    frames = [new SKCodecFrameInfo { Duration = 100, RequiredFrame = -1,
                        DisposalMethod = SKCodecAnimationDisposalMethod.Keep, FullyRecieved = true }];
                if (frames.Length != count) throw new InvalidDataException("Animation frame metadata is incomplete.");
                for (int index = 0; index < frames.Length; index++)
                    if (!frames[index].FullyRecieved || frames[index].RequiredFrame < -1 || frames[index].RequiredFrame >= index)
                        throw new InvalidDataException("Animation contains incomplete or invalid frame dependencies.");
                int repetitions = count == 1 ? 0 : codec.RepetitionCount;
                if (repetitions < -1) throw new InvalidDataException("Animation repetition metadata is invalid.");
                cancellationToken.ThrowIfCancellationRequested();
                GalleryImageInfo info = new(source.Width, source.Height, webp ? "WEBP" : "GIF", StillFrame: false);
                canvas = new(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (canvas.GetPixels() == IntPtr.Zero) throw new InvalidDataException("Animation canvas allocation failed.");
                if (viewport is { } box)
                {
                    PixelSize size = GalleryFitTarget.Calculate(info, new(box.Width, box.Height));
                    if (size.Width != canvas.Width || size.Height != canvas.Height)
                    {
                        preview = new(new SKImageInfo(size.Width, size.Height, canvas.ColorType, canvas.AlphaType));
                        if (preview.GetPixels() == IntPtr.Zero) throw new InvalidDataException("Animation preview allocation failed.");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                GifFrameDecoder decoder = new(encoded, managed, codec, canvas, preview, info, frames, repetitions);
                preview = null;
                canvas = null;
                codec = null;
                managed = null;
                encoded = null;
                return decoder;
            }
            finally
            {
                releasing = true;
                preview?.Dispose();
                canvas?.Dispose();
                codec?.Dispose();
                managed?.Dispose();
                encoded?.Dispose();
                releasing = false;
            }
        }
        catch (Exception cleanup) when (releasing)
        {
            // Retain the stream owners if disposal of their codec failed.
            throw new GifRetirementException(new object?[] { preview, canvas, codec, managed, encoded }, cleanup);
        }
    }

    internal Task<GifDecodedFrame> DecodeAsync(int index, CancellationToken cancellationToken) =>
        DecodeOwnedAsync(index, BeginDecode(index, cancellationToken));

    private async Task<GifDecodedFrame> DecodeOwnedAsync(int index, CancellationTokenSource linked)
    {
        try
        {
            using (linked)
            {
                return await Task.Run(async () =>
                {
                    await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        await GlobalGifGate.WaitAsync(linked.Token).ConfigureAwait(false);
                        try
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            return DecodeCore(index, linked.Token);
                        }
                        finally { GlobalGifGate.Release(); }
                    }
                    finally { _operations.Release(); }
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { EndDecode(); }
    }

    private CancellationTokenSource BeginDecode(int index, CancellationToken cancellationToken)
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if ((uint)index >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            _pending++;
            return linked;
        }
    }

    private void EndDecode()
    {
        lock (_state)
        {
            _pending--;
            if (_closed && _pending == 0) _drained.TrySetResult();
        }
    }

    private GifDecodedFrame DecodeCore(int index, CancellationToken token)
    {
        SKBitmap canvas = _canvas!;
        byte[]? pixels = null;
        try
        {
            int required = _frames[index].RequiredFrame;
            // Skia XML: any non-RestorePrevious frame in [RequiredFrame, index) is usable.
            // Otherwise PriorFrame=-1 asks the codec to composite all required dependencies.
            int prior = required >= 0 && _lastFrame >= required && _lastFrame < index
                && _frames[_lastFrame].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious ? _lastFrame : -1;
            if (prior == -1 || index == 0 || required == -1) canvas.Erase(SKColors.Transparent);
            var options = new SKCodecOptions(index, prior) { ZeroInitialized = SKZeroInitialized.Yes };
            BeforeNativeDecode?.Invoke();
            SKCodecResult decoded = _codec!.GetPixels(_output, canvas.GetPixels(), canvas.RowBytes, options);
            if (decoded != SKCodecResult.Success)
                throw new InvalidDataException("Animation frame decode failed: " + decoded + ".");
            _lastFrame = index;
            token.ThrowIfCancellationRequested();
            SKBitmap output = canvas;
            if (_preview is { } preview)
            {
                if (!canvas.ScalePixels(preview, new SKSamplingOptions(SKFilterMode.Linear)))
                    throw new InvalidDataException("Animation preview scaling failed.");
                output = preview;
            }
            int length = checked(output.RowBytes * output.Height);
            pixels = ArrayPool<byte>.Shared.Rent(length);
            Marshal.Copy(output.GetPixels(), pixels, 0, length);
            token.ThrowIfCancellationRequested();
            GifDecodedFrame frame = new(pixels, output.Width, output.Height, output.RowBytes, index, DurationsMs[index]);
            pixels = null;
            return frame;
        }
        catch
        {
            _lastFrame = -1; // Never reuse a failed/canceled/partially overwritten canvas.
            throw;
        }
        finally { if (pixels != null) ArrayPool<byte>.Shared.Return(pixels, clearArray: true); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_state)
        {
            if (_retirement == null)
            {
                _closed = true;
                if (_pending == 0) _drained.TrySetResult();
                _retirement = RetireAsync();
            }
            return new(_retirement);
        }
    }

    private async Task RetireAsync()
    {
        try
        {
            try
            {
                _shutdown.Cancel(); // Wake waiters while the native operation still owns the canvas.
                await _drained.Task.ConfigureAwait(false);
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                await GlobalGifGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    _preview?.Dispose(); _preview = null;
                    _canvas?.Dispose(); _canvas = null;
                    _codec?.Dispose(); _codec = null;
                    _managed?.Dispose(); _managed = null;
                    _encoded?.Dispose(); _encoded = null;
                }
                finally { GlobalGifGate.Release(); }
            }
            finally { _operations.Dispose(); _shutdown.Dispose(); }
        }
        catch (Exception error) { throw new GifRetirementException(this, error); }
    }

}

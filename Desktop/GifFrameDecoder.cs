using System;
using System.Buffers;
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
/// One bounded GIF canvas, not an all-frame cache. Verified source is copied once and closed;
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
    private TaskCompletionSource? _retirement;
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
        SKCodecFrameInfo[] frames, int repetitionCount)
    {
        _encoded = encoded; _managed = managed; _codec = codec; _canvas = canvas; _preview = preview; _frames = frames;
        _output = canvas.Info;
        Info = new(canvas.Width, canvas.Height, "GIF", StillFrame: false);
        RepetitionCount = repetitionCount;
        int[] durations = new int[frames.Length];
        for (int index = 0; index < frames.Length; index++) durations[index] = NormalizeDuration(frames[index].Duration);
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
        if (bytes < 0 || bytes > MaximumBytes) throw new InvalidDataException("GIF exceeds the 64 MiB source limit.");
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
            throw new InvalidDataException("GIF exceeds the 16 million pixel canvas limit.");
        if (frames <= 0 || frames > MaximumFrames) throw new InvalidDataException("GIF exceeds the 1024 frame limit.");
    }

    internal static bool HasGifHeader(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8);

    internal static Task<GifFrameDecoder?> OpenAsync(GalleryEntry entry, CancellationToken cancellationToken, PixelSize? viewport = null) =>
        OpenAsync(token => ReadVerifiedAsync(entry, token), MaximumPixels, viewport, cancellationToken);

    // Booru display variants have already been fetched through the bounded preview/sample port.
    internal static Task<GifFrameDecoder?> OpenPreviewAsync(byte[] bytes, int maximumPixels, PixelSize viewport,
        CancellationToken cancellationToken) =>
        OpenAsync(_ => Task.FromResult(bytes), Math.Min(maximumPixels, MaximumPixels), viewport, cancellationToken);

    private static async Task<byte[]> ReadVerifiedAsync(GalleryEntry entry, CancellationToken token)
    {
        if (entry.Asset.Length < 0 || entry.Asset.Length > MaximumBytes)
            throw new InvalidDataException("GIF source exceeds the 64 MiB limit.");
        await using Stream source = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token).ConfigureAwait(false);
        byte[] bytes = new byte[checked((int)entry.Asset.Length)];
        await source.ReadExactlyAsync(bytes.AsMemory(), token).ConfigureAwait(false);
        byte[] extra = new byte[1];
        if (await source.ReadAsync(extra.AsMemory(), token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Verified media length changed while copying the GIF source.");
        return bytes;
    }

    private static Task<GifFrameDecoder?> OpenAsync(Func<CancellationToken, Task<byte[]>> read, long maximumPixels,
        PixelSize? viewport, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GlobalGifGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] bytes = await read(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.LongLength > MaximumBytes) throw new InvalidDataException("GIF source exceeds the 64 MiB limit.");
            if (!HasGifHeader(bytes)) return null;

            MemoryStream? encoded = null;
            SKManagedStream? managed = null;
            SKCodec? codec = null;
            SKBitmap? canvas = null;
            SKBitmap? preview = null;
            try
            {
                encoded = new MemoryStream(bytes, writable: false);
                managed = new SKManagedStream(encoded, disposeManagedStream: false);
                codec = SKCodec.Create(managed) ?? throw new InvalidDataException("Unsupported or damaged GIF.");
                if (codec.EncodedFormat != SKEncodedImageFormat.Gif) throw new InvalidDataException("GIF header/codec format mismatch.");
                if (maximumPixels < MaximumPixels && (long)codec.Info.Width * codec.Info.Height > maximumPixels)
                    throw new InvalidDataException("GIF canvas exceeds the preview pixel limit.");
                int count = Math.Max(1, codec.FrameCount); // Skia may report zero for a single-frame image.
                ValidateLimits(bytes.LongLength, codec.Info.Width, codec.Info.Height, count);
                SKCodecFrameInfo[] frames = codec.FrameInfo;
                if (frames.Length == 0 && count == 1)
                    frames = new[] { new SKCodecFrameInfo { Duration = 100, RequiredFrame = -1,
                        DisposalMethod = SKCodecAnimationDisposalMethod.Keep, FullyRecieved = true } };
                if (frames.Length != count) throw new InvalidDataException("GIF frame metadata is incomplete.");
                for (int index = 0; index < frames.Length; index++)
                    if (!frames[index].FullyRecieved || frames[index].RequiredFrame < -1 || frames[index].RequiredFrame >= index)
                        throw new InvalidDataException("GIF contains incomplete or invalid frame dependencies.");
                int repetitions = count == 1 ? 0 : codec.RepetitionCount; // A single image never needs repeated decoding.
                if (repetitions < -1) throw new InvalidDataException("GIF repetition metadata is invalid.");
                cancellationToken.ThrowIfCancellationRequested();
                canvas = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (canvas.GetPixels() == IntPtr.Zero) throw new InvalidDataException("GIF canvas allocation failed.");
                if (viewport is { } box)
                {
                    GalleryImageInfo original = new(canvas.Width, canvas.Height, "GIF", StillFrame: false);
                    PixelSize size = GalleryFitTarget.Calculate(original, new(box.Width, box.Height));
                    if (size.Width != canvas.Width || size.Height != canvas.Height)
                    {
                        preview = new(new SKImageInfo(size.Width, size.Height, canvas.ColorType, canvas.AlphaType));
                        if (preview.GetPixels() == IntPtr.Zero) throw new InvalidDataException("GIF preview allocation failed.");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                GifFrameDecoder result = new(encoded, managed, codec, canvas, preview, frames, repetitions);
                encoded = null; managed = null; codec = null; canvas = null; preview = null;
                return result;
            }
            finally
            {
                try { preview?.Dispose(); canvas?.Dispose(); codec?.Dispose(); managed?.Dispose(); encoded?.Dispose(); }
                catch (Exception cleanup)
                {
                    // Retain the entire partially constructed dependency unit. In particular,
                    // a codec whose retirement failed must not lose its managed stream owner.
                    throw new GifRetirementException(new object?[] { preview, canvas, codec, managed, encoded }, cleanup);
                }
            }
        }
        finally { GlobalGifGate.Release(); }
    }, CancellationToken.None); // The worker owns cleanup once scheduled.

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
            if (_closed) throw new ObjectDisposedException(nameof(GifFrameDecoder));
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
                throw new InvalidDataException("GIF frame decode failed: " + decoded + "."); // IncompleteInput is NOT a frame.
            _lastFrame = index;
            token.ThrowIfCancellationRequested();
            SKBitmap output = canvas;
            if (_preview is { } preview)
            {
                if (!canvas.ScalePixels(preview, new SKSamplingOptions(SKFilterMode.Linear)))
                    throw new InvalidDataException("GIF preview scaling failed.");
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
        TaskCompletionSource completion;
        lock (_state)
        {
            if (_retirement != null) return new(_retirement.Task);
            _closed = true;
            completion = _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_pending == 0) _drained.TrySetResult();
        }
        _ = RetireAsync(completion);
        return new(completion.Task);
    }

    private async Task RetireAsync(TaskCompletionSource completion)
    {
        try
        {
            try
            {
                _shutdown.Cancel(); // Wake waiters while the native operation still owns the canvas.
                await _drained.Task.ConfigureAwait(false);
                await Task.Run(DisposeNativeAsync).ConfigureAwait(false);
            }
            finally { _operations.Dispose(); _shutdown.Dispose(); }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(new GifRetirementException(this, error)); }
    }

    private async Task DisposeNativeAsync()
    {
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
}

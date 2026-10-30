using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    private Task? _disposeTask;
    private bool _closed;
    private int _pending;
    private MemoryStream? _encoded;
    private SKManagedStream? _managed;
    private SKCodec? _codec;
    private SKBitmap? _canvas;
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

    private GifFrameDecoder(MemoryStream encoded, SKManagedStream managed, SKCodec codec, SKBitmap canvas,
        SKCodecFrameInfo[] frames, int repetitionCount)
    {
        _encoded = encoded; _managed = managed; _codec = codec; _canvas = canvas; _frames = frames;
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

    internal static Task<GifFrameDecoder?> OpenAsync(GalleryEntry entry, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GlobalGifGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Asset.Length < 0 || entry.Asset.Length > MaximumBytes)
                throw new InvalidDataException("GIF source exceeds the 64 MiB limit.");
            byte[] bytes;
            // Dispose the manifest's original verified handle BEFORE constructing the codec.
            await using (Stream source = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, cancellationToken).ConfigureAwait(false))
            {
                bytes = new byte[checked((int)entry.Asset.Length)];
                await source.ReadExactlyAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                byte[] extra = new byte[1];
                if (await source.ReadAsync(extra.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("Verified media length changed while copying the GIF source.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            bool gif = bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F'
                && bytes[3] == '8' && (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a';
            if (!gif) return null; // Actual bytes, not filename/extension, choose the existing decoder fallback.

            MemoryStream? encoded = null;
            SKManagedStream? managed = null;
            SKCodec? codec = null;
            SKBitmap? canvas = null;
            try
            {
                encoded = new MemoryStream(bytes, writable: false);
                managed = new SKManagedStream(encoded, disposeManagedStream: false);
                codec = SKCodec.Create(managed) ?? throw new InvalidDataException("Unsupported or damaged GIF.");
                if (codec.EncodedFormat != SKEncodedImageFormat.Gif) throw new InvalidDataException("GIF header/codec format mismatch.");
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
                cancellationToken.ThrowIfCancellationRequested();
                GifFrameDecoder result = new(encoded, managed, codec, canvas, frames, repetitions);
                encoded = null; managed = null; codec = null; canvas = null; // Transfer the whole ownership unit.
                return result;
            }
            finally
            {
                try { canvas?.Dispose(); codec?.Dispose(); managed?.Dispose(); encoded?.Dispose(); }
                catch (Exception cleanup)
                {
                    // Retain the entire partially constructed dependency unit. In particular,
                    // a codec whose retirement failed must not lose its managed stream owner.
                    throw new GifRetirementException(new object?[] { canvas, codec, managed, encoded }, cleanup);
                }
            }
        }
        finally { GlobalGifGate.Release(); }
    }); // Never put cancellation on Task.Run itself: the worker owns cleanup once scheduled.

    internal Task<GifDecodedFrame> DecodeAsync(int index, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_state)
        {
            if (_closed) throw new ObjectDisposedException(nameof(GifFrameDecoder));
            if ((uint)index >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            _pending++;
        }
        return Task.Run(async () =>
        {
            bool ownOperation = false, ownGlobal = false;
            try
            {
                await _operations.WaitAsync(linked.Token).ConfigureAwait(false); ownOperation = true;
                await GlobalGifGate.WaitAsync(linked.Token).ConfigureAwait(false); ownGlobal = true;
                linked.Token.ThrowIfCancellationRequested();
                return DecodeCore(index, linked.Token);
            }
            finally
            {
                if (ownGlobal) GlobalGifGate.Release();
                if (ownOperation) _operations.Release();
                linked.Dispose();
                lock (_state)
                {
                    _pending--;
                    if (_closed && _pending == 0) _drained.TrySetResult();
                }
            }
        });
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
            int length = checked(canvas.RowBytes * canvas.Height);
            pixels = ArrayPool<byte>.Shared.Rent(length);
            Marshal.Copy(canvas.GetPixels(), pixels, 0, length);
            token.ThrowIfCancellationRequested();
            GifDecodedFrame frame = new(pixels, canvas.Width, canvas.Height, canvas.RowBytes, index, DurationsMs[index]);
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
            if (_disposeTask != null) return new(_disposeTask);
            _closed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            if (_pending == 0) _drained.TrySetResult();
        }
        _shutdown.Cancel(); // Wake waiters, not the synchronous native operation or its canvas owner.
        _ = Task.Run(async () =>
        {
            try
            {
                await _drained.Task.ConfigureAwait(false);
                await GlobalGifGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    _canvas?.Dispose(); _canvas = null;
                    _codec?.Dispose(); _codec = null;
                    _managed?.Dispose(); _managed = null;
                    _encoded?.Dispose(); _encoded = null;
                }
                finally { GlobalGifGate.Release(); }
                _operations.Dispose(); _shutdown.Dispose();
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(new GifRetirementException(this, error)); }
        });
        return new(completion.Task);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using SkiaSharp;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

/// <summary>One short-lived memory-output decode. No native window, playback surface, audio or network path.</summary>
internal static class VideoPosterDecoder
{
    private static int _nativeReleaseFailed;
    /// <summary>Largest poster frame (the first-frame poster contract; <see cref="VideoPosterCache.Valid"/> checks the same bounds).</summary>
    internal const int PosterMaximumWidth = 960, PosterMaximumHeight = 640;
    internal sealed record Frame(byte[] Pixels, int Width, int Height, int Pitch);

    /// <summary>The first-frame poster (unchanged contract): decoded from the start, at most 960×640.</summary>
    internal static Task<byte[]> DecodeAsync(Stream verifiedVideo, CancellationToken token) =>
        DecodeCoreAsync(verifiedVideo, TimeSpan.Zero, PosterMaximumWidth, PosterMaximumHeight, "video.poster.decode", token);

    /// <summary>
    /// One scrub-preview frame near <paramref name="start"/> (LibVLC <c>:start-time</c>), scaled to fit the given bounds. Like
    /// <see cref="DecodeAsync"/> the decoder owns <paramref name="verifiedVideo"/> from this call on, including failures and cancellation.
    /// A start at or beyond the end fails like a video without a decodable frame. Not exercised natively yet (X06 core, wiring separate).
    /// </summary>
    internal static Task<byte[]> DecodeAtAsync(Stream verifiedVideo, TimeSpan start, int maximumWidth, int maximumHeight, CancellationToken token)
    {
        if (start < TimeSpan.Zero) { verifiedVideo.Dispose(); throw new ArgumentOutOfRangeException(nameof(start)); }
        if (maximumWidth is < 1 or > PosterMaximumWidth)
        { verifiedVideo.Dispose(); throw new ArgumentOutOfRangeException(nameof(maximumWidth), "The frame bounds exceed the poster limits."); }
        if (maximumHeight is < 1 or > PosterMaximumHeight)
        { verifiedVideo.Dispose(); throw new ArgumentOutOfRangeException(nameof(maximumHeight), "The frame bounds exceed the poster limits."); }
        return DecodeCoreAsync(verifiedVideo, start, maximumWidth, maximumHeight, "video.scrub.decode", token);
    }

    private static async Task<byte[]> DecodeCoreAsync(Stream verifiedVideo, TimeSpan start, int maximumWidth, int maximumHeight,
        string phase, CancellationToken token)
    {
        await using var decoder = new DecodeSession(verifiedVideo, start, maximumWidth, maximumHeight);
        using var measurement = PerformanceDiagnostics.Measure(phase, decoder.Id);
        await decoder.InitializeAsync(token).ConfigureAwait(false);
        Frame frame = await decoder.ReadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using SKBitmap bitmap = new(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (int row = 0; row < frame.Height; row++)
            Marshal.Copy(frame.Pixels, row * frame.Pitch, IntPtr.Add(bitmap.GetPixels(), row * bitmap.RowBytes), frame.Width * 4);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 90);
        token.ThrowIfCancellationRequested();
        return png.ToArray();
    }

    // The delegates and native buffers remain strongly rooted until MediaPlayer.Dispose has
    // returned. Callbacks never throw across the ABI; native stop/disposal never runs in a callback.
    private sealed class DecodeSession : IAsyncDisposable
    {
        internal readonly long Id = PerformanceDiagnostics.NextId();
        private readonly Stream _verifiedVideo;
        private readonly TimeSpan _start;
        private readonly int _maximumWidth, _maximumHeight;
        private readonly object _disposeLock = new();
        private Task? _dispose;
        private readonly VideoCleanupWatch _cleanupWatch = new("video.poster.cleanup");
        private LibVLC? _engine;
        private StreamMediaInput? _input;
        private VerifiedVideoFile? _nativeFile;
        private Media? _media;
        private MediaPlayer? _player;
        private GCHandle _root;
        private readonly TaskCompletionSource<Frame> _frame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IntPtr> _allocations = new();
        private readonly MediaPlayer.LibVLCVideoFormatCb _format;
        private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
        private readonly MediaPlayer.LibVLCVideoLockCb _lock;
        private readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
        private IntPtr _pixels;
        private int _width, _height, _codedWidth, _codedHeight, _pitch, _count;
        private Frame? _last;

        internal DecodeSession(Stream stream, TimeSpan start, int maximumWidth, int maximumHeight)
        {
            _verifiedVideo = stream;
            _start = start; _maximumWidth = maximumWidth; _maximumHeight = maximumHeight;
            _format = Format;
            _cleanup = (ref IntPtr opaque) => { };
            _lock = Lock;
            _unlock = (_, _, _) => Capture();
            _display = (_, _) => { };
        }
        internal async Task InitializeAsync(CancellationToken token)
        {
            if (Volatile.Read(ref _nativeReleaseFailed) != 0)
                throw new InvalidOperationException("Video poster decoding is unavailable after a native cleanup failure.");
            NativeVideoRuntime.EnsureInitialized();
            _engine = new LibVLC("--no-audio", "--no-spu", "--no-osd", "--no-video-title-show", "--no-media-library", "--avcodec-hw=none");
            if (VideoNativeInput.UsesFileInput)
            {
                _nativeFile = await VideoNativeInput.MaterializeAsync(_verifiedVideo, null, token).ConfigureAwait(false);
                _media = new Media(_engine, _nativeFile.Path, FromType.FromPath);
            }
            else
            {
                _input = new StreamMediaInput(_verifiedVideo);
                _media = new Media(_engine, _input);
            }
            _media.AddOption(":no-audio");
            // The first-frame poster passes zero and keeps its exact former option set.
            if (_start > TimeSpan.Zero)
                _media.AddOption(":start-time=" + _start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            _player = new MediaPlayer(_engine);
            // A root independent of this async call remains until native release joins callbacks.
            _root = GCHandle.Alloc(this);
            _player.SetVideoFormatCallbacks(_format, _cleanup);
            _player.SetVideoCallbacks(_lock, _unlock, _display);
            _player.EncounteredError += OnError;
            _player.EndReached += OnEnd;
        }
        internal async Task<Frame> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_player!.Play(_media!)) throw new InvalidDataException("The local video decoder could not start.");
            Frame frame = await _frame.Task.WaitAsync(token).ConfigureAwait(false);
            // vmem receives coded dimensions (for example VP8 160×96 for visible 160×90).
            // Crop padding using the played media's visible track dimensions; retain the native pitch.
            MediaTrack track = _media!.Tracks.FirstOrDefault(item => item.TrackType == TrackType.Video);
            uint visibleWidth = track.Data.Video.Width, visibleHeight = track.Data.Video.Height;
            if (visibleWidth > 0 && visibleHeight > 0 && _codedWidth > 0 && _codedHeight > 0)
                frame = frame with { Width = Math.Clamp((int)((long)frame.Width * visibleWidth / _codedWidth), 1, frame.Width),
                    Height = Math.Clamp((int)((long)frame.Height * visibleHeight / _codedHeight), 1, frame.Height) };
            return frame;
        }
        private uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
        {
            try
            {
                if (width == 0 || height == 0 || (long)width * height > 80_000_000 || _allocations.Count >= 4)
                { _frame.TrySetException(new InvalidDataException("The video frame exceeds safe limits.")); return 0; }
                _codedWidth = (int)width; _codedHeight = (int)height;
                double ratio = Math.Min(1, Math.Min((double)_maximumWidth / width, (double)_maximumHeight / height));
                width = (uint)Math.Max(1, (int)(width * ratio));
                height = (uint)Math.Max(1, (int)(height * ratio));
                _width = (int)width; _height = (int)height;
                pitches = (width * 4 + 31) & ~31u;
                lines = (height + 31) & ~31u;
                _pitch = (int)pitches;
                IntPtr allocation = Marshal.AllocHGlobal(checked((int)(pitches * lines) + 31));
                _allocations.Add(allocation);
                _pixels = new IntPtr((allocation.ToInt64() + 31) & ~31L);
                Marshal.Copy(new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
                return 1;
            }
            catch { _frame.TrySetException(new InvalidDataException("The video frame format is not supported.")); return 0; }
        }
        private IntPtr Lock(IntPtr opaque, IntPtr planes)
        {
            try { Marshal.WriteIntPtr(planes, _pixels); }
            catch { _frame.TrySetException(new InvalidDataException("The video buffer could not be locked.")); }
            return IntPtr.Zero;
        }
        private void Capture()
        {
            try
            {
                if (_frame.Task.IsCompleted || _pixels == IntPtr.Zero) return;
                byte[] bytes = new byte[checked(_pitch * _height)];
                Marshal.Copy(_pixels, bytes, 0, bytes.Length);
                Frame frame = new(bytes, _width, _height, _pitch);
                Volatile.Write(ref _last, frame);
                if (++_count >= 3) _frame.TrySetResult(frame);
            }
            catch { _frame.TrySetException(new InvalidDataException("The decoded video frame could not be read.")); }
        }
        private void OnError(object? sender, EventArgs args) => _frame.TrySetException(new InvalidDataException("The local video could not be decoded."));
        private void OnEnd(object? sender, EventArgs args)
        {
            Frame? frame = Volatile.Read(ref _last);
            if (frame != null) _frame.TrySetResult(frame);
            else _frame.TrySetException(new InvalidDataException("The local video has no decodable video frame."));
        }
        public ValueTask DisposeAsync()
        {
            lock (_disposeLock) return new ValueTask(_dispose ??= Task.Run(DisposeCore));
        }
        private void DisposeCore()
        {
            _cleanupWatch.Begin(Id);
            _cleanupWatch.Owns("native operation, player, callback buffers/delegates, media/input, verified stream, native file, engine");
            try
            {
                MediaPlayer? player = _player;
                if (player != null)
                {
                    _cleanupWatch.Phase("native-stop");
                    try { player.Stop(); }
                    catch (Exception) { /* A successful Dispose can still join all callbacks. */ }
                    _cleanupWatch.Phase("player-dispose");
                    player.Dispose(); _player = null;
                }
                _cleanupWatch.Owns("native operation, callback buffers/delegates, media/input, verified stream, native file, engine");
                _cleanupWatch.Phase("media-dispose");
                _media?.Dispose(); _media = null;
                _cleanupWatch.Owns("native operation, callback buffers/delegates, input, native file, verified stream, engine");
                _cleanupWatch.Phase("input-dispose");
                _input?.Dispose(); _input = null;
                _cleanupWatch.Owns("native operation, callback buffers/delegates, native file, verified stream, engine");
                _cleanupWatch.Phase("native-file-dispose");
                _nativeFile?.Dispose(); _nativeFile = null;
                _cleanupWatch.Owns("native operation, callback buffers/delegates, verified stream, engine");
                _cleanupWatch.Phase("engine-dispose");
                _engine?.Dispose(); _engine = null;
                _cleanupWatch.Owns("callback buffers/delegates, verified stream");
                _cleanupWatch.Phase("stream-dispose");
                _verifiedVideo.Dispose();
                // Only actual successful native release permits freeing callback-reachable memory.
                foreach (IntPtr allocation in _allocations) Marshal.FreeHGlobal(allocation);
                _allocations.Clear(); _pixels = IntPtr.Zero;
                if (_root.IsAllocated) _root.Free();
                GC.KeepAlive(_format); GC.KeepAlive(_cleanup); GC.KeepAlive(_lock); GC.KeepAlive(_unlock); GC.KeepAlive(_display);
                _cleanupWatch.Complete();
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _nativeReleaseFailed, 1);
                if (!_root.IsAllocated) _root = GCHandle.Alloc(this);
                _cleanupWatch.Fail();
                throw new InvalidOperationException("The video poster decoder could not release its native session; ownership is retained.", exception);
            }
        }
    }
}

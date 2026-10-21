using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using SkiaSharp;

namespace MeowChan.Desktop;

/// <summary>One short-lived memory-output decode. No native window, playback surface, audio or network path.</summary>
internal static class VideoPosterDecoder
{
    private static int _nativeReleaseFailed;
    internal sealed record Frame(byte[] Pixels, int Width, int Height, int Pitch);
    internal static async Task<byte[]> DecodeAsync(Stream verifiedVideo, CancellationToken token)
    {
        using var decoder = new DecodeSession(verifiedVideo);
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
    private sealed class DecodeSession : IDisposable
    {
        private readonly Stream _verifiedVideo;
        private LibVLC? _engine;
        private StreamMediaInput? _input;
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

        internal DecodeSession(Stream stream)
        {
            _verifiedVideo = stream;
            _format = Format;
            _cleanup = (ref IntPtr opaque) => { };
            _lock = Lock;
            _unlock = (_, _, _) => Capture();
            _display = (_, _) => { };
            try
            {
                if (Volatile.Read(ref _nativeReleaseFailed) != 0)
                    throw new InvalidOperationException("Video poster decoding is unavailable after a native cleanup failure.");
                NativeVideoRuntime.EnsureInitialized();
                _engine = new LibVLC("--no-audio", "--no-spu", "--no-osd", "--no-video-title-show", "--no-media-library", "--avcodec-hw=none");
                _input = new StreamMediaInput(stream);
                _media = new Media(_engine, _input);
                _media.AddOption(":no-audio");
                _player = new MediaPlayer(_engine);
                // A root independent of this async call remains until native release joins callbacks.
                _root = GCHandle.Alloc(this);
                _player.SetVideoFormatCallbacks(_format, _cleanup);
                _player.SetVideoCallbacks(_lock, _unlock, _display);
                _player.EncounteredError += OnError;
                _player.EndReached += OnEnd;
            }
            catch { Dispose(); throw; }
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
                double ratio = Math.Min(1, Math.Min(960d / width, 640d / height));
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
        public void Dispose()
        {
            MediaPlayer? player = _player;
            if (player != null)
            {
                try { player.Stop(); }
                catch (Exception) { /* Always attempt native release even if Stop failed. */ }
                try { player.Dispose(); _player = null; }
                catch (Exception exception)
                {
                    // Do not free memory/delegates/stream still reachable by native callbacks.
                    // Retaining this single failed session is safer than use-after-free; stop admitting
                    // new decoder sessions so an exceptional native failure cannot accumulate engines.
                    Interlocked.Exchange(ref _nativeReleaseFailed, 1);
                    if (!_root.IsAllocated) _root = GCHandle.Alloc(this);
                    throw new InvalidOperationException("The video poster decoder could not release its native session.", exception);
                }
            }
            Exception? cleanupFailure = null;
            void Release(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception exception) { cleanupFailure ??= exception; }
            }
            Release(_media); _media = null;
            Release(_input); _input = null;
            Release(_engine); _engine = null;
            Release(_verifiedVideo);
            // Player release completed before freeing any callback-reachable memory.
            foreach (IntPtr allocation in _allocations) Marshal.FreeHGlobal(allocation);
            _allocations.Clear(); _pixels = IntPtr.Zero;
            if (_root.IsAllocated) _root.Free();
            GC.KeepAlive(_format); GC.KeepAlive(_cleanup); GC.KeepAlive(_lock); GC.KeepAlive(_unlock); GC.KeepAlive(_display);
            if (cleanupFailure != null) throw new InvalidOperationException("Video poster cleanup failed.", cleanupFailure);
        }
    }
}

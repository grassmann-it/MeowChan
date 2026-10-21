using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace MeowChan.Desktop;

/// <summary>
/// macOS VLC 3's window outputs require an accelerated CGL context, which is absent
/// in some virtual/remote desktops. Render its real software video output through
/// Avalonia instead. One queued frame bounds memory and drops frames if the UI is busy.
/// </summary>
internal sealed class MacVideoSurface : IDisposable
{
    private readonly Image _image;
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VideoCleanup(IntPtr opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetCallbacks(IntPtr player, MediaPlayer.LibVLCVideoLockCb locked,
        MediaPlayer.LibVLCVideoUnlockCb unlocked, MediaPlayer.LibVLCVideoDisplayCb display, IntPtr opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFormatCallbacks(IntPtr player, MediaPlayer.LibVLCVideoFormatCb format, VideoCleanup cleanup);
    private static readonly Lazy<(SetCallbacks Video, SetFormatCallbacks Format)> NativeCallbacks = new(() =>
    {
        IntPtr library = NativeVideoRuntime.GetCallbackLibraryHandle();
        return (Marshal.GetDelegateForFunctionPointer<SetCallbacks>(NativeLibrary.GetExport(library, "libvlc_video_set_callbacks")),
            Marshal.GetDelegateForFunctionPointer<SetFormatCallbacks>(NativeLibrary.GetExport(library, "libvlc_video_set_format_callbacks")));
    });
    private readonly VideoCleanup _cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _lock;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    private readonly List<FrameBuffer> _allocations = new();
    private GCHandle _root;
    private int _queued, _disposed, _presented, _nonUniform;
    private WriteableBitmap? _bitmap;

    private sealed record FrameBuffer(IntPtr Allocation, IntPtr Pixels, int Width, int Height, int Pitch)
    {
        internal GCHandle Root;
        internal int Retired;
    }

    internal int PresentedFrames => Volatile.Read(ref _presented);
    internal bool NonUniform => Volatile.Read(ref _nonUniform) != 0;

    internal MacVideoSurface(Image image, MediaPlayer player)
    {
        _image = image;
        _format = Format;
        _cleanup = Cleanup;
        _lock = Lock;
        _unlock = (_, _, _) => { };
        _display = (opaque, _) => Capture(opaque);
        // Keep delegates and unmanaged buffers alive independently until native
        // MediaPlayer.Dispose has joined every callback (see Dispose contract).
        _root = GCHandle.Alloc(this);
        try
        {
            // LibVLCSharp3 wraps format/lock/display with one shared user-data slot,
            // but passes cleanup unwrapped. Register the stable native ABI directly
            // so each vout owns the exact opaque supplied by its format callback.
            var callbacks = NativeCallbacks.Value;
            callbacks.Format(player.NativeReference, _format, _cleanup);
            callbacks.Video(player.NativeReference, _lock, _unlock, _display, IntPtr.Zero);
        }
        catch { _root.Free(); throw; } // Playback has not started; no callbacks can run yet.
    }

    private uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        try
        {
            if (width == 0 || height == 0 || (long)width * height > 80_000_000) return 0;
            double scale = Math.Min(1, Math.Min(3840d / width, 2160d / height));
            width = (uint)Math.Max(1, (int)(width * scale));
            height = (uint)Math.Max(1, (int)(height * scale));
            pitches = (width * 4 + 31) & ~31u;
            lines = (height + 31) & ~31u;
            lock (_allocations)
            {
                if (_allocations.Count >= 4 || Volatile.Read(ref _disposed) != 0) return 0;
                FrameBuffer buffer = Allocate((int)width, (int)height, (int)pitches, checked((int)(pitches * lines) + 31));
                _allocations.Add(buffer);
                // Each native vout keeps its own format/buffer, including during
                // resolution changes. Never share a new buffer with an old vout.
                opaque = GCHandle.ToIntPtr(buffer.Root);
            }
            Marshal.Copy(new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
            return 1;
        }
        catch { return 0; } // No exception crosses the unmanaged ABI.
    }

    private static FrameBuffer Allocate(int width, int height, int pitch, int length)
    {
        IntPtr allocation = IntPtr.Zero;
        FrameBuffer? buffer = null;
        try
        {
            allocation = Marshal.AllocHGlobal(length);
            buffer = new FrameBuffer(allocation, new IntPtr((allocation.ToInt64() + 31) & ~31L), width, height, pitch);
            buffer.Root = GCHandle.Alloc(buffer);
            return buffer;
        }
        catch
        {
            if (buffer != null && buffer.Root.IsAllocated) buffer.Root.Free();
            Marshal.FreeHGlobal(allocation);
            throw;
        }
    }

    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        try { Marshal.WriteIntPtr(planes, Resolve(opaque).Pixels); }
        catch { }
        return IntPtr.Zero;
    }

    private void Cleanup(IntPtr opaque)
    {
        try
        {
            FrameBuffer buffer = (FrameBuffer)GCHandle.FromIntPtr(opaque).Target!;
            lock (_allocations)
            {
                // VLC's vmem Close calls cleanup after this vout's final display.
                // UI work owns a byte copy; it never reads the native allocation.
                Volatile.Write(ref buffer.Retired, 1);
                if (_allocations.Remove(buffer))
                {
                    Marshal.FreeHGlobal(buffer.Allocation);
                    buffer.Root.Free();
                }
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_visibleBuffer, buffer))
                { _image.Source = null; _visibleBuffer = null; }
            });
        }
        catch { } // The callback ABI cannot carry managed exceptions.
    }

    private FrameBuffer? _visibleBuffer;

    private static FrameBuffer Resolve(IntPtr opaque) => (FrameBuffer)GCHandle.FromIntPtr(opaque).Target!;

    private void Capture(IntPtr opaque)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
        byte[]? bytes = null;
        try
        {
            FrameBuffer buffer = Resolve(opaque);
            int width = buffer.Width, height = buffer.Height, pitch = buffer.Pitch;
            int length = checked(pitch * height);
            bytes = ArrayPool<byte>.Shared.Rent(length);
            Marshal.Copy(buffer.Pixels, bytes, 0, length);
            byte[] frame = bytes;
            Dispatcher.UIThread.Post(() => Present(frame, buffer), DispatcherPriority.Render);
        }
        catch
        {
            if (bytes != null) ArrayPool<byte>.Shared.Return(bytes);
            Volatile.Write(ref _queued, 0);
        }
    }

    private void Present(byte[] bytes, FrameBuffer buffer)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref buffer.Retired) != 0) return;
            int width = buffer.Width, height = buffer.Height, pitch = buffer.Pitch;
            if (_bitmap == null || _bitmap.PixelSize != new PixelSize(width, height))
            {
                _image.Source = null;
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using (ILockedFramebuffer frame = _bitmap.Lock())
                for (int row = 0; row < height; row++)
                    Marshal.Copy(bytes, row * pitch, IntPtr.Add(frame.Address, row * frame.RowBytes), width * 4);
            _image.Source = _bitmap;
            _visibleBuffer = buffer;
            _image.InvalidateVisual();
            // Evidence counts only bytes actually copied into the visible Avalonia
            // surface, independently of VLC's native displayed-picture statistics.
            if (_image.IsEffectivelyVisible && _image.Bounds.Width > 0 && _image.Bounds.Height > 0)
            {
                Interlocked.Increment(ref _presented);
                for (int row = 0; row < height && Volatile.Read(ref _nonUniform) == 0; row += Math.Max(1, height / 64))
                    for (int column = 0; column < width; column += Math.Max(1, width / 64))
                    {
                        int offset = row * pitch + column * 4;
                        if (bytes[offset] != bytes[0] || bytes[offset + 1] != bytes[1] || bytes[offset + 2] != bytes[2])
                        { Volatile.Write(ref _nonUniform, 1); break; }
                    }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); Volatile.Write(ref _queued, 0); }
    }

    // Worker only, after the native player was successfully released. If release
    // fails the owner retains this rooted object rather than freeing live callbacks.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (FrameBuffer buffer in _allocations)
        {
            Marshal.FreeHGlobal(buffer.Allocation);
            buffer.Root.Free();
        }
        _allocations.Clear();
        if (_root.IsAllocated) _root.Free();
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_image.Source, _bitmap)) _image.Source = null;
            _bitmap?.Dispose(); _bitmap = null;
        });
        GC.KeepAlive(_format); GC.KeepAlive(_cleanup); GC.KeepAlive(_lock); GC.KeepAlive(_unlock); GC.KeepAlive(_display);
    }
}

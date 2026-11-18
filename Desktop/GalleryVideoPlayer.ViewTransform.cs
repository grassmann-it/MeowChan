using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;

namespace MeowChan.Desktop;

// Zoom, pan and rotation of the video picture (plan V4). The viewer owns the rules (GalleryViewMath); this part owns the surface.
//  - Linux and macOS: the software surface is an Image inside a clipping host. Zoom, pan and rotation are one render transform
//    of that image: the decoded frames are neither re-decoded nor copied again, playback is not touched.
//  - Windows: LibVLC draws into its own window and cannot be transformed. Zoom and pan become a crop rectangle the output
//    scales to the window; a rotation uses LibVLC's "transform" video filter, which needs the input restarted at the same
//    position (through the one latest-seek path, so pause and position are kept).
internal sealed partial class GalleryVideoPlayer
{
    private readonly Panel _softwareHost = new() { Name = "GallerySoftwareVideoHost", MinHeight = 100, ClipToBounds = true, Background = Brushes.Transparent };
    private readonly MatrixTransform _surfaceTransform = new();
    private (int Width, int Height)? _sourceSize;
    // Windows: what the view wants and what the running player has.
    private volatile int _nativeRotation;
    private int _nativeRotationApplied;
    private int _rotationRunning, _cropScheduled, _sizeQueryPending;
    private string? _wantedCrop;
    private string? _appliedCrop;

    /// <summary>The viewer turns the picture (the player menu's "Rotate 90°").</summary>
    internal event Action? RotateRequested;
    /// <summary>The picture's source size became known or changed, or its area was resized.</summary>
    internal event Action? ViewSurfaceChanged;

    /// <summary>The video's own size once known: the decoded frame on the software surface, the player's report on Windows.</summary>
    internal (int Width, int Height)? SourceSize => _sourceSize;
    /// <summary>The area the picture is placed in (the video surface's cell, in dip).</summary>
    internal Size SurfaceSize => SurfaceControl.Bounds.Size;
    internal Control SurfaceControl => UsesSoftwareVideo ? _softwareHost : _video;
    /// <summary>
    /// The picture can be turned now. On Windows the output only turns when the input reopens at the same position, which a playing
    /// video that is not (yet) known as seekable cannot do: the command is unavailable then rather than promised and not shown.
    /// </summary>
    internal bool CanRotate => !IsDisposed && _sourceSize != null && RotateRequested != null && TurnsNow;
    /// <summary>The surface can turn at this moment (always on the software surface; on Windows see <see cref="CanRotate"/>).</summary>
    internal bool TurnsNow => UsesSoftwareVideo
        || VideoNativeRotation.Decide(_intent.Current.Mode, _length, _knownSeekable) != NativeRotationAction.Unavailable;
    /// <summary>Windows: the wanted rotation has not reached the output yet (it waits for a seekable input; nothing is applied meanwhile).</summary>
    internal bool NativeRotationPending => !UsesSoftwareVideo && _nativeRotation != _nativeRotationApplied;

    /// <summary>Whether <paramref name="source"/> is the picture itself (not the transport or another control of the player).</summary>
    internal bool OwnsSurface(object? source) => source is Visual visual
        && (ReferenceEquals(visual, SurfaceControl) || visual.GetVisualAncestors().Contains(SurfaceControl));

    private void InitializeViewTransform()
    {
        _softwareHost.Children.Add(_softwareVideo);
        _softwareVideo.RenderTransform = _surfaceTransform;
        _softwareVideo.PropertyChanged += (_, change) =>
        {
            if (change.Property != Image.SourceProperty || change.NewValue is not Bitmap bitmap) return;
            (int, int) size = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            if (size.Item1 <= 0 || size.Item2 <= 0 || _sourceSize == size) return;
            _sourceSize = size;
            ViewSurfaceChanged?.Invoke();
        };
        SurfaceControl.SizeChanged += (_, _) => ViewSurfaceChanged?.Invoke();
    }

    /// <summary>Windows: the rotation the next medium opens with (a new medium 0°, the same medium shown again its own).</summary>
    internal void PrepareRotation(int degrees) { _nativeRotation = GalleryRotation.Normalize(degrees); _nativeRotationApplied = 0; }

    // A new medium shows upright and unzoomed until the viewer places it.
    private void ResetViewTransform()
    {
        _sourceSize = null;
        _surfaceTransform.Matrix = Matrix.Identity;
        _nativeRotationApplied = 0; // The wanted rotation is set by the viewer (PrepareRotation) before each medium.
        _wantedCrop = _appliedCrop = null;
        Interlocked.Exchange(ref _cropScheduled, 0);
        Interlocked.Exchange(ref _sizeQueryPending, 0);
        ViewSurfaceChanged?.Invoke();
    }

    /// <summary>
    /// Shows the medium at <paramref name="placement"/> in the orientation of <paramref name="geometry"/>. Cheap and
    /// repeatable: the software surface only receives a matrix.
    /// </summary>
    internal void ApplyViewTransform(GalleryViewGeometry geometry, GalleryViewPlacement placement, double displayScale)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed) return;
        if (UsesSoftwareVideo)
        {
            Size cell = _softwareHost.Bounds.Size;
            _surfaceTransform.Matrix = VideoSurfaceTransform.For(geometry.SourceWidth, geometry.SourceHeight, cell.Width, cell.Height,
                displayScale, placement, geometry.Rotation).ToMatrix();
            return;
        }
        ApplyNativeView(geometry, placement, displayScale);
    }

    private void ApplyNativeView(GalleryViewGeometry geometry, GalleryViewPlacement placement, double displayScale)
    {
        _nativeRotation = geometry.Rotation;
        if (_nativeRotation != _nativeRotationApplied) _ = SyncNativeRotationAsync();
        // The native output always fills its window: the crop is relative to that uncapped fit.
        (int width, int height) = geometry.Oriented;
        double fit = GalleryGeometry.Fit(width, height, geometry.ViewportWidth, geometry.ViewportHeight, displayScale);
        SetNativeCrop(VideoNativeCrop.For(width, height, fit, displayScale, placement).Geometry);
    }

    // The latest wanted crop wins; one queued native command applies it (never a failure of the medium).
    private void SetNativeCrop(string geometry)
    {
        _wantedCrop = geometry;
        if ((_appliedCrop ?? string.Empty) == geometry || Interlocked.Exchange(ref _cropScheduled, 1) != 0) return;
        _ = CommandAsync(playback =>
        {
            Interlocked.Exchange(ref _cropScheduled, 0);
            string wanted = _wantedCrop ?? string.Empty;
            if ((_appliedCrop ?? string.Empty) == wanted) return;
            try { playback.Player.CropGeometry = wanted.Length == 0 ? null : wanted; _appliedCrop = wanted; }
            catch (Exception error) { Serilog.Log.Debug(error, "The native video output refused a crop; the picture stays whole and playback goes on."); }
        });
    }

    // LibVLC's transform filter is a media option, read when the input opens: add it, then reopen at the same position.
    private async Task SyncNativeRotationAsync()
    {
        long generation = _generation;
        if (Interlocked.Exchange(ref _rotationRunning, 1) != 0) return;
        try
        {
            while (_nativeRotation != _nativeRotationApplied && IsCurrent(generation) && !IsDisposed)
            {
                int target = _nativeRotation, attached = _nativeRotationApplied;
                NativeRotationAction action = VideoNativeRotation.Decide(_intent.Current.Mode, _length, _knownSeekable);
                // A playing input that cannot reopen at its position keeps its orientation: nothing is added or marked as applied,
                // the wish stays pending and the next view update tries again once the video is seekable.
                if (action == NativeRotationAction.Unavailable) break;
                bool reopen = action == NativeRotationAction.ReopenAtPosition;
                long position = _timeline.DisplayPosition;
                await CommandAsync(playback =>
                {
                    AddRotationOptions(playback.Media, attached, target);
                    if (reopen) playback.Player.Stop();
                }).ConfigureAwait(true);
                _nativeRotationApplied = target;
                if (!reopen || !IsCurrent(generation)) continue;
                if (_timeline.Request(generation, Math.Clamp(position * 1000d / _length, 0, 1000), _length, seekable: true) is { } request)
                    _ = _seeks.Enqueue(request);
            }
        }
        catch (Exception error) { Serilog.Log.Warning(error, "Turning the native video output failed; the player keeps its last orientation."); }
        finally { Interlocked.Exchange(ref _rotationRunning, 0); }
    }

    /// <summary>The media options of a rotation (0° removes the filter again). Later options win over earlier ones.</summary>
    internal static string[] RotationOptions(int degrees) => GalleryRotation.Normalize(degrees) switch
    {
        0 => new[] { ":video-filter=" },
        int turn => new[] { ":video-filter=transform", ":transform-type=" + turn }
    };

    private static void AddRotationOptions(Media media, int attached, int degrees)
    {
        foreach (string option in VideoNativeRotation.OptionsToAdd(attached, degrees)) media.AddOption(option);
    }

    // A medium shown again with its rotation (a video back from the reader) opens already turned.
    private void AddInitialRotation(Media media)
    {
        if (!UsesSoftwareVideo && _nativeRotation != 0) { AddRotationOptions(media, 0, _nativeRotation); _nativeRotationApplied = _nativeRotation; }
    }

    // Windows reports the video size once its output exists: ask on the worker after the first time ticks, until it is known.
    private void RequestNativeSize(long generation, MediaPlayer player)
    {
        if (UsesSoftwareVideo || _sourceSize != null || Interlocked.Exchange(ref _sizeQueryPending, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            (int, int)? size = null;
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_playback?.Player, player) && IsCurrent(generation))
                {
                    uint width = 0, height = 0;
                    if (player.Size(0, ref width, ref height) && width > 0 && height > 0) size = ((int)width, (int)height);
                }
            }
            catch (Exception) { }
            finally { _operations.Release(); }
            Interlocked.Exchange(ref _sizeQueryPending, 0);
            if (size is not { } known) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrent(generation) || IsDisposed || _sourceSize != null) return;
                // The size of an already turned output is the turned one: report the picture's own size.
                _sourceSize = GalleryRotation.IsQuarterTurn(_nativeRotation) ? (known.Item2, known.Item1) : known;
                ViewSurfaceChanged?.Invoke();
            });
        });
    }
}

/// <summary>What a wanted native (Windows) rotation can do now.</summary>
internal enum NativeRotationAction
{
    /// <summary>Add the options and reopen the input at its position (a seekable playing or paused video).</summary>
    ReopenAtPosition,
    /// <summary>No input is running: the options go on the media and take effect when it next opens.</summary>
    ApplyOnNextOpen,
    /// <summary>A running input that cannot seek back to its position: the output would stay upright, so nothing is applied.</summary>
    Unavailable
}

/// <summary>The rule of the native rotation (W3-17), apart from the player so it needs no window or engine.</summary>
internal static class VideoNativeRotation
{
    internal static NativeRotationAction Decide(VideoPlaybackMode mode, long length, bool knownSeekable) =>
        mode == VideoPlaybackMode.Stopped ? NativeRotationAction.ApplyOnNextOpen
        : length > 0 && knownSeekable ? NativeRotationAction.ReopenAtPosition
        : NativeRotationAction.Unavailable;

    /// <summary>
    /// The media options to add for <paramref name="wanted"/> when <paramref name="attached"/> is what the media already carries:
    /// none when they agree, otherwise the one option set of the wanted turn (options are only ever appended to a media, the later
    /// win, so a repeated or no-op request must add nothing).
    /// </summary>
    internal static string[] OptionsToAdd(int attached, int wanted) =>
        GalleryRotation.Normalize(attached) == GalleryRotation.Normalize(wanted) ? Array.Empty<string>() : GalleryVideoPlayer.RotationOptions(wanted);
}

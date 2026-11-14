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
    internal bool CanRotate => !IsDisposed && _sourceSize != null && RotateRequested != null;

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
            catch (Exception) { /* A refused crop leaves the picture whole; playback goes on. */ }
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
                int target = _nativeRotation;
                bool reopen = _intent.Current.Mode != VideoPlaybackMode.Stopped && _length > 0 && _knownSeekable;
                long position = _timeline.DisplayPosition;
                await CommandAsync(playback =>
                {
                    AddRotationOptions(playback.Media, target);
                    if (reopen) playback.Player.Stop();
                }).ConfigureAwait(true);
                _nativeRotationApplied = target;
                if (!reopen || !IsCurrent(generation)) continue;
                if (_timeline.Request(generation, Math.Clamp(position * 1000d / _length, 0, 1000), _length, seekable: true) is { } request)
                    _ = _seeks.Enqueue(request);
            }
        }
        catch (Exception) { /* The player keeps its last orientation. */ }
        finally { Interlocked.Exchange(ref _rotationRunning, 0); }
    }

    /// <summary>The media options of a rotation (0° removes the filter again). Later options win over earlier ones.</summary>
    internal static string[] RotationOptions(int degrees) => GalleryRotation.Normalize(degrees) switch
    {
        0 => new[] { ":video-filter=" },
        int turn => new[] { ":video-filter=transform", ":transform-type=" + turn }
    };

    private static void AddRotationOptions(Media media, int degrees)
    {
        foreach (string option in RotationOptions(degrees)) media.AddOption(option);
    }

    // A medium shown again with its rotation (a video back from the reader) opens already turned.
    private void AddInitialRotation(Media media)
    {
        if (!UsesSoftwareVideo && _nativeRotation != 0) { AddRotationOptions(media, _nativeRotation); _nativeRotationApplied = _nativeRotation; }
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

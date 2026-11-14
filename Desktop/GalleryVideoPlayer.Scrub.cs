using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MeowChan.Desktop;

/// <summary>Seek-bar hover preview owned by <see cref="GalleryVideoPlayer"/>.</summary>
internal sealed partial class GalleryVideoPlayer
{
    private readonly Popup _scrubPopup = new()
    {
        Name = "GalleryVideoScrubPreview",
        Placement = PlacementMode.Top,
        IsLightDismissEnabled = false,
        IsHitTestVisible = false
    };
    private readonly Image _scrubImage = new()
    {
        Name = "GalleryVideoScrubImage",
        Width = 192,
        Height = 108,
        Stretch = Stretch.Uniform,
        IsHitTestVisible = false
    };
    private readonly TextBlock _scrubTime = new()
    {
        Name = "GalleryVideoScrubTime",
        HorizontalAlignment = HorizontalAlignment.Center,
        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted"),
        IsHitTestVisible = false
    };
    private readonly Border _scrubPreviewFrame = new()
    {
        Name = "GalleryVideoScrubPreviewFrame",
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6),
        IsHitTestVisible = false
    };
    private CancellationTokenSource? _scrubHoverCancellation;
    private VideoScrubPreviewCache? _scrubCache;
    private VideoScrubPreviewSession? _scrubSession;
    private GalleryEntry? _scrubEntry;
    private Bitmap? _scrubBitmap;
    private long _scrubSessionGeneration;
    private int _scrubHoverRevision;
    private int _scrubHoverSlot = -1;
    private readonly List<Task> _scrubRetirements = new();

    /// <summary>
    /// Connects the profile-owned scrub cache. Passing <c>null</c> disables previews without changing video playback.
    /// The parent owns the cache and configures its profile budget before calling this method.
    /// </summary>
    internal void ConfigureScrubPreview(VideoScrubPreviewCache? cache)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (ReferenceEquals(_scrubCache, cache)) return;
        _scrubCache = cache;
        VideoScrubPreviewSession? previous = DetachScrubSession();
        if (cache != null && _scrubEntry != null && !IsDisposed)
        {
            _scrubSessionGeneration = _generation;
            _scrubSession = cache.OpenSession(_scrubEntry.Manifest, _scrubEntry.Asset);
        }
        if (previous != null) RetireScrubSession(previous);
    }

    /// <summary>Test seam for the pointer-to-slot calculation; it does not move the seek slider.</summary>
    internal static double ScrubFractionForPointer(double pointerX, double seekWidth)
    {
        if (double.IsNaN(pointerX) || double.IsNaN(seekWidth) || seekWidth <= 0) return 0;
        return Math.Clamp(pointerX / seekWidth, 0, 1);
    }

    /// <summary>Test seam for stale-result admission. A frame may only publish for its exact media/request.</summary>
    internal static bool IsCurrentScrubRequest(long requestGeneration, int requestRevision, int requestSlot,
        long currentGeneration, int currentRevision, int currentSlot, bool durationKnown) =>
        durationKnown && requestGeneration == currentGeneration && requestRevision == currentRevision && requestSlot == currentSlot;

    private void InitializeScrubPreview()
    {
        StackPanel content = new() { Spacing = 3, IsHitTestVisible = false };
        content.Children.Add(_scrubImage);
        content.Children.Add(_scrubTime);
        _scrubPreviewFrame[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Card");
        _scrubPreviewFrame[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.Accent");
        _scrubPreviewFrame.Child = content;
        _scrubPopup.Child = _scrubPreviewFrame;
        _scrubPopup.PlacementTarget = _seek;
        _root.Children.Add(_scrubPopup);

        _seek.AddHandler(PointerEnteredEvent, (_, e) => OnSeekPointerMoved(e), RoutingStrategies.Bubble, handledEventsToo: true);
        _seek.AddHandler(PointerMovedEvent, (_, e) => OnSeekPointerMoved(e), RoutingStrategies.Bubble, handledEventsToo: true);
        _seek.AddHandler(PointerExitedEvent, (_, _) => CancelScrubPreviewRequest(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnSeekPointerMoved(PointerEventArgs args)
    {
        if (IsDisposed || _scrubSession == null || _length <= 0 || _seek.Bounds.Width <= 0
            || !_canSeek || !_seek.IsEffectivelyEnabled)
        {
            CancelScrubPreviewRequest();
            return;
        }

        Point point = args.GetPosition(_seek);
        double fraction = ScrubFractionForPointer(point.X, _seek.Bounds.Width);
        int slot = VideoScrubSlots.FromFraction(fraction);
        if (_scrubHoverSlot == slot)
        {
            PositionScrubPopup(fraction);
            return;
        }

        CancelScrubPreviewRequest();
        _scrubHoverSlot = slot;
        int revision = _scrubHoverRevision;
        long generation = _scrubSessionGeneration;
        VideoScrubPreviewSession session = _scrubSession;
        TimeSpan duration = TimeSpan.FromMilliseconds(Math.Min(_length, TimeSpan.MaxValue.TotalMilliseconds));
        PositionScrubPopup(fraction);
        _scrubPopup.IsOpen = false;

        if (session.TryGetReady(fraction, out VideoScrubFrame? ready) && ready != null)
        {
            ShowScrubFrame(session, generation, revision, slot, ready);
            return;
        }

        CancellationTokenSource cancellation = new();
        _scrubHoverCancellation = cancellation;
        _ = LoadScrubFrameAsync(session, generation, revision, slot, duration, cancellation);
    }

    private void PositionScrubPopup(double fraction)
    {
        double width = Math.Max(0, _seek.Bounds.Width);
        double x = Math.Clamp(fraction, 0, 1) * width;
        _scrubPopup.PlacementRect = new Rect(x, 0, 1, Math.Max(1, _seek.Bounds.Height));
    }

    private async Task LoadScrubFrameAsync(VideoScrubPreviewSession session, long generation, int revision, int slot,
        TimeSpan duration, CancellationTokenSource cancellation)
    {
        try
        {
            VideoScrubFrame? frame = await session.GetSlotAsync(slot, duration, cancellation.Token).ConfigureAwait(false);
            if (frame == null || cancellation.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() => ShowScrubFrame(session, generation, revision, slot, frame));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Pointer movement/media retirement is an expected cancellation, not a playback failure.
        }
        catch (ObjectDisposedException)
        {
            // The owning session was retired while the optional preview was loading.
        }
        catch (Exception) when (!IsDisposed)
        {
            // A preview failure must never affect the seek control or native playback.
        }
        finally
        {
            if (Dispatcher.UIThread.CheckAccess()) FinishScrubRequest(cancellation);
            else Dispatcher.UIThread.Post(() => FinishScrubRequest(cancellation));
        }
    }

    private void FinishScrubRequest(CancellationTokenSource cancellation)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (ReferenceEquals(_scrubHoverCancellation, cancellation)) _scrubHoverCancellation = null;
        cancellation.Dispose();
    }

    private void ShowScrubFrame(VideoScrubPreviewSession session, long generation, int revision, int slot, VideoScrubFrame frame)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!IsCurrentScrubRequest(generation, revision, slot, _scrubSessionGeneration, _scrubHoverRevision,
                _scrubHoverSlot, _length > 0) || !IsCurrent(generation) || !ReferenceEquals(session, _scrubSession) || IsDisposed)
            return;

        Bitmap bitmap;
        try
        {
            using MemoryStream png = new(frame.Png, writable: false);
            bitmap = new Bitmap(png);
        }
        catch (Exception) when (!IsDisposed)
        {
            return;
        }

        Bitmap? previous = _scrubBitmap;
        _scrubBitmap = bitmap;
        _scrubImage.Source = bitmap;
        _scrubTime.Text = FormatVideoTime((long)Math.Clamp(frame.Fraction * _length, 0, _length));
        previous?.Dispose();
        _scrubPopup.IsOpen = true;
    }

    private void CancelScrubPreviewRequest()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(CancelScrubPreviewRequest);
            return;
        }

        ++_scrubHoverRevision;
        _scrubHoverSlot = -1;
        _scrubHoverCancellation?.Cancel();
        _scrubHoverCancellation = null;
        _scrubPopup.IsOpen = false;
    }

    private VideoScrubPreviewSession? DetachScrubSession()
    {
        CancelScrubPreviewRequest();
        VideoScrubPreviewSession? previous = _scrubSession;
        _scrubSession = null;
        ++_scrubSessionGeneration;
        ClearScrubBitmap();
        return previous;
    }

    private void ClearScrubBitmap()
    {
        _scrubPopup.IsOpen = false;
        _scrubImage.Source = null;
        Bitmap? bitmap = _scrubBitmap;
        _scrubBitmap = null;
        bitmap?.Dispose();
    }

    private static async Task DisposeScrubSessionAsync(VideoScrubPreviewSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) when (!Environment.HasShutdownStarted) { }
    }

    private void RetireScrubSession(VideoScrubPreviewSession session)
    {
        _scrubRetirements.Add(DisposeScrubSessionAsync(session));
    }

    private async Task DisposeScrubPreviewAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(DisposeScrubPreviewAsync).ConfigureAwait(false);
            return;
        }

        _scrubEntry = null;
        VideoScrubPreviewSession? previous = DetachScrubSession();
        if (previous != null) RetireScrubSession(previous);
        if (_scrubRetirements.Count == 0) return;
        Task[] retirements = _scrubRetirements.ToArray();
        _scrubRetirements.Clear();
        await Task.WhenAll(retirements).ConfigureAwait(false);
    }

    private void BeginScrubSession(GalleryEntry entry, long generation)
    {
        Dispatcher.UIThread.VerifyAccess();
        VideoScrubPreviewSession? previous = DetachScrubSession();
        _scrubEntry = entry;
        _scrubSessionGeneration = generation;
        _scrubSession = _scrubCache?.OpenSession(entry.Manifest, entry.Asset);
        if (previous != null) RetireScrubSession(previous);
    }
}

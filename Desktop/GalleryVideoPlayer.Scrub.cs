using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Gallery;

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
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted"),
        IsHitTestVisible = false
    };
    private readonly Border _scrubPreviewFrame = new()
    {
        Name = "GalleryVideoScrubPreviewFrame",
        BorderThickness = new Thickness(1),
        IsHitTestVisible = false
    };
    // Hide-trigger state machine (pure, unit tested) plus the window facts that feed it. The popup is a separate
    // top-level window; it must never outlive minimise, deactivation, hide-to-tray or the timeline's visual tree.
    private readonly VideoScrubPopupGate _scrubGate = new();
    private readonly Dictionary<Window, bool> _scrubWindows = new(); // window -> must be visible for the popup to exist
    private readonly DispatcherTimer _scrubWatchdog = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _scrubHoverCancellation;
    private VideoScrubPreviewCache? _scrubCache;
    private VideoScrubPreviewSession? _scrubSession;
    private GalleryEntry? _scrubEntry;
    private Bitmap? _scrubBitmap;
    private long _scrubSessionGeneration;
    private int _scrubHoverRevision;
    private int _scrubHoverSlot = -1;
    // Completed retirement tasks are removed on every media change; unfinished tasks stay rooted until disposal
    // because they still own the session's verified/native resources.
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
        StackPanel content = new() { IsHitTestVisible = false };
        content[!StackPanel.SpacingProperty] = new DynamicResourceExtension("Workspace.Space.8");
        content.Children.Add(_scrubImage);
        content.Children.Add(_scrubTime);
        _scrubPreviewFrame[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Card");
        _scrubPreviewFrame[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.Accent");
        _scrubPreviewFrame[!Border.PaddingProperty] = new DynamicResourceExtension("Workspace.Inset.Chip");
        _scrubPreviewFrame[!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
        _scrubPreviewFrame.Child = content;
        _scrubPopup.Child = _scrubPreviewFrame;
        _scrubPopup.PlacementTarget = _seek;
        _root.Children.Add(_scrubPopup);

        _seek.AddHandler(PointerEnteredEvent, (_, e) => OnSeekPointerMoved(e, entered: true), RoutingStrategies.Bubble, handledEventsToo: true);
        _seek.AddHandler(PointerMovedEvent, (_, e) => OnSeekPointerMoved(e), RoutingStrategies.Bubble, handledEventsToo: true);
        _seek.AddHandler(PointerExitedEvent, (_, _) =>
        {
            _scrubGate.OnPointerExited();
            CancelScrubPreviewRequest();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _seek.AttachedToVisualTree += (_, _) =>
        {
            _scrubPopup.PlacementTarget = _seek;
            RefreshScrubWindowWatch();
        };
        // The popup must not keep a reference to, or stay open for, a timeline that left the tree.
        _seek.DetachedFromVisualTree += (_, _) => OnScrubTimelineDetached();
        _scrubWatchdog.Tick += (_, _) => EnforceScrubGate();
    }

    private void OnScrubTimelineDetached()
    {
        ReleaseScrubWindowWatch();
        _scrubGate.ApplyEnvironment(VideoScrubEnvironment.Unknown);
        CancelScrubPreviewRequest();
        _scrubPopup.PlacementTarget = null;
    }

    /// <summary>Recomputes the window facts for the gate. Returns true when the popup may show.</summary>
    private bool ApplyScrubEnvironment()
    {
        bool attached = TopLevel.GetTopLevel(_seek) != null;
        bool active = _scrubWindows.Count == 0, minimized = false, visible = true;
        foreach ((Window window, bool mustBeVisible) in _scrubWindows)
        {
            active |= window.IsActive;
            minimized |= window.WindowState == WindowState.Minimized;
            if (mustBeVisible) visible &= window.IsVisible;
        }
        return _scrubGate.ApplyEnvironment(new VideoScrubEnvironment(attached, active, minimized, visible));
    }

    /// <summary>Hides the popup unless the gate (windows, pointer, latches) still allows it.</summary>
    private void EnforceScrubGate()
    {
        bool mayShow = ApplyScrubEnvironment();
        // A mouse pointer that left the bar without a PointerExited (window left behind it) also hides the popup.
        // An active pointer gesture (touch drag, capture) keeps owning the bar.
        if (mayShow && !_seek.IsPointerOver && _seekPointer == null)
        {
            _scrubGate.OnPointerExited();
            mayShow = false;
        }
        if (!mayShow) CancelScrubPreviewRequest();
    }

    private void RefreshScrubWindowWatch()
    {
        Dispatcher.UIThread.VerifyAccess();
        Dictionary<Window, bool> next = new();
        void Add(Window? window, bool mustBeVisible)
        {
            // The owner chain (the main window behind a native video overlay or transport window) counts too.
            for (int depth = 0; window != null && depth < 8; depth++, window = window.Owner as Window)
                next[window] = next.TryGetValue(window, out bool known) ? known | mustBeVisible : mustBeVisible;
        }
        Window? seekWindow = TopLevel.GetTopLevel(_seek) as Window;
        Add(seekWindow, true);
        Add(TopLevel.GetTopLevel(this) as Window, false);
        Add((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow, true);

        foreach (Window window in new List<Window>(_scrubWindows.Keys))
        {
            if (next.ContainsKey(window)) continue;
            window.PropertyChanged -= OnScrubWindowPropertyChanged;
            window.RemoveHandler(KeyDownEvent, OnScrubWindowKeyDown);
        }
        foreach (Window window in next.Keys)
        {
            if (_scrubWindows.ContainsKey(window)) continue;
            window.PropertyChanged += OnScrubWindowPropertyChanged;
            window.AddHandler(KeyDownEvent, OnScrubWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        _scrubWindows.Clear();
        foreach ((Window window, bool mustBeVisible) in next) _scrubWindows[window] = mustBeVisible;
        EnforceScrubGate();
    }

    private void ReleaseScrubWindowWatch()
    {
        foreach (Window window in _scrubWindows.Keys)
        {
            window.PropertyChanged -= OnScrubWindowPropertyChanged;
            window.RemoveHandler(KeyDownEvent, OnScrubWindowKeyDown);
        }
        _scrubWindows.Clear();
    }

    private void OnScrubWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.IsActiveProperty || e.Property == Window.WindowStateProperty
            || e.Property == Visual.IsVisibleProperty) EnforceScrubGate();
    }

    // Escape never stops here (handledEventsToo, tunnel): it only dismisses the hover preview.
    private void OnScrubWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        _scrubGate.OnEscape();
        CancelScrubPreviewRequest();
    }

    /// <summary>Hides the preview now (gallery chrome events such as a medium change call this).</summary>
    internal void DismissScrubPreview() => CancelScrubPreviewRequest();

    private void OnSeekPointerMoved(PointerEventArgs args, bool entered = false)
    {
        bool environmentOk = ApplyScrubEnvironment();
        bool mayShow = entered ? _scrubGate.OnPointerEntered() : _scrubGate.OnPointerMoved();
        if (IsDisposed || _scrubSession == null || _length <= 0 || _seek.Bounds.Width <= 0
            || !_canSeek || !_seek.IsEffectivelyEnabled || !environmentOk || !mayShow)
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
        catch (OperationCanceledException)
        {
            // Pointer movement/media retirement is an expected cancellation, not a playback failure.
        }
        catch (ObjectDisposedException)
        {
            // The owning session was retired while the optional preview was loading.
        }
        catch (InvalidOperationException) when (IsDisposed || Environment.HasShutdownStarted)
        {
            // Dispatcher/session teardown is an expected optional-preview failure.
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
        // A frame that finishes loading after the window was minimised/deactivated/hidden must not open a popup.
        if (!ApplyScrubEnvironment() || !_scrubGate.MayShow) { CancelScrubPreviewRequest(); return; }

        Bitmap bitmap;
        try
        {
            using MemoryStream png = new(frame.Png, writable: false);
            bitmap = new Bitmap(png);
        }
        catch (ArgumentException) when (!IsDisposed)
        {
            // A malformed optional PNG must not affect the seek control or native playback.
            return;
        }
        catch (InvalidOperationException)
        {
            // Avalonia may reject a late/retired bitmap as an optional preview error.
            return;
        }
        catch (NotSupportedException)
        {
            // A codec-independent bitmap failure is also optional; playback remains untouched.
            return;
        }

        Bitmap? previous = _scrubBitmap;
        _scrubBitmap = bitmap;
        _scrubImage.Source = bitmap;
        _scrubTime.Text = FormatVideoTime((long)Math.Clamp(frame.Fraction * _length, 0, _length));
        previous?.Dispose();
        _scrubPopup.IsOpen = true;
        _scrubWatchdog.Start();
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
        _scrubWatchdog.Stop();
    }

    private VideoScrubPreviewSession? DetachScrubSession()
    {
        _scrubGate.OnMediaChanged();
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

    private static Task DisposeScrubSessionAsync(VideoScrubPreviewSession session) => session.DisposeAsync().AsTask();

    private void ReapCompletedScrubRetirements()
    {
        for (int index = _scrubRetirements.Count - 1; index >= 0; index--)
        {
            if (!_scrubRetirements[index].IsCompleted) continue;
            _ = _scrubRetirements[index].Exception;
            _scrubRetirements.RemoveAt(index);
        }
    }

    private void RetireScrubSession(VideoScrubPreviewSession session)
    {
        ReapCompletedScrubRetirements();
        _scrubRetirements.Add(DisposeScrubSessionAsync(session));
        // Pending work is never dropped: it still owns the retired session. Completed work was reaped above.
    }

    private async Task DisposeScrubPreviewAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // Avalonia's Func<Task> overload unwraps the callback and completes only after
            // every retired preview session has finished disposing.
            await Dispatcher.UIThread.InvokeAsync((Func<Task>)DisposeScrubPreviewAsync).ConfigureAwait(false);
            return;
        }

        _scrubEntry = null;
        _scrubGate.Retire();
        ReleaseScrubWindowWatch();
        VideoScrubPreviewSession? previous = DetachScrubSession();
        if (previous != null) RetireScrubSession(previous);
        _scrubPopup.PlacementTarget = null;
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

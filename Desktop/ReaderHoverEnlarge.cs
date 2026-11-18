using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MeowChan.Desktop;

/// <summary>
/// The reader's optional hover preview: while the pointer rests on an attachment for the configured delay, the picture is shown larger
/// in a popup beside it. One popup for the whole reader; it takes no input, closes when the pointer leaves, a button is pressed or the
/// list scrolls, and holds one decoded bitmap at a time (released on close). The decode goes through the same gated, cancellable path
/// as every other reader preview (<see cref="ArchiveAttachmentRow.LoadSized"/>). Off unless <see cref="ReaderMediaPreferences.HoverEnlarge"/>.
/// </summary>
internal sealed class ReaderHoverEnlarge
{
    private readonly Popup _popup;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly DispatcherTimer _timer = new();
    private ArchiveAttachmentView? _target;
    private ArchiveAttachmentRow? _row;
    private Control? _anchor;
    private CancellationTokenSource? _loading;
    private Bitmap? _bitmap;
    private int _request;
    private TransientHover? _hoverLease;
    private int _size = ReaderDisplayPreferences.HoverEnlargeSizeDefault;

    internal bool Enabled { get; private set; }
    internal bool IsOpen => _popup.IsOpen;
    /// <summary>The attachment whose preview is waiting, loading or shown (smoke seam).</summary>
    internal ArchiveAttachmentView? Target => _target;

    internal ReaderHoverEnlarge(Panel host)
    {
        Border frame = new() { Padding = new Thickness(4), IsHitTestVisible = false, BorderThickness = new Thickness(1), Child = _image };
        frame[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Card");
        frame[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.CardEdge");
        frame[!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
        _popup = new Popup
        {
            Child = frame, IsLightDismissEnabled = false, Placement = PlacementMode.Right,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.SlideY,
            IsHitTestVisible = false
        };
        host.Children.Add(_popup);
        _timer.Tick += (_, _) => { _timer.Stop(); _ = ShowAsync(); };
    }

    internal void Configure(ReaderMediaPreferences media)
    {
        media = media.Clamped();
        Enabled = media.HoverEnlarge;
        _size = media.HoverSize;
        _timer.Interval = TimeSpan.FromMilliseconds(media.HoverDelayMs);
        if (!Enabled) Close();
    }

    internal void Enter(ArchiveAttachmentView view, ArchiveAttachmentRow row, Control anchor)
    {
        if (!Enabled || row.LoadSized == null) return;
        Close();
        _target = view; _row = row; _anchor = anchor;
        _hoverLease = new TransientHover(anchor, hovered => { if (!hovered) Close(); });
        _hoverLease.Reconcile(moved: true);
        _timer.Start();
    }

    internal void Leave(ArchiveAttachmentView view)
    {
        if (ReferenceEquals(_target, view)) Close();
    }

    internal void Close()
    {
        _timer.Stop();
        TransientHover? lease = _hoverLease; _hoverLease = null; lease?.Dispose();
        ++_request;
        CancellationTokenSource? loading = _loading;
        _loading = null;
        try { loading?.Cancel(); } catch (ObjectDisposedException) { }
        _target = null; _row = null; _anchor = null;
        if (_popup.IsOpen) _popup.IsOpen = false;
        _image.Source = null;
        if (_bitmap != null) MediaTileThumbnails.Release(_bitmap);
        _bitmap = null;
    }

    private async Task ShowAsync()
    {
        if (!Enabled || _row is not { LoadSized: { } load } || _anchor == null) return;
        if (_hoverLease?.IsCurrent != true) { Close(); return; }
        int request = ++_request;
        ArchiveAttachmentView? target = _target;
        Control anchor = _anchor;
        // The preview never outgrows the window: the longest side is the setting, at most 90 % of the shorter window side.
        double window = TopLevel.GetTopLevel(anchor) is { } top ? Math.Min(top.Bounds.Width, top.Bounds.Height) * 0.9 : _size;
        int side = (int)Math.Max(ReaderDisplayPreferences.HoverEnlargeSizeMinimum / 2, Math.Min(_size, window));
        CancellationTokenSource loading = new();
        _loading = loading;
        Task<Bitmap?>? pending = null;
        try
        {
            pending = load(side, side, loading.Token);
            Bitmap? bitmap = await pending.WaitAsync(TimeSpan.FromSeconds(15), loading.Token);
            if (request != _request || loading.IsCancellationRequested || !ReferenceEquals(_target, target) || _hoverLease?.IsCurrent != true)
            { if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }
            if (bitmap == null) return;
            _bitmap = bitmap;
            _image.Source = bitmap;
            _image.MaxWidth = side; _image.MaxHeight = side;
            _popup.PlacementTarget = anchor;
            _popup.IsOpen = true;
        }
        catch (Exception)
        {
            // A preview that cannot be made is simply not shown.
            loading.Cancel();
            if (pending != null)
            {
                try { if (await pending is { } late) MediaTileThumbnails.Release(late); } catch (Exception) { }
            }
        }
        finally
        {
            if (ReferenceEquals(_loading, loading)) _loading = null;
            loading.Dispose();
        }
    }
}

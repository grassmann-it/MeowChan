using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Archive;

namespace MeowChan.Desktop;

internal sealed record GalleryViewerState(GalleryAssetKey? Key, string? PostId, int? AttachmentPosition,
    bool ManualZoom, double PhysicalZoom, double PanX, double PanY, bool ContextOpen);

internal sealed class GalleryViewer : UserControl
{
    private readonly Image _image = new() { Name = "GalleryImage", Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _viewport = new() { Name = "GalleryViewport", ClipToBounds = true, Background = Brushes.Black };
    private readonly TextBlock _status = new() { Name = "GalleryStatus", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, IsHitTestVisible = false };
    private readonly TextBlock _scope = new() { Name = "GalleryScope", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _warnings = new() { Name = "GalleryWarnings", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly Button _readPosts;
    private readonly TextBlock _position = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _quality = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _context = new() { Spacing = 12 };
    private readonly Border _contextPanel = new() { Name = "GalleryContext", Padding = new Thickness(18), IsVisible = false, Background = new SolidColorBrush(Color.FromRgb(32, 35, 42)) };
    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _close;
    private readonly Button _actual;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _detailLoad;
    private Bitmap? _fitBitmap;
    private Bitmap? _highBitmap;
    private GalleryImageInfo? _info;
    private GalleryEntry? _entry;
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private bool _manualZoom;
    private bool _contextOpen;
    private bool _dragging;
    private Point _dragPoint;
    private int _request;
    private GalleryViewerState? _restore;
    private IReadOnlyList<GalleryEntry> _associations = Array.Empty<GalleryEntry>();

    internal event Action? CloseRequested;
    internal event Action? ReadPostsRequested;
    internal event Action<int>? MoveRequested;
    internal event Action<GalleryEntry>? ReadPostRequested;
    internal event Action<ArchiveQuote>? QuoteRequested;
    internal event Action<GalleryEntry>? AssociationRequested;

    internal void SetAssociations(IReadOnlyList<GalleryEntry> entries) { _associations = entries; UpdateContext(); }

    internal GalleryViewer()
    {
        Focusable = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        Background = new SolidColorBrush(Color.FromRgb(13, 16, 22));
        Grid root = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"), Margin = new Thickness(12), RowSpacing = 8 };
        WrapPanel toolbar = new() { Orientation = Orientation.Horizontal };
        _close = Command(toolbar, "GalleryClose", Texts.T("gallery.viewer0"), () => CloseRequested?.Invoke());
        _previous = Command(toolbar, "GalleryPrevious", Texts.T("gallery.viewer1"), () => Move(-1));
        _next = Command(toolbar, "GalleryNext", Texts.T("gallery.viewer2"), () => Move(1));
        Command(toolbar, "GalleryFit", Texts.T("gallery.fit"), Fit);
        _actual = Command(toolbar, "GalleryActual", "1:1", ActualPixels);
        Command(toolbar, "GalleryMinus", "−", () => Zoom(1 / 1.25));
        Command(toolbar, "GalleryPlus", "+", () => Zoom(1.25));
        Command(toolbar, "GalleryToggleContext", Texts.T("gallery.context"), () => { _contextOpen = !_contextOpen; LayoutContext(); });
        _readPosts = Command(toolbar, "GalleryReadPosts", Texts.T("gallery.read"), () => ReadPostsRequested?.Invoke());
        _readPosts.IsVisible = false;
        toolbar.Children.Add(_position);
        root.Children.Add(toolbar);
        root.Children.Add(_quality); Grid.SetRow(_quality, 1);
        root.Children.Add(_scope); Grid.SetRow(_scope, 2);
        root.Children.Add(_warnings); Grid.SetRow(_warnings, 3);
        _viewport.Children.Add(_image); _viewport.Children.Add(_status);
        _body.Children.Add(_viewport);
        _contextPanel.Child = new ScrollViewer { Content = _context, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _body.Children.Add(_contextPanel); Grid.SetColumn(_contextPanel, 1);
        root.Children.Add(_body); Grid.SetRow(_body, 4); Content = root;
        SizeChanged += (_, _) => LayoutContext();
        _viewport.SizeChanged += (_, _) => FitIfAutomatic();
        _viewport.PointerWheelChanged += (_, e) => { Zoom(e.Delta.Y > 0 ? 1.25 : 1 / 1.25, e.GetPosition(_viewport)); e.Handled = true; };
        _viewport.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed) return;
            _dragging = true; _dragPoint = e.GetPosition(_viewport); e.Pointer.Capture(_viewport); e.Handled = true;
        };
        _viewport.PointerReleased += (_, e) => { _dragging = false; e.Pointer.Capture(null); };
        _viewport.PointerCaptureLost += (_, _) => _dragging = false;
        _viewport.PointerMoved += (_, e) =>
        {
            if (!_dragging || !_manualZoom) return;
            Point point = e.GetPosition(_viewport);
            _panX += point.X - _dragPoint.X; _panY += point.Y - _dragPoint.Y; _dragPoint = point; ApplyTransform();
        };
        _viewport.DoubleTapped += (_, e) => { if (_manualZoom) Fit(); else ActualPixels(); e.Handled = true; };
        KeyDown += OnKeyDown;
        AttachedToVisualTree += (_, _) => Texts.Changed += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        { Avalonia.Threading.Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        var labels = new Dictionary<string, string>
        {
            ["GalleryClose"] = "gallery.viewer0", ["GalleryPrevious"] = "gallery.viewer1",
            ["GalleryNext"] = "gallery.viewer2", ["GalleryFit"] = "gallery.fit",
            ["GalleryActual"] = "gallery.actual", ["GalleryToggleContext"] = "gallery.context",
            ["GalleryReadPosts"] = "gallery.read"
        };
        foreach (Button button in this.GetVisualDescendants().OfType<Button>())
            if (button.Name != null && labels.TryGetValue(button.Name, out string? key))
            { button.Content = Texts.T(key); AutomationProperties.SetName(button, Texts.T(key)); }
        UpdateContext(); UpdateQuality();
    }

    private static Button Command(Panel panel, string name, string label, Action action)
    {
        Button button = new() { Name = name, Content = label, Margin = new Thickness(0, 0, 6, 6) };
        AutomationProperties.SetName(button, name == "GalleryMinus" ? Texts.T("gallery.viewer3") : name == "GalleryPlus" ? Texts.T("gallery.viewer4") : label);
        button.Click += (_, _) => action(); panel.Children.Add(button); return button;
    }
    internal void SetPosition(string label, bool previous, bool next)
    { _position.Text = label; _previous.IsEnabled = previous; _next.IsEnabled = next; }
    internal void SetScope(string message) => _scope.Text = message;
    internal void SetWarnings(string message) => _warnings.Text = message;
    internal void SetEmpty(string message, bool canReadPosts)
    { SetNotice(message); _readPosts.IsVisible = canReadPosts; SetPosition(string.Empty, false, false); }
    internal void SetNotice(string message) => _status.Text = message;

    internal GalleryViewerState CaptureState() => new(_entry?.Key, _entry?.Post?.PostId, _entry?.Attachment?.Position,
        _manualZoom, _zoom, _panX, _panY, _contextOpen);
    internal void RestoreState(GalleryViewerState state)
    {
        _restore = state;
        if (_info != null) ApplyRestoredState();
    }
    private void ApplyRestoredState()
    {
        if (_restore is not { } state || _entry == null || state.Key != _entry.Key || state.PostId != _entry.Post?.PostId
            || state.AttachmentPosition != _entry.Attachment?.Position) return;
        _restore = null; _contextOpen = state.ContextOpen; LayoutContext();
        _manualZoom = state.ManualZoom; _zoom = Math.Clamp(state.PhysicalZoom, 0.01, 16); _panX = state.PanX; _panY = state.PanY;
        FitIfAutomatic(); if (_manualZoom) _ = EnsureDetailAsync();
    }

    internal async Task ShowAsync(GalleryEntry entry, CancellationToken cancellationToken)
    {
        CancelLoads(); int request = ++_request; ClearBitmaps();
        _readPosts.IsVisible = false;
        _entry = entry; _info = null; _manualZoom = false; _panX = _panY = 0;
        _status.Text = Texts.T("gallery.loading"); _quality.Text = string.Empty; _actual.IsEnabled = false; UpdateContext();
        // Capture all UI state before entering the decoder's worker.
        double scale = DisplayScale;
        Size available = _viewport.Bounds.Size;
        // A frozen saved-media entry can start decoding before the new overlay's first layout.
        // Use the window's real client area instead of a 1px decode with a zero-height result.
        if (available.Width <= 0 || available.Height <= 0)
            available = TopLevel.GetTopLevel(this)?.ClientSize ?? new Size(1280, 720);
        int width = (int)Math.Max(1, available.Width * scale);
        int height = (int)Math.Max(1, available.Height * scale);
        using CancellationTokenSource load = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _load = load;
        try
        {
            (Bitmap bitmap, GalleryImageInfo info) = await GalleryImageDecoder.DecodeAsync(entry, width, height, false, load.Token);
            if (request != _request || load.IsCancellationRequested) { bitmap.Dispose(); return; }
            _fitBitmap = bitmap; _info = info; _image.Source = bitmap; _status.Text = string.Empty; _actual.IsEnabled = true;
            Fit(); ApplyRestoredState();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception exception) { if (request == _request) _status.Text = exception.Message; }
        finally { if (ReferenceEquals(_load, load)) _load = null; }
    }

    private async Task EnsureDetailAsync()
    {
        if (_entry == null || _info == null || _highBitmap != null || _detailLoad != null) return;
        GalleryEntry entry = _entry; int request = _request;
        using CancellationTokenSource load = new(); _detailLoad = load;
        _quality.Text = Texts.T("gallery.viewer5");
        try
        {
            (Bitmap bitmap, _) = await GalleryImageDecoder.DecodeAsync(entry, 1, 1, true, load.Token);
            if (request != _request || load.IsCancellationRequested) { bitmap.Dispose(); return; }
            _highBitmap = bitmap; _image.Source = bitmap; ApplyTransform();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception exception) { if (request == _request) _status.Text = Texts.T("gallery.viewer6") + exception.Message; }
        finally { if (ReferenceEquals(_detailLoad, load)) { _detailLoad = null; UpdateQuality(); } }
    }

    private void UpdateContext()
    {
        _context.Children.Clear(); if (_entry == null) return;
        GalleryEntry entry = _entry;
        Command(_context, "GalleryReturnImage", Texts.T("gallery.viewer7"), () => { _contextOpen = false; LayoutContext(); });
        AddContext(entry.Key.Thread.Source + " /" + entry.Key.Thread.Board + "/ · " + entry.Key.Thread.Thread);
        AddContext(entry.Asset.OriginalFileName ?? entry.Asset.RequestedFileName);
        AddContext(entry.Asset.Length.ToString("N0", Texts.Culture) + " bytes");
        if (entry.Historical) AddContext(Texts.T("gallery.viewer8"));
        GalleryEntry[] associations = _associations.Where(value => value.Key == entry.Key && value.Post != null)
            .DistinctBy(value => (value.Post!.PostId, value.Attachment?.Position)).ToArray();
        if (associations.Length > 1)
        {
            ComboBox selector = new() { HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = associations.Select(value => "#" + value.Post!.PostId + " · " + (value.Attachment?.Position + 1)).ToArray(),
                SelectedIndex = Array.FindIndex(associations, value => value.Post?.PostId == entry.Post?.PostId && value.Attachment?.Position == entry.Attachment?.Position) };
            AutomationProperties.SetName(selector, Texts.T("gallery.viewer9"));
            selector.SelectionChanged += (_, _) =>
            {
                if (selector.SelectedIndex < 0) return;
                _entry = associations[selector.SelectedIndex]; UpdateContext(); AssociationRequested?.Invoke(_entry);
            };
            _context.Children.Add(selector);
        }
        if (entry.Post is not { } post) { AddContext(Texts.T("gallery.noPost")); return; }
        AddContext("#" + post.PostId + " · " + ArchivePresentation.PlainText(post.Author) + " · " + post.PostedAt?.ToString("g", Texts.Culture));
        AddContext(ArchivePresentation.PlainText(post.Subject));
        AddContext(ArchivePresentation.PlainText(post.Html));
        if (Uri.TryCreate(entry.ThreadUrl, UriKind.Absolute, out Uri? threadUrl) && threadUrl.Scheme is "http" or "https")
            foreach (ArchiveQuote quote in ArchiveQuoteParser.Parse(entry.Key.Thread, entry.ThreadUrl!, post))
            {
                if (quote.Target == null || quote.IsExternal) continue;
                Command(_context, "GalleryQuote", quote.Label, () => QuoteRequested?.Invoke(quote));
            }
        Command(_context, "GalleryReadPost", Texts.T("gallery.viewer10"), () => ReadPostRequested?.Invoke(entry));
    }
    private void AddContext(string text) => _context.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White });
    private void LayoutContext()
    {
        bool compact = Bounds.Width < 850;
        _contextPanel.IsVisible = _contextOpen; _viewport.IsVisible = !(_contextOpen && compact);
        _contextPanel.Width = compact ? double.NaN : 320;
        Grid.SetColumn(_contextPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(_contextPanel, compact ? 2 : 1);
        FitIfAutomatic();
    }
    private void Move(int delta) { if (delta < 0 ? _previous.IsEnabled : _next.IsEnabled) MoveRequested?.Invoke(delta); }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseRequested?.Invoke(); e.Handled = true; return; }
        // Text selection, input, and scrollable post context keep their native arrows/zoom keys.
        if (e.Source is Visual visual && (visual == _contextPanel || visual.GetVisualAncestors().Contains(_contextPanel))) return;
        if (e.Source is TextBox or SelectableTextBlock) return;
        switch (e.Key)
        {
            case Key.Left: Move(-1); break;
            case Key.Right: Move(1); break;
            case Key.Add: case Key.OemPlus: Zoom(1.25); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.25); break;
            default: return;
        }
        e.Handled = true;
    }
    private double DisplayScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    private void Zoom(double factor, Point? anchor = null)
    {
        if (_info == null) return;
        double old = _zoom; _manualZoom = true; _zoom = Math.Clamp(old * factor, 0.01, 16);
        Point offset = anchor is { } point ? new Point(point.X - _viewport.Bounds.Width / 2, point.Y - _viewport.Bounds.Height / 2) : default;
        _panX = offset.X - (offset.X - _panX) * _zoom / old;
        _panY = offset.Y - (offset.Y - _panY) * _zoom / old;
        ApplyTransform(); _ = EnsureDetailAsync();
    }
    private void ActualPixels() { if (_info == null) return; _manualZoom = true; _zoom = 1; _panX = _panY = 0; ApplyTransform(); _ = EnsureDetailAsync(); }
    private void FitIfAutomatic() { if (!_manualZoom) Fit(); else ApplyTransform(); }
    private void Fit()
    {
        if (_info == null) return;
        _manualZoom = false; _panX = _panY = 0;
        _zoom = GalleryGeometry.Fit(_info.Width, _info.Height, _viewport.Bounds.Width, _viewport.Bounds.Height, DisplayScale);
        ApplyTransform();
    }
    private void ApplyTransform()
    {
        if (_info == null) return;
        double scale = DisplayScale;
        _image.Width = _info.Width * _zoom / scale; _image.Height = _info.Height * _zoom / scale;
        _panX = GalleryGeometry.Pan(_panX, _info.Width, _zoom, scale, _viewport.Bounds.Width);
        _panY = GalleryGeometry.Pan(_panY, _info.Height, _zoom, scale, _viewport.Bounds.Height);
        _image.RenderTransform = new TranslateTransform(_panX, _panY); UpdateQuality();
    }
    private void UpdateQuality()
    {
        if (_info == null) return;
        _quality.Text = _info.Width + " × " + _info.Height + " · " + _info.Format + " · " +
            (_manualZoom ? (_zoom * 100).ToString("0", Texts.Culture) + "%" : Texts.T("gallery.fit")) +
            (_manualZoom && _highBitmap == null ? Texts.T("gallery.viewer11") : string.Empty) +
            (_info.StillFrame ? Texts.T("gallery.viewer12") : string.Empty);
    }
    private void CancelLoads() { _load?.Cancel(); _detailLoad?.Cancel(); _detailLoad = null; }
    private void ClearBitmaps() { _image.Source = null; _highBitmap?.Dispose(); _highBitmap = null; _fitBitmap?.Dispose(); _fitBitmap = null; }
    internal void Stop() { ++_request; CancelLoads(); ClearBitmaps(); _entry = null; _info = null; _restore = null; _associations = Array.Empty<GalleryEntry>(); _contextOpen = false; LayoutContext(); }
}

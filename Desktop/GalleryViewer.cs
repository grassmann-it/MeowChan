using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Archive;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop;

internal sealed record GalleryViewerState(GalleryAssetKey? Key, string? PostId, int? AttachmentPosition,
    bool ManualZoom, double PhysicalZoom, double PanX, double PanY, bool ContextOpen, bool ActualZoom);

internal sealed class GalleryViewer : UserControl
{
    private readonly TranslateTransform _imageTranslation = new();
    private readonly Image _image = new() { Name = "GalleryImage", Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _viewport = new() { Name = "GalleryViewport", ClipToBounds = true, Background = Brushes.Black };
    private readonly TextBlock _status = new() { Name = "GalleryStatus", TextWrapping = TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), IsHitTestVisible = false };
    private readonly TextBlock _scope = new() { Name = "GalleryScope", [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _warnings = new() { Name = "GalleryWarnings", [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _favoriteError = new() { Name = "GalleryFavoriteError", [!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"),
        TextWrapping = TextWrapping.Wrap, Focusable = true, IsVisible = false };
    private readonly Button _readPosts;
    private readonly TextBlock _position = new() { [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _quality = new() { [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _context = new() { Spacing = 12 };
    private readonly Border _contextPanel = new() { Name = "GalleryContext", Padding = new Thickness(18), IsVisible = false, [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Panel") };
    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
    private readonly Grid _visualRoot = new() { ClipToBounds = true };
    private readonly Grid _nativeVideoOverlay = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _close;
    private readonly Button _actual;
    private readonly Button _fit;
    private readonly Button _zoomIn;
    private readonly Button _zoomOut;
    private readonly Button _exportOriginal;
    private readonly ToggleButton _favorite;
    private readonly ToggleButton _contextToggle;
    private readonly Button _helpButton;
    private readonly Border _helpPanel = new() { Name = "GalleryShortcutHelp", Padding = new Thickness(12), [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Panel"), IsVisible = false };
    private readonly StackPanel _helpContent = new() { Spacing = 6 };
    private readonly Border _chrome = new() { Name = "GalleryOverlayControls", Padding = new Thickness(8),
        Margin = new Thickness(10, 10, 10, 0), CornerRadius = new CornerRadius(8),
        [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Panel"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _infoOverlay = new() { Name = "GalleryOverlayInfo", Padding = new Thickness(8, 5),
        Margin = new Thickness(10, 0, 10, 10), CornerRadius = new CornerRadius(8),
        [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Panel"),
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border _statusPlate = new() { Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(7),
        [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Panel"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 650, IsHitTestVisible = false, IsVisible = false };
    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private GalleryVideoPlayer? _video;
    private bool _videoSuspended;
    private bool _nativeOverlayActive;
    private bool _nativeOverlayFallback;
    private bool _nativeOverlayUnsupported;
    private Window? _nativeOverlayWindow;
    private CancellationToken _sessionCancellation;
    private readonly GalleryDecodeQueue _decodeQueue = new();
    private GalleryDecodeRequest? _desiredDecode;
    private int _decodeGeneration;
    private int? _pendingOriginalGeneration;
    private TopLevel? _scaleOwner;
    // Instance seam keeps controlled decodes isolated from other viewers.
    internal Func<GalleryEntry, GalleryDecodeRequest, CancellationToken,
        Task<(Bitmap Bitmap, GalleryImageInfo Info)>> DecodeImageAsync { get; set; } = GalleryImageDecoder.DecodeAsync;
    private Bitmap? _fitBitmap;
    private Bitmap? _highBitmap;
    private GalleryImageInfo? _info;
    private GalleryEntry? _entry;
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private bool _manualZoom;
    private bool _actualZoom;
    private bool _contextOpen;
    private bool _showShortcutHelp = true;
    private bool _favoriteSelected;
    private bool _helpOpen;
    private bool _helpExpanded;
    private bool _videoAutoplay = true;
    private bool _videoStartMuted;
    private int _videoVolume = 70;
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
    internal event Action<bool>? ContextOpenChanged;
    internal event Action? FullscreenRequested;
    internal event EventHandler<KeyEventArgs>? ShellKeyRequested;
    internal event Action<GalleryEntry>? FavoriteToggleRequested;
    internal event Action<GalleryEntry>? RatingEditRequested;
    internal event Action<GalleryEntry, int?>? RatingSetRequested;
    private CommandRouter? _ratingRouter;
    private CommandRouter? _nativeRatingRouter;
    private CommandRouter CreateRatingRouter(Control owner)
    {
        CommandRouter router = new(owner);
        bool Ready() => _entry != null && _ratingButton.IsEnabled;
        router.Register(new("media.rating.edit", owner, () => owner.IsEffectivelyVisible, Ready,
            () => { if (_entry != null) RatingEditRequested?.Invoke(_entry); }, Priority: 200));
        router.Register(new("media.rating.clear", owner, () => owner.IsEffectivelyVisible, Ready,
            () => { if (_entry != null) RatingSetRequested?.Invoke(_entry, null); }, Priority: 200));
        for (int value = 1; value <= 5; value++)
        {
            int selected = value;
            router.Register(new("media.rating.set-" + value, owner, () => owner.IsEffectivelyVisible, Ready,
                () => { if (_entry != null) RatingSetRequested?.Invoke(_entry, selected); }, Priority: 200));
        }
        return router;
    }
    private int? _ratingValue;
    private string? _ratingError;
    private bool _ratingAvailable;
    private readonly Button _ratingButton = new() { Name = "GalleryRating" };
    private readonly TextBlock _ratingStatus = new() { Name = "GalleryRatingStatus", [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap };
    internal void SetRating(int? value, string? error, bool available = true)
    {
        _ratingValue = value; _ratingError = error; _ratingAvailable = available;
        _ratingStatus.Text = MediaRatingText.Label + ": " + MediaRatingText.Value(value) + (error == null ? string.Empty : " · " + error);
        _ratingButton.IsEnabled = available && _entry != null;
    }
    internal event Action<GalleryEntry>? ExportOriginalRequested;
    internal event Action<GalleryEntry>? ShowInFolderRequested;
    internal event Action<GalleryEntry>? DetailsRequested;
    internal event Action<int>? VideoVolumeChanged;

    internal bool IsOverlayOpen => _helpOpen || _contextOpen;
    internal bool OwnsOverlayWindow(Window window) =>
        _nativeOverlayActive && ReferenceEquals(TopLevel.GetTopLevel(_nativeVideoOverlay), window);
    internal IEnumerable<Visual> GetGalleryVisualDescendants() =>
        ((Visual)this).GetVisualDescendants().Concat(_nativeVideoOverlay.GetVisualDescendants()).Distinct();
    internal bool TryDismissOverlay()
    {
        if (_helpOpen) { SetHelp(false); return true; }
        if (_contextOpen) { SetContextOpen(false); return true; }
        return false;
    }

    internal void Configure(bool contextOpen, bool showShortcutHelp, bool videoAutoplay, bool videoStartMuted, int videoVolume)
    {
        _showShortcutHelp = showShortcutHelp;
        _videoAutoplay = videoAutoplay;
        _videoStartMuted = videoStartMuted;
        _videoVolume = Math.Clamp(videoVolume, 0, 100);
        _video?.Configure(_videoAutoplay, _videoStartMuted, _videoVolume);
        UpdateHelpButtonTooltip();
        SetContextOpen(contextOpen, notify: false);
    }

    internal void SetFavorite(bool selected)
    {
        _favoriteSelected = selected;
        UpdateFavorite();
    }

    internal void SetFavoriteError(string? error)
    {
        _favoriteError.Text = error;
        _favoriteError.IsVisible = !string.IsNullOrWhiteSpace(error);
        AutomationProperties.SetName(_favoriteError, error ?? string.Empty);
    }

    internal void SetAssociations(IReadOnlyList<GalleryEntry> entries) { _associations = entries; UpdateContext(); }

    internal GalleryViewer()
    {
        AttachedToVisualTree += (_, _) =>
        {
            _ratingRouter?.Dispose(); _nativeRatingRouter?.Dispose();
            _ratingRouter = CreateRatingRouter(this); _nativeRatingRouter = CreateRatingRouter(_nativeVideoOverlay);
        };
        DetachedFromVisualTree += (_, _) => { _ratingRouter?.Dispose(); _nativeRatingRouter?.Dispose(); };
        _image.RenderTransform = _imageTranslation;
        Focusable = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        Grid root = _visualRoot;
        WrapPanel toolbar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _close = Command(toolbar, "GalleryClose", Texts.T("gallery.viewer0"), () => CloseRequested?.Invoke());
        _previous = Command(toolbar, "GalleryPrevious", Texts.T("gallery.viewer1"), () => Move(-1));
        _next = Command(toolbar, "GalleryNext", Texts.T("gallery.viewer2"), () => Move(1));
        _fit = Command(toolbar, "GalleryFit", Texts.T("gallery.fit"), Fit);
        _actual = Command(toolbar, "GalleryActual", "1:1", ActualPixels);
        _zoomOut = Command(toolbar, "GalleryMinus", "−", () => Zoom(1 / 1.1));
        _zoomIn = Command(toolbar, "GalleryPlus", "+", () => Zoom(1.1));
        _contextToggle = (ToggleButton)Command(toolbar, "GalleryToggleContext", Texts.T("gallery.context"), () => SetContextOpen(!_contextOpen));
        _favorite = (ToggleButton)Command(toolbar, "GalleryFavorite", Texts.T("viewer.favorite.add"), ToggleFavorite);
        CommandBindings.Apply(_ratingButton, "media.rating.edit");
        _ratingButton.Click += (_, _) => { if (_entry != null) RatingEditRequested?.Invoke(_entry); };
        toolbar.Children.Add(_ratingButton);
        _exportOriginal = Command(toolbar, "GalleryExportOriginal", Texts.T("viewer.menu.exportOriginal"), () =>
        { if (_entry != null) ExportOriginalRequested?.Invoke(_entry); });
        Command(toolbar, "GalleryFullscreen", Texts.T("viewer.fullscreen"), () => FullscreenRequested?.Invoke());
        _helpButton = Command(toolbar, "GalleryHelp", Texts.T("viewer.help"), () => SetHelp(!_helpOpen));
        _readPosts = Command(toolbar, "GalleryReadPosts", Texts.T("gallery.read"), () => ReadPostsRequested?.Invoke());
        _readPosts.IsVisible = false;
        // Chrome follows the host theme; the media viewport keeps its black matte.
        _chrome.Child = toolbar;
        StackPanel infoContent = new() { Spacing = 2 };
        Grid infoHeadline = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        infoHeadline.Children.Add(_quality);
        infoHeadline.Children.Add(_position); Grid.SetColumn(_position, 1);
        infoContent.Children.Add(infoHeadline);
        infoContent.Children.Add(_scope);
        infoContent.Children.Add(_warnings);
        infoContent.Children.Add(_favoriteError);
        infoContent.Children.Add(_ratingStatus);
        AutomationProperties.SetLiveSetting(_favoriteError, AutomationLiveSetting.Assertive);
        _infoOverlay.Child = infoContent;
        _helpPanel.Child = new ScrollViewer { Content = _helpContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _helpPanel.HorizontalAlignment = HorizontalAlignment.Right;
        _helpPanel.VerticalAlignment = VerticalAlignment.Top;
        _helpPanel.Margin = new Thickness(12, 94, 12, 12);
        _helpPanel.MaxWidth = 390;
        _helpPanel.MaxHeight = 350;
        _viewport.Children.Add(_image);
        _statusPlate.Child = _status;
        _viewport.Children.Add(_statusPlate);
        _status.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty || e.Property == IsVisibleProperty)
                _statusPlate.IsVisible = _status.IsVisible && !string.IsNullOrWhiteSpace(_status.Text);
        };
        _body.Children.Add(_viewport);
        // Inherit native control states from the host, including popups and focus.
        _contextPanel.Child = new ScrollViewer
        {
            Content = _context,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        _body.Children.Add(_contextPanel); Grid.SetColumn(_contextPanel, 1);
        root.Children.Add(_body);
        root.Children.Add(_infoOverlay);
        root.Children.Add(_chrome);
        root.Children.Add(_helpPanel);
        Content = root;
        _chromeTimer.Tick += (_, _) => HideChromeIfSafe();
        _chrome.SizeChanged += (_, _) => { if (_contextOpen) LayoutContext(); };
        _infoOverlay.SizeChanged += (_, _) => { if (_contextOpen) LayoutContext(); };
        root.AddHandler(PointerMovedEvent, (_, _) => RevealChrome(), RoutingStrategies.Tunnel, handledEventsToo: true);
        root.PointerEntered += (_, _) => RevealChrome();
        root.PointerPressed += (_, _) => RevealChrome();
        _nativeVideoOverlay.AddHandler(PointerMovedEvent, (_, _) => RevealChrome(), RoutingStrategies.Tunnel, handledEventsToo: true);
        _nativeVideoOverlay.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _nativeVideoOverlay.AddHandler(KeyDownEvent, OnNativeOverlayKeyDown, RoutingStrategies.Bubble);
        _nativeVideoOverlay.PointerEntered += (_, _) => RevealChrome();
        _nativeVideoOverlay.PointerPressed += (_, _) => RevealChrome();
        _nativeVideoOverlay.ContextRequested += (_, e) =>
        {
            if (e.Source is Visual input && input.GetVisualAncestors().Prepend(input).Any(control => control is TextBox or ComboBox or Slider)) return;
            if (_entry != null) { ShowMediaMenu(); e.Handled = true; }
        };
        SizeChanged += (_, _) =>
        {
            _chrome.MaxWidth = Math.Max(0, Bounds.Width - 20);
            LayoutContext();
        };
        _viewport.SizeChanged += (_, _) => { UpdateInfoOverlayPosition(); FitIfAutomatic(); };
        _viewport.PointerWheelChanged += (_, e) => { Focus(); Zoom(GalleryGeometry.WheelFactor(e.Delta.Y), e.GetPosition(_viewport)); e.Handled = true; };
        _viewport.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed) return;
            Focus();
            _dragging = true; _dragPoint = e.GetPosition(_viewport); e.Pointer.Capture(_viewport); e.Handled = true;
        };
        _viewport.PointerReleased += (_, e) => { _dragging = false; e.Pointer.Capture(null); };
        _viewport.PointerCaptureLost += (_, _) => _dragging = false;
        _viewport.PointerMoved += (_, e) =>
        {
            if (!_dragging || !_manualZoom) return;
            Point point = e.GetPosition(_viewport);
            _panX += point.X - _dragPoint.X; _panY += point.Y - _dragPoint.Y; _dragPoint = point; ApplyPan();
        };
        _viewport.DoubleTapped += (_, e) => { if (_manualZoom) Fit(); else ActualPixels(); e.Handled = true; };
        _viewport.ContextRequested += (_, e) =>
        {
            if (e.Source is Visual input && input.GetVisualAncestors().Prepend(input).Any(control => control is TextBox or ComboBox or Slider)) return;
            if (_entry != null) { ShowMediaMenu(); e.Handled = true; }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AttachedToVisualTree += (_, _) =>
        {
            Texts.Changed += OnLanguageChanged;
            ShortcutService.Changed += OnShortcutBindingsChanged;
            _scaleOwner = TopLevel.GetTopLevel(this);
            if (_scaleOwner != null) _scaleOwner.ScalingChanged += OnScaleOwnerChanged;
            RevealChrome();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Texts.Changed -= OnLanguageChanged;
            ShortcutService.Changed -= OnShortcutBindingsChanged;
            if (_scaleOwner != null) _scaleOwner.ScalingChanged -= OnScaleOwnerChanged;
            _scaleOwner = null;
            _chromeTimer.Stop();
        };
        UpdateFavorite();
        _exportOriginal.IsEnabled = false;
        UpdateContextButton();
        UpdateHelp();
        UpdateHelpButtonTooltip();
        SetImageCommands(false);
    }

    private void RevealChrome()
    {
        _chrome.IsVisible = true;
        _chromeTimer.Stop();
        if (!_nativeOverlayFallback) _chromeTimer.Start();
    }

    private void HideChromeIfSafe()
    {
        if (_nativeOverlayFallback) return;
        if (_helpOpen || _contextOpen || _chrome.IsPointerOver || _helpPanel.IsPointerOver ||
            _viewport.ContextMenu?.IsOpen == true || _nativeVideoOverlay.ContextMenu?.IsOpen == true ||
            _video?.IsKeyboardFocusWithin == true)
            return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control focused &&
            !ReferenceEquals(focused, this) && focused.GetVisualAncestors().Contains(this))
            return;
        if (_nativeOverlayActive && TopLevel.GetTopLevel(_nativeVideoOverlay)?.FocusManager?.GetFocusedElement() is Control nativeFocused &&
            !ReferenceEquals(nativeFocused, _nativeVideoOverlay) && nativeFocused.GetVisualAncestors().Contains(_nativeVideoOverlay))
            return;
        _chromeTimer.Stop();
        _chrome.IsVisible = false;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        { Avalonia.Threading.Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        CommandBindings.Apply(_ratingButton, "media.rating.edit");
        SetRating(_ratingValue, _ratingError, _ratingAvailable);
        var labels = new Dictionary<string, string>
        {
            ["GalleryClose"] = "gallery.viewer0", ["GalleryPrevious"] = "gallery.viewer1",
            ["GalleryNext"] = "gallery.viewer2", ["GalleryFit"] = "gallery.fit",
            ["GalleryActual"] = "gallery.actual", ["GalleryToggleContext"] = "gallery.context",
            ["GalleryReadPosts"] = "gallery.read", ["GalleryFullscreen"] = "viewer.fullscreen",
            ["GalleryHelp"] = "viewer.help"
        };
        foreach (Button button in this.GetVisualDescendants().OfType<Button>()
            .Concat(_nativeVideoOverlay.GetVisualDescendants().OfType<Button>()).Distinct())
        {
            if (button.Name != null && CommandId(button.Name) is { } id)
            {
                CommandBindings.Apply(button, id, iconOnly: IsIconOnly(button.Name));
                ApplyCompactLabel(button, button.Name);
            }
            else if (button.Name != null && labels.TryGetValue(button.Name, out string? key))
            { button.Content = Texts.T(key); AutomationProperties.SetName(button, Texts.T(key)); }
        }
        UpdateHelpButtonTooltip();
        UpdateContext(); UpdateQuality(); UpdateFavorite(); UpdateContextButton(); UpdateHelp();
    }

    private void OnShortcutBindingsChanged() => OnLanguageChanged(this, EventArgs.Empty);

    private void UpdateHelpButtonTooltip()
    {
        string tip = string.Join(" · ", (_showShortcutHelp ? new[] { "viewer.shortcut-help", "favorites.toggle", "gallery.context-toggle" } : new[] { "viewer.shortcut-help" }).Select(id => CommandBindings.Tooltip(id)));
        ToolTip.SetTip(_helpButton, tip);
        AutomationProperties.SetHelpText(_helpButton, tip);
    }

    private static string? CommandId(string name) => name switch
    {
        "GalleryClose" => "gallery.close", "GalleryPrevious" => "gallery.previous",
        "GalleryNext" => "gallery.next", "GalleryFit" => "gallery.fit",
        "GalleryActual" => "gallery.actual-size", "GalleryMinus" => "gallery.zoom-out",
        "GalleryPlus" => "gallery.zoom-in", "GalleryToggleContext" => "gallery.context-toggle",
        "GalleryReadPosts" => "thread.read", "GalleryFullscreen" => "window.fullscreen",
        "GalleryHelp" => "viewer.shortcut-help", "GalleryFavorite" => "favorites.toggle",
        "GalleryExportOriginal" => "media.export-original",
        "GalleryReturnImage" => "gallery.context-close", "GalleryReadPost" => "gallery.show-in-thread",
        _ => null
    };

    private static bool IsIconOnly(string name) => name is "GalleryPrevious" or "GalleryNext" or "GalleryFit" or
        "GalleryMinus" or "GalleryPlus" or "GalleryHelp" or
        "GalleryToggleContext" or "GalleryFavorite";

    private void ApplyCompactLabel(Button button, string name)
    {
        string? label = name switch
        {
            "GalleryClose" => GalleryViewerText.Get("Close", "Schließen"),
            "GalleryFit" => Texts.T("gallery.fit"),
            "GalleryToggleContext" => GalleryViewerText.Get("Post", "Beitrag"),
            "GalleryFavorite" => GalleryViewerText.Get("Favorite", "Favorit"),
            "GalleryExportOriginal" => "Export",
            "GalleryFullscreen" => GalleryViewerText.Get("Full", "Vollbild"),
            _ => null
        };
        if (label == null || CommandId(name) is not { } id) return;
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        Border icon = (Border)CommandBindings.Icon(id, 14);
        // Semi sets the checked/pressed/disabled foreground on the content
        // presenter; inherit that same foreground for the icon and label.
        icon.Bind(Border.BackgroundProperty, icon.GetObservable(TextElement.ForegroundProperty));
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
    }

    private Button Command(Panel panel, string name, string label, Action action)
    {
        string? id = CommandId(name);
        Button button = name is "GalleryToggleContext" or "GalleryFavorite"
            ? new ToggleButton { Name = name, Margin = new Thickness(0, 0, 6, 6) }
            : new Button { Name = name, Margin = new Thickness(0, 0, 6, 6) };
        if (id == null) { button.Content = label; AutomationProperties.SetName(button, label); }
        else
        {
            CommandBindings.Apply(button, id, iconOnly: IsIconOnly(name));
            ApplyCompactLabel(button, name);
        }
        button.Click += (_, _) => action();
        panel.Children.Add(button); return button;
    }
    internal void SetPosition(string label, bool previous, bool next)
    { _position.Text = label; _previous.IsEnabled = previous; _next.IsEnabled = next; }
    internal void SetScope(string message) => _scope.Text = message;
    internal void SetWarnings(string message) => _warnings.Text = message;
    internal void SetEmpty(string message, bool canReadPosts)
    { SetNotice(message); _readPosts.IsVisible = canReadPosts; _exportOriginal.IsEnabled = false; SetPosition(string.Empty, false, false); }
    internal void SetNotice(string message) => _status.Text = message;
    private void SetImageCommands(bool enabled) => _fit.IsEnabled = _actual.IsEnabled = _zoomIn.IsEnabled = _zoomOut.IsEnabled = enabled;

    private void SetContextOpen(bool open, bool notify = true)
    {
        if (_contextOpen == open) return;
        RevealChrome();
        _contextOpen = open;
        LayoutContext();
        UpdateContextButton();
        if (!open) Focus();
        if (notify) ContextOpenChanged?.Invoke(open);
    }

    private void ToggleFavorite()
    {
        _favorite.IsChecked = _favoriteSelected;
        ApplyCompactLabel(_favorite, "GalleryFavorite");
        if (_entry != null) FavoriteToggleRequested?.Invoke(_entry);
    }

    private void UpdateFavorite()
    {
        string label = Texts.T(_favoriteSelected ? "viewer.favorite.remove" : "viewer.favorite.add");
        _favorite.IsChecked = _favoriteSelected;
        AutomationProperties.SetName(_favorite, label);
        ToolTip.SetTip(_favorite, CommandBindings.Tooltip("favorites.toggle"));
        AutomationProperties.SetHelpText(_favorite, CommandBindings.Tooltip("favorites.toggle"));
        _favorite.IsEnabled = _entry != null;
    }

    private void UpdateContextButton()
    {
        string label = Texts.T(_contextOpen ? "viewer.context.hide" : "viewer.context.show")
            + (CommandCatalog.ShortcutDisplay("gallery.context-toggle") is string gesture ? " · " + gesture : "");
        _contextToggle.IsChecked = _contextOpen;
        AutomationProperties.SetName(_contextToggle, label);
        ToolTip.SetTip(_contextToggle, label);
    }

    private void SetHelp(bool open, bool expanded = false)
    {
        RevealChrome();
        bool wasOpen = _helpOpen;
        _helpOpen = open;
        _helpExpanded = expanded;
        UpdateHelp();
        if (!open && wasOpen) Focus();
    }

    private void UpdateHelp()
    {
        _helpPanel.IsVisible = _helpOpen;
        _helpContent.Children.Clear();
        if (!_helpOpen) return;
        _helpContent.Children.Add(new TextBlock { Text = Texts.T("viewer.help.title"), [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), FontWeight = FontWeight.Bold });
        string[] ids = _helpExpanded
            ? new[] { "gallery.previous", "gallery.next", "gallery.fit", "gallery.actual-size", "gallery.zoom-in", "gallery.zoom-out", "favorites.toggle", "gallery.context-toggle", "video.play-pause", "window.fullscreen", "gallery.close" }
            : new[] { "gallery.previous", "gallery.next", "gallery.fit", "favorites.toggle", "gallery.context-toggle" };
        foreach (string id in ids.Concat(CommandCatalog.All.Where(command => command.IsImplemented
            && command.ExecutionContexts.Contains("gallery") && command.ShortcutDisplay != null).Select(command => command.Id)).Distinct())
            _helpContent.Children.Add(new TextBlock { Text = CommandBindings.Tooltip(id), [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap });
        _helpContent.Children.Add(new TextBlock { Text = Texts.T("viewer.help.escape"), [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap });
        WrapPanel actions = new() { ItemSpacing = 6 };
        if (!_helpExpanded) Command(actions, "GalleryHelpMore", Texts.T("viewer.help.more"), () => SetHelp(true, true));
        Command(actions, "GalleryHelpClose", Texts.T("action.close"), () => SetHelp(false));
        _helpContent.Children.Add(actions);
    }

    private void ShowMediaMenu()
    {
        if (_entry == null) return;
        RevealChrome();
        GalleryEntry entry = _entry;
        static MenuItem Item(string id, Action action, bool enabled = true)
        {
            MenuItem item = new() { IsEnabled = enabled };
            CommandBindings.Apply(item, id);
            item.Click += (_, _) => { if (item.IsEnabled) action(); };
            return item;
        }
        MenuItem favorite = Item("favorites.toggle", () => FavoriteToggleRequested?.Invoke(entry));
        favorite.Header = Texts.T(_favoriteSelected ? "viewer.favorite.remove" : "viewer.favorite.add");
        favorite.ToggleType = MenuItemToggleType.CheckBox;
        favorite.IsChecked = _favoriteSelected;
        AutomationProperties.SetName(favorite, favorite.Header.ToString());
        AutomationProperties.SetHelpText(favorite, CommandBindings.Tooltip("favorites.toggle"));
        ToolTip.SetTip(favorite, CommandBindings.Tooltip("favorites.toggle"));
        ContextMenu menu = new() { ItemsSource = new object[]
        {
            favorite,
            Item("media.rating.edit", () => RatingEditRequested?.Invoke(entry), _ratingButton.IsEnabled),
            new Separator(),
            Item("media.export-original", () => ExportOriginalRequested?.Invoke(entry)),
            Item("media.show-in-folder", () => ShowInFolderRequested?.Invoke(entry)),
            new Separator(),
            Item("gallery.show-in-thread", () => ReadPostRequested?.Invoke(entry), entry.Post != null),
            Item("media.details", () => DetailsRequested?.Invoke(entry))
        } };
        Control owner = _nativeOverlayActive ? _nativeVideoOverlay : _viewport;
        owner.ContextMenu = menu;
        menu.Open(owner);
    }

    private void UseNativeVideoOverlay(bool active)
    {
        active &= OperatingSystem.IsWindows() && !_nativeOverlayUnsupported;
        if (_nativeOverlayActive == active) return;
        Grid previous = _nativeOverlayActive ? _nativeVideoOverlay : _visualRoot;
        Grid next = active ? _nativeVideoOverlay : _visualRoot;
        foreach (Control control in new Control[] { _infoOverlay, _chrome, _helpPanel })
        {
            previous.Children.Remove(control);
            next.Children.Add(control);
        }
        _nativeOverlayActive = active;
        _video?.SetNativeOverlay(active ? _nativeVideoOverlay : null);
        UpdateInfoOverlayPosition();
        RevealChrome();
    }

    private void CheckNativeOverlayTransparency()
    {
        if (!_nativeOverlayActive || TopLevel.GetTopLevel(_nativeVideoOverlay) is not Window overlay) return;
        if (overlay.ActualTransparencyLevel == WindowTransparencyLevel.Transparent) return;
        _nativeOverlayUnsupported = true;
        _nativeOverlayWindow = overlay;
        UseNativeVideoOverlay(false);
        SetVideoFallbackLayout(true);
        overlay.Hide();
    }

    private void SetVideoFallbackLayout(bool fallback)
    {
        if (_nativeOverlayFallback == fallback) return;
        _nativeOverlayFallback = fallback;
        _visualRoot.RowDefinitions = new RowDefinitions(fallback ? "Auto,Auto,*,Auto" : "*");
        Grid.SetRow(_chrome, 0);
        Grid.SetRow(_helpPanel, fallback ? 1 : 0);
        Grid.SetRow(_body, fallback ? 2 : 0);
        Grid.SetRow(_infoOverlay, fallback ? 3 : 0);
        _helpPanel.Margin = fallback ? new Thickness(12, 0, 12, 0) : new Thickness(12, 94, 12, 12);
        LayoutContext();
        RevealChrome();
    }

    internal GalleryViewerState CaptureState() => new(_entry?.Key, _entry?.Post?.PostId, _entry?.Attachment?.Position,
        _manualZoom, _zoom, _panX, _panY, _contextOpen, _actualZoom);
    internal void RestoreState(GalleryViewerState state)
    {
        _restore = state;
        if (_info != null) ApplyRestoredState();
    }
    private void ApplyRestoredState()
    {
        if (_restore is not { } state || _entry == null || state.Key != _entry.Key || state.PostId != _entry.Post?.PostId
            || state.AttachmentPosition != _entry.Attachment?.Position) return;
        _restore = null; _contextOpen = state.ContextOpen; LayoutContext(); UpdateContextButton();
        UpdateLayout(); // Restored zoom uses the viewport after the context panel has been measured.
        _manualZoom = state.ManualZoom; _actualZoom = state.ActualZoom;
        _zoom = _actualZoom && state.PhysicalZoom < CurrentFit
            ? Math.Clamp(state.PhysicalZoom, 1, Math.Max(16, CurrentFit))
            : GalleryGeometry.ClampZoom(state.PhysicalZoom, CurrentFit);
        _panX = state.PanX; _panY = state.PanY;
        FitIfAutomatic(); if (_manualZoom) _ = EnsureDetailAsync();
    }

    internal async Task ShowAsync(GalleryEntry entry, CancellationToken cancellationToken)
    {
        CancelLoads(); int request = ++_request;
        // Invalidate old image input before an asynchronous video stop can yield.
        _info = null; SetImageCommands(false); ClearBitmaps();
        _sessionCancellation = cancellationToken; _videoSuspended = false;
        if (_video != null)
        {
            await _video.StopAsync();
            if (request != _request || cancellationToken.IsCancellationRequested) return;
            _video.IsVisible = false;
        }
        if (entry.Kind != SavedMediaKind.Video)
        {
            UseNativeVideoOverlay(false);
            SetVideoFallbackLayout(false);
        }
        _image.IsVisible = _status.IsVisible = true;
        _readPosts.IsVisible = false;
        if (_entry?.Key != entry.Key) SetFavoriteError(null);
        _entry = entry; _manualZoom = _actualZoom = false; _panX = _panY = 0;
        UpdateInfoOverlayPosition();
        _exportOriginal.IsEnabled = true;
        SetFavorite(false);
        _ratingValue = null; _ratingError = null; _ratingAvailable = false;
        _ratingStatus.Text = string.Empty; _ratingButton.IsEnabled = false;
        _status.Text = Texts.T("gallery.loading"); _quality.Text = string.Empty; UpdateContext();
        if (entry.Kind == SavedMediaKind.Video)
        {
            if (_video == null)
            {
                _video = new GalleryVideoPlayer();
                _video.VolumeChanged += volume => { _videoVolume = volume; VideoVolumeChanged?.Invoke(volume); };
                _video.LayoutUpdated += (_, _) => UpdateInfoOverlayPosition();
                UseNativeVideoOverlay(true);
                _viewport.Children.Add(_video);
            }
            else UseNativeVideoOverlay(true);
            if (_nativeOverlayUnsupported) SetVideoFallbackLayout(true);
            _video.Configure(_videoAutoplay, _videoStartMuted, _videoVolume);
            _image.IsVisible = _status.IsVisible = false; _video.IsVisible = true;
            UpdateInfoOverlayPosition();
            if (_nativeOverlayUnsupported)
                Dispatcher.UIThread.Post(() => _nativeOverlayWindow?.Hide(), DispatcherPriority.Loaded);
            if (_nativeOverlayActive)
                Dispatcher.UIThread.Post(CheckNativeOverlayTransparency, DispatcherPriority.Loaded);
            await _video.ShowAsync(entry, cancellationToken);
            return;
        }
        if (entry.Kind == SavedMediaKind.Other) { _status.Text = Texts.T("gallery.fileOnly"); return; }
        await RequestDecodeAsync(CurrentFitRequest(), debounce: false);
    }

    private GalleryDecodeRequest.Fit CurrentFitRequest()
    {
        Size available = _viewport.Bounds.Size;
        if (available.Width <= 0 || available.Height <= 0)
            available = TopLevel.GetTopLevel(this)?.ClientSize ?? new Size(1280, 720);
        return new(available.Width * DisplayScale, available.Height * DisplayScale);
    }

    private Task EnsureDetailAsync()
    {
        if (_entry == null || _info == null || HasOriginal) return Task.CompletedTask;
        return RequestDecodeAsync(new GalleryDecodeRequest.Original(), debounce: false);
    }

    private bool HasOriginal => _highBitmap != null || (_info != null && _fitBitmap != null &&
        _fitBitmap.PixelSize.Width == _info.Width && _fitBitmap.PixelSize.Height == _info.Height);

    private Task RequestDecodeAsync(GalleryDecodeRequest decode, bool debounce, bool retry = false)
    {
        if (_entry == null) return Task.CompletedTask;
        // Automatic geometry can return to Fit while a manual Original is still
        // loading. Keep that explicit quality request ahead of resize upgrades.
        if (decode is GalleryDecodeRequest.Fit && debounce && _pendingOriginalGeneration.HasValue)
            return Task.CompletedTask;
        // Normalise known Fit requests to the capped target, so tiny viewport
        // changes and changes above the cap cannot queue identical work.
        if (decode is GalleryDecodeRequest.Fit fit && _info != null)
        {
            PixelSize target = GalleryFitTarget.Calculate(_info, fit);
            decode = new GalleryDecodeRequest.Fit(target.Width, target.Height);
            if (HasOriginal || (_fitBitmap != null && GalleryFitTarget.Covers(_fitBitmap.PixelSize, target)))
            {
                // A shrink can make the retained bitmap sufficient while an upgrade runs.
                if (_desiredDecode is GalleryDecodeRequest.Fit)
                { ++_decodeGeneration; _decodeQueue.Cancel(); _desiredDecode = null; }
                return Task.CompletedTask;
            }
        }
        if (!retry && decode == _desiredDecode) return Task.CompletedTask;
        _desiredDecode = decode;
        GalleryEntry entry = _entry;
        int session = _request, generation = ++_decodeGeneration;
        _pendingOriginalGeneration = decode is GalleryDecodeRequest.Original ? generation : null;
        CancellationToken sessionToken = _sessionCancellation;
        return _decodeQueue.Replace(async token =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
            try
            {
                (Bitmap bitmap, GalleryImageInfo info) = await DecodeImageAsync(entry, decode, linked.Token);
                if (session != _request || generation != _decodeGeneration || linked.IsCancellationRequested)
                { bitmap.Dispose(); return; }
                Bitmap? previous = decode is GalleryDecodeRequest.Original ? _highBitmap : _fitBitmap;
                if (decode is GalleryDecodeRequest.Original) _highBitmap = bitmap;
                else _fitBitmap = bitmap;
                _info = info;
                _image.Source = _highBitmap ?? bitmap;
                previous?.Dispose(); // Source is switched before its old backing bitmap is released.
                _status.Text = string.Empty;
                SetImageCommands(true);
                FitIfAutomatic();
                ApplyRestoredState();
                if (_manualZoom && !HasOriginal) _ = EnsureDetailAsync();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (session == _request && generation == _decodeGeneration)
                    _status.Text = (_image.Source != null ? Texts.T("viewer.decodeWarning") : string.Empty) + error.Message;
            }
            finally
            {
                if (_pendingOriginalGeneration == generation) _pendingOriginalGeneration = null;
            }
        }, debounce);
    }

    private void OnScaleOwnerChanged(object? sender, EventArgs change) => FitIfAutomatic();

    private void UpdateContext()
    {
        _context.Children.Clear(); if (_entry == null) return;
        GalleryEntry entry = _entry;
        Command(_context, "GalleryReturnImage", Texts.T("gallery.viewer7"), () => SetContextOpen(false));
        AddContext(entry.Key.Thread.Source + " /" + entry.Key.Thread.Board + "/ · " + entry.Key.Thread.Thread);
        AddContext(entry.Asset.OriginalFileName ?? entry.Asset.RequestedFileName);
        AddContext(entry.Asset.Length.ToString("N0", Texts.Culture) + " bytes");
        if (entry.Historical) AddContext(Texts.T("gallery.viewer8"));
        GalleryEntry[] associations = _associations.Where(value => value.Key == entry.Key && value.Post != null)
            .DistinctBy(value => (value.Post!.PostId, value.Attachment?.Position)).ToArray();
        if (associations.Length > 1)
        {
            ComboBox selector = new() { Name = "GalleryPostAssociation", HorizontalAlignment = HorizontalAlignment.Stretch,
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
    private void AddContext(string text) => _context.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, [!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text") });
    private void LayoutContext()
    {
        bool compact = Bounds.Width < 850;
        _contextPanel.IsVisible = _contextOpen; _viewport.IsVisible = !(_contextOpen && compact);
        _contextPanel.Width = compact ? double.NaN : 320;
        _contextPanel.Margin = _contextOpen && !_nativeOverlayFallback
            ? new Thickness(0, Math.Max(68, _chrome.Bounds.Height + 14), 0, Math.Max(58, _infoOverlay.Bounds.Height + 14))
            : default;
        Grid.SetColumn(_contextPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(_contextPanel, compact ? 2 : 1);
        UpdateInfoOverlayPosition();
        FitIfAutomatic();
    }
    private void UpdateInfoOverlayPosition()
    {
        double bottom = _entry?.Kind == SavedMediaKind.Video && _viewport.IsVisible &&
            !_nativeOverlayActive && !_nativeOverlayFallback && _video != null
            ? Math.Ceiling(_video.TransportBottomInset) + 10 : 10;
        // Video transport can lift the caption well above its usual bottom
        // reserve. Keep it inside the media column rather than across the post
        // context. Native video chrome already has its own media-sized window;
        // fallback chrome has a separate row, and compact context hides media.
        double right = 10;
        if (_contextOpen && _viewport.IsVisible && !_nativeOverlayActive && !_nativeOverlayFallback)
            right += Math.Max(0, _body.Bounds.Width - _viewport.Bounds.Right);
        Thickness margin = new(10, 0, right, bottom);
        if (_infoOverlay.Margin != margin) _infoOverlay.Margin = margin;
    }
    private void Move(int delta) { if (delta < 0 ? _previous.IsEnabled : _next.IsEnabled) MoveRequested?.Invoke(delta); }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        RevealChrome();
        if (e.Handled || ShortcutService.IsCapturing) return;
        Control routingOwner = sender is Control control ? control : this;
        if (!CommandRouter.IsRoutingContextActive(routingOwner, e.Source)) return;
        if (CommandBindings.Matches(e, "window.fullscreen")) { FullscreenRequested?.Invoke(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "viewer.shortcut-help"))
        { SetHelp(!_helpOpen); e.Handled = true; return; }
        if (_helpOpen) return;
        if ((ReferenceEquals(routingOwner, _nativeVideoOverlay) ? _nativeRatingRouter : _ratingRouter)?.Route(e) == true) return;
        if (e.Source is Visual input && input.GetVisualAncestors().Prepend(input).Any(control =>
            control is TextBox or ComboBox or Slider or TabControl or DataGrid or SelectableTextBlock)) return;
        if (CommandBindings.Matches(e, "favorites.toggle")) { ToggleFavorite(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.context-toggle")) { SetContextOpen(!_contextOpen); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.fit")) { Fit(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.actual-size")) { ActualPixels(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "video.play-pause") && _entry?.Kind == SavedMediaKind.Video && _video != null &&
            !(e.Source is Visual spaceSource && spaceSource.GetVisualAncestors().Prepend(spaceSource).Any(control => control is Button)))
        { _ = _video.TogglePlaybackAsync(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.previous")) Move(-1);
        else if (CommandBindings.Matches(e, "gallery.next")) Move(1);
        else if (CommandBindings.Matches(e, "gallery.zoom-in")) Zoom(1.1);
        else if (CommandBindings.Matches(e, "gallery.zoom-out")) Zoom(1 / 1.1);
        else if (CommandBindings.Matches(e, "gallery.close")) CloseRequested?.Invoke();
        else if (CommandBindings.Matches(e, "media.show-in-folder")) { if (_entry != null) ShowInFolderRequested?.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "media.details")) { if (_entry != null) DetailsRequested?.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "gallery.show-in-thread")) { if (_entry?.Post != null) ReadPostRequested?.Invoke(_entry); }
        else { CommandBindings.RouteVisibleActions(this, e); return; }
        e.Handled = true;
    }

    private void OnNativeOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _nativeVideoOverlay.ContextMenu?.IsOpen == true) return;
        if (e.Key == Key.Escape && e.Source is Visual source &&
            source.GetVisualAncestors().Prepend(source).OfType<ComboBox>().Any(combo => combo.IsDropDownOpen)) return;
        ShellKeyRequested?.Invoke(this, e);
    }
    private double DisplayScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    private void Zoom(double factor, Point? anchor = null)
    {
        if (_info == null) return;
        if (!double.IsFinite(factor) || factor <= 0) return;
        // The explicit 1:1 path can remain below Fit; move gently from 1:1 toward Fit.
        bool belowFitActual = _actualZoom && CurrentFit > 1 && _zoom < CurrentFit;
        if (belowFitActual && _zoom == 1 && factor < 1) return;
        double old = _zoom;
        _zoom = belowFitActual
            ? Math.Clamp(old * factor, 1, Math.Max(16, CurrentFit))
            : GalleryGeometry.ClampZoom(old * factor, CurrentFit);
        _actualZoom = belowFitActual && _zoom < CurrentFit;
        _manualZoom = _actualZoom || _zoom > CurrentFit * (1 + 1e-10);
        if (!_manualZoom) { Fit(retry: false); return; }
        Point offset = anchor is { } point ? new Point(point.X - _viewport.Bounds.Width / 2, point.Y - _viewport.Bounds.Height / 2) : default;
        _panX = offset.X - (offset.X - _panX) * _zoom / old;
        _panY = offset.Y - (offset.Y - _panY) * _zoom / old;
        ApplyTransform(); if (_manualZoom) _ = EnsureDetailAsync();
    }
    private double CurrentFit => _info == null ? 1 : GalleryGeometry.Fit(_info.Width, _info.Height,
        _viewport.Bounds.Width, _viewport.Bounds.Height, DisplayScale);
    private void ActualPixels() { if (_info == null) return; _zoom = 1; _manualZoom = _actualZoom = true; _panX = _panY = 0; ApplyTransform(); _ = EnsureDetailAsync(); }
    private void FitIfAutomatic()
    {
        if (_actualZoom && _zoom < CurrentFit) { ApplyTransform(); return; }
        if (!_manualZoom || _zoom <= CurrentFit) Fit(retry: false);
        else { _zoom = GalleryGeometry.ClampZoom(_zoom, CurrentFit); ApplyTransform(); }
    }
    private void Fit() => Fit(retry: true);
    private void Fit(bool retry)
    {
        if (_info == null) return;
        _manualZoom = _actualZoom = false; _panX = _panY = 0;
        _zoom = GalleryGeometry.Fit(_info.Width, _info.Height, _viewport.Bounds.Width, _viewport.Bounds.Height, DisplayScale);
        ApplyTransform();
        _ = RequestDecodeAsync(CurrentFitRequest(), debounce: !retry, retry: retry);
    }
    private void ApplyTransform()
    {
        if (_info == null) return;
        double scale = DisplayScale;
        _image.Width = _info.Width * _zoom / scale; _image.Height = _info.Height * _zoom / scale;
        ApplyPan();
        UpdateQuality();
    }
    private void ApplyPan()
    {
        if (_info == null) return;
        double scale = DisplayScale;
        _panX = GalleryGeometry.Pan(_panX, _info.Width, _zoom, scale, _viewport.Bounds.Width);
        _panY = GalleryGeometry.Pan(_panY, _info.Height, _zoom, scale, _viewport.Bounds.Height);
        _imageTranslation.X = _panX; _imageTranslation.Y = _panY;
    }
    private void UpdateQuality()
    {
        if (_info == null) return;
        _quality.Text = _info.Width + " × " + _info.Height + " · " + _info.Format + " · " +
            (_manualZoom ? (_zoom * 100).ToString("0", Texts.Culture) + "%" : Texts.T("gallery.fit")) +
            (_manualZoom && !HasOriginal ? Texts.T("gallery.viewer11") : string.Empty) +
            (_info.StillFrame ? Texts.T("gallery.viewer12") : string.Empty);
    }
    private void CancelLoads() { ++_decodeGeneration; _decodeQueue.Cancel(); _desiredDecode = null; _pendingOriginalGeneration = null; }
    private void ClearBitmaps() { _image.Source = null; _highBitmap?.Dispose(); _highBitmap = null; _fitBitmap?.Dispose(); _fitBitmap = null; }
    internal Task SuspendMediaAsync()
    {
        _videoSuspended = _entry?.Kind == SavedMediaKind.Video;
        return _videoSuspended && _video != null ? _video.StopAsync() : Task.CompletedTask;
    }
    internal Task ResumeMediaAsync()
    {
        if (!_videoSuspended || _entry == null || _video == null) return Task.CompletedTask;
        _videoSuspended = false;
        return _video.ShowAsync(_entry, _sessionCancellation);
    }
    internal async Task StopAsync()
    {
        ++_request; CancelLoads(); ClearBitmaps(); _entry = null; _info = null; _restore = null;
        UpdateInfoOverlayPosition();
        _exportOriginal.IsEnabled = false;
        _associations = Array.Empty<GalleryEntry>(); _contextOpen = false; _videoSuspended = false; SetHelp(false); LayoutContext(); UpdateContextButton();
        if (_video != null) await _video.DisposeAsync();
        UseNativeVideoOverlay(false);
        SetVideoFallbackLayout(false);
    }
}

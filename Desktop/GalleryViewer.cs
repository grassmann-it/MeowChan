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
using Avalonia.Styling;
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
    private readonly Image _image = new() { Name = "GalleryImage", Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _viewport = new() { Name = "GalleryViewport", ClipToBounds = true, Background = Brushes.Black };
    private readonly TextBlock _status = new() { Name = "GalleryStatus", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, IsHitTestVisible = false };
    private readonly TextBlock _scope = new() { Name = "GalleryScope", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _warnings = new() { Name = "GalleryWarnings", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _favoriteError = new() { Name = "GalleryFavoriteError", Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap, Focusable = true, IsVisible = false };
    private readonly Button _readPosts;
    private readonly TextBlock _position = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _quality = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _context = new() { Spacing = 12 };
    private readonly Border _contextPanel = new() { Name = "GalleryContext", Padding = new Thickness(18), IsVisible = false, Background = new SolidColorBrush(Color.FromRgb(32, 35, 42)) };
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
    private readonly Border _helpPanel = new() { Name = "GalleryShortcutHelp", Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(32, 35, 42)), IsVisible = false };
    private readonly StackPanel _helpContent = new() { Spacing = 6 };
    private readonly Border _chrome = new() { Name = "GalleryOverlayControls", Padding = new Thickness(8),
        Margin = new Thickness(10, 10, 10, 0), CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.FromArgb(224, 21, 25, 32)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _infoOverlay = new() { Name = "GalleryOverlayInfo", Padding = new Thickness(8, 5),
        Margin = new Thickness(10, 0, 10, 10), CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.FromArgb(208, 21, 25, 32)),
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border _statusPlate = new() { Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(7),
        Background = new SolidColorBrush(Color.FromArgb(224, 21, 25, 32)),
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
        Focusable = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        Background = new SolidColorBrush(Color.FromRgb(13, 16, 22));
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
        _exportOriginal = Command(toolbar, "GalleryExportOriginal", Texts.T("viewer.menu.exportOriginal"), () =>
        { if (_entry != null) ExportOriginalRequested?.Invoke(_entry); });
        Command(toolbar, "GalleryFullscreen", Texts.T("viewer.fullscreen"), () => FullscreenRequested?.Invoke());
        _helpButton = Command(toolbar, "GalleryHelp", Texts.T("viewer.help"), () => SetHelp(!_helpOpen));
        _readPosts = Command(toolbar, "GalleryReadPosts", Texts.T("gallery.read"), () => ReadPostsRequested?.Invoke());
        _readPosts.IsVisible = false;
        // The overlay is permanently dark, including in a light workspace.
        // Keep Semi's native control states in the matching local theme.
        _chrome.Child = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = toolbar };
        StackPanel infoContent = new() { Spacing = 2 };
        Grid infoHeadline = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        infoHeadline.Children.Add(_quality);
        infoHeadline.Children.Add(_position); Grid.SetColumn(_position, 1);
        infoContent.Children.Add(infoHeadline);
        infoContent.Children.Add(_scope);
        infoContent.Children.Add(_warnings);
        infoContent.Children.Add(_favoriteError);
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
        _contextPanel.Child = new ScrollViewer { Content = _context, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
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
        _viewport.SizeChanged += (_, _) => FitIfAutomatic();
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
            _panX += point.X - _dragPoint.X; _panY += point.Y - _dragPoint.Y; _dragPoint = point; ApplyTransform();
        };
        _viewport.DoubleTapped += (_, e) => { if (_manualZoom) Fit(); else ActualPixels(); e.Handled = true; };
        _viewport.ContextRequested += (_, e) =>
        {
            if (e.Source is Visual input && input.GetVisualAncestors().Prepend(input).Any(control => control is TextBox or ComboBox or Slider)) return;
            if (_entry != null) { ShowMediaMenu(); e.Handled = true; }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AttachedToVisualTree += (_, _) => { Texts.Changed += OnLanguageChanged; RevealChrome(); };
        DetachedFromVisualTree += (_, _) => { Texts.Changed -= OnLanguageChanged; _chromeTimer.Stop(); };
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

    private void UpdateHelpButtonTooltip()
    {
        string tip = _showShortcutHelp ? Texts.T("viewer.help.hint") : CommandBindings.Tooltip("viewer.shortcut-help");
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
        ToolTip.SetTip(_favorite, label + " · F");
        _favorite.IsEnabled = _entry != null;
    }

    private void UpdateContextButton()
    {
        string label = Texts.T(_contextOpen ? "viewer.context.hide" : "viewer.context.show");
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
        _helpContent.Children.Add(new TextBlock { Text = Texts.T("viewer.help.title"), Foreground = Brushes.White, FontWeight = FontWeight.Bold });
        string[] keys = _helpExpanded
            ? new[] { "viewer.help.move", "viewer.help.zoom", "viewer.help.favorite", "viewer.help.context", "viewer.help.video", "viewer.help.fullscreen", "viewer.help.escape" }
            : new[] { "viewer.help.move", "viewer.help.zoom", "viewer.help.favorite", "viewer.help.context" };
        foreach (string key in keys)
            _helpContent.Children.Add(new TextBlock { Text = Texts.T(key), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
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
        AutomationProperties.SetHelpText(favorite, favorite.Header + " · F");
        ToolTip.SetTip(favorite, favorite.Header + " · F");
        ContextMenu menu = new() { ItemsSource = new object[]
        {
            favorite,
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
        CancelLoads(); int request = ++_request; ClearBitmaps();
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
        _entry = entry; _info = null; _manualZoom = _actualZoom = false; _panX = _panY = 0;
        UpdateInfoOverlayPosition();
        _exportOriginal.IsEnabled = true;
        SetFavorite(false);
        _status.Text = Texts.T("gallery.loading"); _quality.Text = string.Empty; SetImageCommands(false); UpdateContext();
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
            _fitBitmap = bitmap; _info = info; _image.Source = bitmap; _status.Text = string.Empty; SetImageCommands(true);
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
        Command(_context, "GalleryReturnImage", Texts.T("gallery.viewer7"), () => SetContextOpen(false));
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
        if (_infoOverlay.Margin.Bottom == bottom) return;
        _infoOverlay.Margin = new Thickness(10, 0, 10, bottom);
    }
    private void Move(int delta) { if (delta < 0 ? _previous.IsEnabled : _next.IsEnabled) MoveRequested?.Invoke(delta); }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        RevealChrome();
        if (e.Handled) return;
        if (e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.None) { FullscreenRequested?.Invoke(); e.Handled = true; return; }
        if (e.Key == Key.F1 || e.KeySymbol == "?" || (e.Key == Key.OemQuestion && e.KeyModifiers == KeyModifiers.Shift))
        { SetHelp(!_helpOpen); e.Handled = true; return; }
        if (e.KeyModifiers != KeyModifiers.None && !(e.KeyModifiers == KeyModifiers.Shift && (e.Key == Key.OemPlus || e.KeySymbol == "+"))) return;
        if (e.Source is Visual input && input.GetVisualAncestors().Prepend(input).Any(control =>
            control is TextBox or ComboBox or Slider or TabControl or DataGrid or SelectableTextBlock)) return;
        if (e.Key == Key.F) { ToggleFavorite(); e.Handled = true; return; }
        if (e.Key == Key.P) { SetContextOpen(!_contextOpen); e.Handled = true; return; }
        if (e.Key is Key.D0 or Key.NumPad0 || e.KeySymbol == "0") { Fit(); e.Handled = true; return; }
        if (e.Key is Key.D1 or Key.NumPad1 || e.KeySymbol == "1") { ActualPixels(); e.Handled = true; return; }
        if (e.Key == Key.Space && _entry?.Kind == SavedMediaKind.Video && _video != null &&
            !(e.Source is Visual spaceSource && spaceSource.GetVisualAncestors().Prepend(spaceSource).Any(control => control is Button)))
        { _ = _video.TogglePlaybackAsync(); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Left: Move(-1); break;
            case Key.Right: Move(1); break;
            case Key.Add: case Key.OemPlus: Zoom(1.1); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.1); break;
            default: return;
        }
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
        if (!_manualZoom) { _zoom = CurrentFit; _panX = _panY = 0; ApplyTransform(); return; }
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
        if (!_manualZoom || _zoom <= CurrentFit) Fit();
        else { _zoom = GalleryGeometry.ClampZoom(_zoom, CurrentFit); ApplyTransform(); }
    }
    private void Fit()
    {
        if (_info == null) return;
        _manualZoom = _actualZoom = false; _panX = _panY = 0;
        _zoom = GalleryGeometry.Fit(_info.Width, _info.Height, _viewport.Bounds.Width, _viewport.Bounds.Height, DisplayScale);
        ApplyTransform();
        // A larger viewport can require more pixels than the initial fit decode supplied.
        if (_fitBitmap != null && _fitBitmap.PixelSize.Width < _info.Width && (_info.Width * _zoom > _fitBitmap.PixelSize.Width + 1
            || _info.Height * _zoom > _fitBitmap.PixelSize.Height + 1)) _ = EnsureDetailAsync();
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

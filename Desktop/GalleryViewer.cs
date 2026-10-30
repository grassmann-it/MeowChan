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
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

internal sealed record GalleryViewerState(GalleryAssetKey? Key, string? PostId, int? AttachmentPosition,
    bool ManualZoom, double PhysicalZoom, double PanX, double PanY, bool ContextOpen, bool ActualZoom);

// A frozen mapping revision owns only data, never controls. Navigation uses an O(1) asset lookup;
// the existing post/attachment-position identity and first-occurrence order remain unchanged.
internal sealed class GalleryAssociationIndex
{
    private readonly Dictionary<GalleryAssetKey, GalleryEntry[]> _entries;
    internal long Revision { get; }
    internal GalleryAssociationIndex(IReadOnlyList<GalleryEntry> entries, long revision)
    {
        Revision = revision;
        _entries = entries.Where(entry => entry.Post != null).GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group
                .DistinctBy(entry => (entry.Post!.PostId, entry.Attachment?.Position)).ToArray());
    }
    internal IReadOnlyList<GalleryEntry> For(GalleryAssetKey key) =>
        _entries.TryGetValue(key, out GalleryEntry[]? entries) ? Array.AsReadOnly(entries) : Array.Empty<GalleryEntry>();
}

// A diagnostic is data for the owning shell, never an instruction to the transport.
internal sealed record GalleryDiagnosticNotice(string Operation, GalleryEntry? Entry, long Revision,
    string Message, bool MediaUnavailable);

internal sealed partial class GalleryViewer : UserControl
{
    private readonly TranslateTransform _imageTranslation = new();
    private readonly Image _image = new() { Name = "GalleryImage", Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _viewport = new() { Name = "GalleryViewport", ClipToBounds = true, Background = Brushes.Black };
    private readonly TextBlock _status = new() { Name = "GalleryStatus", TextWrapping = TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), IsHitTestVisible = false };
    private readonly TextBlock _scope = new() { Name = "GalleryScope", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _provenance = new() { Name = "GalleryProvenance", [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _warnings = new() { Name = "GalleryWarnings", [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _favoriteError = new() { Name = "GalleryFavoriteError", Classes = { "workspace-error" },
        TextWrapping = TextWrapping.Wrap, Focusable = true, IsVisible = false };
    private readonly Button _readPosts;
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _compactPosition = new() { Name = "GalleryCompactPosition", VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly List<Control> _secondaryTools = new();
    private readonly ToggleButton _toolsToggle;
    private bool _mediaFocus;
    private readonly TextBlock _quality = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _context = new() { Spacing = 12 };
    private readonly Border _contextPanel = new() { Name = "GalleryContext", Classes = { "workspace-section" }, IsVisible = false };
    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
    private readonly Grid _visualRoot = new() { Background = Brushes.Transparent, ClipToBounds = true, RowDefinitions = new RowDefinitions("*,Auto,Auto") };
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
    private readonly Border _helpPanel = new() { Name = "GalleryShortcutHelp", Classes = { "workspace-section" }, IsVisible = false };
    private readonly StackPanel _helpContent = new() { Spacing = 6 };
    private readonly Border _chrome = new() { Name = "GalleryOverlayControls", Classes = { "workspace-media-tools" },
        Margin = new Thickness(8, 8, 8, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border _infoOverlay = new() { Name = "GalleryOverlayInfo", Classes = { "workspace-media-caption" },
        Margin = new Thickness(8, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border _statusPlate = new() { Classes = { "workspace-media-caption" },
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 650, IsHitTestVisible = false, IsVisible = false };
    private GalleryVideoPlayer? _video;
    // Retiring video identity survives a new selection while native cleanup still owns it.
    private GalleryEntry? _videoDiagnosticEntry;
    private long _videoDiagnosticRevision;
    private bool _videoSuspended;
    private bool _nativeOverlayActive;
    private bool _nativeOverlayFallback;
    private bool _nativeOverlayUnsupported;
    private Window? _nativeOverlayWindow;
    private Window? _nativeShortcutWindow;
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
    private readonly long _diagnosticEpoch = PerformanceDiagnostics.NextId();
    private long _navigationId;
    private GalleryViewerState? _restore;
    private GalleryAssociationIndex? _associationIndex;
    private long _associationIndexBuilds;
    internal long AssociationIndexBuildCount => _associationIndexBuilds;
    internal long AssociationRevision => _associationIndex?.Revision ?? 0;

    internal event Action<GalleryDiagnosticNotice>? DiagnosticNotice;
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
    private readonly TextBlock _ratingStatus = new() { Name = "GalleryRatingStatus", TextWrapping = TextWrapping.Wrap };
    internal void SetRating(int? value, string? error, bool available = true)
    {
        bool changedError = _ratingError != error;
        _ratingValue = value; _ratingError = error; _ratingAvailable = available;
        _ratingStatus.Text = MediaRatingText.Label + ": " + MediaRatingText.Value(value);
        if (changedError) ReportDiagnostic("rating", error);
        _ratingButton.IsEnabled = available && _entry != null;
        UpdateMediaFocusVisibility();
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
        _video?.Configure(EffectiveVideoAutoplay, _videoStartMuted, _videoVolume);
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
        // Keep failed-write state in the owning model, not an inline error/focus target.
        _favoriteError.Text = null;
        _favoriteError.IsVisible = false;
        ReportDiagnostic("favorite", error);
    }

    // Compatible fallback for callers that do not track revisions: each call supplies a new snapshot.
    internal void SetAssociations(IReadOnlyList<GalleryEntry> entries)
    {
        PauseSlideshow(); // A replacement mapping is not permission to navigate an old selection.
        SetAssociationIndex(entries, AssociationRevision + 1);
        if (_entry is { } current)
            _entry = _associationIndex!.For(current.Key).FirstOrDefault(value => value.EntryKey == current.EntryKey) ?? current;
        UpdateContext();
    }

    private void SetAssociationIndex(IReadOnlyList<GalleryEntry> entries, long revision)
    {
        if (_associationIndex?.Revision == revision) return;
        using var measurement = PerformanceDiagnostics.Measure("gallery.association-index", epoch: _diagnosticEpoch, revision: revision);
        _associationIndex = new GalleryAssociationIndex(entries, revision);
        ++_associationIndexBuilds;
    }

    // Entry and mapping become current in the same synchronous admission. In particular an open
    // context is never rebuilt for the previous entry against the next entry's mapping.
    internal Task ShowAsync(GalleryEntry entry, IReadOnlyList<GalleryEntry> associations,
        long revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetAssociationIndex(associations, revision);
        return ShowAsync(entry, cancellationToken);
    }

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
        WrapPanel toolbar = new() { Classes = { "workspace-toolbar" }, HorizontalAlignment = HorizontalAlignment.Center };
        StackPanel ToolGroup(string name)
        {
            StackPanel group = new() { Name = name, Classes = { "workspace-tool-group" } };
            toolbar.Children.Add(group);
            return group;
        }
        StackPanel navigation = ToolGroup("GalleryNavigationTools");
        StackPanel zoom = ToolGroup("GalleryZoomTools");
        StackPanel media = ToolGroup("GalleryMediaTools");
        StackPanel view = ToolGroup("GalleryViewTools");
        _close = Command(navigation, "GalleryClose", Texts.T("gallery.viewer0"), () => CloseRequested?.Invoke());
        _previous = Command(navigation, "GalleryPrevious", Texts.T("gallery.viewer1"), () => Move(-1));
        navigation.Children.Add(_compactPosition);
        _next = Command(navigation, "GalleryNext", Texts.T("gallery.viewer2"), () => Move(1));
        _fit = Command(zoom, "GalleryFit", Texts.T("gallery.fit"), Fit);
        _actual = Command(zoom, "GalleryActual", "1:1", ActualPixels);
        _zoomOut = Command(zoom, "GalleryMinus", "−", () => Zoom(1 / 1.1));
        _zoomIn = Command(zoom, "GalleryPlus", "+", () => Zoom(1.1));
        _contextToggle = (ToggleButton)Command(media, "GalleryToggleContext", Texts.T("gallery.context"), () => SetContextOpen(!_contextOpen));
        _favorite = (ToggleButton)Command(media, "GalleryFavorite", Texts.T("viewer.favorite.add"), ToggleFavorite);
        CommandBindings.Apply(_ratingButton, "media.rating.edit");
        _ratingButton.Click += (_, _) => { if (_entry != null) RatingEditRequested?.Invoke(_entry); };
        media.Children.Add(_ratingButton);
        _exportOriginal = Command(media, "GalleryExportOriginal", Texts.T("viewer.menu.exportOriginal"), () =>
        { if (_entry != null) ExportOriginalRequested?.Invoke(_entry); });
        Command(view, "GalleryFullscreen", Texts.T("viewer.fullscreen"), () => FullscreenRequested?.Invoke());
        _helpButton = Command(view, "GalleryHelp", Texts.T("viewer.help"), () => SetHelp(!_helpOpen));
        _readPosts = Command(view, "GalleryReadPosts", Texts.T("gallery.read"), () => { PauseSlideshow(); ReadPostsRequested?.Invoke(); });
        _readPosts.IsVisible = false;
        _secondaryTools.AddRange(new Control[] { zoom, media, view });
        StackPanel focusTools = ToolGroup("GalleryFocusTools");
        _toolsToggle = (ToggleButton)Command(focusTools, "GalleryToolsToggle", string.Empty, () => SetMediaFocus(!_mediaFocus));
        InitializePlaybackControls(toolbar, focusTools);
        InitializeGifControls(focusTools);
        UpdateMediaFocusPresentation();
        // Reserve navigation below the picture; secondary tools collapse only on request.
        // The media viewport keeps its black matte; the footer follows the host theme.
        _chrome.Child = toolbar;
        foreach (TextBlock text in new[] { _quality, _position, _compactPosition, _scope, _ratingStatus })
            text.Classes.Add("workspace-meta");
        StackPanel infoContent = new() { Spacing = 2 };
        Grid infoHeadline = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        infoHeadline.Children.Add(_quality);
        infoHeadline.Children.Add(_position); Grid.SetColumn(_position, 1);
        infoContent.Children.Add(infoHeadline);
        infoContent.Children.Add(_scope);
        infoContent.Children.Add(_provenance);
        infoContent.Children.Add(_warnings);
        infoContent.Children.Add(_favoriteError);
        infoContent.Children.Add(_ratingStatus);
        infoContent.Children.Add(_overlayFallbackNotice);
        infoContent.Children.Add(_slideshowStatus);
        AutomationProperties.SetLiveSetting(_favoriteError, AutomationLiveSetting.Assertive);
        _infoOverlay.Child = infoContent;
        _helpPanel.Child = new ScrollViewer { Content = _helpContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _helpPanel.HorizontalAlignment = HorizontalAlignment.Right;
        _helpPanel.VerticalAlignment = VerticalAlignment.Top;
        _helpPanel.Margin = new Thickness(12);
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
        root.Children.Add(_chrome); Grid.SetRow(_chrome, 1);
        root.Children.Add(_infoOverlay); Grid.SetRow(_infoOverlay, 2);
        root.Children.Add(_helpPanel);
        Content = root;
        InitializeOverlayInteraction();
        _nativeVideoOverlay.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _nativeVideoOverlay.AddHandler(KeyDownEvent, OnNativeOverlayKeyDown, RoutingStrategies.Bubble);
        _nativeVideoOverlay.ContextRequested += (_, e) =>
        {
            if (e.Source is Visual input && input.GetVisualAncestors().Prepend(input).Any(control => control is TextBox or ComboBox or Slider)) return;
            if (_entry != null) { ShowMediaMenu(); e.Handled = true; }
        };
        SizeChanged += (_, _) =>
        {
            _chrome.MaxWidth = Math.Max(0, Bounds.Width - 16);
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
            _viewerInteractionStopped = false;
            if (_scaleOwner != null) _scaleOwner.ScalingChanged += OnScaleOwnerChanged;
            ApplyOverlayPlacement();
            UpdateViewerInteractionTimer();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Texts.Changed -= OnLanguageChanged;
            ShortcutService.Changed -= OnShortcutBindingsChanged;
            if (_scaleOwner != null) _scaleOwner.ScalingChanged -= OnScaleOwnerChanged;
            _scaleOwner = null;
            StopViewerInteraction();
        };
        UpdateFavorite();
        _exportOriginal.IsEnabled = false;
        UpdateContextButton();
        UpdateHelp();
        UpdateHelpButtonTooltip();
        SetImageCommands(false);
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
        UpdateContext(); UpdateQuality(); UpdateFavorite(); UpdateContextButton(); UpdateHelp(); UpdateMediaFocusPresentation();
        RefreshPlaybackControls();
        RefreshGifTransport();
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
        "GalleryToolsToggle" => "gallery.tools-toggle",
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
        Button button = name is "GalleryToggleContext" or "GalleryFavorite" or "GalleryToolsToggle"
            ? new ToggleButton { Name = name }
            : new Button { Name = name };
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
    {
        _position.Text = _compactPosition.Text = label; _previous.IsEnabled = previous; _next.IsEnabled = next;
        UpdateSlideshowAvailability(next);
    }
    internal void SetScope(string message) => _scope.Text = message;
    internal void SetWarnings(string message)
    {
        _warnings.Text = null;
        ReportDiagnostic("source-warning", message);
    }
    internal Task SetEmpty(string message, bool canReadPosts)
    {
        if (_viewerInteractionStopped || _sessionCancellation.IsCancellationRequested) return Task.CompletedTask;
        Task cleanup = SetMediaUnavailable(message);
        _readPosts.IsVisible = canReadPosts; _exportOriginal.IsEnabled = false;
        SetPosition(string.Empty, false, false);
        return cleanup;
    }
    internal void SetLoadingNotice(string message) => _status.Text = message;
    // Compatibility entry for informational/operation failures. Actual failed media must
    // use SetMediaUnavailable, never infer failure from the presence of notification text.
    internal void SetNotice(string message) => ReportDiagnostic("notice", message);

    internal int MediaRequestGeneration => _request;

    // Capture expectedRequestGeneration before asynchronous shell resolution. Admission
    // and native retirement happen synchronously; only the guarded UI teardown awaits.
    internal Task SetMediaUnavailable(string diagnosis, int? expectedRequestGeneration = null)
    {
        if (_viewerInteractionStopped || _sessionCancellation.IsCancellationRequested
            || expectedRequestGeneration is int expected && expected != _request) return Task.CompletedTask;
        int failureRequest = ++_request;
        CancelLoads(); UnbindVideoPlaybackSignals();
        _mediaFailed = true; _navigationPending = false;
        _imageReadyRequest = _videoReadyRequest = -1;
        SetImageCommands(false); _exportOriginal.IsEnabled = false;
        _info = null; ClearBitmaps();
        StopSlideshow();
        _status.Text = GalleryViewerText.Get("Media unavailable. Details are available in diagnostics.",
            "Medium nicht verfügbar. Details stehen in der Diagnose.");
        _status.IsVisible = true;
        GalleryVideoPlayer? retiring = _video;
        Task cleanup = Task.WhenAll(RetireGifAsync(), retiring?.StopUnavailableAsync() ?? Task.CompletedTask);
        ReportDiagnostic("media-unavailable", diagnosis, mediaUnavailable: true);
        return CompleteUnavailableAsync(retiring, cleanup, failureRequest);
    }

    private async Task CompleteUnavailableAsync(GalleryVideoPlayer? retiring, Task cleanup, int failureRequest)
    {
        // Failure propagates to the owning shell. Never detach a drawable or conceal
        // retained native ownership merely because its cleanup task faulted.
        await cleanup;
        if (failureRequest != _request || _viewerInteractionStopped || _sessionCancellation.IsCancellationRequested) return;
        if (retiring != null) retiring.IsVisible = false;
        _image.IsVisible = true;
        UseNativeVideoOverlay(false);
        SetVideoFallbackLayout(false);
        _status.IsVisible = true;
    }

    private void ReportDiagnostic(string operation, string? message, bool mediaUnavailable = false)
    {
        if (_viewerInteractionStopped || _sessionCancellation.IsCancellationRequested || string.IsNullOrWhiteSpace(message)) return;
        string bounded = message.Length <= 2048 ? message : message[..2048];
        DiagnosticNotice?.Invoke(new(operation, _entry, AssociationRevision, bounded, mediaUnavailable));
    }
    private void SetImageCommands(bool enabled) => _fit.IsEnabled = _actual.IsEnabled = _zoomIn.IsEnabled = _zoomOut.IsEnabled = enabled;

    private void SetMediaFocus(bool enabled, bool notify = true)
    {
        if (_mediaFocus == enabled) return;
        // A disappearing control must never retain keyboard focus. Navigation and
        // the reveal toggle remain in the same visual tree, including native video.
        if (enabled && (_secondaryTools.Any(control => control.IsKeyboardFocusWithin) || _infoOverlay.IsKeyboardFocusWithin))
            _toolsToggle.Focus(_keyboardInteraction ? NavigationMethod.Tab : NavigationMethod.Pointer);
        _mediaFocus = enabled;
        _playbackPreferences = _playbackPreferences with { MediaFocus = enabled };
        UpdateMediaFocusPresentation();
        RevealOverlayControls();
        if (notify) PlaybackPreferencesChanged?.Invoke(_playbackPreferences);
    }

    private void UpdateMediaFocusPresentation()
    {
        CommandBindings.ApplyLabeled(_toolsToggle, "gallery.tools-toggle",
            Texts.T(_mediaFocus ? "viewer.tools.show" : "viewer.tools.hide"), 14);
        _toolsToggle.IsChecked = _mediaFocus;
        UpdateMediaFocusVisibility();
    }

    private void UpdateMediaFocusVisibility()
    {
        foreach (Control control in _secondaryTools) control.IsVisible = !_mediaFocus;
        // The legacy action remains routable, but must never reappear as duplicate chrome.
        _autoHideToggle.IsVisible = false;
        _compactPosition.IsVisible = _mediaFocus;
        _quality.IsVisible = _position.IsVisible = _scope.IsVisible = !_mediaFocus;
        _provenance.IsVisible = !_mediaFocus && !string.IsNullOrEmpty(_provenance.Text);
        _ratingStatus.IsVisible = !_mediaFocus;
        // Diagnostic notices have no visibility or auto-hide authority. The capability
        // fallback is neutral layout information, not a dismissible technical banner.
        _infoOverlay.IsVisible = !_mediaFocus || _slideshowStatus.IsVisible;
        _chrome.Margin = new Thickness(8, 8, 8, _infoOverlay.IsVisible ? 0 : 8);
    }

    private void SetContextOpen(bool open, bool notify = true, bool hover = false)
    {
        bool hadFocus = _contextPanel.IsKeyboardFocusWithin;
        _hoverContextOpen = hover && open;
        if (!hover && !open && notify) _suppressPostHoverUntilExit = true;
        if (_contextOpen == open) return;
        _contextOpen = open;
        // Deliberate reading stops automatic progression until explicit restart;
        // opening a panel is never permission to pause the native video itself.
        if (open) PauseSlideshow(pauseVideo: false);
        if (!hover) RevealOverlayControls();
        EnsureContextCurrent();
        LayoutContext();
        UpdateContextButton();
        RefreshPlaybackControls();
        if (!open && (!hover || hadFocus)) FocusGallerySurface();
        if (notify && !hover) ContextOpenChanged?.Invoke(open);
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
        bool wasOpen = _helpOpen;
        _helpOpen = open;
        if (open) HoldSlideshowAdvance();
        RevealOverlayControls();
        _helpExpanded = expanded;
        UpdateHelp();
        RefreshPlaybackControls();
        if (!open && wasOpen) FocusGallerySurface();
    }

    private void UpdateHelp()
    {
        _helpPanel.IsVisible = _helpOpen;
        _helpContent.Children.Clear();
        if (!_helpOpen) return;
        _helpContent.Children.Add(new TextBlock { Text = Texts.T("viewer.help.title"), Classes = { "section-title" } });
        string[] ids = _helpExpanded
            ? new[] { "gallery.previous", "gallery.next", "gallery.tools-toggle", "gallery.fit", "gallery.actual-size", "gallery.zoom-in", "gallery.zoom-out", "favorites.toggle", "gallery.context-toggle", "video.play-pause", "gallery.slideshow-toggle", "gallery.overlay-toggle", "gallery.post-overlay-toggle", "window.fullscreen", "gallery.close" }
            : new[] { "gallery.previous", "gallery.next", "gallery.fit", "favorites.toggle", "gallery.context-toggle", "gallery.slideshow-toggle" };
        foreach (string id in ids.Concat(CommandCatalog.All.Where(command => command.IsImplemented
            && command.ExecutionContexts.Contains("gallery") && command.ShortcutDisplay != null).Select(command => command.Id)).Distinct())
            _helpContent.Children.Add(new TextBlock { Text = CommandBindings.Tooltip(id), [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap });
        _helpContent.Children.Add(new TextBlock { Text = Texts.T("viewer.help.escape"), Classes = { "workspace-help" } });
        WrapPanel actions = new() { Classes = { "workspace-toolbar" } };
        if (!_helpExpanded) Command(actions, "GalleryHelpMore", Texts.T("viewer.help.more"), () => SetHelp(true, true));
        Command(actions, "GalleryHelpClose", Texts.T("action.close"), () => SetHelp(false));
        _helpContent.Children.Add(actions);
    }

    private void ShowMediaMenu()
    {
        HoldSlideshowAdvance();
        RevealOverlayControls();
        if (_entry == null) return;
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
            Item("gallery.show-in-thread", () => { PauseSlideshow(); ReadPostRequested?.Invoke(entry); }, entry.Post != null),
            Item("media.details", () => DetailsRequested?.Invoke(entry))
        } };
        Control owner = _nativeOverlayActive ? _nativeVideoOverlay : _viewport;
        menu.Opened += (_, _) => RefreshPlaybackControls();
        menu.Closed += (_, _) => RefreshPlaybackControls();
        owner.ContextMenu = menu;
        menu.Open(owner);
    }

    private void UseNativeVideoOverlay(bool active)
    {
        active &= OperatingSystem.IsWindows() && !_nativeOverlayUnsupported;
        if (!active) UnbindNativeShortcutWindow();
        if (_nativeOverlayActive == active) return;
        Grid previous = _nativeOverlayActive ? _nativeVideoOverlay : _visualRoot;
        Grid next = active ? _nativeVideoOverlay : _visualRoot;
        foreach (Control control in new Control[] { _helpPanel })
        {
            previous.Children.Remove(control);
            next.Children.Add(control);
        }
        _nativeOverlayActive = active;
        _video?.SetNativeOverlay(active ? _nativeVideoOverlay : null);
        ApplyOverlayPlacement();
    }

    private void CheckNativeOverlayTransparency()
    {
        if (!_nativeOverlayActive || TopLevel.GetTopLevel(_nativeVideoOverlay) is not Window overlay) return;
        BindNativeShortcutWindow(overlay);
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
        _visualRoot.RowDefinitions = new RowDefinitions(fallback ? "Auto,*,Auto,Auto" : "*,Auto,Auto");
        Grid.SetRow(_helpPanel, 0);
        Grid.SetRow(_body, fallback ? 1 : 0);
        Grid.SetRow(_chrome, fallback ? 2 : 1);
        Grid.SetRow(_infoOverlay, fallback ? 3 : 2);
        _helpPanel.Margin = new Thickness(12);
        ApplyOverlayPlacement();
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
        _restore = null; _contextOpen = state.ContextOpen;
        if (_contextOpen) HoldSlideshowAdvance();
        EnsureContextCurrent(); LayoutContext(); UpdateContextButton();
        UpdateLayout(); // Restored zoom uses the viewport after the context panel has been measured.
        _manualZoom = state.ManualZoom; _actualZoom = state.ActualZoom;
        _zoom = _actualZoom && state.PhysicalZoom < CurrentFit
            ? Math.Clamp(state.PhysicalZoom, 1, Math.Max(16, CurrentFit))
            : GalleryGeometry.ClampZoom(state.PhysicalZoom, CurrentFit);
        _panX = state.PanX; _panY = state.PanY;
        FitIfAutomatic(); if (_manualZoom) _ = EnsureDetailAsync();
    }

    internal Task ShowAsync(GalleryEntry entry, CancellationToken cancellationToken)
        => ShowMediaAsync(entry, cancellationToken);

    private async Task ShowMediaAsync(GalleryEntry entry, CancellationToken cancellationToken, bool? resumeAutoplay = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A retiring/disabled transport otherwise leaves FocusManager with no target;
        // subsequent keys then originate on Window and never tunnel through the viewer.
        if (_video?.IsKeyboardFocusWithin == true || _video?.TransportControl.IsKeyboardFocusWithin == true
            || _gifPlayPause.IsKeyboardFocusWithin)
            FocusGallerySurface();
        CancelLoads(); int request = ++_request;
        Task gifRetired = RetireGifAsync();
        BeginSlideshowMedia(entry);
        _navigationId = PerformanceDiagnostics.NextId();
        PerformanceDiagnostics.Record("gallery.navigation", "accepted", _navigationId, asset: entry.Asset.AssetId, epoch: _diagnosticEpoch, revision: AssociationRevision);
        // Invalidate old image input before an asynchronous video stop can yield.
        _info = null; SetImageCommands(false); ClearBitmaps();
        _sessionCancellation = cancellationToken; _videoSuspended = false;
        if (_entry?.Key != entry.Key) SetFavoriteError(null);
        _entry = entry; _manualZoom = _actualZoom = false; _panX = _panY = 0;
        _exportOriginal.IsEnabled = true;
        SetFavorite(false);
        _ratingValue = null; _ratingError = null; _ratingAvailable = false;
        _ratingStatus.Text = string.Empty; _ratingButton.IsEnabled = false;
        _status.Text = Texts.T("gallery.loading"); _quality.Text = string.Empty; UpdateContext();
        await gifRetired;
        if (request != _request || cancellationToken.IsCancellationRequested) return;
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
        if (entry.Kind == SavedMediaKind.Video)
        {
            if (_video == null)
            {
                _video = new GalleryVideoPlayer();
                _video.CleanupDiagnostic += message =>
                {
                    if (!_viewerInteractionStopped && !_sessionCancellation.IsCancellationRequested && _videoDiagnosticEntry != null)
                        DiagnosticNotice?.Invoke(new("video-cleanup", _videoDiagnosticEntry, _videoDiagnosticRevision, message, true));
                };
                _video.VolumeChanged += volume => { _videoVolume = volume; VideoVolumeChanged?.Invoke(volume); };
                _video.LoopChanged += OnVideoLoopChanged;
                _video.TransportControl.SizeChanged += (_, _) => { if (_contextOpen) LayoutContextPlacement(); };
                UseNativeVideoOverlay(true);
                _viewport.Children.Add(_video);
            }
            else UseNativeVideoOverlay(true);
            if (_nativeOverlayUnsupported) SetVideoFallbackLayout(true);
            _videoDiagnosticEntry = entry; _videoDiagnosticRevision = AssociationRevision;
            BindVideoPlaybackSignals(request);
            _video.SetLoopEnabled(_playbackPreferences.Mode == GalleryPlaybackMode.Repeat);
            _video.Configure(resumeAutoplay ?? EffectiveVideoAutoplay, _videoStartMuted, _videoVolume);
            _image.IsVisible = _status.IsVisible = false; _video.IsVisible = true;
            ApplyOverlayPlacement();
            if (_nativeOverlayUnsupported)
                Dispatcher.UIThread.Post(() => _nativeOverlayWindow?.Hide(), DispatcherPriority.Loaded);
            if (_nativeOverlayActive)
                Dispatcher.UIThread.Post(CheckNativeOverlayTransparency, DispatcherPriority.Loaded);
            await _video.ShowAsync(entry, cancellationToken);
            if (request != _request || cancellationToken.IsCancellationRequested) return;
            _videoReadyRequest = request;
            // Initial selection already honored autoplay. Only a slideshow explicitly started
            // during loading may still need Play; never Pause because progression is stopped.
            if (_slideshow.IsRunning) await _video.PlayForSlideshowAsync();
            RefreshPlaybackControls();
            return;
        }
        ApplyOverlayPlacement();
        if (entry.Kind == SavedMediaKind.Other) { StopSlideshow(); _status.Text = Texts.T("gallery.fileOnly"); return; }
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

    private bool HasOriginal => _gif != null || _highBitmap != null || (_info != null && _fitBitmap != null &&
        _fitBitmap.PixelSize.Width == _info.Width && _fitBitmap.PixelSize.Height == _info.Height);

    private Task RequestDecodeAsync(GalleryDecodeRequest decode, bool debounce, bool retry = false)
    {
        if (_entry == null || _gif != null) return Task.CompletedTask;
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
        long mappingRevision = AssociationRevision;
        long navigation = _navigationId;
        long decodeId = PerformanceDiagnostics.NextId();
        PerformanceDiagnostics.Record("gallery.image.request", "accepted", decodeId, asset: entry.Asset.AssetId,
            epoch: _diagnosticEpoch, revision: mappingRevision, value: navigation);
        var queueWait = PerformanceDiagnostics.Measure("gallery.image.queue-wait", decodeId,
            asset: entry.Asset.AssetId, epoch: _diagnosticEpoch, revision: mappingRevision);
        async Task CompleteAsync(Task pending)
        {
            try { await pending; }
            finally { queueWait.Dispose(); } // Also close the wait span for a replaced, never-executed request.
        }
        return CompleteAsync(_decodeQueue.Replace(async token =>
        {
            queueWait.Dispose();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
            try
            {
                if (IsGifCandidate(entry) && await TryAdoptGifAsync(entry, session, generation, linked.Token)) return;
                Bitmap bitmap;
                GalleryImageInfo info;
                using (PerformanceDiagnostics.Measure("gallery.image.load", decodeId, asset: entry.Asset.AssetId, epoch: _diagnosticEpoch, revision: mappingRevision))
                    (bitmap, info) = await DecodeImageAsync(entry, decode, linked.Token);
                if (session != _request || generation != _decodeGeneration || linked.IsCancellationRequested)
                {
                    PerformanceDiagnostics.Record("gallery.image.request", "discarded", decodeId, asset: entry.Asset.AssetId,
                        epoch: _diagnosticEpoch, revision: mappingRevision);
                    DisposeBitmap(bitmap, entry.Key, decodeId, mappingRevision);
                    return;
                }
                using var adoption = PerformanceDiagnostics.Measure("gallery.bitmap.adopt", decodeId, asset: entry.Asset.AssetId, epoch: _diagnosticEpoch, revision: mappingRevision);
                Bitmap? previous = decode is GalleryDecodeRequest.Original ? _highBitmap : _fitBitmap;
                if (decode is GalleryDecodeRequest.Original) _highBitmap = bitmap;
                else _fitBitmap = bitmap;
                _info = info;
                _image.Source = _highBitmap ?? bitmap;
                DisposeBitmap(previous, entry.Key, decodeId, mappingRevision); // Switch Source before releasing its old backing pixels.
                _status.Text = string.Empty;
                _mediaFailed = false;
                SetImageCommands(true);
                FitIfAutomatic();
                ApplyRestoredState();
                MarkVisibleImageReady();
                if (_manualZoom && !HasOriginal) _ = EnsureDetailAsync();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (session == _request && generation == _decodeGeneration)
                {
                    // Preserve the existing failed-decode progression guard, including
                    // an original/detail decode failure with older safe fit pixels present.
                    await SetMediaUnavailable(error.Message, session);
                }
            }
            finally
            {
                if (_pendingOriginalGeneration == generation) _pendingOriginalGeneration = null;
            }
        }, debounce));
    }

    private void OnScaleOwnerChanged(object? sender, EventArgs change) => FitIfAutomatic();

    // The context panel's controls (post text, quote parsing, association selector) are only observable while the
    // panel is open. A closed panel records that its content is stale; opening it, restoring an open state or a
    // language change while open materializes the newest entry/association state exactly once.
    private bool _contextDirty;
    private int _contextGeneration;
    private long _contextBuilds;
    /// <summary>Number of times the context panel's controls were actually built (diagnostics and tests).</summary>
    internal long ContextBuildCount => Interlocked.Read(ref _contextBuilds);

    private void UpdateContext()
    {
        ++_contextGeneration; // Invalidate callbacks retained by controls from an older entry/revision/language.
        _provenance.Text = MediaProvenanceText.Summary(_entry?.AttachmentResolution?.Binding);
        _provenance.IsVisible = !_mediaFocus && !string.IsNullOrEmpty(_provenance.Text);
        if (_entry == null) { _context.Children.Clear(); _contextDirty = false; return; }
        if (!_contextOpen) { _contextDirty = true; return; }
        BuildContext();
    }

    private void EnsureContextCurrent()
    {
        if (_contextDirty && _contextOpen) BuildContext();
    }

    private void BuildContext()
    {
        using var measurement = PerformanceDiagnostics.Measure("gallery.context.build", _navigationId, asset: _entry?.Asset.AssetId, epoch: _diagnosticEpoch, revision: AssociationRevision);
        Control? focused = _context.IsKeyboardFocusWithin
            ? TopLevel.GetTopLevel(_context)?.FocusManager?.GetFocusedElement() as Control : null;
        Control[] prior = _context.GetVisualDescendants().OfType<Control>().ToArray();
        int focusPosition = focused == null ? -1 : Array.IndexOf(prior.Where(value =>
            value.GetType() == focused.GetType() && value.Name == focused.Name && Equals(value.Tag, focused.Tag)).ToArray(), focused);
        _contextDirty = false;
        int generation = _contextGeneration;
        Interlocked.Increment(ref _contextBuilds);
        _context.Children.Clear();
        if (_entry == null) return;
        GalleryEntry entry = _entry;
        Button ContextCommand(string name, string label, Action action) => Command(_context, name, label, () =>
        {
            if (generation == _contextGeneration && _contextOpen) action();
        });
        ContextCommand("GalleryReturnImage", Texts.T("gallery.viewer7"), () => SetContextOpen(false));
        try
        {
            AddContext(entry.Key.Thread.Source + " /" + entry.Key.Thread.Board + "/ · " + entry.Key.Thread.Thread);
            AddContext(entry.Asset.OriginalFileName ?? entry.Asset.RequestedFileName);
            AddContext(MediaProvenanceText.LocalSize(entry.Asset.Length));
            string provenance = MediaProvenanceText.Context(entry.AttachmentResolution?.Binding);
            if (provenance.Length > 0) AddContext(provenance);
            if (entry.Historical) AddContext(Texts.T("gallery.viewer8"));
            IReadOnlyList<GalleryEntry> associations = _associationIndex?.For(entry.Key) ?? Array.Empty<GalleryEntry>();
            if (associations.Count > 1)
            {
                ComboBox selector = new() { Name = "GalleryPostAssociation", HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = associations.Select(value => "#" + value.Post!.PostId + " · " + (value.Attachment?.Position + 1)).ToArray(),
                    SelectedIndex = associations.ToList().FindIndex(value => value.Post?.PostId == entry.Post?.PostId && value.Attachment?.Position == entry.Attachment?.Position) };
                AutomationProperties.SetName(selector, Texts.T("gallery.viewer9"));
                selector.SelectionChanged += (_, _) =>
                {
                    if (generation != _contextGeneration || !_contextOpen || selector.SelectedIndex < 0 || selector.SelectedIndex >= associations.Count) return;
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
                    ContextCommand("GalleryQuote", quote.Label, () => { PauseSlideshow(); QuoteRequested?.Invoke(quote); }).Tag = quote.Target;
                }
            ContextCommand("GalleryReadPost", Texts.T("gallery.viewer10"), () => { PauseSlideshow(); ReadPostRequested?.Invoke(entry); });
        }
        finally
        {
            if (focused != null)
            {
                // Restore only focus that belonged to this panel, by command/quote identity. Never
                // steal focus from navigation or a reader when a background mapping is replaced.
                UpdateLayout();
                Control? replacement = _context.GetVisualDescendants().OfType<Control>().Where(value =>
                    value.GetType() == focused.GetType() && value.Name == focused.Name && Equals(value.Tag, focused.Tag))
                    .Skip(Math.Max(0, focusPosition)).FirstOrDefault();
                (replacement ?? _context.Children.OfType<Button>().FirstOrDefault())?.Focus(NavigationMethod.Tab);
            }
        }
    }
    private void AddContext(string text) => _context.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, [!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text") });
    private void LayoutContext()
    {
        LayoutContextPlacement();
        FitIfAutomatic();
    }
    private void Move(int delta)
    {
        if (!(delta < 0 ? _previous.IsEnabled : _next.IsEnabled)) return;
        _navigationPending = true; // No old-media timer while the shell resolves the next entry.
        MoveRequested?.Invoke(delta);
    }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing) return;
        Control routingOwner = sender is Control control ? control : this;
        if (!CanRouteGalleryShortcut(routingOwner, e)) return;
        if (RevealForKeyboard(e)) return;
        if (CommandBindings.Matches(e, "window.fullscreen")) { FullscreenRequested?.Invoke(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "viewer.shortcut-help"))
        { SetHelp(!_helpOpen); e.Handled = true; return; }
        if (_helpOpen) return;
        if ((ReferenceEquals(routingOwner, _nativeVideoOverlay) ? _nativeRatingRouter : _ratingRouter)?.Route(e) == true) return;
        // This is part of the existing gallery handler, not a second router. Only
        // actual owned transport sliders may opt into these four effective commands.
        if (_entry?.Kind == SavedMediaKind.Video && _video?.IsTransportInput(e.Source) == true)
        {
            if (_video.MatchesTransportShortcut(e, "gallery.previous")) { Move(-1); e.Handled = true; return; }
            if (_video.MatchesTransportShortcut(e, "gallery.next")) { Move(1); e.Handled = true; return; }
            if (_video.MatchesTransportShortcut(e, "video.volume-up")) { AdjustVideoVolume(1); e.Handled = true; return; }
            if (_video.MatchesTransportShortcut(e, "video.volume-down")) { AdjustVideoVolume(-1); e.Handled = true; return; }
        }
        if (!IsGalleryShortcutInput(e)) return;
        foreach (var (_, id) in PlaybackActions())
            if (CommandBindings.Matches(e, id)) { e.Handled = true; _ = ExecutePlaybackActionAsync(id); return; }
        if (CommandBindings.Matches(e, "favorites.toggle")) { ToggleFavorite(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.tools-toggle")) { SetMediaFocus(!_mediaFocus); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.context-toggle")) { SetContextOpen(!_contextOpen); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.fit")) { Fit(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.actual-size")) { ActualPixels(); e.Handled = true; return; }
        // Matches already protects Button Space/Enter. A custom non-activation
        // gesture must still work when a transport/toolbar button has focus.
        if (CommandBindings.Matches(e, "video.play-pause") && _gif?.Evidence.FrameCount > 1)
        { _ = ToggleGifPlaybackAsync(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "video.play-pause") && _entry?.Kind == SavedMediaKind.Video && _video != null)
        { _ = _video.TogglePlaybackAsync(); e.Handled = true; return; }
        if (_entry?.Kind == SavedMediaKind.Video && _video != null)
        {
            if (CommandBindings.Matches(e, "video.volume-up")) { AdjustVideoVolume(1); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.volume-down")) { AdjustVideoVolume(-1); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.mute")) { _ = _video.ToggleMuteAsync(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.loop")) { _video.ToggleLoop(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.play")) { _ = _video.PlayAsync(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.pause")) { _ = _video.PauseAsync(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.stop")) { _ = _video.StopPlaybackAsync(); e.Handled = true; return; }
        }
        if (CommandBindings.Matches(e, "gallery.previous")) Move(-1);
        else if (CommandBindings.Matches(e, "gallery.next")) Move(1);
        else if (CommandBindings.Matches(e, "gallery.zoom-in")) Zoom(1.1);
        else if (CommandBindings.Matches(e, "gallery.zoom-out")) Zoom(1 / 1.1);
        else if (CommandBindings.Matches(e, "gallery.close")) CloseRequested?.Invoke();
        else if (CommandBindings.Matches(e, "media.show-in-folder")) { if (_entry != null) ShowInFolderRequested?.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "media.details")) { if (_entry != null) DetailsRequested?.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "gallery.show-in-thread")) { if (_entry?.Post != null) { PauseSlideshow(); ReadPostRequested?.Invoke(_entry); } }
        else { CommandBindings.RouteVisibleActions(this, e); return; }
        e.Handled = true;
    }

    private void AdjustVideoVolume(int direction)
    {
        if (!_interactionAvailable || _viewerInteractionStopped || _videoSuspended || _mediaFailed
            || _navigationPending || _sessionCancellation.IsCancellationRequested) return;
        _video?.AdjustVolume(direction);
    }

    // Input eligibility is separate from window eligibility: callers must pass
    // CanRouteGalleryShortcut first. F1/F11 retain their existing explicit exceptions.
    internal static bool IsGalleryShortcutInput(KeyEventArgs e) => !e.Handled && !ShortcutService.IsCapturing
        && !CommandRouter.IsNativeInput(e.Source, e.Key)
        && !(e.Source is Visual input && input.GetSelfAndVisualAncestors().Any(control =>
            control is TabControl or DataGrid or SelectableTextBlock));

    private bool CanRouteGalleryShortcut(Control owner, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || !CommandRouter.IsRoutingContextActive(owner, e.Source)) return false;
        // The native video overlay is an authorized gallery surface, not a dialog.
        // Other owned windows must not leave this viewer's tunnel handler active.
        return TopLevel.GetTopLevel(this) is not Window host
            || !host.OwnedWindows.Any(window => window.IsVisible && !OwnsOverlayWindow(window));
    }

    internal static bool IsUnfocusedWindowKey(KeyEventArgs e, Window window, IInputElement? focused) =>
        !e.Handled && ReferenceEquals(e.Source, window) && focused == null;

    internal bool RouteUnfocusedHostShortcut(KeyEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window
            || !IsUnfocusedWindowKey(e, window, window.FocusManager?.GetFocusedElement())) return false;
        // Re-enter the SAME gallery policy, not visible-button fallback. This is
        // restricted to an actual Window source with no focused native control.
        OnKeyDown(this, e);
        return e.Handled;
    }

    private void BindNativeShortcutWindow(Window window)
    {
        if (ReferenceEquals(window, _nativeShortcutWindow)) return;
        UnbindNativeShortcutWindow();
        _nativeShortcutWindow = window;
        window.AddHandler(KeyDownEvent, OnUnfocusedNativeWindowKeyDown, RoutingStrategies.Bubble);
    }

    private void UnbindNativeShortcutWindow()
    {
        _nativeShortcutWindow?.RemoveHandler(KeyDownEvent, OnUnfocusedNativeWindowKeyDown);
        _nativeShortcutWindow = null;
    }

    private void OnUnfocusedNativeWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(window, _nativeShortcutWindow)
            || !IsUnfocusedWindowKey(e, window, window.FocusManager?.GetFocusedElement())) return;
        // Match the normal shell's Escape hierarchy even when native focus was lost:
        // close help/context or leave fullscreen before closing the gallery itself.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            OnNativeOverlayKeyDown(_nativeVideoOverlay, e);
            if (e.Handled) return;
        }
        OnKeyDown(_nativeVideoOverlay, e);
        if (!e.Handled) OnNativeOverlayKeyDown(_nativeVideoOverlay, e);
    }

    private void OnNativeOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (!CanRouteGalleryShortcut(_nativeVideoOverlay, e)) return;
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
    private void DisposeBitmap(Bitmap? bitmap, GalleryAssetKey? key, long request, long revision)
    {
        if (bitmap == null) return;
        using var measurement = PerformanceDiagnostics.Measure("gallery.bitmap.dispose", request, asset: key?.AssetId, epoch: _diagnosticEpoch, revision: revision);
        bitmap.Dispose();
    }
    private void ClearBitmaps()
    {
        _image.Source = null;
        DisposeBitmap(_highBitmap, _entry?.Key, _navigationId, AssociationRevision); _highBitmap = null;
        DisposeBitmap(_fitBitmap, _entry?.Key, _navigationId, AssociationRevision); _fitBitmap = null;
    }
    private VideoPlaybackMode? _suspendedPlaybackMode;
    internal Task SuspendMediaAsync()
    {
        // Reader suspension retires the drawable/input, not the user's play/pause choice.
        // Returning must not turn a paused video into autoplay because config still says On.
        if (!_videoSuspended) _suspendedPlaybackMode = _video?.PlaybackMode;
        PauseSlideshow();
        _gif?.SetActive(false);
        _videoSuspended = _entry?.Kind == SavedMediaKind.Video;
        return _videoSuspended && _video != null ? _video.StopAsync() : Task.CompletedTask;
    }
    internal Task ResumeMediaAsync()
    {
        UpdateGifPresentation();
        if (!_videoSuspended || _entry == null || _video == null) return Task.CompletedTask;
        _videoSuspended = false;
        return ShowMediaAsync(_entry, _sessionCancellation, _suspendedPlaybackMode == VideoPlaybackMode.Playing);
    }
    internal async Task StopAsync()
    {
        StopViewerInteraction();
        UnbindVideoPlaybackSignals();
        _video?.SetExternalTransportHost(null);
        ++_request; CancelLoads();
        await RetireGifAsync();
        ClearBitmaps(); _entry = null; _info = null; _restore = null;
        _provenance.Text = string.Empty; _provenance.IsVisible = false;
        _exportOriginal.IsEnabled = false;
        _associationIndex = null; _context.Children.Clear(); _contextDirty = false; ++_contextGeneration;
        _contextOpen = false; _videoSuspended = false; SetHelp(false); LayoutContext(); UpdateContextButton();
        if (_video != null) await _video.DisposeAsync();
        UseNativeVideoOverlay(false);
        SetVideoFallbackLayout(false);
        // Preferences survive this viewer's close; a new instance receives them via ConfigurePlayback.
    }
}

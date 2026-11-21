using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
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
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Archive;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

internal sealed record GalleryViewerState(GalleryAssetKey? Key, string? PostId, int? AttachmentPosition,
    bool ManualZoom, double PhysicalZoom, double PanX, double PanY, bool ContextOpen, bool ActualZoom, int Rotation = 0);

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

/// <summary>Where the gallery's way back returns to; it only names the target (the command stays gallery.close).</summary>
// Tagging (QW6) is appended, not inserted: the values 0 to 4 are what tests and smokes already name.
internal enum GalleryReturnTarget { None, Media, Thread, Archive, Downloads, Tagging }

// A diagnostic is data for the owning shell, never an instruction to the transport.
internal sealed record GalleryDiagnosticNotice(string Operation, GalleryEntry? Entry, long Revision,
    string Message, bool MediaUnavailable);

internal sealed partial class GalleryViewer : UserControl, IAsyncDisposable
{
    private readonly TranslateTransform _imageTranslation = new();
    private readonly RotateTransform _imageRotation = new();
    private readonly Image _image = new() { Name = "GalleryImage", Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _viewport = new() { Name = "GalleryViewport", ClipToBounds = true, Background = Brushes.Black };
    private readonly TextBlock _status = new() { Name = "GalleryStatus", TextWrapping = TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), IsHitTestVisible = false };
    private readonly TextBlock _scope = new() { Name = "GalleryScope", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _provenance = new() { Name = "GalleryProvenance", [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    // Warnings travel as diagnostics and never fill this line; keep it out of the layout so it does not leave an empty row.
    private readonly TextBlock _warnings = new() { Name = "GalleryWarnings", [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly SelectableTextBlock _favoriteError = new() { Name = "GalleryFavoriteError", Classes = { "workspace-error" },
        TextWrapping = TextWrapping.Wrap, Focusable = true, IsVisible = false };
    private readonly Button _readPosts;
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _compactPosition = new() { Name = "GalleryCompactPosition", VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MinWidth = 44 };
    private readonly List<Control> _secondaryTools = new();
    private readonly ToggleButton _toolsToggle;
    private bool _mediaFocus;
    private readonly TextBlock _quality = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _context = new() { Spacing = 12 };
    private readonly Border _contextPanel = new() { Name = "GalleryContext", Classes = { "workspace-section" }, IsVisible = false };
    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
    private readonly Grid _visualRoot = new() { Background = Brushes.Transparent, ClipToBounds = true, RowDefinitions = new RowDefinitions("*,Auto,Auto") };
    private readonly Grid _nativeVideoOverlay = new NativeVideoOverlayRoot() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _close;
    private readonly Button _actual;
    private Button _actualRow = null!;
    private readonly Button _fit;
    private readonly Button _zoomIn;
    private readonly Button _zoomOut;
    private readonly Button _rotate;
    private readonly Button _exportOriginal;
    // "Save frame as image" (⋯ and Shift+S): the shown video frame as a PNG; only while a video is shown.
    private readonly Button _saveFrame;
    private readonly ToggleButton _favorite;
    private readonly ToggleButton _contextToggle;
    private readonly Button _helpButton;
    private Button _fullscreenButton = null!;
    private readonly Border _helpPanel = new() { Name = "GalleryShortcutHelp", Classes = { "workspace-section" }, IsVisible = false };
    private readonly StackPanel _helpContent = new() { Spacing = 8 };
    private readonly TextBlock _helpTitle = new();
    private readonly Button _helpClose;
    private readonly Border _chrome = new() { Name = "GalleryOverlayControls", Classes = { "workspace-media-tools" },
        Margin = new Thickness(8, 0, 8, 0), [!Border.PaddingProperty] = new DynamicResourceExtension("Workspace.Inset.MediaTools"), HorizontalAlignment = HorizontalAlignment.Stretch };
    // GL1+GL2: the docked dock below the picture, without a gap to it: [filmstrip (when at the bottom)] [_chrome: video bar row
    // (GalleryDockTransportHost, GL2), tools] [info line]. The floating footer (_overlayFooter) has the same order: [filmstrip]
    // [GalleryOverlayTransportHost] [_chrome] [info]. The Windows transparency fallback keeps the video bar under the picture.
    private readonly StackPanel _dockStack = new() { Name = "GalleryDock", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border _infoOverlay = new() { Name = "GalleryOverlayInfo", Classes = { "workspace-media-caption" },
        Margin = new Thickness(8, 0, 8, 8), Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Stretch }; // GL5b: one 20 dip line (it grows with a safety line)
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
        Task<(Bitmap Bitmap, GalleryImageInfo Info)>> DecodeImageAsync { get; set; } = GalleryImageCache.DecodeAsync;
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
    /// <summary>F1 and the Help tool: the owner opens (or closes) the one shortcuts window on <see cref="ShortcutHelp"/>.</summary>
    internal event Action<Views.Shortcuts.ShortcutHelpContext>? ShortcutHelpRequested;
    internal event EventHandler<KeyEventArgs>? ShellKeyRequested;
    internal event Action<GalleryEntry>? FavoriteToggleRequested;
    /// <summary>The saved-ratings manager (the shell's dialog), from the rating rows of the "⋯" menu.</summary>
    internal event Action? ManageRatingsRequested;
    private readonly Button _manageRatingsButton = new() { Name = "GalleryManageRatings" };
    internal Button ManageRatingsButton => _manageRatingsButton;
    internal event Action<GalleryEntry, int?>? RatingSetRequested;
    /// <summary>"Rate and tag…" of a filmstrip tile's menu: the shell opens the popover for that tile's medium on the filmstrip.</summary>
    internal event Action<GalleryEntry, Control>? RateAndTagTileRequested;
    private CommandRouter? _ratingRouter;
    private CommandRouter? _nativeRatingRouter;
    private CommandRouter CreateRatingRouter(Control owner)
    {
        CommandRouter router = new(owner);
        bool Ready() => _entry != null && _ratingAvailable;
        // R "Rate and tag" (Tom's decision of 15.11.: no rating menu, no dialog): the Tags segment opens with its stars focused.
        // Executable whenever a medium is shown: RequestRateAndTag says why when neither half can open (never a key consumed in silence).
        // A text selection in the post panel does not keep R (a bare letter does nothing to it; Ctrl+C stays the selection's).
        router.RegisterOwned(new("media.rating.edit", owner, () => owner.IsEffectivelyVisible, () => _entry != null && !_terminalRetirement,
            RequestRateAndTag, Priority: 200, AllowInTextSelection: true));
        router.RegisterOwned(new("media.rating.manage", owner, () => owner.IsEffectivelyVisible, () => ManageRatingsRequested != null,
            () => ManageRatingsRequested?.Invoke(), Priority: 200));
        router.RegisterOwned(new("media.rating.clear", owner, () => owner.IsEffectivelyVisible, Ready,
            () => RequestRating(null), Priority: 200));
        for (int value = 1; value <= 5; value++)
        {
            int selected = value;
            router.RegisterOwned(new("media.rating.set-" + value, owner, () => owner.IsEffectivelyVisible, Ready,
                () => RequestRating(selected), Priority: 200));
        }
        // Colour labels (6 to 9): the same medium and gate as the rating keys; "Remove label" has no default key.
        foreach (string color in MeowChan.Ratings.MediaLabelColors.All)
        {
            string chosen = color;
            router.RegisterOwned(new(MediaLabelText.CommandId(color), owner, () => owner.IsEffectivelyVisible, Ready,
                () => RequestLabel(chosen), Priority: 200));
        }
        router.RegisterOwned(new("media.label.clear", owner, () => owner.IsEffectivelyVisible, Ready, () => RequestLabel(null), Priority: 200));
        return router;
    }
    private int? _ratingValue;
    private string? _ratingError;
    private bool _ratingAvailable;
    // A write is under way: the stars of the Post row stay enabled (the focus must not be lost under a keyboard user) and ignore clicks.
    private bool _ratingPending;
    private readonly TextBlock _ratingStatus = new() { Name = "GalleryRatingStatus", TextWrapping = TextWrapping.Wrap };
    private string? _labelColor;
    /// <summary>The shown medium's colour label (red, yellow, green, blue) or null; named in the info line beside the rating, never a colour alone.</summary>
    internal void SetLabel(string? color)
    {
        if (_labelColor == color) return;
        _labelColor = color;
        if (!string.IsNullOrEmpty(_ratingStatus.Text) || color != null) UpdateRatingStatusText();
    }
    internal event Action<GalleryEntry, string?>? LabelSetRequested;
    private void RequestLabel(string? color)
    {
        if (_entry == null) return;
        LabelSetRequested?.Invoke(_entry, color);
    }
    private void UpdateRatingStatusText()
    {
        string text = MediaRatingText.Label + ": " + MediaRatingText.Value(_ratingValue);
        _ratingStatus.Text = _labelColor == null ? text : text + " · " + MediaLabelText.Name(_labelColor);
    }
    internal void SetRating(int? value, string? error, bool available = true, bool pending = false)
    {
        bool changedError = _ratingError != error;
        _ratingValue = value; _ratingError = error; _ratingAvailable = available; _ratingPending = pending;
        UpdateRatingStatusText();
        if (changedError) ReportDiagnostic("rating", error);
        ApplyChrome();
        RefreshDecisionRow(); // the Tags panel's row shows the same rating
        RefreshPostDecision(); // and so does the Post row, with the error of a failed write under it
    }

    internal event Action<GalleryEntry>? ExportOriginalRequested;
    internal event Action<GalleryEntry>? ShowInFolderRequested;
    /// <summary>"Add to tagging queue" for the shown medium (menu and shortcut); the owner writes the queue and tells the person what happened.</summary>
    internal event Action<GalleryEntry>? QueueRequested;
    internal event Action<GalleryEntry>? DetailsRequested;
    /// <summary>
    /// Extra context lines for an entry that is not a chan post (a downloaded provider medium), or null. The owner supplies
    /// them from local data; the viewer only shows them in place of the "no post" line.
    /// </summary>
    internal Func<GalleryEntry, IReadOnlyList<string>?>? ProviderContext { get; set; }
    internal event Action<int>? VideoVolumeChanged;

    internal bool IsOverlayOpen => _helpOpen || _contextOpen || IsFavoriteConfirmOpen;
    /// <summary>
    /// Whether the help or the favourite question owns the keys while nothing has the focus. The side panel does not: with it open (docked or over
    /// the picture) and the focus gone (a retired video transport, hidden controls) the gallery keys still reach <see cref="RouteUnfocusedHostShortcut"/>.
    /// </summary>
    internal bool BlocksUnfocusedHostKeys => _helpOpen || IsFavoriteConfirmOpen;
    internal bool OwnsOverlayWindow(Window window) =>
        _nativeOverlayActive && ReferenceEquals(TopLevel.GetTopLevel(_nativeVideoOverlay), window);
    internal IEnumerable<Visual> GetGalleryVisualDescendants() =>
        ((Visual)this).GetVisualDescendants().Concat(_nativeVideoOverlay.GetVisualDescendants())
            .Concat(_overflowPanel.GetVisualDescendants()).Prepend(_overflowPanel)
            .Concat(_slideshowHome?.GetVisualDescendants() ?? []).Distinct(); // G2: the slideshow settings also while the flyout borrows them
    internal bool TryDismissOverlay()
    {
        if (IsFavoriteConfirmOpen) { CloseFavoriteConfirm(); return true; }
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

    internal void SetFavorite(bool selected, bool pending = false)
    {
        // A change of the shown medium's favourite (never its first known state) flashes the badge while the controls are auto-hidden.
        bool changed = _favoriteKnown && (_favoriteSelected != selected || pending && !_favoritePending);
        _favoriteKnown = true;
        _favoritePending = pending;
        _favoriteSelected = selected;
        ReconcileFavoriteConfirm(selected, pending);
        UpdateFavorite();
        if (changed) FlashFavoriteBadge();
        RefreshDecisionRow(); // the panel's row and the header star stay in step
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
        FilmstripShown(entry, associations);
        return ShowAsync(entry, cancellationToken);
    }

    internal GalleryViewer()
    {
        // The "where am I" line goes through the work-view gate (ApplyChrome), which needs its id from the first frame.
        WorkViewElement.SetId(_scope, "gallery.info.scope");
        // Subscribe before any of these subtrees can enter a presentation root.
        foreach (Control control in new Control[] { _overlayFooter, _chrome, _infoOverlay,
            _contextPanel, _helpPanel, _favoriteConfirmPanel, _controlsEdgeIndicator, _postEdgeIndicator })
            GalleryControlTransfer.Track(control, () => !_terminalRetirement);
        foreach (Control indicator in new Control[] { _controlsEdgeIndicator, _postEdgeIndicator })
            GalleryControlTransfer.Track(indicator, () => !_terminalRetirement && !_viewerInteractionStopped);
        // Where the keys of a surface of their own (the filmstrip, the tagging strip, the info line's rating, the post and help panels, the
        // question) or the edge handles take the click, the focus is theirs: the pointer reclaim below leaves these subtrees alone.
        foreach (Control region in new Control[] { _filmstrip, _taggingStrip, _infoOverlay, _contextPanel, _helpPanel, _favoriteConfirmPanel,
            _controlsEdgeIndicator, _postEdgeIndicator })
            PointerFocusReclaim.SetExempt(region, true);
        AttachedToVisualTree += (_, _) =>
        {
            _ratingRouter?.Dispose(); _nativeRatingRouter?.Dispose();
            _ratingRouter = CreateRatingRouter(this); _nativeRatingRouter = CreateRatingRouter(_nativeVideoOverlay);
        };
        DetachedFromVisualTree += (_, _) => { _ratingRouter?.Dispose(); _nativeRatingRouter?.Dispose(); };
        // Rotation first, then the pan: the pan stays in screen space whatever the medium's orientation.
        _image.RenderTransform = new TransformGroup { Children = { _imageRotation, _imageTranslation } };
        Focusable = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        Grid root = _visualRoot;
        // One non-wrapping line with a true window-centred navigation group. Unequal action groups at the edges must not move the
        // previous/position/next group away from the picture's centre; at constrained widths the centre yields to the real edge gap.
        GeometricCenterPanel toolbar = new() { Name = "GalleryToolbar", VerticalAlignment = VerticalAlignment.Center };
        StackPanel Side(string name, HorizontalAlignment align)
        {
            StackPanel side = new() { Name = name, Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
            toolbar.Children.Add(side);
            return side;
        }
        StackPanel leftSide = Side("GalleryToolbarLeft", HorizontalAlignment.Left), middleSide = Side("GalleryToolbarMiddle", HorizontalAlignment.Center),
            rightSide = Side("GalleryToolbarRight", HorizontalAlignment.Right);
        StackPanel ToolGroup(string name, StackPanel side)
        {
            StackPanel group = new() { Name = name, Classes = { "workspace-tool-group" } };
            side.Children.Add(group);
            return group;
        }
        // G1: the mode switch "View | Tag" comes first; the rest of the bar follows the mode (GalleryViewer.Chrome.cs).
        InitializeModeSwitch(ToolGroup("GalleryModeTools", leftSide));
        StackPanel navigation = _navigationTools = ToolGroup("GalleryNavigationTools", middleSide);
        // Layout, media and view actions form the trailing task region. Navigation remains the sole centre owner.
        StackPanel zoom = _zoomTools = ToolGroup("GalleryZoomTools", rightSide);
        StackPanel media = _mediaTools = ToolGroup("GalleryMediaTools", rightSide);
        // The tools a work view brings onto the bar (rating, tags, slideshow numbers, mode, help): a group of its own, away from
        // the favourite and the way back; empty (hidden) in Barebones.
        _workViewTools = ToolGroup("GalleryWorkViewTools", rightSide);
        StackPanel view = _viewTools = ToolGroup("GalleryViewTools", rightSide);
        // Frequent controls stay on the toolbar; the rare ones are rows of the "⋯" menu (GalleryViewer.Overflow.cs).
        // The way back is not a toolbar button: it floats at the top LEFT edge of the picture, before everything else (Tom's rule of
        // 14.11.: back and close come first, other buttons stand apart on the right), names where it returns to and is
        // fully hidden (not dimmed) while auto-hidden controls are away. The favourite star sits directly right of it as a pair (UpdateFavoriteIndicators);
        // every other action is in the toolbar group below, apart from both.
        _close = Command(navigation, "GalleryClose", Texts.T("gallery.viewer0"), () => CloseRequested?.Invoke());
        navigation.Children.Remove(_close);
        _close.HorizontalAlignment = HorizontalAlignment.Left;
        _close.VerticalAlignment = VerticalAlignment.Top;
        _close.Margin = new Thickness(12);
        _close.ZIndex = 17; // Above the picture, the edge hints and the favorite badge; below the post panel and the help.
        _close.Classes.Add("gallery-back");
        PointerFocusReclaim.SetExempt(_close, true); // the way back leaves the viewer: nothing to give the keys back to
        _close.PointerEntered += (_, _) => UpdateCornerChrome();
        _close.PointerExited += (_, _) => UpdateCornerChrome();
        _close.GotFocus += (_, _) => UpdateCornerChrome();
        _close.LostFocus += (_, _) => UpdateCornerChrome();
        _previous = Command(navigation, "GalleryPrevious", Texts.T("gallery.viewer1"), () => Move(-1));
        navigation.Children.Add(_compactPosition);
        _next = Command(navigation, "GalleryNext", Texts.T("gallery.viewer2"), () => Move(1));
        _fit = Command(zoom, "GalleryFit", Texts.T("gallery.fit"), Fit);
        _actual = Command(zoom, "GalleryActual", "1:1", ActualPixels);
        _contextToggle = (ToggleButton)Command(media, "GalleryToggleContext", Texts.T("gallery.context"), ToggleContext);
        _favorite = (ToggleButton)Command(media, "GalleryFavorite", Texts.T("viewer.favorite.add"), ToggleFavorite);
        _readPosts = Command(view, "GalleryReadPosts", Texts.T("gallery.read"), () => { PauseSlideshow(); ReadPostsRequested?.Invoke(); });
        _readPosts.IsVisible = false;
        view.Children.Add(_overflowButton);
        view.Children.Add(_layoutButton); // GL3 (Tom's E2): the Layout menu is a button of its own beside "More" (GalleryViewer.LayoutMenu.cs)
        _zoomIn = Command(_overflowPanel, "GalleryPlus", "+", () => Zoom(1.1), overflow: true, keepOpen: true);
        _zoomOut = Command(_overflowPanel, "GalleryMinus", "−", () => Zoom(1 / 1.1), overflow: true, keepOpen: true);
        // GL5d (S2 stage 4): below GalleryChromePlan.TinyWidth the 1:1 button of the bar is this row of "More" (the key 1 works everywhere).
        _actualRow = Command(_overflowPanel, "GalleryActualMenu", "1:1", ActualPixels, overflow: true, keepOpen: true);
        _actualRow.IsVisible = false;
        _rotate = Command(_overflowPanel, "GalleryRotate", CommandCatalog.Label("gallery.rotate"), () => RotateView(), overflow: true, keepOpen: true);
        AddOverflowSeparator();
        CommandBindings.Apply(_manageRatingsButton, "media.rating.manage");
        _manageRatingsButton.Click += (_, _) => { PauseSlideshow(); ManageRatingsRequested?.Invoke(); };
        AddOverflowItem(_manageRatingsButton, "media.rating.manage", label: () => CommandCatalog.Label("media.rating.manage"));
        CreateTagsButton(_overflowPanel);
        _exportOriginal = Command(_overflowPanel, "GalleryExportOriginal", Texts.T("viewer.menu.exportOriginal"), () =>
        { if (_entry != null) ExportOriginalRequested?.Invoke(_entry); }, overflow: true);
        _saveFrame = Command(_overflowPanel, "GallerySaveFrame", CommandCatalog.Label("video.snapshot"), RequestSaveFrame, overflow: true);
        _saveFrame.IsEnabled = false; // Only a shown video has a frame to save.
        _secondaryTools.AddRange(new Control[] { zoom, media, view });
        _focusTools = ToolGroup("GalleryFocusTools", middleSide);
        // The tools toggle is the far right of the bar in a group of its own: the secondary groups (with "More" and the layout button)
        // go away in focus mode, the toggle must stay.
        StackPanel toggleGroup = ToolGroup("GalleryToolsToggleGroup", rightSide);
        _toolsToggle = (ToggleButton)Command(toggleGroup, "GalleryToolsToggle", string.Empty, () => SetMediaFocus(!_mediaFocus));
        InitializePlaybackControls(toolbar, _focusTools);
        InitializeGifControls(_focusTools);
        AddOverflowSeparator();
        _slideshowLoan.PlaceAtHome(); // FP1: the one owner of the settings panel is "⋯"; placing it twice is a no-op (the flyout borrows it later)
        AddOverflowSeparator();
        AddOverflowItem(_overlayToggle, "gallery.overlay-toggle", check: true);
        AddOverflowItem(_postOverlayToggle, "gallery.post-overlay-toggle", check: true);
        _fullscreenButton = Command(_overflowPanel, "GalleryFullscreen", Texts.T("viewer.fullscreen"), () => FullscreenRequested?.Invoke(), overflow: true);
        _helpButton = Command(_overflowPanel, "GalleryHelp", Texts.T("viewer.help"), RequestShortcutHelp, overflow: true);
        InitializeOverflowMenu();
        InitializeLayoutMenu();
        PlaceSlideshowToggle();
        UpdateMediaFocusPresentation();
        // Reserve navigation below the picture; secondary tools collapse only on request.
        // The media viewport keeps its black matte; the footer follows the host theme.
        // TG1 source seam: _chrome.Child = WithTaggingStrip(toolbar);
        _chrome.Child = WithDockTransport(WithTaggingStrip(toolbar)); // the tagging mode's decision strip sits below the toolbar (PT3); GL2: the video bar above it
        foreach (TextBlock text in new[] { _quality, _position, _compactPosition, _scope, _ratingStatus })
            text.Classes.Add("workspace-meta");
        // GL5c: one line "size · zoom · source · number" with the integrity note and the rating as segments, the status segment on the
        // right; the warning and error lines stay below it (GalleryViewer.InfoLine.cs).
        AutomationProperties.SetLiveSetting(_favoriteError, AutomationLiveSetting.Assertive);
        _infoOverlay.Child = BuildInfoContent();
        // Close sits at the top right of the surface it closes (× · Esc), outside the scrolling list of shortcuts.
        _helpTitle.Classes.Add("section-title");
        _helpTitle.VerticalAlignment = VerticalAlignment.Center;
        _helpTitle.TextWrapping = TextWrapping.Wrap;
        Grid helpHeader = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        helpHeader.Children.Add(_helpTitle);
        _helpClose = CloseLayerButton("GalleryHelpClose", () => SetHelp(false));
        helpHeader.Children.Add(_helpClose); Grid.SetColumn(_helpClose, 1);
        ScrollViewer helpScroll = new() { Content = _helpContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid helpRoot = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        helpRoot.Children.Add(helpHeader);
        helpRoot.Children.Add(helpScroll); Grid.SetRow(helpScroll, 1);
        _helpPanel.Child = helpRoot;
        _helpPanel.HorizontalAlignment = HorizontalAlignment.Right;
        _helpPanel.VerticalAlignment = VerticalAlignment.Top;
        _helpPanel.Margin = new Thickness(12, TopRowClearance, 12, 12); // below the top row of the way back and the star (kept below it: the help is also centred over the picture in narrow viewers)
        _helpPanel.MaxWidth = 390;
        _helpPanel.MaxHeight = 350;
        _viewport.Children.Add(_image);
        _statusPlate.Child = _status;
        _viewport.Children.Add(_statusPlate);
        InitializeFileOnlyPlate();
        _status.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty || e.Property == IsVisibleProperty)
                _statusPlate.IsVisible = _status.IsVisible && !string.IsNullOrWhiteSpace(_status.Text);
        };
        _body.Children.Add(_viewport);
        // Inherit native control states from the host, including popups and focus.
        ScrollViewer postScroll = new()
        {
            Name = "GalleryPanelPostScroll",
            Content = _context,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        // The content keeps the section inset on both sides; the vertical scrollbar owns the outer gutter and is not mistaken for right padding.
        postScroll[!ScrollViewer.PaddingProperty] = new DynamicResourceExtension("Workspace.Inset.Section");
        CreateSidePanel(postScroll);
        _body.Children.Add(_contextPanel); Grid.SetColumn(_contextPanel, 1);
        root.Children.Add(_close); // First child: first in the tab order, as the toolbar button was.
        GalleryControlTransfer.Track(_close, () => !_terminalRetirement);
        root.Children.Add(_body);
        _dockStack.Children.Add(_chrome);
        _dockStack.Children.Add(_infoOverlay);
        root.Children.Add(_dockStack); Grid.SetRow(_dockStack, 1);
        root.Children.Add(_helpPanel);
        root.Children.Add(_modeAnnouncement); // the mode's live region, at no size (GalleryViewer.Chrome.cs)
        Content = root;
        InitializeOverlayInteraction();
        InitializeFavoriteIndicators();
        InitializeFavoriteConfirm();
        _nativeVideoOverlay.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _nativeVideoOverlay.AddHandler(KeyUpEvent, OnFavoriteKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        _nativeVideoOverlay.AddHandler(KeyUpEvent, OnContextKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        _nativeVideoOverlay.AddHandler(KeyDownEvent, OnNativeOverlayKeyDown, RoutingStrategies.Bubble);
        _nativeVideoOverlay.ContextRequested += OnMediaContextRequested;
        SizeChanged += (_, _) =>
        {
            _chrome.MaxWidth = Math.Max(0, Bounds.Width - 16);
            UpdateBackLabel();
            UpdateToolbarDensity();
            LayoutContext();
            ApplyChrome(); // the width moves blocks under "More" in the plan's overflow order
        };
        _viewport.SizeChanged += (_, _) => FitIfAutomatic();
        InitializeViewTransform();
        _viewport.PointerWheelChanged += OnViewWheel;
        _viewport.PointerPressed += OnViewportPointerPressed;
        _viewport.PointerReleased += OnViewPointerReleased;
        _viewport.PointerCaptureLost += (_, _) => _dragging = false;
        _viewport.PointerMoved += OnViewPointerMoved;
        _viewport.DoubleTapped += OnViewDoubleTapped;
        // Windows draws the video in its own window; the transparent overlay above it receives the pointer there.
        _nativeVideoOverlay.PointerWheelChanged += OnViewWheel;
        _nativeVideoOverlay.PointerPressed += OnViewportPointerPressed;
        _nativeVideoOverlay.PointerReleased += OnViewPointerReleased;
        _nativeVideoOverlay.PointerCaptureLost += (_, _) => _dragging = false;
        _nativeVideoOverlay.PointerMoved += OnViewPointerMoved;
        _nativeVideoOverlay.DoubleTapped += OnViewDoubleTapped;
        _viewport.ContextRequested += OnMediaContextRequested;
        // The keyboard's Menu key and Shift+F10 come from the focused control (the viewer or a toolbar button), not from the viewport.
        AddHandler(ContextRequestedEvent, OnKeyboardContextRequested);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnFavoriteKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnContextKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true); // the key-up of a context key the gallery handled
        AttachedToVisualTree += (_, _) =>
        {
            if (_terminalRetirement) return;
            Texts.Changed += OnLanguageChanged;
            ShortcutService.Changed += OnShortcutBindingsChanged;
            _scaleOwner = TopLevel.GetTopLevel(this);
            _viewerInteractionStopped = false;
            if (_scaleOwner != null) _scaleOwner.ScalingChanged += OnScaleOwnerChanged;
            // The key-up of F must reach the viewer wherever the focus is (or is not): a latch that waits for it would swallow every later F.
            _scaleOwner?.AddHandler(KeyUpEvent, OnFavoriteKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (_scaleOwner is Window hostWindow) hostWindow.Deactivated += OnHostWindowDeactivated;
            ApplyOverlayPlacement();
            UpdateViewerInteractionTimer();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Texts.Changed -= OnLanguageChanged;
            ShortcutService.Changed -= OnShortcutBindingsChanged;
            if (_scaleOwner != null) _scaleOwner.ScalingChanged -= OnScaleOwnerChanged;
            _scaleOwner?.RemoveHandler(KeyUpEvent, OnFavoriteKeyUp);
            if (_scaleOwner is Window hostWindow) hostWindow.Deactivated -= OnHostWindowDeactivated;
            _favoriteGestureKey = null;
            _scaleOwner = null;
            StopViewerInteraction();
        };
        // The star keeps its place beside the way back, and the panels start below the top row, whatever width the label gives it.
        _close.SizeChanged += (_, _) => { UpdateFavoriteIndicators(); if (_contextOpen) LayoutContextPlacement(); };
        InitializeWorkViewTools();
        RegisterWorkViewIds();
        InitializeFilmstrip();
        InitializeWheelMode();
        UpdateFavorite();
        _exportOriginal.IsEnabled = false;
        UpdateContextButton();
        UpdateHelp();
        UpdateHelpButtonTooltip();
        SetImageCommands(false);
        ApplyCloseLabel();
    }


    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        { Avalonia.Threading.Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        CommandBindings.Apply(_manageRatingsButton, "media.rating.manage");
        SetRating(_ratingValue, _ratingError, _ratingAvailable, _ratingPending);
        if (IsFavoriteConfirmOpen) UpdateFavoriteConfirmText();
        UpdateTagsButton();
        PresentToolbarControls();
        PresentLayoutButton();
        RefreshFileOnlyText();
        UpdateHelpButtonTooltip();
        UpdateContext(); UpdateQuality(); UpdateHelp();
        RefreshPlaybackControls();
        RefreshOverflowItems();
        RefreshGifTransport();
        RefreshTaggingTexts();
        PresentModeSwitch();
    }

    // Names, icons, labels and tooltips of the toolbar and menu controls from the current language, shortcuts and density.
    private void PresentToolbarControls()
    {
        var labels = new Dictionary<string, string>
        {
            ["GalleryClose"] = "gallery.viewer0", ["GalleryPrevious"] = "gallery.viewer1",
            ["GalleryNext"] = "gallery.viewer2", ["GalleryFit"] = "gallery.fit",
            ["GalleryActual"] = "gallery.actual", ["GalleryToggleContext"] = "gallery.context",
            ["GalleryReadPosts"] = "gallery.read", ["GalleryFullscreen"] = "viewer.fullscreen",
            ["GalleryHelp"] = "viewer.help"
        };
        foreach (Button button in GetGalleryVisualDescendants().OfType<Button>())
        {
            if (button.Name != null && CommandId(button.Name) is { } id)
            {
                CommandBindings.Apply(button, id, iconOnly: IsIconOnly(button.Name) && button.Tag is not OverflowItem);
                ApplyCompactLabel(button, button.Name);
            }
            else if (button.Name != null && labels.TryGetValue(button.Name, out string? key))
            { button.Content = Texts.T(key); AutomationProperties.SetName(button, Texts.T(key)); }
        }
        ApplyCloseLabel();
        UpdateFavorite(); UpdateContextButton(); UpdateMediaFocusPresentation();
        PresentMovableTools();
        RefreshOverflowItems();
    }

    private void OnShortcutBindingsChanged() => OnLanguageChanged(this, EventArgs.Empty);

    private void UpdateHelpButtonTooltip()
    {
        // HB1: two lines - the help command with its key, then the keys of the other commands the help lists ("Label · key · Label · key").
        string tip = CommandBindings.Tooltip("viewer.shortcut-help");
        if (_showShortcutHelp)
            tip = CommandBindings.TooltipHeadline("viewer.shortcut-help") + "\n"
                + string.Join(" · ", new[] { "favorites.add", "favorites.remove", "gallery.context-toggle" }
                    .Where(id => CommandCatalog.ShortcutDisplay(id) != null).Select(id => CommandBindings.TooltipHeadline(id)));
        ToolTip.SetTip(_helpButton, tip);
        AutomationProperties.SetHelpText(_helpButton, tip);
    }

    private static string? CommandId(string name) => name switch
    {
        "GalleryClose" => "gallery.close", "GalleryPrevious" => "gallery.previous",
        "GalleryNext" => "gallery.next", "GalleryFit" => "gallery.fit",
        "GalleryActual" or "GalleryActualMenu" => "gallery.actual-size", "GalleryMinus" => "gallery.zoom-out",
        "GalleryPlus" => "gallery.zoom-in", "GalleryRotate" => "gallery.rotate", "GalleryToggleContext" => "gallery.context-toggle",
        "GalleryReadPosts" => "thread.read", "GalleryFullscreen" => "window.fullscreen",
        "GalleryHelp" => "viewer.shortcut-help", "GalleryFavorite" => "favorites.toggle",
        "GalleryExportOriginal" => "media.export-original", "GallerySaveFrame" => "video.snapshot",
        "GalleryOpenExternal" => "media.open-external", "GalleryFileOnlyFolder" => "media.show-in-folder",
        "GalleryToolsToggle" => "gallery.tools-toggle",
        "GalleryReadPost" => "gallery.show-in-thread",
        _ => null
    };

    private static bool IsIconOnly(string name) => name is "GalleryPrevious" or "GalleryNext" or "GalleryFit" or
        "GalleryMinus" or "GalleryPlus" or "GalleryHelp" or
        "GalleryToggleContext" or "GalleryFavorite" or "GalleryToolsToggle";

    /// <summary>GL5d: the tools that show only their icon at every width (the tooltip, name and shortcut stay on the control).</summary>
    private static bool AlwaysIconOnly(string name) => name is "GalleryFit" or "GalleryToggleContext" or "GalleryFavorite";

    internal static string? CompactLabelFor(string name) => name switch
    {
        "GalleryClose" => Texts.T("command.gallery.close.compact"),
        "GalleryFit" => Texts.T("gallery.fit"),
        "GalleryActual" => Texts.T("gallery.actual"), // I1b: "1:1" gets its catalog icon like its neighbour "Fit".
        "GalleryToggleContext" => Texts.T("gallery.context"),
        "GalleryFavorite" => Texts.T("command.gallery.favorite.compact"),
        "GalleryExportOriginal" => Texts.T("command.gallery.export-original.compact"),
        // Reuse the already shared fullscreen compact label; the full command title remains unchanged.
        "GalleryFullscreen" => Texts.T("command.window.fullscreen.compact"),
        _ => null
    };

    // MEDIA103-NAV P1 and the back rule: the gallery's way back says where it goes (the Medien page, the thread, the Archive
    // or Downloads list it was opened from). Presentation only: the command (gallery.close), Escape and the key binding stay.
    private GalleryReturnTarget _returnTarget;
    internal GalleryReturnTarget ReturnTarget
    {
        get => _returnTarget;
        set
        {
            if (_returnTarget == value) return;
            _returnTarget = value;
            ApplyCloseLabel();
            UpdateHelp();
        }
    }

    internal bool ClosesToMedia
    {
        get => _returnTarget == GalleryReturnTarget.Media;
        set
        {
            if (value) ReturnTarget = GalleryReturnTarget.Media;
            else if (_returnTarget == GalleryReturnTarget.Media) ReturnTarget = GalleryReturnTarget.None;
        }
    }

    internal string CloseLabel => _returnTarget switch
    {
        GalleryReturnTarget.Media => Texts.T("command.gallery.close.media"),
        GalleryReturnTarget.Thread => Texts.T("command.gallery.close.thread"),
        GalleryReturnTarget.Archive => Texts.T("command.gallery.close.archive"),
        GalleryReturnTarget.Downloads => Texts.T("command.gallery.close.downloads"),
        GalleryReturnTarget.Tagging => Texts.T("command.gallery.close.tagging"),
        _ => CommandCatalog.Label("gallery.close")
    };

    /// <summary>The Escape note of the shortcut help: it names the page it returns to where the way back does (Media, the Tagging page).</summary>
    private string EscapeHelpKey => _returnTarget switch
    {
        GalleryReturnTarget.Media => "viewer.help.escape.media",
        GalleryReturnTarget.Tagging => "viewer.help.escape.tagging",
        _ => "viewer.help.escape"
    };

    private double _backLabelledWidth;

    // The way back shows its arrow and its label while the labelled pair at the corner fits (GalleryBackLabel: computed from the measured width of the
    // labelled button, not from a breakpoint), otherwise only the arrow. The button owns this presentation; the toolbar's density (ApplyCompactLabel)
    // never touches it, because the icon-only square of that style clipped the label to its first letter (Tom, 16.11., half-screen windows).
    private void ApplyCloseLabel()
    {
        CommandBindings.Apply(_close, "gallery.close");
        string label = CloseLabel;
        string tip = CommandBindings.Tooltip("gallery.close", visibleLabel: label);
        _backLabel = label;
        // The labelled form first, so the button is measured at the width its label needs; the arrow-only form follows from that width.
        PresentBack(withLabel: true);
        _backLabelledWidth = MeasureLabelledBack();
        _compactBack = !GalleryBackLabel.ShowsLabel(Bounds.Width, _backLabelledWidth, StarGap);
        if (_compactBack) PresentBack(withLabel: false);
        ToolTip.SetTip(_close, tip);
        AutomationProperties.SetName(_close, label);
        AutomationProperties.SetHelpText(_close, tip);
    }

    private string _backLabel = string.Empty;

    private void PresentBack(bool withLabel)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        Border icon = (Border)CommandBindings.Icon("archive.back-to-list", 16); // the arrow-left of every "back"
        icon.Bind(Border.BackgroundProperty, icon.GetObservable(TextElement.ForegroundProperty));
        content.Children.Add(icon);
        if (withLabel) content.Children.Add(new TextBlock { Text = _backLabel, VerticalAlignment = VerticalAlignment.Center });
        _close.Classes.Set("icon-only", !withLabel); // the square of the input height for the arrow alone (WorkspaceStyles .icon-only)
        _close.Content = content;
    }

    // The width of the button with its label, without its margin. 0 while it is not in a laid-out visual tree (no styles, no font): the label shows then.
    private double MeasureLabelledBack()
    {
        if (TopLevel.GetTopLevel(_close) == null) return 0;
        _close.ApplyTemplate();
        _close.Measure(Size.Infinity);
        double width = Math.Max(0, _close.DesiredSize.Width - _close.Margin.Left - _close.Margin.Right);
        // The probe validated the measure at an unbounded size; the layout must measure and arrange the button again at its real room, or its
        // parent never learns the new size and arranges it at the old one.
        _close.InvalidateMeasure();
        return width;
    }

    // On every width change: the form follows the rule; a flip re-presents the button (and measures again when the first measure had to wait).
    private void UpdateBackLabel()
    {
        if (_close == null) return;
        if (_backLabelledWidth <= 0 && !_compactBack && TopLevel.GetTopLevel(_close) != null)
        {
            _backLabelledWidth = MeasureLabelledBack(); // the button is in the labelled form here (an unmeasured one never collapsed)
        }
        bool compact = !GalleryBackLabel.ShowsLabel(Bounds.Width, _backLabelledWidth, StarGap);
        if (compact == _compactBack) return;
        _compactBack = compact;
        PresentBack(withLabel: !compact);
    }

    /// <summary>Whether the way back shows only its arrow now (tests and smokes).</summary>
    internal bool IsBackCompact => _compactBack;

    /// <summary>The width of the way back with its label in the current language and target, 0 before its first measure (tests and smokes).</summary>
    internal double BackLabelledWidth => _backLabelledWidth;

    private bool _compactBack;

    // Tom's rule of 15.11.: with auto-hide the picture ends up clean. While the overlay controls are away, the way back and the star beside it are
    // fully hidden (transparent, not hit-testable, not focusable: GalleryCornerChrome) and come back with mouse motion, a favourite or rating
    // change, the pointer over one of them, or the keyboard focus inside the pair (Tab reveals the controls first). The way out stays Esc and
    // the keys; the pair hides again after "Hide controls after".
    private void UpdateCornerChrome()
    {
        if (_close == null) return;
        bool autoHidden = OverlayControlsEffective && _overlayHidden;
        bool pointer = HasCurrentGalleryPointer && (_close.IsPointerOver || _favoriteIndicator.IsPointerOver || _nativeFavoriteIndicator.IsPointerOver);
        bool focus = _close.IsKeyboardFocusWithin || _favoriteIndicator.IsKeyboardFocusWithin || _nativeFavoriteIndicator.IsKeyboardFocusWithin;
        // A pointer click that left the focus on a control does not pin the pair (as for the footer); only keyboard focus holds it.
        bool held = IsGalleryWindowActive && (pointer || _keyboardInteraction && focus);
        bool motion = CornerWindowLive(_lastCornerMotion);
        GalleryCornerChrome back = GalleryCornerChrome.Resolve(autoHidden, held, motion);
        // A change of the favourite shows the badge alone with its new state, without the way back.
        GalleryCornerChrome star = GalleryCornerChrome.Resolve(autoHidden, held, motion || CornerWindowLive(_lastFavoriteFlash));
        back.ApplyTo(_close);
        star.ApplyTo(_favoriteIndicator);
        star.ApplyTo(_nativeFavoriteIndicator);
        // A hidden control must not keep the focus (it would type into nothing): the focus returns to the picture.
        bool focusLost = _close.IsKeyboardFocusWithin && !back.Focusable
            || (_favoriteIndicator.IsKeyboardFocusWithin || _nativeFavoriteIndicator.IsKeyboardFocusWithin) && !star.Focusable;
        if (focusLost && !_restoringCornerFocus)
        {
            _restoringCornerFocus = true;
            try { FocusGallerySurface(); } finally { _restoringCornerFocus = false; }
        }
    }

    private bool _restoringCornerFocus;
    private long? _lastCornerMotion, _lastFavoriteFlash;

    private bool CornerWindowLive(long? stamp) => stamp is long at
        && Stopwatch.GetElapsedTime(at) < TimeSpan.FromMilliseconds(_controlsIdleHideMs);

    /// <summary>Whether recent motion or a change still keeps the auto-hidden pair shown (the interaction timer runs to hide it again).</summary>
    private bool CornerRevealTimed => OverlayControlsEffective && _overlayHidden
        && (CornerWindowLive(_lastCornerMotion) || CornerWindowLive(_lastFavoriteFlash));

    /// <summary>Mouse motion or a touch brings the pair back for the idle delay (the footer keeps its edge reveal).</summary>
    private void RevealCorner()
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        _lastCornerMotion = Stopwatch.GetTimestamp();
        UpdateCornerChrome();
        UpdateViewerInteractionTimer();
    }

    /// <summary>
    /// M, U, F, a click or an outside change of the favourite: the badge shows its new state alone for the idle delay while the controls are
    /// auto-hidden, then hides again.
    /// </summary>
    private void FlashFavoriteBadge()
    {
        if (_terminalRetirement || _viewerInteractionStopped || _close == null) return;
        _lastFavoriteFlash = Stopwatch.GetTimestamp();
        UpdateCornerChrome();
        UpdateViewerInteractionTimer();
    }

    private void ApplyCompactLabel(Button button, string name, string? visibleLabel = null)
    {
        string? label = visibleLabel ?? CompactLabelFor(name);
        if (label == null || CommandId(name) is not { } id) return;
        if (name == "GalleryClose") return; // The way back is a corner control, not a toolbar tool: ApplyCloseLabel decides its form by its measured label.
        if (button.Tag is OverflowItem) return; // A menu row shows its own text.
        // The favourite button draws the app's one favourite mark for its state (outline or filled); the other icons follow the foreground.
        // Untinted here: the marked toggle already shows its checked fill, and the gold tint on that fill fell to 1.5:1 (Light) and
        // 1.7:1 (Dark), below the 3:1 of an icon (video-contrast smoke, wave 3). The filled shape keeps the state readable.
        Border Icon(int size)
        {
            if (id == "favorites.toggle") return MeowChan.Desktop.Controls.FavoriteStar.Create(_favoriteSelected, size, tinted: false);
            Border plain = (Border)CommandBindings.Icon(id, size);
            // Semi sets the checked/pressed/disabled foreground on the content
            // presenter; inherit that same foreground for the icon and label.
            plain.Bind(Border.BackgroundProperty, plain.GetObservable(TextElement.ForegroundProperty));
            return plain;
        }
        if (_compactToolbar || AlwaysIconOnly(name))
        {
            // GL5d (S1): Fit, the post panel and the favourite are icons with a tooltip at every width; a narrow viewer (below
            // CompactToolbarWidth) shows the labelled controls that remain as icons too. The name, tooltip and shortcut stay on the control.
            button.Classes.Set("command-icon", true);
            button.Classes.Set("icon-only", true); // GL5d: the square of the input height (WorkspaceStyles .icon-only)
            button.Content = Icon(16);
            return;
        }
        button.Classes.Set("icon-only", false);
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        Border icon = Icon(14);
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
    }

    private Button Command(Panel panel, string name, string label, Action action, bool overflow = false, bool keepOpen = false)
    {
        string? id = CommandId(name);
        Button button = name is "GalleryToggleContext" or "GalleryFavorite" or "GalleryToolsToggle"
            ? new ToggleButton { Name = name }
            : new Button { Name = name };
        if (id == null) { button.Content = label; AutomationProperties.SetName(button, label); }
        else
        {
            CommandBindings.Apply(button, id, iconOnly: IsIconOnly(name) && !overflow);
            if (!overflow) ApplyCompactLabel(button, name);
        }
        button.Click += (_, _) => action();
        if (overflow)
        {
            AddOverflowItem(button, id ?? throw new InvalidOperationException("Menu row without command: " + name),
                keepOpen: keepOpen, label: name == "GalleryFullscreen" ? () => Texts.T("viewer.fullscreen") : () => CommandCatalog.Label(id),
                host: panel);
            return button;
        }
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
        CancelFilmstripContextRequest();
        int failureRequest = ++_request;
        CancelLoads(); UnbindVideoPlaybackSignals();
        _mediaFailed = true; _navigationPending = false; _browseQueue.Clear();
        _imageReadyRequest = _videoReadyRequest = -1;
        SetImageCommands(false); _exportOriginal.IsEnabled = false; _saveFrame.IsEnabled = false;
        _info = null; ClearBitmaps();
        StopSlideshow();
        HideFileOnlyPlate();
        _status.Text = Texts.T("gallery.mediaUnavailable");
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
    private void SetImageCommands(bool enabled) => _fit.IsEnabled = _actual.IsEnabled = _actualRow.IsEnabled = _zoomIn.IsEnabled = _zoomOut.IsEnabled = _rotate.IsEnabled = enabled;

    private void SetMediaFocus(bool enabled, bool notify = true)
    {
        if (_mediaFocus == enabled) return;
        // A disappearing control must never retain keyboard focus. Navigation and
        // the reveal toggle remain in the same visual tree, including native video.
        if (enabled && (_secondaryTools.Any(control => control.IsKeyboardFocusWithin) || _workViewTools.IsKeyboardFocusWithin || _infoOverlay.IsKeyboardFocusWithin))
            _toolsToggle.Focus(_keyboardInteraction ? NavigationMethod.Tab : NavigationMethod.Pointer);
        _mediaFocus = enabled;
        _playbackPreferences = _playbackPreferences with { MediaFocus = enabled };
        UpdateMediaFocusPresentation();
        RevealOverlayControls();
        if (notify) PlaybackPreferencesChanged?.Invoke(_playbackPreferences);
    }

    private void UpdateMediaFocusPresentation()
    {
        // GL5d (S1): icon only at every width; the icon is the action (hidden tools: show = eye, shown tools: hide = eye-off), the state's
        // label is its name and tooltip.
        string label = Texts.T(_mediaFocus ? "viewer.tools.show" : "viewer.tools.hide");
        CommandBindings.Apply(_toolsToggle, "gallery.tools-toggle", iconOnly: true);
        _toolsToggle.Classes.Set("icon-only", true);
        _toolsToggle.Content = CommandBindings.NamedIcon(_mediaFocus ? "eye" : "eye-off", 16);
        string toolsTip = CommandBindings.Tooltip("gallery.tools-toggle", null, label);
        ToolTip.SetTip(_toolsToggle, toolsTip);
        AutomationProperties.SetName(_toolsToggle, label);
        AutomationProperties.SetHelpText(_toolsToggle, toolsTip);
        _toolsToggle.IsChecked = _mediaFocus;
        ApplyChrome();
    }

    private void SetContextOpen(bool open, bool notify = true, bool hover = false)
    {
        bool hadFocus = _contextPanel.IsKeyboardFocusWithin;
        _hoverContextOpen = hover && open;
        if (!hover && !open && notify) _suppressPostHoverUntilExit = true;
        if (_contextOpen == open) return;
        _contextOpen = open;
        bool tagOnly = !open && ReleaseTagPanel(); // the slot closed under the tag panel: it goes, the post panel's preference stays
        // Deliberate reading stops automatic progression until explicit restart;
        // opening a panel is never permission to pause the native video itself.
        // In the tagging mode the tag panel is part of the work, not reading: the slideshow may go on (it waits while the panel is worked in).
        if (open && SlideshowBlockedByPanel) PauseSlideshow(pauseVideo: false);
        if (!hover) RevealOverlayControls();
        EnsureContextCurrent();
        LayoutContext();
        UpdateContextButton();
        ApplyChrome(); // the info line's rating yields to the panel's rows while the panel is open (QW5)
        RefreshPlaybackControls();
        if (!open && (!hover || hadFocus)) FocusGallerySurface();
        if (notify && !hover && !tagOnly) ContextOpenChanged?.Invoke(open);
    }

    private void ToggleFavorite()
    {
        _favorite.IsChecked = _favoriteSelected;
        ApplyCompactLabel(_favorite, "GalleryFavorite");
        if (!CanRequestFavorite || _entry == null) return;
        FlashFavoriteBadge();
        FavoriteToggleRequested?.Invoke(_entry);
    }

    private void UpdateFavorite()
    {
        string label = Texts.T(_favoriteSelected ? "viewer.favorite.remove" : "viewer.favorite.add");
        _favorite.IsChecked = _favoriteSelected;
        ApplyCompactLabel(_favorite, "GalleryFavorite");
        AutomationProperties.SetName(_favorite, label);
        string tip = FavoriteKeys.Tooltip(_favoriteSelected, "gallery");
        ToolTip.SetTip(_favorite, tip);
        AutomationProperties.SetHelpText(_favorite, tip);
        _favorite.IsEnabled = _entry != null && !_terminalRetirement;
        UpdateFavoriteIndicators();
    }

    private void UpdateContextButton()
    {
        bool postOpen = PostContextVisible; // the tag panel in the slot does not make the post panel's toggle "open"
        string label = Texts.T(postOpen ? "viewer.context.hide" : "viewer.context.show")
            + (CommandCatalog.ShortcutDisplay("gallery.context-toggle") is string gesture ? " · " + gesture : "");
        _contextToggle.IsChecked = postOpen;
        AutomationProperties.SetName(_contextToggle, label);
        ToolTip.SetTip(_contextToggle, label);
        SyncSidePanel();
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

    // F1 and the Help tool open the one shortcuts window of the owner (toggle) instead of the former in-gallery panel.
    private void RequestShortcutHelp() => ShortcutHelpRequested?.Invoke(ShortcutHelp());

    /// <summary>
    /// The context of F1 in the gallery: the Gallery group on top and expanded, the tag panel's group after it, the gallery's
    /// commands in the order of the former help list, which of them the current work view keeps under More, and the
    /// way-back note (it depends on where the gallery returns to and is no shortcut, so it stays as a note).
    /// </summary>
    internal Views.Shortcuts.ShortcutHelpContext ShortcutHelp()
    {
        string[] ids = { "gallery.previous", "gallery.next", "gallery.tools-toggle", "gallery.fit", "gallery.actual-size", "gallery.zoom-in",
            "gallery.zoom-out", "favorites.add", "favorites.remove", "favorites.toggle", "gallery.context-toggle", "video.play-pause",
            "gallery.slideshow-toggle", "gallery.overlay-toggle", "gallery.post-overlay-toggle", "window.fullscreen", "gallery.close" };
        string[] preferred = ids.Concat(CommandCatalog.All.Where(command => command.IsImplemented
                && command.ExecutionContexts.Contains("gallery") && command.ShortcutDisplay != null).Select(command => command.Id))
            .Distinct(StringComparer.Ordinal).Where(id => CommandCatalog.TryGet(id, out _)).ToArray();
        WorkView view = _workView;
        WorkViewPage[] pages = { WorkViewPage.Shell, WorkViewPage.Gallery };
        return new Views.Shortcuts.ShortcutHelpContext(ShortcutGroups.Gallery, preferred, id => WorkViewHelp.UnderMore(id, view, pages),
            CommandScope.None, new[] { "viewer.help.scheme", EscapeHelpKey },
            ExpandedAreas: new[] { ShortcutGroups.TagPanel });
    }

    private void UpdateHelp()
    {
        _helpPanel.IsVisible = _helpOpen;
        _helpContent.Children.Clear();
        _helpTitle.Text = Texts.T("viewer.help.title");
        RefreshCloseLayerButton(_helpClose, Texts.T("viewer.help.close"));
        if (!_helpOpen) return;
        string[] ids = _helpExpanded
            ? new[] { "gallery.previous", "gallery.next", "gallery.tools-toggle", "gallery.fit", "gallery.actual-size", "gallery.zoom-in", "gallery.zoom-out", "favorites.add", "favorites.remove", "favorites.toggle", "gallery.context-toggle", "video.play-pause", "gallery.slideshow-toggle", "gallery.overlay-toggle", "gallery.post-overlay-toggle", "window.fullscreen", "gallery.close" }
            : new[] { "gallery.previous", "gallery.next", "gallery.fit", "favorites.add", "favorites.remove", "gallery.context-toggle", "gallery.slideshow-toggle" };
        foreach (string id in ids.Concat(CommandCatalog.All.Where(command => command.IsImplemented
            && command.ExecutionContexts.Contains("gallery") && command.ShortcutDisplay != null).Select(command => command.Id)).Distinct())
            _helpContent.Children.Add(new TextBlock { Text = id == "gallery.close" ? CommandBindings.Tooltip(id, visibleLabel: CloseLabel) : CommandBindings.Tooltip(id), [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap });
        AddTaggingHelp();
        _helpContent.Children.Add(new TextBlock { Text = Texts.T(EscapeHelpKey), Classes = { "workspace-help" } });
        WrapPanel actions = new() { Classes = { "workspace-toolbar" } };
        if (!_helpExpanded) Command(actions, "GalleryHelpMore", Texts.T("viewer.help.more"), () => SetHelp(true, true));
        if (actions.Children.Count > 0) _helpContent.Children.Add(actions);
    }

    // Unlike _viewerInteractionStopped (also used for reusable detachment), this
    // closes placement admission permanently before terminal stop begins.
    private bool _terminalRetirement;

    private void UseNativeVideoOverlay(bool active)
    {
        if (_terminalRetirement) return;
        active &= OperatingSystem.IsWindows() && !_nativeOverlayUnsupported;
        if (!active) UnbindNativeShortcutWindow();
        if (_nativeOverlayActive == active) return;
        Grid next = active ? _nativeVideoOverlay : _visualRoot;
        MoveOverlayControl(_helpPanel, next);
        MoveOverlayControl(_favoriteConfirmPanel, next);
        _nativeOverlayActive = active;
        _video?.SetNativeOverlay(active ? _nativeVideoOverlay : null);
        ApplyOverlayPlacement();
    }

    private void CheckNativeOverlayTransparency()
    {
        if (_terminalRetirement || !_nativeOverlayActive || TopLevel.GetTopLevel(_nativeVideoOverlay) is not Window overlay) return;
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
        Grid.SetRow(_favoriteConfirmPanel, fallback ? 1 : 0);
        Grid.SetRow(_body, fallback ? 1 : 0);
        Grid.SetRow(_dockStack, fallback ? 2 : 1);
        // In the fallback row 0 lies above the picture and the way back is in the picture row, so the help needs no clearance there.
        _helpPanel.Margin = fallback ? new Thickness(12) : new Thickness(12, TopRowClearance, 12, 12);
        ApplyOverlayPlacement();
    }

    internal GalleryViewerState CaptureState() => new(_entry?.Key, _entry?.Post?.PostId, _entry?.Attachment?.Position,
        _manualZoom, _zoom, _panX, _panY, _contextOpen, _actualZoom, _rotation);
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
        _rotation = GalleryRotation.Normalize(state.Rotation);
        double fit = CurrentFit, maxZoom = CurrentGeometry?.MaxZoom ?? Math.Max(16, fit);
        _zoom = _actualZoom && state.PhysicalZoom < fit
            ? Math.Clamp(state.PhysicalZoom, 1, maxZoom)
            : Math.Clamp(double.IsFinite(state.PhysicalZoom) ? state.PhysicalZoom : fit, fit, maxZoom);
        _panX = state.PanX; _panY = state.PanY;
        FitIfAutomatic(); if (_manualZoom) _ = EnsureDetailAsync();
    }

    internal Task ShowAsync(GalleryEntry entry, CancellationToken cancellationToken)
        => ShowMediaAsync(entry, cancellationToken);

    internal static bool CanPanDecodedImage(SavedMediaKind? kind, GalleryImageInfo? info) =>
        info != null && kind is SavedMediaKind.Image or SavedMediaKind.Unknown;

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // VideoView forwards unhandled overlay presses to its owner. In particular,
        // ComboBox opens on release: image panning must not capture that press or
        // steal focus from a native video control before it receives the release.
        if (_viewerInteractionStopped || !_interactionAvailable || _videoSuspended || _mediaFailed
            || _sessionCancellation.IsCancellationRequested || sender is not Control root || !IsEffectivelyVisible) return;
        if (IsVideoView)
        {
            // A video is dragged only while it is zoomed in, and only by the picture itself, never by the transport.
            if (!_manualZoom || !IsOverVideoSurface(e.Source, root) || CurrentGeometry == null)
            {
                FocusSurfaceAfterPlainClick(root, e);
                return;
            }
        }
        else if (ReferenceEquals(root, _nativeVideoOverlay) || !CanPanDecodedImage(_entry?.Kind, _info) || !_image.IsVisible || _image.Source == null)
        {
            FocusSurfaceAfterPlainClick(root, e);
            return;
        }
        if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed) return;
        Focus();
        _dragging = true; _dragPoint = e.GetPosition(root); e.Pointer.Capture(root); e.Handled = true;
    }

    // A plain click on the picture (not on a control of the transport) gives the keys a target again: with no focused element the
    // keys would start at the Window and miss the viewer's own handlers.
    private void FocusSurfaceAfterPlainClick(Control root, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(visual => visual is Button or Slider or TextBox or ComboBox)) return;
        (ReferenceEquals(root, _nativeVideoOverlay) ? (Control)_nativeVideoOverlay : this).Focus(NavigationMethod.Pointer);
    }

    private async Task ShowMediaAsync(GalleryEntry entry, CancellationToken cancellationToken, bool? resumeAutoplay = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_filmstripContextTarget is { } contextTarget && contextTarget.Key != entry.Key) CancelFilmstripContextRequest();
        int filmstripContextRequest = _filmstripContextRequest;
        _favoriteGestureKey = null; // a new medium starts with no held favourite key
        // A retiring/disabled transport otherwise leaves FocusManager with no target;
        // subsequent keys then originate on Window and never tunnel through the viewer.
        if (_video?.IsKeyboardFocusWithin == true || _video?.TransportControl.IsKeyboardFocusWithin == true
            || _gifPlayPause.IsKeyboardFocusWithin)
            FocusGallerySurface();
        CancelLoads(); int request = ++_request;
        CloseFavoriteConfirm(); // A question never survives a medium or generation change.
        Task gifRetired = RetireGifAsync();
        BeginSlideshowMedia(entry);
        _navigationId = PerformanceDiagnostics.NextId();
        PerformanceDiagnostics.Record("gallery.navigation", "accepted", _navigationId, asset: entry.Asset.AssetId, epoch: _diagnosticEpoch, revision: AssociationRevision);
        // Invalidate old image input before an asynchronous video stop can yield.
        _info = null; SetImageCommands(false); ClearBitmaps();
        _sessionCancellation = cancellationToken; _videoSuspended = false;
        if (_entry?.Key != entry.Key) SetFavoriteError(null);
        InvalidateMediaMenu(); // The menu belongs to the medium it was opened on.
        HideFileOnlyPlate();
        ResetViewForMedium(entry);
        ReleaseTransientOverlays();
        _entry = entry; _manualZoom = _actualZoom = false; _panX = _panY = 0;
        _exportOriginal.IsEnabled = true;
        _saveFrame.IsEnabled = false; // a frame can be saved once the video is ready (RefreshFrameCommands)
        RefreshTaggingStrip(); // the decisions follow the shown medium (PT3)
        ResetMediaTags(); // A new medium's tags are unknown until the owner reports them.
        _favoriteKnown = _favoritePending = _favoriteSelected = false;
        UpdateFavorite();
        _ratingValue = null; _ratingError = null; _ratingAvailable = false; _ratingPending = false; _labelColor = null;
        _ratingStatus.Text = string.Empty; ClearRatingSaved();
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
                _video.ConfigureScrubPreview(_scrubPreviewCache);
                _video.ConfigureSeekStep(_videoSeekStepSeconds);
                _video.CleanupDiagnostic += message =>
                {
                    if (!_viewerInteractionStopped && !_sessionCancellation.IsCancellationRequested && _videoDiagnosticEntry != null)
                        DiagnosticNotice?.Invoke(new("video-cleanup", _videoDiagnosticEntry, _videoDiagnosticRevision, message, true));
                };
                _video.VolumeChanged += volume => { _videoVolume = volume; VideoVolumeChanged?.Invoke(volume); };
                // The transport hands the keys back after a pointer action of its own (the player attaches PointerFocusReclaim to it); the
                // viewer's own scope leaves it out so one click never reclaims twice.
                PointerFocusReclaim.SetExempt(_video.TransportControl, true);
                _video.ShortcutFocusRequested += ReclaimGallerySurface;
                _video.DurationChanged += OnVideoDurationChanged;
                _video.LoopChanged += OnVideoLoopChanged;
                _video.TransportControl.SizeChanged += (_, _) => { if (_contextOpen) LayoutContextPlacement(); };
                WorkViewElement.SetId(_video.TransportControl, "gallery.video.transport"); Mark(_video.TransportControl);
                WorkViewElement.SetId(_video.PlayerMenuControl, "gallery.video.player-menu"); Mark(_video.PlayerMenuControl);
                _video.FullscreenRequested += () => FullscreenRequested?.Invoke();
                _video.SaveFrameRequested += RequestSaveFrame;
                _video.RotateRequested += () => RotateView();
                _video.ViewSurfaceChanged += OnVideoViewSurfaceChanged;
                UseNativeVideoOverlay(true);
                _viewport.Children.Add(_video);
            }
            else UseNativeVideoOverlay(true);
            if (_nativeOverlayUnsupported) SetVideoFallbackLayout(true);
            _videoDiagnosticEntry = entry; _videoDiagnosticRevision = AssociationRevision;
            BindVideoPlaybackSignals(request);
            ApplyEffectiveVideoLoop();
            _video.Configure(resumeAutoplay ?? EffectiveVideoAutoplay, _videoStartMuted, _videoVolume);
            _image.IsVisible = _status.IsVisible = false; _video.IsVisible = true;
            ApplyOverlayPlacement();
            if (_nativeOverlayUnsupported)
                Dispatcher.UIThread.Post(() => _nativeOverlayWindow?.Hide(), DispatcherPriority.Loaded);
            if (_nativeOverlayActive)
                Dispatcher.UIThread.Post(CheckNativeOverlayTransparency, DispatcherPriority.Loaded);
            _video.PrepareRotation(_rotation);
            await _video.ShowAsync(entry, cancellationToken);
            if (request != _request || cancellationToken.IsCancellationRequested) return;
            _videoReadyRequest = request;
            _slideshow.VideoReady(_slideshowGeneration);
            // Initial selection already honored autoplay. Only a slideshow explicitly started
            // during loading may still need Play; never Pause because progression is stopped.
            if (_slideshow.IsRunning) await _video.PlayForSlideshowAsync();
            RefreshPlaybackControls();
            CompleteFilmstripContextRequest(entry, filmstripContextRequest);
            return;
        }
        ApplyOverlayPlacement();
        if (entry.Kind == SavedMediaKind.Other)
        {
            StopSlideshow();
            ShowFileOnlyPlate(entry);
            CompleteFilmstripContextRequest(entry, filmstripContextRequest);
            return;
        }
        await RequestDecodeAsync(CurrentFitRequest(), debounce: false);
        CompleteFilmstripContextRequest(entry, filmstripContextRequest);
    }

    /// <summary>The display-size target the viewer decodes for now; the preloader decodes the neighbors at the same size.</summary>
    internal GalleryDecodeRequest.Fit PreloadFitRequest => CurrentFitRequest(oriented: false);

    private GalleryDecodeRequest.Fit CurrentFitRequest(bool oriented = true)
    {
        Size available = _viewport.Bounds.Size;
        if (available.Width <= 0 || available.Height <= 0)
            available = TopLevel.GetTopLevel(this)?.ClientSize ?? new Size(1280, 720);
        // A picture turned a quarter fits its swapped box: decode for the orientation it is shown in.
        if (oriented && GalleryRotation.IsQuarterTurn(_rotation)) available = new Size(available.Height, available.Width);
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
        if (!PostContextVisible) { _contextDirty = true; return; }
        BuildContext();
    }

    private void EnsureContextCurrent()
    {
        if (_contextDirty && PostContextVisible) BuildContext();
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
        Button ContextCommand(Panel panel, string name, string label, Action action) => Command(panel, name, label, () =>
        {
            if (generation == _contextGeneration && _contextOpen) action();
        });
        // V2: the post as a card. The host's header (segment and ×) is GallerySidePanel; the card starts with where the post is.
        try
        {
            var post = entry.Post;
            AddContext(entry.Key.Thread.Source + " /" + entry.Key.Thread.Board + "/ · " + entry.Key.Thread.Thread
                + (post != null ? " · #" + post.PostId : string.Empty), "workspace-meta", "GalleryPostWhere");
            // Review 15.11.: favourite and stars, then the medium's tags, stand on top of the card, with or without a post (GalleryViewer.PostPanel.cs).
            _context.Children.Add(PostDecisionSection());
            _context.Children.Add(BuildPostTagsSection(ContextCommand));
            _context.Children.Add(PostRule());
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
                    InvalidateMediaMenu(); _entry = associations[selector.SelectedIndex]; UpdateContext(); AssociationRequested?.Invoke(_entry);
                };
                _context.Children.Add(selector);
            }
            if (post == null)
            {
                IReadOnlyList<string>? provider = ProviderContext?.Invoke(entry);
                if (provider == null) AddContext(Texts.T("gallery.noPost"));
                else foreach (string line in provider) AddContext(line);
                AddFileFacts(entry);
                return;
            }
            string byline = string.Join(" · ", new[] { ArchivePresentation.PlainText(post.Author), post.PostedAt?.ToString("g", Texts.Culture) }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            if (byline.Length > 0) AddContext(byline, "workspace-meta", "GalleryPostByline");
            string subject = ArchivePresentation.PlainText(post.Subject);
            if (!string.IsNullOrWhiteSpace(subject)) AddContext(subject, null, "GalleryPostSubject").FontWeight = FontWeight.SemiBold;
            string text = ArchivePresentation.PlainText(post.Html);
            if (!string.IsNullOrWhiteSpace(text)) AddContext(text, null, "GalleryPostText");
            // Quotes as a row of offered chips; a click opens the quoted post as before.
            if (Uri.TryCreate(entry.ThreadUrl, UriKind.Absolute, out Uri? threadUrl) && threadUrl.Scheme is "http" or "https")
            {
                WrapPanel quotes = new() { Name = "GalleryPostQuotes", ItemSpacing = 8, LineSpacing = 8 };
                foreach (ArchiveQuote quote in ArchiveQuoteParser.Parse(entry.Key.Thread, entry.ThreadUrl!, post))
                {
                    if (quote.Target == null || quote.IsExternal) continue;
                    Button chip = ContextCommand(quotes, "GalleryQuote", quote.Label, () => { PauseSlideshow(); QuoteRequested?.Invoke(quote); });
                    CommandBindings.BindExisting(chip, "reader.open-quote"); // a key bound to "Show quoted post" opens the focused chip, as in the reader
                    WorkspaceChips.MarkOffered(chip);
                    chip.Tag = quote.Target;
                }
                if (quotes.Children.Count > 0) _context.Children.Add(quotes);
            }
            AddFileFacts(entry);
            // The action at the bottom left: the post in its thread (T).
            StackPanel actions = new() { Name = "GalleryPostActions", Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            // I1b: the one action of the post card carries its command icon before the text (the label stays the content and the name).
            CommandBindings.ShowIconBeforeText(
                ContextCommand(actions, "GalleryReadPost", Texts.T("gallery.viewer10"), () => { PauseSlideshow(); ReadPostRequested?.Invoke(entry); }),
                CommandCatalog.Get("gallery.show-in-thread").Icon);
            _context.Children.Add(actions);
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
                (replacement ?? _context.GetVisualDescendants().OfType<Button>().FirstOrDefault())?.Focus(NavigationMethod.Tab);
            }
        }
    }

    // The file of the card: its name, the saved size (KiB/MiB with the exact byte count), its provenance and the historical note.
    private void AddFileFacts(GalleryEntry entry)
    {
        AddContext(entry.Asset.OriginalFileName ?? entry.Asset.RequestedFileName, "workspace-meta", "GalleryPostFile");
        AddContext(MediaProvenanceText.LocalSize(entry.Asset.Length), "workspace-meta", "GalleryPostFileSize");
        string provenance = MediaProvenanceText.Context(entry.AttachmentResolution?.Binding);
        if (provenance.Length > 0) AddContext(provenance);
        if (entry.Historical) AddContext(Texts.T("gallery.viewer8"), "workspace-help");
    }
    /// <summary>A "×" that closes the layer it sits in (help, post panel): 32×32, top right, tooltip "{what it does} · Esc".</summary>
    private static Button CloseLayerButton(string name, Action close)
    {
        Button button = new() { Name = name, Classes = { "command-icon" }, Width = 32, Height = 32, MinWidth = 32,
            Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        Border icon = (Border)CommandBindings.Icon("gallery.close", 14); // the "x" glyph
        icon.Bind(Border.BackgroundProperty, icon.GetObservable(TextElement.ForegroundProperty));
        button.Content = icon;
        button.Click += (_, _) => close();
        return button;
    }

    private static void RefreshCloseLayerButton(Button? button, string label)
    {
        if (button == null) return;
        string tip = Texts.T("viewer.closeLayer", label);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, tip);
        ToolTip.SetTip(button, tip);
    }

    private SelectableTextBlock AddContext(string text, string? style = null, string? name = null)
    {
        SelectableTextBlock line = new() { Text = text, Name = name, TextWrapping = TextWrapping.Wrap };
        // The meta and help classes bring their own muted foreground; the post's own text stays the full text colour.
        if (style == null) line[!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        else line.Classes.Add(style);
        _context.Children.Add(line);
        return line;
    }
    private void LayoutContext()
    {
        if (_terminalRetirement) return;
        LayoutContextPlacement();
        FitIfAutomatic();
    }
    private void Move(int delta)
    {
        CancelFilmstripContextRequest();
        if (!(delta < 0 ? _previous.IsEnabled : _next.IsEnabled)) return;
        _browseQueue.Clear(); // an explicit move supersedes wheel turns still waiting
        _navigationPending = true; // No old-media timer while the shell resolves the next entry.
        MoveRequested?.Invoke(delta);
    }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Every key here is handled in the gallery's context: a default that a saved binding of another page took away (a saved archive command on F, say)
        // stays active in the gallery, where nothing else has it (EffectiveShortcutBindings.YieldedContexts).
        using var keyContext = EffectiveShortcutBindings.EnterKeyContext("gallery");
        ForgetContextKeyUp(e);
        RouteKeyDown(sender, e);
    }
    private void RouteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing) return;
        Control routingOwner = sender is Control control ? control : this;
        if (!CanRouteGalleryShortcut(routingOwner, e)) return;
        RouteAdmittedKey(routingOwner, e);
    }

    /// <summary>
    /// The gallery's key policy for a key the window admitted (<see cref="CanRouteGalleryShortcut"/>). Tests run it on a viewer built without a
    /// window, with the real key on the real source element (entry point audit, 16.11.2026).
    /// </summary>
    internal void RouteKeyForTests(KeyEventArgs e)
    {
        using var keyContext = EffectiveShortcutBindings.EnterKeyContext("gallery");
        ForgetContextKeyUp(e);
        if (!e.Handled && !ShortcutService.IsCapturing) RouteAdmittedKey(this, e);
    }

    private void RouteAdmittedKey(Control routingOwner, KeyEventArgs e)
    {
        if (RevealForKeyboard(e)) return;
        if (CommandBindings.Matches(e, "window.fullscreen")) { FullscreenRequested?.Invoke(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "viewer.shortcut-help"))
        { RequestShortcutHelp(); e.Handled = true; return; }
        if (_helpOpen) return;
        if (IsFavoriteConfirmOpen) { RouteFavoriteConfirmKey(e); return; }
        // The Menu key and Shift+F10 open the same media menu as a right-click, for the medium shown now.
        // A focused control with a menu of its own (a tag chip, the slideshow toggle, the speed button) opens that one, as its right-click does
        // (sweep S2 F4). With the focus on another control of the dock (a tool, the info line; not the filmstrip or the video bar) they open the
        // layout menu, as a right-click on free dock area does (GL3). The handled key's key-up is consumed (ConsumeContextKey): no second menu.
        if (MeowChan.Desktop.MediaActions.MediaMenuPresenter.IsContextKey(e) && ContextKeyOwner(e.Source) is { } menuOwner && OpenOwnContextMenu(menuOwner))
        { ConsumeContextKey(e); return; }
        if (MeowChan.Desktop.MediaActions.MediaMenuPresenter.IsContextKey(e) && IsLayoutMenuKeySource(e.Source) && ShowLayoutContextMenu())
        { ConsumeContextKey(e); return; }
        if (_entry != null && MeowChan.Desktop.MediaActions.MediaMenuPresenter.IsContextKey(e)) { ShowMediaMenu(); ConsumeContextKey(e); return; }
        if (RouteDecisionRowKey(e)) return; // the stars of the panel rove with the arrows and Esc returns to the picture
        // The tagging mode's card keys are the bare digits 1 to 5 while the cards are shown: they come before the rating's 1 to 5 (Lightroom keys).
        if (_tagging != null && _cardsOn && _tagSurface?.IsKeyboardFocusWithin != true && RouteCardKey(e)) return;
        if ((ReferenceEquals(routingOwner, _nativeVideoOverlay) ? _nativeRatingRouter : _ratingRouter)?.Route(e) == true) return;
        if (_contextOpen && _sidePanel.RouteSegmentKey(e)) return;
        if (_tagging != null && RouteTaggingKey(e)) return; // the tagging mode's keys come first (PT3)
        if (_tagPanelOpen && RouteTagPanelKey(e)) return;
        // This is part of the existing gallery handler, not a second router. Only
        // actual owned transport sliders may opt into these four effective commands.
        if (_entry?.Kind == SavedMediaKind.Video && _video?.IsTransportInput(e.Source) == true)
        {
            foreach (var (id, seconds) in SeekCommands)
                if (_video.MatchesTransportShortcut(e, id)) { SeekVideo(seconds); e.Handled = true; return; }
            if (_video.MatchesTransportShortcut(e, "gallery.previous")) { Move(-1); e.Handled = true; return; }
            if (_video.MatchesTransportShortcut(e, "gallery.next")) { Move(1); e.Handled = true; return; }
            if (_video.MatchesTransportShortcut(e, "video.volume-up")) { AdjustVideoVolume(1); e.Handled = true; return; }
            if (_video.MatchesTransportShortcut(e, "video.volume-down")) { AdjustVideoVolume(-1); e.Handled = true; return; }
        }
        if (!IsGalleryShortcutInput(e)) return;
        if (HandleFilmstripKey(e) || HandleLayoutKey(e)) return;
        if (CommandBindings.Matches(e, "gallery.mode-toggle")) { RequestGalleryMode(!IsTagMode); e.Handled = true; return; }
        foreach (var (_, id) in PlaybackActions())
            if (CommandBindings.Matches(e, id)) { e.Handled = true; ExplainBlockedPlaybackKey(id); _ = ExecutePlaybackActionAsync(id); return; }
        if (CommandBindings.Matches(e, "favorites.add")) { RequestFavorite(true); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "favorites.remove")) { RequestFavorite(false); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "favorites.toggle")) { RouteFavoriteToggleKey(e); return; }
        if (CommandBindings.Matches(e, "gallery.tools-toggle")) { SetMediaFocus(!_mediaFocus); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.context-toggle")) { ToggleContext(); e.Handled = true; return; }
        if (CommandBindings.Matches(e, "gallery.context-close")) { CloseContextByCommand(); e.Handled = true; return; }
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
            foreach (var (id, seconds) in SeekCommands)
                if (CommandBindings.Matches(e, id)) { SeekVideo(seconds); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.step-back")) { SeekVideoByStep(forward: false); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.step-forward")) { SeekVideoByStep(forward: true); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.speed-down")) { _video.StepRate(-1); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.speed-up")) { _video.StepRate(1); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.speed-reset")) { _video.StepRate(0); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.snapshot")) { RequestSaveFrame(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.ab-loop")) { _video.ToggleAbLoop(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.frame-step")) { _ = _video.StepFrameAsync(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.speed-choose")) { RevealControlsForCommand(); _video.OpenSpeedMenu(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.audio-track")) { _video.CycleAudioTrack(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.loop")) { ToggleVideoLoopByKey(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.play")) { _ = _video.PlayAsync(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.pause")) { _ = _video.PauseAsync(); e.Handled = true; return; }
            if (CommandBindings.Matches(e, "video.stop")) { _ = _video.StopPlaybackAsync(); e.Handled = true; return; }
        }
        if (CommandBindings.Matches(e, "gallery.previous")) Move(-1);
        else if (CommandBindings.Matches(e, "gallery.next")) Move(1);
        else if (CommandBindings.Matches(e, "gallery.zoom-in")) Zoom(1.1);
        else if (CommandBindings.Matches(e, "gallery.zoom-out")) Zoom(1 / 1.1);
        else if (CommandBindings.Matches(e, "gallery.rotate")) RotateView();
        else if (CommandBindings.Matches(e, "gallery.close")) CloseRequested?.Invoke();
        else if (CommandBindings.Matches(e, "media.tags.edit")) RequestTagsByKey();
        else if (CommandBindings.Matches(e, "media.queue.add")) { if (_entry != null && QueueRequested != null) QueueRequested.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "media.export-original")) { if (_entry != null) ExportOriginalRequested?.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "media.show-in-folder")) { if (_entry != null) ShowInFolderRequested?.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "media.copy-path")) { if (_entry != null) CopyPathRequested?.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "media.open-external")) { if (_entry != null) OpenExternalRequested?.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "media.details")) { if (_entry != null) DetailsRequested?.Invoke(_entry); }
        else if (CommandBindings.Matches(e, "gallery.show-in-thread"))
        {
            if (_entry?.Post != null) { PauseSlideshow(); ReadPostRequested?.Invoke(_entry); }
            else if (_entry != null) ExplainBlocked(MeowChan.Desktop.MediaActions.MediaMenuReasons.NoPost); // as the menu entry says
        }
        else if (CommandBindings.Matches(e, ReaderPlaybackMenu.GalleryExtras.PostBookmarkCommandId)) { if (_entry != null && PostBookmarkRequested != null) RequestPostBookmark(_entry); }
        else
        {
            // A command that exists only as a button of the hidden footer: the controls come first, then the existing executor runs.
            if (OverlayControlsEffective && _overlayHidden && CommandBindings.HasActionBehind(this, _overlayFooter, e)) RevealControlsForCommand();
            CommandBindings.RouteVisibleActions(this, e);
            return;
        }
        e.Handled = true;
    }

    private static readonly (string Id, int Seconds)[] SeekCommands =
    {
        ("video.seek-back", -5), ("video.seek-forward", 5), ("video.seek-back-far", -30), ("video.seek-forward-far", 30)
    };

    private bool CanUseVideoTransport => _interactionAvailable && !_viewerInteractionStopped && !_videoSuspended && !_mediaFailed
        && !_navigationPending && !_sessionCancellation.IsCancellationRequested && _entry?.Kind == SavedMediaKind.Video && _video != null;

    private void SeekVideo(int seconds)
    {
        if (CanUseVideoTransport) _video!.SeekRelative(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Raised for "Save frame as image". The owner decides the folder: the dedicated frames folder the person chose
    /// (asked for on first use, never an export folder) and then calls <see cref="SaveVideoFrameAsync"/>.
    /// </summary>
    internal event Action<GalleryEntry>? SaveFrameRequested;

    private void RequestSaveFrame()
    {
        if (CanUseVideoTransport && _videoReadyRequest == _request && _entry != null) SaveFrameRequested?.Invoke(_entry);
    }

    /// <summary>
    /// Saves the shown frame of the current video into <paramref name="directory"/> as "{file name}-{m-ss-mmm}.png" (a free
    /// name, never an existing file) and returns its path, or null when no frame could be saved.
    /// </summary>
    internal async Task<string?> SaveVideoFrameAsync(string directory)
    {
        if (!CanUseVideoTransport || _entry is not { } entry || _video is not { } video) return null;
        if (!await Task.Run(() => System.IO.Directory.Exists(directory)).ConfigureAwait(true)) return null;
        if (!CanUseVideoTransport || !ReferenceEquals(entry, _entry) || !ReferenceEquals(video, _video)) return null;
        string fileName = entry.Asset.OriginalFileName ?? entry.Asset.RequestedFileName ?? string.Empty;
        long position = video.PositionMs;
        // Written under a temporary name and moved into place without replacing a file (a frame is never overwritten).
        string temporary = GalleryFrameFile.TemporaryPath(directory);
        bool saved = await video.SaveFrameAsync(temporary).ConfigureAwait(true) && ReferenceEquals(entry, _entry);
        string? path = saved ? await Task.Run(() => GalleryFrameFile.Commit(temporary, directory, fileName, position)).ConfigureAwait(true) : null;
        if (!saved) await Task.Run(() => GalleryFrameFile.Discard(temporary)).ConfigureAwait(true);
        return path;
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
            || !host.OwnedWindows.Any(window => window.IsVisible && !OwnsOverlayWindow(window) && !Views.Shortcuts.ShortcutsWindow.IsPassive(window));
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
        window.AddHandler(KeyUpEvent, OnFavoriteKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Deactivated += OnHostWindowDeactivated;
    }

    private void UnbindNativeShortcutWindow()
    {
        _nativeShortcutWindow?.RemoveHandler(KeyDownEvent, OnUnfocusedNativeWindowKeyDown);
        _nativeShortcutWindow?.RemoveHandler(KeyUpEvent, OnFavoriteKeyUp);
        if (_nativeShortcutWindow != null) _nativeShortcutWindow.Deactivated -= OnHostWindowDeactivated;
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
            source.GetVisualAncestors().Prepend(source).OfType<ComboBox>()
                .Any(combo => combo.IsDropDownOpen || combo.IsKeyboardFocusWithin))
        {
            // Native ComboBox handling closes the drop-down before this parent
            // handler runs on some platforms. Keep that same Escape from falling
            // through to the shell and closing the gallery as a second action.
            e.Handled = true;
            return;
        }
        if (ConsumeRecentlyClosedPlaybackMenuEscape(e)) return;
        ShellKeyRequested?.Invoke(this, e);
    }
    private double DisplayScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    private void CancelLoads() { ++_decodeGeneration; _decodeQueue.Cancel(); _desiredDecode = null; _pendingOriginalGeneration = null; }
    private void DisposeBitmap(Bitmap? bitmap, GalleryAssetKey? key, long request, long revision)
    {
        if (bitmap == null) return;
        using var measurement = PerformanceDiagnostics.Measure("gallery.bitmap.dispose", request, asset: key?.AssetId, epoch: _diagnosticEpoch, revision: revision);
        GalleryImageCache.Shared.Release(bitmap); // Kept display-size images stay in the cache; any other bitmap is disposed.
    }
    private void ClearBitmaps()
    {
        _image.Source = null;
        DisposeBitmap(_highBitmap, _entry?.Key, _navigationId, AssociationRevision); _highBitmap = null;
        DisposeBitmap(_fitBitmap, _entry?.Key, _navigationId, AssociationRevision); _fitBitmap = null;
    }
    private VideoPlaybackMode? _suspendedPlaybackMode;
    internal async Task SuspendMediaAsync()
    {
        // Reader suspension retires the drawable/input, not the user's play/pause choice.
        // Returning must not turn a paused video into autoplay because config still says On.
        if (!_videoSuspended) _suspendedPlaybackMode = _video?.PlaybackMode;
        PauseSlideshow();
        CloseFavoriteConfirm();
        _gif?.SetActive(false);
        _videoSuspended = _entry?.Kind == SavedMediaKind.Video;
        if (_videoSuspended && _video is { } video)
        {
            int request = _request;
            await video.StopAsync();
            // Hiding only GalleryHost changes effective ancestor visibility, not
            // VideoView's own visibility notification for its separate overlay.
            // Never let an old suspension hide a resumed/replaced selection.
            if (_videoSuspended && request == _request && !_terminalRetirement && ReferenceEquals(video, _video))
                video.IsVisible = false;
        }
    }
    internal Task ResumeMediaAsync()
    {
        UpdateGifPresentation();
        if (!_videoSuspended || _entry == null || _video == null) return Task.CompletedTask;
        _videoSuspended = false;
        return ShowMediaAsync(_entry, _sessionCancellation, _suspendedPlaybackMode == VideoPlaybackMode.Playing);
    }
    public ValueTask DisposeAsync() => new(StopAsync());

    internal async Task StopAsync()
    {
        _terminalRetirement = true;
        foreach (var reclaim in _pointerFocusReclaims) reclaim.Dispose();
        _pointerFocusReclaims.Clear();
        _favoriteConfirmKey = null; _favoriteGestureKey = null; _favoriteConfirmPanel.IsVisible = false;
        UpdateFavoriteIndicators();
        // Permanently close both owners' placement admission before the FIRST await.
        // In particular, player disposal happens only after GIF retirement below.
        foreach (Control control in new Control[] { _overlayFooter, _chrome, _infoOverlay,
            _contextPanel, _helpPanel, _favoriteConfirmPanel, _controlsEdgeIndicator, _postEdgeIndicator, _close })
            GalleryControlTransfer.Cancel(control);
        RetireFilmstrip();
        CloseLayoutMenu();
        _layoutContextHost?.Dispose();
        _video?.RetireTransportPlacement();
        StopViewerInteraction();
        UnbindVideoPlaybackSignals();
        ++_request; CancelLoads();
        _decodeQueue.Dispose();
        await RetireGifAsync();
        InvalidateMediaMenu();
        ClearBitmaps(); _entry = null; _info = null; _restore = null;
        _provenance.Text = string.Empty; _provenance.IsVisible = false;
        _exportOriginal.IsEnabled = false; _saveFrame.IsEnabled = false;
        _associationIndex = null; _context.Children.Clear(); _contextDirty = false; ++_contextGeneration;
        ReleaseTagPanel(); _contextOpen = false; _videoSuspended = false; SetHelp(false); LayoutContext(); UpdateContextButton();
        if (_video != null) await _video.DisposeAsync();
        // Native stop/disposal has completed. Release the overlay as a subtree;
        // never restore its help/context/chrome/transport to the attached main root.
        UnbindNativeShortcutWindow();
        _video?.SetNativeOverlay(null);
        _nativeOverlayActive = false;
        // Preferences survive this viewer's close; a new instance receives them via ConfigurePlayback.
    }
}

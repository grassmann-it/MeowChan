using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>What the gallery side panel shows: the post card or the quick tag panel. Exactly one is visible.</summary>
internal enum GallerySidePanelSegment { Post, Tags }

/// <summary>
/// The gallery side panel shows the medium counter at the left above the Post and Tags switches, with the close button at the right.
/// Below the header, only the content slot for <see cref="Segment"/> is visible.
/// A 6-dip grip on the left edge drags the width (<see cref="Config.GalleryPanelWidthMinimum"/>–<see cref="Config.GalleryPanelWidthMaximum"/>).
/// The host only shows state and raises requests: which segment may be shown, what closing does and where the width is kept is
/// the viewer's (GalleryViewer.SidePanel.cs). Focus order: segment, content, close.
/// </summary>
internal sealed class GallerySidePanel : Grid
{
    /// <summary>The key of the preferred width in the layout profile's view preferences.</summary>
    internal const string WidthPreferenceKey = "gallery.panelWidth";
    internal const double GripWidth = 6;
    // The grip lies on the outer edge of the bordered slot (workspace-section, inset 16), not on the content.
    private const double SlotInset = 16;
    private readonly ToggleButton _post = new() { Name = "GalleryPanelPost", Classes = { "media-control" } };
    private readonly ToggleButton _tags = new() { Name = "GalleryPanelTags", Classes = { "media-control" } };
    private readonly WrapPanel _segment = new() { Name = "GalleryPanelSegment", ItemSpacing = 4, LineSpacing = 4,
        ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _headerCounter = new() { Name = "GalleryPanelHeaderCounter", Focusable = false,
        HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly ContentControl _postSlot = new() { Name = "GalleryPanelPostContent", Focusable = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly ContentControl _tagSlot = new() { Name = "GalleryPanelTagContent", Focusable = false, IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _grip = new() { Name = "GalleryPanelResize", Width = GripWidth, Background = Brushes.Transparent, Focusable = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
    private GallerySidePanelSegment _shown = GallerySidePanelSegment.Post;
    private bool _tagsAvailable = true;
    private string? _tagsUnavailableReason;
    private double? _dragStartX;
    private double _dragStartWidth;

    internal GallerySidePanel(Button close)
    {
        CloseButton = close ?? throw new ArgumentNullException(nameof(close));
        Name = "GallerySidePanel";
        RowDefinitions = new RowDefinitions("Auto,Auto,*");
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _segment.Children.Add(_post);
        _segment.Children.Add(_tags);
        _segment.Margin = new Thickness(0, 0, 24, 0);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Center;
        // Child order is the focus order: segment, the shown content, then the close button.
        Children.Add(_segment); SetRow(_segment, 1);
        Children.Add(_headerCounter); SetColumnSpan(_headerCounter, 2);
        Children.Add(_postSlot); SetRow(_postSlot, 2); SetColumnSpan(_postSlot, 2);
        Children.Add(_tagSlot); SetRow(_tagSlot, 2); SetColumnSpan(_tagSlot, 2);
        Children.Add(close); SetRow(close, 1); SetColumn(close, 1);
        _grip.Margin = new Thickness(-SlotInset, -SlotInset, 0, -SlotInset);
        Children.Add(_grip); SetRowSpan(_grip, 3);
        _post.Click += (_, _) => OnSegmentClick(GallerySidePanelSegment.Post);
        _tags.Click += (_, _) => OnSegmentClick(GallerySidePanelSegment.Tags);
        _grip.PointerPressed += OnGripPressed;
        _grip.PointerMoved += OnGripMoved;
        _grip.PointerReleased += OnGripReleased;
        _grip.PointerCaptureLost += (_, _) => EndDrag(commit: true);
        // The resize cursor needs the platform's cursor factory, so it is set once the grip is shown in a window.
        _grip.AttachedToVisualTree += (_, _) => _grip.Cursor ??= new Cursor(StandardCursorType.SizeWestEast);
        Localize();
        ApplySegment();
    }

    internal ToggleButton PostButton => _post;
    internal ToggleButton TagsButton => _tags;
    internal Button CloseButton { get; }
    internal Control Grip => _grip;
    internal ContentControl PostSlot => _postSlot;
    internal ContentControl TagSlot => _tagSlot;

    /// <summary>The live medium counter above the Post and Tags switches.</summary>
    internal Control? HeaderCounter
    {
        get => _headerCounter.Content as Control;
        set => _headerCounter.Content = value;
    }

    /// <summary>The post card (built and kept by the viewer).</summary>
    internal Control? PostContent { get => _postSlot.Content as Control; set => _postSlot.Content = value; }

    /// <summary>The tag panel's surface while it is open (TP2 sets the QuickTagSurface here), otherwise null.</summary>
    internal Control? TagContent { get => _tagSlot.Content as Control; set => _tagSlot.Content = value; }

    /// <summary>The segment shown now. Setting it only switches visibility; both contents stay instantiated.</summary>
    internal GallerySidePanelSegment Segment
    {
        get => _shown;
        set { _shown = value; ApplySegment(); }
    }

    /// <summary>
    /// Raised when the person picks a segment (click or ←/→). The viewer decides and sets <see cref="Segment"/>. The flag says whether
    /// the focus goes into the content (a click) or stays on the segment (the arrow keys, so the next arrow still works).
    /// </summary>
    internal event Action<GallerySidePanelSegment, bool>? SegmentRequested;

    /// <summary>Raised while the grip is dragged, with the clamped width the panel asks for.</summary>
    internal event Action<double>? WidthDragged;

    /// <summary>Raised once when a drag ends, with the last clamped width (the one to keep).</summary>
    internal event Action<double>? WidthCommitted;

    /// <summary>The Tags segment is offered only when the shown medium can be tagged; the reason is its tooltip otherwise.</summary>
    internal void SetTagsAvailable(bool available, string? reason)
    {
        if (_tagsAvailable == available && _tagsUnavailableReason == reason) return;
        _tagsAvailable = available;
        _tagsUnavailableReason = available ? null : reason;
        Localize();
        ApplySegment();
    }

    /// <summary>Labels, accessible names and tooltips from the current language and shortcuts.</summary>
    internal void Localize()
    {
        Present(_post, Texts.T("gallery.panel.post"), "gallery.context-toggle", null);
        Present(_tags, Texts.T("gallery.panel.tags"), "media.tags.edit", _tagsAvailable ? null : _tagsUnavailableReason);
        AutomationProperties.SetName(_segment, Texts.T("gallery.panel.segments"));
        AutomationProperties.SetHelpText(_segment, Texts.T("gallery.panel.segmentHelp"));
        string resize = Texts.T("gallery.panel.resize", Config.GalleryPanelWidthMinimum, Config.GalleryPanelWidthMaximum);
        ToolTip.SetTip(_grip, resize);
        AutomationProperties.SetName(_grip, resize);
    }

    private static void Present(ToggleButton button, string label, string commandId, string? reason)
    {
        if (button.Content is TextBlock text) text.Text = label;
        else button.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(button, label);
        string tip = CommandCatalog.ShortcutDisplay(commandId) is string gesture ? label + " · " + gesture : label;
        if (reason != null) tip = reason;
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetHelpText(button, tip);
    }

    private void ApplySegment()
    {
        bool tags = _shown == GallerySidePanelSegment.Tags;
        _postSlot.IsVisible = !tags;
        _tagSlot.IsVisible = tags;
        _post.IsChecked = !tags;
        _tags.IsChecked = tags;
        // The shown segment stays usable (it is the current state); the other is offered when available.
        _tags.IsEnabled = tags || _tagsAvailable;
    }

    private void OnSegmentClick(GallerySidePanelSegment segment)
    {
        ApplySegment(); // a ToggleButton flips itself on click; the shown state is the viewer's
        if (segment != _shown) SegmentRequested?.Invoke(segment, true);
    }

    /// <summary>
    /// ←/→ while a segment button has the focus: Left picks Post, Right picks Tags (when available) and the focus moves to that button.
    /// Returns true when handled, so the gallery does not page to the next medium.
    /// </summary>
    internal bool RouteSegmentKey(KeyEventArgs e)
    {
        if (e.Handled || ArrowTarget(e.Key, e.KeyModifiers) is not { } target) return false;
        if (!_post.IsFocused && !_tags.IsFocused) return false;
        // An arrow pointing at the segment that is already shown is still a gallery
        // navigation command. The post edge indicator focuses the Post segment;
        // swallowing its first Left would make the native key look like a reveal-only
        // press instead of moving to the previous medium.
        if (target == _shown) return false;
        e.Handled = true;
        if (target == GallerySidePanelSegment.Tags && !_tagsAvailable) return true;
        SegmentRequested?.Invoke(target, false);
        (_shown == GallerySidePanelSegment.Tags ? _tags : _post).Focus(NavigationMethod.Directional);
        return true;
    }

    /// <summary>The segment an arrow key picks (Left: Post, Right: Tags), or null for any other key or with a modifier.</summary>
    internal static GallerySidePanelSegment? ArrowTarget(Key key, KeyModifiers modifiers) => modifiers != KeyModifiers.None ? null
        : key == Key.Left ? GallerySidePanelSegment.Post : key == Key.Right ? GallerySidePanelSegment.Tags : null;

    /// <summary>Moves the focus to the first focusable control of the shown content (a selectable text only when nothing else is there).</summary>
    internal bool FocusContent()
    {
        ContentControl slot = _shown == GallerySidePanelSegment.Tags ? _tagSlot : _postSlot;
        InputElement[] candidates = slot.GetLogicalDescendants().OfType<InputElement>()
            .Where(element => element.Focusable && element.IsEffectivelyEnabled && element.IsEffectivelyVisible).ToArray();
        InputElement? target = candidates.FirstOrDefault(element => element is not SelectableTextBlock) ?? candidates.FirstOrDefault();
        return target?.Focus(NavigationMethod.Tab) == true;
    }

    // ---- width ----

    /// <summary>A wished width within the panel's limits; anything not finite is the default.</summary>
    internal static double ClampWidth(double requested) => double.IsFinite(requested)
        ? Math.Clamp(Math.Round(requested), Config.GalleryPanelWidthMinimum, Config.GalleryPanelWidthMaximum)
        : Config.GalleryPanelWidthDefault;

    /// <summary>The width a drag asks for: the grip is on the left edge of a right-aligned panel, so moving left widens it.</summary>
    internal static double DragWidth(double startWidth, double startX, double currentX) => ClampWidth(startWidth + startX - currentX);

    /// <summary>
    /// The width the panel gets in a viewer of <paramref name="viewerWidth"/>. Docked (from 900 dip) it keeps the wish while the picture
    /// keeps <paramref name="minimumPicture"/>; laid over the picture below 900 dip it keeps that strip of the picture free as far as the
    /// minimum width allows (the existing narrow rule); with the overlay option it only has to fit the viewer.
    /// </summary>
    internal static double EffectiveWidth(double preferred, double viewerWidth, bool overlayOption, bool narrow, double minimumPicture)
    {
        double wish = ClampWidth(preferred);
        if (viewerWidth <= 0) return wish;
        double fit = Math.Max(0, viewerWidth - 24);
        if (overlayOption) return Math.Min(wish, fit);
        if (narrow) return Math.Min(fit, Math.Clamp(viewerWidth - minimumPicture - 24, Config.GalleryPanelWidthMinimum, wish));
        return Math.Min(wish, Math.Max(0, viewerWidth - minimumPicture - 8));
    }

    private Control? Slot => Parent as Control;

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed || Slot is not { } slot) return;
        _dragStartX = e.GetPosition(null).X;
        _dragStartWidth = slot.Bounds.Width > 0 ? slot.Bounds.Width : Config.GalleryPanelWidthDefault;
        e.Pointer.Capture(_grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartX is not double start) return;
        WidthDragged?.Invoke(DragWidth(_dragStartWidth, start, e.GetPosition(null).X));
        e.Handled = true;
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStartX is not double start) return;
        double width = DragWidth(_dragStartWidth, start, e.GetPosition(null).X);
        _dragStartX = null;
        e.Pointer.Capture(null);
        WidthDragged?.Invoke(width);
        WidthCommitted?.Invoke(width);
        e.Handled = true;
    }

    private void EndDrag(bool commit)
    {
        if (_dragStartX == null) return;
        _dragStartX = null;
        if (commit && Slot is { } slot && slot.Bounds.Width > 0) WidthCommitted?.Invoke(ClampWidth(slot.Bounds.Width));
    }
}

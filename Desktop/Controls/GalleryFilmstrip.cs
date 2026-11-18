using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop.Controls;

/// <summary>What a filmstrip tile says about its medium, from the saved file name alone (no file is read).</summary>
internal enum FilmstripTileKind { Image, Gif, Video }

/// <summary>
/// The filmstrip of the gallery (V3): a band of square thumbnails of every medium of the open list, the shown one marked, scrollable
/// by wheel, drag, the arrows at its ends and the keyboard, and virtualized: only the tiles in view exist as controls, and only the
/// tiles in view plus the preload on each side hold a decoded thumbnail (<see cref="FilmstripThumbnailWindow{T}"/>). A click on a tile
/// asks the viewer to show it (<see cref="TileActivated"/>); the strip never changes the shown medium by itself.
/// <para>The strip owns no list: it reads the viewer's entries by index (the list only grows while a gallery is open) and takes the
/// thumbnails from the shared preview cache through <see cref="ArchiveListThumbnails"/> (local files only; nothing is fetched).</para>
/// </summary>
internal sealed class GalleryFilmstrip : Border
{
    private const double DragThreshold = 4;

    // ---- parts ----
    private readonly FilmstripPanel _strip = new() { Name = "GalleryFilmstripTiles", Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Button _back = new() { Name = "GalleryFilmstripBack" };
    private readonly Button _forward = new() { Name = "GalleryFilmstripForward" };
    private readonly TextBlock _position = new() { Name = "GalleryFilmstripPosition", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly Dictionary<int, FilmstripTile> _live = new();
    private readonly Stack<FilmstripTile> _pool = new();
    private readonly ThumbnailDecodeGate _gate = new(2);
    private FilmstripThumbnailWindow<Bitmap> _thumbnails;

    // ---- state ----
    private IReadOnlyList<GalleryEntry>? _entries;
    private int _count;
    private bool _hasMore;
    private int _current = -1;
    private double _tile = Config.GalleryFilmstripHeightDefault;
    private double _gap = Config.GalleryFilmstripTileGapDefault;
    private bool _pointerOver;
    private int _preload = Config.GalleryFilmstripPreloadDefault;
    private double _offset;
    private double _viewport;
    private int _hovered = -1;
    private bool _pressed, _dragging, _fromStrip, _retired, _followCurrent;
    private Point _pressPoint;
    private double _pressOffset;
    private int _pressedTile = -1;
    // MB1b: the length of a video when the viewer learned it (the strip never reads a file); drawn as the badge's duration chip.
    private readonly Dictionary<int, TimeSpan> _durations = new();
    private bool _keyboardFocus, _backShown, _forwardShown;
    // Filmstrip flags (decision 123): the favourite star, the own rating and the "no tags yet" mark, drawn by the same badge view as the length.
    private readonly FilmstripFlagModel _flags = new();
    private bool _atBottom = true; // GL1: the band's place; only its accessible description depends on it (the viewer sets margins and parent)

    internal GalleryFilmstrip()
    {
        Name = "GalleryFilmstrip";
        Focusable = true;
        IsTabStop = true;
        Height = GalleryFilmstripPlan.BandHeight(_tile);
        CornerRadius = new CornerRadius(5);
        BorderThickness = new Thickness(1);
        BorderBrush = Brushes.Transparent;
        Padding = new Thickness(4, 0);
        _thumbnails = CreateWindow();
        WorkspaceIcons.MakeSquare(_back, WorkspaceIcons.ChevronLeft, string.Empty);
        WorkspaceIcons.MakeSquare(_forward, WorkspaceIcons.ChevronLeft, string.Empty);
        // The forward arrow is the back arrow mirrored: one drawn shape, not a second icon in the shared table.
        if (_forward.Content is Control chevron) { chevron.RenderTransformOrigin = RelativePoint.Center; chevron.RenderTransform = new ScaleTransform(-1, 1); }
        // GL5e: 32 dip squares centred on the band, drawn over the ends of the strip: they show only while there is somewhere to scroll
        // and the pointer is over the band or the keyboard is in it, so the strip keeps its width whether they show or not.
        foreach (Button button in new[] { _back, _forward })
        {
            button.Classes.Add("icon-only");
            button.Classes.Add("filmstrip-chevron"); // GL6: the 80 ms Opacity fade of WorkspaceStyles.axaml (none under reduced motion)
            button.Width = 32; button.Height = 32; button.VerticalAlignment = VerticalAlignment.Center; button.Focusable = false;
            button.IsVisible = false; button.Opacity = 0; button.IsHitTestVisible = false;
        }
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        _forward.HorizontalAlignment = HorizontalAlignment.Right;
        _back.Click += (_, _) => ScrollByUser(GalleryFilmstripPlan.PageOffset(_offset, -1, Slots, _tile, _viewport, _gap));
        _forward.Click += (_, _) => ScrollByUser(GalleryFilmstripPlan.PageOffset(_offset, 1, Slots, _tile, _viewport, _gap));
        _position.Classes.Add("workspace-meta");
        _position.IsVisible = false; // the viewer fills it only while the arrows are away (QW1)
        AutomationProperties.SetAccessibilityView(_position, AccessibilityView.Raw);
        Grid root = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        root.Children.Add(_strip);
        root.Children.Add(_back);
        root.Children.Add(_forward);
        root.Children.Add(_position); Grid.SetColumn(_position, 1);
        Child = root;
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.List);
        _strip.Cursor = new Cursor(StandardCursorType.Hand);
        _strip.SizeChanged += (_, e) =>
        {
            _viewport = e.NewSize.Width;
            // A strip the user has not scrolled keeps the shown medium in its middle when the window is resized.
            ScrollTo(_followCurrent && _current >= 0 ? GalleryFilmstripPlan.CentreOffset(_current, Slots, _tile, _viewport, _gap) : _offset);
        };
        _strip.PointerPressed += OnPointerPressed;
        _strip.PointerMoved += OnPointerMoved;
        _strip.PointerReleased += OnPointerReleased;
        _strip.PointerCaptureLost += (_, _) => { _pressed = _dragging = false; };
        _strip.PointerExited += (_, _) => SetHovered(-1);
        PointerWheelChanged += OnWheel;
        // A right-click on a tile asks for that tile's menu (Menu key and Shift+F10 carry no position and stay with the viewer: the shown medium's).
        AddHandler(ContextRequestedEvent, OnContextRequested, Avalonia.Interactivity.RoutingStrategies.Bubble);
        PointerEntered += (_, _) => { _pointerOver = true; UpdateChevrons(); };
        PointerExited += (_, _) => { _pointerOver = false; UpdateChevrons(); };
        GotFocus += (_, e) => { _keyboardFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional; UpdateFocusVisual(); UpdateChevrons(); };
        LostFocus += (_, _) => { _keyboardFocus = false; UpdateFocusVisual(); UpdateChevrons(); };
        AttachedToVisualTree += (_, _) => { _retired = false; Texts.Changed += OnTextsChanged; Refresh(); };
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnTextsChanged;
        UpdateFocusVisual();
        RefreshTexts();
    }

    // ---- inputs ----

    /// <summary>Loads and leases the thumbnail of one entry (null when it has none). Null (the default) reads the shared preview cache.</summary>
    internal Func<GalleryEntry, CancellationToken, ThumbnailDecodeGate, Task<Bitmap?>>? ThumbnailLoader { get; set; }

    /// <summary>The cache of video posters (the profile's cache directory unless a test supplies another).</summary>
    internal string PosterCacheDirectory { get; set; } = System.IO.Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1");

    /// <summary>
    /// Where the flags of a tile come from (the owner reads the favourite list, the rating list and the tag index it already holds; the strip never
    /// reads a file for them). It is asked only for the flags that are shown, so a strip with every flag off costs nothing per tile.
    /// </summary>
    internal Func<GalleryEntry, FilmstripFlagKinds, FilmstripFlagFacts>? FlagSource
    {
        get => _flags.Source;
        set { if (_flags.Source == value) return; _flags.Source = value; UpdateTileNames(); }
    }

    /// <summary>The user asked for the tile (a click or Enter): the entry's index, or the entry count for the "more" tile.</summary>
    internal event Action<int>? TileActivated;
    /// <summary>The user right-clicked the tile of an entry (never the "more" tile): the entry's index. The strip opens no menu itself.</summary>
    internal event Action<int>? TileContextRequested;
    /// <summary>The user left the strip with Escape or Enter and wants the picture to take the keyboard back.</summary>
    internal event Action? ExitRequested;

    internal int Count => _count;
    internal bool HasMore => _hasMore;
    internal int CurrentIndex => _current;
    internal double TileSize => _tile;
    internal int Preload => _preload;
    internal double ScrollOffset => _offset;
    internal double ViewportWidth => _viewport;
    internal bool IsKeyboardFocused => _keyboardFocus;
    internal IReadOnlyCollection<int> LiveTileIndices => _live.Keys;
    internal int HeldThumbnails => _thumbnails.HeldCount;
    internal int PendingThumbnails => _thumbnails.PendingCount;
    internal bool IsTileLoaded(int index) => _live.TryGetValue(index, out FilmstripTile? tile) && tile.HasPicture;
    internal GalleryFilmstripPlan.TileRange VisibleRange => GalleryFilmstripPlan.Visible(_offset, _viewport, _tile, Slots, _gap);
    internal bool BackEnabled => _back.IsEnabled;
    internal bool ForwardEnabled => _forward.IsEnabled;
    internal double TileGapSize => _gap;
    /// <summary>Whether the arrow shows now: there is somewhere to scroll and the pointer or the keyboard is in the band (tests and smokes).</summary>
    internal bool BackShown => _backShown;
    internal bool ForwardShown => _forwardShown;
    /// <summary>The control of one realized tile (tests and smokes).</summary>
    internal Control? TileControl(int index) => _live.GetValueOrDefault(index);
    internal Control BackButton => _back;
    internal Control ForwardButton => _forward;

    // Tiles of the entries plus the one "more" tile at the end while further threads are still to be loaded.
    private int Slots => _count + (_hasMore ? 1 : 0);

    /// <summary>Gives the strip its list. The same list that grew keeps its thumbnails; another list starts empty.</summary>
    internal void SetEntries(IReadOnlyList<GalleryEntry>? entries, int count, bool hasMore)
    {
        bool same = ReferenceEquals(entries, _entries);
        _entries = entries;
        _count = entries == null ? 0 : Math.Clamp(count, 0, entries.Count);
        _hasMore = hasMore && entries != null;
        if (!same) { ClearTiles(); _thumbnails.Reset(); _durations.Clear(); _current = -1; _offset = 0; }
        RefreshTexts();
        ScrollTo(_offset);
    }

    /// <summary>
    /// Tells the strip how long the video at <paramref name="index"/> is (null forgets it); its tile then shows the length as a chip and says it in
    /// its name. Only a video carries one; nothing is read from the file.
    /// </summary>
    internal void SetDuration(int index, TimeSpan? duration)
    {
        if (index < 0 || index >= _count) return;
        if (duration is { } length && length >= TimeSpan.Zero) { if (_durations.TryGetValue(index, out TimeSpan known) && known == length) return; _durations[index] = length; }
        else if (!_durations.Remove(index)) return;
        if (_live.TryGetValue(index, out FilmstripTile? tile)) BindTile(tile, index);
    }

    /// <summary>Takes the flag choices (Settings, "Filmstrip shows"); the tiles in view draw again only when what is shown changed.</summary>
    internal void ConfigureFlags(FilmstripFlagSettings settings)
    {
        if (_flags.Configure(settings)) UpdateTileNames();
    }

    /// <summary>The gallery's tagging view (the right tag panel) opened or closed: the "no tags yet" mark follows it when the setting says so.</summary>
    internal void SetTaggingViewOpen(bool open)
    {
        if (_flags.SetTaggingViewOpen(open)) UpdateTileNames();
    }

    /// <summary>
    /// A favourite, a rating or a tag changed somewhere: the tiles in view read their flags again (the badge view draws only the tiles whose state
    /// really changed). The owner calls it from the notification it already has; nothing here polls.
    /// </summary>
    internal void RefreshFlags()
    {
        if (!_retired && _flags.Shown != FilmstripFlagKinds.None) UpdateTileNames();
    }

    /// <summary>The flags that are shown now (tests and smokes).</summary>
    internal FilmstripFlagKinds ShownFlags => _flags.Shown;

    /// <summary>The badge state of the tile at <paramref name="index"/> with the length known so far and the flags shown now (tests and smokes); default for the "more" tile.</summary>
    internal MediaBadgeState BadgeStateAt(int index) =>
        index < 0 || index >= _count || _entries == null ? default
            : _flags.StateFor(_entries[index], BadgeStateOf(FileName(_entries[index]), _durations.TryGetValue(index, out TimeSpan length) ? length : null));

    /// <summary>Marks the shown medium and scrolls it into view (centred, or just into view when the strip itself asked for it).</summary>
    internal void SetCurrent(int index)
    {
        index = index < 0 || index >= _count ? -1 : index;
        bool changed = index != _current;
        _current = index;
        foreach (FilmstripTile tile in _live.Values) tile.SetCurrent(tile.Index == _current);
        if (index >= 0 && !_dragging)
        {
            ScrollTo(_fromStrip ? GalleryFilmstripPlan.RevealOffset(_offset, index, Slots, _tile, _viewport, _gap)
                : GalleryFilmstripPlan.CentreOffset(index, Slots, _tile, _viewport, _gap));
            _followCurrent = !_fromStrip;
        }
        else Refresh();
        _fromStrip = false;
        if (changed) UpdateTileNames();
    }

    /// <summary>Tile size (the height setting) and preload (tiles decoded in advance on each side), both clamped to their ranges.</summary>
    internal void Configure(int height, int preload, int gap = Config.GalleryFilmstripTileGapDefault)
    {
        height = GalleryFilmstripPlan.ClampHeight(height);
        preload = GalleryFilmstripPlan.ClampPreload(preload);
        gap = GalleryFilmstripPlan.ClampGap(gap);
        bool resized = height != (int)_tile || gap != (int)_gap;
        if (!resized && preload == _preload) return;
        _preload = preload;
        _gap = gap;
        if (resized)
        {
            _tile = height;
            Height = GalleryFilmstripPlan.BandHeight(_tile);
            ClearTiles();
        }
        ScrollTo(_current >= 0 && resized ? GalleryFilmstripPlan.CentreOffset(_current, Slots, _tile, _viewport, _gap) : _offset);
    }

    /// <summary>The position text at the right end ("3 / 27"); the viewer owns the text. Null or empty hides the block, so no gap stays.</summary>
    internal void SetPositionText(string? text)
    {
        _position.Text = text ?? string.Empty;
        _position.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>Whether the position block shows a text now (tests and smokes).</summary>
    internal bool IsPositionShown => _position.IsVisible;

    /// <summary>Sets the band background (the overlay tone) or clears it (docked).</summary>
    internal void SetBackground(IBrush? background)
    {
        if (background == null) ClearValue(BackgroundProperty);
        else Background = background;
    }

    /// <summary>Scrolls the strip, clamped; realizes the tiles in view and moves the decode window.</summary>
    internal void ScrollTo(double offset)
    {
        _offset = GalleryFilmstripPlan.ClampOffset(offset, Slots, _tile, _viewport, _gap);
        Refresh();
    }

    /// <summary>Stops all work and releases every thumbnail (the viewer was stopped); the strip stays valid but empty.</summary>
    internal void Retire()
    {
        _retired = true;
        ClearTiles();
        _thumbnails.Dispose();
        _thumbnails = CreateWindow();
        _entries = null; _count = 0; _hasMore = false; _current = -1;
    }

    // ---- keyboard ----

    /// <summary>
    /// The keys of a focused strip that the viewer's own routing does not give it: Home/End show the first/last medium, Page Up/Down
    /// scroll by a page, the gallery-open binding activates the focused tile, and Escape returns to the picture. Left/Right stay with the viewer (they step the shown medium,
    /// which the strip follows). Returns whether the key was used.
    /// </summary>
    internal bool HandleKey(KeyEventArgs e)
    {
        if (e.Handled || !IsKeyboardFocusWithin) return false;
        // A focused tile denotes its own medium, even when the viewer is showing another.
        // Enter on the band itself still returns to the current picture; real buttons stay native.
        if (!MeowChan.Desktop.Commands.ShortcutService.IsCapturing
            && MeowChan.Desktop.Commands.ShortcutService.Current.Matches(e, "gallery.open"))
        {
            if (e.Source is FilmstripTile tile)
            {
                _fromStrip = true;
                TileActivated?.Invoke(tile.Index);
                ExitRequested?.Invoke();
                return true;
            }
            if (ReferenceEquals(e.Source, this)) { ExitRequested?.Invoke(); return true; }
            return false;
        }
        if (e.KeyModifiers != KeyModifiers.None) return false;
        switch (e.Key)
        {
            case Key.Home when _count > 0: _fromStrip = true; TileActivated?.Invoke(0); return true;
            case Key.End when _count > 0: _fromStrip = true; TileActivated?.Invoke(_count - 1); return true;
            case Key.PageUp: ScrollByUser(GalleryFilmstripPlan.PageOffset(_offset, -1, Slots, _tile, _viewport, _gap)); return true;
            case Key.PageDown: ScrollByUser(GalleryFilmstripPlan.PageOffset(_offset, 1, Slots, _tile, _viewport, _gap)); return true;
            case Key.Escape: ExitRequested?.Invoke(); return true;
            default: return false;
        }
    }

    /// <summary>The entry of a tile (null for the "more" tile or an index outside the list).</summary>
    internal GalleryEntry? EntryAt(int index) => _entries != null && index >= 0 && index < _count && index < _entries.Count ? _entries[index] : null;

    // ---- pointer ----

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || e.Source is not Visual source || !source.GetSelfAndVisualAncestors().Contains(_strip)
            || !e.TryGetPosition(_strip, out Point position)) return;
        // The pointer is on the tile strip: it is this tile's menu or none (the gap between tiles and the "more" tile never open the shown medium's).
        e.Handled = true;
        int tile = GalleryFilmstripPlan.TileAt(position.X, _offset, _tile, Slots, _gap);
        if (tile >= 0 && tile < _count) TileContextRequested?.Invoke(tile);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(_strip);
        if (!point.Properties.IsLeftButtonPressed) return;
        _pressed = true; _dragging = false;
        _pressPoint = point.Position; _pressOffset = _offset;
        _pressedTile = GalleryFilmstripPlan.TileAt(point.Position.X, _offset, _tile, Slots, _gap);
        e.Pointer.Capture(_strip);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point position = e.GetPosition(_strip);
        if (!_pressed) { SetHovered(GalleryFilmstripPlan.TileAt(position.X, _offset, _tile, Slots, _gap)); return; }
        double moved = position.X - _pressPoint.X;
        if (!_dragging && Math.Abs(moved) >= DragThreshold) { _dragging = true; SetHovered(-1); }
        if (_dragging) ScrollByUser(_pressOffset - moved);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool click = _pressed && !_dragging;
        int tile = click ? GalleryFilmstripPlan.TileAt(e.GetPosition(_strip).X, _offset, _tile, Slots, _gap) : -1;
        _pressed = _dragging = false;
        if (ReferenceEquals(e.Pointer.Captured, _strip)) e.Pointer.Capture(null);
        e.Handled = true;
        if (click && tile >= 0 && tile == _pressedTile) { _fromStrip = tile < _count && tile != _current; TileActivated?.Invoke(tile); }
        _pressedTile = -1;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (StripWheel.Notches(e.Delta) == 0) return;
        // Wheel up (and a pad swipe to the right) look back along the strip, wheel down forward (the notch is the one every strip shares, StripWheel).
        ScrollByUser(StripWheel.NextOffset(_offset, e.Delta, GalleryFilmstripPlan.Pitch(_tile, _gap)));
        e.Handled = true;
    }

    // A scroll by the user (wheel, drag, arrows, Page keys) ends the "keep the shown medium centred" behaviour until the next medium.
    private void ScrollByUser(double offset)
    {
        _followCurrent = false;
        ScrollTo(offset);
    }

    private void SetHovered(int index)
    {
        if (index == _hovered) return;
        if (_live.TryGetValue(_hovered, out FilmstripTile? old)) old.SetHover(false);
        _hovered = index;
        if (_live.TryGetValue(index, out FilmstripTile? now)) now.SetHover(true);
    }

    // ---- virtualization ----

    /// <summary>
    /// Realizes the tiles in view and moves the decode window. A strip that is hidden (or has no width yet) shows no tile and holds no
    /// thumbnail, so a gallery with the filmstrip off decodes nothing for it.
    /// </summary>
    internal void Refresh()
    {
        if (_retired) return;
        GalleryFilmstripPlan.TileRange visible = IsVisible && _viewport > 0 ? VisibleRange : GalleryFilmstripPlan.TileRange.Empty;
        // Tiles that left the view go back to the pool before the decode window moves, so no tile shows a preview that was given back.
        foreach (int index in _live.Keys.Where(index => !visible.Contains(index)).ToArray()) Recycle(index);
        if (!visible.IsEmpty)
            for (int index = visible.First; index <= visible.Last; index++)
                if (!_live.ContainsKey(index)) Realize(index);
        _strip.Offset = _offset;
        _strip.TileSize = _tile;
        _strip.Gap = _gap;
        _strip.InvalidateArrange();
        GalleryFilmstripPlan.TileRange decode = GalleryFilmstripPlan.Decode(visible, _preload, _count);
        _thumbnails.SetWindow(decode, _current >= 0 ? _current : visible.IsEmpty ? 0 : (visible.First + visible.Last) / 2);
        _back.IsEnabled = _offset > 0.5;
        _forward.IsEnabled = _offset < GalleryFilmstripPlan.MaximumOffset(Slots, _tile, _viewport, _gap) - 0.5;
        UpdateChevrons();
        UpdateEdgeFade();
    }

    // GL5e: an arrow shows while that way can scroll and the pointer is over the band or the keyboard is in it.
    private void UpdateChevrons()
    {
        bool active = _pointerOver || _keyboardFocus;
        _backShown = active && _back.IsEnabled;
        _forwardShown = active && _forward.IsEnabled;
        ShowChevron(_back, _backShown);
        ShowChevron(_forward, _forwardShown);
    }

    // GL6: an arrow stays in the tree while that way can scroll and only its Opacity moves (the filmstrip-chevron style fades it in 80 ms, or at
    // once under reduced motion); a hidden arrow takes no clicks and is left out of the accessibility tree like the removed one was.
    private static void ShowChevron(Button button, bool shown)
    {
        button.IsVisible = button.IsEnabled;
        button.Opacity = shown ? 1 : 0;
        button.IsHitTestVisible = shown;
        AutomationProperties.SetAccessibilityView(button, shown ? AccessibilityView.Content : AccessibilityView.Raw);
    }

    /// <summary>The width of the fade at a scrollable end of the strip (dip).</summary>
    internal const double EdgeFadeWidth = 24;

    // GL5e: a scrollable end fades out over 24 dip instead of ending hard. An opacity mask does it for any band background (the overlay
    // tone, or the dock's panel colour), with no colour to read; an end with nothing further stays solid.
    private (bool, bool, double) _fadeKey;

    private void UpdateEdgeFade()
    {
        bool left = _back.IsEnabled && _viewport > 0, right = _forward.IsEnabled && _viewport > 0;
        if (!left && !right) { _strip.OpacityMask = null; _fadeKey = default; return; }
        double fade = Math.Min(0.5, EdgeFadeWidth / _viewport);
        if (_fadeKey == (left, right, fade) && _strip.OpacityMask != null) return; // a drag moves the offset every frame; the mask changes rarely
        _fadeKey = (left, right, fade);
        Color solid = Colors.Black; // an opacity mask reads only the alpha: no colour on screen
        LinearGradientBrush mask = new() { StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative) };
        mask.GradientStops.Add(new GradientStop(left ? Colors.Transparent : solid, 0));
        mask.GradientStops.Add(new GradientStop(solid, left ? fade : 0));
        mask.GradientStops.Add(new GradientStop(solid, right ? 1 - fade : 1));
        mask.GradientStops.Add(new GradientStop(right ? Colors.Transparent : solid, 1));
        _strip.OpacityMask = mask;
    }

    /// <summary>Whether a scrollable end of the strip fades (tests and smokes): left, right.</summary>
    internal (bool Left, bool Right) EdgeFades => (_back.IsEnabled && _viewport > 0, _forward.IsEnabled && _viewport > 0);

    private void Realize(int index)
    {
        FilmstripTile tile = _pool.Count > 0 ? _pool.Pop() : new FilmstripTile();
        _live[index] = tile;
        _strip.Children.Add(tile);
        BindTile(tile, index);
    }

    private void Recycle(int index)
    {
        if (!_live.Remove(index, out FilmstripTile? tile)) return;
        if (index == _hovered) _hovered = -1;
        _strip.Children.Remove(tile);
        tile.Unbind();
        _pool.Push(tile);
    }

    private void ClearTiles()
    {
        foreach (int index in _live.Keys.ToArray()) Recycle(index);
        _pool.Clear();
    }

    private void BindTile(FilmstripTile tile, int index)
    {
        tile.Index = index;
        if (index >= _count || _entries == null)
        {
            tile.BindMore(Texts.T("gallery.filmstrip.more"), index + 1, Slots);
            return;
        }
        GalleryEntry entry = _entries[index];
        string name = FileName(entry);
        MediaBadgeState badges = _flags.StateFor(entry, BadgeStateOf(name, _durations.TryGetValue(index, out TimeSpan length) ? length : null));
        MediaBadgeOptions options = _flags.Options;
        tile.Bind(index, name, TileName(index + 1, _count, name, badges, options), badges, options, new Size(_tile, _tile), index + 1, Slots,
            _thumbnails.TryGet(index), index == _current);
    }

    private void UpdateTileNames()
    {
        foreach ((int index, FilmstripTile tile) in _live.ToArray()) BindTile(tile, index);
    }

    private FilmstripThumbnailWindow<Bitmap> CreateWindow()
    {
        var window = new FilmstripThumbnailWindow<Bitmap>(LoadAsync, MediaTileThumbnails.Release);
        window.Loaded += OnThumbnailLoaded;
        return window;
    }

    private Task<Bitmap?> LoadAsync(int index, CancellationToken token)
    {
        GalleryEntry? entry = _entries != null && index >= 0 && index < _count ? _entries[index] : null;
        if (entry == null) return Task.FromResult<Bitmap?>(null);
        return ThumbnailLoader != null ? ThumbnailLoader(entry, token, _gate)
            : ArchiveListThumbnails.LoadAsync(new ArchiveAssetRow(entry.Manifest, entry.Asset), PosterCacheDirectory, token, _gate);
    }

    private void OnThumbnailLoaded(int index)
    {
        if (_retired || !_live.TryGetValue(index, out FilmstripTile? tile)) return;
        tile.SetPicture(_thumbnails.TryGet(index));
    }

    // ---- texts and visuals ----

    private void OnTextsChanged(object? sender, EventArgs e)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        { Avalonia.Threading.Dispatcher.UIThread.Post(() => OnTextsChanged(sender, e)); return; }
        RefreshTexts();
        UpdateTileNames();
    }

    /// <summary>The band sits at the bottom of the dock (true) or at the top under the way back (false): a screen reader hears where it is.</summary>
    internal void SetDock(bool bottom)
    {
        if (_atBottom == bottom) return;
        _atBottom = bottom;
        RefreshTexts();
    }

    private void RefreshTexts()
    {
        string name = Texts.T("gallery.filmstrip.name", _count);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetHelpText(this, Texts.T(_atBottom ? "gallery.filmstrip.dock.bottom" : "gallery.filmstrip.dock.top"));
        ToolTip.SetTip(this, null);
        string back = Texts.T("gallery.filmstrip.scrollBack"), forward = Texts.T("gallery.filmstrip.scrollForward");
        AutomationProperties.SetName(_back, back); ToolTip.SetTip(_back, back);
        AutomationProperties.SetName(_forward, forward); ToolTip.SetTip(_forward, forward);
    }

    private void UpdateFocusVisual()
    {
        if (_keyboardFocus) this[!BorderBrushProperty] = new DynamicResourceExtension("Workspace.Accent");
        else BorderBrush = Brushes.Transparent;
    }

    internal static FilmstripTileKind KindOf(string fileName)
    {
        if (SavedMediaTypes.Classify(fileName) == SavedMediaKind.Video) return FilmstripTileKind.Video;
        return string.Equals(System.IO.Path.GetExtension(fileName), ".gif", StringComparison.OrdinalIgnoreCase) ? FilmstripTileKind.Gif : FilmstripTileKind.Image;
    }

    /// <summary>The badges of a tile (MB1b): the kind from the file name and the length when known; a filmstrip tile has no other badge.</summary>
    internal static MediaBadgeState BadgeStateOf(string fileName, TimeSpan? duration = null) => KindOf(fileName) switch
    {
        FilmstripTileKind.Video => new MediaBadgeState(Kind: MediaBadgeKind.Video, Duration: duration),
        FilmstripTileKind.Gif => new MediaBadgeState(Kind: MediaBadgeKind.Gif),
        _ => default
    };

    /// <summary>
    /// The name of a tile: position, file name and what the badges say (video with its length, animated image); a plain image has no badge and
    /// keeps the word "image". The badge sentence replaces the kind word, so nothing is said twice.
    /// </summary>
    internal static string TileName(int position, int count, string fileName, in MediaBadgeState badges) =>
        TileName(position, count, fileName, badges, MediaBadgeOptions.Filmstrip);

    /// <summary><see cref="TileName(int, int, string, in MediaBadgeState)"/> for the options the tile draws with (the flags say "Favorite · Rating 4 of 5 · No tags yet" too).</summary>
    internal static string TileName(int position, int count, string fileName, in MediaBadgeState badges, in MediaBadgeOptions options)
    {
        string said = MediaBadgeText.Describe(badges, options);
        return Texts.T("gallery.filmstrip.tile", position, count, fileName, said.Length > 0 ? said : KindText(KindOf(fileName)));
    }

    internal static string KindText(FilmstripTileKind kind) => kind switch
    {
        FilmstripTileKind.Video => Texts.T("gallery.filmstrip.kind.video"),
        FilmstripTileKind.Gif => Texts.T("gallery.filmstrip.kind.gif"),
        _ => Texts.T("gallery.filmstrip.kind.image")
    };

    internal static string FileName(GalleryEntry entry) => System.IO.Path.GetFileName(entry.Asset.OriginalFileName ?? entry.Asset.Path);

    // ---- the panel that places the realized tiles ----

    private sealed class FilmstripPanel : Panel
    {
        internal double Offset { get; set; }
        internal double TileSize { get; set; } = Config.GalleryFilmstripHeightDefault;
        internal double Gap { get; set; } = Config.GalleryFilmstripTileGapDefault;

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (Control child in Children) child.Measure(new Size(TileSize, TileSize));
            return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, GalleryFilmstripPlan.BandHeight(TileSize));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double pitch = GalleryFilmstripPlan.Pitch(TileSize, Gap);
            double top = Math.Max(0, (finalSize.Height - TileSize) / 2);
            foreach (Control child in Children)
                if (child is FilmstripTile tile) child.Arrange(new Rect(tile.Index * pitch - Offset, top, TileSize, TileSize));
            return finalSize;
        }
    }

    // ---- one tile ----

    private sealed class FilmstripTile : Border
    {
        // GL5e: no frame at rest (the picture fills the whole tile); the frame lives in an overlay of the content, so the tile never loses
        // size to a border: shown = 2 dip accent with a 1 dip control outline inside it, hovered = 1 dip control outline.
        private readonly Border _inner = new() { CornerRadius = new CornerRadius(4), ClipToBounds = true };
        private readonly Border _frame = new() { CornerRadius = new CornerRadius(4), IsHitTestVisible = false, BorderBrush = Brushes.Transparent };
        private readonly Border _frameInner = new() { CornerRadius = new CornerRadius(2), IsHitTestVisible = false, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1) };
        private readonly Image _image = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        private readonly TextBlock _extension = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis }.MetaText();
        // MB1b: the play mark (dense, 32 percent of the tile), the length and the GIF chip come from the shared badge view; it is drawn
        // over the whole tile, takes no hit and no focus and is not in the accessibility tree (the tile's name carries the words).
        private readonly MediaBadgeView _badges = new() { Name = "GalleryFilmstripBadges", Options = MediaBadgeOptions.Filmstrip };
        private readonly Avalonia.Controls.Shapes.Path _moreGlyph;
        private bool _current, _hover, _more;

        internal int Index { get; set; }
        internal bool HasPicture => _image.Source != null;
        internal bool IsCurrent => _current;
        internal bool IsMoreTile => _more;

        internal FilmstripTile()
        {
            Focusable = true;
            CornerRadius = new CornerRadius(4);
            _frame.Child = _frameInner;
            _inner[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Raised");
            _extension[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
            _moreGlyph = WorkspaceIcons.Create(WorkspaceIcons.More);
            _moreGlyph.IsVisible = false;
            Grid content = new();
            content.Children.Add(_extension);
            content.Children.Add(_image);
            content.Children.Add(_moreGlyph);
            content.Children.Add(_badges);
            content.Children.Add(_frame);
            _inner.Child = content;
            Child = _inner;
            AutomationProperties.SetControlTypeOverride(this, AutomationControlType.ListItem);
            ApplyBorders();
        }

        internal void Bind(int index, string fileName, string name, in MediaBadgeState badges, in MediaBadgeOptions options, Size tile, int position, int size, Bitmap? picture, bool current)
        {
            Index = index; _more = false;
            _moreGlyph.IsVisible = false;
            _extension.Text = System.IO.Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
            _extension.IsVisible = true;
            // The dense play mark sits in the middle: a placeholder's format text moves up out of its way.
            bool video = badges.Kind == MediaBadgeKind.Video;
            _extension.VerticalAlignment = video ? VerticalAlignment.Top : VerticalAlignment.Center;
            _extension.Margin = video ? new Thickness(4, 4, 4, 0) : default;
            _badges.Apply(badges, tile, options);
            ToolTip.SetTip(this, TipOf(fileName, badges, options));
            AutomationProperties.SetName(this, name);
            AutomationProperties.SetPositionInSet(this, position);
            AutomationProperties.SetSizeOfSet(this, size);
            SetPicture(picture);
            _current = current;
            ApplyBorders();
        }

        // The file name; with a flag on the tile (favourite, rating, no tags yet) a second line says it in words, like the tile's accessible name.
        private static string TipOf(string fileName, in MediaBadgeState badges, in MediaBadgeOptions options)
        {
            if (!badges.Favorite && badges.Rating is not >= 1 && badges.TagCount != 0) return fileName;
            // Only the flags, not the kind (the length and "GIF" are chips on the tile already); the sentence is cached per state.
            string flags = MediaBadgeSentences.Get(new MediaBadgeState(Favorite: badges.Favorite, Rating: badges.Rating, TagCount: badges.TagCount), options);
            return flags.Length == 0 ? fileName : fileName + Environment.NewLine + flags;
        }

        internal void BindMore(string name, int position, int size)
        {
            _more = true; _current = false;
            _image.Source = null;
            _extension.IsVisible = false; _badges.Clear();
            _moreGlyph.IsVisible = true;
            ToolTip.SetTip(this, name);
            AutomationProperties.SetName(this, name);
            AutomationProperties.SetPositionInSet(this, position);
            AutomationProperties.SetSizeOfSet(this, size);
            ApplyBorders();
        }

        internal void Unbind()
        {
            // Never hold a preview the window may have given back.
            _image.Source = null;
            _badges.Clear();
            _current = _hover = false;
            ApplyBorders();
        }

        internal void SetPicture(Bitmap? picture)
        {
            _image.Source = picture;
            _extension.IsVisible = picture == null && !_more;
        }

        internal void SetCurrent(bool current) { if (_current == current) return; _current = current; ApplyBorders(); }
        internal void SetHover(bool hover) { if (_hover == hover) return; _hover = hover; ApplyBorders(); }

        // The shown tile has the 2 dip accent frame and a 1 dip control outline inside it, so it reads in both palettes without colour
        // alone; a hovered tile gets the control outline as its frame (fill, border and label change together, as elsewhere).
        private void ApplyBorders()
        {
            _frame.BorderThickness = new Thickness(_current ? 2 : _hover ? 1 : 0);
            if (_current) _frame[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.Accent");
            else if (_hover) _frame[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.ControlBorder");
            else _frame.BorderBrush = Brushes.Transparent;
            if (_current) _frameInner[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.ControlBorder");
            else _frameInner.BorderBrush = Brushes.Transparent;
        }
    }
}

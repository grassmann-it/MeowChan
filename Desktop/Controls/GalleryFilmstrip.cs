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
    private const double ScrollNotchTiles = 2;
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
    private int _preload = Config.GalleryFilmstripPreloadDefault;
    private double _offset;
    private double _viewport;
    private int _hovered = -1;
    private bool _pressed, _dragging, _fromStrip, _retired, _followCurrent;
    private Point _pressPoint;
    private double _pressOffset;
    private int _pressedTile = -1;
    private bool _keyboardFocus;

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
        foreach (Button button in new[] { _back, _forward })
        {
            button.Width = 24; button.Height = double.NaN; button.VerticalAlignment = VerticalAlignment.Stretch; button.Focusable = false;
        }
        _back.Click += (_, _) => ScrollByUser(GalleryFilmstripPlan.PageOffset(_offset, -1, Slots, _tile, _viewport));
        _forward.Click += (_, _) => ScrollByUser(GalleryFilmstripPlan.PageOffset(_offset, 1, Slots, _tile, _viewport));
        _position.Classes.Add("workspace-meta");
        AutomationProperties.SetAccessibilityView(_position, AccessibilityView.Raw);
        Grid root = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        root.Children.Add(_back);
        root.Children.Add(_strip); Grid.SetColumn(_strip, 1);
        root.Children.Add(_forward); Grid.SetColumn(_forward, 2);
        root.Children.Add(_position); Grid.SetColumn(_position, 3);
        Child = root;
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.List);
        _strip.Cursor = new Cursor(StandardCursorType.Hand);
        _strip.SizeChanged += (_, e) =>
        {
            _viewport = e.NewSize.Width;
            // A strip the user has not scrolled keeps the shown medium in its middle when the window is resized.
            ScrollTo(_followCurrent && _current >= 0 ? GalleryFilmstripPlan.CentreOffset(_current, Slots, _tile, _viewport) : _offset);
        };
        _strip.PointerPressed += OnPointerPressed;
        _strip.PointerMoved += OnPointerMoved;
        _strip.PointerReleased += OnPointerReleased;
        _strip.PointerCaptureLost += (_, _) => { _pressed = _dragging = false; };
        _strip.PointerExited += (_, _) => SetHovered(-1);
        PointerWheelChanged += OnWheel;
        GotFocus += (_, e) => { _keyboardFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional; UpdateFocusVisual(); };
        LostFocus += (_, _) => { _keyboardFocus = false; UpdateFocusVisual(); };
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

    /// <summary>The user asked for the tile (a click or Enter): the entry's index, or the entry count for the "more" tile.</summary>
    internal event Action<int>? TileActivated;
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
    internal GalleryFilmstripPlan.TileRange VisibleRange => GalleryFilmstripPlan.Visible(_offset, _viewport, _tile, Slots);
    internal bool BackEnabled => _back.IsEnabled;
    internal bool ForwardEnabled => _forward.IsEnabled;
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
        if (!same) { ClearTiles(); _thumbnails.Reset(); _current = -1; _offset = 0; }
        RefreshTexts();
        ScrollTo(_offset);
    }

    /// <summary>Marks the shown medium and scrolls it into view (centred, or just into view when the strip itself asked for it).</summary>
    internal void SetCurrent(int index)
    {
        index = index < 0 || index >= _count ? -1 : index;
        bool changed = index != _current;
        _current = index;
        foreach (FilmstripTile tile in _live.Values) tile.SetCurrent(tile.Index == _current);
        if (index >= 0 && !_dragging)
        {
            ScrollTo(_fromStrip ? GalleryFilmstripPlan.RevealOffset(_offset, index, Slots, _tile, _viewport)
                : GalleryFilmstripPlan.CentreOffset(index, Slots, _tile, _viewport));
            _followCurrent = !_fromStrip;
        }
        else Refresh();
        _fromStrip = false;
        if (changed) UpdateTileNames();
    }

    /// <summary>Tile size (the height setting) and preload (tiles decoded in advance on each side), both clamped to their ranges.</summary>
    internal void Configure(int height, int preload)
    {
        height = GalleryFilmstripPlan.ClampHeight(height);
        preload = GalleryFilmstripPlan.ClampPreload(preload);
        bool resized = height != (int)_tile;
        if (!resized && preload == _preload) return;
        _preload = preload;
        if (resized)
        {
            _tile = height;
            Height = GalleryFilmstripPlan.BandHeight(_tile);
            ClearTiles();
        }
        ScrollTo(_current >= 0 && resized ? GalleryFilmstripPlan.CentreOffset(_current, Slots, _tile, _viewport) : _offset);
    }

    /// <summary>The position text at the right end ("3 / 27"); the viewer owns the text.</summary>
    internal void SetPositionText(string? text) => _position.Text = text ?? string.Empty;

    /// <summary>Sets the band background (the overlay tone) or clears it (docked).</summary>
    internal void SetBackground(IBrush? background)
    {
        if (background == null) ClearValue(BackgroundProperty);
        else Background = background;
    }

    /// <summary>Scrolls the strip, clamped; realizes the tiles in view and moves the decode window.</summary>
    internal void ScrollTo(double offset)
    {
        _offset = GalleryFilmstripPlan.ClampOffset(offset, Slots, _tile, _viewport);
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
    /// scroll by a page, Enter and Escape hand the keyboard back to the picture. Left/Right stay with the viewer (they step the shown medium,
    /// which the strip follows). Returns whether the key was used.
    /// </summary>
    internal bool HandleKey(KeyEventArgs e)
    {
        if (!IsKeyboardFocusWithin || e.KeyModifiers != KeyModifiers.None) return false;
        switch (e.Key)
        {
            case Key.Home when _count > 0: _fromStrip = true; TileActivated?.Invoke(0); return true;
            case Key.End when _count > 0: _fromStrip = true; TileActivated?.Invoke(_count - 1); return true;
            case Key.PageUp: ScrollByUser(GalleryFilmstripPlan.PageOffset(_offset, -1, Slots, _tile, _viewport)); return true;
            case Key.PageDown: ScrollByUser(GalleryFilmstripPlan.PageOffset(_offset, 1, Slots, _tile, _viewport)); return true;
            case Key.Enter or Key.Escape: ExitRequested?.Invoke(); return true;
            default: return false;
        }
    }

    // ---- pointer ----

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(_strip);
        if (!point.Properties.IsLeftButtonPressed) return;
        _pressed = true; _dragging = false;
        _pressPoint = point.Position; _pressOffset = _offset;
        _pressedTile = GalleryFilmstripPlan.TileAt(point.Position.X, _offset, _tile, Slots);
        e.Pointer.Capture(_strip);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point position = e.GetPosition(_strip);
        if (!_pressed) { SetHovered(GalleryFilmstripPlan.TileAt(position.X, _offset, _tile, Slots)); return; }
        double moved = position.X - _pressPoint.X;
        if (!_dragging && Math.Abs(moved) >= DragThreshold) { _dragging = true; SetHovered(-1); }
        if (_dragging) ScrollByUser(_pressOffset - moved);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool click = _pressed && !_dragging;
        int tile = click ? GalleryFilmstripPlan.TileAt(e.GetPosition(_strip).X, _offset, _tile, Slots) : -1;
        _pressed = _dragging = false;
        if (ReferenceEquals(e.Pointer.Captured, _strip)) e.Pointer.Capture(null);
        e.Handled = true;
        if (click && tile >= 0 && tile == _pressedTile) { _fromStrip = tile < _count && tile != _current; TileActivated?.Invoke(tile); }
        _pressedTile = -1;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        double notches = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (notches == 0) return;
        // Wheel up (and a pad swipe to the right) look back along the strip, wheel down forward.
        ScrollByUser(_offset - notches * ScrollNotchTiles * GalleryFilmstripPlan.Pitch(_tile));
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
        _strip.InvalidateArrange();
        GalleryFilmstripPlan.TileRange decode = GalleryFilmstripPlan.Decode(visible, _preload, _count);
        _thumbnails.SetWindow(decode, _current >= 0 ? _current : visible.IsEmpty ? 0 : (visible.First + visible.Last) / 2);
        _back.IsEnabled = _offset > 0.5;
        _forward.IsEnabled = _offset < GalleryFilmstripPlan.MaximumOffset(Slots, _tile, _viewport) - 0.5;
    }

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
        tile.Bind(index, KindOf(name), name, Texts.T("gallery.filmstrip.tile", index + 1, _count, name, KindText(KindOf(name))), index + 1, Slots,
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

    private void RefreshTexts()
    {
        string name = Texts.T("gallery.filmstrip.name", _count);
        AutomationProperties.SetName(this, name);
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

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (Control child in Children) child.Measure(new Size(TileSize, TileSize));
            return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, GalleryFilmstripPlan.BandHeight(TileSize));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double pitch = GalleryFilmstripPlan.Pitch(TileSize);
            double top = Math.Max(0, (finalSize.Height - TileSize) / 2);
            foreach (Control child in Children)
                if (child is FilmstripTile tile) child.Arrange(new Rect(tile.Index * pitch - Offset, top, TileSize, TileSize));
            return finalSize;
        }
    }

    // ---- one tile ----

    private sealed class FilmstripTile : Border
    {
        private readonly Border _inner = new() { CornerRadius = new CornerRadius(3), ClipToBounds = true, BorderThickness = new Thickness(1) };
        private readonly Image _image = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        private readonly TextBlock _extension = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis }.MetaText();
        private readonly Border _play = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4), IsHitTestVisible = false, IsVisible = false };
        private readonly Avalonia.Controls.Shapes.Path _playGlyph = new() { Width = 12, Height = 12, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        private readonly Border _gif = WorkspaceChips.CreateCornerBadge("GalleryFilmstripGif", HorizontalAlignment.Left, VerticalAlignment.Bottom);
        private readonly Avalonia.Controls.Shapes.Path _moreGlyph;
        private bool _current, _hover, _more;

        internal int Index { get; set; }
        internal bool HasPicture => _image.Source != null;
        internal bool IsCurrent => _current;
        internal bool IsMoreTile => _more;

        internal FilmstripTile()
        {
            BorderThickness = new Thickness(2);
            CornerRadius = new CornerRadius(5);
            _inner[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Raised");
            _extension[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
            _play[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Overlay");
            _playGlyph[!Shape.FillProperty] = new DynamicResourceExtension("Workspace.Text");
            _playGlyph.AttachedToVisualTree += (_, _) => _playGlyph.Data ??= Geometry.Parse("M2,1 L11,6 L2,11 Z");
            _moreGlyph = WorkspaceIcons.Create(WorkspaceIcons.More);
            _moreGlyph.IsVisible = false;
            _play.Child = _playGlyph;
            Grid content = new();
            content.Children.Add(_extension);
            content.Children.Add(_image);
            content.Children.Add(_moreGlyph);
            content.Children.Add(_play);
            content.Children.Add(_gif);
            _inner.Child = content;
            Child = _inner;
            AutomationProperties.SetControlTypeOverride(this, AutomationControlType.ListItem);
            ApplyBorders();
        }

        internal void Bind(int index, FilmstripTileKind kind, string fileName, string name, int position, int size, Bitmap? picture, bool current)
        {
            Index = index; _more = false;
            _moreGlyph.IsVisible = false;
            _extension.Text = System.IO.Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
            _extension.IsVisible = true;
            _play.IsVisible = kind == FilmstripTileKind.Video;
            _gif.IsVisible = kind == FilmstripTileKind.Gif;
            if (_gif.IsVisible) ((TextBlock)_gif.Child!).Text = KindText(FilmstripTileKind.Gif);
            ToolTip.SetTip(this, fileName);
            AutomationProperties.SetName(this, name);
            AutomationProperties.SetPositionInSet(this, position);
            AutomationProperties.SetSizeOfSet(this, size);
            SetPicture(picture);
            _current = current;
            ApplyBorders();
        }

        internal void BindMore(string name, int position, int size)
        {
            _more = true; _current = false;
            _image.Source = null;
            _extension.IsVisible = false; _play.IsVisible = false; _gif.IsVisible = false;
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
            if (_current) this[!BorderBrushProperty] = new DynamicResourceExtension("Workspace.Accent");
            else if (_hover) this[!BorderBrushProperty] = new DynamicResourceExtension("Workspace.ControlBorder");
            else BorderBrush = Brushes.Transparent;
            if (_current) _inner[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.ControlBorder");
            else _inner.BorderBrush = Brushes.Transparent;
        }
    }
}

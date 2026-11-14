using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>Bindable row of the media page grid.</summary>
internal sealed record MediaLibraryTileRow(MediaLibraryTile Tile, int Index, Func<MediaLibraryEntry, MediaLibraryEntryState>? StateOf = null)
{
    private MediaLibraryEntryState? _state;
    /// <summary>The profile's own state of the shown occurrence, read lazily: only tiles that are actually shown look it up.</summary>
    public MediaLibraryEntryState? State => StateOf == null || Entry.Presence != MediaLibraryPresence.Present ? null : _state ??= StateOf(Entry);
    public string StateDescription => State is { } state ? MediaLibraryStateText.Description(state) : string.Empty;

    /// <summary>The occurrence the tile shows; gallery, thumbnail and "show in thread" use it.</summary>
    public MediaLibraryEntry Entry => Tile.Entry;
    public string Name => Entry.Name ?? Entry.MediaId ?? string.Empty;
    /// <summary>True for a downloaded provider medium (Danbooru): it has a post of the provider, not a chan thread.</summary>
    public bool IsProviderMedium => Entry.BooruPostId != null;
    public bool IsImported => Entry.ImportItemId != null;
    /// <summary>True for a file of a read-only folder source.</summary>
    public bool IsFolderFile => Entry.FolderItemId != null;
    public string ThreadLabel => IsProviderMedium ? Texts.T("booru.media.tile.post", Entry.BooruPostId!)
        : IsImported ? Texts.T("import.media.tile", Entry.ThreadTitle)
        : IsFolderFile ? Texts.T("folderSource.media.board", Entry.ThreadTitle)
        : Texts.T("media.tile.thread", string.IsNullOrWhiteSpace(Entry.ThreadTitle) ? "#" + Entry.Thread.Thread : Entry.ThreadTitle);
    public string Detail => Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing")
        : Entry.Unassigned && !IsProviderMedium && !IsImported && !IsFolderFile ? Texts.T("media.tile.unassigned")
        : Entry.Length is long length ? SizeText.Format(length) : string.Empty;
    public string DetailWithState => State is { } state && MediaLibraryStateText.Compact(state, Tile.IsGroup) is { Length: > 0 } compact
        ? (Detail.Length == 0 ? compact : Detail + " · " + compact) : Detail;
    /// <summary>The own tags the tile offers as filter chips (media tags first, then thread tags); empty when unknown or none.</summary>
    public IReadOnlyList<PrivateTagDefinition> TagChips => State?.CarriedTags ?? Array.Empty<PrivateTagDefinition>();
    /// <summary>True when the chip at this index is a thread tag: media tags come first and every carried tag is listed once.</summary>
    public bool IsThreadTagChip(int index) => State is { } state && index >= state.MediaTags.Count && index < TagChips.Count;
    /// <summary>How many chips fit beside what else the row shows: a grouped tile shares its row with the group favorite.</summary>
    public int ShownTagChips => Math.Min(TagChips.Count, Tile.IsGroup ? 1 : 2);
    /// <summary>I7: the type badge of the preview corner: GIF, video or other file; empty for images.</summary>
    public string KindLabel => MediaLibraryFilterEngine.KindOf(Entry) switch
    {
        MediaLibraryKindFilter.Gif => Texts.T("media.tile.kind.gif"),
        MediaLibraryKindFilter.Video => Texts.T("media.tile.kind.video"),
        MediaLibraryKindFilter.Other => Texts.T("media.tile.kind.other"),
        _ => string.Empty
    };
    /// <summary>I7: a saved occurrence the user can tag that has no media tag yet (thread tags do not count, as in "Without media tags").</summary>
    public bool IsUntagged => State is { CanEditMediaTags: true, MediaTags.Count: 0 };
    /// <summary>Favorite state over every saved occurrence of the bytes; only a grouped tile has one to report.</summary>
    public string FavoriteLabel => Tile.IsGroup ? MediaLibraryStateText.Favorite(Tile.Favorite) : string.Empty;
    /// <summary>"in N threads" for equal bytes found elsewhere; empty for a single occurrence.</summary>
    public string Duplicates => !Tile.IsGroup ? string.Empty
        : Tile.ProviderPostCount > 0 && Tile.ArchivedThreadCount == 0 && Tile.ProviderPostCount > 1 ? Texts.T("booru.media.tile.inPosts", Tile.ProviderPostCount)
        : Tile.ProviderPostCount > 0 && Tile.ArchivedThreadCount > 0 ? Texts.T("booru.media.tile.inMixed", Tile.ArchivedThreadCount, Tile.ProviderPostCount)
        : Tile.ThreadCount > 1 ? Texts.T("media.tile.inThreads", Tile.ThreadCount)
        : Texts.Count("media.tile.sameThread", Tile.Occurrences.Count);
}

/// <summary>
/// One tile: verified thumbnail for saved images and the local poster of saved videos, a placeholder otherwise. Thumbnails load only while
/// visible and never fetch anything from the network.
/// </summary>
internal sealed class MediaLibraryTileView : UserControl
{
    private static int _pendingLoads;
    /// <summary>Tile previews waiting for a load slot or being loaded right now, over all media pages (the sound check waits for them).</summary>
    internal static int PendingThumbnailLoads => Volatile.Read(ref _pendingLoads);
    // Four loads at a time: with the file check remembered per session a load is mostly decoding, which spreads over the cores.
    private static readonly SemaphoreSlim ThumbnailGate = new(4, 4);
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxWidth = 174, MaxHeight = 120 };
    private readonly LetterboxPanel _preview = new();
    private readonly TextBlock _placeholder = new TextBlock { TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center }.CaptionText();
    private readonly TextBlock _name = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _thread = new TextBlock { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }.CaptionText();
    // The thread acts like a meta tag: its label filters the page to that thread.
    private readonly Button _threadLink = new() { Name = "MediaTileThread", Padding = new Thickness(0), Classes = { "link" },
        HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _detail = new TextBlock { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }.CaptionText();
    // Equal bytes in several places: the hint opens the list of all occurrences.
    private readonly TextBlock _duplicates = new TextBlock { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }.CaptionText();
    private readonly Button _duplicatesLink = new() { Name = "MediaTileDuplicates", Padding = new Thickness(0), Classes = { "link" }, IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
    // Selected tiles carry a check mark besides the list's highlight, so the state does not depend on color alone.
    private readonly Border _selectedMark = WorkspaceChips.CreateSelectionCheck("MediaTileSelected");
    // MB1b: what the tile says about the medium (favourite, kind, file missing, queue, own tags or none yet, booru source) is drawn by the
    // shared media badges over exactly the shown picture: one render-only view, no child control per badge. Only the favourite toggle stays a
    // button (bottom right); the badge carries the state at rest, so the toggle is quiet unless the tile is hovered or focused. The queue
    // badge is refreshed alone when the queue changes (RefreshQueueBadge).
    private readonly MediaBadgeView _badges = new() { Name = "MediaTileBadges", Options = MediaBadgeOptions.MediaPage };
    private readonly StackPanel _topRight = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0) };
    private MediaLibraryBadgeSentence _badgeText;
    private string _sentence = string.Empty;
    private readonly ToggleButton _favoriteToggle = CreateQuietFavoriteToggle();
    private readonly TextBlock _favorite = new TextBlock { Name = "MediaTileFavorite", MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false }.CaptionText();
    // Own tags act like the thread label beside them: each chip filters the page to that tag. The row is the favorite row of grouped tiles,
    // so the cell keeps its height; what does not fit is summarized as "+N" with the names in its tooltip.
    // C1: filled own-tag chips (chip-own chip-compact); a thread tag carries a small house mark besides (R7).
    private readonly Button[] _tagChips = new Button[2];
    private readonly TextBlock _moreTags = new TextBlock { Name = "MediaTileMoreTags", MaxLines = 1, IsVisible = false, VerticalAlignment = VerticalAlignment.Center }.CaptionText();
    private IDisposable? _selection;
    private Bitmap? _bitmap;
    private Grid? _rows;
    private MediaTileAppearance _appearance = MediaTileMetrics.Standard;
    /// <summary>True while the tile shows a verified thumbnail or video poster.</summary>
    internal bool HasThumbnail => _image.Source != null;
    /// <summary>Where the picture is drawn, in the preview's coordinates (the corner marks sit on it).</summary>
    internal Rect PictureBounds => _preview.PictureBounds;
    private CancellationTokenSource? _loading;
    private int _request;
    private bool _visible;

    /// <summary>Set by the page: loads a verified thumbnail (image, or the local video poster) for a tile, or null when none can be shown.</summary>
    internal Func<MediaLibraryTile, CancellationToken, Task<Bitmap?>>? ThumbnailLoader { get; set; }
    /// <summary>Set by the page: a kept image preview leased to this tile, or null. Videos always use the verified asynchronous loader.</summary>
    internal Func<MediaLibraryTile, Bitmap?>? ThumbnailCached { get; set; }
    /// <summary>Set by the page: takes back a bitmap that came from the page's cache instead of disposing it (other tiles may show it).</summary>
    internal Action<Bitmap>? ThumbnailRelease { get; set; }
    /// <summary>
    /// True when the preview now shown came from the page's cache (it appeared in the same turn the tile was bound), false when it was
    /// loaded asynchronously. A fade-in belongs only to the loaded ones.
    /// </summary>
    internal bool ThumbnailWasCached { get; private set; }
    /// <summary>Set by the page: shows only the media of the tile's thread.</summary>
    internal Action<MediaLibraryEntry>? ThreadFilter { get; set; }
    /// <summary>Set by the page: shows only the media that carry this own tag.</summary>
    internal Action<Guid>? TagFilter { get; set; }
    /// <summary>Set by the page: hides the media that carry this own tag (Shift+click or Shift+Enter on the chip, S7).</summary>
    internal Action<Guid>? TagExclude { get; set; }
    /// <summary>Set by the page: lists every occurrence of the tile's bytes.</summary>
    internal Action<MediaLibraryTile>? ShowOccurrences { get; set; }
    /// <summary>Set by the page: adds or removes the shown occurrence as a favourite (the shared media action). Null hides the star.</summary>
    internal Func<MediaLibraryEntry, Task>? FavoriteToggle { get; set; }
    /// <summary>Set by the page: whether the favourite action is wired at all (a harness page without the shell has none).</summary>
    internal Func<bool>? CanToggleFavorite { get; set; }
    /// <summary>Set by the page: whether the shown occurrence is in the tagging queue (MP1). Null: no queue mark.</summary>
    internal Func<MediaLibraryEntry, bool>? InTagQueue { get; set; }
    /// <summary>The style class of the favourite toggle that is only shown while the tile is hovered or focused (the badge shows the state at rest).</summary>
    internal const string QuietFavoriteClass = "tile-favorite-quiet";
    /// <summary>The shared badge view over the shown picture (tests and smokes read its state and plan).</summary>
    internal MediaBadgeView Badges => _badges;
    /// <summary>The badge sentence in the tile's name (every fact of the medium, also the ones the Layout menu hides; empty when there is none).</summary>
    internal string BadgeDescription => _sentence;
    /// <summary>The favourite toggle (tests and smokes).</summary>
    internal ToggleButton FavoriteToggleButton => _favoriteToggle;

    private static ToggleButton CreateQuietFavoriteToggle()
    {
        ToggleButton toggle = WorkspaceChips.CreateFavoriteToggle("MediaTileFavoriteToggle");
        toggle.Classes.Add(QuietFavoriteClass);
        return toggle;
    }

    /// <summary>
    /// MP5: the size and the caption parts the media page's Layout menu chose. Setting it changes sizes and visibility of this tile's own parts only
    /// (no row is rebuilt, no thumbnail is loaded again); the page sets it on the tiles it has realized and on every tile it makes later.
    /// The tile's name (screen reader and tooltip) keeps every part, shown or not.
    /// </summary>
    internal MediaTileAppearance Appearance
    {
        get => _appearance;
        set
        {
            if (_appearance == value) return;
            _appearance = value;
            ApplyAppearance();
        }
    }

    private void ApplyAppearance()
    {
        MediaTileAppearance a = _appearance;
        MaxWidth = a.CellWidth; Height = a.CellHeight;
        _preview.Height = a.PreviewHeight; _preview.MaxWidth = a.PictureWidth;
        _image.MaxWidth = a.PictureWidth; _image.MaxHeight = a.PreviewHeight;
        if (_rows != null) _rows.RowDefinitions[0].Height = new GridLength(a.PreviewHeight);
        _name.IsVisible = a.Shows(MediaTileCaption.Name);
        _threadLink.IsVisible = a.Shows(MediaTileCaption.Source);
        Present(); // the badges and the tile's name follow; the preview and its bitmap stay
    }

    /// <summary>Taller than an archive cell: preview, name, thread, footer and the favorite/tag row plus margins. The page's panel uses it as cell height.</summary>
    internal const double TileHeight = 226;

    public MediaLibraryTileView()
    {
        MaxWidth = 190; Height = TileHeight;
        // The cell stays uniform (the grid's cell height is fixed); the picture is letterboxed in it, the badges lie exactly on the picture and
        // the selection check and the star are arranged on its corners.
        LetterboxPanel preview = _preview; preview.Height = 120; preview.MaxWidth = 174;
        _topRight.Children.Add(_selectedMark);
        // The check keeps clear of a badge in the same corner; the badges' place follows the picture, so the inset follows its bounds.
        _badges.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) UpdateTopRightInset(); };
        // Top left, where the favourite badge sits: the toggle shows only on hover or focus and then takes the badge's place, so it never covers the
        // tag count or the untagged mark of the bottom right corner, nor the kind of the bottom left.
        _favoriteToggle.HorizontalAlignment = HorizontalAlignment.Left; _favoriteToggle.VerticalAlignment = VerticalAlignment.Top;
        _favoriteToggle.Margin = new Thickness(2, 2, 0, 0);
        _favoriteToggle.Click += async (_, e) =>
        {
            e.Handled = true; // The tile itself is not activated by its star.
            if (DataContext is not MediaLibraryTileRow row || FavoriteToggle is not { } toggle) return;
            _favoriteToggle.IsChecked = row.State?.Favorite == MediaLibraryFlag.Yes; // The page re-presents with the stored state.
            _favoriteToggle.IsEnabled = false;
            try { await toggle(row.Entry); }
            finally { if (ReferenceEquals(DataContext, row)) _favoriteToggle.IsEnabled = true; }
        };
        preview.Children.Add(_image); preview.Children.Add(_placeholder); preview.Children.Add(_badges); preview.Children.Add(_topRight);
        preview.Children.Add(_favoriteToggle);
        Grid grid = _rows = new() { Margin = new Thickness(8, 8, 8, 4), RowDefinitions = new RowDefinitions("120,Auto,Auto,Auto,Auto"), RowSpacing = 2 };
        grid.Children.Add(preview);
        grid.Children.Add(_name); Grid.SetRow(_name, 1);
        _threadLink.Content = _thread;
        _threadLink.Click += (_, e) => { e.Handled = true; if (DataContext is MediaLibraryTileRow row) ThreadFilter?.Invoke(row.Entry); };
        grid.Children.Add(_threadLink); Grid.SetRow(_threadLink, 2);
        _duplicatesLink.Content = _duplicates;
        _duplicatesLink.Click += (_, e) => { e.Handled = true; if (DataContext is MediaLibraryTileRow row) ShowOccurrences?.Invoke(row.Tile); };
        // The hint is a link button: with the theme's minimum height and border it centers its text lower than the plain size
        // text beside it. Without them both sit on one baseline; a hint too long for the width puts the size on a clean second line,
        // which needs the room the thread link's minimum height would otherwise take.
        _duplicatesLink.MinHeight = 0; _duplicatesLink.BorderThickness = new Thickness(0);
        // A link, not a chip: no frame (the theme's button outline made it look like a boxed chip of its own).
        _threadLink.MinHeight = 0; _threadLink.BorderThickness = new Thickness(0);
        foreach (Control part in new Control[] { _duplicatesLink, _detail }) part.VerticalAlignment = VerticalAlignment.Center;
        WrapPanel footer = new() { ItemSpacing = 8, LineSpacing = 0, Children = { _duplicatesLink, _detail } };
        grid.Children.Add(footer); Grid.SetRow(footer, 3);
        TagRow tags = new() { Name = "MediaTileTags", ClipToBounds = true, Children = { _favorite } };
        for (int index = 0; index < _tagChips.Length; index++)
        {
            Avalonia.Controls.Shapes.Path mark = WorkspaceChips.CreateThreadMark();
            mark.Name = "MediaTileTagThreadMark"; mark.IsVisible = false;
            TextBlock label = new() { Name = "MediaTileTagName", MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            DockPanel content = new() { Children = { mark, label } };
            DockPanel.SetDock(mark, Dock.Left);
            Button chip = _tagChips[index] = WorkspaceChips.MarkOwn(new Button { Name = "MediaTileTag", IsVisible = false, Content = content }, compact: true);
            // Shift turns the click (or Enter/Space) into an exclusion; the modifier is read where the gesture starts.
            bool excludeIntent = false;
            chip.AddHandler(PointerPressedEvent, (_, e) => excludeIntent = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel);
            chip.AddHandler(KeyDownEvent, (_, e) => { if (e.Key is Key.Enter or Key.Space) excludeIntent = e.KeyModifiers.HasFlag(KeyModifiers.Shift); }, RoutingStrategies.Tunnel);
            chip.Click += (_, e) =>
            {
                e.Handled = true;
                bool exclude = excludeIntent;
                excludeIntent = false;
                if (chip.Tag is not PrivateTagDefinition tag) return;
                if (exclude && TagExclude != null) TagExclude(tag.Id.Value); else TagFilter?.Invoke(tag.Id.Value);
            };
            tags.Children.Add(chip);
        }
        tags.Children.Add(_moreTags);
        _favorite.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(tags); Grid.SetRow(tags, 4);
        foreach (TextBlock text in new[] { _thread, _detail, _duplicates, _favorite, _moreTags }) text.Classes.Add("workspace-meta");
        Content = grid;
        DataContextChanged += (_, _) => { Reset(); Present(); if (_visible) _ = LoadAsync(); };
        EffectiveViewportChanged += (_, e) =>
        {
            bool visible = IsEffectivelyVisible && new Rect(Bounds.Size).Intersects(e.EffectiveViewport);
            if (visible == _visible) return;
            _visible = visible;
            if (visible) _ = LoadAsync(); else Reset();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _selection?.Dispose();
            _selection = WorkspaceChips.ObserveSelection(this, _selectedMark);
        };
        DetachedFromVisualTree += (_, _) => { _visible = false; Reset(); _selection?.Dispose(); _selection = null; _selectedMark.IsVisible = false; };
    }

    private void Present()
    {
        MediaLibraryTileRow? row = DataContext as MediaLibraryTileRow;
        _name.Text = row?.Name;
        _thread.Text = row?.ThreadLabel;
        string threadHint = row == null ? string.Empty : Texts.T("media.filter.threadHint", row.ThreadLabel);
        ToolTip.SetTip(_threadLink, threadHint);
        AutomationProperties.SetName(_threadLink, threadHint);
        _detail.Text = row?.DetailWithState;
        string duplicates = row?.Duplicates ?? string.Empty;
        _duplicates.Text = duplicates;
        _duplicatesLink.IsVisible = duplicates.Length > 0;
        _favorite.Text = row?.FavoriteLabel;
        _favorite.IsVisible = !string.IsNullOrEmpty(_favorite.Text);
        PresentTags(row);
        string duplicatesHint = row == null || duplicates.Length == 0 ? string.Empty : Texts.T("media.tile.duplicatesHint", row.Tile.Occurrences.Count);
        ToolTip.SetTip(_duplicatesLink, duplicatesHint);
        AutomationProperties.SetName(_duplicatesLink, duplicatesHint);
        _placeholder.Text = row?.Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing") : string.Empty;
        PresentCorners(row);
        UpdateBadges(row);
        UpdateName(row);
    }

    // The tile's name (screen reader and tooltip): every fact of the row, shown on the tile or not. GIF and video are said by the badge sentence;
    // only "file" (neither a picture nor a video) is added here.
    private void UpdateName(MediaLibraryTileRow? row)
    {
        string description = row == null ? string.Empty : string.Join(Environment.NewLine, new[] { row.Name,
            MediaLibraryFilterEngine.KindOf(row.Entry) == MediaLibraryKindFilter.Other ? Texts.T("media.tile.kind.other") : string.Empty,
            _sentence, row.ThreadLabel, row.Duplicates, row.FavoriteLabel, row.Detail, row.StateDescription }.Where(part => part.Length > 0));
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, description);
        AutomationProperties.SetHelpText(this, _sentence);
    }

    // The badges follow the row. The view is told the picture's size; the letterbox panel repeats that on every layout. Nothing is allocated when
    // the state did not change (the view compares, the sentence is cached). The sentence names every fact, also the ones the Layout menu's caption
    // parts hide from the picture.
    private void UpdateBadges(MediaLibraryTileRow? row)
    {
        if (row == null) { _badges.Clear(); _badgeText.Reset(); _sentence = string.Empty; UpdateTopRightInset(); return; }
        MediaBadgeState full = MediaLibraryBadges.ForRow(row, InTagQueue?.Invoke(row.Entry) == true);
        _sentence = _badgeText.Get(full, _badges.Options);
        _badges.Apply(MediaLibraryBadges.Visible(full, _appearance), _preview.PictureBounds.Size);
        UpdateTopRightInset();
    }

    // The selection check is arranged on the marks band, which can be wider than a thin picture, so that difference is part of the inset.
    private void UpdateTopRightInset()
    {
        Rect picture = _preview.PictureBounds, band = _preview.MarksBounds;
        double inset = MediaLibraryBadges.CornerInset + Math.Max(0, band.Right - picture.Right) + MediaLibraryBadges.TopRightExtent(_badges.Plan, picture.Width);
        if (_topRight.Margin.Right != inset) _topRight.Margin = new Thickness(0, 2, inset, 0);
    }

    /// <summary>
    /// MP1: refreshes the queue badge for the occurrence and puts it into the tile's name. Cheap (one set lookup, nothing built when the state
    /// is the same): the page calls it on the realized tiles when the queue changed, without rebuilding them.
    /// </summary>
    internal void RefreshQueueBadge()
    {
        MediaLibraryTileRow? row = DataContext as MediaLibraryTileRow;
        string before = _sentence;
        UpdateBadges(row);
        if (!ReferenceEquals(before, _sentence)) UpdateName(row);
    }

    /// <summary>Sets the favourite toggle (the badges are set by <see cref="UpdateBadges"/>).</summary>
    private void PresentCorners(MediaLibraryTileRow? row)
    {
        bool canFavorite = FavoriteToggle != null && CanToggleFavorite?.Invoke() != false && row is { Tile.IsGroup: false, State.Favorite: not MediaLibraryFlag.Unknown };
        bool marked = canFavorite && row!.State!.Favorite == MediaLibraryFlag.Yes;
        _favoriteToggle.IsVisible = canFavorite;
        _favoriteToggle.IsChecked = marked;
        _favoriteToggle.IsEnabled = true;
        string favoriteName = canFavorite ? Texts.T(marked ? "viewer.favorite.remove" : "viewer.favorite.add") : string.Empty;
        ToolTip.SetTip(_favoriteToggle, canFavorite ? favoriteName : null);
        AutomationProperties.SetName(_favoriteToggle, favoriteName);
    }

    private void PresentTags(MediaLibraryTileRow? row)
    {
        IReadOnlyList<PrivateTagDefinition> tags = row?.TagChips ?? Array.Empty<PrivateTagDefinition>();
        int shown = row?.ShownTagChips ?? 0;
        // Beside the group favorite there is room for one chip; the favorite gives up width before the chip does.
        _favorite.MaxWidth = shown > 0 ? 80 : double.PositiveInfinity;
        for (int index = 0; index < _tagChips.Length; index++)
        {
            Button chip = _tagChips[index];
            PrivateTagDefinition? tag = index < shown ? tags[index] : null;
            bool thread = tag != null && row!.IsThreadTagChip(index);
            chip.Tag = tag;
            chip.IsVisible = tag != null;
            ChipLabel(chip).Text = tag?.Name.Display;
            ChipThreadMark(chip).IsVisible = thread;
            chip.Classes.Set(WorkspaceChips.Thread, thread);
            string hint = tag == null ? string.Empty : Texts.T(thread ? "media.tile.threadTagHint" : "media.tile.tagHint", tag.Name.Display);
            ToolTip.SetTip(chip, tag == null ? null : hint);
            AutomationProperties.SetName(chip, hint);
        }
        int more = tags.Count - shown;
        _moreTags.IsVisible = more > 0;
        _moreTags.Text = more > 0 ? "+" + more.ToString(Texts.Culture) : null;
        string moreHint = more > 0 ? Texts.T("media.tile.moreTags", string.Join(", ", tags.Skip(shown).Select(tag => tag.Name.Display))) : string.Empty;
        ToolTip.SetTip(_moreTags, more > 0 ? moreHint : null);
        AutomationProperties.SetName(_moreTags, moreHint);
    }

    /// <summary>The tag name inside a tile chip.</summary>
    internal static TextBlock ChipLabel(Button chip) => (TextBlock)((DockPanel)chip.Content!).Children[1];
    /// <summary>The thread mark inside a tile chip; visible only for a thread tag.</summary>
    internal static Avalonia.Controls.Shapes.Path ChipThreadMark(Button chip) => (Avalonia.Controls.Shapes.Path)((DockPanel)chip.Content!).Children[0];

    /// <summary>
    /// One line: the favorite and the "+N" summary keep their width, the chips share what is left and trim their names, so a long tag
    /// never pushes another part out of the tile.
    /// </summary>
    private sealed class TagRow : Panel
    {
        private const double Spacing = 8;

        protected override Size MeasureOverride(Size availableSize)
        {
            Control[] visible = Children.Where(child => child.IsVisible).ToArray();
            double left = Math.Max(0, availableSize.Width - Spacing * Math.Max(0, visible.Length - 1)), height = 0;
            foreach (Control child in visible.Where(child => child is not Button))
            {
                child.Measure(new Size(left, availableSize.Height));
                left = Math.Max(0, left - child.DesiredSize.Width);
            }
            Button[] chips = visible.OfType<Button>().ToArray();
            for (int index = 0; index < chips.Length; index++)
            {
                chips[index].Measure(new Size(left / (chips.Length - index), availableSize.Height));
                left = Math.Max(0, left - chips[index].DesiredSize.Width);
            }
            foreach (Control child in visible) height = Math.Max(height, child.DesiredSize.Height);
            double width = visible.Sum(child => child.DesiredSize.Width) + Spacing * Math.Max(0, visible.Length - 1);
            return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            foreach (Control child in Children.Where(child => child.IsVisible))
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                x += child.DesiredSize.Width + Spacing;
            }
            return finalSize;
        }
    }

    private async Task LoadAsync()
    {
        if (DataContext is not MediaLibraryTileRow { Entry.Presence: MediaLibraryPresence.Present } row
            || ThumbnailLoader is not { } loader || _loading != null || _bitmap != null) return;
        int request = _request;
        if (ThumbnailCached?.Invoke(row.Tile) is { } cached)
        {
            _bitmap = cached;
            _image.Source = cached; _preview.Aspect = MediaSlotGeometry.AspectOf(cached);
            _placeholder.Text = string.Empty;
            ThumbnailWasCached = true;
            return;
        }
        using CancellationTokenSource loading = new();
        _loading = loading;
        _placeholder.Text = Texts.T("archive.loadingThumbnail");
        Interlocked.Increment(ref _pendingLoads);
        try
        {
            await ThumbnailGate.WaitAsync(loading.Token);
            Bitmap? bitmap;
            try { bitmap = await Task.Run(() => loader(row.Tile, loading.Token), loading.Token); }
            finally { ThumbnailGate.Release(); }
            if (request != _request || !_visible || !ReferenceEquals(DataContext, row)) { Discard(bitmap); return; }
            _bitmap = bitmap;
            ThumbnailWasCached = false;
            _image.Source = bitmap; _preview.Aspect = MediaSlotGeometry.AspectOf(bitmap);
            _placeholder.Text = bitmap == null ? Texts.T("archive.previewUnavailable") : string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (request == _request) _placeholder.Text = Texts.T("archive.previewUnavailable"); }
        finally
        {
            Interlocked.Decrement(ref _pendingLoads);
            if (ReferenceEquals(_loading, loading)) _loading = null;
        }
    }

    private void Discard(Bitmap? bitmap)
    {
        if (bitmap == null) return;
        if (ThumbnailRelease is { } release) release(bitmap); else bitmap.Dispose();
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel(); _loading = null;
        _image.Source = null; _preview.Aspect = null; Discard(_bitmap); _bitmap = null; ThumbnailWasCached = false;
        MediaLibraryTileRow? row = DataContext as MediaLibraryTileRow;
        _placeholder.Text = row?.Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing") : string.Empty;
    }
}

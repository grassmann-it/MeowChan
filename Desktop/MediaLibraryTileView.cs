using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
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
    public string ThreadLabel => IsProviderMedium ? Texts.T("booru.media.tile.post", Entry.BooruPostId!)
        : IsImported ? Texts.T("import.media.tile", Entry.ThreadTitle)
        : Texts.T("media.tile.thread", string.IsNullOrWhiteSpace(Entry.ThreadTitle) ? "#" + Entry.Thread.Thread : Entry.ThreadTitle);
    public string Detail => Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing")
        : Entry.Unassigned && !IsProviderMedium && !IsImported ? Texts.T("media.tile.unassigned")
        : Entry.Length is long length ? SizeText.Format(length) : string.Empty;
    public string DetailWithState => State is { } state && MediaLibraryStateText.Compact(state, Tile.IsGroup) is { Length: > 0 } compact
        ? (Detail.Length == 0 ? compact : Detail + " · " + compact) : Detail;
    /// <summary>The own tags the tile offers as filter chips (media tags first, then thread tags); empty when unknown or none.</summary>
    public IReadOnlyList<PrivateTagDefinition> TagChips => State?.CarriedTags ?? Array.Empty<PrivateTagDefinition>();
    /// <summary>How many chips fit beside what else the row shows: a grouped tile shares its row with the group favorite.</summary>
    public int ShownTagChips => Math.Min(TagChips.Count, Tile.IsGroup ? 1 : 2);
    /// <summary>Favorite state over every saved occurrence of the bytes; only a grouped tile has one to report.</summary>
    public string FavoriteLabel => Tile.IsGroup ? MediaLibraryStateText.Favorite(Tile.Favorite) : string.Empty;
    /// <summary>"in N threads" for equal bytes found elsewhere; empty for a single occurrence.</summary>
    public string Duplicates => !Tile.IsGroup ? string.Empty
        : Tile.ProviderPostCount > 0 && Tile.ArchivedThreadCount == 0 && Tile.ProviderPostCount > 1 ? Texts.T("booru.media.tile.inPosts", Tile.ProviderPostCount)
        : Tile.ProviderPostCount > 0 && Tile.ArchivedThreadCount > 0 ? Texts.T("booru.media.tile.inMixed", Tile.ArchivedThreadCount, Tile.ProviderPostCount)
        : Tile.ThreadCount > 1 ? Texts.T("media.tile.inThreads", Tile.ThreadCount)
        : Texts.T("media.tile.sameThread", Tile.Occurrences.Count);
}

/// <summary>
/// One tile: verified thumbnail for saved images and the local poster of saved videos, a placeholder otherwise. Thumbnails load only while
/// visible and never fetch anything from the network.
/// </summary>
internal sealed class MediaLibraryTileView : UserControl
{
    private static readonly SemaphoreSlim ThumbnailGate = new(2, 2);
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxWidth = 174, MaxHeight = 120 };
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
    private readonly Border _selectedMark = new Border { Name = "MediaTileSelected", IsVisible = false, Padding = new Thickness(5, 1),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0),
        Child = new TextBlock { Text = "✓", FontWeight = FontWeight.Bold, FontSize = 12 } }.BadgeCorners();
    // Downloaded provider media carry a small source badge next to the preview, so they are told apart without opening anything.
    private readonly Border _providerBadge = new Border { Name = "MediaTileProvider", IsVisible = false, Padding = new Thickness(5, 1),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 2, 0, 0),
        Child = new TextBlock { FontWeight = FontWeight.SemiBold }.CaptionText() }.BadgeCorners();
    private readonly TextBlock _favorite = new TextBlock { Name = "MediaTileFavorite", MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false }.CaptionText();
    // Own tags act like the thread label beside them: each chip filters the page to that tag. The row is the favorite row of grouped tiles,
    // so the cell keeps its height; what does not fit is summarized as "+N" with the names in its tooltip.
    private readonly Button[] _tagChips = new Button[2];
    private readonly TextBlock _moreTags = new TextBlock { Name = "MediaTileMoreTags", MaxLines = 1, IsVisible = false, VerticalAlignment = VerticalAlignment.Center }.CaptionText();
    private IDisposable? _selection;
    private Bitmap? _bitmap;
    /// <summary>True while the tile shows a verified thumbnail or video poster.</summary>
    internal bool HasThumbnail => _image.Source != null;
    private CancellationTokenSource? _loading;
    private int _request;
    private bool _visible;

    /// <summary>Set by the page: loads a verified thumbnail (image, or the local video poster) for a tile, or null when none can be shown.</summary>
    internal Func<MediaLibraryTile, CancellationToken, Task<Bitmap?>>? ThumbnailLoader { get; set; }
    /// <summary>Set by the page: shows only the media of the tile's thread.</summary>
    internal Action<MediaLibraryEntry>? ThreadFilter { get; set; }
    /// <summary>Set by the page: shows only the media that carry this own tag.</summary>
    internal Action<Guid>? TagFilter { get; set; }
    /// <summary>Set by the page: lists every occurrence of the tile's bytes.</summary>
    internal Action<MediaLibraryTile>? ShowOccurrences { get; set; }

    /// <summary>Taller than an archive cell: preview, name, thread, footer and the favorite/tag row plus margins. The page's panel uses it as cell height.</summary>
    internal const double TileHeight = 226;

    public MediaLibraryTileView()
    {
        MaxWidth = 190; Height = TileHeight;
        Grid preview = new() { Height = 120, MaxWidth = 174 };
        _selectedMark[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Accent");
        ((TextBlock)_selectedMark.Child!)[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Background");
        _providerBadge[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.AccentSurface");
        _providerBadge[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.Border");
        _providerBadge.BorderThickness = new Thickness(1);
        ((TextBlock)_providerBadge.Child!)[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        preview.Children.Add(_image); preview.Children.Add(_placeholder); preview.Children.Add(_providerBadge); preview.Children.Add(_selectedMark);
        Grid grid = new() { Margin = new Thickness(6, 6, 6, 4), RowDefinitions = new RowDefinitions("120,Auto,Auto,Auto,Auto"), RowSpacing = 3 };
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
        _threadLink.MinHeight = 0;
        foreach (Control part in new Control[] { _duplicatesLink, _detail }) part.VerticalAlignment = VerticalAlignment.Center;
        WrapPanel footer = new() { ItemSpacing = 6, LineSpacing = 0, Children = { _duplicatesLink, _detail } };
        grid.Children.Add(footer); Grid.SetRow(footer, 3);
        TagRow tags = new() { Name = "MediaTileTags", ClipToBounds = true, Children = { _favorite } };
        for (int index = 0; index < _tagChips.Length; index++)
        {
            Button chip = _tagChips[index] = new Button { Name = "MediaTileTag", Padding = new Thickness(0), MinHeight = 0,
                Classes = { "link" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "workspace-meta" } }.CaptionText() };
            chip.Click += (_, e) => { e.Handled = true; if (chip.Tag is PrivateTagDefinition tag) TagFilter?.Invoke(tag.Id.Value); };
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
            ListBoxItem? item = this.FindAncestorOfType<ListBoxItem>();
            _selection = item?.GetObservable(ListBoxItem.IsSelectedProperty).Subscribe(new SelectionObserver(this));
        };
        DetachedFromVisualTree += (_, _) => { _visible = false; Reset(); _selection?.Dispose(); _selection = null; _selectedMark.IsVisible = false; };
    }

    private void ShowSelected(bool selected)
    {
        _selectedMark.IsVisible = selected;
        ToolTip.SetTip(_selectedMark, selected ? Texts.T("media.tile.selected") : null);
    }

    private sealed class SelectionObserver(MediaLibraryTileView view) : IObserver<bool>
    {
        public void OnNext(bool value) => view.ShowSelected(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private void Present()
    {
        MediaLibraryTileRow? row = DataContext as MediaLibraryTileRow;
        _name.Text = row?.Name;
        _thread.Text = row?.ThreadLabel;
        _providerBadge.IsVisible = row?.IsProviderMedium == true;
        ((TextBlock)_providerBadge.Child!).Text = _providerBadge.IsVisible ? Texts.T("booru.media.badge") : null;
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
        string description = row == null ? string.Empty : string.Join(Environment.NewLine, new[] { row.Name, row.IsProviderMedium ? Texts.T("booru.media.badge") : string.Empty, row.ThreadLabel, row.Duplicates, row.FavoriteLabel, row.Detail, row.StateDescription }.Where(part => part.Length > 0));
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, description);
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
            chip.Tag = tag;
            chip.IsVisible = tag != null;
            ((TextBlock)chip.Content!).Text = tag?.Name.Display;
            string hint = tag == null ? string.Empty : Texts.T("media.tile.tagHint", tag.Name.Display);
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

    /// <summary>
    /// One line: the favorite and the "+N" summary keep their width, the chips share what is left and trim their names, so a long tag
    /// never pushes another part out of the tile.
    /// </summary>
    private sealed class TagRow : Panel
    {
        private const double Spacing = 6;

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
        using CancellationTokenSource loading = new();
        _loading = loading;
        _placeholder.Text = Texts.T("archive.loadingThumbnail");
        try
        {
            await ThumbnailGate.WaitAsync(loading.Token);
            Bitmap? bitmap;
            try { bitmap = await Task.Run(() => loader(row.Tile, loading.Token), loading.Token); }
            finally { ThumbnailGate.Release(); }
            if (request != _request || !_visible || !ReferenceEquals(DataContext, row)) { bitmap?.Dispose(); return; }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _placeholder.Text = bitmap == null ? Texts.T("archive.previewUnavailable") : string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (request == _request) _placeholder.Text = Texts.T("archive.previewUnavailable"); }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; }
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel(); _loading = null;
        _image.Source = null; _bitmap?.Dispose(); _bitmap = null;
        MediaLibraryTileRow? row = DataContext as MediaLibraryTileRow;
        _placeholder.Text = row?.Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing") : string.Empty;
    }
}

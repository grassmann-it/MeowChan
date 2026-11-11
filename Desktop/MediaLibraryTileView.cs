using System;
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
    public string ThreadLabel => Texts.T("media.tile.thread", string.IsNullOrWhiteSpace(Entry.ThreadTitle) ? "#" + Entry.Thread.Thread : Entry.ThreadTitle);
    public string Detail => Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing")
        : Entry.Unassigned ? Texts.T("media.tile.unassigned")
        : Entry.Length is long length ? (length / 1_000_000d).ToString("0.###", Texts.Culture) + " MB" : string.Empty;
    public string DetailWithState => State is { } state && MediaLibraryStateText.Compact(state, Tile.IsGroup) is { Length: > 0 } compact
        ? (Detail.Length == 0 ? compact : Detail + " · " + compact) : Detail;
    /// <summary>Favorite state over every saved occurrence of the bytes; only a grouped tile has one to report.</summary>
    public string FavoriteLabel => Tile.IsGroup ? MediaLibraryStateText.Favorite(Tile.Favorite) : string.Empty;
    /// <summary>"in N threads" for equal bytes found elsewhere; empty for a single occurrence.</summary>
    public string Duplicates => !Tile.IsGroup ? string.Empty
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
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxWidth = 170, MaxHeight = 125 };
    private readonly TextBlock _placeholder = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _name = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _thread = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    // The thread acts like a meta tag: its label filters the page to that thread.
    private readonly Button _threadLink = new() { Name = "MediaTileThread", Padding = new Thickness(0), Classes = { "link" },
        HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _detail = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    // Equal bytes in several places: the hint opens the list of all occurrences.
    private readonly TextBlock _duplicates = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly Button _duplicatesLink = new() { Name = "MediaTileDuplicates", Padding = new Thickness(0), Classes = { "link" }, IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
    // Selected tiles carry a check mark besides the list's highlight, so the state does not depend on color alone.
    private readonly Border _selectedMark = new() { Name = "MediaTileSelected", IsVisible = false, Padding = new Thickness(5, 1), CornerRadius = new CornerRadius(8),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0),
        Child = new TextBlock { Text = "✓", FontWeight = FontWeight.Bold, FontSize = 12 } };
    private readonly TextBlock _favorite = new() { Name = "MediaTileFavorite", MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, IsVisible = false };
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
    /// <summary>Set by the page: lists every occurrence of the tile's bytes.</summary>
    internal Action<MediaLibraryTile>? ShowOccurrences { get; set; }

    /// <summary>Taller than an archive cell: preview, name, thread, footer and the favorite row of grouped tiles plus margins. The page's panel uses it as cell height.</summary>
    internal const double TileHeight = 248;

    public MediaLibraryTileView()
    {
        MaxWidth = 190; Height = TileHeight;
        Grid preview = new() { Height = 125, MaxWidth = 170 };
        _selectedMark[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Accent");
        ((TextBlock)_selectedMark.Child!)[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Background");
        preview.Children.Add(_image); preview.Children.Add(_placeholder); preview.Children.Add(_selectedMark);
        Grid grid = new() { Margin = new Thickness(8), RowDefinitions = new RowDefinitions("125,Auto,Auto,Auto,Auto"), RowSpacing = 4 };
        grid.Children.Add(preview);
        grid.Children.Add(_name); Grid.SetRow(_name, 1);
        _threadLink.Content = _thread;
        _threadLink.Click += (_, e) => { e.Handled = true; if (DataContext is MediaLibraryTileRow row) ThreadFilter?.Invoke(row.Entry); };
        grid.Children.Add(_threadLink); Grid.SetRow(_threadLink, 2);
        _duplicatesLink.Content = _duplicates;
        _duplicatesLink.Click += (_, e) => { e.Handled = true; if (DataContext is MediaLibraryTileRow row) ShowOccurrences?.Invoke(row.Tile); };
        StackPanel footer = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _duplicatesLink, _detail } };
        grid.Children.Add(footer); Grid.SetRow(footer, 3);
        grid.Children.Add(_favorite); Grid.SetRow(_favorite, 4);
        foreach (TextBlock text in new[] { _thread, _detail, _duplicates, _favorite }) text.Classes.Add("workspace-meta");
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
        string threadHint = row == null ? string.Empty : Texts.T("media.filter.threadHint", row.ThreadLabel);
        ToolTip.SetTip(_threadLink, threadHint);
        AutomationProperties.SetName(_threadLink, threadHint);
        _detail.Text = row?.DetailWithState;
        string duplicates = row?.Duplicates ?? string.Empty;
        _duplicates.Text = duplicates;
        _duplicatesLink.IsVisible = duplicates.Length > 0;
        _favorite.Text = row?.FavoriteLabel;
        _favorite.IsVisible = !string.IsNullOrEmpty(_favorite.Text);
        string duplicatesHint = row == null || duplicates.Length == 0 ? string.Empty : Texts.T("media.tile.duplicatesHint", row.Tile.Occurrences.Count);
        ToolTip.SetTip(_duplicatesLink, duplicatesHint);
        AutomationProperties.SetName(_duplicatesLink, duplicatesHint);
        _placeholder.Text = row?.Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing") : string.Empty;
        string description = row == null ? string.Empty : string.Join(Environment.NewLine, new[] { row.Name, row.ThreadLabel, row.Duplicates, row.FavoriteLabel, row.Detail, row.StateDescription }.Where(part => part.Length > 0));
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, description);
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

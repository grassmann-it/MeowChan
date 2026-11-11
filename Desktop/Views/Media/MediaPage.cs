using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan;

/// <summary>
/// Archive-wide media grid. Reads the catalog and media manifests only; builds the derived media index
/// in the background, shows a cached index right away, and never changes archive data.
/// </summary>
public sealed partial class MediaPage : UserControl
{
    private readonly Func<CancellationToken, Task<ArchiveReader>> _openReader;
    private readonly Func<string, CancellationToken, Task<MediaAssetSnapshot>> _openManifest;
    private readonly string? _cachePath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Task<MediaAssetSnapshot>> _manifests = new(StringComparer.Ordinal);
    private readonly TextBlock _title = new() { Classes = { "section-title" } };
    private readonly TextBlock _subtitle = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } };
    private readonly ComboBox _sort = new() { Name = "MediaSort", MinWidth = 160 };
    private readonly Button _shuffle = new() { Name = "MediaShuffle", IsVisible = false };
    private readonly CheckBox _missing = new() { Name = "MediaShowMissing" };
    private readonly Button _refresh = new() { Name = "MediaRefresh" };
    private readonly TextBlock _status = new() { Name = "MediaStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly ListBox _grid = new() { Name = "MediaGrid", SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _empty = new() { Name = "MediaEmpty", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 40, 8, 8), IsVisible = false, Classes = { "muted" } };
    private MediaLibrarySnapshot? _snapshot;
    private IReadOnlyList<MediaLibraryEntry> _shown = Array.Empty<MediaLibraryEntry>();
    private IReadOnlyList<MediaLibraryTile> _tiles = Array.Empty<MediaLibraryTile>();
    private MediaLibraryProgress? _progress;
    private CancellationTokenSource? _building;
    private bool _failed;
    private bool _localizing;
    private bool _available;
    private int _seed = Environment.TickCount;
    private int _unfiltered;

    /// <summary>Opens the gallery for a window of shown entries; the int is the chosen entry inside it.</summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>, int, Task>? OpenGallery { get; set; }
    internal Func<MediaLibraryEntry, Task>? ShowInThread { get; set; }
    /// <summary>Edits the own media tags of one entry through the host's tag commands.</summary>
    internal Func<MediaLibraryEntry, Task>? EditTags { get; set; }
    internal Task BuildTask { get; private set; } = Task.CompletedTask;
    /// <summary>One entry per tile: the occurrence each tile shows. Gallery windows are made from this list.</summary>
    internal IReadOnlyList<MediaLibraryEntry> ShownEntries => _shown;
    internal IReadOnlyList<MediaLibraryTile> Tiles => _tiles;
    internal MediaLibrarySnapshot? Snapshot => _snapshot;
    internal ListBox Grid => _grid;

    internal MediaPage(Func<CancellationToken, Task<ArchiveReader>> openReader,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>>? openManifest = null, string? cachePath = null)
    {
        _openReader = openReader ?? throw new ArgumentNullException(nameof(openReader));
        _openManifest = openManifest ?? MediaAssetReader.OpenAsync;
        _cachePath = cachePath;
        Name = "MediaPage";
        WrapPanel tools = new() { ItemSpacing = 8, LineSpacing = 6 };
        foreach (Control control in new Control[] { _sort, _shuffle, _missing, _refresh }) { control.VerticalAlignment = VerticalAlignment.Center; tools.Children.Add(control); }
        StackPanel header = new() { Spacing = 6, Margin = new Thickness(12, 12, 12, 6) };
        header.Children.Add(_title); header.Children.Add(_subtitle); header.Children.Add(tools);
        header.Children.Add(BuildFilterRow()); header.Children.Add(_filterChips); header.Children.Add(_filterError); header.Children.Add(_status); header.Children.Add(BuildSelectionBar());
        _grid.ItemsPanel = new FuncTemplate<Panel?>(() => new MediaTilePanel());
        _grid.ItemTemplate = new FuncDataTemplate<MediaLibraryTileRow>((_, _) => new MediaLibraryTileView { ThumbnailLoader = LoadThumbnailAsync, ThreadFilter = FilterToThread, ShowOccurrences = ShowOccurrences });
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Panel body = new();
        body.Children.Add(_grid); body.Children.Add(_empty);
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(header);
        Border surface = new() { Child = body, Margin = new Thickness(12, 0, 12, 12), Classes = { "panel" } };
        layout.Children.Add(surface); Avalonia.Controls.Grid.SetRow(surface, 1);
        Content = layout;

        _sort.SelectionChanged += (_, _) => { if (!_localizing) Present(); };
        _shuffle.Click += (_, _) => { _seed = unchecked(_seed * 31 + 17); Present(); };
        _missing.IsCheckedChanged += (_, _) => { if (!_localizing) Present(); };
        _refresh.Click += (_, _) => BuildTask = BuildAsync(force: true);
        _grid.DoubleTapped += (_, _) => _ = OpenSelectedAsync();
        _grid.KeyDown += OnGridKeyDown;
        _grid.SelectionChanged += (_, _) => { if (_restoring) return; ShowSelectionNote(null); UpdateSelectionBar(); };
        _grid.ContextRequested += OnContextRequested;
        Texts.Changed += OnLanguageChanged;
        Localize();
    }

    internal bool IsAvailable
    {
        set { _available = value; UpdateStatus(); }
    }

    internal MediaLibrarySort Sort => _sort.SelectedIndex switch { 1 => MediaLibrarySort.Newest, 2 => MediaLibrarySort.Random, _ => MediaLibrarySort.ByThread };

    /// <summary>Shows the cached index at once, then brings it up to date in the background.</summary>
    internal Task ActivateAsync()
    {
        RefreshUserData();
        if (_building != null) return BuildTask;
        if (_snapshot == null && _cachePath != null && MediaLibraryIndexCache.Load(_cachePath) is { } cached)
        {
            _snapshot = cached;
            Present();
        }
        return BuildTask = BuildAsync(force: false);
    }

    private async Task BuildAsync(bool force)
    {
        if (_lifetime.IsCancellationRequested) return;
        _building?.Cancel();
        CancellationTokenSource building = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _building = building;
        _failed = false;
        lock (_manifests) _manifests.Clear(); // Files may have changed: thumbnails re-open their manifests.
        Progress<MediaLibraryProgress> progress = new(value => { if (ReferenceEquals(_building, building)) { _progress = value; UpdateStatus(); } });
        UpdateStatus();
        try
        {
            MediaLibrarySnapshot? previous = force ? null : _snapshot;
            MediaLibrarySnapshot built = await Task.Run(() => MediaLibraryIndexBuilder.BuildAsync(_openReader, _openManifest,
                previous, progress, building.Token), building.Token);
            if (!ReferenceEquals(_building, building)) return;
            _snapshot = built;
            if (_cachePath != null)
            {
                try { await Task.Run(() => MediaLibraryIndexCache.Save(_cachePath, built, building.Token), building.Token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // A cache is optional.
            }
            Present();
        }
        catch (OperationCanceledException) when (building.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (ReferenceEquals(_building, building)) _failed = true;
        }
        finally
        {
            if (ReferenceEquals(_building, building)) { _building = null; _progress = null; UpdateStatus(); }
            building.Dispose();
        }
    }

    private void Present()
    {
        // Selected tiles that are still shown stay selected; the others drop out (see MediaPage.Selection.cs).
        MediaLibraryTileKey[] selected = SelectedTiles.Select(MediaLibraryTileKey.Of).ToArray();
        IReadOnlyList<MediaLibraryEntry> all = _snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>();
        bool includeMissing = _missing.IsChecked == true;
        // Filter the occurrences first; a tile of equal bytes stays when any of its occurrences passed.
        _tiles = MediaLibraryGrouping.Build(all, ApplyFilter(all), Sort, includeMissing, _seed, _userData.IsFavorite);
        _shown = _tiles.Select(tile => tile.Entry).ToArray();
        _unfiltered = _filterActive ? MediaLibraryGrouping.CountTiles(all, includeMissing) : _shown.Count;
        // Own state is read from the data this presentation was built with, so a row never mixes two reads.
        MediaLibraryUserData userData = _userData;
        Func<MediaLibraryEntry, MediaLibraryEntryState> state = entry => MediaLibraryEntryState.Of(entry, userData);
        MediaLibraryTileRow[] rows = _tiles.Select((tile, index) => new MediaLibraryTileRow(tile, index, state)).ToArray();
        _restoring = true;
        try
        {
            _grid.ItemsSource = rows;
            foreach (int index in MediaLibrarySelection.Restore(selected, _tiles)) _grid.Selection.Select(index);
        }
        finally { _restoring = false; }
        _shuffle.IsVisible = Sort == MediaLibrarySort.Random;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int threads = _snapshot?.Threads.Count ?? 0;
        int missing = _shown.Count(entry => entry.Presence == MediaLibraryPresence.Missing);
        List<string> parts = new();
        if (_progress is { } progress && progress.ThreadsTotal > 0) parts.Add(Texts.T("media.indexing", progress.ThreadsDone, progress.ThreadsTotal));
        if (_snapshot != null) parts.Add(_filterActive && !_filterFailed ? Texts.T("media.filter.count", _shown.Count, _unfiltered)
            : missing > 0 ? Texts.T("media.countMissing", _shown.Count, threads, missing) : Texts.T("media.count", _shown.Count, threads));
        if (_snapshot is { Complete: false } && _building == null) parts.Add(Texts.T("media.incomplete"));
        if (_failed) parts.Add(Texts.T("media.failed"));
        if (_snapshot == null && _building == null && !_failed && !_available) parts.Add(Texts.T("media.notReady"));
        _status.Text = string.Join(" · ", parts);
        _empty.Text = Texts.T(_filterActive ? "media.filter.noMatch" : "media.empty");
        // A refused filter explains itself above the grid; the grid stays empty.
        _empty.IsVisible = _snapshot != null && _shown.Count == 0 && _building == null && !_filterFailed;
        _refresh.IsEnabled = !_lifetime.IsCancellationRequested;
        UpdateSelectionBar();
    }

    internal string PosterCacheDirectory { get; set; } = System.IO.Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1");

    private Task<Bitmap?> LoadThumbnailAsync(MediaLibraryTile tile, CancellationToken token) =>
        MediaTileThumbnails.LoadAsync(tile, OpenManifestAsync, PosterCacheDirectory, token);

    private Task<MediaAssetSnapshot> OpenManifestAsync(string directory, CancellationToken token)
    {
        Task<MediaAssetSnapshot> manifestTask;
        lock (_manifests)
        {
            if (!_manifests.TryGetValue(directory, out manifestTask!))
                _manifests[directory] = manifestTask = _openManifest(directory, _lifetime.Token);
        }
        return manifestTask.WaitAsync(token);
    }

    private MediaLibraryTileRow? SelectedRow => _grid.SelectedItem as MediaLibraryTileRow;

    internal async Task OpenSelectedAsync()
    {
        if (SelectedRow is not { } row || OpenGallery == null) return;
        (IReadOnlyList<MediaLibraryEntry> window, int start) = MediaLibraryView.GalleryWindow(_shown, row.Index);
        if (start < 0) return;
        await OpenGallery(window, start);
    }

    internal async Task EditSelectedTagsAsync()
    {
        if (SelectedTiles.Count == 1 && SelectedRow is { State.CanEditMediaTags: true } row && EditTags != null) await EditTags(row.Entry);
    }

    internal async Task ShowSelectedInThreadAsync()
    {
        if (SelectedRow is { Entry.PostId: not null } row && ShowInThread != null) await ShowInThread(row.Entry);
    }

    private async void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (OnSelectionKey(e)) return;
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || SelectedRow == null) return;
        e.Handled = true;
        // Several selected tiles open together; one opens around itself.
        if (SelectedTiles.Count > 1) await OpenSelectionGalleryAsync(); else await OpenSelectedAsync();
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        bool single = SelectedTiles.Count == 1; // The per-tile entries act on one tile only.
        MenuItem open = new() { Header = Texts.T("media.open"), IsEnabled = single && row.Entry.Presence == MediaLibraryPresence.Present };
        open.Click += (_, _) => _ = OpenSelectedAsync();
        MenuItem thread = new() { Header = Texts.T("media.showInThread"), IsEnabled = single && row.Entry.PostId != null };
        thread.Click += (_, _) => _ = ShowSelectedInThreadAsync();
        MenuItem onlyThread = new() { Header = Texts.T("media.filter.onlyThread"), IsEnabled = single };
        onlyThread.Click += (_, _) => FilterToThread(row.Entry);
        MenuItem tags = new() { Header = Texts.T("media.tags.edit"), IsEnabled = single && row.State?.CanEditMediaTags == true && EditTags != null };
        tags.Click += (_, _) => _ = EditSelectedTagsAsync();
        MenuItem occurrences = new() { Header = Texts.T("media.occurrences"), IsEnabled = single && row.Tile.IsGroup };
        occurrences.Click += (_, _) => ShowOccurrences(row.Tile);
        List<object> items = new() { open, tags, thread, onlyThread, occurrences, new Separator() };
        foreach ((Button action, string key) in new[] { (_selTag, "media.selection.tag"), (_selGallery, "media.selection.gallery"), (_selExport, "media.selection.export") })
        {
            MenuItem item = new() { Header = Texts.T(key), IsEnabled = action.IsEnabled };
            item.Click += (_, _) => action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            items.Add(item);
        }
        ContextMenu menu = new() { ItemsSource = items };
        _grid.ContextMenu = menu;
        menu.Open(_grid);
        e.Handled = true;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Localize);

    private void Localize()
    {
        _localizing = true;
        try
        {
            int sort = Math.Max(0, _sort.SelectedIndex);
            _sort.ItemsSource = new[] { Texts.T("media.sort.thread"), Texts.T("media.sort.newest"), Texts.T("media.sort.random") };
            _sort.SelectedIndex = sort;
        }
        finally { _localizing = false; }
        _title.Text = Texts.T("media.title");
        _subtitle.Text = Texts.T("media.subtitle");
        AutomationProperties.SetName(_sort, Texts.T("media.sort"));
        _shuffle.Content = Texts.T("media.shuffle");
        _missing.Content = Texts.T("media.showMissing");
        _refresh.Content = Texts.T("media.refresh");
        AutomationProperties.SetName(_grid, Texts.T("media.title"));
        LocalizeFilters();
        LocalizeSelection();
        if (_grid.ItemsSource != null) Present(); else UpdateStatus();
    }

    internal void EndLifetime()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _building?.Cancel();
        Texts.Changed -= OnLanguageChanged;
        _occurrences?.Close();
        _selectionTag?.CloseForLifetime();
    }
}

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
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan;

/// <summary>
/// Archive-wide media grid. Reads the catalog and media manifests only; builds the derived media index
/// in the background, shows a cached index right away, and never changes archive data.
/// </summary>
public sealed partial class MediaPage : UserControl
{
    private readonly Func<CancellationToken, Task<ArchiveReader>> _openReader;
    private readonly Func<string, CancellationToken, Task<MediaAssetSnapshot>> _openManifest;
    private readonly MediaLibraryIndexCacheFile? _cacheFile;
    private readonly CancellationTokenSource _lifetime = new();
    // Manifest views of this page (thumbnails, sound check) that the index build may reuse; see MediaManifestCache.
    private readonly MediaManifestCache _manifests;
    private readonly TextBlock _title = new() { Classes = { "section-title" } };
    private readonly TextBlock _subtitle = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } };
    private readonly ComboBox _sort = new() { Name = "MediaSort", MinWidth = 160 };
    private readonly Button _shuffle = new() { Name = "MediaShuffle", IsVisible = false };
    private readonly CheckBox _missing = new() { Name = "MediaShowMissing" };
    private readonly Button _refresh = new() { Name = "MediaRefresh" };
    private readonly TextBlock _status = new() { Name = "MediaStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly ListBox _grid = new() { Name = "MediaGrid", SelectionMode = SelectionMode.Multiple, Classes = { "workspace-selection-bar" } };
    private readonly TextBlock _empty = new() { Name = "MediaEmpty", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 40, 8, 8), IsVisible = false, Classes = { "muted" } };
    // An empty library points back to where media comes from; a filter that matches nothing offers its reset (MediaPage.FilterChips.cs).
    private readonly Button _goDownloads = new() { Name = "MediaGoDownloads", Classes = { "primary" } };
    private readonly Button _goArchive = new() { Name = "MediaGoArchive" };
    private readonly WrapPanel _emptyActions = new() { Name = "MediaEmptyActions", ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(8, 0, 8, 8) };
    private readonly StackPanel _emptyState = new() { Name = "MediaEmptyState", VerticalAlignment = VerticalAlignment.Top, IsVisible = false };
    private MediaLibrarySnapshot? _snapshot;
    private IReadOnlyList<MediaLibraryEntry> _shown = Array.Empty<MediaLibraryEntry>();
    private IReadOnlyList<MediaLibraryTile> _tiles = Array.Empty<MediaLibraryTile>();
    private MediaLibraryProgress? _progress;
    private CancellationTokenSource? _building;
    // The saved index is read once per page, off the UI thread; while it loads the status says so.
    private bool _cacheTried;
    private bool _loadingCache;
    private MediaLibraryIndexCacheWrite? _cacheWrite;
    // Counts the status line needs on every update, computed once per presentation or index instead of per progress tick.
    private int _shownMissing;
    private int _providerEntries;
    private bool _failed;
    private bool _localizing;
    private bool _available;
    private int _seed = Environment.TickCount;
    private int _unfiltered;
    private bool _sharedMediaMenusInitialized;
    private long _mediaMenuRevision;
    private long _mediaMenuSerial;
    private bool _sharedPointerContextPending;

    /// <summary>Opens the gallery for a window of shown entries; the int is the chosen entry inside it.</summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>, int, Task>? OpenGallery { get; set; }
    /// <summary>Set by the shell: the footer destinations offered by the empty state.</summary>
    internal Action? GoToDownloadsRequested { get; set; }
    internal Action? GoToArchiveRequested { get; set; }
    internal Func<MediaLibraryEntry, Task>? ShowInThread { get; set; }
    /// <summary>Edits the own media tags of one entry through the host's tag commands.</summary>
    internal Func<MediaLibraryEntry, Task>? EditTags { get; set; }
    // The shell supplies the guarded executor for the shared menu. Keeping this seam on
    // the page means menu construction never resolves a selected row at invocation time.
    internal Func<string, MediaLibraryEntry, Func<bool>, Task>? ExecuteMediaMenuActionAsync { get; set; }
    /// <summary>
    /// Read-only access to the local Booru catalog (downloaded provider posts). Null: the page shows archived media only.
    /// An unreadable catalog (the function returns null) keeps the provider media the previous index showed.
    /// </summary>
    internal Func<BooruCatalogSnapshot?>? ReadBooruCatalog { get; set; }
    /// <summary>Read-only access to the import catalog (manually imported files). Null: the page shows no imports.</summary>
    internal Func<Import.ManualImportCatalogSnapshot?>? ReadImportCatalog { get; set; }
    /// <summary>Opens a provider post page; the shell supplies the browser launcher, tests a recorder.</summary>
    internal Func<Uri, Task>? OpenProviderPage { get; set; }
    /// <summary>Copies a provider post address to the clipboard; the default uses this page's clipboard.</summary>
    internal Func<Uri, Task>? CopyProviderPage { get; set; }
    internal Func<MediaLibraryEntry, Func<bool>, Task>? EditTagsForMenuAsync { get; set; }
    internal ContextMenu? MediaMenu => _mediaMenuPresenter.Menu;
    internal MediaMenuSession? MediaMenuSession => _mediaMenuPresenter.Session;
    internal Task BuildTask { get; private set; } = Task.CompletedTask;
    /// <summary>At most this often does a running build update the status line (the build reports once per thread).</summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    /// <summary>Write limit of the saved index in bytes; the shell reads the setting each time. Null: the default limit.</summary>
    internal Func<long>? CacheLimitBytes { get; set; }
    /// <summary>The configured limits of entries and threads the saved index may hold (settings).</summary>
    internal Func<(int Entries, int Threads)>? CacheCountLimits { get; set; }
    /// <summary>Reads the saved index; tests replace it to hold the load open.</summary>
    internal Func<MediaLibraryIndexCacheFile, CancellationToken, Task<MediaLibrarySnapshot?>> LoadCache { get; set; } =
        (file, token) => file.LoadAsync(token);
    internal MediaLibraryIndexCacheFile? CacheFile => _cacheFile;
    internal bool LoadingCache => _loadingCache;
    internal MediaLibraryIndexCacheWrite? LastCacheWrite => _cacheWrite;
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
        // Only the real manifest reader is checked by file identity; a supplied opener keeps the per-build behavior.
        _manifests = new MediaManifestCache(openManifest, _lifetime.Token);
        _cacheFile = cachePath == null ? null : new MediaLibraryIndexCacheFile(cachePath);
        Name = "MediaPage";
        WrapPanel tools = new() { ItemSpacing = 6, LineSpacing = 4 };
        foreach (Control control in new Control[] { _filters, _sort, _shuffle, _missing, _refresh, _exportAll }) { control.VerticalAlignment = VerticalAlignment.Center; tools.Children.Add(control); }
        StackPanel header = new() { Spacing = 4, Margin = new Thickness(12, 8, 12, 4) };
        header.Children.Add(_title); header.Children.Add(_subtitle); header.Children.Add(tools);
        // The filter fields open as a surface over the grid (MediaPage.FilterSurface.cs); what is applied stays in the header.
        Border filterSurface = BuildFilterSurface(BuildFilterRow());
        header.Children.Add(BuildFilterChipRow()); header.Children.Add(_filterError); header.Children.Add(BuildStatusRow(_status)); header.Children.Add(BuildExportAllAsk()); header.Children.Add(BuildSelectionBar());
        _grid.ItemsPanel = new FuncTemplate<Panel?>(() => new MediaTilePanel { ItemHeight = MediaLibraryTileView.TileHeight });
        _grid.ItemTemplate = new FuncDataTemplate<MediaLibraryTileRow>((_, _) => new MediaLibraryTileView { ThumbnailLoader = LoadThumbnailAsync, ThreadFilter = FilterToThread,
            TagFilter = FilterToTag, ShowOccurrences = ShowOccurrences, FavoriteToggle = ToggleTileFavoriteAsync,
            CanToggleFavorite = () => ExecuteMediaMenuActionAsync != null });
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Panel body = new();
        _emptyActions.Children.Add(_goDownloads); _emptyActions.Children.Add(_goArchive);
        _emptyState.Children.Add(_empty); _emptyState.Children.Add(_emptyActions);
        body.Children.Add(_grid); body.Children.Add(_emptyState);
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(header);
        Border surface = new() { Name = "MediaGridSurface", Child = body, Margin = new Thickness(12, 0, 12, 8), Classes = { "panel" } };
        layout.Children.Add(surface); Avalonia.Controls.Grid.SetRow(surface, 1);
        layout.Children.Add(filterSurface); Avalonia.Controls.Grid.SetRow(filterSurface, 1);
        Content = layout;

        _sort.SelectionChanged += (_, _) => { if (!_localizing) Present(); };
        _shuffle.Click += (_, _) => { _seed = unchecked(_seed * 31 + 17); Present(); };
        _missing.IsCheckedChanged += (_, _) => { if (!_localizing) Present(); };
        _refresh.Click += (_, _) => BuildTask = BuildAsync(force: true);
        _goDownloads.Click += (_, _) => GoToDownloadsRequested?.Invoke();
        _goArchive.Click += (_, _) => GoToArchiveRequested?.Invoke();
        _grid.DoubleTapped += (_, _) => _ = OpenSelectedAsync();
        _grid.KeyDown += OnGridKeyDown;
        _grid.SelectionChanged += (_, _) => { if (_restoring) return; ShowSelectionNote(null); UpdateSelectionBar(); };
        InstallReturnInterruption();
        Texts.Changed += OnLanguageChanged;
        Localize();
    }

    internal bool IsAvailable
    {
        set { _available = value; UpdateStatus(); }
    }

    internal MediaLibrarySort Sort => _sort.SelectedIndex switch { 1 => MediaLibrarySort.Newest, 2 => MediaLibrarySort.Random, _ => MediaLibrarySort.ByThread };

    /// <summary>
    /// Shows the saved index as soon as it is read (in the background; the page stays responsive and says that it is
    /// loading), then brings it up to date in the background.
    /// </summary>
    internal Task ActivateAsync()
    {
        RefreshUserData();
        UpdateSearchTip();
        if (_building != null) return BuildTask;
        return BuildTask = BuildAsync(force: false);
    }

    private void SetSnapshot(MediaLibrarySnapshot snapshot)
    {
        _snapshot = snapshot;
        _providerEntries = snapshot.Entries.Count(entry => entry.BooruPostId != null);
    }

    /// <summary>Rebuilds the index (reusing unchanged threads), for example after an import added files.</summary>
    internal Task RebuildAsync() => BuildTask = BuildAsync(force: false);

    private async Task BuildAsync(bool force)
    {
        if (_lifetime.IsCancellationRequested) return;
        _building?.Cancel();
        CancellationTokenSource building = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _building = building;
        _failed = false;
        _manifests.Invalidate(); // Files may have changed: earlier manifest views must prove their file identity again.
        Progress<MediaLibraryProgress> posted = new(value => { if (ReferenceEquals(_building, building)) { _progress = value; UpdateStatusText(); } });
        // The build reports once per thread; the status line needs at most a few updates per second (and the last one).
        ThrottledProgress<MediaLibraryProgress> progress = new(posted, ProgressInterval, value => value.ThreadsDone >= value.ThreadsTotal);
        UpdateStatus();
        try
        {
            if (!force && _snapshot == null && _cacheFile is { } cacheFile && !_cacheTried)
            {
                _cacheTried = true;
                _loadingCache = true;
                UpdateStatus();
                MediaLibrarySnapshot? cached;
                Func<MediaLibraryIndexCacheFile, CancellationToken, Task<MediaLibrarySnapshot?>> load = LoadCache;
                try { cached = await Task.Run(() => load(cacheFile, building.Token), building.Token); }
                finally { _loadingCache = false; }
                if (!ReferenceEquals(_building, building)) return;
                if (cached != null && _snapshot == null)
                {
                    SetSnapshot(cached);
                    Present();
                }
                else UpdateStatus();
            }
            MediaLibrarySnapshot? previous = force ? null : _snapshot;
            Func<BooruCatalogSnapshot?>? readBooru = ReadBooruCatalog;
            Func<Import.ManualImportCatalogSnapshot?>? readImports = ReadImportCatalog;
            // A refresh reads every manifest again (no reuse of kept views); identities are still recorded for the next open.
            MediaManifestCache manifests = force ? new MediaManifestCache(_manifests.UsesIdentity ? null : _openManifest, building.Token) : _manifests;
            MediaLibrarySnapshot built = await Task.Run(() => MediaLibraryIndexBuilder.BuildAsync(_openReader, manifests,
                previous, progress, building.Token, readBooru, readImports), building.Token);
            if (!ReferenceEquals(_building, building)) return;
            SetSnapshot(built);
            _progress = null;
            Present();
            if (_cacheFile is { } file)
            {
                long limit = CacheLimitBytes?.Invoke() ?? MediaLibraryIndexCache.DefaultLimitBytes;
                // Written only when the index differs from what the file holds; otherwise this is a comparison in memory.
                (int entryLimit, int threadLimit) = CacheCountLimits?.Invoke() ?? (MediaLibraryIndexCache.DefaultEntryLimit, MediaLibraryIndexCache.DefaultThreadLimit);
                try { _cacheWrite = await Task.Run(() => file.SaveAsync(built, limit, entryLimit, threadLimit, building.Token), building.Token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // A cache is optional.
                if (!ReferenceEquals(_building, building)) return;
            }
            StartAudioProbe(built);
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

    /// <summary>How often the grid has been rebuilt; lets a test see that typing does not rebuild it per keystroke.</summary>
    internal int PresentCount { get; private set; }

    private void Present()
    {
        PresentCount++;
        InvalidateSharedMediaMenu();
        DismissExportAllAsk(); // A pending "export all" question was about tiles that may no longer be shown.
        // Selected tiles that are still shown stay selected; the others drop out (see MediaPage.Selection.cs).
        MediaLibraryTileKey[] selected = SelectedTiles.Select(MediaLibraryTileKey.Of).ToArray();
        IReadOnlyList<MediaLibraryEntry> all = _snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>();
        bool includeMissing = _missing.IsChecked == true;
        // Filter the occurrences first; a tile of equal bytes stays when any of its occurrences passed.
        _tiles = MediaLibraryGrouping.Build(all, ApplyFilter(all), Sort, includeMissing, _seed, _userData.IsFavorite);
        _shown = _tiles.Select(tile => tile.Entry).ToArray();
        _shownMissing = _shown.Count(entry => entry.Presence == MediaLibraryPresence.Missing);
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
        UpdateStatusText();
        _empty.Text = Texts.T(_filterActive ? "media.filter.noMatch" : "media.empty");
        // A refused filter explains itself above the grid; the grid stays empty.
        bool showEmpty = _snapshot != null && _shown.Count == 0 && _building == null && !_filterFailed;
        _empty.IsVisible = showEmpty;
        _emptyState.IsVisible = showEmpty;
        // An empty library points to Downloads and Archive; a filter that matches nothing offers to reset it (C3).
        _emptyActions.IsVisible = showEmpty;
        _goDownloads.IsVisible = _goArchive.IsVisible = !_filterActive;
        _emptyReset.IsVisible = _filterActive;
        _refresh.IsEnabled = !_lifetime.IsCancellationRequested;
        UpdateUntaggedLink();
        UpdateSearchResult();
        UpdateFilterToggle();
        UpdateSelectionBar();
    }

    // The status line alone: what a progress tick changes. Every count in it is kept, so a tick costs no pass over the index.
    private void UpdateStatusText()
    {
        int threads = _snapshot?.Threads.Count ?? 0;
        int missing = _shownMissing;
        List<string> parts = new();
        if (_loadingCache) parts.Add(Texts.T("media.cache.loading"));
        if (_progress is { } progress && progress.ThreadsTotal > 0) parts.Add(Texts.T("media.indexing", progress.ThreadsDone, progress.ThreadsTotal));
        if (_snapshot != null) parts.Add(_filterActive && !_filterFailed ? Texts.T("media.filter.count", _shown.Count, _unfiltered)
            : missing > 0 ? Texts.Count("media.countMissing", threads, _shown.Count, threads, missing) : Texts.Count("media.count", threads, _shown.Count, threads));
        if (_snapshot != null && _providerEntries is > 0 and int provider)
            parts.Add(Texts.T("booru.media.statusCount", provider));
        if (_snapshot is { Complete: false } && _building == null) parts.Add(Texts.T("media.incomplete"));
        if (_failed) parts.Add(Texts.T("media.failed"));
        if (_snapshot == null && _building == null && !_failed && !_available) parts.Add(Texts.T("media.notReady"));
        if (CacheLimitNote() is { } note) parts.Add(note);
        _status.Text = string.Join(" · ", parts);
    }

    // The saved index has a write limit (a setting); near it and above it the page says so instead of silently rebuilding.
    private string? CacheLimitNote()
    {
        if (_cacheWrite is not { } write) return null;
        string Size(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#", Texts.Culture) + " MiB";
        string setting = Texts.T("budgets.mediaIndexCache"), category = Texts.T("settings.storageLimits");
        return write.Status switch
        {
            MediaLibraryIndexCacheWriteStatus.TooManyEntries => Texts.T("media.cache.tooMany",
                (CacheCountLimits?.Invoke().Entries ?? MediaLibraryIndexCache.DefaultEntryLimit).ToString("N0", Texts.Culture),
                (CacheCountLimits?.Invoke().Threads ?? MediaLibraryIndexCache.DefaultThreadLimit).ToString("N0", Texts.Culture),
                Texts.T("budgets.mediaIndexEntries"), Texts.T("budgets.mediaIndexThreads"), category),
            MediaLibraryIndexCacheWriteStatus.TooLarge => Texts.T("media.cache.overLimit", Size(write.LimitBytes), setting, category),
            _ when write.NearOrOverLimit => Texts.T("media.cache.nearLimit", Size(write.Bytes), Size(write.LimitBytes), setting, category),
            _ => null
        };
    }

    internal string PosterCacheDirectory { get; set; } = System.IO.Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1");

    private Task<Bitmap?> LoadThumbnailAsync(MediaLibraryTile tile, CancellationToken token) =>
        MediaTileThumbnails.LoadAsync(tile, OpenManifestAsync, PosterCacheDirectory, token);

    private Task<MediaAssetSnapshot> OpenManifestAsync(string directory, CancellationToken token) => _manifests.OpenAsync(directory, token);

    private MediaLibraryTileRow? SelectedRow => _grid.SelectedItem as MediaLibraryTileRow;

    internal async Task OpenSelectedAsync()
    {
        if (SelectedRow is not { } row || OpenGallery == null) return;
        (IReadOnlyList<MediaLibraryEntry> window, int start) = MediaLibraryView.GalleryWindow(_shown, row.Index);
        if (start < 0) return;
        await OpenGallery(window, start);
    }

    /// <summary>Puts the keyboard focus back on the grid (its selected tile, else the first) once the tiles are laid out; the list itself is not focusable.</summary>
    internal void FocusGrid(Func<bool>? current = null) => Dispatcher.UIThread.Post(() =>
    {
        if (_lifetime.IsCancellationRequested || !_grid.IsEffectivelyVisible || current?.Invoke() == false) return;
        int index = SelectedRow?.Index ?? (_grid.ItemCount > 0 ? 0 : -1);
        if (index < 0) return;
        _grid.ScrollIntoView(index);
        if (current?.Invoke() == false) return; // Scrolling can run layout and invalidate the presentation.
        (_grid.ContainerFromIndex(index) as Control ?? _grid).Focus();
    }, DispatcherPriority.Loaded);

    internal void RestoreMediaMenuActionFocus(Control? origin, Func<bool> hostCurrent)
    {
        long revision = _mediaMenuRevision;
        long serial = _mediaMenuSerial;
        object? rows = _grid.ItemsSource;
        object? originData = origin?.DataContext;
        bool Current() => hostCurrent() && !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible
            && revision == _mediaMenuRevision && serial == _mediaMenuSerial
            && ReferenceEquals(rows, _grid.ItemsSource);

        Dispatcher.UIThread.Post(() =>
        {
            if (!Current()) return;
            if (origin is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }
                && origin.IsAttachedToVisualTree() && ReferenceEquals(originData, origin.DataContext)
                && origin.Focus()) return;
            FocusGrid(Current);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>I7: the star of a tile corner runs the shared "favorites.toggle" media action for the shown occurrence.</summary>
    private Task ToggleTileFavoriteAsync(MediaLibraryEntry entry)
    {
        if (ExecuteMediaMenuActionAsync is not { } execute || _lifetime.IsCancellationRequested) return Task.CompletedTask;
        object? rows = _grid.ItemsSource;
        return execute("favorites.toggle", entry, () => !_lifetime.IsCancellationRequested && ReferenceEquals(rows, _grid.ItemsSource));
    }

    internal async Task EditSelectedTagsAsync()
    {
        if (SelectedTiles.Count == 1 && SelectedRow is { State.CanEditMediaTags: true } row && EditTags != null) await EditTags(row.Entry);
    }

    /// <summary>The post address of a provider medium as the catalog states it, validated; null for any other entry.</summary>
    internal Uri? ProviderPostUrl(MediaLibraryEntry entry)
    {
        if (entry.BooruPostId is not { } postId) return null;
        BooruPostRecord? record = null;
        try { record = ReadBooruCatalog?.Invoke()?.Posts.FirstOrDefault(post => post.SourceId == BooruMediaIdentity.Source && post.PostId == postId); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { }
        return BooruMediaIdentity.PostUrl(postId, record?.Post.PostUrl);
    }

    internal async Task OpenProviderPostAsync(MediaLibraryEntry entry)
    {
        if (ProviderPostUrl(entry) is not { } url) return;
        try
        {
            await (OpenProviderPage?.Invoke(url) ?? ExternalLauncher.OpenAsync(url.AbsoluteUri));
            ShowSelectionNote(null);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ShowSelectionNote(Texts.T("booru.media.openFailed")); }
    }

    internal async Task CopyProviderPostAsync(MediaLibraryEntry entry)
    {
        if (ProviderPostUrl(entry) is not { } url) return;
        try
        {
            if (CopyProviderPage != null) await CopyProviderPage(url);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(url.AbsoluteUri);
            else throw new InvalidOperationException("The clipboard is unavailable.");
            ShowSelectionNote(Texts.T("booru.media.copied"));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ShowSelectionNote(Texts.T("booru.media.copyFailed")); }
    }

    internal async Task ShowSelectedInThreadAsync()
    {
        if (SelectedRow is { Entry.PostId: not null } row && ShowInThread != null) await ShowInThread(row.Entry);
    }

    private async void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (OnSelectionKey(e)) return;
        try
        {
            // The gallery's own "show in thread" command (T by default): one tile with a post; otherwise it does nothing.
            if (CommandBindings.Matches(e, "gallery.show-in-thread"))
            {
                e.Handled = true;
                if (SelectedTiles.Count == 1) await ShowSelectedInThreadAsync();
                return;
            }
            // media.tags.edit: several selected tiles tag together (the selection bar's action), one edits its own tags.
            if (CommandBindings.Matches(e, "media.tags.edit"))
            {
                e.Handled = true;
                await EditTagsByShortcutAsync();
                return;
            }
            // Rating, details, export and folder of the medium (menu commands): the same actions and gates as the tile menu.
            foreach (string id in MediaShortcutCommands)
                if (CommandBindings.Matches(e, id))
                {
                    e.Handled = true;
                    await RunMediaCommandByShortcutAsync(id);
                    return;
                }
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || SelectedRow == null) return;
            e.Handled = true;
            // Several selected tiles open together; one opens around itself.
            if (SelectedTiles.Count > 1) await OpenSelectionGalleryAsync(); else await OpenSelectedAsync();
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.grid.key")) { /* Logged and shown. */ }
    }

    /// <summary>The Tags shortcut: the same two paths as the menu (one tile) and the selection bar (several), with the same gates.</summary>
    internal async Task EditTagsByShortcutAsync()
    {
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy) return;
        if (SelectedTiles.Count > 1) { await TagSelectionAsync(); return; }
        if (SelectedRow is not { } row || row.State?.CanEditMediaTags != true) return;
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible;
        MediaLibraryEntry entry = row.Entry;
        if (EditTagsForMenuAsync != null) await EditTagsForMenuAsync(entry, Current);
        else await ExecuteMediaActionAsync("media.tags.edit", entry, Current);
    }

    private static readonly string[] MediaShortcutCommands = { "media.rating.edit", "media.details", "media.export-original", "media.show-in-folder" };

    /// <summary>The shortcut for a menu command of one medium. Export with several selected tiles exports the selection;
    /// the others need exactly one saved tile, like the menu entries they stand for.</summary>
    internal async Task RunMediaCommandByShortcutAsync(string id)
    {
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy || ExecuteMediaMenuActionAsync == null) return;
        if (id == "media.export-original" && SelectedTiles.Count > 1) { await ExportSelectionAsync(); return; }
        if (SelectedTiles.Count != 1 || SelectedRow is not { } row || row.Entry.Presence != MediaLibraryPresence.Present) return;
        if (id == "media.rating.edit" && row.State?.RatingKnown != true) return;
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible;
        await ExecuteMediaActionAsync(id, row.Entry, Current);
    }

    internal void InitializeSharedMediaMenus()
    {
        if (_sharedMediaMenusInitialized) return;
        _sharedMediaMenusInitialized = true;
        _grid.AddHandler(PointerPressedEvent, OnSharedPointerPressed, RoutingStrategies.Tunnel);
        _grid.AddHandler(ContextRequestedEvent, OnSharedContextRequested, RoutingStrategies.Bubble);
        _grid.AddHandler(KeyDownEvent, OnSharedContextKeyDown, RoutingStrategies.Tunnel);
        _grid.PropertyChanged += (_, e) =>
        {
            if (e.Property == ItemsControl.ItemsSourceProperty) InvalidateSharedMediaMenu();
        };
        DetachedFromVisualTree += (_, _) => InvalidateSharedMediaMenu();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) InvalidateSharedMediaMenu();
        };
    }

    internal void InvalidateSharedMediaMenu()
    {
        ++_mediaMenuRevision;
        ++_mediaMenuSerial;
        _sharedPointerContextPending = false;
        _mediaMenuPresenter.Invalidate();
    }

    private static bool IsNativeContextSource(object? source) => MediaMenuPresenter.IsNativeTextSource(source);

    private static MediaLibraryTileRow? RowAt(object? source) => source is Visual visual
        ? visual.GetSelfAndVisualAncestors().OfType<Control>().Select(control => control.DataContext)
            .OfType<MediaLibraryTileRow>().FirstOrDefault()
        : null;

    private static Control? RowOrigin(object? source, MediaLibraryTileRow row)
    {
        if (source is not Visual visual) return null;
        Control[] controls = visual.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        return controls.FirstOrDefault(control => control is ListBoxItem && ReferenceEquals(control.DataContext, row))
            ?? controls.FirstOrDefault(control => control is MediaLibraryTileView && ReferenceEquals(control.DataContext, row))
            ?? controls.FirstOrDefault(control => ReferenceEquals(control.DataContext, row));
    }

    private static bool IsContextKey(KeyEventArgs e) => MediaMenuPresenter.IsContextKey(e);

    private Control ResolveMenuOrigin(object? source, MediaLibraryTileRow? row)
    {
        if (row != null && RowAt(source) is not null)
            return RowOrigin(source, row) ?? _grid;
        // A request raised by the ListBox itself has no pointer position by
        // definition. Its exact owner is still the ListBox, not the selected
        // row's recycled container.
        return source as ListBox ?? _grid;
    }

    private void OnSharedPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _lifetime.IsCancellationRequested || !IsEffectivelyVisible
            || IsNativeContextSource(e.Source)
            || !e.GetCurrentPoint(_grid).Properties.IsRightButtonPressed) return;
        MediaLibraryTileRow? row = RowAt(e.Source);
        OpenSharedMediaMenu(row ?? SelectedRow, ResolveMenuOrigin(e.Source, row ?? SelectedRow));
        _sharedPointerContextPending = true;
        e.Handled = true;
        // Avalonia may follow a handled right-button press with a positionless
        // ContextRequested event. Consume only that paired event; a synthetic
        // keyboard request after this turn must still be routed normally.
        Dispatcher.UIThread.Post(() => _sharedPointerContextPending = false, DispatcherPriority.Background);
    }

    private void OnSharedContextKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsContextKey(e)) return;
        MediaLibraryTileRow? row = RowAt(e.Source) ?? SelectedRow;
        OpenSharedMediaMenu(row, ResolveMenuOrigin(e.Source, row));
        e.Handled = true;
    }

    private void OnSharedContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || IsNativeContextSource(e.Source)) return;
        if (_sharedPointerContextPending)
        {
            _sharedPointerContextPending = false;
            e.Handled = true;
            return;
        }
        MediaLibraryTileRow? row = RowAt(e.Source) ?? SelectedRow;
        OpenSharedMediaMenu(row, ResolveMenuOrigin(e.Source, row));
        e.Handled = true;
    }

    private void OpenSharedMediaMenu(MediaLibraryTileRow? row, Control origin)
    {
        if (_lifetime.IsCancellationRequested || !IsEffectivelyVisible) return;
        long revision = _mediaMenuRevision;
        long serial = ++_mediaMenuSerial;
        object? rows = _grid.ItemsSource;
        object? originData = origin.DataContext;
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible
            && revision == _mediaMenuRevision && serial == _mediaMenuSerial
            && ReferenceEquals(rows, _grid.ItemsSource)
            && origin.IsAttachedToVisualTree() && origin.IsEffectivelyVisible
            && ReferenceEquals(originData, origin.DataContext);

        if (row == null)
        {
            MediaMenuSession blank = new(MediaMenuSurface.MediaPage, null, Current);
            Task RunBlank(Func<Task> action) => RunMenuActionAsync(action, blank);
            ContextMenu blankMenu = new() { ItemsSource = new Control[]
            {
                MediaMenuBuilder.ContextItem(blank, "media.exportAll", ExportAllAsync, () => _exportAll.IsEnabled, RunBlank)
            } };
            blank.Invalidated += () => blankMenu.Close();
            _mediaMenuPresenter.Show(blankMenu, blank, origin, attachToOrigin: true, fallbackFocus: () => FocusGrid(Current));
            return;
        }
        MediaLibraryEntry entry = row.Entry;
        MediaLibraryTile tile = row.Tile;
        MediaLibraryEntry[] shown = _shown.ToArray();
        (IReadOnlyList<MediaLibraryEntry> galleryWindow, int galleryStart) = MediaLibraryView.GalleryWindow(shown, row.Index);
        MediaLibraryEntryState? state = row.State;
        int selectedCount = SelectedTiles.Count;
        bool several = selectedCount > 1;
        bool hostActions = ExecuteMediaMenuActionAsync != null;
        MediaMenuFacts facts = new(MediaMenuSurface.MediaPage, MediaMenuTarget.Of(entry))
        {
            Present = entry.Presence == MediaLibraryPresence.Present,
            GalleryWired = galleryStart >= 0 && (OpenGallery != null || hostActions),
            SeveralSelected = several,
            Favorite = state?.Favorite switch
            {
                MediaLibraryFlag.Yes => MediaMenuFavorite.On,
                MediaLibraryFlag.No => MediaMenuFavorite.Off,
                _ => MediaMenuFavorite.Unknown
            },
            FavoriteWired = hostActions,
            RatingWired = hostActions,
            RatingKnown = state?.RatingKnown == true,
            TagsWired = EditTagsForMenuAsync != null || hostActions,
            TagsBlocked = state?.CanEditMediaTags == true ? null : state == null || !state.TagsKnown ? MediaMenuReasons.TagsUnknown
                : MediaMenuReasons.Tags(entry.AttachmentRevisionKey == null && entry.LocalSlot == null
                    ? GalleryMediaTagUnavailable.NoPostVersion : GalleryMediaTagUnavailable.InvalidIdentity,
                    providerMedia: entry.BooruPostId != null),
            ExportWired = hostActions,
            FolderWired = hostActions,
            CopyPathWired = hostActions,
            DetailsWired = hostActions,
            Posts = entry.PostId != null ? MediaMenuPostScope.Single : MediaMenuPostScope.None,
            ShowInThreadWired = ShowInThread != null,
            ProviderMedia = entry.BooruPostId != null,
            ProviderPostKnown = ProviderPostUrl(entry) != null
        };
        MediaMenuModel model = MediaMenuComposition.Compose(facts);
        MediaMenuSession session = new(MediaMenuSurface.MediaPage, model.Target, Current);
        Task Run(Func<Task> action) => RunMenuActionAsync(action, session);
        Dictionary<MediaMenuAction, Func<Task>> handlers = new()
        {
            [MediaMenuAction.Open] = () => OpenMediaMenuAsync(entry, galleryWindow, galleryStart, Current),
            [MediaMenuAction.Favorite] = () => ExecuteMediaActionAsync("favorites.toggle", entry, Current),
            [MediaMenuAction.Rating] = () => ExecuteMediaActionAsync("media.rating.edit", entry, Current),
            [MediaMenuAction.Tags] = () => EditTagsForMenuAsync != null
                ? EditTagsForMenuAsync(entry, Current) : ExecuteMediaActionAsync("media.tags.edit", entry, Current),
            [MediaMenuAction.Export] = () => ExecuteMediaActionAsync("media.export-original", entry, Current),
            [MediaMenuAction.ShowInFolder] = () => ExecuteMediaActionAsync("media.show-in-folder", entry, Current),
            [MediaMenuAction.CopyPath] = () => ExecuteMediaActionAsync("media.copy-path", entry, Current),
            [MediaMenuAction.Details] = () => ExecuteMediaActionAsync("media.details", entry, Current),
            [MediaMenuAction.ShowInThread] = () => ShowInThread == null || entry.PostId == null ? Task.CompletedTask : ShowInThread(entry),
            [MediaMenuAction.OpenProviderPost] = () => OpenProviderPostAsync(entry),
            [MediaMenuAction.CopyProviderPost] = () => CopyProviderPostAsync(entry)
        };
        // Everything below the common section belongs to this page: it works on the grid, not on one medium.
        List<Control> context = new();
        if (selectedCount > 0 && !(selectedCount == 1 && ReferenceEquals(SelectedRow, row)))
            context.Add(MediaMenuBuilder.Note(Texts.T("media.menu.note.selection", selectedCount)));
        context.Add(MediaMenuBuilder.ContextItem(session, "media.filter.onlyThread", () => { FilterToThread(entry); return Task.CompletedTask; },
            () => !several, Run, MediaMenuReasons.SeveralSelected));
        context.Add(MediaMenuBuilder.ContextItem(session, "media.occurrences", () => { ShowOccurrences(tile); return Task.CompletedTask; },
            () => tile.IsGroup, Run, MediaMenuReasons.Unavailable));
        foreach ((Button action, string key) in new[] { (_selTag, "media.selection.tag"),
            (_selGallery, "media.selection.gallery"), (_selExport, "media.selection.export") })
        {
            Button selectedAction = action;
            context.Add(MediaMenuBuilder.ContextItem(session, key, () =>
            {
                selectedAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return Task.CompletedTask;
            }, () => selectedAction.IsEnabled, Run, ToolTip.GetTip(selectedAction) as string ?? MediaMenuReasons.Unavailable));
        }
        context.Add(MediaMenuBuilder.ContextItem(session, "media.exportAll", ExportAllAsync, () => _exportAll.IsEnabled, Run));
        ContextMenu menu = MediaMenuBuilder.Build(session, model, handlers, Run, context, ShowSelectionNote);
        _mediaMenuPresenter.Show(menu, session, origin, attachToOrigin: true, fallbackFocus: () => FocusGrid(Current));
    }

    private readonly MediaMenuPresenter _mediaMenuPresenter = new();

    private Task OpenMediaMenuAsync(MediaLibraryEntry entry, IReadOnlyList<MediaLibraryEntry> galleryWindow,
        int galleryStart, Func<bool> current) => ExecuteMediaMenuActionAsync != null
            ? ExecuteMediaMenuActionAsync("media.open", entry, current)
            : OpenGallery?.Invoke(galleryWindow, galleryStart) ?? Task.CompletedTask;

    private async Task ExecuteMediaActionAsync(string id, MediaLibraryEntry entry, Func<bool> current)
    {
        if (ExecuteMediaMenuActionAsync != null) await ExecuteMediaMenuActionAsync(id, entry, current);
        else if (id == "media.tags.edit" && EditTags != null) await EditTags(entry);
    }

    private async Task RunMenuActionAsync(Func<Task> action, MediaMenuSession session)
    {
        if (!session.IsCurrent) return;
        try { await action(); }
        catch (OperationCanceledException) when (!session.IsCurrent || _lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { if (session.IsCurrent) ShowSelectionNote(error.Message); }
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
        CommandBindings.ApplyLabeled(_goDownloads, "navigation.downloads", Texts.T("nav.goDownloads"));
        CommandBindings.ApplyLabeled(_goArchive, "navigation.archive", Texts.T("nav.goArchive"));
        AutomationProperties.SetName(_grid, Texts.T("media.title"));
        LocalizeFilters();
        LocalizeFilterSurface();
        LocalizeSelection();
        if (_grid.ItemsSource != null) Present(); else UpdateStatus();
    }

    internal void EndLifetime()
    {
        if (_lifetime.IsCancellationRequested) return;
        InvalidateSharedMediaMenu();
        _lifetime.Cancel();
        _nameFilter.CancelPendingQuery();
        _building?.Cancel();
        Texts.Changed -= OnLanguageChanged;
        _occurrences?.Close();
        _selectionTag?.CloseForLifetime();
    }
}

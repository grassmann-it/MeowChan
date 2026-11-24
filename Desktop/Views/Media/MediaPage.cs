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
using MeowChan.Desktop.Controls;
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
    private readonly TextBlock _title = new() { Classes = { "page-title" } };
    private readonly TextBlock _subtitle = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } };
    // The criterion with its "Sort" prefix inside the box (MediaPage.Sorting.cs); its minimum width follows the toolbar stage (MediaPage.Toolbar.cs).
    private readonly PrefixedComboBox _sort = new() { Name = "MediaSort", MinWidth = MediaToolbarLayout.SortMinWidth };
    private readonly Button _shuffle = new() { Name = "MediaShuffle", IsVisible = false };
    private readonly CheckBox _missing = new() { Name = "MediaShowMissing" };
    private readonly Button _refresh = new() { Name = "MediaRefresh" };
    // The saved ratings live with the media: the dialog that lists and removes them opens from here (it used to sit on Diagnostics).
    private readonly Button _manageRatings = new() { Name = "MediaManageRatings" };
    private readonly TextBlock _status = new() { Name = "MediaStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly ListBox _grid = new() { Name = "MediaGrid", SelectionMode = SelectionMode.Multiple, Classes = { "workspace-selection-bar" } };
    private readonly TextBlock _empty = new() { Name = "MediaEmpty", IsVisible = false };
    // An empty library points back to where media comes from; a filter that matches nothing offers its reset (MediaPage.FilterChips.cs).
    private readonly Button _goDownloads = new() { Name = "MediaGoDownloads" };
    // QW-A #1: the empty library also offers the two ways to bring a collection in: copy files (the import dialog) or link a folder (read-only).
    private readonly Button _importFiles = new() { Name = "MediaImportFiles", Classes = { "primary" } };
    private readonly Button _linkFolder = new() { Name = "MediaLinkFolder" };
    private readonly TextBlock _emptyHelp = new() { Name = "MediaEmptyHelp", TextWrapping = Avalonia.Media.TextWrapping.Wrap, TextAlignment = Avalonia.Media.TextAlignment.Center, MaxWidth = 520 };
    private readonly WrapPanel _emptyActions = new() { Name = "MediaEmptyActions", ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    // The shared empty state (centered, one sentence, one primary action); its sentence and actions are added in the constructor.
    private readonly WorkspaceEmptyState _emptyState = new() { Name = "MediaEmptyState", IsVisible = false };
    // Everything under the title: it is hidden while the library is empty, so the empty page shows the sentence and its action only.
    private readonly StackPanel _headerBody = new() { Spacing = 8 };
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
    private readonly PressOpenedContextGate _sharedPointerContext = new();
    private MediaLibraryUserOrder? _userOrder;

    /// <summary>Opens the gallery for a window of shown entries; the int is the chosen entry inside it.</summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>, int, Task>? OpenGallery { get; set; }
    internal Func<IReadOnlyList<MediaLibraryEntry>, int, Task>? OpenSelectionGallery { get; set; }
    /// <summary>Set by the shell: the footer destinations offered by the empty state.</summary>
    internal Action? GoToDownloadsRequested { get; set; }
    /// <summary>Set by the shell: "Import files…" of the empty state (the same dialog as the header's More and the shortcut). Null hides the button.</summary>
    internal Action? ImportFilesRequested { get; set; }
    /// <summary>Set by the shell: "Link folder…" of the empty state (the folder flow of Settings, Storage and limits, Media folders). Null hides the button.</summary>
    internal Action? LinkFolderRequested { get; set; }
    /// <summary>Set by the shell: "Media folders…" under More (the settings group). Null hides the entry.</summary>
    internal Action? OpenMediaFoldersRequested { get; set; }
    internal Func<MediaLibraryEntry, Task>? ShowInThread { get; set; }
    /// <summary>Opens the Rules page on the thread of an archived medium (the shell supplies it; catalog media have no thread).</summary>
    internal Action<MediaLibraryEntry>? EditThreadRules { get; set; }
    internal static bool HasThreadRules(MediaLibraryEntry entry) => !entry.IsCatalogMedium && entry.Thread.Thread != null;
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
    /// <summary>Read-only access to the folder source catalog (files of read-only folders). Null: the page shows no folder files.</summary>
    internal Func<FolderSources.FolderSourceCatalogSnapshot?>? ReadFolderSources { get; set; }
    /// <summary>Opens a provider post page; the shell supplies the browser launcher, tests a recorder.</summary>
    internal Func<Uri, Task>? OpenProviderPage { get; set; }
    /// <summary>Copies a provider post address to the clipboard; the default uses this page's clipboard.</summary>
    internal Func<Uri, Task>? CopyProviderPage { get; set; }
    internal Func<MediaLibraryEntry, Func<bool>, Task>? EditTagsForMenuAsync { get; set; }
    /// <summary>
    /// Opens the quick tag popover (the shell resolves the exact file and owns the tag session) on the given anchor for one entry:
    /// the entry, the tile it opened on, the context of the suggestions and the page's guard. Completes when the popover closed.
    /// Null falls back to the tag dialog of <see cref="EditTagsForMenuAsync"/>.
    /// </summary>
    internal Func<MediaLibraryEntry, Control, TagContext, Func<bool>, RateAndTagRequest, Task>? TagPopoverAsync { get; set; }
    /// <summary>The favourite and rating row of the selection's "Rate and tag" popover over these entries (the shell supplies the decision session); null: no row.</summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>, QuickTagInitialFocus, QuickTagPopoverDecision?>? DecisionForEntries { get; set; }
    /// <summary>Limits and suggestions of the selection's tag popover (the shell binds the saved settings); unset uses the defaults.</summary>
    internal Func<TagContext, QuickTagPopoverOptions>? TagPopoverOptions { get; set; }
    /// <summary>Opens the manager of saved ratings (the shell's dialog); the page re-reads ratings when the dialog has closed.</summary>
    internal Func<Task>? ManageMediaRatingsAsync
    {
        get => _manageMediaRatings;
        set { _manageMediaRatings = value; _manageRatings.IsEnabled = !_lifetime.IsCancellationRequested && value != null; }
    }
    private Func<Task>? _manageMediaRatings;
    internal bool CanManageRatings => !_lifetime.IsCancellationRequested && _manageRatings.IsEnabled;
    internal async Task ManageRatingsAsync()
    {
        if (_lifetime.IsCancellationRequested || _manageMediaRatings == null) return;
        try { await _manageMediaRatings(); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.ratings.manage")) { /* Logged and shown. */ }
        if (!_lifetime.IsCancellationRequested) RefreshUserData();
    }
    internal ContextMenu? MediaMenu => _mediaMenuPresenter.Menu;
    internal MediaMenuSession? MediaMenuSession => _mediaMenuPresenter.Session;
    internal Task BuildTask { get; private set; } = Task.CompletedTask;
    /// <summary>At most this often (5 times a second) does a running build update the status line (the build reports once per thread).</summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);
    /// <summary>Write limit of the saved index in bytes; the shell reads the setting each time. Null: the default limit.</summary>
    internal Func<long>? CacheLimitBytes { get; set; }
    /// <summary>The configured limits of entries and threads the saved index may hold (settings).</summary>
    internal Func<(int Entries, int Threads)>? CacheCountLimits { get; set; }
    /// <summary>The configured write limits of the saved sound check of videos (settings); the shell reads them each time. Null: the default limits.</summary>
    internal Func<MediaAudioCacheLimits>? AudioCacheLimits { get; set; }
    /// <summary>Reads the saved index; tests replace it to hold the load open.</summary>
    internal Func<MediaLibraryIndexCacheFile, CancellationToken, Task<MediaLibrarySnapshot?>> LoadCache { get; set; } =
        (file, token) => file.LoadAsync(token);
    internal MediaLibraryIndexCacheFile? CacheFile => _cacheFile;
    /// <summary>
    /// A manifest cache of the same kind as the page's own (real reader with file identities, or the page's custom opener) that
    /// belongs to the caller. A caller that builds an index on the side (the import check) must not use or invalidate the page's
    /// views: its reuse comes from the identities recorded in <see cref="Snapshot"/>.
    /// </summary>
    internal MediaManifestCache CreateManifestCache() => new(_manifests.UsesIdentity ? null : _openManifest);
    internal bool LoadingCache => _loadingCache;
    /// <summary>One entry per tile: the occurrence each tile shows. Gallery windows are made from this list.</summary>
    internal IReadOnlyList<MediaLibraryEntry> ShownEntries => _shown;
    internal IReadOnlyList<MediaLibraryTile> Tiles => _tiles;
    /// <summary>Positions of the saved tiles in <see cref="ShownEntries"/>, found once per presentation for the gallery window of a menu.</summary>
    internal int[] PresentPositions { get; private set; } = Array.Empty<int>();
    internal MediaLibrarySnapshot? Snapshot => _snapshot;
    internal ListBox Grid => _grid;

    internal MediaPage(Func<CancellationToken, Task<ArchiveReader>> openReader,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>>? openManifest = null, string? cachePath = null)
    {
        _openReader = openReader ?? throw new ArgumentNullException(nameof(openReader));
        // Every key of the page runs in the "media" context, as the gallery's keys do in theirs: a default that a saved binding took away only elsewhere
        // (a partial yield) stays active here (EffectiveShortcutBindings.EnterKeyContext).
        _pageKeyContext = KeyContextHost.Install(this, _ => "media");
        _openManifest = openManifest ?? MediaAssetReader.OpenAsync;
        // Only the real manifest reader is checked by file identity; a supplied opener keeps the per-build behavior.
        _manifests = new MediaManifestCache(openManifest, _lifetime.Token);
        _cacheFile = cachePath == null ? null : new MediaLibraryIndexCacheFile(cachePath);
        Name = "MediaPage";
        InitializeLayoutButton(); // MP5: the "Layout" button of the toolbar (MediaPage.Layout.cs)
        InitializePanelToggle(); // S-4: keep the media sidebar switch beside the layout action in the right toolbar cluster
        Grid tools = BuildToolbar(); // search and filters left, the order right (MediaPage.Toolbar.cs)
        // The page grid (M1): the head follows the other pages (workspace-page-header owns the 4 dip between title and the first line,
        // margin 12/8/12/8); the lines under it (search row, chips, status, filter section) are 8 apart.
        // LY-03: the title starts where every page title starts (PageHeader.PageTopOffset); the page has no margin of its own.
        StackPanel header = new() { Margin = new Thickness(12, PageHeader.PageTopOffset, 12, 8), Classes = { "workspace-page-header" } };
        header.Children.Add(_title); header.Children.Add(_headerBody);
        _headerBody.Children.Add(_subtitle); _headerBody.Children.Add(tools); _headerBody.Children.Add(_searchCarryOffer);
        // The filter fields open as an inline section under the status line that pushes the grid down (S1, MediaPage.FilterSurface.cs);
        // what is applied stays in the header above it.
        Border filterSurface = BuildFilterSurface(BuildFilterRow());
        _headerBody.Children.Add(BuildFilterChipRow()); _headerBody.Children.Add(_filterError); _headerBody.Children.Add(_filterRetry); _headerBody.Children.Add(BuildQueryHint()); _headerBody.Children.Add(BuildStatusRow(_status));
        _headerBody.Children.Add(filterSurface);
        _headerBody.Children.Add(BuildExportAllAsk()); _headerBody.Children.Add(BuildSelectionBar());
        _grid.ItemsPanel = new FuncTemplate<Panel?>(() => new MediaTilePanel { ItemHeight = _tileAppearance.CellHeight, ColumnWidth = _tileAppearance.CellWidth });
        _grid.ItemTemplate = new FuncDataTemplate<MediaLibraryTileRow>((_, _) => new MediaLibraryTileView { ThumbnailLoader = LoadThumbnailAsync, ThumbnailCached = MediaTileThumbnails.TryGetCached,
            ThumbnailRelease = MediaTileThumbnails.Release, ThumbnailHandover = TakeHandedOverThumbnail, ThreadFilter = FilterToThread,
            TagFilter = FilterToTag, TagExclude = ExcludeTag, ShowOccurrences = ShowOccurrences, FavoriteToggle = ToggleTileFavoriteAsync,
            CanToggleFavorite = () => ExecuteMediaMenuActionAsync != null, InTagQueue = IsInTagQueue, Appearance = _tileAppearance,
            EditTags = EditTagsOfTile, OpenTagging = () => OpenTaggingPage?.Invoke(), CanOpenTagging = () => OpenTaggingPage != null });
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Panel body = new();
        _emptyActions.Children.Add(_importFiles); _emptyActions.Children.Add(_linkFolder); _emptyActions.Children.Add(_goDownloads);
        _empty.Classes.Add(WorkspaceEmptyState.TextClass);
        _emptyHelp.Classes.Add("muted");
        _emptyState.Children.Add(_empty); _emptyState.Children.Add(_emptyHelp); _emptyState.Children.Add(_emptyActions);
        body.Children.Add(_grid); body.Children.Add(_emptyState);
        InstallFocus(body); // M4: the rows the focus mode hides are grouped, its row takes their place (MediaPage.Focus.cs)
        Border surface = new() { Name = "MediaGridSurface", Child = body, Margin = new Thickness(12, 0, 12, 8), Classes = { "workspace-section-flush" } };
        Control bodyRow = WrapWithTagSidebar(surface); // MP4: the tag list of the Booru mode left of the grid (MediaPage.BooruMode.cs)
        // The header belongs to the grid/filter column, not above the media sidebar.
        // Both sidebar and divider span the page host independently of that column's chrome.
        _contentGrid!.Children.Add(header);
        Content = bodyRow;
        InstallLayout(); // MP5: finds the rows the layout shows or hides

        _sort.SelectionChanged += (_, _) => OnSortChanged();
        _sortDirection.Click += (_, _) => { if (!_localizing) ToggleSortDirection(); };
        _shuffle.Click += (_, _) => { _seed = unchecked(_seed * 31 + 17); Present(); };
        // Showing missing files is a filter term since M2 (MediaPage.Filters.cs WireFlag): chips, work view and the grid follow.
        _refresh.Click += (_, _) => BuildTask = BuildAsync(force: true);
        _manageRatings.Click += async (_, _) => await ManageRatingsAsync();
        _goDownloads.Click += (_, _) => GoToDownloadsRequested?.Invoke();
        _importFiles.Click += (_, _) => { if (!_lifetime.IsCancellationRequested) ImportFilesRequested?.Invoke(); };
        _linkFolder.Click += (_, _) => { if (!_lifetime.IsCancellationRequested) LinkFolderRequested?.Invoke(); };
        InstallTileEnter(); // Enter on a focused tile (MediaPage.TileEnter.cs); registered first, so it runs after the other tunnel handlers
        InstallTileInteraction(); // double click on a tile, Ctrl+wheel and Ctrl+Plus/Minus for the tile size (MediaPage.Interaction.cs)
        _grid.KeyDown += OnGridKeyDown;
        _grid.AddHandler(KeyDownEvent, OnGridPageShortcut, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPageRateAndTagKey, RoutingStrategies.Tunnel);
        _grid.AddHandler(KeyDownEvent, OnSelectAllWhilePresenting, RoutingStrategies.Tunnel);
        _grid.SelectionChanged += (_, e) =>
        {
            if (_restoring) return;
            // A new selection owns the page even without a routed pointer/key event (e.g. accessibility).
            // Do not let an earlier gallery return overwrite it while the next gallery is resolving.
            EndPendingReturn();
            _selectionStamp++;
            ShowSelectionNote(null);
            UpdateSelectionBar();
            OnPanelSelectionChanged(e);
        };
        InstallReturnInterruption();
        RegisterWorkViewIds();
        Texts.Changed += OnLanguageChanged;
        Localize();
    }

    internal bool IsAvailable
    {
        set { _available = value; UpdateStatus(); }
    }

    internal MediaLibrarySort Sort => SelectedSort;

    /// <summary>
    /// Shows the saved index as soon as it is read (in the background; the page stays responsive and says that it is
    /// loading), then brings it up to date in the background.
    /// </summary>
    internal Task ActivateAsync()
    {
        RefreshUserData();
        UpdateSearchTip();
        WarmBooruCatalog();
        RefreshTagQueue(load: true);
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
                    Present(keepView: true);
                }
                else UpdateStatus();
            }
            MediaLibrarySnapshot? previous = force ? null : _snapshot;
            Func<BooruCatalogSnapshot?>? readBooru = ReadBooruCatalog;
            Func<Import.ManualImportCatalogSnapshot?>? readImports = ReadImportCatalog;
            Func<FolderSources.FolderSourceCatalogSnapshot?>? readFolders = ReadFolderSources;
            // A refresh reads every manifest again (no reuse of kept views); identities are still recorded for the next open.
            MediaManifestCache manifests = force ? new MediaManifestCache(_manifests.UsesIdentity ? null : _openManifest, building.Token) : _manifests;
            MediaLibrarySnapshot built = await Task.Run(() => MediaLibraryIndexBuilder.BuildAsync(_openReader, manifests,
                previous, progress, building.Token, readBooru, readImports, readFolders), building.Token);
            if (!ReferenceEquals(_building, building)) return;
            SetSnapshot(built);
            _progress = null;
            Present(keepView: true); // the rebuilt index replaces the rows under the user's eyes: the view stays where it is
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

    /// <summary>
    /// From this many entries on, filtering, ordering and grouping run off the UI thread; only the swap of the rows happens on it.
    /// Smaller libraries present at once, so a click shows its result in the same turn.
    /// </summary>
    internal int BackgroundPresentEntries { get; set; } = 2000;
    /// <summary>Completes when the presentation that is running now (if any) has been applied or was replaced by a newer one.</summary>
    internal Task PresentTask { get; private set; } = Task.CompletedTask;
    /// <summary>
    /// Test seam: when set, every background presentation awaits it (with its number, see <see cref="PresentRunCount"/>) after its rows
    /// are computed and before it asks to be applied, so a test can hold a run open (a late, already cancelled run must not overwrite
    /// a newer one). Runs off the UI thread.
    /// </summary>
    internal Func<int, CancellationToken, Task>? PresentGate { get; set; }
    /// <summary>How many background presentations have been started; the n-th one passes n to <see cref="PresentGate"/>.</summary>
    internal int PresentRunCount { get; private set; }
    private CancellationTokenSource? _presenting;
    private bool _presentPending;
    private bool _presentPendingKeepsView;
    // Counts the user's own selection changes: a presentation computed in the background keeps the selection it was started with
    // only when nothing was selected or unselected meanwhile.
    private long _selectionStamp;
    // The selection stamp right after a Ctrl+A that came while a presentation was running (-1: none).
    private long _selectAllStamp = -1;
    private PresentIndexHolder? _presentIndex;

    private sealed class PresentIndexHolder(MediaLibrarySnapshot snapshot)
    {
        internal MediaLibrarySnapshot Snapshot { get; } = snapshot;
        private readonly Lazy<MediaLibraryPresentIndex> _index = new(() => new MediaLibraryPresentIndex(snapshot.Entries), LazyThreadSafetyMode.ExecutionAndPublication);
        internal MediaLibraryPresentIndex Index => _index.Value;
    }

    // Everything a presentation reads from the page and its controls, taken on the UI thread; the computation touches nothing else.
    private sealed record PresentInputs(IReadOnlyList<MediaLibraryEntry> All, PresentIndexHolder? Holder, MediaLibraryFilter? Filter,
        bool InvalidFilter, MediaLibrarySort Sort, MediaLibrarySortDirection Direction, bool IncludeMissing, int Seed, MediaLibraryUserData UserData,
        MediaLibraryUserOrder? UserOrder, MediaAudioMap Audio,
        MediaLibraryUntagged? Untagged, IReadOnlyList<MediaLibraryTile> OldTiles, int[] SelectedPositions, long SelectionStamp)
    {
        // MP1/MP4: the page-side terms (tagging queue, source tags), the queue keys taken now and the reader of the source tags (it reads
        // the Booru catalog, so a presentation with source tags always runs off the UI thread).
        internal MediaPageTermFilter? PageTerms { get; init; }
        internal IReadOnlySet<TagQueueKey>? Queue { get; init; }
        internal Func<MediaSourceTagIndex?>? SourceTags { get; init; }
        /// <summary>The search text this presentation was made for; the row where it was found names it.</summary>
        internal string? SearchText { get; init; }
        /// <summary>
        /// The data asked for this presentation (the index, own state, the tagging queue, sound results, the language), not the user's query: it
        /// keeps the view where it is (MediaPage.ViewKeep.cs).
        /// </summary>
        internal bool KeepView { get; init; }
    }

    private sealed record PresentResult(IReadOnlyList<MediaLibraryTile> Tiles, MediaLibraryEntry[] Shown, int[] PresentPositions,
        MediaLibraryTileRow[] Rows, int ShownMissing, int Unfiltered, bool FilterActive, string? ErrorKey, int AudioUnknown,
        int? UntaggedTiles, MediaLibraryUntagged? Untagged, IReadOnlyList<int> Restored, MediaLibraryUserOrder? UserOrder, MediaTextCounts? Breakdown = null);

    /// <param name="keepView">
    /// True when the data changed and not the query (a rebuilt index, own state, the queue, sound results, the language): the grid keeps its scroll
    /// anchor, the selected tile's place on screen and the previews it shows (MediaPage.ViewKeep.cs). A changed filter, order or search starts fresh.
    /// </param>
    private void Present(bool keepView = false)
    {
        _presenting?.Cancel();
        _presenting = null;
        // A query presentation that is still being computed is not turned into a data one by a refresh that replaces it.
        keepView = keepView && !(_presentPending && !_presentPendingKeepsView);
        PresentInputs inputs = CapturePresentInputs() with { KeepView = keepView };
        RefreshThreadMedia(inputs.Filter);
        // Source tags read the Booru catalog: its first read (tens of MiB) never happens on the UI thread (see WarmBooruCatalog).
        if (inputs.All.Count < BackgroundPresentEntries && !(inputs.PageTerms?.NeedsSourceTags == true && ReadBooruCatalog != null && !_booruCatalogWarm))
        {
            _presentPending = false;
            PresentTask = Task.CompletedTask;
            ApplyPresent(inputs, ComputePresent(inputs, CancellationToken.None));
            return;
        }
        CancellationTokenSource presenting = new();
        _presenting = presenting;
        _presentPending = true;
        _presentPendingKeepsView = keepView;
        PresentTask = PresentInBackgroundAsync(inputs, presenting, ++PresentRunCount);
    }

    private async Task PresentInBackgroundAsync(PresentInputs inputs, CancellationTokenSource presenting, int run)
    {
        try
        {
            Func<int, CancellationToken, Task>? gate = PresentGate;
            PresentResult result = await Task.Run(async () =>
            {
                PresentResult computed = ComputePresent(inputs, presenting.Token);
                if (gate != null) await gate(run, presenting.Token).ConfigureAwait(false);
                return computed;
            }, presenting.Token);
            if (presenting.IsCancellationRequested || _lifetime.IsCancellationRequested) return;
            _presentPending = false;
            ApplyPresent(inputs, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.present"))
        {
            // Logged and shown; the grid keeps what it had.
            if (ReferenceEquals(_presenting, presenting)) { _presentPending = false; UpdateStatus(); }
        }
        finally
        {
            if (ReferenceEquals(_presenting, presenting)) _presenting = null;
            presenting.Dispose();
        }
    }

    private PresentInputs CapturePresentInputs()
    {
        // The source and board choices belong to the index; they are rebuilt here, on the UI thread, when the index changed.
        if (!ReferenceEquals(_optionsFor, _snapshot)) RenderSourceOptions();
        MediaLibrarySnapshot? snapshot = _snapshot;
        if (snapshot != null && !ReferenceEquals(_presentIndex?.Snapshot, snapshot)) _presentIndex = new PresentIndexHolder(snapshot);
        MediaLibraryFilter? filter = null;
        bool invalid = false;
        try { filter = CurrentFilter(); }
        catch (ArgumentException) { invalid = true; }
        // Selected tiles that are still shown stay selected; the others drop out (see MediaPage.Selection.cs).
        MediaPageTermFilter? pageTerms = MediaPageTermFilter.From(_terms);
        Func<BooruCatalogSnapshot?>? readBooru = ReadBooruCatalog;
        MediaSourceTagIndexCache sourceTagCache = _sourceTagCache;
        return new PresentInputs(snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>(), snapshot == null ? null : _presentIndex, filter, invalid,
            Sort, SortDirection, HasField(MediaFilterField.Missing), _seed, _userData, snapshot == null ? null : _userOrder,
            _audio, _untagged, _tiles, _grid.Selection.SelectedIndexes.ToArray(), _selectionStamp)
        {
            PageTerms = pageTerms,
            SearchText = _nameFilter.Text,
            Queue = pageTerms?.NeedsQueue == true ? _queueKeys : null,
            SourceTags = pageTerms?.NeedsSourceTags != true ? null : () =>
            {
                try { return readBooru == null ? null : sourceTagCache.For(readBooru()); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { return null; }
            }
        };
    }

    // Filter, group and order (the index), and make the rows. Touches no control and no page state, so it may run on any thread.
    private static PresentResult ComputePresent(PresentInputs input, CancellationToken token)
    {
        IReadOnlyList<MediaLibraryEntry> all = input.All;
        string? errorKey = null;
        IReadOnlyList<MediaLibraryEntry> passing;
        int audioUnknown = 0;
        int? untaggedTiles = null;
        bool filterActive = false;
        IReadOnlyList<MediaTextTally>? tally = null; // where the words were found, for the media that pass every other filter
        MediaLibraryUntagged? untagged = input.Untagged;
        if (input.InvalidFilter)
        {
            errorKey = "media.filter.error.invalid";
            filterActive = true;
            passing = Array.Empty<MediaLibraryEntry>();
        }
        else
        {
            try
            {
                MediaLibraryFilter filter = input.Filter!;
                filterActive = !filter.IsEmpty || input.PageTerms != null;
                MediaLibraryFilterResult result = MediaLibraryFilterEngine.Evaluate(all, filter, input.UserData, input.Audio);
                passing = result.Passing;
                // MP1/MP4: the tagging queue and the source tags narrow what the core filter let through.
                tally = result.TextTally;
                if (input.PageTerms is { } pageTerms)
                {
                    MediaSourceTagIndex? sourceTags = input.SourceTags?.Invoke();
                    passing = pageTerms.Apply(passing, input.Queue, sourceTags);
                    // The row counts after the page-side terms too, so its numbers are exact with a queue or source tag term active.
                    if (tally != null) tally = KeepTallied(tally, pageTerms.Apply(tally.Select(item => item.Entry).ToArray(), input.Queue, sourceTags));
                }
                audioUnknown = result.UnknownSoundVideos;
                if (!filter.WithoutMediaTags)
                {
                    if (untagged?.IsFor(all, input.UserData) != true) untagged = MediaLibraryUntagged.Of(all, input.UserData);
                    untaggedTiles = untagged?.CountTiles(passing);
                }
            }
            catch (MediaLibraryFilterException exception)
            {
                tally = null;
                errorKey = exception.Error switch
                {
                    MediaLibraryFilterError.UnknownTag => "media.filter.error.unknownTag",
                    MediaLibraryFilterError.TagsUnavailable => "media.filter.error.tags",
                    MediaLibraryFilterError.FavoritesUnavailable => "media.filter.error.favorites",
                    _ => "media.filter.error.ratings"
                };
                passing = Array.Empty<MediaLibraryEntry>();
            }
            catch (MediaPageTermException exception)
            {
                errorKey = exception.TextKey;
                filterActive = true;
                passing = Array.Empty<MediaLibraryEntry>();
            }
            catch (ArgumentException)
            {
                errorKey = "media.filter.error.invalid";
                filterActive = true;
                passing = Array.Empty<MediaLibraryEntry>();
            }
        }
        if (errorKey != null) untaggedTiles = null;
        token.ThrowIfCancellationRequested();
        // Filter the occurrences first; a tile of equal bytes stays when any of its occurrences passed.
        MediaLibraryUserData userData = input.UserData;
        // S01: the tag count and rating keys are read for every index entry; they are made here (off the UI thread for a large index),
        // and only when the chosen order needs them. The page keeps the result for the next presentation (ApplyPresent).
        MediaLibraryUserOrder? userOrder = MediaLibraryUserOrder.For(input.Sort, all, userData, input.UserOrder);
        IReadOnlyList<MediaLibraryTile> tiles = input.Holder is { } holder
            ? holder.Index.Build(passing, input.Sort, input.IncludeMissing, input.Seed, userData.IsFavorite, token, input.Direction, userOrder)
            : MediaLibraryGrouping.Build(all, passing, input.Sort, input.IncludeMissing, input.Seed, userData.IsFavorite, input.Direction, userOrder);
        MediaLibraryEntry[] shown = new MediaLibraryEntry[tiles.Count];
        int missing = 0;
        for (int i = 0; i < shown.Length; i++)
        {
            shown[i] = tiles[i].Entry;
            if (shown[i].Presence == MediaLibraryPresence.Missing) missing++;
        }
        int unfiltered = !filterActive ? shown.Length
            : input.Holder is { } counted ? counted.Index.CountTiles(input.IncludeMissing) : MediaLibraryGrouping.CountTiles(all, input.IncludeMissing);
        token.ThrowIfCancellationRequested();
        // Own state is read from the data this presentation was built with, so a row never mixes two reads.
        Func<MediaLibraryEntry, MediaLibraryEntryState> state = entry => MediaLibraryEntryState.Of(entry, userData);
        MediaLibraryTileRow[] rows = new MediaLibraryTileRow[tiles.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = new MediaLibraryTileRow(tiles[i], i, state);
        IReadOnlyList<int> restored = RestoredSelection(input, tiles);
        // errorKey is null here exactly when the filter ran; with a text it also tallied. The tiles the scope shows are the grid's own.
        MediaTextCounts? breakdown = tally == null || errorKey != null ? null
            : MediaTextCounts.Of(tally, input.IncludeMissing, input.Filter!.TextFields, shown.Length);
        return new PresentResult(tiles, shown, MediaLibraryView.PresentIndex(shown), rows, missing, unfiltered, filterActive, errorKey,
            audioUnknown, untaggedTiles, untagged, restored, userOrder ?? input.UserOrder, breakdown);
    }

    // The entries of the tally the page-side terms kept. Both lists follow the order of the index (the terms only remove), so one pass pairs them.
    private static IReadOnlyList<MediaTextTally> KeepTallied(IReadOnlyList<MediaTextTally> tally, IReadOnlyList<MediaLibraryEntry> kept)
    {
        List<MediaTextTally> result = new(kept.Count);
        int next = 0;
        foreach (MediaTextTally item in tally)
        {
            if (next == kept.Count) break;
            if (!ReferenceEquals(item.Entry, kept[next])) continue;
            result.Add(item);
            next++;
        }
        return result;
    }

    private static IReadOnlyList<int> RestoredSelection(PresentInputs input, IReadOnlyList<MediaLibraryTile> tiles)
    {
        if (input.SelectedPositions.Length == 0) return Array.Empty<int>();
        MediaLibraryTileKey[] selected = MediaLibrarySelection.Pick(input.OldTiles, input.SelectedPositions).Select(MediaLibraryTileKey.Of).ToArray();
        return MediaLibrarySelection.Restore(selected, tiles);
    }

    private void ApplyPresent(PresentInputs input, PresentResult result)
    {
        // A presentation the data asked for keeps the view: where the grid stands is taken before anything of the old rows changes
        // (MediaPage.ViewKeep.cs); the previews they show are taken right before the swap.
        ViewKeep? keep = input.KeepView ? CaptureView() : null;
        PresentCount++;
        InvalidateSharedMediaMenu();
        DismissExportAllAsk(); // A pending "export all" question was about tiles that may no longer be shown.
        DismissQueueAsk();
        _filterActive = result.FilterActive;
        _filterFailed = result.ErrorKey != null;
        _untaggedTiles = result.UntaggedTiles;
        _untagged = result.Untagged;
        _userOrder = result.UserOrder;
        _audioUnknown = result.AudioUnknown;
        _filterErrorKey = result.ErrorKey;
        _filterError.Text = result.ErrorKey == null ? null : Texts.T(result.ErrorKey);
        _filterError.IsVisible = result.ErrorKey != null;
        _filterRetry.Content = Texts.T("media.filter.retry");
        _filterRetry.IsVisible = result.ErrorKey is "media.filter.error.queue" or "media.filter.error.sourceTags";
        _filterRetry.IsEnabled = true;
        AutomationProperties.SetName(_filterRetry, Texts.T("media.filter.retry"));
        UpdateAudioNote();
        // The selection may have changed while the rows were made; then it is carried over from what is selected now.
        // Ctrl+A pressed meanwhile (and nothing changed the selection after it) meant every tile of the new list, not of the old one.
        bool allRequested = _selectAllStamp == _selectionStamp;
        _selectAllStamp = -1;
        IReadOnlyList<int> restored = allRequested ? Enumerable.Range(0, result.Tiles.Count).ToArray()
            : input.SelectionStamp == _selectionStamp ? result.Restored
            : MediaLibrarySelection.Restore(SelectedTiles.Select(MediaLibraryTileKey.Of).ToArray(), result.Tiles);
        _tiles = result.Tiles;
        _shown = result.Shown;
        _mediaViewRanks = null;
        PresentPositions = result.PresentPositions;
        _shownMissing = result.ShownMissing;
        _unfiltered = result.Unfiltered;
        _breakdownCounts = result.Breakdown;
        _breakdownText = input.SearchText ?? string.Empty;
        if (keep != null) HandOverThumbnails();
        _restoring = true;
        try
        {
            _grid.ItemsSource = result.Rows;
            SelectIndexes(restored);
            if (keep != null) { HoldListScroll(); RestoreView(keep); }
        }
        finally { _restoring = false; }
        RefreshWorkView(); // the shuffle button follows the random order (state) and the view (placement)
        UpdateSortDirection();
        UpdateStatus();
        RefreshTagSidebar(); // MP4: the tag list counts the tiles now shown
        // No results: nothing to choose, so the panel gives its place to the empty state (the choice stays); results again bring it back.
        if (_panelHasItems != _tiles.Count > 0) ApplyLayoutPlan();
        SyncPanelToRows(); // the media panel keeps its medium (by its key) or follows the restored selection
    }

    private void UpdateStatus()
    {
        UpdateStatusText();
        _empty.Text = Texts.T(_filterActive ? "media.filter.noMatch" : "media.empty");
        // A refused filter explains itself above the grid; the grid stays empty.
        bool showEmpty = _snapshot != null && _shown.Count == 0 && _building == null && !_filterFailed && !_presentPending;
        _empty.IsVisible = showEmpty;
        _emptyState.IsVisible = showEmpty;
        // An empty library points to Downloads and Archive; a filter that matches nothing offers to reset it (C3).
        _emptyActions.IsVisible = showEmpty;
        _goDownloads.IsVisible = !_filterActive;
        _importFiles.IsVisible = !_filterActive && ImportFilesRequested != null;
        _linkFolder.IsVisible = !_filterActive && LinkFolderRequested != null;
        _emptyHelp.IsVisible = showEmpty && !_filterActive && (ImportFilesRequested != null || LinkFolderRequested != null);
        // An empty library shows the sentence and its action only; with a filter the filters stay, since they are the way out.
        _headerBody.IsVisible = !(showEmpty && !_filterActive);
        _emptyReset.IsVisible = _filterActive;
        // AP2/A6: a file name that matches nothing here can be tried in Archive in one step.
        string name = _nameFilter.Text ?? string.Empty;
        _emptySearchInArchive.IsVisible = showEmpty && _filterActive && MediaLibraryFilter.HasText(name) && SearchInArchiveRequested != null;
        if (_emptySearchInArchive.IsVisible)
        {
            _emptySearchInArchive.Content = Texts.T("search.carry.toArchive", name);
            AutomationProperties.SetName(_emptySearchInArchive, Texts.T("search.carry.toArchive", name));
        }
        _refresh.IsEnabled = !_lifetime.IsCancellationRequested;
        UpdateUntaggedLink();
        UpdateSearchResult();
        UpdateFilterToggle();
        UpdateSelectionBar();
        EndFocusWithoutHeader();
    }

    // The status line alone: what a progress tick changes. Every count in it is kept, so a tick costs no pass over the index.
    private void UpdateStatusText()
    {
        int threads = _snapshot?.Threads.Count ?? 0;
        int missing = _shownMissing;
        List<string> parts = new();
        if (_loadingCache) parts.Add(Texts.T("media.cache.loading"));
        if (_progress is { } progress && progress.ThreadsTotal > 0) parts.Add(Texts.Count("media.indexing", progress.ThreadsTotal, progress.ThreadsDone, progress.ThreadsTotal));
        if (_snapshot != null) parts.Add(_filterActive && !_filterFailed ? CountPhrases.MediaFilterSummary(_shown.Count, _unfiltered)
            : CountPhrases.MediaSummary(_shown.Count, threads, missing));
        if (ThreadMediaSummary() is { } threadMedia) parts.Add(threadMedia);
        if (_snapshot != null && _providerEntries is > 0 and int provider)
            parts.Add(Texts.Count("booru.media.statusCount", provider));
        if (_snapshot != null && _queueOpen > 0) parts.Add(Texts.T("media.status.queue", _queueOpen.ToString("N0", Texts.Culture))); // MP3
        if (_snapshot is { Complete: false } && _building == null) parts.Add(Texts.T("media.incomplete"));
        if (_failed) parts.Add(Texts.T("media.failed"));
        if (_snapshot == null && _building == null && !_failed && !_available) parts.Add(Texts.T("media.notReady"));
        if (CacheLimitNote() is { } note) parts.Add(note);
        _status.Text = string.Join(" · ", parts);
        UpdateStatusAttention(); // MP5: a status line the layout left out still shows what is not a count
        RefreshFocusRow();
    }

    // The saved index has a write limit (a setting); near it and above it the page says so instead of silently rebuilding.
    private string? CacheLimitNote()
    {
        if (_cacheWrite is not { } write) return null;
        string Size(long bytes) => SizeText.Format(bytes);
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

    // A decoded preview is kept by its bytes (the tile view gets a lease and gives it back), so scrolling back does not decode it again.
    private Task<Bitmap?> LoadThumbnailAsync(MediaLibraryTile tile, CancellationToken token) =>
        MediaTileThumbnails.LoadAsync(tile, OpenManifestAsync, PosterCacheDirectory, token);

    private Task<MediaAssetSnapshot> OpenManifestAsync(string directory, CancellationToken token) => _manifests.OpenAsync(directory, token);

    private MediaLibraryTileRow? SelectedRow => _grid.SelectedItem as MediaLibraryTileRow;

    internal Task OpenSelectedAsync() => SelectedRow is { } row ? OpenRowAsync(row) : Task.CompletedTask;

    // The gallery around one tile (Enter, double click, the media panel's "open in gallery"): a playing panel video pauses first.
    private async Task OpenRowAsync(MediaLibraryTileRow row)
    {
        if (OpenGallery == null) return;
        (IReadOnlyList<MediaLibraryEntry> window, int start) = MediaLibraryView.GalleryWindow(_shown, PresentPositions, row.Index);
        // A tile whose file is not saved (shown with "missing files") has nothing to show: say so instead of a click or Enter that does nothing.
        if (start < 0) { ShowSelectionNote(MediaMenuReasons.NotSaved); return; }
        _ = PausePanelVideo();
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
        if (_grid.Selection.Count == 1 && SelectedRow is { State.CanEditMediaTags: true } row && EditTags != null) await EditTags(row.Entry);
    }

    /// <summary>The post address of a provider medium as the catalog states it, validated; null for any other entry.</summary>
    internal Uri? ProviderPostUrl(MediaLibraryEntry entry)
    {
        if (entry.BooruPostId is not { } postId) return null;
        BooruPostRecord? record = null;
        // The first read of a large catalog (tens of MiB) is slow: it never happens here, on the UI thread of a menu. Until the
        // background read has finished, the canonical address of the post is used; the catalog only refines it.
        if (_booruCatalogWarm)
            try { record = ReadBooruCatalog?.Invoke()?.TryGetPost(BooruMediaIdentity.Source, postId); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { }
        else WarmBooruCatalog();
        return BooruMediaIdentity.PostUrl(postId, record?.Post.PostUrl);
    }

    private volatile bool _booruCatalogWarm;
    private int _booruCatalogWarming;

    /// <summary>Reads the Booru catalog once in the background, so that later lookups on the UI thread are served from the store's cache.</summary>
    private void WarmBooruCatalog()
    {
        if (_booruCatalogWarm || ReadBooruCatalog is not { } read || _lifetime.IsCancellationRequested
            || Interlocked.Exchange(ref _booruCatalogWarming, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { read(); _booruCatalogWarm = true; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { }
            finally { Volatile.Write(ref _booruCatalogWarming, 0); }
        });
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
        if (e.Handled || ShortcutService.IsCapturing || CommandRouter.IsNativeInput(e.Source, e.Key)) return;
        if (OnSelectionKey(e)) return;
        try
        {
            // The gallery's own "show in thread" command (T by default): one tile with a post; otherwise it does nothing.
            if (CommandBindings.Matches(e, "gallery.show-in-thread"))
            {
                e.Handled = true;
                if (_grid.Selection.Count > 1) ShowSelectionNote(MediaMenuReasons.SeveralSelected);
                else if (_grid.Selection.Count == 1) await ShowSelectedInThreadAsync();
                else ShowSelectionNote(MediaMenuReasons.Unavailable);
                return;
            }
            // The media rules of the thread the tile belongs to (the menu item's command).
            if (CommandBindings.Matches(e, "rules.edit-scope"))
            {
                e.Handled = true;
                if (_grid.Selection.Count > 1) ShowSelectionNote(MediaMenuReasons.SeveralSelected);
                else if (!_lifetime.IsCancellationRequested && _building == null && SelectedRow is { } ruleRow
                    && HasThreadRules(ruleRow.Entry) && EditThreadRules != null) EditThreadRules(ruleRow.Entry);
                else ShowSelectionNote(MediaMenuReasons.Unavailable);
                return;
            }
            // media.tags.edit: several selected tiles tag together (the selection bar's action), one edits its own tags.
            if (CommandBindings.Matches(e, "media.tags.edit"))
            {
                e.Handled = true;
                await EditTagsByShortcutAsync();
                return;
            }
            // media.queue.add: the selection (or nothing when nothing is selected) goes to the tagging queue, like the selection bar's action.
            if (CommandBindings.Matches(e, "media.queue.add"))
            {
                e.Handled = true;
                if (_grid.Selection.Count > 0 && _building == null && AddToTagQueue != null) await AddSelectionToQueueAsync();
                else ExplainSelectionUnavailable();
                return;
            }
            // tagging.remove (Delete): the selected media that are in the tagging queue leave it; otherwise the key does nothing. It never deletes a file.
            if (CommandBindings.Matches(e, "tagging.remove"))
            {
                e.Handled = true;
                await RemoveSelectionFromQueueAsync();
                return;
            }
            // M / U / F: every selected tile (revision 20, MediaPage.FavoriteKeys).
            if (TryRouteGridFavoriteKey(e)) return;
            // R: the tile the key came from (a focused tile that is not selected becomes the selection, as Enter does), or the selection.
            if (CommandBindings.Matches(e, "media.rating.edit"))
            {
                e.Handled = true;
                await RateAndTagByKeyAsync(e.Source as Control);
                return;
            }
            // Rating, details, export, folder and file path of the medium (menu commands): the same actions and gates as the tile menu.
            foreach (string id in MediaShortcutCommands)
                if (CommandBindings.Matches(e, id))
                {
                    e.Handled = true;
                    await RunMediaCommandByShortcutAsync(id);
                    return;
                }
            if (!IsOpenKey(e) || SelectedRow == null) return;
            e.Handled = true;
            // Several selected tiles open together; one opens around itself.
            if (_grid.Selection.Count > 1) await OpenSelectionGalleryAsync(); else await OpenSelectedAsync();
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.grid.key")) { /* Logged and shown. */ }
    }

    /// <summary>The Tags shortcut: the same popover as "Rate and tag…" for the one tile or the selection, with the tag field focused.</summary>
    internal async Task EditTagsByShortcutAsync()
    {
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy) return;
        if (_grid.Selection.Count > 1) { await TagSelectionAsync(initialFocus: QuickTagInitialFocus.TagInput); return; }
        if (SelectedRow is not { } row) return;
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible;
        MediaLibraryEntry entry = row.Entry;
        Control anchor = _grid.ContainerFromItem(row) ?? _grid;
        await OpenRateAndTagAsync(entry, row, anchor, Current, initialFocus: QuickTagInitialFocus.TagInput);
    }

    /// <summary>Why the tile cannot be tagged (null when it can): unknown tag index, a provider medium, no bound version.</summary>
    private static string? TagsBlockedReason(MediaLibraryEntry entry, MediaLibraryEntryState? state) =>
        state?.CanEditMediaTags == true ? null : state == null || !state.TagsKnown ? MediaMenuReasons.TagsUnknown
            : MediaMenuReasons.Tags(entry.AttachmentRevisionKey == null && entry.LocalSlot == null
                ? GalleryMediaTagUnavailable.NoPostVersion : GalleryMediaTagUnavailable.InvalidIdentity,
                providerMedia: entry.BooruPostId != null);

    /// <summary>
    /// "Rate and tag…" of the tiles (menu, R, Ctrl+Shift+T): the popover on the tile with the favourite and stars row on top and the tag field under
    /// it; several selected tiles are rated and tagged together. A tile that cannot be tagged gets the stars and the reason as text. Without a popover
    /// host the tag dialog opens as before.
    /// </summary>
    private async Task OpenRateAndTagAsync(MediaLibraryEntry entry, MediaLibraryTileRow row, Control anchor, Func<bool> current, QuickTagInitialFocus initialFocus,
        CancellationToken sidebarToken = default)
    {
        if (!sidebarToken.CanBeCanceled && _grid.Selection.Count > 1 && !_selectionBusy) { await TagSelectionAsync(anchor, initialFocus); return; }
        string? blocked = TagsBlockedReason(entry, row.State);
        if (TagPopoverAsync is { } popover)
        {
            await popover(entry, anchor, QuickTagPopoverContext.ContextOf(entry, _shown, row.Index), current, new RateAndTagRequest(initialFocus, blocked, FocusGridNow, sidebarToken));
            return;
        }
        if (blocked != null)
        {
            QuickTagPopover.OpenBlocked(anchor, entry.Name ?? string.Empty, blocked, new QuickTagPopoverOptions { IsCurrent = current, FocusFallback = FocusGridNow });
            return;
        }
        if (EditTagsForMenuAsync != null) await EditTagsForMenuAsync(entry, current);
        else await ExecuteMediaActionAsync("media.tags.edit", entry, current);
    }

    // media.rating.edit (R) opens "Rate and tag" on the tile; the stars and the removal rate the selected tile directly (Shift+1 to 5, Shift+0).
    private static readonly string[] MediaShortcutCommands =
    {
        "media.rating.edit", "media.rating.set-1", "media.rating.set-2", "media.rating.set-3", "media.rating.set-4", "media.rating.set-5", "media.rating.clear",
        "media.label.red", "media.label.yellow", "media.label.green", "media.label.blue", "media.label.clear",
        "media.details", "media.export-original", "media.show-in-folder", "media.copy-path", "media.next-untagged"
    };

    /// <summary>The shortcut for a menu command of one medium. With several selected tiles (KB-06, never silent): export exports the selection, a
    /// rating key rates every selected saved tile whose rating is known, and details, folder and file path say that they need a single item (the
    /// menu's reason, in the selection note line); otherwise they need exactly one saved tile, like the menu entries they stand for.</summary>
    internal async Task RunMediaCommandByShortcutAsync(string id)
    {
        if (id == "media.next-untagged") { SelectNextUntagged(); return; }
        if (id == "media.rating.edit") { await RateAndTagByKeyAsync(null); return; }
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy) { ExplainSelectionUnavailable(); return; }
        if (id == "media.export-original" && _grid.Selection.Count > 1) { await ExportSelectionAsync(); return; }
        if (_grid.Selection.Count > 1)
        {
            if (!id.StartsWith("media.rating.", StringComparison.Ordinal)) { ShowSelectionNote(MediaMenuReasons.SeveralSelected); return; }
            await RateSelectionByShortcutAsync(id);
            return;
        }
        if (ExecuteMediaMenuActionAsync == null || _grid.Selection.Count != 1 || SelectedRow is not { } row || row.Entry.Presence != MediaLibraryPresence.Present) return;
        if (id.StartsWith("media.rating.", StringComparison.Ordinal) && row.State?.RatingKnown != true) return;
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible;
        await ExecuteMediaActionAsync(id, row.Entry, Current);
    }

    // Freeze the selected row identities before writing. A rating can passively replace the grid rows; that refresh must not cancel the remaining
    // selected targets or retarget the shortcut to replacement rows. Tiles that cannot be rated are left alone.
    private async Task RateSelectionByShortcutAsync(string id)
    {
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible;
        MediaLibraryTileRow[] targets = _grid.Selection.SelectedItems.OfType<MediaLibraryTileRow>()
            .Where(row => row.Entry.Presence == MediaLibraryPresence.Present && row.State?.RatingKnown == true)
            .OrderBy(row => row.Index).ToArray();
        if (ExecuteMediaMenuActionAsync == null || targets.Length == 0)
        {
            ShowSelectionNote(MediaMenuReasons.Unavailable);
            return;
        }
        _selectionBusy = true;
        UpdateSelectionBar();
        try
        {
            foreach (MediaLibraryTileRow row in targets)
            {
                if (!Current()) { ExplainSelectionUnavailable(); return; }
                await ExecuteMediaActionAsync(id, row.Entry, Current);
            }
        }
        finally
        {
            _selectionBusy = false;
            if (!_lifetime.IsCancellationRequested) UpdateSelectionBar();
        }
    }

    /// <summary>
    /// R on the Media page (audit 16.11.2026, "Rate and tag sometimes does not open on R"): the combined popover for the tile the key came from, or for the
    /// selection. A tile that has the keyboard focus without being selected (Ctrl+arrow, the return from the gallery focuses a tile and selects none)
    /// becomes the selection, as Enter makes it; before, R did nothing there. Several selected tiles are rated and tagged together. Without a tile, with a
    /// file that is not saved or while the index is being built the note under the selection bar says why, never silence.
    /// </summary>
    internal async Task RateAndTagByKeyAsync(Control? source)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (_building != null) { ShowSelectionNote(Texts.T("media.selection.reason.building")); return; }
        if (_selectionBusy) return; // the selection's own popover or dialog is open and has the keyboard
        if (source?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
                .FirstOrDefault(item => ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), _grid)) is { DataContext: MediaLibraryTileRow focused }
            && !_grid.Selection.SelectedItems.Contains(focused))
            _grid.SelectedItem = focused;
        if (_grid.Selection.Count > 1) { await TagSelectionAsync(initialFocus: QuickTagInitialFocus.TagInput); return; }
        if (SelectedRow is not { } row) { ShowSelectionNote(MediaMenuReasons.Unavailable); return; }
        if (row.Entry.Presence != MediaLibraryPresence.Present) { ShowSelectionNote(MediaMenuReasons.NotSaved); return; }
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible;
        await OpenRateAndTagAsync(row.Entry, row, _grid.ContainerFromItem(row) ?? _grid, Current, initialFocus: QuickTagInitialFocus.TagInput);
    }

    // Route only the page's media commands before ListBox typeahead. Panel video/favourite
    // keys keep their existing tunnel route; native navigation and activation stay untouched.
    private static bool IsPageMediaShortcut(KeyEventArgs e) =>
        e.Key is not (Key.Enter or Key.Space or Key.Tab or Key.Escape)
        && (MediaShortcutCommands.Any(id => CommandBindings.Matches(e, id))
            || CommandBindings.Matches(e, "media.tags.edit")
            || CommandBindings.Matches(e, "gallery.show-in-thread")
            || CommandBindings.Matches(e, "rules.edit-scope")
            || CommandBindings.Matches(e, "media.queue.add")
            || CommandBindings.Matches(e, "tagging.remove"));

    private void OnGridPageShortcut(object? sender, KeyEventArgs e)
    {
        if (IsPageMediaShortcut(e)) OnGridKeyDown(sender, e);
    }

    // Toolbar/sidebar focus uses the selection, not the panel's displayed medium.
    private void OnPageRateAndTagKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _lifetime.IsCancellationRequested || ShortcutService.IsCapturing || e.Source is not Visual source
            || CommandRouter.IsNativeInput(e.Source, e.Key) || !IsPageMediaShortcut(e)) return;
        Visual[] ancestors = source.GetSelfAndVisualAncestors().ToArray();
        if (!ancestors.Contains(this) || ancestors.Any(visual => ReferenceEquals(visual, _grid)
            || _panel != null && ReferenceEquals(visual, _panel))) return;
        // The tag list owns Delete/Back (remove the selected filter term). Do not
        // consume them as the page's tagging.remove shortcut on the tunnel down.
        if (e.Key is Key.Delete or Key.Back && ancestors.Contains(_tagSidebar.List)) return;
        OnGridKeyDown(sender, e);
    }

    /// <summary>The grid's place for the keyboard after a popover closed without its tile (rebuilt, scrolled away): the selected tile, else the grid.</summary>
    internal bool FocusGridNow()
    {
        if (_lifetime.IsCancellationRequested || !_grid.IsEffectivelyVisible) return false;
        if (SelectedRow is { } row && _grid.ContainerFromItem(row) is Control tile && tile.Focus()) return true;
        return _grid.Focus();
    }

    internal void InitializeSharedMediaMenus()
    {
        if (_sharedMediaMenusInitialized) return;
        _sharedMediaMenusInitialized = true;
        _grid.AddHandler(PointerPressedEvent, OnSharedPointerPressed, RoutingStrategies.Tunnel);
        _grid.AddHandler(PointerReleasedEvent, OnSharedPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
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
        _sharedPointerContext.Reset();
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

    // The press opens the menu and arms the gate; the ContextRequested that Avalonia 12 raises with the RELEASE of that click is consumed once.
    // The gate ends after the release (OnSharedPointerReleased), never on a tick posted by the press: a click lasts far longer than a Background
    // tick, and a gate dropped that early let the release open the menu a second time, re-resolved for whatever was under the pointer then.
    private void OnSharedPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _sharedPointerContext.BeginPress(); // each press starts its own gesture
        if (e.Handled || _lifetime.IsCancellationRequested || !IsEffectivelyVisible
            || IsNativeContextSource(e.Source) || IsTileTagChip(e.Source)
            || !e.GetCurrentPoint(_grid).Properties.IsRightButtonPressed) return;
        MediaLibraryTileRow? row = TargetForMenu(RowAt(e.Source));
        Control origin = ResolveMenuOrigin(e.Source, row);
        if (row is not null)
        {
            if (!_grid.Selection.SelectedItems.Contains(row)) _grid.SelectedItem = row;
            (_grid.ContainerFromItem(row) as Control ?? origin).Focus();
        }
        // MP5: a right click on empty grid space (no tile under the pointer) opens the layout menu, also while a tile is selected.
        OpenSharedMediaMenu(row, origin);
        _sharedPointerContext.MenuOpenedByPress();
        e.Handled = true;
    }

    // The release of that right click: the request Avalonia sends with it comes through the handlers after this one, so the gate ends on the next tick.
    private void OnSharedPointerReleased(object? sender, PointerReleasedEventArgs e) => _sharedPointerContext.Released(e.InitialPressMouseButton);

    private void OnSharedContextKeyDown(object? sender, KeyEventArgs e)
    {
        _sharedPointerContext.Reset(); // a key ends a pointer gesture whose release never came (a menu popup took it)
        if (!IsContextKey(e) || IsTileTagChip(e.Source)) return; // a tag chip has its own menu (the chip's host opens it)
        MediaLibraryTileRow? row = TargetForMenu(RowAt(e.Source)) ?? SelectedRow;
        Control origin = ResolveMenuOrigin(e.Source, row);
        if (row is not null)
        {
            if (!_grid.Selection.SelectedItems.Contains(row)) _grid.SelectedItem = row;
            (_grid.ContainerFromItem(row) as Control ?? origin).Focus();
        }
        OpenSharedMediaMenu(row, origin);
        e.Handled = true;
    }

    private void OnSharedContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || IsNativeContextSource(e.Source)) return;
        if (_sharedPointerContext.TryConsume())
        {
            e.Handled = true;
            return;
        }
        MediaLibraryTileRow? row = TargetForMenu(RowAt(e.Source)) ?? SelectedRow;
        Control origin = ResolveMenuOrigin(e.Source, row);
        if (row is not null)
        {
            if (!_grid.Selection.SelectedItems.Contains(row)) _grid.SelectedItem = row;
            (_grid.ContainerFromItem(row) as Control ?? origin).Focus();
        }
        OpenSharedMediaMenu(row, origin);
        e.Handled = true;
    }

    // MD-01/PT-01 (17.11.2026), as Archive and Downloads do: a tile outside the selection becomes the selection before its menu opens, so every entry
    // acts on the clicked tile; a tile inside a multi-selection keeps the selection and the menu acts on all of it.
    private MediaLibraryTileRow? TargetForMenu(MediaLibraryTileRow? row)
    {
        if (row == null || row.Index < 0 || row.Index >= _tiles.Count || _grid.Selection.IsSelected(row.Index)) return row;
        // Replace the target in one selection-model operation. Clearing first publishes an empty selection,
        // which lets the preview/grid rebuild between the right-click target resolution and menu composition.
        _grid.SelectedItem = row;
        return row;
    }

    private void OpenSharedMediaMenu(MediaLibraryTileRow? row, Control origin, ReaderInlineVideoSlot? playback = null)
    {
        if (_lifetime.IsCancellationRequested || !IsEffectivelyVisible) return;
        long revision = _mediaMenuRevision;
        long serial = ++_mediaMenuSerial;
        HashSet<int> selection = _grid.Selection.SelectedIndexes.ToHashSet();
        object? rows = _grid.ItemsSource;
        object? originData = origin.DataContext;
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible
            && revision == _mediaMenuRevision && serial == _mediaMenuSerial
            && selection.SetEquals(_grid.Selection.SelectedIndexes)
            && ReferenceEquals(rows, _grid.ItemsSource)
            && origin.IsAttachedToVisualTree() && origin.IsEffectivelyVisible
            && ReferenceEquals(originData, origin.DataContext);

        if (row == null)
        {
            MediaMenuSession blank = new(MediaMenuSurface.MediaPage, null, Current);
            Task RunBlank(Func<Task> action) => RunMenuActionAsync(action, blank);
            // MP5: empty grid space offers the layout entries (the same as the toolbar's Layout menu) below the two actions on the whole grid.
            List<Control> blankItems = new();
            if (_building != null) { blankItems.Add(MediaMenuBuilder.Note(Texts.T("media.selection.reason.building"))); blankItems.Add(new Separator()); }
            blankItems.AddRange(new Control[]
            {
                MediaMenuBuilder.ContextItem(blank, "media.exportAll", ExportAllAsync, () => _exportAll.IsEnabled, RunBlank, icon: MenuIcon("export.open-all-results-html-dialog")),
                MediaMenuBuilder.ContextItem(blank, "media.queue.all", AddAllShownToQueueAsync, () => AddToTagQueue != null && _tiles.Count > 0, RunBlank,
                    MediaMenuReasons.Unavailable, MenuIcon("media.queue.all")),
                new Separator()
            });
            blankItems.AddRange(LayoutMenuItems());
            if (QueueGroupMenu(blank, _tiles.Select(tile => tile.Entry).ToArray(), RunBlank) is { } blankGroups) blankItems.Add(blankGroups);
            ContextMenu blankMenu = new() { ItemsSource = blankItems };
            blank.Invalidated += () => blankMenu.Close();
            _mediaMenuPresenter.Show(blankMenu, blank, origin, attachToOrigin: true, fallbackFocus: () => FocusGrid(Current));
            return;
        }
        MediaLibraryEntry entry = row.Entry;
        MediaLibraryTile tile = row.Tile;
        // The page replaces its lists, never changes them in place: no copy, and the saved positions were found once per presentation.
        (IReadOnlyList<MediaLibraryEntry> galleryWindow, int galleryStart) = MediaLibraryView.GalleryWindow(_shown, PresentPositions, row.Index);
        MediaLibraryEntryState? state = row.State;
        int selectedCount = _grid.Selection.Count;
        // Several: the clicked tile is one of several selected (a tile outside the selection was made the selection, TargetForMenu); every entry then
        // acts on the selection frozen now, and the one-file entries (details, folder, path, gallery, thread) say why they are off.
        bool several = selectedCount > 1 && _grid.Selection.IsSelected(row.Index);
        bool hostActions = ExecuteMediaMenuActionAsync != null;
        IReadOnlyList<MediaLibraryTile>? queueTiles = several ? SelectedTiles : null;
        MediaLibraryTileRow[] favoriteRows = several ? _grid.Selection.SelectedItems.OfType<MediaLibraryTileRow>().ToArray() : [row];
        bool?[] favoriteStates = favoriteRows.Select(selected => FavoriteKeyState(selected, PendingFavorite)).ToArray();
        MediaMenuFacts facts = new(MediaMenuSurface.MediaPage, MediaMenuTarget.Of(entry))
        {
            Present = entry.Presence == MediaLibraryPresence.Present,
            GalleryWired = galleryStart >= 0 && (OpenGallery != null || hostActions),
            SeveralSelected = several,
            // Several: "Remove from favorites" when every writable selected tile is a favourite, else "Add" (the F key's rule, FavoriteKeys).
            Favorite = several
                ? !favoriteStates.Any(known => known != null) ? MediaMenuFavorite.Unknown
                    : favoriteStates.All(known => known != false) ? MediaMenuFavorite.On : MediaMenuFavorite.Off
                : state?.Favorite switch
                {
                    MediaLibraryFlag.Yes => MediaMenuFavorite.On,
                    MediaLibraryFlag.No => MediaMenuFavorite.Off,
                    _ => MediaMenuFavorite.Unknown
                },
            FavoriteWired = hostActions,
            RatingWired = hostActions,
            RatingKnown = state?.RatingKnown == true,
            TagsWired = EditTagsForMenuAsync != null || TagPopoverAsync != null || hostActions,
            TagsBlocked = TagsBlockedReason(entry, state),
            TagsBlockedExplained = true,
            QueueWired = AddToTagQueue != null,
            QueueBlocked = TagQueueActions.BlockedReason(entry.Presence == MediaLibraryPresence.Present, entry.BooruPostId != null),
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
            [MediaMenuAction.Favorite] = several
                ? () => Current() ? FavoriteKeyTask = WriteFavoriteKeyAsync(FavoriteKeyAction.Toggle, favoriteRows) : Task.CompletedTask
                : () => ExecuteMediaActionAsync("favorites.toggle", entry, Current),
            [MediaMenuAction.RateAndTag] = () => OpenRateAndTagAsync(entry, row, origin, Current, initialFocus: QuickTagInitialFocus.TagInput),
            // One tile: that tile. Several selected: the selection frozen now, like the selection actions below.
            [MediaMenuAction.Queue] = queueTiles is { Count: > 1 } ? () => AddTilesToQueueAsync(queueTiles) : () => AddEntryToQueueAsync(entry),
            [MediaMenuAction.Export] = several ? () => Current() ? ExportSelectionAsync() : Task.CompletedTask
                : () => ExecuteMediaActionAsync("media.export-original", entry, Current),
            [MediaMenuAction.ShowInFolder] = () => ExecuteMediaActionAsync("media.show-in-folder", entry, Current),
            [MediaMenuAction.CopyPath] = () => ExecuteMediaActionAsync("media.copy-path", entry, Current),
            [MediaMenuAction.Details] = () => ExecuteMediaActionAsync("media.details", entry, Current),
            [MediaMenuAction.ShowInThread] = () => ShowInThread == null || entry.PostId == null ? Task.CompletedTask : ShowInThread(entry),
            [MediaMenuAction.OpenProviderPost] = () => OpenProviderPostAsync(entry),
            [MediaMenuAction.CopyProviderPost] = () => CopyProviderPostAsync(entry)
        };
        // Everything below the common section belongs to this page: it works on the grid, not on one medium.
        List<Control> context = new();
        if (several) context.Add(MediaMenuBuilder.Note(Texts.T("media.menu.note.selection", selectedCount)));
        context.Add(MediaMenuBuilder.ContextItem(session, "media.filter.onlyThread", () => { FilterToThread(entry); return Task.CompletedTask; },
            () => !several, Run, MediaMenuReasons.SeveralSelected, MenuIcon("tags.filter-only")));
        context.Add(MediaMenuBuilder.ContextItem(session, "media.occurrences", () => { ShowOccurrences(tile); return Task.CompletedTask; },
            () => tile.IsGroup, Run, MediaMenuReasons.Unavailable, "list"));
        context.Add(MediaMenuBuilder.ContextCommandItem(session, "rules.edit-scope", () => { EditThreadRules?.Invoke(entry); return Task.CompletedTask; },
            () => EditThreadRules != null && HasThreadRules(entry), Run, Texts.T("media.rulesThreadUnavailable"), Texts.T("rules.thisThread")));
        // "Tag the selection" is the common "Rate and tag…" entry when several tiles are selected (one entry, one surface).
        foreach ((Button action, string key, string icon) in new[] {
            (_selGallery, "media.selection.gallery", MenuIcon("gallery.open")), (_selExport, "media.selection.export", MenuIcon("media.export-original")) })
        {
            Button selectedAction = action;
            context.Add(MediaMenuBuilder.ContextItem(session, key, () =>
            {
                selectedAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return Task.CompletedTask;
            }, () => selectedAction.IsEnabled, Run, ToolTip.GetTip(selectedAction) as string ?? MediaMenuReasons.Unavailable, icon));
        }
        context.Add(MediaMenuBuilder.ContextItem(session, "media.exportAll", ExportAllAsync, () => _exportAll.IsEnabled, Run,
            icon: MenuIcon("export.open-all-results-html-dialog")));
        context.Add(MediaMenuBuilder.ContextItem(session, "media.queue.all", AddAllShownToQueueAsync, () => AddToTagQueue != null && _tiles.Count > 0, Run,
            MediaMenuReasons.Unavailable, MenuIcon("media.queue.all")));
        IReadOnlyList<MediaLibraryEntry> queueEntries = queueTiles is { Count: > 1 }
            ? queueTiles.Select(tileEntry => tileEntry.Entry).ToArray() : new[] { entry };
        if (QueueGroupMenu(session, queueEntries, Run) is { } groups) context.Add(groups);
        // The media panel's menu of a video starts with the playback group (play / pause, mute, repeat), as the reader's menu of a video does.
        // While the index is being built every action of the menu refuses (the guard above), like the selection bar's: the menu says why, on top.
        List<Control>? leading = ReaderPlaybackMenu.MenuItems(playback, session, Run);
        if (_building != null) (leading ??= new()).Insert(0, MediaMenuBuilder.Note(Texts.T("media.selection.reason.building")));
        ContextMenu menu = MediaMenuBuilder.Build(session, model, handlers, Run, context, ShowSelectionNote, leading);
        _mediaMenuPresenter.Show(menu, session, origin, attachToOrigin: true, fallbackFocus: () => FocusGrid(Current));
    }

    private Control? QueueGroupMenu(MediaMenuSession session, IReadOnlyList<MediaLibraryEntry> entries, Func<Func<Task>, Task> run)
    {
        if (AddToTagQueueInGroup == null || ReadTagQueue is not { } read || entries.Count == 0) return null;
        TagQueueSnapshot? queue;
        try { queue = read(); }
        catch (Exception error) when (error is InvalidDataException or IOException or ObjectDisposedException) { return null; }
        if (queue is not { Groups.Count: > 0 }) return null;
        MediaLibraryEntry[] frozen = entries.ToArray();
        List<Control> choices = new();
        foreach (TagQueueGroup group in queue.Groups.OrderBy(item => item.Order))
        {
            string groupId = group.Id;
            choices.Add(MediaMenuBuilder.ContextItem(session, "media.queue.group:" + groupId, group.Name,
                () => AddEntriesToQueueInGroupAsync(frozen, groupId),
                () => AddToTagQueueInGroup != null && !_selectionBusy,
                run, MediaMenuReasons.Unavailable));
        }
        return MediaMenuBuilder.Submenu(session, "media.queue.group", Texts.T("media.queue.group"), choices, icon: MenuIcon("media.queue.add"));
    }

    // The page's own menu entries take the icon of the command that does the same elsewhere (the menu's icon column stays filled).
    private static string MenuIcon(string commandId) => CommandCatalog.Get(commandId).Icon;

    private readonly MediaMenuPresenter _mediaMenuPresenter = new();

    private Task OpenMediaMenuAsync(MediaLibraryEntry entry, IReadOnlyList<MediaLibraryEntry> galleryWindow,
        int galleryStart, Func<bool> current)
    {
        _ = PausePanelVideo();
        return ExecuteMediaMenuActionAsync != null
            ? ExecuteMediaMenuActionAsync("media.open", entry, current)
            : OpenGallery?.Invoke(galleryWindow, galleryStart) ?? Task.CompletedTask;
    }

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
            LocalizeSortControls();
        }
        finally { _localizing = false; }
        _title.Text = Texts.T("media.title");
        _subtitle.Text = Texts.T("media.subtitle");
        // The tools of the row beside the order are icons (M1); the name is the tooltip and the accessible name.
        WorkspaceIcons.MakeSquare(_shuffle, WorkspaceIcons.Shuffle, Texts.T("media.shuffle"));
        _missing.Content = Texts.T("media.showMissing");
        WorkspaceIcons.MakeSquare(_refresh, WorkspaceIcons.Refresh, Texts.T("media.refresh"));
        LocalizeFocus();
        LocalizeBooruMode();
        LocalizeLayout();
        SyncPanelToggle();
        LocalizePanel();
        LocalizeQueueTools();
        LocalizeWorkView();
        CommandBindings.Apply(_manageRatings, "media.rating.manage");
        _manageRatings.IsEnabled = !_lifetime.IsCancellationRequested && ManageMediaRatingsAsync != null;
        CommandBindings.ApplyLabeled(_goDownloads, "navigation.downloads", Texts.T("nav.goDownloads"));
        CommandBindings.Apply(_importFiles, "import.open", iconWithLabel: true);
        CommandBindings.Apply(_linkFolder, "media.link-folder", iconWithLabel: true);
        _emptyHelp.Text = Texts.T("media.empty.copyOrLink");
        AutomationProperties.SetName(_grid, Texts.T("media.title"));
        LocalizeFilters();
        LocalizeFilterSurface();
        LocalizeSelection();
        if (_grid.ItemsSource != null) Present(keepView: true); else UpdateStatus();
    }

    private readonly IDisposable _pageKeyContext;

    internal void EndLifetime()
    {
        if (_lifetime.IsCancellationRequested) return;
        _pageKeyContext.Dispose();
        InvalidateSharedMediaMenu();
        _lifetime.Cancel();
        _nameFilter.CancelPendingQuery();
        _building?.Cancel();
        EndBooruModeLifetime();
        Texts.Changed -= OnLanguageChanged;
        _occurrences?.Close();
        _selectionTag?.CloseForLifetime();
        _panelFollowTimer?.Stop();
        _panelAnnounceTimer?.Stop();
        CancelPanelLoad();
        _ = ReleasePanelVideos();
        _panel?.ReleasePicture();
    }
}

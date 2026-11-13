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
    private readonly ComboBox _sort = new() { Name = "MediaSort", MinWidth = 160 };
    private readonly Button _shuffle = new() { Name = "MediaShuffle", IsVisible = false };
    private readonly CheckBox _missing = new() { Name = "MediaShowMissing" };
    private readonly Button _refresh = new() { Name = "MediaRefresh" };
    private readonly TextBlock _status = new() { Name = "MediaStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly ListBox _grid = new() { Name = "MediaGrid", SelectionMode = SelectionMode.Multiple, Classes = { "workspace-selection-bar" } };
    private readonly TextBlock _empty = new() { Name = "MediaEmpty", IsVisible = false };
    // An empty library points back to where media comes from; a filter that matches nothing offers its reset (MediaPage.FilterChips.cs).
    private readonly Button _goDownloads = new() { Name = "MediaGoDownloads", Classes = { "primary" } };
    private readonly WrapPanel _emptyActions = new() { Name = "MediaEmptyActions", ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    // The shared empty state (centered, one sentence, one primary action); its sentence and actions are added in the constructor.
    private readonly WorkspaceEmptyState _emptyState = new() { Name = "MediaEmptyState", IsVisible = false };
    // Everything under the title: it is hidden while the library is empty, so the empty page shows the sentence and its action only.
    private readonly StackPanel _headerBody = new() { Spacing = 4 };
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
    /// <summary>Opens a provider post page; the shell supplies the browser launcher, tests a recorder.</summary>
    internal Func<Uri, Task>? OpenProviderPage { get; set; }
    /// <summary>Copies a provider post address to the clipboard; the default uses this page's clipboard.</summary>
    internal Func<Uri, Task>? CopyProviderPage { get; set; }
    internal Func<MediaLibraryEntry, Func<bool>, Task>? EditTagsForMenuAsync { get; set; }
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
        _openManifest = openManifest ?? MediaAssetReader.OpenAsync;
        // Only the real manifest reader is checked by file identity; a supplied opener keeps the per-build behavior.
        _manifests = new MediaManifestCache(openManifest, _lifetime.Token);
        _cacheFile = cachePath == null ? null : new MediaLibraryIndexCacheFile(cachePath);
        Name = "MediaPage";
        WrapPanel tools = new() { Classes = { "workspace-toolbar" } };
        foreach (Control control in new Control[] { _filters, _sort, _shuffle, _missing, _refresh, _exportAll })
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            // Buttons match the 32 dip combo box beside them (Workspace.Size.Input) instead of sitting 1 dip inside its edges at 30 (G3).
            if (control is Button) control.MinHeight = 32;
            tools.Children.Add(control);
        }
        StackPanel header = new() { Spacing = 4, Margin = new Thickness(12, 8, 12, 4) };
        header.Children.Add(_title); header.Children.Add(_headerBody);
        _headerBody.Children.Add(_subtitle); _headerBody.Children.Add(tools);
        // The filter fields open as a surface over the grid (MediaPage.FilterSurface.cs); what is applied stays in the header.
        Border filterSurface = BuildFilterSurface(BuildFilterRow());
        _headerBody.Children.Add(BuildFilterChipRow()); _headerBody.Children.Add(_filterError); _headerBody.Children.Add(BuildStatusRow(_status));
        _headerBody.Children.Add(BuildExportAllAsk()); _headerBody.Children.Add(BuildSelectionBar());
        _grid.ItemsPanel = new FuncTemplate<Panel?>(() => new MediaTilePanel { ItemHeight = MediaLibraryTileView.TileHeight });
        _grid.ItemTemplate = new FuncDataTemplate<MediaLibraryTileRow>((_, _) => new MediaLibraryTileView { ThumbnailLoader = LoadThumbnailAsync, ThumbnailCached = MediaTileThumbnails.TryGetCached,
            ThumbnailRelease = MediaTileThumbnails.Release, ThreadFilter = FilterToThread,
            TagFilter = FilterToTag, ShowOccurrences = ShowOccurrences, FavoriteToggle = ToggleTileFavoriteAsync,
            CanToggleFavorite = () => ExecuteMediaMenuActionAsync != null });
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Panel body = new();
        _emptyActions.Children.Add(_goDownloads);
        _empty.Classes.Add(WorkspaceEmptyState.TextClass);
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
        _grid.DoubleTapped += (_, _) => _ = OpenSelectedAsync();
        _grid.KeyDown += OnGridKeyDown;
        _grid.AddHandler(KeyDownEvent, OnSelectAllWhilePresenting, RoutingStrategies.Tunnel);
        _grid.SelectionChanged += (_, _) => { if (_restoring) return; _selectionStamp++; ShowSelectionNote(null); UpdateSelectionBar(); };
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
        WarmBooruCatalog();
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
        bool InvalidFilter, MediaLibrarySort Sort, bool IncludeMissing, int Seed, MediaLibraryUserData UserData, MediaAudioMap Audio,
        MediaLibraryUntagged? Untagged, IReadOnlyList<MediaLibraryTile> OldTiles, int[] SelectedPositions, long SelectionStamp);

    private sealed record PresentResult(IReadOnlyList<MediaLibraryTile> Tiles, MediaLibraryEntry[] Shown, int[] PresentPositions,
        MediaLibraryTileRow[] Rows, int ShownMissing, int Unfiltered, bool FilterActive, string? ErrorKey, int AudioUnknown,
        int? UntaggedTiles, MediaLibraryUntagged? Untagged, IReadOnlyList<int> Restored);

    private void Present()
    {
        _presenting?.Cancel();
        _presenting = null;
        PresentInputs inputs = CapturePresentInputs();
        if (inputs.All.Count < BackgroundPresentEntries)
        {
            _presentPending = false;
            PresentTask = Task.CompletedTask;
            ApplyPresent(inputs, ComputePresent(inputs, CancellationToken.None));
            return;
        }
        CancellationTokenSource presenting = new();
        _presenting = presenting;
        _presentPending = true;
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
        return new PresentInputs(snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>(), snapshot == null ? null : _presentIndex, filter, invalid,
            Sort, _missing.IsChecked == true, _seed, _userData, _audio, _untagged, _tiles, _grid.Selection.SelectedIndexes.ToArray(), _selectionStamp);
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
                filterActive = !filter.IsEmpty;
                MediaLibraryFilterResult result = MediaLibraryFilterEngine.Evaluate(all, filter, input.UserData, input.Audio);
                passing = result.Passing;
                audioUnknown = result.UnknownSoundVideos;
                if (!filter.WithoutMediaTags)
                {
                    if (untagged?.IsFor(all, input.UserData) != true) untagged = MediaLibraryUntagged.Of(all, input.UserData);
                    untaggedTiles = untagged?.CountTiles(passing);
                }
            }
            catch (MediaLibraryFilterException exception)
            {
                errorKey = exception.Error switch
                {
                    MediaLibraryFilterError.UnknownTag => "media.filter.error.unknownTag",
                    MediaLibraryFilterError.TagsUnavailable => "media.filter.error.tags",
                    MediaLibraryFilterError.FavoritesUnavailable => "media.filter.error.favorites",
                    _ => "media.filter.error.ratings"
                };
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
        IReadOnlyList<MediaLibraryTile> tiles = input.Holder is { } holder
            ? holder.Index.Build(passing, input.Sort, input.IncludeMissing, input.Seed, userData.IsFavorite, token)
            : MediaLibraryGrouping.Build(all, passing, input.Sort, input.IncludeMissing, input.Seed, userData.IsFavorite);
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
        return new PresentResult(tiles, shown, MediaLibraryView.PresentIndex(shown), rows, missing, unfiltered, filterActive, errorKey,
            audioUnknown, untaggedTiles, untagged, restored);
    }

    private static IReadOnlyList<int> RestoredSelection(PresentInputs input, IReadOnlyList<MediaLibraryTile> tiles)
    {
        if (input.SelectedPositions.Length == 0) return Array.Empty<int>();
        MediaLibraryTileKey[] selected = MediaLibrarySelection.Pick(input.OldTiles, input.SelectedPositions).Select(MediaLibraryTileKey.Of).ToArray();
        return MediaLibrarySelection.Restore(selected, tiles);
    }

    private void ApplyPresent(PresentInputs input, PresentResult result)
    {
        PresentCount++;
        InvalidateSharedMediaMenu();
        DismissExportAllAsk(); // A pending "export all" question was about tiles that may no longer be shown.
        _filterActive = result.FilterActive;
        _filterFailed = result.ErrorKey != null;
        _untaggedTiles = result.UntaggedTiles;
        _untagged = result.Untagged;
        _audioUnknown = result.AudioUnknown;
        _filterError.Text = result.ErrorKey == null ? null : Texts.T(result.ErrorKey);
        _filterError.IsVisible = result.ErrorKey != null;
        _filterReset.IsEnabled = _filterActive;
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
        PresentPositions = result.PresentPositions;
        _shownMissing = result.ShownMissing;
        _unfiltered = result.Unfiltered;
        _restoring = true;
        try
        {
            _grid.ItemsSource = result.Rows;
            SelectIndexes(restored);
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
        bool showEmpty = _snapshot != null && _shown.Count == 0 && _building == null && !_filterFailed && !_presentPending;
        _empty.IsVisible = showEmpty;
        _emptyState.IsVisible = showEmpty;
        // An empty library points to Downloads and Archive; a filter that matches nothing offers to reset it (C3).
        _emptyActions.IsVisible = showEmpty;
        _goDownloads.IsVisible = !_filterActive;
        // An empty library shows the sentence and its action only; with a filter the filters stay, since they are the way out.
        _headerBody.IsVisible = !(showEmpty && !_filterActive);
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
    private async Task<Bitmap?> LoadThumbnailAsync(MediaLibraryTile tile, CancellationToken token) =>
        await MediaTileThumbnails.LoadAsync(tile, OpenManifestAsync, PosterCacheDirectory, token).ConfigureAwait(false) is { } bitmap
            ? MediaTileThumbnails.Remember(tile, bitmap) : null;

    private Task<MediaAssetSnapshot> OpenManifestAsync(string directory, CancellationToken token) => _manifests.OpenAsync(directory, token);

    private MediaLibraryTileRow? SelectedRow => _grid.SelectedItem as MediaLibraryTileRow;

    internal async Task OpenSelectedAsync()
    {
        if (SelectedRow is not { } row || OpenGallery == null) return;
        (IReadOnlyList<MediaLibraryEntry> window, int start) = MediaLibraryView.GalleryWindow(_shown, PresentPositions, row.Index);
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
        if (OnSelectionKey(e)) return;
        try
        {
            // The gallery's own "show in thread" command (T by default): one tile with a post; otherwise it does nothing.
            if (CommandBindings.Matches(e, "gallery.show-in-thread"))
            {
                e.Handled = true;
                if (_grid.Selection.Count == 1) await ShowSelectedInThreadAsync();
                return;
            }
            // The media rules of the thread the tile belongs to (the menu item's command).
            if (CommandBindings.Matches(e, "rules.edit-scope"))
            {
                e.Handled = true;
                if (!_lifetime.IsCancellationRequested && _building == null && SelectedRow is { } ruleRow
                    && HasThreadRules(ruleRow.Entry)) EditThreadRules?.Invoke(ruleRow.Entry);
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
            if (_grid.Selection.Count > 1) await OpenSelectionGalleryAsync(); else await OpenSelectedAsync();
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.grid.key")) { /* Logged and shown. */ }
    }

    /// <summary>The Tags shortcut: the same two paths as the menu (one tile) and the selection bar (several), with the same gates.</summary>
    internal async Task EditTagsByShortcutAsync()
    {
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy) return;
        if (_grid.Selection.Count > 1) { await TagSelectionAsync(); return; }
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
        if (id == "media.export-original" && _grid.Selection.Count > 1) { await ExportSelectionAsync(); return; }
        if (_grid.Selection.Count != 1 || SelectedRow is not { } row || row.Entry.Presence != MediaLibraryPresence.Present) return;
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
        // The page replaces its lists, never changes them in place: no copy, and the saved positions were found once per presentation.
        (IReadOnlyList<MediaLibraryEntry> galleryWindow, int galleryStart) = MediaLibraryView.GalleryWindow(_shown, PresentPositions, row.Index);
        MediaLibraryEntryState? state = row.State;
        int selectedCount = _grid.Selection.Count;
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
        context.Add(MediaMenuBuilder.ContextCommandItem(session, "rules.edit-scope", () => { EditThreadRules?.Invoke(entry); return Task.CompletedTask; },
            () => EditThreadRules != null && HasThreadRules(entry), Run, Texts.T("media.rulesThreadUnavailable"), Texts.T("rules.thisThread")));
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

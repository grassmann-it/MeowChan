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
using MeowChan.Desktop.Commands;
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
    private bool _sharedMediaMenusInitialized;
    private long _mediaMenuRevision;
    private long _mediaMenuSerial;
    private Control? _mediaMenuOwner;
    private bool _sharedPointerContextPending;

    /// <summary>Opens the gallery for a window of shown entries; the int is the chosen entry inside it.</summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>, int, Task>? OpenGallery { get; set; }
    internal Func<MediaLibraryEntry, Task>? ShowInThread { get; set; }
    /// <summary>Edits the own media tags of one entry through the host's tag commands.</summary>
    internal Func<MediaLibraryEntry, Task>? EditTags { get; set; }
    // The shell supplies the guarded executor for the shared menu. Keeping this seam on
    // the page means menu construction never resolves a selected row at invocation time.
    internal Func<string, MediaLibraryEntry, Func<bool>, Task>? ExecuteMediaMenuActionAsync { get; set; }
    internal Func<string, MediaLibraryEntry, string?>? MediaMenuActionUnavailableReason { get; set; }
    internal Func<MediaLibraryEntry, Func<bool>, Task>? EditTagsForMenuAsync { get; set; }
    internal ContextMenu? MediaMenu { get; private set; }
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
        WrapPanel tools = new() { ItemSpacing = 6, LineSpacing = 4 };
        foreach (Control control in new Control[] { _filters, _sort, _shuffle, _missing, _refresh, _exportAll }) { control.VerticalAlignment = VerticalAlignment.Center; tools.Children.Add(control); }
        StackPanel header = new() { Spacing = 4, Margin = new Thickness(12, 8, 12, 4) };
        header.Children.Add(_title); header.Children.Add(_subtitle); header.Children.Add(tools);
        // The filter fields open as a surface over the grid (MediaPage.FilterSurface.cs); what is applied stays in the header.
        Border filterSurface = BuildFilterSurface(BuildFilterRow());
        header.Children.Add(_filterChips); header.Children.Add(_filterError); header.Children.Add(_status); header.Children.Add(BuildExportAllAsk()); header.Children.Add(BuildSelectionBar());
        _grid.ItemsPanel = new FuncTemplate<Panel?>(() => new MediaTilePanel { ItemHeight = MediaLibraryTileView.TileHeight });
        _grid.ItemTemplate = new FuncDataTemplate<MediaLibraryTileRow>((_, _) => new MediaLibraryTileView { ThumbnailLoader = LoadThumbnailAsync, ThreadFilter = FilterToThread, ShowOccurrences = ShowOccurrences });
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Panel body = new();
        body.Children.Add(_grid); body.Children.Add(_empty);
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

    private void Present()
    {
        InvalidateSharedMediaMenu();
        DismissExportAllAsk(); // A pending "export all" question was about tiles that may no longer be shown.
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
        UpdateFilterToggle();
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

    /// <summary>Puts the keyboard focus back on the grid (its selected tile, else the first) once the tiles are laid out; the list itself is not focusable.</summary>
    internal void FocusGrid() => Dispatcher.UIThread.Post(() =>
    {
        if (_lifetime.IsCancellationRequested || !_grid.IsEffectivelyVisible) return;
        int index = SelectedRow?.Index ?? (_grid.ItemCount > 0 ? 0 : -1);
        if (index < 0) return;
        _grid.ScrollIntoView(index);
        (_grid.ContainerFromIndex(index) as Control ?? _grid).Focus();
    }, DispatcherPriority.Loaded);

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
        try
        {
            // The gallery's own "show in thread" command (T by default): one tile with a post; otherwise it does nothing.
            if (CommandBindings.Matches(e, "gallery.show-in-thread"))
            {
                e.Handled = true;
                if (SelectedTiles.Count == 1) await ShowSelectedInThreadAsync();
                return;
            }
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || SelectedRow == null) return;
            e.Handled = true;
            // Several selected tiles open together; one opens around itself.
            if (SelectedTiles.Count > 1) await OpenSelectionGalleryAsync(); else await OpenSelectedAsync();
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.grid.key")) { /* Logged and shown. */ }
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
        if (MediaMenu is not { } menu) return;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>()) item.IsEnabled = false;
        menu.Close();
        if (_mediaMenuOwner is { } owner && ReferenceEquals(owner.ContextMenu, menu)) owner.ContextMenu = null;
        _mediaMenuOwner = null;
    }

    private static bool IsNativeContextSource(object? source) => source is Visual visual
        && visual.GetSelfAndVisualAncestors().Any(control => control is TextBox or ComboBox or Slider or SelectableTextBlock
            or Ursa.Controls.AutoCompleteBox);

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

    private static bool IsContextKey(KeyEventArgs e) => !e.Handled && !CommandRouter.IsNativeInput(e.Source, e.Key)
        && (e.Key == Key.Apps && e.KeyModifiers == KeyModifiers.None
            || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift
            || CommandBindings.Matches(e, "menu.context-open"));

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

        List<object> items = new();
        if (row == null)
        {
            MenuItem exportAll = LocalizedMenuItem("media.exportAll", _ => ExportAllAsync(),
                () => _exportAll.IsEnabled && Current());
            items.Add(exportAll);
        }
        else
        {
            MediaLibraryEntry entry = row.Entry;
            MediaLibraryTile tile = row.Tile;
            MediaLibraryEntry[] shown = _shown.ToArray();
            (IReadOnlyList<MediaLibraryEntry> galleryWindow, int galleryStart) =
                MediaLibraryView.GalleryWindow(shown, row.Index);
            MediaLibraryEntryState? state = row.State;
            bool present = entry.Presence == MediaLibraryPresence.Present;
            bool single = SelectedTiles.Count == 1;
            bool canTags = state?.CanEditMediaTags == true && (EditTagsForMenuAsync != null || ExecuteMediaMenuActionAsync != null);
            string tagReason = !present ? Texts.T("archive.notSaved")
                : state == null || !state.TagsKnown ? Texts.T("tags.unavailable")
                : state.TagTarget == null ? UnboundTagReason(entry) : Texts.T("tags.unavailable");

            items.Add(CommandMenuItem(entry, "media.open", () => OpenMediaMenuAsync(entry, galleryWindow, galleryStart, Current),
                single && present && galleryStart >= 0 && (OpenGallery != null || ExecuteMediaMenuActionAsync != null),
                Current, Texts.T("archive.notSaved")));
            items.Add(CommandMenuItem(entry, "favorites.toggle", () => ExecuteMediaActionAsync("favorites.toggle", entry, Current),
                present && (state?.Favorite is MediaLibraryFlag.No or MediaLibraryFlag.Yes) && ExecuteMediaMenuActionAsync != null,
                Current, Texts.T("media.selection.reason.unavailable")));
            items.Add(CommandMenuItem(entry, "media.rating.edit", () => ExecuteMediaActionAsync("media.rating.edit", entry, Current),
                present && state?.RatingKnown == true && ExecuteMediaMenuActionAsync != null,
                Current, Texts.T("media.selection.reason.unavailable")));
            items.Add(CommandMenuItem(entry, "media.tags.edit", () => EditTagsForMenuAsync != null
                    ? EditTagsForMenuAsync(entry, Current)
                    : ExecuteMediaActionAsync("media.tags.edit", entry, Current),
                canTags, Current, tagReason));
            items.Add(CommandMenuItem(entry, "media.export-original", () => ExecuteMediaActionAsync("media.export-original", entry, Current),
                present && ExecuteMediaMenuActionAsync != null,
                Current, Texts.T("export.missing")));
            items.Add(CommandMenuItem(entry, "media.show-in-folder", () => ExecuteMediaActionAsync("media.show-in-folder", entry, Current),
                present && ExecuteMediaMenuActionAsync != null, Current, Texts.T("media.selection.reason.unavailable")));
            items.Add(CommandMenuItem(entry, "media.details", () => ExecuteMediaActionAsync("media.details", entry, Current),
                present && ExecuteMediaMenuActionAsync != null, Current, Texts.T("archive.notSaved")));
            items.Add(new Separator());
            items.Add(CommandMenuItem(entry, "gallery.show-in-thread", () => ShowInThread == null || entry.PostId == null
                    ? Task.CompletedTask : ShowInThread(entry),
                single && entry.PostId != null && ShowInThread != null, Current, Texts.T("gallery.noPost")));
            items.Add(LocalizedMenuItem("media.filter.onlyThread", _ => { FilterToThread(entry); return Task.CompletedTask; },
                () => single && Current()));
            items.Add(LocalizedMenuItem("media.occurrences", _ => { ShowOccurrences(tile); return Task.CompletedTask; },
                () => tile.IsGroup && Current()));
            foreach ((Button action, string key) in new[] { (_selTag, "media.selection.tag"),
                (_selGallery, "media.selection.gallery"), (_selExport, "media.selection.export") })
            {
                Button selectedAction = action;
                items.Add(LocalizedMenuItem(key, _ =>
                {
                    if (Current()) selectedAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return Task.CompletedTask;
                }, () => selectedAction.IsEnabled && Current()));
            }
            items.Add(LocalizedMenuItem("media.exportAll", _ => ExportAllAsync(),
                () => _exportAll.IsEnabled && Current()));
        }

        ContextMenu menu = new() { ItemsSource = items };
        ContextMenu? previousMenu = MediaMenu;
        Control? previousOwner = _mediaMenuOwner;
        previousMenu?.Close();
        if (previousOwner is { } previous && ReferenceEquals(previous.ContextMenu, previousMenu)) previous.ContextMenu = null;
        MediaMenu = menu;
        _mediaMenuOwner = origin;
        origin.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_mediaMenuOwner, origin) && ReferenceEquals(origin.ContextMenu, menu))
            {
                origin.ContextMenu = null;
                _mediaMenuOwner = null;
            }
            RestoreMenuFocus(menu, origin, Current);
        };
        menu.Open(origin);
    }

    private Task OpenMediaMenuAsync(MediaLibraryEntry entry, IReadOnlyList<MediaLibraryEntry> galleryWindow,
        int galleryStart, Func<bool> current) => ExecuteMediaMenuActionAsync != null
            ? ExecuteMediaMenuActionAsync("media.open", entry, current)
            : OpenGallery?.Invoke(galleryWindow, galleryStart) ?? Task.CompletedTask;

    private static string UnboundTagReason(MediaLibraryEntry entry) =>
        entry.PostId == null || entry.Position == null || entry.AttachmentRevisionKey == null
            ? Texts.T("tags.media.unbound") : Texts.T("tags.media.unsupported");

    private MenuItem CommandMenuItem(MediaLibraryEntry entry, string id, Func<Task> action, bool available, Func<bool> current, string reason)
    {
        string? hostReason = MediaMenuActionUnavailableReason?.Invoke(id, entry);
        MenuItem item = CommandBindings.CreateMenuItem(id, () => _ = RunMenuActionAsync(action, current),
            () => available && current(), () => hostReason ?? (available ? null : reason));
        item.Tag = id;
        return item;
    }

    private MenuItem LocalizedMenuItem(string key, Func<MediaLibraryEntry?, Task> action, Func<bool> canExecute)
    {
        MenuItem item = new() { Header = Texts.T(key), Tag = key,
            Command = new DelegateCommand(() => _ = RunMenuActionAsync(() => action(null), canExecute), canExecute) };
        AutomationProperties.SetName(item, item.Header?.ToString() ?? string.Empty);
        return item;
    }

    private async Task ExecuteMediaActionAsync(string id, MediaLibraryEntry entry, Func<bool> current)
    {
        if (ExecuteMediaMenuActionAsync != null) await ExecuteMediaMenuActionAsync(id, entry, current);
        else if (id == "media.tags.edit" && EditTags != null) await EditTags(entry);
    }

    private async Task RunMenuActionAsync(Func<Task> action, Func<bool> current)
    {
        if (!current()) return;
        try { await action(); }
        catch (OperationCanceledException) when (!current() || _lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { if (current()) ShowSelectionNote(error.Message); }
    }

    private void RestoreMenuFocus(ContextMenu menu, Control origin, Func<bool> current)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!current() || ReferenceEquals(MediaMenu, menu) && menu.IsOpen) return;
            TopLevel? top = TopLevel.GetTopLevel(origin);
            object? focused = top?.FocusManager?.GetFocusedElement();
            if (focused is Control control && !ReferenceEquals(control, menu) && control.GetSelfAndVisualAncestors().OfType<ContextMenu>().Any()) return;
            if (origin.IsAttachedToVisualTree() && origin.IsEffectivelyVisible && origin.IsEffectivelyEnabled) origin.Focus();
            else FocusGrid();
        }, DispatcherPriority.Background);
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
        LocalizeFilterSurface();
        LocalizeSelection();
        if (_grid.ItemsSource != null) Present(); else UpdateStatus();
    }

    internal void EndLifetime()
    {
        if (_lifetime.IsCancellationRequested) return;
        InvalidateSharedMediaMenu();
        _lifetime.Cancel();
        _building?.Cancel();
        Texts.Changed -= OnLanguageChanged;
        _occurrences?.Close();
        _selectionTag?.CloseForLifetime();
    }
}

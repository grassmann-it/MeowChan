using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan;

public partial class ArchivePage : UserControl
{
    private const int PageSize = 50;
    private readonly Func<CancellationToken, Task<ArchiveReader>>? _openReader;
    private readonly Func<ResourceId, bool, Task>? _setCapture;
    private readonly Action<string>? _openFolder;
    private readonly Func<string, CancellationToken, Task<MediaAssetSnapshot>> _openMedia = MediaAssetReader.OpenAsync;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly SemaphoreSlim _thumbnailGate = new SemaphoreSlim(2, 2);
    private TaskCompletionSource<IReadOnlyList<ArchiveAssetRow>> _mediaReady = NewMediaReady();
    private static TaskCompletionSource<IReadOnlyList<ArchiveAssetRow>> NewMediaReady() => new TaskCompletionSource<IReadOnlyList<ArchiveAssetRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _mediaGate = new SemaphoreSlim(1, 1);
    private CancellationTokenSource? _contentCancellation;
    private CancellationTokenSource? _mediaCancellation;
    private CancellationTokenSource? _previewCancellation;
    private int _contentRequest;
    private int _mediaRequest;
    private int _previewRequest;
    private int _listOffset;
    private int _listTotal;
    private int _postOffset;
    private bool _available;
    private bool _busy;
    private bool _ended;
    private bool _updatingList;
    private bool _showReader;
    private bool _mediaLoading;
    private ResourceId? _selectedThread;
    private ArchivedThreadView? _thread;
    private IReadOnlyList<ArchiveAssetRow> _assets = Array.Empty<ArchiveAssetRow>();
    private Bitmap? _preview;
    private string? _previewPath;
    internal Func<ResourceId, GalleryEntryKey?, Control?, Task>? OpenGallery { get; set; }
    internal Func<IReadOnlyList<GalleryEntry>, GalleryEntryKey, Control?, Task>? OpenMediaGallery { get; set; }
    internal Func<ArchiveSearchRequest, int, Control?, Task>? OpenResultsGallery { get; set; }
    internal Func<IReadOnlyList<ThreadWorkSnapshot>>? GetWorkSnapshots { get; set; }
    private int _workFilter;
    private bool _threadTable;
    private string _archiveLayout = "List";
    private double _archiveSplitWidth = 270;
    internal Func<string, double, Task>? SaveViewPreferencesAsync { get; set; }
    internal string ArchiveLayoutPreference
    {
        get => _archiveLayout;
        set { _archiveLayout = value == "Table" ? "Table" : "List"; _threadTable = _archiveLayout == "Table"; UpdateLayoutMode(); }
    }
    internal double ArchiveSplitPreference
    {
        get => _archiveSplitWidth;
        set { _archiveSplitWidth = double.IsFinite(value) ? Math.Clamp(value, 180, 1200) : 270; UpdateLayoutMode(); }
    }
    private bool _mediaTiles = true;
    internal bool MediaTilesPreference
    {
        get => _mediaTiles;
        set
        {
            MediaPosition? position = _shownMedia.Count > 0 ? CaptureMediaPosition() : null;
            _mediaTiles = value;
            if (IsInitialized || _largeReader)
            {
                Control<DataGrid>("ArchiveMediaGrid").IsVisible = !value;
                Control<ListBox>("ArchiveMediaTiles").IsVisible = value;
                Control<Button>("ArchiveMediaColumnsButton").IsVisible = !value;
                Control<Button>("ArchiveMediaMode").Content = Texts.T(value ? "gallery.list" : "gallery.tiles");
                if (position != null) RestoreMediaPosition(position);
            }
        }
    }
    internal Func<bool, Task>? SaveMediaTiles { get; set; }
    private bool _allVersions = true;
    private int _mediaSort;
    private bool _localizingMediaChoices;
    private bool _archiveFavoritesOnly;

    public ArchivePage()
    {
        InitializeComponent(); InitializeArchiveResultActions(); InitializeSuggestions(); InitializeSearchEditors(); InitializeArchiveSelection(); InitializeArchiveMediaCommands(); InitializeRatingCommands(); InitializeMediaPositionInput();
        // Navigation scrolls explicitly; selection must not re-anchor while media is loading.
        Control<ListBox>("ArchivePosts").AutoScrollToSelectedItem = false;
        Control<ListBox>("ArchiveMediaTiles").AutoScrollToSelectedItem = false;
        InitializeReaderInteraction(); InitializeReaderUpdates(); InitializeReadingPositionInput(); InitializePostBookmarks();
        InitializePaginationFocus();
    }
    internal ArchivePage(Func<CancellationToken, Task<ArchiveReader>> openReader,
        Func<ResourceId, bool, Task> setCapture, Action<string> openFolder,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>>? openMedia = null) : this()
    {
        _openReader = openReader;
        _setCapture = setCapture;
        _openFolder = openFolder;
        if (openMedia != null) _openMedia = openMedia;
        Texts.Changed += OnLanguageChanged;
        Localize();
        UpdateLayoutMode();
        Control<DataGrid>("ArchiveMediaGrid").IsVisible = false;
        Control<ListBox>("ArchiveMediaTiles").IsVisible = true;
        Control<Button>("ArchiveMediaColumnsButton").IsVisible = false;
        Control<ComboBox>("ArchiveVersionScope").SelectedIndex = 1;
    }
    internal bool IsAvailable { set { _available = value; UpdateActions(); } }
    internal bool IsBusy => _busy;
    internal bool IsMediaLoading => _mediaLoading;
    internal Task MediaLoadTask { get; private set; } = Task.CompletedTask;
    internal ArchivedThreadView? CurrentThread => _thread;

    internal async Task ActivateAsync()
    {
        if (_ended || !_available || _openReader == null || _busy) return;
        InvalidateSuggestions();
        ClosePreview();
        await LoadListAsync();
    }

    private async Task<bool> LoadListAsync(ListLoad? pending = null, CancellationToken applicationToken = default)
    {
        if (_ended || !_available || _openReader == null || _busy || applicationToken.IsCancellationRequested) return false;
        pending ??= NewListLoad(_appliedSearch ?? ArchiveSearchRequest.Create(new ArchiveSearchSpec()), _listOffset);
        // Everything needed for membership and SQL is captured before opening the reader.
        _runningList = pending;
        _retryList = null;
        CancelReferences(); CancelInlineReplies();
        int generation = ++_contentRequest;
        _contentCancellation?.Cancel();
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, applicationToken);
        _contentCancellation = loading;
        CancellationToken token = loading.Token;
        CancelMediaLoad(); ClosePreview();
        _busy = true;
        BeginContentFeedback();
        Message(string.Empty);
        UpdateActions();
        ResourceId? selected = _selectedThread;
        bool committed = false;
        try
        {
            await using ArchiveReader reader = await _openReader(token);
            int offset = pending.Offset;
            int workFilter = WorkIndex(pending.Request.Spec.WorkFilter);
            async Task<ArchiveThreadPage> ReadPage(int target)
            {
                if (workFilter == 0)
                    return await reader.SearchThreadsBySpecAsync(pending.Request, target, PageSize, token);
                IReadOnlyList<ArchiveThreadSummary> all = await reader.SelectThreadSummariesBySpecAsync(pending.Request, token);
                ArchiveThreadSummary[] matches = all.Where(item => WorkMatches(pending.Work.GetValueOrDefault(item.Url), workFilter)).ToArray();
                return new ArchiveThreadPage(Array.AsReadOnly(matches.Skip(target).Take(PageSize).ToArray()), matches.Length, target);
            }
            ArchiveThreadPage result = await ReadPage(offset);
            if (offset > 0 && (result.TotalCount <= offset || result.Items.Count == 0))
            {
                offset = 0; // Exactly one fallback, using the SAME request and work membership.
                result = await ReadPage(offset);
            }
            IReadOnlyDictionary<ResourceId, ArchiveThreadPreview> previews = await reader.ReadThreadPreviewsAsync(
                result.Items.Select(summary => summary.Id).ToArray(), token);
            ArchiveThreadRow[] rows = result.Items.Select(summary =>
            {
                previews.TryGetValue(summary.Id, out ArchiveThreadPreview? preview);
                return new ArchiveThreadRow(summary, pending.Work.GetValueOrDefault(summary.Url))
                {
                    PreviewText = preview?.FirstCapturedText ?? string.Empty,
                    PreviewAttachment = preview?.FirstAttachment is CapturedAttachment attachment
                        ? CreateListAttachmentRow(summary, attachment) : null
                };
            }).ToArray();
            if (_ended || token.IsCancellationRequested || generation != _contentRequest) return false;
            _updatingList = true;
            try
            {
                _appliedSearch = pending.Request; _query = pending.Request.Spec; _appliedWork = pending.Work;
                _workFilter = workFilter; _listOffset = offset; _listTotal = result.TotalCount;
                ListBox list = Control<ListBox>("ArchiveThreads");
                DataGrid table = Control<DataGrid>("ArchiveThreadTable");
                list.ItemsSource = rows; table.ItemsSource = rows;
                list.SelectedItem = rows.FirstOrDefault(row => row.Summary.Id == selected);
                table.SelectedItem = rows.FirstOrDefault(row => row.Summary.Id == selected);
                RestoreArchiveSelection(rows, selected);
                _listLoaded = true;
                pending.AfterCommit?.Invoke();
                UpdateAppliedFilters(); UpdateArchiveEmptyState();
                committed = true;
            }
            finally { _updatingList = false; }
            Message(string.Empty);
            AppliedSearchChanged?.Invoke();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_ended && generation == _contentRequest && !token.IsCancellationRequested)
            { _retryList = pending; OperationMessage("content", exception.Message, true); }
        }
        finally
        {
            if (ReferenceEquals(_contentCancellation, loading)) _contentCancellation = null;
            if (ReferenceEquals(_runningList, pending)) _runningList = null;
            if (!_ended && generation == _contentRequest)
            { _busy = false; UpdateActions(); }
        }
        if (committed && _showReader && selected != null && !_ended && generation == _contentRequest)
            await LoadThreadAsync(selected, resetPage: false);
        return committed;
    }

    internal static bool WorkMatches(ThreadWorkSnapshot? work, int choice) => work != null && (choice switch
    {
        1 => work.Intent == MeowChan.Persistence.WorkIntent.Watching && work.Source == MeowChan.Persistence.ThreadSourceStateValue.Active,
        2 => work.Intent == MeowChan.Persistence.WorkIntent.Watching && work.Source != MeowChan.Persistence.ThreadSourceStateValue.Active,
        3 => work.Intent == MeowChan.Persistence.WorkIntent.Repairing || work.Media.Any(media => media.Status == MeowChan.Persistence.MediaWorkStatus.NeedsIntervention),
        4 => work.Intent == MeowChan.Persistence.WorkIntent.Completed,
        5 => work.Intent == MeowChan.Persistence.WorkIntent.Suppressed,
        _ => true
    });

    private void OnThreadSelected(object? sender, SelectionChangedEventArgs e) => ArchiveSelectionChanged(sender);

    // URSA-12: a preflighted local snapshot keeps failed group navigation from clearing the current reader.
    internal async Task LoadThreadAsync(ResourceId id, bool resetPage = true, ArchivedThreadView? preparedThread = null)
    {
        if (_ended || !_available || _openReader == null) return;
        ReaderAnchor? retainedAnchor = !resetPage && _thread?.Summary.Id == id ? CaptureReaderAnchor() : null;
        CancelReferences();
        CancelInlineReplies();
        int request = ++_contentRequest;
        _contentCancellation?.Cancel();
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _contentCancellation = loading;
        CancellationToken cancellationToken = loading.Token;
        CancelMediaLoad();
        ClosePreview();
        if (resetPage || _selectedThread != id) _postOffset = 0;
        _retainedMediaPosition = CaptureMediaPosition();
        _mediaProjectionPending = true;
        _selectedThread = id;
        _thread = null;
        _assets = Array.Empty<ArchiveAssetRow>();
        _busy = true;
        BeginContentFeedback();
        _showReader = true;
        _updatingList = true;
        try
        {
            ArchiveThreadRow[] rows = ArchiveVisibleRows;
            if (rows.Any(row => row.Summary.Id == id) && _archiveSelectionIds.Count == 0)
            {
                _archiveSelectionIds.Clear(); _archiveSelectionIds.Add(id); _archiveLeadId = id;
            }
            RestoreArchiveSelection(rows);
        }
        finally { _updatingList = false; }
        RenderThread();
        UpdateLayoutMode();
        Message(string.Empty);
        try
        {
            // The catalog is local. Display its posts before touching potentially disconnected media drives.
            ArchivedThreadView? thread = preparedThread;
            if (thread == null)
            {
                await using ArchiveReader reader = await _openReader(cancellationToken);
                thread = await reader.ReadThreadAsync(id, cancellationToken);
            }
            if (_ended || request != _contentRequest) return;
            _thread = thread;
            if (thread == null) _mediaProjectionPending = false;
            ReaderSnapshotLoaded();
            if (_thread != null && _postOffset >= _thread.PostCount) _postOffset = 0;
            _busy = false;
            RenderThread();
            Message(string.Empty);
            if (thread != null)
            {
                MediaLoadTask = LoadMediaAsync(thread);
                if (retainedAnchor != null) await RestoreReaderAnchorAsync(retainedAnchor);
                else if (_largeReader && thread.PostCount > 0)
                    await FocusPostAsync(thread.GetPosts(_postOffset, 1).First().PostId, resetPage ? null : _readerInteractionRevision);
                else if (_largeReader) Control<Button>("ArchiveCloseReader").Focus();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _contentRequest) OperationMessage("content", exception.Message, true); }
        finally
        {
            if (ReferenceEquals(_contentCancellation, loading)) _contentCancellation = null;
            if (!_ended && request == _contentRequest)
            {
                _busy = false;
                if (_thread == null)
                {
                    _mediaProjectionPending = false;
                    _retainedMediaPosition = null;
                    ++_mediaPositionRequest;
                }
                UpdateActions();
            }
        }
    }

    private async Task LoadMediaAsync(ArchivedThreadView thread)
    {
        int request = ++_mediaRequest;
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _mediaCancellation = loading;
        CancellationToken cancellationToken = loading.Token;
        _mediaLoading = true;
        RenderMedia();
        try
        {
            (List<ArchiveAssetRow> assets, List<string> warnings) loaded = await Task.Run(async () =>
            {
                // A cancelled NAS operation may still be waiting in the OS. Keep at most one scan
                // running; changing threads cancels queued scans without blocking local post reading.
                await _mediaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    List<ArchiveAssetRow> assets = new List<ArchiveAssetRow>();
                    List<string> warnings = new List<string>();
                    foreach (string directory in thread.MediaDirectories.DistinctBy(CanonicalMediaDirectory, StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            MediaAssetSnapshot manifest = await _openMedia(directory, cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (manifest.Warning != null) warnings.Add(manifest.Warning);
                            for (int offset = 0; offset < manifest.Count; offset += 500)
                                assets.AddRange(manifest.GetPage(offset, 500).Select(asset => new ArchiveAssetRow(manifest, asset)));
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception exception) { warnings.Add(directory + ": " + exception.Message); }
                    }
                    return (assets, warnings);
                }
                finally { _mediaGate.Release(); }
            }, cancellationToken);
            if (_ended || request != _mediaRequest) return;
            _assets = loaded.assets.AsReadOnly();
            OperationMessage("media", string.Join(Environment.NewLine, loaded.warnings), loaded.warnings.Count > 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _mediaRequest) OperationMessage("media", exception.Message, true); }
        finally
        {
            if (ReferenceEquals(_mediaCancellation, loading)) _mediaCancellation = null;
            if (!_ended && request == _mediaRequest) { _mediaLoading = false; _mediaProjectionPending = false; _mediaReady.TrySetResult(_assets); RenderMedia(); }
        }
    }

    private void CancelMediaLoad()
    {
        ClearOperationError("media");
        ++_mediaRequest;
        _mediaReady.TrySetCanceled();
        _mediaReady = NewMediaReady();
        _mediaCancellation?.Cancel();
        _mediaLoading = false;
        _mediaProjectionPending = false;
        ++_mediaPositionRequest;
        if (_ended) _retainedMediaPosition = null;
    }

    private void RenderThread()
    {
        Control<TextBlock>("ArchiveSelectHint").IsVisible = _thread == null && !_busy;
        Control<TabControl>("ArchiveContentTabs").IsVisible = _thread != null;
        Control<TextBlock>("ArchiveReaderTitle").Text = _thread?.Summary.Title ?? string.Empty;
        string status = _thread?.Summary.LastCapturedAt is DateTimeOffset captured
            ? Texts.T("archive.captured", captured.ToLocalTime().ToString("g", Texts.Culture), _thread.PostCount)
            : _thread != null ? Texts.T("archive.mediaOnly") : string.Empty;
        if (_thread != null)
            status += " · " + Texts.T(_thread.Summary.SourceState switch
            {
                ThreadSourceState.Archived => "archive.sourceArchived",
                ThreadSourceState.NotFound => "archive.sourceMissing",
                _ => "archive.sourceActive"
            });
        Control<TextBlock>("ArchiveReaderStatus").Text = status;
        if (_thread != null)
        {
            ArchivePostRow[] posts = _thread.GetPosts(_postOffset, PageSize).Select(post =>
            {
                string text = ArchivePresentation.PlainText(post.Html);
                if (text.Length > 65536) text = text[..65536] + "\n\n" + Texts.T("archive.displayLimit");
                string header = "#" + post.PostId + (string.IsNullOrEmpty(post.Author) ? string.Empty : " · " + post.Author)
                    + (post.PostedAt.HasValue ? " · " + post.PostedAt.Value.ToLocalTime().ToString("g", Texts.Culture) : string.Empty)
                    + (post.IsDeleted ? " · " + Texts.T("archive.deletedPost") : string.Empty);
                return new ArchivePostRow(post.PostId, header, post.Subject ?? string.Empty, text,
                    Array.AsReadOnly((post.Attachments ?? Array.Empty<CapturedAttachment>()).Select(attachment => CreateAttachmentRow(attachment, post.PostId, _mediaReady.Task)).ToArray()))
                { Quotes = Array.AsReadOnly(ArchiveQuoteParser.Parse(_thread.Summary.Id, _thread.Summary.Url, post)
                    .Select(quote => new ArchiveQuoteRow(quote)).ToArray()) };
            }).ToArray();
            ListBox postList = Control<ListBox>("ArchivePosts");
            // A new page is a new index space. Do not carry its predecessor's
            // virtualized realization window and estimated item positions over.
            postList.ItemsSource = null;
            postList.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
            postList.ItemsSource = posts;
            if (!posts.Any(post => post.Id == _readerPostId)) _readerPostId = posts.FirstOrDefault()?.Id;
            UpdateReaderNavigation();
            RefreshInlineReplies(posts);
        }
        else { Control<ListBox>("ArchivePosts").ItemsSource = null; CancelInlineReplies(); }
        Control<TextBlock>("ArchiveNoPosts").IsVisible = _thread?.PostCount == 0;
        RenderMedia();
    }

    private void RenderMedia()
    {
        ConfigureArchiveMediaFavorites();
        ConfigureMediaRatings();
        DataGrid grid = Control<DataGrid>("ArchiveMediaGrid");
        ListBox tiles = Control<ListBox>("ArchiveMediaTiles");
        MediaPosition? position = CaptureMediaPosition();
        IReadOnlyList<ArchiveAssetRow> shown = VisibleAssets();
        _rebindingMedia = true;
        try { grid.ItemsSource = shown; tiles.ItemsSource = shown; }
        finally { _rebindingMedia = false; }
        _shownMedia = shown;
        _shownMediaThread = _selectedThread;
        if (shown.Count > 0)
        {
            _retainedMediaPosition = null;
            RestoreMediaPosition(position);
        }
        else
        {
            ++_mediaPositionRequest;
            _retainedMediaPosition = _mediaProjectionPending || _mediaLoading || _busy ? position : null;
        }
        TextBlock empty = Control<TextBlock>("ArchiveNoMedia");
        empty.Text = Texts.T(_mediaLoading ? "archive.loadingMedia"
            : _archiveFavoritesOnly ? "archive.noFavorites" : "archive.noMedia");
        empty.IsVisible = shown.Count == 0;
        UpdateActions();
    }

    private IReadOnlyList<ArchiveAssetRow> VisibleAssets()
    {
        IEnumerable<ArchiveAssetRow> rows = _allVersions ? _assets : _assets.Where(row => row.Asset.IsCurrent);
        if (_archiveFavoritesOnly) rows = rows.Where(row => row.IsFavorite?.Invoke() == true);
        if (_mediaRatingError == null) rows = rows.Where(row => MeowChan.Ratings.MediaRatingProjection.Matches(row.Rating, _mediaRatingFilter));
        IOrderedEnumerable<ArchiveAssetRow> ordered = _mediaSort switch
        {
            1 => rows.OrderBy(row => row.Asset.Length),
            2 => rows.OrderBy(row => row.Asset.Sha256, StringComparer.Ordinal),
            3 when _mediaRatingError == null => rows.OrderByDescending(row => MeowChan.Ratings.MediaRatingProjection.SortRank(row.Rating)).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
        };
        return ordered.ThenBy(row => MediaKey(_selectedThread, row).Directory, StringComparer.Ordinal)
            .ThenBy(row => row.Asset.AssetId, StringComparer.Ordinal).ToArray();
    }

    private void UpdateActions()
    {
        UpdateFeedbackVisuals();
        UpdateMediaRatingAction();
        UpdateArchiveSelectionActions();
        UpdateReaderNavigation();
        bool ready = _available && !_busy && !_ended;
        Control<Button>("SearchArchiveButton").IsEnabled = ready;
        UpdateSearchActions(ready);
        Control<Button>("ArchiveResetFilters").IsEnabled = ready;
        UpdateResultsExportAction(ready);
        Control<Button>("ExportArchiveButton").IsEnabled = ready && _selectedThread != null && !_exportRunning;
        Control<Button>("ArchiveHistoryBack").IsVisible = _history.Count > 0;
        Control<Button>("RefreshArchiveButton").IsEnabled = ready;
        Control<Button>("PreviousThreadsButton").IsEnabled = ready && _listOffset > 0;
        Control<Button>("NextThreadsButton").IsEnabled = ready && _listOffset + PageSize < _listTotal;
        Control<Button>("PreviousPostsButton").IsEnabled = ready && _postOffset > 0;
        Control<Button>("NextPostsButton").IsEnabled = ready && _thread != null && _postOffset + PageSize < _thread.PostCount;
        Control<Grid>("ArchivePostNavigation").IsVisible = _thread?.PostCount > PageSize;
        Control<Button>("CapturePostsButton").IsEnabled = ready && _thread != null;
        Button capture = Control<Button>("CapturePostsButton");
        string captureAction = Texts.T(_thread?.Summary.CaptureEnabled == true ? "archive.captureOff" : "archive.captureOn");
        capture.Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5,
            Children =
            {
                CommandBindings.Icon("archive.capture-toggle", 16, _thread?.Summary.CaptureEnabled == true ? "capturing" : "stopped"),
                new TextBlock { Text = captureAction, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
            }
        };
        string captureTip = captureAction + " · " + Texts.T("archive.captureHint");
        ToolTip.SetTip(capture, captureTip);
        AutomationProperties.SetName(capture, captureAction);
        AutomationProperties.SetHelpText(capture, captureTip);
        Control<Button>("PreviewArchiveMediaButton").IsEnabled = ready && SelectedMediaRow() != null;
        Control<Button>("ArchiveOpenGallery").IsEnabled = ready && _selectedThread != null;
        Control<Button>("ArchiveReadPosts").IsEnabled = ready && _selectedThread != null
            && FrozenArchiveSelection().Length == 1;
        Control<Button>("ArchiveResultsGallery").IsEnabled = ready && _listTotal > 0;
        Control<TextBlock>("ArchiveThreadPage").Text = PageLabel(_listOffset, _listTotal);
        Control<TextBlock>("ArchivePostPage").Text = PageLabel(_postOffset, _thread?.PostCount ?? 0);
        RefreshArchiveResultActions();
    }
    private static string PageLabel(int offset, int total) => Texts.T("archive.page", total == 0 ? 0 : offset + 1, Math.Min(offset + PageSize, total), total);

    private async void OnCaptureClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || !_available || _thread == null || _setCapture == null) return;
        ResourceId id = _thread.Summary.Id;
        bool enabled = !_thread.Summary.CaptureEnabled;
        CancelReferences();
        CancelInlineReplies();
        int request = ++_contentRequest;
        _busy = true;
        ClearOperationError("action");
        UpdateActions();
        try { await _setCapture(id, enabled); }
        catch (Exception exception) { if (!_ended && request == _contentRequest) Message(exception.Message, true); return; }
        finally { if (!_ended && request == _contentRequest) { _busy = false; UpdateActions(); } }
        if (!_ended && request == _contentRequest && _selectedThread == id) await LoadThreadAsync(id, resetPage: false);
    }

    private ArchiveAttachmentRow CreateAttachmentRow(CapturedAttachment attachment, string postId,
        Task<IReadOnlyList<ArchiveAssetRow>> ready)
    {
        bool large = _largeReader;
        return new ArchiveAttachmentRow(attachment)
        {
            LargePreview = large,
            LoadThumbnail = async cancellationToken =>
            {
                using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                IReadOnlyList<ArchiveAssetRow> assets = await ready.WaitAsync(loading.Token);
                ArchiveAssetRow? match = FindMatchingAsset(attachment, assets);
                if (match == null) throw new InvalidDataException(Texts.T(assets.Any(asset =>
                    asset.Asset.MediaIdentifier == attachment.MediaId && asset.Asset.Variant == attachment.Variant)
                    ? "archive.differentVersion" : "archive.notSaved"));
                return await Task.Run(async () =>
                {
                    await _thumbnailGate.WaitAsync(loading.Token).ConfigureAwait(false);
                    try
                    {
                        if (SavedMediaTypes.Classify(match.Asset.OriginalFileName ?? match.Asset.Path) == SavedMediaKind.Video)
                            return await VideoPosterCache.LoadAsync(match.Manifest, match.Asset,
                                Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1"),
                                large ? 1040 : 240, large ? 720 : 180, loading.Token).ConfigureAwait(false);
                        return await DecodeThumbnailAsync(match, loading.Token, large ? 1040 : 240, large ? 720 : 180).ConfigureAwait(false);
                    }
                    finally { _thumbnailGate.Release(); }
                }, loading.Token);
            },
            OpenPreview = async () =>
            {
                if (_ended || _busy || !_available || !ready.IsCompletedSuccessfully) return;
                ArchiveAssetRow? match = FindMatchingAsset(attachment, ready.Result);
                if (match != null && _selectedThread != null && OpenGallery != null)
                    await OpenGallery(_selectedThread, new GalleryEntryKey(
                        new GalleryAssetKey(_selectedThread, match.Manifest.Directory, match.Asset.AssetId),
                        postId, attachment.Position), this);
                else Message(Texts.T("archive.notSaved"));
            }
        };
    }

    // A list thumbnail starts only when ArchiveAttachmentView enters the viewport. It uses
    // the captured local path and never opens a thread view, manifest, or media directory.
    private ArchiveAttachmentRow CreateListAttachmentRow(ArchiveThreadSummary summary, CapturedAttachment attachment)
        => new(attachment)
        {
            LoadThumbnail = token => LoadListThumbnailAsync(summary, attachment, token),
            OpenPreview = async () =>
            {
                if (OpenGallery != null) await OpenGallery(summary.Id, null, this);
            }
        };

    private Task<Bitmap?> LoadListThumbnailAsync(ArchiveThreadSummary summary,
        CapturedAttachment attachment, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        string? path = attachment.PlannedPath;
        if (attachment.IsDeleted || string.IsNullOrEmpty(path) ||
            SavedMediaTypes.Classify(attachment.OriginalFileName ?? path) != SavedMediaKind.Image)
            return null;
        string relative = Path.GetRelativePath(summary.MediaDirectory, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        // File opening, codec inspection and decoding are synchronous even for an
        // asynchronous FileStream. Keep the whole operation off the UI thread.
        await _thumbnailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 64L * 1024 * 1024) return null;
            using SKCodec? codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false));
            if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > 40_000_000) return null;
            int width = Math.Min(codec.Info.Width, Math.Min(160,
                Math.Max(1, (int)(120L * codec.Info.Width / codec.Info.Height))));
            stream.Position = 0;
            cancellationToken.ThrowIfCancellationRequested();
            Bitmap bitmap = Bitmap.DecodeToWidth(stream, width);
            if (cancellationToken.IsCancellationRequested) { bitmap.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
            return bitmap;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        finally { _thumbnailGate.Release(); }
    }, cancellationToken);

    internal static ArchiveAssetRow? FindMatchingAsset(CapturedAttachment attachment, IReadOnlyList<ArchiveAssetRow> assets)
        => assets.LastOrDefault(asset => attachment.MediaId != null
            && asset.Asset.MediaIdentifier == attachment.MediaId && asset.Asset.Variant == attachment.Variant
            && (attachment.ExpectedMd5 == null || attachment.ExpectedMd5 == asset.Asset.Md5)
            && (attachment.ExpectedSize == null || attachment.ExpectedSize == asset.Asset.Length));

    internal static async Task<Bitmap?> DecodeThumbnailAsync(ArchiveAssetRow row, CancellationToken cancellationToken, int maximumWidth = 240, int maximumHeight = 180)
    {
        string extension = System.IO.Path.GetExtension(row.Asset.Path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")) return null;
        if (row.Asset.Length > 64L * 1024 * 1024) throw new InvalidDataException(Texts.T("archive.tooLarge"));
        await using Stream stream = await row.Manifest.OpenVerifiedReadAsync(row.Asset.AssetId, cancellationToken).ConfigureAwait(false);
        using SKCodec? codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false));
        if (codec == null) throw new InvalidDataException(Texts.T("archive.invalidImage"));
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 40_000_000
            || (long)codec.Info.Height > 180L * codec.Info.Width)
            throw new InvalidDataException(Texts.T("archive.tooLarge"));
        int width = Math.Min(codec.Info.Width, Math.Min(maximumWidth, Math.Max(1, (int)((long)maximumHeight * codec.Info.Width / codec.Info.Height))));
        stream.Position = 0;
        cancellationToken.ThrowIfCancellationRequested();
        Bitmap bitmap = Bitmap.DecodeToWidth(stream, width);
        if (cancellationToken.IsCancellationRequested) { bitmap.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return bitmap;
    }

    private async void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || !_available || SelectedMediaRow() is not ArchiveAssetRow row) return;
        await OpenAssetGalleryAsync(row, sender as Control);
    }

    private async Task ShowPreviewAsync(ArchiveAssetRow row)
    {
        ClosePreview();
        int request = _previewRequest;
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCancellation = loading;
        CancellationToken cancellationToken = loading.Token;
        _previewPath = row.Asset.Path;
        Control<Border>("ArchivePreview").IsVisible = true;
        Control<TextBlock>("PreviewTitle").Text = row.Name;
        Control<TextBox>("ArchivePreviewPath").Text = row.Asset.Path;
        Control<TextBlock>("ArchivePreviewMessage").Text = Texts.T("archive.loading");
        try
        {
            Bitmap? bitmap = await Task.Run(async () =>
            {
                string extension = System.IO.Path.GetExtension(row.Asset.Path).ToLowerInvariant();
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")) return null;
                if (row.Asset.Length > 64L * 1024 * 1024) throw new InvalidDataException(Texts.T("archive.tooLarge"));
                await using Stream stream = await row.Manifest.OpenVerifiedReadAsync(row.Asset.AssetId, cancellationToken).ConfigureAwait(false);
                using (SKCodec? codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false)))
                {
                    if (codec == null) throw new InvalidDataException(Texts.T("archive.invalidImage"));
                    if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 80_000_000)
                        throw new InvalidDataException(Texts.T("archive.tooLarge"));
                    int width = Math.Min(codec.Info.Width, Math.Min(1200, Math.Max(1, (int)(1200L * codec.Info.Width / codec.Info.Height))));
                    stream.Position = 0;
                    return Bitmap.DecodeToWidth(stream, width);
                }
            }, cancellationToken);
            if (_ended || request != _previewRequest || !Control<Border>("ArchivePreview").IsVisible) { bitmap?.Dispose(); return; }
            _preview = bitmap;
            Control<Image>("ArchivePreviewImage").Source = bitmap;
            Control<TextBlock>("ArchivePreviewMessage").Text = bitmap == null ? Texts.T("archive.previewUnavailable") : string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _previewRequest) Control<TextBlock>("ArchivePreviewMessage").Text = exception.Message; }
        finally { if (ReferenceEquals(_previewCancellation, loading)) _previewCancellation = null; }
    }

    private void ClosePreview()
    {
        ++_previewRequest;
        _previewCancellation?.Cancel();
        Control<Border>("ArchivePreview").IsVisible = false;
        Control<Image>("ArchivePreviewImage").Source = null;
        _preview?.Dispose();
        _preview = null;
        _previewPath = null;
        Control<TextBox>("ArchivePreviewPath").Text = string.Empty;
        Control<TextBlock>("ArchivePreviewMessage").Text = string.Empty;
    }
    private void OnClosePreview(object? sender, RoutedEventArgs e) => ClosePreview();
    private void OnOpenMediaFolder(object? sender, RoutedEventArgs e)
    {
        if (!_ended && _previewPath is string path && System.IO.Path.GetDirectoryName(path) is string directory) _openFolder?.Invoke(directory);
    }
    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await ActivateAsync();
    private async void OnPreviousThreads(object? sender, RoutedEventArgs e) => await NavigateResultsPage(_listOffset / PageSize);
    private async void OnNextThreads(object? sender, RoutedEventArgs e) => await NavigateResultsPage(_listOffset / PageSize + 2);
    private void OnPreviousPosts(object? sender, RoutedEventArgs e)
    {
        if (_busy || _postOffset == 0) return;
        ++_readerInteractionRevision;
        _postOffset = Math.Max(0, _postOffset - PageSize);
        ++_readerFocusRequest;
        _readerPostId = null;
        RenderThread();
        QueueReadingPositionCapture(DispatcherPriority.Loaded);
    }
    private void OnNextPosts(object? sender, RoutedEventArgs e)
    {
        if (_busy || _thread == null || _postOffset + PageSize >= _thread.PostCount) return;
        ++_readerInteractionRevision;
        _postOffset += PageSize;
        ++_readerFocusRequest;
        _readerPostId = null;
        RenderThread();
        QueueReadingPositionCapture(DispatcherPriority.Loaded);
    }
    private void OnBackClick(object? sender, RoutedEventArgs e) { _showReader = false; UpdateLayoutMode(); }
    private void OnMediaSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_rebindingMedia) return;
        if (!_applyingMediaRestore && !IsPendingMediaSelectionEcho(sender)) ++_mediaPositionRequest;
        if (sender is DataGrid && Control<DataGrid>("ArchiveMediaGrid").SelectedItem is ArchiveAssetRow gridRow)
            Control<ListBox>("ArchiveMediaTiles").SelectedItem = gridRow;
        if (sender is ListBox && Control<ListBox>("ArchiveMediaTiles").SelectedItem is ArchiveAssetRow tileRow)
            Control<DataGrid>("ArchiveMediaGrid").SelectedItem = tileRow;
        UpdateActions();
    }
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateLayoutMode();
        Control<ScrollViewer>("ArchiveSearchPanelScroll").MaxHeight = Math.Max(120, Bounds.Height * .55);
    }
    private bool CanShowArchivePreviewBesideList => Bounds.Width >= (_archiveLayout == "Table" ? 1450 : 1000);
    private void OnArchiveFavoritesFilterClick(object? sender, RoutedEventArgs e)
    {
        _archiveFavoritesOnly = !_archiveFavoritesOnly;
        Control<Button>("ArchiveFavoritesFilter").Content = Texts.T(
            _archiveFavoritesOnly ? "archive.allMedia" : "archive.favoritesOnly");
        RenderMedia();
    }
    private async void OnArchiveLayoutClick(object? sender, RoutedEventArgs e)
    {
        string layout = sender == Control<Button>("ArchiveLayoutTable") ? "Table" : "List";
        if (_archiveLayout == layout) return;
        ArchiveLayoutPreference = layout;
        RestoreArchiveSelection(ArchiveVisibleRows);
        await PersistArchiveViewAsync();
    }
    private async void OnArchiveSplitReleased(object? sender, PointerReleasedEventArgs e) => await SaveArchiveSplitAsync();
    private async void OnArchiveSplitKeyUp(object? sender, KeyEventArgs e) => await SaveArchiveSplitAsync();
    private async Task SaveArchiveSplitAsync()
    {
        if (Control<Grid>("ArchiveSplit").ColumnDefinitions[0].ActualWidth <= 0) return;
        _archiveSplitWidth = Math.Clamp(Control<Grid>("ArchiveSplit").ColumnDefinitions[0].ActualWidth, 180, 1200);
        await PersistArchiveViewAsync();
    }
    private async Task PersistArchiveViewAsync()
    {
        if (SaveViewPreferencesAsync == null) return;
        try { await SaveViewPreferencesAsync(_archiveLayout, _archiveSplitWidth); }
        catch (Exception exception) { if (!_ended) Message(exception.Message, true); }
    }
    private async void OnFavoritesOverviewClick(object? sender, RoutedEventArgs e) => await OpenFavoritesOverviewAsync();
    private void UpdateLayoutMode()
    {
        if (!IsInitialized && !_largeReader) return;
        bool narrow = !CanShowArchivePreviewBesideList;
        bool compact = Bounds.Height > 0 && Bounds.Height < 500;
        Control<Grid>("ArchivePageHeader").IsVisible = !(_showReader && narrow);
        Control<Grid>("ArchiveRoot").Margin = new Thickness(compact ? 8 : 12);
        Control<Grid>("ArchiveRoot").RowSpacing = compact ? 6 : 8;
        Control<TextBlock>("ArchiveSubtitle").IsVisible = Bounds.Height >= 650;
        Control<TextBlock>("ArchivePartialHint").IsVisible = !compact;
        ToolTip.SetTip(Control<TextBlock>("ArchiveReaderStatus"), Texts.T("archive.partial"));
        Grid split = Control<Grid>("ArchiveSplit");
        // Keep the wide split stable before the first selection. Reflowing the list
        // after a click moves rows under the pointer and breaks rapid Ctrl selection.
        bool beside = !narrow;
        // Retain both tracks across size changes; replacing the definitions
        // during layout can invalidate Avalonia's in-progress measure ranges.
        split.ColumnDefinitions[0].Width = new GridLength(beside
            ? (_archiveLayout == "Table" ? Math.Max(_archiveColumnMode == "Advanced" ? 900 : 650, _archiveSplitWidth) : _archiveSplitWidth) : 0);
        split.ColumnDefinitions[1].Width = new GridLength(beside ? 6 : 0);
        split.ColumnSpacing = beside ? 6 : 0;
        Grid.SetColumn(Control<Grid>("ArchiveListPane"), beside ? 0 : 2);
        Control<GridSplitter>("ArchiveSplitter").IsVisible = beside;
        Control<Grid>("ArchiveReaderPane").IsVisible = _largeReader || !narrow || _showReader;
        Control<Grid>("ArchiveListPane").IsVisible = !narrow || !_showReader;
        Control<ListBox>("ArchiveThreads").IsVisible = _archiveLayout == "List";
        Control<DataGrid>("ArchiveThreadTable").IsVisible = _archiveLayout == "Table";
        Control<Button>("ArchiveThreadColumnsButton").IsVisible = _archiveLayout == "Table";
        Control<Button>("ArchiveLayoutList").IsEnabled = _archiveLayout != "List";
        Control<Button>("ArchiveLayoutTable").IsEnabled = _archiveLayout != "Table";
        Control<Button>("ArchiveBackButton").IsVisible = narrow && !_largeReader;
        RefreshArchiveResultActions();
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_ended) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        // Refresh computed labels without losing the chosen manifest version or thread.
        // Restoring a post anchor selects the Posts tab; preserve other active reader views.
        ReaderAnchor? anchor = ReaderShowsPosts ? CaptureReaderAnchor() : null;
        _assets = Array.AsReadOnly(_assets.Select(row => new ArchiveAssetRow(row.Manifest, row.Asset)).ToArray());
        Localize();
        LocalizeReaderUpdates();
        RenderThread();
        if (anchor != null) _ = RestoreReaderAnchorAsync(anchor);
        ListBox list = Control<ListBox>("ArchiveThreads");
        if (list.ItemsSource is IEnumerable<ArchiveThreadRow> rows)
        {
            ArchiveThreadRow[] localized = rows.Select(row => new ArchiveThreadRow(row.Summary, row.Work)
            { PreviewText = row.PreviewText, PreviewAttachment = row.PreviewAttachment }).ToArray();
            _updatingList = true;
            try
            {
                list.ItemsSource = localized;
                Control<DataGrid>("ArchiveThreadTable").ItemsSource = localized;
                RestoreArchiveSelection(localized, _selectedThread);
            }
            finally { _updatingList = false; }
        }
    }
    private void Localize()
    {
        foreach (Control control in this.GetLogicalDescendants().Concat(Control<Grid>("ArchiveReaderPane").GetLogicalDescendants()).OfType<Control>().Distinct())
        {
            if (control.Tag is not string key || !control.Classes.Contains("localized")
                || control.Classes.Contains("command-icon")) continue;
            if (control is TextBlock text) text.Text = Texts.T(key);
            if (control is Button button) button.Content = Texts.T(key);
        }
        LocalizeExport();
        LocalizeSearch();
        UpdateArchiveEmptyState();
        Control<Button>("ArchiveLayoutList").Content = Texts.T("gallery.list");
        Control<Button>("ArchiveLayoutTable").Content = Texts.T("gallery.table");
        RefreshArchiveResultActions();
        LocalizeArchiveColumns();
        CommandBindings.Apply(Control<Button>("RefreshArchiveButton"), "archive.refresh", iconOnly: true);
        CommandBindings.Apply(Control<Button>("SearchArchiveButton"), "archive.search", iconOnly: true);
        CommandBindings.Apply(Control<Button>("ArchiveBackButton"), "archive.back-to-list");
        CommandBindings.Apply(Control<Button>("PreviousThreadsButton"), "archive.previous-page", iconOnly: true);
        CommandBindings.Apply(Control<Button>("NextThreadsButton"), "archive.next-page", iconOnly: true);
        CommandBindings.Apply(Control<Button>("PreviousPostsButton"), "pagination.previous", iconOnly: true);
        CommandBindings.Apply(Control<Button>("NextPostsButton"), "pagination.next", iconOnly: true);
        CommandBindings.Apply(Control<Button>("PreviousReferences"), "pagination.previous", iconOnly: true);
        CommandBindings.Apply(Control<Button>("NextReferences"), "pagination.next", iconOnly: true);
        CommandBindings.Apply(Control<Button>("ArchiveHistoryBack"), "reader.history-back", iconOnly: true);
        CommandBindings.Apply(Control<Button>("ArchiveReaderFullscreen"), "window.fullscreen", iconWithLabel: true);
        CommandBindings.Apply(Control<Button>("ArchiveCloseReader"), "reader.close", iconWithLabel: true);
        CommandBindings.Apply(Control<Button>("ArchiveOpenGallery"), "gallery.open", iconWithLabel: true);
        CommandBindings.Apply(Control<Button>("ArchiveReadPosts"), "reader.expand", iconWithLabel: true);
        CommandBindings.Apply(Control<Button>("ArchiveGalleryReturn"), "gallery.return", iconOnly: true);
        CommandBindings.Apply(Control<Button>("ExportArchiveButton"), "export.open-html-dialog", iconWithLabel: true);
        CommandBindings.Apply(Control<Button>("ReaderPreviousThread"), "reader.previous-thread", iconOnly: true);
        CommandBindings.Apply(Control<Button>("ReaderPreviousPost"), "reader.previous-post", iconOnly: true);
        CommandBindings.Apply(Control<Button>("ReaderNextPost"), "reader.next-post", iconOnly: true);
        CommandBindings.Apply(Control<Button>("ReaderNextThread"), "reader.next-thread", iconOnly: true);
        CommandBindings.Apply(Control<Button>("ArchiveResultsGallery"), "gallery.open-results", iconWithLabel: true);
        CommandBindings.Apply(Control<Button>("ArchiveFavoritesOverview"), "favorites.open", iconWithLabel: true);
        Control<TabItem>("ArchiveReferencesTab").Header = Texts.T("archive.references");
        Control<TabItem>("ArchivePostsTab").Header = Texts.T("archive.posts");
        Control<TabItem>("ArchiveMediaTab").Header = Texts.T("archive.media");
        Control<Button>("ArchiveMediaMode").Content = Texts.T(_mediaTiles ? "gallery.list" : "gallery.tiles");
        Control<Button>("ArchiveFavoritesFilter").Content = Texts.T(
            _archiveFavoritesOnly ? "archive.allMedia" : "archive.favoritesOnly");
        // Replacing translated labels clears selection synchronously; it is not a new user choice.
        _localizingMediaChoices = true;
        try
        {
            Control<ComboBox>("ArchiveVersionScope").ItemsSource = new[] { Texts.T("gallery.latestVersions"), Texts.T("gallery.allVersions") };
            Control<ComboBox>("ArchiveVersionScope").SelectedIndex = _allVersions ? 1 : 0;
            Control<ComboBox>("ArchiveMediaSort").ItemsSource = new[] { Texts.T("gallery.sortName"), Texts.T("gallery.sortSize"), Texts.T("gallery.sortVersion"), MediaRatingText.Get("Rating descending", "Bewertung absteigend") };
            Control<ComboBox>("ArchiveMediaSort").SelectedIndex = _mediaSort;
        }
        finally { _localizingMediaChoices = false; }
        LocalizeMediaRatings();
        if (this.FindControl<MenuItem>("ArchiveTableGalleryMenu") is MenuItem gallery) gallery.Header = Texts.T("gallery.open");
        if (this.FindControl<MenuItem>("ArchiveTableReadMenu") is MenuItem read) read.Header = Texts.T("gallery.read");
        if (this.FindControl<MenuItem>("ArchiveTableExportMenu") is MenuItem export) export.Header = Texts.T("gallery.export");
        DataGrid media = Control<DataGrid>("ArchiveMediaGrid");
        media.Columns[0].Header = Texts.T("archive.name"); media.Columns[1].Header = Texts.T("archive.size"); media.Columns[2].Header = Texts.T("archive.version");
        UpdateActions();
    }
    private void Message(string text, bool error = false) => OperationMessage("action", text, error);
    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;
    internal void EndLifetime()
    {
        if (_ended) return;
        _ended = true;
        ClearPaginationInput();
        _runningList = null; _retryList = null;
        SearchStateChanged?.Invoke();
        InvalidateSuggestions();
        Control<IdentitySuggestionBox>("ArchiveSourceFilter").EndSuggestions();
        Control<IdentitySuggestionBox>("ArchiveBoardFilter").EndSuggestions();
        _readerUpdateTimer?.Stop();
        CancelInlineReplies();
        EndExportDialog();
        ++_contentRequest;
        CancelMediaLoad();
        UpdateFeedbackVisuals();
        CancelReferences();
        _exportCancellation?.Cancel();
        _lifetime.Cancel();
        Texts.Changed -= OnLanguageChanged;
        ClosePreview();
    }
}

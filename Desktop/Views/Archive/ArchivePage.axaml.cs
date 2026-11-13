using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;
using MeowChan.Diagnostics;
using MeowChan.Desktop.WorkViews;

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
        set
        {
            bool updating = _updatingList;
            _updatingList = true;
            try
            {
                _archiveLayout = value == "Table" ? "Table" : "List";
                _threadTable = _archiveLayout == "Table";
                UpdateLayoutMode();
                // DataGrid initializes/reconciles its selection when first shown.
                // Finish that layout before restoring the identity-owned selection.
                if (this.IsAttachedToVisualTree()) UpdateLayout();
                RestoreArchiveSelection(ArchiveVisibleRows, _selectedThread);
            }
            finally { _updatingList = updating; }
        }
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
                ArchiveMediaGrid.IsVisible = !value;
                ArchiveMediaTiles.IsVisible = value;
                ArchiveMediaColumnsButton.IsVisible = !value;
                ArchiveMediaMode.Content = Texts.T(value ? "gallery.list" : "gallery.tiles");
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
        InitializeComponent(); InitializeThreadFind(); InitializeUnifiedSearch(); InitializePostPageKeys(); InitializeTransferPreview(); InitializeArchiveResultActions(); InitializeSuggestions(); InitializeSearchEditors(); InitializeArchiveSelection(); InitializeArchiveMediaCommands(); InitializeRatingCommands(); InitializeMediaPositionInput(); InitializeMediaSelection(); InitializeThreadFavoriteCommands(); InitializePrivateTags(); InitializeStatusOverlay(); InitializeReaderToolbarIcons();
        // Navigation scrolls explicitly; selection must not re-anchor while media is loading.
        ArchivePosts.AutoScrollToSelectedItem = false;
        ArchiveMediaTiles.AutoScrollToSelectedItem = false;
        InitializeReaderInteraction(); InitializeQuotePreview(); InitializeUnreadLine(); InitializeReaderUpdates(); InitializeReadingPositionInput(); InitializePostBookmarks();
        InitializePaginationFocus();
        InitializeArchivePresentation();
        InitializeFilterSurface();
        InitializeArchiveWorkView();
        InitializeArchiveMediaCounts();
        InitializeReaderDisplay();
        InitializeReaderView(); AttachPostFilter(); AttachConversationView();
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
        ArchiveMediaGrid.IsVisible = false;
        ArchiveMediaTiles.IsVisible = true;
        ArchiveMediaColumnsButton.IsVisible = false;
        ArchiveVersionScope.SelectedIndex = 1;
    }
    internal bool IsAvailable
    {
        set
        {
            if (!value) InvalidateFilenameSearch();
            if (_available != value)
            {
                CancelReaderMediaActivation();
                CancelPendingGalleryOpen();
                if (!value) _exportCancellation?.Cancel();
            }
            bool becameAvailable = value && !_available;
            _available = value; UpdateActions();
            // An open dialog must recover its fields/actions with readiness, without reopening it.
            UpdateExportActions();
            // A list read before the runtime existed shows unknown work status; refresh the same applied
            // request once, without touching the draft. A busy page picks it up with its next load.
            if (becameAvailable && _listReadWithoutRuntime && !_busy && !_ended) _ = LoadListAsync();
        }
    }
    // Catalog reads may begin before the session is ready (startup). This is never a mutation gate:
    // every write, export, gallery and engine action stays bound to _available.
    private bool _readable;
    private bool _listReadWithoutRuntime;
    private bool CanRead => _available || _readable;
    internal bool IsReadable { set { if (_readable == value) return; _readable = value; UpdateActions(); } }
    internal bool IsBusy => _busy;
    internal bool IsMediaLoading => _mediaLoading;
    internal Task MediaLoadTask { get; private set; } = Task.CompletedTask;
    internal ArchivedThreadView? CurrentThread => _thread;

    internal async Task ActivateAsync()
    {
        if (_ended || IsFilenameSearchOpen || !CanRead || _openReader == null || _busy) return;
        InvalidateSuggestions();
        ClosePreview();
        await LoadListAsync();
    }

    private async Task<bool> LoadListAsync(ListLoad? pending = null, CancellationToken applicationToken = default)
    {
        InvalidateFilenameSearch();
        if (_ended || !CanRead || _openReader == null || _busy || applicationToken.IsCancellationRequested) return false;
        if (pending == null)
        {
            // A plain refresh re-reads tag membership, as it re-reads work status. If a filtered tag is
            // gone, refuse visibly and keep the shown result instead of widening it.
            try { pending = NewListLoad(RefreshTagMembership(_appliedSearch ?? ArchiveSearchRequest.Create(new ArchiveSearchSpec())), _listOffset); }
            catch (InvalidOperationException exception) { ShowTagFilterError(exception.Message); return false; }
        }
        // Work filters need the runtime. Without it every thread would be excluded and read as "0 results";
        // refuse locally instead, keeping the draft and the current result untouched.
        if (!_available && pending.Request.Spec.WorkFilter != MeowChan.Archive.ArchiveWorkFilter.All)
        {
            SearchEditorError(Texts.T("archive.workStatusLoading"));
            return false;
        }
        // Everything needed for membership and SQL is captured before opening the reader.
        _runningList = pending;
        CancelArchiveMediaCounts();
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
                        ? CreateListAttachmentRow(summary, preview.FirstPostId, attachment) : null
                };
            }).ToArray();
            if (_ended || token.IsCancellationRequested || generation != _contentRequest) return false;
            ConfigureThreadFavoriteRows(rows);
            ConfigurePrivateTagRows(rows);
            _updatingList = true;
            try
            {
                _appliedSearch = pending.Request; _query = pending.Request.Spec; _appliedWork = pending.Work;
                _workFilter = workFilter; _listOffset = offset; _listTotal = result.TotalCount;
                ListBox list = ArchiveThreads;
                DataGrid table = ArchiveThreadTable;
                list.ItemsSource = rows; table.ItemsSource = rows;
                // Reconcile the replacement collection while selection events are suppressed,
                // before restoring identity-owned selection (as on layout/language changes).
                UpdateLayout();
                list.SelectedItem = rows.FirstOrDefault(row => row.Summary.Id == selected);
                table.SelectedItem = rows.FirstOrDefault(row => row.Summary.Id == selected);
                RestoreArchiveSelection(rows, selected);
                _listLoaded = true; _listReadWithoutRuntime = !_available;
                _archiveMediaCountDatabasePath = reader.DatabasePath;
                pending.AfterCommit?.Invoke();
                ClearTagFilterError();
                UpdateAppliedFilters(); UpdateArchiveEmptyState();
                committed = true;
            }
            finally { _updatingList = false; }
            Message(string.Empty);
            AppliedSearchChanged?.Invoke();
            BeginArchiveMediaCounts(rows);
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
    internal Task LoadThreadAsync(ResourceId id, bool resetPage = true, ArchivedThreadView? preparedThread = null) =>
        LoadThreadAsync(id, resetPage, preparedThread, null);

    private async Task LoadThreadAsync(ResourceId id, bool resetPage, ArchivedThreadView? preparedThread,
        ReaderFocusIntent? focusIntent, FilenameSearchOwner? filenameNavigation = null)
    {
        if (!ReferenceEquals(_filenameSearchOwner, filenameNavigation)) InvalidateFilenameSearch();
        if (_ended || !CanRead || _openReader == null) return;
        if (resetPage || _unreadMark?.Thread != id) _unreadMark = null;
        ReaderAnchor? retainedAnchor = !resetPage && _thread?.Summary.Id == id ? CaptureReaderAnchor() : null;
        CancelReferences();
        CancelInlineReplies();
        int request = ++_contentRequest;
        focusIntent?.BeginContent(request);
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
        _mediaMenuPresenter.Invalidate();
        CloseThreadFind();
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
            CloseThreadFind();
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
                if (retainedAnchor != null) await RestoreReaderAnchorAsync(retainedAnchor, focusIntent: focusIntent);
                else if (filenameNavigation == null && _largeReader && thread.PostCount > 0)
                {
                    // The first post of the shown page; a view still being prepared is waited for (the identity is not).
                    await AwaitProjectionAsync();
                    if (_ended || request != _contentRequest) return;
                    if (Projection is { Count: > 0 } shown)
                        await FocusPostAsync(shown[Math.Min(_postOffset, shown.Count - 1)].Post.PostId,
                            resetPage ? null : _readerInteractionRevision, focusIntent: focusIntent);
                    else if (focusIntent != null) focusIntent.Restore(ArchiveCloseReader);
                    else ArchiveCloseReader.Focus();
                }
                else if (filenameNavigation == null && _largeReader)
                {
                    if (focusIntent != null) focusIntent.Restore(ArchiveCloseReader);
                    else ArchiveCloseReader.Focus();
                }
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
        CancelReaderMediaActivation();
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
        CloseQuotePreview();
        ReaderFindOpen.IsEnabled = !_ended && !_busy && _thread != null;
        ArchiveSelectHint.IsVisible = _thread == null && !_busy;
        ArchiveContentTabs.IsVisible = _thread != null;
        ArchiveReaderTitle.Text = _thread?.Summary.Title ?? string.Empty;
        string status = _thread?.Summary.LastCapturedAt is DateTimeOffset captured
            ? Texts.Count("archive.captured", _thread.PostCount, captured.ToLocalTime().ToString("g", Texts.Culture), _thread.PostCount)
            : _thread != null ? Texts.T("archive.mediaOnly") : string.Empty;
        if (_thread != null)
            status += " · " + Texts.T(_thread.Summary.SourceState switch
            {
                ThreadSourceState.Archived => "archive.sourceArchived",
                ThreadSourceState.NotFound => "archive.sourceMissing",
                _ => "archive.sourceActive"
            });
        ReaderPostProjection? projection = _thread != null ? ResolveProjection(out _) : null;
        ArchiveReaderStatus.Text = status + ReaderViewStatus(projection);
        if (_thread != null)
        {
            string? openingPost = ArchivePresentation.OpeningPostId(_thread);
            (string? unreadFirst, int unreadCount) = UnreadBoundary();
            // The reading-mark line sits on the first shown post at or after the first new one; its count stays the thread's.
            string? unreadCarrier = null;
            if (unreadFirst != null && projection != null && projection.NearestPosition(unreadFirst) is >= 0 and var carrier
                && projection[carrier].ThreadIndex >= _thread.FindPostIndex(unreadFirst))
                unreadCarrier = projection[carrier].Post.PostId;
            _unreadFirstNewId = unreadCarrier;
            int pageSize = ReaderPageSize;
            if (projection != null)
            {
                if (_projectionPagePost is { } pagePost)
                {
                    int position = projection.NearestPosition(pagePost);
                    _postOffset = position >= 0 ? position / pageSize * pageSize : 0;
                    _projectionPagePost = null;
                }
                else if (_postOffset > 0 && _postOffset >= projection.Count) _postOffset = Math.Max(0, (projection.Count - 1) / pageSize * pageSize);
            }
            Dictionary<string, bool> capturedIds = new(StringComparer.Ordinal);
            ArchivedThreadView current = _thread;
            bool IsDead(ArchiveQuote quote) => quote.Target is { } target && target.Thread == current.Summary.Id
                && !(capturedIds.TryGetValue(target.PostId, out bool captured) ? captured
                    : capturedIds[target.PostId] = current.FindPostIndex(target.PostId) >= 0);
            DateTimeOffset? openingPostAt = openingPost == null
                ? null : current.Posts.FirstOrDefault(candidate => candidate.PostId == openingPost)?.PostedAt;
            PostRowContext context = new(current, openingPost, openingPostAt, DateTimeOffset.Now, unreadCarrier, unreadCount,
                _readerView.HideMedia, projection?.IsOrdered == true, IsDead);
            ArchivePostRow[] posts = projection == null ? Array.Empty<ArchivePostRow>()
                : projection.Page(_postOffset, pageSize).Select(entry => BuildPostRow(entry, context)).ToArray();
            ListBox postList = ArchivePosts;
            // A new page is a new index space. Do not carry its predecessor's
            // virtualized realization window and estimated item positions over.
            postList.ItemsSource = null;
            postList.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
            postList.ItemsSource = posts;
            if (!posts.Any(post => post.Id == _readerPostId)) _readerPostId = posts.FirstOrDefault()?.Id;
            UpdateReaderNavigation();
            RefreshInlineReplies(posts);
        }
        else { ArchivePosts.ItemsSource = null; CancelInlineReplies(); _unreadFirstNewId = null; }
        // No captured text, a view still being prepared, or a view nothing passes.
        string? noPosts = _thread == null ? null : _thread.PostCount == 0 ? "archive.noPosts"
            : projection == null ? "reader.view.preparing" : projection.Count == 0 ? "reader.view.empty" : null;
        TextBlock noPostsText = ArchiveNoPosts;
        noPostsText.IsVisible = noPosts != null;
        if (noPosts != null && !Equals(noPostsText.Tag, noPosts)) { noPostsText.Tag = noPosts; noPostsText.Text = Texts.T(noPosts); }
        Dispatcher.UIThread.Post(UpdateUnreadReturn, DispatcherPriority.Background);
        RenderMedia();
        AnnounceProjection();
    }

    private readonly record struct PostRowContext(ArchivedThreadView Thread, string? OpeningPost,
        DateTimeOffset? OpeningPostAt, DateTimeOffset RenderedAt, string? UnreadCarrier, int UnreadCount,
        bool HideMedia, bool SuppressReplyToggle, Func<ArchiveQuote, bool> IsDead);

    // One immutable row of a page. A placeholder keeps no attachments, so it never offers or loads media.
    private ArchivePostRow BuildPostRow(ReaderPostEntry entry, PostRowContext context)
    {
        CapturedPost post = entry.Post;
        ArchivedThreadView thread = context.Thread;
        ResourceId threadId = thread.Summary.Id;
        (string text, IReadOnlyList<ArchiveTextMark> marks) = ArchiveText.PlainTextWithMarks(post.Html);
        if (text.Length > 65536) text = text[..65536] + "\n\n" + Texts.T("archive.displayLimit");
        ReaderPostVerdict verdict = entry.Verdict;
        bool stub = verdict.Visibility == ReaderPostVisibility.Stub;
        IReadOnlyList<ArchiveAttachmentRow> attachments = stub ? Array.Empty<ArchiveAttachmentRow>()
            : Array.AsReadOnly((post.Attachments ?? Array.Empty<CapturedAttachment>()).Select(attachment => CreateAttachmentRow(threadId, attachment, post.PostId, _mediaReady.Task)).ToArray());
        return new ArchivePostRow(post.PostId, post.Subject ?? string.Empty, text, attachments)
        {
            Author = post.Author ?? string.Empty,
            Posted = ReaderDateText.Posted(post.PostedAt, this.ReaderDateStyle, context.OpeningPostAt, context.RenderedAt),
            PostedFull = ReaderDateText.PostedFull(post.PostedAt),
            IsDeleted = post.IsDeleted,
            IsOpeningPost = post.PostId == context.OpeningPost,
            UnreadCount = post.PostId == context.UnreadCarrier ? context.UnreadCount : 0,
            Marks = marks,
            StartExpanded = _expandedPosts.Contains((threadId, post.PostId)),
            ExpandedChanged = open => SetPostExpanded(threadId, post.PostId, open),
            Quotes = Array.AsReadOnly(ArchiveQuoteParser.Parse(threadId, thread.Summary.Url, post)
                .Select(quote => new ArchiveQuoteRow(quote) { IsDead = context.IsDead(quote) }).ToArray()),
            ThreadIndex = entry.ThreadIndex,
            Depth = entry.Depth,
            ParentId = entry.ParentIndex >= 0 && entry.ParentIndex < thread.PostCount ? thread.Posts[entry.ParentIndex].PostId : null,
            IsStub = stub,
            StubText = stub ? ReaderStubText(post.PostId, verdict.Reason) : string.Empty,
            IsHighlighted = (verdict.Marks & ReaderPostMarks.Highlight) != 0,
            IsRevealed = (verdict.Marks & ReaderPostMarks.Revealed) != 0,
            MediaHidden = context.HideMedia && attachments.Count > 0,
            SuppressReplyToggle = context.SuppressReplyToggle
        };
    }

    private void RenderMedia()
    {
        ConfigureArchiveMediaFavorites();
        ConfigureMediaRatings();
        DataGrid grid = ArchiveMediaGrid;
        ListBox tiles = ArchiveMediaTiles;
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
        TextBlock empty = ArchiveNoMedia;
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
        UpdateFilenameSearchActions();
        UpdateFeedbackVisuals();
        UpdateMediaRatingAction();
        UpdateMediaSelectionBar();
        UpdateThreadFavoriteActions();
        UpdatePrivateTagActions();
        UpdateHashSearchAction();
        UpdateArchiveCleanupAction();
        UpdateTransferPreviewAction();
        UpdateArchiveSelectionActions();
        UpdateReaderNavigation();
        bool ready = _available && !_busy && !_ended && !IsFilenameSearchOpen;
        bool readReady = CanRead && !_busy && !_ended && !IsFilenameSearchOpen; // list/search/paging/reading only
        SearchArchiveButton.IsEnabled = ArchiveScope == MeowChan.Desktop.Controls.SearchScope.Text ? readReady : CanRunArchiveScope;
        UpdateSearchActions(readReady);
        RefreshArchiveSearchContext();
        ArchiveResetFilters.IsEnabled = readReady;
        UpdateResultsExportAction(ready);
        ExportArchiveButton.IsEnabled = ready && _selectedThread != null && !_exportRunning;
        ArchiveHistoryBack.IsVisible = _history.Count > 0;
        RefreshArchiveButton.IsEnabled = readReady;
        PreviousThreadsButton.IsEnabled = readReady && _listOffset > 0;
        NextThreadsButton.IsEnabled = readReady && _listOffset + PageSize < _listTotal;
        int shownPosts = ShownPostCount;
        PreviousPostsButton.IsEnabled = readReady && _postOffset > 0;
        NextPostsButton.IsEnabled = readReady && _thread != null && _postOffset + ReaderPageSize < shownPosts;
        ArchivePostNavigation.IsVisible = shownPosts > ReaderPageSize;
        CapturePostsButton.IsEnabled = ready && _thread != null;
        Button capture = CapturePostsButton;
        string captureAction = Texts.T(_thread?.Summary.CaptureEnabled == true ? "archive.captureOff" : "archive.captureOn");
        capture.Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4,
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
        PreviewArchiveMediaButton.IsEnabled = ready && SelectedMediaRow() != null;
        ArchiveOpenGallery.IsEnabled = ready && _selectedThread != null;
        ArchiveReadPosts.IsEnabled = readReady && _selectedThread != null
            && FrozenArchiveSelection().Length == 1;
        ArchiveResultsGallery.IsEnabled = ready && _listTotal > 0;
        ArchiveThreadPage.Text = PageLabel(_listOffset, _listTotal);
        ArchivePostPage.Text = PageLabel(_postOffset, shownPosts, ReaderPageSize);
        RefreshArchiveResultActions();
    }
    private static string PageLabel(int offset, int total, int pageSize = PageSize) => Texts.T("archive.page", total == 0 ? 0 : offset + 1, Math.Min(offset + pageSize, total), total);

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

    private ArchiveAttachmentRow CreateAttachmentRow(ResourceId resource, CapturedAttachment attachment, string postId,
        Task<IReadOnlyList<ArchiveAssetRow>> ready)
    {
        bool large = _largeReader;
        global::MeowChan.Desktop.ReaderDisplayPreferences.ThumbnailDecodeBounds bounds =
            global::MeowChan.Desktop.ReaderDisplayPreferences.DecodeBounds(ReaderThumbnailWidth, large);
        int thumbnailWidth = ReaderDisplayPreferences.EffectiveThumbnailWidth(ReaderThumbnailWidth, large);
        ArchiveAttachmentRow row = new(attachment);
        row = row with
        {
            LargePreview = large,
            ThumbnailWidth = thumbnailWidth,
            LoadThumbnail = async cancellationToken =>
            {
                using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                IReadOnlyList<ArchiveAssetRow> assets = await ready.WaitAsync(loading.Token);
                ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(assets.Select(asset => asset.Manifest).Distinct(), resource, postId, attachment);
                row.Binding = resolved?.Binding;
                ArchiveAssetRow? match = resolved == null ? null : assets.FirstOrDefault(asset => ReferenceEquals(asset.Manifest, resolved.Snapshot) && asset.Asset == resolved.Asset);
                if (match == null) throw new InvalidDataException(Texts.T(assets.Any(asset =>
                    asset.Asset.MediaIdentifier == attachment.MediaId && asset.Asset.Variant == attachment.Variant)
                    ? "archive.differentVersion" : "archive.notSaved"));
                // The normal-size previews are kept by their bytes (the large ones are about 3 MiB each and are not), so scrolling the
                // reader back shows them at once. The image size is the tile default, so the media page and the archive tiles share it.
                bool video = SavedMediaTypes.Classify(match.Asset.OriginalFileName ?? match.Asset.Path) == SavedMediaKind.Video;
                string? key = large ? null : MediaTileThumbnails.ByteKey(match.Asset,
                    video ? $"@poster{bounds.MaximumWidth}x{bounds.MaximumHeight}"
                        : bounds is { MaximumWidth: 240, MaximumHeight: 180 } ? string.Empty
                        : $"@reader{bounds.MaximumWidth}x{bounds.MaximumHeight}");
                return await Task.Run(async () =>
                {
                    await _thumbnailGate.WaitAsync(loading.Token).ConfigureAwait(false);
                    try
                    {
                        loading.Token.ThrowIfCancellationRequested(); // Coverage can change while queued for a slot.
                        if (video)
                            return await MediaTileThumbnails.LoadPosterAsync(match.Manifest, match.Asset,
                                Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1"),
                                bounds.MaximumWidth, bounds.MaximumHeight, key, loading.Token).ConfigureAwait(false);
                        return await MediaTileThumbnails.KeepAsync(key, () => DecodeThumbnailAsync(match, loading.Token,
                            bounds.MaximumWidth, bounds.MaximumHeight, MediaTileThumbnails.Verified)).ConfigureAwait(false);
                    }
                    finally { _thumbnailGate.Release(); }
                }, loading.Token).ConfigureAwait(false);
            },
            OpenPreview = async () =>
            {
                if (_ended || _lifetime.IsCancellationRequested || IsReaderDialogOpen || _busy || !_available || !ready.IsCompletedSuccessfully) return;
                ArchiveAssetRow? match = FindMatchingAsset(resource, postId, attachment, ready.Result);
                if (match != null && _selectedThread == resource && OpenGallery != null)
                    await OpenGallery(resource, new GalleryEntryKey(
                        new GalleryAssetKey(resource, match.Manifest.Directory, match.Asset.AssetId),
                        postId, attachment.Position), this);
                else Message(Texts.T("archive.notSaved"));
            }
        };
        return row;
    }

    // Only visible rows resolve their exact local binding. PlannedPath is never evidence of a saved revision.
    private ArchiveAttachmentRow CreateListAttachmentRow(ArchiveThreadSummary summary, string? postId, CapturedAttachment attachment)
    {
        ArchiveAttachmentRow row = new(attachment);
        row = row with
        {
            LoadThumbnail = token => LoadListThumbnailAsync(summary, postId, attachment, binding => row.Binding = binding, token),
            OpenPreview = async () =>
            {
                await OpenThreadGalleryAsync(summary.Id, null, this);
            }
        };
        return row;
    }

    private Task<Bitmap?> LoadListThumbnailAsync(ArchiveThreadSummary summary, string? postId,
        CapturedAttachment attachment, Action<MediaAttachmentBinding?> setBinding, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var openReader = _openReader;
        if (attachment.IsDeleted || postId == null || openReader == null) return null;
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await using ArchiveReader reader = await openReader(loading.Token);
        ArchivedThreadView? thread = await reader.ReadThreadAsync(summary.Id, loading.Token);
        if (thread == null) return null;
        List<MediaAssetSnapshot> manifests = new();
        foreach (string directory in thread.MediaDirectories)
            manifests.Add(await MediaAssetReader.OpenAsync(directory, loading.Token));
        ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(manifests, summary.Id, postId, attachment);
        setBinding(resolved?.Binding);
        if (resolved == null) return null;
        await _thumbnailGate.WaitAsync(loading.Token);
        try
        {
            loading.Token.ThrowIfCancellationRequested();
            return await ArchiveListThumbnails.LoadAsync(new(resolved.Snapshot, resolved.Asset),
                Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1"), loading.Token);
        }
        finally { _thumbnailGate.Release(); }
    }, cancellationToken);

    internal static ArchiveAssetRow? FindMatchingAsset(ResourceId resource, string postId,
        CapturedAttachment attachment, IReadOnlyList<ArchiveAssetRow> assets)
    {
        ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(assets.Select(asset => asset.Manifest).Distinct(), resource, postId, attachment);
        return resolved == null ? null : assets.FirstOrDefault(asset => ReferenceEquals(asset.Manifest, resolved.Snapshot) && asset.Asset == resolved.Asset);
    }

    internal static async Task<Bitmap?> DecodeThumbnailAsync(ArchiveAssetRow row, CancellationToken cancellationToken, int maximumWidth = 240, int maximumHeight = 180,
        VerifiedFileMemo? verifiedFiles = null)
    {
        string extension = System.IO.Path.GetExtension(row.Asset.Path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")) return null;
        if (row.Asset.Length > 64L * 1024 * 1024) throw new InvalidDataException(Texts.T("archive.tooLarge"));
        long request = PerformanceDiagnostics.NextId();
        Stream verified;
        using (PerformanceDiagnostics.Measure("gallery.thumbnail.verify", request, asset: row.Asset.AssetId))
            verified = await row.Manifest.OpenSessionVerifiedReadAsync(row.Asset.AssetId, verifiedFiles, cancellationToken).ConfigureAwait(false);
        PerformanceDiagnostics.Record("gallery.thumbnail.verified-bytes", request: request, asset: row.Asset.AssetId, value: row.Asset.Length);
        await using Stream stream = verified;
        using var decode = PerformanceDiagnostics.Measure("gallery.thumbnail.decode", request, asset: row.Asset.AssetId);
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
        ArchivePreview.IsVisible = true;
        PreviewTitle.Text = row.Name;
        ArchivePreviewPath.Text = row.Asset.Path;
        ArchivePreviewMessage.Text = Texts.T("archive.loading");
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
            if (_ended || request != _previewRequest || !ArchivePreview.IsVisible) { bitmap?.Dispose(); return; }
            _preview = bitmap;
            // D1: the preview is one asynchronous load, so it fades in (not under motion-off).
            MeowChan.Desktop.Motion.ImageFade.Show(ArchivePreviewImage, bitmap, arrivedAsync: true);
            ArchivePreviewMessage.Text = bitmap == null ? Texts.T("archive.previewUnavailable") : string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _previewRequest) ArchivePreviewMessage.Text = exception.Message; }
        finally { if (ReferenceEquals(_previewCancellation, loading)) _previewCancellation = null; }
    }

    private void ClosePreview()
    {
        ++_previewRequest;
        _previewCancellation?.Cancel();
        ArchivePreview.IsVisible = false;
        ArchivePreviewImage.Source = null;
        _preview?.Dispose();
        _preview = null;
        _previewPath = null;
        ArchivePreviewPath.Text = string.Empty;
        ArchivePreviewMessage.Text = string.Empty;
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
        if (IsFilenameSearchOpen || _busy || _postOffset == 0) return;
        ++_readerInteractionRevision;
        _postOffset = Math.Max(0, _postOffset - ReaderPageSize);
        ++_readerFocusRequest;
        _readerPostId = null;
        RenderThread();
        QueueReadingPositionCapture(DispatcherPriority.Loaded);
    }
    private void OnNextPosts(object? sender, RoutedEventArgs e)
    {
        if (IsFilenameSearchOpen || _busy || _thread == null || _postOffset + ReaderPageSize >= ShownPostCount) return;
        ++_readerInteractionRevision;
        _postOffset += ReaderPageSize;
        ++_readerFocusRequest;
        _readerPostId = null;
        RenderThread();
        QueueReadingPositionCapture(DispatcherPriority.Loaded);
    }
    private void OnBackClick(object? sender, RoutedEventArgs e) { if (IsFilenameSearchOpen) return; CloseThreadFind(); _showReader = false; UpdateLayoutMode(); }
    private void OnMediaSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_rebindingMedia) return;
        if (!_applyingMediaRestore && !IsPendingMediaSelectionEcho(sender)) ++_mediaPositionRequest;
        // Only the list that is shown is read; switching between list and tiles carries the selection over (RestoreMediaPosition).
        if (sender is DataGrid && _mediaTiles || sender is ListBox && !_mediaTiles) return;
        UpdateActions();
    }
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateLayoutMode();
    }
    private bool CanShowArchivePreviewBesideList => Bounds.Width >= (_archiveLayout == "Table" ? 1450 : 1000);
    private void OnArchiveFavoritesFilterClick(object? sender, RoutedEventArgs e)
    {
        _archiveFavoritesOnly = !_archiveFavoritesOnly;
        ArchiveFavoritesFilter.Content = Texts.T(
            _archiveFavoritesOnly ? "archive.allMedia" : "archive.favoritesOnly");
        RenderMedia();
    }
    private async void OnArchiveLayoutClick(object? sender, RoutedEventArgs e)
    {
        string layout = sender == Control<Button>("ArchiveLayoutTable") ? "Table" : "List";
        if (_archiveLayout == layout)
        {
            // A selected toggle remains selected when it is clicked again.
            UpdateLayoutMode();
            return;
        }
        ArchiveLayoutPreference = layout;
        RestoreArchiveSelection(ArchiveVisibleRows);
        await PersistArchiveViewAsync();
    }
    private async void OnArchiveSplitReleased(object? sender, PointerReleasedEventArgs e) => await SaveArchiveSplitAsync();
    private async void OnArchiveSplitKeyUp(object? sender, KeyEventArgs e) => await SaveArchiveSplitAsync();
    private async Task SaveArchiveSplitAsync()
    {
        if (ArchiveSplit.ColumnDefinitions[0].ActualWidth <= 0) return;
        _archiveSplitWidth = Math.Clamp(ArchiveSplit.ColumnDefinitions[0].ActualWidth, 180, 1200);
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
        ArchivePageHeader.IsVisible = !(_showReader && narrow);
        ArchiveRoot.Margin = new Thickness(compact ? 8 : 12);
        ArchiveRoot.RowSpacing = compact ? 6 : 8;
        ArchiveSubtitle.IsVisible = Bounds.Height >= 650;
        ToolTip.SetTip(ArchiveReaderStatus, Texts.T("archive.partial"));
        Grid split = ArchiveSplit;
        // Keep the wide split stable before the first selection. Reflowing the list
        // after a click moves rows under the pointer and breaks rapid Ctrl selection.
        bool beside = !narrow;
        // Retain both tracks across size changes; replacing the definitions
        // during layout can invalidate Avalonia's in-progress measure ranges.
        split.ColumnDefinitions[0].Width = new GridLength(beside
            ? (_archiveLayout == "Table" ? Math.Max(WorkViewDensity.ArchiveTableMinWidth(ArchiveWorkView), _archiveSplitWidth) : _archiveSplitWidth) : 0);
        split.ColumnDefinitions[1].Width = new GridLength(beside ? 6 : 0);
        split.ColumnSpacing = beside ? 6 : 0;
        // The shared footer row aligns both pane edges only side by side. Stacked,
        // a hidden list footer must not reserve blank height below the reader.
        Grid.SetIsSharedSizeScope(split, beside);
        Grid.SetColumn(ArchiveListPane, beside ? 0 : 2);
        ArchiveSplitter.IsVisible = beside;
        ArchiveReaderPane.IsVisible = _largeReader || !narrow || _showReader;
        ArchiveListPane.IsVisible = !narrow || !_showReader;
        ArchiveThreads.IsVisible = _archiveLayout == "List";
        ArchiveThreadTable.IsVisible = _archiveLayout == "Table";
        ArchiveThreadColumnsButton.IsVisible = _archiveLayout == "Table";
        ArchiveLayoutList.IsChecked = _archiveLayout == "List";
        ArchiveLayoutTable.IsChecked = _archiveLayout == "Table";
        ArchiveBackButton.IsVisible = narrow && !_largeReader;
        RefreshArchiveResultActions();
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_ended) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        ReaderLocalizationTask = LocalizeArchiveAsync();
    }

    private async Task LocalizeArchiveAsync()
    {
        using ReaderFocusIntent focusIntent = new(this);
        // Refresh computed labels without losing the chosen manifest version or thread.
        // Restoring a post anchor selects the Posts tab; preserve other active reader views.
        ReaderAnchor? anchor = ReaderShowsPosts ? CaptureReaderAnchor() : null;
        _assets = Array.AsReadOnly(_assets.Select(row => new ArchiveAssetRow(row.Manifest, row.Asset)).ToArray());
        Localize();
        LocalizeReaderUpdates();
        RenderThread();
        ListBox list = ArchiveThreads;
        if (list.ItemsSource is IEnumerable<ArchiveThreadRow> rows)
        {
            ArchiveThreadRow[] localized = rows.Select(row =>
            {
                ArchiveThreadRow translated = new(row.Summary, row.Work)
                { PreviewText = row.PreviewText, PreviewAttachment = row.PreviewAttachment };
                translated.CopyMediaCountFrom(row);
                return translated;
            }).ToArray();
            ConfigureThreadFavoriteRows(localized);
            ConfigurePrivateTagRows(localized);
            _updatingList = true;
            try
            {
                list.ItemsSource = localized;
                ArchiveThreadTable.ItemsSource = localized;
                UpdateLayout();
                RestoreArchiveSelection(localized, _selectedThread);
            }
            finally { _updatingList = false; }
            BeginArchiveMediaCounts(localized);
        }
        if (anchor != null) await RestoreReaderAnchorAsync(anchor, focusIntent: focusIntent);
    }
    // The Reader toolbar rule (P6): every labelled action carries an icon before its text, none is text alone. The ones that set their
    // text in code (tags, find, view, the two conditional returns) keep a string as content; the icon comes from
    // their content template, so what sets or reads the label is unchanged. Close, gallery, read, full screen, history and export
    // get theirs from CommandBindings in Localize.
    private void InitializeReaderToolbarIcons()
    {
        CommandBindings.ShowIconBeforeText(ReaderTagsOpen, CommandCatalog.Get("thread.tags").Icon);
        CommandBindings.ShowIconBeforeText(ReaderFindOpen, CommandCatalog.Get("reader.find").Icon);
        CommandBindings.ShowIconBeforeText(ReaderViewOpen, "eye");
        CommandBindings.ShowIconBeforeText(ReaderUnreadReturn, CommandCatalog.Get("reader.history-back").Icon);
        CommandBindings.ShowIconBeforeText(ReaderSkipUnavailableGroupMember, CommandCatalog.Get("reader.skip-unavailable-group-member").Icon);
    }

    private void Localize()
    {
        foreach (Control control in this.GetLogicalDescendants().Concat(ArchiveReaderPane.GetLogicalDescendants()).OfType<Control>().Distinct())
        {
            if (control.Tag is not string key || !control.Classes.Contains("localized")
                || control.Classes.Contains("command-icon")) continue;
            if (control is TextBlock text) text.Text = Texts.T(key);
            if (control is Button button) button.Content = Texts.T(key);
        }
        LocalizeThreadFind();
        LocalizeUnifiedSearch();
        LocalizeReaderView();
        LocalizePrivateTags();
        LocalizeExport();
        LocalizeSearch();
        LocalizeEmptyState();
        UpdateArchiveEmptyState();
        Control<Button>("ArchiveLayoutList").Content = Texts.T("gallery.list");
        Control<Button>("ArchiveLayoutTable").Content = Texts.T("gallery.table");
        RefreshArchiveResultActions();
        LocalizeArchiveColumns();
        CommandBindings.Apply(SearchArchiveButton, "archive.search", iconOnly: true);
        CommandBindings.Apply(ArchiveBackButton, "archive.back-to-list");
        CommandBindings.Apply(PreviousThreadsButton, "archive.previous-page", iconOnly: true);
        CommandBindings.Apply(NextThreadsButton, "archive.next-page", iconOnly: true);
        CommandBindings.Apply(PreviousPostsButton, "pagination.previous", iconOnly: true);
        CommandBindings.Apply(NextPostsButton, "pagination.next", iconOnly: true);
        CommandBindings.Apply(PreviousReferences, "pagination.previous", iconOnly: true);
        CommandBindings.Apply(NextReferences, "pagination.next", iconOnly: true);
        CommandBindings.Apply(ArchiveHistoryBack, "reader.history-back", iconWithLabel: true);
        CommandBindings.Apply(ArchiveReaderFullscreen, "window.fullscreen", iconWithLabel: true);
        ApplyReaderCloseLabel();
        CommandBindings.Apply(ArchiveOpenGallery, "gallery.open", iconWithLabel: true);
        CommandBindings.Apply(ArchiveReadPosts, "reader.expand", iconWithLabel: true);
        CommandBindings.Apply(ArchiveGalleryReturn, "gallery.return", iconWithLabel: true);
        CommandBindings.Apply(ArchiveMediaReturn, "media.return", iconWithLabel: true);
        CommandBindings.Apply(ExportArchiveButton, "export.open-html-dialog", iconWithLabel: true);
        CommandBindings.Apply(ReaderPreviousThread, "reader.previous-thread", iconOnly: true);
        CommandBindings.Apply(ReaderPreviousPost, "reader.previous-post", iconOnly: true);
        CommandBindings.Apply(ReaderNextPost, "reader.next-post", iconOnly: true);
        CommandBindings.Apply(ReaderNextThread, "reader.next-thread", iconOnly: true);
        CommandBindings.Apply(ArchiveResultsGallery, "gallery.open-results", iconWithLabel: true);
        CommandBindings.Apply(ArchiveFavoritesOverview, "favorites.open", iconWithLabel: true);
        ArchiveReferencesTab.Header = ReferencesTabHeader();
        ArchivePostsTab.Header = Texts.T("archive.posts");
        ArchiveMediaTab.Header = Texts.T("archive.media");
        ArchiveMediaMode.Content = Texts.T(_mediaTiles ? "gallery.list" : "gallery.tiles");
        ArchiveFavoritesFilter.Content = Texts.T(
            _archiveFavoritesOnly ? "archive.allMedia" : "archive.favoritesOnly");
        // Replacing translated labels clears selection synchronously; it is not a new user choice.
        _localizingMediaChoices = true;
        try
        {
            ArchiveVersionScope.ItemsSource = new[] { Texts.T("gallery.latestVersions"), Texts.T("gallery.allVersions") };
            ArchiveVersionScope.SelectedIndex = _allVersions ? 1 : 0;
            ArchiveMediaSort.ItemsSource = new[] { Texts.T("gallery.sortName"), Texts.T("gallery.sortSize"), Texts.T("gallery.sortVersion"), Texts.T("media.rating.filter.sortDescending") };
            ArchiveMediaSort.SelectedIndex = _mediaSort;
        }
        finally { _localizingMediaChoices = false; }
        LocalizeMediaRatings();
        LocalizeMediaSelection();
        if (this.FindControl<MenuItem>("ArchiveTableGalleryMenu") is MenuItem gallery) gallery.Header = Texts.T("gallery.open");
        if (this.FindControl<MenuItem>("ArchiveTableReadMenu") is MenuItem read) read.Header = Texts.T("gallery.read");
        if (this.FindControl<MenuItem>("ArchiveTableExportMenu") is MenuItem export) export.Header = Texts.T("gallery.export");
        DataGrid media = ArchiveMediaGrid;
        media.Columns[0].Header = Texts.T("archive.name"); media.Columns[1].Header = Texts.T("archive.size"); media.Columns[2].Header = Texts.T("archive.version");
        UpdateActions();
    }
    private void Message(string text, bool error = false) => OperationMessage("action", text, error);
    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;
    internal void EndLifetime()
    {
        if (_ended) return;
        InvalidateFilenameSearch();
        CloseThreadFind();
        CloseQuotePreview();
        _ended = true;
        _mediaMenuPresenter.Invalidate();
        EndMediaSelectionLifetime();
        StopObservingReaderMediaKeys();
        CancelArchiveMediaCounts();
        EndArchiveCleanupLifetime();
        EndTransferPreviewLifetime();
        EndUrlTextExportLifetime();
        ClearPaginationInput();
        _runningList = null; _retryList = null;
        SearchStateChanged?.Invoke();
        InvalidateSuggestions();
        ArchiveSourceFilter.EndSuggestions();
        ArchiveBoardFilter.EndSuggestions();
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

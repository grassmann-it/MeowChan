using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
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
    internal Func<ArchiveThreadQuery, int, Control?, Task>? OpenResultsGallery { get; set; }
    internal Func<IReadOnlyList<ThreadWorkSnapshot>>? GetWorkSnapshots { get; set; }
    private int _workFilter;
    private bool _threadTable = true;
    private bool _mediaTiles = true;
    internal bool MediaTilesPreference
    {
        get => _mediaTiles;
        set
        {
            _mediaTiles = value;
            if (IsInitialized || _largeReader)
            {
                Control<DataGrid>("ArchiveMediaGrid").IsVisible = !value;
                Control<ListBox>("ArchiveMediaTiles").IsVisible = value;
                Control<Button>("ArchiveMediaMode").Content = Texts.T(value ? "gallery.list" : "gallery.tiles");
            }
        }
    }
    internal Func<bool, Task>? SaveMediaTiles { get; set; }
    private bool _allVersions = true;
    private int _mediaSort;

    public ArchivePage()
    {
        InitializeComponent(); InitializeArchiveSelection();
        // Navigation scrolls explicitly; selection must not re-anchor while media is loading.
        Control<ListBox>("ArchivePosts").AutoScrollToSelectedItem = false;
        InitializeReaderInteraction();
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
        ClosePreview();
        await LoadListAsync();
    }

    private async Task LoadListAsync()
    {
        if (_ended || !_available || _openReader == null) return;
        CancelReferences();
        int request = ++_contentRequest;
        _contentCancellation?.Cancel();
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _contentCancellation = loading;
        CancellationToken cancellationToken = loading.Token;
        CancelMediaLoad();
        ClosePreview();
        _busy = true;
        Message(Texts.T("archive.loading"));
        UpdateActions();
        ResourceId? selected = _selectedThread;
        try
        {
            await using ArchiveReader reader = await _openReader(cancellationToken);
            ArchiveThreadPage result;
            if (_workFilter == 0)
                result = await reader.SearchThreadsAsync(_query, _listOffset, PageSize, cancellationToken);
            else
            {
                IReadOnlyList<ArchiveThreadSummary> all = await reader.SelectThreadSummariesAsync(_query, cancellationToken);
                IReadOnlyDictionary<string, ThreadWorkSnapshot> work = (GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>())
                    .GroupBy(item => item.Url, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                ArchiveThreadSummary[] filtered = all.Where(item => WorkMatches(work.GetValueOrDefault(item.Url), _workFilter)).ToArray();
                if (filtered.Length <= _listOffset && _listOffset > 0) _listOffset = 0;
                result = new ArchiveThreadPage(Array.AsReadOnly(filtered.Skip(_listOffset).Take(PageSize).ToArray()), filtered.Length, _listOffset);
            }
            if (result.TotalCount <= _listOffset && _listOffset > 0)
            {
                _listOffset = 0;
                result = await reader.SearchThreadsAsync(_query, _listOffset, PageSize, cancellationToken);
            }
            if (_ended || request != _contentRequest) return;
            _listTotal = result.TotalCount;
            _updatingList = true;
            try
            {
                IReadOnlyDictionary<string, ThreadWorkSnapshot> work = (GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>())
                    .GroupBy(item => item.Url, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                ArchiveThreadRow[] rows = result.Items.Select(summary => new ArchiveThreadRow(summary, work.GetValueOrDefault(summary.Url))).ToArray();
                ListBox list = Control<ListBox>("ArchiveThreads");
                list.ItemsSource = rows;
                list.SelectedItem = rows.FirstOrDefault(row => row.Summary.Id == selected);
                DataGrid table = Control<DataGrid>("ArchiveThreadTable");
                table.ItemsSource = rows;
                table.SelectedItem = rows.FirstOrDefault(row => row.Summary.Id == selected);
                RestoreArchiveSelection(rows, selected);
                Control<TextBlock>("ArchiveEmpty").IsVisible = rows.Length == 0;
            }
            finally { _updatingList = false; }
            Message(string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _contentRequest) Message(exception.Message, true); }
        finally
        {
            if (ReferenceEquals(_contentCancellation, loading)) _contentCancellation = null;
            if (!_ended && request == _contentRequest) { _busy = false; UpdateActions(); }
        }
        if (_showReader && selected != null && !_ended && request == _contentRequest) await LoadThreadAsync(selected, resetPage: false);
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

    internal async Task LoadThreadAsync(ResourceId id, bool resetPage = true)
    {
        if (_ended || !_available || _openReader == null) return;
        ReaderAnchor? retainedAnchor = !resetPage && _thread?.Summary.Id == id ? CaptureReaderAnchor() : null;
        CancelReferences();
        int request = ++_contentRequest;
        _contentCancellation?.Cancel();
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _contentCancellation = loading;
        CancellationToken cancellationToken = loading.Token;
        CancelMediaLoad();
        ClosePreview();
        if (resetPage || _selectedThread != id) _postOffset = 0;
        _selectedThread = id;
        _thread = null;
        _assets = Array.Empty<ArchiveAssetRow>();
        _busy = true;
        _showReader = true;
        _updatingList = true;
        try
        {
            ArchiveThreadRow[] rows = ArchiveVisibleRows;
            if (rows.Any(row => row.Summary.Id == id) && !_archiveSelectionIds.Contains(id))
            {
                _archiveSelectionIds.Clear(); _archiveSelectionIds.Add(id); _archiveLeadId = id;
            }
            RestoreArchiveSelection(rows);
        }
        finally { _updatingList = false; }
        RenderThread();
        UpdateLayoutMode();
        Message(Texts.T("archive.loading"));
        try
        {
            // The catalog is local. Display its posts before touching potentially disconnected media drives.
            await using ArchiveReader reader = await _openReader(cancellationToken);
            ArchivedThreadView? thread = await reader.ReadThreadAsync(id, cancellationToken);
            if (_ended || request != _contentRequest) return;
            _thread = thread;
            if (_thread != null && _postOffset >= _thread.PostCount) _postOffset = 0;
            _busy = false;
            RenderThread();
            Message(string.Empty);
            if (thread != null)
            {
                MediaLoadTask = LoadMediaAsync(thread);
                if (retainedAnchor != null) await RestoreReaderAnchorAsync(retainedAnchor);
                else if (_largeReader && thread.PostCount > 0)
                    await FocusPostAsync(thread.GetPosts(_postOffset, 1).First().PostId);
                else if (_largeReader) Control<Button>("ArchiveCloseReader").Focus();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _contentRequest) Message(exception.Message, true); }
        finally
        {
            if (ReferenceEquals(_contentCancellation, loading)) _contentCancellation = null;
            if (!_ended && request == _contentRequest) { _busy = false; UpdateActions(); }
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
                    foreach (string directory in thread.MediaDirectories)
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
            Message(string.Join(Environment.NewLine, loaded.warnings), loaded.warnings.Count > 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _mediaRequest) Message(exception.Message, true); }
        finally
        {
            if (ReferenceEquals(_mediaCancellation, loading)) _mediaCancellation = null;
            if (!_ended && request == _mediaRequest) { _mediaLoading = false; _mediaReady.TrySetResult(_assets); RenderMedia(); }
        }
    }

    private void CancelMediaLoad()
    {
        ++_mediaRequest;
        _mediaReady.TrySetCanceled();
        _mediaReady = NewMediaReady();
        _mediaCancellation?.Cancel();
        _mediaLoading = false;
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
            Control<ListBox>("ArchivePosts").ItemsSource = posts;
        }
        else Control<ListBox>("ArchivePosts").ItemsSource = null;
        Control<TextBlock>("ArchiveNoPosts").IsVisible = _thread?.PostCount == 0;
        RenderMedia();
    }

    private void RenderMedia()
    {
        DataGrid grid = Control<DataGrid>("ArchiveMediaGrid");
        ListBox tiles = Control<ListBox>("ArchiveMediaTiles");
        ArchiveAssetRow? selected = (grid.SelectedItem ?? tiles.SelectedItem) as ArchiveAssetRow;
        IReadOnlyList<ArchiveAssetRow> shown = VisibleAssets();
        grid.ItemsSource = shown;
        tiles.ItemsSource = shown;
        if (selected != null)
        {
            ArchiveAssetRow? rebound = shown.FirstOrDefault(row => row.Manifest.Directory == selected.Manifest.Directory
                && row.Asset.AssetId == selected.Asset.AssetId);
            grid.SelectedItem = rebound;
            tiles.SelectedItem = rebound;
        }
        TextBlock empty = Control<TextBlock>("ArchiveNoMedia");
        empty.Text = Texts.T(_mediaLoading ? "archive.loadingMedia" : "archive.noMedia");
        empty.IsVisible = shown.Count == 0;
        UpdateActions();
    }

    private IReadOnlyList<ArchiveAssetRow> VisibleAssets()
    {
        IEnumerable<ArchiveAssetRow> rows = _allVersions ? _assets : _assets.Where(row => row.Asset.IsCurrent);
        IOrderedEnumerable<ArchiveAssetRow> ordered = _mediaSort switch
        {
            1 => rows.OrderBy(row => row.Asset.Length),
            2 => rows.OrderBy(row => row.Asset.Sha256, StringComparer.Ordinal),
            _ => rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
        };
        return ordered.ThenBy(row => row.Manifest.Directory, StringComparer.Ordinal)
            .ThenBy(row => row.Asset.AssetId, StringComparer.Ordinal).ToArray();
    }

    private void UpdateActions()
    {
        UpdateArchiveSelectionActions();
        UpdateReaderNavigation();
        bool ready = _available && !_busy && !_ended;
        Control<Button>("SearchArchiveButton").IsEnabled = ready;
        Control<Button>("ExportArchiveButton").IsEnabled = ready && _selectedThread != null && !_exportRunning;
        Control<Button>("ArchiveHistoryBack").IsVisible = _history.Count > 0;
        Control<Button>("RefreshArchiveButton").IsEnabled = ready;
        Control<Button>("PreviousThreadsButton").IsEnabled = ready && _listOffset > 0;
        Control<Button>("NextThreadsButton").IsEnabled = ready && _listOffset + PageSize < _listTotal;
        Control<Button>("PreviousPostsButton").IsEnabled = ready && _postOffset > 0;
        Control<Button>("NextPostsButton").IsEnabled = ready && _thread != null && _postOffset + PageSize < _thread.PostCount;
        Control<Grid>("ArchivePostNavigation").IsVisible = _thread?.PostCount > PageSize;
        Control<Button>("CapturePostsButton").IsEnabled = ready && _thread != null;
        Control<Button>("CapturePostsButton").Content = Texts.T(_thread?.Summary.CaptureEnabled == true ? "archive.captureOff" : "archive.captureOn");
        ToolTip.SetTip(Control<Button>("CapturePostsButton"), Texts.T("archive.captureHint"));
        Control<Button>("PreviewArchiveMediaButton").IsEnabled = ready && SelectedMediaRow() != null;
        Control<Button>("ArchiveOpenGallery").IsEnabled = ready && _selectedThread != null;
        Control<Button>("ArchiveReadPosts").IsEnabled = ready && _selectedThread != null;
        Control<Button>("ArchiveResultsGallery").IsEnabled = ready && _listTotal > 0;
        Control<TextBlock>("ArchiveThreadPage").Text = PageLabel(_listOffset, _listTotal);
        Control<TextBlock>("ArchivePostPage").Text = PageLabel(_postOffset, _thread?.PostCount ?? 0);
    }
    private static string PageLabel(int offset, int total) => Texts.T("archive.page", total == 0 ? 0 : offset + 1, Math.Min(offset + PageSize, total), total);

    private async void OnCaptureClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || !_available || _thread == null || _setCapture == null) return;
        ResourceId id = _thread.Summary.Id;
        bool enabled = !_thread.Summary.CaptureEnabled;
        CancelReferences();
        int request = ++_contentRequest;
        _busy = true;
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
    private async void OnPreviousThreads(object? sender, RoutedEventArgs e) { if (_busy || _listOffset == 0) return; _listOffset = Math.Max(0, _listOffset - PageSize); await LoadListAsync(); }
    private async void OnNextThreads(object? sender, RoutedEventArgs e) { if (_busy || _listOffset + PageSize >= _listTotal) return; _listOffset += PageSize; await LoadListAsync(); }
    private void OnPreviousPosts(object? sender, RoutedEventArgs e) { if (_busy || _postOffset == 0) return; _postOffset = Math.Max(0, _postOffset - PageSize); RenderThread(); }
    private void OnNextPosts(object? sender, RoutedEventArgs e) { if (_busy || _thread == null || _postOffset + PageSize >= _thread.PostCount) return; _postOffset += PageSize; RenderThread(); }
    private void OnBackClick(object? sender, RoutedEventArgs e) { _showReader = false; UpdateLayoutMode(); }
    private void OnMediaSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid && Control<DataGrid>("ArchiveMediaGrid").SelectedItem is ArchiveAssetRow gridRow)
            Control<ListBox>("ArchiveMediaTiles").SelectedItem = gridRow;
        if (sender is ListBox && Control<ListBox>("ArchiveMediaTiles").SelectedItem is ArchiveAssetRow tileRow)
            Control<DataGrid>("ArchiveMediaGrid").SelectedItem = tileRow;
        UpdateActions();
    }
    private void OnPostLayoutChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid || grid.Children.Count < 2) return;
        bool beside = !_largeReader && grid.Bounds.Width >= 600;
        if (grid.Children[0] is Control media)
        {
            media.MaxWidth = _largeReader ? 1040 : 268;
            media.HorizontalAlignment = _largeReader ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Left;
        }
        // SizeChanged can run while an ancestor Grid is measuring. Keep its
        // definition collections intact so cached cell ranges remain valid.
        grid.ColumnDefinitions[0].Width = beside ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = beside ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        grid.RowDefinitions[1].Height = beside ? new GridLength(0) : GridLength.Auto;
        grid.ColumnSpacing = beside ? 12 : 0;
        grid.RowSpacing = beside ? 0 : 8;
        Grid.SetColumn(grid.Children[1], beside ? 1 : 0);
        Grid.SetRow(grid.Children[1], beside ? 0 : 1);
    }
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateLayoutMode();
    private void UpdateLayoutMode()
    {
        if (!IsInitialized && !_largeReader) return;
        bool narrow = Bounds.Width < 1000;
        bool compact = Bounds.Height > 0 && Bounds.Height < 500;
        Control<Grid>("ArchiveRoot").Margin = new Thickness(compact ? 12 : 20);
        Control<Grid>("ArchiveRoot").RowSpacing = compact ? 8 : 12;
        Control<TextBlock>("ArchiveSubtitle").IsVisible = !compact;
        Control<TextBlock>("ArchivePartialHint").IsVisible = !compact;
        ToolTip.SetTip(Control<TextBlock>("ArchiveReaderStatus"), Texts.T("archive.partial"));
        Grid split = Control<Grid>("ArchiveSplit");
        bool beside = _showReader && !narrow;
        // Retain both tracks across size changes; replacing the definitions
        // during layout can invalidate Avalonia's in-progress measure ranges.
        split.ColumnDefinitions[0].Width = new GridLength(beside ? 270 : 0);
        split.ColumnSpacing = beside ? 12 : 0;
        Grid.SetColumn(Control<Grid>("ArchiveListPane"), beside ? 0 : 1);
        Control<Grid>("ArchiveReaderPane").IsVisible = _largeReader || _showReader;
        Control<Grid>("ArchiveListPane").IsVisible = !narrow || !_showReader;
        Control<ListBox>("ArchiveThreads").IsVisible = !_threadTable || _showReader;
        Control<DataGrid>("ArchiveThreadTable").IsVisible = _threadTable && !_showReader;
        Control<Button>("ArchiveThreadMode").Content = Texts.T(_threadTable ? "gallery.cards" : "gallery.table");
        Control<Button>("ArchiveBackButton").IsVisible = narrow && !_largeReader;
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_ended) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        // Refresh computed labels without losing the chosen manifest version or thread.
        ReaderAnchor? anchor = _showReader ? CaptureReaderAnchor() : null;
        _assets = Array.AsReadOnly(_assets.Select(row => new ArchiveAssetRow(row.Manifest, row.Asset)).ToArray());
        Localize();
        RenderThread();
        if (anchor != null) _ = RestoreReaderAnchorAsync(anchor);
        ListBox list = Control<ListBox>("ArchiveThreads");
        if (list.ItemsSource is IEnumerable<ArchiveThreadRow> rows)
        {
            ArchiveThreadRow[] localized = rows.Select(row => new ArchiveThreadRow(row.Summary, row.Work)).ToArray();
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
            if (control.Tag is not string key || !control.Classes.Contains("localized")) continue;
            if (control is TextBlock text) text.Text = Texts.T(key);
            if (control is Button button) button.Content = Texts.T(key);
        }
        LocalizeExport();
        LocalizeSearch();
        Control<TabItem>("ArchiveReferencesTab").Header = Texts.T("archive.references");
        Control<TabItem>("ArchivePostsTab").Header = Texts.T("archive.posts");
        Control<TabItem>("ArchiveMediaTab").Header = Texts.T("archive.media");
        Control<Button>("ArchiveMediaMode").Content = Texts.T(_mediaTiles ? "gallery.list" : "gallery.tiles");
        Control<Button>("ArchiveGalleryReturn").Content = Texts.T("gallery.return");
        Control<ComboBox>("ArchiveVersionScope").ItemsSource = new[] { Texts.T("gallery.latestVersions"), Texts.T("gallery.allVersions") };
        Control<ComboBox>("ArchiveVersionScope").SelectedIndex = _allVersions ? 1 : 0;
        Control<ComboBox>("ArchiveMediaSort").ItemsSource = new[] { Texts.T("gallery.sortName"), Texts.T("gallery.sortSize"), Texts.T("gallery.sortVersion") };
        Control<ComboBox>("ArchiveMediaSort").SelectedIndex = _mediaSort;
        if (Control<DataGrid>("ArchiveThreadTable").ContextMenu is ContextMenu menu)
        {
            MenuItem[] actions = menu.Items.OfType<MenuItem>().ToArray();
            if (actions.Length >= 3)
            { actions[0].Header = Texts.T("gallery.open"); actions[1].Header = Texts.T("gallery.read"); actions[2].Header = Texts.T("gallery.export"); }
        }
        DataGrid media = Control<DataGrid>("ArchiveMediaGrid");
        media.Columns[0].Header = Texts.T("archive.name"); media.Columns[1].Header = Texts.T("archive.size"); media.Columns[2].Header = Texts.T("archive.version");
        UpdateActions();
    }
    private void Message(string text, bool error = false)
    {
        TextBlock message = Control<TextBlock>("ArchiveMessage");
        message.Text = text;
        message.Classes.Set("error", error);
        message.Classes.Set("muted", !error);
    }
    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;
    internal void EndLifetime()
    {
        if (_ended) return;
        _ended = true;
        EndExportDialog();
        ++_contentRequest;
        CancelMediaLoad();
        CancelReferences();
        _exportCancellation?.Cancel();
        _lifetime.Cancel();
        Texts.Changed -= OnLanguageChanged;
        ClosePreview();
    }
}

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
using oChan.Archive;
using oChan.Desktop;
using oChan.Desktop.Localization;
using oChan.Interfaces;
using oChan.Storage;
using SkiaSharp;

namespace oChan;

public partial class ArchivePage : UserControl
{
    private const int PageSize = 50;
    private readonly Func<CancellationToken, Task<ArchiveReader>>? _openReader;
    private readonly Func<ResourceId, bool, Task>? _setCapture;
    private readonly Action<string>? _openFolder;
    private readonly Func<string, CancellationToken, Task<MediaAssetSnapshot>> _openMedia = MediaAssetReader.OpenAsync;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
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

    public ArchivePage() { InitializeComponent(); }
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
            ArchiveThreadPage result = await reader.SearchThreadsAsync(_query, _listOffset, PageSize, cancellationToken);
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
                ArchiveThreadRow[] rows = result.Items.Select(summary => new ArchiveThreadRow(summary)).ToArray();
                ListBox list = Control<ListBox>("ArchiveThreads");
                list.ItemsSource = rows;
                list.SelectedItem = rows.FirstOrDefault(row => row.Summary.Id == selected);
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

    private async void OnThreadSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingList || _ended || !_available || Control<ListBox>("ArchiveThreads").SelectedItem is not ArchiveThreadRow row) return;
        _history.Clear();
        await LoadThreadAsync(row.Summary.Id, resetPage: true);
    }

    internal async Task LoadThreadAsync(ResourceId id, bool resetPage = true)
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
        if (resetPage || _selectedThread != id) _postOffset = 0;
        _selectedThread = id;
        _thread = null;
        _assets = Array.Empty<ArchiveAssetRow>();
        _busy = true;
        _showReader = true;
        _updatingList = true;
        try
        {
            ListBox list = Control<ListBox>("ArchiveThreads");
            if (list.ItemsSource is IEnumerable<ArchiveThreadRow> rows)
                list.SelectedItem = rows.FirstOrDefault(row => row.Summary.Id == id);
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
            if (thread != null) MediaLoadTask = LoadMediaAsync(thread);
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
            if (!_ended && request == _mediaRequest) { _mediaLoading = false; RenderMedia(); }
        }
    }

    private void CancelMediaLoad()
    {
        ++_mediaRequest;
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
                    Array.AsReadOnly((post.Attachments ?? Array.Empty<CapturedAttachment>()).Select(attachment => new ArchiveAttachmentRow(attachment)).ToArray()))
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
        ArchiveAssetRow? selected = grid.SelectedItem as ArchiveAssetRow;
        grid.ItemsSource = _assets;
        if (selected != null)
            grid.SelectedItem = _assets.FirstOrDefault(row => ReferenceEquals(row.Manifest, selected.Manifest)
                && row.Asset.AssetId == selected.Asset.AssetId);
        TextBlock empty = Control<TextBlock>("ArchiveNoMedia");
        empty.Text = Texts.T(_mediaLoading ? "archive.loadingMedia" : "archive.noMedia");
        empty.IsVisible = _assets.Count == 0;
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool ready = _available && !_busy && !_ended;
        Control<Button>("SearchArchiveButton").IsEnabled = ready;
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
        Control<Button>("PreviewArchiveMediaButton").IsEnabled = ready && Control<DataGrid>("ArchiveMediaGrid").SelectedItem is ArchiveAssetRow;
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

    private void OnAttachmentClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || !_available || sender is not Button { DataContext: ArchiveAttachmentRow row }) return;
        ArchiveAssetRow[] matches = _assets.Where(asset => asset.Asset.MediaIdentifier == row.Attachment.MediaId
            && asset.Asset.Variant == row.Attachment.Variant).ToArray();
        if (matches.Length == 0) { Message(Texts.T(_mediaLoading ? "archive.loadingMedia" : "archive.notSaved")); return; }
        ArchiveAssetRow? match = matches.LastOrDefault(asset =>
            (row.Attachment.ExpectedMd5 == null || row.Attachment.ExpectedMd5 == asset.Asset.Md5)
            && (row.Attachment.ExpectedSize == null || row.Attachment.ExpectedSize == asset.Asset.Length));
        Control<TabControl>("ArchiveContentTabs").SelectedIndex = 1;
        Control<DataGrid>("ArchiveMediaGrid").SelectedItem = match ?? matches[0];
        Message(match == null ? Texts.T("archive.differentVersion") : string.Empty);
        UpdateActions();
    }

    private async void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || !_available || Control<DataGrid>("ArchiveMediaGrid").SelectedItem is not ArchiveAssetRow row) return;
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
    private void OnMediaSelected(object? sender, SelectionChangedEventArgs e) => UpdateActions();
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateLayoutMode();
    private void UpdateLayoutMode()
    {
        if (!IsInitialized) return;
        bool narrow = Bounds.Width < 1000;
        bool compact = Bounds.Height > 0 && Bounds.Height < 500;
        Control<Grid>("ArchiveRoot").Margin = new Thickness(compact ? 12 : 20);
        Control<Grid>("ArchiveRoot").RowSpacing = compact ? 8 : 12;
        Control<TextBlock>("ArchiveSubtitle").IsVisible = !compact;
        Control<TextBlock>("ArchivePartialHint").IsVisible = !compact;
        ToolTip.SetTip(Control<TextBlock>("ArchiveReaderStatus"), Texts.T("archive.partial"));
        Grid split = Control<Grid>("ArchiveSplit");
        split.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "270,*");
        Grid.SetColumn(Control<Grid>("ArchiveReaderPane"), narrow ? 0 : 1);
        Control<Grid>("ArchiveReaderPane").IsVisible = !narrow || _showReader;
        Control<Grid>("ArchiveListPane").IsVisible = !narrow || !_showReader;
        Control<Button>("ArchiveBackButton").IsVisible = narrow;
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_ended) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        // Refresh computed labels without losing the chosen manifest version or thread.
        _assets = Array.AsReadOnly(_assets.Select(row => new ArchiveAssetRow(row.Manifest, row.Asset)).ToArray());
        Localize();
        RenderThread();
        ListBox list = Control<ListBox>("ArchiveThreads");
        if (list.ItemsSource is IEnumerable<ArchiveThreadRow> rows)
        {
            ArchiveThreadRow[] localized = rows.Select(row => new ArchiveThreadRow(row.Summary)).ToArray();
            _updatingList = true;
            try { list.ItemsSource = localized; list.SelectedItem = localized.FirstOrDefault(row => row.Summary.Id == _selectedThread); }
            finally { _updatingList = false; }
        }
    }
    private void Localize()
    {
        foreach (Control control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is not string key || !control.Classes.Contains("localized")) continue;
            if (control is TextBlock text) text.Text = Texts.T(key);
            if (control is Button button) button.Content = Texts.T(key);
        }
        LocalizeSearch();
        Control<TabItem>("ArchiveReferencesTab").Header = Texts.T("archive.references");
        Control<TabItem>("ArchivePostsTab").Header = Texts.T("archive.posts");
        Control<TabItem>("ArchiveMediaTab").Header = Texts.T("archive.media");
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
        ++_contentRequest;
        CancelMediaLoad();
        CancelReferences();
        _lifetime.Cancel();
        Texts.Changed -= OnLanguageChanged;
        ClosePreview();
    }
}

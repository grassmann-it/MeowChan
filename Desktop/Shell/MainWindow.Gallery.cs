using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan;

public partial class MainWindow
{
    private GalleryViewer? _gallery;
    private Task _galleryCleanupTask = Task.CompletedTask;
    internal Task GalleryCleanupTask => _galleryCleanupTask;
    private void TrackGalleryCleanup(Task task) => _galleryCleanupTask = _galleryCleanupTask.IsCompletedSuccessfully
        ? task : Task.WhenAll(_galleryCleanupTask, task);
    private CancellationTokenSource? _galleryCancellation;
    private IReadOnlyList<ResourceId> _galleryThreads = Array.Empty<ResourceId>();
    private readonly List<GalleryEntry> _galleryEntries = new();
    private int _galleryThreadIndex;
    private int _galleryIndex;
    private int _galleryRequest;
    private Control? _galleryOrigin;
    private Action? _galleryRestoreOrigin;
    private Action? _galleryCompleted;
    private GalleryEntryKey? _galleryStart;
    private bool _galleryStartMissing;
    private bool _galleryAllVersions;
    private int _galleryResolvingRequest = -1;
    private bool _galleryResultsScope;
    private readonly List<string> _galleryWarnings = new();
    private bool _gallerySuspended;
    private bool _galleryVisitedReader;
    private bool _galleryReaderOrigin;
    private bool _galleryLargeReaderOrigin;
    private GalleryViewerState? _galleryReturnState;
    private WorkspacePage _galleryOriginPage;

    internal async Task OpenGalleryAsync(ResourceId id, GalleryEntryKey? start = null, Control? origin = null)
    {
        if (_gallerySuspended && _largeReaderOpen && start != null)
        {
            int existing = _galleryEntries.FindIndex(entry => entry.EntryKey == start);
            if (existing >= 0)
            {
                _galleryIndex = existing;
                await ShowGalleryEntryAsync();
                ReturnToGallery();
                return;
            }
        }
        _galleryStart = start;
        _galleryAllVersions = start != null;
        await StartGalleryAsync(Array.AsReadOnly(new[] { id }), origin);
    }

    internal async Task OpenMediaGalleryAsync(IReadOnlyList<GalleryEntry> entries, GalleryEntryKey start, Control? origin = null)
    {
        _galleryStart = null;
        _galleryAllVersions = true;
        await StartGalleryAsync(Array.Empty<ResourceId>(), origin, entries, start);
    }

    private async Task OpenFavoritesGalleryAsync(IReadOnlyList<GalleryEntry> entries, GalleryEntryKey start, Action completed)
    {
        await OpenMediaGalleryAsync(entries, start);
        if (_gallery != null) _galleryCompleted = completed;
        else if (!_isClosing) completed();
    }

    internal Task OpenSearchGalleryAsync(ArchiveThreadQuery query, int workFilter = 0, Control? origin = null) =>
        OpenSearchGalleryAsync(ArchiveSearchRequest.Create(ArchiveSearchSpec.FromLegacy(query, MeowChan.ArchivePage.WorkChoice(workFilter))), workFilter, origin);

    internal async Task OpenSearchGalleryAsync(ArchiveSearchRequest query, int workFilter = 0, Control? origin = null)
    {
        // Freeze the work projection with the query before asynchronous selection begins.
        IReadOnlySet<string>? eligibleUrls = MeowChan.ArchivePage.CaptureExportWorkUrls(workFilter, _session.GetThreadWorkSnapshots());
        _galleryStart = null;
        _galleryAllVersions = false;
        await StartGalleryAsync(Array.Empty<ResourceId>(), origin, selectThreads: async token =>
        {
            await using ArchiveReader reader = await _session.OpenArchiveReaderAsync(token);
            return await MeowChan.ArchivePage.ResolveExportResultIdsAsync(reader, query, eligibleUrls, token);
        });
    }

    private async Task StartGalleryAsync(IReadOnlyList<ResourceId> ids, Control? origin,
        IReadOnlyList<GalleryEntry>? frozenEntries = null, GalleryEntryKey? entryStart = null,
        Func<CancellationToken, Task<IReadOnlyList<ResourceId>>>? selectThreads = null)
    {
        if (_archivePage?.IsArchiveCleanupOpen == true) return;
        bool largeReaderOrigin = _largeReaderOpen;
        bool readerOrigin = largeReaderOrigin && ArchivePage.ReaderShowsPosts;
        WorkspacePage originPage = _selectedPage;
        CloseGallery(restoreOrigin: false);
        int request = ++_galleryRequest;
        // Keep a retiring video's native drawable attached until VLC has
        // stopped. A new viewer must not replace that host prematurely.
        await _galleryCleanupTask;
        if (_isClosing || request != _galleryRequest || _archivePage?.IsArchiveCleanupOpen == true) return;
        _galleryOrigin = origin;
        _galleryOriginPage = originPage;
        _galleryReaderOrigin = readerOrigin;
        _galleryLargeReaderOrigin = largeReaderOrigin;
        _galleryRestoreOrigin = _archivePage?.CaptureGalleryOrigin();
        _galleryVisitedReader = false;
        _galleryThreads = ids;
        _galleryResultsScope = selectThreads != null;
        _galleryWarnings.Clear();
        _galleryThreadIndex = 0;
        _galleryIndex = 0;
        _galleryEntries.Clear();
        _galleryStartMissing = false;
        if (frozenEntries != null)
        {
            _galleryEntries.AddRange(frozenEntries);
            int start = _galleryEntries.FindIndex(entry => entry.EntryKey == entryStart);
            if (entryStart != null && start < 0) _galleryStartMissing = true;
            _galleryIndex = Math.Max(0, start);
        }
        _galleryCancellation = new CancellationTokenSource();
        _gallery = new GalleryViewer();
        _gallery.SetPosition(string.Empty, false, false);
        ApplyGalleryPreferences();
        WireGalleryActions();
        _gallery.FullscreenRequested += ToggleFullscreen;
        _gallery.ShellKeyRequested += OnShellKeyDown;
        _gallery.ContextOpenChanged += open => QueueGalleryPreference(open, null);
        _gallery.VideoVolumeChanged += volume => QueueGalleryPreference(null, volume);
        _gallery.CloseRequested += () => CloseGallery();
        _gallery.ReadPostsRequested += () => _ = ReadEmptyGalleryPostsAsync();
        _gallery.MoveRequested += delta => _ = MoveGalleryAsync(delta);
        _gallery.ReadPostRequested += entry => _ = ReadGalleryPostAsync(entry);
        _gallery.QuoteRequested += quote => _ = ReadGalleryQuoteAsync(quote);
        _gallery.AssociationRequested += entry =>
        {
            int position = _galleryEntries.FindIndex(item => item.EntryKey == entry.EntryKey);
            if (position >= 0) _galleryIndex = position;
        };
        ContentControl host = this.FindControl<ContentControl>("GalleryHost")!;
        host.Content = _gallery;
        host.IsVisible = true;
        UpdateGalleryFullscreenPresentation();
        _gallery.Focus();
        UpdateGalleryScope();
        if (selectThreads != null)
        {
            CancellationToken token = _galleryCancellation.Token;
            _gallery.SetNotice(Texts.T("gallery.loading"));
            try
            {
                IReadOnlyList<ResourceId> selected = await Task.Run(() => selectThreads(token), token);
                if (request != _galleryRequest || token.IsCancellationRequested || _gallery == null
                    || _archivePage?.IsArchiveCleanupOpen == true) return;
                _galleryThreads = selected;
                UpdateGalleryScope();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                if (request == _galleryRequest) _gallery?.SetNotice(exception.Message);
                return;
            }
        }
        if (_galleryStartMissing) _gallery.SetNotice(Texts.T("gallery.selectedUnavailable"));
        else if (_galleryEntries.Count > 0) await ShowGalleryEntryAsync();
        else await ResolveNextGalleryEntryAsync(request);
    }

    private async Task ResolveNextGalleryEntryAsync(int request)
    {
        if (_gallery == null || _galleryCancellation == null || _galleryResolvingRequest == _galleryRequest) return;
        _galleryResolvingRequest = request;
        UpdateGalleryPosition();
        CancellationToken token = _galleryCancellation.Token;
        try
        {
            while (_galleryThreadIndex < _galleryThreads.Count && _galleryEntries.Count <= _galleryIndex)
            {
                token.ThrowIfCancellationRequested();
                ResourceId id = _galleryThreads[_galleryThreadIndex++];
                _gallery?.SetNotice(Texts.T("gallery.more"));
                bool allVersions = _galleryAllVersions;
                GalleryThreadResult? result = await Task.Run(async () =>
                {
                    await using ArchiveReader reader = await _session.OpenArchiveReaderAsync(token);
                    ArchivedThreadView? thread = await reader.ReadThreadAsync(id, token);
                    if (thread == null) return null;
                    List<MediaAssetSnapshot> manifests = new();
                    List<string> errors = new();
                    foreach (string directory in thread.MediaDirectories)
                    {
                        token.ThrowIfCancellationRequested();
                        try { manifests.Add(await MediaAssetReader.OpenAsync(directory, token)); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception) { errors.Add(directory + ": " + exception.Message); }
                    }
                    GalleryThreadResult built = GallerySelection.Build(thread, manifests, allVersions, includeOtherMedia: true);
                    List<GalleryEntry> verified = new();
                    foreach (GalleryEntry entry in built.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry.Kind == SavedMediaKind.Other) continue;
                        if (entry.Kind == SavedMediaKind.Video) { verified.Add(entry); continue; }
                        if (KnownImageSuffix(entry.Asset.OriginalFileName ?? entry.Asset.Path)) { verified.Add(entry); continue; }
                        try
                        {
                            if (entry.Asset.Length > GalleryImageDecoder.MaximumBytes) continue;
                            await using Stream stream = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
                            GalleryImageDecoder.Inspect(stream);
                            verified.Add(entry);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception) { errors.Add(entry.Asset.Path + ": " + exception.Message); }
                    }
                    return new GalleryThreadResult(verified.AsReadOnly(), new List<string>(built.Warnings.Concat(errors)));
                }, token);
                if (request != _galleryRequest || token.IsCancellationRequested || _gallery == null
                    || _archivePage?.IsArchiveCleanupOpen == true) return;
                if (result == null) continue;
                if (_galleryStart != null)
                {
                    int start = -1;
                    for (int i = 0; i < result.Entries.Count; i++)
                        if (result.Entries[i].EntryKey == _galleryStart) { start = i; break; }
                    if (start >= 0) _galleryIndex = _galleryEntries.Count + start;
                    else _galleryStartMissing = true;
                    _galleryStart = null;
                }
                _galleryEntries.AddRange(result.Entries);
                _galleryWarnings.AddRange(result.Warnings);
                _gallery.SetWarnings(string.Join(Environment.NewLine, _galleryWarnings.Distinct()));
            }
            if (request != _galleryRequest || _gallery == null) return;
            if (_galleryStartMissing) _gallery.SetNotice(Texts.T("gallery.selectedUnavailable"));
            else if (_galleryIndex < _galleryEntries.Count) await ShowGalleryEntryAsync();
            else
            {
                // A lookahead over image-less threads must keep the last image navigable.
                if (_galleryEntries.Count > 0)
                {
                    _galleryIndex = _galleryEntries.Count - 1;
                    await ShowGalleryEntryAsync();
                }
                else _gallery.SetEmpty(Texts.T("gallery.empty"), _galleryThreads.Count > 0);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (request == _galleryRequest) _gallery?.SetNotice(exception.Message); }
        finally
        {
            if (_galleryResolvingRequest == request)
            {
                _galleryResolvingRequest = -1;
                if (request == _galleryRequest) UpdateGalleryPosition();
            }
        }
    }

    private void UpdateGalleryScope()
    {
        ResourceId? thread = _galleryThreads.FirstOrDefault() ?? _galleryEntries.FirstOrDefault()?.Key.Thread;
        string scope = _galleryResultsScope
            ? Texts.T("gallery.results") + " · " + _galleryThreads.Count + " " + Texts.T("gallery.threads")
            : Texts.T("gallery.thread") + " · " + (thread == null ? string.Empty
                : thread.Source + " /" + thread.Board + "/ · " + thread.Thread);
        _gallery?.SetScope(scope);
    }

    private async Task ReadEmptyGalleryPostsAsync()
    {
        ResourceId? thread = _galleryThreads.FirstOrDefault();
        if (thread == null || !NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        OpenGalleryReader();
        SuspendGallery();
        await ArchivePage.LoadThreadAsync(thread);
    }

    private static bool KnownImageSuffix(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";

    private async Task MoveGalleryAsync(int delta)
    {
        if (_gallery == null || _galleryCancellation == null || _galleryResolvingRequest == _galleryRequest) return;
        if (delta < 0 && _galleryIndex == 0) return;
        _galleryIndex += delta;
        if (_galleryIndex >= _galleryEntries.Count) await ResolveNextGalleryEntryAsync(_galleryRequest);
        else await ShowGalleryEntryAsync();
    }

    private async Task ShowGalleryEntryAsync()
    {
        GalleryViewer? viewer = _gallery;
        if (viewer == null || _galleryCancellation == null || _galleryIndex >= _galleryEntries.Count) return;
        UpdateGalleryPosition();
        viewer.SetAssociations(_galleryEntries);
        GalleryEntry entry = _galleryEntries[_galleryIndex];
        await viewer.ShowAsync(entry, _galleryCancellation.Token);
        if (ReferenceEquals(_gallery, viewer) && _galleryIndex < _galleryEntries.Count && _galleryEntries[_galleryIndex].EntryKey == entry.EntryKey)
            RefreshGalleryFavorite();
    }

    private void UpdateGalleryPosition()
    {
        if (_gallery == null) return;
        if (_galleryIndex >= _galleryEntries.Count)
        {
            _gallery.SetPosition(string.Empty, false, false);
            return;
        }
        // MoveGalleryAsync cannot accept navigation while resolving. Do not
        // expose enabled arrows before that guard is released after decoding.
        bool ready = _galleryResolvingRequest != _galleryRequest;
        _gallery.SetPosition((_galleryIndex + 1) + (_galleryThreadIndex < _galleryThreads.Count
            ? " · " + Texts.T("gallery.more") : " / " + _galleryEntries.Count),
            ready && _galleryIndex > 0,
            ready && (_galleryIndex + 1 < _galleryEntries.Count || _galleryThreadIndex < _galleryThreads.Count));
    }

    private async Task ReadGalleryPostAsync(GalleryEntry entry)
    {
        if (entry.Post == null || !NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        int request = _galleryRequest;
        OpenGalleryReader();
        SuspendGallery();
        await ArchivePage.LoadThreadAsync(entry.Key.Thread);
        if (request == _galleryRequest && _gallerySuspended && _gallery != null)
            await ArchivePage.FocusPostAsync(entry.Post.PostId);
    }

    private async Task ReadGalleryQuoteAsync(ArchiveQuote quote)
    {
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        OpenGalleryReader();
        SuspendGallery();
        await ArchivePage.NavigateQuoteAsync(new ArchiveQuoteRow(quote));
    }

    private void OpenGalleryReader()
    {
        if (_largeReaderOpen) return;
        OpenLargeReader();
        ArchivePage.SetReaderScope(_galleryThreads.Count > 0 ? _galleryThreads
            : _galleryEntries.Select(entry => entry.Key.Thread).Distinct().ToArray());
    }

    private void SuspendGallery()
    {
        if (_gallery == null) return;
        _galleryReturnState = _gallery.CaptureState();
        TrackGalleryCleanup(_gallery.SuspendMediaAsync());
        _gallerySuspended = true;
        _galleryVisitedReader = true;
        this.FindControl<ContentControl>("GalleryHost")!.IsVisible = false;
        UpdateGalleryFullscreenPresentation();
        ArchivePage.GalleryReturn = ReturnToGallery;
        ArchivePage.ShowGalleryReturn(true);
    }

    private void ReturnToGallery()
    {
        if (_archivePage?.IsArchiveCleanupOpen == true) return;
        if (!_gallerySuspended || _gallery == null) return;
        _gallerySuspended = false;
        ArchivePage.ShowGalleryReturn(false);
        this.FindControl<ContentControl>("GalleryHost")!.IsVisible = true;
        UpdateGalleryFullscreenPresentation();
        if (_galleryReturnState != null) _gallery.RestoreState(_galleryReturnState);
        TrackGalleryCleanup(_gallery.ResumeMediaAsync());
        RefreshGalleryFavorite();
        _gallery.Focus();
    }

    private void CloseGallery(bool restoreOrigin = true)
    {
        _archivePage?.CancelPendingGalleryOpen();
        TrackGalleryCleanup(FlushGalleryPreferencesAsync());
        GalleryEntry? current = _galleryIndex >= 0 && _galleryIndex < _galleryEntries.Count ? _galleryEntries[_galleryIndex] : null;
        bool followReader = restoreOrigin && _galleryReaderOrigin;
        bool restorePage = restoreOrigin && _galleryVisitedReader;
        Action? restore = _galleryRestoreOrigin;
        Action? completed = _galleryCompleted;
        _galleryCompleted = null;
        _gallerySuspended = false;
        _galleryVisitedReader = false;
        if (_archivePage != null) { _archivePage.ShowGalleryReturn(false); _archivePage.GalleryReturn = null; }
        ++_galleryRequest;
        _galleryCancellation?.Cancel(); _galleryCancellation?.Dispose(); _galleryCancellation = null;
        GalleryViewer? retiring = _gallery;
        _gallery = null;
        ContentControl? host = this.FindControl<ContentControl>("GalleryHost");
        if (host != null) host.IsVisible = false;
        UpdateGalleryFullscreenPresentation();
        if (retiring != null) TrackGalleryCleanup(StopAndDetachGalleryAsync(retiring, host));
        _galleryRestoreOrigin = null;
        if (followReader)
        {
            // Preserve explicit group/bookmark reader origins when closing a gallery.
            if (_readerGroupReturn != null || _postBookmarkReaderReturn != null) restore?.Invoke();
            else if (current?.Post != null) _ = FollowGalleryPostAsync(current);
            else restore?.Invoke();
        }
        else if (restorePage)
        {
            if (!_galleryLargeReaderOrigin) CloseLargeReader(restoreOrigin: false);
            restore?.Invoke();
            if (_selectedPage != _galleryOriginPage) NavigateWorkspace(_galleryOriginPage, activateArchive: false);
            (_archivePage == null ? _galleryOrigin : _archivePage.GalleryOriginFocusTarget(_galleryOrigin))?.Focus();
        }
        else if (restoreOrigin) (_archivePage == null ? _galleryOrigin : _archivePage.GalleryOriginFocusTarget(_galleryOrigin))?.Focus();
        _galleryOrigin = null;
        _galleryResolvingRequest = -1;
        if (restoreOrigin && !_isClosing) completed?.Invoke();
    }

    private void UpdateGalleryFullscreenPresentation()
    {
        ContentControl host = WorkspaceControl<ContentControl>("GalleryHost");
        bool immersive = WindowState == WindowState.FullScreen
            && _gallery != null && !_gallerySuspended && host.IsVisible;
        // The shell caption is client content, so native fullscreen cannot hide it.
        // Span its existing row without changing the underlying page/reader layout.
        WorkspaceControl<Border>("WorkspaceTitleBarFrame").IsVisible = !immersive;
        Grid.SetRow(host, immersive ? 0 : 1);
        Grid.SetRowSpan(host, immersive ? 4 : 3);
        RefreshReadingResumePresentation();
    }

    private static async Task StopAndDetachGalleryAsync(GalleryViewer retiring, ContentControl? host)
    {
        // VLC requires its drawable to remain valid until native Stop returns.
        // Hiding preserves it; removing the viewer destroys its native child.
        await retiring.StopAsync();
        if (host != null && ReferenceEquals(host.Content, retiring)) host.Content = null;
    }

    private async Task FollowGalleryPostAsync(GalleryEntry entry)
    {
        int request = _galleryRequest;
        if (!_largeReaderOpen) OpenGalleryReader();
        if (ArchivePage.CurrentThread?.Summary.Id != entry.Key.Thread)
            await ArchivePage.LoadThreadAsync(entry.Key.Thread);
        if (request == _galleryRequest && _largeReaderOpen && entry.Post != null)
            await ArchivePage.FocusPostAsync(entry.Post.PostId);
    }

}

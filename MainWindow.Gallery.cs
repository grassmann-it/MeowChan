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
    private CancellationTokenSource? _galleryCancellation;
    private IReadOnlyList<ResourceId> _galleryThreads = Array.Empty<ResourceId>();
    private readonly List<GalleryEntry> _galleryEntries = new();
    private int _galleryThreadIndex;
    private int _galleryIndex;
    private int _galleryRequest;
    private Control? _galleryOrigin;
    private Action? _galleryRestoreOrigin;
    private GalleryEntryKey? _galleryStart;
    private bool _galleryStartMissing;
    private bool _galleryAllVersions;
    private int _galleryResolvingRequest = -1;
    private bool _galleryResultsScope;
    private readonly List<string> _galleryWarnings = new();
    private bool _gallerySuspended;
    private bool _galleryVisitedReader;
    private GalleryViewerState? _galleryReturnState;
    private WorkspacePage _galleryOriginPage;

    internal async Task OpenGalleryAsync(ResourceId id, GalleryEntryKey? start = null, Control? origin = null)
    {
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

    internal async Task OpenSearchGalleryAsync(ArchiveThreadQuery query, int workFilter = 0, Control? origin = null)
    {
        // Freeze the work projection with the query before asynchronous selection begins.
        IReadOnlyDictionary<string, ThreadWorkSnapshot> work = _session.GetThreadWorkSnapshots()
            .GroupBy(item => item.Url, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _galleryStart = null;
        _galleryAllVersions = false;
        await StartGalleryAsync(Array.Empty<ResourceId>(), origin, selectThreads: async token =>
        {
            await using ArchiveReader reader = await _session.OpenArchiveReaderAsync(token);
            if (workFilter == 0) return await reader.SelectThreadIdsAsync(query, token);
            IReadOnlyList<ArchiveThreadSummary> summaries = await reader.SelectThreadSummariesAsync(query, token);
            return summaries.Where(summary => MeowChan.ArchivePage.WorkMatches(work.GetValueOrDefault(summary.Url), workFilter))
                .Select(summary => summary.Id).ToArray();
        });
    }

    private async Task StartGalleryAsync(IReadOnlyList<ResourceId> ids, Control? origin,
        IReadOnlyList<GalleryEntry>? frozenEntries = null, GalleryEntryKey? entryStart = null,
        Func<CancellationToken, Task<IReadOnlyList<ResourceId>>>? selectThreads = null)
    {
        CloseGallery();
        int request = ++_galleryRequest;
        _galleryOrigin = origin;
        _galleryOriginPage = _selectedPage;
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
        _gallery.CloseRequested += CloseGallery;
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
        _gallery.Focus();
        UpdateGalleryScope();
        if (selectThreads != null)
        {
            CancellationToken token = _galleryCancellation.Token;
            _gallery.SetNotice(Texts.T("gallery.loading"));
            try
            {
                IReadOnlyList<ResourceId> selected = await Task.Run(() => selectThreads(token), token);
                if (request != _galleryRequest || token.IsCancellationRequested || _gallery == null) return;
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
                    GalleryThreadResult built = GallerySelection.Build(thread, manifests, allVersions);
                    List<GalleryEntry> verified = new();
                    foreach (GalleryEntry entry in built.Entries)
                    {
                        token.ThrowIfCancellationRequested();
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
                if (request != _galleryRequest || token.IsCancellationRequested || _gallery == null) return;
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
        finally { if (_galleryResolvingRequest == request) _galleryResolvingRequest = -1; }
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
        viewer.SetPosition((_galleryIndex + 1) + (_galleryThreadIndex < _galleryThreads.Count
            ? " · " + Texts.T("gallery.more") : " / " + _galleryEntries.Count),
            _galleryIndex > 0, _galleryIndex + 1 < _galleryEntries.Count || _galleryThreadIndex < _galleryThreads.Count);
        viewer.SetAssociations(_galleryEntries);
        await viewer.ShowAsync(_galleryEntries[_galleryIndex], _galleryCancellation.Token);
    }

    private async Task ReadGalleryPostAsync(GalleryEntry entry)
    {
        if (entry.Post == null || !NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        int request = _galleryRequest;
        SuspendGallery();
        await ArchivePage.LoadThreadAsync(entry.Key.Thread);
        if (request == _galleryRequest && _gallerySuspended && _gallery != null)
            ArchivePage.ShowPost(entry.Post.PostId);
    }

    private async Task ReadGalleryQuoteAsync(ArchiveQuote quote)
    {
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        SuspendGallery();
        await ArchivePage.NavigateQuoteAsync(new ArchiveQuoteRow(quote));
    }

    private void SuspendGallery()
    {
        if (_gallery == null) return;
        _galleryReturnState = _gallery.CaptureState();
        _gallerySuspended = true;
        _galleryVisitedReader = true;
        this.FindControl<ContentControl>("GalleryHost")!.IsVisible = false;
        ArchivePage.GalleryReturn = ReturnToGallery;
        ArchivePage.ShowGalleryReturn(true);
    }

    private void ReturnToGallery()
    {
        if (!_gallerySuspended || _gallery == null) return;
        _gallerySuspended = false;
        ArchivePage.ShowGalleryReturn(false);
        this.FindControl<ContentControl>("GalleryHost")!.IsVisible = true;
        if (_galleryReturnState != null) _gallery.RestoreState(_galleryReturnState);
        _gallery.Focus();
    }

    private void CloseGallery()
    {
        _archivePage?.CancelPendingGalleryOpen();
        bool restorePage = _galleryVisitedReader;
        _gallerySuspended = false;
        _galleryVisitedReader = false;
        if (_archivePage != null) { _archivePage.ShowGalleryReturn(false); _archivePage.GalleryReturn = null; }
        ++_galleryRequest;
        _galleryCancellation?.Cancel(); _galleryCancellation?.Dispose(); _galleryCancellation = null;
        _gallery?.Stop(); _gallery = null;
        ContentControl? host = this.FindControl<ContentControl>("GalleryHost");
        if (host != null) { host.IsVisible = false; host.Content = null; }
        if (restorePage) _galleryRestoreOrigin?.Invoke();
        _galleryRestoreOrigin = null;
        if (restorePage && _selectedPage != _galleryOriginPage) NavigateWorkspace(_galleryOriginPage);
        _galleryOrigin?.Focus(); _galleryOrigin = null;
        _galleryResolvingRequest = -1;
    }
}

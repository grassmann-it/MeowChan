using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.VisualTree;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan;

public partial class MainWindow
{
    private readonly PlaybackArbitrationGate _playbackGate = new();
    private GalleryViewer? _gallery;
    private Task _galleryCleanupTask = Task.CompletedTask;
    // Only actual stop/detach drains own native input/drawable lifetime; preference failures do not.
    private Task _galleryNativeCleanupTask = Task.CompletedTask;
    private void TrackNativeGalleryCleanup(Task task)
    {
        _galleryNativeCleanupTask = _galleryNativeCleanupTask.IsCompletedSuccessfully
            ? task : Task.WhenAll(_galleryNativeCleanupTask, task);
        TrackGalleryCleanup(task);
    }
    internal Task GalleryCleanupTask => _galleryCleanupTask;
    private void TrackGalleryCleanup(Task task) => _galleryCleanupTask = _galleryCleanupTask.IsCompletedSuccessfully
        ? task : Task.WhenAll(_galleryCleanupTask, task);
    private CancellationTokenSource? _galleryCancellation;
    private IReadOnlyList<ResourceId> _galleryThreads = Array.Empty<ResourceId>();
    private readonly List<GalleryEntry> _galleryEntries = new();
    private ThreadGalleryPositions? _galleryThreadPositions;
    private long _galleryAssociationsRevision;
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
    // True while the gallery overlay actually covers the shell content (open, visible, not suspended for the reader).
    private bool _galleryCoversWorkspace;
    private readonly ReaderGalleryAdornerCoverage _galleryReaderAdorners = new();
    private bool _sessionDirtyWhileCovered;
    private bool _galleryVisitedReader;
    private bool _galleryReaderOrigin;
    private bool _galleryLargeReaderOrigin;
    private GalleryViewerState? _galleryReturnState;
    private WorkspacePage _galleryOriginPage;
    // F4/F7: where a gallery opened on the Media page closes back to (selection, view, the opened tile), taken while the page is still in front.
    private MediaLibraryReturnPoint? _galleryMediaReturn;

    // Direct archive list/media-menu delegates share the same contract as the specialized wrappers.
    // Downloads, Favorites and Media retain their existing independent entry contracts.
    private Func<bool>? ArchiveGalleryHandoffGuard() => _selectedPage == WorkspacePage.Archive || _largeReaderOpen
        ? _archivePage?.CaptureGalleryHandoffGuard() : null;

    // A revoked Archive handoff can retain its guard until native cleanup drains.
    // Like the fallback guard, it belongs to Archive/reader requests, not independent Downloads opens.
    private Func<bool>? ArchiveReaderMediaActivationGuard() => _selectedPage == WorkspacePage.Archive || _largeReaderOpen
        ? _archivePage?.ReaderMediaActivationGuard : null;

    internal async Task OpenGalleryAsync(ResourceId id, GalleryEntryKey? start = null, Control? origin = null)
    {
        Func<bool>? activationRequest = ArchiveReaderMediaActivationGuard();
        Func<bool>? readerRequest = activationRequest ?? ArchiveGalleryHandoffGuard();
        if (readerRequest?.Invoke() == false) return;
        if (activationRequest == null && _gallerySuspended && _largeReaderOpen && start != null)
        {
            int existing = _galleryEntries.FindIndex(entry => entry.EntryKey == start);
            if (existing >= 0)
            {
                _galleryIndex = existing;
                await ShowGalleryEntryAsync();
                if (readerRequest?.Invoke() == false) return;
                ReturnToGallery();
                return;
            }
        }
        _galleryStart = start;
        _galleryAllVersions = start != null;
        await StartGalleryAsync(Array.AsReadOnly(new[] { id }), origin, stillRequested: readerRequest);
    }

    internal async Task OpenMediaGalleryAsync(IReadOnlyList<GalleryEntry> entries, GalleryEntryKey start, Control? origin = null,
        Func<bool>? stillRequested = null, ThreadMediaPositions? threadMedia = null, ThreadGalleryPositions? threadPositions = null)
    {
        stillRequested ??= _archivePage?.GallerySelectionGuard ?? ArchiveGalleryHandoffGuard();
        if (stillRequested?.Invoke() == false) return;
        _galleryStart = null;
        _galleryAllVersions = true;
        // StartGalleryAsync rechecks this after retiring native media, alongside its own request revision.
        await StartGalleryAsync(Array.Empty<ResourceId>(), origin, entries, start, stillRequested: stillRequested,
            threadPositions: threadPositions ?? (threadMedia == null ? null : ThreadGalleryPositions.Create(threadMedia, entries, capturedOrder: false)));
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
        Func<bool>? stillRequested = _archivePage?.GallerySelectionGuard ?? ArchiveGalleryHandoffGuard();
        if (stillRequested?.Invoke() == false) return;
        // Freeze the work projection with the query before asynchronous selection begins.
        IReadOnlySet<string>? eligibleUrls = MeowChan.ArchivePage.CaptureExportWorkUrls(workFilter, _session.GetThreadWorkSnapshots());
        _galleryStart = null;
        _galleryAllVersions = false;
        await StartGalleryAsync(Array.Empty<ResourceId>(), origin, selectThreads: async token =>
        {
            await using ArchiveReader reader = await _session.OpenArchiveReaderAsync(token);
            return await MeowChan.ArchivePage.ResolveExportResultIdsAsync(reader, query, eligibleUrls, token);
        }, stillRequested: stillRequested);
    }

    internal GalleryReturnTarget GalleryReturnTargetFor(Control? origin, bool readerOrigin, WorkspacePage originPage) =>
        // Reader-origin is authoritative: a stale or reused media control must never relabel a reader gallery.
        readerOrigin ? GalleryReturnTarget.Thread
        : originPage == WorkspacePage.Media || origin != null && ReferenceEquals(origin, _mediaPage?.Grid)
            ? GalleryReturnTarget.Media
        : originPage switch
        {
            WorkspacePage.Archive => GalleryReturnTarget.Archive,
            WorkspacePage.Downloads => GalleryReturnTarget.Downloads,
            WorkspacePage.Media => GalleryReturnTarget.Media,
            // QW6: the queue walk started on the Tagging page (its "Start/Continue" button, a row's "Continue here") returns there.
            WorkspacePage.Tagging => GalleryReturnTarget.Tagging,
            _ => GalleryReturnTarget.None
        };

    private async Task StartGalleryAsync(IReadOnlyList<ResourceId> ids, Control? origin,
        IReadOnlyList<GalleryEntry>? frozenEntries = null, GalleryEntryKey? entryStart = null,
        Func<CancellationToken, Task<IReadOnlyList<ResourceId>>>? selectThreads = null, Func<bool>? stillRequested = null,
        ThreadGalleryPositions? threadPositions = null)
    {
        if (stillRequested?.Invoke() == false || _archivePage?.IsArchiveCleanupOpen == true) return;
        await _playbackGate.EnterGalleryAsync(() => Task.WhenAll(
            _archivePage?.ReleaseInlineVideos() ?? Task.CompletedTask,
            _mediaPage?.ReleasePanelVideos() ?? Task.CompletedTask));
        if (stillRequested?.Invoke() == false || _archivePage?.IsArchiveCleanupOpen == true) return;
        bool largeReaderOrigin = _largeReaderOpen;
        bool readerOrigin = largeReaderOrigin && ArchivePage.ReaderShowsPosts;
        WorkspacePage originPage = _selectedPage;
        // Retiring the previous viewer is an internal handoff, not a user cancellation.
        // Navigation/explicit close still invalidate the page request while cleanup is held.
        CloseGalleryCore(restoreOrigin: false, cancelPendingOpen: false);
        int request = ++_galleryRequest;
        // Keep a retiring video's native drawable attached until VLC has
        // stopped. A new viewer must not replace that host prematurely.
        await _galleryCleanupTask;
        if (_isClosing || request != _galleryRequest || _archivePage?.IsArchiveCleanupOpen == true
            || stillRequested?.Invoke() == false) return;
        _galleryOrigin = origin;
        _galleryOriginPage = originPage;
        _galleryMediaReturn = CaptureGalleryMediaReturn(GalleryReturnTargetFor(origin, readerOrigin, originPage));
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
        _galleryThreadPositions = threadPositions;
        ++_galleryAssociationsRevision;
        _galleryStartMissing = false;
        if (frozenEntries != null)
        {
            _galleryEntries.AddRange(frozenEntries);
            int start = _galleryEntries.FindIndex(entry => entry.EntryKey == entryStart);
            if (entryStart != null && start < 0) _galleryStartMissing = true;
            _galleryIndex = Math.Max(0, start);
        }
        _galleryCancellation = new CancellationTokenSource();
        // The gallery's way back names where closing returns to: the Medien grid (CloseGallery focuses the tile), the thread
        // in the reader, or the Archive/Downloads list it was opened from.
        _gallery = new GalleryViewer
        {
            ReturnTarget = GalleryReturnTargetFor(origin, readerOrigin, originPage),
            ProviderContext = BooruGalleryContext,
            ThreadPositions = threadPositions
        };
        ConfigureGalleryScrubPreview();
        _gallery.ApplyWorkView(ConfiguredWorkView); // WV4: the toolbar and "More" of the gallery follow the window's work view
        UpdateMediaReturn(); // the gallery owns the way back while it is open
        GalleryViewer diagnosticOwner = _gallery;
        _gallery.DiagnosticNotice += notice =>
        {
            if (_isClosing || !ReferenceEquals(_gallery, diagnosticOwner)) return;
            string context = notice.Entry is { } entry
                ? entry.Key.Thread.Source + "/" + entry.Key.Thread.Board + "/" + entry.Key.Thread.Thread + "/" + entry.Asset.AssetId
                : "gallery";
            context += "@" + notice.Revision;
            _feedback.Record(notice.Operation == "source-warning" ? FeedbackCategory.Warning : FeedbackCategory.Error,
                "gallery." + notice.Operation, context, notice.Message, faultKey: "gallery." + notice.Operation);
        };
        _gallery.SetPosition(string.Empty, false, false);
        ApplyGalleryPreferences();
        WireGalleryActions();
        _gallery.FullscreenRequested += ToggleFullscreen;
        _gallery.ShortcutHelpRequested += context => { if (!_isClosing) ShowShellHelp(context); };
        _gallery.ShellKeyRequested += OnShellKeyDown;
        GalleryViewer preferenceOwner = _gallery;
        _gallery.ContextOpenChanged += open =>
        { if (ReferenceEquals(_gallery, preferenceOwner) && !_isClosing) QueueGalleryPreference(open, null); };
        _gallery.PanelWidthChanged += width =>
        { if (ReferenceEquals(_gallery, preferenceOwner) && !_isClosing) QueueGalleryPanelWidth(width); };
        _gallery.VideoVolumeChanged += volume =>
        { if (ReferenceEquals(_gallery, preferenceOwner) && !_isClosing) QueueGalleryPreference(null, volume); };
        _gallery.PlaybackPreferencesChanged += preferences =>
        { if (ReferenceEquals(_gallery, preferenceOwner) && !_isClosing) QueueGalleryPlaybackPreference(preferences); };
        _gallery.WheelModeChanged += mode => OnGalleryWheelModeChanged(preferenceOwner, mode);
        _gallery.LayoutChoiceChanged += (mode, choice) => OnGalleryLayoutChoiceChanged(preferenceOwner, mode, choice);
        _gallery.LayoutResetPerformed += (mode, previous) => OnGalleryLayoutReset(preferenceOwner, mode, previous);
        _gallery.CloseRequested += () => CloseGallery();
        _gallery.TaggingModeRequested += tag => { if (ReferenceEquals(_gallery, preferenceOwner)) SwitchGalleryTagging(tag); };
        // Open actions nobody awaits (interaction audit): a failure is logged and shown, never an unobserved task (OpenActions).
        _gallery.ReadPostsRequested += () => OpenActions.Start(ReadEmptyGalleryPostsAsync, "gallery.readPosts");
        _gallery.MoveRequested += delta => OpenActions.Start(() => MoveGalleryAsync(delta), "gallery.move");
        _gallery.ReadPostRequested += entry => OpenActions.Start(() => ReadGalleryPostAsync(entry), "gallery.readPost");
        _gallery.QuoteRequested += quote => OpenActions.Start(() => ReadGalleryQuoteAsync(quote), "gallery.readQuote");
        _gallery.AssociationRequested += entry =>
        {
            int position = _galleryEntries.FindIndex(item => item.EntryKey == entry.EntryKey);
            if (position >= 0) { _galleryIndex = position; UpdateGalleryPosition(); RefreshGalleryMediaTags(); }
        };
        ContentControl host = GalleryHost;
        host.Content = _gallery;
        host.IsVisible = true;
        HideFeedbackForGallery();
        UpdateGalleryPresentation();
        _gallery.Focus();
        UpdateGalleryInteractionAvailability();
        UpdateGalleryScope();
        if (selectThreads != null)
        {
            CancellationToken token = _galleryCancellation.Token;
            GalleryViewer resolvingViewer = _gallery;
            int mediaGeneration = resolvingViewer.MediaRequestGeneration;
            _gallery.SetLoadingNotice(Texts.T("gallery.loading"));
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
                if (request == _galleryRequest && ReferenceEquals(_gallery, resolvingViewer))
                {
                    Task failure = resolvingViewer.SetMediaUnavailable(UserFacingError.Report(exception, "Resolving the gallery media"), mediaGeneration);
                    TrackNativeGalleryCleanup(failure);
                    await failure;
                }
                return;
            }
        }
        if (_galleryStartMissing) _gallery.SetNotice(Texts.T("gallery.selectedUnavailable"));
        else if (_galleryEntries.Count > 0) await ShowGalleryEntryAsync();
        else await ResolveNextGalleryEntryAsync(request, _galleryIndex);
    }

    private async Task ResolveNextGalleryEntryAsync(int request, int requestedIndex)
    {
        if (_gallery == null || _galleryCancellation == null || _galleryResolvingRequest == _galleryRequest) return;
        _galleryResolvingRequest = request;
        UpdateGalleryPosition();
        CancellationToken token = _galleryCancellation.Token;
        GalleryViewer resolvingViewer = _gallery;
        int mediaGeneration = resolvingViewer.MediaRequestGeneration;
        bool continuing = resolvingViewer.Filmstrip.CurrentIndex >= 0;
        bool Current() => request == _galleryRequest && !token.IsCancellationRequested && ReferenceEquals(_gallery, resolvingViewer)
            && _archivePage?.IsArchiveCleanupOpen != true;
        try
        {
            while (_galleryThreadIndex < _galleryThreads.Count && _galleryEntries.Count <= requestedIndex)
            {
                token.ThrowIfCancellationRequested();
                ResourceId id = _galleryThreads[_galleryThreadIndex];
                resolvingViewer.SetLoadingNotice(Texts.T(continuing ? "gallery.filmstrip.more" : "gallery.more"));
                bool allVersions = _galleryAllVersions;
                bool singleThread = _galleryThreads.Count == 1;
                ArchivedThreadView? thread;
                await using (ArchiveReader reader = await _session.OpenArchiveReaderAsync(token))
                    thread = await reader.ReadThreadAsync(id, token);
                if (!Current()) return;
                if (thread == null)
                {
                    ++_galleryThreadIndex;
                    continue;
                }
                if (singleThread && thread.MediaPositions.Count > 0)
                    resolvingViewer.SetLoadingNotice(Texts.T("gallery.loading") + " " + Texts.T("thread.media.total", thread.MediaPositions.Count));

                (GalleryThreadResult result, ThreadGalleryPositions? positions) = await Task.Run(async () =>
                {
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
                    return (new GalleryThreadResult(verified.AsReadOnly(), new List<string>(built.Warnings.Concat(errors))),
                        singleThread ? ThreadGalleryPositions.Create(thread.MediaPositions, verified) : null);
                }, token);
                if (!Current()) return;
                if (_galleryStart != null)
                {
                    int start = -1;
                    for (int i = 0; i < result.Entries.Count; i++)
                        if (result.Entries[i].EntryKey == _galleryStart) { start = i; break; }
                    if (start >= 0) requestedIndex = _galleryEntries.Count + start;
                    else _galleryStartMissing = true;
                    _galleryStart = null;
                }
                _galleryEntries.AddRange(result.Entries);
                ++_galleryThreadIndex;
                _galleryThreadPositions = resolvingViewer.ThreadPositions = positions;
                if (positions != null) UpdateGalleryScope();
                ++_galleryAssociationsRevision;
                _galleryWarnings.AddRange(result.Warnings);
                resolvingViewer.SetWarnings(string.Join(Environment.NewLine, _galleryWarnings.Distinct()));
            }
            if (!Current()) return;
            if (_galleryStartMissing) resolvingViewer.SetNotice(Texts.T("gallery.selectedUnavailable"));
            else if (requestedIndex < _galleryEntries.Count)
            {
                _galleryIndex = requestedIndex;
                await ShowGalleryEntryAsync();
            }
            else
            {
                // A lookahead over image-less threads must keep the last image navigable.
                if (_galleryEntries.Count > 0)
                {
                    await ShowGalleryEntryAsync();
                }
                else
                {
                    string message = Texts.T("gallery.empty");
                    if (_galleryThreadPositions is { } media) message += " " + Texts.T("thread.media.total", media.Count);
                    Task empty = resolvingViewer.SetEmpty(message, _galleryThreads.Count > 0);
                    TrackNativeGalleryCleanup(empty);
                    await empty;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (Current())
            {
                string diagnosis = UserFacingError.Report(exception, "Resolving the gallery media");
                if (continuing && mediaGeneration == resolvingViewer.MediaRequestGeneration)
                    resolvingViewer.ReportGalleryContinuationFailure(diagnosis);
                else
                {
                    Task failure = resolvingViewer.SetMediaUnavailable(diagnosis, mediaGeneration);
                    TrackNativeGalleryCleanup(failure);
                    await failure;
                }
            }
        }
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
        int providerPosts = _galleryEntries.Select(entry => entry.Key.Thread).Where(BooruMediaIdentity.IsBooruThread).Distinct().Count();
        // Downloaded provider posts are no chan thread: a window of them is named by its source, a mixed one is a result list.
        string scope = providerPosts > 0 && !_galleryResultsScope
            ? providerPosts == _galleryEntries.Select(entry => entry.Key.Thread).Distinct().Count()
                ? Texts.Count("booru.media.gallery.scope", providerPosts)
                : Texts.T("gallery.results") + " · " + _galleryEntries.Select(entry => entry.Key.Thread).Distinct().Count() + " " + Texts.T("gallery.threads")
            : _galleryResultsScope
            ? Texts.T("gallery.results") + " · " + _galleryThreads.Count + " " + Texts.T("gallery.threads")
            : Texts.T("gallery.thread") + " · " + (thread == null ? string.Empty
                : thread.Source + " /" + thread.Board + "/ · " + thread.Thread);
        if (_galleryThreadPositions is { Unavailable: > 0 } media)
            scope += " · " + Texts.T("thread.media.unavailable", media.Unavailable);
        _gallery?.SetScope(scope);
    }

    private async Task ReadEmptyGalleryPostsAsync()
    {
        ResourceId? thread = _galleryThreads.FirstOrDefault();
        if (thread == null) return;
        int request = _galleryRequest;
        // F1: the gallery stays open (and says so) for a thread the archive does not hold, before anything is suspended.
        if (!await ArchiveHoldsJumpTargetAsync(thread, () => request == _galleryRequest && _gallery != null && !_isClosing)) return;
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        OpenGalleryReader();
        if (!_largeReaderOpen) return;
        SuspendGallery();
        await ArchivePage.LoadThreadAsync(thread);
    }

    private static bool KnownImageSuffix(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";

    private async Task MoveGalleryAsync(int delta)
    {
        if (_gallery == null || _galleryCancellation == null || _galleryResolvingRequest == _galleryRequest) return;
        if (delta < 0 && _galleryIndex == 0) return;
        // A sum of wheel steps never overshoots the ends: not before the first entry, not past the one-past-the-last place that loads the next thread.
        int target = Math.Clamp(_galleryIndex + delta, 0, _galleryEntries.Count);
        if (target == _galleryEntries.Count) await ResolveNextGalleryEntryAsync(_galleryRequest, target);
        else
        {
            _galleryIndex = target;
            await ShowGalleryEntryAsync();
        }
    }

    private async Task ShowGalleryEntryAsync()
    {
        GalleryViewer? viewer = _gallery;
        if (viewer == null || _galleryCancellation == null || _galleryIndex >= _galleryEntries.Count) return;
        UpdateGalleryPosition();
        CancelGalleryPreload(); // the user moved on: whatever was decoding around the previous image stops
        GalleryEntry entry = _galleryEntries[_galleryIndex];
        Task showing = viewer.ShowAsync(entry, _galleryEntries, _galleryAssociationsRevision, _galleryCancellation.Token);
        // Selection is admitted synchronously; read its saved favorite before slow decoding
        // yields. Do not show the previous entry or an invented unmarked state while loading.
        if (ReferenceEquals(_gallery, viewer) && _galleryIndex < _galleryEntries.Count && _galleryEntries[_galleryIndex].EntryKey == entry.EntryKey)
            RefreshGalleryFavorite();
        await showing;
        if (ReferenceEquals(_gallery, viewer) && _galleryIndex < _galleryEntries.Count && _galleryEntries[_galleryIndex].EntryKey == entry.EntryKey)
        {
            RefreshGalleryFavorite();
            StartGalleryPreload(viewer);
        }
    }

    private void UpdateGalleryPosition()
    {
        if (_gallery == null) return;
        bool ready = _galleryResolvingRequest != _galleryRequest;
        bool moreThreads = _galleryThreadIndex < _galleryThreads.Count;
        bool canLoadMore = moreThreads && _galleryEntries.Count > 0;
        _gallery.SetFilmstripContinuation(canLoadMore, !ready && (canLoadMore || _gallery.Filmstrip.Count > 0));
        if (CurrentGalleryPosition() is not { } position)
        {
            _gallery.SetPosition(string.Empty, false, false);
            return;
        }
        // MoveGalleryAsync cannot accept navigation while resolving. Do not
        // expose enabled arrows before that guard is released after decoding.
        // The tagging mode names the place in the whole queue ("Queue 12 of 200") instead of the place in the walk's list (PT3).
        string label = position.Queue ? MediaPositionText.Long(position)! : _galleryThreadPositions == null
            ? position.Number + " / " + position.Count
            : position.Number > 0 ? Texts.T("reader.sidebar.position", position.Number, position.Count)
            : Texts.T("thread.media.total", position.Count);
        _gallery.SetPosition(label,
            ready && _galleryIndex > 0,
            ready && (_galleryIndex + 1 < _galleryEntries.Count || moreThreads), position);
    }

    private GalleryTagPanelPosition? GalleryListPosition() =>
        _galleryIndex < 0 || _galleryIndex >= _galleryEntries.Count ? null
        : new(_galleryIndex + 1, _galleryEntries.Count, _galleryThreadIndex < _galleryThreads.Count);

    private GalleryTagPanelPosition? CurrentGalleryPosition()
    {
        GalleryTagPanelPosition? current = GalleryListPosition();
        if (current == null) return null;
        return TaggingPosition(_galleryEntries[_galleryIndex]) ?? (_galleryThreadPositions is { } media
            ? new(media.NumberAt(_galleryIndex) ?? 0, media.Count, false) : current);
    }

    private async Task ReadGalleryPostAsync(GalleryEntry entry)
    {
        if (entry.Post == null) return;
        int request = _galleryRequest;
        // F1: the gallery stays open (and says so) for a thread the archive does not hold, before anything is suspended.
        if (!await ArchiveHoldsJumpTargetAsync(entry.Key.Thread, () => request == _galleryRequest && _gallery != null && !_isClosing)) return;
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        request = _galleryRequest;
        OpenGalleryReader();
        if (!_largeReaderOpen) return;
        SuspendGallery();
        await ArchivePage.LoadThreadAsync(entry.Key.Thread);
        if (request == _galleryRequest && _gallerySuspended && _gallery != null)
            await ArchivePage.FocusPostAsync(entry.Post.PostId);
    }

    private async Task ReadGalleryQuoteAsync(ArchiveQuote quote)
    {
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        OpenGalleryReader();
        if (!_largeReaderOpen) return;
        SuspendGallery();
        await ArchivePage.NavigateQuoteAsync(new ArchiveQuoteRow(quote));
    }

    private void OpenGalleryReader()
    {
        if (_largeReaderOpen) return;
        OpenLargeReader();
        if (!_largeReaderOpen) return;
        ArchivePage.SetReaderScope(_galleryThreads.Count > 0 ? _galleryThreads
            : _galleryEntries.Select(entry => entry.Key.Thread).Distinct().ToArray());
    }

    private void SuspendGallery()
    {
        if (_gallery == null) return;
        _galleryReturnState = _gallery.CaptureState();
        _gallery.SetInteractionAvailable(false);
        TrackNativeGalleryCleanup(_gallery.SuspendMediaAsync());
        _gallerySuspended = true;
        _galleryVisitedReader = true;
        GalleryHost.IsVisible = false;
        UpdateGalleryPresentation();
        ArchivePage.GalleryReturn = ReturnToGallery;
        ArchivePage.ShowGalleryReturn(true);
        UpdateMediaReturn();
    }

    private void ReturnToGallery()
    {
        if (_archivePage?.IsArchiveCleanupOpen == true) return;
        if (!_gallerySuspended || _gallery == null) return;
        _gallerySuspended = false;
        ArchivePage.ShowGalleryReturn(false);
        GalleryHost.IsVisible = true;
        HideFeedbackForGallery();
        UpdateGalleryPresentation();
        if (_galleryReturnState != null) _gallery.RestoreState(_galleryReturnState);
        TrackGalleryCleanup(_gallery.ResumeMediaAsync());
        UpdateGalleryInteractionAvailability();
        RefreshGalleryFavorite();
        _gallery.Focus();
    }

    private void CloseGallery(bool restoreOrigin = true) => CloseGalleryCore(restoreOrigin, cancelPendingOpen: true);

    private void CloseGalleryCore(bool restoreOrigin, bool cancelPendingOpen)
    {
        GalleryReturnTarget closingTarget = _gallery?.ReturnTarget ?? GalleryReturnTarget.None;
        TagQueueKey? lastTagged = EndGalleryTagging(persist: true); // the tagging mode ends with its gallery and keeps the resume point (PT3)
        if (cancelPendingOpen) _archivePage?.CancelPendingGalleryOpen();
        TrackGalleryCleanup(FlushGalleryPreferencesAsync());
        GalleryEntry? current = _galleryIndex >= 0 && _galleryIndex < _galleryEntries.Count ? _galleryEntries[_galleryIndex] : null;
        bool followReader = restoreOrigin && _galleryReaderOrigin;
        bool restorePage = restoreOrigin && _galleryVisitedReader;
        MediaLibraryReturnPoint? mediaReturn = _galleryMediaReturn;
        _galleryMediaReturn = null;
        Action? restore = _galleryRestoreOrigin;
        Action? completed = _galleryCompleted;
        _galleryCompleted = null;
        _gallerySuspended = false;
        _galleryVisitedReader = false;
        if (_archivePage != null) { _archivePage.ShowGalleryReturn(false); _archivePage.GalleryReturn = null; }
        CancelGalleryPreload();
        ++_galleryRequest;
        _galleryCancellation?.Cancel(); _galleryCancellation?.Dispose(); _galleryCancellation = null;
        GalleryViewer? retiring = _gallery;
        CloseGalleryLayoutToast(); // GL3: the undo of "Reset layout" belongs to the gallery that closes
        _gallery = null;
        ContentControl? host = this.FindControl<ContentControl>("GalleryHost");
        if (host != null) host.IsVisible = false;
        UpdateGalleryPresentation();
        if (cancelPendingOpen) _playbackGate.ExitGallery();
        if (retiring != null) TrackNativeGalleryCleanup(StopAndDetachGalleryAsync(retiring, host));
        if (_isClosing) TrackGalleryCleanup(DisposeGalleryScrubPreviewAsync());
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
        RefreshMediaPageUserData();
        // The media grid itself cannot take focus; its selected tile does, once the refreshed tiles are laid out. Keyed on the way back the
        // gallery named, not on its origin control: a tag-queue walk from the page's header has none (F7). The activation of a page that the
        // reader had replaced (and the refresh of own state just above) rebuilds and swaps the rows after a single focus, which resets focus and
        // scroll; the page's own return restores now and once more after that presentation, as "Back to media" does (F4).
        if (restoreOrigin && !_isClosing && closingTarget == GalleryReturnTarget.Media && _mediaPage != null && _selectedPage == WorkspacePage.Media)
        {
            if (mediaReturn != null) StartMediaPageReturn(mediaReturn, "gallery.mediaReturn");
            else _mediaPage.FocusGrid();
        }
        if (restoreOrigin && !_isClosing) completed?.Invoke();
        UpdateMediaReturn();
        if (restoreOrigin && closingTarget == GalleryReturnTarget.Tagging) ReturnToTaggingPage(lastTagged);
    }

    private MediaLibraryReturnPoint? CaptureGalleryMediaReturn(GalleryReturnTarget target)
    {
        // The grid's own entry has already ended a pending second restore (OpenMediaLibraryGalleryAsync); ending it here as well would also
        // revoke a Booru handoff that still guards this open.
        if (target != GalleryReturnTarget.Media || _mediaPage == null || _selectedPage != WorkspacePage.Media) return null;
        return _mediaPage.CaptureGalleryReturnPoint();
    }

    // The restore runs on after the navigation and nobody awaits it: OpenActions logs and shows a failure instead of dropping it (F8).
    // MediaReturnTask is that observed run, for the smokes that wait for the second restore.
    private void StartMediaPageReturn(MediaLibraryReturnPoint point, string operation)
    {
        MediaPage page = _mediaPage!;
        MediaReturnTask = OpenActions.StartObserved(() => page.ReturnToAsync(point), operation);
    }

    private void UpdateGalleryPresentation()
    {
        ContentControl host = GalleryHost;
        UpdateMediaFocusChrome();
        RefreshReadingResumePresentation();
        SetGalleryCoversWorkspace(_gallery != null && !_gallerySuspended && host.IsVisible);
    }

    /// <summary>
    /// Tracks real coverage rather than Avalonia visibility: the covered pages stay in the tree so drafts, selection,
    /// scroll position and focus survive. Expensive background presentation work is skipped while covered and one
    /// refresh of the newest projection is applied when the content becomes usable again.
    /// </summary>
    private void SetGalleryCoversWorkspace(bool covers)
    {
        if (_galleryCoversWorkspace == covers) return;
        _galleryCoversWorkspace = covers;
        if (covers)
        {
            LayoutUpdated += OnGalleryCoveredLayoutUpdated;
            UpdateGalleryCoveredAdorners();
        }
        else
        {
            LayoutUpdated -= OnGalleryCoveredLayoutUpdated;
            _galleryReaderAdorners.Clear();
        }
        _archivePage?.SetObscuredByGallery(covers);
        _mediaPage?.SetObscuredByGallery(covers);
        if (!covers && _sessionDirtyWhileCovered && !_isClosing)
        {
            _sessionDirtyWhileCovered = false;
            _sessionRefresh.RequestImmediate();
        }
    }

    private void OnGalleryCoveredLayoutUpdated(object? sender, EventArgs e) => UpdateGalleryCoveredAdorners();

    private void UpdateGalleryCoveredAdorners()
    {
        // Ursa pagination adorners live in the window's AdornerLayer, not in
        // ReaderHost. Keeping the covered reader in the tree must not leave its
        // disabled-button masks above the gallery, intercepting native clicks.
        // Include late-created adorners without touching gallery/notification UI.
        Control reader = ReaderHost;
        _galleryReaderAdorners.Update(reader, _archivePage,
            this.GetVisualDescendants().OfType<AdornerLayer>().SelectMany(layer => layer.Children));
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

/// <summary>Temporarily covers only adorners owned by the retained reader/archive.</summary>
internal sealed class ReaderGalleryAdornerCoverage
{
    private readonly Dictionary<Control, IDisposable> _hidden = new();

    internal void Update(Control reader, Control? archive, IEnumerable<Control> adorners)
    {
        foreach (Control adorner in adorners)
        {
            if (_hidden.ContainsKey(adorner)) continue;
            Visual? adorned = adorner.GetValue(AdornerLayer.AdornedElementProperty);
            if (adorned == null || !adorned.GetSelfAndVisualAncestors().Any(ancestor =>
                ReferenceEquals(ancestor, reader) || ReferenceEquals(ancestor, archive))) continue;
            // A disposable priority override preserves bindings/styles/local values,
            // including an adorner that was already hidden before gallery entry.
            // SetValue's nullable result also covers non-reversible local values;
            // retain each actual override until Clear, never just for this update.
            if (adorner.SetValue(Visual.IsVisibleProperty, false, BindingPriority.Animation) is { } visibility)
                _hidden.Add(adorner, visibility);
        }
    }

    internal void Clear()
    {
        foreach (IDisposable visibility in _hidden.Values) visibility.Dispose();
        _hidden.Clear();
    }
}

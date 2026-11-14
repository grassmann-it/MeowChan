using MeowChan.Desktop.WorkViews;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Diagnostics;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private DispatcherTimer? _readerUpdateTimer;
    private bool _checkingReaderUpdate;
    private bool _readerUpdateCatchUp;
    private long _readerCoverageRevision;
    internal long ReaderSummaryChecks { get; private set; }
    internal long ReaderSummaryCoveredSkips { get; private set; }
    internal long ReaderSummaryStaleResults { get; private set; }
    private bool _followReader;
    private ResourceId? _liveReaderId;
    private ArchiveThreadSummary? _pendingReaderUpdate;
    private ArchiveThreadSummary? _latestReaderSummary;
    internal Func<ResourceId, bool>? CanFollowLive { get; set; }
    internal Func<bool>? ReaderRefreshAllowed { get; set; }

    private void InitializeReaderUpdates()
    {
        _readerUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _readerUpdateTimer.Tick += async (_, _) => await CheckReaderUpdatesAsync();
        _readerUpdateTimer.Start();
        // R3: following live is the View menu's "Show new posts automatically" (the command keeps its id and its Ctrl+Shift+L,
        // which the shell routes to ToggleFollowLiveByShortcut); the shortcut must not also toggle through the menu's key handler.
        RegisterViewOption("watch.follow-live", () => _followReader, () => _ = SetFollowLiveAsync(!_followReader),
            isEnabled: () => CanChangeFollowLive, note: FollowLiveNote, shortcutViaRouter: true);
        LocalizeReaderUpdates();
    }

    private void LocalizeReaderUpdates()
    {
        ReaderShowNewPosts.Content = CommandCatalog.Label("reader.show-new-posts");
        ToolTip.SetTip(ReaderShowNewPosts, CommandBindings.Tooltip("reader.show-new-posts"));
        LocalizeReaderUnread();
        LocalizeReaderLink();
        UpdateReaderUpdateControls();
    }

    private void ReaderSnapshotLoaded()
    {
        ResourceId? id = _thread?.Summary.Id;
        if (_liveReaderId != id)
        {
            _followReader = false;
            _liveReaderId = id;
        }
        _pendingReaderUpdate = null;
        _latestReaderSummary = _thread?.Summary;
        UpdateReaderUpdateControls();
    }

    private bool MayFollow(ArchiveThreadSummary? summary) => summary != null && summary.CaptureEnabled
        && summary.SourceState == ThreadSourceState.Active && CanFollowLive?.Invoke(summary.Id) == true;

    /// <summary>Why following live is impossible for this thread, or null when nothing but a transient state is in the way.</summary>
    internal static string? FollowLiveReason(ArchiveThreadSummary? summary, bool watched)
    {
        if (summary == null) return null;
        if (!watched) return Texts.T("reader.live.reason.notWatched");
        if (!summary.CaptureEnabled) return Texts.T("reader.live.reason.captureOff");
        return summary.SourceState switch
        {
            ThreadSourceState.Archived => Texts.T("reader.live.reason.sourceArchived"),
            ThreadSourceState.NotFound => Texts.T("reader.live.reason.sourceGone"),
            _ => null
        };
    }

    private void UpdateReaderUpdateControls()
    {
        ArchiveThreadSummary? summary = _latestReaderSummary;
        if (!MayFollow(summary)) _followReader = false;
        // The status line says whether the new posts are shown automatically (the switch itself is in the View menu).
        TextBlock liveState = ReaderLiveState;
        liveState.Text = Texts.T("reader.live.stateOn");
        WorkViewGate.Apply(liveState, ArchiveWorkView, _followReader);
        // Live state: Required in every view (WorkViewPolicy.Reader), so the gate only records the state.
        WorkViewGate.Apply(ReaderShowNewPosts, ArchiveWorkView, _pendingReaderUpdate != null);
        ReaderShowNewPosts.IsEnabled = !_busy;
        TextBlock notice = ReaderNewPostsNotice;
        WorkViewGate.Apply(notice, ArchiveWorkView, _pendingReaderUpdate != null);
        int added = (_pendingReaderUpdate?.PostCount ?? 0) - (_thread?.PostCount ?? 0);
        notice.Text = _pendingReaderUpdate == null ? string.Empty
            : added > 0 ? Texts.Count("reader.newPostsCount", added) : Texts.T("reader.savedUpdate");
    }

    /// <summary>Whether the open thread's new saved posts are shown without a click (session-wide, reset on a thread switch).</summary>
    internal bool FollowLive => _followReader;

    /// <summary>The switch is usable: the thread is watched with post capture on and an active source, and nothing is running.</summary>
    internal bool CanChangeFollowLive => !_ended && !IsFilenameSearchOpen && !_busy && MayFollow(_latestReaderSummary);

    /// <summary>The tip of the menu entry: what it does and the one cause that is missing right now (also shown while disabled).</summary>
    private string FollowLiveNote()
    {
        ArchiveThreadSummary? summary = _latestReaderSummary;
        string tip = Texts.T("reader.liveHint");
        bool watched = summary != null && CanFollowLive?.Invoke(summary.Id) == true;
        if (FollowLiveReason(summary, watched) is { } reason) tip += Environment.NewLine + reason;
        return tip;
    }

    /// <summary>Switches "Show new posts automatically" for the open thread; refused (and off) where following is impossible.</summary>
    internal async Task SetFollowLiveAsync(bool enabled)
    {
        if (_ended) return;
        _followReader = enabled && MayFollow(_latestReaderSummary);
        UpdateReaderUpdateControls();
        if (_followReader) await CheckReaderUpdatesAsync();
    }

    // The coverage epoch also rejects a read that returns after cover -> uncover. Keep one
    // catch-up flag while that read still owns the slot; its completion starts the latest query.
    private void ReaderCoverageChanged()
    {
        ++_readerCoverageRevision;
        _readerUpdateCatchUp = true;
        if (!_obscuredByGallery && !_checkingReaderUpdate) _ = CheckReaderUpdatesAsync();
    }

    private bool CanCheckReaderUpdate => !_ended && !IsFilenameSearchOpen && _available && !_busy && !_obscuredByGallery
        && _thread != null && _openReader != null && (_largeReader || IsEffectivelyVisible) && _showReader;

    // One summary query at a time. Ordinary timer ticks do not pile up follow-up queries.
    internal async Task CheckReaderUpdatesAsync()
    {
        if (_obscuredByGallery)
        {
            _readerUpdateCatchUp = true;
            ReaderSummaryCoveredSkips++;
            PerformanceDiagnostics.Record("archive.reader.summary", "covered-skip", revision: _readerCoverageRevision);
        }
        if (_checkingReaderUpdate || !CanCheckReaderUpdate) return;
        _checkingReaderUpdate = true;
        try
        {
            do
            {
                _readerUpdateCatchUp = false;
                ResourceId id = _thread!.Summary.Id;
                int request = _contentRequest;
                int interaction = _readerInteractionRevision;
                long coverage = _readerCoverageRevision;
                long diagnostic = PerformanceDiagnostics.NextId();
                bool Current() => !_ended && !IsFilenameSearchOpen && !_obscuredByGallery && coverage == _readerCoverageRevision
                    && request == _contentRequest && _thread?.Summary.Id == id;
                using var measurement = PerformanceDiagnostics.Measure("archive.reader.summary", diagnostic,
                    revision: coverage);
                try
                {
                    ReaderSummaryChecks++;
                    await using ArchiveReader reader = await _openReader!(_lifetime.Token);
                    ArchiveThreadSummary? summary = await reader.ReadThreadSummaryAsync(id, _lifetime.Token);
                    if (!Current())
                    {
                        ReaderSummaryStaleResults++;
                        PerformanceDiagnostics.Record("archive.reader.summary", "stale", diagnostic, revision: coverage);
                    }
                    else
                    {
                        _latestReaderSummary = summary;
                        if (summary == null)
                        {
                            _pendingReaderUpdate = null; _followReader = false;
                        }
                        else if (summary.Revision > _thread!.Summary.Revision && summary.PostCount > _thread.PostCount)
                            _pendingReaderUpdate = summary;
                        if (!MayFollow(summary)) _followReader = false;
                        UpdateReaderUpdateControls();
                        if (_followReader && _pendingReaderUpdate != null && interaction == _readerInteractionRevision && MayRefreshReaderNow)
                            await ShowReaderUpdatesAsync(deliberate: false);
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception error)
                {
                    if (Current()) Message(Texts.T("reader.updateFailed", error.Message), true);
                    PerformanceDiagnostics.Record("archive.reader.summary", "failed", diagnostic, revision: coverage);
                }
            } while (_readerUpdateCatchUp && CanCheckReaderUpdate);
        }
        finally { _checkingReaderUpdate = false; }
    }

    private bool MayRefreshReaderNow => !_ended && !_obscuredByGallery && ReaderShowsPosts
        && !IsReaderDialogOpen && (ReaderRefreshAllowed?.Invoke() ?? true);

    private async void OnShowReaderUpdates(object? sender, RoutedEventArgs e) => await ShowReaderUpdatesAsync();

    // The two shortcuts act where the live bar is shown: when the switch or the button is not usable, nothing happens.
    internal bool CanToggleFollowLiveByShortcut => !_ended && !IsReaderDialogOpen && ReaderStatusLine.IsEffectivelyVisible
        && CanChangeFollowLive;
    internal void ToggleFollowLiveByShortcut()
    {
        if (CanToggleFollowLiveByShortcut) _ = SetFollowLiveAsync(!_followReader);
    }
    internal bool CanShowNewPostsByShortcut => !_ended && !IsReaderDialogOpen && ReaderShowNewPosts.IsEffectivelyVisible
        && ReaderShowNewPosts.IsEnabled;

    internal async Task ShowReaderUpdatesAsync(bool deliberate = true)
    {
        if (_ended || _busy || _thread == null || _pendingReaderUpdate == null || !MayRefreshReaderNow) return;
        ResourceId id = _thread.Summary.Id;
        int previousCount = _thread.PostCount;
        int interactionRevision = _readerInteractionRevision;
        bool follow = _followReader;
        // Guard the inner anchor restoration as well as the final follow target. Capturing
        // permission after LoadThreadAsync returns would already be too late for its focus write.
        using ReaderFocusIntent? focusIntent = deliberate ? null : new(this, requireRefreshAllowed: true);
        // A manual refresh marks where the old view ended; following live keeps its position and sets no mark.
        if (deliberate && previousCount > 0 && _thread.GetPosts(previousCount - 1, 1).FirstOrDefault() is { } lastRead)
            _unreadMark = (id, lastRead.PostId);
        await LoadThreadAsync(id, resetPage: false, preparedThread: null, focusIntent: focusIntent);
        if (_ended || _thread?.Summary.Id != id || _thread.PostCount == 0
            || (!deliberate && (interactionRevision != _readerInteractionRevision || !MayRefreshReaderNow))) return;
        // Manual refresh reveals the first appended post; live following stays at the newest.
        int index = follow ? _thread.PostCount - 1 : Math.Min(previousCount, _thread.PostCount - 1);
        await FocusPostAsync(_thread.GetPosts(index, 1).Single().PostId,
            deliberate ? null : interactionRevision, focusIntent: focusIntent);
    }
}

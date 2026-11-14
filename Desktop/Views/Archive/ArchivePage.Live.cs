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
        LocalizeReaderUpdates();
    }

    private void LocalizeReaderUpdates()
    {
        ReaderFollowLive.Content = CommandCatalog.Label("watch.follow-live");
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
            ReaderFollowLive.IsChecked = false;
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
        CheckBox follow = ReaderFollowLive;
        follow.IsEnabled = !_busy && MayFollow(summary);
        if (!MayFollow(summary)) { _followReader = false; follow.IsChecked = false; }
        // The tip names the one cause that is missing right now, and is shown on the disabled box too.
        string tip = Texts.T("reader.liveHint");
        if (CommandCatalog.ShortcutDisplay("watch.follow-live") is { } followShortcut) tip += " · " + followShortcut;
        bool watched = summary != null && CanFollowLive?.Invoke(summary.Id) == true;
        if (FollowLiveReason(summary, watched) is { } reason) tip += Environment.NewLine + reason;
        ToolTip.SetTip(follow, tip);
        ToolTip.SetShowOnDisabled(follow, true);
        Avalonia.Automation.AutomationProperties.SetHelpText(follow, tip);
        ReaderShowNewPosts.IsVisible = _pendingReaderUpdate != null;
        ReaderShowNewPosts.IsEnabled = !_busy;
        TextBlock notice = ReaderNewPostsNotice;
        notice.IsVisible = _pendingReaderUpdate != null;
        int added = (_pendingReaderUpdate?.PostCount ?? 0) - (_thread?.PostCount ?? 0);
        notice.Text = _pendingReaderUpdate == null ? string.Empty
            : added > 0 ? Texts.T("reader.newPostsCount", added) : Texts.T("reader.savedUpdate");
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
                            ReaderFollowLive.IsChecked = false;
                        }
                        else if (summary.Revision > _thread!.Summary.Revision && summary.PostCount > _thread.PostCount)
                            _pendingReaderUpdate = summary;
                        if (!MayFollow(summary)) { _followReader = false; ReaderFollowLive.IsChecked = false; }
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

    private async void OnReaderFollowLiveChanged(object? sender, RoutedEventArgs e)
    {
        bool enabled = ReaderFollowLive.IsChecked == true;
        _followReader = enabled && MayFollow(_latestReaderSummary);
        if (_followReader) await CheckReaderUpdatesAsync();
    }

    private async void OnShowReaderUpdates(object? sender, RoutedEventArgs e) => await ShowReaderUpdatesAsync();

    // The two shortcuts act on the same controls as the live bar: when the box or the button is not usable, nothing happens.
    internal bool CanToggleFollowLiveByShortcut => !_ended && !IsReaderDialogOpen && ReaderFollowLive.IsEffectivelyVisible
        && ReaderFollowLive.IsEnabled;
    internal void ToggleFollowLiveByShortcut()
    {
        if (CanToggleFollowLiveByShortcut) ReaderFollowLive.IsChecked = ReaderFollowLive.IsChecked != true;
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

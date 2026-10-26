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
        Control<CheckBox>("ReaderFollowLive").Content = CommandCatalog.Label("watch.follow-live");
        ToolTip.SetTip(Control<CheckBox>("ReaderFollowLive"), Texts.T("reader.liveHint"));
        Control<Button>("ReaderShowNewPosts").Content = CommandCatalog.Label("reader.show-new-posts");
        UpdateReaderUpdateControls();
    }

    private void ReaderSnapshotLoaded()
    {
        ResourceId? id = _thread?.Summary.Id;
        if (_liveReaderId != id)
        {
            _followReader = false;
            Control<CheckBox>("ReaderFollowLive").IsChecked = false;
            _liveReaderId = id;
        }
        _pendingReaderUpdate = null;
        _latestReaderSummary = _thread?.Summary;
        UpdateReaderUpdateControls();
    }

    private bool MayFollow(ArchiveThreadSummary? summary) => summary != null && summary.CaptureEnabled
        && summary.SourceState == ThreadSourceState.Active && CanFollowLive?.Invoke(summary.Id) == true;

    private void UpdateReaderUpdateControls()
    {
        ArchiveThreadSummary? summary = _latestReaderSummary;
        CheckBox follow = Control<CheckBox>("ReaderFollowLive");
        follow.IsEnabled = !_busy && MayFollow(summary);
        if (!MayFollow(summary)) { _followReader = false; follow.IsChecked = false; }
        Control<Button>("ReaderShowNewPosts").IsVisible = _pendingReaderUpdate != null;
        Control<Button>("ReaderShowNewPosts").IsEnabled = !_busy;
        TextBlock notice = Control<TextBlock>("ReaderNewPostsNotice");
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

    private bool CanCheckReaderUpdate => !_ended && _available && !_busy && !_obscuredByGallery
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
                bool Current() => !_ended && !_obscuredByGallery && coverage == _readerCoverageRevision
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
                            Control<CheckBox>("ReaderFollowLive").IsChecked = false;
                        }
                        else if (summary.Revision > _thread!.Summary.Revision && summary.PostCount > _thread.PostCount)
                            _pendingReaderUpdate = summary;
                        if (!MayFollow(summary)) { _followReader = false; Control<CheckBox>("ReaderFollowLive").IsChecked = false; }
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
        bool enabled = Control<CheckBox>("ReaderFollowLive").IsChecked == true;
        _followReader = enabled && MayFollow(_latestReaderSummary);
        if (_followReader) await CheckReaderUpdatesAsync();
    }

    private async void OnShowReaderUpdates(object? sender, RoutedEventArgs e) => await ShowReaderUpdatesAsync();

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
        await LoadThreadAsync(id, resetPage: false, preparedThread: null, focusIntent: focusIntent);
        if (_ended || _thread?.Summary.Id != id || _thread.PostCount == 0
            || (!deliberate && (interactionRevision != _readerInteractionRevision || !MayRefreshReaderNow))) return;
        // Manual refresh reveals the first appended post; live following stays at the newest.
        int index = follow ? _thread.PostCount - 1 : Math.Min(previousCount, _thread.PostCount - 1);
        await FocusPostAsync(_thread.GetPosts(index, 1).Single().PostId,
            deliberate ? null : interactionRevision, focusIntent: focusIntent);
    }
}

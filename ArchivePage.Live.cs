using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private DispatcherTimer? _readerUpdateTimer;
    private bool _checkingReaderUpdate;
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

    // Only a single small local summary query per visible reader. Never starts a source check.
    internal async Task CheckReaderUpdatesAsync()
    {
        if (_checkingReaderUpdate || _ended || !_available || _busy || _thread == null || _openReader == null
            || !(_largeReader || IsEffectivelyVisible) || !_showReader) return;
        _checkingReaderUpdate = true;
        ResourceId id = _thread.Summary.Id;
        int request = _contentRequest;
        try
        {
            await using ArchiveReader reader = await _openReader(_lifetime.Token);
            ArchiveThreadSummary? summary = await reader.ReadThreadSummaryAsync(id, _lifetime.Token);
            if (_ended || request != _contentRequest || _thread?.Summary.Id != id) return;
            _latestReaderSummary = summary;
            if (summary == null) { _pendingReaderUpdate = null; _followReader = false; Control<CheckBox>("ReaderFollowLive").IsChecked = false; }
            else if (summary.Revision > _thread.Summary.Revision && summary.PostCount > _thread.PostCount)
                _pendingReaderUpdate = summary;
            if (!MayFollow(summary)) { _followReader = false; Control<CheckBox>("ReaderFollowLive").IsChecked = false; }
            UpdateReaderUpdateControls();
            if (_followReader && _pendingReaderUpdate != null && MayRefreshReaderNow)
                await ShowReaderUpdatesAsync(deliberate: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_ended && request == _contentRequest) Message(Texts.T("reader.updateFailed", error.Message), true); }
        finally { _checkingReaderUpdate = false; }
    }

    private bool MayRefreshReaderNow => ReaderShowsPosts && !IsReaderDialogOpen && (ReaderRefreshAllowed?.Invoke() ?? true);

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
        await LoadThreadAsync(id, resetPage: false);
        if (_ended || _thread?.Summary.Id != id || _thread.PostCount == 0) return;
        // Manual refresh reveals the first appended post; live following stays at the newest.
        int index = follow ? _thread.PostCount - 1 : Math.Min(previousCount, _thread.PostCount - 1);
        await FocusPostAsync(_thread.GetPosts(index, 1).Single().PostId, deliberate ? null : interactionRevision);
    }
}

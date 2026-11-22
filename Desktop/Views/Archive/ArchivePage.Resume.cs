using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    internal event Action<LocalReadingPosition>? ConsciousReadingPositionChanged;
    internal Func<bool>? ReadingPositionCaptureAllowed { get; set; }
    /// <summary>The saved reading place of a thread (RH1), for the "Continue reading" entry of the thread menu; null without one.</summary>
    internal Func<ResourceId, LocalReadingPosition?>? ReadingPlaceFor { get; set; }
    /// <summary>Opens a saved reading place (the shell's one resume path).</summary>
    internal Func<LocalReadingPosition, Task>? ResumeReadingPlace { get; set; }
    private bool _resumingPosition;
    private int _readingInputEpoch;
    private int? _pendingReadingInputEpoch;
    private int? _queuedReadingInputEpoch;
    private readonly TextBlock _readingPositionStatus = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Focusable = true };
    internal void InvalidatePendingReadingInput() { ++_readingInputEpoch; _pendingReadingInputEpoch = null; }
    internal void FlushReadingPositionInput()
    {
        int? epoch = _pendingReadingInputEpoch;
        _pendingReadingInputEpoch = null;
        if (epoch == _readingInputEpoch && CanCaptureReadingPosition)
        {
            ArchivePosts.UpdateLayout();
            ReaderPositionChanged(true);
        }
    }

    private void QueueReadingPositionCapture(DispatcherPriority priority)
    {
        if (!CanCaptureReadingPosition) return;
        int epoch = _readingInputEpoch;
        _pendingReadingInputEpoch = epoch;
        if (_queuedReadingInputEpoch == epoch) return;
        _queuedReadingInputEpoch = epoch;
        Dispatcher.UIThread.Post(() =>
        {
            if (_queuedReadingInputEpoch == epoch) _queuedReadingInputEpoch = null;
            if (epoch == _readingInputEpoch) FlushReadingPositionInput();
        }, priority);
    }

    private bool CanCaptureReadingPosition => !_resumingPosition && _largeReader && (ReaderShowsPosts || ReaderPostsInBackground)
        && !_busy && !_ended && ReadingPositionCaptureAllowed?.Invoke() != false;

    internal Func<bool> CaptureReadingRequestGuard()
    {
        int content = _contentRequest;
        int focus = _readerFocusRequest;
        int interaction = _readerInteractionRevision;
        return () => !_ended && content == _contentRequest && focus == _readerFocusRequest && interaction == _readerInteractionRevision;
    }

    private void InitializeReadingPositionInput()
    {
        StackPanel header = ArchiveReaderPane.Children.OfType<StackPanel>().First();
        header.Children.Add(_readingPositionStatus);
        AutomationProperties.SetLiveSetting(_readingPositionStatus, AutomationLiveSetting.Polite);
        ListBox posts = ArchivePosts;
        // Only input on an explicitly opened reader counts. Layout, previews,
        // list selection and background refresh never create a persisted entry.
        void CaptureAfterInput() => QueueReadingPositionCapture(DispatcherPriority.Background);
        posts.AddHandler(PointerWheelChangedEvent, (_, _) => CaptureAfterInput(), RoutingStrategies.Bubble, handledEventsToo: true);
        posts.AddHandler(PointerReleasedEvent, (_, _) => CaptureAfterInput(), RoutingStrategies.Bubble, handledEventsToo: true);
        posts.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.PageDown or Key.PageUp or Key.Home or Key.End or Key.Space)
                CaptureAfterInput();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    partial void ReaderPositionChanged(bool deliberate);

    partial void ReaderPositionChanged(bool deliberate)
    {
        if (!deliberate || !CanCaptureReadingPosition) return;
        ReaderAnchor? anchor = CaptureReaderAnchor();
        if (anchor == null || anchor.Thread.Thread == null) return;
        _readingPositionStatus.IsVisible = false;
        // The title is a local snapshot for the reading history list; it is the open thread's own title.
        string? title = _thread != null && _thread.Summary.Id == anchor.Thread ? LocalReadingPosition.CleanTitle(_thread.Summary.Title) : null;
        ConsciousReadingPositionChanged?.Invoke(new LocalReadingPosition(anchor.Thread.Source, anchor.Thread.Board,
            anchor.Thread.Thread, anchor.PostId, anchor.Top, anchor.SelectedPostId, title));
    }

    private static IReadOnlyList<string> PostIdsOf(ArchivedThreadView view)
    {
        List<string> ids = new(view.PostCount);
        for (int offset = 0; offset < view.PostCount; offset += 500) ids.AddRange(view.GetPosts(offset, 500).Select(post => post.PostId));
        return ids;
    }

    internal void ShowReadingPositionMessage(string message, bool error = false)
    {
        Message(message, error);
        _readingPositionStatus.Text = message;
        _readingPositionStatus.IsVisible = !string.IsNullOrEmpty(message);
        _readingPositionStatus.Classes.Set("workspace-error", error);
    }

    internal async Task ResumeReadingPositionAsync(LocalReadingPosition position, Func<bool>? stillOnVisit = null)
    {
        if (_ended || _busy || _resumingPosition || !_available || _openReader == null) return;
        Func<bool> contentCurrent = CaptureReadingRequestGuard();
        bool Current() => contentCurrent() && stillOnVisit?.Invoke() != false;
        _resumingPosition = true;
        try
        {
            await using ArchiveReader reader = await _openReader(_lifetime.Token);
            ArchivedThreadView? view = await reader.ReadThreadAsync(position.ThreadId, _lifetime.Token);
            if (!Current()) return;
            if (view == null || view.PostCount == 0)
            {
                ShowReadingPositionMessage(Texts.T("resume.missingThread"), true);
                return;
            }
            int index = view.FindPostIndex(position.PostId);
            bool fallback = index < 0;
            // A post that was never captured (or is gone) resolves to the next one that exists, not to the top of the thread.
            if (fallback) index = ReadingJump.Resolve(PostIdsOf(view), position.PostId).Index;
            string postId = view.GetPosts(index, 1).First().PostId;
            // The saved anchor is the reading mark: the posts after it have not been read since.
            // A replacement post is not an anchor; clear any old mark from the same thread rather than
            // presenting an unrelated unread line after this explicit Continue reading jump.
            _unreadMark = ResumeUnreadMark(view, position, fallback);
            _history.Clear(); // F2: continue reading is a jump; "Back to last read" never flips to the thread shown before it
            ShowCapturedView(view, index / PageSize * PageSize, postId);
            contentCurrent = CaptureReadingRequestGuard();
            await RevealPostAsync(postId, Current); // The saved place is shown even where the reader view hides it.
            if (!Current()) return;
            int shownContent = _contentRequest;
            OpenReaderRequested?.Invoke(null);
            if (shownContent != _contentRequest || stillOnVisit?.Invoke() == false) return;
            contentCurrent = CaptureReadingRequestGuard();
            await RestoreReaderAnchorAsync(new ReaderAnchor(position.ThreadId, postId,
                fallback ? 0 : position.Top, fallback ? postId : position.SelectedPostId),
                stillRequested: stillOnVisit, onRestored: () =>
                {
                    // The landing post is marked briefly, as after a quote or bookmark jump.
                    _ = FlashPostAsync(postId);
                    ShowReadingPositionMessage(fallback ? Texts.T("resume.fallback") : string.Empty);
                });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (Current()) ShowReadingPositionMessage(Texts.T("resume.readFailed", error.Message), true);
        }
        finally { _resumingPosition = false; }
    }
}

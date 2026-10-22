using System;
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
    private bool _resumingPosition;
    private int _readingInputEpoch;
    private int? _pendingReadingInputEpoch;
    private readonly TextBlock _readingPositionStatus = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Focusable = true };
    internal void InvalidatePendingReadingInput() { ++_readingInputEpoch; _pendingReadingInputEpoch = null; }
    internal void FlushReadingPositionInput()
    {
        int? epoch = _pendingReadingInputEpoch;
        _pendingReadingInputEpoch = null;
        if (epoch == _readingInputEpoch) ReaderPositionChanged(true);
    }

    private void InitializeReadingPositionInput()
    {
        StackPanel header = Control<Grid>("ArchiveReaderPane").Children.OfType<StackPanel>().First();
        header.Children.Add(_readingPositionStatus);
        AutomationProperties.SetLiveSetting(_readingPositionStatus, AutomationLiveSetting.Polite);
        ListBox posts = Control<ListBox>("ArchivePosts");
        // Only input on an explicitly opened reader counts. Layout, previews,
        // list selection and background refresh never create a persisted entry.
        void CaptureAfterInput()
        {
            int epoch = _readingInputEpoch;
            _pendingReadingInputEpoch = epoch;
            Dispatcher.UIThread.Post(() =>
            {
                if (epoch == _readingInputEpoch) FlushReadingPositionInput();
            }, DispatcherPriority.Background);
        }
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
        if (!deliberate || _resumingPosition || !_largeReader || !ReaderShowsPosts || _busy || _ended) return;
        ReaderAnchor? anchor = CaptureReaderAnchor();
        if (anchor == null || anchor.Thread.Thread == null) return;
        _readingPositionStatus.IsVisible = false;
        ConsciousReadingPositionChanged?.Invoke(new LocalReadingPosition(anchor.Thread.Source, anchor.Thread.Board,
            anchor.Thread.Thread, anchor.PostId, anchor.Top, anchor.SelectedPostId));
    }

    internal void ShowReadingPositionMessage(string message, bool error = false)
    {
        Message(message, error);
        _readingPositionStatus.Text = message;
        _readingPositionStatus.IsVisible = !string.IsNullOrEmpty(message);
        _readingPositionStatus.Classes.Set("error", error);
    }

    internal async Task ResumeReadingPositionAsync(LocalReadingPosition position, Func<bool>? stillRequested = null)
    {
        if (_ended || _busy || _resumingPosition || !_available || _openReader == null) return;
        int request = _contentRequest;
        _resumingPosition = true;
        try
        {
            await using ArchiveReader reader = await _openReader(_lifetime.Token);
            ArchivedThreadView? view = await reader.ReadThreadAsync(position.ThreadId, _lifetime.Token);
            if (_ended || request != _contentRequest || stillRequested?.Invoke() == false) return;
            if (view == null || view.PostCount == 0)
            {
                ShowReadingPositionMessage(Texts.T("resume.missingThread"), true);
                return;
            }
            int index = view.FindPostIndex(position.PostId);
            bool fallback = index < 0;
            string postId = fallback ? view.GetPosts(0, 1).First().PostId : position.PostId;
            ShowCapturedView(view, fallback ? 0 : index / PageSize * PageSize, postId);
            OpenReaderRequested?.Invoke(null);
            await RestoreReaderAnchorAsync(new ReaderAnchor(position.ThreadId, postId,
                fallback ? 0 : position.Top, fallback ? postId : position.SelectedPostId));
            ShowReadingPositionMessage(fallback ? Texts.T("resume.fallback") : string.Empty);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_ended) ShowReadingPositionMessage(Texts.T("resume.readFailed", error.Message), true); }
        finally { _resumingPosition = false; }
    }
}

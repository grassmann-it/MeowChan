using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Reader idea I3: the reading mark as a visible object. A thin accent line with "N new posts since last time" sits in its own
// row between the last post read and the first one after it (never inside a card), and the reader toolbar offers "Back to last read" once the view has
// scrolled past it. The mark comes from the existing reading-position anchor (Continue reading) or from a manual
// "Show new posts"; nothing new is stored, and a thread opened from the list starts without one.
public partial class ArchivePage
{
    // The last post read before this view, in one thread; the line sits between it and its successor.
    private (ResourceId Thread, string LastReadPostId)? _unreadMark;
    private string? _unreadFirstNewId;

    internal string? UnreadFirstNewPostId => _unreadFirstNewId;
    private void InitializeUnreadLine()
    {
        ArchivePosts.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => UpdateUnreadReturn(), RoutingStrategies.Bubble);
        LocalizeReaderUnread();
    }

    private void LocalizeReaderUnread()
    {
        string label = Texts.T("reader.unreadReturn");
        ReaderUnreadReturn.Content = label;
        Avalonia.Automation.AutomationProperties.SetName(ReaderUnreadReturn, label);
        ToolTip.SetTip(ReaderUnreadReturn, Texts.T("reader.unreadReturnTip")); // The toolbar's own Back carries the same name.
    }

    // Where the first unread post is, and how many follow the last read one (0 when there is no mark or nothing new).
    private (string? FirstNewId, int Count) UnreadBoundary()
    {
        if (_thread == null || _unreadMark is not { } mark || mark.Thread != _thread.Summary.Id) return (null, 0);
        int last = _thread.FindPostIndex(mark.LastReadPostId);
        if (last < 0 || last + 1 >= _thread.PostCount) return (null, 0);
        return (_thread.Posts[last + 1].PostId, _thread.PostCount - last - 1);
    }

    // The posts after the later of the saved anchor and the saved selection were not read yet.
    private static string LastReadOf(ArchivedThreadView view, LocalReadingPosition position)
    {
        int anchor = view.FindPostIndex(position.PostId);
        int selected = position.SelectedPostId == null ? -1 : view.FindPostIndex(position.SelectedPostId);
        return selected > anchor ? position.SelectedPostId! : position.PostId;
    }

    // Continue reading only creates an unread boundary when the saved post is still present.
    // A fallback is a replacement landing point, not evidence of a new read boundary.
    internal static (ResourceId Thread, string LastReadPostId)? ResumeUnreadMark(ArchivedThreadView view,
        LocalReadingPosition position, bool fallback) => fallback ? null : (position.ThreadId, LastReadOf(view, position));

    private bool IsPastUnreadLine()
    {
        if (_unreadFirstNewId == null || Projection is not { } shown) return false;
        int index = shown.PositionOf(_unreadFirstNewId);
        if (index < 0) return false;
        ListBox list = ArchivePosts;
        if (list.ContainerFromIndex(index) is Control container)
        {
            if (container.TranslatePoint(default, list)?.Y is not { } top) return false; // Not laid out yet.
            // It is past once the line itself (it sits above the card, ReaderUnreadLineHost.Air clear of it) has scrolled out, or the whole post has.
            return top < ReaderUnreadLineHost.Air || top + container.Bounds.Height <= 0;
        }
        // Not realized: the panel realizes one contiguous range, so the post is above it (past) or below it.
        return list.ItemsPanelRoot is VirtualizingStackPanel { FirstRealizedIndex: >= 0 and var firstRealized } && index < firstRealized;
    }

    private void UpdateUnreadReturn()
    {
        bool past = _unreadFirstNewId != null && IsPastUnreadLine();
        if (ReaderUnreadReturn.IsVisible != past) ReaderUnreadReturn.IsVisible = past;
    }

    private async void OnReaderUnreadReturnClick(object? sender, RoutedEventArgs e)
    {
        if (_unreadFirstNewId is not { } post || _busy || _ended || IsFilenameSearchOpen) return;
        ++_readerInteractionRevision;
        await FocusPostAsync(post);
        if (!_ended) _ = FlashPostAsync(post);
    }
}

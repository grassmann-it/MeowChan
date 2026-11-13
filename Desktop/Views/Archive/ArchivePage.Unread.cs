using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Reader idea I3: the reading mark as a visible object. A thin accent line with "N new posts since last time" sits at the
// top of the first post after the last one read, and the reader toolbar offers "Back to last read" once the view has
// scrolled past it. The mark comes from the existing reading-position anchor (Continue reading) or from a manual
// "Show new posts"; nothing new is stored, and a thread opened from the list starts without one.
public partial class ArchivePage
{
    // The last post read before this view, in one thread; the line sits between it and its successor.
    private (ResourceId Thread, string LastReadPostId)? _unreadMark;
    private string? _unreadFirstNewId;

    internal string? UnreadFirstNewPostId => _unreadFirstNewId;
    internal bool UnreadReturnVisible => ReaderUnreadReturn.IsVisible;

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
    }

    // Where the first unread post is, and how many follow the last read one (0 when there is no mark or nothing new).
    private (string? FirstNewId, int Count) UnreadBoundary()
    {
        if (_thread == null || _unreadMark is not { } mark || mark.Thread != _thread.Summary.Id) return (null, 0);
        int last = _thread.FindPostIndex(mark.LastReadPostId);
        if (last < 0 || last + 1 >= _thread.PostCount) return (null, 0);
        return (_thread.GetPosts(last + 1, 1).FirstOrDefault()?.PostId, _thread.PostCount - last - 1);
    }

    internal void SetUnreadMark(ResourceId thread, string lastReadPostId) => _unreadMark = (thread, lastReadPostId);

    // The posts after the later of the saved anchor and the saved selection were not read yet.
    private static string LastReadOf(ArchivedThreadView view, LocalReadingPosition position)
    {
        int anchor = view.FindPostIndex(position.PostId);
        int selected = position.SelectedPostId == null ? -1 : view.FindPostIndex(position.SelectedPostId);
        return selected > anchor ? position.SelectedPostId! : position.PostId;
    }

    private bool IsPastUnreadLine()
    {
        if (_thread == null || _unreadFirstNewId == null || ArchivePosts.ItemsSource is not ArchivePostRow[] rows) return false;
        int line = _thread.FindPostIndex(_unreadFirstNewId);
        if (line < 0) return false;
        if (line < _postOffset) return true; // The line is on an earlier page of posts.
        if (line >= _postOffset + PageSize) return false;
        ListBox list = ArchivePosts;
        (ListBoxItem? Item, double Top) first = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(item => item.DataContext is ArchivePostRow)
            .Select(item => (Item: (ListBoxItem?)item, Top: item.TranslatePoint(default, list)?.Y ?? double.NaN))
            .Where(item => item.Top < list.Bounds.Height && item.Top + item.Item!.Bounds.Height > 0)
            .OrderBy(item => item.Top).FirstOrDefault();
        if (first.Item?.DataContext is not ArchivePostRow top) return false;
        int visible = _thread.FindPostIndex(top.Id);
        // At the post that carries the line, it is past once the line itself (its first rows) has scrolled out.
        return visible > line || visible == line && first.Top < -UnreadLineHeight;
    }

    private const double UnreadLineHeight = 28;

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

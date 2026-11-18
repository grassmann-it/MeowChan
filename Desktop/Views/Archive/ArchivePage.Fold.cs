using System.Collections.Generic;
using Avalonia.Interactivity;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

// Reader idea I6: a long post is shown folded (18 of its lines, a fade and "Show more"); the choice is remembered per
// post for the session, and the thread find opens a folded post that has a hit.
public partial class ArchivePage
{
    private readonly HashSet<(ResourceId Thread, string PostId)> _expandedPosts = new();

    private void SetPostExpanded(ResourceId thread, string postId, bool expanded)
    {
        if (expanded) _expandedPosts.Add((thread, postId));
        else _expandedPosts.Remove((thread, postId));
    }

    private void OnFoldToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ArchivePostRow row }) return;
        e.Handled = true;
        row.Expanded = !row.Expanded;
    }

    // A hit in a post's body opens the post, so the match is never hidden behind the fold.
    private void ExpandPostForFind(string postId)
    {
        if (_thread == null) return;
        SetPostExpanded(_thread.Summary.Id, postId, true);
        if (ArchivePosts.ItemsSource is IEnumerable<ArchivePostRow> rows)
            foreach (ArchivePostRow row in rows)
                if (row.Id == postId) { row.Expanded = true; break; }
    }
}

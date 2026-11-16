namespace MeowChan.Desktop;

/// <summary>What the paging gesture (pagination.previous / pagination.next) pages in the reader.</summary>
internal enum ReaderPageTarget { None, Posts, References }

/// <summary>
/// The reader's paging gesture decided from state alone (pure): the selected tab, the offsets and the counts. It never looks at whether a pager button is
/// visible: the pager row is collapsed by a work view or the layout, and the gesture has to keep working then.
/// </summary>
internal static class ReaderPaging
{
    /// <summary>
    /// What the gesture pages. <paramref name="referencesTabSelected"/>: the References tab is the shown one (it pages the backlinks). On every other tab
    /// it pages the thread's posts: on the Posts tab in view, on the Media tab in the background (the page and the current post move, the tab stays).
    /// </summary>
    internal static ReaderPageTarget Target(bool previous, bool referencesTabSelected, bool readReady, bool hasThread, int postOffset,
        int postPageSize, int shownPosts, bool hasBacklinkPost, int referenceOffset, int referenceTotal, int referencePageSize)
    {
        if (!readReady || !hasThread) return ReaderPageTarget.None;
        if (referencesTabSelected)
            return hasBacklinkPost && (previous ? referenceOffset > 0 : referenceOffset + referencePageSize < referenceTotal)
                ? ReaderPageTarget.References : ReaderPageTarget.None;
        return previous ? postOffset > 0 ? ReaderPageTarget.Posts : ReaderPageTarget.None
            : postOffset + postPageSize < shownPosts ? ReaderPageTarget.Posts : ReaderPageTarget.None;
    }
}

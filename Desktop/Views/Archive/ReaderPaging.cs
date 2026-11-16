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
    /// What the gesture pages. <paramref name="referencesTabSelected"/>: the References tab is the shown one (it pages the backlinks);
    /// <paramref name="postsTabSelected"/>: the posts tab is shown (it pages the thread's posts). Any other tab has no pager, so the gesture does nothing there.
    /// </summary>
    internal static ReaderPageTarget Target(bool previous, bool referencesTabSelected, bool postsTabSelected, bool readReady, bool hasThread, int postOffset,
        int postPageSize, int shownPosts, bool hasBacklinkPost, int referenceOffset, int referenceTotal, int referencePageSize)
    {
        if (!readReady || !hasThread) return ReaderPageTarget.None;
        if (referencesTabSelected)
            return hasBacklinkPost && (previous ? referenceOffset > 0 : referenceOffset + referencePageSize < referenceTotal)
                ? ReaderPageTarget.References : ReaderPageTarget.None;
        if (!postsTabSelected) return ReaderPageTarget.None;
        return previous ? postOffset > 0 ? ReaderPageTarget.Posts : ReaderPageTarget.None
            : postOffset + postPageSize < shownPosts ? ReaderPageTarget.Posts : ReaderPageTarget.None;
    }
}

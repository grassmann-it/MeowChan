namespace MeowChan.Desktop;

/// <summary>Availability of the paging shortcut within the reader's References tab.</summary>
internal static class ReaderPaging
{
    internal static bool CanPageReferences(bool previous, bool readReady, bool hasThread, bool hasBacklinkPost,
        int offset, int total, int pageSize) =>
        readReady && hasThread && hasBacklinkPost && (previous ? offset > 0 : offset + pageSize < total);
}

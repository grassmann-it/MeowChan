using System.Collections.Generic;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.6: gallery overflow (GalleryViewer.Overflow). Owned by WV4; no rows yet (the page ignores the work view today).
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> GalleryEntries() => [];
}

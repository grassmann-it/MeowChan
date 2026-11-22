using Avalonia.Controls;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class ArchivePage
{
    // I1b: the page's tool buttons that were text alone get a catalog icon before their text, like their neighbours (Gallery, Favorites,
    // the reader toolbar). The text stays the content (set again with every language), so the tooltip, the accessible name and everything
    // that reads the label are unchanged; the content template draws the icon. Called once from InitializeReaderToolbarIcons.
    internal static readonly (string Name, string Icon)[] ToolButtonIcons =
    [
        ("ArchiveLayoutList", "list"), ("ArchiveLayoutTable", "columns"),
        ("ArchiveMediaLayoutTiles", "grid"), ("ArchiveMediaLayoutTable", "columns"),
        ("ArchiveAddSource", "plus"), ("ArchiveAddBoard", "plus"),
        ("ArchiveTagManage", "sliders"), ("ReaderTagManage", "sliders"),
        ("ArchiveUseDeviceZone", "check"),
        ("ArchiveRetrySearch", "refresh-cw"), ("ArchiveRetrySuggestions", "refresh-cw"),
        ("ArchiveFilterApply", "check"), ("ArchiveFilterClose", "x"),
        ("ArchiveResetFilters", CommandCatalog.Get("media.filter.reset-all").Icon),
        ("ArchiveEmptySearchInMedia", CommandCatalog.Get("archive.open-results-in-media").Icon),
        ("ArchiveMediaSelectionTag", CommandCatalog.Get("tagging.tag-together").Icon),
        ("ArchiveMediaSelectionQueue", CommandCatalog.Get("media.queue.add").Icon),
        ("ArchiveMediaSelectionGallery", CommandCatalog.Get("gallery.open").Icon),
        ("ArchiveMediaSelectionExport", CommandCatalog.Get("media.export-original").Icon),
        ("ArchiveMediaSelectionClear", "x"),
        ("ArchiveFavoritesFilter", CommandCatalog.Get("favorites.filter").Icon),
        ("ArchiveEditRatingButton", CommandCatalog.Get("media.rating.edit").Icon),
        ("ArchiveManageRatingsButton", CommandCatalog.Get("media.rating.manage").Icon),
        ("PreviewArchiveMediaButton", CommandCatalog.Get("gallery.open").Icon),
        ("ReaderFindSearch", CommandCatalog.Get("reader.find").Icon), ("ReaderFindRetry", "refresh-cw"),
        ("ReaderTagAdd", "plus")
    ];

    private void InitializeToolButtonIcons()
    {
        foreach ((string name, string icon) in ToolButtonIcons)
            CommandBindings.EnsureIconBeforeText(Control<Button>(name), icon);
    }
}

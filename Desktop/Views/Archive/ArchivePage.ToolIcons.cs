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
        ("ArchiveAddSource", "plus"), ("ArchiveAddBoard", "plus"),
        ("ArchiveTagManage", "sliders"), ("ReaderTagManage", "sliders"),
        ("ArchiveUseDeviceZone", "check")
    ];

    private void InitializeToolButtonIcons()
    {
        foreach ((string name, string icon) in ToolButtonIcons)
            CommandBindings.EnsureIconBeforeText(Control<Button>(name), icon);
    }
}

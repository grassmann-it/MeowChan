using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class ArchivePage
{
    /// <summary>
    /// The key of <c>reader.find</c> (Primary+F) with the keyboard focus inside the reader opens the find bar of the open thread (RD-02): in the
    /// embedded reader (its posts list, a post and its text, its toolbar and its find bar are inside the pane) and in the large reader, also from
    /// the media sidebar beside its pane. With the focus anywhere else on the Archive page (the results list, the search fields) the same key keeps
    /// focusing the archive search. The window asks first (MainWindow.OnShellKeyDown), because it runs before the pane sees the key.
    /// A selection in a post's text does not hold the key back (Primary+F types nothing), as a text field of the reader other than the find bar's own does.
    /// </summary>
    /// <returns>True when the key opened (or kept) the find bar and is consumed.</returns>
    internal bool TryOpenReaderFindByKey(KeyEventArgs e)
    {
        // The gesture first: this runs for every key the window sees while the Archive page or the large reader is shown.
        if (_ended || _thread == null || IsReaderDialogOpen || ShortcutService.IsCapturing || !ShortcutService.Current.Matches(e, "reader.find")) return false;
        if (e.Source is not Avalonia.Visual source) return false;
        Avalonia.Visual[] path = source.GetSelfAndVisualAncestors().ToArray();
        if (!path.Contains(ArchiveReaderPane) && (_mediaSidebar == null || !path.Contains(_mediaSidebar))) return false;
        if (!IsThreadFindSource(source) && path.Any(node => node is TextBox)) return false;
        OnOpenThreadFind(null, e);
        return ReaderFindBar.IsVisible;
    }
}

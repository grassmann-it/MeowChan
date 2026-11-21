using System.Linq;
using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

public partial class ArchivePage
{
    /// <summary>The neighbouring reader box (layout regression seam).</summary>
    internal Control ReaderBox => ArchiveReaderContent;

    // Header rows stay above the posts column only, on either sidebar side.
    private StackPanel? ReaderHeaderRows => ArchiveReaderPane.Children.OfType<StackPanel>().FirstOrDefault();

    // UX-LEDGER-26 supersedes the old frame-to-posts alignment. Vertical space
    // belongs to the media, not to padding derived from a neighbouring box.
    private static void ApplyMediaSidebarInsets(ReaderMediaSidebar sidebar, bool archiveView)
    {
        sidebar.Margin = default;
        sidebar.Padding = archiveView ? default : new Thickness(16, 0, 16, 0);
        // F15 (21.11.): the archive view's sidebar sits flush at the pane edge, so its header row (star and image icons at the right) stood without
        // distance there and looked cut off. The row keeps the same inset on both sides; the card below stays flush with its neighbour.
        sidebar.HeaderRow.Margin = archiveView ? new Thickness(8, 0, 8, 0) : default;
    }
}

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
        // Inset the header from the top and give its controls room at the archive pane edges.
        double horizontalInset = archiveView ? 8 : 0;
        sidebar.HeaderRow.Margin = new Thickness(horizontalInset, 8 + ReaderMediaSidebar.HeaderTopInset, horizontalInset, 0);
    }
}

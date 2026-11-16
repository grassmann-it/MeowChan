using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan;

// Tom (16.11.2026): the media sidebar's frame has the top and bottom of the reader's bordered box (ReaderSidebarAlignment). The archive view lifts the
// sidebar over the strip above the box by the header line and its gap (ArchivePage.MediaSidebar.cs), the large reader's sidebar spans the frame; in both the
// box and the sidebar are measured after every layout pass and the sidebar's top and bottom inset are set from them (one correction, no iteration: the
// sidebar's insets never move the box).
public partial class ArchivePage
{
    private ReaderSidebarAlignment.Placement? _mediaSidebarPlacement;

    /// <summary>Where the sidebar's header line and frame stand in its host, from the last alignment (smoke seam; null while it is hidden).</summary>
    internal ReaderSidebarAlignment.Placement? MediaSidebarPlacement => _mediaSidebarShown ? _mediaSidebarPlacement : null;

    /// <summary>The reader's bordered box the sidebar's frame is aligned with (the box with the tabs, the posts and the pager).</summary>
    internal Control ReaderBox => ArchiveReaderContent;

    // The header rows of the archive view's pane (title, toolbar, status): the stack above the box.
    private StackPanel? ReaderHeaderRows => ArchiveReaderPane.Children.OfType<StackPanel>().FirstOrDefault();

    // The sidebar's own margin and horizontal inset for its host (the vertical insets are measured).
    private static void ApplyMediaSidebarInsets(ReaderMediaSidebar sidebar, bool archiveView)
    {
        sidebar.Margin = ReaderSidebarAlignment.HostMargin(archiveView);
        sidebar.Padding = ReaderSidebarAlignment.Padding(archiveView);
    }

    private void AlignMediaSidebarFrame()
    {
        if (_ended || !_mediaSidebarShown || _mediaSidebar is not { } sidebar || _mediaSidebarHost is not { } host || !sidebar.IsVisible) return;
        Control box = ArchiveReaderContent;
        if (!box.IsVisible || !host.IsVisualAncestorOf(box)) return;
        if (sidebar.TranslatePoint(default, host) is not { } sidebarOrigin || box.TranslatePoint(default, host) is not { } boxOrigin) return;
        double sidebarTop = sidebarOrigin.Y, sidebarBottom = sidebarTop + sidebar.Bounds.Height, boxTop = boxOrigin.Y, boxBottom = boxTop + box.Bounds.Height;
        if (!ReaderSidebarAlignment.IsUsable(sidebarTop, sidebarBottom, boxTop, boxBottom)) return;
        ReaderSidebarAlignment.Insets wanted = ReaderSidebarAlignment.InsetsFor(sidebarTop, sidebarBottom, boxTop, boxBottom);
        if (ReaderSidebarAlignment.Differs(sidebar.Padding, wanted))
        {
            Thickness padding = sidebar.Padding;
            sidebar.Padding = new Thickness(padding.Left, wanted.Top, padding.Right, wanted.Bottom);
        }
        _mediaSidebarPlacement = ReaderSidebarAlignment.PlacementOf(sidebarTop, sidebarBottom, wanted);
    }
}

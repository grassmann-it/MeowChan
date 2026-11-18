using Avalonia;
using MeowChan.Desktop;

namespace MeowChan;

// UX-LEDGER-27 / protocol 1.9e: the panel is a full-height sidebar, not a card aligned
// to the neighbouring grid. Its title/actions stay inside its own top line. No lifted
// header, negative margin, or reservation on the summary/selection line is needed.
public sealed partial class MediaPage
{
    internal const double PanelHeaderLineHeight = ReaderMediaSidebar.HeaderLineHeight;

    // Compatibility seam for the diagnostic smoke checks; the header is never lifted now.
    internal bool IsPanelHeaderLifted => false;

    private void AlignPanel(ReaderMediaSidebar panel, bool panelRight, double edge)
    {
        panel.Margin = panelRight ? new Thickness(0, 0, edge, 0) : new Thickness(edge, 0, 0, 0);
    }
}

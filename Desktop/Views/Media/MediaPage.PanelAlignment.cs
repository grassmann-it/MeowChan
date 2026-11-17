using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

// Tom (15.11.2026): the media panel's card is exactly as tall as the grid card beside it (same top edge, same bottom edge, nothing under it),
// and the panel's header line ("No. 337 · file · 3.9 MiB ☆ ⧉") stands on the last line of the page header, the selection row or the summary
// line, centred on it. The header is lifted out of the body row by a negative top margin; the line keeps the panel's room free on its side.
// It is lifted only while that line is the last one of the header: an open filter section, a confirmation or a note under the selection row
// keeps the header inside the body row (the card then starts one header line lower and still ends with the grid).
// The summary and the selection actions are one swapping 32 dip line (MediaPage.Layout.cs): the summary hides while tiles are selected, unless it
// reports a read problem, and then the selection row under it is the last line. Only that visible last line keeps the panel's room free.
public sealed partial class MediaPage
{
    /// <summary>The height of the page header's last line (the 32 dip line of the selection row and the summary, Workspace.Size.Control) and of the panel's header line.</summary>
    internal const double PanelHeaderLineHeight = ReaderMediaSidebar.HeaderLineHeight;
    /// <summary>The page header's bottom margin, the space between its last line and the grid card (MediaPage's header margin).</summary>
    private const double HeaderBottomMargin = 8;

    private bool _panelAlignmentInstalled, _panelHeaderLifted;
    private Control? _panelHeaderLine;

    /// <summary>Whether the panel's header line stands on the page header's last line now (tests and smokes).</summary>
    internal bool IsPanelHeaderLifted => _panelHeaderLifted;

    // The rows whose visibility decides whether the header's last line is the selection row or the summary line.
    private Control?[] PanelAlignmentRows => new Control?[] { _filterSurface, _exportAllAsk, _selectionNote, _queueAsk, _selectionRow, _statusRow };

    private bool PanelHeaderCanLift => !_focusMode && _filterSurface is not { IsVisible: true } && !_exportAllAsk.IsVisible
        && !_selectionNote.IsVisible && !_queueAsk.IsVisible && PanelHeaderLine != null;

    // The header's visible last line: the selection row while it shows (it stands under the summary), else the summary line.
    private Control? PanelHeaderLine => _selectionRow.IsVisible ? _selectionRow : _statusRow is { IsVisible: true } status ? status : null;

    // The panel's margins and the room the header's last line keeps for it; LayoutBody calls this with the column it laid out.
    private void AlignPanel(ReaderMediaSidebar panel, bool panelRight, double edge, double reserve)
    {
        if (!_panelAlignmentInstalled)
        {
            _panelAlignmentInstalled = true;
            foreach (Control? row in PanelAlignmentRows)
                if (row != null) row.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) _ = RealignPanel(); };
        }
        bool lift = _panelShown && panel.IsVisible && PanelHeaderCanLift;
        _panelHeaderLifted = lift;
        double top = lift ? -(PanelHeaderLineHeight + HeaderBottomMargin) : 0;
        panel.Margin = panelRight ? new Thickness(0, top, edge, 8) : new Thickness(edge, top, 0, 8);
        Thickness room = panelRight ? new Thickness(0, 0, reserve, 0) : new Thickness(reserve, 0, 0, 0);
        _panelHeaderLine = lift ? PanelHeaderLine : null;
        _selectionRow.Margin = ReferenceEquals(_panelHeaderLine, _selectionRow) ? room : default;
        if (_statusRow != null) _statusRow.Margin = ReferenceEquals(_panelHeaderLine, _statusRow) ? room : default;
    }

    // A row above the grid appeared or went away: the header's last line may have changed (or the summary and the selection row swapped).
    private bool RealignPanel()
    {
        if (_panel is null || _plan is not { } plan || !_panelShown) return false;
        bool lift = PanelHeaderCanLift;
        if (_panelHeaderLifted == lift && (!lift || ReferenceEquals(_panelHeaderLine, PanelHeaderLine))) return false;
        LayoutBody(plan);
        return true;
    }
}

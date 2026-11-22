using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// F10: the labels of the toolbar's switches follow the width of the row (MediaToolbarLabels). Only the presentation changes; the buttons, their
// names, tooltips, shortcuts and the stage rules of MediaToolbarLayout stay as they are.
public sealed partial class MediaPage
{
    private double _labelRowWidth;
    private bool _filterLabelShown = true, _sidebarLabelsShown;
    private string? _labelsKey;

    /// <summary>Whether the filter toggle shows its text now (tests and smokes).</summary>
    internal bool FilterLabelShown => _filterLabelShown;

    /// <summary>Whether the tag list and media panel switches show their words now (tests and smokes).</summary>
    internal bool SidebarLabelsShown => _sidebarLabelsShown;

    private void InstallToolbarLabels(Grid tools) =>
        tools.SizeChanged += (_, e) => { if (e.WidthChanged) ApplyToolbarLabels(e.NewSize.Width); };

    /// <summary>Applies the label rules for a row width.</summary>
    internal void ApplyToolbarLabels(double rowWidth)
    {
        if (_lifetime.IsCancellationRequested) return;
        _labelRowWidth = rowWidth;
        _filterLabelShown = MediaToolbarLayout.For(rowWidth) != MediaToolbarLayout.Level.Compact && MediaToolbarLabels.ShowsFilterLabel(rowWidth);
        _filters.LabelVisible = _filterLabelShown;
        ReapplyToolbarLabels(force: false);
    }

    private void ReapplyToolbarLabels(bool force = true)
    {
        _sidebarLabelsShown = MediaToolbarLabels.ShowsSidebarLabels(_labelRowWidth);
        string key = (_sidebarLabelsShown ? "1" : "0") + _layout.PanelSide + _layout.TagListSide;
        if (!force && key == _labelsKey) return;
        _labelsKey = key;
        MediaToolbarLabels.Apply(_booruMode, _sidebarLabelsShown, Texts.T("media.toolbar.tagsLabel"), _layout.TagListSide == MediaTagListSide.Right);
        MediaToolbarLabels.Apply(_panelToggle, _sidebarLabelsShown, Texts.T("media.toolbar.mediaLabel"), _layout.PanelSide == MediaPanelSide.Right);
    }
}

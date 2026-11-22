using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The toolbar switches trade their short labels for icons as the row narrows. Their names and hints stay available.
public sealed partial class MediaPage
{
    private readonly MediaToolbarLabels.SidebarContent _tagsLabelContent = new();
    private readonly MediaToolbarLabels.SidebarContent _mediaLabelContent = new();
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
        _sidebarToggles.ShowLabels = _sidebarLabelsShown;
        _sidebarToggles.Describe(_booruMode, Texts.T("media.tagList"), Texts.T("media.tagList.tip", PlatformKeyNames.Alt()));
        _sidebarToggles.Describe(_panelToggle, CommandCatalog.Label(MediaPanelKeys.ToggleCommandId),
            CommandBindings.Tooltip(MediaPanelKeys.ToggleCommandId, PanelHint()));
        string key = _layout.PanelSide.ToString() + _layout.TagListSide;
        if (!force && key == _labelsKey) return;
        _labelsKey = key;
        bool tagsRight = _layout.TagListSide == MediaTagListSide.Right;
        string tagsIcon = tagsRight ? "panel-right" : "panel-left";
        if (IconButtons.KeyOf(_booruMode) != tagsIcon)
        {
            IconButtons.Record(_booruMode, tagsIcon);
            _tagsLabelContent.SetIcon(CommandBindings.NamedIcon(tagsIcon));
        }
        _tagsLabelContent.Apply(Texts.T("media.toolbar.tagsLabel"), tagsRight);
        _mediaLabelContent.Apply(Texts.T("media.toolbar.mediaLabel"), _layout.PanelSide == MediaPanelSide.Right);
    }
}

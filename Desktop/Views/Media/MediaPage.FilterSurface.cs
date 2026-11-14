using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// UX103 P8, S1/S2: the media filter fields open as an inline section under the status line that pushes the tile grid down
// (no overlay, no Close button: the toggle, Escape and the shortcut close it). The toggle is a FilterToggleButton with the
// active-filter count as a badge. Presentation only: the fields, their names and their live filtering are those of
// MediaPage.Filters.cs; the applied chips, error, status and selection bar stay in the header around the section.
public sealed partial class MediaPage
{
    /// <summary>The most the open section may take; its fields scroll inside it, so the tiles always keep room. A layout measure,
    /// not a user limit (so no setting): it is a fixed ceiling by design (260 dip), and the section scrolls beyond it.</summary>
    internal const double FilterSectionMaxHeight = 260;

    private readonly FilterToggleButton _filters = new() { Name = "MediaFilters", VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _filterScroll = new() { Name = "MediaFilterSurfaceScroll", MaxHeight = FilterSectionMaxHeight };
    private Border? _filterSurface;
    private FilterSurfaceController? _filterSurfaceController;

    internal bool IsFilterSurfaceOpen => _filters.IsChecked == true;

    /// <summary>The inline section in the header; its visibility follows the toggle. Holds the unchanged filter row.</summary>
    private Border BuildFilterSurface(Control fields)
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(_filterScroll, ScrollBarVisibility.Disabled);
        _filterScroll.Content = new Border { Padding = new Thickness(0, 0, 12, 0), Child = fields };
        Border surface = new()
        {
            Name = "MediaFilterSurface", Child = _filterScroll, Padding = new Thickness(10, 8), Margin = new Thickness(0, 4, 0, 4),
            IsVisible = false, Classes = { "workspace-section" }
        };
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.Cycle);
        _filterSurfaceController = new FilterSurfaceController(this, _filters, surface, () => _lifetime.IsCancellationRequested, drivesVisibility: true, overlay: false);
        return _filterSurface = surface;
    }

    private ToggleButton FilterSurfaceToggle => _filters;

    private bool FocusWithin(Control owner) => FilterSurfaceController.FocusWithin(this, owner);

    internal void CloseFilterSurface(bool restoreFocus) => _filterSurfaceController?.Close(restoreFocus);

    // Filters apply live, so the toggle says how many are on while the fields are hidden (C3: a badge with the count, one per chip).
    private void UpdateFilterToggle()
    {
        int count = ActiveFilterCount;
        string label = Texts.T("media.filters");
        _filters.Show(label, count, count > 0 ? Texts.Count("media.filters.countName", count) : label);
    }

    private void LocalizeFilterSurface()
    {
        AutomationProperties.SetName(_filterSurface!, Texts.T("media.filters"));
        UpdateFilterToggle();
    }
}

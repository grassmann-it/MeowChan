using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// UX103 P8: the media filter fields open as a local surface over the tile grid, like the archive filters (P2).
// Presentation only: the fields, their names and their live filtering are those of MediaPage.Filters.cs; the
// expander is the toggle, the applied chips, error, status and selection bar stay in the header.
public sealed partial class MediaPage
{
    private readonly Expander _filters = new() { Name = "MediaFilters", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _filterClose = new() { Name = "MediaFilterClose", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ScrollViewer _filterScroll = new() { Name = "MediaFilterSurfaceScroll" };
    private Border? _filterSurface;

    internal bool IsFilterSurfaceOpen => _filters.IsExpanded;

    /// <summary>The surface over the grid; its visibility follows the toggle. Holds the unchanged filter row.</summary>
    private Border BuildFilterSurface(Control fields)
    {
        // The expander keeps only its toggle: its own content area never takes room in the header.
        _filters.Styles.Add(new Style(x => Selectors.Or(
            x.OfType<Expander>().Template().Is<Control>().Name("ExpanderContent"),
            x.OfType<Expander>().Template().Is<Control>().Name("PART_ContentPresenter"),
            x.OfType<Expander>().Template().Is<Control>().Name("ExpanderContentLayoutContainer")))
        { Setters = { new Setter(MaxHeightProperty, 0d) } });
        ScrollViewer.SetHorizontalScrollBarVisibility(_filterScroll, ScrollBarVisibility.Disabled);
        _filterScroll.Content = new Border { Padding = new Thickness(0, 0, 12, 0), Child = fields };
        Grid content = new() { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 6 };
        content.Children.Add(_filterScroll);
        content.Children.Add(_filterClose); Avalonia.Controls.Grid.SetRow(_filterClose, 1);
        Border surface = new()
        {
            Name = "MediaFilterSurface", Child = content, Padding = new Thickness(10, 8), Margin = new Thickness(12, 0, 12, 8),
            VerticalAlignment = VerticalAlignment.Top, ZIndex = 10, IsVisible = false, Classes = { "raised" }
        };
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.Cycle);
        surface.KeyDown += OnFilterSurfaceKeyDown;
        _filterClose.Click += (_, _) => CloseFilterSurface(restoreFocus: true);
        _filters.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty) OnFilterSurfaceToggled(e.GetNewValue<bool>());
        };
        return _filterSurface = surface;
    }

    private ToggleButton? FilterSurfaceToggle => _filters.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault();

    private bool FocusWithin(Control owner) => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
        && focused.GetSelfAndVisualAncestors().Contains(owner);

    private void OnFilterSurfaceToggled(bool open)
    {
        if (_filterSurface is not { } surface || _lifetime.IsCancellationRequested) return;
        if (!open)
        {
            // Hiding the surface must not strand keyboard focus on an invisible field.
            bool focused = FocusWithin(surface);
            surface.IsVisible = false;
            if (focused) FilterSurfaceToggle?.Focus(NavigationMethod.Tab);
            return;
        }
        surface.IsVisible = true;
        // Only a deliberate open from the toggle moves focus in; a programmatic open keeps the current focus.
        if (!FocusWithin(_filters)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_lifetime.IsCancellationRequested || !IsFilterSurfaceOpen || !surface.IsEffectivelyVisible) return;
            surface.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(element => element.Focusable && element.IsEffectivelyEnabled && element.IsEffectivelyVisible)
                ?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    internal void CloseFilterSurface(bool restoreFocus)
    {
        bool focused = _filterSurface != null && FocusWithin(_filterSurface);
        _filters.IsExpanded = false;
        if (restoreFocus || focused) FilterSurfaceToggle?.Focus(NavigationMethod.Tab);
    }

    private void OnFilterSurfaceKeyDown(object? sender, KeyEventArgs e)
    {
        // Open dropdowns handle their own Escape first.
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        CloseFilterSurface(restoreFocus: true);
    }

    // Filters apply live, so the toggle says how many are on while the fields are hidden (C3: "Filters (3)", one per chip).
    private void UpdateFilterToggle()
    {
        int count = ActiveFilterCount;
        string header = count > 0 ? Texts.T("media.filters.count", count) : Texts.T("media.filters");
        if (!Equals(_filters.Header, header)) _filters.Header = header;
        AutomationProperties.SetName(_filters, count > 0 ? Texts.Count("media.filters.countName", count) : header);
    }

    private void LocalizeFilterSurface()
    {
        _filterClose.Content = Texts.T("action.close");
        AutomationProperties.SetName(_filterSurface!, Texts.T("media.filters"));
        UpdateFilterToggle();
    }
}

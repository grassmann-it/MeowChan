using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan;

// UX103 P2: the archive filter drafts open as a local surface over the result
// list. Presentation only: the expander keeps the toggle command, IsExpanded and
// the draft marker; drafts, Search and the applied chips keep their contracts.
public partial class ArchivePage
{
    private void InitializeFilterSurface()
    {
        Expander filters = ArchiveFilters;
        filters.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty) OnFilterSurfaceToggled(e.GetNewValue<bool>());
        };
    }

    internal bool IsFilterSurfaceOpen => ArchiveFilters.IsExpanded;

    private ToggleButton? FilterSurfaceToggle => ArchiveFilters
        .GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault();

    private bool FocusWithin(Control owner) => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
        && focused.GetSelfAndVisualAncestors().Contains(owner);

    private void OnFilterSurfaceToggled(bool open)
    {
        if (_ended) return;
        Border surface = ArchiveFilterSurface;
        if (!open)
        {
            // Hiding the surface must not strand keyboard focus on an invisible field.
            if (FocusWithin(surface)) FilterSurfaceToggle?.Focus(NavigationMethod.Tab);
            return;
        }
        // Only a deliberate open from the toggle moves focus in; programmatic opens
        // (invalid draft reveal, commands) keep their own focus target.
        if (!FocusWithin(ArchiveFilters)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || !IsFilterSurfaceOpen || !surface.IsEffectivelyVisible) return;
            surface.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(element => element.Focusable && element.IsEffectivelyEnabled && element.IsEffectivelyVisible)
                ?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    internal void CloseFilterSurface(bool restoreFocus)
    {
        bool focused = FocusWithin(ArchiveFilterSurface);
        ArchiveFilters.IsExpanded = false;
        if (restoreFocus || focused) FilterSurfaceToggle?.Focus(NavigationMethod.Tab);
    }

    private void OnFilterSurfaceKeyDown(object? sender, KeyEventArgs e)
    {
        // Open dropdowns, suggestions and date editors handle their own Escape first.
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        CloseFilterSurface(restoreFocus: true);
    }

    private void OnFilterCloseClick(object? sender, RoutedEventArgs e) => CloseFilterSurface(restoreFocus: true);

    // Same apply path as the main Search button; the surface closes only when a
    // new request was actually applied, so invalid drafts stay visible for repair.
    private async void OnFilterApplyClick(object? sender, RoutedEventArgs e)
    {
        var before = _appliedSearch;
        await ApplySearchAsync();
        if (!_ended && _invalidSearchEditor == null && !ReferenceEquals(before, _appliedSearch))
            CloseFilterSurface(restoreFocus: true);
    }
}

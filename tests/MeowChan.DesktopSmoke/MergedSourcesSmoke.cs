using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop.Plugins;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// Fixture setup only: the real SourcesPage owns the real management control and both detail levels.
// No replacement workspace, service, draft, menu command or confirmation implementation lives here.
internal static class MergedSourcesSmoke
{
    internal static SourcesPage Create(PluginManagementServices services, out PluginsPage management)
    {
        SourcesPage sources = new();
        management = sources.PluginManagement;
        management.Attach(services);
        management.SetFilter(SourcesPluginsFilter.AllPlugins);
        return sources;
    }

    internal static void OpenPlugins(MainWindow window)
    {
        SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Sources);
        ContentControl host = window.FindControl<ContentControl>("SourcesPageHost")!;
        if (window.SelectedPage != MainWindow.WorkspacePage.Sources || host.Content is not SourcesPage sources)
            throw new InvalidOperationException("The real merged Sources workspace was not reached.");
        Filter(sources.PluginManagement, SourcesPluginsFilter.AllPlugins);
    }

    internal static void Filter(PluginsPage management, SourcesPluginsFilter filter)
    {
        // Exercise the actual dropdown's change handler, not a second filtering model in the fixture.
        ComboBox dropdown = management.FindControl<ComboBox>("PluginCategory")!;
        if (!dropdown.Items.OfType<string>().SequenceEqual(new[] { Texts.T("plugins.sources"), Texts.T("plugins.filters"), Texts.T("plugins.all") }))
            throw new InvalidOperationException("The merged dropdown lost its Sources / Image filters / All plugins order or translation.");
        dropdown.SelectedIndex = (int)filter;
        if (management.Filter != filter)
            throw new InvalidOperationException("The merged Show dropdown did not select " + filter);
    }

    internal static void OpenSourceRow(SourcesPage sources, SelectionListItem row)
    {
        SelectionList list = sources.FindControl<SelectionList>("SourceCards")!;
        if (!list.Items.Contains(row)) throw new InvalidOperationException("The source row is not in the actual list.");
        list.SelectedItem = row; // explicit fixture selection, not a claim about OS pointer input
        row.Focus();
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = row, Key = Key.Enter });
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Source = row, Key = Key.Enter });
    }
}

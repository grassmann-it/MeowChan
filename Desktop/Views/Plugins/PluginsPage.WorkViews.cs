using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// Work views (plan AP5, WV6, §1.10). Approval and activation flows (enable, candidate choice, SDK transition, the draft
// footer with its reasons, recovery) and every problem or error stay in all views. Adding a file and the plugin folder move
// under More; the developer area is reached through "Show developer details" of the plugin's More actions in Barebones.
public partial class PluginsPage
{
    // A page without a main window (fixtures, tests) shows everything, as before work views; the window sets its own view.
    private WorkView _workView = WorkView.Advanced;
    private bool _developerRevealed;

    internal WorkView WorkView => _workView;

    /// <summary>The window applied <paramref name="view"/>: folded blocks return to their default for every plugin (R13); nothing is saved.</summary>
    internal void ApplyWorkView(WorkView view, bool viewChanged)
    {
        if (_ended) return;
        bool changed = viewChanged || view != _workView;
        _workView = view;
        if (changed)
        {
            _developerRevealed = false;
            _pluginExpansionStates.Clear();
            ResetSelectedPluginDisclosures();
        }
        RefreshWorkViewPresentation(changed);
        RefreshPluginActions();
        RefreshPluginSections();
    }

    private Button[] HeaderMovableActions() => [C<Button>("PluginsAdd"), C<Button>("PluginsFolder")];

    private void RefreshWorkViewPresentation(bool viewChanged = false)
    {
        if (_ended) return;
        WorkView view = _workView;
        Button[] header = HeaderMovableActions();
        foreach (Button button in header) WorkViewGate.Apply(button, view);
        WorkViewMoreSurface.Configure(C<Button>("PluginsMoreButton"), WorkViewMoreSurface.ForButtons(header, view).Count());
        WorkViewPresenter.ApplyExpander(C<Expander>("PluginDirectoryExpander"), view, viewChanged);
        // The developer area: a build error, candidate diagnostics, a probe result or any problem of the plugin keeps it on the page (R6).
        WorkViewGate.Apply(C<Expander>("PluginDeveloperExpander"), view, nonDefaultState: _developerRevealed || DeveloperAreaNeedsAttention());
    }

    private bool DeveloperAreaNeedsAttention() => Selected is { } entry
        && (entry.Diagnostics.Count > 0 || C<StackPanel>("PluginBuildDiagnostics").Children.Count > 0
            || !string.IsNullOrEmpty(C<TextBox>("PluginProbeResult").Text) || C<TextBlock>("PluginAttention").IsVisible);

    // What a disclosure of the selected plugin shows before the user touches it in this view.
    private bool DefaultOpen(string policyId) =>
        WorkViewPolicy.Resolve(policyId, _workView) is Presence.Direct or Presence.Required;

    private void ResetSelectedPluginDisclosures()
    {
        // Setting them is not the user's choice: nothing is remembered for the plugin.
        bool restoring = _restoringPluginTools;
        _restoringPluginTools = true;
        try
        {
            C<Expander>("PluginInfoExpander").IsExpanded = DefaultOpen("plugins.detail.info");
            C<Expander>("PluginGrantExpander").IsExpanded = DefaultOpen("plugins.detail.grants");
            C<Expander>("PluginDeveloperExpander").IsExpanded = DefaultOpen("plugins.detail.developer");
        }
        finally { _restoringPluginTools = restoring; }
    }

    /// <summary>Shows the developer area in this view (until the view changes); <paramref name="expand"/> also opens it.</summary>
    internal void RevealDeveloperArea(bool expand = true)
    {
        if (_ended) return;
        _developerRevealed = true;
        RefreshWorkViewPresentation();
        if (expand) C<Expander>("PluginDeveloperExpander").IsExpanded = true;
    }

    private void OnPluginsMoreClick(object? sender, RoutedEventArgs e) =>
        WorkViewMoreSurface.Open(C<Button>("PluginsMoreButton"), WorkViewMoreSurface.ForButtons(HeaderMovableActions(), _workView));
}

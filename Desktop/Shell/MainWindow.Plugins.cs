using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Plugins;

namespace MeowChan;

public partial class MainWindow
{
    private void InitializePluginPage()
    {
        _pluginsPage = _sourcesPage!.PluginManagement;
        _pluginsPage.GetEditorOptions = () => new PluginEditorOptions(_config.PluginEditorExecutable, _config.PluginEditorArguments);
        _pluginsPage.SaveEditorOptionsAsync = options => _session.SaveViewPreferencesAsync(config =>
        {
            config.PluginEditorExecutable = options.Executable;
            config.PluginEditorArguments = options.Arguments.ToArray();
        });
        _pluginsPage.ApplyWorkView(ConfiguredWorkView, viewChanged: true);
        _sourcesPage.GetSourceMediaIntegrityPolicies = _session.GetSourceMediaIntegrityPolicies;
        _sourcesPage.SetSourceMediaIntegrityPermission = _session.SetSourceMediaIntegrityPermissionAsync;
        _sourcesPage.GetChanSiteEnabled = _session.GetChanSiteEnabled;
        _sourcesPage.SetChanSiteEnabled = _session.SetChanSiteEnabledAsync;
        AttachPluginServices();
    }
    private void AttachPluginServices()
    {
        if (_diagnosticsPage != null)
        {
            _diagnosticsPage.GetTargetWaitStates = _session.GetTargetWaitStates;
            _diagnosticsPage.CheckTargetsAsync = () => _session.CheckTargetsAsync();
        }
        if (_session.Plugins is not { } plugins || _pluginsPage == null) return;
        PluginManagementServices services = new(plugins.ProfileStore, plugins.CompilationService, plugins.Inventory,
            plugins.PluginDirectory, plugins.DescribeTestAsync, plugins.StartTestWorkerAsync, PluginProbe.ReadAsync)
        {
            GetWorkerSnapshots = plugins.Runtime.GetSnapshots,
            GetProductionWorkerSnapshots = plugins.GetProductionWorkerSnapshots,
            GetRuleScopes = () => _session.MediaRules.Rules.Select(rule => rule.Scope).ToArray()
        };
        _pluginsPage.Attach(services);
        _rulesPage?.AttachPluginServices(services);
    }
    private void OnPluginsNavigationClick(object? sender, RoutedEventArgs e)
    {
        ToolsNavigationButton.Flyout?.Hide();
        if (NavigateWorkspace(WorkspacePage.Sources)) _ = _sourcesPage?.ActivateAsync(SourcesPluginsFilter.AllPlugins);
    }
}

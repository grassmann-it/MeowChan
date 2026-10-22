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
        _pluginsPage = new PluginsPage
        {
            GetEditorOptions = () => new PluginEditorOptions(_config.PluginEditorExecutable, _config.PluginEditorArguments),
            SaveEditorOptionsAsync = options => _session.SaveViewPreferencesAsync(config =>
            {
                config.PluginEditorExecutable = options.Executable;
                config.PluginEditorArguments = options.Arguments.ToArray();
            }),
            OpenSourceWatches = source => { if (NavigateWorkspace(WorkspacePage.Sources)) _sourcesPage?.SelectSource(source); }
        };
        this.FindControl<ContentControl>("PluginsPageHost")!.Content = _pluginsPage;
        _sourcesPage!.OpenPluginSettings = source =>
        {
            if (NavigateWorkspace(WorkspacePage.Plugins)) _ = _pluginsPage.ActivateAsync(source);
        };
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
        { GetWorkerSnapshots = plugins.Runtime.GetSnapshots, GetRuleScopes = () => _session.MediaRules.Rules.Select(rule => rule.Scope).ToArray() };
        _pluginsPage.Attach(services);
        _rulesPage?.AttachPluginServices(services);
    }
    private void OnPluginsNavigationClick(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Button>("ToolsNavigationButton")!.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Plugins);
    }
}

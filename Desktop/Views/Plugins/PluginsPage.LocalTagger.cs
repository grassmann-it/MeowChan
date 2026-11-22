using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Taggers;

namespace MeowChan;

public partial class PluginsPage
{
    private async void OnLocalTaggerClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _services?.LocalTaggerModels is not LocalTaggerModelStore models || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        await new LocalTaggerModelWindow(models, _lifetime.Token).ShowDialog(owner);
    }
}

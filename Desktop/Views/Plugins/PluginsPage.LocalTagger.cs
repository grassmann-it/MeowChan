using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Taggers;

namespace MeowChan;

public partial class PluginsPage
{
    private bool _localTaggerOpen;

    private async void OnLocalTaggerClick(object? sender, RoutedEventArgs e) =>
        await OpenActions.RunAsync(async () =>
        {
            if (_ended || _localTaggerOpen || _services?.LocalTaggerModels is not LocalTaggerModelStore models ||
                TopLevel.GetTopLevel(this) is not Window owner) return;

            _localTaggerOpen = true;
            try
            {
                await new LocalTaggerModelWindow(models, _lifetime.Token).ShowDialog(owner);
            }
            finally
            {
                _localTaggerOpen = false;
            }
        }, "plugins.localTagger.open");
}

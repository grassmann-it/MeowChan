using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class DesktopSessionShortcutScopeTests
{
    [Fact]
    public void StartingAHeadlessSessionKeepsTheActiveShortcutSnapshot() => BooruComponentDispatcher.Run(() =>
    {
        using TempDirectory directory = new();
        EffectiveShortcutBindings active = ShortcutService.Current;
        new Config
        {
            AutomaticUpdateChecks = false,
            SaveUrlsOnExit = false,
            DownloadsPaused = true,
            DownloadPath = Path.Combine(directory.Path, "media"),
            ShortcutBindings = new ShortcutBindingsSection().WithSingleKeyShortcuts(!active.SingleKeyShortcuts)
        }.SaveConfig(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        try
        {
            BooruComponentDispatcher.Wait(host.StartAsync());
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.NotEqual(active.SingleKeyShortcuts, host.Config.ShortcutBindings.SingleKeyShortcuts);
            Assert.Same(active, ShortcutService.Current);
        }
        finally { BooruComponentDispatcher.Wait(host.DisposeAsync().AsTask()); }
    });
}
